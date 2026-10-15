import { useEffect, useMemo, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { ScheduleDialog } from './ScheduleDialog';
import { errorText, type Dataset, type ReportListItem, type ReportRun } from './types';

export function ReportsPage() {
  const { query, navigate } = useRouter();
  const tab = query.get('tab') === 'history' ? 'history' : 'library';
  return (
    <>
      <PageHead title="Reports & Analytics" desc="Build, schedule and export reports on any inventory data."
        actions={<Link className="btn primary" to="/reports/new"><Icon name="plus" size={14} /> New report</Link>} />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'library'} className={tab === 'library' ? 'on' : ''} onClick={() => navigate('/reports', true)}>Reports</button>
        <button role="tab" aria-selected={tab === 'history'} className={tab === 'history' ? 'on' : ''} onClick={() => navigate('/reports?tab=history', true)}>Run history</button>
      </div>
      {tab === 'library' ? <Library /> : <History />}
    </>
  );
}

function Library() {
  const list = useApi(() => api.get<ReportListItem[]>('/reports'), []);
  const datasets = useApi(() => api.get<Dataset[]>('/reports/datasets'), []);
  const { can } = useAuth();
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [ds, setDs] = useState('');
  const [scheduling, setScheduling] = useState<ReportListItem | null>(null);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const dsName = useMemo(() => Object.fromEntries((datasets.data ?? []).map((d) => [d.key, d.name])), [datasets.data]);
  const rows = (list.data ?? []).filter((r) => (!ds || r.dataset === ds) && (!q || `${r.name} ${r.description ?? ''}`.toLowerCase().includes(q.toLowerCase())));

  async function send(r: ReportListItem) {
    if (r.recipients.length === 0) { setScheduling(r); return; }
    const ok = await confirm({ title: `Send "${r.name}" now?`, confirmLabel: 'Send', message: `The report is generated as ${r.format.toUpperCase()} and e-mailed to ${r.recipients.join(', ')}.` });
    if (!ok) return;
    try { const res = await api.post<{ message: string }>(`/reports/${r.id}/send`); setMsg({ tone: 'info', text: res.message }); }
    catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }
  async function remove(r: ReportListItem) {
    const ok = await confirm({ title: `Delete "${r.name}"?`, danger: true, confirmLabel: 'Delete', message: 'The report definition and its schedule are deleted. Files already in the run history are kept until they expire.' });
    if (!ok) return;
    try { await api.del(`/reports/${r.id}`); list.reload(); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }

  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <ErrorBox error={list.error} />
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search reports…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search reports" />
        <select className="in" value={ds} onChange={(e) => setDs(e.target.value)} aria-label="Data source">
          <option value="">All data sources</option>
          {(datasets.data ?? []).map((d) => <option key={d.key} value={d.key}>{d.name}</option>)}
        </select>
      </div>
      <Panel flush>
        <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No reports match.'} columns={[
          { key: 'n', label: 'Report', className: 'wrap', render: (r) => <>
            <Link to={`/reports/${r.id}`}><b>{r.name}</b></Link>{r.system && <> <Pill tone="info">Built-in</Pill></>}{!r.system && r.shared && <> <Pill>Shared</Pill></>}
            {r.description && <div className="faint" style={{ fontSize: 12 }}>{r.description}</div>}
          </> },
          { key: 'd', label: 'Data source', render: (r) => dsName[r.dataset] ?? r.dataset },
          { key: 's', label: 'Schedule', className: 'wrap', render: (r) => r.scheduleText
            ? <><span>{r.scheduleText}</span><div className="faint" style={{ fontSize: 11.5 }}>Next {fmt.date(r.nextRunAt)} · {r.recipients.length} recipient(s)</div></>
            : <span className="muted">—</span> },
          { key: 'l', label: 'Last run', render: (r) => r.lastRunAt ? <><Pill tone={r.lastStatus === 'succeeded' ? 'ok' : r.lastStatus === 'failed' ? 'crit' : 'warn'}>{fmt.label(r.lastStatus)}</Pill> <span className="faint">{fmt.ago(r.lastRunAt)}</span></> : <span className="muted">never</span> },
          { key: 'o', label: 'Owner', render: (r) => r.system ? 'Zentriq' : r.owner ?? '—' },
          { key: 'a', label: '', render: (r) => <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
            <Link className="btn sm" to={`/reports/${r.id}`}>Open</Link>
            {can('reports.design') && (r.owned || can('settings.manage')) && <button className="btn sm" onClick={() => setScheduling(r)}><Icon name="clock" size={13} /> Schedule</button>}
            {can('reports.export') && <button className="btn sm" onClick={() => void send(r)}>Send now</button>}
            {can('reports.design') && !r.system && (r.owned || can('settings.manage')) && <button className="btn sm danger" onClick={() => void remove(r)} aria-label={`Delete ${r.name}`}><Icon name="trash" size={13} /></button>}
          </div> },
        ]} />
      </Panel>
      {scheduling && <ScheduleDialog report={scheduling} onClose={() => setScheduling(null)} onSaved={() => { setScheduling(null); list.reload(); }} />}
    </>
  );
}

function History() {
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const runs = useApi(() => api.get<Paged<ReportRun>>('/reports/runs', { page, pageSize }), [page]);
  const [err, setErr] = useState<string | null>(null);
  const running = (runs.data?.items ?? []).some((r) => r.status === 'running');
  useEffect(() => {
    if (!running) return;
    const t = setTimeout(runs.reload, 5000);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running, runs.data]);

  async function download(r: ReportRun) {
    setErr(null);
    try {
      const res = await fetch(`/api/v1/reports/runs/${r.id}/file`, { credentials: 'same-origin' });
      if (!res.ok) throw new Error(res.status === 404 ? 'The file has expired (kept 30 days).' : res.statusText);
      const a = document.createElement('a');
      a.href = URL.createObjectURL(await res.blob());
      a.download = r.fileName ?? `report.${r.format ?? 'xlsx'}`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    } catch (e) { setErr(errorText(e)); }
  }

  return (
    <>
      {err && <Callout tone="crit" icon="alert">{err}</Callout>}
      <ErrorBox error={runs.error} />
      <Panel flush sub="Scheduled and on-demand deliveries. Files are kept for 30 days.">
        <DataTable rows={runs.data?.items ?? []} empty={runs.loading ? 'Loading…' : 'No report has been delivered yet.'} columns={[
          { key: 't', label: 'Started', render: (r) => fmt.date(r.startedAt) },
          { key: 'n', label: 'Report', className: 'wrap', render: (r) => r.reportId ? <Link to={`/reports/${r.reportId}`}>{r.reportName}</Link> : r.reportName },
          { key: 'g', label: 'Trigger', render: (r) => r.trigger === 'schedule' ? 'Schedule' : `Manual${r.requestedBy ? ` · ${r.requestedBy}` : ''}` },
          { key: 's', label: 'Status', render: (r) => <span title={r.error ?? ''}><Pill tone={r.status === 'succeeded' ? 'ok' : r.status === 'failed' ? 'crit' : 'warn'}>{fmt.label(r.status)}</Pill></span> },
          { key: 'r', label: 'Rows', render: (r) => r.rows?.toLocaleString() ?? '—' },
          { key: 'to', label: 'Sent to', className: 'wrap', render: (r) => r.recipients.length ? <span title={r.recipients.join(', ')}>{r.recipients.slice(0, 2).join(', ')}{r.recipients.length > 2 ? ` +${r.recipients.length - 2}` : ''}</span> : '—' },
          { key: 'e', label: 'Detail', className: 'wrap', render: (r) => r.error ? <span className="faint">{r.error}</span> : '' },
          { key: 'f', label: '', render: (r) => r.downloadable ? <button className="btn sm" onClick={() => void download(r)}><Icon name="down" size={13} /> {r.format?.toUpperCase()}</button> : null },
        ]} />
        {runs.data && <Pager page={page} pageSize={pageSize} total={runs.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}
