// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import App, { HouseholdLists } from './App';
import type { ShoppingItem } from './shoppingApi';

const listPath = '/api/households/home/shopping';
const food = { id: 'milk', name: '牛奶', location: 'fridge', category: 'dairy', quantity: 1, unit: '瓶' };
const onion = { id: 'onion', name: '洋蔥', completed: false, version: 1 };
const salt = { id: 'legacy&salt/%2F', name: '鹽', completed: false, version: 1 };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status });

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

test('recipe additions, checkoffs and removals use individual shopping items and current versions', async () => {
  const saltPath = listPath + '/item?id=legacy%26salt%2F%252F';
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === '/api/households/home/inventory') return json([food]);
    if (path === listPath && options?.method === 'GET') return json([salt]);
    if (path === listPath && options?.method === 'POST') return json(onion, 201);
    if (path === saltPath && options?.method === 'PUT') return json({ ...salt, completed: true, version: 2 });
    if (path === saltPath + '&version=2' && options?.method === 'DELETE') return new Response(null, { status: 204 });
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);

  const recipe = await screen.findByRole('button', { name: '加入採買清單' });
  await userEvent.click(recipe);
  const checked = screen.getByRole('checkbox', { name: '鹽' });
  expect(await screen.findByRole('checkbox', { name: '洋蔥' })).not.toBeChecked();
  expect(fetcher).toHaveBeenCalledWith(listPath, expect.objectContaining({
    method: 'POST', body: JSON.stringify({ name: '洋蔥', completed: false }),
  }));

  await userEvent.click(checked);
  expect(checked).toBeChecked();
  expect(fetcher).toHaveBeenCalledWith(saltPath, expect.objectContaining({
    method: 'PUT', body: JSON.stringify({ name: '鹽', completed: true, version: 1 }),
  }));

  const [removeSalt] = screen.getAllByRole('button', { name: '移除' });
  await userEvent.click(removeSalt);
  expect(fetcher).toHaveBeenCalledWith(saltPath + '&version=2', expect.objectContaining({ method: 'DELETE' }));
  expect(screen.queryByRole('checkbox', { name: '鹽' })).not.toBeInTheDocument();
  expect(screen.getByRole('checkbox', { name: '洋蔥' })).toBeInTheDocument();
});

test('the visible shopping list picks up someone else’s change on the next poll', async () => {
  vi.useFakeTimers();
  let shopping = [salt];
  const fetcher = vi.fn(async (path: string) => {
    if (path === '/api/households/home/inventory') return json([]);
    if (path === listPath) return json(shopping);
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  await act(async () => { render(<HouseholdLists householdId="home" />); });
  expect(screen.getByRole('checkbox', { name: '鹽' })).not.toBeChecked();

  shopping = [{ ...salt, completed: true, version: 2 }, onion];
  await act(async () => { await vi.advanceTimersByTimeAsync(14_999); });
  expect(screen.queryByRole('checkbox', { name: '洋蔥' })).not.toBeInTheDocument();
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(screen.getByRole('checkbox', { name: '鹽' })).toBeChecked();
  expect(screen.getByRole('checkbox', { name: '洋蔥' })).toBeInTheDocument();
});

test('a stale shopping edit reloads the family list and the user can retry with the new version', async () => {
  const itemPath = listPath + '/item?id=legacy%26salt%2F%252F';
  let shopping = [salt];
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === '/api/households/home/inventory') return json([]);
    if (path === listPath && options?.method === 'GET') return json(shopping);
    if (path === itemPath && options?.method === 'PUT') {
      const update = JSON.parse(options.body as string);
      if (update.version === 1) {
        shopping = [{ ...salt, name: '海鹽', version: 2 }];
        return new Response(null, { status: 409 });
      }
      return json({ ...shopping[0], completed: update.completed, version: 3 });
    }
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);
  await userEvent.click(await screen.findByRole('checkbox', { name: '鹽' }));
  expect(await screen.findByText(/已重新載入/)).toBeInTheDocument();
  const reloaded = screen.getByRole('checkbox', { name: '海鹽' });
  expect(reloaded).not.toBeChecked();
  await userEvent.click(reloaded);
  expect(reloaded).toBeChecked();
  expect(fetcher).toHaveBeenCalledWith(itemPath, expect.objectContaining({
    method: 'PUT', body: JSON.stringify({ name: '海鹽', completed: true, version: 2 }),
  }));
});

test('retrying recipe additions after a partial failure keeps the committed item once', async () => {
  const shopping: ShoppingItem[] = [];
  let saltAttempts = 0;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === '/api/households/home/inventory') return json([food]);
    if (path === listPath && options?.method === 'GET') return json(shopping);
    if (path === listPath && options?.method === 'POST') {
      const item = JSON.parse(options.body as string);
      if (item.name === '鹽' && saltAttempts++ === 0) return new Response(null, { status: 503 });
      const committed = { ...item, id: crypto.randomUUID(), version: 1 };
      shopping.push(committed);
      return json(committed, 201);
    }
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);
  const recipe = await screen.findByRole('button', { name: '加入採買清單' });
  await userEvent.click(recipe);
  expect(await screen.findByText('伺服器錯誤 503')).toBeInTheDocument();
  expect(await screen.findByRole('checkbox', { name: '洋蔥' })).toBeInTheDocument();
  expect(screen.queryByRole('checkbox', { name: '鹽' })).not.toBeInTheDocument();

  await userEvent.click(recipe);
  expect(await screen.findByRole('checkbox', { name: '鹽' })).toBeInTheDocument();
  expect(screen.getAllByRole('checkbox', { name: '洋蔥' })).toHaveLength(1);
});

test('pairing-code shopping still writes the full list through the original API', async () => {
  sessionStorage.setItem('fresh-days-household-code', 'test-code');
  const { version: _version, ...legacy } = salt;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === '/api/inventory') return json([]);
    if (path === '/api/shopping' && options?.method === 'GET') return json([legacy]);
    if (path === '/api/shopping' && options?.method === 'PUT') return new Response(null, { status: 204 });
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: '移除' }));
  expect(await screen.findByText('採買清單已儲存')).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/shopping', expect.objectContaining({
    method: 'PUT', body: '[]',
    headers: { 'x-household-code': 'test-code', 'content-type': 'application/json' },
  }));
});
