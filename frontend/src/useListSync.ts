import { useEffect, useRef, useState } from 'react';
import { ListConflict, ListRequestError, type ListScope } from './listRequest';

type ListRead<T> = (scope: ListScope, signal: AbortSignal) => Promise<T[]>;
type ListSave<T> = (scope: ListScope, previous: T[], next: T[], signal: AbortSignal) => Promise<T[]>;

type Requests = {
  controller: AbortController;
  revision: number;
  reading: boolean;
  saving: boolean;
};

const readDenied = (error: unknown) =>
  error instanceof ListRequestError && (error.status === 401 || error.status === 403);

export function useListSync<T>({ householdId, code }: ListScope, read: ListRead<T>, save: ListSave<T>,
  savedMessage: string, report: (message: string) => void) {
  const [entries, setEntries] = useState<T[]>([]);
  const [ready, setReady] = useState(false);
  const [loadError, setLoadError] = useState('');
  const [pending, setPending] = useState(false);
  const requests = useRef<Requests>(null);

  useEffect(() => {
    if (!householdId && !code) return;

    const controller = new AbortController();
    const state: Requests = { controller, revision: 0, reading: false, saving: false };
    requests.current = state;
    let loaded = false;
    setReady(false);
    setLoadError('');
    setPending(false);

    async function refresh() {
      if (state.reading || state.saving) return;
      state.reading = true;
      const revision = state.revision;
      try {
        const result = await read({ householdId, code }, controller.signal);
        if (controller.signal.aborted || revision !== state.revision) return;
        setEntries(result);
        setReady(true);
        setLoadError('');
        loaded = true;
      } catch (error) {
        if (controller.signal.aborted || revision !== state.revision) return;
        if (!loaded || readDenied(error)) {
          loaded = false;
          setReady(false);
          setLoadError((error as Error).message);
        }
      } finally {
        state.reading = false;
      }
    }

    void refresh();
    const timer = householdId ? setInterval(() => void refresh(), 15_000) : undefined;
    return () => {
      controller.abort();
      clearInterval(timer);
    };
  }, [householdId, code, read]);

  async function update(next: T[]) {
    const state = requests.current!;
    if (state.saving) return;
    const { signal } = state.controller;
    const scope = { householdId, code };
    state.saving = true;
    state.revision += 1;
    setPending(true);

    try {
      const result = await save(scope, entries, next, signal);
      if (signal.aborted) return;
      setEntries(result);
      report(savedMessage);
    } catch (error) {
      if (signal.aborted) return;
      const conflict = error instanceof ListConflict;
      report(conflict ? '清單已被其他成員更新，正在重新載入…' : (error as Error).message);
      if (!householdId) return;

      try {
        const latest = await read(scope, signal);
        if (signal.aborted) return;
        setEntries(latest);
        if (conflict) report('清單已被其他成員更新，已重新載入。請確認後再操作。');
      } catch (reloadError) {
        if (signal.aborted) return;
        if (readDenied(reloadError)) {
          setReady(false);
          setLoadError((reloadError as Error).message);
        }
        if (conflict) report('清單已被其他成員更新。暫時無法載入，請稍後再試。');
      }
    } finally {
      state.saving = false;
      if (!signal.aborted) setPending(false);
    }
  }

  return { entries, loadError, pending, ready, update };
}