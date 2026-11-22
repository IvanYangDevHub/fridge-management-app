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
    .mockResolvedValueOnce(json({ householdId: 'legacy-id' }, 201))
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

  await user.click(screen.getByRole('button', { name: '登出' }));
  expect(await screen.findByRole('button', { name: '使用 Google 登入' })).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/auth/logout', { method: 'POST' });
});

test('a returning account without claim permission can sign out', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(json({ ...signedIn, canClaimLegacy: false })));
  render(<AuthApp />);
  expect(await screen.findByText('owner@gmail.com')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: '登出' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '認領舊家庭' })).not.toBeInTheDocument();
});
