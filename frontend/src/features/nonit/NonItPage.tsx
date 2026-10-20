import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart } from '../../components/charts';
import { Icon } from '../../components/Icon';
import { useConfirm } from '../../components/Confirm';
import { Callout, DataTable, ErrorBox, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { useAddDepartment, useDepartments, type Department } from '../departments/Departments';
import { AssetDetails, AssetForm, BulkUpload, errText, printLabels, ScanDialog } from './NonItDialogs';
import {
  daysUntil, day, money, total, STATUSES, statusLabel, statusTone, CONDITIONS,
  type NonItAsset, type NonItCategory, type NonItField, type NonItGroup, type NonItSummary,
} from './types';

type Tab = 'assets' | 'categories' | 'departments' | 'fields';
interface Paged { items: NonItAsset[]; total: number; page: number; pageSize: number }
const DUE: [string, string][] = [
  ['', 'Any'], ['warranty', 'Warranty ends in 90 days'], ['amc', 'AMC ends in 90 days'], ['insurance', 'Insurance ends in 90 days'],
  ['maintenance', 'Maintenance due in 30 days'], ['overdue', 'Maintenance overdue'], ['expired', 'Warranty or AMC expired'],
];

export function NonItPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canEdit = can('assets.manage');
  const [tab, setTabState] = useState<Tab>((['assets', 'categories', 'departments', 'fields'] as Tab[]).find((t) => t === query.get('tab')) ?? 'assets');
  const setTab = (t: Tab) => { setTabState(t); navigate(`/non-it?tab=${t}`); };
  const [tick, setTick] = useState(0);
  const reload = () => setTick((x) => x + 1);
  const summary = useApi(() => api.get<NonItSummary>('/nonit/summary'), [tick]);
  const cats = useApi(() => api.get<NonItCategory[]>('/nonit/categories'), [tick]);
  const fields = useApi(() => api.get<NonItField[]>('/nonit/fields'), [tick]);
  const groups = useApi(() => api.get<NonItGroup[]>('/nonit/groups'), [tick]);
  const deps = useDepartments();
  const [form, setForm] = useState<{ asset: NonItAsset | null; initial?: Record<string, string> } | null>(null);
  const [details, setDetails] = useState<string | null>(query.get('id'));
  const [upload, setUpload] = useState(false);
  const [scan, setScan] = useState(false);
  const s = summary.data;

  return (
    <>
      <PageHead title="Non-IT Asset Inventory" desc="Every chair, vehicle, appliance and tool — tagged, tracked and valued."
        actions={<>
          <button className="btn" onClick={() => setScan(true)}><Icon name="search" size={15} /> Scan barcode</button>
          {canEdit && <button className="btn" onClick={() => setUpload(true)}><Icon name="upload" size={15} /> Bulk upload</button>}
          {canEdit && <button className="btn primary" onClick={() => setForm({ asset: null })}><Icon name="plus" size={15} /> Add asset</button>}
        </>} />
      <ErrorBox error={summary.error} />
      {s && (
        <Kpis items={[
          { label: 'Assets', value: s.total.toLocaleString(), sub: `${s.items.toLocaleString()} items · ${s.addedLast30} added in 30 days` },
          { label: 'Purchase value', value: total(s.purchaseValue), sub: `Current value ${total(s.currentValue)}` },
          { label: 'Cover ending in 90 days', value: (s.warranty90 + s.amc90 + s.insurance90).toLocaleString(), sub: `${s.warranty90} warranty · ${s.amc90} AMC · ${s.insurance90} insurance`, to: '/non-it?tab=assets&due=warranty' },
          { label: 'Maintenance due', value: s.maintenance30.toLocaleString(), sub: s.maintenanceOverdue ? `${s.maintenanceOverdue} overdue` : 'next 30 days', to: '/non-it?tab=assets&due=maintenance' },
          { label: 'Disposed', value: s.disposed.toLocaleString(), sub: 'kept for the record', to: '/non-it?tab=assets&status=disposed' },
        ]} />
      )}
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'assets'} className={tab === 'assets' ? 'on' : ''} onClick={() => setTab('assets')}>Assets</button>
        <button role="tab" aria-selected={tab === 'categories'} className={tab === 'categories' ? 'on' : ''} onClick={() => setTab('categories')}>Groups &amp; categories<span className="cnt">{cats.data?.length ?? 0}</span></button>
        <button role="tab" aria-selected={tab === 'departments'} className={tab === 'departments' ? 'on' : ''} onClick={() => setTab('departments')}>Departments<span className="cnt">{deps.data?.items.length ?? 0}</span></button>
        <button role="tab" aria-selected={tab === 'fields'} className={tab === 'fields' ? 'on' : ''} onClick={() => setTab('fields')}>Custom fields<span className="cnt">{fields.data?.length ?? 0}</span></button>
      </div>
      {tab === 'assets' && <AssetsTab tick={tick} summary={s} categories={cats.data ?? []} departments={deps.data?.items ?? []} canEdit={canEdit}
        onOpen={setDetails} onReload={reload} onAdd={() => setForm({ asset: null })} onUpload={() => setUpload(true)} />}
      {tab === 'categories' && <CategoriesTab list={cats.data ?? []} groups={groups.data ?? []} canEdit={canEdit} onChanged={reload} />}
      {tab === 'departments' && <DepartmentsTab list={deps.data?.items ?? []} canEdit={canEdit} onChanged={() => { deps.reload(); reload(); }}
        onOpen={(id) => { navigate(`/non-it?tab=assets&department=${id}`); setTabState('assets'); }} />}
      {tab === 'fields' && <FieldsTab list={fields.data ?? []} canEdit={canEdit} onChanged={reload} />}

      {form && <AssetForm asset={form.asset} initial={form.initial} categories={cats.data ?? []} groups={(groups.data ?? []).map((g) => g.name)} departments={deps.data?.items ?? []} fields={fields.data ?? []}
        onListsChanged={() => { deps.reload(); reload(); }} onClose={() => setForm(null)} onSaved={(id) => { setForm(null); reload(); setDetails(id); }} />}
      {details && !form && <AssetDetails key={`${details}:${tick}`} id={details} fields={fields.data ?? []} canEdit={canEdit} onClose={() => setDetails(null)}
        onEdit={(a) => setForm({ asset: a })} onDeleted={() => { setDetails(null); reload(); }} />}
      {upload && <BulkUpload onClose={() => setUpload(false)} onDone={reload} />}
      {scan && <ScanDialog onClose={() => setScan(false)} onFound={(a) => { setScan(false); setDetails(a.id); }}
        onNew={(code) => { setScan(false); if (canEdit) setForm({ asset: null, initial: { barcode: code } }); }} />}
    </>
  );
}

function AssetsTab({ tick, summary, categories, departments, canEdit, onOpen, onReload, onAdd, onUpload }: {
  tick: number; summary: NonItSummary | null; categories: NonItCategory[]; departments: { id: string; name: string }[]; canEdit: boolean;
  onOpen: (id: string) => void; onReload: () => void; onAdd: () => void; onUpload: () => void;
}) {
  const { query } = useRouter();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [group, setGroup] = useState('');
  const [category, setCategory] = useState('');
  const [status, setStatus] = useState(query.get('status') ?? 'active');
  const [department, setDepartment] = useState(query.get('department') ?? '');
  const [due, setDue] = useState(query.get('due') ?? '');
  const [sort, setSort] = useState<{ col: string; dir: 'asc' | 'desc' } | null>(null);
  const [page, setPage] = useState(1);
  const [sel, setSel] = useState<string[]>([]);
  const [bulk, setBulk] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const filters = { q: search, group, category, status, department, due };
  const data = useApi(() => api.get<Paged>('/nonit/assets', { ...filters, sort: sort?.col, dir: sort?.dir, page, pageSize: 50 }),
    [tick, search, group, category, status, department, due, sort?.col, sort?.dir, page]);
  const rows = data.data?.items ?? [];
  const groups = useMemo(() => [...new Set(categories.map((c) => c.group))], [categories]);
  const exportUrl = (format: string) => `/api/v1/nonit/export?${new URLSearchParams(Object.entries({ ...filters, format }).filter(([, v]) => v)).toString()}`;
  const head = (col: string, label: string) => (
    <th className="sort" onClick={() => setSort((s) => ({ col, dir: s?.col === col && s.dir === 'asc' ? 'desc' : 'asc' }))}>
      {label}{sort?.col === col ? (sort.dir === 'asc' ? ' ▲' : ' ▼') : ''}
    </th>
  );
  const allOn = rows.length > 0 && rows.every((r) => sel.includes(r.id));

  async function remove() {
    if (!window.confirm(`Delete ${sel.length} asset(s) and their history?`)) return;
    try { const r = await api.post<{ deleted: number }>('/nonit/assets/delete', { ids: sel }); setMsg({ tone: 'ok', text: `${r.deleted} asset(s) deleted.` }); setSel([]); onReload(); }
    catch (e) { setMsg({ tone: 'crit', text: errText(e) }); }
  }

  return (
    <>
      {summary && summary.total > 0 && (
        <div className="grid-3">
          <Panel title="By category group">
            <CategoryChart kind="bar" data={summary.byGroup.map((g) => ({ key: g.key, label: g.key, value: g.count }))} onSelect={(x) => { setGroup(x.key === 'Uncategorised' ? '' : x.key); setPage(1); }} />
          </Panel>
          <Panel title="By status">
            <CategoryChart kind="share" centerLabel="assets" data={summary.byStatus.map((x) => ({ key: x.key, label: statusLabel(x.key), value: x.count,
              color: { in_use: 'var(--ok)', in_store: 'var(--info)', under_repair: 'var(--warn)', under_maintenance: '#c08a1f', reserved: 'var(--s7)', lost: 'var(--crit)', disposed: 'var(--line-strong)' }[x.key] }))}
              onSelect={(x) => { setStatus(x.key); setPage(1); }} />
          </Panel>
          <Panel title="Coming up" sub="Warranty, AMC, insurance and maintenance">
            {summary.upcoming.length === 0 ? <div className="chart-empty">Nothing due in the next 90 days.</div> : (
              <div className="mini-list">
                {summary.upcoming.slice(0, 6).map((u, i) => {
                  const d = daysUntil(u.date) ?? 0;
                  return (
                    <div key={i} className="mi" onClick={() => onOpen(u.id)}>
                      <span><b>{u.name}</b> <span className="faint mono">{u.assetTag}</span><br /><span className="faint">{u.what}</span></span>
                      <span className="r"><Pill tone={d < 0 ? 'crit' : d <= 30 ? 'warn' : ''}>{d < 0 ? `${-d} d overdue` : d === 0 ? 'today' : `in ${d} d`}</Pill></span>
                    </div>
                  );
                })}
              </div>
            )}
          </Panel>
        </div>
      )}
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q.trim()); setPage(1); }}>
        <input className="in grow" placeholder="Search tag, name, serial, barcode, model, person or location…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search non-IT assets" />
        <select className="in" value={group} onChange={(e) => { setGroup(e.target.value); setCategory(''); setPage(1); }} aria-label="Category group">
          <option value="">All groups</option>{groups.map((g) => <option key={g}>{g}</option>)}
        </select>
        <select className="in" value={category} onChange={(e) => { setCategory(e.target.value); setPage(1); }} aria-label="Category">
          <option value="">All categories</option>{categories.filter((c) => !group || c.group === group).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Status">
          <option value="active">All except disposed</option><option value="">All, including disposed</option>
          {STATUSES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        <select className="in" value={department} onChange={(e) => { setDepartment(e.target.value); setPage(1); }} aria-label="Department">
          <option value="">All departments</option>{departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <select className="in" value={due} onChange={(e) => { setDue(e.target.value); setPage(1); }} aria-label="Due">
          {DUE.map(([k, l]) => <option key={k} value={k}>{k ? l : 'Any due date'}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <div className="toolbar-row">
        {sel.length > 0 ? <>
          <b>{sel.length} selected</b>
          {canEdit && <button className="btn sm" onClick={() => setBulk(true)}>Change…</button>}
          <button className="btn sm" onClick={() => printLabels(rows.filter((r) => sel.includes(r.id)))}>Print labels</button>
          {canEdit && <button className="btn sm danger" onClick={() => void remove()}><Icon name="trash" size={13} /> Delete</button>}
          <button className="btn sm ghost" onClick={() => setSel([])}>Clear selection</button>
        </> : <span className="faint">{data.data ? `${data.data.total.toLocaleString()} asset(s)` : ''}</span>}
        <span style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
          <a className="btn sm" href={exportUrl('xlsx')} download><Icon name="down" size={13} /> Excel</a>
          <a className="btn sm" href={exportUrl('csv')} download><Icon name="down" size={13} /> CSV</a>
        </span>
      </div>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
      <ErrorBox error={data.error} />
      {data.data && data.data.total === 0 && !search && !group && !category && status === 'active' && !department && !due ? (
        <Panel>
          <div className="empty-state">
            <h3>No non-IT assets yet</h3>
            <p className="muted">Add items one by one, scan their barcodes, or upload many at once from the Excel form.</p>
            {canEdit && <div className="toolbar-row" style={{ justifyContent: 'center' }}>
              <button className="btn primary" onClick={onAdd}><Icon name="plus" size={15} /> Add asset</button>
              <button className="btn" onClick={onUpload}><Icon name="upload" size={15} /> Bulk upload</button>
              <a className="btn" href="/api/v1/nonit/template" download><Icon name="down" size={15} /> Download Excel form</a>
            </div>}
          </div>
        </Panel>
      ) : (
        <div className="tbl-wrap">
          <table className="t">
            <thead>
              <tr>
                <th style={{ width: 32 }}><input type="checkbox" className="row-sel" checked={allOn} aria-label="Select all"
                  onChange={(e) => setSel(e.target.checked ? [...new Set([...sel, ...rows.map((r) => r.id)])] : sel.filter((id) => !rows.some((r) => r.id === id)))} /></th>
                {head('tag', 'Asset tag')}{head('name', 'Name')}{head('category', 'Category')}{head('status', 'Status')}
                {head('location', 'Location / department')}<th>Assigned to</th>{head('cost', 'Cost')}{head('maintenance', 'Warranty / maintenance')}
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 ? <tr><td colSpan={9} className="empty-cell">{data.loading ? 'Loading…' : 'No assets match.'}</td></tr> : rows.map((r) => {
                const w = daysUntil(r.warrantyUntil), m = daysUntil(r.nextMaintenance);
                return (
                  <tr key={r.id} className="click" onClick={() => onOpen(r.id)}>
                    <td onClick={(e) => e.stopPropagation()}><input type="checkbox" className="row-sel" checked={sel.includes(r.id)} aria-label={`Select ${r.assetTag}`}
                      onChange={(e) => setSel((x) => (e.target.checked ? [...x, r.id] : x.filter((id) => id !== r.id)))} /></td>
                    <td className="mono">{r.assetTag}</td>
                    <td className="wrap"><b>{r.name}</b>{(r.manufacturer || r.model) && <div className="faint" style={{ fontSize: 12 }}>{[r.manufacturer, r.model].filter(Boolean).join(' ')}</div>}</td>
                    <td>{r.category ?? '—'}<div className="faint" style={{ fontSize: 12 }}>{r.group}</div></td>
                    <td><Pill tone={statusTone(r.status)}>{statusLabel(r.status)}</Pill>{r.quantity !== 1 && <span className="faint"> ×{r.quantity}</span>}</td>
                    <td>{r.location ?? '—'}{r.department && <div className="faint" style={{ fontSize: 12 }}>{r.department}</div>}</td>
                    <td>{r.assignedTo ?? '—'}</td>
                    <td className="num">{money(r.purchaseCost)}</td>
                    <td style={{ fontSize: 12.5 }}>
                      {r.warrantyUntil && <div><span className="faint">Warranty</span> <span style={w != null && w < 0 ? { color: 'var(--muted)' } : w != null && w <= 90 ? { color: 'var(--warn)', fontWeight: 600 } : undefined}>{day(r.warrantyUntil)}</span></div>}
                      {r.nextMaintenance && <div><span className="faint">Service</span> <span style={m != null && m < 0 ? { color: 'var(--crit)', fontWeight: 600 } : m != null && m <= 30 ? { color: 'var(--warn)', fontWeight: 600 } : undefined}>{day(r.nextMaintenance)}</span></div>}
                      {!r.warrantyUntil && !r.nextMaintenance && '—'}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {data.data && data.data.total > 50 && <Pager page={page} pageSize={50} total={data.data.total} onPage={setPage} />}
        </div>
      )}
      {bulk && <BulkChange ids={sel} categories={categories} departments={departments} onClose={() => setBulk(false)}
        onDone={(n) => { setBulk(false); setMsg({ tone: 'ok', text: `${n} asset(s) changed.` }); onReload(); }} />}
    </>
  );
}

function BulkChange({ ids, categories, departments, onClose, onDone }: {
  ids: string[]; categories: NonItCategory[]; departments: { id: string; name: string }[]; onClose: () => void; onDone: (n: number) => void;
}) {
  const [v, setV] = useState({ status: '', condition: '', location: '', department: '', assignedTo: '', category: '' });
  const [error, setError] = useState<string | null>(null);
  const set = (k: keyof typeof v) => (e: { target: { value: string } }) => setV((x) => ({ ...x, [k]: e.target.value }));
  async function save() {
    try {
      const r = await api.post<{ updated: number }>('/nonit/assets/bulk', {
        ids, status: v.status || null, condition: v.condition || null, location: v.location.trim() || null, assignedTo: v.assignedTo.trim() || null,
        departmentId: v.department && v.department !== '-' ? v.department : null, clearDepartment: v.department === '-', categoryId: v.category || null,
      });
      onDone(r.updated);
    } catch (e) { setError(errText(e)); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal-card">
        <header><h2>Change {ids.length} asset(s)</h2></header>
        <div className="body form-v">
          <p className="muted" style={{ margin: 0 }}>Only the fields you fill in are changed.</p>
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Status<select className="in" value={v.status} onChange={set('status')}><option value="">Keep</option>{STATUSES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></label>
          <label>Condition<select className="in" value={v.condition} onChange={set('condition')}><option value="">Keep</option>{CONDITIONS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></label>
          <label>Category<select className="in" value={v.category} onChange={set('category')}><option value="">Keep</option>{categories.map((c) => <option key={c.id} value={c.id}>{c.group} › {c.name}</option>)}</select></label>
          <label>Location<input className="in" value={v.location} onChange={set('location')} placeholder="Keep" /></label>
          <label>Department<select className="in" value={v.department} onChange={set('department')}><option value="">Keep</option><option value="-">Not assigned</option>{departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select></label>
          <label>Assigned to<input className="in" value={v.assignedTo} onChange={set('assignedTo')} placeholder="Keep" /></label>
        </div>
        <footer><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" onClick={() => void save()}>Apply</button></footer>
      </div>
    </div>
  );
}

function CategoriesTab({ list, groups, canEdit, onChanged }: { list: NonItCategory[]; groups: NonItGroup[]; canEdit: boolean; onChanged: () => void }) {
  const confirm = useConfirm();
  const [name, setName] = useState('');
  const [group, setGroup] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  async function run(f: () => Promise<unknown>) {
    setError(null);
    try { await f(); onChanged(); } catch (e) { setError(errText(e)); }
  }
  const ask = (title: string, label: string, defaultValue = '') => confirm({ title, message: '', confirmLabel: defaultValue ? 'Rename' : 'Add', input: { label, type: 'text', defaultValue } });
  async function addGroup() {
    const n = await ask('Add a group', 'Group name, e.g. Kitchen & pantry, Medical equipment');
    if (n) await run(async () => { await api.post('/nonit/groups', { name: n }); setGroup(n); });
  }
  async function addCategory(g: string, n = name) {
    if (!n.trim()) return;
    await run(async () => { await api.post('/nonit/categories', { name: n.trim(), group: g || 'Custom' }); setName(''); });
  }
  async function addInGroup(g: string) {
    const n = await ask(`Add a category to ${g}`, 'Category name');
    if (n) await addCategory(g, n);
  }
  async function renameGroup(g: NonItGroup) {
    const n = await ask(`Rename "${g.name}"`, 'Group name', g.name);
    if (n && n !== g.name) await run(() => api.put(`/nonit/groups/${g.id}`, { name: n }));
  }
  async function deleteGroup(g: NonItGroup) {
    if (await confirm({ title: `Delete the group "${g.name}"?`, message: 'It has no categories.', confirmLabel: 'Delete', danger: true })) await run(() => api.del(`/nonit/groups/${g.id}`));
  }
  async function rename(c: NonItCategory) {
    const n = await ask(`Rename "${c.name}"`, 'Category name', c.name);
    if (n && n !== c.name) await run(() => api.put(`/nonit/categories/${c.id}`, { name: n }));
  }
  async function move(c: NonItCategory, g: string) { if (g && g !== c.group) await run(() => api.put(`/nonit/categories/${c.id}`, { name: c.name, group: g })); }
  async function remove(c: NonItCategory) {
    if (await confirm({ title: `Delete the category "${c.name}"?`, message: 'It has no assets.', confirmLabel: 'Delete', danger: true })) await run(() => api.del(`/nonit/categories/${c.id}`));
  }
  const f = filter.trim().toLowerCase();
  const shown = groups.filter((g) => !f || g.name.toLowerCase().includes(f) || list.some((c) => c.group.toLowerCase() === g.name.toLowerCase() && c.name.toLowerCase().includes(f)));
  return (
    <>
      <div className="toolbar-row">
        <input className="in" placeholder="Find a group or category…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Find" />
        {canEdit && <form className="inline-add grow" onSubmit={(e) => { e.preventDefault(); void addCategory(group); }}>
          <input className="in grow" placeholder="New category, e.g. Podium, Water cooler, Golf cart" value={name} onChange={(e) => setName(e.target.value)} aria-label="Category name" />
          <select className="in" value={group} onChange={(e) => e.target.value === '__add' ? void addGroup() : setGroup(e.target.value)} aria-label="Group">
            <option value="">Group: Custom</option>
            {groups.map((g) => <option key={g.id} value={g.name}>{g.name}</option>)}
            <option value="__add">＋ New group…</option>
          </select>
          <button className="btn primary" type="submit" disabled={!name.trim()}><Icon name="plus" size={14} /> Add category</button>
        </form>}
        {canEdit && <button className="btn" onClick={() => void addGroup()}><Icon name="plus" size={14} /> Add group</button>}
      </div>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <div className="cat-groups">
        {shown.map((g) => {
          const cats = list.filter((c) => c.group.toLowerCase() === g.name.toLowerCase() && (!f || g.name.toLowerCase().includes(f) || c.name.toLowerCase().includes(f)));
          return (
            <Panel key={g.id} title={g.name} sub={`${g.categories} categor${g.categories === 1 ? 'y' : 'ies'} · ${g.count} asset(s)`}
              right={canEdit && <span className="acts">
                <button className="icon-btn" title={`Add a category to ${g.name}`} aria-label={`Add a category to ${g.name}`} onClick={() => void addInGroup(g.name)}><Icon name="plus" size={14} /></button>
                <button className="icon-btn" title="Rename group" aria-label={`Rename ${g.name}`} onClick={() => void renameGroup(g)}><Icon name="edit" size={14} /></button>
                {g.categories === 0 && <button className="icon-btn" title="Delete group" aria-label={`Delete ${g.name}`} onClick={() => void deleteGroup(g)}><Icon name="trash" size={14} /></button>}
              </span>}>
              {cats.length === 0 ? <p className="faint" style={{ margin: 0, fontSize: 13 }}>No categories yet{canEdit && <> — <button className="link-btn" onClick={() => void addInGroup(g.name)}>add one</button></>}.</p> : (
                <ul className="cat-list">
                  {cats.map((c) => (
                    <li key={c.id}>
                      <span>{c.name}</span>
                      <span className="faint">{c.count || ''}</span>
                      {!c.builtin && <Pill>custom</Pill>}
                      {canEdit && <span className="acts">
                        <select className="in sm move" value="" onChange={(e) => void move(c, e.target.value)} aria-label={`Move ${c.name} to another group`} title="Move to another group">
                          <option value="">Move…</option>{groups.filter((x) => x.id !== g.id).map((x) => <option key={x.id} value={x.name}>{x.name}</option>)}
                        </select>
                        <button className="icon-btn" title="Rename" aria-label={`Rename ${c.name}`} onClick={() => void rename(c)}><Icon name="edit" size={14} /></button>
                        {c.count === 0 && <button className="icon-btn" title="Delete" aria-label={`Delete ${c.name}`} onClick={() => void remove(c)}><Icon name="trash" size={14} /></button>}
                      </span>}
                    </li>
                  ))}
                </ul>)}
            </Panel>
          );
        })}
      </div>
    </>
  );
}

/** Departments for non-IT assets (the same list IT assets use). */
function DepartmentsTab({ list, canEdit, onChanged, onOpen }: { list: Department[]; canEdit: boolean; onChanged: () => void; onOpen: (id: string) => void }) {
  const confirm = useConfirm();
  const add = useAddDepartment();
  const [error, setError] = useState<string | null>(null);
  async function run(f: () => Promise<unknown>) {
    setError(null);
    try { await f(); onChanged(); } catch (e) { setError(errText(e)); }
  }
  async function rename(d: Department) {
    const name = await confirm({ title: `Rename "${d.name}"`, message: '', confirmLabel: 'Rename', input: { label: 'Name', type: 'text', defaultValue: d.name } });
    if (name && name !== d.name) await run(() => api.put(`/departments/${d.id}`, { name, code: d.code, description: d.description }));
  }
  async function remove(d: Department) {
    const n = (d.nonItAssets ?? 0) + d.assets;
    if (!(await confirm({ title: `Delete "${d.name}"?`, message: n ? `${n} asset(s) become unassigned.` : 'Nothing is assigned to it.', confirmLabel: 'Delete', danger: true }))) return;
    await run(() => api.del(`/departments/${d.id}`));
  }
  return (
    <Panel flush title="Departments" sub="Shared with IT Asset Inventory" right={canEdit && <button className="btn sm primary" onClick={() => void run(add)}><Icon name="plus" size={13} /> Add department</button>}>
      {error && <div style={{ padding: '0 18px' }}><Callout tone="crit" icon="alert">{error}</Callout></div>}
      <DataTable rows={list} empty="No departments yet. Add the departments, units or cost centres your assets belong to." columns={[
        { key: 'n', label: 'Department', render: (d) => <b>{d.name}</b> },
        { key: 'x', label: 'Non-IT assets', render: (d) => (d.nonItAssets ?? 0) > 0 ? <button className="link-btn" onClick={() => onOpen(d.id)}>{(d.nonItAssets ?? 0).toLocaleString()}</button> : <span className="faint">0</span> },
        { key: 'i', label: 'IT assets', render: (d) => d.assets.toLocaleString() },
        { key: 'a', label: '', render: (d) => canEdit ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
          <button className="btn sm" onClick={() => void rename(d)}>Rename</button>
          <button className="btn sm danger" onClick={() => void remove(d)}>Delete</button>
        </div> : null },
      ]} />
    </Panel>
  );
}

function FieldsTab({ list, canEdit, onChanged }: { list: NonItField[]; canEdit: boolean; onChanged: () => void }) {
  const [label, setLabel] = useState('');
  const [type, setType] = useState<NonItField['type']>('text');
  const [options, setOptions] = useState('');
  const [error, setError] = useState<string | null>(null);
  async function add() {
    setError(null);
    try {
      await api.post('/nonit/fields', { label, type, options: type === 'list' ? options.split(',').map((o) => o.trim()).filter(Boolean) : [], sort: list.length });
      setLabel(''); setOptions(''); onChanged();
    } catch (e) { setError(errText(e)); }
  }
  async function remove(f: NonItField) {
    if (!window.confirm(`Delete the field "${f.label}"? Values already entered stay in the asset records but are no longer shown.`)) return;
    try { await api.del(`/nonit/fields/${f.id}`); onChanged(); } catch (e) { setError(errText(e)); }
  }
  const TYPES: [NonItField['type'], string][] = [['text', 'Text'], ['number', 'Number'], ['date', 'Date'], ['yesno', 'Yes / no'], ['list', 'List of choices']];
  return (
    <>
      <p className="muted" style={{ marginTop: 0 }}>Extra details your organisation records for every non-IT asset. They appear on the form, in the Excel form, the export and the asset details.</p>
      {canEdit && (
        <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); void add(); }}>
          <input className="in grow" placeholder="Field name, e.g. Floor, Cost centre, Fuel type, Registration number" value={label} onChange={(e) => setLabel(e.target.value)} aria-label="Field name" />
          <select className="in" value={type} onChange={(e) => setType(e.target.value as NonItField['type'])} aria-label="Type">{TYPES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
          {type === 'list' && <input className="in" placeholder="Choices, separated by commas" value={options} onChange={(e) => setOptions(e.target.value)} aria-label="Choices" />}
          <button className="btn primary" type="submit" disabled={!label.trim()}><Icon name="plus" size={14} /> Add field</button>
        </form>
      )}
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <div className="tbl-wrap">
        <table className="t">
          <thead><tr><th>Field</th><th>Type</th><th>Choices</th><th>Column in Excel</th><th /></tr></thead>
          <tbody>
            {list.length === 0 ? <tr><td colSpan={5} className="empty-cell">No custom fields yet.</td></tr> : list.map((f) => (
              <tr key={f.id}>
                <td><b>{f.label}</b></td>
                <td>{TYPES.find(([k]) => k === f.type)?.[1]}</td>
                <td className="wrap">{f.options.join(', ') || '—'}</td>
                <td className="mono">{f.label}</td>
                <td>{canEdit && <button className="icon-btn" title="Delete" onClick={() => void remove(f)}><Icon name="trash" size={14} /></button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
