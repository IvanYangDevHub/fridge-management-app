// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { HouseholdLists } from './App';

const inventory = '/api/households/home/inventory';
const shopping = '/api/households/home/shopping';
const milk = { id: 'food/1%?', name: '鮮奶', quantity: 1, unit: '瓶', location: 'fridge',
  category: 'dairy', expiresAt: null, status: 'active', version: 1 };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status });
const item = inventory + '/item?id=food%2F1%25%3F';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

test('an open household sees committed changes on its next poll and stops polling when closed', async () => {
  vi.useFakeTimers();
  let records = [milk];
  const fetcher = vi.fn(async (path: string) => {
    if (path === inventory) return json(records);
    if (path === shopping) return json([]);
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  const { unmount } = render(<HouseholdLists householdId="home" />);
  await act(async () => {});
  expect(screen.getByText('1 瓶 · 冷藏 · 乳製品 · 未設定到期日')).toBeInTheDocument();

  records = [{ ...milk, quantity: 2, version: 2 }];
  await act(async () => vi.advanceTimersByTimeAsync(14_999));
  expect(screen.queryByText('2 瓶 · 冷藏 · 乳製品 · 未設定到期日')).not.toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(1));
  expect(screen.getByText('2 瓶 · 冷藏 · 乳製品 · 未設定到期日')).toBeInTheDocument();

  const requests = fetcher.mock.calls.length;
  unmount();
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  expect(fetcher).toHaveBeenCalledTimes(requests);
});

test('a conflict reloads inventory and the user can act on the current version', async () => {
  let records = [milk];
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === inventory) return json(records);
    if (path === shopping) return json([]);
    if (path === item && options?.method === 'PUT') {
      records = [{ ...milk, quantity: 2, version: 2 }];
      return json({}, 409);
    }
    if (path === item + '&version=2' && options?.method === 'DELETE') return new Response(null, { status: 204 });
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);
  fireEvent.click(await screen.findByRole('button', { name: '已用完' }));
  expect(await screen.findByText(/已重新載入/)).toBeInTheDocument();
  expect(screen.getByText('2 瓶 · 冷藏 · 乳製品 · 未設定到期日')).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith(item, expect.objectContaining({
    method: 'PUT', body: expect.stringContaining('"version":1'),
  }));
  fireEvent.click(screen.getByRole('button', { name: '刪除 鮮奶' }));
  expect(await screen.findByText('庫存已儲存')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '刪除 鮮奶' })).not.toBeInTheDocument();
});

test('creating and editing food saves one item and uses the version opened in the form', async () => {
  vi.useFakeTimers();
  let records = [milk];
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === inventory && options?.method === 'POST') {
      const newFood = JSON.parse(options.body as string);
      const saved = { ...newFood, id: 'added-by-server', version: 1 };
      records = [saved, ...records];
      return json(saved, 201);
    }
    if (path === inventory) return json(records);
    if (path === shopping) return json([]);
    if (path === item && options?.method === 'PUT') return json({}, 409);
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);
  await act(async () => {});

  fireEvent.change(screen.getByLabelText('食材名稱'), { target: { value: '米' } });
  fireEvent.click(screen.getByRole('button', { name: '加入清單' }));
  await act(async () => {});
  expect(screen.getByText('米', { selector: 'strong' })).toBeInTheDocument();

  fireEvent.click(screen.getByRole('button', { name: '編輯 鮮奶' }));
  expect(screen.getByRole('heading', { name: '編輯食材' })).toBeInTheDocument();
  expect(screen.getByLabelText('數量')).toHaveValue(1);
  fireEvent.click(screen.getByRole('button', { name: '取消編輯' }));
  expect(screen.getByRole('heading', { name: '加入食材' })).toBeInTheDocument();

  fireEvent.click(screen.getByRole('button', { name: '編輯 鮮奶' }));
  records = [records[0], { ...milk, quantity: 2, version: 2 }];
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  fireEvent.change(screen.getByLabelText('數量'), { target: { value: '3' } });
  fireEvent.change(screen.getByLabelText('食材名稱'), { target: { value: '新鮮鮮奶' } });
  fireEvent.click(screen.getByRole('button', { name: '儲存變更' }));
  await act(async () => {});
  expect(screen.getByText(/已重新載入/)).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith(item, expect.objectContaining({
    method: 'PUT', body: expect.stringContaining('"version":1'),
  }));
  expect(screen.getByText('2 瓶 · 冷藏 · 乳製品 · 未設定到期日')).toBeInTheDocument();
});

function deferredResponse() {
  let resolve!: (value: Response) => void;
  let reject!: (reason: Error) => void;
  const response = new Promise<Response>((done, fail) => { resolve = done; reject = fail; });
  return { response, resolve, reject };
}

test.each(['resolve', 'reject'])('a superseded background read can %s without rolling back a local edit', async (outcome) => {
  vi.useFakeTimers();
  const deferredPoll = deferredResponse();
  const deferredWrite = deferredResponse();
  let readCount = 0;
  vi.stubGlobal('fetch', vi.fn(async (path: string) => {
    if (path === inventory) return ++readCount === 2 ? deferredPoll.response : json([milk]);
    if (path === shopping) return json([]);
    if (path === item) return deferredWrite.response;
    throw new Error(path);
  }));
  render(<HouseholdLists householdId="home" />);
  await act(async () => vi.advanceTimersByTimeAsync(30_000));
  expect(readCount).toBe(2);
  const action = screen.getByRole('button', { name: '已用完' });
  fireEvent.click(action);
  expect(action).toBeDisabled();
  await act(async () => {
    if (outcome === 'resolve') deferredPoll.resolve(json([]));
    else deferredPoll.reject(new Error('outdated fetch failed'));
  });
  expect(screen.getByText('鮮奶', { selector: 'strong' })).toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  expect(readCount).toBe(2);
  await act(async () => deferredWrite.resolve(json({ ...milk, status: 'consumed', version: 2 })));
  expect(screen.getByText('庫存已儲存')).toBeInTheDocument();
  expect(screen.queryByText('鮮奶', { selector: 'strong' })).not.toBeInTheDocument();
});

test.each(['resolve', 'reject'])('an inventory save can %s after the household closes', async (outcome) => {
  const deferred = deferredResponse();
  let signal: AbortSignal | null | undefined;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === inventory) return json([milk]);
    if (path === shopping) return json([]);
    if (path === item) { signal = options?.signal; return deferred.response; }
    throw new Error(path);
  });
  vi.stubGlobal('fetch', fetcher);
  const { unmount } = render(<HouseholdLists householdId="home" />);
  fireEvent.click(await screen.findByRole('button', { name: '已用完' }));
  unmount();
  expect(signal?.aborted).toBe(true);
  await act(async () => {
    if (outcome === 'resolve') deferred.resolve(json({ ...milk, status: 'consumed' }));
    else deferred.reject(new Error('old request failed'));
  });
  expect(fetcher.mock.calls.filter(([path]) => path === inventory)).toHaveLength(1);
});

test.each(['resolve', 'reject'])('a conflict reload can %s after the household closes', async (outcome) => {
  const deferred = deferredResponse();
  let reads = 0;
  let signal: AbortSignal | null | undefined;
  vi.stubGlobal('fetch', vi.fn(async (path: string, options?: RequestInit) => {
    if (path === shopping) return json([]);
    if (path === item) return json({}, 409);
    if (++reads === 1) return json([milk]);
    signal = options?.signal;
    return deferred.response;
  }));
  const { unmount } = render(<HouseholdLists householdId="home" />);
  fireEvent.click(await screen.findByRole('button', { name: '已用完' }));
  expect(await screen.findByText(/正在重新載入/)).toBeInTheDocument();
  unmount();
  expect(signal?.aborted).toBe(true);
  await act(async () => {
    if (outcome === 'resolve') deferred.resolve(json([]));
    else deferred.reject(new Error('old request failed'));
  });
});

test.each([401, 403])('a transient poll failure preserves the display and %s clears inaccessible lists', async (denied) => {
  vi.useFakeTimers();
  let status = 200;
  vi.stubGlobal('fetch', vi.fn(async (path: string) => {
    if (path === shopping) return json([]);
    if (status === -1) throw new TypeError('offline');
    return json([milk], status);
  }));
  render(<HouseholdLists householdId="home" />);
  await act(async () => {});
  status = -1;
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  expect(screen.getByText('鮮奶', { selector: 'strong' })).toBeInTheDocument();

  status = 503;
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  expect(screen.getByText('鮮奶', { selector: 'strong' })).toBeInTheDocument();

  status = denied;
  await act(async () => vi.advanceTimersByTimeAsync(15_000));
  expect(screen.getByText(`伺服器錯誤 ${denied}`)).toBeInTheDocument();
  expect(screen.queryByText('鮮奶', { selector: 'strong' })).not.toBeInTheDocument();
});

test.each([[409, 503], [409, 403], [503, 503]])('a %s save reports the result of a failed %s reload', async (saveStatus, reloadStatus) => {
  let reads = 0;
  vi.stubGlobal('fetch', vi.fn(async (path: string) => {
    if (path === shopping) return json([]);
    if (path === item) return json({}, saveStatus);
    return json([milk], ++reads === 1 ? 200 : reloadStatus);
  }));
  render(<HouseholdLists householdId="home" />);
  const consumed = await screen.findByRole('button', { name: '已用完' });
  await act(async () => fireEvent.click(consumed));
  if (reloadStatus === 403) {
    expect(screen.getByText('伺服器錯誤 403')).toBeInTheDocument();
    expect(consumed).not.toBeInTheDocument();
  } else {
    expect(screen.getByText(saveStatus === 409 ? /暫時無法載入/ : '伺服器錯誤 503')).toBeInTheDocument();
    expect(consumed).toBeEnabled();
  }
});

test('pairing-code mode keeps whole-list writes and its data after a rejected save', async () => {
  sessionStorage.setItem('fresh-days-household-code', 'paired');
  let status = 503;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (options?.method === 'PUT') return new Response(null, { status });
    if (path === '/api/inventory') return json([{ ...milk, version: undefined }]);
    return json([{ id: 's1', name: '洋蔥', completed: false }]);
  });
  vi.stubGlobal('fetch', fetcher);
  const { default: App } = await import('./App');
  render(<App />);
  const consumed = await screen.findByRole('button', { name: '已用完' });
  await act(async () => fireEvent.click(consumed));
  expect(screen.getByText('伺服器錯誤 503')).toBeInTheDocument();
  expect(consumed).toBeEnabled();

  status = 204;
  await act(async () => fireEvent.click(screen.getByRole('button', { name: '加入採買清單' })));
  expect(screen.getByText('鹽')).toBeInTheDocument();
  await act(async () => fireEvent.click(screen.getByRole('checkbox', { name: '洋蔥' })));
  expect(screen.getByRole('checkbox', { name: '洋蔥' })).toBeChecked();
  await act(async () => fireEvent.click(screen.getAllByRole('button', { name: '移除' })[0]));
  expect(screen.queryByRole('checkbox', { name: '洋蔥' })).not.toBeInTheDocument();
  await act(async () => fireEvent.click(consumed));
  expect(fetcher).toHaveBeenCalledWith('/api/inventory', expect.objectContaining({
    method: 'PUT', body: expect.stringContaining('"status":"consumed"'),
  }));
  expect(screen.getByText('庫存已儲存')).toBeInTheDocument();
});

test('legacy item defaults, date suggestions, location filters, and recipe display work in a shared household', async () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-11-22T06:00:00Z'));
  const records = [
    { ...milk, id: 'eggs', name: '雞蛋', category: 'protein', expiresAt: '2026-11-22' },
    { ...milk, id: 'greens', name: '菠菜', category: 'vegetable', expiresAt: '2026-11-24' },
    { ...milk, id: 'salt', name: '鹽', location: 'pantry', expiresAt: '2026-12-02' },
    { ...milk, id: 'oil', name: '食用油', location: 'pantry' },
    { id: 'legacy', name: '舊食材', version: 1 },
  ];
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    if (path === inventory) return json(records);
    if (path === shopping) return json([]);
    if (path === inventory + '/item?id=eggs') return json({ ...records[0], status: 'consumed', version: 2 });
    throw new Error(`${options?.method} ${path}`);
  });
  vi.stubGlobal('fetch', fetcher);
  render(<HouseholdLists householdId="home" />);
  await act(async () => {});
  expect(screen.getByText('今天到期')).toBeInTheDocument();
  expect(screen.getByText('3 天內到期')).toBeInTheDocument();
  expect(screen.getByText('缺少材料：無')).toBeInTheDocument();

  fireEvent.click(screen.getByRole('button', { name: '編輯 舊食材' }));
  expect(screen.getByLabelText('數量')).toHaveValue(1);
  expect(screen.getByLabelText('單位')).toHaveValue('份');
  expect(screen.getByLabelText('保存位置')).toHaveValue('fridge');
  expect(screen.getByLabelText('分類')).toHaveValue('other');
  fireEvent.click(screen.getByRole('button', { name: '取消編輯' }));

  const input = screen.getByLabelText('食材名稱');
  const form = input.closest('form')!;
  fireEvent.change(input, { target: { value: ' ' } });
  fireEvent.submit(form);
  expect(fetcher).toHaveBeenCalledTimes(2);
  fireEvent.change(input, { target: { value: '牛奶' } });
  expect(screen.getByLabelText('到期日')).toHaveValue('2026-11-29');
  fireEvent.change(screen.getByLabelText('到期日'), { target: { value: '2026-11-27' } });
  fireEvent.change(input, { target: { value: '牛奶一瓶' } });
  expect(screen.getByLabelText('到期日')).toHaveValue('2026-11-27');
  fireEvent.change(screen.getByLabelText('數量'), { target: { value: '4' } });
  fireEvent.change(screen.getByLabelText('單位'), { target: { value: '瓶' } });
  fireEvent.change(screen.getByLabelText('保存位置'), { target: { value: 'freezer' } });
  fireEvent.change(screen.getByLabelText('分類'), { target: { value: 'other' } });

  fireEvent.click(screen.getByRole('button', { name: '冷藏' }));
  expect(screen.queryByRole('button', { name: '編輯 鹽' })).not.toBeInTheDocument();
  const eggRow = screen.getByRole('button', { name: '編輯 雞蛋' }).closest('li')!;
  fireEvent.click(eggRow.querySelectorAll('button')[1]);
  expect(screen.getByRole('button', { name: '加入清單' })).toBeDisabled();
  fireEvent.submit(form);
  await act(async () => {});
  expect(fetcher).toHaveBeenCalledTimes(3);
  expect(screen.getByText('庫存已儲存')).toBeInTheDocument();
});
