import { listRequest, type ListScope } from './listRequest';

export type Location = 'fridge' | 'freezer' | 'pantry';
export type FoodItem = {
  id: string;
  name: string;
  quantity: number;
  unit: string;
  location: Location;
  category: string;
  expiresAt: string | null;
  createdAt: string;
  status: 'active' | 'consumed';
  version?: number;
};

export function readInventory(scope: ListScope, signal: AbortSignal) {
  return listRequest<FoodItem[]>(scope, 'inventory', 'GET', undefined, signal);
}

export async function saveInventory(scope: ListScope, previous: FoodItem[], next: FoodItem[], signal: AbortSignal) {
  if (!scope.householdId) {
    await listRequest<void>(scope, 'inventory', 'PUT', next, signal);
    return next;
  }

  const edited = next.find((item) => !previous.includes(item));
  if (edited) {
    const saved = edited.version === undefined
      ? await listRequest<FoodItem>(scope, 'inventory', 'POST', edited, signal)
      : await listRequest<FoodItem>(scope, 'inventory', 'PUT', edited, signal, { id: edited.id });
    return next.map((item) => item === edited ? saved : item);
  }

  const deleted = previous.find((item) => !next.includes(item))!;
  await listRequest<void>(scope, 'inventory', 'DELETE', undefined, signal, { id: deleted.id, version: deleted.version });
  return next;
}