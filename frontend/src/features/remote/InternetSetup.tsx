import { useState } from 'react';
import { api, ApiError } from '../../api/client';
import { Icon } from '../../components/Icon';
import { Callout, Pill } from '../../components/ui';

export interface InternetCheckResult {
  host: string; port: number; ok: boolean; resolved: boolean; publicAddress: boolean; reachable: boolean; sameServer: boolean;
  addresses: { ip: string; public: boolean }[]; message: string; checkedAt: string; confirmedFromOutside: boolean; usable: boolean;
}

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : e instanceof Error ? e.message : String(e);
};

/**
 * Public address of remote support (domain name or public IP and HTTPS port): checked from the server — resolves to a
 * public address, and an HTTPS connection reaches this server's remote support — before it is saved and used for links.
 */
export function InternetSetup({ host: initialHost, port: initialPort, enginePort, onSaved, compact }: {
  host?: string | null; port?: number | null; enginePort: number; onSaved: (r: InternetCheckResult) => void; compact?: boolean;
}) {
  const [host, setHost] = useState(initialHost ?? '');
  const [port, setPort] = useState<number>(initialPort ?? enginePort);
  const [confirmed, setConfirmed] = useState(false);
  const [result, setResult] = useState<InternetCheckResult | null>(null);
  const [busy, setBusy] = useState<'check' | 'save' | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run(save: boolean) {
    setBusy(save ? 'save' : 'check'); setError(null);
    try {
      const r = await api.post<InternetCheckResult>('/remote/internet', { host, port, confirmedFromOutside: confirmed, save });
      setResult(r);
      if (save && r.usable) onSaved(r);
    } catch (e) {
      if (e instanceof ApiError && e.status === 422 && e.body) setResult(e.body as InternetCheckResult);
      else setError(errText(e));
    } finally { setBusy(null); }
  }

  const steps = [
    ['Public address', result ? (result.resolved ? (result.publicAddress ? 'ok' : 'crit') : 'crit') : ''],
    ['HTTPS reaches this server', result ? (result.ok ? 'ok' : result.confirmedFromOutside && result.publicAddress ? 'info' : 'crit') : ''],
  ] as const;

  return (
    <div className="form-v">
      <div className="rs-inet">
        <label className="fld"><span>Public domain name or public IP</span>
          <input className="in" placeholder="support.example.com or 203.0.113.10" value={host} onChange={(e) => { setHost(e.target.value); setResult(null); }} /></label>
        <label className="fld"><span>HTTPS port</span>
          <input className="in" type="number" min={1} max={65535} value={port} onChange={(e) => { setPort(Number(e.target.value) || enginePort); setResult(null); }} /></label>
      </div>
      {!compact && <ol className="rs-howto">
        <li>Point a public DNS record to your public IP address (or use the public IP itself).</li>
        <li>On the router or firewall, forward TCP <b>{port}</b> to this server, port <b>{enginePort}</b>. Only remote support is published — not the console. HTTPS only; port 80 is not used.</li>
        <li>For no browser warning outside, give the server a certificate from a public CA that includes the name (see the installation guide).</li>
      </ol>}
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      {result && <div className={`rs-check ${result.usable ? 'ok' : 'bad'}`}>
        <div className="rs-check-steps">{steps.map(([l, t]) => <Pill key={l} tone={t as 'ok' | 'crit' | 'info' | ''}>{l}</Pill>)}</div>
        <p>{result.message}</p>
        {result.addresses.length > 0 && <small className="muted">Resolves to {result.addresses.map((a) => `${a.ip}${a.public ? '' : ' (private)'}`).join(', ')}</small>}
      </div>}
      {result && !result.ok && result.publicAddress && <label className="row-check">
        <input type="checkbox" checked={confirmed} onChange={(e) => { setConfirmed(e.target.checked); }} />
        I opened <span className="mono">https://{host}:{port}</span> from outside the network (for example a phone on mobile data) and it works
      </label>}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button className="btn" disabled={!host.trim() || !!busy} onClick={() => void run(false)}><Icon name="globe" size={13} /> {busy === 'check' ? 'Checking…' : 'Check'}</button>
        <button className="btn primary" disabled={!host.trim() || !!busy || (!!result && !result.ok && !(confirmed && result.publicAddress))} onClick={() => void run(true)}>
          {busy === 'save' ? 'Checking…' : 'Check and use this address'}</button>
      </div>
    </div>
  );
}
