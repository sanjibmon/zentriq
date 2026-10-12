import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import { useRouter } from '../../app/router';
import { LogoMark } from '../../components/Logo';
import { Callout } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';

/** Opened from the e-mailed link: /reset-password?token=… */
export function ResetPasswordPage() {
  const { query, navigate } = useRouter();
  const token = query.get('token') ?? '';
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [mfaCode, setMfaCode] = useState('');
  const [needMfa, setNeedMfa] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (next !== repeat) { setError('The passwords do not match.'); return; }
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ message: string }>('/auth/reset', { token, newPassword: next, mfaCode: needMfa ? mfaCode : null }, true);
      setDone(r.message);
    } catch (err) {
      if (err instanceof ApiError && (err.body as { mfaRequired?: boolean } | null)?.mfaRequired) { setNeedMfa(true); setError(null); }
      else setError(errorText(err));
    } finally { setBusy(false); }
  }

  return (
    <div className="login" style={{ gridTemplateColumns: '1fr' }}>
      <section className="login-panel">
        <form className="login-card" onSubmit={submit} noValidate>
          <div className="login-card-logo"><LogoMark size={40} /></div>
          <h1>Choose a new password</h1>
          {done ? (
            <>
              <Callout tone="ok" icon="info">{done}</Callout>
              <button type="button" className="btn primary big login-submit" onClick={() => navigate('/', true)}>Go to sign in</button>
            </>
          ) : !token ? (
            <Callout tone="crit" icon="alert">This link is incomplete. Open the link from the e-mail again, or request a new one from the sign-in page.</Callout>
          ) : (
            <>
              {error && <Callout tone="crit" icon="alert">{error}</Callout>}
              <label className="login-field"><span>New password</span><input className="in" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} autoFocus /></label>
              <label className="login-field"><span>Repeat new password</span><input className="in" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} /></label>
              <small className="muted">At least 12 characters with three of: upper case, lower case, digits, symbols.</small>
              {needMfa && (
                <label className="login-field"><span>Code from your authenticator app</span>
                  <input className="in mono" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={mfaCode} onChange={(e) => setMfaCode(e.target.value.replace(/\D/g, ''))} autoFocus />
                </label>
              )}
              <button className="btn primary big login-submit" type="submit" disabled={busy || !next || !repeat || (needMfa && mfaCode.length !== 6)}>{busy ? 'Saving…' : 'Set new password'}</button>
            </>
          )}
          <button type="button" className="btn ghost sm" onClick={() => navigate('/', true)}>Back to sign in</button>
        </form>
      </section>
    </div>
  );
}
