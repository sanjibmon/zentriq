import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, ApiError } from '../../api/client';
import { Callout, fmt, Pill } from '../../components/ui';
import { Icon } from '../../components/Icon';
import type { Department } from '../departments/Departments';
import { code128Svg } from './code128';
import {
  CONDITIONS, day, daysUntil, money, STATUSES, statusLabel, statusTone,
  type ImportResult, type NonItAsset, type NonItCategory, type NonItField,
} from './types';

export const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : e instanceof Error ? e.message : String(e);
};
const fieldErrors = (e: unknown): Record<string, string> => {
  const b = e instanceof ApiError ? (e.body as { errors?: Record<string, string[]> } | null) : null;
  return Object.fromEntries(Object.entries(b?.errors ?? {}).map(([k, v]) => [k, v.join(' ')]));
};

function Modal({ title, wide, onClose, children, footer }: { title: string; wide?: boolean; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEffect(() => {
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-label={title} onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={`modal-card${wide ? ' wide' : ''}`}>
        <header><h2>{title}</h2><button className="icon-btn" style={{ marginLeft: 'auto' }} onClick={onClose} aria-label="Close"><Icon name="x" /></button></header>
        <div className="body">{children}</div>
        {footer && <footer>{footer}</footer>}
      </div>
    </div>
  );
}

// ---- Add / edit ---------------------------------------------------------------------------------------------------------

type Values = Record<string, string>;
const EMPTY: Values = { status: 'in_use', quantity: '1' };

function toValues(a: NonItAsset): Values {
  const v: Values = {};
  const put = (k: string, x: unknown) => { if (x != null && x !== '') v[k] = String(x).slice(0, k.endsWith('Date') || k.endsWith('Until') || k.endsWith('Maintenance') ? 10 : undefined); };
  (['assetTag', 'name', 'categoryId', 'manufacturer', 'model', 'serialNumber', 'barcode', 'status', 'condition', 'quantity', 'location', 'departmentId',
    'assignedTo', 'purchaseDate', 'vendor', 'purchaseCost', 'invoiceNumber', 'poNumber', 'warrantyUntil', 'amcVendor', 'amcUntil', 'insuranceUntil',
    'lastMaintenance', 'nextMaintenance', 'usefulLifeYears', 'salvageValue', 'notes'] as const).forEach((k) => put(k, a[k]));
  for (const [k, x] of Object.entries(a.custom ?? {})) put(`custom.${k}`, x);
  return v;
}

function toBody(v: Values, rowVersion?: number) {
  const s = (k: string) => (v[k]?.trim() ? v[k]!.trim() : null);
  const n = (k: string) => (v[k]?.trim() ? Number(v[k]) : null);
  const custom: Record<string, string> = {};
  for (const [k, x] of Object.entries(v)) if (k.startsWith('custom.') && x.trim()) custom[k.slice(7)] = x.trim();
  return {
    assetTag: s('assetTag'), name: s('name'), categoryId: s('categoryId'), manufacturer: s('manufacturer'), model: s('model'), serialNumber: s('serialNumber'),
    barcode: s('barcode'), status: s('status') ?? 'in_use', condition: s('condition'), quantity: n('quantity') ?? 1, location: s('location'),
    departmentId: s('departmentId'), assignedTo: s('assignedTo'), purchaseDate: s('purchaseDate'), vendor: s('vendor'), purchaseCost: n('purchaseCost'),
    invoiceNumber: s('invoiceNumber'), poNumber: s('poNumber'), warrantyUntil: s('warrantyUntil'), amcVendor: s('amcVendor'), amcUntil: s('amcUntil'),
    insuranceUntil: s('insuranceUntil'), lastMaintenance: s('lastMaintenance'), nextMaintenance: s('nextMaintenance'),
    usefulLifeYears: n('usefulLifeYears'), salvageValue: n('salvageValue'), notes: s('notes'), custom, rowVersion: rowVersion ?? null,
  };
}

export function AssetForm({ asset, initial, categories, departments, fields, onClose, onSaved }: {
  asset: NonItAsset | null; initial?: Values; categories: NonItCategory[]; departments: Department[]; fields: NonItField[];
  onClose: () => void; onSaved: (id: string) => void;
}) {
  const [v, setV] = useState<Values>(() => (asset ? toValues(asset) : { ...EMPTY, ...initial }));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (k: string) => (e: { target: { value: string } }) => setV((x) => ({ ...x, [k]: e.target.value }));
  const groups = useMemo(() => [...new Set(categories.map((c) => c.group))], [categories]);
  const err = (k: string) => errors[k] && <span className="field-err">{errors[k]}</span>;

  async function save() {
    setBusy(true); setErrors({}); setMsg(null);
    try {
      const body = toBody(v, asset?.rowVersion);
      const r = asset ? await api.put<{ id: string }>(`/nonit/assets/${asset.id}`, body) : await api.post<{ id: string }>('/nonit/assets', body);
      onSaved(r.id);
    } catch (e) {
      const fe = fieldErrors(e);
      setErrors(fe);
      setMsg(Object.keys(fe).length ? 'Check the highlighted fields.' : errText(e));
    } finally { setBusy(false); }
  }

  const F = ({ k, label, type = 'text', full, hint, ...rest }: { k: string; label: string; type?: string; full?: boolean; hint?: string; step?: string; min?: string; placeholder?: string }) => (
    <label className={`f${full ? ' full' : ''}`}>{label}
      <input className={`in${errors[k] ? ' err' : ''}`} type={type} value={v[k] ?? ''} onChange={set(k)} {...rest} />
      {hint && <span className="hint">{hint}</span>}{err(k)}
    </label>
  );

  return (
    <Modal wide title={asset ? `Edit ${asset.assetTag}` : 'Add non-IT asset'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy} onClick={() => void save()}>{asset ? 'Save changes' : 'Add asset'}</button></>}>
      {msg && <Callout tone="crit" icon="alert">{msg}</Callout>}
      <div className="form-sections">
        <section>
          <h4>Item</h4>
          <div className="form-grid">
            {F({ k: 'name', label: 'Name *', full: true, placeholder: 'e.g. Ergonomic chair, Split AC 1.5 ton' })}
            <label className="f">Category *
              <select className={`in${errors.category ? ' err' : ''}`} value={v.categoryId ?? ''} onChange={set('categoryId')}>
                <option value="">Choose…</option>
                {groups.map((g) => <optgroup key={g} label={g}>{categories.filter((c) => c.group === g).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</optgroup>)}
              </select>{err('category')}
            </label>
            <label className="f">Status
              <select className="in" value={v.status ?? 'in_use'} onChange={set('status')}>{STATUSES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
            </label>
            {F({ k: 'manufacturer', label: 'Manufacturer / brand' })}
            {F({ k: 'model', label: 'Model' })}
            {F({ k: 'serialNumber', label: 'Serial number' })}
            <label className="f">Condition
              <select className="in" value={v.condition ?? ''} onChange={set('condition')}><option value="">Not recorded</option>{CONDITIONS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
            </label>
            {F({ k: 'assetTag', label: 'Asset tag', hint: asset ? undefined : 'Empty = next number (NIT-000001 …)' })}
            {F({ k: 'barcode', label: 'Barcode', hint: 'Scan into this box, or leave empty to use the asset tag' })}
            {F({ k: 'quantity', label: 'Quantity', type: 'number', min: '0' })}
          </div>
        </section>
        <section>
          <h4>Where and who</h4>
          <div className="form-grid">
            {F({ k: 'location', label: 'Location', placeholder: 'Site / building / floor / room' })}
            <label className="f">Department
              <select className="in" value={v.departmentId ?? ''} onChange={set('departmentId')}><option value="">Not assigned</option>{departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select>
              {err('department')}
            </label>
            {F({ k: 'assignedTo', label: 'Assigned to', placeholder: 'Person or team' })}
          </div>
        </section>
        <section>
          <h4>Purchase</h4>
          <div className="form-grid">
            {F({ k: 'purchaseDate', label: 'Purchase date', type: 'date' })}
            {F({ k: 'vendor', label: 'Vendor' })}
            {F({ k: 'purchaseCost', label: 'Purchase cost (per item)', type: 'number', step: '0.01', min: '0' })}
            {F({ k: 'invoiceNumber', label: 'Invoice number' })}
            {F({ k: 'poNumber', label: 'PO number' })}
            {F({ k: 'usefulLifeYears', label: 'Useful life (years)', type: 'number', step: '0.5', min: '0', hint: 'For straight-line depreciation' })}
            {F({ k: 'salvageValue', label: 'Salvage value', type: 'number', step: '0.01', min: '0' })}
          </div>
        </section>
        <section>
          <h4>Warranty, AMC and maintenance</h4>
          <div className="form-grid">
            {F({ k: 'warrantyUntil', label: 'Warranty until', type: 'date' })}
            {F({ k: 'insuranceUntil', label: 'Insurance until', type: 'date' })}
            {F({ k: 'amcVendor', label: 'AMC vendor' })}
            {F({ k: 'amcUntil', label: 'AMC until', type: 'date' })}
            {F({ k: 'lastMaintenance', label: 'Last maintenance', type: 'date' })}
            {F({ k: 'nextMaintenance', label: 'Next maintenance', type: 'date' })}
          </div>
        </section>
        {fields.length > 0 && (
          <section>
            <h4>More details</h4>
            <div className="form-grid">
              {fields.map((f) => {
                const k = `custom.${f.key}`;
                if (f.type === 'list' || f.type === 'yesno') {
                  const opts = f.type === 'yesno' ? ['Yes', 'No'] : f.options;
                  return <label key={f.id} className="f">{f.label}<select className="in" value={v[k] ?? ''} onChange={set(k)}><option value="" />{opts.map((o) => <option key={o}>{o}</option>)}</select>{err(k)}</label>;
                }
                return <span key={f.id} style={{ display: 'contents' }}>{F({ k, label: f.label, type: f.type === 'number' ? 'number' : f.type === 'date' ? 'date' : 'text' })}</span>;
              })}
            </div>
          </section>
        )}
        <section>
          <h4>Notes</h4>
          <textarea className="in" rows={3} style={{ width: '100%', fontFamily: 'inherit', fontSize: 14 }} value={v.notes ?? ''} onChange={set('notes')} />
        </section>
      </div>
    </Modal>
  );
}

// ---- Asset details ---------------------------------------------------------------------------------------------------

const ACTION: Record<string, string> = {
  created: 'Added', updated: 'Changed', imported: 'Added by bulk upload', 'import-updated': 'Updated by bulk upload', 'bulk-updated': 'Changed (several at once)',
};
const COLUMN: Record<string, string> = {
  asset_tag: 'Asset tag', barcode: 'Barcode', name: 'Name', category_id: 'Category', manufacturer: 'Manufacturer', model: 'Model', serial_number: 'Serial number',
  status: 'Status', condition: 'Condition', quantity: 'Quantity', location: 'Location', department_id: 'Department', assigned_to: 'Assigned to',
  purchase_date: 'Purchase date', vendor: 'Vendor', purchase_cost: 'Purchase cost', invoice_number: 'Invoice number', po_number: 'PO number',
  warranty_until: 'Warranty until', amc_vendor: 'AMC vendor', amc_until: 'AMC until', insurance_until: 'Insurance until', last_maintenance: 'Last maintenance',
  next_maintenance: 'Next maintenance', useful_life_years: 'Useful life', salvage_value: 'Salvage value', disposed_on: 'Disposed on', notes: 'Notes', custom: 'More details',
};

function Due({ date, label }: { date: string | null; label: string }) {
  const d = daysUntil(date);
  if (d == null) return <>—</>;
  const tone = d < 0 ? 'crit' : d <= 30 ? 'warn' : '';
  return <>{day(date)} {tone && <Pill tone={tone}>{d < 0 ? `${label} ${-d} d ago` : `in ${d} d`}</Pill>}</>;
}

export function AssetDetails({ id, fields, canEdit, onClose, onEdit, onDeleted }: {
  id: string; fields: NonItField[]; canEdit: boolean; onClose: () => void; onEdit: (a: NonItAsset) => void; onDeleted: () => void;
}) {
  const [a, setA] = useState<NonItAsset | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.get<NonItAsset>(`/nonit/assets/${id}`).then(setA, (e) => setError(errText(e))); }, [id]);
  async function remove() {
    if (!a || !window.confirm(`Delete ${a.assetTag} ${a.name}? Its history is deleted too.`)) return;
    try { await api.post('/nonit/assets/delete', { ids: [a.id] }); onDeleted(); } catch (e) { setError(errText(e)); }
  }
  const kv = (items: [string, ReactNode][]) => <dl className="kv-grid">{items.map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v ?? '—'}</dd></div>)}</dl>;
  return (
    <div className="drawer-scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <aside className="drawer" style={{ width: 'min(760px, 100%)' }} aria-label="Asset details">
        <header>
          <h2>{a ? <>{a.name} <span className="faint mono" style={{ fontSize: 13 }}>{a.assetTag}</span></> : 'Asset'}</h2>
          {a && canEdit && <button className="btn sm" onClick={() => onEdit(a)}><Icon name="edit" size={14} /> Edit</button>}
          {a && <button className="btn sm" onClick={() => printLabels([a])}>Print label</button>}
          {a && canEdit && <button className="btn sm danger" onClick={() => void remove()}><Icon name="trash" size={14} /> Delete</button>}
          <button className="icon-btn" onClick={onClose} aria-label="Close"><Icon name="x" /></button>
        </header>
        <div className="body">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {!a ? <div className="empty">Loading…</div> : (
            <>
              <div className="nonit-head">
                <div>
                  <Pill tone={statusTone(a.status)}>{statusLabel(a.status)}</Pill>{' '}
                  <span className="muted">{[a.group, a.category].filter(Boolean).join(' › ')}</span>
                </div>
                <div className="barcode" dangerouslySetInnerHTML={{ __html: code128Svg(a.barcode ?? a.assetTag, { module: 1.6, height: 44 }) }} />
              </div>
              {kv([
                ['Manufacturer / model', [a.manufacturer, a.model].filter(Boolean).join(' ') || null], ['Serial number', a.serialNumber && <span className="mono">{a.serialNumber}</span>],
                ['Condition', a.condition ? fmt.label(a.condition) : null], ['Quantity', a.quantity],
                ['Location', a.location], ['Department', a.department], ['Assigned to', a.assignedTo],
              ])}
              <h4 className="sec">Purchase and value</h4>
              {kv([
                ['Purchase date', day(a.purchaseDate)], ['Vendor', a.vendor], ['Cost', money(a.purchaseCost)], ['Current value', money(a.currentValue)],
                ['Invoice / PO', [a.invoiceNumber, a.poNumber].filter(Boolean).join(' / ') || null], ['Useful life', a.usefulLifeYears != null ? `${a.usefulLifeYears} years` : null],
              ])}
              <h4 className="sec">Warranty, AMC and maintenance</h4>
              {kv([
                ['Warranty until', <Due date={a.warrantyUntil} label="ended" />], ['AMC', a.amcVendor || a.amcUntil ? <>{a.amcVendor ?? ''} <Due date={a.amcUntil} label="ended" /></> : null],
                ['Insurance until', <Due date={a.insuranceUntil} label="ended" />], ['Last maintenance', day(a.lastMaintenance)],
                ['Next maintenance', <Due date={a.nextMaintenance} label="overdue" />], ...(a.disposedOn ? [['Disposed on', day(a.disposedOn)] as [string, ReactNode]] : []),
              ])}
              {fields.length > 0 && <><h4 className="sec">More details</h4>{kv(fields.map((f) => [f.label, a.custom?.[f.key] ?? null]))}</>}
              {a.notes && <><h4 className="sec">Notes</h4><p style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{a.notes}</p></>}
              <h4 className="sec">History</h4>
              <ol className="timeline">
                {(a.history ?? []).map((h, i) => (
                  <li key={i}>
                    <b>{ACTION[h.action] ?? fmt.label(h.action)}</b> <span className="faint">{fmt.date(h.at)}{h.actor ? ` · ${h.actor}` : ''}</span>
                    {Object.keys(h.changes ?? {}).length > 0 && (
                      <div className="faint" style={{ fontSize: 12.5 }}>
                        {Object.entries(h.changes).map(([k, c]) => {
                          const ch = c as { from?: unknown; to?: unknown } | null;
                          const show = (x: unknown) => (x == null || x === '' ? '—' : typeof x === 'object' ? 'changed' : String(x));
                          return <div key={k}>{COLUMN[k] ?? k}: {ch && typeof ch === 'object' && 'to' in ch ? <>{show(ch.from)} → {show(ch.to)}</> : show(c)}</div>;
                        })}
                      </div>
                    )}
                  </li>
                ))}
              </ol>
              <p className="faint" style={{ fontSize: 12 }}>Added {fmt.date(a.createdAt)}{a.createdBy ? ` by ${a.createdBy}` : ''} · last changed {fmt.date(a.updatedAt)}{a.updatedBy ? ` by ${a.updatedBy}` : ''}</p>
            </>
          )}
        </div>
      </aside>
    </div>
  );
}

// ---- Bulk upload --------------------------------------------------------------------------------------------------------

export function BulkUpload({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [update, setUpdate] = useState(true);
  const [createCats, setCreateCats] = useState(false);
  const [check, setCheck] = useState<ImportResult | null>(null);
  const [done, setDone] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(dryRun: boolean) {
    if (!file) return;
    setBusy(true); setError(null);
    try {
      const r = await api.upload<ImportResult>('/nonit/import', { dryRun, update, createCategories: createCats, name: file.name }, file);
      if (dryRun) setCheck(r); else { setDone(r); if (r.ok) onDone(); }
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  const result = done ?? check;
  return (
    <Modal wide title="Bulk upload from Excel" onClose={onClose}
      footer={done ? <button className="btn primary" onClick={onClose}>Close</button> : <>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn" disabled={!file || busy} onClick={() => void run(true)}>Check file</button>
        <button className="btn primary" disabled={!file || busy || !check || !check.ok} onClick={() => void run(false)}>
          {check?.ok ? `Upload ${check.created + check.updated} row(s)` : 'Upload'}
        </button>
      </>}>
      <ol className="steps-v">
        <li>
          <b>Download the form</b>
          <p className="muted">An Excel workbook with the columns, drop-down lists of your categories, statuses and departments, and instructions.</p>
          <a className="btn sm" href="/api/v1/nonit/template" download><Icon name="down" size={14} /> Download Excel form</a>
        </li>
        <li>
          <b>Fill one row per item and save</b>
          <p className="muted">Name and category are required. A row with an existing asset tag updates that asset; empty cells leave its values as they are.</p>
        </li>
        <li>
          <b>Choose the file and check it</b>
          <div className="toolbar-row" style={{ marginTop: 6 }}>
            <input type="file" accept=".xlsx,.csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setCheck(null); setDone(null); }} aria-label="Excel or CSV file" />
          </div>
          <label className="row-check"><input type="checkbox" checked={update} onChange={(e) => { setUpdate(e.target.checked); setCheck(null); }} /> <span>Update assets whose tag already exists (otherwise skip them)</span></label>
          <label className="row-check"><input type="checkbox" checked={createCats} onChange={(e) => { setCreateCats(e.target.checked); setCheck(null); }} /> <span>Create categories that don't exist yet (group "Custom")</span></label>
        </li>
      </ol>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      {result && (
        <div style={{ marginTop: 12 }}>
          {result.ok
            ? <Callout tone="ok" icon="info">{done ? 'Uploaded' : 'Ready to upload'}: {result.rows} row(s) — {result.created} new, {result.updated} updated{result.skipped ? `, ${result.skipped} skipped` : ''}.
                {result.newCategories.length > 0 && <> New categories: {result.newCategories.join(', ')}.</>}</Callout>
            : <Callout tone="crit" icon="alert">{result.errors.length} problem(s) in {result.rows} row(s). Nothing was saved — fix the file and check it again.</Callout>}
          {result.unknownColumns.length > 0 && <p className="faint" style={{ fontSize: 12.5 }}>Columns not used: {result.unknownColumns.join(', ')}</p>}
          {result.errors.length > 0 && (
            <div className="tbl-wrap" style={{ maxHeight: 260, overflow: 'auto' }}>
              <table className="t"><thead><tr><th>Row</th><th>Problem</th></tr></thead>
                <tbody>{result.errors.map((e, i) => <tr key={i}><td className="mono">{e.row}</td><td className="wrap">{e.message}</td></tr>)}</tbody></table>
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}

// ---- Barcode scan -------------------------------------------------------------------------------------------------------

interface Detector { detect: (src: CanvasImageSource) => Promise<{ rawValue: string }[]> }
type DetectorCtor = new (o?: { formats?: string[] }) => Detector;

/**
 * Find an asset by barcode: a USB / Bluetooth scanner types into the box and presses Enter; on devices whose browser can read
 * barcodes from the camera (BarcodeDetector), the camera can be used too.
 */
export function ScanDialog({ onClose, onFound, onNew }: { onClose: () => void; onFound: (a: NonItAsset) => void; onNew: (code: string) => void }) {
  const [code, setCode] = useState('');
  const [missing, setMissing] = useState<string | null>(null);
  const [camera, setCamera] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const video = useRef<HTMLVideoElement>(null);
  const Ctor = (window as unknown as { BarcodeDetector?: DetectorCtor }).BarcodeDetector;

  async function lookup(c: string) {
    const t = c.trim();
    if (!t) return;
    setMissing(null); setError(null);
    try { onFound(await api.get<NonItAsset>('/nonit/lookup', { code: t })); }
    catch (e) { if (e instanceof ApiError && e.status === 404) setMissing(t); else setError(errText(e)); }
  }

  useEffect(() => {
    if (!camera || !Ctor) return;
    let stream: MediaStream | null = null;
    let stop = false;
    const detector = new Ctor({ formats: ['code_128', 'code_39', 'ean_13', 'ean_8', 'qr_code', 'upc_a', 'itf'] });
    (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
        if (!video.current) return;
        video.current.srcObject = stream;
        await video.current.play();
        while (!stop) {
          const found = await detector.detect(video.current).catch(() => []);
          if (found[0]?.rawValue) { setCode(found[0].rawValue); setCamera(false); void lookup(found[0].rawValue); return; }
          await new Promise((r) => setTimeout(r, 250));
        }
      } catch (e) { setError(`Camera not available: ${e instanceof Error ? e.message : String(e)}`); setCamera(false); }
    })();
    return () => { stop = true; stream?.getTracks().forEach((t) => t.stop()); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [camera]);

  return (
    <Modal title="Scan a barcode" onClose={onClose}>
      <p className="muted" style={{ marginTop: 0 }}>Scan the label with a barcode scanner, or type the asset tag, barcode or serial number and press Enter.</p>
      <form onSubmit={(e) => { e.preventDefault(); void lookup(code); }} className="toolbar-row">
        <input className="in grow" autoFocus value={code} onChange={(e) => setCode(e.target.value)} placeholder="Barcode, asset tag or serial number" aria-label="Barcode" />
        <button className="btn primary" type="submit">Find</button>
        {Ctor && <button className="btn" type="button" onClick={() => setCamera((c) => !c)}>{camera ? 'Stop camera' : 'Use camera'}</button>}
      </form>
      {camera && <video ref={video} className="scan-video" muted playsInline />}
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      {missing && (
        <Callout tone="warn" icon="info">
          No asset has the code <b className="mono">{missing}</b>. <button className="link" onClick={() => onNew(missing)}>Add a new asset with this barcode</button>
        </Callout>
      )}
    </Modal>
  );
}

// ---- Labels ---------------------------------------------------------------------------------------------------------------

/** Opens a print-ready sheet of barcode labels (asset tag, name, location). */
export function printLabels(assets: Pick<NonItAsset, 'assetTag' | 'barcode' | 'name' | 'location'>[]) {
  const w = window.open('', '_blank', 'width=900,height=700');
  if (!w) { window.alert('Allow pop-ups for this site to print labels.'); return; }
  const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
  const labels = assets.map((a) => `<div class="l"><div class="n">${esc(a.name)}</div>${code128Svg(a.barcode ?? a.assetTag, { module: 1.4, height: 46 })}${a.location ? `<div class="loc">${esc(a.location)}</div>` : ''}</div>`).join('');
  w.document.write(`<!doctype html><html><head><title>Asset labels</title><style>
    @page{margin:8mm} body{font-family:Segoe UI,Arial,sans-serif;margin:0}
    .grid{display:grid;grid-template-columns:repeat(3,63mm);gap:3mm}
    .l{border:1px dashed #bbb;border-radius:2mm;padding:2.5mm;height:34mm;box-sizing:border-box;display:flex;flex-direction:column;align-items:center;justify-content:center;overflow:hidden;break-inside:avoid}
    .l svg{max-width:100%;height:auto}.n{font-size:10pt;font-weight:600;text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:100%}
    .loc{font-size:8pt;color:#555}.bar{padding:8px 0;font-size:12px;color:#444}@media print{.bar{display:none}}
  </style></head><body><div class="bar">${assets.length} label(s) · 3 per row (63 × 34 mm). <button onclick="print()">Print</button></div><div class="grid">${labels}</div></body></html>`);
  w.document.close();
}
