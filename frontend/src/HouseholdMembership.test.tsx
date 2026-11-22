// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import HouseholdApp from './HouseholdApp';

const home = { id: 'home', name: '台中家', role: 'member' };
const other = { id: 'other', name: '台北家', role: 'owner' };
const json = (value: unknown) => new Response(JSON.stringify(value));
const status = (code: number) => new Response(null, { status: code });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('a member can cancel leaving, retry a failure, and return to their other household', async () => {
  let failures = 1;
  const fetcher = vi.fn(async (path: string) => {
    switch (path) {
      case '/api/households': return json([home, other]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping':
      case '/api/households/other/inventory':
      case '/api/households/other/shopping': return json([]);
      case '/api/households/home/members/me': return status(failures-- > 0 ? 503 : 204);
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  render(<HouseholdApp preferredHouseholdId="home" />);

  const leave = await screen.findByRole('button', { name: '退出家庭' });
  await user.click(leave);
  await user.click(screen.getByRole('button', { name: '取消' }));
  expect(fetcher.mock.calls.map(([path]) => path)).not.toContain('/api/households/home/members/me');
  await user.click(leave);
  await user.click(screen.getByRole('button', { name: '確認退出家庭' }));
  expect(await screen.findByText('無法退出家庭，請再試一次。')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '確認退出家庭' }));

  expect(await screen.findByRole('button', { name: '管理家庭邀請' })).toBeInTheDocument();
  expect(screen.getByLabelText('目前家庭')).toHaveValue('other');
  expect(screen.queryByRole('option', { name: '台中家' })).toBeNull();
  expect(fetcher).toHaveBeenCalledWith('/api/households/home/members/me', { method: 'DELETE' });
});


test('an owner manages the household roster, confirms removal, and loses owner controls after transferring', async () => {
  const ownerHousehold = { ...home, role: 'owner' };
  const owner = { userId: 'owner', email: 'owner@gmail.com', role: 'owner' };
  const member = { userId: 'member', email: 'member@gmail.com', role: 'member' };
  const successor = { userId: 'successor', email: 'successor@gmail.com', role: 'member' };
  const path = '/api/households/home/members';
  let rosterFailures = 1;
  let removalFailures = 1;
  const fetcher = vi.fn(async (url: string) => {
    switch (url) {
      case '/api/households': return json([ownerHousehold, other]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping': return json([]);
      case path: return rosterFailures-- > 0 ? status(503) : json([owner, member, successor]);
      case path + '/member': return status(removalFailures-- > 0 ? 503 : 204);
      case path + '/successor/transfer': return status(204);
      default: throw new Error(url);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  render(<HouseholdApp preferredHouseholdId="home" />);

  const manage = await screen.findByRole('button', { name: '管理家庭成員' });
  expect(screen.queryByRole('button', { name: '退出家庭' })).toBeNull();
  await user.click(manage);
  expect(await screen.findByText('無法載入家庭成員，請再試一次。')).toBeInTheDocument();
  await user.click(manage);
  const ownerRow = (await screen.findByText('owner@gmail.com')).closest('li')!;
  expect(within(ownerRow).getByText('擁有者')).toBeInTheDocument();
  expect(within(ownerRow).queryByRole('button')).toBeNull();

  const memberRow = screen.getByText('member@gmail.com').closest('li')!;
  await user.click(within(memberRow).getByRole('button', { name: '移除成員' }));
  await user.click(screen.getByRole('button', { name: '確認移除成員' }));
  expect(await screen.findByText('無法移除成員，請再試一次。')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '確認移除成員' }));
  expect(screen.queryByText('member@gmail.com')).toBeNull();
  expect(fetcher).toHaveBeenCalledWith(path + '/member', { method: 'DELETE' });

  const successorRow = screen.getByText('successor@gmail.com').closest('li')!;
  await user.click(within(successorRow).getByRole('button', { name: '轉移擁有權' }));
  expect(screen.getByText(/轉移.*successor@gmail.com/)).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '確認轉移擁有權' }));
  expect(await screen.findByRole('button', { name: '退出家庭' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '管理家庭邀請' })).toBeNull();
  expect(screen.queryByRole('button', { name: '管理家庭成員' })).toBeNull();
  expect(screen.getByLabelText('目前家庭')).toHaveValue('home');
  expect(fetcher).toHaveBeenCalledWith(path + '/successor/transfer', { method: 'POST' });
});


test('only owners can confirm household deletion after seeing the backup notice', async () => {
  let fails = true;
  const fetcher = vi.fn(async (path: string) => {
    switch (path) {
      case '/api/households': return json([home, other]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping':
      case '/api/households/other/inventory':
      case '/api/households/other/shopping': return json([]);
      case '/api/households/other?confirm=true': {
        const response = status(fails ? 503 : 204);
        fails = false;
        return response;
      }
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  render(<HouseholdApp preferredHouseholdId="home" />);

  const picker = await screen.findByLabelText('目前家庭');
  expect(screen.queryByRole('button', { name: '刪除家庭' })).not.toBeInTheDocument();
  await user.selectOptions(picker, 'other');
  const remove = screen.getByRole('button', { name: '刪除家庭' });
  await user.click(remove);
  expect(screen.getByText(/備份保留期/)).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '取消' }));
  expect(fetcher).not.toHaveBeenCalledWith('/api/households/other?confirm=true', expect.anything());
  await user.click(remove);
  await user.click(screen.getByRole('button', { name: '確認刪除家庭' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('無法刪除家庭，請再試一次。');
  await user.click(screen.getByRole('button', { name: '確認刪除家庭' }));

  expect(picker).toHaveValue('home');
  expect(screen.queryByRole('option', { name: '台北家' })).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/households/other?confirm=true', { method: 'DELETE' });
});
