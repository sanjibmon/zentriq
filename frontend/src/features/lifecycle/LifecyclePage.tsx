import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useColumns } from '../../components/columns';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Pager, Panel, Pill, type Column } from '../../components/ui';
import { useDepartments } from '../departments/Departments';
import { useLocations } from '../locations/Locations';
import { AddToContractDialog, BulkStageDialog, ContractDialog, StockDialog, useLcMeta } from './LifecycleDialogs';
import { CustomFieldsPanel, ImportDialog, PersonDialog, TagSettingsPanel, type ImportKind } from './LifecycleExtras';
import { LifecyclePanel } from './LifecyclePanel';
import {
  CONTRACT_KIND, KIND_LABEL, money, moneyShort, STAGE_ORDER, STAGE_TONE, stageLabel, statusLabel,
  type LcAsset, type LcContract, type LcEvent, type LcMeta, type LcPerson, type LcPolicy, type LcRefresh, type LcSummary,
} from './types';

type Tab = 'overview' | 'assets' | 'people' | 'contracts' | 'refresh' | 'history' | 'settings';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['assets', 'IT assets'], ['people', 'People'], ['contracts', 'Contracts'], ['refresh', 'Refresh planning'], ['history', 'History'], ['settings', 'Policies']];

export function LifecyclePage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const canEdit = can('lifecycle.manage');
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const meta = useLcMeta();
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [stock, setStock] = useState(false);
  const [upload, setUpload] = useState<ImportKind | null>(null);
  const done = (text: string) => { setMsg({ tone: 'info', text }); setStock(false); setTick((x) => x + 1); };
  const m = meta.data;
  return (
    <>
      <PageHead title="Asset Lifecycle" actions={<>
        {canEdit && m && <button className="btn" onClick={() => setUpload('assets')}><Icon name="upload" size={13} /> Bulk upload</button>}
        {canEdit && m && <button className="btn primary" onClick={() => setStock(true)}><Icon name="plus" size={13} /> Add new assets</button>}
      </>} />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/lifecycle?tab=${t}`, true)}>{l}</button>)}
      </div>
      <ErrorBox error={meta.error} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {!m ? <Loading /> : <>
        {tab === 'overview' && <Overview meta={m} tick={tick} />}
        {tab === 'assets' && <Assets meta={m} tick={tick} onMsg={done} />}
        {tab === 'people' && <People meta={m} tick={tick} onMsg={done} onUpload={() => setUpload('people')} />}
        {tab === 'contracts' && <Contracts meta={m} tick={tick} onMsg={done} onUpload={() => setUpload('contracts')} />}
        {tab === 'refresh' && <Refresh meta={m} />}
        {tab === 'history' && <History meta={m} />}
        {tab === 'settings' && <Policies meta={m} onSaved={() => { meta.reload(); done('Saved.'); }} onReload={meta.reload} />}
      </>}
      {stock && m && <StockDialog meta={m} onClose={() => setStock(false)} onDone={done} />}
      {upload && <ImportDialog kind={upload} onClose={() => setUpload(null)} onDone={done} />}
    </>
  );
}

// ---- overview ------------------------------------------------------------------------------------------------------

function Overview({ meta, tick }: { meta: LcMeta; tick: number }) {
  const s = useApi(() => api.get<LcSummary>('/lifecycle/summary'), [tick]);
  if (s.error) return <ErrorBox error={s.error} />;
  const d = s.data;
  if (!d) return <Loading />;
  const max = Math.max(1, ...STAGE_ORDER.map((x) => d.stages[x] ?? 0));
  return (
    <>
      <Kpis items={[
        { label: 'IT assets', value: d.total.toLocaleString(), sub: `${(d.stages.in_use ?? 0).toLocaleString()} in use · ${(d.stages.in_stock ?? 0).toLocaleString()} in stock`, to: '/lifecycle?tab=assets' },
        { label: 'Book value', value: <span title={money(d.bookValue, d.currency)}>{moneyShort(d.bookValue, d.currency)}</span>, sub: `of ${moneyShort(d.purchaseValue, d.currency)} cost · ${d.withCost} with a cost` },
        { label: 'Warranty ending', value: d.warranty90.toLocaleString(), sub: `${d.warranty30} within 30 days · ${d.warrantyExpired} in service expired`, to: '/lifecycle?tab=assets&warranty=expiring' },
        { label: 'Contracts ending', value: d.contracts90.toLocaleString(), sub: 'within 90 days', to: '/lifecycle?tab=contracts' },
        { label: 'Due for refresh', value: d.refreshDue.toLocaleString(), sub: 'age policy or OS out of support', to: '/lifecycle?tab=refresh' },
        { label: 'Missing', value: (d.stages.missing ?? 0).toLocaleString(), sub: `${d.loansOverdue} loan(s) overdue`, to: '/lifecycle?tab=assets&stage=missing' },
      ]} />
      <div className="grid-2">
        <Panel title="Lifecycle stages" sub="IT assets in each stage">
          <div className="lc-funnel">
            {STAGE_ORDER.map((st) => (
              <Link key={st} to={`/lifecycle?tab=assets&stage=${st}`} className="lc-funnel-row">
                <span className="l">{stageLabel(meta, st)}</span>
                <span className="bar"><i className={`t-${STAGE_TONE[st] || 'muted'}`} style={{ width: `${((d.stages[st] ?? 0) / max) * 100}%` }} /></span>
                <b>{(d.stages[st] ?? 0).toLocaleString()}</b>
              </Link>
            ))}
          </div>
        </Panel>
        <Panel title="Value by device class" flush>
          <DataTable rows={d.byClass.map((c) => ({ ...c, id: c.class }))} empty="No IT assets." columns={[
            { key: 'c', label: 'Class', render: (r) => <Link to={`/lifecycle?tab=assets&class=${r.class}`}>{r.label}</Link> },
            { key: 'n', label: 'Assets', render: (r) => r.n.toLocaleString() },
            { key: 'v', label: 'Book value', render: (r) => money(r.bookValue, d.currency) },
          ]} />
        </Panel>
      </div>
      <div className="grid-2">
        <Panel title="Needs attention">
          <ul className="lc-attn">
            <li><Link to="/lifecycle?tab=assets&untagged=1">{d.untagged.toLocaleString()} asset(s) without an asset tag</Link></li>
            <li><Link to="/lifecycle?tab=assets&stage=in_use&status=in_use">{d.unassigned.toLocaleString()} laptop(s) / desktop(s) / mobile(s) in use but not assigned to a person</Link></li>
            <li><Link to="/lifecycle?tab=assets&warranty=none">{d.noWarranty.toLocaleString()} asset(s) in use or in stock without warranty dates</Link></li>
            <li><Link to="/lifecycle?tab=assets&warranty=expired">{d.warrantyExpired.toLocaleString()} asset(s) in use or in stock with an expired warranty</Link></li>
            <li><Link to="/lifecycle?tab=assets&status=loaned">{d.loansOverdue.toLocaleString()} loan(s) past the return date</Link></li>
          </ul>
        </Panel>
        <Panel title="Recent changes" flush>
          <DataTable rows={d.recent} empty="No lifecycle changes yet." columns={[
            { key: 'at', label: 'When', render: (e) => fmt.ago(e.at) },
            { key: 'a', label: 'Asset', render: (e) => <Link to={`/lifecycle/assets/${e.assetId}`}>{e.assetTag || e.name || '—'}</Link> },
            { key: 'k', label: 'Change', render: (e) => <>{KIND_LABEL[e.kind] ?? e.kind}{e.toStage && e.toStage !== e.fromStage ? ` → ${stageLabel(meta, e.toStage)}` : ''}{e.person ? <span className="faint"> · {e.person}</span> : null}</> },
          ]} />
        </Panel>
      </div>
    </>
  );
}

// ---- assets --------------------------------------------------------------------------------------------------------

type Row = LcAsset & { total?: number };

function Assets({ meta, tick, onMsg }: { meta: LcMeta; tick: number; onMsg: (m: string) => void }) {
  const { query } = useRouter();
  const { can } = useAuth();
  const canEdit = can('lifecycle.manage');
  const deps = useDepartments();
  const locs = useLocations();
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(query.get('q') ?? '');
  const [stage, setStage] = useState(query.get('stage') ?? '');
  const [status, setStatus] = useState(query.get('status') ?? '');
  const [cls, setCls] = useState(query.get('class') ?? '');
  const [dep, setDep] = useState('');
  const [loc, setLoc] = useState('');
  const [warranty, setWarranty] = useState(query.get('warranty') ?? '');
  const [untagged] = useState(query.get('untagged') === '1');
  const [sort, setSort] = useState('name');
  const [page, setPage] = useState(1);
  const [picked, setPicked] = useState<string[]>([]);
  const [dialog, setDialog] = useState<'stage' | 'contract' | null>(null);
  const [busy, setBusy] = useState(false);
  const [local, setLocal] = useState(0);
  const pageSize = 50;
  const data = useApi(() => api.get<{ items: Row[]; total: number }>('/lifecycle/assets', {
    q: search, stage, status, class: cls, departmentId: dep, locationId: loc, warranty, untagged: untagged || undefined, sort, page, pageSize,
  }), [search, stage, status, cls, dep, loc, warranty, untagged, sort, page, tick, local]);
  const rows = data.data?.items ?? [];
  const reset = () => { setPage(1); setPicked([]); };
  const allPicked = rows.length > 0 && rows.every((r) => picked.includes(r.id));
  const changed = (m: string) => { setDialog(null); setPicked([]); setLocal((x) => x + 1); onMsg(m); };
  async function tagSelected() {
    setBusy(true);
    try { const r = await api.post<{ tags: Record<string, string> }>('/lifecycle/tags', { assetIds: picked }); changed(`${Object.keys(r.tags).length} asset(s) have a tag.`); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  const all: Column<Row>[] = [
    ...(canEdit ? [{
      key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all on this page" checked={allPicked} onChange={(e) => setPicked(e.target.checked ? rows.map((r) => r.id) : [])} />,
      render: (r: Row) => <input type="checkbox" className="row-sel" aria-label={`Select ${r.assetTag ?? r.name}`} checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id))} />,
    }] : []),
    { key: 'tag', label: 'Asset tag', render: (r) => r.assetTag ? <span className="mono"><b>{r.assetTag}</b></span> : <span className="muted">—</span> },
    { key: 'name', label: 'Device', className: 'wrap', render: (r) => <Link to={`/lifecycle/assets/${r.id}`}><b>{r.name ?? '—'}</b></Link> },
    { key: 'model', label: 'Model', className: 'wrap', render: (r) => [r.manufacturer, r.model].filter(Boolean).join(' ') || '—' },
    { key: 'serial', label: 'Serial number', render: (r) => <span className="mono">{r.serial ?? '—'}</span> },
    { key: 'class', label: 'Class', render: (r) => r.classLabel ?? '—' },
    { key: 'stage', label: 'Stage', render: (r) => <><Pill tone={STAGE_TONE[r.stage]}>{stageLabel(meta, r.stage)}</Pill><div className="faint" style={{ fontSize: 11.5 }}>{statusLabel(meta, r.status)}</div></> },
    { key: 'person', label: 'Assigned to', render: (r) => r.assignedTo ? <>{r.assignedTo}{r.expectedReturn ? <div className="faint" style={{ fontSize: 11.5 }}>loan until {fmt.day(r.expectedReturn)}</div> : null}</> : <span className="muted">—</span> },
    { key: 'dep', label: 'Department', optional: true, render: (r) => r.department ?? '—' },
    { key: 'loc', label: 'Location', render: (r) => r.location ?? '—' },
    { key: 'warranty', label: 'Warranty', render: (r) => r.warrantyEnd ? <span style={{ color: r.warrantyDays != null && r.warrantyDays < 0 ? 'var(--crit)' : r.warrantyDays != null && r.warrantyDays <= 90 ? 'var(--warn)' : undefined }}>{fmt.day(r.warrantyEnd)}</span> : <span className="muted">—</span> },
    { key: 'cost', label: 'Cost', optional: true, render: (r) => money(r.purchaseCost, r.currency ?? meta.currency) },
    { key: 'book', label: 'Book value', render: (r) => money(r.bookValue, r.currency ?? meta.currency) },
    { key: 'age', label: 'Age', optional: true, render: (r) => r.ageMonths != null ? <>{Math.floor(r.ageMonths / 12)}y {r.ageMonths % 12}m{r.refreshDue ? <> <Pill tone="warn">refresh</Pill></> : null}</> : '—' },
    { key: 'vendor', label: 'Vendor', optional: true, render: (r) => r.vendor ?? '—' },
    { key: 'po', label: 'PO / invoice', optional: true, render: (r) => [r.poNumber, r.invoiceNumber].filter(Boolean).join(' · ') || '—' },
    { key: 'seen', label: 'Last seen', optional: true, render: (r) => fmt.ago(r.lastSeenAt) },
  ];
  const { columns, chooser } = useColumns('lifecycle', all);
  const statuses = meta.stages.find((s) => s.id === stage)?.statuses ?? [];
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); reset(); setSearch(q); }}>
        <input className="in grow" placeholder="Search tag, device, serial, model, person, vendor, PO…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search IT assets" />
        <select className="in" value={stage} onChange={(e) => { reset(); setStage(e.target.value); setStatus(''); }} aria-label="Stage">
          <option value="">All stages except disposed</option>
          {meta.stages.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
          <option value="all">All, including disposed</option>
        </select>
        {statuses.length > 0 && <select className="in" value={status} onChange={(e) => { reset(); setStatus(e.target.value); }} aria-label="Status">
          <option value="">All statuses</option>
          {statuses.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
        </select>}
        <select className="in" value={cls} onChange={(e) => { reset(); setCls(e.target.value); }} aria-label="Class">
          <option value="">All classes</option>
          {meta.policies.map((p) => <option key={p.class} value={p.class}>{p.label}</option>)}
        </select>
        <select className="in" value={dep} onChange={(e) => { reset(); setDep(e.target.value); }} aria-label="Department">
          <option value="">All departments</option>
          {(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <select className="in" value={loc} onChange={(e) => { reset(); setLoc(e.target.value); }} aria-label="Location">
          <option value="">All locations</option>
          {(locs.data?.items ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </select>
        <select className="in" value={warranty} onChange={(e) => { reset(); setWarranty(e.target.value); }} aria-label="Warranty">
          <option value="">Any warranty</option>
          <option value="active">Warranty active</option>
          <option value="expiring">Ending in 90 days</option>
          <option value="expired">Expired</option>
          <option value="none">Not recorded</option>
        </select>
        <select className="in" value={sort} onChange={(e) => { reset(); setSort(e.target.value); }} aria-label="Sort">
          <option value="name">Sort: device</option><option value="tag">Sort: tag</option><option value="stage">Sort: stage</option>
          <option value="person">Sort: person</option><option value="warranty">Sort: warranty end</option><option value="value">Sort: book value</option>
          <option value="age">Sort: age</option><option value="changed">Sort: recently changed</option>
        </select>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      {canEdit && picked.length > 0 && <div className="bulk-bar">
        <b>{picked.length} selected</b>
        <button className="btn sm" onClick={() => setDialog('stage')}>Change stage</button>
        <button className="btn sm" disabled={busy} onClick={() => void tagSelected()}>Give asset tags</button>
        <button className="btn sm" onClick={() => setDialog('contract')}>Add to contract</button>
        <Link className="btn sm" to={`/lifecycle/labels?ids=${picked.join(',')}`}><Icon name="report" size={13} /> Print labels</Link>
        <button className="btn sm ghost" onClick={() => setPicked([])}>Clear</button>
      </div>}
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} columns={columns} empty={data.loading ? 'Loading…' : 'No IT assets match.'} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={(p) => { setPage(p); setPicked([]); }} />}
      </Panel>
      {dialog === 'stage' && <BulkStageDialog ids={picked} meta={meta} onClose={() => setDialog(null)} onDone={changed} />}
      {dialog === 'contract' && <AddToContractDialog ids={picked} onClose={() => setDialog(null)} onDone={changed} />}
    </>
  );
}

// ---- people --------------------------------------------------------------------------------------------------------

function People({ meta, tick, onMsg, onUpload }: { meta: LcMeta; tick: number; onMsg: (m: string) => void; onUpload: () => void }) {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const canEdit = can('lifecycle.manage');
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('active');
  const [holding, setHolding] = useState(false);
  const [local, setLocal] = useState(0);
  const [edit, setEdit] = useState<LcPerson | 'new' | null>(null);
  const person = query.get('person');
  const people = useApi(() => api.get<LcPerson[]>('/lifecycle/people', { q: search, status: status || undefined, holding: holding || undefined }), [search, status, holding, tick, local]);
  if (person) return <PersonHoldings person={person} meta={meta} onBack={() => navigate('/lifecycle?tab=people', true)} />;
  async function remove(p: LcPerson) {
    if (!p.id) return;
    if (!(await confirm({ title: 'Remove person', message: `Remove ${p.person} from People? Their history stays on the assets.`, confirmLabel: 'Remove', danger: true }))) return;
    try { await api.del(`/lifecycle/people/${p.id}`); setLocal((x) => x + 1); onMsg(`${p.person} removed.`); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e)); }
  }
  const rows = people.data ?? [];
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search name, e-mail, employee ID, department, job title…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search people" />
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="active">Active</option><option value="left">Left the organisation</option><option value="">Everyone</option>
        </select>
        <label className="check-inline"><input type="checkbox" checked={holding} onChange={(e) => setHolding(e.target.checked)} /> Holding assets</label>
        <button className="btn" type="submit">Search</button>
        {canEdit && <span style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
          <button type="button" className="btn" onClick={onUpload}><Icon name="upload" size={13} /> Bulk upload</button>
          <button type="button" className="btn primary" onClick={() => setEdit('new')}><Icon name="plus" size={13} /> Add person</button>
        </span>}
      </form>
      <ErrorBox error={people.error} />
      <Panel flush sub={`${rows.length} people — the directory used for assignments, with the IT assets and returnable accessories they hold`}>
        <DataTable rows={rows.map((p) => ({ ...p, id: p.id ?? 'name:' + p.person, pid: p.id }))} empty={people.loading ? 'Loading…' : 'Nobody yet. Add people one by one or with Bulk upload; assigning an asset to a new name adds the person too.'} columns={[
          { key: 'p', label: 'Person', render: (r) => <><Link to={`/lifecycle?tab=people&person=${encodeURIComponent(r.person)}`}><b>{r.person}</b></Link>{r.status === 'left' && <> <Pill tone="warn">left</Pill></>}
            {r.title && <div className="faint" style={{ fontSize: 11.5 }}>{r.title}</div>}</> },
          { key: 'id', label: 'Employee ID', render: (r) => r.employeeId ? <span className="mono">{r.employeeId}</span> : '—' },
          { key: 'e', label: 'E-mail', render: (r) => r.email ?? '—' },
          { key: 'd', label: 'Department', render: (r) => r.department ?? '—' },
          { key: 'loc', label: 'Location', render: (r) => r.location ?? '—' },
          { key: 'a', label: 'IT assets', render: (r) => r.assets || '—' },
          { key: 'l', label: 'On loan', render: (r) => r.loans ? <>{r.loans}{r.overdue ? <> <Pill tone="crit">{r.overdue} overdue</Pill></> : null}</> : '—' },
          { key: 'x', label: 'Accessories', render: (r) => r.accessories || '—' },
          { key: 'v', label: 'Book value', render: (r) => r.value ? money(r.value, meta.currency) : '—' },
          ...(canEdit ? [{ key: 'act', label: '', render: (r: LcPerson & { id: string; pid: string | null }) => <span style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
            <button className="btn sm" onClick={() => setEdit({ ...r, id: r.pid })} aria-label={`Edit ${r.person}`}><Icon name="edit" size={13} /></button>
            {r.assets === 0 && r.pid && <button className="btn sm ghost" onClick={() => void remove({ ...r, id: r.pid })} aria-label={`Remove ${r.person}`}><Icon name="trash" size={13} /></button>}
          </span> }] : []),
        ]} />
      </Panel>
      {edit && <PersonDialog person={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onDone={(m) => { setEdit(null); setLocal((x) => x + 1); onMsg(m); }} />}
    </>
  );
}

function PersonHoldings({ person, meta, onBack }: { person: string; meta: LcMeta; onBack: () => void }) {
  const h = useApi(() => api.get<{ assets: LcAsset[]; accessories: { item: string; category: string; quantity: number; lastIssued: string | null; serials: string[] | null }[] }>('/lifecycle/people/holdings', { person }), [person]);
  return (
    <>
      <div className="toolbar-row">
        <button className="btn" onClick={onBack}>← All people</button>
        <h3 style={{ margin: 0 }}>{person}</h3>
        <span style={{ marginLeft: 'auto' }} />
        <Link className="btn" to={`/lifecycle/handover?person=${encodeURIComponent(person)}&kind=handover`}>Handover form</Link>
        <Link className="btn" to={`/lifecycle/handover?person=${encodeURIComponent(person)}&kind=return`}>Return form (leaving)</Link>
      </div>
      <ErrorBox error={h.error} />
      <Panel title="IT assets" flush>
        <DataTable rows={h.data?.assets ?? []} empty={h.loading ? 'Loading…' : 'No IT assets.'} columns={[
          { key: 't', label: 'Asset tag', render: (r) => <span className="mono">{r.assetTag ?? '—'}</span> },
          { key: 'n', label: 'Device', render: (r) => <Link to={`/lifecycle/assets/${r.id}`}>{r.name}</Link> },
          { key: 'm', label: 'Model', render: (r) => [r.manufacturer, r.model].filter(Boolean).join(' ') || '—' },
          { key: 's', label: 'Serial', render: (r) => <span className="mono">{r.serial ?? '—'}</span> },
          { key: 'st', label: 'Stage', render: (r) => <>{stageLabel(meta, r.stage)} · {statusLabel(meta, r.status)}</> },
          { key: 'since', label: 'Since', render: (r) => fmt.day(r.assignedAt) },
          { key: 'r', label: 'Loan ends', render: (r) => r.expectedReturn ? fmt.day(r.expectedReturn) : '—' },
        ]} />
      </Panel>
      <Panel title="IT accessories (returnable)" flush>
        <DataTable rows={(h.data?.accessories ?? []).map((a, i) => ({ ...a, id: i }))} empty="None." columns={[
          { key: 'i', label: 'Accessory', render: (r) => r.item },
          { key: 'c', label: 'Category', render: (r) => r.category },
          { key: 'q', label: 'Quantity', render: (r) => r.quantity },
          { key: 's', label: 'Serials', render: (r) => (r.serials ?? []).join(', ') || '—' },
          { key: 'd', label: 'Last issued', render: (r) => fmt.day(r.lastIssued) },
        ]} />
      </Panel>
    </>
  );
}

// ---- contracts -----------------------------------------------------------------------------------------------------

function Contracts({ meta, tick, onMsg, onUpload }: { meta: LcMeta; tick: number; onMsg: (m: string) => void; onUpload: () => void }) {
  const { can } = useAuth();
  const canEdit = can('lifecycle.manage');
  const confirm = useConfirm();
  const [local, setLocal] = useState(0);
  const list = useApi(() => api.get<LcContract[]>('/lifecycle/contracts'), [tick, local]);
  const [editing, setEditing] = useState<LcContract | 'new' | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const detail = useApi(() => (open ? api.get<{ contract: LcContract; assets: LcAsset[] }>(`/lifecycle/contracts/${open}`) : Promise.resolve(null)), [open, local]);
  async function remove(c: LcContract) {
    if (!(await confirm({ title: 'Delete contract', message: `Delete "${c.name}"? The assets stay; only the contract and its links are removed.`, confirmLabel: 'Delete', danger: true }))) return;
    try { await api.del(`/lifecycle/contracts/${c.id}`); setOpen(null); setLocal((x) => x + 1); onMsg('Contract deleted.'); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e)); }
  }
  async function unlink(assetId: string) {
    if (!open) return;
    try { await api.post(`/lifecycle/contracts/${open}/assets`, { remove: [assetId] }); setLocal((x) => x + 1); }
    catch (e) { onMsg(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <>
      <div className="toolbar-row">
        <span className="muted">Warranty extensions, AMC, support, lease and insurance contracts that cover many assets. Reminders go out before they end (Integrations).</span>
        <span style={{ marginLeft: 'auto' }} />
        {canEdit && <button className="btn" onClick={onUpload}><Icon name="upload" size={13} /> Bulk upload</button>}
        {canEdit && <button className="btn primary" onClick={() => setEditing('new')}><Icon name="plus" size={13} /> New contract</button>}
      </div>
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={list.data ?? []} empty={list.loading ? 'Loading…' : 'No contracts yet.'} columns={[
          { key: 'n', label: 'Contract', render: (c) => <button className="linkish" onClick={() => setOpen(c.id)}><b>{c.name}</b></button> },
          { key: 'k', label: 'Type', render: (c) => CONTRACT_KIND[c.kind] ?? c.kind },
          { key: 'v', label: 'Vendor', render: (c) => c.vendor ?? '—' },
          { key: 'r', label: 'Reference', render: (c) => c.reference ?? '—' },
          { key: 'e', label: 'Ends', render: (c) => c.endDate ? <>{fmt.day(c.endDate)} {c.daysLeft != null && c.daysLeft < 0 ? <Pill tone="crit">ended</Pill> : c.daysLeft != null && c.daysLeft <= 90 ? <Pill tone="warn">{c.daysLeft} days</Pill> : null}</> : '—' },
          { key: 'a', label: 'Assets', render: (c) => c.assets ?? 0 },
          { key: 'c', label: 'Cost', render: (c) => money(c.cost ?? null, c.currency ?? meta.currency) },
          ...(canEdit ? [{ key: 'x', label: '', render: (c: LcContract) => <span style={{ display: 'flex', gap: 4 }}>
            <button className="icon-btn" aria-label="Edit" onClick={() => setEditing(c)}><Icon name="edit" size={14} /></button>
            <button className="icon-btn" aria-label="Delete" onClick={() => void remove(c)}><Icon name="trash" size={14} /></button></span> }] : []),
        ]} />
      </Panel>
      {open && detail.data && <Panel title={`Assets covered by ${detail.data.contract.name}`} sub="Add assets from the IT assets tab: select › Add to contract" flush
        right={<button className="btn sm" onClick={() => setOpen(null)}>Close</button>}>
        <DataTable rows={detail.data.assets} empty="No assets on this contract yet." columns={[
          { key: 't', label: 'Asset tag', render: (r) => <span className="mono">{r.assetTag ?? '—'}</span> },
          { key: 'n', label: 'Device', render: (r) => <Link to={`/lifecycle/assets/${r.id}`}>{r.name}</Link> },
          { key: 's', label: 'Serial', render: (r) => <span className="mono">{r.serial ?? '—'}</span> },
          { key: 'st', label: 'Stage', render: (r) => stageLabel(meta, r.stage) },
          ...(canEdit ? [{ key: 'x', label: '', render: (r: LcAsset) => <button className="btn sm ghost" onClick={() => void unlink(r.id)}>Remove</button> }] : []),
        ]} />
      </Panel>}
      {editing && <ContractDialog contract={editing === 'new' ? null : editing} meta={meta} onClose={() => setEditing(null)}
        onDone={(m, id) => { setEditing(null); setLocal((x) => x + 1); setOpen(id); onMsg(m); }} />}
    </>
  );
}

// ---- refresh planning ------------------------------------------------------------------------------------------------

function Refresh({ meta }: { meta: LcMeta }) {
  const r = useApi(() => api.get<LcRefresh>('/lifecycle/refresh'), []);
  if (r.error) return <ErrorBox error={r.error} />;
  if (!r.data) return <Loading />;
  const reason = (x: string) => (x === 'age' ? 'Age' : x === 'warranty' ? 'Warranty ended' : 'OS out of support');
  return (
    <>
      <Panel title="Replacement forecast" sub="Devices reaching their refresh age (Policies) per year, with the purchase cost (or the average cost of the class)" flush>
        <DataTable rows={r.data.forecast.map((f) => ({ ...f, id: f.year }))} empty="No refresh policy, or nothing due in the next five years." columns={[
          { key: 'y', label: 'Year', render: (f) => <b>{f.year}{f.year === new Date().getFullYear() ? ' (incl. overdue)' : ''}</b> },
          { key: 'd', label: 'Devices', render: (f) => f.devices.toLocaleString() },
          { key: 'c', label: 'Estimated budget', render: (f) => <>{money(f.estimatedCost, meta.currency)}{f.withoutCost ? <span className="faint"> · {f.withoutCost} without any cost</span> : null}</> },
        ]} />
      </Panel>
      <Panel title={`Refresh candidates (${r.data.candidates.length})`} sub="In use or in stock: past the refresh age, warranty ended within a year of the refresh date, or operating system out of support" flush>
        <DataTable rows={r.data.candidates} empty="Nothing to refresh." columns={[
          { key: 't', label: 'Asset tag', render: (c) => <span className="mono">{c.assetTag ?? '—'}</span> },
          { key: 'n', label: 'Device', render: (c) => <Link to={`/lifecycle/assets/${c.id}`}>{c.name}</Link> },
          { key: 'm', label: 'Model', render: (c) => c.model ?? '—' },
          { key: 'p', label: 'Assigned to', render: (c) => c.assignedTo ?? '—' },
          { key: 'a', label: 'Age', render: (c) => c.ageMonths != null ? `${Math.floor(c.ageMonths / 12)}y ${c.ageMonths % 12}m` : '—' },
          { key: 'o', label: 'OS', className: 'wrap', render: (c) => c.osName ?? '—' },
          { key: 'r', label: 'Why', render: (c) => c.reasons.map((x) => <Pill key={x} tone="warn">{reason(x)}</Pill>) },
          { key: 'd', label: 'Due', render: (c) => fmt.day(c.dueOn) },
        ]} />
      </Panel>
    </>
  );
}

// ---- history -------------------------------------------------------------------------------------------------------

function History({ meta }: { meta: LcMeta }) {
  const [page, setPage] = useState(1);
  const [kind, setKind] = useState('');
  const ev = useApi(() => api.get<{ items: LcEvent[]; total: number }>('/lifecycle/events', { kind, page, pageSize: 50 }), [kind, page]);
  return (
    <>
      <div className="toolbar-row">
        <select className="in" value={kind} onChange={(e) => { setPage(1); setKind(e.target.value); }} aria-label="Change">
          <option value="">All changes</option>
          {Object.entries(KIND_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
      </div>
      <ErrorBox error={ev.error} />
      <Panel flush>
        <DataTable rows={ev.data?.items ?? []} empty={ev.loading ? 'Loading…' : 'No changes.'} columns={[
          { key: 'at', label: 'When', render: (e) => fmt.date(e.at) },
          { key: 'a', label: 'Asset', render: (e) => <Link to={`/lifecycle/assets/${e.assetId}`}>{e.assetTag || e.name || '—'}</Link> },
          { key: 'k', label: 'Change', render: (e) => <>{KIND_LABEL[e.kind] ?? e.kind}{e.toStage && e.toStage !== e.fromStage ? <span className="faint"> · {e.fromStage ? `${stageLabel(meta, e.fromStage)} → ` : ''}{stageLabel(meta, e.toStage)}</span> : null}</> },
          { key: 'p', label: 'Person', render: (e) => e.person ?? '—' },
          { key: 'r', label: 'Reason', className: 'wrap', render: (e) => e.reason ?? '—' },
          { key: 'b', label: 'By', render: (e) => e.actor ?? '—' },
        ]} />
        {ev.data && <Pager page={page} pageSize={50} total={ev.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}

// ---- policies and settings -----------------------------------------------------------------------------------------

function Policies({ meta, onSaved, onReload }: { meta: LcMeta; onSaved: () => void; onReload: () => void }) {
  const { can } = useAuth();
  const canEdit = can('settings.manage');
  const [rows, setRows] = useState<LcPolicy[]>(meta.policies);
  const [s, setS] = useState({ currency: meta.currency, notSeenDays: String(meta.notSeenDays) });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => setRows(meta.policies), [meta.policies]);
  const set = (cls: string, k: keyof LcPolicy, v: string) => setRows((r) => r.map((p) => (p.class === cls ? { ...p, [k]: k === 'tagPrefix' || k === 'depreciationMethod' ? v : Number(v) } : p)));
  async function save() {
    setBusy(true); setError(null);
    try {
      for (const p of rows) {
        const o = meta.policies.find((x) => x.class === p.class);
        if (o && JSON.stringify(o) === JSON.stringify(p)) continue;
        await api.put(`/lifecycle/policies/${p.class}`, { tagPrefix: p.tagPrefix, usefulLifeMonths: p.usefulLifeMonths, depreciationMethod: p.depreciationMethod, salvagePercent: p.salvagePercent, refreshYears: p.refreshYears });
      }
      await api.put('/lifecycle/settings', { currency: s.currency, notSeenDays: Number(s.notSeenDays) });
      onSaved();
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <TagSettingsPanel meta={meta} onSaved={onReload} />
      <CustomFieldsPanel meta={meta} onSaved={onReload} />
      <Panel title="Policies per device class" sub="Asset tag prefix, depreciation (unless set on the asset) and the age at which devices are due for replacement" flush>
        <div className="tbl-wrap"><table className="t">
          <thead><tr><th>Class</th><th>Tag prefix</th><th>Depreciation</th><th>Useful life (months)</th><th>Salvage %</th><th>Refresh after (years)</th></tr></thead>
          <tbody>{rows.map((p) => (
            <tr key={p.class}>
              <td><b>{p.label}</b></td>
              <td><input className="in mono" style={{ width: 90 }} value={p.tagPrefix} disabled={!canEdit} onChange={(e) => set(p.class, 'tagPrefix', e.target.value.toUpperCase())} maxLength={8} /></td>
              <td><select className="in" value={p.depreciationMethod} disabled={!canEdit} onChange={(e) => set(p.class, 'depreciationMethod', e.target.value)}>
                {meta.depreciationMethods.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}</select></td>
              <td><input className="in" type="number" min={0} max={600} style={{ width: 90 }} value={p.usefulLifeMonths} disabled={!canEdit} onChange={(e) => set(p.class, 'usefulLifeMonths', e.target.value)} /></td>
              <td><input className="in" type="number" min={0} max={100} step="0.5" style={{ width: 80 }} value={p.salvagePercent} disabled={!canEdit} onChange={(e) => set(p.class, 'salvagePercent', e.target.value)} /></td>
              <td><input className="in" type="number" min={0} max={30} step="0.5" style={{ width: 80 }} value={p.refreshYears} disabled={!canEdit} onChange={(e) => set(p.class, 'refreshYears', e.target.value)} /></td>
            </tr>))}
          </tbody>
        </table></div>
      </Panel>
      <Panel title="Settings">
        <div className="form-v"><div className="grid-2 tight">
          <label>Currency<input className="in mono" value={s.currency} maxLength={3} disabled={!canEdit} onChange={(e) => setS({ ...s, currency: e.target.value.toUpperCase() })} /></label>
          <label>Mark as missing when not seen for (days, 0 = never)<input className="in" type="number" min={0} max={3650} value={s.notSeenDays} disabled={!canEdit} onChange={(e) => setS({ ...s, notSeenDays: e.target.value })} /></label>
        </div></div>
        {canEdit && <div style={{ marginTop: 12 }}><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>}
      </Panel>
    </>
  );
}

// ---- one asset -----------------------------------------------------------------------------------------------------

export function LifecycleAssetPage({ id }: { id: string }) {
  const d = useApi(() => api.get<{ asset: LcAsset }>(`/lifecycle/assets/${id}`), [id]);
  const a = d.data?.asset;
  return (
    <>
      <PageHead title={a ? `${a.assetTag ? `${a.assetTag} · ` : ''}${a.name ?? 'IT asset'}` : 'IT asset'}
        desc={a ? [a.manufacturer, a.model, a.serial ? `S/N ${a.serial}` : null].filter(Boolean).join(' · ') : undefined}
        actions={<>
          {a && a.assetStatus === 'active' && <Link className="btn" to={`/hardware/${id}`}>Inventory details</Link>}
          {a && <Link className="btn" to={`/lifecycle/labels?ids=${id}`}>Print label</Link>}
          <Link className="btn" to="/lifecycle?tab=assets">All IT assets</Link>
        </>} />
      <ErrorBox error={d.error} />
      <LifecyclePanel assetId={id} onMessage={() => d.reload()} />
    </>
  );
}
