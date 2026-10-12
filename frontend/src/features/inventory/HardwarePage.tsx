import { useState } from 'react';
import { api } from '../../api/client';
import type { HardwareRow, Paged } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel } from '../../components/ui';

export function HardwarePage() {
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<HardwareRow>>('/inventory/hardware', { q: search, page, pageSize }), [search, page]);
  return (
    <>
      <PageHead title="Hardware Inventory" desc="Manufacturer, model, serial number, processor, memory and storage from agents and credentialed scans." />
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search hostname, vendor, model, serial or CPU…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search hardware" />
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={(data.data?.items ?? []).map((r) => ({ ...r, id: r.assetId }))} empty={data.loading ? 'Loading…' : 'No hardware inventory yet. Install agents or run a scan with credentials.'} columns={[
          { key: 'h', label: 'Device', render: (r) => <><Link to={`/assets/${r.assetId}`}><b>{r.hostname ?? r.assetId}</b></Link><div className="faint" style={{ fontSize: 11.5 }}>{r.osName ?? fmt.label(r.category)}</div></> },
          { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (r) => [r.vendor, r.model].filter(Boolean).join(' ') || '—' },
          { key: 's', label: 'Serial number', render: (r) => <span className="mono">{r.serialNumber ?? '—'}</span> },
          { key: 'c', label: 'Processor', className: 'wrap', render: (r) => <>{r.cpu ?? '—'}{r.cpuCores ? <span className="faint"> · {r.cpuCores} cores</span> : null}</> },
          { key: 'r', label: 'Memory', render: (r) => (r.ramGb != null ? `${r.ramGb} GB` : '—') },
          { key: 'd', label: 'Disk', render: (r) => (r.diskGb ? `${r.diskGb} GB` : '—') },
          { key: 't', label: 'Type', render: (r) => fmt.label(r.chassis) },
          { key: 'u', label: 'Collected', render: (r) => <span title={fmt.date(r.collectedAt)}>{fmt.ago(r.collectedAt)} · {fmt.label(r.source)}</span> },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}
