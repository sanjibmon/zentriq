import { useState } from 'react';
import { api } from '../../api/client';
import type { Paged, SoftwareInstall, SoftwareRow } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Pager, Panel } from '../../components/ui';

export function SoftwarePage() {
  const initialQ = new URLSearchParams(window.location.search).get('q') ?? '';
  const [q, setQ] = useState(initialQ);
  const [search, setSearch] = useState(initialQ);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<SoftwareRow>>('/inventory/software', { q: search, page, pageSize }), [search, page]);
  const installs = useApi(() => (selected ? api.get<SoftwareInstall[]>('/inventory/software/installs', { name: selected }) : Promise.resolve(null)), [selected]);

  return (
    <>
      <PageHead title="Software Inventory" desc="Installed applications reported by agents and credentialed (agentless) scans, grouped by product." />
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search product or publisher…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search software" />
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <div className="grid-2" style={{ gridTemplateColumns: selected ? 'minmax(0,3fr) minmax(0,2fr)' : '1fr' }}>
        <Panel flush>
          <DataTable rows={(data.data?.items ?? []).map((r) => ({ ...r, id: r.name }))} empty={data.loading ? 'Loading…' : 'No software inventory yet. Install agents or run a scan with credentials.'} columns={[
            { key: 'n', label: 'Product', render: (r) => <button className="linkish" onClick={() => setSelected(r.name)}><b>{r.name}</b></button> },
            { key: 'p', label: 'Publisher', render: (r) => r.publisher ?? '—' },
            { key: 'i', label: 'Installs', render: (r) => r.installs.toLocaleString() },
            { key: 'v', label: 'Versions', className: 'wrap', render: (r) => r.versions > 0 ? <span title={r.latestVersions.join(', ')}>{r.versions} ({r.latestVersions.slice(0, 2).join(', ')}{r.versions > 2 ? '…' : ''})</span> : '—' },
          ]} />
          {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
        </Panel>
        {selected && (
          <Panel title={selected} sub={installs.data ? `${installs.data.length} device(s)` : ''} right={<button className="btn sm" onClick={() => setSelected(null)}>Close</button>} flush>
            <ErrorBox error={installs.error} />
            <DataTable rows={(installs.data ?? []).map((r, i) => ({ ...r, id: `${r.assetId}-${i}` }))} empty={installs.loading ? 'Loading…' : 'None.'} columns={[
              { key: 'h', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}>{r.hostname ?? r.ip ?? r.assetId}</Link> },
              { key: 'v', label: 'Version', render: (r) => r.version ?? '—' },
              { key: 'd', label: 'Installed', render: (r) => fmt.day(r.installedOn) },
              { key: 's', label: 'Source', render: (r) => fmt.label(r.source) },
            ]} />
          </Panel>
        )}
      </div>
    </>
  );
}
