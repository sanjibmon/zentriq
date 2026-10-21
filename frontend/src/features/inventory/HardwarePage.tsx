import { useState } from 'react';
import { api } from '../../api/client';
import type { HardwareRow, Paged } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { ChangesTab } from './Changes';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel } from '../../components/ui';

export function HardwarePage() {
  const initialQ = new URLSearchParams(window.location.search).get('q') ?? '';
  const [q, setQ] = useState(initialQ);
  const [search, setSearch] = useState(initialQ);
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const [tab, setTab] = useState<'all' | 'changes'>(new URLSearchParams(window.location.search).get('tab') === 'changes' ? 'changes' : 'all');
  const data = useApi(() => api.get<Paged<HardwareRow>>('/inventory/hardware', { q: search, page, pageSize }), [search, page]);
  return (
    <>
      <PageHead title="Hardware Inventory" />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'all'} className={tab === 'all' ? 'on' : ''} onClick={() => setTab('all')}>Devices</button>
        <button role="tab" aria-selected={tab === 'changes'} className={tab === 'changes' ? 'on' : ''} onClick={() => setTab('changes')}>Changes</button>
      </div>
      {tab === 'changes' ? <ChangesTab kind="hardware" /> : <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search hostname, vendor, model, serial or CPU…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search hardware" />
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={(data.data?.items ?? []).map((r) => ({ ...r, id: r.assetId }))} empty={data.loading ? 'Loading…' : 'No hardware inventory yet. Install agents or run a scan with credentials.'} columns={[
          { key: 'h', label: 'Device', render: (r) => <Link to={`/hardware/${r.assetId}`}><b>{r.hostname ?? r.assetId}</b></Link> },
          { key: 'os', label: 'Operating system', className: 'wrap', render: (r) => r.osName ?? <span className="faint">{fmt.label(r.category)}</span> },
          { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (r) => [r.vendor, r.model].filter(Boolean).join(' ') || '—' },
          { key: 's', label: 'Serial number', render: (r) => <span className="mono">{r.serialNumber ?? '—'}</span> },
          { key: 'c', label: 'Processor', className: 'wrap', render: (r) => <>{r.cpu ?? '—'}{r.cpuCores ? <span className="faint"> · {r.cpuCores} cores</span> : null}</> },
          { key: 'r', label: 'Memory', render: (r) => (r.ramGb != null ? `${r.ramGb} GB` : '—') },
          { key: 'd', label: 'Disk', render: (r) => (r.diskGb ? `${r.diskGb} GB` : '—') },
          { key: 't', label: 'Type', render: (r) => fmt.label(r.chassis) },
          { key: 'u', label: 'Collected', render: (r) => <span title={fmt.date(r.collectedAt)}>{fmt.ago(r.collectedAt)}<div className="faint" style={{ fontSize: 11.5 }}>{fmt.label(r.source)}</div></span> },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
      </>}
    </>
  );
}
