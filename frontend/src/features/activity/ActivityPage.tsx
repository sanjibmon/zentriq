import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill } from '../../components/ui';

export interface ActivityRow {
  id: string; assetId: string; hostname: string | null; ip: string | null; channel: 'agent' | 'agentless'; kind: 'service' | 'uninstall' | 'agent-update' | 'remote-support';
  target: string; argument: string | null; operation: string; status: string; detail: string | null; resultState: string | null;
  requestedAt: string; completedAt: string | null; requestedBy: string | null;
}

const STATUS: Record<string, ['ok' | 'crit' | 'warn' | '', string]> = {
  succeeded: ['ok', 'Succeeded'], failed: ['crit', 'Failed'], queued: ['warn', 'Waiting for device'], sent: ['warn', 'Sent to agent'], running: ['warn', 'In progress'], expired: ['', 'Expired'],
};
const OP: Record<string, string> = { start: 'Start service', stop: 'Stop service', restart: 'Restart service', uninstall: 'Uninstall software', update: 'Update agent' };

/** Every service start/stop/restart and software uninstall requested from the console, across all devices. */
export function ActivityPage() {
  const { query } = useRouter();
  const assetId = query.get('assetId') ?? '';
  const [kind, setKind] = useState(query.get('kind') ?? '');
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Paged<ActivityRow>>('/activity', { kind, status, q: search, assetId, page, pageSize: 50 }), [kind, status, search, assetId, page]);
  const rows = data.data?.items ?? [];
  const pending = rows.some((r) => ['queued', 'sent', 'running'].includes(r.status));
  useEffect(() => {
    if (!pending) return;
    const t = setInterval(data.reload, 5000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [pending]);
  const host = assetId && rows[0] ? rows[0].hostname ?? rows[0].ip : null;

  return (
    <>
      <PageHead title="Activity Log" desc={assetId ? <>Device actions on <b>{host ?? 'this device'}</b> · <Link to="/activity">show all devices</Link></> : 'Every action on every device — who did what, where and when.'} />
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search device, service, software, user or result…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search activity" />
        <select className="in" value={kind} onChange={(e) => { setPage(1); setKind(e.target.value); }} aria-label="Type">
          <option value="">All actions</option>
          <option value="service">Service actions</option>
          <option value="uninstall">Software uninstalls</option>
          <option value="agent-update">Agent updates</option>
          <option value="remote-support">Remote support agent</option>
        </select>
        <select className="in" value={status} onChange={(e) => { setPage(1); setStatus(e.target.value); }} aria-label="Result">
          <option value="">Any result</option>
          <option value="pending">In progress</option>
          <option value="succeeded">Succeeded</option>
          <option value="failed">Failed</option>
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No device actions yet. Start, stop or restart a service from an asset, or uninstall software from Software Inventory.'} columns={[
          { key: 't', label: 'Requested', render: (r) => <span title={fmt.date(r.requestedAt)}>{fmt.date(r.requestedAt)}</span> },
          { key: 'h', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}><b>{r.hostname ?? r.ip ?? '—'}</b></Link> },
          { key: 'a', label: 'Action', render: (r) => <>{r.kind === 'remote-support' ? (r.operation === 'install' ? 'Install remote support' : 'Remove remote support') : OP[r.operation] ?? fmt.label(r.operation)}<div className="faint mono" style={{ fontSize: 11.5 }}>{r.kind === 'agent-update' ? `to ${r.target}` : r.kind === 'remote-support' ? 'Zentriq Remote Support agent' : `${r.target}${r.argument ? ` ${r.argument}` : ''}`}</div></> },
          { key: 'c', label: 'Via', render: (r) => r.channel === 'agent' ? 'Zentriq Agent' : 'WinRM / WMI' },
          { key: 's', label: 'Result', render: (r) => <Pill tone={STATUS[r.status]?.[0] ?? ''}>{STATUS[r.status]?.[1] ?? fmt.label(r.status)}</Pill> },
          { key: 'd', label: 'Detail', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.detail ?? (r.status === 'queued' ? 'Runs at the device’s next check-in.' : '')}{r.completedAt ? <span className="faint"> · {fmt.ago(r.completedAt)}</span> : null}</span> },
          { key: 'b', label: 'By', render: (r) => r.requestedBy ?? '—' },
        ]} />
        {data.data && <Pager page={page} pageSize={50} total={data.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}
