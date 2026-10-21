import { useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useColumns } from '../../components/columns';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill, type Column } from '../../components/ui';
import { useDepartments } from '../departments/Departments';
import { ManageOrgDialog, useAddLocation, useLocations } from '../locations/Locations';
import { ItemDialog, MovementDialog, type MovementPreset } from './AccessoryDialogs';
import { KIND, money, type AccessoryItem, type AccessorySummary, type Holding, type MovementKind, type MovementRow, type StockRow } from './types';

type Tab = 'stock' | 'issued' | 'movements' | 'catalogue';

export function AccessoriesPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canEdit = can('assets.manage');
  const [tab, setTabState] = useState<Tab>((['stock', 'issued', 'movements', 'catalogue'] as Tab[]).find((t) => t === query.get('tab')) ?? 'stock');
  const setTab = (t: Tab) => { setTabState(t); navigate(`/accessories?tab=${t}`, true); };
  const [tick, setTick] = useState(0);
  const summary = useApi(() => api.get<AccessorySummary>('/accessories/summary'), [tick]);
  const items = useApi(() => api.get<AccessoryItem[]>('/accessories/items', { all: true }), [tick]);
  const locs = useLocations();
  const addLocation = useAddLocation();
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [move, setMove] = useState<{ kind: MovementKind; preset?: MovementPreset } | null>(null);
  const [editing, setEditing] = useState<AccessoryItem | 'new' | null>(null);
  const [manageOrg, setManageOrg] = useState(false);
  const s = summary.data;
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); locs.reload(); };
  const noLocations = locs.data && locs.data.items.length === 0;
  const noItems = items.data && items.data.length === 0;

  return (
    <>
      <PageHead title="IT Accessories" actions={<>
        <button className="btn" onClick={() => setManageOrg(true)}>Departments &amp; locations</button>
        {canEdit && <button className="btn" onClick={() => setEditing('new')}><Icon name="plus" size={13} /> Add accessory</button>}
        {canEdit && <button className="btn" disabled={!!noItems || !!noLocations} onClick={() => setMove({ kind: 'receive' })}>Receive stock</button>}
        {canEdit && <button className="btn primary" disabled={!!noItems || !!noLocations} onClick={() => setMove({ kind: 'issue' })}>Issue</button>}
      </>} />
      {s && <Kpis items={[
        { label: 'Accessories', value: s.items.toLocaleString(), sub: `${s.locations} location(s)` },
        { label: 'In stock', value: s.inStock.toLocaleString(), sub: s.faulty ? `${s.faulty} faulty kept apart` : 'usable units' },
        { label: 'Stock value', value: money(s.stockValue), sub: 'quantity × unit cost' },
        { label: 'To reorder', value: s.lowStock.toLocaleString(), sub: 'at or below the reorder level', to: '/accessories?tab=stock&low=1' },
        { label: 'Issued (30 days)', value: s.issued30d.toLocaleString(), sub: 'units given to people' },
        { label: 'With people', value: s.outstanding.toLocaleString(), sub: 'returnable units issued', to: '/accessories?tab=issued' },
      ]} />}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {noLocations && <Callout tone="info" icon="info">
        Stock is kept per location (store, office, floor). Add at least one location first.{' '}
        {canEdit && <button className="btn sm" onClick={() => void (async () => { try { if (await addLocation()) locs.reload(); } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } })()}>Add location</button>}
      </Callout>}
      {!noLocations && noItems && <Callout tone="info" icon="info">Add the accessories you stock (mouse, keyboard, headset…), then receive the quantities you bought.</Callout>}
      <div className="tabs" role="tablist">
        {([['stock', 'Stock'], ['issued', 'Issued to people'], ['movements', 'Movements'], ['catalogue', 'Catalogue']] as [Tab, string][]).map(([t, l]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{l}
            {t === 'catalogue' && items.data && <span className="cnt">{items.data.filter((i) => i.active).length}</span>}
            {t === 'issued' && s && s.outstanding > 0 && <span className="cnt">{s.outstanding}</span>}
          </button>
        ))}
      </div>
      {tab === 'stock' && <StockTab tick={tick} canEdit={canEdit} categories={s?.categories ?? []} onMove={(kind, preset) => setMove({ kind, preset })} lowFirst={query.get('low') === '1'} />}
      {tab === 'issued' && <IssuedTab tick={tick} canEdit={canEdit} onReturn={(h) => setMove({ kind: 'return', preset: { itemId: h.itemId, person: h.person, quantity: h.quantity, serials: [] } })} />}
      {tab === 'movements' && <MovementsTab tick={tick} />}
      {tab === 'catalogue' && <CatalogueTab items={items.data ?? []} loading={items.loading} canEdit={canEdit} onEdit={setEditing} onChanged={changed} onError={(t) => setMsg({ tone: 'crit', text: t })} />}
      <ErrorBox error={items.error ?? summary.error} />

      {move && items.data && locs.data && (
        <MovementDialog kind={move.kind} preset={move.preset} items={items.data} locations={locs.data.items} onLocationsChanged={locs.reload}
          onClose={() => setMove(null)} onDone={(m) => { setMove(null); changed(m); }} />
      )}
      {editing && <ItemDialog item={editing === 'new' ? null : editing} categories={s?.categories ?? []} onClose={() => setEditing(null)} onSaved={(m) => { setEditing(null); changed(m); }} />}
      {manageOrg && <ManageOrgDialog canEdit={canEdit} onClose={() => { setManageOrg(false); locs.reload(); setTick((x) => x + 1); }} />}
    </>
  );
}

// ---- stock -------------------------------------------------------------------------------------------------------

function StockTab({ tick, canEdit, categories, onMove, lowFirst }: { tick: number; canEdit: boolean; categories: string[]; onMove: (k: MovementKind, p: MovementPreset) => void; lowFirst: boolean }) {
  const locs = useLocations();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [locationId, setLocationId] = useState('');
  const [category, setCategory] = useState('');
  const [low, setLow] = useState(lowFirst);
  const stock = useApi(() => api.get<StockRow[]>('/accessories/stock', { q: search, locationId, category, low }), [search, locationId, category, low, tick]);
  const rows = stock.data ?? [];
  const cats = [...new Set([...rows.map((r) => r.category), ...categories])].sort();
  const all: Column<StockRow>[] = [
    { key: 'i', label: 'Accessory', className: 'wrap', render: (r) => <><b>{r.item}</b>{(r.brand || r.model) && <div className="faint" style={{ fontSize: 11.5 }}>{[r.brand, r.model].filter(Boolean).join(' ')}</div>}</> },
    { key: 'c', label: 'Category', render: (r) => r.category },
    { key: 'l', label: 'Location', render: (r) => r.location },
    { key: 'q', label: 'In stock', render: (r) => <span className={`stock-qty${r.quantity === 0 ? ' out' : r.low ? ' low' : ''}`} title={r.low ? `At or below the reorder level (${r.reorderLevel})` : undefined}>{r.quantity}{r.low && <> <Pill tone={r.quantity === 0 ? 'crit' : 'warn'}>{r.quantity === 0 ? 'Out of stock' : 'Reorder'}</Pill></>}</span> },
    { key: 'f', label: 'Faulty', render: (r) => r.faulty ? r.faulty : <span className="muted">—</span> },
    { key: 'r', label: 'Reorder level', render: (r) => r.reorderLevel || <span className="muted">—</span> },
    { key: 'v', label: 'Value', optional: true, render: (r) => money(r.unitCost == null ? null : r.unitCost * r.quantity) },
    { key: 'uc', label: 'Unit cost', optional: true, render: (r) => money(r.unitCost) },
    { key: 'sku', label: 'SKU / part number', optional: true, render: (r) => r.sku ? <span className="mono">{r.sku}</span> : <span className="muted">—</span> },
    { key: 'type', label: 'Type', optional: true, render: (r) => <>{r.returnable ? 'Returnable' : 'Consumable'}{r.serialTracked ? ' · serial numbers' : ''}</> },
    { key: 'u', label: 'Last change', optional: true, render: (r) => <span title={fmt.date(r.updatedAt)}>{r.updatedAt ? fmt.ago(r.updatedAt) : '—'}</span> },
    { key: 'x', label: '', render: (r) => canEdit ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
      <button className="btn sm primary" disabled={r.quantity === 0} onClick={() => onMove('issue', { itemId: r.itemId, locationId: r.locationId })}>Issue</button>
      <button className="btn sm" onClick={() => onMove('receive', { itemId: r.itemId, locationId: r.locationId })}>Receive</button>
      <select className="in sm" value="" aria-label={`More for ${r.item}`} onChange={(e) => { const k = e.target.value as MovementKind; if (k) onMove(k, { itemId: r.itemId, locationId: r.locationId, quantity: k === 'adjust' ? r.quantity : 1 }); }}>
        <option value="">More…</option>
        <option value="transfer" disabled={r.quantity === 0}>Transfer</option>
        <option value="writeoff" disabled={r.quantity === 0 && r.faulty === 0}>Write off</option>
        <option value="adjust" disabled={r.serialTracked}>Stock count</option>
      </select>
    </div> : null },
  ];
  const { columns, chooser } = useColumns('accessories.stock', all);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search accessory, brand, model or SKU…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search stock" />
        <select className="in" value={locationId} onChange={(e) => setLocationId(e.target.value)} aria-label="Location">
          <option value="">All locations</option>
          {(locs.data?.items ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </select>
        <select className="in" value={category} onChange={(e) => setCategory(e.target.value)} aria-label="Category">
          <option value="">All categories</option>
          {cats.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
        <label className="row-check"><input type="checkbox" checked={low} onChange={(e) => setLow(e.target.checked)} /> Only to reorder</label>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      <ErrorBox error={stock.error} />
      <Panel flush>
        <DataTable rows={rows} columns={columns} empty={stock.loading ? 'Loading…' : low ? 'Nothing to reorder.' : 'No stock yet. Use Receive stock to add quantities.'} />
      </Panel>
    </>
  );
}

// ---- holdings ----------------------------------------------------------------------------------------------------

function IssuedTab({ tick, canEdit, onReturn }: { tick: number; canEdit: boolean; onReturn: (h: Holding) => void }) {
  const deps = useDepartments();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [returnable, setReturnable] = useState(false);
  const list = useApi(() => api.get<Holding[]>('/accessories/holdings', { q: search, departmentId, returnableOnly: returnable }), [search, departmentId, returnable, tick]);
  const rows = list.data ?? [];
  const people = new Set(rows.map((r) => r.personKey)).size;
  const all: Column<Holding>[] = [
    { key: 'p', label: 'Person', render: (r) => <b>{r.person}</b> },
    { key: 'i', label: 'Accessory', className: 'wrap', render: (r) => <>{r.item}<div className="faint" style={{ fontSize: 11.5 }}>{r.category}{r.returnable ? '' : ' · consumable'}</div></> },
    { key: 'q', label: 'Quantity', render: (r) => <span className="stock-qty">{r.quantity}</span> },
    { key: 'd', label: 'Department', render: (r) => r.department ?? <span className="muted">—</span> },
    { key: 's', label: 'Serial numbers', optional: true, className: 'wrap', render: (r) => r.serials.length ? <span className="mono" style={{ fontSize: 12 }}>{r.serials.join(', ')}</span> : <span className="muted">—</span> },
    { key: 'l', label: 'Last issued', render: (r) => <span title={fmt.date(r.lastIssued)}>{fmt.day(r.lastIssued)}</span> },
    { key: 'x', label: '', render: (r) => canEdit ? <button className="btn sm" onClick={() => onReturn(r)}>Return</button> : null },
  ];
  const { columns, chooser } = useColumns('accessories.issued', all);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search person or accessory…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search issued accessories" />
        <select className="in" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} aria-label="Department">
          <option value="">All departments</option>
          {(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <label className="row-check" title="Items expected back when a person leaves"><input type="checkbox" checked={returnable} onChange={(e) => setReturnable(e.target.checked)} /> Returnable only</label>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      <ErrorBox error={list.error} />
      <Panel flush title={rows.length ? `${people} person(s) hold ${rows.reduce((a, r) => a + r.quantity, 0)} accessor${rows.length === 1 ? 'y' : 'ies'}` : undefined}
        sub={rows.length ? 'Search a name to see the collect-back list when someone leaves' : undefined}>
        <DataTable rows={rows} columns={columns} empty={list.loading ? 'Loading…' : 'Nobody holds accessories.'} />
      </Panel>
    </>
  );
}

// ---- movements ---------------------------------------------------------------------------------------------------

function MovementsTab({ tick }: { tick: number }) {
  const locs = useLocations();
  const deps = useDepartments();
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('');
  const [locationId, setLocationId] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [days, setDays] = useState('90');
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const list = useApi(() => api.get<Paged<MovementRow>>('/accessories/movements', { q: search, kind, locationId, departmentId, days, page, pageSize }), [search, kind, locationId, departmentId, days, page, tick]);
  const reset = () => setPage(1);
  const all: Column<MovementRow>[] = [
    { key: 't', label: 'Date', render: (r) => <span title={fmt.date(r.occurredAt)}>{fmt.date(r.occurredAt)}</span> },
    { key: 'k', label: 'Movement', render: (r) => <Pill tone={KIND[r.kind][1]}>{KIND[r.kind][0]}</Pill> },
    { key: 'i', label: 'Accessory', className: 'wrap', render: (r) => <><b>{r.item}</b><div className="faint" style={{ fontSize: 11.5 }}>{r.category}</div></> },
    { key: 'q', label: 'Quantity', render: (r) => { const n = r.quantity + r.faultyChange; return <span className="stock-qty">{r.kind === 'transfer' ? -n : n > 0 ? `+${n}` : n}{r.faultyChange !== 0 && <span className="faint" style={{ fontWeight: 400 }}> faulty</span>}</span>; } },
    { key: 'l', label: 'Location', render: (r) => <>{r.location}{r.toLocation && <> → {r.toLocation}</>}</> },
    { key: 'p', label: 'Person / department', className: 'wrap', render: (r) => r.person || r.department ? <>{r.person ?? '—'}{r.department && <div className="faint" style={{ fontSize: 11.5 }}>{r.department}</div>}</> : <span className="muted">—</span> },
    { key: 'r', label: 'Reason', className: 'wrap', render: (r) => r.reason ?? (r.condition ? fmt.label(r.condition) : <span className="muted">—</span>) },
    { key: 'ref', label: 'Reference / vendor', className: 'wrap', render: (r) => r.reference || r.vendor ? <>{r.reference}{r.vendor && <div className="faint" style={{ fontSize: 11.5 }}>{r.vendor}</div>}</> : <span className="muted">—</span> },
    { key: 'dev', label: 'Device', optional: true, render: (r) => r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.device ?? 'device'}</Link> : <span className="muted">—</span> },
    { key: 'sn', label: 'Serial numbers', optional: true, className: 'wrap', render: (r) => r.serials.length ? <span className="mono" style={{ fontSize: 12 }}>{r.serials.join(', ')}</span> : <span className="muted">—</span> },
    { key: 'uc', label: 'Unit cost', optional: true, render: (r) => money(r.unitCost) },
    { key: 'n', label: 'Note', optional: true, className: 'wrap', render: (r) => r.note ?? <span className="muted">—</span> },
    { key: 'b', label: 'By', render: (r) => r.by ?? <span className="muted">—</span> },
  ];
  const { columns, chooser } = useColumns('accessories.movements', all);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); reset(); setSearch(q); }}>
        <input className="in grow" placeholder="Search accessory, person, invoice, vendor or serial…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search movements" />
        <select className="in" value={kind} onChange={(e) => { reset(); setKind(e.target.value); }} aria-label="Movement">
          <option value="">All movements</option>
          {(Object.keys(KIND) as MovementKind[]).map((k) => <option key={k} value={k}>{KIND[k][0]}</option>)}
        </select>
        <select className="in" value={locationId} onChange={(e) => { reset(); setLocationId(e.target.value); }} aria-label="Location">
          <option value="">All locations</option>
          {(locs.data?.items ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </select>
        <select className="in" value={departmentId} onChange={(e) => { reset(); setDepartmentId(e.target.value); }} aria-label="Department">
          <option value="">All departments</option>
          {(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <select className="in" value={days} onChange={(e) => { reset(); setDays(e.target.value); }} aria-label="Period">
          <option value="7">Last 7 days</option>
          <option value="30">Last 30 days</option>
          <option value="90">Last 90 days</option>
          <option value="365">Last 12 months</option>
          <option value="3660">All</option>
        </select>
        <button className="btn" type="submit">Search</button>
        {chooser}
      </form>
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={list.data?.items ?? []} columns={columns} empty={list.loading ? 'Loading…' : 'No movements in this period.'} />
        {list.data && <Pager page={page} pageSize={pageSize} total={list.data.total} onPage={setPage} />}
      </Panel>
      <p className="faint" style={{ fontSize: 12 }}>Reports › IT accessory movements and IT accessory stock give the same data with any columns, grouping and export.</p>
    </>
  );
}

// ---- catalogue ---------------------------------------------------------------------------------------------------

function CatalogueTab({ items, loading, canEdit, onEdit, onChanged, onError }: {
  items: AccessoryItem[]; loading: boolean; canEdit: boolean; onEdit: (i: AccessoryItem) => void; onChanged: (m: string) => void; onError: (m: string) => void;
}) {
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [showInactive, setShowInactive] = useState(false);
  const f = q.trim().toLowerCase();
  const rows = items.filter((i) => (showInactive || i.active) && (!f || [i.name, i.category, i.brand, i.model, i.sku].some((x) => x?.toLowerCase().includes(f))));
  async function remove(i: AccessoryItem) {
    if (!(await confirm({ title: `Delete ${i.name}?`, danger: true, confirmLabel: 'Delete', message: 'Only accessories without stock history can be deleted.' }))) return;
    try { await api.del(`/accessories/items/${i.id}`); onChanged(`${i.name} deleted.`); } catch (e) { onError(((e as { body?: { title?: string } }).body?.title) ?? (e instanceof Error ? e.message : String(e))); }
  }
  const all: Column<AccessoryItem>[] = [
    { key: 'n', label: 'Accessory', className: 'wrap', render: (i) => <><b>{i.name}</b>{!i.active && <> <Pill>Inactive</Pill></>}{(i.brand || i.model) && <div className="faint" style={{ fontSize: 11.5 }}>{[i.brand, i.model].filter(Boolean).join(' ')}</div>}</> },
    { key: 'c', label: 'Category', render: (i) => i.category },
    { key: 'q', label: 'In stock', render: (i) => <span className={`stock-qty${i.low ? (i.quantity === 0 ? ' out' : ' low') : ''}`}>{i.quantity}</span> },
    { key: 'loc', label: 'By location', className: 'wrap', render: (i) => i.locations.length ? <span style={{ fontSize: 12.5 }}>{i.locations.map((l) => `${l.location}: ${l.quantity}`).join(' · ')}</span> : <span className="muted">—</span> },
    { key: 'r', label: 'Reorder level', render: (i) => i.reorderLevel || <span className="muted">—</span> },
    { key: 't', label: 'Type', render: (i) => <>{i.returnable ? 'Returnable' : 'Consumable'}{i.serialTracked && <div className="faint" style={{ fontSize: 11.5 }}>serial numbers</div>}</> },
    { key: 'uc', label: 'Unit cost', render: (i) => money(i.unitCost) },
    { key: 'sku', label: 'SKU / part number', optional: true, render: (i) => i.sku ? <span className="mono">{i.sku}</span> : <span className="muted">—</span> },
    { key: 'f', label: 'Faulty', optional: true, render: (i) => i.faulty || <span className="muted">—</span> },
    { key: 'notes', label: 'Notes', optional: true, className: 'wrap', render: (i) => i.notes ?? <span className="muted">—</span> },
    { key: 'x', label: '', render: (i) => canEdit ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
      <button className="btn sm" onClick={() => onEdit(i)}>Edit</button>
      {i.movements === 0 && <button className="btn sm danger" onClick={() => void remove(i)}>Delete</button>}
    </div> : null },
  ];
  const { columns, chooser } = useColumns('accessories.catalogue', all);
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Filter accessories…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Filter accessories" />
        <label className="row-check"><input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} /> Show inactive</label>
        {chooser}
      </div>
      <Panel flush>
        <DataTable rows={rows} columns={columns} empty={loading ? 'Loading…' : 'No accessories yet. Use Add accessory.'} />
      </Panel>
    </>
  );
}
