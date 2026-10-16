import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { KC, LicensingStatus, Overview, Paged, Scan } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Link, useRouter } from '../../app/router';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';
import { CategoryChart, CHART_KINDS, HBars, PostureBars, TrendArea, type ChartKind, type Slice } from '../../components/charts';
import { ReportWidget } from './ReportWidget';

const TYPE_LABEL: Record<string, string> = {
  laptop: 'Laptop', desktop: 'Desktop', server: 'Physical server', virtual: 'Virtual machine', other: 'Other hardware',
  network_device: 'Network device', printer: 'Printer', iot: 'IoT', 'not inventoried': 'Not inventoried yet',
};
const COVERAGE: Record<string, [string, string]> = {
  agent: ['Zentriq Agent', 'var(--ok)'], agentless: ['Agentless (credentials)', 'var(--info)'], 'discovered only': ['Discovered only — no inventory', 'var(--warn)'],
};
const HEALTH: [string, string, string][] = [
  ['online', 'Online', 'var(--ok)'], ['stale', 'Not seen recently', 'var(--warn)'],
  ['offline', 'Offline > 7 days', 'var(--crit)'], ['never', 'Never checked in', 'var(--line-strong)'],
];
const SEEN: [string, string][] = [
  ['Online now', 'var(--ok)'], ['Last 24 hours', 'var(--info)'], ['1–7 days', 'var(--warn)'], ['7–30 days', 'var(--crit)'], ['Over 30 days', 'var(--crit)'],
];
const RESULT_LABEL: Record<string, string> = {
  success: 'Inventoried', reachable_unauthenticated: 'Reachable, no credential', inventory_unavailable: 'Up, ports blocked',
  auth_failed: 'Credential rejected', inventory_failed: 'Collection failed', skipped: 'Skipped', excluded: 'Excluded', cancelled: 'Cancelled',
};
const RESULT_COLOR: Record<string, string> = { success: 'var(--ok)', reachable_unauthenticated: 'var(--warn)', inventory_unavailable: 'var(--info)', auth_failed: 'var(--crit)', inventory_failed: 'var(--crit)' };
const ORDER: Record<string, string[]> = {
  free: ['Critical (< 10% free)', 'Low (10–25% free)', 'OK (25–50% free)', 'Plenty (> 50% free)'],
};
const FREE_COLOR: Record<string, string> = { 'Critical (< 10% free)': 'var(--crit)', 'Low (10–25% free)': 'var(--warn)', 'OK (25–50% free)': 'var(--info)', 'Plenty (> 50% free)': 'var(--ok)' };
const POSTURE_FACET: Record<string, string> = { 'Disk encryption': 'encryption', 'Firewall on': 'firewall', 'Secure Boot': 'secureboot', 'TPM present': 'tpm', 'Antivirus active': 'antivirus' };

const slices = (rows: KC[], label: (k: string) => string = fmt.label, color?: (k: string) => string | undefined): Slice[] =>
  rows.map((r) => ({ key: r.key, label: label(r.key), value: r.count, color: color?.(r.key) }));
const ordered = (rows: KC[], order: string[]) => order.map((k) => ({ key: k, count: rows.find((r) => r.key === k)?.count ?? 0 }));
/** Actual sizes ("8 GB", "1.9 TB", "12 cores") in ascending order. */
const bySize = (rows: KC[]) => [...rows].sort((a, b) => size(a.key) - size(b.key));
function size(k: string) { const n = parseFloat(k); return Number.isNaN(n) ? Number.MAX_VALUE : /TB/i.test(k) ? n * 1024 : n; }

function Hero({ label, value, sub, to, meter, tone }: { label: string; value: string; sub?: string; to?: string; meter?: number; tone?: 'crit' }) {
  const body = (
    <>
      <span className="k">{label}</span>
      <span className="v" style={tone === 'crit' ? { color: 'var(--crit)' } : undefined}>{value}</span>
      <span className="meter" aria-hidden="true" style={meter == null ? { visibility: 'hidden' } : undefined}><i style={{ width: `${Math.min(100, Math.max(0, meter ?? 0))}%` }} /></span>
      <span className="s">{sub ?? ''}</span>
    </>
  );
  return to ? <Link to={to} className="hero-card">{body}</Link> : <div className="hero-card">{body}</div>;
}

interface VulnSummary { bySeverity: KC[]; aging: { bucket: string; critical: number; high: number; medium: number; low: number }[] }

// ---- Widget registry ---------------------------------------------------------------------------------------
interface WidgetDef { id: string; title: string; wide?: boolean; full?: boolean; defaultOn: boolean; description: string; chart?: ChartKind }
const WIDGETS: WidgetDef[] = [
  { id: 'kpi.tiles', title: 'Summary tiles', full: true, defaultOn: true, description: 'Assets, servers, endpoints, coverage, offline devices, software' },
  { id: 'assets.category', chart: 'donut', title: 'Assets by category', defaultOn: true, description: 'Endpoints, servers, network devices · click to list those assets' },
  { id: 'assets.type', chart: 'donut', title: 'Device type', defaultOn: true, description: 'Laptop, desktop, physical server, virtual machine' },
  { id: 'assets.coverage', chart: 'donut', title: 'How assets are inventoried', defaultOn: true, description: 'Agent, agentless or discovered only' },
  { id: 'assets.trend', title: 'Asset growth', wide: true, defaultOn: true, description: 'Active assets per day, last 30 days' },
  { id: 'assets.seen', chart: 'bar', title: 'Last contact / offline', defaultOn: true, description: 'Online now … offline more than 30 days' },
  { id: 'assets.os', chart: 'bar', title: 'Operating systems', wide: true, defaultOn: true, description: 'Top operating systems' },
  { id: 'security.posture', title: 'Security posture', defaultOn: true, description: 'Encryption, firewall, Secure Boot, TPM, antivirus' },
  { id: 'agents.health', chart: 'bar', title: 'Agent health', defaultOn: true, description: 'Online, not seen recently, offline, never checked in' },
  { id: 'software.top', chart: 'bar', title: 'Most installed software', defaultOn: true, description: 'Top 10 applications by devices' },
  { id: 'hw.cores', chart: 'column', title: 'Processor (cores)', defaultOn: true, description: 'Devices by number of CPU cores' },
  { id: 'hw.ram', chart: 'column', title: 'Memory (RAM)', defaultOn: true, description: 'Devices by installed memory' },
  { id: 'hw.disk', chart: 'column', title: 'Disk capacity (HDD/SSD)', defaultOn: true, description: 'Devices by total disk size' },
  { id: 'hw.free', chart: 'bar', title: 'Free disk space', defaultOn: true, description: 'Lowest free space on any volume' },
  { id: 'hw.cpu', chart: 'bar', title: 'Processor models', defaultOn: false, description: 'Top CPU models' },
  { id: 'hw.vendor', chart: 'bar', title: 'Manufacturers', defaultOn: true, description: 'Hardware vendors' },
  { id: 'scans.results', chart: 'donut', title: 'Discovery results', defaultOn: true, description: 'Outcome of scans in the last 30 days' },
  { id: 'license.usage', title: 'Licensing', defaultOn: true, description: 'Trial or license and device usage' },
  { id: 'scans.recent', title: 'Recent scans', defaultOn: false, description: 'Last five scans' },
  { id: 'vuln.severity', chart: 'donut', title: 'Open vulnerabilities', defaultOn: false, description: 'Software and Windows findings by severity' },
  { id: 'vuln.aging', chart: 'column', title: 'Vulnerability aging', defaultOn: false, description: 'How long open findings have been open' },
];
/** A widget on the dashboard. Saved reports are widgets too (id "report:<id>"). */
type LayoutItem = { id: string; wide: boolean; chart?: ChartKind; reportId?: string; title?: string | null };
type Layout = LayoutItem[];
const DEFAULT_LAYOUT: Layout = WIDGETS.filter((w) => w.defaultOn).map((w) => ({ id: w.id, wide: !!w.wide, chart: w.chart }));
interface DashboardDto {
  id: string; name: string; owned: boolean;
  widgets: { type: string; title?: string | null; width: number; settings?: { chart?: ChartKind; reportId?: string } | null }[];
}
const defFor = (it: LayoutItem): WidgetDef | undefined => it.reportId
  ? { id: it.id, title: it.title || 'Saved report', defaultOn: false, description: 'Saved report', chart: 'bar' }
  : WIDGETS.find((w) => w.id === it.id);
const toBody = (layout: Layout) => layout.map((w) => ({
  type: w.reportId ? 'report' : w.id, title: w.reportId ? (w.title ?? null) : null, width: w.wide ? 8 : 4, height: 2,
  settings: { ...(w.chart ? { chart: w.chart } : {}), ...(w.reportId ? { reportId: w.reportId } : {}) },
}));
const fromDto = (d: DashboardDto | null): Layout | null => {
  // Only a layout saved by this dashboard counts. Dashboards from earlier versions (kpi.assets, assets.byOs, …)
  // would otherwise show just the two widget types that still exist; they get the default layout instead.
  const ws = d?.widgets ?? [];
  const known = ws.length > 0 && ws.every((w) => WIDGETS.some((x) => x.id === w.type) || (w.type === 'report' && !!w.settings?.reportId));
  if (!known) return null;
  return ws.map((w) => w.type === 'report'
    ? { id: `report:${w.settings!.reportId}`, reportId: w.settings!.reportId, title: w.title, wide: w.width >= 8, chart: isKind(w.settings?.chart) ? w.settings!.chart : undefined }
    : { id: w.type, wide: w.width >= 8, chart: isKind(w.settings?.chart) ? w.settings!.chart : WIDGETS.find((x) => x.id === w.type)?.chart });
};

/** Adds a saved report to the user's own dashboard (creating it from the default layout when needed). */
export async function pinReportToDashboard(reportId: string, name: string, wide: boolean): Promise<'added' | 'present'> {
  const list = await api.get<DashboardDto[]>('/dashboards');
  const mine = list.find((d) => d.owned) ?? null;
  const layout = fromDto(mine) ?? DEFAULT_LAYOUT;
  if (layout.some((w) => w.reportId === reportId)) return 'present';
  const next = [...layout, { id: `report:${reportId}`, reportId, title: name, wide }];
  const body = { name: mine?.name ?? 'My dashboard', shared: false, filters: {}, refreshSeconds: 60, widgets: toBody(next) };
  if (mine) await api.put(`/dashboards/${mine.id}`, body); else await api.post('/dashboards', body);
  return 'added';
}
const isKind = (k: unknown): k is ChartKind => CHART_KINDS.some(([c]) => c === k);

export function DashboardPage() {
  const { me, can } = useAuth();
  const { navigate } = useRouter();
  const confirm = useConfirm();
  const [tick, setTick] = useState(0);
  const ov = useApi(() => api.get<Overview>('/dashboard/overview'), [tick]);
  const lic = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const scans = useApi(() => api.get<Paged<Scan>>('/discovery/scans', { pageSize: 5 }), [tick]);
  const dashboards = useApi(() => api.get<DashboardDto[]>('/dashboards'), []);
  const org = useApi(() => api.get<{ profile: { name?: string | null } | null }>('/organization'), []);
  const company = org.data?.profile?.name?.trim();
  const [msg, setMsg] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  useEffect(() => { const t = setInterval(() => setTick((x) => x + 1), 60000); return () => clearInterval(t); }, []);

  const mine = dashboards.data?.find((d) => d.owned) ?? null;
  const saved: Layout | null = useMemo(() => fromDto(mine), [mine]);
  const layout = saved ?? DEFAULT_LAYOUT;
  const wantsVulns = layout.some((w) => w.id.startsWith('vuln.')) && can('vuln.view');
  const vulns = useApi(() => (wantsVulns ? api.get<VulnSummary>('/vulnerabilities/overview') : Promise.resolve(null)), [tick, wantsVulns]);

  const o = ov.data;
  const k = o?.kpi;
  const pctOf = (a: number, b: number) => (b ? Math.round((a / b) * 100) : 0);
  const go = (facet: string, value: string, label: string) => navigate(`/assets?facet=${encodeURIComponent(facet)}&value=${encodeURIComponent(value)}&label=${encodeURIComponent(label)}`);

  async function collectAll() {
    if (!(await confirm({ title: 'Collect inventory now from every asset?', confirmLabel: 'Collect now', message: 'Agents report within about 2 minutes; assets without an agent are scanned agentlessly with your stored credentials.' }))) return;
    try { setMsg((await api.post<{ message: string }>('/assets/collect', { all: true })).message); } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }
  async function saveLayout(next: Layout) {
    const body = { name: mine?.name ?? 'My dashboard', shared: false, filters: {}, refreshSeconds: 60, widgets: toBody(next) };
    try {
      if (mine) await api.put(`/dashboards/${mine.id}`, body); else await api.post('/dashboards', body);
      dashboards.reload();
      setEditing(false);
      setMsg('Dashboard layout saved.');
    } catch (e) {
      // Keep the dialog open and say why (e.g. the server is older than this console).
      throw e;   // shown in the dialog, which stays open
    }
  }

  function render(id: string, chart: ChartKind = 'bar'): ReactNode {
    if (!o || !k) return null;
    const C = (data: Slice[], onSelect?: (s: Slice) => void, opts: { centerLabel?: string; color?: string; max?: number } = {}) =>
      <CategoryChart kind={chart} data={data} onSelect={onSelect} {...opts} />;
    switch (id) {
      case 'vuln.severity': {
        if (!can('vuln.view')) return <div className="chart-empty">You do not have access to vulnerability data.</div>;
        const v = vulns.data;
        if (!v) return <div className="chart-empty">Loading…</div>;
        const SEV: Record<string, string> = { critical: 'var(--crit)', high: '#e8590c', medium: 'var(--warn)', low: 'var(--info)', none: 'var(--line-strong)' };
        return C(v.bySeverity.map((r) => ({ key: r.key, label: fmt.label(r.key), value: r.count, color: SEV[r.key] })),
          (s) => navigate(`/vulnerabilities?tab=findings&severity=${encodeURIComponent(s.key)}`), { centerLabel: 'open' });
      }
      case 'vuln.aging': {
        if (!can('vuln.view')) return <div className="chart-empty">You do not have access to vulnerability data.</div>;
        const v = vulns.data;
        if (!v) return <div className="chart-empty">Loading…</div>;
        return C(v.aging.map((b) => ({ key: b.bucket, label: b.bucket, value: b.critical + b.high + b.medium + b.low })),
          () => navigate('/vulnerabilities?tab=findings&sort=age'), { centerLabel: 'open' });
      }
      case 'kpi.tiles':
        return (
          <div className="dash-hero">
            <Hero label="Assets" value={k.assets.toLocaleString()} sub={`+${k.newLast7} in the last 7 days`} to="/assets" />
            <Hero label="Servers" value={k.servers.toLocaleString()} sub={`${k.virtual} virtual machines in total`} to="/assets?category=server" />
            <Hero label="Endpoints" value={k.endpoints.toLocaleString()} sub="laptops and desktops" to="/assets?category=endpoint" />
            <Hero label="Network & other" value={k.networkDevices.toLocaleString()} sub="switches, printers, IoT" to="/assets?category=network_device" />
            <Hero label="Inventory coverage" value={`${pctOf(k.inventoried, k.assets)}%`} meter={pctOf(k.inventoried, k.assets)} sub={`${k.inventoried} of ${k.assets} with hardware & software`} to="/assets?inventory=missing" />
            <Hero label="Agent coverage" value={`${pctOf(k.withAgent, k.assets)}%`} meter={pctOf(k.withAgent, k.assets)} sub={`${k.agentsOnline} of ${k.agents} agents online`} to="/assets?agent=without" />
            <Hero label="Offline 7+ days" value={(k.offline7 ?? 0).toLocaleString()} tone={(k.offline7 ?? 0) > 0 ? 'crit' : undefined} sub="no agent check-in, scan or inventory" to="/assets?offlineDays=7" />
            <Hero label="Software" value={k.softwareTitles.toLocaleString()} sub={`${k.softwareInstalls.toLocaleString()} installations`} to="/software" />
          </div>
        );
      case 'assets.category':
        return C(slices(o.byCategory), (s) => navigate(`/assets?category=${encodeURIComponent(s.key)}`), { centerLabel: 'assets' });
      case 'assets.type':
        return C(slices(o.byType, (t) => TYPE_LABEL[t] ?? fmt.label(t)), (s) => go('type', s.key, `Type: ${s.label}`), { centerLabel: 'devices' });
      case 'assets.coverage':
        return C(slices(o.coverage, (c) => COVERAGE[c]?.[0] ?? c, (c) => COVERAGE[c]?.[1]), (s) => go('coverage', s.key, `Inventoried by: ${s.label}`), { centerLabel: 'assets' });
      case 'assets.trend':
        return <TrendArea points={o.trend.map((t) => ({ label: new Date(t.day).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }), total: t.total, added: t.new }))} />;
      case 'assets.seen':
        return C(SEEN.map(([key, color]) => ({ key, label: key === 'Online now' ? 'Online now' : key === 'Last 24 hours' ? 'Last 24 hours' : `Offline ${key}`, value: o.seen.find((r) => r.key === key)?.count ?? 0, color })),
          (s) => go('seen', s.key, `Last contact: ${s.label}`), { centerLabel: 'assets' });
      case 'assets.os':
        return C(slices(o.byOs, (x) => x), (s) => go('os', s.key, `OS: ${s.label}`), { centerLabel: 'assets' });
      case 'security.posture':
        return <PostureBars rows={o.posture} onSelect={(key, part) => go(POSTURE_FACET[key]!, part, `${key}: ${part === 'yes' ? 'compliant' : part === 'no' ? 'not compliant' : 'unknown'}`)} />;
      case 'agents.health': {
        const total = o.agentHealth.reduce((a, r) => a + r.count, 0);
        const online = o.agentHealth.find((r) => r.key === 'online')?.count ?? 0;
        return total === 0 ? <div className="chart-empty">No agents yet. <Link to="/agents">Deploy the agent</Link>.</div> : (
          <div className="stack-v">
            <div className="big-stat"><b>{pctOf(online, total)}%</b> <span className="muted">of {total} agents online</span></div>
            {C(HEALTH.map(([key, label, color]) => ({ key, label, value: o.agentHealth.find((r) => r.key === key)?.count ?? 0, color })),
              (s) => go('health', s.key, `Agent: ${s.label}`), { max: total, centerLabel: 'agents' })}
          </div>
        );
      }
      case 'software.top':
        return C(slices(o.topSoftware, (x) => x), (s) => navigate(`/software?q=${encodeURIComponent(s.key)}`), { centerLabel: 'installs' });
      case 'hw.cores':
        return C(slices(bySize(o.cores), (x) => x), (s) => go('cores', s.key, `Processor: ${s.label}`), { centerLabel: 'devices' });
      case 'hw.cpu':
        return C(slices(o.cpu, (x) => x), (s) => go('cpu', s.key, `CPU: ${s.label}`), { centerLabel: 'devices' });
      case 'hw.ram':
        return C(slices(bySize(o.ram), (x) => x), (s) => go('ram', s.key, `Memory: ${s.label}`), { centerLabel: 'devices' });
      case 'hw.disk':
        return C(slices(bySize(o.disk), (x) => x), (s) => go('disk', s.key, `Disk capacity: ${s.label}`), { centerLabel: 'devices' });
      case 'hw.free':
        return C(slices(ordered(o.free, ORDER.free!), (x) => x, (x) => FREE_COLOR[x]), (s) => go('free', s.key, `Free disk: ${s.label}`), { centerLabel: 'devices' });
      case 'hw.vendor':
        return C(slices(o.byManufacturer, (x) => x), (s) => go('vendor', s.key, `Manufacturer: ${s.label}`), { centerLabel: 'devices' });
      case 'scans.results':
        return C(slices(o.scanResults, (r) => RESULT_LABEL[r] ?? fmt.label(r), (r) => RESULT_COLOR[r]), () => navigate('/discovery'), { centerLabel: 'results' });
      case 'license.usage':
        return lic.data ? (
          <div className="stack-v">
            <div><Pill tone={statusTone(lic.data.mode)}>{fmt.label(lic.data.mode)}</Pill> <span className="muted">{lic.data.summary}</span></div>
            <HBars data={lic.data.usage.filter((u) => u.limit != null || u.billable > 0).map((u) => ({ key: u.workload, label: `${fmt.label(u.workload)}${u.limit != null ? ` (limit ${u.limit})` : ''}`, value: u.billable }))}
              max={Math.max(1, ...lic.data.usage.map((u) => u.limit ?? u.billable))} onSelect={() => navigate('/licensing')} />
          </div>
        ) : <ErrorBox error={lic.error} />;
      case 'scans.recent':
        return scans.data ? (
          <DataTable rows={scans.data.items} empty="No scans yet." columns={[
            { key: 'n', label: 'Scan', render: (r) => <b>{r.name || `Scan #${r.number}`}</b> },
            { key: 's', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
            { key: 't', label: 'Started', render: (r) => fmt.ago(r.startedAt ?? r.createdAt) },
          ]} />
        ) : null;
      default:
        return null;
    }
  }

  const sub: Record<string, string> = {
    'assets.type': 'From hardware inventory', 'assets.trend': 'Green bars = newly discovered', 'security.posture': 'Windows devices · click a colour to list devices',
    'assets.seen': 'Online = agent checked in (45 min); otherwise last scan or inventory', 'agents.health': 'Online = checked in within 45 min; not seen recently = up to 7 days', 'hw.free': 'Lowest free space on any volume', 'scans.results': `${k?.scans30 ?? 0} scans in the last 30 days`,
  };
  const links: Record<string, ReactNode> = {
    'agents.health': <Link to="/agents" className="btn sm">Agents</Link>, 'software.top': <Link to="/software" className="btn sm">All software</Link>,
    'scans.results': <Link to="/discovery" className="btn sm">Scans</Link>, 'license.usage': <Link to="/licensing" className="btn sm">Details</Link>,
    'scans.recent': <Link to="/discovery" className="btn sm">All scans</Link>,
  };

  return (
    <>
      <PageHead title={company ? `${company} Dashboard` : 'Dashboard'} desc={`Welcome back, ${me?.displayName ?? ''}`} crumbs={false}
        actions={<>
          <button className="btn" onClick={() => setEditing(true)}>Customize</button>
          {can('scans.run') && <button className="btn" onClick={() => void collectAll()}>Collect inventory now</button>}
          <Link to="/discovery?new=1" className="btn primary">New scan</Link>
        </>} />
      {msg && <Callout icon="info">{msg}</Callout>}
      <ErrorBox error={ov.error} />
      {!o || !k ? <Loading /> : (
        <>
          <div className="dash-grid">
            {layout.map((item) => {
              const { id, wide, chart } = item;
              const def = defFor(item);
              if (!def) return null;
              if (def.full) return <div key={id} className="dash-full">{render(id)}</div>;
              if (item.reportId) return (
                <div key={id} className={`dash-tile${wide ? ' dash-wide' : ''}`}>
                  <Panel title={def.title} sub="Saved report" right={<Link to={`/reports/${item.reportId}`} className="btn sm">Open</Link>}>
                    <ReportWidget reportId={item.reportId} chart={chart} tick={tick} />
                  </Panel>
                </div>
              );
              return (
                <div key={id} className={`dash-tile${wide ? ' dash-wide' : ''}`}>
                  <Panel title={def.title} sub={sub[id]} right={links[id]} flush={id === 'scans.recent'}>{render(id, chart ?? def.chart)}</Panel>
                </div>
              );
            })}
          </div>
          <p className="faint" style={{ fontSize: 12 }}>Updated {fmt.ago(o.generatedAt)} · refreshes every minute.</p>
        </>
      )}
      {editing && <CustomizeDialog layout={layout} onClose={() => setEditing(false)} onSave={saveLayout} />}
    </>
  );
}

function CustomizeDialog({ layout, onClose, onSave }: { layout: Layout; onClose: () => void; onSave: (l: Layout) => Promise<void> }) {
  // Visible widgets in their order, then the hidden ones.
  const [items, setItems] = useState<(LayoutItem & { on: boolean })[]>(() => [
    ...layout.map((l) => ({ ...l, chart: l.chart ?? defFor(l)?.chart, on: true })),
    ...WIDGETS.filter((w) => !layout.some((l) => l.id === w.id)).map((w) => ({ id: w.id, wide: !!w.wide, chart: w.chart, on: false })),
  ]);
  const [drag, setDrag] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const move = (from: number, to: number) => setItems((xs) => {
    if (to < 0 || to >= xs.length) return xs;
    const copy = [...xs];
    const [x] = copy.splice(from, 1);
    copy.splice(to, 0, x!);
    return copy;
  });
  const set = (i: number, patch: Partial<{ on: boolean; wide: boolean; chart: ChartKind }>) => setItems((xs) => xs.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const [error, setError] = useState<string | null>(null);
  const reports = useApi(() => api.get<{ id: string; name: string; definition?: unknown }[]>('/reports'), []);
  const addable = (reports.data ?? []).filter((r) => !items.some((x) => x.reportId === r.id));
  async function save(next: Layout) {
    setBusy(true); setError(null);
    try { await onSave(next); } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="cz-title">
      <div className="modal-card" style={{ width: 'min(640px, 100%)' }}>
        <header><h2 id="cz-title">Customize dashboard</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">The layout was not saved: {error}</Callout>}
          <small className="muted">Tick the charts you want, choose how each is drawn (donut, pie, bar or column), drag them (or use the arrows) to change the order, and choose which ones are wide. Saved reports can be added as charts too. Saved for your account.</small>
          <ul className="cz-list">
            {items.map((it, i) => {
              const def = defFor(it)!;
              return (
                <li key={it.id} draggable onDragStart={() => setDrag(i)} onDragOver={(e) => e.preventDefault()} onDrop={() => { if (drag != null) move(drag, i); setDrag(null); }}
                  className={it.on ? '' : 'off'}>
                  <span className="grip" aria-hidden="true">⋮⋮</span>
                  <label className="row-check" style={{ flex: 1 }}>
                    <input type="checkbox" checked={it.on} onChange={(e) => set(i, { on: e.target.checked })} />
                    <span><b>{def.title}</b><br /><small className="muted">{def.description}</small></span>
                  </label>
                  {def.chart && (
                    <select className="in sm" aria-label={`Chart type for ${def.title}`} value={it.chart ?? def.chart} disabled={!it.on} onChange={(e) => set(i, { chart: e.target.value as ChartKind })}>
                      {CHART_KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                    </select>
                  )}
                  {!def.full && <label className="row-check" title="Wide: spans two columns"><input type="checkbox" checked={it.wide} disabled={!it.on} onChange={(e) => set(i, { wide: e.target.checked })} /> Wide</label>}
                  <button type="button" className="btn sm" aria-label={`Move ${def.title} up`} disabled={i === 0} onClick={() => move(i, i - 1)}>↑</button>
                  <button type="button" className="btn sm" aria-label={`Move ${def.title} down`} disabled={i === items.length - 1} onClick={() => move(i, i + 1)}>↓</button>
                </li>
              );
            })}
          </ul>
          {addable.length > 0 && (
            <label className="stack-label">Add a saved report
              <select className="in" value="" onChange={(e) => {
                const r = addable.find((x) => x.id === e.target.value);
                if (r) setItems((xs) => [...xs, { id: `report:${r.id}`, reportId: r.id, title: r.name, wide: false, chart: 'bar' as ChartKind, on: true }]);
              }}>
                <option value="">Choose a report…</option>
                {addable.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
              </select>
            </label>
          )}
        </div>
        <footer>
          <button type="button" className="btn" style={{ marginRight: 'auto' }} disabled={busy} onClick={() => void save(DEFAULT_LAYOUT)}>Reset to default</button>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="button" className="btn primary" disabled={busy || !items.some((x) => x.on)} onClick={() => void save(items.filter((x) => x.on).map(({ id, wide, chart, reportId, title }) => ({ id, wide, chart, reportId, title })))}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </div>
    </div>
  );
}
