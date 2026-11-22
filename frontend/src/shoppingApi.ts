import { listRequest, type ListScope } from './listRequest';

export type ShoppingItem = { id: string; name: string; completed: boolean };

export function readShopping(scope: ListScope, signal: AbortSignal) {
  return listRequest<ShoppingItem[]>(scope, 'shopping', 'GET', undefined, signal);
}

export async function saveShopping(scope: ListScope, _previous: ShoppingItem[], next: ShoppingItem[]) {
  await listRequest<void>(scope, 'shopping', 'PUT', next);
  return next;
}
