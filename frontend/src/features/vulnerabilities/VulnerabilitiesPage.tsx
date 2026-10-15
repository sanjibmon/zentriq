import { useEffect, useRef, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart, HBars, TrendArea } from '../../components/charts';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import {
  cap, errorText, pct, prioTone, PRIORITY_COLOR, PRIORITY_HELP, PRIORITY_SHORT, SEVERITY_COLOR, sevTone,
  type Finding, type ProductMapping, type VulnImport, type VulnOverview, type VulnSettings, cvssText } from './types';
import { CveDialog, ExceptionDialog } from './VulnDialogs';

type Tab = 'overview' | 'findings' | 'products' | 'feeds';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['findings', 'Findings'], ['products', 'Software mapping'], ['feeds', 'Feeds & settings']];

export function VulnerabilitiesPage() {
  const { query, navigate } = useRouter();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const [cve, setCve] = useState<string | null>(null);
  const go = (t: Tab, extra = '') => navigate(`/vulnerabilities?tab=${t}${extra}`, true);
  return (
    <>
      <PageHead title="Vulnerability Management" desc="Known CVEs in installed software, prioritised by exploitability." />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => go(t)}>{l}</button>)}
      </div>
      {tab === 'overview' && <Overview onCve={setCve} go={go} />}
      {tab === 'findings' && <Findings onCve={setCve} />}
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
      {noData && <Callout tone="warn" icon="info">No vulnerability data yet. Open <button className="linkish" onClick={() => go('feeds')}>Feeds &amp; settings</button> to update from NVD, CISA KEV and EPSS (or import the files on an offline server). Findings appear once the data is in and installed software is evaluated.</Callout>}
      {!noData && nvd?.lastOkAt && Date.now() - new Date(nvd.lastOkAt).getTime() > 7 * 86400000 && <Callout tone="warn" icon="alert">NVD data was last updated {fmt.ago(nvd.lastOkAt)}. New vulnerabilities are missed until the feeds are updated.</Callout>}
      <Kpis items={[
        { label: 'Open findings', value: k.open.toLocaleString(), sub: `${k.cves.toLocaleString()} distinct CVEs`, to: '/vulnerabilities?tab=findings' },
        { label: 'Critical', value: <span style={{ color: k.critical ? SEVERITY_COLOR.critical : undefined }}>{k.critical.toLocaleString()}</span>, sub: `${k.high.toLocaleString()} high`, to: '/vulnerabilities?tab=findings&severity=critical' },
        { label: 'Known exploited', value: <span style={{ color: k.kev ? SEVERITY_COLOR.critical : undefined }}>{k.kev.toLocaleString()}</span>, sub: 'on the CISA KEV list', to: '/vulnerabilities?tab=findings&kev=true' },
        { label: 'Overdue', value: k.overdue.toLocaleString(), sub: 'past SLA due date', to: '/vulnerabilities?tab=findings&overdue=true' },
        { label: 'Affected devices', value: `${k.affectedAssets.toLocaleString()} / ${k.assets.toLocaleString()}`, sub: k.assets ? `${Math.round((100 * k.affectedAssets) / k.assets)}% of active devices` : '' },
        { label: 'Remediation (90 d)', value: `${k.mttrDays} d`, sub: `mean time to fix · ${k.slaMet}% within SLA` },
      ]} />
      <div className="grid-3">
        <Panel title="Open by severity">
          <CategoryChart kind="donut" centerLabel="open" data={o.data.bySeverity.map((s) => ({ key: s.key, label: cap(s.key), value: s.count, color: SEVERITY_COLOR[s.key] }))}
            onSelect={(s) => go('findings', `&severity=${s.key}`)} />
        </Panel>
        <Panel title="Open by priority" sub="Risk-based: CVSS + KEV + EPSS">
          <HBars data={o.data.byPriority.map((p) => ({ key: p.key, label: PRIORITY_SHORT[p.key] ?? p.key, value: p.count, color: PRIORITY_COLOR[p.key] }))}
            onSelect={(s) => go('findings', `&priority=${s.key}`)} />
        </Panel>
        <Panel title="Most vulnerable software">
          <HBars data={o.data.topSoftware.map((s, i) => ({ key: `${i}`, label: s.key, value: s.count }))} onSelect={(s) => go('findings', `&q=${encodeURIComponent(s.label)}`)} />
        </Panel>
      </div>
      <Panel title="Findings over the last 30 days" sub="Line: open findings · bars: newly detected">
        <TrendArea unit="open findings" addedLabel="new" points={o.data.trend.map((t) => ({ label: new Date(`${t.day}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }), total: t.open, added: t.opened }))} />
      </Panel>
      <div className="grid-2" style={{ marginTop: 14 }}>
        <Panel title="Top CVEs to fix" flush>
          <DataTable rows={o.data.topCves.map((c) => ({ ...c, id: c.cve }))} empty="No open findings." columns={[
            { key: 'c', label: 'CVE', render: (c) => <button className="linkish mono" onClick={() => onCve(c.cve)}>{c.cve}</button> },
            { key: 'p', label: 'Priority', render: (c) => <Pill tone={prioTone(c.priority)}>{c.priority}</Pill> },
            { key: 's', label: 'CVSS', render: (c) => <Pill tone={sevTone(c.severity)}>{cvssText(c.cvss)}</Pill> },
            { key: 'k', label: 'KEV', render: (c) => c.kev ? <Pill tone="crit">Exploited</Pill> : '' },
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

export function RiskBar({ v }: { v: number }) {
  const color = v >= 80 ? SEVERITY_COLOR.critical : v >= 60 ? SEVERITY_COLOR.high : v >= 35 ? SEVERITY_COLOR.medium : SEVERITY_COLOR.low;
  return <span className="risk-bar" title={`Risk score ${v} / 100`}><span className="track"><i style={{ width: `${v}%`, background: color }} /></span><b>{v}</b></span>;
}

// ---- Findings ---------------------------------------------------------------------------------

function Findings({ onCve }: { onCve: (c: string) => void }) {
  const { query } = useRouter();
  const { can } = useAuth();
  const [status, setStatus] = useState(query.get('status') ?? 'open');
  const [priority, setPriority] = useState(query.get('priority') ?? '');
  const [severity, setSeverity] = useState(query.get('severity') ?? '');
  const [kev, setKev] = useState(query.get('kev') === 'true');
  const [overdue, setOverdue] = useState(query.get('overdue') === 'true');
  const assetId = query.get('assetId');
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(q);
  const [sort, setSort] = useState('risk');
  const [page, setPage] = useState(1);
  const [picked, setPicked] = useState<number[]>([]);
  const [exc, setExc] = useState<'accepted' | 'false_positive' | 'open' | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const pageSize = 50;
  const filters = { status, priority: priority || undefined, severity: severity || undefined, kev: kev || undefined, overdue: overdue || undefined, q: search || undefined, assetId: assetId || undefined, sort };
  const data = useApi(() => api.get<Paged<Finding>>('/vulnerabilities/findings', { ...filters, page, pageSize }), [status, priority, severity, kev, overdue, search, sort, page, assetId]);
  useEffect(() => { setPicked([]); }, [status, priority, severity, kev, overdue, search, sort, page, assetId]);
  const items = data.data?.items ?? [];
  const canExcept = can('vuln.except');
  const now = Date.now();

  async function exportXlsx() {
    const f = [{ column: 'status', op: 'eq', value: status === 'all' ? undefined : status }].filter((x) => x.value);
    if (priority) f.push({ column: 'priority', op: 'eq', value: priority });
    if (severity) f.push({ column: 'severity', op: 'eq', value: severity });
    if (kev) f.push({ column: 'kev', op: 'eq', value: 'yes' });
    if (overdue) f.push({ column: 'overdue', op: 'eq', value: 'yes' });
    try {
      await api.downloadJson('/reports/export', {
        dataset: 'vulnerabilities', format: 'xlsx', name: 'Vulnerability findings',
        definition: { columns: ['hostname', 'cve', 'severity', 'cvss', 'priority', 'risk', 'kev', 'epss', 'software', 'version', 'fixed_version', 'status', 'first_seen', 'due_at'], filters: f, sort: [{ column: 'risk', dir: 'desc' }] },
      }, 'vulnerabilities.xlsx');
    } catch (e) { setMsg(errorText(e)); }
  }

  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search CVE, software, device or description…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search findings" />
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Status">
          <option value="open">Open</option><option value="accepted">Risk accepted</option><option value="false_positive">False positive</option><option value="fixed">Fixed</option><option value="all">All</option>
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
          <option value="risk">Sort: risk</option><option value="due">Sort: due date</option><option value="cvss">Sort: CVSS</option><option value="host">Sort: device</option><option value="first">Sort: newest</option>
        </select>
        <button className="btn" type="submit">Search</button>
        {can('reports.export') && <button className="btn" type="button" onClick={() => void exportXlsx()}><Icon name="down" size={14} /> Excel</button>}
      </form>
      {assetId && <Callout tone="info" icon="info">Showing one device. <Link to="/vulnerabilities?tab=findings">Show all devices</Link></Callout>}
      {msg && <Callout tone="info" icon="info">{msg}</Callout>}
      <ErrorBox error={data.error} />
      {canExcept && picked.length > 0 && <div className="toolbar-row">
        <b>{picked.length} selected</b>
        {status !== 'accepted' && <button className="btn sm" onClick={() => setExc('accepted')}>Accept risk…</button>}
        {status !== 'false_positive' && <button className="btn sm" onClick={() => setExc('false_positive')}>Mark false positive…</button>}
        {status !== 'open' && <button className="btn sm" onClick={() => setExc('open')}>Re-open</button>}
        <button className="btn sm" onClick={() => setPicked([])}>Clear</button>
      </div>}
      <Panel flush>
        <DataTable rows={items} empty={data.loading ? 'Loading…' : status === 'open' ? 'No open findings match.' : 'No findings match.'} columns={[
          ...(canExcept ? [{
            key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all on this page" checked={items.length > 0 && items.filter((f) => f.status !== 'fixed').every((f) => picked.includes(f.id))}
              onChange={(e) => setPicked(e.target.checked ? items.filter((f) => f.status !== 'fixed').map((f) => f.id) : [])} />,
            render: (f: Finding) => f.status === 'fixed' ? null : <input type="checkbox" className="row-sel" aria-label={`Select ${f.cve} on ${f.hostname}`} checked={picked.includes(f.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, f.id] : p.filter((x) => x !== f.id))} />,
          }] : []),
          { key: 'p', label: 'Priority', render: (f) => <span title={PRIORITY_HELP[f.priority]}><Pill tone={prioTone(f.priority)}>{f.priority}</Pill></span> },
          { key: 'r', label: 'Risk', render: (f) => <RiskBar v={f.risk} /> },
          { key: 'c', label: 'CVE', className: 'wrap', render: (f) => <>
            <button className="linkish mono" onClick={() => onCve(f.cve)}>{f.cve}</button> {f.kev && <Pill tone="crit">KEV</Pill>}
            {f.summary && <div className="faint clamp-2" style={{ fontSize: 11.5, maxWidth: 380 }} title={f.summary}>{f.summary}</div>}
          </> },
          { key: 's', label: 'CVSS', render: (f) => <Pill tone={sevTone(f.severity)}>{cvssText(f.cvss)} {cap(f.severity)}</Pill> },
          { key: 'e', label: 'EPSS', render: (f) => pct(f.epss) },
          { key: 'h', label: 'Device', render: (f) => <><Link to={`/assets/${f.assetId}`}>{f.hostname ?? f.assetId}</Link><div className="faint" style={{ fontSize: 11.5 }}>{f.os ?? ''}</div></> },
          { key: 'sw', label: 'Software', className: 'wrap', render: (f) => <>{f.software} <span className="mono faint">{f.version}</span>{f.fixedVersion && <div style={{ fontSize: 11.5 }}>Fixed in <b>{f.fixedVersion}</b></div>}</> },
          { key: 'd', label: status === 'fixed' ? 'Fixed' : 'Due', render: (f) => f.status === 'fixed' ? fmt.day(f.fixedAt)
            : f.status === 'open' ? <span style={{ color: f.dueAt && new Date(f.dueAt).getTime() < now ? 'var(--crit)' : undefined, fontWeight: f.dueAt && new Date(f.dueAt).getTime() < now ? 600 : undefined }}>{fmt.day(f.dueAt)}</span>
            : <span title={`${f.exceptionReason ?? ''}${f.exceptionBy ? ` — ${f.exceptionBy}` : ''}`}><Pill tone="warn">{f.status === 'accepted' ? `Accepted to ${fmt.day(f.exceptionUntil)}` : 'False positive'}</Pill></span> },
          { key: 'f', label: 'First seen', render: (f) => fmt.day(f.firstSeenAt) },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
      {exc && <ExceptionDialog ids={picked} initial={exc} onClose={() => setExc(null)} onSaved={(n) => { setExc(null); setPicked([]); setMsg(`${n} finding(s) updated.`); data.reload(); }} />}
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
        <DataTable rows={['nvd', 'kev', 'epss'].map((s) => ({ id: s, ...(o.data?.feeds.find((f) => f.source === s) ?? { source: s, lastRunAt: null, lastOkAt: null, items: null, message: null }) }))} columns={[
          { key: 'n', label: 'Source', className: 'wrap', render: (f) => <><b>{FEEDS[f.source]?.name ?? f.source}</b><div className="faint" style={{ fontSize: 11.5 }}>{FEEDS[f.source]?.desc}</div></> },
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
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(clearKey = false) {
    setBusy(true); setError(null);
    try {
      await api.put('/vulnerabilities/settings', { autoSync, slaDays: sla, minCvss: Number(minCvss), nvdApiKey: key.trim() || null, clearNvdApiKey: clearKey || null });
      setKey('');
      onSaved();
    } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  }
  return (
    <Panel title="Settings">
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <label className="row-check"><input type="checkbox" disabled={!manage} checked={autoSync} onChange={(e) => setAutoSync(e.target.checked)} /> Update NVD, KEV and EPSS data automatically every day</label>
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
