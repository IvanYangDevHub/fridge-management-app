import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { createLineWebhookServer } from './line-webhook.mjs';

const secret = 'test-secret';
const calls = [];
let server;
let testDir;
let baseUrl;
before(async () => {
  testDir = await mkdtemp(join(tmpdir(), 'fresh-days-line-'));
  server = createLineWebhookServer({
    secret,
    accessToken: 'test-token',
    householdCode: 'test-code',
    dataFile: join(testDir, 'state.json'),
    now: () => new Date('2026-11-07T02:00:00Z'),
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      return { ok: true };
    },
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  baseUrl = `http://127.0.0.1:${server.address().port}`;
});
after(async () => {
  await new Promise((resolve) => server.close(resolve));
  await rm(testDir, { recursive: true });
});

function signedRequest(body, signature = createHmac('sha256', secret).update(body).digest('base64')) {
  return fetch(`${baseUrl}/webhook/line`, {
    method: 'POST',
    headers: { 'x-line-signature': signature, 'content-type': 'application/json' },
    body,
  });
}

test('LINE verification accepts signed empty events', async () => {
  const response = await signedRequest('{"destination":"Utest","events":[]}');
  assert.equal(response.status, 200);
  assert.equal(calls.length, 0);
});

test('rejects invalid signature before processing events', async () => {
  const response = await signedRequest('{"events":[{"type":"follow","replyToken":"token"}]}', 'wrong');
  assert.equal(response.status, 401);
  assert.equal(calls.length, 0);
});

test('replies to ping after verifying a webhook', async () => {
  const body = JSON.stringify({ events: [{ type: 'message', replyToken: 'reply-token', message: { type: 'text', text: 'ping' } }] });
  const response = await signedRequest(body);
  assert.equal(response.status, 200);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'https://api.line.me/v2/bot/message/reply');
  assert.deepEqual(JSON.parse(calls[0].options.body), {
    replyToken: 'reply-token',
    messages: [{ type: 'text', text: 'pong 🥬 鮮日子 LINE Webhook 已連線。' }],
  });
});

test('syncs inventory, subscribes once and pushes the due summary once per day', async () => {
  const inventory = await fetch(`${baseUrl}/api/inventory`, {
    method: 'POST', headers: { 'x-household-code': 'test-code' },
    body: JSON.stringify([{ id: 'milk-1', name: '牛奶', expiresAt: '2026-11-08', status: 'active' }]),
  });
  assert.equal(inventory.status, 204);
  const stored = await fetch(`${baseUrl}/api/inventory`, { headers: { 'x-household-code': 'test-code' } });
  assert.equal(stored.status, 200);
  assert.equal((await stored.json())[0].name, '牛奶');
  const shoppingWrite = await fetch(`${baseUrl}/api/shopping`, {
    method: 'PUT', headers: { 'x-household-code': 'test-code' },
    body: JSON.stringify([{ id: 'salt-1', name: '鹽', completed: false }]),
  });
  assert.equal(shoppingWrite.status, 204);
  const shoppingRead = await fetch(`${baseUrl}/api/shopping`, { headers: { 'x-household-code': 'test-code' } });
  assert.deepEqual(await shoppingRead.json(), [{ id: 'salt-1', name: '鹽', completed: false }]);
  const denied = await fetch(`${baseUrl}/api/inventory`, { headers: { 'x-household-code': 'wrong' } });
  assert.equal(denied.status, 401);
  const subscribe = JSON.stringify({ events: [{ type: 'message', replyToken: 'subscribe-token', source: { userId: 'Utest' }, message: { type: 'text', text: '訂閱 test-code' } }] });
  assert.equal((await signedRequest(subscribe)).status, 200);
  await server.pushDue();
  await server.pushDue();
  const pushes = calls.filter((call) => call.url.endsWith('/push'));
  assert.equal(pushes.length, 1);
  assert.match(JSON.parse(pushes[0].options.body).messages[0].text, /牛奶/);
});
