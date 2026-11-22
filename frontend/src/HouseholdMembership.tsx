import { useState } from 'react';

type Member = { userId: string; email: string; role: 'owner' | 'member' };
type MembershipChange = {
  path: string;
  method: 'DELETE' | 'POST';
  label: string;
  prompt: string;
  onSuccess: () => void;
};

export default function HouseholdMembership({ householdId, role, onLeave, onTransfer }: {
  householdId: string;
  role: Member['role'];
  onLeave: (id: string) => void;
  onTransfer: (id: string) => void;
}) {
  const [members, setMembers] = useState<Member[] | null>(null);
  const [confirmation, setConfirmation] = useState<MembershipChange | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const path = '/api/households/' + householdId + '/members';

  async function loadMembers() {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(path);
      if (!response.ok) throw new Error('HTTP ' + response.status);
      setMembers(await response.json() as Member[]);
    } catch {
      setError('無法載入家庭成員，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  async function apply(change: MembershipChange) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(change.path, { method: change.method });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      setConfirmation(null);
      change.onSuccess();
    } catch {
      setError('無法' + change.label + '，請再試一次。');
    } finally {
      setBusy(false);
    }
  }

  function choose(change: MembershipChange) {
    setError('');
    setConfirmation(change);
  }

  return <section className="panel" aria-label="家庭成員">
    {role === 'owner'
      ? <button className="secondary" disabled={busy} onClick={() => void loadMembers()}>管理家庭成員</button>
      : <button className="secondary" disabled={busy} onClick={() => choose({
        path: path + '/me',
        method: 'DELETE',
        label: '退出家庭',
        prompt: '退出後將失去此家庭的存取權，已新增的食材與採買項目會保留。',
        onSuccess: () => onLeave(householdId),
      })}>退出家庭</button>}
    {role === 'owner' && members !== null && <ul>
      {members.map((member) => <li className="item" key={member.userId}>
        <strong>{member.email}</strong>
        {member.role === 'owner' ? <span>擁有者</span> : <div>
          <button className="secondary" disabled={busy} onClick={() => choose({
            path: path + '/' + member.userId,
            method: 'DELETE',
            label: '移除成員',
            prompt: '要移除 ' + member.email + ' 的家庭存取權嗎？',
            onSuccess: () => setMembers((current) => current!.filter((entry) => entry.userId !== member.userId)),
          })}>移除成員</button>
          <button className="secondary" disabled={busy} onClick={() => choose({
            path: path + '/' + member.userId + '/transfer',
            method: 'POST',
            label: '轉移擁有權',
            prompt: '要將擁有權轉移給 ' + member.email + ' 嗎？轉移後你將成為一般成員。',
            onSuccess: () => onTransfer(householdId),
          })}>轉移擁有權</button>
        </div>}
      </li>)}
    </ul>}
    {confirmation && <div>
      <p>{confirmation.prompt}</p>
      <button className="primary" disabled={busy} onClick={() => void apply(confirmation)}>確認{confirmation.label}</button>
      <button className="secondary" disabled={busy} onClick={() => {
        setConfirmation(null);
        setError('');
      }}>取消</button>
    </div>}
    {error && <p role="alert">{error}</p>}
  </section>;
}
