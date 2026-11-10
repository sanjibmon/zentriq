import { useCallback, useRef, useState, type Dispatch, type SetStateAction } from 'react';

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
export function useSticky<T>(key: string, initial: T): [T, Dispatch<SetStateAction<T>>] {
  const first = useRef(JSON.stringify(initial));
  const [value, setValue] = useState<T>(() => {
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
  return [value, set];
}
