import { useState } from 'react';
import { api } from '../../api/client';
import type { HardwareRow, Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { ChangesTab } from './Changes';
import { useColumns } from '../../components/columns';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, type Column } from '../../components/ui';
import { useDepartments } from '../departments/Departments';
import { AssignOrgDialog, ManageOrgDialog, useLocations } from '../locations/Locations';

type Row = HardwareRow & { id: string };

/** A small marker when the value was entered manually (the device does not report it). */
const manualOf = (r: HardwareRow, ...fields: string[]) =>
  fields.some((f) => r.enteredManually?.includes(f)) ? <span className="faint" title="Entered manually in Zentriq — the device does not report it" style={{ fontSize: 11.5, marginLeft: 6 }}>✎ manual</span> : null;

export function HardwarePage() {
  const { can } = useAuth();
  const params = new URLSearchParams(window.location.search);
  const initialQ = params.get('q') ?? '';
  const [q, setQ] = useState(initialQ);
  const [search, setSearch] = useState(initialQ);
  const [department, setDepartment] = useState(params.get('department') ?? '');
  const [location, setLocation] = useState(params.get('location') ?? '');
  const [type, setType] = useState('');
  const [vendor, setVendor] = useState('');
  const [os, setOs] = useState('');
  const [page, setPage] = useState(1);
  const [picked, setPicked] = useState<string[]>([]);
  const [assign, setAssign] = useState(false);
  const [manage, setManage] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const pageSize = 50;
  const [tab, setTab] = useState<'all' | 'changes'>(params.get('tab') === 'changes' ? 'changes' : 'all');
  const deps = useDepartments();
  const locs = useLocations();
  const filters = useApi(() => api.get<{ types: string[]; vendors: string[]; os: string[] }>('/inventory/hardware/filters'), []);
  const data = useApi(() => api.get<Paged<HardwareRow>>('/inventory/hardware', { q: search, department, location, type, vendor, os, page, pageSize }), [search, department, location, type, vendor, os, page]);
  const rows: Row[] = (data.data?.items ?? []).map((r) => ({ ...r, id: r.assetId }));
  const reset = () => { setPage(1); setPicked([]); };
  const canEdit = can('assets.manage');
  const allPicked = rows.length > 0 && rows.every((r) => picked.includes(r.id));

  const all: Column<Row>[] = [
    ...(canEdit ? [{
      key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all on this page" checked={allPicked} onChange={(e) => setPicked(e.target.checked ? rows.map((r) => r.id) : [])} />,
      render: (r: Row) => <input type="checkbox" className="row-sel" aria-label={`Select ${r.hostname ?? r.assetId}`} checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id))} />,
    }] : []),
    { key: 'h', label: 'Device', render: (r) => <Link to={`/hardware/${r.assetId}`}><b>{r.hostname ?? r.assetId}</b></Link> },
    { key: 'os', label: 'Operating system', className: 'wrap', render: (r) => r.osName ?? <span className="faint">{fmt.label(r.category)}</span> },
    { key: 'dep', label: 'Department', render: (r) => r.department ?? <span className="muted">—</span> },
    { key: 'loc', label: 'Location', render: (r) => r.location ?? <span className="muted">—</span> },
    { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (r) => <>{[r.vendor, r.model].filter(Boolean).join(' ') || <span className="muted">Not detected</span>}{manualOf(r, 'manufacturer', 'model')}</> },
    { key: 's', label: 'Serial number', render: (r) => <>{r.serialNumber ? <span className="mono">{r.serialNumber}</span> : <span className="muted">Not detected</span>}{manualOf(r, 'serialNumber')}</> },
    { key: 'c', label: 'Processor', className: 'wrap', render: (r) => <>{r.cpu ?? '—'}{r.cpuCores ? <span className="faint"> · {r.cpuCores} cores</span> : null}</> },
    { key: 'r', label: 'Memory', render: (r) => (r.ramGb != null ? `${r.ramGb} GB` : '—') },
    { key: 'd', label: 'Disk', render: (r) => (r.diskGb ? `${r.diskGb} GB` : '—') },
    { key: 't', label: 'Type', render: (r) => fmt.label(r.chassis) },
    { key: 'ip', label: 'IP address', optional: true, render: (r) => r.ip ? <span className="mono">{r.ip}</span> : <span className="muted">—</span> },
    { key: 'cores', label: 'Cores', optional: true, render: (r) => r.cpuCores ?? '—' },
    { key: 'u', label: 'Collected', render: (r) => <span title={fmt.date(r.collectedAt)}>{fmt.ago(r.collectedAt)}<div className="faint" style={{ fontSize: 11.5 }}>{fmt.label(r.source)}</div></span> },
  ];
  const { columns, chooser } = useColumns('hardware', all);
  const f = filters.data;

  return (
    <>
      <PageHead title="Hardware Inventory" actions={tab === 'all' ? <>
        <button className="btn" onClick={() => setManage(true)}>Departments &amp; locations</button>
        {canEdit && picked.length > 0 && <button className="btn primary" onClick={() => setAssign(true)}>Department / location ({picked.length})</button>}
      </> : undefined} />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'all'} className={tab === 'all' ? 'on' : ''} onClick={() => setTab('all')}>Devices</button>
        <button role="tab" aria-selected={tab === 'changes'} className={tab === 'changes' ? 'on' : ''} onClick={() => setTab('changes')}>Changes</button>
      </div>
      {tab === 'changes' ? <ChangesTab kind="hardware" /> : <>
      {msg && <Callout icon="info">{msg}</Callout>}
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); reset(); setSearch(q); }}>
        <input className="in grow" placeholder="Search hostname, vendor, model, serial or CPU…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search hardware" />
        <select className="in" value={department} onChange={(e) => { reset(); setDepartment(e.target.value); }} aria-label="Department">
          <option value="">All departments</option>
          <option value="none">Not assigned</option>
          {(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <select className="in" value={location} onChange={(e) => { reset(); setLocation(e.target.value); }} aria-label="Location">
          <option value="">All locations</option>
          <option value="none">No location</option>
          {(locs.data?.items ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </select>
        <select className="in" value={type} onChange={(e) => { reset(); setType(e.target.value); }} aria-label="Type">
          <option value="">All types</option>
          {(f?.types ?? []).map((t) => <option key={t} value={t}>{fmt.label(t)}</option>)}
        </select>
        <select className="in" value={vendor} onChange={(e) => { reset(); setVendor(e.target.value); }} aria-label="Manufacturer">
          <option value="">All manufacturers</option>
          {(f?.vendors ?? []).map((v) => <option key={v} value={v}>{v}</option>)}
        </select>
        <select className="in" value={os} onChange={(e) => { reset(); setOs(e.target.value); }} aria-label="Operating system">
          <option value="">All operating systems</option>
          {(f?.os ?? []).map((v) => <option key={v} value={v}>{v}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No hardware inventory matches. Install agents or run an inventory scan.'} columns={columns} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={(p) => { setPage(p); setPicked([]); }} />}
      </Panel>
      </>}
      {assign && <AssignOrgDialog assetIds={picked} onClose={() => setAssign(false)} onDone={(m) => { setAssign(false); setPicked([]); setMsg(m); data.reload(); locs.reload(); deps.reload(); }} />}
      {manage && <ManageOrgDialog canEdit={canEdit} onClose={() => { setManage(false); locs.reload(); deps.reload(); data.reload(); }} />}
    </>
  );
}
