import { api } from '../../api/client';
import type { AssetSummary, Job, KeyCount, LicensingStatus, Paged, Scan } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Link } from '../../app/router';
import { DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';

function Bars({ rows }: { rows: KeyCount[] }) {
  const max = Math.max(1, ...rows.map((r) => r.count));
  if (rows.length === 0) return <div className="empty">No data yet. Run a discovery scan to populate the inventory.</div>;
  return (
    <div className="bars">
      {[...rows].sort((a, b) => b.count - a.count).map((r) => (
        <div className="row" key={r.key}>
          <span>{fmt.label(r.key)}</span>
          <div className="track"><div className="fill" style={{ width: `${(r.count / max) * 100}%` }} /></div>
          <span className="n">{r.count.toLocaleString()}</span>
        </div>
      ))}
    </div>
  );
}

export function DashboardPage() {
  const { me, can } = useAuth();
  const summary = useApi(() => api.get<AssetSummary>('/assets/summary'), []);
  const lic = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const scans = useApi(() => api.get<Paged<Scan>>('/discovery/scans', { pageSize: 5 }), []);
  const jobs = useApi(() => (can('jobs.view') ? api.get<Paged<Job>>('/jobs', { pageSize: 6 }) : Promise.resolve(null)), [me?.id]);
  const s = summary.data;

  return (
    <>
      <PageHead title="Dashboard" desc={`Welcome back, ${me?.displayName ?? ''}. A live overview of your estate.`} crumbs={false}
        actions={<Link to="/discovery?new=1" className="btn primary">New scan</Link>} />
      <ErrorBox error={summary.error} />
      {summary.loading && !s ? <Loading /> : s && (
        <Kpis items={[
          { label: 'Active assets', value: s.total.toLocaleString(), to: '/assets' },
          { label: 'Servers', value: (s.byCategory.find((c) => c.key === 'server')?.count ?? 0).toLocaleString(), to: '/assets?category=server' },
          { label: 'Endpoints', value: (s.byCategory.find((c) => c.key === 'endpoint')?.count ?? 0).toLocaleString(), to: '/assets?category=endpoint' },
          { label: 'Not seen 30 days', value: s.stale.toLocaleString(), sub: 'stale' },
          { label: 'Retired', value: s.retired.toLocaleString() },
        ]} />
      )}
      <div className="grid-2">
        <Panel title="Assets by category">{s ? <Bars rows={s.byCategory} /> : <Loading />}</Panel>
        <Panel title="Operating systems">{s ? <Bars rows={s.byOs} /> : <Loading />}</Panel>
      </div>
      <div className="grid-2">
        <Panel title="Licensing" right={<Link to="/licensing" className="btn sm">Details</Link>}>
          <ErrorBox error={lic.error} />
          {lic.data && (
            <div className="stack-v">
              <div><Pill tone={statusTone(lic.data.mode)}>{fmt.label(lic.data.mode)}</Pill> <span className="muted">{lic.data.summary}</span></div>
              <Bars rows={lic.data.usage.map((u) => ({ key: `${u.workload}${u.limit != null ? ` / ${u.limit}` : ''}`, count: u.billable }))} />
            </div>
          )}
        </Panel>
        <Panel title="Recent scans" right={<Link to="/discovery" className="btn sm">All scans</Link>} flush>
          <ErrorBox error={scans.error} />
          {scans.data && (
            <DataTable rows={scans.data.items} empty="No scans yet." columns={[
              { key: 'n', label: 'Scan', render: (r) => <b>{r.name || `Scan #${r.number}`}</b> },
              { key: 's', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
              { key: 'p', label: 'Progress', render: (r) => `${r.processed}/${r.totalTargets}` },
              { key: 't', label: 'Started', render: (r) => fmt.ago(r.startedAt ?? r.createdAt) },
            ]} />
          )}
        </Panel>
      </div>
      {jobs.data && (
        <Panel title="Background jobs" sub="Durable jobs run by the Zentriq Worker service" flush>
          <DataTable rows={jobs.data.items} empty="No jobs yet." columns={[
            { key: 'type', label: 'Job', render: (r) => fmt.label(r.jobType) },
            { key: 'st', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
            { key: 'pr', label: 'Progress', render: (r) => (r.progressPct != null ? `${r.progressPct}%` : '—') },
            { key: 'at', label: 'Created', render: (r) => fmt.ago(r.createdAt) },
            { key: 'err', label: 'Detail', className: 'wrap', render: (r) => r.lastError ?? r.progressText ?? '' },
          ]} />
        </Panel>
      )}
    </>
  );
}
