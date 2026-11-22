// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import AuthApp from './AuthApp';

const phaseTwo = { mode: 'phase-two', googleClientId: 'test-google-client' };
const session = { userId: 'alice', email: 'alice@gmail.com', canClaimLegacy: false };
const home = { id: 'home', name: '台中家', role: 'owner' };
const cottage = { id: 'cottage', name: '台北家', role: 'member' };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

test.each(['resolve', 'reject'])('switching households ignores a request that later %ss', async (outcome) => {
  let finishHome!: (response: Response) => void;
  let failHome!: (error: Error) => void;
  const oldInventory = new Promise<Response>((resolve, reject) => { finishHome = resolve; failHome = reject; });
  let homeReads = 0;
  const fetcher = vi.fn((path: string) => {
    switch (path) {
      case '/api/auth/config': return Promise.resolve(json(phaseTwo));
      case '/api/auth/session': return Promise.resolve(json(session));
      case '/api/households': return Promise.resolve(json([home, cottage]));
      case '/api/households/home/inventory':
        return homeReads++ === 0 ? oldInventory : Promise.resolve(json([{ id: 'old', name: '台中庫存' }]));
      case '/api/households/home/shopping': return Promise.resolve(json([{ id: 'old', name: '台中採買', completed: false }]));
      case '/api/households/cottage/inventory': return Promise.resolve(json([{ id: 'new', name: '台北庫存' }]));
      case '/api/households/cottage/shopping': return Promise.resolve(json([{ id: 'new', name: '台北採買', completed: false }]));
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  const select = await screen.findByLabelText('目前家庭');
  expect(select).toHaveValue(home.id);
  await userEvent.selectOptions(select, cottage.id);
  expect(await screen.findAllByText('台北庫存')).not.toHaveLength(0);
  expect(screen.getByText('台北採買')).toBeInTheDocument();

  await act(async () => {
    if (outcome === 'resolve') finishHome(json([{ id: 'old', name: '台中庫存' }]));
    else failHome(new Error('old request failed'));
  });
  expect(screen.queryByText('台中庫存')).not.toBeInTheDocument();
  expect(screen.queryByText('台中採買')).not.toBeInTheDocument();
  await userEvent.selectOptions(select, home.id);
  expect(screen.queryByText('台北庫存')).not.toBeInTheDocument();
  expect(screen.queryByText('台北採買')).not.toBeInTheDocument();
  expect(await screen.findAllByText('台中庫存')).not.toHaveLength(0);
  expect(screen.getByText('台中採買')).toBeInTheDocument();
});


test('creating a household selects it and opens its inventory and shopping lists', async () => {
  const fetcher = vi.fn((path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return Promise.resolve(json(phaseTwo));
      case '/api/auth/session': return Promise.resolve(json(session));
      case '/api/households': return Promise.resolve(options?.method === 'POST' ? json(home, 201) : json([]));
      case '/api/households/home/inventory':
      case '/api/households/home/shopping': return Promise.resolve(json([]));
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  const name = await screen.findByLabelText('家庭名稱');
  const create = screen.getByRole('button', { name: '建立家庭' });
  expect(create).toBeDisabled();
  expect(screen.queryByLabelText('目前家庭')).not.toBeInTheDocument();
  await userEvent.type(name, '  台中家  ');
  await userEvent.click(create);
  expect(fetcher).toHaveBeenCalledWith('/api/households', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ name: '台中家' }),
  }));
  expect(await screen.findByLabelText('目前家庭')).toHaveValue(home.id);
  expect(await screen.findByRole('heading', { name: '冰箱庫存' })).toBeInTheDocument();
  expect(screen.getByRole('heading', { name: '採買清單' })).toBeInTheDocument();
});


test('failed household reads and creation can be retried safely', async () => {
  let getCount = 0;
  let postCount = 0;
  let finishCreate!: (response: Response) => void;
  const pendingCreate = new Promise<Response>((resolve) => { finishCreate = resolve; });
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return json(phaseTwo);
      case '/api/auth/session': return json(session);
      case '/api/households':
        if (options?.method === 'POST') return postCount++ === 0 ? pendingCreate : json(home, 201);
        return getCount++ === 0 ? json({}, 503) : json([]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping': return json([]);
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  expect(await screen.findByRole('alert')).toHaveTextContent('無法載入家庭。');
  await userEvent.click(screen.getByRole('button', { name: '重試' }));
  await userEvent.type(await screen.findByLabelText('家庭名稱'), '台中家');
  const create = screen.getByRole('button', { name: '建立家庭' });
  await userEvent.click(create);
  expect(create).toBeDisabled();
  await act(async () => finishCreate(json({}, 503)));
  expect(await screen.findByRole('alert')).toHaveTextContent('無法建立家庭，請再試一次。');
  expect(screen.getByLabelText('家庭名稱')).toHaveValue('台中家');
  await userEvent.click(create);
  expect(await screen.findByLabelText('目前家庭')).toHaveValue(home.id);
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});

test.each(['resolve', 'reject'])('signing out ignores a household lookup that later %ss', async (outcome) => {
  let finishLookup!: (response: Response) => void;
  let failLookup!: (error: Error) => void;
  let signal!: AbortSignal;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return json(phaseTwo);
      case '/api/auth/session': return json(session);
      case '/api/auth/logout': return new Response(null, { status: 204 });
      case '/api/households':
        signal = options!.signal!;
        return new Promise<Response>((resolve, reject) => { finishLookup = resolve; failLookup = reject; });
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  expect(await screen.findByText('正在載入家庭…')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: '登出' }));
  expect(await screen.findByRole('heading', { name: '登入' })).toBeInTheDocument();
  expect(signal.aborted).toBe(true);
  await act(async () => {
    if (outcome === 'resolve') finishLookup(json([home]));
    else failLookup(new Error('aborted'));
  });
  expect(screen.queryByLabelText('目前家庭')).not.toBeInTheDocument();
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});

test('scoped inventory writes use the active household and report save failures', async () => {
  const counts = new Map<string, number>();
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (options?.method === 'PUT') {
      const current = counts.get(path) ?? 0;
      counts.set(path, current + 1);
      if (current === 0) return json({}, 503);
      if (path.endsWith('/item?id=food')) return json({ id: 'food', name: '牛奶', status: 'consumed', version: 2 });
      return new Response(null, { status: 204 });
    }
    switch (path) {
      case '/api/auth/config': return json(phaseTwo);
      case '/api/auth/session': return json(session);
      case '/api/households': return json([home]);
      case '/api/households/home/inventory':
        return json([{ id: 'food', name: '牛奶', expiresAt: '2000-01-01', status: 'active', category: 'dairy', version: 1 }]);
      case '/api/households/home/shopping':
        return json([{ id: 'shop', name: '米', completed: false }]);
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  const consumed = await screen.findByRole('button', { name: '已用完' });
  await userEvent.click(consumed);
  expect(await screen.findByText('伺服器錯誤 503')).toBeInTheDocument();
  await userEvent.click(consumed);
  expect(await screen.findByText('庫存已儲存')).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/households/home/inventory/item?id=food', expect.objectContaining({
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: expect.stringContaining('"status":"consumed"'),
  }));


});

test('phase one still retries a rejected stored pairing code with the new code', async () => {
  sessionStorage.setItem('fresh-days-household-code', 'old-code');
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === '/api/auth/config') return json({ mode: 'phase-one' });
    const headers = new Headers(options?.headers);
    return headers.get('x-household-code') === 'old-code' ? json({}, 401) : json([]);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  expect(await screen.findByText('配對碼不正確')).toBeInTheDocument();
  const input = screen.getByLabelText('家庭配對碼');
  await userEvent.clear(input);
  await userEvent.type(input, 'new-code');
  await userEvent.click(screen.getByRole('button', { name: '連線' }));
  expect(await screen.findByRole('heading', { name: '冰箱庫存' })).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/inventory', expect.objectContaining({
    headers: { 'x-household-code': 'new-code' },
  }));
});
