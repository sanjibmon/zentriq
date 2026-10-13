import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { SmtpConfig } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Callout, ErrorBox, Panel } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';
import { errorText } from '../discovery/DirectoryPanel';

type Settings = Record<string, unknown>;

/** Sign-in policy (MFA, lockout, reset link lifetime) and the SMTP server used for password-reset e-mails. */
export function SecuritySettings() {
  const settings = useApi(() => api.get<Settings>('/system/settings'), []);
  const confirm = useConfirm();
  const smtp = useApi(() => api.get<SmtpConfig>('/system/smtp'), []);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const s = settings.data;

  async function put(key: string, value: unknown) {
    setMsg(null);
    try { await api.put(`/system/settings/${key}`, { value }); settings.reload(); setMsg({ tone: 'ok', text: 'Saved.' }); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }

  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="grid-2">
        <Panel title="Sign-in security">
          <ErrorBox error={settings.error} />
          {s && (
            <div className="form-v">
              <label className="row-check">
                <input type="checkbox" checked={s['security.mfa_required'] === true} onChange={(e) => {
                  const on = e.target.checked;
                  void (async () => {
                    if (on && !(await confirm({ title: 'Require two-step verification for every user?', confirmLabel: 'Require MFA', message: 'Users without MFA must set up an authenticator app at their next sign-in before they can use Zentriq. Set it up for your own account first (My account).' }))) return;
                    await put('security.mfa_required', on);
                  })();
                }} />
                <span><b>Require two-step verification (MFA) for all users</b><br /><small className="muted">Microsoft Authenticator, Google Authenticator or any TOTP app.</small></span>
              </label>
              <NumberSetting label="Lock an account after this many wrong passwords" value={Number(s['security.lockout_threshold'] ?? 5)} min={3} max={20} onSave={(v) => void put('security.lockout_threshold', v)} />
              <NumberSetting label="Lock for (minutes)" value={Number(s['security.lockout_minutes'] ?? 15)} min={1} max={1440} onSave={(v) => void put('security.lockout_minutes', v)} />
              <label className="row-check" style={{ borderTop: '1px solid var(--line)', paddingTop: 10 }}>
                <input type="checkbox" checked={s['display.show_ipv6'] === true} onChange={(e) => void put('display.show_ipv6', e.target.checked)} />
                <span><b>Show IPv6 addresses in the asset inventory</b><br /><small className="muted">Off by default: assets show their IPv4 addresses only.</small></span>
              </label>
              <NumberSetting label="Password reset link valid for (minutes)" value={Number(s['security.password_reset_minutes'] ?? 30)} min={5} max={1440} onSave={(v) => void put('security.password_reset_minutes', v)} />
            </div>
          )}
        </Panel>
        <Panel title="E-mail (SMTP)" sub="Used for “Forgot password?” e-mails">
          <ErrorBox error={smtp.error} />
          {smtp.data && <SmtpForm cfg={smtp.data} onSaved={() => { smtp.reload(); setMsg({ tone: 'ok', text: 'E-mail settings saved.' }); }} />}
        </Panel>
      </div>
    </>
  );
}

function NumberSetting({ label, value, min, max, onSave }: { label: string; value: number; min: number; max: number; onSave: (v: number) => void }) {
  const [v, setV] = useState(String(value));
  useEffect(() => setV(String(value)), [value]);
  const changed = Number(v) !== value;
  return (
    <label>{label}
      <div style={{ display: 'flex', gap: 6 }}>
        <input className="in" type="number" min={min} max={max} value={v} onChange={(e) => setV(e.target.value)} style={{ maxWidth: 120 }} />
        {changed && <button type="button" className="btn sm" onClick={() => onSave(Number(v))}>Save</button>}
      </div>
    </label>
  );
}

function SmtpForm({ cfg, onSaved }: { cfg: SmtpConfig; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(cfg.enabled);
  const [host, setHost] = useState(cfg.host ?? '');
  const [port, setPort] = useState(String(cfg.port || 587));
  const [security, setSecurity] = useState<SmtpConfig['security']>(cfg.security ?? 'starttls');
  const [username, setUsername] = useState(cfg.username ?? '');
  const [password, setPassword] = useState('');
  const [fromAddress, setFromAddress] = useState(cfg.fromAddress ?? '');
  const [fromName, setFromName] = useState(cfg.fromName ?? 'Zentriq');
  const [consoleUrl, setConsoleUrl] = useState(cfg.consoleUrl ?? '');
  const [testTo, setTestTo] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await api.put('/system/smtp', { enabled, host, port: Number(port), security, username, password: password || null, fromAddress, fromName, consoleUrl });
      setPassword('');
      onSaved();
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }
  async function sendTest() {
    setBusy(true); setTest(null); setError(null);
    try { setTest(await api.post<{ ok: boolean; message: string }>('/system/smtp/test', { to: testTo })); } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <form className="form-v" onSubmit={save}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <label className="row-check"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Send password-reset e-mails</label>
      <div className="form-grid">
        <label>SMTP server <input className="in mono" value={host} onChange={(e) => setHost(e.target.value)} placeholder="smtp.office365.com" /></label>
        <label>Port <input className="in" type="number" value={port} onChange={(e) => setPort(e.target.value)} /></label>
        <label>Encryption
          <select className="in" value={security} onChange={(e) => { const v = e.target.value as SmtpConfig['security']; setSecurity(v); setPort(v === 'starttls' ? '587' : '25'); }}>
            <option value="starttls">STARTTLS (587)</option>
            <option value="none">None (25, internal relay only)</option>
          </select>
        </label>
        <label>User name (optional) <input className="in" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" /></label>
        <label>Password <input className="in" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" placeholder={cfg.hasPassword ? 'Saved — leave empty to keep' : ''} /></label>
        <label>Sender address <input className="in" type="email" value={fromAddress} onChange={(e) => setFromAddress(e.target.value)} placeholder="zentriq@company.com" /></label>
        <label>Sender name <input className="in" value={fromName} onChange={(e) => setFromName(e.target.value)} /></label>
        <label className="full">Console address in e-mail links <input className="in mono" value={consoleUrl} onChange={(e) => setConsoleUrl(e.target.value)} placeholder={cfg.defaultConsoleUrl} />
          <small className="muted">The address users open the console with (default {cfg.defaultConsoleUrl}).</small>
        </label>
      </div>
      <small className="muted">Implicit TLS on port 465 is not supported; use STARTTLS on 587. The password is encrypted on this server and never shown again.</small>
      <div><button className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save e-mail settings'}</button></div>
      <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
        <input className="in" type="email" placeholder="Send a test e-mail to…" value={testTo} onChange={(e) => setTestTo(e.target.value)} style={{ maxWidth: 260 }} />
        <button type="button" className="btn" disabled={busy || !testTo} onClick={() => void sendTest()}>Send test</button>
        <small className="muted">Save first; the test uses the saved settings.</small>
      </div>
      {test && <Callout tone={test.ok ? 'ok' : 'crit'} icon={test.ok ? 'info' : 'alert'}>{test.message}</Callout>}
    </form>
  );
}
