import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useColumns } from '../../components/columns';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Pager, Panel, Pill, statusTone, type Column } from '../../components/ui';
import { AccountDialog } from './CloudDialogs';
import {
  disk, gb, kindLabel, KIND_LABEL, PROVIDERS, providerLabel, providerShort, SEV_TONE, SEVERITIES, stateLabel, stateTone,
  type CloudAccount, type CloudChange, type CloudFinding, type CloudResource, type CloudSummary, type Provider,
} from './types';

type Tab = 'overview' | 'resources' | 'virtualization' | 'findings' | 'accounts' | 'changes';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['resources', 'Resources'], ['virtualization', 'Virtualization'], ['findings', 'Findings'], ['accounts', 'Connections'], ['changes', 'Changes']];

export function ProviderBadge({ provider, full }: { provider: string; full?: boolean }) {
  return <span className={`cl-prov cl-${provider}`} title={providerLabel(provider)}><i />{full ? providerLabel(provider) : providerShort(provider)}</span>;
}

export function CloudPage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const manage = can('credentials.manage');
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [adding, setAdding] = useState(false);
  const done = (text: string, tone: 'info' | 'crit' = 'info') => { setMsg({ tone, text }); setAdding(false); setTick((x) => x + 1); };
  return (
    <>
      <PageHead title="Cloud & Infrastructure" icon="cloud"
        actions={manage && <button className="btn primary" onClick={() => setAdding(true)}><Icon name="plus" size={13} /> Add connection</button>} />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/cloud?tab=${t}`, true)}>{l}</button>)}
      </div>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {tab === 'overview' && <Overview tick={tick} onAdd={manage ? () => setAdding(true) : undefined} />}
      {tab === 'resources' && <Resources />}
      {tab === 'virtualization' && <Virtualization />}
      {tab === 'findings' && <Findings tick={tick} onMsg={done} />}
      {tab === 'accounts' && <Accounts tick={tick} onMsg={done} onAdd={manage ? () => setAdding(true) : undefined} />}
      {tab === 'changes' && <Changes />}
      {adding && <AccountDialog account={null} onClose={() => setAdding(false)} onDone={(m) => { done(m); navigate('/cloud?tab=accounts', true); }} />}
    </>
  );
}

// ---- overview ------------------------------------------------------------------------------------------------------

function Overview({ tick, onAdd }: { tick: number; onAdd?: () => void }) {
  const s = useApi(() => api.get<CloudSummary>('/cloud/summary'), [tick]);
  if (s.error) return <ErrorBox error={s.error} />;
  const d = s.data;
  if (!d) return <Loading />;
  if (d.accounts === 0) return (
    <Panel>
      <div className="cl-empty">
        <div className="cl-empty-icons">{PROVIDERS.map((p) => <ProviderBadge key={p.id} provider={p.id} full />)}</div>
        <h3>Connect your cloud and virtualization platforms</h3>
        <p className="muted">Zentriq reads virtual machines, hosts, disks, networks, storage and databases with a read-only account, links each machine to its inventoried device,
          and points out what costs money for nothing (unattached disks, idle IPs, machines switched off for months) and what is exposed (RDP/SSH open to the internet, public storage).</p>
        <ul className="cl-howto">
          <li><b>Azure</b> — an app registration with the <i>Reader</i> role on the subscriptions.</li>
          <li><b>AWS</b> — an IAM user access key with the <i>ReadOnlyAccess</i> policy.</li>
          <li><b>Google Cloud</b> — a service account JSON key with the <i>Viewer</i> role on the projects.</li>
          <li><b>VMware</b> — a vCenter 6.5+ user with the <i>Read-only</i> role.</li>
          <li><b>Hyper-V</b> — the host names and a saved Windows credential (Hyper-V Administrators).</li>
          <li><b>Nutanix</b> — a Prism Central or Prism Element user with the <i>Viewer</i> role.</li>
          <li><b>Proxmox VE</b> — an API token with the <i>PVEAuditor</i> role.</li>
          <li><b>XCP-ng / Citrix Hypervisor</b> — a pool user with the <i>Read Only</i> role, on the pool master.</li>
          <li><b>oVirt / RHV / OLVM</b> — an engine user with a read-only role.</li>
        </ul>
        {onAdd ? <button className="btn primary" onClick={onAdd}><Icon name="plus" size={13} /> Add connection</button> : <p className="muted">Ask an administrator to add a connection.</p>}
      </div>
    </Panel>
  );
  const open = SEVERITIES.reduce((n, sev) => n + (d.findings[sev] ?? 0), 0);
  const ch = (k: string) => d.changes7[k] ?? 0;
  return (
    <>
      {d.accountsFailed > 0 && <Callout tone="crit" icon="alert">{d.accountsFailed} connection(s) failed at the last sync. <Link to="/cloud?tab=accounts">See Connections</Link>.</Callout>}
      <Kpis items={[
        { label: 'Virtual machines', value: d.vms.toLocaleString(), sub: `${d.vmsRunning.toLocaleString()} running · ${(d.vms - d.vmsRunning).toLocaleString()} stopped`, to: '/cloud?tab=resources&kind=vm' },
        { label: 'Linked to inventory', value: d.vms ? `${Math.round((d.vmsLinked / d.vms) * 100)}%` : '—', sub: `${d.unlinkedVms} running VM(s) without agent or scan`, to: '/cloud?tab=resources&kind=vm&linked=no' },
        { label: 'Open findings', value: open.toLocaleString(), sub: `${(d.findings.critical ?? 0) + (d.findings.high ?? 0)} critical/high · ${d.idleCost} cost`, to: '/cloud?tab=findings' },
        { label: 'Capacity', value: `${d.vcpu.toLocaleString()} vCPU`, sub: `${d.memoryGb.toLocaleString()} GB memory · ${disk(d.storageGb)} storage` },
        { label: 'Hosts', value: d.hosts.toLocaleString(), sub: 'Hyper-V and ESXi', to: '/cloud?tab=virtualization' },
        { label: 'Changes (7 days)', value: (ch('created') + ch('changed') + ch('deleted')).toLocaleString(), sub: `${ch('created')} new · ${ch('deleted')} deleted`, to: '/cloud?tab=changes' },
      ]} />
      <div className="grid-2">
        <Panel title="By platform" sub={`${d.resources.toLocaleString()} resources from ${d.accounts} connection(s) · last sync ${fmt.ago(d.lastSync)}`} flush>
          <DataTable rows={d.byProvider.map((p) => ({ ...p, id: p.provider }))} columns={[
            { key: 'p', label: 'Platform', render: (r) => <Link to={`/cloud?tab=resources&provider=${r.provider}`}><ProviderBadge provider={r.provider} full /></Link> },
            { key: 'n', label: 'Resources', render: (r) => r.n.toLocaleString() },
            { key: 'v', label: 'Virtual machines', render: (r) => r.vms.toLocaleString() },
            { key: 'r', label: 'Running', render: (r) => <>{r.running.toLocaleString()}{r.vms ? <span className="cl-bar"><i style={{ width: `${(r.running / r.vms) * 100}%` }} /></span> : null}</> },
          ]} />
        </Panel>
        <Panel title="Top findings" flush right={<Link className="btn sm" to="/cloud?tab=findings">All findings</Link>}>
          <DataTable rows={d.topFindings.map((f) => ({ ...f, id: f.rule }))} empty="No open findings." columns={[
            { key: 's', label: 'Severity', render: (r) => <Pill tone={SEV_TONE[r.severity]}>{r.severity}</Pill> },
            { key: 't', label: 'Finding', className: 'wrap', render: (r) => <Link to={`/cloud?tab=findings&rule=${r.rule}`}>{r.title}</Link> },
            { key: 'n', label: 'Resources', render: (r) => r.n.toLocaleString() },
          ]} />
        </Panel>
      </div>
      <div className="grid-2">
        <Panel title="Resource types" flush>
          <DataTable rows={Object.entries(d.byKind).sort((a, b) => b[1] - a[1]).map(([k, n]) => ({ id: k, k, n }))} columns={[
            { key: 'k', label: 'Type', render: (r) => <Link to={`/cloud?tab=resources&kind=${r.k}`}>{kindLabel(r.k)}</Link> },
            { key: 'n', label: 'Count', render: (r) => r.n.toLocaleString() },
          ]} />
        </Panel>
        <Panel title="Recent changes" flush right={<Link className="btn sm" to="/cloud?tab=changes">All changes</Link>}>
          <DataTable rows={d.recentChanges} empty="No changes yet." columns={[
            { key: 'at', label: 'When', render: (c) => fmt.ago(c.at) },
            { key: 'n', label: 'Resource', render: (c) => <Link to={`/cloud/resources/${c.resourceId}`}>{c.name ?? '—'}</Link> },
            { key: 'c', label: 'Change', render: (c) => <ChangeText c={c} /> },
          ]} />
        </Panel>
      </div>
    </>
  );
}

const CHANGE_TONE: Record<string, 'ok' | 'warn' | 'crit' | 'info'> = { created: 'ok', changed: 'info', deleted: 'crit', restored: 'warn' };
const FIELD_LABEL: Record<string, string> = { name: 'name', state: 'state', size: 'size', os: 'OS', privateIp: 'private IP', publicIp: 'public IP', cpu: 'vCPU', memoryMb: 'memory (MB)', diskGb: 'storage (GB)', parent: 'host' };

export function ChangeText({ c }: { c: Pick<CloudChange, 'change' | 'detail' | 'kind'> }) {
  const fields = c.change === 'changed' ? Object.entries(c.detail as Record<string, [string | null, string | null]>) : [];
  return (
    <span className="cl-change">
      <Pill tone={CHANGE_TONE[c.change]}>{c.change}</Pill> <span className="faint">{kindLabel(c.kind)}</span>
      {fields.slice(0, 3).map(([k, v]) => <span key={k} className="cl-diff">{FIELD_LABEL[k] ?? k}: <s>{Array.isArray(v) ? v[0] ?? '—' : ''}</s> → <b>{Array.isArray(v) ? v[1] ?? '—' : ''}</b></span>)}
      {fields.length > 3 && <span className="faint"> +{fields.length - 3} more</span>}
    </span>
  );
}

// ---- resources -----------------------------------------------------------------------------------------------------

type Paged<T> = { items: T[]; total: number };

function Resources() {
  const { query } = useRouter();
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(query.get('q') ?? '');
  const [provider, setProvider] = useState(query.get('provider') ?? '');
  const [kind, setKind] = useState(query.get('kind') ?? '');
  const [state, setState] = useState(query.get('state') ?? '');
  const [linked, setLinked] = useState(query.get('linked') ?? '');
  const [findings, setFindings] = useState(query.get('findings') === '1');
  const [deleted, setDeleted] = useState(false);
  const [accountId, setAccountId] = useState(query.get('account') ?? '');
  const [sort, setSort] = useState('name');
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<CloudResource>>('/cloud/resources', {
    q: search, provider, kind, state, linked, accountId, findings: findings || undefined, deleted: deleted || undefined, sort, page, pageSize,
  }), [search, provider, kind, state, linked, accountId, findings, deleted, sort, page]);
  const reset = () => setPage(1);
  const all: Column<CloudResource>[] = [
    { key: 'name', label: 'Name', className: 'wrap', render: (r) => <Link to={`/cloud/resources/${r.id}`}><b>{r.name ?? r.externalId}</b></Link> },
    { key: 'kind', label: 'Type', render: (r) => kindLabel(r.kind) },
    { key: 'provider', label: 'Platform', render: (r) => <ProviderBadge provider={r.provider} /> },
    { key: 'account', label: 'Connection', optional: true, render: (r) => r.account },
    { key: 'state', label: 'State', render: (r) => r.state ? <Pill tone={stateTone(r.state)}>{stateLabel(r.state)}</Pill> : <span className="muted">—</span> },
    { key: 'region', label: 'Region / host', render: (r) => r.region ?? r.group ?? '—' },
    { key: 'group', label: 'Group / VPC / cluster', optional: true, render: (r) => r.group ?? '—' },
    { key: 'size', label: 'Size', render: (r) => r.size ?? '—' },
    { key: 'spec', label: 'vCPU / memory', render: (r) => r.cpu || r.memoryMb ? `${r.cpu ?? '?'} · ${gb(r.memoryMb)}` : r.diskGb != null ? disk(r.diskGb) : '—' },
    { key: 'os', label: 'OS / engine', className: 'wrap', render: (r) => r.os ?? '—' },
    { key: 'ip', label: 'IP address', render: (r) => <span className="mono">{r.privateIp ?? r.publicIp ?? '—'}</span> },
    { key: 'public', label: 'Public IP', optional: true, render: (r) => <span className="mono">{r.publicIp ?? '—'}</span> },
    { key: 'device', label: 'Inventoried device', render: (r) => r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.assetName ?? 'device'}</Link> : r.kind === 'vm' || r.kind === 'host' ? <span className="muted">not linked</span> : <span className="muted">—</span> },
    { key: 'findings', label: 'Findings', render: (r) => r.openFindings ? <Pill tone={SEV_TONE[r.worstSeverity ?? 'info']}>{r.openFindings}</Pill> : <span className="muted">—</span> },
    { key: 'department', label: 'Department', optional: true, render: (r) => r.department ?? '—' },
    { key: 'tags', label: 'Tags', optional: true, className: 'wrap', render: (r) => Object.entries(r.tags).slice(0, 4).map(([k, v]) => <span key={k} className="cl-tag">{k}={v}</span>) },
    { key: 'since', label: 'In this state since', optional: true, render: (r) => fmt.day(r.stateSince) },
    { key: 'seen', label: 'Last seen', optional: true, render: (r) => fmt.ago(r.lastSeen) },
  ];
  const { columns, chooser } = useColumns('cloud.resources', all);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); reset(); setSearch(q); }}>
        <input className="in grow" placeholder="Search name, ID, host name, IP, OS, region, tag…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search resources" />
        <select className="in" value={provider} onChange={(e) => { reset(); setProvider(e.target.value); }} aria-label="Platform">
          <option value="">All platforms</option>{PROVIDERS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
        </select>
        <select className="in" value={kind} onChange={(e) => { reset(); setKind(e.target.value); }} aria-label="Type">
          <option value="">All types</option>{Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        <select className="in" value={state} onChange={(e) => { reset(); setState(e.target.value); }} aria-label="State">
          <option value="">Any state</option><option value="running">Running</option><option value="stopped">Not running</option>
        </select>
        <select className="in" value={linked} onChange={(e) => { reset(); setLinked(e.target.value); }} aria-label="Linked to inventory">
          <option value="">Linked or not</option><option value="yes">Linked to a device</option><option value="no">Not linked</option>
        </select>
        <select className="in" value={sort} onChange={(e) => { reset(); setSort(e.target.value); }} aria-label="Sort">
          <option value="name">Sort: name</option><option value="kind">Sort: type</option><option value="provider">Sort: platform</option><option value="state">Sort: state</option>
          <option value="region">Sort: region</option><option value="size">Sort: size</option><option value="findings">Sort: findings</option><option value="lastSeen">Sort: last seen</option>
        </select>
        {accountId && <button type="button" className="btn sm" onClick={() => { reset(); setAccountId(''); }}>One connection ✕</button>}
        <label className="check-inline"><input type="checkbox" checked={findings} onChange={(e) => { reset(); setFindings(e.target.checked); }} /> With findings</label>
        <label className="check-inline"><input type="checkbox" checked={deleted} onChange={(e) => { reset(); setDeleted(e.target.checked); }} /> Deleted</label>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data?.items ?? []} columns={columns} empty={data.loading ? 'Loading…' : 'No resources match. Add a connection, or wait for its first sync.'} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}

// ---- virtualization ------------------------------------------------------------------------------------------------

function Virtualization() {
  const hosts = useApi(() => api.get<Paged<CloudResource>>('/cloud/resources', { kind: 'host', pageSize: 500, sort: 'name' }), []);
  const clusters = useApi(() => api.get<Paged<CloudResource>>('/cloud/resources', { kind: 'cluster', pageSize: 500 }), []);
  const stores = useApi(() => api.get<Paged<CloudResource>>('/cloud/resources', { kind: 'datastore', pageSize: 500 }), []);
  const n = (v: unknown) => (typeof v === 'number' ? v : null);
  return (
    <>
      <ErrorBox error={hosts.error ?? stores.error ?? clusters.error} />
      <Panel title="Hosts" sub="Hyper-V hosts and VMware ESXi hosts, with the virtual machines on them" flush>
        <DataTable rows={hosts.data?.items ?? []} empty={hosts.loading ? 'Loading…' : 'No hosts. Add a VMware vCenter or Hyper-V connection.'} columns={[
          { key: 'n', label: 'Host', render: (r) => <Link to={`/cloud/resources/${r.id}`}><b>{r.name}</b></Link> },
          { key: 'p', label: 'Platform', render: (r) => <ProviderBadge provider={r.provider} /> },
          { key: 'c', label: 'Cluster / domain', render: (r) => r.group ?? '—' },
          { key: 's', label: 'State', render: (r) => <Pill tone={stateTone(r.state)}>{stateLabel(r.state)}</Pill> },
          { key: 'os', label: 'Operating system', className: 'wrap', render: (r) => <>{r.os ?? '—'}{r.properties.osEndOfSupport === true && <> <Pill tone="warn">out of support</Pill></>}</> },
          { key: 'vm', label: 'Virtual machines', render: (r) => <Link to={`/cloud?tab=resources&kind=vm&q=${encodeURIComponent(r.name ?? '')}`}>{n(r.properties.vms) ?? '—'}</Link> },
          { key: 'hw', label: 'CPU / memory', render: (r) => r.cpu || r.memoryMb ? `${r.cpu ?? '?'} · ${gb(r.memoryMb)}` : '—' },
          { key: 'mc', label: 'Memory given to running VMs', render: (r) => { const p = n(r.properties.memoryCommitPercent); return p == null ? '—' : <span className="cl-meter"><span className="cl-bar"><i className={p > 150 ? 'crit' : p > 100 ? 'warn' : ''} style={{ width: `${Math.min(100, p / 2)}%` }} /></span>{p}%</span>; } },
          { key: 'd', label: 'Inventoried device', render: (r) => r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.assetName}</Link> : <span className="muted">not linked</span> },
        ]} />
      </Panel>
      <div className="grid-2">
        <Panel title="Datastores" sub="VMware datastores — free space" flush>
          <DataTable rows={stores.data?.items ?? []} empty="No datastores." columns={[
            { key: 'n', label: 'Datastore', render: (r) => <Link to={`/cloud/resources/${r.id}`}>{r.name}</Link> },
            { key: 't', label: 'Type', render: (r) => r.size ?? '—' },
            { key: 'c', label: 'Capacity', render: (r) => disk(r.diskGb) },
            { key: 'f', label: 'Free', render: (r) => { const p = n(r.properties.freePercent); return p == null ? '—' : <span className="cl-meter"><span className="cl-bar"><i className={p < 10 ? 'crit' : p < 20 ? 'warn' : 'ok'} style={{ width: `${p}%` }} /></span>{p}% · {disk(n(r.properties.freeGb))}</span>; } },
          ]} />
        </Panel>
        <Panel title="Clusters" flush>
          <DataTable rows={clusters.data?.items ?? []} empty="No clusters." columns={[
            { key: 'n', label: 'Cluster', render: (r) => <Link to={`/cloud/resources/${r.id}`}>{r.name}</Link> },
            { key: 'h', label: 'Hosts', render: (r) => n(r.properties.hosts) ?? '—' },
            { key: 'ha', label: 'HA', render: (r) => r.properties.ha === true ? <Pill tone="ok">on</Pill> : r.properties.ha === false ? <Pill tone="warn">off</Pill> : '—' },
            { key: 'drs', label: 'DRS', render: (r) => r.properties.drs === true ? <Pill tone="ok">on</Pill> : r.properties.drs === false ? <Pill>off</Pill> : '—' },
          ]} />
        </Panel>
      </div>
    </>
  );
}

// ---- findings ------------------------------------------------------------------------------------------------------

function Findings({ tick, onMsg }: { tick: number; onMsg: (m: string, tone?: 'info' | 'crit') => void }) {
  const { query } = useRouter();
  const { can } = useAuth();
  const canIgnore = can('assets.manage');
  const [status, setStatus] = useState('open');
  const [severity, setSeverity] = useState('');
  const [provider, setProvider] = useState('');
  const [rule, setRule] = useState(query.get('rule') ?? '');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [local, setLocal] = useState(0);
  const data = useApi(() => api.get<CloudFinding[]>('/cloud/findings', { status, severity, provider, rule, q: search }), [status, severity, provider, rule, search, tick, local]);
  const meta = useApi(() => api.get<{ rules: { id: string; label: string }[] }>('/cloud/meta'), []);
  async function ignore(f: CloudFinding, ignored: boolean) {
    try { await api.post(`/cloud/findings/${f.id}/ignore`, { ignored }); setLocal((x) => x + 1); onMsg(ignored ? `"${f.title}" on ${f.resource} is ignored.` : 'The finding is open again.'); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e), 'crit'); }
  }
  const rows = data.data ?? [];
  const counts = SEVERITIES.map((s) => [s, rows.filter((r) => r.severity === s).length] as const);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search resource, finding, connection…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search findings" />
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="open">Open</option><option value="ignored">Ignored</option><option value="resolved">Resolved</option><option value="all">All</option>
        </select>
        <select className="in" value={severity} onChange={(e) => setSeverity(e.target.value)} aria-label="Severity">
          <option value="">All severities</option>{SEVERITIES.map((s) => <option key={s} value={s}>{s[0]!.toUpperCase() + s.slice(1)}</option>)}
        </select>
        <select className="in" value={rule} onChange={(e) => setRule(e.target.value)} aria-label="Finding">
          <option value="">All findings</option>{(meta.data?.rules ?? []).map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
        </select>
        <select className="in" value={provider} onChange={(e) => setProvider(e.target.value)} aria-label="Platform">
          <option value="">All platforms</option>{PROVIDERS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      {rows.length > 0 && <div className="cl-sevbar">{counts.filter(([, n]) => n > 0).map(([s, n]) => <span key={s}><Pill tone={SEV_TONE[s]}>{s}</Pill> {n}</span>)}</div>}
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : status === 'open' ? 'No open findings.' : 'Nothing here.'} columns={[
          { key: 's', label: 'Severity', render: (f) => <Pill tone={SEV_TONE[f.severity]}>{f.severity}</Pill> },
          { key: 't', label: 'Finding', className: 'wrap', render: (f) => <><b>{f.title}</b>{f.detail && <div className="faint" style={{ fontSize: 12 }}>{f.detail}</div>}</> },
          { key: 'r', label: 'Resource', className: 'wrap', render: (f) => <><Link to={`/cloud/resources/${f.resourceId}`}>{f.resource ?? '—'}</Link><div className="faint" style={{ fontSize: 11.5 }}>{kindLabel(f.kind)}{f.region ? ` · ${f.region}` : ''}</div></> },
          { key: 'p', label: 'Platform', render: (f) => <><ProviderBadge provider={f.provider} /><div className="faint" style={{ fontSize: 11.5 }}>{f.account}</div></> },
          { key: 'f', label: 'Since', render: (f) => f.resolvedAt ? <>resolved {fmt.day(f.resolvedAt)}</> : fmt.day(f.firstSeen) },
          ...(canIgnore ? [{ key: 'x', label: '', render: (f: CloudFinding) => f.resolvedAt ? null : f.ignored
            ? <button className="btn sm" onClick={() => void ignore(f, false)} title={`Ignored by ${f.ignoredBy ?? '?'}`}>Reopen</button>
            : <button className="btn sm ghost" onClick={() => void ignore(f, true)}>Ignore</button> }] : []),
        ]} />
      </Panel>
    </>
  );
}

// ---- connections ---------------------------------------------------------------------------------------------------

function Accounts({ tick, onMsg, onAdd }: { tick: number; onMsg: (m: string, tone?: 'info' | 'crit') => void; onAdd?: () => void }) {
  const { can } = useAuth();
  const manage = can('credentials.manage');
  const canSync = can('scans.run');
  const confirm = useConfirm();
  const [local, setLocal] = useState(0);
  const [edit, setEdit] = useState<CloudAccount | null>(null);
  const list = useApi(() => api.get<CloudAccount[]>('/cloud/accounts'), [tick, local]);
  async function sync(a: CloudAccount) {
    try { await api.post(`/cloud/accounts/${a.id}/sync`); setLocal((x) => x + 1); onMsg(`Sync of "${a.name}" queued. It takes from a few seconds to a few minutes.`); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e), 'crit'); }
  }
  async function remove(a: CloudAccount) {
    if (!(await confirm({ title: 'Delete connection', message: `Delete "${a.name}"? Its ${a.resources} resource(s), changes and findings are removed from Zentriq. Nothing changes in ${providerLabel(a.provider)}.`, confirmLabel: 'Delete', danger: true }))) return;
    try { await api.del(`/cloud/accounts/${a.id}`); setLocal((x) => x + 1); onMsg(`"${a.name}" deleted.`); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e), 'crit'); }
  }
  const where = (a: CloudAccount) => {
    const s = a.settings;
    switch (a.provider) {
      case 'azure': return <>Tenant <span className="mono">{s.tenantId}</span>{s.subscriptions?.length ? ` · ${s.subscriptions.length} subscription(s)` : ' · all subscriptions'}</>;
      case 'aws': return <>Key <span className="mono">{s.accessKeyId}</span>{s.regions?.length ? ` · ${s.regions.join(', ')}` : ' · all regions'}</>;
      case 'gcp': return <>{s.clientEmail ? <span className="mono">{s.clientEmail}</span> : 'Service account'}{s.projects?.length ? ` · ${s.projects.join(', ')}` : ' · all projects it can see'}</>;
      case 'vmware': case 'nutanix': case 'proxmox': case 'xenserver': case 'ovirt':
        return <><span className="mono">{s.host}{s.port ? `:${s.port}` : ''}</span> as {s.username}</>;
      default: return <>{(s.hosts ?? []).join(', ')}{a.credentialName ? <> · as {a.credentialName}</> : null}</>;
    }
  };
  if (list.error) return <ErrorBox error={list.error} />;
  if (!list.data) return <Loading />;
  if (list.data.length === 0) return <Callout icon="info">No connections yet. {onAdd ? <button className="btn sm primary" onClick={onAdd}>Add connection</button> : 'Ask an administrator to add one.'}</Callout>;
  return (
    <>
      <div className="cl-accounts">
        {list.data.map((a) => {
          const running = a.jobStatus === 'queued' || a.jobStatus === 'running';
          const status = running ? a.jobStatus! : a.lastStatus ?? 'never synced';
          return (
            <section key={a.id} className={`panel cl-account${a.enabled ? '' : ' off'}`}>
              <header>
                <ProviderBadge provider={a.provider} full />
                <h3>{a.name}</h3>
                <Pill tone={running ? 'info' : statusTone(a.lastStatus)}>{status}</Pill>
                {!a.enabled && <Pill tone="warn">disabled</Pill>}
                <div className="cl-acts">
                  {canSync && a.enabled && <button className="btn sm" disabled={running} onClick={() => void sync(a)}><Icon name="retry" size={13} /> Sync now</button>}
                  {manage && <button className="btn sm" onClick={() => setEdit(a)}><Icon name="edit" size={13} /> Edit</button>}
                  {manage && <button className="btn sm ghost" aria-label={`Delete ${a.name}`} onClick={() => void remove(a)}><Icon name="trash" size={13} /></button>}
                </div>
              </header>
              <p className="muted">{where(a)}</p>
              <dl className="cl-stats">
                <div><dt>Resources</dt><dd><Link to={`/cloud?tab=resources&account=${a.id}`}>{a.resources.toLocaleString()}</Link></dd></div>
                <div><dt>Linked devices</dt><dd>{a.lastCounts.linked ?? '—'}</dd></div>
                <div><dt>Findings</dt><dd>{a.lastCounts.findings ?? '—'}</dd></div>
                <div><dt>Last sync</dt><dd title={fmt.date(a.lastSyncAt)}>{fmt.ago(a.lastSyncAt)}</dd></div>
                <div><dt>Every</dt><dd>{a.syncHours} h</dd></div>
              </dl>
              {a.lastCounts.identity && <p className="faint" style={{ fontSize: 12 }}>Signed in as {a.lastCounts.identity}</p>}
              {a.lastStatus === 'failed' && a.lastError && <Callout tone="crit" icon="alert">{a.lastError}</Callout>}
              {a.lastStatus === 'partial' && a.lastWarnings.length > 0 && <Callout tone="warn" icon="alert">
                Read in part: {a.lastWarnings.slice(0, 3).join(' · ')}{a.lastWarnings.length > 3 ? ` (+${a.lastWarnings.length - 3} more)` : ''}
              </Callout>}
            </section>
          );
        })}
      </div>
      <p className="muted" style={{ fontSize: 12.5 }}>
        Zentriq only reads: nothing is started, stopped or changed on the platforms. Cloud resources do not count towards the device licence.
        Tag resources with <span className="mono">department</span> or <span className="mono">location</span> (matching a Zentriq department or location name) to fill those columns.
      </p>
      {edit && <AccountDialog account={edit} onClose={() => setEdit(null)} onDone={(m) => { setEdit(null); setLocal((x) => x + 1); onMsg(m); }} />}
    </>
  );
}

// ---- changes -------------------------------------------------------------------------------------------------------

function Changes() {
  const [days, setDays] = useState(30);
  const [change, setChange] = useState('');
  const [kind, setKind] = useState('');
  const data = useApi(() => api.get<CloudChange[]>('/cloud/changes', { days, change, kind }), [days, change, kind]);
  return (
    <>
      <div className="toolbar-row">
        <select className="in" value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period">
          <option value={1}>Last 24 hours</option><option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option><option value={365}>Last year</option>
        </select>
        <select className="in" value={change} onChange={(e) => setChange(e.target.value)} aria-label="Change">
          <option value="">All changes</option><option value="created">New</option><option value="changed">Changed</option><option value="deleted">Deleted</option><option value="restored">Back again</option>
        </select>
        <select className="in" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Type">
          <option value="">All types</option>{Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} empty={data.loading ? 'Loading…' : 'No changes in this period.'} columns={[
          { key: 'at', label: 'When', render: (c) => fmt.date(c.at) },
          { key: 'n', label: 'Resource', render: (c) => <Link to={`/cloud/resources/${c.resourceId}`}><b>{c.name ?? '—'}</b></Link> },
          { key: 'c', label: 'Change', className: 'wrap', render: (c) => <ChangeText c={c} /> },
          { key: 'p', label: 'Platform', render: (c) => <><ProviderBadge provider={c.provider as Provider} /> <span className="faint">{c.account}</span></> },
          { key: 'r', label: 'Region', render: (c) => c.region ?? '—' },
        ]} />
      </Panel>
    </>
  );
}
