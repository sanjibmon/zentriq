import { useState } from 'react';
import { SelfServiceTab } from './SelfServiceTab';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Column } from '../../components/ui';
import { DecideDialog, DeployDialog, GroupDialog, PackageDialog, RuleDialog } from './DeploymentDialogs';
import {
  DETAIL_SECTIONS, errText, KIND_LABEL, MODE_LABEL, MONO_DETAILS, platformLabel, type AppEvent, type AppRequest, type Catalog, type CatalogEntry, type DeviceRow, type Group,
  type InventoryDevice, type InventoryRow, type Job, type JobDevice, type Mode, type Package, type Rule, type Settings, type Summary,
} from './types';

type Tab = 'overview' | 'approved' | 'catalog' | 'inventory' | 'requests' | 'events' | 'groups' | 'packages' | 'deployments' | 'selfservice' | 'devices';
const TABS: [Tab, string][] = [
  ['overview', 'Overview'], ['approved', 'Approved Software'], ['catalog', 'Software Catalogue'], ['inventory', 'Found on Devices'], ['requests', 'Requests'],
  ['events', 'Events'], ['groups', 'Device Groups'], ['packages', 'Packages'], ['deployments', 'Deployments'], ['selfservice', 'Self-Service'], ['devices', 'Devices'],
];

export function DeploymentPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canManage = can('deploy.manage');
  const canRun = can('deploy.run');
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview';
  const kindFilter = query.get('kind') ?? '';
  const setTab = (t: Tab) => navigate(`/modules/deploy?tab=${t}`, true);
  const [tick, setTick] = useState(0);
  const summary = useApi(() => api.get<Summary>('/deployment/summary'), [tick]);
  const groups = useApi(() => api.get<Group[]>('/deployment/groups'), [tick]);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const s = summary.data;
  const ctx = { tick, canManage, canRun, groups: groups.data ?? [], changed, failed };

  return (
    <>
      <PageHead title="Software Deployment" icon="package" />
      {s && <Kpis items={[
        { label: 'Application Control', value: MODE_LABEL[s.settings.mode], sub: `${s.blocking} blocking · ${s.auditing} auditing`, to: '/modules/deploy?tab=devices' },
        { label: 'Approved Software', value: s.approvedRules.toLocaleString(), sub: `${s.blockedRules} blocked`, to: '/modules/deploy?tab=approved' },
        { label: 'Requests', value: s.pendingRequests.toLocaleString(), sub: 'Waiting for a decision', to: '/modules/deploy?tab=requests' },
        { label: 'Blocked (24 h)', value: s.blocked24h.toLocaleString(), sub: `${s.audited24h} reported in audit`, to: '/modules/deploy?tab=events&kind=blocked' },
        { label: 'Found Installed (7 d)', value: s.detected7d.toLocaleString(), sub: 'Not approved', to: '/modules/deploy?tab=events&kind=detected' },
        { label: 'Deployments', value: s.deploymentsOpen.toLocaleString(), sub: `${s.deploymentsFailed7d} failed (7 d)`, to: '/modules/deploy?tab=deployments' },
      ]} />}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{l}
            {t === 'requests' && s && s.pendingRequests > 0 && <span className="cnt">{s.pendingRequests}</span>}
          </button>
        ))}
      </div>
      <ErrorBox error={summary.error} />
      {tab === 'overview' && s && <OverviewTab summary={s} {...ctx} onTab={setTab} />}
      {tab === 'approved' && <RulesTab {...ctx} />}
      {tab === 'catalog' && <CatalogTab {...ctx} />}
      {tab === 'inventory' && <InventoryTab {...ctx} />}
      {tab === 'requests' && <RequestsTab {...ctx} />}
      {tab === 'events' && <EventsTab key={kindFilter} {...ctx} initialKind={kindFilter} />}
      {tab === 'groups' && <GroupsTab {...ctx} reloadGroups={groups.reload} />}
      {tab === 'packages' && <PackagesTab {...ctx} />}
      {tab === 'deployments' && <DeploymentsTab {...ctx} />}
      {tab === 'selfservice' && <SelfServiceTab {...ctx} />}
      {tab === 'devices' && <DevicesTab {...ctx} />}
    </>
  );
}

interface Ctx { tick: number; canManage: boolean; canRun: boolean; groups: Group[]; changed: (t: string) => void; failed: (e: unknown) => void }

// ---- overview and settings ------------------------------------------------------------------------------------

function OverviewTab({ summary, canManage, changed, failed, onTab }: Ctx & { summary: Summary; onTab: (t: Tab) => void }) {
  const [f, setF] = useState<Settings>(summary.settings);
  const [busy, setBusy] = useState(false);
  const confirm = useConfirm();
  const dirty = JSON.stringify(f) !== JSON.stringify(summary.settings);
  async function save() {
    if (f.mode === 'block' && summary.settings.mode !== 'block') {
      const ok = await confirm({
        title: 'Block software that is not approved?',
        message: <>Installations of software that is not on the approved list are stopped on every device with the Zentriq Agent (device groups can set their own mode), and users see your message. Approve what is already in use first — <b>Found on devices</b> helps — and run in audit mode for a while.</>,
        confirmLabel: 'Turn on blocking',
      });
      if (!ok) return;
    }
    setBusy(true);
    try { await api.put('/deployment/settings', f); changed('Application control settings saved; agents receive them within seconds.'); }
    catch (e) { failed(e); } finally { setBusy(false); }
  }
  const steps: [string, string, Tab][] = [
    ['Approve Software', 'From the catalogue or from what is installed.', 'catalog'],
    ['Audit', 'Report, do not stop. Approve what you see.', 'events'],
    ['Block', 'For a device group first, then for everyone.', 'groups'],
  ];
  return (
    <div className="grid-2">
      <Panel title="Application Control" sub="All devices with the Zentriq Agent; device groups can override">
        <div className="form-v">
          <label>Mode
            <select className="in" value={f.mode} disabled={!canManage} onChange={(e) => setF({ ...f, mode: e.target.value as Mode })}>
              <option value="off">Off — not checked</option>
              <option value="audit">Audit — report software that is not approved</option>
              <option value="block">Block — stop it and show the message</option>
            </select>
          </label>
          <label>Message users see
            <textarea className="in" rows={3} value={f.message} disabled={!canManage} onChange={(e) => setF({ ...f, message: e.target.value })} maxLength={500} />
            {f.message !== summary.settings.defaultMessage && canManage && <small><button type="button" className="link-btn" onClick={() => setF({ ...f, message: summary.settings.defaultMessage })}>Use the default message</button></small>}
          </label>
          <label className="row-check"><input type="checkbox" checked={f.allowRequests} disabled={!canManage} onChange={(e) => setF({ ...f, allowRequests: e.target.checked })} /> Users can request approval</label>
          <label className="row-check" title="Block mode only. Software installed before application control was turned on is never removed."><input type="checkbox" checked={f.autoRemove} disabled={!canManage} onChange={(e) => setF({ ...f, autoRemove: e.target.checked })} />
            Remove unapproved software installed anyway</label>
          <label className="row-check" title="Stopping, disabling or removing it on the device needs a code; it is reinstalled if removed."><input type="checkbox" checked={f.protectRemoteAgent} disabled={!canManage} onChange={(e) => setF({ ...f, protectRemoteAgent: e.target.checked })} />
            Tamper-protect the remote support agent</label>
          {canManage && <div><button className="btn primary" disabled={!dirty || busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>}
          {summary.settings.enabledAt && <small className="faint">On since {fmt.date(summary.settings.enabledAt)} · revision {summary.settings.revision}</small>}
        </div>
      </Panel>
      <Panel title="Rolling It Out">
        <ol className="mini-steps">
          {steps.map(([t, d, to], i) => <li key={t}><span className="n">{i + 1}</span><div><b>{t}</b><span className="muted" style={{ fontSize: 12.5 }}>{d}</span><div><button className="link-btn" onClick={() => onTab(to)}>Open</button></div></div></li>)}
        </ol>
        <p className="faint" style={{ fontSize: 12.5, margin: '12px 0 0' }} title="Installations by services (Windows Update, Configuration Manager, Intune, Zentriq deployments) are not stopped; the inventory check reports anything installed that is not approved.">
          Always allowed: Windows components and updates, drivers, Microsoft runtimes, Zentriq; on Linux, package updates, kernels and libraries.
        </p>
        <div className="stat-line">
          <span><b>{summary.capableAgents}</b> of {summary.agents} agents ready{summary.capableAgents < summary.agents && <> · <Link to="/agents">Update the Others</Link></>}</span>
          <span>WDAC <b>{summary.wdac}</b></span>
          <span>Remote agent protected <b>{summary.remoteProtected}</b></span>
          <span>Catalogue <b>{summary.catalog.entries}</b></span>
        </div>
      </Panel>
    </div>
  );
}

// ---- approved and blocked software -------------------------------------------------------------------------------

function RulesTab({ tick, canManage, groups, changed, failed }: Ctx) {
  const [q, setQ] = useSticky('DeploymentPage.RulesTab.q', '');
  const [search, setSearch] = useSticky('DeploymentPage.RulesTab.search', '');
  const [action, setAction] = useState('');
  const [groupId, setGroupId] = useState('');
  const [local, setLocal] = useState(0);
  const rules = useApi(() => api.get<Rule[]>('/deployment/rules', { q: search, action, groupId }), [search, action, groupId, tick, local]);
  const [editing, setEditing] = useState<Rule | 'new' | null>(null);
  const [test, setTest] = useState(false);
  const confirm = useConfirm();
  async function remove(r: Rule) {
    if (!(await confirm({ title: `Remove “${r.label}”?`, message: r.action === 'allow' ? 'Installing it is then checked like any other software.' : 'It is no longer blocked by this rule.', confirmLabel: 'Remove', danger: true }))) return;
    try { await api.del(`/deployment/rules/${r.id}`); setLocal((x) => x + 1); changed(`“${r.label}” removed.`); } catch (e) { failed(e); }
  }
  const cols: Column<Rule>[] = [
    { key: 'a', label: '', render: (r) => <Pill tone={r.action === 'allow' ? 'ok' : 'crit'}>{r.action === 'allow' ? 'Approved' : 'Blocked'}</Pill> },
    { key: 'l', label: 'Software', className: 'wrap', render: (r) => <><b>{r.label}</b>{r.note && <div className="faint" style={{ fontSize: 11.5 }}>{r.note}</div>}</> },
    { key: 'm', label: 'Matches', className: 'wrap', render: (r) => <span style={{ fontSize: 12 }}>
      {r.name && <div>Name: <span className="mono">{r.name}</span></div>}
      {r.publisher && <div>Publisher: <span className="mono">{r.publisher}</span></div>}
      {(r.versionMin || r.versionMax) && <div>Version: {r.versionMin ?? 'any'} – {r.versionMax ?? 'any'}</div>}
      {r.sha256 && <div>SHA-256: <span className="mono">{r.sha256.slice(0, 16)}…</span></div>}
    </span> },
    { key: 'p', label: 'Platform', render: (r) => platformLabel(r.platform) },
    { key: 's', label: 'Applies To', render: (r) => r.device ? <>Device {r.device}</> : r.group ? <>Group {r.group}</> : 'All devices' },
    { key: 'src', label: 'Added', render: (r) => <span title={`${fmt.date(r.createdAt)}${r.createdBy ? ' by ' + r.createdBy : ''}`}>{fmt.label(r.source)} · {fmt.ago(r.createdAt)}</span> },
    { key: 'x', label: '', render: (r) => canManage ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => setEditing(r)}>Edit</button>
      <button className="btn sm" aria-label={`Remove ${r.label}`} onClick={() => void remove(r)}><Icon name="trash" size={13} /></button>
    </div> : null },
  ];
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search software, publisher or SHA-256…" value={q} onChange={(e) => { setQ(e.target.value); if (!e.target.value.trim()) { setSearch(''); } }} aria-label="Search approved software" />
        <select className="in" value={action} onChange={(e) => setAction(e.target.value)} aria-label="Approved or blocked">
          <option value="">Approved and blocked</option><option value="allow">Approved</option><option value="block">Blocked</option>
        </select>
        <select className="in" value={groupId} onChange={(e) => setGroupId(e.target.value)} aria-label="Device group">
          <option value="">All groups</option>{groups.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
        <button className="btn" type="button" onClick={() => setTest(true)}>Test software…</button>
        {canManage && <button className="btn primary" type="button" onClick={() => setEditing('new')}><Icon name="plus" size={13} /> Add</button>}
      </form>
      <ErrorBox error={rules.error} />
      <DataTable columns={cols} rows={rules.data ?? []} empty={rules.loading ? 'Loading…' : 'No approved software yet. Approve software from the catalogue or from what is found on devices.'} />
      {editing && <RuleDialog rule={editing === 'new' ? null : editing} groups={groups} onClose={() => setEditing(null)} onSaved={(m) => { setEditing(null); setLocal((x) => x + 1); changed(m); }} />}
      {test && <TestDialog onClose={() => setTest(false)} />}
    </>
  );
}

function TestDialog({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ name: '', publisher: '', version: '', sha256: '', platform: 'windows' });
  const [result, setResult] = useState<{ approved: boolean; reason: string; effect: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function run() {
    setError(null);
    try { setResult(await api.post('/deployment/rules/test', f)); } catch (e) { setError(errText(e)); }
  }
  return (
    <Modal title="Would it be allowed?" onClose={onClose} footer={<><button className="btn" onClick={onClose}>Close</button><button className="btn primary" disabled={!f.name.trim()} onClick={() => void run()}>Test</button></>}>
        <div className="form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="grid-2 tight">
            <label>Software name<input className="in" value={f.name} autoFocus onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Google Chrome" /></label>
            <label>Publisher or signer<input className="in" value={f.publisher} onChange={(e) => setF({ ...f, publisher: e.target.value })} placeholder="e.g. Google LLC" /></label>
            <label>Version<input className="in" value={f.version} onChange={(e) => setF({ ...f, version: e.target.value })} /></label>
            <label>Platform<select className="in" value={f.platform} onChange={(e) => setF({ ...f, platform: e.target.value })}><option value="windows">Windows</option><option value="linux">Linux</option></select></label>
          </div>
          {result && <Callout tone={result.approved ? 'ok' : 'warn'} icon={result.approved ? 'shield' : 'alert'}><b>{result.approved ? 'Allowed' : 'Not approved'}</b> — {result.reason} On devices of the organisation: {result.effect}.</Callout>}
        </div>
    </Modal>
  );
}

// ---- standard catalogue --------------------------------------------------------------------------------------

function CatalogTab({ tick, canManage, groups, changed, failed }: Ctx) {
  const [q, setQ] = useSticky('DeploymentPage.CatalogTab.q', '');
  const [category, setCategory] = useSticky('DeploymentPage.CatalogTab.category', '');
  const [platform, setPlatform] = useSticky('DeploymentPage.CatalogTab.platform', '');
  const [local, setLocal] = useState(0);
  const cat = useApi(() => api.get<Catalog>('/deployment/catalog'), [tick, local]);
  const [sel, setSel] = useState<string[]>([]);
  const [groupId, setGroupId] = useState('');
  const [adding, setAdding] = useState(false);
  const entries = (cat.data?.entries ?? []).filter((e) =>
    (!category || e.category === category) && (!platform || e.platforms.includes(platform))
    && (!q.trim() || `${e.name} ${e.publisher ?? ''} ${e.category}`.toLowerCase().includes(q.trim().toLowerCase())));
  async function decide(action: 'allow' | 'block') {
    try {
      const r = await api.post<{ created: number }>('/deployment/catalog/approve', { keys: sel, groupId: groupId || null, action });
      setSel([]); setLocal((x) => x + 1);
      changed(`${r.created} rule(s) added: ${action === 'allow' ? 'approved' : 'blocked'} for ${groupId ? groups.find((g) => g.id === groupId)?.name : 'all devices'}.`);
    } catch (e) { failed(e); }
  }
  async function removeOwn(e: CatalogEntry) {
    try { await api.del(`/deployment/catalog/${e.id}`); setLocal((x) => x + 1); changed(`${e.name} removed from your catalogue.`); } catch (x) { failed(x); }
  }
  const all = entries.length > 0 && entries.every((e) => sel.includes(e.key));
  const cols: Column<CatalogEntry & { id: string }>[] = [
    { key: 'c', label: canManage ? <input type="checkbox" aria-label="Select all" checked={all} onChange={(ev) => setSel(ev.target.checked ? [...new Set([...sel, ...entries.map((e) => e.key)])] : sel.filter((k) => !entries.some((e) => e.key === k)))} /> : '', fixed: true,
      render: (e) => canManage ? <input type="checkbox" aria-label={`Select ${e.name}`} checked={sel.includes(e.key)} onChange={(ev) => setSel(ev.target.checked ? [...sel, e.key] : sel.filter((k) => k !== e.key))} /> : null },
    { key: 'n', label: 'Software', className: 'wrap', render: (e) => <><b>{e.name}</b>{!e.builtIn && <> <Pill tone="info">Your entry</Pill></>}<div className="faint" style={{ fontSize: 11.5 }}>{e.publisher ?? '—'}</div></> },
    { key: 'cat', label: 'Category', render: (e) => e.category },
    { key: 'p', label: 'Platforms', render: (e) => e.platforms.map((p) => platformLabel(p)).join(', ') },
    { key: 'd', label: 'On Devices', render: (e) => e.devices ? e.devices.toLocaleString() : <span className="muted">—</span> },
    { key: 's', label: 'Status', render: (e) => e.blocked ? <Pill tone="crit">Blocked</Pill> : e.approved ? <Pill tone="ok">Approved</Pill> : <span className="muted">Not approved</span> },
    { key: 'i', label: 'Silent Install', optional: true, className: 'wrap', render: (e) => <span className="mono" style={{ fontSize: 11.5 }}>{[e.windowsSilentArgs, e.debPackage && `apt: ${e.debPackage}`, e.rpmPackage && `dnf: ${e.rpmPackage}`].filter(Boolean).join(' · ') || '—'}</span> },
    { key: 'w', label: '', render: (e) => <span style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      {e.website && <a className="btn sm" href={e.website} target="_blank" rel="noreferrer">Website</a>}
      {!e.builtIn && canManage && <button className="btn sm" aria-label={`Remove ${e.name}`} onClick={() => void removeOwn(e)}><Icon name="trash" size={13} /></button>}
    </span> },
  ];
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search the catalogue…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search the catalogue" />
        <select className="in" value={category} onChange={(e) => setCategory(e.target.value)} aria-label="Category"><option value="">All categories</option>{(cat.data?.categories ?? []).map((c) => <option key={c}>{c}</option>)}</select>
        <select className="in" value={platform} onChange={(e) => setPlatform(e.target.value)} aria-label="Platform"><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select>
        <span className="hint">{entries.length} of {cat.data?.entries.length ?? '…'}</span>
        {canManage && <button className="btn" onClick={() => setAdding(true)}><Icon name="plus" size={13} /> Add Your Own</button>}
      </div>
      {canManage && sel.length > 0 && <div className="toolbar-row">
        <b>{sel.length} selected</b>
        <select className="in" value={groupId} onChange={(e) => setGroupId(e.target.value)} aria-label="For"><option value="">for all devices</option>{groups.map((g) => <option key={g.id} value={g.id}>for group {g.name}</option>)}</select>
        <button className="btn primary" onClick={() => void decide('allow')}>Approve</button>
        <button className="btn" onClick={() => void decide('block')}>Block</button>
        <button className="btn" onClick={() => setSel([])}>Clear</button>
      </div>}
      <ErrorBox error={cat.error} />
      <DataTable columns={cols} rows={entries.map((e) => ({ ...e, id: e.key }))} empty={cat.loading ? 'Loading…' : 'Nothing matches.'} />
      {adding && <OwnEntryDialog onClose={() => setAdding(false)} onSaved={(m) => { setAdding(false); setLocal((x) => x + 1); changed(m); }} categories={cat.data?.categories ?? []} />}
    </>
  );
}

function OwnEntryDialog({ onClose, onSaved, categories }: { onClose: () => void; onSaved: (m: string) => void; categories: string[] }) {
  const [f, setF] = useState({ name: '', publisher: '', category: 'Line-of-business', windows: true, linux: false, namePatterns: '', publisherPatterns: '', website: '', notes: '' });
  const [error, setError] = useState<string | null>(null);
  async function save() {
    try {
      await api.post('/deployment/catalog', {
        name: f.name, publisher: f.publisher || null, category: f.category, platforms: [f.windows && 'windows', f.linux && 'linux'].filter(Boolean),
        namePatterns: f.namePatterns.split(/[;\n]/).map((x) => x.trim()).filter(Boolean), publisherPatterns: f.publisherPatterns.split(/[;\n]/).map((x) => x.trim()).filter(Boolean),
        website: f.website || null, notes: f.notes || null,
      });
      onSaved(`${f.name} added to your catalogue. Select it and approve it.`);
    } catch (e) { setError(errText(e)); }
  }
  return (
    <Modal title="Your own software" onClose={onClose} footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.name.trim() || (!f.windows && !f.linux)} onClick={() => void save()}>Add</button></>}>
        <div className="form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="grid-2 tight">
            <label>Name<input className="in" value={f.name} autoFocus onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Contoso Payroll" /></label>
            <label>Publisher<input className="in" value={f.publisher} onChange={(e) => setF({ ...f, publisher: e.target.value })} /></label>
            <label>Category<input className="in" list="dep-cats" value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })} /><datalist id="dep-cats">{categories.map((c) => <option key={c} value={c} />)}</datalist></label>
            <label>Website<input className="in" value={f.website} onChange={(e) => setF({ ...f, website: e.target.value })} /></label>
          </div>
          <div style={{ display: 'flex', gap: 16 }}>
            <label className="row-check"><input type="checkbox" checked={f.windows} onChange={(e) => setF({ ...f, windows: e.target.checked })} /> Windows</label>
            <label className="row-check"><input type="checkbox" checked={f.linux} onChange={(e) => setF({ ...f, linux: e.target.checked })} /> Linux</label>
          </div>
          <label>Names it installs under (one per line or separated by ;, wildcards * and ?)<textarea className="in" rows={2} value={f.namePatterns} onChange={(e) => setF({ ...f, namePatterns: e.target.value })} placeholder={'Contoso Payroll*\ncontoso-payroll'} /></label>
          <label>Publishers or code-signing certificates<textarea className="in" rows={2} value={f.publisherPatterns} onChange={(e) => setF({ ...f, publisherPatterns: e.target.value })} placeholder="Contoso Ltd" /></label>
        </div>
    </Modal>
  );
}

// ---- software found on devices -------------------------------------------------------------------------------

function InventoryTab({ tick, canManage, groups, changed, failed }: Ctx) {
  const [q, setQ] = useSticky('DeploymentPage.InventoryTab.q', '');
  const [search, setSearch] = useSticky('DeploymentPage.InventoryTab.search', '');
  const [status, setStatus] = useSticky('DeploymentPage.InventoryTab.status', 'unapproved');
  const [platform, setPlatform] = useSticky('DeploymentPage.InventoryTab.platform', '');
  const [local, setLocal] = useState(0);
  const rows = useApi(() => api.get<InventoryRow[]>('/deployment/inventory', { q: search, status, platform }), [search, status, platform, tick, local]);
  const [sel, setSel] = useState<string[]>([]);
  const [groupId, setGroupId] = useState('');
  const [withPublisher, setWithPublisher] = useState(true);
  const [open, setOpen] = useState<InventoryRow | null>(null);
  const list = (rows.data ?? []).map((r) => ({ ...r, id: `${r.platform}|${r.name}|${r.publisher ?? ''}` }));
  async function approve() {
    const items = list.filter((r) => sel.includes(r.id)).map((r) => ({ name: r.name, publisher: r.publisher, platform: r.platform }));
    try {
      const r = await api.post<{ created: number }>('/deployment/rules/from-inventory', { items, groupId: groupId || null, withPublisher });
      setSel([]); setLocal((x) => x + 1); changed(`${r.created} item(s) approved.`);
    } catch (e) { failed(e); }
  }
  const allSel = list.length > 0 && list.every((r) => sel.includes(r.id));
  const cols: Column<InventoryRow & { id: string }>[] = [
    { key: 'c', label: canManage ? <input type="checkbox" aria-label="Select all" checked={allSel} onChange={(e) => setSel(e.target.checked ? list.filter((r) => !r.approved).map((r) => r.id) : [])} /> : '', fixed: true,
      render: (r) => canManage && !r.approved ? <input type="checkbox" aria-label={`Select ${r.name}`} checked={sel.includes(r.id)} onChange={(e) => setSel(e.target.checked ? [...sel, r.id] : sel.filter((x) => x !== r.id))} /> : null },
    { key: 'n', label: 'Software', className: 'wrap', render: (r) => <><b>{r.name}</b><div className="faint" style={{ fontSize: 11.5 }}>{r.publisher ?? '—'}</div></> },
    { key: 'p', label: 'Platform', render: (r) => platformLabel(r.platform) },
    { key: 'd', label: 'Devices', render: (r) => <button className="link-btn" onClick={() => setOpen(r)}>{r.devices.toLocaleString()}</button> },
    { key: 'v', label: 'Versions', className: 'wrap', render: (r) => <span style={{ fontSize: 12 }}>{r.versions.join(', ') || '—'}</span> },
    { key: 's', label: 'Status', className: 'wrap', render: (r) => r.approved ? <Pill tone="ok">{r.outcome === 'built-in' ? 'Always allowed' : 'Approved'}</Pill> : <span title={r.reason}><Pill tone={r.outcome === 'blocked' ? 'crit' : 'warn'}>{r.outcome === 'blocked' ? 'Blocked' : 'Not approved'}</Pill></span> },
  ];
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search software or publisher…" value={q} onChange={(e) => { setQ(e.target.value); if (!e.target.value.trim()) { setSearch(''); } }} aria-label="Search software" />
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status"><option value="unapproved">Not approved</option><option value="approved">Approved</option><option value="">All</option></select>
        <select className="in" value={platform} onChange={(e) => setPlatform(e.target.value)} aria-label="Platform"><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select>
        <button className="btn" type="submit">Search</button>
      </form>
      {canManage && sel.length > 0 && <div className="toolbar-row">
        <b>{sel.length} selected</b>
        <select className="in" value={groupId} onChange={(e) => setGroupId(e.target.value)} aria-label="For"><option value="">for all devices</option>{groups.map((g) => <option key={g.id} value={g.id}>for group {g.name}</option>)}</select>
        <label className="row-check"><input type="checkbox" checked={withPublisher} onChange={(e) => setWithPublisher(e.target.checked)} /> Only from the same publisher (Windows)</label>
        <button className="btn primary" onClick={() => void approve()}>Approve</button>
      </div>}
      <ErrorBox error={rows.error} />
      <DataTable columns={cols} rows={list} onRow={setOpen} rowTitle="Show the devices"
        empty={rows.loading ? 'Loading…' : status === 'unapproved' ? 'All software found on devices is approved.' : 'No software found.'} />
      {open && <InventoryDevicesDialog item={open} canManage={canManage} groups={groups} onClose={() => setOpen(null)}
        onChanged={(m) => { setLocal((x) => x + 1); changed(m); }} failed={failed} />}
    </>
  );
}

/** The devices a piece of software was found on; approve it for one device, a group or all devices. */
function InventoryDevicesDialog({ item, canManage, groups, onClose, onChanged, failed }: {
  item: InventoryRow; canManage: boolean; groups: Group[]; onClose: () => void; onChanged: (m: string) => void; failed: (e: unknown) => void;
}) {
  const [local, setLocal] = useState(0);
  const devices = useApi(() => api.get<InventoryDevice[]>('/deployment/inventory/devices', { name: item.name, publisher: item.publisher ?? '', platform: item.platform }), [item.name, item.publisher, item.platform, local]);
  const [withPublisher, setWithPublisher] = useState(item.platform === 'windows' && !!item.publisher);
  const [groupId, setGroupId] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [q, setQ] = useState('');
  async function approve(assetId: string | null, label: string) {
    setBusy(assetId ?? 'all');
    try {
      const r = await api.post<{ created: number }>('/deployment/rules/from-inventory', {
        items: [{ name: item.name, publisher: item.publisher, platform: item.platform }], withPublisher, assetId, groupId: assetId ? null : groupId || null,
      });
      setLocal((x) => x + 1);
      onChanged(r.created ? `${item.name} approved for ${label}.` : `${item.name} was already approved for ${label}.`);
    } catch (e) { failed(e); } finally { setBusy(null); }
  }
  const rows = (devices.data ?? []).filter((d) => !q.trim() || `${d.hostname} ${d.department} ${d.location} ${d.owner} ${d.ip}`.toLowerCase().includes(q.trim().toLowerCase()))
    .map((d) => ({ ...d, id: d.assetId }));
  const pending = rows.filter((d) => !d.approved).length;
  return (
    <Modal title={item.name} wide onClose={onClose} footer={<button className="btn" onClick={onClose}>Close</button>}>
      <div className="form-v">
        <div className="stat-line" style={{ marginTop: 0 }}>
          <span>{item.publisher ?? 'Unknown publisher'}</span><span>{platformLabel(item.platform)}</span>
          <span><b>{devices.data?.length ?? item.devices}</b> device(s)</span><span><b>{pending}</b> not approved</span>
          {item.versions.length > 0 && <span>Versions {item.versions.join(', ')}</span>}
        </div>
        {canManage && <div className="toolbar-row">
          <select className="in" value={groupId} onChange={(e) => setGroupId(e.target.value)} aria-label="Approve for"><option value="">All devices</option>{groups.map((g) => <option key={g.id} value={g.id}>Group {g.name}</option>)}</select>
          <button className="btn primary" disabled={busy !== null} onClick={() => void approve(null, groupId ? `the group ${groups.find((g) => g.id === groupId)?.name}` : 'all devices')}>
            {busy === 'all' ? 'Approving…' : groupId ? 'Approve for the Group' : 'Approve for All Devices'}</button>
          {item.platform === 'windows' && item.publisher && <label className="row-check"><input type="checkbox" checked={withPublisher} onChange={(e) => setWithPublisher(e.target.checked)} /> Only from {item.publisher}</label>}
          <input className="in grow" placeholder="Filter devices…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Filter devices" />
        </div>}
        <ErrorBox error={devices.error} />
        <DataTable rows={rows} empty={devices.loading ? 'Loading…' : 'No devices.'} columns={[
          { key: 'h', label: 'Device', className: 'wrap', render: (d) => <><Link to={`/assets/${d.assetId}`}>{d.hostname ?? d.assetId}</Link><div className="faint">{[d.osName, d.ip].filter(Boolean).join(' · ')}</div></> },
          { key: 'g', label: 'Department / Location', className: 'wrap', render: (d) => [d.department, d.location].filter(Boolean).join(' · ') || <span className="faint">—</span> },
          { key: 'u', label: 'User', render: (d) => d.owner ?? <span className="faint">—</span> },
          { key: 'v', label: 'Version', render: (d) => d.version ?? '—' },
          { key: 'o', label: 'Agent', render: (d) => !d.agent ? <span className="faint">No agent</span> : d.online ? <Pill tone="ok">Online</Pill> : <span className="faint" title={fmt.date(d.lastCheckin)}>{fmt.ago(d.lastCheckin)}</span> },
          { key: 's', label: 'Status', render: (d) => d.approved ? <Pill tone="ok">{d.outcome === 'built-in' ? 'Always Allowed' : 'Approved'}</Pill> : <span title={d.reason}><Pill tone={d.outcome === 'blocked' ? 'crit' : 'warn'}>{d.outcome === 'blocked' ? 'Blocked' : 'Not Approved'}</Pill></span> },
          { key: 'x', label: '', render: (d) => canManage && !d.approved && d.outcome !== 'blocked'
            ? <button className="btn sm" disabled={busy !== null} onClick={() => void approve(d.assetId, d.hostname ?? 'this device')}>{busy === d.assetId ? 'Approving…' : 'Approve on This Device'}</button> : null },
        ]} />
      </div>
    </Modal>
  );
}

// ---- requests and events -------------------------------------------------------------------------------------

function RequestsTab({ tick, canManage, groups, changed, failed }: Ctx) {
  const [status, setStatus] = useSticky('DeploymentPage.RequestsTab.status', 'pending');
  const [local, setLocal] = useState(0);
  const reqs = useApi(() => api.get<AppRequest[]>('/deployment/requests', { status }), [status, tick, local]);
  const [deciding, setDeciding] = useState<AppRequest | null>(null);
  const [showing, setShowing] = useState<AppRequest | null>(null);
  const confirm = useConfirm();
  async function reject(r: AppRequest) {
    const note = await confirm({ title: `Reject ${r.name}?`, message: 'The software stays blocked on the device.', confirmLabel: 'Reject', input: { label: 'Note (optional)', type: 'text' } });
    if (note === null) return;
    try { await api.post(`/deployment/requests/${r.id}/reject`, { note }); setLocal((x) => x + 1); changed(`Request for ${r.name} rejected.`); } catch (e) { failed(e); }
  }
  const cols: Column<AppRequest>[] = [
    { key: 't', label: 'Asked', render: (r) => <span title={fmt.date(r.createdAt)}>{fmt.ago(r.createdAt)}</span> },
    { key: 'n', label: 'Software', className: 'wrap', render: (r) => <><b>{r.name}</b>{r.version && ` ${r.version}`}<div className="faint" style={{ fontSize: 11.5 }}>{r.publisher ?? '—'}{r.path && <> · <span className="mono">{r.path}</span></>}</div></> },
    { key: 'u', label: 'By', render: (r) => <>{r.user ?? '—'}<div className="faint" style={{ fontSize: 11.5 }}>{r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.device}</Link> : r.device}</div></> },
    { key: 'r', label: 'Reason', className: 'wrap', render: (r) => r.reason ?? <span className="muted">—</span> },
    { key: 's', label: 'Status', className: 'wrap', render: (r) => r.status === 'pending' ? <Pill tone="warn">Waiting</Pill> : <><Pill tone={r.status === 'approved' ? 'ok' : 'crit'}>{r.status === 'approved' ? 'Approved' : 'Rejected'}</Pill>
      <div className="faint" style={{ fontSize: 11.5 }}>{r.decidedBy} · {fmt.ago(r.decidedAt)}{r.decisionNote && ` · ${r.decisionNote}`}</div></> },
    { key: 'x', label: '', render: (r) => <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => setShowing(r)}>Details</button>
      {canManage && r.status === 'pending' && <><button className="btn sm primary" onClick={() => setDeciding(r)}>Approve…</button>
      <button className="btn sm" onClick={() => void reject(r)}>Reject</button></>}
    </div> },
  ];
  return (
    <>
      <div className="toolbar-row">
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status"><option value="pending">Waiting for a decision</option><option value="approved">Approved</option><option value="rejected">Rejected</option><option value="">All</option></select>
      </div>
      <ErrorBox error={reqs.error} />
      <DataTable columns={cols} rows={reqs.data ?? []} onRow={setShowing} rowTitle="Show the details" empty={reqs.loading ? 'Loading…' : status === 'pending' ? 'No requests waiting.' : 'No requests.'} />
      {showing && <RequestDetailsDialog r={showing} canManage={canManage} onClose={() => setShowing(null)}
        onApprove={() => { setDeciding(showing); setShowing(null); }} onReject={() => { const r = showing; setShowing(null); void reject(r); }} />}
      {deciding && <DecideDialog title={`Approve ${deciding.name}`} item={deciding} groups={groups} endpoint={`/deployment/requests/${deciding.id}/approve`}
        onClose={() => setDeciding(null)} onDone={(m) => { setDeciding(null); setLocal((x) => x + 1); changed(m); }} />}
    </>
  );
}

function EventsTab({ tick, canManage, groups, changed, initialKind }: Ctx & { initialKind: string }) {
  const [kind, setKind] = useSticky('DeploymentPage.EventsTab.kind', initialKind);
  const [showing, setShowing] = useState<AppEvent | null>(null);
  const [q, setQ] = useSticky('DeploymentPage.EventsTab.q', '');
  const [search, setSearch] = useSticky('DeploymentPage.EventsTab.search', '');
  const [days, setDays] = useSticky('DeploymentPage.EventsTab.days', 7);
  const events = useApi(() => api.get<AppEvent[]>('/deployment/events', { kind, q: search, days }), [kind, search, days, tick]);
  const [approving, setApproving] = useState<AppEvent | null>(null);
  const tone = (k: string) => (k === 'blocked' || k === 'remove-failed' ? 'crit' : k === 'audited' || k === 'detected' ? 'warn' : k === 'removed' ? 'ok' : 'info');
  const cols: Column<AppEvent>[] = [
    { key: 't', label: 'When', render: (e) => <span title={fmt.date(e.at)}>{fmt.ago(e.at)}</span> },
    { key: 'k', label: 'What', render: (e) => <Pill tone={tone(e.kind)}>{KIND_LABEL[e.kind] ?? e.kind}</Pill> },
    { key: 'n', label: 'Software', className: 'wrap', render: (e) => <><b>{e.name}</b>{e.version && ` ${e.version}`}<div className="faint" style={{ fontSize: 11.5 }}>{e.publisher ?? '—'}</div></> },
    { key: 'd', label: 'Device', render: (e) => <>{e.assetId ? <Link to={`/assets/${e.assetId}`}>{e.device}</Link> : e.device ?? '—'}<div className="faint" style={{ fontSize: 11.5 }}>{e.user ?? ''}</div></> },
    { key: 'x', label: 'Detail', className: 'wrap', render: (e) => <span style={{ fontSize: 12.5 }}>{e.detail}</span> },
    { key: 'a', label: '', render: (e) => <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => setShowing(e)}>Details</button>
      {canManage && ['blocked', 'audited', 'detected'].includes(e.kind) && <button className="btn sm" onClick={() => setApproving(e)}>Approve…</button>}
    </div> },
  ];
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search software, device, user or file…" value={q} onChange={(e) => { setQ(e.target.value); if (!e.target.value.trim()) { setSearch(''); } }} aria-label="Search events" />
        <select className="in" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind"><option value="">All events</option>{Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
        <select className="in" value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period"><option value={1}>24 hours</option><option value={7}>7 days</option><option value={30}>30 days</option><option value={90}>90 days</option></select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={events.error} />
      <DataTable columns={cols} rows={events.data ?? []} onRow={setShowing} rowTitle="Show the details" empty={events.loading ? 'Loading…' : 'Nothing in this period.'} />
      {showing && <Modal title={`${KIND_LABEL[showing.kind] ?? showing.kind}: ${showing.name}`} wide onClose={() => setShowing(null)}
        footer={<>{canManage && ['blocked', 'audited', 'detected'].includes(showing.kind) && <button className="btn primary" onClick={() => { setApproving(showing); setShowing(null); }}>Approve…</button>}
          <button className="btn" onClick={() => setShowing(null)}>Close</button></>}>
        <dl className="kv">
          <dt>When</dt><dd>{fmt.date(showing.at)}</dd>
          <dt>Device</dt><dd>{showing.assetId ? <Link to={`/assets/${showing.assetId}`}>{showing.device}</Link> : showing.device ?? '—'}</dd>
          {showing.user && <><dt>User</dt><dd>{showing.user}</dd></>}
          {showing.detail && <><dt>Result</dt><dd>{showing.detail}</dd></>}
        </dl>
        <SoftwareDetails item={showing} />
      </Modal>}
      {approving && <DecideDialog title={`Approve ${approving.name}`} item={approving} groups={groups} endpoint={`/deployment/events/${approving.id}/approve`}
        onClose={() => setApproving(null)} onDone={(m) => { setApproving(null); changed(m); }} />}
    </>
  );
}

// ---- device groups --------------------------------------------------------------------------------------------

function GroupsTab({ canManage, groups, changed, failed, reloadGroups }: Ctx & { reloadGroups: () => void }) {
  const [editing, setEditing] = useState<Group | 'new' | null>(null);
  const [showing, setShowing] = useState<Group | null>(null);
  const confirm = useConfirm();
  async function remove(g: Group) {
    if (!(await confirm({ title: `Delete the group ${g.name}?`, message: `Its ${g.rules} rule(s) are deleted with it; its devices follow the organisation's settings again.`, confirmLabel: 'Delete', danger: true }))) return;
    try { await api.del(`/deployment/groups/${g.id}`); reloadGroups(); changed(`Group ${g.name} deleted.`); } catch (e) { failed(e); }
  }
  const cols: Column<Group>[] = [
    { key: 'n', label: 'Group', className: 'wrap', render: (g) => <><b>{g.name}</b>{g.description && <div className="faint" style={{ fontSize: 11.5 }}>{g.description}</div>}</> },
    { key: 'm', label: 'Members', className: 'wrap', render: (g) => <span style={{ fontSize: 12 }}>{[g.departments.length ? `Departments: ${g.departments.join(', ')}` : '', g.locations.length ? `Locations: ${g.locations.join(', ')}` : '', g.assetIds.length ? `${g.assetIds.length} device(s)` : ''].filter(Boolean).join(' · ')}{g.platform && ` (${platformLabel(g.platform)} only)`}</span> },
    { key: 'd', label: 'Devices', render: (g) => <button className="btn sm" onClick={() => setShowing(g)}>{g.devices}</button> },
    { key: 'a', label: 'Application Control', render: (g) => g.appMode ? MODE_LABEL[g.appMode] : <span className="muted">Organisation's</span> },
    { key: 'w', label: 'WDAC (Windows)', render: (g) => g.wdacMode === 'off' ? <span className="muted">Off</span> : <Pill tone={g.wdacMode === 'enforce' ? 'crit' : 'warn'}>{g.wdacMode === 'enforce' ? 'Enforced' : 'Audit'}</Pill> },
    { key: 'r', label: 'Own Rules', render: (g) => g.rules },
    { key: 'x', label: '', render: (g) => canManage ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => setEditing(g)}>Edit</button>
      <button className="btn sm" aria-label={`Delete ${g.name}`} onClick={() => void remove(g)}><Icon name="trash" size={13} /></button>
    </div> : null },
  ];
  return (
    <>
      <div className="toolbar-row">
        <span className="hint grow">Devices by department, location or name, with their own rules, mode and WDAC.</span>
        {canManage && <button className="btn primary" onClick={() => setEditing('new')}><Icon name="plus" size={13} /> New group</button>}
      </div>
      <DataTable columns={cols} rows={groups} empty="No device groups." />
      {editing && <GroupDialog group={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={(m) => { setEditing(null); reloadGroups(); changed(m); }} />}
      {showing && <GroupDevices group={showing} onClose={() => setShowing(null)} />}
    </>
  );
}

function GroupDevices({ group, onClose }: { group: Group; onClose: () => void }) {
  const list = useApi(() => api.get<{ id: string; hostname: string | null; osName: string | null; department: string | null; location: string | null; agent: boolean; appControl: string | null; wdac: string | null }[]>(`/deployment/groups/${group.id}/devices`), [group.id]);
  return (
    <Modal title={`Devices of ${group.name}`} wide onClose={onClose}>
        <div>
          <DataTable rows={list.data ?? []} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
            { key: 'h', label: 'Device', render: (d) => <Link to={`/assets/${d.id}`}>{d.hostname ?? d.id}</Link> },
            { key: 'o', label: 'System', render: (d) => d.osName ?? '—' },
            { key: 'g', label: 'Department / Location', render: (d) => [d.department, d.location].filter(Boolean).join(' · ') || '—' },
            { key: 'a', label: 'Agent', render: (d) => d.agent ? <>{MODE_LABEL[d.appControl ?? 'off'] ?? d.appControl}{d.wdac && d.wdac !== 'off' && ` · WDAC ${d.wdac}`}</> : <span className="muted">No agent</span> },
          ]} />
        </div>
    </Modal>
  );
}

// ---- packages and deployments --------------------------------------------------------------------------------

function PackagesTab({ tick, canManage, canRun, groups, changed, failed }: Ctx) {
  const [local, setLocal] = useState(0);
  const pkgs = useApi(() => api.get<Package[]>('/deployment/packages'), [tick, local]);
  const [adding, setAdding] = useState<Package | 'new' | null>(null);
  const [deploying, setDeploying] = useState<Package | null>(null);
  const confirm = useConfirm();
  async function remove(p: Package) {
    if (!(await confirm({ title: `Delete ${p.name}?`, message: p.jobs ? 'It was deployed before: it is kept in the history without its file.' : 'The package and its file are deleted.', confirmLabel: 'Delete', danger: true }))) return;
    try { await api.del(`/deployment/packages/${p.id}`); setLocal((x) => x + 1); changed(`${p.name} deleted.`); } catch (e) { failed(e); }
  }
  const cols: Column<Package>[] = [
    { key: 'n', label: 'Package', className: 'wrap', render: (p) => <><b>{p.name}</b>{p.version && ` ${p.version}`}{!p.active && <> <Pill>Retired</Pill></>}<div className="faint" style={{ fontSize: 11.5 }}>{p.publisher ?? ''}{p.notes && ` · ${p.notes}`}</div></> },
    { key: 'f', label: 'File', className: 'wrap', render: (p) => <span style={{ fontSize: 12 }}><span className="mono">{p.fileName}</span><div className="faint">{p.format.toUpperCase()} · {fmt.bytes(p.size)} · {platformLabel(p.platform)}</div></span> },
    { key: 'a', label: 'Silent Install', className: 'wrap', render: (p) => <span className="mono" style={{ fontSize: 11.5 }}>{p.format === 'deb' ? 'apt install' : p.format === 'rpm' ? 'dnf / zypper install' : p.installArgs ?? '—'}</span> },
    { key: 'h', label: 'SHA-256', optional: true, render: (p) => <span className="mono" style={{ fontSize: 11 }} title={p.sha256}>{p.sha256.slice(0, 16)}…</span> },
    { key: 'i', label: 'Installed by Deployments', render: (p) => `${p.installed} device(s) · ${p.jobs} deployment(s)` },
    { key: 'x', label: '', render: (p) => <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      {canRun && p.active && <button className="btn sm primary" onClick={() => setDeploying(p)}>Deploy…</button>}
      {canManage && <button className="btn sm" onClick={() => setAdding(p)}>Edit</button>}
      {canManage && <button className="btn sm" aria-label={`Delete ${p.name}`} onClick={() => void remove(p)}><Icon name="trash" size={13} /></button>}
    </div> },
  ];
  return (
    <>
      <div className="toolbar-row">
        <span className="hint grow">.msi, .exe (with silent switches), .deb and .rpm. Agents verify the SHA-256 before installing.</span>
        {canManage && <button className="btn primary" onClick={() => setAdding('new')}><Icon name="upload" size={13} /> Upload package</button>}
      </div>
      <ErrorBox error={pkgs.error} />
      <DataTable columns={cols} rows={pkgs.data ?? []} empty={pkgs.loading ? 'Loading…' : 'No packages yet.'} />
      {adding && <PackageDialog pkg={adding === 'new' ? null : adding} onClose={() => setAdding(null)} onSaved={(m) => { setAdding(null); setLocal((x) => x + 1); changed(m); }} />}
      {deploying && <DeployDialog pkg={deploying} groups={groups} onClose={() => setDeploying(null)} onDone={(m) => { setDeploying(null); changed(m); }} />}
    </>
  );
}

function DeploymentsTab({ tick, canRun, changed, failed }: Ctx) {
  const [local, setLocal] = useState(0);
  const jobs = useApi(() => api.get<Job[]>('/deployment/jobs'), [tick, local]);
  const { query, navigate } = useRouter();
  const [open, setOpen] = useState<string | null>(query.get('job'));
  async function act(j: Job, what: 'retry' | 'cancel') {
    try {
      const r = await api.post<{ retried?: number; cancelled?: number }>(`/deployment/jobs/${j.id}/${what}`);
      setLocal((x) => x + 1);
      changed(what === 'retry' ? `${r.retried ?? 0} device(s) get the package again.` : `${r.cancelled ?? 0} device(s) that had not started were cancelled.`);
    } catch (e) { failed(e); }
  }
  const cols: Column<Job>[] = [
    { key: 't', label: 'Started', render: (j) => <span title={`${fmt.date(j.createdAt)}${j.createdBy ? ' by ' + j.createdBy : ''}`}>{fmt.ago(j.createdAt)}</span> },
    { key: 'p', label: 'Package', className: 'wrap', render: (j) => <><b>{j.operation === 'install' ? 'Install' : 'Uninstall'} {j.package}</b>{j.version && ` ${j.version}`}{j.note && <div className="faint" style={{ fontSize: 11.5 }}>{j.note}</div>}</> },
    { key: 'd', label: 'Devices', render: (j) => j.devices },
    { key: 's', label: 'Progress', className: 'wrap', render: (j) => <span style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
      {j.succeeded > 0 && <Pill tone="ok">{j.succeeded} done</Pill>}{j.running > 0 && <Pill tone="info">{j.running} running</Pill>}
      {j.waiting > 0 && <Pill>{j.waiting} waiting</Pill>}{j.failed > 0 && <Pill tone="crit">{j.failed} failed</Pill>}{j.skipped > 0 && <Pill tone="warn">{j.skipped} skipped</Pill>}
    </span> },
    { key: 'x', label: '', render: (j) => <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => setOpen(open === j.id ? null : j.id)}>{open === j.id ? 'Hide' : 'Devices'}</button>
      {canRun && j.failed > 0 && <button className="btn sm" onClick={() => void act(j, 'retry')}><Icon name="retry" size={13} /> Retry failed</button>}
      {canRun && j.waiting > 0 && <button className="btn sm" onClick={() => void act(j, 'cancel')}>Cancel waiting</button>}
    </div> },
  ];
  return (
    <>
      <div className="toolbar-row"><span className="hint grow">Online devices start at once; others when they connect (up to 7 days). Start from <button className="link-btn" onClick={() => navigate('/modules/deploy?tab=packages', true)}>Packages</button>.</span>
        <button className="btn" onClick={() => setLocal((x) => x + 1)}><Icon name="retry" size={13} /> Refresh</button></div>
      <ErrorBox error={jobs.error} />
      <DataTable columns={cols} rows={jobs.data ?? []} empty={jobs.loading ? 'Loading…' : 'No deployments yet.'} />
      {open && <JobDevices id={open} tick={tick + local} />}
    </>
  );
}

function JobDevices({ id, tick }: { id: string; tick: number }) {
  const list = useApi(() => api.get<JobDevice[]>(`/deployment/jobs/${id}`), [id, tick]);
  const tone = (s: string) => (s === 'succeeded' ? 'ok' : s === 'failed' || s === 'expired' ? 'crit' : s === 'skipped' ? 'warn' : s === 'running' || s === 'sent' ? 'info' : '');
  return (
    <Panel title="Devices of the deployment">
      <ErrorBox error={list.error} />
      <DataTable rows={(list.data ?? []).map((d) => ({ ...d, id: d.assetId }))} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
        { key: 'h', label: 'Device', render: (d) => <Link to={`/assets/${d.assetId}`}>{d.hostname ?? d.assetId}</Link> },
        { key: 's', label: 'Status', render: (d) => <Pill tone={tone(d.status)}>{fmt.label(d.status === 'queued' ? 'waiting' : d.status === 'sent' ? 'received' : d.status)}</Pill> },
        { key: 'd', label: 'Detail', className: 'wrap', render: (d) => <span style={{ fontSize: 12 }}>{d.detail ?? '—'}</span> },
        { key: 't', label: 'Finished', render: (d) => d.completedAt ? fmt.ago(d.completedAt) : d.sentAt ? `received ${fmt.ago(d.sentAt)}` : '—' },
      ]} />
    </Panel>
  );
}

function DevicesTab({ tick }: Ctx) {
  const list = useApi(() => api.get<DeviceRow[]>('/deployment/devices'), [tick]);
  const [q, setQ] = useSticky('DeploymentPage.DevicesTab.q', '');
  const rows = (list.data ?? []).filter((d) => !q.trim() || `${d.hostname} ${d.department} ${d.location}`.toLowerCase().includes(q.trim().toLowerCase())).map((d) => ({ ...d, id: d.agentId }));
  const remote = (s: string | null) => s === 'on' ? <Pill tone="ok">Protected</Pill> : s === 'missing' ? <Pill tone="crit">Removed — reinstalling</Pill> : s === 'off' ? <Pill tone="warn">Not protected</Pill> : <span className="muted">Not installed</span>;
  return (
    <>
      <div className="toolbar-row"><input className="in grow" placeholder="Search device, department or location…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search devices" /></div>
      <ErrorBox error={list.error} />
      <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No agents.'} columns={[
        { key: 'h', label: 'Device', render: (d) => <>{d.assetId ? <Link to={`/assets/${d.assetId}`}>{d.hostname}</Link> : d.hostname}<div className="faint" style={{ fontSize: 11.5 }}>{[d.department, d.location].filter(Boolean).join(' · ')}</div></> },
        { key: 'p', label: 'Platform', render: (d) => <>{d.platform.startsWith('linux') ? 'Linux' : 'Windows'} · {d.agentVersion ?? '—'}</> },
        { key: 'a', label: 'Application Control', render: (d) => !d.appControlCapable ? <span className="muted">Agent too old</span> : <>{d.appControl === 'error' ? <Pill tone="crit">Not working</Pill> : MODE_LABEL[d.appControl ?? 'off'] ?? d.appControl}{!d.policyCurrent && <span className="faint" title="The device has not received the latest policy yet (offline)."> · pending</span>}</> },
        { key: 'w', label: 'WDAC', render: (d) => d.platform.startsWith('linux') ? <span className="muted">—</span> : d.wdac === 'unsupported' ? <span className="muted" title="This edition of Windows has no ConfigCI module">Not available</span> : fmt.label(d.wdac ?? 'off') },
        { key: 'r', label: 'Remote Support Agent', render: (d) => remote(d.remoteProtection) },
        { key: 'o', label: 'Online', render: (d) => d.online ? <Pill tone="ok">Online</Pill> : <span className="muted" title={fmt.date(d.lastCheckin)}>{fmt.ago(d.lastCheckin)}</span> },
      ]} />
    </>
  );
}

// ---- installer details -------------------------------------------------------------------------------------------

/** Everything the device reported about the software (file, version, signature, origin), grouped and in a fixed order. */
function SoftwareDetails({ item }: { item: { name: string; publisher: string | null; version: string | null; sha256: string | null; path: string | null; details: Record<string, string> | null } }) {
  const d: Record<string, string> = { ...(item.details ?? {}) };
  if (!d['Product'] && !d['Package'] && !d['Requested name']) d['Product'] = item.name;
  if (!d['Version'] && item.version) d['Version'] = item.version;
  if (!d['Publisher'] && item.publisher) d['Publisher'] = item.publisher;
  if (!d['SHA-256'] && item.sha256) d['SHA-256'] = item.sha256;
  if (!d['Folder'] && !d['File name'] && item.path) d['File'] = item.path;
  const known = new Set(DETAIL_SECTIONS.flatMap(([, keys]) => keys));
  const other = Object.keys(d).filter((k) => !known.has(k));
  const sections: [string, string[]][] = [...DETAIL_SECTIONS.map(([t, keys]) => [t, keys.filter((k) => d[k])] as [string, string[]]), ['Other', other]];
  return (
    <>
      {sections.filter(([, keys]) => keys.length > 0).map(([title, keys]) => (
        <div key={title}>
          <div className="kv-section">{title}</div>
          <dl className="kv">
            {keys.map((k) => <div key={k} style={{ display: 'contents' }}><dt>{k}</dt><dd className={MONO_DETAILS.has(k) ? 'mono' : undefined}>
              {/^https?:\/\//.test(d[k]) ? <a href={d[k]} target="_blank" rel="noreferrer noopener">{d[k]}</a> : d[k]}</dd></div>)}
          </dl>
        </div>
      ))}
      {!item.details && <p className="faint" style={{ fontSize: 12.5, marginTop: 12 }}>The device sent only the basic details (its agent is older than build 110).</p>}
    </>
  );
}

function RequestDetailsDialog({ r, canManage, onClose, onApprove, onReject }: { r: AppRequest; canManage: boolean; onClose: () => void; onApprove: () => void; onReject: () => void }) {
  return (
    <Modal title={`Request: ${r.name}${r.version ? ' ' + r.version : ''}`} wide onClose={onClose}
      footer={<>{canManage && r.status === 'pending' && <><button className="btn" onClick={onReject}>Reject</button><button className="btn primary" onClick={onApprove}>Approve…</button></>}
        <button className="btn" onClick={onClose}>Close</button></>}>
      <div className="kv-section" style={{ marginTop: 0 }}>Request</div>
      <dl className="kv">
        <dt>Status</dt><dd>{r.status === 'pending' ? <Pill tone="warn">Waiting</Pill> : <Pill tone={r.status === 'approved' ? 'ok' : 'crit'}>{r.status === 'approved' ? 'Approved' : 'Rejected'}</Pill>}
          {r.decidedBy && <span className="faint"> · {r.decidedBy} · {fmt.date(r.decidedAt)}{r.decisionNote && ` · ${r.decisionNote}`}</span>}</dd>
        <dt>Asked</dt><dd>{fmt.date(r.createdAt)}</dd>
        <dt>User</dt><dd>{r.user ?? '—'}</dd>
        <dt>Reason</dt><dd>{r.reason ?? <span className="faint">No reason given</span>}</dd>
      </dl>
      <div className="kv-section">Device</div>
      <dl className="kv">
        <dt>Device</dt><dd>{r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.device}</Link> : r.device ?? '—'}</dd>
        {r.deviceOs && <><dt>Operating system</dt><dd>{r.deviceOs}</dd></>}
        {r.deviceIp && <><dt>IP address</dt><dd className="mono">{r.deviceIp}</dd></>}
        {r.department && <><dt>Department</dt><dd>{r.department}</dd></>}
        <dt>Platform</dt><dd>{platformLabel(r.platform)}{r.agentVersion && <span className="faint"> · agent {r.agentVersion}</span>}</dd>
        {r.lastCheckin && <><dt>Last seen</dt><dd>{fmt.ago(r.lastCheckin)}</dd></>}
      </dl>
      <SoftwareDetails item={r} />
    </Modal>
  );
}
