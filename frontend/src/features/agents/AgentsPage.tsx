import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { AgentPackage, AgentRow, EnrollmentToken, NewEnrollmentToken } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CopyBox } from '../../components/CopyBox';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Tone } from '../../components/ui';

const HEALTH: Record<AgentRow['health'], [string, Tone]> = {
  online: ['Online', 'ok'],
  stale: ['Not seen recently', 'warn'],
  offline: ['Offline > 7 days', 'crit'],
  never: ['Never checked in', ''],
  revoked: ['Revoked', 'crit'],
};

/** Agents must reach the server by an address that resolves for them; the address used for this console is the best default. */
function suggestedUrl(p: AgentPackage | null): string {
  const port = p?.agentPort || 8444;
  const host = window.location.hostname;
  if (!host || host === 'localhost' || host === '127.0.0.1') return p?.defaultServerUrl ?? '';
  return `https://${host.includes(':') ? `[${host}]` : host}:${port}`;
}

export function AgentsPage() {
  const { can } = useAuth();
  const manage = can('agents.manage');
  const [tab, setTab] = useState<'agents' | 'codes'>('agents');
  const [created, setCreated] = useState<NewEnrollmentToken | null>(null);
  const [newOpen, setNewOpen] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const agents = useApi(() => api.get<AgentRow[]>('/agents'), []);
  const pkg = useApi(() => api.get<AgentPackage>('/agents/package'), []);
  const tokens = useApi(() => (manage ? api.get<EnrollmentToken[]>('/agents/enrollment-tokens') : Promise.resolve([])), [manage]);
  const rows = agents.data ?? [];
  const count = (h: AgentRow['health']) => rows.filter((r) => r.health === h).length;

  async function revoke(a: AgentRow) {
    if (!window.confirm(`Revoke the agent on ${a.hostname ?? a.id}? It will stop reporting until it is enrolled again.`)) return;
    await api.post(`/agents/${a.id}/revoke`);
    agents.reload();
  }
  async function collect(a: AgentRow) {
    const r = await api.post<{ message: string }>(`/agents/${a.id}/collect`);
    setMsg(r.message);
  }
  async function showCode(t: EnrollmentToken) {
    setMsg(null);
    try {
      setCreated(await api.get<NewEnrollmentToken>(`/agents/enrollment-tokens/${t.id}/code`));
      window.scrollTo({ top: 0, behavior: 'smooth' });
    } catch (err) {
      setMsg(err instanceof Error ? err.message : String(err));
    }
  }
  async function revokeToken(t: EnrollmentToken) {
    if (!window.confirm(`Revoke enrollment code "${t.name}"? Agents already enrolled are not affected.`)) return;
    await api.post(`/agents/enrollment-tokens/${t.id}/revoke`);
    tokens.reload();
  }

  return (
    <>
      <PageHead title="Agents" desc="The Zentriq Agent reports inventory from Windows devices that agentless scans cannot reach — laptops off the network, branch offices, locked-down hosts. It only connects out to this server over HTTPS."
        actions={<>
          {pkg.data?.package && <a className="btn" href={pkg.data.package.url} download>Download agent (MSI{pkg.data.package.version ? ` ${pkg.data.package.version}` : ''})</a>}
          {manage && <button className="btn primary" onClick={() => setNewOpen(true)}>New enrollment code</button>}
        </>} />
      {msg && <Callout>{msg}</Callout>}
      <ErrorBox error={agents.error ?? pkg.error} />
      {pkg.data && !pkg.data.enrollmentAvailable && <Callout tone="warn" icon="alert">Agent enrollment is not configured on this server (agent CA missing). Run <span className="mono">zentriqctl repair</span> on the server.</Callout>}
      {created && (
        <Panel title={`Enrollment code “${created.name}”`} sub="You can show it again later from the Enrollment codes tab while it is active" right={<button className="btn sm" onClick={() => setCreated(null)}>Done</button>}>
          <div className="form-v">
            <Callout tone="warn" icon="lock">Anyone with this code can enroll a device until it expires ({fmt.date(created.expiresAt)}){created.maxUses ? ` or is used ${created.maxUses} time(s)` : ''}. Treat it like a password; revoke it if it leaks.</Callout>
            <label>Enrollment code<CopyBox value={created.code} label="Enrollment code" rows={3} /></label>
            <label>Silent install (Intune, SCCM, GPO or an elevated prompt)<CopyBox value={created.installCommand} label="Install command" rows={3} /></label>
            <small className="muted">Agents connect to <span className="mono">{created.serverUrl}</span>. Devices outside your network need that address and port to be reachable (for example published through your firewall or reverse proxy).</small>
          </div>
        </Panel>
      )}
      <Kpis items={[
        { label: 'Agents', value: rows.filter((r) => r.status !== 'revoked').length },
        { label: 'Online', value: count('online') },
        { label: 'Not seen recently', value: count('stale') + count('offline') },
        { label: 'Agent port', value: pkg.data ? <span className="mono">{pkg.data.agentPort || 'disabled'}</span> : '—' },
      ]} />
      <div className="tabs" role="tablist">
        <button className={tab === 'agents' ? 'on' : ''} onClick={() => setTab('agents')}>Agents<span className="cnt">{rows.length}</span></button>
        {manage && <button className={tab === 'codes' ? 'on' : ''} onClick={() => setTab('codes')}>Enrollment codes<span className="cnt">{tokens.data?.length ?? 0}</span></button>}
      </div>
      {tab === 'agents' ? (
        <Panel flush>
          <DataTable rows={rows} empty={agents.loading ? 'Loading…' : 'No agents yet. Create an enrollment code and install the agent MSI on a device.'} columns={[
            { key: 'h', label: 'Device', render: (r) => <>{r.assetId ? <Link to={`/assets/${r.assetId}`}><b>{r.hostname ?? r.id}</b></Link> : <b>{r.hostname ?? r.id}</b>}<div className="faint" style={{ fontSize: 11.5 }}>{r.osName ?? ''}</div></> },
            { key: 's', label: 'Health', render: (r) => <Pill tone={HEALTH[r.health][1]}>{HEALTH[r.health][0]}</Pill> },
            { key: 'c', label: 'Last check-in', render: (r) => <span title={fmt.date(r.lastCheckinAt)}>{fmt.ago(r.lastCheckinAt)}</span> },
            { key: 'i', label: 'Last inventory', render: (r) => <span title={fmt.date(r.lastInventoryAt)}>{fmt.ago(r.lastInventoryAt)}</span> },
            { key: 'ip', label: 'Last address', render: (r) => <span className="mono">{r.lastIp ?? '—'}</span> },
            { key: 'v', label: 'Version', render: (r) => r.version },
            { key: 'e', label: 'Certificate', render: (r) => r.certificateExpires ? `expires ${fmt.day(r.certificateExpires)}` : '—' },
            { key: 'x', label: '', render: (r) => manage && r.status !== 'revoked' ? <div style={{ display: 'flex', gap: 6 }}><button className="btn sm" onClick={() => void collect(r)}>Collect now</button><button className="btn sm" onClick={() => void revoke(r)}>Revoke</button></div> : null },
          ]} />
        </Panel>
      ) : (
        <Panel flush>
          <ErrorBox error={tokens.error} />
          <DataTable rows={tokens.data ?? []} empty="No enrollment codes." columns={[
            { key: 'n', label: 'Name', render: (t) => <b>{t.name}</b> },
            { key: 's', label: 'State', render: (t) => <Pill tone={t.state === 'active' ? 'ok' : t.state === 'revoked' ? 'crit' : 'warn'}>{fmt.label(t.state)}</Pill> },
            { key: 'u', label: 'Uses', render: (t) => `${t.uses}${t.maxUses ? ` / ${t.maxUses}` : ''}` },
            { key: 'url', label: 'Server address', render: (t) => <span className="mono">{t.serverUrl}</span> },
            { key: 'e', label: 'Expires', render: (t) => fmt.date(t.expiresAt) },
            { key: 'b', label: 'Created', render: (t) => `${fmt.day(t.createdAt)}${t.createdBy ? ` · ${t.createdBy}` : ''}` },
            { key: 'x', label: '', render: (t) => t.state === 'active' ? <div style={{ display: 'flex', gap: 6 }}><button className="btn sm" onClick={() => void showCode(t)}>Show code</button><button className="btn sm" onClick={() => void revokeToken(t)}>Revoke</button></div> : null },
          ]} />
        </Panel>
      )}
      {tab === 'agents' && rows.length === 0 && (tokens.data ?? []).some((t) => t.state === 'active') && (
        <Panel title="Installed the agent but the device is not listed?">
          <ul className="plain">
            <li>On the device, open <span className="mono">C:\ProgramData\Zentriq\Agent\Logs\agent.log</span>. Each attempt is logged with the reason it failed.</li>
            <li><b>“does not support proxies with the https scheme”</b> — a proxy was entered during setup. Leave <i>Proxy</i> empty unless the device must use a web proxy. To fix an installed agent run in an elevated PowerShell: <span className="mono">Remove-ItemProperty HKLM:\SOFTWARE\Zentriq\Agent -Name ProxyUrl; Restart-Service ZentriqAgent</span>, or install the latest agent MSI.</li>
            <li><b>Cannot connect / timed out</b> — the device must reach this server on TCP {pkg.data?.agentPort ?? 8444}. Check the server address in the code and allow the port in Windows Firewall on this server.</li>
            <li><b>Enrollment code rejected</b> — the code expired, was revoked or reached its maximum number of devices. Create a new code.</li>
            <li>A code&apos;s <i>Uses</i> count goes up when a device enrolls successfully; 0 uses means no device has enrolled with it yet.</li>
          </ul>
        </Panel>
      )}
      <Panel title="How agents connect">
        <ul className="plain">
          <li>Install <b>ZentriqAgent-x64.msi</b> with the enrollment code (Windows 10/11, Windows Server 2016+ with .NET Framework 4.8).</li>
          <li>The agent creates its own key, receives a client certificate from this server and then authenticates with it (mutual TLS on port {pkg.data?.agentPort ?? 8444}). The enrollment code is deleted from the device after use.</li>
          <li>It checks in every 2 minutes and sends full inventory every 12 hours, or within about 2 minutes when you click <i>Collect now</i>. It also carries out service start/stop/restart requested on the asset page. Inventory is queued while the device is offline.</li>
          <li>Devices found by both an agent and an agentless scan are merged into one asset (matched by SMBIOS UUID, serial number and MAC address).</li>
        </ul>
      </Panel>
      {newOpen && <NewCodeDialog defaultUrl={suggestedUrl(pkg.data)} onClose={() => setNewOpen(false)} onCreated={(t) => { setNewOpen(false); setCreated(t); setTab('codes'); tokens.reload(); }} />}
    </>
  );
}

function NewCodeDialog({ defaultUrl, onClose, onCreated }: { defaultUrl: string; onClose: () => void; onCreated: (t: NewEnrollmentToken) => void }) {
  const [name, setName] = useState('');
  const [days, setDays] = useState(30);
  const [maxUses, setMaxUses] = useState('');
  const [url, setUrl] = useState(defaultUrl);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const t = await api.post<NewEnrollmentToken>('/agents/enrollment-tokens', { name, validDays: days, maxUses: maxUses ? Number(maxUses) : null, serverUrl: url.trim() || null });
      onCreated(t);
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as { errors?: Record<string, string[]> } | null) : null;
      setError(body?.errors ? Object.values(body.errors).flat().join(' ') : err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="nc-title">
      <form className="modal-card" onSubmit={submit}>
        <header><h2 id="nc-title">New enrollment code</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder="e.g. Laptops – Intune rollout" /></label>
          <label>Server address agents use
            <input className="in mono" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://zentriq.example.com:8444" />
            <small className="muted">For devices outside the LAN use a public name that reaches this server's agent port.</small>
          </label>
          <div className="form-grid">
            <label>Valid for (days) <input className="in" type="number" min={1} max={365} value={days} onChange={(e) => setDays(Number(e.target.value))} /></label>
            <label>Maximum devices (optional) <input className="in" type="number" min={1} value={maxUses} onChange={(e) => setMaxUses(e.target.value)} placeholder="Unlimited" /></label>
          </div>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !name.trim()}>{busy ? 'Creating…' : 'Create code'}</button>
        </footer>
      </form>
    </div>
  );
}
