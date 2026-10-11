import { useState } from 'react';
import { api } from '../../api/client';
import type { Asset, Paged } from '../../api/types';
import { useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill, statusTone } from '../../components/ui';

const CATEGORIES = ['endpoint', 'server', 'virtual_host', 'network_device', 'printer', 'iot', 'other'];

export function AssetsPage() {
  const { query } = useRouter();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState(query.get('category') ?? '');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<Asset>>('/assets', { q: search, category, status, page, pageSize, sort: 'hostname' }), [search, category, status, page]);

  return (
    <>
      <PageHead title="Asset Inventory" desc="Every device Zentriq has discovered or that was added manually. Billable assets count toward your license." />
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search hostname, serial number, OS or owner…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search assets" />
        <select className="in" value={category} onChange={(e) => { setPage(1); setCategory(e.target.value); }} aria-label="Category">
          <option value="">All categories</option>
          {CATEGORIES.map((c) => <option key={c} value={c}>{fmt.label(c)}</option>)}
        </select>
        <select className="in" value={status} onChange={(e) => { setPage(1); setStatus(e.target.value); }} aria-label="Status">
          <option value="">Active</option>
          <option value="retired">Retired</option>
          <option value="all">All statuses</option>
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data?.items ?? []} empty={data.loading ? 'Loading…' : 'No assets match. Run a discovery scan to find devices.'} columns={[
          { key: 'h', label: 'Hostname', render: (r) => <><b>{r.hostname ?? r.ip ?? '—'}</b>{r.fqdn && <div className="faint" style={{ fontSize: 11.5 }}>{r.fqdn}</div>}</> },
          { key: 'ip', label: 'IP address', render: (r) => <span className="mono">{r.ip ?? '—'}</span> },
          { key: 'c', label: 'Category', render: (r) => fmt.label(r.category) },
          { key: 'os', label: 'Operating system', render: (r) => r.osName ?? fmt.label(r.osFamily) },
          { key: 'cr', label: 'Criticality', render: (r) => fmt.label(r.criticality) },
          { key: 'inv', label: 'Inventory', render: (r) => <Pill tone={r.inventoryStatus === 'success' ? 'ok' : r.inventoryStatus === 'pending' ? '' : 'warn'}>{fmt.label(r.inventoryStatus)}</Pill> },
          { key: 'st', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
          { key: 'ls', label: 'Last seen', render: (r) => fmt.ago(r.lastSeenAt) },
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}
