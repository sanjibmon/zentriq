import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { idleSeconds, markActive, noteIdleSignout, setIdleLimit, sinceLastRequest } from '../app/idle';

/**
 * Warns before the console signs out after inactivity and signs out at the limit (Settings › Security). While the user
 * works without causing requests, it tells the server about the activity once a minute.
 */
export function IdleWatcher({ minutes, onSignOut }: { minutes: number; onSignOut: () => Promise<void> }) {
  const [limit, setLimit] = useState(minutes);
  const [left, setLeft] = useState<number | null>(null);
  useEffect(() => setLimit(minutes), [minutes]);
  useEffect(() => setIdleLimit(limit), [limit]);

  useEffect(() => {
    let done = false;
    const tick = () => {
      if (done) return;
      const idle = idleSeconds();
      if (idle < 60 && sinceLastRequest() > 60_000)
        api.get<{ idleSignoutMinutes: number }>('/auth/alive').then((r) => setLimit(r.idleSignoutMinutes)).catch(() => { /* 401 signs out */ });
      if (limit <= 0) { setLeft(null); return; }
      const total = limit * 60;
      const warn = Math.min(60, Math.floor(total / 2));
      if (idle >= total) {
        done = true;
        noteIdleSignout();
        void onSignOut();
        return;
      }
      setLeft(idle >= total - warn ? total - idle : null);
    };
    tick();
    const t = window.setInterval(tick, 1000);
    return () => { done = true; window.clearInterval(t); };
  }, [limit, onSignOut]);

  if (left === null) return null;
  const stay = () => {
    markActive(true);
    setLeft(null);
    api.get<{ idleSignoutMinutes: number }>('/auth/alive').then((r) => setLimit(r.idleSignoutMinutes)).catch(() => { /* 401 signs out */ });
  };
  return (
    <div className="modal-scrim" role="alertdialog" aria-modal="true" aria-labelledby="idle-title" aria-describedby="idle-body" onKeyDown={(e) => e.key === 'Escape' && stay()}>
      <div className="modal-card confirm-card">
        <header><h2 id="idle-title">Are you still there?</h2></header>
        <div className="body" id="idle-body">
          You will be signed out in <b>{left} second{left === 1 ? '' : 's'}</b> because the console has not been used for a while.
        </div>
        <footer>
          <button type="button" className="btn" onClick={() => void onSignOut()}>Sign out now</button>
          <button type="button" className="btn primary" autoFocus onClick={stay}>Stay signed in</button>
        </footer>
      </div>
    </div>
  );
}
