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

function safeExpiry(value: unknown): string | null {
  if (typeof value !== 'string' || !/^(?!0000)\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const date = new Date(value + 'T00:00:00Z');
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value ? value : null;
}

export function safeFood(item: FoodItem): FoodItem {
  const { quantity, unit, category, location, expiresAt, status, createdAt } = item;
  return {
    ...item,
    quantity: Number.isFinite(quantity) && quantity > 0 ? quantity : 1,
    unit: typeof unit === 'string' ? unit : '份',
    category: typeof category === 'string' ? category : 'other',
    location: location === 'freezer' || location === 'pantry' ? location : 'fridge',
    expiresAt: safeExpiry(expiresAt),
    status: status === 'consumed' ? 'consumed' : 'active',
    createdAt: typeof createdAt === 'string' ? createdAt : '',
  };
}

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
      : await listRequest<FoodItem>(scope, 'inventory', 'PUT', safeFood(edited), signal, { id: edited.id });
    return next.map((item) => item === edited ? saved : item);
  }

  const deleted = previous.find((item) => !next.includes(item))!;
  await listRequest<void>(scope, 'inventory', 'DELETE', undefined, signal, { id: deleted.id, version: deleted.version });
  return next;
}