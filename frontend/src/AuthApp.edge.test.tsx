// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import AuthApp from './AuthApp';

const phaseTwo = { mode: 'phase-two', googleClientId: 'test-google-client' };
const owner = { userId: 'owner-id', email: 'owner@gmail.com', canClaimLegacy: true };
const json = (body: unknown) => new Response(JSON.stringify(body));
const response = (status: number) => new Response(null, { status });

function installGoogle() {
  let callback: (response: { credential: string }) => void;
  vi.stubGlobal('google', { accounts: { id: {
    initialize: (options: { callback: typeof callback }) => { callback = options.callback; },
    renderButton: (root: HTMLElement) => {
      const button = document.createElement('button');
      button.textContent = '使用 Google 登入';
      button.onclick = () => callback({ credential: 'test-credential' });
      root.append(button);
    },
  } } });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('a failed connection shows an error with a retry', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(response(503))
    .mockResolvedValueOnce(json({ mode: 'phase-one' })));
  render(<AuthApp />);
  expect(screen.getByText('正在連線…')).toBeInTheDocument();
  expect(await screen.findByRole('alert')).toHaveTextContent('無法連線到鮮日子。');
  await userEvent.click(screen.getByRole('button', { name: '重試' }));
  expect(await screen.findByLabelText('家庭配對碼')).toBeInTheDocument();
});

test('unmount aborts an in-flight session lookup', async () => {
  let requestSignal!: AbortSignal;
  const fetcher = vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockImplementationOnce((_url, options: RequestInit) => {
      requestSignal = options.signal!;
      return new Promise((_, reject) => requestSignal.addEventListener('abort', () => reject(new Error('aborted'))));
    });
  vi.stubGlobal('fetch', fetcher);
  const view = render(<AuthApp />);
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
  await act(async () => view.unmount());
  expect(requestSignal.aborted).toBe(true);
});

test('loads the Google button only when needed and reports a rejected credential', async () => {
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(response(401))
    .mockResolvedValueOnce(response(401)));
  render(<AuthApp />);
  await screen.findByRole('heading', { name: '登入' });
  const script = document.querySelector('script[src="https://accounts.google.com/gsi/client"]')!;
  expect(script).toBeInTheDocument();
  installGoogle();
  await act(async () => script.dispatchEvent(new Event('load')));
  await userEvent.click(screen.getByRole('button', { name: '使用 Google 登入' }));
  expect(await screen.findByText('Google 登入失敗，請再試一次。')).toBeInTheDocument();
});

test.each(['error', 'load'])('reports a failed Google script on %s', async (eventName) => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(json(phaseTwo)).mockResolvedValueOnce(response(401)));
  render(<AuthApp />);
  await screen.findByRole('heading', { name: '登入' });
  const script = document.querySelector('script[src="https://accounts.google.com/gsi/client"]')!;
  await act(async () => script.dispatchEvent(new Event(eventName)));
  expect(await screen.findByRole('alert')).toHaveTextContent('無法載入 Google 登入，請重新整理後再試。');
});

test.each([
  [401, '登入已過期，請重新登入。', false],
  [403, '這個 Google 帳號無法認領舊家庭。', false],
  [409, '舊家庭已被認領。', false],
  [503, '無法認領舊家庭，請再試一次。', true],
])('handles claim status %s', async (status, message, canRetry) => {
  installGoogle();
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(json(owner))
    .mockResolvedValueOnce(json([]))
    .mockResolvedValueOnce(response(status)));
  render(<AuthApp />);
  await userEvent.click(await screen.findByRole('button', { name: '認領舊家庭' }));
  expect(await screen.findByText(message)).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: '認領舊家庭' }) !== null).toBe(canRetry);
});

test('logout can retry after a service failure and clears an already expired session', async () => {
  installGoogle();
  vi.stubGlobal('fetch', vi.fn()
    .mockResolvedValueOnce(json(phaseTwo))
    .mockResolvedValueOnce(json(owner))
    .mockResolvedValueOnce(json([]))
    .mockResolvedValueOnce(response(503))
    .mockResolvedValueOnce(response(401)));
  render(<AuthApp />);
  await userEvent.click(await screen.findByRole('button', { name: '登出' }));
  expect(await screen.findByText('登出失敗，請再試一次。')).toBeInTheDocument();
  expect(screen.getByText('owner@gmail.com')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: '登出' }));
  expect(await screen.findByRole('button', { name: '使用 Google 登入' })).toBeInTheDocument();
});

test('the shipped entry point checks the cutover phase', async () => {
  vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(json({ mode: 'phase-one' }))));
  const root = document.createElement('div');
  root.id = 'root';
  document.body.append(root);
  await act(async () => { await import('./main'); });
  expect(await screen.findByLabelText('家庭配對碼')).toBeInTheDocument();
  root.remove();
});
