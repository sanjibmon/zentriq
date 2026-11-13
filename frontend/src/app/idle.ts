// Sign-out after inactivity (Settings › Security). Activity is the user typing, clicking, scrolling or moving the
// mouse in any console tab (shared through localStorage); background refreshes are not activity. Every API request
// tells the server how long the user has been idle (X-Zentriq-Idle), and the server ends the session at the limit too.

const KEY = 'zq.lastActivity';
const NOTICE = 'zq.idleSignout';
let last = Date.now();
let lastRequest = 0;
let limitMinutes = 0;

function shared(): number {
  try {
    const v = Number(localStorage.getItem(KEY));
    if (Number.isFinite(v) && v > last && v <= Date.now() + 5000) last = v;
  } catch { /* storage blocked: this tab's own activity only */ }
  return last;
}

/** Records activity (at most every 5 seconds, unless forced). */
export function markActive(force = false) {
  const now = Date.now();
  if (!force && now - last < 5000) return;
  last = now;
  try { localStorage.setItem(KEY, String(now)); } catch { /* ignore */ }
}

/** Whole seconds since the user's last activity in any console tab. */
export function idleSeconds(): number {
  return Math.max(0, Math.floor((Date.now() - shared()) / 1000));
}

export const noteRequest = () => { lastRequest = Date.now(); };
export const sinceLastRequest = () => Date.now() - lastRequest;

/** The current setting in minutes; 0 = never. */
export const setIdleLimit = (m: number) => { limitMinutes = m; };
export const idleLimit = () => limitMinutes;
export const idleExpired = () => limitMinutes > 0 && idleSeconds() >= limitMinutes * 60;

/** Remembers why the console signed out, for the sign-in page. */
export function noteIdleSignout() {
  try { sessionStorage.setItem(NOTICE, String(limitMinutes)); } catch { /* ignore */ }
}
export function takeIdleNotice(): string | null {
  try {
    const m = sessionStorage.getItem(NOTICE);
    if (m === null) return null;
    sessionStorage.removeItem(NOTICE);
    return `You were signed out after ${m} minute${m === '1' ? '' : 's'} without activity. Sign in again to continue.`;
  } catch { return null; }
}

let installed = false;
export function installActivityTracking() {
  if (installed) return;
  installed = true;
  const on = () => markActive();
  for (const ev of ['pointerdown', 'keydown', 'wheel', 'touchstart', 'mousemove']) window.addEventListener(ev, on, { passive: true, capture: true });
  window.addEventListener('scroll', on, { passive: true, capture: true });
  markActive(true);
}
