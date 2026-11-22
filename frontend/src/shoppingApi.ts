import { listRequest, type ListScope } from './listRequest';

export type ShoppingItem = { id: string; name: string; completed: boolean; version?: number };

export function readShopping(scope: ListScope, signal: AbortSignal) {
  return listRequest<ShoppingItem[]>(scope, 'shopping', 'GET', undefined, signal);
}

export async function saveShopping(scope: ListScope, previous: ShoppingItem[], next: ShoppingItem[],
  signal?: AbortSignal): Promise<ShoppingItem[]> {
  if (!scope.householdId) {
    await listRequest<void>(scope, 'shopping', 'PUT', next, signal);
    return next;
  }

  const previousById = new Map(previous.map((item) => [item.id, item]));
  const saved: ShoppingItem[] = [];
  for (const item of next) {
    const current = previousById.get(item.id);
    previousById.delete(item.id);

    if (current === item) {
      saved.push(item);
      continue;
    }

    const data = { name: item.name, completed: item.completed };
    const committed = current
      ? await listRequest<ShoppingItem>(scope, 'shopping', 'PUT', { ...data, version: current.version }, signal, { id: item.id })
      : await listRequest<ShoppingItem>(scope, 'shopping', 'POST', data, signal);
    saved.push(committed);
  }

  for (const removed of previousById.values()) {
    await listRequest<void>(scope, 'shopping', 'DELETE', undefined, signal, { id: removed.id, version: removed.version });
  }
  return saved;
}
