import { readFile, rename, writeFile } from 'node:fs/promises';

const taipeiDate = (date) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Taipei', year: 'numeric', month: '2-digit', day: '2-digit' }).format(date);

export function dueItems(items, now = new Date()) {
  const today = taipeiDate(now);
  const end = taipeiDate(new Date(now.getTime() + 3 * 86400000));
  return items.filter((item) => item.status !== 'consumed' && typeof item.expiresAt === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(item.expiresAt) && item.expiresAt <= end)
    .sort((a, b) => a.expiresAt.localeCompare(b.expiresAt));
}

export function expiryMessage(items, now = new Date()) {
  const today = taipeiDate(now);
  const due = dueItems(items, now);
  if (!due.length) return null;
  const lines = due.slice(0, 15).map((item) => {
    const label = item.expiresAt < today ? '已過期' : item.expiresAt === today ? '今天到期' : `${item.expiresAt} 到期`;
    return `・${item.name}：${label}`;
  });
  return `【鮮日子｜${today} 到期提醒】\n需處理 ${due.length} 項食材：\n${lines.join('\n')}${due.length > 15 ? '\n・其餘請查看冰箱清單' : ''}\n請確認實際保存狀態。`;
}

export async function loadState(file) {
  try {
    const state = JSON.parse(await readFile(file, 'utf8'));
    return { items: Array.isArray(state.items) ? state.items : [], shoppingItems: Array.isArray(state.shoppingItems) ? state.shoppingItems : [], subscribers: Array.isArray(state.subscribers) ? state.subscribers : [], sentDate: state.sentDate || null };
  } catch (error) {
    if (error.code === 'ENOENT') return { items: [], shoppingItems: [], subscribers: [], sentDate: null };
    throw error;
  }
}

export async function saveState(file, state) {
  const temporary = `${file}.tmp`;
  await writeFile(temporary, JSON.stringify(state), { mode: 0o600 });
  await rename(temporary, file);
}

export { taipeiDate };
