import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { LoginResult, SetupStatus } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Icon } from '../../components/Icon';
import { LogoMark, PRODUCT } from '../../components/Logo';

export function LoginPage() {
  const { refresh } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPw, setShowPw] = useState(false);
  const [mfaCode, setMfaCode] = useState('');
  const [mfaStep, setMfaStep] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const mfaRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    api.get<SetupStatus>('/setup/status').then(setStatus).catch(() => setStatus(null));
  }, []);
  useEffect(() => {
    if (mfaStep) mfaRef.current?.focus();
  }, [mfaStep]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!email.trim() || !password) {
      setError('Enter your e-mail address and password.');
      return;
    }
    setBusy(true);
    try {
      await api.post<LoginResult>('/auth/login', { email: email.trim(), password, mfaCode: mfaStep ? mfaCode.trim() : null }, true);
      await refresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401 && (err.body as LoginResult | null)?.mfaRequired) {
        setMfaStep(true);
        setError(mfaStep ? 'The verification code is not valid. Try the current code from your authenticator app.' : null);
      } else if (err instanceof ApiError && err.status === 429) {
        setError('Too many sign-in attempts. Wait a minute and try again.');
      } else if (err instanceof ApiError && err.status === 401) {
        setError('The e-mail address or password is incorrect, or the account is locked.');
      } else {
        setError('Zentriq could not be reached. Check that the Zentriq services are running, then try again.');
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login">
      <section className="login-brand" aria-label={PRODUCT.full}>
        <div className="login-brand-top">
          <LogoMark size={44} />
          <div>
            <div className="login-product">{PRODUCT.full}</div>
            <div className="login-tagline">{PRODUCT.tagline}</div>
          </div>
        </div>
        <p className="login-strap">{PRODUCT.strap}</p>
        <ul className="login-points">
          <li><Icon name="radar" /> Authorized network and directory discovery</li>
          <li><Icon name="box" /> Hardware, software and license inventory</li>
          <li><Icon name="lock" /> Your data stays on your servers</li>
        </ul>
        <div className="login-brand-foot">© Zentriq{status?.productVersion ? ` · version ${status.productVersion}` : ''}</div>
      </section>

      <section className="login-panel">
        <form className="login-card" onSubmit={submit} noValidate>
          <div className="login-card-logo">
            <LogoMark size={40} />
          </div>
          <h1>Sign in</h1>
          <p className="muted">Use your Zentriq account.</p>

          {error && (
            <div className="callout crit login-error" role="alert">
              <Icon name="alert" />
              <div>{error}</div>
            </div>
          )}

          {!mfaStep ? (
            <>
              <label className="login-field">
                <span>Username (e-mail)</span>
                <input className="in" type="email" name="username" autoComplete="username" autoFocus value={email}
                  onChange={(e) => setEmail(e.target.value)} placeholder="name@company.com" disabled={busy} required />
              </label>
              <label className="login-field">
                <span>Password</span>
                <div className="login-pw">
                  <input className="in" type={showPw ? 'text' : 'password'} name="password" autoComplete="current-password" value={password}
                    onChange={(e) => setPassword(e.target.value)} disabled={busy} required />
                  <button type="button" className="icon-btn" onClick={() => setShowPw((s) => !s)} aria-label={showPw ? 'Hide password' : 'Show password'} title={showPw ? 'Hide password' : 'Show password'}>
                    <Icon name="eye" />
                  </button>
                </div>
              </label>
            </>
          ) : (
            <label className="login-field">
              <span>Verification code</span>
              <input ref={mfaRef} className="in mono" inputMode="numeric" autoComplete="one-time-code" maxLength={8} value={mfaCode}
                onChange={(e) => setMfaCode(e.target.value.replace(/\D/g, ''))} placeholder="6-digit code" disabled={busy} />
              <small className="muted">Enter the code from your authenticator app.</small>
            </label>
          )}

          <button className="btn primary big login-submit" type="submit" disabled={busy}>
            {busy ? 'Signing in…' : mfaStep ? 'Verify' : 'Sign in'}
          </button>
          {mfaStep && (
            <button type="button" className="btn ghost sm" onClick={() => { setMfaStep(false); setMfaCode(''); setError(null); }}>
              Back
            </button>
          )}

          <p className="login-help muted">
            Forgotten your password? Ask a Zentriq administrator to reset it.
            {status && !status.initialized && ' Setup has not been completed on this server; run the Zentriq installer.'}
          </p>
        </form>
        {status?.licenseMode === 'Trial' && status.trialExpiresAt && (
          <div className="login-trial">Evaluation · trial ends {new Date(status.trialExpiresAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}</div>
        )}
      </section>
    </div>
  );
}
