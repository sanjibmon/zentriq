import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { Asset, Paged } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout } from '../../components/ui';
import { DepartmentSelect, useDepartments } from '../departments/Departments';
import { LocationSelect, type Location } from '../locations/Locations';
import { ISSUE_REASONS, WRITEOFF_REASONS, type AccessoryItem, type MovementKind } from './types';

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : b?.title ?? (e instanceof Error ? e.message : String(e));
};

// ---- catalogue ---------------------------------------------------------------------------------------------------

export function ItemDialog({ item, categories, onClose, onSaved }: { item: AccessoryItem | null; categories: string[]; onClose: () => void; onSaved: (msg: string) => void }) {
  const [f, setF] = useState({
    name: item?.name ?? '', category: item?.category ?? '', brand: item?.brand ?? '', model: item?.model ?? '', sku: item?.sku ?? '',
    unitCost: item?.unitCost?.toString() ?? '', reorderLevel: (item?.reorderLevel ?? 5).toString(), returnable: item?.returnable ?? true,
    serialTracked: item?.serialTracked ?? false, notes: item?.notes ?? '', active: item?.active ?? true,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (k: keyof typeof f, v: string | boolean) => setF({ ...f, [k]: v });
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    const body = { ...f, unitCost: f.unitCost === '' ? null : Number(f.unitCost), reorderLevel: f.reorderLevel === '' ? 0 : Number(f.reorderLevel) };
    try {
      if (item) await api.put(`/accessories/items/${item.id}`, body); else await api.post('/accessories/items', body);
      onSaved(item ? `${f.name} saved.` : `${f.name} added. Use Receive stock to add quantities.`);
    } catch (x) { setError(errText(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={item ? `Edit ${item.name}` : 'Add accessory'} onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="acc-item" className="btn primary" disabled={busy || !f.name.trim() || !f.category.trim()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <form id="acc-item" className="form-v" onSubmit={save}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="grid-2 tight">
          <label>Name<input className="in" value={f.name} onChange={(e) => set('name', e.target.value)} placeholder="e.g. Logitech M90 USB mouse" autoFocus /></label>
          <label>Category
            <input className="in" list="acc-cats" value={f.category} onChange={(e) => set('category', e.target.value)} placeholder="Mouse, Keyboard, Headset…" />
            <datalist id="acc-cats">{categories.map((c) => <option key={c} value={c} />)}</datalist>
          </label>
          <label>Brand<input className="in" value={f.brand} onChange={(e) => set('brand', e.target.value)} /></label>
          <label>Model<input className="in" value={f.model} onChange={(e) => set('model', e.target.value)} /></label>
          <label>SKU / part number / barcode<input className="in" value={f.sku} onChange={(e) => set('sku', e.target.value)} /></label>
          <label>Unit cost<input className="in" type="number" min={0} step="0.01" value={f.unitCost} onChange={(e) => set('unitCost', e.target.value)} /></label>
          <label>Reorder level<input className="in" type="number" min={0} value={f.reorderLevel} onChange={(e) => set('reorderLevel', e.target.value)} />
            <small className="muted">Alert when the stock at a location falls to this number (0 = no alert).</small></label>
        </div>
        <label className="row-check"><input type="checkbox" checked={f.returnable} onChange={(e) => set('returnable', e.target.checked)} /> Returnable — expected back when the person leaves (untick for consumables such as cables or batteries)</label>
        <label className="row-check"><input type="checkbox" checked={f.serialTracked} disabled={!!item && item.movements > 0} onChange={(e) => set('serialTracked', e.target.checked)} /> Track each unit by serial number (docking stations, premium headsets…)</label>
        {item && <label className="row-check"><input type="checkbox" checked={f.active} onChange={(e) => set('active', e.target.checked)} /> Active (untick to hide it; the history is kept)</label>}
        <label>Notes<textarea className="in" rows={2} value={f.notes} onChange={(e) => set('notes', e.target.value)} /></label>
      </form>
    </Modal>
  );
}

// ---- people and devices ------------------------------------------------------------------------------------------

export function PersonInput({ value, onChange, id }: { value: string; onChange: (v: string) => void; id: string }) {
  const [q, setQ] = useState(value);
  useEffect(() => { const t = setTimeout(() => setQ(value), 250); return () => clearTimeout(t); }, [value]);
  const people = useApi(() => api.get<{ name: string; source: string }[]>('/accessories/people', { q }), [q]);
  return (
    <>
      <input className="in" list={id} value={value} onChange={(e) => onChange(e.target.value)} placeholder="Name of the employee" autoComplete="off" />
      <datalist id={id}>{(people.data ?? []).map((p) => <option key={p.name} value={p.name}>{p.source === 'directory' ? 'Directory' : p.source === 'device' ? 'Logged-on user' : 'Has accessories'}</option>)}</datalist>
    </>
  );
}

function DevicePicker({ value, onChange }: { value: { id: string; name: string } | null; onChange: (v: { id: string; name: string } | null) => void }) {
  const [text, setText] = useState(value?.name ?? '');
  const [q, setQ] = useState('');
  useEffect(() => { const t = setTimeout(() => setQ(text), 250); return () => clearTimeout(t); }, [text]);
  const list = useApi(() => (q.trim().length >= 2 ? api.get<Paged<Asset>>('/assets', { q, pageSize: 20 }) : Promise.resolve(null)), [q]);
  const options = (list.data?.items ?? []).map((a) => ({ id: a.id, name: a.hostname ?? a.ip ?? a.id }));
  return (
    <>
      <input className="in" list="acc-devices" value={text} placeholder="Optional: hostname of the device it is used with" autoComplete="off"
        onChange={(e) => { setText(e.target.value); const m = options.find((o) => o.name.toLowerCase() === e.target.value.trim().toLowerCase()); onChange(m ?? null); }} />
      <datalist id="acc-devices">{options.map((o) => <option key={o.id} value={o.name} />)}</datalist>
    </>
  );
}

// ---- serial numbers ----------------------------------------------------------------------------------------------

function SerialPicker({ itemId, status, locationId, person, value, onChange }: {
  itemId: string; status: 'in_stock' | 'issued' | 'faulty'; locationId?: string; person?: string; value: string[]; onChange: (v: string[]) => void;
}) {
  const units = useApi(() => api.get<{ serial: string; location: string | null; person: string | null }[]>(`/accessories/items/${itemId}/units`, { status, locationId: status === 'issued' ? undefined : locationId, person: status === 'issued' ? person : undefined }), [itemId, status, locationId, person]);
  const list = units.data ?? [];
  if (units.loading && !units.data) return <small className="muted">Loading serial numbers…</small>;
  if (list.length === 0) return <small className="muted">{status === 'issued' ? 'No units of this accessory are issued to this person.' : 'No units of this accessory here.'}</small>;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 12px', gridColumn: '1 / -1' }}>
      {list.map((u) => (
        <label key={u.serial} className="row-check" style={{ fontSize: 12.5 }}>
          <input type="checkbox" checked={value.includes(u.serial)} onChange={(e) => onChange(e.target.checked ? [...value, u.serial] : value.filter((x) => x !== u.serial))} />
          <span className="mono">{u.serial}</span>
        </label>
      ))}
    </div>
  );
}

// ---- movements ---------------------------------------------------------------------------------------------------

interface Line { key: number; itemId: string; quantity: string; unitCost: string; serialText: string; serials: string[] }
export interface MovementPreset { itemId?: string; locationId?: string; person?: string; departmentId?: string | null; quantity?: number; serials?: string[] }

const TITLE: Record<MovementKind, string> = {
  receive: 'Receive stock', issue: 'Issue accessories', return: 'Return accessories', transfer: 'Transfer stock', writeoff: 'Write off', adjust: 'Stock count',
};
const BUTTON: Record<MovementKind, string> = {
  receive: 'Receive', issue: 'Issue', return: 'Return', transfer: 'Transfer', writeoff: 'Write off', adjust: 'Save count',
};

/** Receive, issue, return, transfer, write off or count accessories (several accessories at once). */
export function MovementDialog({ kind, items, locations, preset, onClose, onDone, onLocationsChanged }: {
  kind: MovementKind; items: AccessoryItem[]; locations: Location[]; preset?: MovementPreset; onClose: () => void; onDone: (msg: string) => void; onLocationsChanged: () => void;
}) {
  const deps = useDepartments();
  const [locationId, setLocationId] = useState(preset?.locationId ?? (locations.length === 1 ? locations[0]!.id : ''));
  const [toLocationId, setToLocationId] = useState('');
  const [person, setPerson] = useState(preset?.person ?? '');
  const [departmentId, setDepartmentId] = useState(preset?.departmentId ?? '');
  const [device, setDevice] = useState<{ id: string; name: string } | null>(null);
  const [condition, setCondition] = useState<'good' | 'faulty'>('good');
  const [reason, setReason] = useState(kind === 'issue' ? ISSUE_REASONS[0]! : kind === 'writeoff' ? WRITEOFF_REASONS[0]! : kind === 'adjust' ? 'Stock count' : '');
  const [reference, setReference] = useState('');
  const [vendor, setVendor] = useState('');
  const [note, setNote] = useState('');
  const [lines, setLines] = useState<Line[]>([{ key: 1, itemId: preset?.itemId ?? '', quantity: String(preset?.quantity ?? (kind === 'adjust' ? '' : 1)), unitCost: '', serialText: '', serials: preset?.serials ?? [] }]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const byId = new Map(items.map((i) => [i.id, i]));
  const setLine = (k: number, patch: Partial<Line>) => setLines(lines.map((l) => (l.key === k ? { ...l, ...patch } : l)));
  const stockAt = (itemId: string, loc: string) => byId.get(itemId)?.locations.find((x) => x.locationId === loc);
  const pickStatus: 'in_stock' | 'issued' | 'faulty' = kind === 'return' ? 'issued' : kind === 'writeoff' && condition === 'faulty' ? 'faulty' : 'in_stock';

  function qtyOf(l: Line): number {
    const it = byId.get(l.itemId);
    if (it?.serialTracked && kind !== 'adjust') return kind === 'receive' ? l.serialText.split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean).length : l.serials.length;
    return l.quantity === '' ? NaN : Number(l.quantity);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = {
        kind, locationId, toLocationId: kind === 'transfer' ? toLocationId : null,
        person: kind === 'issue' || kind === 'return' ? person.trim() || null : null,
        departmentId: kind === 'issue' ? departmentId || null : null, assetId: kind === 'issue' ? device?.id ?? null : null,
        condition: kind === 'return' || kind === 'writeoff' ? condition : null,
        reason: reason.trim() || null, reference: reference.trim() || null, vendor: kind === 'receive' ? vendor.trim() || null : null, note: note.trim() || null,
        lines: lines.filter((l) => l.itemId).map((l) => ({
          itemId: l.itemId, quantity: qtyOf(l), unitCost: kind === 'receive' && l.unitCost !== '' ? Number(l.unitCost) : null,
          serials: byId.get(l.itemId)?.serialTracked ? (kind === 'receive' ? l.serialText.split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean) : l.serials) : null,
        })),
      };
      const r = await api.post<{ message: string }>('/accessories/movements', body);
      onDone(r.message);
    } catch (x) { setError(errText(x)); } finally { setBusy(false); }
  }

  const usable = items.filter((i) => i.active);
  const valid = locationId && lines.some((l) => l.itemId) && lines.every((l) => !l.itemId || (Number.isFinite(qtyOf(l)) && (kind === 'adjust' ? qtyOf(l) >= 0 : qtyOf(l) > 0)))
    && (kind !== 'transfer' || (toLocationId && toLocationId !== locationId)) && (kind !== 'issue' || person.trim() || departmentId) && (kind !== 'return' || person.trim())
    && (kind !== 'writeoff' || reason.trim());

  return (
    <Modal title={TITLE[kind]} wide onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="acc-move" className={`btn ${kind === 'writeoff' ? 'danger' : 'primary'}`} disabled={busy || !valid}>{busy ? 'Saving…' : BUTTON[kind]}</button>
    </>}>
      <form id="acc-move" className="form-v" onSubmit={save}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="grid-2 tight">
          <label>{kind === 'receive' ? 'Received at (location)' : kind === 'return' ? 'Returned to (location)' : kind === 'transfer' ? 'From (location)' : 'Location'}
            <LocationSelect value={locationId} onChange={setLocationId} list={locations} onAdded={onLocationsChanged} canAdd allowNone noneLabel="— Choose a location —" />
          </label>
          {kind === 'transfer' && <label>To (location)
            <LocationSelect value={toLocationId} onChange={setToLocationId} list={locations.filter((l) => l.id !== locationId)} onAdded={onLocationsChanged} canAdd allowNone noneLabel="— Choose a location —" />
          </label>}
          {(kind === 'issue' || kind === 'return') && <label>{kind === 'issue' ? 'Issued to' : 'Returned by'}<PersonInput id="acc-people" value={person} onChange={setPerson} /></label>}
          {kind === 'issue' && <label>Department / unit
            <DepartmentSelect value={departmentId ?? ''} onChange={setDepartmentId} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd />
          </label>}
          {kind === 'issue' && <label>Device (optional)<DevicePicker value={device} onChange={setDevice} /></label>}
          {(kind === 'issue' || kind === 'writeoff') && <label>Reason
            <input className="in" list="acc-reasons" value={reason} onChange={(e) => setReason(e.target.value)} />
            <datalist id="acc-reasons">{(kind === 'issue' ? ISSUE_REASONS : WRITEOFF_REASONS).map((r) => <option key={r} value={r} />)}</datalist>
          </label>}
          {(kind === 'return' || kind === 'writeoff') && <label>{kind === 'return' ? 'Condition' : 'Write off from'}
            <select className="in" value={condition} onChange={(e) => setCondition(e.target.value as 'good' | 'faulty')}>
              <option value="good">{kind === 'return' ? 'Good — back into stock' : 'Usable stock'}</option>
              <option value="faulty">{kind === 'return' ? 'Faulty — kept apart, not issued again' : 'Faulty units'}</option>
            </select>
          </label>}
          {kind === 'receive' && <label>Vendor<input className="in" value={vendor} onChange={(e) => setVendor(e.target.value)} /></label>}
          {(kind === 'receive' || kind === 'issue' || kind === 'transfer') && <label>{kind === 'receive' ? 'Invoice / PO number' : 'Ticket / reference'}<input className="in" value={reference} onChange={(e) => setReference(e.target.value)} /></label>}
          {kind === 'adjust' && <label>Reason<input className="in" value={reason} onChange={(e) => setReason(e.target.value)} /></label>}
        </div>

        <div className="line-grid">
          <b style={{ fontSize: 13 }}>{kind === 'adjust' ? 'Counted quantities' : 'Accessories'}</b>
          {lines.map((l) => {
            const it = byId.get(l.itemId);
            const here = locationId && l.itemId ? stockAt(l.itemId, locationId) : undefined;
            return (
              <div key={l.key} className={`line-row${kind === 'receive' ? '' : ' no-cost'}`}>
                <div style={{ display: 'grid', gap: 2 }}>
                  <select className="in" value={l.itemId} onChange={(e) => setLine(l.key, { itemId: e.target.value, serials: [], serialText: '' })} aria-label="Accessory">
                    <option value="">— Choose an accessory —</option>
                    {usable.map((i) => <option key={i.id} value={i.id} disabled={lines.some((x) => x.key !== l.key && x.itemId === i.id)}>{i.name} · {i.category}</option>)}
                  </select>
                  {it && locationId && kind !== 'receive' && <small className="muted">At this location: {here?.quantity ?? 0} in stock{here?.faulty ? ` · ${here.faulty} faulty` : ''}</small>}
                </div>
                {it?.serialTracked && kind !== 'adjust'
                  ? <div className="in" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--surface-2)' }} title="Number of serial numbers">{Number.isFinite(qtyOf(l)) ? qtyOf(l) : 0}</div>
                  : <input className="in" type="number" min={kind === 'adjust' ? 0 : 1} value={l.quantity} onChange={(e) => setLine(l.key, { quantity: e.target.value })} aria-label={kind === 'adjust' ? 'Counted' : 'Quantity'} placeholder={kind === 'adjust' ? 'Counted' : 'Qty'} />}
                {kind === 'receive' && <input className="in cost" type="number" min={0} step="0.01" value={l.unitCost} onChange={(e) => setLine(l.key, { unitCost: e.target.value })} aria-label="Unit cost" placeholder={it?.unitCost != null ? `Cost ${it.unitCost}` : 'Unit cost'} />}
                <button type="button" className="icon-btn" aria-label="Remove line" disabled={lines.length === 1} onClick={() => setLines(lines.filter((x) => x.key !== l.key))}><Icon name="x" size={14} /></button>
                {it?.serialTracked && kind === 'receive' && <textarea className="in mono" rows={2} value={l.serialText} onChange={(e) => setLine(l.key, { serialText: e.target.value })} placeholder="Serial numbers, one per line (or separated by commas)" />}
                {it?.serialTracked && kind !== 'receive' && kind !== 'adjust' && (locationId || kind === 'return') && (
                  <SerialPicker itemId={it.id} status={pickStatus} locationId={locationId} person={person} value={l.serials} onChange={(v) => setLine(l.key, { serials: v })} />
                )}
              </div>
            );
          })}
          <div><button type="button" className="btn sm" onClick={() => setLines([...lines, { key: Math.max(...lines.map((x) => x.key)) + 1, itemId: '', quantity: kind === 'adjust' ? '' : '1', unitCost: '', serialText: '', serials: [] }])}><Icon name="plus" size={12} /> Add another accessory</button></div>
        </div>
        <label>Note<textarea className="in" rows={2} value={note} onChange={(e) => setNote(e.target.value)} /></label>
        {kind === 'adjust' && <small className="muted">Enter what you counted; the difference to the stock on record is booked as a correction with the reason above.</small>}
      </form>
    </Modal>
  );
}
