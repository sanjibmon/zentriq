import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '../api/client';

export interface ApiState<T> {
  data: T | null;
  error: ApiError | Error | null;
  loading: boolean;
  reload: () => void;
}

// Page refresh: the Refresh button in a page header reloads every useApi on the page (filters and paging stay as they are).
const refreshers = new Set<() => void>();
export function refreshPage() {
  refreshers.forEach((f) => f());
}

/** Loads data from the API and re-runs when `deps` change, or when the page's Refresh button is pressed. */
export function useApi<T>(load: () => Promise<T>, deps: unknown[]): ApiState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | Error | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps);
  useEffect(() => {
    let alive = true;
    setLoading(true);
    run()
      .then((d) => {
        if (alive) {
          setData(d);
          setError(null);
        }
      })
      .catch((e: unknown) => alive && setError(e instanceof Error ? e : new Error(String(e))))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [run, tick]);
  useEffect(() => {
    const f = () => setTick((t) => t + 1);
    refreshers.add(f);
    return () => {
      refreshers.delete(f);
    };
  }, []);
  return { data, error, loading, reload: () => setTick((t) => t + 1) };
}
