export type ListScope = { householdId?: string; code: string };

export class ListConflict extends Error {}
export class ListRequestError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

export async function listRequest<T>(scope: ListScope, list: 'inventory' | 'shopping',
  method: 'GET' | 'POST' | 'PUT' | 'DELETE', data?: unknown, signal?: AbortSignal,
  item?: { id: string; version?: number }): Promise<T> {
  const path = scope.householdId ? `households/${scope.householdId}/${list}` : list;
  const itemPath = item
    ? `/item?id=${encodeURIComponent(item.id)}${item.version === undefined ? '' : `&version=${item.version}`}`
    : '';
  const response = await fetch(`/api/${path}${itemPath}`, {
    method,
    signal,
    headers: { ...(scope.code ? { 'x-household-code': scope.code } : {}), ...(data ? { 'content-type': 'application/json' } : {}) },
    ...(data ? { body: JSON.stringify(data) } : {}),
  });
  if (response.status === 409) throw new ListConflict();
  if (!response.ok) throw new ListRequestError(response.status,
    scope.code && response.status === 401 ? '配對碼不正確' : `伺服器錯誤 ${response.status}`);
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}