import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Tone } from '../../components/ui';
import { errText } from '../deployment/types';
import { describeTargets, emptyTargets, stateOf, TargetPicker, targetsEmpty, targetsOf, type TargetState, type Targets } from './Targets';

// ---- types ----------------------------------------------------------------------------------------------------------

interface Settings { autoScan: boolean; scanHours: number; rebootDelayMinutes: number; rebootMessage: string }
interface Summary {
  devices: number; scanned: number; upToDate: number; urgentDevices: number; rebootPending: number; scanFailed: number; notScanned: number; missingPatches: number;
  urgentPatches: number; failed: number; installing: number; installed30d: number; policies: number; olderAgents: number; settings: Settings;
}
interface Patch {
  id: string; platform: 'windows' | 'linux'; kb: string | null; title: string; classification: string; severity: string; product: string | null; package: string | null;
  version: string | null; releasedAt: string | null; size: number | null; reboot: boolean | null; cves: string[]; url: string | null; approval: 'none' | 'approved' | 'declined';
  approvedBy: string | null; approvedAt: string | null; firstSeenAt: string; missing: number; failed: number; pendingReboot: number; installed: number;
}
interface DeviceRow {
  id: string; device: string | null; os: string | null; platform: 'windows' | 'linux'; capable: boolean; lastSeenAt: string | null; scannedAt: string | null; scanOk: boolean | null;
  scanMessage: string | null; source: string | null; rebootPending: boolean; missing: number; failed: number; urgent: number; pendingReboot: number; installed: number;
  lastAction: string | null; lastOperation: string | null;
}
interface DevicePatch {
  id: string; kb: string | null; title: string; classification: string; severity: string; releasedAt: string | null; approval: string; reboot: boolean | null; state: string;
  detectedAt: string; installedAt: string | null; message: string | null; cves: string[]; package: string | null; version: string | null;
}
interface DeviceDetail {
  scan: { scannedAt: string; ok: boolean; message: string | null; source: string | null; rebootPending: boolean; missing: number } | null;
  patches: DevicePatch[]; actions: { id: string; operation: string; status: string; detail: string | null; requestedAt: string; completedAt: string | null }[];
}
interface Job {
  id: string; createdAt: string; name: string; note: string | null; reboot: string; allMissing: boolean; chosen: number; policy: string | null; createdBy: string | null;
  devices: number; skipped: number; waiting: number; running: number; succeeded: number; failed: number; patches: number;
}
interface JobDevice { assetId: string; device: string | null; patches: number; status: string; detail: string | null; actionId: string | null; requestedAt: string | null; completedAt: string | null }
interface Policy {
  id: string; name: string; enabled: boolean; platform: 'windows' | 'linux'; classifications: string[]; severities: string[]; approvedOnly: boolean; delayDays: number;
  targets: Targets; days: number[]; startTime: string; windowHours: number; reboot: 'never' | 'when-needed'; lastRunAt: string | null; lastJobId: string | null;
}

export const CLASS_LABEL: Record<string, string> = {
  critical: 'Critical Updates', security: 'Security Updates', definition: 'Definition Updates', updates: 'Updates', 'update-rollup': 'Update Rollups',
  'feature-pack': 'Feature Packs', 'service-pack': 'Service Packs', driver: 'Drivers', tools: 'Tools', upgrade: 'Upgrades', other: 'Other',
};
const SEVERITY: Record<string, [Tone, string]> = { critical: ['crit', 'Critical'], important: ['warn', 'Important'], moderate: ['info', 'Moderate'], low: ['', 'Low'], unspecified: ['', '—'] };
const STATE: Record<string, [Tone, string]> = { missing: ['warn', 'Missing'], failed: ['crit', 'Failed'], pending_reboot: ['info', 'Restart Pending'], installed: ['ok', 'Installed'] };
const STATUS: Record<string, [Tone, string]> = {
  queued: ['warn', 'Waiting for device'], sent: ['warn', 'Sent to agent'], running: ['warn', 'Installing'], succeeded: ['ok', 'Done'], failed: ['crit', 'Failed'], expired: ['', 'Expired'], skipped: ['', 'Skipped'],
};
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const sev = (s: string) => <Pill tone={SEVERITY[s]?.[0] ?? ''}>{SEVERITY[s]?.[1] ?? fmt.label(s)}</Pill>;

type Tab = 'overview' | 'updates' | 'devices' | 'deployments' | 'policies' | 'settings';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['updates', 'Missing Updates'], ['devices', 'Devices'], ['deployments', 'Deployments'], ['policies', 'Automatic Deployment'], ['settings', 'Settings']];

interface Ctx { tick: number; canApprove: boolean; canDeploy: boolean; changed: (t: string) => void; failed: (e: unknown) => void; deploy: (d: DeployPreset) => void }
interface DeployPreset { patchIds?: string[]; titles?: string[]; allMissing?: boolean; assets?: { id: string; name: string }[]; classifications?: string[] }

export function PatchPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview';
  const setTab = (t: Tab) => navigate(`/modules/patch?tab=${t}`, true);
  const [tick, setTick] = useState(0);
  const summary = useApi(() => api.get<Summary>('/patches/summary'), [tick]);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [deploy, setDeploy] = useState<DeployPreset | null>(null);
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const ctx: Ctx = { tick, canApprove: can('patch.approve'), canDeploy: can('patch.deploy'), changed, failed, deploy: setDeploy };
  const s = summary.data;
  return (
    <>
      <PageHead title="Patch Management" icon="patch" />
      {s && <Kpis items={[
        { label: 'Up To Date', value: `${s.upToDate} / ${s.scanned}`, sub: `${s.devices} device(s) with the agent`, to: '/modules/patch?tab=devices' },
        { label: 'Missing Critical / Security', value: s.urgentDevices.toLocaleString(), sub: `device(s) · ${s.urgentPatches} update(s)`, to: '/modules/patch?tab=updates' },
        { label: 'Missing Updates', value: s.missingPatches.toLocaleString(), sub: 'Distinct updates', to: '/modules/patch?tab=updates' },
        { label: 'Restart Pending', value: s.rebootPending.toLocaleString(), sub: 'device(s)', to: '/modules/patch?tab=devices' },
        { label: 'Failed', value: s.failed.toLocaleString(), sub: `${s.scanFailed} scan(s) failed`, to: '/modules/patch?tab=devices' },
        { label: 'Installing', value: s.installing.toLocaleString(), sub: `${s.installed30d} installed (30 d)`, to: '/modules/patch?tab=deployments' },
      ]} />}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{l}</button>)}
      </div>
      <ErrorBox error={summary.error} />
      {tab === 'overview' && s && <OverviewTab s={s} {...ctx} />}
      {tab === 'updates' && <UpdatesTab {...ctx} />}
      {tab === 'devices' && <DevicesTab {...ctx} openId={query.get('device')} />}
      {tab === 'deployments' && <DeploymentsTab {...ctx} />}
      {tab === 'policies' && <PoliciesTab {...ctx} />}
      {tab === 'settings' && s && <SettingsTab settings={s.settings} {...ctx} />}
      {deploy && <DeployDialog preset={deploy} onClose={() => setDeploy(null)} onDone={(t) => { setDeploy(null); changed(t); }} />}
    </>
  );
}

// ---- overview -------------------------------------------------------------------------------------------------------

function OverviewTab({ s, canDeploy, changed, failed, deploy }: Ctx & { s: Summary }) {
  const [busy, setBusy] = useState(false);
  async function scanAll() {
    setBusy(true);
    try { const r = await api.post<{ requested: number }>('/patches/scan', { all: true }); changed(`${r.requested} device(s) scan for missing updates now; results appear within minutes.`); }
    catch (e) { failed(e); } finally { setBusy(false); }
  }
  return (
    <div className="grid-2">
      <Panel title="How It Works" sub="Windows Update Agent on Windows; apt, dnf, yum or zypper on Linux">
        <ol className="steps-v" style={{ margin: 0, paddingLeft: 18, display: 'grid', gap: 8, fontSize: 13.5 }}>
          <li><b>Scan.</b> Every device with the Zentriq Agent looks for missing updates {s.settings.autoScan ? `every ${s.settings.scanHours} h` : 'when you ask (automatic scans are off)'}, from its own update source (Windows Update, WSUS or its repositories).</li>
          <li><b>Review.</b> <i>Missing Updates</i> lists each update with its classification, severity, CVEs and the devices that need it. Approve or decline.</li>
          <li><b>Deploy.</b> Install now on chosen devices, or let <i>Automatic Deployment</i> install them in a maintenance window, with or without a restart.</li>
        </ol>
        {canDeploy && <div style={{ display: 'flex', gap: 8, marginTop: 14, flexWrap: 'wrap' }}>
          <button className="btn" disabled={busy} onClick={() => void scanAll()}><Icon name="retry" size={14} /> {busy ? 'Asking…' : 'Scan All Devices'}</button>
          <button className="btn primary" onClick={() => deploy({ allMissing: true, classifications: ['critical', 'security'] })}><Icon name="patch" size={14} /> Install Critical & Security Updates</button>
        </div>}
      </Panel>
      <Panel title="Attention">
        <div style={{ display: 'grid', gap: 8 }}>
          {s.olderAgents > 0 && <Callout tone="warn" icon="alert">{s.olderAgents} agent(s) are older than build 114 and cannot scan or install updates. Update them in <Link to="/agents">Agents</Link>.</Callout>}
          {s.notScanned > 0 && <Callout tone="info">{s.notScanned} device(s) have not reported a scan yet.</Callout>}
          {s.scanFailed > 0 && <Callout tone="crit" icon="alert">{s.scanFailed} device(s) could not scan (for example, the Windows Update service is disabled). See <Link to="/modules/patch?tab=devices">Devices</Link>.</Callout>}
          {s.policies === 0 && <Callout tone="info">No automatic deployment policy yet. <Link to="/modules/patch?tab=policies">Add one</Link> to install security updates every week.</Callout>}
          {s.olderAgents === 0 && s.notScanned === 0 && s.scanFailed === 0 && s.policies > 0 && <span className="muted">Nothing needs your attention.</span>}
        </div>
      </Panel>
    </div>
  );
}

// ---- missing updates ------------------------------------------------------------------------------------------------

function UpdatesTab({ tick, canApprove, canDeploy, changed, failed, deploy }: Ctx) {
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [platform, setPlatform] = useState('');
  const [classification, setClassification] = useState('');
  const [severity, setSeverity] = useState('');
  const [approval, setApproval] = useState('');
  const [state, setState] = useState('missing');
  const [sel, setSel] = useState<string[]>([]);
  const [open, setOpen] = useState<Patch | null>(null);
  const list = useApi(() => api.get<Patch[]>('/patches', { q: search, platform, classification, severity, approval, state }), [tick, search, platform, classification, severity, approval, state]);
  const rows = list.data ?? [];
  const chosen = rows.filter((r) => sel.includes(r.id));
  async function approve(a: 'approved' | 'declined' | 'none') {
    try { const r = await api.post<{ changed: number }>('/patches/approval', { ids: sel, approval: a }); setSel([]); changed(`${r.changed} update(s) ${a === 'none' ? 'reset' : a}.`); }
    catch (e) { failed(e); }
  }
  const mixed = new Set(chosen.map((c) => c.platform)).size > 1;
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search title, KB, package or CVE…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search updates" />
        <select className="in" value={state} onChange={(e) => setState(e.target.value)} aria-label="State"><option value="missing">Missing on devices</option><option value="installed">Installed everywhere</option><option value="">All</option></select>
        <select className="in" value={platform} onChange={(e) => setPlatform(e.target.value)} aria-label="Platform"><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select>
        <select className="in" value={classification} onChange={(e) => setClassification(e.target.value)} aria-label="Classification"><option value="">Any classification</option>{Object.entries(CLASS_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
        <select className="in" value={severity} onChange={(e) => setSeverity(e.target.value)} aria-label="Severity"><option value="">Any severity</option>{['critical', 'important', 'moderate', 'low', 'unspecified'].map((k) => <option key={k} value={k}>{SEVERITY[k]![1] === '—' ? 'Not rated' : SEVERITY[k]![1]}</option>)}</select>
        <select className="in" value={approval} onChange={(e) => setApproval(e.target.value)} aria-label="Approval"><option value="">Any approval</option><option value="approved">Approved</option><option value="declined">Declined</option><option value="none">Not decided</option></select>
        <button className="btn" type="submit">Search</button>
      </form>
      {sel.length > 0 && <div className="toolbar-row" style={{ marginBottom: 10 }}>
        <b>{sel.length} selected</b>
        {canApprove && <><button className="btn sm" onClick={() => void approve('approved')}>Approve</button><button className="btn sm" onClick={() => void approve('declined')}>Decline</button><button className="btn sm" onClick={() => void approve('none')}>Reset</button></>}
        {canDeploy && <button className="btn sm primary" disabled={mixed} title={mixed ? 'Choose Windows or Linux updates, not both' : undefined}
          onClick={() => deploy({ patchIds: sel, titles: chosen.map((c) => c.title) })}>Install</button>}
        <button className="btn sm" onClick={() => setSel([])}>Clear</button>
      </div>}
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={rows} onRow={setOpen} rowTitle="Show the devices" empty={list.loading ? 'Loading…' : state === 'missing' ? 'No missing updates reported. Devices scan automatically; Overview › Scan All Devices asks now.' : 'No updates.'} columns={[
          { key: 'sel', label: <input type="checkbox" aria-label="Select all" checked={rows.length > 0 && sel.length === rows.length} onChange={(e) => setSel(e.target.checked ? rows.map((r) => r.id) : [])} />, title: 'Select', fixed: true,
            render: (r) => <input type="checkbox" aria-label={`Select ${r.title}`} checked={sel.includes(r.id)} onChange={() => setSel(sel.includes(r.id) ? sel.filter((x) => x !== r.id) : [...sel, r.id])} /> },
          { key: 't', label: 'Update', className: 'wrap', render: (r) => <><b>{r.title}</b><div className="faint" style={{ fontSize: 11.5 }}>
            {r.platform === 'linux' ? `Linux · ${r.product ?? 'repository'}` : [r.kb ? `KB${r.kb}` : null, r.product].filter(Boolean).join(' · ')}
            {r.cves.length > 0 && <> · {r.cves.slice(0, 3).join(', ')}{r.cves.length > 3 ? ` +${r.cves.length - 3}` : ''}</>}</div></> },
          { key: 'c', label: 'Classification', render: (r) => CLASS_LABEL[r.classification] ?? fmt.label(r.classification) },
          { key: 's', label: 'Severity', render: (r) => sev(r.severity) },
          { key: 'r', label: 'Released', render: (r) => r.releasedAt ? fmt.day(r.releasedAt) : <span className="faint">—</span> },
          { key: 'm', label: 'Missing On', render: (r) => <>{r.missing + r.failed > 0 ? <b>{r.missing + r.failed}</b> : <span className="faint">0</span>}{r.failed > 0 && <span className="faint"> ({r.failed} failed)</span>}{r.pendingReboot > 0 && <div className="faint" style={{ fontSize: 11.5 }}>{r.pendingReboot} restart pending</div>}</> },
          { key: 'i', label: 'Installed', render: (r) => r.installed },
          { key: 'a', label: 'Approval', render: (r) => r.approval === 'approved' ? <Pill tone="ok">Approved</Pill> : r.approval === 'declined' ? <Pill tone="crit">Declined</Pill> : <span className="faint">—</span> },
        ]} />
      </Panel>
      {open && <PatchDevicesDialog patch={open} canDeploy={canDeploy} onClose={() => setOpen(null)} onDeploy={(assets) => { setOpen(null); deploy({ patchIds: [open.id], titles: [open.title], assets }); }} />}
    </>
  );
}

function PatchDevicesDialog({ patch, canDeploy, onClose, onDeploy }: { patch: Patch; canDeploy: boolean; onClose: () => void; onDeploy: (assets: { id: string; name: string }[]) => void }) {
  const list = useApi(() => api.get<{ assetId: string; device: string | null; state: string; detectedAt: string; installedAt: string | null; message: string | null }[]>(`/patches/${encodeURIComponent(patch.id)}/devices`), [patch.id]);
  const rows = (list.data ?? []).map((d) => ({ ...d, id: d.assetId }));
  const missing = rows.filter((r) => r.state === 'missing' || r.state === 'failed');
  return (
    <Modal wide title={patch.title} onClose={onClose} footer={<>
      {canDeploy && missing.length > 0 && <button className="btn primary" onClick={() => onDeploy(missing.map((m) => ({ id: m.assetId, name: m.device ?? m.assetId })))}>Install On {missing.length} Device(s)</button>}
      <button className="btn" onClick={onClose}>Close</button>
    </>}>
      <div className="form-v">
        <div className="faint" style={{ fontSize: 12.5 }}>
          {[CLASS_LABEL[patch.classification], patch.kb ? `KB${patch.kb}` : null, patch.product, patch.size ? fmt.bytes(patch.size) : null, patch.reboot ? 'May need a restart' : null].filter(Boolean).join(' · ')}
          {patch.url && <> · <a href={patch.url} target="_blank" rel="noreferrer noopener">Details</a></>}
        </div>
        {patch.cves.length > 0 && <div style={{ fontSize: 12.5 }}>Fixes {patch.cves.join(', ')}</div>}
        <ErrorBox error={list.error} />
        <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
          { key: 'd', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}><b>{r.device ?? '—'}</b></Link> },
          { key: 's', label: 'State', render: (r) => <Pill tone={STATE[r.state]?.[0] ?? ''}>{STATE[r.state]?.[1] ?? r.state}</Pill> },
          { key: 't', label: 'Since', render: (r) => fmt.ago(r.installedAt ?? r.detectedAt) },
          { key: 'm', label: 'Detail', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.message ?? ''}</span> },
        ]} />
      </div>
    </Modal>
  );
}

// ---- devices --------------------------------------------------------------------------------------------------------

function DevicesTab({ tick, canDeploy, changed, failed, deploy, openId }: Ctx & { openId: string | null }) {
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [state, setState] = useState('');
  const [open, setOpen] = useState<string | null>(openId);
  const list = useApi(() => api.get<DeviceRow[]>('/patches/devices', { q: search, state }), [tick, search, state]);
  const rows = list.data ?? [];
  async function scan(ids: string[]) {
    try { const r = await api.post<{ requested: number; skipped: number }>('/patches/scan', { assetIds: ids }); changed(`${r.requested} device(s) scan now${r.skipped ? `; ${r.skipped} already scanning or without a capable agent` : ''}.`); }
    catch (e) { failed(e); }
  }
  const current = rows.find((r) => r.id === open);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search device…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search devices" />
        <select className="in" value={state} onChange={(e) => setState(e.target.value)} aria-label="State">
          <option value="">All devices</option><option value="urgent">Missing critical / security</option><option value="missing">Missing updates</option><option value="uptodate">Up to date</option>
          <option value="reboot">Restart pending</option><option value="failed">Failed</option><option value="notscanned">Not scanned</option>
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={rows} onRow={(r) => setOpen(r.id)} rowTitle="Show the updates" empty={list.loading ? 'Loading…' : 'No devices with the Zentriq Agent.'} columns={[
          { key: 'd', label: 'Device', render: (r) => <><b>{r.device ?? '—'}</b><div className="faint" style={{ fontSize: 11.5 }}>{r.os ?? (r.platform === 'linux' ? 'Linux' : 'Windows')}</div></> },
          { key: 's', label: 'Last Scan', className: 'wrap', render: (r) => !r.capable ? <span className="faint">Agent too old</span> : !r.scannedAt ? <span className="faint">Not yet</span>
            : r.scanOk ? <span title={r.source ?? undefined}>{fmt.ago(r.scannedAt)}<div className="faint" style={{ fontSize: 11.5 }}>{r.source}</div></span>
            : <span><Pill tone="crit">Failed</Pill><div className="faint" style={{ fontSize: 11.5, maxWidth: 340 }}>{r.scanMessage}</div></span> },
          { key: 'm', label: 'Missing', render: (r) => r.missing + r.failed === 0 ? (r.scanOk ? <Pill tone="ok">Up To Date</Pill> : <span className="faint">—</span>)
            : <><b>{r.missing + r.failed}</b>{r.urgent > 0 && <> <Pill tone="crit">{r.urgent} critical / security</Pill></>}</> },
          { key: 'f', label: 'Failed', render: (r) => r.failed > 0 ? <Pill tone="crit">{r.failed}</Pill> : <span className="faint">0</span> },
          { key: 'rb', label: 'Restart', render: (r) => r.rebootPending ? <Pill tone="info">Pending</Pill> : <span className="faint">—</span> },
          { key: 'a', label: 'Last Action', render: (r) => r.lastAction ? <Pill tone={STATUS[r.lastAction]?.[0] ?? ''}>{r.lastOperation === 'scan' ? 'Scan' : 'Install'}: {STATUS[r.lastAction]?.[1] ?? r.lastAction}</Pill> : <span className="faint">—</span> },
          { key: 'x', label: '', render: (r) => canDeploy && r.capable ? <div className="row-actions">
            <button className="btn sm" onClick={() => void scan([r.id])}>Scan Now</button>
            {r.missing + r.failed > 0 && <button className="btn sm primary" onClick={() => deploy({ allMissing: true, assets: [{ id: r.id, name: r.device ?? r.id }] })}>Install Missing</button>}
          </div> : null },
        ]} />
      </Panel>
      {open && <DeviceDialog id={open} name={current?.device ?? null} onClose={() => setOpen(null)} canDeploy={canDeploy}
        onDeploy={(ids, titles) => { const a = [{ id: open, name: current?.device ?? open }]; setOpen(null); deploy(ids ? { patchIds: ids, titles, assets: a } : { allMissing: true, assets: a }); }} />}
    </>
  );
}

function DeviceDialog({ id, name, canDeploy, onClose, onDeploy }: { id: string; name: string | null; canDeploy: boolean; onClose: () => void; onDeploy: (ids: string[] | null, titles?: string[]) => void }) {
  const d = useApi(() => api.get<DeviceDetail>(`/patches/devices/${id}`), [id]);
  const [showInstalled, setShowInstalled] = useState(false);
  const [sel, setSel] = useState<string[]>([]);
  const all = d.data?.patches ?? [];
  const rows = all.filter((p) => showInstalled || p.state !== 'installed');
  const missing = all.filter((p) => p.state === 'missing' || p.state === 'failed');
  return (
    <Modal wide title={`Updates on ${name ?? 'the device'}`} onClose={onClose} footer={<>
      {canDeploy && sel.length > 0 && <button className="btn primary" onClick={() => onDeploy(sel, all.filter((p) => sel.includes(p.id)).map((p) => p.title))}>Install {sel.length} Selected</button>}
      {canDeploy && missing.length > 0 && sel.length === 0 && <button className="btn primary" onClick={() => onDeploy(null)}>Install All Missing ({missing.length})</button>}
      <button className="btn" onClick={onClose}>Close</button>
    </>}>
      <div className="form-v">
        <ErrorBox error={d.error} />
        {d.data?.scan && (d.data.scan.ok
          ? <div className="faint" style={{ fontSize: 12.5 }}>Scanned {fmt.ago(d.data.scan.scannedAt)} from {d.data.scan.source ?? 'its update source'}{d.data.scan.rebootPending ? ' · a restart is pending' : ''}.</div>
          : <Callout tone="crit" icon="alert">The last scan failed ({fmt.ago(d.data.scan.scannedAt)}): {d.data.scan.message}</Callout>)}
        {d.data && !d.data.scan && <Callout tone="info">This device has not reported a scan yet.</Callout>}
        <label className="row-check"><input type="checkbox" checked={showInstalled} onChange={(e) => setShowInstalled(e.target.checked)} /> Show installed updates</label>
        <DataTable rows={rows} empty={d.loading ? 'Loading…' : 'No missing updates.'} columns={[
          { key: 'sel', label: '', fixed: true, render: (p) => (p.state === 'missing' || p.state === 'failed') && canDeploy
            ? <input type="checkbox" aria-label={`Select ${p.title}`} checked={sel.includes(p.id)} onChange={() => setSel(sel.includes(p.id) ? sel.filter((x) => x !== p.id) : [...sel, p.id])} /> : null },
          { key: 't', label: 'Update', className: 'wrap', render: (p) => <><b>{p.title}</b><div className="faint" style={{ fontSize: 11.5 }}>{[p.kb ? `KB${p.kb}` : null, CLASS_LABEL[p.classification], p.cves.slice(0, 3).join(', ') || null].filter(Boolean).join(' · ')}</div></> },
          { key: 'v', label: 'Severity', render: (p) => sev(p.severity) },
          { key: 's', label: 'State', render: (p) => <Pill tone={STATE[p.state]?.[0] ?? ''}>{STATE[p.state]?.[1] ?? p.state}</Pill> },
          { key: 'w', label: 'Since', render: (p) => fmt.ago(p.installedAt ?? p.detectedAt) },
          { key: 'm', label: 'Detail', className: 'wrap', render: (p) => <span style={{ fontSize: 12.5 }}>{p.message ?? (p.approval === 'declined' ? 'Declined' : '')}</span> },
        ]} />
        {(d.data?.actions ?? []).length > 0 && <>
          <b style={{ fontSize: 13 }}>Recent Actions</b>
          <DataTable rows={d.data!.actions} columns={[
            { key: 't', label: 'Requested', render: (a) => fmt.date(a.requestedAt) },
            { key: 'o', label: 'Action', render: (a) => a.operation === 'scan' ? 'Scan' : 'Install' },
            { key: 's', label: 'Result', render: (a) => <Pill tone={STATUS[a.status]?.[0] ?? ''}>{STATUS[a.status]?.[1] ?? a.status}</Pill> },
            { key: 'd', label: 'Detail', className: 'wrap', render: (a) => <span style={{ fontSize: 12.5 }}>{a.detail}</span> },
          ]} />
        </>}
      </div>
    </Modal>
  );
}

// ---- deploy -----------------------------------------------------------------------------------------------------------

export function DeployDialog({ preset, onClose, onDone }: { preset: DeployPreset; onClose: () => void; onDone: (m: string) => void }) {
  const [targets, setTargets] = useState<TargetState>(emptyTargets(preset.assets ?? []));
  const [classes, setClasses] = useState<string[]>(preset.classifications ?? Object.keys(CLASS_LABEL).filter((c) => c !== 'upgrade' && c !== 'driver'));
  const [reboot, setReboot] = useState<'when-needed' | 'never'>('when-needed');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const confirm = useConfirm();
  async function go() {
    if (targets.to === 'all' && !(await confirm({ title: 'Install on all devices?', message: 'Every device with the Zentriq Agent that misses these updates installs them now' + (reboot === 'when-needed' ? ' and restarts when they need it.' : '.'), confirmLabel: 'Install' }))) return;
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ sent: number; skipped: number; nothing: number }>('/patches/jobs', {
        ...targetsOf(targets), patchIds: preset.patchIds ?? [], allMissing: preset.allMissing === true, classifications: preset.allMissing ? classes : null, reboot, note: note || null,
      });
      onDone(`Installing on ${r.sent} device(s)${r.skipped ? `; ${r.skipped} skipped (${r.nothing} already up to date; others without a capable agent)` : ''}. Follow it in Deployments.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  const what = preset.allMissing ? 'Missing Updates' : preset.titles?.length === 1 ? preset.titles[0]! : `${preset.patchIds?.length ?? 0} Updates`;
  return (
    <Modal title={`Install ${what}`} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || targetsEmpty(targets) || (preset.allMissing === true && classes.length === 0)} onClick={() => void go()}>{busy ? 'Starting…' : 'Install'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {preset.allMissing && <div><b style={{ fontSize: 13 }}>Classifications</b>
          <div className="check-grid" style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px', marginTop: 6 }}>
            {Object.entries(CLASS_LABEL).map(([k, l]) => <label key={k} className="row-check"><input type="checkbox" checked={classes.includes(k)} onChange={() => setClasses(classes.includes(k) ? classes.filter((x) => x !== k) : [...classes, k])} /> {l}</label>)}
          </div>
          <small className="muted">Declined updates are not installed.</small>
        </div>}
        {!preset.allMissing && (preset.titles?.length ?? 0) > 1 && <div className="faint" style={{ fontSize: 12.5 }}>{preset.titles!.slice(0, 5).join(' · ')}{preset.titles!.length > 5 ? ` · +${preset.titles!.length - 5}` : ''}</div>}
        <TargetPicker value={targets} onChange={setTargets} />
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <label className="row-check"><input type="radio" checked={reboot === 'when-needed'} onChange={() => setReboot('when-needed')} /> Restart when the updates need it (with a notice)</label>
          <label className="row-check"><input type="radio" checked={reboot === 'never'} onChange={() => setReboot('never')} /> Do not restart</label>
        </div>
        <label>Note (optional)<input className="in" value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. change 4711" /></label>
        <small className="muted">Online devices start within seconds, the others when they next connect (up to 7 days). Each device installs only what it is missing and scans again afterwards.</small>
      </div>
    </Modal>
  );
}

// ---- deployments ----------------------------------------------------------------------------------------------------

function DeploymentsTab({ tick, canDeploy, changed, failed }: Ctx) {
  const list = useApi(() => api.get<Job[]>('/patches/jobs'), [tick]);
  const [open, setOpen] = useState<Job | null>(null);
  const rows = list.data ?? [];
  const active = rows.some((j) => j.waiting + j.running > 0);
  useEffect(() => {
    if (!active) return;
    const t = setInterval(list.reload, 8000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [active]);
  return (
    <>
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={rows} onRow={setOpen} rowTitle="Show the devices" empty={list.loading ? 'Loading…' : 'No deployments yet.'} columns={[
          { key: 't', label: 'Started', render: (j) => <span title={fmt.date(j.createdAt)}>{fmt.ago(j.createdAt)}</span> },
          { key: 'n', label: 'Deployment', className: 'wrap', render: (j) => <><b>{j.name}</b><div className="faint" style={{ fontSize: 11.5 }}>{j.policy ? `Automatic (${j.policy})` : j.createdBy ?? ''}{j.note ? ` · ${j.note}` : ''} · {j.reboot === 'never' ? 'no restart' : 'restart when needed'}</div></> },
          { key: 'd', label: 'Devices', render: (j) => <>{j.devices - j.skipped}{j.skipped > 0 && <span className="faint"> (+{j.skipped} skipped)</span>}<div className="faint" style={{ fontSize: 11.5 }}>{j.patches} update installation(s)</div></> },
          { key: 's', label: 'Progress', render: (j) => <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
            {j.waiting > 0 && <Pill tone="warn">{j.waiting} waiting</Pill>}{j.running > 0 && <Pill tone="warn">{j.running} installing</Pill>}
            {j.succeeded > 0 && <Pill tone="ok">{j.succeeded} done</Pill>}{j.failed > 0 && <Pill tone="crit">{j.failed} failed</Pill>}
            {j.waiting + j.running + j.succeeded + j.failed === 0 && <span className="faint">—</span>}</div> },
          { key: 'x', label: '', render: (j) => canDeploy && j.waiting > 0 ? <button className="btn sm" onClick={async () => {
            try { const r = await api.post<{ cancelled: number }>(`/patches/jobs/${j.id}/cancel`); changed(`${r.cancelled} waiting device(s) cancelled.`); } catch (e) { failed(e); }
          }}>Cancel Waiting</button> : null },
        ]} />
      </Panel>
      {open && <JobDialog job={open} onClose={() => setOpen(null)} />}
    </>
  );
}

function JobDialog({ job, onClose }: { job: Job; onClose: () => void }) {
  const list = useApi(() => api.get<JobDevice[]>(`/patches/jobs/${job.id}`), [job.id]);
  const rows = (list.data ?? []).map((d) => ({ ...d, id: d.assetId }));
  return (
    <Modal wide title={job.name} onClose={onClose} footer={<button className="btn" onClick={onClose}>Close</button>}>
      <ErrorBox error={list.error} />
      <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
        { key: 'd', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}><b>{r.device ?? '—'}</b></Link> },
        { key: 'p', label: 'Updates', render: (r) => r.patches || '—' },
        { key: 's', label: 'Status', render: (r) => <Pill tone={STATUS[r.status]?.[0] ?? ''}>{STATUS[r.status]?.[1] ?? r.status}</Pill> },
        { key: 'm', label: 'Detail', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.detail}{r.completedAt ? <span className="faint"> · {fmt.ago(r.completedAt)}</span> : null}</span> },
      ]} />
    </Modal>
  );
}

// ---- policies -------------------------------------------------------------------------------------------------------

function PoliciesTab({ tick, canApprove, changed, failed }: Ctx) {
  const list = useApi(() => api.get<Policy[]>('/patches/policies'), [tick]);
  const [edit, setEdit] = useState<Policy | 'new' | null>(null);
  const confirm = useConfirm();
  async function remove(p: Policy) {
    if (!(await confirm({ title: `Delete the policy "${p.name}"?`, danger: true, confirmLabel: 'Delete', message: 'Deployments it started stay in the history.' }))) return;
    try { await api.del(`/patches/policies/${p.id}`); changed(`Policy "${p.name}" deleted.`); } catch (e) { failed(e); }
  }
  return (
    <>
      <Panel title="Automatic Deployment" sub="In its maintenance window (server time), a policy installs the missing updates it covers on its devices — once per window"
        right={canApprove ? <button className="btn primary sm" onClick={() => setEdit('new')}><Icon name="plus" size={13} /> New Policy</button> : undefined} flush>
        <ErrorBox error={list.error} />
        <DataTable rows={list.data ?? []} empty={list.loading ? 'Loading…' : 'No policies yet.'} columns={[
          { key: 'n', label: 'Policy', className: 'wrap', render: (p) => <><b>{p.name}</b> {!p.enabled && <Pill>Off</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{p.platform === 'linux' ? 'Linux' : 'Windows'} · {describeTargets(p.targets)}</div></> },
          { key: 'c', label: 'Updates', className: 'wrap', render: (p) => <span style={{ fontSize: 12.5 }}>{p.classifications.map((c) => CLASS_LABEL[c] ?? c).join(', ')}{p.severities.length ? ` · ${p.severities.map((s) => SEVERITY[s]?.[1]).join(', ')}` : ''}{p.approvedOnly ? ' · approved only' : ''}{p.delayDays ? ` · ${p.delayDays} days after release` : ''}</span> },
          { key: 'w', label: 'Window', render: (p) => <>{p.days.length === 7 ? 'Every day' : p.days.map((d) => DAYS[d]).join(', ')} {p.startTime}<div className="faint" style={{ fontSize: 11.5 }}>{p.windowHours} h · {p.reboot === 'never' ? 'no restart' : 'restart when needed'}</div></> },
          { key: 'l', label: 'Last Run', render: (p) => p.lastRunAt ? fmt.ago(p.lastRunAt) : <span className="faint">Not yet</span> },
          { key: 'x', label: '', render: (p) => canApprove ? <div className="row-actions">
            <button className="btn sm" onClick={() => setEdit(p)}>Edit</button>
            <button className="btn sm danger" aria-label={`Delete ${p.name}`} onClick={() => void remove(p)}><Icon name="trash" size={13} /></button>
          </div> : null },
        ]} />
      </Panel>
      {edit && <PolicyDialog policy={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={(t) => { setEdit(null); changed(t); }} />}
    </>
  );
}

export function PolicyDialog({ policy, onClose, onSaved }: { policy: Policy | null; onClose: () => void; onSaved: (m: string) => void }) {
  const [f, setF] = useState({
    name: policy?.name ?? '', enabled: policy?.enabled ?? true, platform: policy?.platform ?? 'windows', classifications: policy?.classifications ?? ['critical', 'security'],
    severities: policy?.severities ?? [], approvedOnly: policy?.approvedOnly ?? false, delayDays: policy?.delayDays ?? 0, days: policy?.days ?? [3], startTime: policy?.startTime ?? '22:00',
    windowHours: policy?.windowHours ?? 4, reboot: policy?.reboot ?? 'when-needed',
  });
  const [targets, setTargets] = useState<TargetState>(stateOf(policy?.targets));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const toggle = <T,>(list: T[], v: T) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);
  async function save() {
    setBusy(true); setError(null);
    try {
      const body = { ...f, ...targetsOf(targets) };
      if (policy) await api.put(`/patches/policies/${policy.id}`, body); else await api.post('/patches/policies', body);
      onSaved(`Policy "${f.name}" saved.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={policy ? 'Edit Policy' : 'New Policy'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || !f.name.trim() || f.classifications.length === 0 || f.days.length === 0 || targetsEmpty(targets)} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="form-grid-2">
          <label>Name<input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Security updates on Wednesday" maxLength={120} /></label>
          <label>Platform<select className="in" value={f.platform} onChange={(e) => setF({ ...f, platform: e.target.value as 'windows' | 'linux' })}><option value="windows">Windows</option><option value="linux">Linux</option></select></label>
        </div>
        <div><b style={{ fontSize: 13 }}>Classifications</b>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px', marginTop: 6 }}>
            {(f.platform === 'linux' ? ['security', 'updates'] : Object.keys(CLASS_LABEL)).map((k) => <label key={k} className="row-check"><input type="checkbox" checked={f.classifications.includes(k)} onChange={() => setF({ ...f, classifications: toggle(f.classifications, k) })} /> {f.platform === 'linux' && k === 'updates' ? 'Other Package Updates' : CLASS_LABEL[k]}</label>)}
          </div>
        </div>
        <div><b style={{ fontSize: 13 }}>Severity</b> <span className="faint" style={{ fontSize: 12 }}>(none chosen: any)</span>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px', marginTop: 6 }}>
            {['critical', 'important', 'moderate', 'low'].map((k) => <label key={k} className="row-check"><input type="checkbox" checked={f.severities.includes(k)} onChange={() => setF({ ...f, severities: toggle(f.severities, k) })} /> {SEVERITY[k]![1]}</label>)}
          </div>
        </div>
        <div className="form-grid-2">
          <label className="row-check"><input type="checkbox" checked={f.approvedOnly} onChange={(e) => setF({ ...f, approvedOnly: e.target.checked })} /> Only approved updates</label>
          <label>Wait after the release (days)<input className="in" type="number" min={0} max={365} value={f.delayDays} onChange={(e) => setF({ ...f, delayDays: Number(e.target.value) })} /></label>
        </div>
        <TargetPicker value={targets} onChange={setTargets} />
        <div><b style={{ fontSize: 13 }}>Maintenance Window</b>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px', marginTop: 6 }}>
            {DAYS.map((d, i) => <label key={d} className="row-check"><input type="checkbox" checked={f.days.includes(i)} onChange={() => setF({ ...f, days: toggle(f.days, i).sort() })} /> {d}</label>)}
          </div>
        </div>
        <div className="form-grid-2">
          <label>Starts at (server time)<input className="in" type="time" value={f.startTime} onChange={(e) => setF({ ...f, startTime: e.target.value })} /></label>
          <label>Lasts (hours)<input className="in" type="number" min={1} max={24} value={f.windowHours} onChange={(e) => setF({ ...f, windowHours: Number(e.target.value) })} /></label>
        </div>
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <label className="row-check"><input type="radio" checked={f.reboot === 'when-needed'} onChange={() => setF({ ...f, reboot: 'when-needed' })} /> Restart when needed</label>
          <label className="row-check"><input type="radio" checked={f.reboot === 'never'} onChange={() => setF({ ...f, reboot: 'never' })} /> Do not restart</label>
          <label className="row-check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> On</label>
        </div>
        <small className="muted">Devices that are offline when the window opens install the updates when they next connect (up to 7 days). Declined updates are never installed by a policy.</small>
      </div>
    </Modal>
  );
}

// ---- settings -------------------------------------------------------------------------------------------------------

function SettingsTab({ settings, canApprove, changed, failed }: Ctx & { settings: Settings }) {
  const [f, setF] = useState(settings);
  const [busy, setBusy] = useState(false);
  const dirty = JSON.stringify(f) !== JSON.stringify(settings);
  async function save() {
    setBusy(true);
    try { await api.put('/patches/settings', f); changed('Patch settings saved.'); } catch (e) { failed(e); } finally { setBusy(false); }
  }
  return (
    <div className="grid-2">
      <Panel title="Scans and Restarts">
        <div className="form-v">
          <label className="row-check"><input type="checkbox" checked={f.autoScan} disabled={!canApprove} onChange={(e) => setF({ ...f, autoScan: e.target.checked })} /> Scan devices automatically</label>
          <label>Scan every (hours)<input className="in" type="number" min={1} max={168} value={f.scanHours} disabled={!canApprove || !f.autoScan} onChange={(e) => setF({ ...f, scanHours: Number(e.target.value) })} /></label>
          <label>Restart notice (minutes before the restart)<input className="in" type="number" min={1} max={240} value={f.rebootDelayMinutes} disabled={!canApprove} onChange={(e) => setF({ ...f, rebootDelayMinutes: Number(e.target.value) })} /></label>
          <label>Message users see before the restart<textarea className="in" rows={3} maxLength={500} value={f.rebootMessage} disabled={!canApprove} onChange={(e) => setF({ ...f, rebootMessage: e.target.value })} /></label>
          {canApprove && <div><button className="btn primary" disabled={!dirty || busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>}
        </div>
      </Panel>
      <Panel title="Update Sources">
        <div style={{ fontSize: 13.5, display: 'grid', gap: 8 }}>
          <div><b>Windows:</b> the device's own Windows Update configuration — Windows Update / Microsoft Update, or WSUS when a Group Policy sets a WSUS server. Zentriq does not change it.</div>
          <div><b>Linux:</b> the repositories configured on the device (apt, dnf, yum or zypper). Security updates are recognised from the -security suites (Debian, Ubuntu) and from advisories (RHEL and its rebuilds, Fedora, Amazon Linux).</div>
          <div className="faint">Requires the Zentriq Agent of build 114 or later.</div>
        </div>
      </Panel>
    </div>
  );
}
