import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { AgentPackage, AgentRow, AgentSettingsResponse, EnrollmentToken, NewEnrollmentToken } from '../../api/types';
import { Modal } from '../../components/Modal';
import { Icon } from '../../components/Icon';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CopyBox } from '../../components/CopyBox';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Tone } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';

const HEALTH: Record<AgentRow['health'], [string, Tone]> = {
  online: ['Online', 'ok'],
  stale: ['Offline', 'warn'],
  offline: ['Offline > 7 days', 'crit'],
  never: ['Never checked in', ''],
  revoked: ['Revoked', 'crit'],
  replaced: ['Replaced by a newer enrollment', ''],
};

/** "30 min", "1 hour", "3 hours", "1 day". */
/** How long ago, as a duration: "4 min", "2 h 15 min", "3 d 4 h". */
const duration = (iso: string) => {
  const m = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 60000));
  if (m < 1) return 'less than 1 min';
  if (m < 60) return `${m} min`;
  if (m < 1440) return `${Math.floor(m / 60)} h${m % 60 ? ` ${m % 60} min` : ''}`;
  return `${Math.floor(m / 1440)} d${Math.floor((m % 1440) / 60) ? ` ${Math.floor((m % 1440) / 60)} h` : ''}`;
};

export const every = (m: number) => m % 1440 === 0 ? `${m / 1440} day${m === 1440 ? '' : 's'}` : m % 60 === 0 ? `${m / 60} hour${m === 60 ? '' : 's'}` : `${m} min`;
const CHECKIN = [5, 15, 30, 60, 120, 180];
const INVENTORY = [30, 60, 120, 180, 360, 720, 1440];

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
  const [tab, setTab] = useState<'agents' | 'codes' | 'settings'>('agents');
  const [picked, setPicked] = useState<string[]>([]);
  const [schedule, setSchedule] = useState<{ ids: string[] | 'all' } | null>(null);
  const [uninstallOpen, setUninstallOpen] = useState(false);
  const [control, setControl] = useState<AgentRow | null>(null);
  const [created, setCreated] = useState<NewEnrollmentToken | null>(null);
  const [newOpen, setNewOpen] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [live, setLive] = useState(0);
  // Online / offline changes within seconds (live connections): keep the list current.
  useEffect(() => { const t = setInterval(() => { if (!document.hidden) setLive((x) => x + 1); }, 10000); return () => clearInterval(t); }, []);
  const agents = useApi(() => api.get<AgentRow[]>('/agents'), [live]);
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
      message: 'At its next check-in (set by the check-in interval) each agent downloads the package from this server over its secure agent connection, verifies the checksum and installs it silently. The agent service restarts for a few seconds; enrollment, certificate and history are kept. Inventory and device actions continue afterwards.',
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
  async function deleteToken(t: EnrollmentToken) {
    if (!(await confirm({ title: `Delete enrollment code "${t.name}"?`, confirmLabel: 'Delete', danger: true, message: 'The code is removed from the list. Devices enrolled with it are not affected.' }))) return;
    try { await api.del(`/agents/enrollment-tokens/${t.id}`); tokens.reload(); } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }
  async function deleteInactive() {
    const n = (tokens.data ?? []).filter((t) => t.state !== 'active').length;
    if (!(await confirm({ title: `Delete ${n} old enrollment code(s)?`, confirmLabel: 'Delete', danger: true, message: 'Revoked, expired and used-up codes are removed. Devices enrolled with them are not affected.' }))) return;
    try { const r = await api.post<{ deleted: number }>('/agents/enrollment-tokens/delete-inactive', {}); setMsg(`${r.deleted} enrollment code(s) deleted.`); tokens.reload(); }
    catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }
  async function revokeToken(t: EnrollmentToken) {
    if (!(await confirm({ title: `Revoke enrollment code "${t.name}"?`, danger: true, confirmLabel: 'Revoke code', message: 'Devices can no longer enroll with this code (deployments that still use it will fail). Agents already enrolled are not affected.' }))) return;
    await api.post(`/agents/enrollment-tokens/${t.id}/revoke`);
    tokens.reload();
  }

  return (
    <>
      <PageHead title="Agents"
        actions={<>
          {pkg.data?.package && <a className="btn" href={pkg.data.package.url} download>Download agent</a>}
          {manage && <button className="btn" onClick={() => setUninstallOpen(true)}><Icon name="key" size={13} /> Agent code</button>}
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
            <div className="code-pair">
              <label>Server address<CopyBox value={created.serverUrl} label="Server address" /></label>
              <label>Enrollment code<CopyBox value={created.code} label="Enrollment code" /></label>
            </div>
            <small className="muted">In the installer wizard enter both, and choose how often the agent checks its connection and sends inventory (this code suggests every {every(created.checkinMinutes)} and {every(created.inventoryMinutes)}).</small>
            <label>Silent install (Intune, SCCM, GPO or an elevated prompt)<CopyBox value={created.installCommand} label="Install command" rows={2} /></label>
            <small className="muted">Devices outside your network need <span className="mono">{created.serverUrl}</span> to be reachable (published through your firewall). If the server&apos;s address changes, agents on the network find it again by themselves.</small>
          </div>
        </Panel>
      )}
      <Kpis items={[
        { label: 'Agents', value: current.length },
        { label: 'Online', value: count('online') },
        { label: 'Offline', value: count('stale') + count('offline') },
        { label: 'Agent port', value: pkg.data ? <span className="mono">{pkg.data.agentPort || 'disabled'}</span> : '—' },
      ]} />
      <div className="tabs" role="tablist">
        <button className={tab === 'agents' ? 'on' : ''} onClick={() => setTab('agents')}>Agents<span className="cnt">{current.length}</span></button>
        {manage && <button className={tab === 'codes' ? 'on' : ''} onClick={() => setTab('codes')}>Enrollment codes<span className="cnt">{tokens.data?.length ?? 0}</span></button>}
        {manage && <button className={tab === 'settings' ? 'on' : ''} onClick={() => setTab('settings')}>Settings</button>}
      </div>
      {tab === 'agents' && hidden > 0 && (
        <label className="row-check" style={{ margin: '0 0 8px', fontSize: 12.5 }}>
          <input type="checkbox" checked={showOld} onChange={(e) => setShowOld(e.target.checked)} /> Show {hidden} older, replaced or revoked registration(s)
        </label>
      )}
      {tab === 'settings' ? <SettingsTab onSaved={(t) => setMsg(t)} onAll={() => setSchedule({ ids: 'all' })} /> : tab === 'agents' ? (
        <Panel flush title="Agents" sub="Each agent checks its connection and sends inventory on its own schedule"
          right={manage && <div style={{ display: 'flex', gap: 6 }}>
            {picked.length > 0 && <button className="btn sm primary" onClick={() => setSchedule({ ids: picked })}><Icon name="clock" size={12} /> Schedule ({picked.length})</button>}
            <button className="btn sm" onClick={() => setSchedule({ ids: 'all' })}><Icon name="clock" size={12} /> Schedule for all</button>
          </div>}>
          <DataTable rows={rows} empty={agents.loading ? 'Loading…' : 'No agents yet. Create an enrollment code and install the agent MSI on a device.'} columns={[
            ...(manage ? [{ key: 'sel', label: <input type="checkbox" aria-label="Select all" checked={rows.length > 0 && picked.length === rows.filter((x) => x.status === 'active').length} onChange={(e) => setPicked(e.target.checked ? rows.filter((x) => x.status === 'active').map((x) => x.id) : [])} />,
              render: (r: AgentRow) => r.status === 'active' ? <input type="checkbox" aria-label={`Select ${r.hostname ?? r.id}`} checked={picked.includes(r.id)} onChange={() => setPicked(picked.includes(r.id) ? picked.filter((x) => x !== r.id) : [...picked, r.id])} /> : null }] : []),
            { key: 'h', label: 'Device', render: (r) => <>{r.assetId ? <Link to={`/assets/${r.assetId}`}><b>{r.hostname ?? r.id}</b></Link> : <b>{r.hostname ?? r.id}</b>}<TamperState r={r} /></> },
            { key: 'os', label: 'Operating system', className: 'wrap', render: (r) => r.osName ?? <span className="faint">—</span> },
            { key: 'ip', label: 'IP address', render: (r) => r.lastIp ? <span className="mono">{r.lastIp}</span> : <span className="faint">—</span> },
            { key: 's', label: 'Connection', render: (r) => r.health === 'online'
              ? <Pill tone="ok">Online</Pill>
              : (r.health === 'stale' || r.health === 'offline') && r.lastCheckinAt
                ? <span title={`Last contact ${fmt.date(r.lastCheckinAt)}`}><Pill tone={r.health === 'offline' ? 'crit' : 'warn'}>Offline · {duration(r.lastCheckinAt)}</Pill></span>
                : <Pill tone={HEALTH[r.health][1]}>{HEALTH[r.health][0]}</Pill> },
            { key: 'sch', label: 'Schedule', render: (r) => r.status === 'active' ? <span className="faint" style={{ fontSize: 12.5 }}>check-in {every(r.checkinMinutes)}<br />inventory {every(r.inventoryMinutes)}</span> : '' },
            { key: 'c', label: 'Last contact', render: (r) => <span className="faint" style={{ fontSize: 12.5 }}><span title={fmt.date(r.lastCheckinAt)}>{r.online ? 'now' : fmt.ago(r.lastCheckinAt)}</span><br /><span title={`Last inventory ${fmt.date(r.lastInventoryAt)}`}>inventory {fmt.ago(r.lastInventoryAt)}</span></span> },
            { key: 'v', label: 'Version', render: (r) => <span title={r.certificateExpires ? `Certificate expires ${fmt.day(r.certificateExpires)}` : ''}>{r.version}<UpdateState r={r} /></span> },
            { key: 'x', label: '', render: (r) => manage && r.status === 'active' ? <div style={{ display: 'flex', gap: 6, flexWrap: 'nowrap', justifyContent: 'flex-end' }}>{r.status === 'active' && r.updateAvailable && r.canSelfUpdate && !(r.update && ['queued', 'sent', 'running'].includes(r.update.status)) && <button className="btn sm primary" onClick={() => void update([r])}>Update</button>}{r.status === 'active' && r.updateAvailable && !r.canSelfUpdate && <button className="btn sm" onClick={() => void manualUpdate(r)}>Update…</button>}<button className="btn sm" title="Collect inventory now" onClick={() => void collect(r)}>Collect</button><button className="btn sm" title="Tamper protection, device code, restart or disable" onClick={() => setControl(r)}>Agent…</button><button className="btn sm danger" onClick={() => void revoke(r)}>Revoke</button></div> : null },
          ]} />
        </Panel>
      ) : (
        <Panel flush title="Enrollment codes" sub="20 characters; given with the server address when the agent is installed"
          right={(tokens.data ?? []).some((t) => t.state !== 'active') && <button className="btn sm" onClick={() => void deleteInactive()}><Icon name="trash" size={12} /> Delete old codes ({(tokens.data ?? []).filter((t) => t.state !== 'active').length})</button>}>
          <ErrorBox error={tokens.error} />
          <DataTable rows={tokens.data ?? []} empty="No enrollment codes." columns={[
            { key: 'n', label: 'Name', render: (t) => <b>{t.name}</b> },
            { key: 's', label: 'State', render: (t) => <Pill tone={t.state === 'active' ? 'ok' : t.state === 'revoked' ? 'crit' : 'warn'}>{fmt.label(t.state)}</Pill> },
            { key: 'u', label: 'Uses', render: (t) => `${t.uses}${t.maxUses ? ` / ${t.maxUses}` : ''}` },
            { key: 'url', label: 'Server address', render: (t) => <span className="mono">{t.serverUrl}</span> },
            { key: 'e', label: 'Expires', render: (t) => fmt.date(t.expiresAt) },
            { key: 'b', label: 'Created', render: (t) => `${fmt.day(t.createdAt)}${t.createdBy ? ` · ${t.createdBy}` : ''}` },
            { key: 'x', label: '', render: (t) => t.state === 'active'
              ? <div style={{ display: 'flex', gap: 6 }}><button className="btn sm" onClick={() => void showCode(t)}>Show code</button><button className="btn sm" onClick={() => void revokeToken(t)}>Revoke</button></div>
              : <button className="btn sm danger" onClick={() => void deleteToken(t)}><Icon name="trash" size={12} /> Delete</button> },
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
      {schedule && <ScheduleDialog ids={schedule.ids} onClose={() => setSchedule(null)} onDone={(m) => { setSchedule(null); setMsg(m); setPicked([]); agents.reload(); }} />}
      {uninstallOpen && <UninstallCodeDialog onClose={() => setUninstallOpen(false)} />}
      {control && <AgentControlDialog agent={control} onClose={() => setControl(null)} onDone={(m) => { setControl(null); setMsg(m); agents.reload(); }} />}
      {newOpen && <NewCodeDialog defaultUrl={suggestedUrl(pkg.data)} onClose={() => setNewOpen(false)} onCreated={(t) => { setNewOpen(false); setCreated(t); setTab('codes'); tokens.reload(); }} />}
    </>
  );
}

function NewCodeDialog({ defaultUrl, onClose, onCreated }: { defaultUrl: string; onClose: () => void; onCreated: (t: NewEnrollmentToken) => void }) {
  const [name, setName] = useState('');
  const [days, setDays] = useState(30);
  const [maxUses, setMaxUses] = useState('');
  const [url, setUrl] = useState(defaultUrl);
  const [checkin, setCheckin] = useState(30);
  const [inventory, setInventory] = useState(180);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const t = await api.post<NewEnrollmentToken>('/agents/enrollment-tokens', { name, validDays: days, maxUses: maxUses ? Number(maxUses) : null, serverUrl: url.trim() || null, checkinMinutes: checkin, inventoryMinutes: inventory });
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
            <label>Check the connection every
              <select className="in" value={checkin} onChange={(e) => setCheckin(Number(e.target.value))}>{CHECKIN.map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
            <label>Send inventory every
              <select className="in" value={inventory} onChange={(e) => setInventory(Number(e.target.value))}>{INVENTORY.filter((m) => m >= checkin).map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
          </div>
          <small className="muted">Suggested in the installer; the person installing can change them, and you can change them later per agent. Console requests (collect now, service actions, updates) run at the next check-in.</small>
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
  if (u && ['queued', 'sent'].includes(u.status)) return <div><Pill tone="warn">{u.automatic ? `Auto-update to ${u.target}` : `Update to ${u.target} requested`}</Pill></div>;
  if (u && u.status === 'running') return <div><Pill tone="warn">Updating to {u.target}{u.automatic ? ' (automatic)' : ''}…</Pill></div>;
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

/** Check-in and inventory intervals for selected agents (or all), applied at their next check-in. */
function ScheduleDialog({ ids, onClose, onDone }: { ids: string[] | 'all'; onClose: () => void; onDone: (msg: string) => void }) {
  const [checkin, setCheckin] = useState(30);
  const [inventory, setInventory] = useState(180);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    setBusy(true); setError(null);
    try {
      const r = await api.put<{ message: string }>('/agents/schedule', { ...(ids === 'all' ? { all: true } : { agentIds: ids }), checkinMinutes: checkin, inventoryMinutes: inventory });
      onDone(r.message);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={ids === 'all' ? 'Schedule for all agents' : `Schedule for ${ids.length} agent(s)`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Apply'}</button></>}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <div className="form-v">
        <label>Check the connection every
          <select className="in" value={checkin} onChange={(e) => { const v = Number(e.target.value); setCheckin(v); if (inventory < v) setInventory(INVENTORY.find((m) => m >= v)!); }}>{CHECKIN.map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
        <label>Send inventory every
          <select className="in" value={inventory} onChange={(e) => setInventory(Number(e.target.value))}>{INVENTORY.filter((m) => m >= checkin).map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
        <small className="muted">Applied at each agent&apos;s next check-in. A device counts as offline when it misses two check-ins; its last contact is shown in IT Asset Inventory.</small>
      </div>
    </Modal>
  );
}

interface UninstallCodeState { protection?: boolean; active: boolean; code: string | null; expiresAt: string | null; createdBy: string | null; createdAt: string | null }
const UNINSTALL_EXE = '"C:\\Program Files\\Zentriq Agent\\ZentriqAgent.exe"';

/** One random code that removes the agent on any device, valid for one hour. */
function UninstallCodeDialog({ onClose }: { onClose: () => void }) {
  const confirm = useConfirm();
  const state = useApi(() => api.get<UninstallCodeState>('/agents/uninstall-code'), []);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const c = state.data;
  async function create() {
    if (c?.active && !(await confirm({ title: 'Create a new uninstall code?', confirmLabel: 'Create new code', message: 'The current code stops working on all devices.' }))) return;
    setBusy(true); setErr(null);
    try { await api.post('/agents/uninstall-code'); state.reload(); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  async function cancel() {
    if (!(await confirm({ title: 'Cancel the uninstall code?', confirmLabel: 'Cancel code', danger: true, message: 'The code stops working on all devices.' }))) return;
    setBusy(true); setErr(null);
    try { await api.del('/agents/uninstall-code'); state.reload(); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title="Agent code (uninstall, stop, disable)" onClose={onClose} footer={<>
      {c?.active && <button className="btn danger" style={{ marginRight: 'auto' }} disabled={busy} onClick={() => void cancel()}>Cancel code</button>}
      <button className="btn" onClick={onClose}>Close</button>
      <button className="btn primary" disabled={busy || !c} onClick={() => void create()}>{c?.active ? 'Create new code' : 'Create code'}</button>
    </>}>
      <div className="form-v">
        <ErrorBox error={state.error} />
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        {c && c.protection === false && <Callout tone="info" icon="info">Tamper protection is off (Agents › Settings): agents can be removed, stopped and disabled without a code.</Callout>}
        {c && (c.active && c.code ? <>
          <div className="big-code" aria-label="Uninstall code">{c.code}</div>
          <p style={{ margin: 0 }}>Works on <b>all devices</b> until <b>{fmt.date(c.expiresAt)}</b> (one hour){c.createdBy ? ` · created by ${c.createdBy}` : ''}.</p>
          <label>Remove the agent (on the device, as administrator)<CopyBox value={`${UNINSTALL_EXE} --uninstall ${c.code}`} label="Uninstall command" rows={2} /></label>
          <label>Stop the agent until the device restarts<CopyBox value={`${UNINSTALL_EXE} --stop ${c.code}`} label="Stop command" /></label>
          <label>Disable the agent (until enabled again with --enable)<CopyBox value={`${UNINSTALL_EXE} --disable ${c.code}`} label="Disable command" /></label>
          <small className="muted">Or Apps &gt; Zentriq Agent &gt; Modify &gt; Remove, and enter the code. <span className="mono">--pause {'<code>'}</span> turns tamper protection off for one hour so the Services console can stop or disable the agent. Online devices receive the code within seconds, others when they next connect. For one device only, use <b>Agent… › Code for this device</b> in the agents list.</small>
        </> : <p className="muted" style={{ margin: 0 }}>No code is active. Create one to remove, stop or disable the agent on devices: a random 16-character code that works on every device for one hour, then expires.</p>)}
      </div>
    </Modal>
  );
}

/** Defaults for new agents and uninstall protection. */
function SettingsTab({ onSaved, onAll }: { onSaved: (msg: string) => void; onAll: () => void }) {
  const data = useApi(() => api.get<AgentSettingsResponse>('/agents/settings'), []);
  const [f, setF] = useState<AgentSettingsResponse['settings'] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (data.data) setF(data.data.settings); }, [data.data]);
  async function save() {
    if (!f) return;
    setBusy(true); setError(null);
    try { await api.put('/agents/settings', f); onSaved('Agent settings saved.'); data.reload(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  if (!f) return <Panel title="Settings"><p className="muted">Loading…</p></Panel>;
  return (
    <div className="grid-2">
      <Panel title="Schedule for new agents" sub="Suggested in new enrollment codes and the installer">
        <div className="form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Check the connection every
            <select className="in" value={f.checkinMinutes} onChange={(e) => setF({ ...f, checkinMinutes: Number(e.target.value) })}>{CHECKIN.map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
          <label>Send inventory every
            <select className="in" value={f.inventoryMinutes} onChange={(e) => setF({ ...f, inventoryMinutes: Number(e.target.value) })}>{INVENTORY.filter((m) => m >= f.checkinMinutes).map((m) => <option key={m} value={m}>{every(m)}</option>)}</select></label>
          <div style={{ display: 'flex', gap: 8 }}>
            <button className="btn primary" disabled={busy} onClick={() => void save()}>Save</button>
            <button className="btn" onClick={onAll}>Apply a schedule to all agents…</button>
          </div>
        </div>
      </Panel>
      <Panel title="Tamper protection" sub="Stops users and local or domain administrators from removing, stopping or disabling the agent">
        <div className="form-v">
          <label className="row-check"><input type="checkbox" checked={f.uninstallProtection} onChange={(e) => setF({ ...f, uninstallProtection: e.target.checked })} /> Require a code to uninstall, stop or disable the agent</label>
          <ul className="plain muted" style={{ margin: 0, fontSize: 13 }}>
            <li>The agent service cannot be stopped, disabled or reconfigured in Services, with <span className="mono">sc</span> or PowerShell; its registry key and program folder are read-only for administrators.</li>
            <li>A change made behind its back (for example after taking ownership) is undone within two minutes and reported here and in the audit log; a watchdog task starts the agent again after a restart.</li>
            <li>On the device, a one-hour code (Agents › Agent code, or Agent… › Code for this device) allows <span className="mono">--uninstall</span>, <span className="mono">--stop</span>, <span className="mono">--disable</span> or <span className="mono">--pause</span>.</li>
            <li>From here you can always pause protection, restart or disable an agent (Agent… in the list). Updates from the console keep working.</li>
          </ul>
          <div><button className="btn primary" disabled={busy} onClick={() => void save()}>Save</button></div>
        </div>
      </Panel>
      <Panel title="Automatic updates" sub="Keep agents at the version of this server">
        <div className="form-v">
          <label className="row-check"><input type="checkbox" checked={f.autoUpdate} onChange={(e) => setF({ ...f, autoUpdate: e.target.checked })} /> Update agents automatically when this server has a newer agent</label>
          <label>At most this many agents update at the same time
            <input className="in" type="number" min={1} max={1000} value={f.autoUpdateMax} disabled={!f.autoUpdate} onChange={(e) => setF({ ...f, autoUpdateMax: Number(e.target.value) || 1 })} style={{ maxWidth: 140 }} /></label>
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>After Zentriq on this server is updated, each older agent updates itself at its next check-in: it downloads the package from this server over its secure connection, checks the checksum and installs it silently (the service restarts for a few seconds; enrollment and history are kept). A failed update is retried after 24 hours. Agents installed before self-update existed need the new MSI once. Off: update agents with the Update button.</p>
          <div><button className="btn primary" disabled={busy} onClick={() => void save()}>Save</button></div>
        </div>
      </Panel>
    </div>
  );
}

/** Tamper protection state and the last tampering the agent undid or refused (under the device name). */
function TamperState({ r }: { r: AgentRow }) {
  const t = r.tamper;
  if (r.status !== 'active' || !t) return null;
  const recent = t.lastAt && Date.now() - new Date(t.lastAt).getTime() < 7 * 86400000;
  return <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', marginTop: 2 }}>
    {t.state === 'paused' && <Pill tone="warn">Protection paused{t.until ? ` until ${new Date(t.until).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}` : ''}</Pill>}
    {recent && <span title={`${fmt.date(t.lastAt)}: ${t.last ?? ''}`}><Pill tone="crit">Tampering blocked · {fmt.ago(t.lastAt)}</Pill></span>}
  </div>;
}

/** Console control of one agent: its own code, pause / resume tamper protection, restart, disable. */
function AgentControlDialog({ agent, onClose, onDone }: { agent: AgentRow; onClose: () => void; onDone: (msg: string) => void }) {
  const confirm = useConfirm();
  const [code, setCode] = useState<{ code: string; expiresAt: string } | null>(null);
  const [hours, setHours] = useState(1);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const name = agent.hostname ?? agent.id;
  const t = agent.tamper;
  const state = !agent.uninstallProtected ? 'Off' : t?.state === 'paused' ? `Paused${t.until ? ` until ${fmt.date(t.until)}` : ''}` : t?.state === 'pending' ? 'Being applied' : 'On';
  async function getCode() {
    setBusy(true); setErr(null);
    try { setCode(await api.post<{ code: string; expiresAt: string }>(`/agents/${agent.id}/code`)); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  async function run(operation: 'pause' | 'resume' | 'restart' | 'disable') {
    if (operation === 'disable' && !(await confirm({
      title: `Disable the agent on ${name}?`, danger: true, confirmLabel: 'Disable agent',
      message: <>The agent service is set to Disabled and stops. The device stays <b>offline</b> here — no inventory, actions, updates or remote support — until someone enables the service on the device (<span className="mono">ZentriqAgent.exe --enable</span> as administrator) or reinstalls the agent.</>,
    }))) return;
    setBusy(true); setErr(null);
    try { const r = await api.post<{ message: string }>(`/agents/${agent.id}/control`, { operation, hours }); onDone(r.message); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Agent on ${name}`} onClose={onClose} footer={<button className="btn" onClick={onClose}>Close</button>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <p style={{ margin: 0 }}>Tamper protection on this device: <b>{state}</b>{t?.lastAt ? <> · last tampering blocked {fmt.date(t.lastAt)}<br /><span className="muted" style={{ fontSize: 12.5 }}>{t.last}</span></> : null}</p>
        {!agent.canControl && <Callout tone="warn" icon="alert">This agent ({agent.version}) predates console control. Update it first; uninstall codes work already.</Callout>}
        <div className="field-title">Code for this device</div>
        {code ? <><div className="big-code" aria-label="Device code">{code.code}</div>
          <small className="muted">Valid on {name} only, until {fmt.date(code.expiresAt)}. On the device as administrator: <span className="mono">ZentriqAgent.exe --uninstall | --stop | --disable | --pause {code.code}</span></small></>
          : <div><button className="btn" disabled={busy || !agent.uninstallProtected} onClick={() => void getCode()}><Icon name="key" size={13} /> Show a one-hour code</button></div>}
        <div className="field-title">From the console</div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          {t?.state === 'paused'
            ? <button className="btn" disabled={busy || !agent.canControl} onClick={() => void run('resume')}>Resume protection now</button>
            : <><button className="btn" disabled={busy || !agent.canControl || !agent.uninstallProtected} onClick={() => void run('pause')}>Pause protection for</button>
              <select className="in" value={hours} onChange={(e) => setHours(Number(e.target.value))} aria-label="Pause for" style={{ width: 'auto' }}>{[1, 2, 4, 8, 24].map((h) => <option key={h} value={h}>{h} hour{h > 1 ? 's' : ''}</option>)}</select></>}
          <button className="btn" disabled={busy || !agent.canControl} onClick={() => void run('restart')}>Restart agent</button>
          <button className="btn danger" disabled={busy || !agent.canControl} onClick={() => void run('disable')}>Disable agent…</button>
        </div>
        <small className="muted">While paused, administrators on the device can stop or disable the agent in Services; protection returns by itself afterwards. Online agents act within seconds, others at their next check-in.</small>
      </div>
    </Modal>
  );
}
