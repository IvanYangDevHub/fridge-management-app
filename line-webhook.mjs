import { createHmac, timingSafeEqual } from 'node:crypto';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { dueItems, expiryMessage, loadState, saveState, taipeiDate } from './expiry-notifications.mjs';

const MAX_BODY_BYTES = 1024 * 1024;

export function validLineSignature(body, signature, secret) {
  if (!signature || !secret) return false;
  const expected = createHmac('sha256', secret).update(body).digest();
  let received;
  try {
    received = Buffer.from(signature, 'base64');
  } catch {
    return false;
  }
  return received.length === expected.length && timingSafeEqual(received, expected);
}

async function readBody(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) throw new Error('payload too large');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

function replyText(event, code, state) {
  if (event.type === 'follow') return '歡迎使用鮮日子！傳送「訂閱 配對碼」開啟每日到期提醒。';
  if (event.type === 'message' && event.message?.type === 'text' && event.message.text?.trim().toLowerCase() === 'ping') {
    return 'pong 🥬 鮮日子 LINE Webhook 已連線。';
  }
  if (event.type === 'message' && event.message?.type === 'text') {
    const input = event.message.text.trim();
    const userId = event.source?.userId;
    if (input.startsWith('訂閱 ')) {
      if (!userId || input.slice(3).trim() !== code) return '配對碼不正確，請向家庭管理者確認。';
      if (!state.subscribers.includes(userId)) state.subscribers.push(userId);
      return '已訂閱每日到期提醒。傳送「取消訂閱」可停止通知。';
    }
    if (input === '取消訂閱' && userId) {
      state.subscribers = state.subscribers.filter((id) => id !== userId);
      return '已取消訂閱到期提醒。';
    }
    if (input === '到期') return state.subscribers.includes(userId) ? expiryMessage(state.items) || '目前沒有三天內到期的食材。' : '請先傳送「訂閱 配對碼」。';
  }
  return null;
}

export function createLineWebhookServer({ secret, accessToken, householdCode, dataFile = './line-data.json', fetchImpl = fetch, logger = console, now = () => new Date() }) {
  if (!secret || !accessToken || !householdCode) throw new Error('LINE_CHANNEL_SECRET, LINE_CHANNEL_ACCESS_TOKEN and HOUSEHOLD_CODE are required');
  let statePromise = loadState(dataFile);
  const updateState = (fn) => {
    let result;
    statePromise = statePromise.then(async (state) => {
      result = await fn(state);
      await saveState(dataFile, state);
      return state;
    });
    return statePromise.then(() => result);
  };
  async function pushDue() {
    const current = now();
    const date = taipeiDate(current);
    const hour = Number(new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Taipei', hour: '2-digit', hourCycle: 'h23' }).format(current));
    if (hour < 9) return;
    const state = await statePromise;
    if (state.sentDate === date || !state.subscribers.length || !dueItems(state.items, current).length) return;
    const message = expiryMessage(state.items, current);
    for (const userId of state.subscribers) {
      const result = await fetchImpl('https://api.line.me/v2/bot/message/push', {
        method: 'POST',
        headers: { authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' },
        body: JSON.stringify({ to: userId, messages: [{ type: 'text', text: message }] }),
      });
      if (!result.ok) throw new Error(`LINE push failed: HTTP ${result.status}`);
    }
    await updateState((currentState) => { currentState.sentDate = date; });
  }
  const timer = setInterval(() => pushDue().catch((error) => logger.error('Daily expiry notification failed', error)), 60000);
  timer.unref();
  setImmediate(() => pushDue().catch((error) => logger.error('Daily expiry notification failed', error)));
  const server = createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    if (request.method === 'GET' && path === '/health') {
      response.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' }).end('ok');
      return;
    }
    if (path === '/api/inventory' || path === '/api/shopping') {
      if (request.headers['x-household-code'] !== householdCode) { response.writeHead(401).end(); return; }
      const field = path === '/api/inventory' ? 'items' : 'shoppingItems';
      if (request.method === 'GET') {
        const state = await statePromise;
        response.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }).end(JSON.stringify(state[field]));
        return;
      }
      if (request.method !== 'PUT' && request.method !== 'POST') { response.writeHead(405).end(); return; }
      try {
        const items = JSON.parse((await readBody(request)).toString('utf8'));
        if (!Array.isArray(items) || items.length > 500 || items.some((item) => typeof item.id !== 'string' || typeof item.name !== 'string' || item.name.length > 100 || (field === 'items' && item.expiresAt != null && !/^\d{4}-\d{2}-\d{2}$/.test(item.expiresAt)))) throw new Error('invalid data');
        await updateState((state) => { state[field] = items; });
        response.writeHead(204).end();
      } catch { response.writeHead(400).end(); }
      return;
    }
    if (request.method !== 'POST' || path !== '/webhook/line') {
      response.writeHead(404).end();
      return;
    }

    let body;
    try {
      body = await readBody(request);
    } catch {
      response.writeHead(413).end();
      return;
    }
    if (!validLineSignature(body, request.headers['x-line-signature'], secret)) {
      response.writeHead(401).end();
      return;
    }
    let payload;
    try {
      payload = JSON.parse(body.toString('utf8'));
      if (!Array.isArray(payload.events)) throw new Error('events must be an array');
    } catch {
      response.writeHead(400).end();
      return;
    }

    // LINE's Verify request has an empty events array and expects HTTP 200.
    for (const event of payload.events) {
      let text;
      try { text = await updateState((state) => replyText(event, householdCode, state)); }
      catch (error) { logger.error('State update failed', error); continue; }
      if (!text || !event.replyToken) continue;
      try {
        const result = await fetchImpl('https://api.line.me/v2/bot/message/reply', {
          method: 'POST',
          headers: { authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' },
          body: JSON.stringify({ replyToken: event.replyToken, messages: [{ type: 'text', text }] }),
        });
        if (!result.ok) logger.error(`LINE reply failed: HTTP ${result.status}`);
      } catch (error) {
        logger.error('LINE reply request failed', error);
      }
    }
    response.writeHead(200).end();
  });
  server.pushDue = pushDue;
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const port = Number(process.env.PORT || 3000);
  createLineWebhookServer({
    secret: process.env.LINE_CHANNEL_SECRET,
    accessToken: process.env.LINE_CHANNEL_ACCESS_TOKEN,
    householdCode: process.env.HOUSEHOLD_CODE,
    dataFile: process.env.LINE_DATA_FILE || './line-data.json',
  }).listen(port, () => console.log(`LINE Webhook listening on port ${port}`));
}
