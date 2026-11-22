// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import AuthApp from './AuthApp';

const home = { id: 'home', name: '台中家', role: 'owner' };
const cottage = { id: 'cottage', name: '台北家', role: 'member' };
const session = { userId: 'alice', email: 'alice@gmail.com', canClaimLegacy: false };
const invitation = { id: 'invite-id', email: 'family@gmail.com', expiresAt: '2030-01-08T08:00:00Z', token: 'invitation-token' };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const config = () => json({ mode: 'phase-two', googleClientId: 'test-google-client' });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  history.replaceState(null, '', '/');
});

test('an owner creates a shareable invitation and can revoke it after reopening the app', async () => {
  let pending = false;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return config();
      case '/api/auth/session': return json(session);
      case '/api/households': return json([home, cottage]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping':
      case '/api/households/cottage/inventory':
      case '/api/households/cottage/shopping': return json([]);
      case '/api/households/home/invitations':
        if (options?.method === 'POST') {
          pending = true;
          return json(invitation, 201);
        }
        return json(pending ? [{ ...invitation, token: undefined }] : []);
      case '/api/households/home/invitations/invite-id':
        pending = false;
        return new Response(null, { status: 204 });
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  const firstVisit = render(<AuthApp />);
  await user.click(await screen.findByRole('button', { name: '管理家庭邀請' }));
  await user.type(await screen.findByLabelText('受邀者的 Google Email'), 'family@gmail.com');
  await user.click(screen.getByRole('button', { name: '建立邀請' }));
  expect(await screen.findByLabelText('邀請連結')).toHaveValue(new URL('/#invite=invitation-token', location.origin).href);
  expect(fetcher).toHaveBeenCalledWith('/api/households/home/invitations', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ email: 'family@gmail.com' }),
  }));
  firstVisit.unmount();

  render(<AuthApp />);
  await user.click(await screen.findByRole('button', { name: '管理家庭邀請' }));
  expect(await screen.findByText('family@gmail.com')).toBeInTheDocument();
  expect(screen.queryByLabelText('邀請連結')).toBeNull();
  await user.click(screen.getByRole('button', { name: '撤銷邀請' }));
  await screen.findByText('目前沒有待處理的邀請。');
  expect(fetcher).toHaveBeenCalledWith('/api/households/home/invitations/invite-id', { method: 'DELETE' });
  await user.selectOptions(screen.getByLabelText('目前家庭'), cottage.id);
  expect(screen.queryByRole('button', { name: '管理家庭邀請' })).toBeNull();
});

test('an invite survives Google sign-in and opens the newly joined household', async () => {
  history.replaceState(null, '', '/?source=family#invite=invitation-token');
  let credentialCallback!: (response: { credential: string }) => void;
  vi.stubGlobal('google', { accounts: { id: {
    initialize: ({ callback }: { callback: typeof credentialCallback }) => { credentialCallback = callback; },
    renderButton: (node: HTMLElement) => {
      const button = document.createElement('button');
      button.textContent = '使用 Google 登入';
      button.onclick = () => credentialCallback({ credential: 'google-test' });
      node.append(button);
    },
  } } });

  let joined = false;
  const fetcher = vi.fn(async (path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return config();
      case '/api/auth/session': return json({}, 401);
      case '/api/auth/google': return json(session);
      case '/api/households': return json(joined ? [home, cottage] : [home]);
      case '/api/households/invitations/redeem':
        joined = true;
        return json(cottage, 201);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping':
      case '/api/households/cottage/inventory':
      case '/api/households/cottage/shopping': return json([]);
      default: throw new Error(`${path} ${options?.method}`);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  const user = userEvent.setup();
  expect(await screen.findByText('收到家庭邀請，請使用受邀的 Google 帳號登入。')).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: '使用 Google 登入' }));
  await user.click(await screen.findByRole('button', { name: '接受家庭邀請' }));
  expect(await screen.findByText('已加入台北家。')).toBeInTheDocument();
  expect(await screen.findByLabelText('目前家庭')).toHaveValue(cottage.id);
  expect(fetcher).toHaveBeenCalledWith('/api/households/invitations/redeem', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ token: 'invitation-token' }),
  }));
  expect(location.hash).toBe('');
  expect(location.search).toBe('?source=family');
});

test('an owner can retry failed invitation actions and revoke the active share link', async () => {
  const existing = { id: 'previous', email: 'previous@gmail.com', expiresAt: invitation.expiresAt };
  let loadCount = 0;
  let createCount = 0;
  let revokeCount = 0;
  let completeCreate!: (response: Response) => void;
  const createRequest = new Promise<Response>((resolve) => { completeCreate = resolve; });
  vi.stubGlobal('fetch', vi.fn(async (path: string, options?: RequestInit) => {
    switch (path) {
      case '/api/auth/config': return config();
      case '/api/auth/session': return json(session);
      case '/api/households': return json([home]);
      case '/api/households/home/inventory':
      case '/api/households/home/shopping': return json([]);
      case '/api/households/home/invitations':
        if (options?.method === 'POST') return createCount++ === 0 ? createRequest : json(invitation, 201);
        return loadCount++ === 0 ? json({}, 503) : json([existing]);
      case '/api/households/home/invitations/previous':
        return new Response(null, { status: 204 });
      case '/api/households/home/invitations/invite-id':
        return new Response(null, { status: revokeCount++ === 0 ? 503 : 204 });
      default: throw new Error(path);
    }
  }));
  render(<AuthApp />);
  const user = userEvent.setup();
  const manage = await screen.findByRole('button', { name: '管理家庭邀請' });
  await user.click(manage);
  expect(await screen.findByRole('alert')).toHaveTextContent('無法載入待處理的邀請');
  await user.click(manage);
  const create = await screen.findByRole('button', { name: '建立邀請' });
  expect(create).toBeDisabled();
  await user.type(screen.getByLabelText('受邀者的 Google Email'), 'family@gmail.com');
  await user.click(create);
  expect(create).toBeDisabled();
  await act(async () => completeCreate(json({}, 503)));
  expect(await screen.findByRole('alert')).toHaveTextContent('無法建立邀請');
  await user.click(create);

  const link = await screen.findByLabelText<HTMLInputElement>('邀請連結');
  await user.click(link);
  expect(link.selectionEnd).toBe(link.value.length);
  const previousRow = screen.getByText(existing.email).closest('li')!;
  await user.click(within(previousRow).getByRole('button', { name: '撤銷邀請' }));
  expect(screen.queryByText(existing.email)).toBeNull();
  expect(link).toBeInTheDocument();

  const revoke = screen.getByRole('button', { name: '撤銷邀請' });
  await user.click(revoke);
  expect(await screen.findByRole('alert')).toHaveTextContent('無法撤銷邀請');
  expect(link).toBeInTheDocument();
  await user.click(revoke);
  await screen.findByText('目前沒有待處理的邀請。');
  expect(screen.queryByLabelText('邀請連結')).toBeNull();
  expect(screen.queryByRole('alert')).toBeNull();
});

test('an unavailable invitation leaves the link in place so the user can switch accounts', async () => {
  history.replaceState(null, '', '/#invite=invitation-token');
  const fetcher = vi.fn(async (path: string) => {
    switch (path) {
      case '/api/auth/config': return config();
      case '/api/auth/session': return json(session);
      case '/api/households': return json([]);
      case '/api/households/invitations/redeem': return json({}, 404);
      default: throw new Error(path);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  render(<AuthApp />);
  await userEvent.click(await screen.findByRole('button', { name: '接受家庭邀請' }));
  expect(await screen.findByText('無法接受家庭邀請，請確認連結與登入的 Google 帳號。')).toBeInTheDocument();
  expect(location.hash).toBe('#invite=invitation-token');
  expect(screen.queryByLabelText('目前家庭')).toBeNull();
});
