import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Callout, PageHead, Panel, Pill } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';
import { MfaSetup } from './MfaSetup';

/** My account: password and two-step verification. */
export function AccountPage() {
  const { me, refresh } = useAuth();
  const [msg, setMsg] = useState<string | null>(null);
  if (!me) return null;
  return (
    <>
      <PageHead title="My account" desc={`${me.displayName} · ${me.email}`} />
      {msg && <Callout tone="ok" icon="info">{msg}</Callout>}
      <Panel title="Two-step verification (MFA)" right={<Pill tone={me.mfaEnabled ? 'ok' : 'warn'}>{me.mfaEnabled ? 'On' : 'Off'}</Pill>}>
        {me.mfaEnabled
          ? <MfaDisable onDone={() => { setMsg('Two-step verification is off.'); void refresh(); }} />
          : <MfaSetup onDone={() => { setMsg('Two-step verification is on. You will be asked for a code from your authenticator app each time you sign in.'); void refresh(); }} />}
      </Panel>
      <Panel title="Change password"><ChangePassword onDone={() => setMsg('Password changed. Other sessions have been signed out.')} /></Panel>
    </>
  );
}

function MfaDisable({ onDone }: { onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await api.post('/auth/mfa/disable', { password, code }); onDone(); } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }
  if (!open)
    return <div className="form-v"><p style={{ margin: 0 }}>Each sign-in asks for a code from your authenticator app. If you get a new phone, turn MFA off here and set it up again — or ask an administrator to reset it.</p><div><button className="btn" onClick={() => setOpen(true)}>Turn off MFA…</button></div></div>;
  return (
    <form className="form-v" onSubmit={submit} style={{ maxWidth: 420 }}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <label>Password <input className="in" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
      <label>Current code from the app <input className="in mono" inputMode="numeric" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} /></label>
      <div style={{ display: 'flex', gap: 8 }}><button className="btn" type="button" onClick={() => setOpen(false)}>Cancel</button><button className="btn primary" disabled={busy || !password || code.length !== 6}>Turn off</button></div>
    </form>
  );
}

function ChangePassword({ onDone }: { onDone: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (next !== repeat) { setError('The new passwords do not match.'); return; }
    setBusy(true); setError(null);
    try { await api.post('/auth/password', { currentPassword: current, newPassword: next }); setCurrent(''); setNext(''); setRepeat(''); onDone(); } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }
  return (
    <form className="form-v" onSubmit={submit} style={{ maxWidth: 420 }}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <label>Current password <input className="in" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} /></label>
      <label>New password <input className="in" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} /><small className="muted">At least 12 characters with three of: upper case, lower case, digits, symbols.</small></label>
      <label>Repeat new password <input className="in" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} /></label>
      <div><button className="btn primary" disabled={busy || !current || !next}>{busy ? 'Saving…' : 'Change password'}</button></div>
    </form>
  );
}

/** Shown instead of the console when MFA is mandatory and this account has not set it up yet. */
export function MfaRequiredPage() {
  const { me, refresh, logout } = useAuth();
  return (
    <div className="login" style={{ gridTemplateColumns: '1fr' }}>
      <section className="login-panel">
        <div className="login-card" style={{ width: 'min(560px, 100%)' }}>
          <h1>Set up two-step verification</h1>
          <p className="muted">Your organization requires a code from an authenticator app at every sign-in. Set it up now to continue{me ? ` as ${me.email}` : ''}.</p>
          <MfaSetup onDone={() => void refresh()} />
          <button type="button" className="btn ghost sm" style={{ marginTop: 12 }} onClick={() => void logout()}>Sign out</button>
        </div>
      </section>
    </div>
  );
}
