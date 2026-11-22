// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import AuthApp from './AuthApp';

const signedIn = { userId: 'owner-id', email: 'owner@gmail.com', canClaimLegacy: true };
const phaseTwo = { mode: 'phase-two', googleClientId: 'test-google-client' };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status });
const response = (status: number) => new Response(null, { status });

function installGoogle() {
  let receiveCredential: (response: { credential: string }) => void;
  const initialize = vi.fn(({ callback }) => { receiveCredential = callback; });
  const renderButton = vi.fn((node: HTMLElement) => {
    const button = document.createElement('button');
    button.textContent = '使用 Google 登入';
    button.onclick = () => receiveCredential({ credential: 'google-token' });
    node.append(button);
  });
  vi.stubGlobal('google', { accounts: { id: { initialize, renderButton } } });
  return initialize;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

test('phase one keeps its pairing form', async () => {
  const fetcher = vi.fn().mockResolvedValue(json({ mode: 'phase-one' }));
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  expect(await screen.findByLabelText('家庭配對碼')).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledTimes(1);
});

test('Google sign-in submits the credential and lets the eligible owner claim and sign out', async () => {
  const initialize = installGoogle();
  const fetcher = vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(response(401))
    .mockResolvedValueOnce(json(signedIn))
    .mockResolvedValueOnce(json([]))
    .mockResolvedValueOnce(json({ householdId: 'legacy-id' }, 201))
    .mockResolvedValueOnce(json([{ id: 'legacy-id', name: '舊家庭', role: 'owner' }]))
    .mockResolvedValueOnce(json([]))
    .mockResolvedValueOnce(json([]))
    .mockResolvedValueOnce(response(204));
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: '使用 Google 登入' }));
  expect(initialize).toHaveBeenCalledWith(expect.objectContaining({ client_id: 'test-google-client' }));
  expect(fetcher).toHaveBeenCalledWith('/api/auth/google', expect.objectContaining({
    method: 'POST',
    body: JSON.stringify({ credential: 'google-token' }),
  }));
  expect(screen.getByText('owner@gmail.com')).toBeInTheDocument();
  expect(screen.queryByLabelText('家庭配對碼')).not.toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: '認領舊家庭' }));
  expect(await screen.findByText('已認領舊家庭。')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '認領舊家庭' })).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/households/legacy/claim', { method: 'POST' });
  expect(await screen.findByLabelText('目前家庭')).toHaveValue('legacy-id');
  expect(await screen.findByRole('heading', { name: '冰箱庫存' })).toBeInTheDocument();

  await user.click(screen.getByRole('button', { name: '登出' }));
  expect(await screen.findByRole('button', { name: '使用 Google 登入' })).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/auth/logout', { method: 'POST' });
});

test('a returning account without claim permission can sign out', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(json({ ...signedIn, canClaimLegacy: false }))
    .mockResolvedValueOnce(json([])));
  render(<AuthApp />);
  expect(await screen.findByText('owner@gmail.com')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: '登出' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '認領舊家庭' })).not.toBeInTheDocument();
});


test.each([
  [204, '已刪除鮮日子帳號。'],
  [401, '登入已過期，請重新登入。'],
] as const)('account deletion confirms, handles ownership and failed requests, and clears the session on %s', async (outcome, message) => {
  installGoogle();
  const attempts = [409, 503, outcome];
  const fetcher = vi.fn(async (path: string) => {
    switch (path) {
      case '/api/auth/config': return json(phaseTwo);
      case '/api/auth/session': return json(signedIn);
      case '/api/households': return json([]);
      case '/api/auth/account?confirm=true': return response(attempts.shift()!);
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  render(<AuthApp />);

  const remove = await screen.findByRole('button', { name: '刪除個人帳號' });
  await user.click(remove);
  expect(screen.getByText(/共用清單.*保留/)).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '取消' }));
  expect(fetcher).not.toHaveBeenCalledWith('/api/auth/account?confirm=true', expect.anything());
  await user.click(remove);
  await user.click(screen.getByRole('button', { name: '確認刪除個人帳號' }));
  expect(await screen.findByText('請先轉移擁有權或刪除你擁有的所有家庭。')).toBeInTheDocument();
  await user.click(remove);
  await user.click(screen.getByRole('button', { name: '確認刪除個人帳號' }));
  expect(await screen.findByText('無法刪除個人帳號，請再試一次。')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '確認刪除個人帳號' }));

  expect(await screen.findByRole('button', { name: '使用 Google 登入' })).toBeInTheDocument();
  expect(screen.getByText(message)).toBeInTheDocument();
  expect(screen.queryByText('owner@gmail.com')).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/auth/account?confirm=true', { method: 'DELETE' });
});
