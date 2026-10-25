import { useState } from 'react';
import { api } from '../../api/client';
import type { AuditEvent, Paged } from '../../api/types';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill, statusTone } from '../../components/ui';
import { useRouter } from '../../app/router';
import { Icon } from '../../components/Icon';
import { CsvExportDialog, LogRetention } from './LogRetention';

export function AuditPage() {
  const { query, navigate } = useRouter();
  const tab = query.get('tab') === 'retention' ? 'retention' : 'log';
  const [csv, setCsv] = useState(false);
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [outcome, setOutcome] = useState('');
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Paged<AuditEvent>>('/audit', { q: search, outcome, page, pageSize: 50 }), [search, outcome, page]);
  return (
    <>
      <PageHead title="Audit Logs" actions={tab === 'log' ? <button className="btn" onClick={() => setCsv(true)}><Icon name="down" size={14} /> Download CSV</button> : undefined} />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'log'} className={tab === 'log' ? 'on' : ''} onClick={() => navigate('/audit', true)}>Audit log</button>
        <button role="tab" aria-selected={tab === 'retention'} className={tab === 'retention' ? 'on' : ''} onClick={() => navigate('/audit?tab=retention', true)}>Retention &amp; export</button>
      </div>
      {csv && <CsvExportDialog what="audit log" path="/audit/export" onClose={() => setCsv(false)} />}
      {tab === 'retention' ? <LogRetention /> : <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search user, action or target…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search audit log" />
        <select className="in" value={outcome} onChange={(e) => { setPage(1); setOutcome(e.target.value); }} aria-label="Outcome">
          <option value="">Any outcome</option>
          <option value="Success">Success</option>
          <option value="Failure">Failure</option>
          <option value="Denied">Denied</option>
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data?.items ?? []} empty={data.loading ? 'Loading…' : 'No events.'} columns={[
          { key: 't', label: 'Time', render: (r) => <span className="mono">{fmt.date(r.occurredAt)}</span> },
          { key: 'u', label: 'User', render: (r) => r.actorName },
          { key: 'a', label: 'Action', render: (r) => <b>{r.action}</b> },
          { key: 'g', label: 'Target', className: 'wrap', render: (r) => r.target ?? '' },
          { key: 'ip', label: 'Source IP', render: (r) => <span className="mono">{r.sourceIp ?? ''}</span> },
          { key: 'o', label: 'Outcome', render: (r) => <Pill tone={statusTone(r.outcome)}>{r.outcome}</Pill> },
        ]} />
        {data.data && <Pager page={page} pageSize={50} total={data.data.total} onPage={setPage} />}
      </Panel>
      </>}
    </>
  );
}
