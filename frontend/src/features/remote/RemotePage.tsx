import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { CopyBox } from '../../components/CopyBox';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { TrustPanel } from './TrustPanel';
import { openRemote, VIEW_LABEL, type RemoteDevice, type RemoteSession, type RemoteSettings, type RemoteStatus, type RemoteView } from './remote';

type Tab = 'devices' | 'sessions' | 'setup';
const TABS: [Tab, string][] = [['devices', 'Devices'], ['sessions', 'Session history'], ['setup', 'Deployment & settings']];
const err = (e: unknown) => (e instanceof Error ? e.message : String(e));

export function RemotePage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'devices') as Tab;
  const status = useApi(() => api.get<RemoteStatus>('/remote/status'), []);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit' | 'ok'; text: string } | null>(null);
  const s = status.data;
  return (
    <>
      <PageHead title="Remote Support" desc="See and control devices, open a terminal or transfer files — with the user's consent."
        actions={can('remote.unattended') && s?.running && <button className="btn" onClick={() => openRemote({}, 'console').catch((e) => setMsg({ tone: 'crit', text: err(e) }))}><Icon name="remote" size={13} /> Open remote console</button>} />
      <div className="tabs" role="tablist">
        {TABS.filter(([t]) => t !== 'setup' || can('remote.manage')).map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/remote?tab=${t}`, true)}>{l}</button>)}
      </div>
      <ErrorBox error={status.error} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {s && !s.installed && <Callout tone="warn" icon="info">{s.message}</Callout>}
      {s && s.installed && !s.running && <Callout tone="crit" icon="alert">Remote support is installed but not running: {s.message}</Callout>}
      {s && tab === 'devices' && <Devices status={s} onMsg={setMsg} onChanged={status.reload} />}
      {tab === 'sessions' && <Sessions />}
      {s && tab === 'setup' && <Setup status={s} onChanged={status.reload} />}
    </>
  );
}

// ---- Devices ------------------------------------------------------------------------------------------------

function Devices({ status, onMsg, onChanged }: { status: RemoteStatus; onMsg: (m: { tone: 'info' | 'crit' | 'ok'; text: string } | null) => void; onChanged: () => void }) {
  const { can } = useAuth();
  const confirm = useConfirm();
  const [filter, setFilter] = useState('');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  useEffect(() => { const t = setTimeout(() => setSearch(q), 300); return () => clearTimeout(t); }, [q]);
  const list = useApi(() => api.get<RemoteDevice[]>('/remote/devices', { q: search, filter }), [search, filter]);
  const manage = can('remote.manage');
  const rows = list.data ?? [];

  async function open(d: RemoteDevice, view: RemoteView) {
    onMsg(null);
    try { await openRemote({ assetId: d.assetId, nodeId: d.nodeId }, view); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  async function deploy(op: 'install' | 'uninstall', assetIds: string[]) {
    if (op === 'uninstall' && !(await confirm({ title: `Remove Zentriq Remote Support from ${assetIds.length} device(s)?`, message: 'Remote sessions to these devices will no longer be possible until it is installed again.', confirmLabel: 'Remove', danger: true }))) return;
    try {
      const r = await api.post<{ message: string }>('/remote/deploy', { assetIds, operation: op });
      onMsg({ tone: 'info', text: r.message });
      setPicked([]);
      setTimeout(list.reload, 1500);
    } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  async function sync() {
    try { onMsg({ tone: 'ok', text: (await api.post<{ message: string }>('/remote/sync')).message }); list.reload(); onChanged(); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  const pickable = rows.filter((d) => d.assetId);
  const toggle = (id: string) => setPicked(picked.includes(id) ? picked.filter((x) => x !== id) : [...picked, id]);

  return (
    <>
      <Kpis items={[
        { label: 'With remote support', value: status.devices.toLocaleString(), sub: `${status.online.toLocaleString()} online now` },
        { label: 'Ready to install', value: status.readyToInstall.toLocaleString(), sub: 'Zentriq Agent can install it' },
        { label: 'Not matched', value: status.unmapped.toLocaleString(), sub: 'remote agents without an asset' },
        { label: 'Sessions (24 h)', value: status.sessions24h.toLocaleString(), sub: status.lastSync ? `synced ${fmt.ago(status.lastSync)}` : 'not synced yet', to: '/remote?tab=sessions' },
      ]} />
      <Panel flush title="Devices" sub="Windows devices and every device with the remote support agent" right={<div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <input className="in" placeholder="Name or IP…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search devices" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Show">
          <option value="">All</option><option value="online">Online</option><option value="installed">Remote support installed</option>
          <option value="missing">Not installed</option><option value="unmapped">Not matched to an asset</option>
        </select>
        {manage && picked.length > 0 && <>
          <button className="btn sm primary" onClick={() => void deploy('install', picked)}>Install ({picked.length})</button>
          <button className="btn sm" onClick={() => void deploy('uninstall', picked)}>Remove ({picked.length})</button>
        </>}
        {manage && <button className="btn sm" onClick={() => void sync()} title="Read the device list from the remote support server now"><Icon name="retry" size={12} /> Refresh</button>}
      </div>}>
        <ErrorBox error={list.error} />
        <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
          ...(manage ? [{ key: 'sel', label: <input type="checkbox" aria-label="Select all" checked={pickable.length > 0 && picked.length === pickable.length} onChange={(e) => setPicked(e.target.checked ? pickable.map((d) => d.assetId!) : [])} />,
            render: (d: RemoteDevice) => d.assetId ? <input type="checkbox" aria-label={`Select ${d.name}`} checked={picked.includes(d.assetId)} onChange={() => toggle(d.assetId!)} /> : null }] : []),
          { key: 'n', label: 'Device', className: 'wrap', render: (d) => <>{d.assetId ? <Link to={`/assets/${d.assetId}`}>{d.hostname ?? d.name}</Link> : <b>{d.name}</b>}
            <div className="faint" style={{ fontSize: 11.5 }}>{[d.os, d.ip, d.department].filter(Boolean).join(' · ')}</div>
            {d.nodeId && !d.assetId && <div className="faint" style={{ fontSize: 11.5 }}>Not matched to an asset (different host name)</div>}</> },
          { key: 's', label: 'Remote support', render: (d) => !d.nodeId ? <Pill>Not installed</Pill> : d.online ? <Pill tone="ok">Online</Pill>
            : <span title={d.lastConnectedAt ? fmt.date(d.lastConnectedAt) : ''}><Pill tone="warn">Offline</Pill>{d.lastConnectedAt && <div className="faint" style={{ fontSize: 11 }}>seen {fmt.ago(d.lastConnectedAt)}</div>}</span> },
          { key: 'a', label: 'Last request', className: 'wrap', render: (d) => d.lastAction ? <span title={d.lastAction.detail ?? ''}><Pill tone={d.lastAction.status === 'succeeded' ? 'ok' : d.lastAction.status === 'failed' ? 'crit' : 'info'}>{fmt.label(d.lastAction.status)}</Pill>
            {d.lastAction.status === 'failed' && <div className="faint clamp-2" style={{ fontSize: 11, maxWidth: 240 }}>{d.lastAction.detail}</div>}</span> : '' },
          { key: 'x', label: '', render: (d) => d.nodeId
            ? <div style={{ display: 'flex', gap: 6 }}>
                <button className="btn sm primary" disabled={!d.online} onClick={() => void open(d, 'desktop')} title={d.online ? 'Remote desktop' : 'The device is offline'}>Desktop</button>
                <button className="btn sm" disabled={!d.online} onClick={() => void open(d, 'terminal')}>Terminal</button>
                <button className="btn sm" disabled={!d.online} onClick={() => void open(d, 'files')}>Files</button>
              </div>
            : manage && d.assetId ? (d.canInstall
              ? <button className="btn sm" onClick={() => void deploy('install', [d.assetId!])}>Install</button>
              : <span className="faint" style={{ fontSize: 12 }}>{d.hasAgent ? 'Update the Zentriq Agent to install' : 'No Zentriq Agent — use the installer'}</span>) : null },
        ]} />
      </Panel>
      <p className="faint" style={{ fontSize: 12 }}>Sessions open in a new browser tab. {status.publicUrl && <>The remote support server is <span className="mono">{status.publicUrl}</span> — the first time, your browser may ask you to trust its certificate.</>}</p>
    </>
  );
}

// ---- Sessions -------------------------------------------------------------------------------------------------

function Sessions() {
  const [page, setPage] = useState(1);
  const d = useApi(() => api.get<Paged<RemoteSession>>('/remote/sessions', { page, pageSize: 50 }), [page]);
  return (
    <Panel flush title="Session history" sub="Every remote session opened from Zentriq (also in the audit log)">
      <ErrorBox error={d.error} />
      <DataTable rows={d.data?.items ?? []} empty={d.loading ? 'Loading…' : 'No remote sessions yet.'} columns={[
        { key: 't', label: 'When', render: (s) => <span title={fmt.date(s.at)}>{fmt.date(s.at)}</span> },
        { key: 'u', label: 'Technician', render: (s) => s.user },
        { key: 'v', label: 'Session', render: (s) => VIEW_LABEL[s.view] ?? s.view },
        { key: 'd', label: 'Device', render: (s) => s.assetId ? <Link to={`/assets/${s.assetId}`}>{s.device ?? s.assetId}</Link> : s.device ?? '—' },
        { key: 'i', label: 'From', render: (s) => <span className="mono">{s.ip ?? '—'}</span> },
      ]} />
      {d.data && <Pager page={page} pageSize={50} total={d.data.total} onPage={setPage} />}
    </Panel>
  );
}

// ---- Deployment & settings ----------------------------------------------------------------------------------------

function Setup({ status, onChanged }: { status: RemoteStatus; onChanged: () => void }) {
  const settings = useApi(() => api.get<RemoteSettings>('/remote/settings'), []);
  const [form, setForm] = useState<RemoteSettings | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (settings.data) setForm(settings.data); }, [settings.data]);
  async function save() {
    if (!form) return;
    setBusy(true); setMsg(null);
    try { await api.put('/remote/settings', form); setMsg({ tone: 'ok', text: 'Saved. The new consent settings apply to the next session.' }); settings.reload(); onChanged(); }
    catch (e) { setMsg({ tone: 'crit', text: err(e) }); } finally { setBusy(false); }
  }
  async function download() {
    setMsg(null);
    try {
      const res = await fetch('/api/v1/remote/installer', { credentials: 'same-origin' });
      if (!res.ok) { const j = await res.json().catch(() => null) as { title?: string } | null; throw new Error(j?.title ?? res.statusText); }
      const url = URL.createObjectURL(await res.blob());
      const a = document.createElement('a'); a.href = url; a.download = 'ZentriqRemoteSupport.exe'; a.click();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    } catch (e) { setMsg({ tone: 'crit', text: err(e) }); }
  }
  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="grid-2">
        <Panel title="User consent and privacy">
          {!form ? <p className="muted">Loading…</p> : <div className="form-v">
            <label className="row-check"><input type="checkbox" checked={form.prompt} onChange={(e) => setForm({ ...form, prompt: e.target.checked })} /> Ask the person at the device to allow each session (desktop, terminal, files)</label>
            <label className="row-check"><input type="checkbox" checked={form.notify} onChange={(e) => setForm({ ...form, notify: e.target.checked })} /> Notify the person when a session starts</label>
            <label className="row-check"><input type="checkbox" checked={form.privacyBar} onChange={(e) => setForm({ ...form, privacyBar: e.target.checked })} /> Show a bar on the device's screen during remote desktop (with a disconnect button)</label>
            {!form.prompt && <Callout tone="warn" icon="alert">Without consent, technicians can connect to a device without the user approving it. Use this only where your policy allows unattended access (for example servers) and the users are informed.</Callout>}
            <div><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>
          </div>}
        </Panel>
        <Panel title="Install on devices">
          <div className="form-v">
            <p style={{ margin: 0 }}><b>Devices with the Zentriq Agent:</b> select them on the <Link to="/remote?tab=devices">Devices</Link> tab and click <b>Install</b>. The agent downloads the installer from this server, checks it and installs it within about 2 minutes.</p>
            <p style={{ margin: 0 }}><b>Other Windows devices:</b> download the installer and run it as administrator (or deploy it with Group Policy, Intune or another tool):</p>
            <div><button className="btn" disabled={!status.running} onClick={() => void download()}><Icon name="down" size={13} /> Download ZentriqRemoteSupport.exe</button></div>
            <CopyBox label="Silent install command" value="ZentriqRemoteSupport.exe -fullinstall" />
            <small className="muted">Remove it again with <span className="mono">ZentriqRemoteSupport.exe -fulluninstall</span>. Devices connect to <span className="mono">{status.publicUrl ?? `port ${status.port}`}</span> (outbound HTTPS); publish that port for devices outside your network.</small>
          </div>
        </Panel>
      </div>
      <Panel title="Server">
        <dl className="kv-grid" style={{ margin: 0 }}>
          <div><dt>Status</dt><dd>{status.running ? <Pill tone="ok">Running</Pill> : status.installed ? <Pill tone="crit">Not running</Pill> : <Pill>Not installed</Pill>}</dd></div>
          <div><dt>Address</dt><dd className="mono">{status.publicUrl ?? '—'}</dd></div>
          <div><dt>Windows service</dt><dd className="mono">ZentriqRemote</dd></div>
          <div><dt>Engine</dt><dd>MeshCentral (open source, Apache-2.0), bundled and configured by Zentriq</dd></div>
        </dl>
        <p className="faint" style={{ fontSize: 12, marginBottom: 0 }}>Technicians sign in through Zentriq with a link valid for 2 minutes; there is no separate password. Who may open sessions is controlled by the permissions <span className="mono">remote.start</span> (sessions), <span className="mono">remote.manage</span> (install, settings) and <span className="mono">remote.unattended</span> (full remote console) in Users, Roles &amp; Sites.</p>
      </Panel>
      <TrustPanel />
    </>
  );
}
