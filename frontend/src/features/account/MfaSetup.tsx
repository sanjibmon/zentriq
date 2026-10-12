import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { MfaEnrollment } from '../../api/types';
import { Callout } from '../../components/ui';
import { CopyBox } from '../../components/CopyBox';
import { errorText } from '../discovery/DirectoryPanel';

/** Authenticator-app enrollment: QR code (Microsoft Authenticator, Google Authenticator, …), then confirm with a code. */
export function MfaSetup({ onDone }: { onDone: () => void }) {
  const [enrollment, setEnrollment] = useState<MfaEnrollment | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function start() {
    setBusy(true); setError(null);
    try { setEnrollment(await api.post<MfaEnrollment>('/auth/mfa/enroll')); } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  }
  async function confirm(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await api.post('/auth/mfa/confirm', { code: code.trim() }); onDone(); } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  if (!enrollment)
    return (
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <p style={{ margin: 0 }}>Protect your account with a 6-digit code from an authenticator app on your phone — <b>Microsoft Authenticator</b>, <b>Google Authenticator</b> or any app that supports time-based codes (TOTP).</p>
        <div><button className="btn primary" onClick={() => void start()} disabled={busy}>{busy ? 'Preparing…' : 'Set up authenticator app'}</button></div>
      </div>
    );

  return (
    <form className="form-v" onSubmit={confirm}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <ol className="plain" style={{ paddingLeft: 18, listStyle: 'decimal', margin: 0, display: 'grid', gap: 6 }}>
        <li>Open Microsoft Authenticator or Google Authenticator and choose <i>Add account</i> → <i>Other account</i> / <i>Scan a QR code</i>.</li>
        <li>Scan this code:</li>
      </ol>
      <div style={{ display: 'flex', gap: 18, alignItems: 'center', flexWrap: 'wrap' }}>
        <div className="qr" role="img" aria-label="QR code for your authenticator app" style={{ background: '#fff', padding: 8, borderRadius: 8, width: 212, height: 212, border: '1px solid var(--line)' }}
          dangerouslySetInnerHTML={{ __html: enrollment.qrSvg.replace(/<svg /, '<svg width="196" height="196" ') }} />
        <div style={{ flex: 1, minWidth: 220 }} className="form-v">
          <small className="muted">Can&apos;t scan? Enter this key in the app (time-based, 6 digits):</small>
          <CopyBox value={enrollment.secret.replace(/(.{4})/g, '$1 ').trim()} label="Secret key" />
        </div>
      </div>
      <label>3. Enter the 6-digit code shown in the app
        <input className="in mono" style={{ maxWidth: 180, letterSpacing: 3, fontSize: 18 }} inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code}
          onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} autoFocus />
      </label>
      <div><button className="btn primary" type="submit" disabled={busy || code.length !== 6}>{busy ? 'Checking…' : 'Turn on MFA'}</button></div>
    </form>
  );
}
