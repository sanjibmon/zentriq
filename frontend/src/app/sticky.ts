import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from 'react';

const PREFIX = 'zq.filter.';

/** True when the page was opened from a link that sets its own filters (a dashboard chart, a count on another page). */
function openedWithFilters(): boolean {
  try {
    const q = new URLSearchParams(window.location.search);
    for (const k of q.keys()) if (k !== 'tab') return true;
  } catch { /* no window */ }
  return false;
}

/**
 * A list filter that stays chosen: when you leave the page and come back (or reload), the filter is still applied until
 * you clear it. A link that brings its own filters (for example from a dashboard chart) wins over the remembered ones.
 * Kept in this browser only.
 */
function urlParam(name: string | undefined): string | null {
  if (!name) return null;
  try { return new URLSearchParams(window.location.search).get(name); } catch { return null; }
}

/**
 * `param`: the address can set this filter (e.g. `?tab=devices&state=urgent` from a count on another page); it wins over
 * the remembered value, also when the link is followed while the list is already open.
 */
export function useSticky<T>(key: string, initial: T, param?: string): [T, Dispatch<SetStateAction<T>>] {
  const first = useRef(JSON.stringify(initial));
  const [value, setValue] = useState<T>(() => {
    const fromUrl = urlParam(param);
    if (fromUrl !== null) return fromUrl as unknown as T;
    if (openedWithFilters()) return initial;
    try {
      const raw = localStorage.getItem(PREFIX + key);
      return raw === null ? initial : (JSON.parse(raw) as T);
    } catch { return initial; }
  });
  const set = useCallback<Dispatch<SetStateAction<T>>>((next) => {
    setValue((prev) => {
      const v = typeof next === 'function' ? (next as (p: T) => T)(prev) : next;
      try {
        // Back to the page's default: nothing to remember.
        if (v === undefined || JSON.stringify(v) === first.current) localStorage.removeItem(PREFIX + key);
        else localStorage.setItem(PREFIX + key, JSON.stringify(v));
      } catch { /* storage unavailable */ }
      return v;
    });
  }, [key]);
  // A link with this filter followed while the list is open (same page, other tile).
  const applied = useRef<string | null>(urlParam(param));
  const search = typeof window === 'undefined' ? '' : window.location.search;
  useEffect(() => {
    const v = urlParam(param);
    if (v !== null && v !== applied.current) { applied.current = v; setValue(v as unknown as T); }
  }, [param, search]);
  return [value, set];
}
