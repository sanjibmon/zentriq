import { useEffect, useRef, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart, ChartKindPicker, TrendArea, useChartKind, type ChartKind, type Slice } from '../../components/charts';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import {
  cap, errorText, pct, prioTone, PRIORITY_COLOR, PRIORITY_HELP, PRIORITY_SHORT, SEVERITY_COLOR, sevTone, STATUS_LABEL, statusTone,
  type Finding, type ProductMapping, type Remediation, type VulnImport, type VulnOverview, type VulnSettings, cvssText } from './types';
import { CveDialog, ExceptionDialog, FindingDrawer, WorkflowDialog } from './VulnDialogs';

type Tab = 'overview' | 'findings' | 'remediation' | 'products' | 'feeds';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['findings', 'Findings'], ['remediation', 'Remediation plan'], ['products', 'Software mapping'], ['feeds', 'Feeds & settings']];

export function VulnerabilitiesPage() {
  const { query, navigate } = useRouter();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const [cve, setCve] = useState<string | null>(null);
  const go = (t: Tab, extra = '') => navigate(`/vulnerabilities?tab=${t}${extra}`, true);
  return (
    <>
      <PageHead title="Vulnerability Management" />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => go(t)}>{l}</button>)}
      </div>
      {tab === 'overview' && <Overview onCve={setCve} go={go} />}
      {tab === 'findings' && <Findings onCve={setCve} />}
      {tab === 'remediation' && <RemediationPlan />}
      {tab === 'products' && <Products />}
      {tab === 'feeds' && <Feeds />}
      {cve && <CveDialog cve={cve} onClose={() => setCve(null)} />}
    </>
  );
}

// ---- Overview ---------------------------------------------------------------------------------

function Overview({ onCve, go }: { onCve: (c: string) => void; go: (t: Tab, extra?: string) => void }) {
  const o = useApi(() => api.get<VulnOverview>('/vulnerabilities/overview'), []);
  const k = o.data?.kpi;
  if (o.error) return <ErrorBox error={o.error} />;
  if (!o.data || !k) return <p className="muted">Loading…</p>;
  const noData = k.cveCatalogue === 0;
  const nvd = o.data.feeds.find((f) => f.source === 'nvd');
  return (
    <>
      {noData && <Callout tone="warn" icon="info">No vulnerability data yet. Open <button className="linkish" onClick={() => go('feeds')}>Feeds &amp; settings</button> to update from Microsoft, NVD, CISA KEV and EPSS (or import the files on an offline server). Findings appear once the data is in and devices are evaluated.</Callout>}
      {k.windowsNoBuild > 0 && k.windowsCves > 0 && <Callout tone="warn" icon="info">{k.windowsNoBuild} of {k.windowsDevices} Windows device(s) have no full build number in their inventory, so Windows vulnerabilities cannot be checked on them. Update their agent (Agents › Update) or collect inventory again; agentless devices report it at the next scan.</Callout>}
      {!noData && nvd?.lastOkAt && Date.now() - new Date(nvd.lastOkAt).getTime() > 7 * 86400000 && <Callout tone="warn" icon="alert">NVD data was last updated {fmt.ago(nvd.lastOkAt)}. New vulnerabilities are missed until the feeds are updated.</Callout>}
      <Kpis items={[
        { label: 'Open findings', value: k.open.toLocaleString(), sub: `${k.cves.toLocaleString()} distinct CVEs`, to: '/vulnerabilities?tab=findings' },
        { label: 'Critical', value: <span style={{ color: k.critical ? SEVERITY_COLOR.critical : undefined }}>{k.critical.toLocaleString()}</span>, sub: `${k.high.toLocaleString()} high`, to: '/vulnerabilities?tab=findings&severity=critical' },
        { label: 'Known exploited', value: <span style={{ color: k.exploited ? SEVERITY_COLOR.critical : undefined }}>{k.exploited.toLocaleString()}</span>, sub: 'CISA KEV or Microsoft', to: '/vulnerabilities?tab=findings&kev=true' },
        { label: 'Overdue', value: k.overdue.toLocaleString(), sub: 'past SLA due date', to: '/vulnerabilities?tab=findings&overdue=true' },
        { label: 'Affected devices', value: `${k.affectedAssets.toLocaleString()} / ${k.assets.toLocaleString()}`, sub: k.assets ? `${Math.round((100 * k.affectedAssets) / k.assets)}% of active devices` : '' },
        { label: 'Remediation (90 d)', value: `${k.mttrDays} d`, sub: `mean time to fix · ${k.slaMet}% within SLA` },
      ]} />
      <Kpis items={[
        { label: 'Windows (OS)', value: k.osOpen.toLocaleString(), sub: `open · ${k.windowsCves.toLocaleString()} Windows CVEs known`, to: '/vulnerabilities?tab=findings&source=os' },
        { label: 'Applications', value: k.softwareOpen.toLocaleString(), sub: 'open in installed software', to: '/vulnerabilities?tab=findings&source=software' },
        { label: 'In progress', value: k.inProgress.toLocaleString(), sub: 'being remediated', to: '/vulnerabilities?tab=findings&status=in_progress' },
        { label: 'Unassigned', value: k.unassigned.toLocaleString(), sub: 'open, no owner yet', to: '/vulnerabilities?tab=findings&assignee=none' },
        { label: 'Risk accepted', value: k.accepted.toLocaleString(), sub: 'with an expiry date', to: '/vulnerabilities?tab=findings&status=accepted' },
        { label: 'Fixed (30 d)', value: k.fixed30.toLocaleString(), sub: 'detected as fixed', to: '/vulnerabilities?tab=findings&status=fixed' },
      ]} />
      <div className="grid-3">
        <ChartTile id="vuln.severity" title="Open by severity" fallback="share" centerLabel="open"
          data={o.data.bySeverity.map((s) => ({ key: s.key, label: cap(s.key), value: s.count, color: SEVERITY_COLOR[s.key] }))} onSelect={(s) => go('findings', `&severity=${s.key}`)} />
        <ChartTile id="vuln.priority" title="Open by priority" sub="Risk-based: CVSS + KEV + EPSS" fallback="bar" centerLabel="open"
          data={o.data.byPriority.map((p) => ({ key: p.key, label: PRIORITY_SHORT[p.key] ?? p.key, value: p.count, color: PRIORITY_COLOR[p.key] }))} onSelect={(s) => go('findings', `&priority=${s.key}`)} />
        <ChartTile id="vuln.software" title="Most vulnerable software" fallback="bar" centerLabel="open"
          data={o.data.topSoftware.map((s, i) => ({ key: `${i}`, label: s.key, value: s.count }))} onSelect={(s) => go('findings', `&q=${encodeURIComponent(s.label)}`)} />
      </div>
      <div className="grid-2" style={{ marginBottom: 14 }}>
        <AgingTile rows={o.data.aging} onSelect={() => go('findings', '&sort=age')} />
        <ChartTile id="vuln.source" title="Where they are" sub="Windows itself or installed applications" fallback="share" centerLabel="open"
          data={o.data.bySource.map((s) => ({ key: s.key, label: s.key === 'os' ? 'Windows (OS)' : 'Applications', value: s.count, color: s.key === 'os' ? '#2f7dbd' : '#7a5af8' }))} onSelect={(s) => go('findings', `&source=${s.key}`)} />
      </div>
      <Panel title="Findings over the last 30 days" sub="Line: open findings · bars: newly detected">
        <TrendArea unit="open findings" addedLabel="new" points={o.data.trend.map((t) => ({ label: new Date(`${t.day}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }), total: t.open, added: t.opened }))} />
      </Panel>
      <div className="grid-2" style={{ marginTop: 14 }}>
        <Panel title="Top CVEs to fix" flush>
          <DataTable rows={o.data.topCves.map((c) => ({ ...c, id: c.cve }))} empty="No open findings." columns={[
            { key: 'c', label: 'CVE', className: 'wrap', render: (c) => <><button className="linkish mono" onClick={() => onCve(c.cve)}>{c.cve}</button>{c.title && <div className="faint clamp-2" style={{ fontSize: 11.5, maxWidth: 260 }}>{c.title}</div>}</> },
            { key: 'p', label: 'Priority', render: (c) => <Pill tone={prioTone(c.priority)}>{c.priority}</Pill> },
            { key: 's', label: 'CVSS', render: (c) => <Pill tone={sevTone(c.severity)}>{cvssText(c.cvss)}</Pill> },
            { key: 'k', label: 'Exploited', render: (c) => c.exploited ? <Pill tone="crit">{c.kev ? 'KEV' : 'Yes'}</Pill> : '' },
            { key: 'e', label: 'EPSS', render: (c) => pct(c.epss) },
            { key: 'a', label: 'Devices', render: (c) => c.assets.toLocaleString() },
          ]} />
        </Panel>
        <Panel title="Riskiest devices" flush>
          <DataTable rows={o.data.topAssets.map((a) => ({ ...a, id: a.assetId }))} empty="No open findings." columns={[
            { key: 'h', label: 'Device', render: (a) => <Link to={`/assets/${a.assetId}`}>{a.hostname ?? a.assetId}</Link> },
            { key: 'r', label: 'Max risk', render: (a) => <RiskBar v={a.maxRisk} /> },
            { key: 'o', label: 'Open', render: (a) => <Link to={`/vulnerabilities?tab=findings&assetId=${a.assetId}`}>{a.open.toLocaleString()}</Link> },
            { key: 'k', label: 'KEV', render: (a) => a.kev ? <Pill tone="crit">{a.kev}</Pill> : '' },
          ]} />
        </Panel>
      </div>
    </>
  );
}

/** A chart tile whose type (share bar, bar, column, pie) the viewer can switch; the choice is remembered. */
function ChartTile({ id, title, sub, data, onSelect, fallback, centerLabel }: { id: string; title: string; sub?: string; data: Slice[]; onSelect?: (s: Slice) => void; fallback: ChartKind; centerLabel?: string }) {
  const [kind, setKind] = useChartKind(id, fallback);
  return (
    <Panel title={title} sub={sub} right={<ChartKindPicker value={kind} onChange={setKind} />}>
      {data.every((d) => d.value === 0) ? <div className="chart-empty">No open findings.</div> : <CategoryChart kind={kind} data={data} onSelect={onSelect} centerLabel={centerLabel} />}
    </Panel>
  );
}

/** Aging: stacked by severity (default) or totals per age bucket as a share bar, bar, column or pie. */
function AgingTile({ rows, onSelect }: { rows: VulnOverview['aging']; onSelect: () => void }) {
  const [kind, setKind] = useChartKind('vuln.aging', 'bar');
  const [stacked, setStacked] = useState(() => { try { return localStorage.getItem('zq.chart.vuln.aging.stacked') !== 'false'; } catch { return true; } });
  const pick = (v: boolean, k?: ChartKind) => { setStacked(v); if (k) setKind(k); try { localStorage.setItem('zq.chart.vuln.aging.stacked', String(v)); } catch { /* storage unavailable */ } };
  const AGE_COLOR = ['#2f9e6e', '#e0a100', '#e8590c', '#d9480f', '#c92a2a'];
  return (
    <Panel title="Aging" sub="How long open findings have been open"
      right={<ChartKindPicker value={stacked ? 'stacked' : kind} onChange={(k) => pick(false, k)} extra={['stacked', 'Stacked', () => pick(true)]} />}>
      {stacked ? <AgingBars rows={rows} onSelect={onSelect} />
        : rows.length === 0 ? <div className="chart-empty">No open findings.</div>
        : <CategoryChart kind={kind} centerLabel="open" onSelect={onSelect}
            data={rows.map((r, i) => ({ key: r.bucket, label: r.bucket, value: r.critical + r.high + r.medium + r.low, color: AGE_COLOR[i] }))} />}
    </Panel>
  );
}

/** Open findings per age bucket, stacked by severity. */
function AgingBars({ rows, onSelect }: { rows: VulnOverview['aging']; onSelect: () => void }) {
  if (rows.length === 0) return <div className="chart-empty">No open findings.</div>;
  const max = Math.max(1, ...rows.map((r) => r.critical + r.high + r.medium + r.low));
  const parts: [keyof Omit<VulnOverview['aging'][number], 'bucket'>, string][] = [['critical', 'Critical'], ['high', 'High'], ['medium', 'Medium'], ['low', 'Low']];
  return (
    <div className="aging">
      {rows.map((r) => {
        const total = r.critical + r.high + r.medium + r.low;
        return <button key={r.bucket} type="button" className="aging-row" onClick={onSelect} title={parts.map(([k, l]) => `${l}: ${r[k]}`).join(' · ')}>
          <span className="lbl">{r.bucket}</span>
          <span className="bar">{parts.map(([k]) => r[k] > 0 && <i key={k} style={{ width: `${(r[k] / max) * 100}%`, background: SEVERITY_COLOR[k] }} />)}</span>
          <span className="n">{total.toLocaleString()}</span>
        </button>;
      })}
      <div className="legend-row">{parts.map(([k, l]) => <span key={k}><i style={{ background: SEVERITY_COLOR[k] }} />{l}</span>)}</div>
    </div>
  );
}

export function RiskBar({ v }: { v: number }) {
  const color = v >= 80 ? SEVERITY_COLOR.critical : v >= 60 ? SEVERITY_COLOR.high : v >= 35 ? SEVERITY_COLOR.medium : SEVERITY_COLOR.low;
  return <span className="risk-bar" title={`Risk score ${v} / 100`}><span className="track"><i style={{ width: `${v}%`, background: color }} /></span><b>{v}</b></span>;
}

// ---- Findings ---------------------------------------------------------------------------------

function Findings({ onCve }: { onCve: (c: string) => void }) {
  const { query } = useRouter();
  const { can } = useAuth();
  const [status, setStatus] = useState(query.get('status') ?? 'active');
  const [source, setSource] = useState(query.get('source') ?? '');
  const [assignee, setAssignee] = useState(query.get('assignee') ?? '');
  const vendor = query.get('vendor');
  const product = query.get('product');
  const [open, setOpen] = useState<number | null>(null);
  const [wf, setWf] = useState<'assign' | 'ticket' | 'note' | null>(null);
  const [priority, setPriority] = useState(query.get('priority') ?? '');
  const [severity, setSeverity] = useState(query.get('severity') ?? '');
  const [kev, setKev] = useState(query.get('kev') === 'true');
  const [overdue, setOverdue] = useState(query.get('overdue') === 'true');
  const assetId = query.get('assetId');
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(q);
  const [sort, setSort] = useState(query.get('sort') ?? 'risk');
  const [page, setPage] = useState(1);
  const [picked, setPicked] = useState<number[]>([]);
  const [exc, setExc] = useState<'accepted' | 'false_positive' | 'open' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const pageSize = 50;
  const filters = {
    status, priority: priority || undefined, severity: severity || undefined, kev: kev || undefined, overdue: overdue || undefined, q: search || undefined,
    assetId: assetId || undefined, sort, source: source || undefined, assignee: assignee || undefined, vendor: vendor || undefined, product: product || undefined,
  };
  const deps = [status, priority, severity, kev, overdue, search, sort, page, assetId, source, assignee, vendor, product];
  const data = useApi(() => api.get<Paged<Finding>>('/vulnerabilities/findings', { ...filters, page, pageSize }), deps);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { setPicked([]); }, deps);
  const items = data.data?.items ?? [];
  const canExcept = can('vuln.except');
  const canManage = can('vuln.manage');
  const canPick = canExcept || canManage;
  const now = Date.now();

  async function workflow(action: 'start') {
    try {
      const r = await api.post<{ updated: number }>('/vulnerabilities/findings/workflow', { ids: picked, action });
      setMsg(`${r.updated} finding(s) moved to In progress${r.updated < picked.length ? ' (the others were not open)' : ''}.`);
      setPicked([]); data.reload();
    } catch (e) { setMsg(errorText(e)); }
  }

  async function exportXlsx() {
    const f = [{ column: 'status', op: status === 'active' ? 'in' : 'eq', value: status === 'all' ? undefined : status === 'active' ? 'open,in_progress' : status }].filter((x) => x.value);
    if (source) f.push({ column: 'source', op: 'eq', value: source });
    if (priority) f.push({ column: 'priority', op: 'eq', value: priority });
    if (severity) f.push({ column: 'severity', op: 'eq', value: severity });
    if (kev) f.push({ column: 'kev', op: 'eq', value: 'yes' });
    if (overdue) f.push({ column: 'overdue', op: 'eq', value: 'yes' });
    try {
      await api.downloadJson('/reports/export', {
        dataset: 'vulnerabilities', format: 'xlsx', name: 'Vulnerability findings',
        definition: { columns: ['hostname', 'cve', 'title', 'severity', 'cvss', 'priority', 'risk', 'exploited', 'epss', 'source', 'software', 'version', 'fixed_version', 'kb', 'status', 'assignee', 'ticket', 'first_seen', 'due_at'], filters: f, sort: [{ column: 'risk', dir: 'desc' }] },
      }, 'vulnerabilities.xlsx');
    } catch (e) { setMsg(errorText(e)); }
  }

  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search CVE, software, device, KB, ticket…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search findings" />
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Status">
          <option value="active">Open + in progress</option><option value="open">Open</option><option value="in_progress">In progress</option><option value="accepted">Risk accepted</option>
          <option value="false_positive">False positive</option><option value="fixed">Fixed</option><option value="all">All</option>
        </select>
        <select className="in" value={source} onChange={(e) => { setSource(e.target.value); setPage(1); }} aria-label="Found in">
          <option value="">Windows and applications</option><option value="os">Windows (OS)</option><option value="software">Applications</option>
        </select>
        <select className="in" value={assignee} onChange={(e) => { setAssignee(e.target.value); setPage(1); }} aria-label="Assignee">
          <option value="">Anyone</option><option value="me">Assigned to me</option><option value="none">Unassigned</option>
        </select>
        <select className="in" value={priority} onChange={(e) => { setPriority(e.target.value); setPage(1); }} aria-label="Priority">
          <option value="">Any priority</option>{['P1', 'P2', 'P3', 'P4'].map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
        <select className="in" value={severity} onChange={(e) => { setSeverity(e.target.value); setPage(1); }} aria-label="Severity">
          <option value="">Any severity</option>{['critical', 'high', 'medium', 'low'].map((s) => <option key={s} value={s}>{cap(s)}</option>)}
        </select>
        <label className="row-check"><input type="checkbox" checked={kev} onChange={(e) => { setKev(e.target.checked); setPage(1); }} /> Known exploited</label>
        <label className="row-check"><input type="checkbox" checked={overdue} onChange={(e) => { setOverdue(e.target.checked); setPage(1); }} /> Overdue</label>
        <select className="in" value={sort} onChange={(e) => setSort(e.target.value)} aria-label="Sort">
          <option value="risk">Sort: risk</option><option value="due">Sort: due date</option><option value="cvss">Sort: CVSS</option><option value="host">Sort: device</option><option value="first">Sort: newest</option><option value="age">Sort: oldest</option>
        </select>
        <button className="btn" type="submit">Search</button>
        {can('reports.export') && <button className="btn" type="button" onClick={() => void exportXlsx()}><Icon name="down" size={14} /> Excel</button>}
      </form>
      {assetId && <Callout tone="info" icon="info">Showing one device. <Link to="/vulnerabilities?tab=findings">Show all devices</Link></Callout>}
      {product && <Callout tone="info" icon="info">Showing {product}. <Link to="/vulnerabilities?tab=findings">Show everything</Link></Callout>}
      {msg && <Callout tone="info" icon="info">{msg}</Callout>}
      <ErrorBox error={data.error} />
      {canPick && picked.length > 0 && <div className="toolbar-row">
        <b>{picked.length} selected</b>
        {canManage && <>
          <button className="btn sm primary" onClick={() => void workflow('start')}>Start remediation</button>
          <button className="btn sm" onClick={() => setWf('assign')}>Assign…</button>
          <button className="btn sm" onClick={() => setWf('ticket')}>Ticket…</button>
          <button className="btn sm" onClick={() => setWf('note')}>Add note…</button>
        </>}
        {canExcept && <>
          {status !== 'accepted' && <button className="btn sm" onClick={() => setExc('accepted')}>Accept risk…</button>}
          {status !== 'false_positive' && <button className="btn sm" onClick={() => setExc('false_positive')}>Mark false positive…</button>}
          {status !== 'open' && status !== 'active' && <button className="btn sm" onClick={() => setExc('open')}>Re-open</button>}
        </>}
        <button className="btn sm" onClick={() => setPicked([])}>Clear</button>
      </div>}
      <Panel flush>
        <DataTable rows={items} empty={data.loading ? 'Loading…' : status === 'open' ? 'No open findings match.' : 'No findings match.'} columns={[
          ...(canPick ? [{
            key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all on this page" checked={items.length > 0 && items.filter((f) => f.status !== 'fixed').every((f) => picked.includes(f.id))}
              onChange={(e) => setPicked(e.target.checked ? items.filter((f) => f.status !== 'fixed').map((f) => f.id) : [])} />,
            render: (f: Finding) => f.status === 'fixed' ? null : <input type="checkbox" className="row-sel" aria-label={`Select ${f.cve} on ${f.hostname}`} checked={picked.includes(f.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, f.id] : p.filter((x) => x !== f.id))} />,
          }] : []),
          { key: 'p', label: 'Priority', render: (f) => <span title={PRIORITY_HELP[f.priority]}><Pill tone={prioTone(f.priority)}>{f.priority}</Pill></span> },
          { key: 'r', label: 'Risk', render: (f) => <RiskBar v={f.risk} /> },
          { key: 'c', label: 'CVE', className: 'wrap', render: (f) => <>
            <button className="linkish mono" onClick={() => setOpen(f.id)}>{f.cve}</button> {f.exploited && <Pill tone="crit">{f.kev ? 'KEV' : 'Exploited'}</Pill>}
            {f.summary && <div className="faint clamp-2" style={{ fontSize: 11.5, maxWidth: 380 }} title={f.summary}>{f.summary}</div>}
          </> },
          { key: 's', label: 'CVSS', render: (f) => <Pill tone={sevTone(f.severity)}>{cvssText(f.cvss)} {cap(f.severity)}</Pill> },
          { key: 'e', label: 'EPSS', render: (f) => pct(f.epss) },
          { key: 'h', label: 'Device', render: (f) => <><Link to={`/assets/${f.assetId}`}>{f.hostname ?? f.assetId}</Link><div className="faint" style={{ fontSize: 11.5 }}>{f.os ?? ''}</div></> },
          { key: 'sw', label: 'Software / Windows', className: 'wrap', render: (f) => f.source === 'os'
            ? <>{f.kb ? <><b>KB{f.kb}</b> missing</> : 'Windows update missing'}<div className="faint mono" style={{ fontSize: 11.5 }}>build {f.version} → {f.fixedVersion}</div></>
            : <>{f.software} <span className="mono faint">{f.version}</span>{f.fixedVersion && <div style={{ fontSize: 11.5 }}>Fixed in <b>{f.fixedVersion}</b></div>}</> },
          { key: 'd', label: status === 'fixed' ? 'Fixed' : 'Due', render: (f) => f.status === 'fixed' ? fmt.day(f.fixedAt)
            : f.status === 'open' || f.status === 'in_progress' ? <span style={{ color: f.dueAt && new Date(f.dueAt).getTime() < now ? 'var(--crit)' : undefined, fontWeight: f.dueAt && new Date(f.dueAt).getTime() < now ? 600 : undefined }}>{fmt.day(f.dueAt)}</span>
            : <span title={`${f.exceptionReason ?? ''}${f.exceptionBy ? ` — ${f.exceptionBy}` : ''}`}><Pill tone="warn">{f.status === 'accepted' ? `Accepted to ${fmt.day(f.exceptionUntil)}` : 'False positive'}</Pill></span> },
          { key: 'w', label: 'Status', className: 'wrap', render: (f) => <>
            <Pill tone={statusTone(f.status)}>{STATUS_LABEL[f.status] ?? f.status}</Pill>
            {(f.assignee || f.ticket) && <div className="faint" style={{ fontSize: 11.5 }}>{[f.assignee, f.ticket].filter(Boolean).join(' · ')}</div>}
          </> },
          { key: 'f', label: 'First seen', render: (f) => fmt.day(f.firstSeenAt) },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
      {exc && <ExceptionDialog ids={picked} initial={exc} onClose={() => setExc(null)} onSaved={(n) => { setExc(null); setPicked([]); setMsg(`${n} finding(s) updated.`); data.reload(); }} />}
      {wf && <WorkflowDialog ids={picked} action={wf} onClose={() => setWf(null)} onSaved={(n) => { setWf(null); setPicked([]); setMsg(`${n} finding(s) updated.`); data.reload(); }} />}
      {open != null && <FindingDrawer id={open} onClose={() => setOpen(null)} onChanged={() => data.reload()} onCve={onCve} />}
    </>
  );
}

// ---- Remediation plan -------------------------------------------------------------------------

/** Open findings grouped by the action that closes them, ranked by priority and the risk removed. */
function RemediationPlan() {
  const [source, setSource] = useState('');
  const plan = useApi(() => api.get<{ items: Remediation[]; total: number }>('/vulnerabilities/remediations', { source: source || undefined }), [source]);
  const items = plan.data?.items ?? [];
  const maxRisk = Math.max(1, ...items.map((x) => x.riskReduction));
  const totals = items.reduce((a, x) => ({ findings: a.findings + x.findings, risk: a.risk + x.riskReduction }), { findings: 0, risk: 0 });
  const top5 = items.slice(0, 5).reduce((a, x) => a + x.riskReduction, 0);
  return (
    <>
      <Callout tone="info" icon="info">
        Each line is one action. Doing it closes every listed finding on every device where it applies; findings close automatically at the next inventory.
        {items.length > 5 && totals.risk > 0 && <> The first five actions remove <b>{Math.round((100 * top5) / totals.risk)}%</b> of the total risk.</>}
      </Callout>
      <div className="toolbar-row">
        <select className="in" value={source} onChange={(e) => setSource(e.target.value)} aria-label="Found in">
          <option value="">Windows and applications</option><option value="os">Windows updates</option><option value="software">Application updates</option>
        </select>
        <span className="muted">{items.length.toLocaleString()} action(s) · {totals.findings.toLocaleString()} open finding(s)</span>
      </div>
      <ErrorBox error={plan.error} />
      <Panel flush>
        <DataTable rows={items.map((x, i) => ({ ...x, id: `${i}` }))} empty={plan.loading ? 'Loading…' : 'Nothing to remediate.'} columns={[
          { key: 'p', label: 'Priority', render: (x) => <span title={PRIORITY_HELP[x.topPriority]}><Pill tone={prioTone(x.topPriority)}>{x.topPriority}</Pill></span> },
          { key: 'a', label: 'Action', className: 'wrap', render: (x) => <>
            <b>{x.action}</b>
            <div className="faint" style={{ fontSize: 11.5 }}>
              {x.source === 'os' ? 'Windows update' : `${x.vendor}:${x.product}`}
              {x.kb && x.kbUrl && <> · <a href={x.kbUrl} target="_blank" rel="noreferrer noopener">KB{x.kb} in the Microsoft Update Catalog</a></>}
              {x.withoutFix > 0 && x.targetVersion && <> · {x.withoutFix} finding(s) without a published fix</>}
            </div>
          </> },
          { key: 'd', label: 'Devices', render: (x) => x.devices.toLocaleString() },
          { key: 'f', label: 'Findings', render: (x) => <Link to={`/vulnerabilities?tab=findings&source=${x.source}&vendor=${encodeURIComponent(x.vendor)}&product=${encodeURIComponent(x.product)}`}>{x.findings.toLocaleString()}</Link> },
          { key: 'c', label: 'CVEs', render: (x) => x.cves.toLocaleString() },
          { key: 'e', label: 'Exploited', render: (x) => x.exploited ? <Pill tone="crit">{x.exploited}</Pill> : '' },
          { key: 'o', label: 'Overdue', render: (x) => x.overdue ? <span style={{ color: 'var(--crit)', fontWeight: 600 }}>{x.overdue}</span> : '0' },
          { key: 'r', label: 'Risk removed', render: (x) => <span className="risk-bar" title={`Sum of the risk scores of the findings it closes: ${x.riskReduction}`}>
            <span className="track"><i style={{ width: `${(100 * x.riskReduction) / maxRisk}%`, background: PRIORITY_COLOR[x.topPriority] }} /></span><b>{x.riskReduction.toLocaleString()}</b></span> },
        ]} />
      </Panel>
    </>
  );
}

// ---- Software → NVD product mapping ---------------------------------------------------------

function Products() {
  const { can } = useAuth();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<ProductMapping | null>(null);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<ProductMapping>>('/vulnerabilities/products', { q: search, status, page, pageSize }), [search, status, page]);
  const manage = can('vuln.manage');
  return (
    <>
      <Callout tone="info" icon="info">Each installed application is matched to its NVD product name (CPE vendor:product). Well-known products are mapped automatically; map the rest here so their vulnerabilities are found, or ignore internal software that has no CVEs.</Callout>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search software, vendor or product…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search software mappings" />
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Mapping status">
          <option value="">All</option><option value="unmapped">Not mapped</option><option value="auto">Mapped automatically</option><option value="manual">Mapped manually</option><option value="ignored">Ignored</option>
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data?.items ?? []} empty={data.loading ? 'Loading…' : 'No installed software yet.'} columns={[
          { key: 's', label: 'Installed software', className: 'wrap', render: (m) => <><b>{m.software}</b>{m.publisher && <div className="faint" style={{ fontSize: 11.5 }}>{m.publisher}</div>}</> },
          { key: 'm', label: 'NVD product', render: (m) => m.vendor ? <span className="mono">{m.vendor}:{m.product}</span> : <span className="muted">—</span> },
          { key: 'st', label: 'Status', render: (m) => <Pill tone={m.status === 'unmapped' ? 'warn' : m.status === 'ignored' ? '' : 'ok'}>{({ auto: 'Automatic', manual: 'Manual', ignored: 'Ignored', unmapped: 'Not mapped' } as const)[m.status]}</Pill> },
          { key: 'c', label: 'Known CVEs', render: (m) => m.vendor ? (m.syncedAt ? m.cves.toLocaleString() : <span className="faint" title="Downloaded at the next feed update">pending</span>) : '—' },
          { key: 'o', label: 'Open findings', render: (m) => m.openFindings ? <Link to={`/vulnerabilities?tab=findings&q=${encodeURIComponent(m.software)}`}>{m.openFindings.toLocaleString()}</Link> : '0' },
          { key: 'u', label: 'Changed', render: (m) => m.updatedBy ? <span title={fmt.date(m.updatedAt)}>{m.updatedBy}</span> : <span className="faint">{fmt.day(m.updatedAt)}</span> },
          { key: 'a', label: '', render: (m) => manage ? <button className="btn sm" onClick={() => setEditing(m)}>{m.status === 'unmapped' ? 'Map…' : 'Change…'}</button> : null },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
      {editing && <MappingDialog m={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); data.reload(); }} />}
    </>
  );
}

function MappingDialog({ m, onClose, onSaved }: { m: ProductMapping; onClose: () => void; onSaved: () => void }) {
  const [vendor, setVendor] = useState(m.vendor ?? '');
  const [product, setProduct] = useState(m.product ?? '');
  const [q, setQ] = useState(m.software.replace(/\s+\(?(x64|x86|64-bit|32-bit)\)?/gi, '').replace(/\s+[\d.]+$/, '').slice(0, 60));
  const [hits, setHits] = useState<{ vendor: string; product: string; title: string }[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function find() {
    setSearching(true); setError(null);
    try { setHits(await api.get('/vulnerabilities/cpe-search', { q })); } catch (e) { setError(errorText(e)); } finally { setSearching(false); }
  }
  async function save(status: string) {
    setBusy(true); setError(null);
    try { await api.put(`/vulnerabilities/products/${m.id}`, { status, vendor, product }); onSaved(); } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="map-title">
      <div className="modal-card" style={{ width: 'min(640px, 100%)' }}>
        <header><h2 id="map-title">Map "{m.software}"</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <form className="toolbar-row" style={{ margin: 0 }} onSubmit={(e) => { e.preventDefault(); void find(); }}>
            <input className="in grow" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search NVD products" />
            <button className="btn" type="submit" disabled={searching || q.trim().length < 3}><Icon name="search" size={14} /> {searching ? 'Searching…' : 'Search NVD'}</button>
          </form>
          {hits && (hits.length === 0 ? <p className="muted" style={{ margin: 0 }}>No NVD products found. Try a shorter name or enter the CPE vendor and product below.</p> :
            <div className="check-list" style={{ maxHeight: 220 }}>
              {hits.map((h) => <label key={`${h.vendor}:${h.product}`}><input type="radio" name="cpe" checked={vendor === h.vendor && product === h.product} onChange={() => { setVendor(h.vendor); setProduct(h.product); }} />
                <span><span className="mono">{h.vendor}:{h.product}</span> <span className="muted">· {h.title}</span></span></label>)}
            </div>)}
          <div className="form-grid">
            <label>CPE vendor <input className="in mono" value={vendor} onChange={(e) => setVendor(e.target.value)} placeholder="e.g. google" /></label>
            <label>CPE product <input className="in mono" value={product} onChange={(e) => setProduct(e.target.value)} placeholder="e.g. chrome" /></label>
          </div>
          <small className="muted">As in <span className="mono">cpe:2.3:a:<b>vendor</b>:<b>product</b>:…</span>. CVEs for a new mapping are downloaded at the next feed update.</small>
        </div>
        <footer>
          <button type="button" className="btn" onClick={() => void save('ignored')} disabled={busy} style={{ marginRight: 'auto' }}>Ignore (no CVEs)</button>
          {m.status !== 'auto' && <button type="button" className="btn" onClick={() => void save('auto')} disabled={busy}>Use automatic</button>}
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="button" className="btn primary" onClick={() => void save('manual')} disabled={busy || !vendor.trim() || !product.trim()}>Save mapping</button>
        </footer>
      </div>
    </div>
  );
}

// ---- Feeds, imports and settings -----------------------------------------------------------

const FEEDS: Record<string, { name: string; desc: string; file: string }> = {
  msrc: { name: 'Microsoft (Windows)', desc: 'Monthly security updates: Windows CVEs, KBs and fixed builds', file: 'CVRF JSON for one month, e.g. https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/2026-Oct saved with Accept: application/json' },
  nvd: { name: 'NVD (NIST)', desc: 'CVE descriptions, CVSS scores and affected product versions', file: 'NVD CVE API 2.0 JSON (or the JSON returned for a product search), optionally .gz' },
  kev: { name: 'CISA KEV', desc: 'Known Exploited Vulnerabilities catalog', file: 'known_exploited_vulnerabilities.json' },
  epss: { name: 'FIRST EPSS', desc: 'Probability of exploitation in the next 30 days', file: 'epss_scores-YYYY-MM-DD.csv.gz (or .csv)' },
};

function Feeds() {
  const { can } = useAuth();
  const manage = can('vuln.manage');
  const o = useApi(() => api.get<VulnOverview>('/vulnerabilities/overview'), []);
  const imports = useApi(() => api.get<VulnImport[]>('/vulnerabilities/imports'), []);
  const settings = useApi(() => api.get<VulnSettings>('/vulnerabilities/settings'), []);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit' | 'ok'; text: string } | null>(null);
  const [source, setSource] = useState('kev');
  const fileRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const pending = (imports.data ?? []).some((i) => i.status === 'queued' || i.status === 'running');
  useEffect(() => {
    if (!pending) return;
    const t = setTimeout(() => { imports.reload(); o.reload(); }, 5000);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pending, imports.data]);

  async function act(path: string) {
    setMsg(null);
    try { const r = await api.post<{ message: string }>(path); setMsg({ tone: 'info', text: r.message }); }
    catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }
  async function upload() {
    const f = fileRef.current?.files?.[0];
    if (!f) return;
    setUploading(true); setMsg(null);
    try {
      const r = await api.upload<{ message: string }>('/vulnerabilities/import', { source, name: f.name }, f);
      setMsg({ tone: 'info', text: r.message });
      if (fileRef.current) fileRef.current.value = '';
      imports.reload();
    } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setUploading(false); }
  }

  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text} {msg.tone === 'info' && <Link to="/activity">Activity log</Link>}</Callout>}
      <Panel title="Vulnerability data" sub="Updated daily when automatic updates are on" right={manage && <div style={{ display: 'flex', gap: 6 }}>
        <button className="btn sm" onClick={() => void act('/vulnerabilities/evaluate')}>Re-evaluate now</button>
        <button className="btn sm primary" onClick={() => void act('/vulnerabilities/sync')}><Icon name="retry" size={12} /> Update now</button>
      </div>} flush>
        <DataTable rows={['msrc', 'nvd', 'kev', 'epss', 'evaluate'].map((s) => ({ id: s, ...(o.data?.feeds.find((f) => f.source === s) ?? { source: s, lastRunAt: null, lastOkAt: null, items: null, message: null }) }))} columns={[
          { key: 'n', label: 'Source', className: 'wrap', render: (f) => f.source === 'evaluate'
            ? <><b>Evaluation</b><div className="faint" style={{ fontSize: 11.5 }}>Devices checked against the data (every 30 minutes)</div></>
            : <><b>{FEEDS[f.source]?.name ?? f.source}</b><div className="faint" style={{ fontSize: 11.5 }}>{FEEDS[f.source]?.desc}</div></> },
          { key: 'ok', label: 'Last successful update', render: (f) => f.lastOkAt ? <span title={fmt.date(f.lastOkAt)}>{fmt.ago(f.lastOkAt)}</span> : <Pill tone="warn">Never</Pill> },
          { key: 'i', label: 'Records', render: (f) => f.items?.toLocaleString() ?? '—' },
          { key: 'm', label: 'Last result', className: 'wrap', render: (f) => <span className={f.lastRunAt && f.lastOkAt !== f.lastRunAt ? '' : 'faint'}>{f.message ?? '—'}</span> },
        ]} />
      </Panel>

      <div className="grid-2" style={{ marginTop: 14 }}>
        {manage && <Panel title="Import from file" sub="For servers without internet access">
          <div className="form-v">
            <p className="muted" style={{ margin: 0, fontSize: 13 }}>Download the files on a connected computer and upload them here. Findings are re-evaluated after each import.</p>
            <label>Source
              <select className="in" value={source} onChange={(e) => setSource(e.target.value)}>
                {Object.entries(FEEDS).map(([k, f]) => <option key={k} value={k}>{f.name}</option>)}
              </select>
              <small className="muted">Expected: {FEEDS[source]!.file}</small>
            </label>
            <label>File <input ref={fileRef} className="in" type="file" accept=".json,.gz,.csv" /></label>
            <div><button className="btn primary" disabled={uploading} onClick={() => void upload()}><Icon name="upload" size={14} /> {uploading ? 'Uploading…' : 'Upload and import'}</button></div>
          </div>
        </Panel>}
        {settings.data && <SettingsPanel s={settings.data} manage={manage} onSaved={() => { settings.reload(); setMsg({ tone: 'ok', text: 'Settings saved. SLA changes apply to findings evaluated from now on.' }); }} />}
      </div>

      <Panel title="Recent imports" flush>
        <ErrorBox error={imports.error} />
        <DataTable rows={imports.data ?? []} empty="No files imported." columns={[
          { key: 't', label: 'Uploaded', render: (i) => fmt.date(i.createdAt) },
          { key: 's', label: 'Source', render: (i) => FEEDS[i.source]?.name ?? i.source },
          { key: 'f', label: 'File', className: 'wrap', render: (i) => <>{i.fileName} <span className="faint">({fmt.bytes(i.size)})</span></> },
          { key: 'st', label: 'Status', render: (i) => <Pill tone={i.status === 'done' ? 'ok' : i.status === 'failed' ? 'crit' : 'warn'}>{fmt.label(i.status)}</Pill> },
          { key: 'm', label: 'Result', className: 'wrap', render: (i) => i.message ?? '—' },
          { key: 'b', label: 'By', render: (i) => i.by ?? '—' },
        ]} />
      </Panel>
    </>
  );
}

function SettingsPanel({ s, manage, onSaved }: { s: VulnSettings; manage: boolean; onSaved: () => void }) {
  const [autoSync, setAutoSync] = useState(s.autoSync);
  const [sla, setSla] = useState(s.slaDays);
  const [minCvss, setMinCvss] = useState(String(s.minCvss));
  const [key, setKey] = useState('');
  const [months, setMonths] = useState(String(s.msrcMonths));
  const [weight, setWeight] = useState(s.criticalityWeight);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(clearKey = false) {
    setBusy(true); setError(null);
    try {
      await api.put('/vulnerabilities/settings', { autoSync, slaDays: sla, minCvss: Number(minCvss), nvdApiKey: key.trim() || null, clearNvdApiKey: clearKey || null, msrcMonths: Number(months), criticalityWeight: weight });
      setKey('');
      onSaved();
    } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  }
  return (
    <Panel title="Settings">
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <label className="row-check"><input type="checkbox" disabled={!manage} checked={autoSync} onChange={(e) => setAutoSync(e.target.checked)} /> Update Microsoft, NVD, KEV and EPSS data automatically every day</label>
        <label>Windows: monthly security updates to load
          <input className="in" type="number" min={1} max={36} disabled={!manage} value={months} onChange={(e) => setMonths(e.target.value)} style={{ maxWidth: 120 }} />
          <small className="muted">A device missing older updates is still reported for every month loaded; 12 covers a year.</small>
        </label>
        <label className="row-check"><input type="checkbox" disabled={!manage} checked={weight} onChange={(e) => setWeight(e.target.checked)} /> Weight the risk score by the device's business criticality (Critical ×1.25, High ×1.1, Low ×0.8)</label>
        <div>
          <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Remediation SLA (days from detection)</div>
          <div className="sla-grid">
            {(['critical', 'high', 'medium', 'low'] as const).map((k) => <label key={k}><span><span className="dot" style={{ background: SEVERITY_COLOR[k] }} />{cap(k)}</span>
              <input className="in" type="number" min={1} max={3650} disabled={!manage} value={sla[k]} onChange={(e) => setSla({ ...sla, [k]: Number(e.target.value) })} /></label>)}
          </div>
          <small className="muted">Known-exploited CVEs use the CISA due date when it is earlier.</small>
        </div>
        <label>Ignore CVEs below CVSS
          <input className="in" type="number" min={0} max={10} step={0.1} disabled={!manage} value={minCvss} onChange={(e) => setMinCvss(e.target.value)} style={{ maxWidth: 120 }} />
        </label>
        <label><span>NVD API key {s.nvdApiKeySet ? <Pill tone="ok">Set</Pill> : <Pill>Not set</Pill>}</span>
          <input className="in" type="password" autoComplete="off" disabled={!manage} value={key} onChange={(e) => setKey(e.target.value)} placeholder={s.nvdApiKeySet ? 'Enter a new key to replace it' : 'Optional — makes updates about 10× faster'} />
          <small className="muted">Free from nvd.nist.gov/developers/request-an-api-key. Stored encrypted; never shown again.</small>
        </label>
        {manage && <div style={{ display: 'flex', gap: 8 }}>
          <button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save settings'}</button>
          {s.nvdApiKeySet && <button className="btn" disabled={busy} onClick={() => void save(true)}>Remove API key</button>}
        </div>}
      </div>
    </Panel>
  );
}
