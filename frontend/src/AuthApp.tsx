import { useCallback, useEffect, useRef, useState } from 'react';
import LegacyApp from './App';
import HouseholdApp from './HouseholdApp';

export type Session = { userId: string; email: string; canClaimLegacy: boolean };
type Product = { mode: 'phase-one' } | { mode: 'phase-two'; googleClientId: string };
type Startup = { mode: 'loading' } | { mode: 'error' } |
  { mode: 'phase-one' } | { mode: 'phase-two'; googleClientId: string; session: Session | null };
type GoogleButtonApi = {
  initialize(options: { client_id: string; callback: (response: { credential: string }) => void }): void;
  renderButton(element: HTMLElement, options: { theme: string; size: string; locale: string }): void;
};

declare global {
  interface Window {
    google?: { accounts: { id: GoogleButtonApi } };
  }
}

function checked(response: Response) {
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response;
}

export default function AuthApp() {
  const [startup, setStartup] = useState<Startup>({ mode: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    async function load() {
      const product: Product = await checked(await fetch('/api/auth/config', options)).json();
      if (product.mode === 'phase-one') {
        setStartup(product);
        return;
      }
      const current = await fetch('/api/auth/session', options);
      const session = current.status === 401 ? null : await checked(current).json() as Session;
      setStartup({ ...product, session });
    }

    void load().catch(() => {
      if (!controller.signal.aborted) setStartup({ mode: 'error' });
    });
    return () => controller.abort();
  }, [attempt]);

  if (startup.mode === 'phase-one') return <LegacyApp />;
  if (startup.mode === 'phase-two') return <GoogleSignIn clientId={startup.googleClientId} initialSession={startup.session} />;

  return <main className="app-shell"><section className="panel">
    <h1>鮮日子</h1>
    {startup.mode === 'loading' ? <p role="status">正在連線…</p> :
      <><p role="alert">無法連線到鮮日子。</p><button className="secondary" onClick={() => {
        setStartup({ mode: 'loading' });
        setAttempt(attempt + 1);
      }}>重試</button></>}
  </section></main>;
}

function GoogleSignIn({ clientId, initialSession }: { clientId: string; initialSession: Session | null }) {
  const [session, setSession] = useState(initialSession);
  const [invitationToken, setInvitationToken] = useState(() => new URLSearchParams(location.hash.slice(1)).get('invite'));
  const [preferredHouseholdId, setPreferredHouseholdId] = useState('');
  const [householdVersion, setHouseholdVersion] = useState(0);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  const signIn = useCallback(async (credential: string) => {
    setBusy(true);
    setMessage('');
    try {
      const response = await fetch('/api/auth/google', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ credential }),
      });
      setSession(await checked(response).json() as Session);
    } catch {
      setMessage('Google 登入失敗，請再試一次。');
    } finally {
      setBusy(false);
    }
  }, []);

  async function signOut() {
    setBusy(true);
    setMessage('');
    try {
      const response = await fetch('/api/auth/logout', { method: 'POST' });
      if (response.status !== 401) checked(response);
      setSession(null);
      setPreferredHouseholdId('');
    } catch {
      setMessage('登出失敗，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  async function acceptInvitation(token: string) {
    setBusy(true);
    setMessage('');
    try {
      const response = await fetch('/api/households/invitations/redeem', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token }),
      });
      const household: { id: string; name: string } = await checked(response).json();
      history.replaceState(null, '', location.pathname + location.search);
      setInvitationToken(null);
      setPreferredHouseholdId(household.id);
      setHouseholdVersion((version) => version + 1);
      setMessage(`已加入${household.name}。`);
    } catch {
      setMessage('無法接受家庭邀請，請確認連結與登入的 Google 帳號。');
    } finally {
      setBusy(false);
    }
  }

  async function claim(current: Session) {
    setBusy(true);
    setMessage('');
    try {
      const response = await fetch('/api/households/legacy/claim', { method: 'POST' });
      if (response.status === 401) {
        setSession(null);
        setMessage('登入已過期，請重新登入。');
        return;
      }
      if (response.status === 403 || response.status === 409) {
        setSession({ ...current, canClaimLegacy: false });
        setMessage(response.status === 403 ? '這個 Google 帳號無法認領舊家庭。' : '舊家庭已被認領。');
        return;
      }
      checked(response);
      setSession({ ...current, canClaimLegacy: false });
      setMessage('已認領舊家庭。');
      setHouseholdVersion((version) => version + 1);
    } catch {
      setMessage('無法認領舊家庭，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  return <main className="app-shell">
    <header className="hero"><div><h1>鮮日子</h1><p className="muted">把冰箱裡的每一份新鮮，好好記下來。</p></div></header>
    <section className="panel">
      {session === null
        ? <><h2>登入</h2>
          {invitationToken && <p>收到家庭邀請，請使用受邀的 Google 帳號登入。</p>}
          <div inert={busy}><GoogleButton clientId={clientId} onCredential={signIn} /></div></>
        : <><h2>Google 帳號</h2><p>{session.email}</p><div className="item-actions">
          {invitationToken &&
            <button className="primary" disabled={busy} onClick={() => void acceptInvitation(invitationToken)}>接受家庭邀請</button>}
          {session.canClaimLegacy &&
            <button className="primary" disabled={busy} onClick={() => void claim(session)}>認領舊家庭</button>}
          <button className="secondary" disabled={busy} onClick={() => void signOut()}>登出</button>
        </div></>}
      <p role="status">{message}</p>
    </section>
    {session && <HouseholdApp key={`${session.userId}:${householdVersion}`} preferredHouseholdId={preferredHouseholdId} />}
  </main>;
}

function GoogleButton({ clientId, onCredential }: { clientId: string; onCredential: (credential: string) => Promise<void> }) {
  const container = useRef<HTMLDivElement>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    function renderButton() {
      const identity = window.google?.accounts.id;
      if (!identity) {
        setFailed(true);
        return;
      }
      const element = container.current!;
      element.replaceChildren();
      identity.initialize({ client_id: clientId, callback: ({ credential }) => { void onCredential(credential); } });
      identity.renderButton(element, { theme: 'outline', size: 'large', locale: 'zh_TW' });
    }
    if (window.google) {
      renderButton();
      return;
    }

    const script = document.createElement('script');
    script.src = 'https://accounts.google.com/gsi/client';
    script.async = true;
    script.onload = renderButton;
    script.onerror = () => setFailed(true);
    document.head.append(script);
    return () => {
      script.onload = null;
      script.onerror = null;
      script.remove();
    };
  }, [clientId, onCredential]);

  return <><div ref={container} />{failed && <p role="alert">無法載入 Google 登入，請重新整理後再試。</p>}</>;
}
