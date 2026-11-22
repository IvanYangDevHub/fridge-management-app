export type ListScope = { householdId?: string; code: string };

export async function listRequest<T>(scope: ListScope, list: 'inventory' | 'shopping',
  method: 'GET' | 'PUT', data?: unknown, signal?: AbortSignal): Promise<T> {
  const path = scope.householdId ? `households/${scope.householdId}/${list}` : list;
  const response = await fetch(`/api/${path}`, {
    method,
    signal,
    headers: { ...(scope.code ? { 'x-household-code': scope.code } : {}), ...(data ? { 'content-type': 'application/json' } : {}) },
    ...(data ? { body: JSON.stringify(data) } : {}),
  });
  if (!response.ok) throw new Error(scope.code && response.status === 401 ? '配對碼不正確' : `伺服器錯誤 ${response.status}`);
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
