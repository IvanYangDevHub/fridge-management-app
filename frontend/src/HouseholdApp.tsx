import { useEffect, useState, type FormEvent } from 'react';
import { HouseholdLists } from './App';

type Household = { id: string; name: string; role: 'owner' | 'member' };

export default function HouseholdApp() {
  const [households, setHouseholds] = useState<Household[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [name, setName] = useState('');
  const [creating, setCreating] = useState(false);
  const [createFailed, setCreateFailed] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const response = await fetch('/api/households', { signal: controller.signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const result: Household[] = await response.json();
        if (controller.signal.aborted) return;
        setHouseholds(result);
        setSelectedId(result[0]?.id ?? '');
      } catch {
        if (!controller.signal.aborted) setLoadFailed(true);
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [attempt]);

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setCreating(true);
    setCreateFailed(false);
    try {
      const response = await fetch('/api/households', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: name.trim() }),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const household: Household = await response.json();
      setHouseholds((current) => [...current, household]);
      setSelectedId(household.id);
      setName('');
    } catch {
      setCreateFailed(true);
    } finally {
      setCreating(false);
    }
  }

  return <>
    <section className="panel">
      <h2>家庭</h2>
      {loading && <p role="status">正在載入家庭…</p>}
      {loadFailed && <><p role="alert">無法載入家庭。</p><button className="secondary" onClick={() => {
        setLoadFailed(false);
        setLoading(true);
        setAttempt(attempt + 1);
      }}>重試</button></>}
      {households.length > 0 && <label>目前家庭
        <select value={selectedId} onChange={(event) => setSelectedId(event.target.value)}>
          {households.map((household) => <option value={household.id} key={household.id}>{household.name}</option>)}
        </select>
      </label>}
      {!loading && !loadFailed && <form className="form-grid" onSubmit={create}>
        <label>家庭名稱<input value={name} onChange={(event) => setName(event.target.value)} maxLength={100} required /></label>
        <button className="primary" disabled={creating || !name.trim()}>建立家庭</button>
      </form>}
      {createFailed && <p role="alert">無法建立家庭，請再試一次。</p>}
    </section>
    {selectedId && <HouseholdLists key={selectedId} householdId={selectedId} />}
  </>;
}
