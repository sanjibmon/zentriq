import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { KC, LicensingStatus, Overview, Paged, Scan } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Link, useRouter } from '../../app/router';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';
import { Donut, HBars, PostureBars, TrendArea, type Slice } from '../../components/charts';

const TYPE_LABEL: Record<string, string> = {
  laptop: 'Laptop', desktop: 'Desktop', server: 'Physical server', virtual: 'Virtual machine', other: 'Other hardware',
  network_device: 'Network device', printer: 'Printer', iot: 'IoT', 'not inventoried': 'Not inventoried yet',
};
const COVERAGE: Record<string, [string, string]> = {
  agent: ['Zentriq Agent', 'var(--ok)'], agentless: ['Agentless (credentials)', 'var(--info)'], 'discovered only': ['Discovered only — no inventory', 'var(--warn)'],
};
const HEALTH: Record<string, [string, string]> = {
  online: ['Online', 'var(--ok)'], stale: ['Not seen recently', 'var(--warn)'], offline: ['Offline > 7 days', 'var(--crit)'], never: ['Never checked in', 'var(--line-strong)'],
};
const RESULT_LABEL: Record<string, string> = {
  success: 'Inventoried', reachable_unauthenticated: 'Reachable, no credential', inventory_unavailable: 'Up, ports blocked',
  auth_failed: 'Credential rejected', inventory_failed: 'Collection failed', skipped: 'Skipped', excluded: 'Excluded', cancelled: 'Cancelled',
};
const CRIT_COLOR: Record<string, string> = { Critical: 'var(--crit)', High: 'var(--warn)', Medium: 'var(--info)', Low: 'var(--line-strong)' };
const RESULT_COLOR: Record<string, string> = { success: 'var(--ok)', reachable_unauthenticated: 'var(--warn)', inventory_unavailable: 'var(--info)', auth_failed: 'var(--crit)', inventory_failed: 'var(--crit)' };
const RAM_ORDER = ['< 4 GB', '4–8 GB', '8–16 GB', '16–32 GB', '32 GB +'];

const slices = (rows: KC[], label: (k: string) => string = fmt.label, color?: (k: string) => string | undefined): Slice[] =>
  rows.map((r) => ({ key: r.key, label: label(r.key), value: r.count, color: color?.(r.key) }));

function Hero({ label, value, sub, to, meter }: { label: string; value: string; sub?: string; to?: string; meter?: number }) {
  const body = (
    <>
      <span className="k">{label}</span>
      <span className="v">{value}</span>
      {meter != null && <span className="meter" aria-hidden="true"><i style={{ width: `${Math.min(100, Math.max(0, meter))}%` }} /></span>}
      {sub && <span className="s">{sub}</span>}
    </>
  );
  return to ? <Link to={to} className="hero-card">{body}</Link> : <div className="hero-card">{body}</div>;
}

export function DashboardPage() {
  const { me, can } = useAuth();
  const { navigate } = useRouter();
  const [tick, setTick] = useState(0);
  const ov = useApi(() => api.get<Overview>('/dashboard/overview'), [tick]);
  const lic = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const scans = useApi(() => api.get<Paged<Scan>>('/discovery/scans', { pageSize: 5 }), [tick]);
  const [msg, setMsg] = useState<string | null>(null);
  useEffect(() => { const t = setInterval(() => setTick((x) => x + 1), 60000); return () => clearInterval(t); }, []);
  const o = ov.data;
  const k = o?.kpi;
  const pctOf = (a: number, b: number) => (b ? Math.round((a / b) * 100) : 0);

  async function collectAll() {
    if (!window.confirm('Collect inventory now from every asset? Agents report within about 2 minutes; assets without an agent are scanned agentlessly with your stored credentials.')) return;
    try { setMsg((await api.post<{ message: string }>('/assets/collect', { all: true })).message); } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }

  return (
    <>
      <PageHead title="Dashboard" desc={`Welcome back, ${me?.displayName ?? ''}. Live overview of your estate — click any chart to drill in.`} crumbs={false}
        actions={<>
          {can('scans.run') && <button className="btn" onClick={() => void collectAll()}>Collect inventory now</button>}
          <Link to="/discovery?new=1" className="btn primary">New scan</Link>
        </>} />
      {msg && <Callout icon="info">{msg}</Callout>}
      <ErrorBox error={ov.error} />
      {!o || !k ? <Loading /> : (
        <>
          <div className="dash-hero">
            <Hero label="Assets" value={k.assets.toLocaleString()} sub={`+${k.newLast7} in the last 7 days`} to="/assets" />
            <Hero label="Servers" value={k.servers.toLocaleString()} sub={`${k.virtual} virtual machines in total`} to="/assets?category=server" />
            <Hero label="Endpoints" value={k.endpoints.toLocaleString()} sub="laptops and desktops" to="/assets?category=endpoint" />
            <Hero label="Network & other" value={k.networkDevices.toLocaleString()} sub="switches, printers, IoT" to="/assets?category=network_device" />
            <Hero label="Inventory coverage" value={`${pctOf(k.inventoried, k.assets)}%`} meter={pctOf(k.inventoried, k.assets)} sub={`${k.inventoried} of ${k.assets} with hardware & software`} to="/assets?inventory=missing" />
            <Hero label="Agent coverage" value={`${pctOf(k.withAgent, k.assets)}%`} meter={pctOf(k.withAgent, k.assets)} sub={`${k.agentsOnline} of ${k.agents} agents online`} to="/assets?agent=without" />
            <Hero label="Software" value={k.softwareTitles.toLocaleString()} sub={`${k.softwareInstalls.toLocaleString()} installations`} to="/software" />
            <Hero label="Needs attention" value={(k.stale + k.lowDisk).toLocaleString()} sub={`${k.stale} not seen 30 days · ${k.lowDisk} low disk`} to="/assets" />
          </div>

          <div className="grid-3">
            <Panel title="Assets by category">
              <Donut data={slices(o.byCategory)} onSelect={(s) => navigate(`/assets?category=${encodeURIComponent(s.key)}`)} centerLabel="assets" />
            </Panel>
            <Panel title="Device type" sub="From hardware inventory">
              <Donut data={slices(o.byType, (t) => TYPE_LABEL[t] ?? fmt.label(t))} centerLabel="devices" />
            </Panel>
            <Panel title="How assets are inventoried">
              <Donut data={slices(o.coverage, (c) => COVERAGE[c]?.[0] ?? c, (c) => COVERAGE[c]?.[1])} centerLabel="assets"
                onSelect={(s) => navigate(s.key === 'agent' ? '/assets?agent=with' : s.key === 'agentless' ? '/assets?agent=without&inventory=success' : '/assets?inventory=missing')} />
            </Panel>
          </div>

          <div className="grid-2">
            <Panel title="Asset growth" sub="Active assets per day, last 30 days · green bars = newly discovered">
              <TrendArea points={o.trend.map((t) => ({ label: new Date(t.day).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }), total: t.total, added: t.new }))} />
            </Panel>
            <Panel title="Operating systems">
              <HBars data={slices(o.byOs, (x) => x)} onSelect={(s) => s.key !== 'Other' && s.key !== 'Unknown' && navigate(`/assets?q=${encodeURIComponent(s.key)}`)} />
            </Panel>
          </div>

          <div className="grid-3">
            <Panel title="Security posture" sub="Windows devices with inventory">
              <PostureBars rows={o.posture} />
            </Panel>
            <Panel title="Agent health" right={<Link to="/agents" className="btn sm">Agents</Link>}>
              <Donut data={slices(o.agentHealth, (h) => HEALTH[h]?.[0] ?? h, (h) => HEALTH[h]?.[1])} centerLabel="agents" onSelect={() => navigate('/agents')} />
            </Panel>
            <Panel title="Most installed software" right={<Link to="/software" className="btn sm">All software</Link>}>
              <HBars data={slices(o.topSoftware, (x) => x)} color="var(--s7)" onSelect={(s) => navigate(`/software?q=${encodeURIComponent(s.key)}`)} />
            </Panel>
          </div>

          <div className="grid-3">
            <Panel title="Manufacturers">
              <HBars data={slices(o.byManufacturer, (x) => x)} color="var(--s2)" onSelect={(s) => s.key !== 'Other' && navigate(`/hardware?q=${encodeURIComponent(s.key)}`)} />
            </Panel>
            <Panel title="Memory (RAM)">
              <HBars data={slices([...o.ram].sort((a, b) => RAM_ORDER.indexOf(a.key) - RAM_ORDER.indexOf(b.key)), (x) => x)} color="var(--s3)" />
            </Panel>
            <Panel title="Business criticality">
              <Donut data={slices(['Critical', 'High', 'Medium', 'Low'].map((c) => ({ key: c, count: o.byCriticality.find((x) => x.key === c)?.count ?? 0 })).filter((x) => x.count > 0), fmt.label, (c) => CRIT_COLOR[c])} centerLabel="assets" onSelect={() => navigate('/assets')} />
            </Panel>
          </div>

          <div className="grid-3">
            <Panel title="Discovery results" sub={`${k.scans30} scans in the last 30 days (responding devices)`} right={<Link to="/discovery" className="btn sm">Scans</Link>}>
              <Donut data={slices(o.scanResults, (r) => RESULT_LABEL[r] ?? fmt.label(r), (r) => RESULT_COLOR[r])} centerLabel="results" onSelect={() => navigate('/discovery')} />
            </Panel>
            <Panel title="Licensing" right={<Link to="/licensing" className="btn sm">Details</Link>}>
              <ErrorBox error={lic.error} />
              {lic.data && (
                <div className="stack-v">
                  <div><Pill tone={statusTone(lic.data.mode)}>{fmt.label(lic.data.mode)}</Pill> <span className="muted">{lic.data.summary}</span></div>
                  <HBars data={lic.data.usage.filter((u) => u.limit != null || u.billable > 0).map((u) => ({ key: u.workload, label: `${fmt.label(u.workload)}${u.limit != null ? ` (limit ${u.limit})` : ''}`, value: u.billable }))}
                    max={Math.max(1, ...lic.data.usage.map((u) => u.limit ?? u.billable))} color="var(--accent)" />
                </div>
              )}
            </Panel>
            <Panel title="Recent scans" flush>
              <ErrorBox error={scans.error} />
              {scans.data && (
                <DataTable rows={scans.data.items} empty="No scans yet." columns={[
                  { key: 'n', label: 'Scan', render: (r) => <b>{r.name || `Scan #${r.number}`}</b> },
                  { key: 's', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
                  { key: 't', label: 'Started', render: (r) => fmt.ago(r.startedAt ?? r.createdAt) },
                ]} />
              )}
            </Panel>
          </div>
          <p className="faint" style={{ fontSize: 12 }}>Updated {fmt.ago(o.generatedAt)} · refreshes every minute.</p>
        </>
      )}
    </>
  );
}
