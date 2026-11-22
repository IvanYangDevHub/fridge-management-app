import { useState, type FormEvent } from 'react';

type PendingInvitation = { id: string; email: string; expiresAt: string };
type CreatedInvitation = PendingInvitation & { token: string };

export default function HouseholdInvitations({ householdId }: { householdId: string }) {
  const [pending, setPending] = useState<PendingInvitation[] | null>(null);
  const [issued, setIssued] = useState<CreatedInvitation | null>(null);
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const path = `/api/households/${householdId}/invitations`;

  async function load() {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(path);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      setPending(await response.json() as PendingInvitation[]);
      setIssued(null);
    } catch {
      setError('無法載入待處理的邀請，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError('');
    try {
      const response = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: email.trim() }),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const invitation: CreatedInvitation = await response.json();
      setPending((current) => [...current!, invitation]);
      setIssued(invitation);
      setEmail('');
    } catch {
      setError('無法建立邀請，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  async function revoke(id: string) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(`${path}/${id}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      setPending((current) => current!.filter((invitation) => invitation.id !== id));
      setIssued((current) => current?.id === id ? null : current);
    } catch {
      setError('無法撤銷邀請，請確認邀請狀態後再試。');
    } finally {
      setBusy(false);
    }
  }

  return <section className="panel" aria-label="家庭邀請">
    <button className="secondary" disabled={busy} onClick={() => void load()}>
      {pending === null ? '管理家庭邀請' : '重新載入待處理邀請'}
    </button>
    {error && <p role="alert">{error}</p>}
    {pending !== null && <>
      <form className="form-grid" onSubmit={create}>
        <label>受邀者的 Google Email
          <input type="email" value={email} maxLength={254} required onChange={(event) => setEmail(event.target.value)} />
        </label>
        <button className="primary" disabled={busy || !email.trim()}>建立邀請</button>
      </form>
      {issued && <label>邀請連結
        <input readOnly value={new URL(`/#invite=${issued.token}`, window.location.origin).href}
          onFocus={(event) => event.target.select()} />
      </label>}
      {pending.length === 0 ? <p>目前沒有待處理的邀請。</p> :
        <ul>
          {pending.map((invitation) => <li className="item" key={invitation.id}>
            <div>
              <strong>{invitation.email}</strong>
              <p>到期 <time dateTime={invitation.expiresAt}>{new Date(invitation.expiresAt).toLocaleString('zh-TW')}</time></p>
            </div>
            <button className="secondary" disabled={busy} onClick={() => void revoke(invitation.id)}>撤銷邀請</button>
          </li>)}
        </ul>}
    </>}
  </section>;
}
