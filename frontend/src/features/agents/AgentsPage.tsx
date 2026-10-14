import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { AgentPackage, AgentRow, EnrollmentToken, NewEnrollmentToken } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CopyBox } from '../../components/CopyBox';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Tone } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';

const HEALTH: Record<AgentRow['health'], [string, Tone]> = {
  online: ['Online', 'ok'],
  stale: ['Not seen recently', 'warn'],
  offline: ['Offline > 7 days', 'crit'],
  never: ['Never checked in', ''],
  revoked: ['Revoked', 'crit'],
  replaced: ['Replaced by a newer enrollment', ''],
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
  const confirm = useConfirm();
  const [tab, setTab] = useState<'agents' | 'codes'>('agents');
  const [created, setCreated] = useState<NewEnrollmentToken | null>(null);
  const [newOpen, setNewOpen] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const agents = useApi(() => api.get<AgentRow[]>('/agents'), []);
  const pkg = useApi(() => api.get<AgentPackage>('/agents/package'), []);
  const tokens = useApi(() => (manage ? api.get<EnrollmentToken[]>('/agents/enrollment-tokens') : Promise.resolve([])), [manage]);
  const all = agents.data ?? [];
  const [showOld, setShowOld] = useState(false);
  // One row per device: when a device was enrolled more than once, keep the registration with the latest agent
  // (newest version, then most recent check-in). Replaced and revoked registrations are hidden unless asked for.
  const current = latestPerDevice(all.filter((r) => r.status === 'active'));
  const hidden = all.length - current.length;
  const rows = showOld ? all : current;
  const count = (h: AgentRow['health']) => current.filter((r) => r.health === h).length;
  const outdated = current.filter((r) => r.updateAvailable);
  const selfUpdatable = outdated.filter((r) => r.canSelfUpdate && r.update?.status !== 'running' && r.update?.status !== 'queued' && r.update?.status !== 'sent');
  const legacy = outdated.filter((r) => !r.canSelfUpdate);
  const updating = rows.some((r) => r.update && ['queued', 'sent', 'running'].includes(r.update.status));
  useEffect(() => {
    if (!updating) return;
    const t = setInterval(agents.reload, 10000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [updating]);

  async function revoke(a: AgentRow) {
    if (!(await confirm({ title: `Revoke the agent on ${a.hostname ?? a.id}?`, danger: true, confirmLabel: 'Revoke agent', message: 'Its certificate is no longer accepted: the device stops reporting inventory and cannot receive service or uninstall actions until it is enrolled again. The asset and its history are kept.' }))) return;
    await api.post(`/agents/${a.id}/revoke`);
    agents.reload();
  }
  async function collect(a: AgentRow) {
    const r = await api.post<{ message: string }>(`/agents/${a.id}/collect`);
    setMsg(r.message);
  }
  async function update(list: AgentRow[] | 'all') {
    const n = list === 'all' ? outdated.length : list.length;
    const target = pkg.data?.package?.version ?? rows.find((r) => r.latestVersion)?.latestVersion ?? 'the latest version';
    if (!(await confirm({
      title: list === 'all' ? `Update ${n} agent(s) to ${target}?` : `Update the agent on ${list[0]!.hostname ?? list[0]!.id} to ${target}?`,
      confirmLabel: 'Update agent',
      message: 'At its next check-in (within about 2 minutes) each agent downloads the package from this server over its secure agent connection, verifies the checksum and installs it silently. The agent service restarts for a few seconds; enrollment, certificate and history are kept. Inventory and device actions continue afterwards.',
    }))) return;
    setMsg(null);
    try {
      const r = await api.post<{ message: string }>('/agents/update', list === 'all' ? { all: true } : { agentIds: list.map((a) => a.id) });
      setMsg(r.message);
      agents.reload();
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }
  async function manualUpdate(a: AgentRow) {
    const ok = await confirm({
      title: `Update the agent on ${a.hostname ?? a.id}`,
      confirmLabel: 'Download agent MSI',
      message: `This agent (${a.version}) was installed before agents could update themselves, so it cannot receive the update from here. Install the new MSI on ${a.hostname ?? 'the device'} once: copy ZentriqAgent-x64.msi to it and run "msiexec /i ZentriqAgent-x64.msi /qn" in an elevated prompt, or assign the package with GPO, Intune or SCCM. No enrollment code is needed; the device keeps its registration and history. Afterwards the Update button works for it.`,
    });
    if (ok && pkg.data?.package) window.location.href = pkg.data.package.url;
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
    if (!(await confirm({ title: `Revoke enrollment code "${t.name}"?`, danger: true, confirmLabel: 'Revoke code', message: 'Devices can no longer enroll with this code (deployments that still use it will fail). Agents already enrolled are not affected.' }))) return;
    await api.post(`/agents/enrollment-tokens/${t.id}/revoke`);
    tokens.reload();
  }

  return (
    <>
      <PageHead title="Agents" desc="Always-on inventory from every Windows device."
        actions={<>
          {pkg.data?.package && <a className="btn" href={pkg.data.package.url} download>Download agent</a>}
          {manage && <button className="btn primary" onClick={() => setNewOpen(true)}>New enrollment code</button>}
        </>} />
      {msg && <Callout>{msg}</Callout>}
      <ErrorBox error={agents.error ?? pkg.error} />
      {pkg.data && !pkg.data.enrollmentAvailable && <Callout tone="warn" icon="alert">Agent enrollment is not configured on this server (agent CA missing). Run <span className="mono">zentriqctl repair</span> on the server.</Callout>}
      {manage && outdated.length > 0 && pkg.data?.package && (
        <Callout tone="info" icon="info">
          <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
            <span style={{ flex: '1 1 320px' }}>
              Agent <b>{pkg.data.package.version ?? 'latest'}</b> is available · {outdated.length} agent(s) out of date{legacy.length > 0 ? ` · ${legacy.length} need a one-time manual install (Update (manual))` : ''}
            </span>
            {selfUpdatable.length > 0 && <button className="btn primary sm" onClick={() => void update('all')}>Update {selfUpdatable.length} agent(s)</button>}
          </div>
        </Callout>
      )}
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
        { label: 'Agents', value: current.length },
        { label: 'Online', value: count('online') },
        { label: 'Not seen recently', value: count('stale') + count('offline') },
        { label: 'Agent port', value: pkg.data ? <span className="mono">{pkg.data.agentPort || 'disabled'}</span> : '—' },
      ]} />
      <div className="tabs" role="tablist">
        <button className={tab === 'agents' ? 'on' : ''} onClick={() => setTab('agents')}>Agents<span className="cnt">{current.length}</span></button>
        {manage && <button className={tab === 'codes' ? 'on' : ''} onClick={() => setTab('codes')}>Enrollment codes<span className="cnt">{tokens.data?.length ?? 0}</span></button>}
      </div>
      {tab === 'agents' && hidden > 0 && (
        <label className="row-check" style={{ margin: '0 0 8px', fontSize: 12.5 }}>
          <input type="checkbox" checked={showOld} onChange={(e) => setShowOld(e.target.checked)} /> Show {hidden} older, replaced or revoked registration(s)
        </label>
      )}
      {tab === 'agents' ? (
        <Panel flush>
          <DataTable rows={rows} empty={agents.loading ? 'Loading…' : 'No agents yet. Create an enrollment code and install the agent MSI on a device.'} columns={[
            { key: 'h', label: 'Device', render: (r) => <>{r.assetId ? <Link to={`/assets/${r.assetId}`}><b>{r.hostname ?? r.id}</b></Link> : <b>{r.hostname ?? r.id}</b>}<div className="faint" style={{ fontSize: 11.5 }}>{r.osName ?? ''}</div></> },
            { key: 's', label: 'Health', render: (r) => <Pill tone={HEALTH[r.health][1]}>{HEALTH[r.health][0]}</Pill> },
            { key: 'c', label: 'Last check-in', render: (r) => <span title={fmt.date(r.lastCheckinAt)}>{fmt.ago(r.lastCheckinAt)}</span> },
            { key: 'i', label: 'Last inventory', render: (r) => <span title={fmt.date(r.lastInventoryAt)}>{fmt.ago(r.lastInventoryAt)}</span> },
            { key: 'ip', label: 'Last address', render: (r) => <span className="mono">{r.lastIp ?? '—'}</span> },
            { key: 'v', label: 'Version', render: (r) => <>{r.version}<UpdateState r={r} /></> },
            { key: 'e', label: 'Certificate', render: (r) => r.certificateExpires ? `expires ${fmt.day(r.certificateExpires)}` : '—' },
            { key: 'x', label: '', render: (r) => manage && r.status === 'active' ? <div style={{ display: 'flex', gap: 6 }}>{r.status === 'active' && r.updateAvailable && r.canSelfUpdate && !(r.update && ['queued', 'sent', 'running'].includes(r.update.status)) && <button className="btn sm primary" onClick={() => void update([r])}>Update</button>}{r.status === 'active' && r.updateAvailable && !r.canSelfUpdate && <button className="btn sm" onClick={() => void manualUpdate(r)}>Update (manual)</button>}<button className="btn sm" onClick={() => void collect(r)}>Collect now</button><button className="btn sm" onClick={() => void revoke(r)}>Revoke</button></div> : null },
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

function UpdateState({ r }: { r: AgentRow }) {
  const u = r.update;
  if (r.status !== 'active') return null;
  if (u && ['queued', 'sent'].includes(u.status)) return <div><Pill tone="warn">Update to {u.target} requested</Pill></div>;
  if (u && u.status === 'running') return <div><Pill tone="warn">Updating to {u.target}…</Pill></div>;
  if (u && u.status === 'failed' && r.updateAvailable) return <div title={u.detail ?? ''}><Pill tone="crit">Update failed</Pill></div>;
  if (r.updateAvailable) return <div className="faint" style={{ fontSize: 11.5 }} title={r.canSelfUpdate ? '' : 'This agent predates console updates: install the new MSI once.'}>{r.latestVersion} available{r.canSelfUpdate ? '' : ' · install MSI once'}</div>;
  return null;
}

const ver = (v: string) => v.split('.').map((x) => parseInt(x, 10) || 0);
function newer(a: AgentRow, b: AgentRow) {
  const va = ver(a.version), vb = ver(b.version);
  for (let i = 0; i < 4; i++) if ((va[i] ?? 0) !== (vb[i] ?? 0)) return (va[i] ?? 0) > (vb[i] ?? 0);
  return (a.lastCheckinAt ?? '') > (b.lastCheckinAt ?? '');
}
/** The registration with the latest agent for each device (same asset, or same host name when not matched yet). */
function latestPerDevice(rows: AgentRow[]): AgentRow[] {
  const best = new Map<string, AgentRow>();
  for (const r of rows) {
    const key = r.assetId ?? `host:${(r.hostname ?? r.id).toLowerCase()}`;
    const cur = best.get(key);
    if (!cur || newer(r, cur)) best.set(key, r);
  }
  return rows.filter((r) => best.get(r.assetId ?? `host:${(r.hostname ?? r.id).toLowerCase()}`) === r);
}
