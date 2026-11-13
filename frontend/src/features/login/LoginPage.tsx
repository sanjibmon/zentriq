import { takeIdleNotice } from '../../app/idle';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { LoginResult, SetupStatus } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Icon } from '../../components/Icon';
import { LogoMark, PRODUCT } from '../../components/Logo';
import { CompanyLogo } from '../../components/CompanyLogo';
import { BrandArt } from './BrandArt';
import { Command, HelpSteps, Step, UiPath } from '../../components/Help';

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
  const [forgot, setForgot] = useState(false);
  const [resetByEmail, setResetByEmail] = useState<boolean | null>(null);
  const [notice, setNotice] = useState<string | null>(() => takeIdleNotice());
  const [directory, setDirectory] = useState(false);
  const [providers, setProviders] = useState<{ directory: { domain: string | null } | null; microsoft: boolean }>({ directory: null, microsoft: false });
  const mfaRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    api.get<SetupStatus>('/setup/status').then(setStatus).catch(() => setStatus(null));
    api.get<{ passwordResetByEmail: boolean; directory: { domain: string | null } | null; microsoft: boolean }>('/auth/options')
      .then((o) => { setResetByEmail(o.passwordResetByEmail); setProviders({ directory: o.directory ?? null, microsoft: !!o.microsoft }); })
      .catch(() => setResetByEmail(false));
    // Sign In With Microsoft comes back here with a message when it did not work.
    const sso = new URLSearchParams(window.location.search).get('ssoError');
    if (sso) { setError(sso); window.history.replaceState(null, '', window.location.pathname); }
  }, []);
  useEffect(() => {
    if (mfaStep) mfaRef.current?.focus();
  }, [mfaStep]);

  async function sendReset(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!email.trim()) { setError('Enter the e-mail address of your Zentriq account.'); return; }
    setBusy(true);
    try {
      const r = await api.post<{ message: string }>('/auth/forgot', { email: email.trim() }, true);
      setNotice(r.message);
      setForgot(false);
    } catch (err) {
      if (err instanceof ApiError && err.status === 429) setError('Too many requests. Wait a minute and try again.');
      else if (err instanceof ApiError && err.status === 503) setError(err.message);
      else setError('Zentriq could not be reached. Try again later.');
    } finally {
      setBusy(false);
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    if (!email.trim() || !password) {
      setError(directory ? 'Enter your user name and password.' : 'Enter your e-mail address and password.');
      return;
    }
    setBusy(true);
    try {
      await api.post<LoginResult>('/auth/login', { email: email.trim(), password, mfaCode: mfaStep ? mfaCode.trim() : null, directory: directory || null }, true);
      await refresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401 && (err.body as LoginResult | null)?.mfaRequired) {
        setMfaStep(true);
        setError(mfaStep ? 'The verification code is not valid. Try the current code from your authenticator app.' : null);
      } else if (err instanceof ApiError && err.status === 429) {
        setError('Too many sign-in attempts. Wait a minute and try again.');
      } else if (err instanceof ApiError && err.status === 401) {
        setError(err.message && !/^HTTP \d+/.test(err.message) ? err.message : 'The e-mail address or password is incorrect, or the account is locked.');
      } else {
        setError('Zentriq could not be reached. Check that the Zentriq services are running, then try again.');
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login">
      <section className="login-brand" aria-label={`${PRODUCT.name} – ${PRODUCT.tagline}`}>
        <div className="login-brand-top">
          <LogoMark size={48} />
          <div>
            <div className="login-product">{PRODUCT.name}</div>
            <div className="login-tagline">{PRODUCT.tagline}</div>
          </div>
        </div>
        <BrandArt />
      </section>

      <section className="login-panel">
        {forgot ? (
        <form className="login-card" onSubmit={sendReset} noValidate>
          <div className="login-card-logo"><LogoMark size={40} /><CompanyLogo className="login-company-logo" height={40} /></div>
          <h1>Reset password</h1>
          {resetByEmail === false ? (
            <>
              <p className="muted">Password reset by e-mail is not set up on this server.</p>
              <HelpSteps>
                <Step title="Ask an administrator">A Zentriq administrator can set a new password for you in <UiPath>Users, Roles &amp; Groups</UiPath>.</Step>
                <Step title="Only administrator?">Sign in to the Zentriq server and run:
                  <Command value="zentriqctl reset-password --email you@company.com" shell="Command Prompt (Administrator)" label="Reset password" /></Step>
              </HelpSteps>
            </>
          ) : (
            <>
              <p className="muted">Enter the e-mail address of your Zentriq account. We will send you a link to choose a new password.</p>
              {error && <div className="callout crit login-error" role="alert"><Icon name="alert" /><div>{error}</div></div>}
              <label className="login-field">
                <span>E-mail</span>
                <input className="in" type="email" autoComplete="username" autoFocus value={email} onChange={(e) => setEmail(e.target.value)} placeholder="name@company.com" disabled={busy} />
              </label>
              <button className="btn primary big login-submit" type="submit" disabled={busy}>{busy ? 'Sending…' : 'Send reset link'}</button>
            </>
          )}
          <button type="button" className="btn ghost sm" onClick={() => { setForgot(false); setError(null); }}>Back to sign in</button>
        </form>
        ) : (
        <form className="login-card" onSubmit={submit} noValidate>
          {/* Zentriq on top; the organization's logo (System Settings) below it, under a thin rule. */}
          <div className="login-card-logo">
            <LogoMark size={40} />
            <CompanyLogo className="login-company-logo" height={40} />
          </div>
          <h1>Sign in</h1>

          {notice && <div className="callout ok login-error" role="status"><Icon name="info" /><div>{notice}</div></div>}
          {error && (
            <div className="callout crit login-error" role="alert">
              <Icon name="alert" />
              <div>{error}</div>
            </div>
          )}

          {!mfaStep ? (
            <>
              {providers.directory && (
                <div className="seg login-kind" role="radiogroup" aria-label="Account type">
                  <button type="button" role="radio" aria-checked={!directory} className={!directory ? 'on' : ''} onClick={() => setDirectory(false)}>Zentriq Account</button>
                  <button type="button" role="radio" aria-checked={directory} className={directory ? 'on' : ''} onClick={() => setDirectory(true)}>Directory Account</button>
                </div>
              )}
              <label className="login-field">
                <span>{directory ? 'User name' : 'Username'}</span>
                <input className="in" type={directory ? 'text' : 'email'} name="username" autoComplete="username" autoFocus value={email}
                  onChange={(e) => setEmail(e.target.value)} placeholder={directory ? (providers.directory?.domain ? `${providers.directory.domain}\\name or name` : 'DOMAIN\\name or name@domain') : 'name@company.com'} disabled={busy} required />
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
              <small className="muted">Open Microsoft Authenticator or Google Authenticator and enter the 6-digit code for Zentriq. Lost your phone? Ask an administrator to reset your MFA.</small>
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

          {!mfaStep && providers.microsoft && (
            <div className="login-sso">
              <div className="login-or">or</div>
              <a className="btn big ms" href={`/api/v1/auth/oidc/start${email.includes('@') ? `?hint=${encodeURIComponent(email.trim())}` : ''}`}>
                <span className="ms-logo" aria-hidden="true"><i style={{ background: '#f25022' }} /><i style={{ background: '#7fba00' }} /><i style={{ background: '#00a4ef' }} /><i style={{ background: '#ffb900' }} /></span>
                Sign In With Microsoft
              </a>
            </div>
          )}
          {!mfaStep && !directory && (
            <button type="button" className="btn ghost sm" onClick={() => { setForgot(true); setError(null); setNotice(null); }}>Forgot password?</button>
          )}
          {status && !status.initialized && <p className="login-help muted">Setup has not been completed on this server; run the Zentriq installer.</p>}
        </form>
        )}
        {status?.licenseMode === 'Trial' && status.trialExpiresAt && (
          <div className="login-trial">Evaluation · trial ends {new Date(status.trialExpiresAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}</div>
        )}
      </section>
    </div>
  );
}
