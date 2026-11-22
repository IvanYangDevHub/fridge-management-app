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
};

export function readInventory(scope: ListScope, signal: AbortSignal) {
  return listRequest<FoodItem[]>(scope, 'inventory', 'GET', undefined, signal);
}

export async function saveInventory(scope: ListScope, _previous: FoodItem[], next: FoodItem[]) {
  await listRequest<void>(scope, 'inventory', 'PUT', next);
  return next;
}
