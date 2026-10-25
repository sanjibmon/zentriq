import { useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { Modal } from '../../components/Modal';
import { Callout } from '../../components/ui';
import { CustomFieldInputs, customText, LcPersonInput } from './LifecycleExtras';
import { DeviceTypeSelect } from '../assets/DeviceManagement';
import { DepartmentSelect, useDepartments } from '../departments/Departments';
import { LocationSelect, useLocations } from '../locations/Locations';
import { CONTRACT_KIND, stageLabel, type LcAsset, type LcContract, type LcMeta, type Stage } from './types';

export function useLcMeta() {
  return useApi(() => api.get<LcMeta>('/lifecycle/meta'), []);
}

const err = (e: unknown) => (e instanceof Error ? e.message : String(e));
const num = (s: string) => (s.trim() === '' ? null : Number(s));
const day = (s: string) => (s.trim() === '' ? null : s);

function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label>{label}{children}{hint && <small className="muted">{hint}</small>}</label>;
}

function Footer({ busy, onClose, label = 'Save', danger }: { busy: boolean; onClose: () => void; label?: string; danger?: boolean }) {
  return (
    <>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="lc-form" className={`btn ${danger ? 'danger' : 'primary'}`} disabled={busy}>{busy ? 'Saving…' : label}</button>
    </>
  );
}

// ---- stage / assign / return / dispose -----------------------------------------------------------------------------

export type StageMode = 'stage' | 'assign' | 'return' | 'dispose';

export function StageDialog({ asset, meta, mode, onClose, onDone }: {
  asset: LcAsset; meta: LcMeta; mode: StageMode; onClose: () => void; onDone: (msg: string) => void;
}) {
  const deps = useDepartments();
  const locs = useLocations();
  const current = meta.stages.find((s) => s.id === asset.stage);
  const choices: Stage[] = mode === 'assign' ? ['in_use'] : mode === 'dispose' ? ['disposed']
    : mode === 'return' ? (current?.next ?? []).filter((s) => s === 'in_stock' || s === 'in_maintenance')
    : [asset.stage, ...(current?.next ?? [])].filter((s) => s !== 'disposed');
  const [stage, setStage] = useState<Stage>(choices.includes('in_stock') && mode === 'return' ? 'in_stock' : choices[0] ?? asset.stage);
  const statuses = meta.stages.find((s) => s.id === stage)?.statuses ?? [];
  const [status, setStatus] = useState<string>(mode === 'assign' ? 'assigned' : mode === 'return' ? 'awaiting_deployment' : stage === asset.stage ? asset.status : statuses[0]?.id ?? '');
  const [reason, setReason] = useState('');
  const [person, setPerson] = useState(mode === 'assign' ? '' : asset.assignedTo ?? '');
  const [email, setEmail] = useState(mode === 'assign' ? '' : asset.assignedEmail ?? '');
  const [ret, setRet] = useState('');
  const [dep, setDep] = useState(asset.departmentId ?? '');
  const [loc, setLoc] = useState(asset.locationId ?? '');
  const [d, setD] = useState({ disposedOn: new Date().toISOString().slice(0, 10), vendor: '', value: '', san: '', sanBy: '', sanOn: '', cert: '' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pickStage = (s: Stage) => {
    setStage(s);
    const st = meta.stages.find((x) => x.id === s)?.statuses ?? [];
    setStatus(s === 'in_use' ? (person.trim() ? 'assigned' : 'in_use') : s === asset.stage ? asset.status : st[0]?.id ?? '');
  };
  const needsPerson = stage === 'in_use' && (status === 'assigned' || status === 'loaned');
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = {
        stage, status, reason: reason.trim() || null,
        assignedTo: needsPerson ? person.trim() || null : null, assignedEmail: needsPerson ? email.trim() || null : null,
        expectedReturn: status === 'loaned' ? day(ret) : null,
        departmentId: stage === 'in_use' && dep ? dep : null, locationId: loc && loc !== asset.locationId ? loc : null,
        ...(stage === 'disposed' ? {
          disposedOn: day(d.disposedOn), disposalVendor: d.vendor.trim() || null, disposalValue: num(d.value), sanitizationMethod: d.san || null,
          sanitizedBy: d.sanBy.trim() || null, sanitizedOn: day(d.sanOn), certificateRef: d.cert.trim() || null,
        } : {}),
      };
      const r = await api.post<{ message: string }>(mode === 'return' ? `/lifecycle/assets/${asset.id}/return` : `/lifecycle/assets/${asset.id}/stage`,
        mode === 'return' ? { stage, status, reason: body.reason, locationId: body.locationId } : body);
      onDone(`${asset.assetTag ?? asset.name ?? 'Asset'}: ${r.message}`);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  const title = mode === 'assign' ? 'Assign to a person' : mode === 'return' ? 'Return / check in' : mode === 'dispose' ? 'Dispose of the asset' : 'Change lifecycle stage';
  return (
    <Modal title={`${title} — ${asset.assetTag ?? asset.name ?? ''}`} wide={mode === 'dispose'} onClose={onClose} footer={<Footer busy={busy} onClose={onClose} label={mode === 'dispose' ? 'Dispose' : mode === 'assign' ? 'Assign' : mode === 'return' ? 'Check in' : 'Change'} danger={mode === 'dispose'} />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <p className="muted" style={{ margin: 0 }}>Now: <b>{stageLabel(meta, asset.stage)}</b>{asset.assignedTo ? <> · {asset.assignedTo}</> : null}</p>
        {choices.length === 0 && <Callout tone="warn" icon="info">{stageLabel(meta, asset.stage)} assets cannot change stage.</Callout>}
        <div className="grid-2 tight">
          {mode !== 'assign' && mode !== 'dispose' && <Field label="Stage">
            <select className="in" value={stage} onChange={(e) => pickStage(e.target.value as Stage)}>
              {choices.map((s) => <option key={s} value={s}>{stageLabel(meta, s)}{s === asset.stage ? ' (current)' : ''}</option>)}
            </select>
          </Field>}
          <Field label={stage === 'disposed' ? 'Disposal method' : 'Status'}>
            <select className="in" value={status} onChange={(e) => setStatus(e.target.value)}>
              {statuses.filter((s) => mode !== 'assign' || s.id !== 'in_use').map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
            </select>
          </Field>
        </div>
        {needsPerson && <div className="grid-2 tight">
          <Field label="Person"><LcPersonInput id="lc-person" value={person} onChange={setPerson} onPicked={(p) => { if (p.email && !email.trim()) setEmail(p.email); }} /></Field>
          <Field label="E-mail (optional)"><input className="in" type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={254} /></Field>
          {status === 'loaned' && <Field label="Return by"><input className="in" type="date" value={ret} onChange={(e) => setRet(e.target.value)} required /></Field>}
          <Field label="Department / unit">
            <DepartmentSelect value={dep} onChange={setDep} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd />
          </Field>
        </div>}
        {stage !== 'disposed' && <Field label="Location (optional)">
          <LocationSelect value={loc} onChange={setLoc} list={locs.data?.items ?? []} onAdded={locs.reload} canAdd />
        </Field>}
        {stage === 'disposed' && <>
          <Callout tone="warn" icon="alert">
            Disposal is final. Record how the data was removed (NIST SP 800-88) and keep the certificate of data destruction.
            The device leaves the inventory, stops counting for licensing and cannot change stage again.
          </Callout>
          <div className="grid-2 tight">
            <Field label="Disposed on"><input className="in" type="date" value={d.disposedOn} onChange={(e) => setD({ ...d, disposedOn: e.target.value })} required /></Field>
            <Field label="Disposal vendor (e.g. R2 / e-Stewards certified recycler)"><input className="in" value={d.vendor} onChange={(e) => setD({ ...d, vendor: e.target.value })} maxLength={200} /></Field>
            <Field label="Sale / recovery value"><input className="in" type="number" min={0} step="0.01" value={d.value} onChange={(e) => setD({ ...d, value: e.target.value })} /></Field>
            <Field label="Data sanitisation">
              <select className="in" value={d.san} onChange={(e) => setD({ ...d, san: e.target.value })} required>
                <option value="">— Choose —</option>
                {meta.sanitizationMethods.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}
              </select>
            </Field>
            {d.san && d.san !== 'not_required' && <>
              <Field label="Data removed by"><input className="in" value={d.sanBy} onChange={(e) => setD({ ...d, sanBy: e.target.value })} required maxLength={200} /></Field>
              <Field label="Data removed on"><input className="in" type="date" value={d.sanOn} onChange={(e) => setD({ ...d, sanOn: e.target.value })} required /></Field>
            </>}
            <Field label="Certificate / reference"><input className="in" value={d.cert} onChange={(e) => setD({ ...d, cert: e.target.value })} maxLength={200} placeholder="Certificate of destruction number" /></Field>
          </div>
        </>}
        <Field label={stage === 'retired' || status === 'lost' || status === 'stolen' ? 'Reason (required)' : 'Reason / note'}>
          <textarea className="in" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000}
            placeholder={stage === 'in_maintenance' ? 'Fault, vendor ticket / RMA number' : stage === 'missing' ? 'Where and when it was last seen; police or insurance reference' : ''} />
        </Field>
      </form>
    </Modal>
  );
}

// ---- details: tag, purchase, depreciation, lease, warranty -----------------------------------------------------------

export function DetailsDialog({ asset, meta, custom, onClose, onDone }: {
  asset: LcAsset; meta: LcMeta; custom?: Record<string, string | number | boolean | null> | null; onClose: () => void; onDone: (msg: string) => void;
}) {
  const [cv, setCv] = useState<Record<string, string>>(() => Object.fromEntries(meta.fields.map((x) => [x.key, customText(custom?.[x.key])])));
  const s = (v: string | number | null | undefined) => (v == null ? '' : String(v));
  const [f, setF] = useState({
    assetTag: s(asset.assetTag), acquisition: asset.acquisition, purchaseDate: s(asset.purchaseDate), purchaseCost: s(asset.purchaseCost),
    currency: s(asset.currency) || meta.currency, vendor: s(asset.vendor), invoiceNumber: s(asset.invoiceNumber), poNumber: s(asset.poNumber),
    custom: asset.depreciationCustom, depreciationMethod: s(asset.depreciationMethod), usefulLifeMonths: s(asset.usefulLifeMonths), salvageValue: s(asset.salvageValue),
    lessor: s(asset.lessor), leaseStart: s(asset.leaseStart), leaseEnd: s(asset.leaseEnd), leaseMonthlyCost: s(asset.leaseMonthlyCost),
    warrantyProvider: s(asset.warrantyProvider), warrantyType: s(asset.warrantyType), warrantyStart: s(asset.warrantyStart), warrantyEnd: s(asset.warrantyEnd),
    notes: s(asset.notes),
  });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const policy = meta.policies.find((p) => p.class === asset.class);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await api.put(`/lifecycle/assets/${asset.id}`, {
        assetTag: f.assetTag.trim() || null, acquisition: f.acquisition, purchaseDate: day(f.purchaseDate), inServiceDate: null, purchaseCost: num(f.purchaseCost),
        currency: f.currency.trim() || null, vendor: f.vendor.trim() || null, invoiceNumber: f.invoiceNumber.trim() || null, poNumber: f.poNumber.trim() || null,
        depreciationMethod: f.custom ? f.depreciationMethod || null : null, usefulLifeMonths: f.custom ? num(f.usefulLifeMonths) : null, salvageValue: f.custom ? num(f.salvageValue) : null,
        lessor: f.lessor.trim() || null, leaseStart: day(f.leaseStart), leaseEnd: day(f.leaseEnd), leaseMonthlyCost: num(f.leaseMonthlyCost),
        warrantyProvider: f.warrantyProvider.trim() || null, warrantyType: f.warrantyType.trim() || null, warrantyStart: day(f.warrantyStart), warrantyEnd: day(f.warrantyEnd),
        notes: f.notes.trim() || null, rowVersion: asset.rowVersion,
        custom: meta.fields.length ? Object.fromEntries(meta.fields.map((x) => [x.key, cv[x.key]?.trim() || null])) : null,
      });
      onDone('Lifecycle details saved.');
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Lifecycle details — ${asset.assetTag ?? asset.name ?? ''}`} wide onClose={onClose} footer={<Footer busy={busy} onClose={onClose} />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <h4 className="lc-h">Identification</h4>
        <div className="grid-2 tight">
          <Field label="Asset tag" hint={meta.tags.mode === 'manual' ? 'Your own tag (letters, digits and . _ / -).' : `Your own tag, or leave empty and use "Give the next tag" (format ${meta.tags.format}, e.g. ${meta.tags.example}; class prefix ${policy?.tagPrefix ?? 'AST'}).`}><input className="in mono" value={f.assetTag} onChange={set('assetTag')} maxLength={40} /></Field>
          <Field label="Acquisition">
            <select className="in" value={f.acquisition} onChange={set('acquisition')}>
              {meta.acquisitions.map((a) => <option key={a} value={a}>{a === 'byod' ? 'Personal device (BYOD)' : a[0]!.toUpperCase() + a.slice(1)}</option>)}
            </select>
          </Field>
        </div>
        {meta.fields.length > 0 && <><h4 className="lc-h">Custom fields</h4><CustomFieldInputs fields={meta.fields} values={cv} onChange={setCv} /></>}
        <h4 className="lc-h">Purchase</h4>
        <div className="grid-3 tight">
          <Field label="Purchase date"><input className="in" type="date" value={f.purchaseDate} onChange={set('purchaseDate')} /></Field>
          <Field label="Cost"><input className="in" type="number" min={0} step="0.01" value={f.purchaseCost} onChange={set('purchaseCost')} /></Field>
          <Field label="Currency"><input className="in mono" value={f.currency} onChange={set('currency')} maxLength={3} /></Field>
          <Field label="Vendor"><input className="in" value={f.vendor} onChange={set('vendor')} maxLength={200} /></Field>
          <Field label="Invoice"><input className="in" value={f.invoiceNumber} onChange={set('invoiceNumber')} maxLength={100} /></Field>
          <Field label="Purchase order"><input className="in" value={f.poNumber} onChange={set('poNumber')} maxLength={100} /></Field>
        </div>
        <label className="row-check"><input type="checkbox" checked={f.custom} onChange={(e) => setF({ ...f, custom: e.target.checked })} />
          Own depreciation for this asset (otherwise {policy?.label ?? 'class'} policy: {meta.depreciationMethods.find((m) => m.id === policy?.depreciationMethod)?.label ?? '—'}, {policy?.usefulLifeMonths ?? 0} months, {policy?.salvagePercent ?? 0}% salvage)</label>
        {f.custom && <div className="grid-3 tight">
          <Field label="Method">
            <select className="in" value={f.depreciationMethod} onChange={set('depreciationMethod')}>
              <option value="">Policy</option>
              {meta.depreciationMethods.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}
            </select>
          </Field>
          <Field label="Useful life (months)"><input className="in" type="number" min={0} max={600} value={f.usefulLifeMonths} onChange={set('usefulLifeMonths')} /></Field>
          <Field label="Salvage value"><input className="in" type="number" min={0} step="0.01" value={f.salvageValue} onChange={set('salvageValue')} /></Field>
        </div>}
        {(f.acquisition === 'lease' || f.acquisition === 'rental' || f.lessor) && <>
          <h4 className="lc-h">Lease / rental</h4>
          <div className="grid-3 tight">
            <Field label="Lessor"><input className="in" value={f.lessor} onChange={set('lessor')} maxLength={200} /></Field>
            <Field label="Starts"><input className="in" type="date" value={f.leaseStart} onChange={set('leaseStart')} /></Field>
            <Field label="Ends"><input className="in" type="date" value={f.leaseEnd} onChange={set('leaseEnd')} /></Field>
            <Field label="Cost per month"><input className="in" type="number" min={0} step="0.01" value={f.leaseMonthlyCost} onChange={set('leaseMonthlyCost')} /></Field>
          </div>
        </>}
        <h4 className="lc-h">Warranty</h4>
        <div className="grid-3 tight">
          <Field label="Provider"><input className="in" value={f.warrantyProvider} onChange={set('warrantyProvider')} maxLength={200} placeholder="Dell, Lenovo, HP…" /></Field>
          <Field label="Type"><input className="in" value={f.warrantyType} onChange={set('warrantyType')} maxLength={100} placeholder="Onsite NBD, ProSupport, carry-in…" /></Field>
          <span />
          <Field label="Starts"><input className="in" type="date" value={f.warrantyStart} onChange={set('warrantyStart')} /></Field>
          <Field label="Ends"><input className="in" type="date" value={f.warrantyEnd} onChange={set('warrantyEnd')} /></Field>
        </div>
        <Field label="Notes"><textarea className="in" rows={2} value={f.notes} onChange={set('notes')} maxLength={4000} /></Field>
      </form>
    </Modal>
  );
}

// ---- add to stock / on order ---------------------------------------------------------------------------------------

export function StockDialog({ meta, onClose, onDone }: { meta: LcMeta; onClose: () => void; onDone: (msg: string) => void }) {
  const deps = useDepartments();
  const locs = useLocations();
  const [stage, setStage] = useState<Stage>('in_stock');
  const [status, setStatus] = useState('new');
  const [type, setType] = useState('laptop');
  const [mf, setMf] = useState('');
  const [model, setModel] = useState('');
  const [serials, setSerials] = useState('');
  const [f, setF] = useState({ purchaseDate: '', purchaseCost: '', currency: meta.currency, vendor: '', invoiceNumber: '', poNumber: '', warrantyProvider: '', warrantyEnd: '', notes: '' });
  const [dep, setDep] = useState('');
  const [loc, setLoc] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [withTags, setWithTags] = useState(meta.tags.mode === 'manual');
  const list = withTags
    ? serials.split(/\n+/).map((x) => x.trim()).filter(Boolean).map((line) => { const [sn, tag] = line.split(/[\t,;]+|\s+/).map((v) => v.trim()); return { serial: sn ?? '', tag: tag || null }; })
    : serials.split(/[\n,;]+/).map((x) => x.trim()).filter(Boolean).map((sn) => ({ serial: sn, tag: null as string | null }));
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ message: string }>('/lifecycle/stock', {
        items: list.map((x) => ({ serial: x.serial, assetTag: x.tag, manufacturer: mf.trim() || null, model: model.trim() || null, deviceType: type || null, hostname: null })),
        stage, status, departmentId: dep || null, locationId: loc || null,
        purchaseDate: day(f.purchaseDate), purchaseCost: num(f.purchaseCost), currency: f.currency.trim() || null, vendor: f.vendor.trim() || null,
        invoiceNumber: f.invoiceNumber.trim() || null, poNumber: f.poNumber.trim() || null, warrantyProvider: f.warrantyProvider.trim() || null,
        warrantyEnd: day(f.warrantyEnd), notes: f.notes.trim() || null,
      });
      onDone(r.message);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  const stages = meta.stages.filter((s) => s.id === 'planned' || s.id === 'on_order' || s.id === 'in_stock');
  return (
    <Modal title="Add new IT assets (stock / on order)" wide onClose={onClose} footer={<Footer busy={busy} onClose={onClose} label={`Add ${list.length || ''} item(s)`} />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <small className="muted">For devices Zentriq has not seen yet (new purchases). When one is first seen by an agent or a scan with the same serial number, its inventory is recorded on this item and it moves to <b>In use</b> by itself.</small>
        <div className="grid-3 tight">
          <Field label="Stage">
            <select className="in" value={stage} onChange={(e) => { const s = e.target.value as Stage; setStage(s); setStatus(meta.stages.find((x) => x.id === s)?.statuses[0]?.id ?? ''); }}>
              {stages.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
            </select>
          </Field>
          <Field label="Status">
            <select className="in" value={status} onChange={(e) => setStatus(e.target.value)}>
              {(stages.find((s) => s.id === stage)?.statuses ?? []).map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
            </select>
          </Field>
          <Field label="Device type"><DeviceTypeSelect value={type} onChange={setType} /></Field>
          <Field label="Manufacturer"><input className="in" value={mf} onChange={(e) => setMf(e.target.value)} maxLength={200} /></Field>
          <Field label="Model"><input className="in" value={model} onChange={(e) => setModel(e.target.value)} maxLength={200} /></Field>
        </div>
        <Field label={withTags ? `Serial number and asset tag, one item per line (${list.length})` : `Serial numbers (${list.length})`}
          hint={withTags ? 'Serial, then your tag: "5CG4410XYZ ACME-LAP-0101" (space, tab or comma between). Lines without a tag get one by the tag settings.'
            : `One per line (or separated by commas). A barcode scanner can type them in.${meta.tags.mode === 'auto' ? ` Tags are given automatically (${meta.tags.example} …).` : ''}`}>
          <textarea className="in mono" rows={5} value={serials} onChange={(e) => setSerials(e.target.value)} required placeholder={withTags ? '5CG4410XYZ  ACME-LAP-0101\n5CG4410XZA  ACME-LAP-0102' : ''} />
        </Field>
        <label className="row-check"><input type="checkbox" checked={withTags} onChange={(e) => setWithTags(e.target.checked)} /> <span>I'll enter our own asset tag after each serial number</span></label>
        <div className="grid-3 tight">
          <Field label="Purchase date"><input className="in" type="date" value={f.purchaseDate} onChange={set('purchaseDate')} /></Field>
          <Field label="Cost per item"><input className="in" type="number" min={0} step="0.01" value={f.purchaseCost} onChange={set('purchaseCost')} /></Field>
          <Field label="Currency"><input className="in mono" value={f.currency} onChange={set('currency')} maxLength={3} /></Field>
          <Field label="Vendor"><input className="in" value={f.vendor} onChange={set('vendor')} maxLength={200} /></Field>
          <Field label="Invoice"><input className="in" value={f.invoiceNumber} onChange={set('invoiceNumber')} maxLength={100} /></Field>
          <Field label="Purchase order"><input className="in" value={f.poNumber} onChange={set('poNumber')} maxLength={100} /></Field>
          <Field label="Warranty provider"><input className="in" value={f.warrantyProvider} onChange={set('warrantyProvider')} maxLength={200} /></Field>
          <Field label="Warranty ends"><input className="in" type="date" value={f.warrantyEnd} onChange={set('warrantyEnd')} /></Field>
        </div>
        <div className="grid-2 tight">
          <Field label="Department / unit"><DepartmentSelect value={dep} onChange={setDep} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd /></Field>
          <Field label="Location (store)"><LocationSelect value={loc} onChange={setLoc} list={locs.data?.items ?? []} onAdded={locs.reload} canAdd /></Field>
        </div>
      </form>
    </Modal>
  );
}

// ---- bulk stage change ---------------------------------------------------------------------------------------------

export function BulkStageDialog({ ids, meta, onClose, onDone }: { ids: string[]; meta: LcMeta; onClose: () => void; onDone: (msg: string) => void }) {
  const allowed: Stage[] = ['in_stock', 'in_use', 'in_maintenance', 'missing', 'retired'];
  const [stage, setStage] = useState<Stage>('retired');
  const [status, setStatus] = useState('end_of_life');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ changed: number; errors: { assetId: string; message: string }[] }>('/lifecycle/stage', { assetIds: ids, stage, status, reason: reason.trim() || null });
      onDone(`${r.changed} asset(s) changed to ${stageLabel(meta, stage)}.${r.errors.length ? ` ${r.errors.length} not changed: ${r.errors.slice(0, 3).map((x) => x.message).join(' · ')}` : ''}`);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Change stage of ${ids.length} asset(s)`} onClose={onClose} footer={<Footer busy={busy} onClose={onClose} label="Change" />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <small className="muted">Assets whose current stage cannot move to the chosen one are left as they are and listed. Assign and dispose one asset at a time.</small>
        <div className="grid-2 tight">
          <Field label="Stage">
            <select className="in" value={stage} onChange={(e) => { const s = e.target.value as Stage; setStage(s); setStatus(s === 'in_use' ? 'in_use' : meta.stages.find((x) => x.id === s)?.statuses[0]?.id ?? ''); }}>
              {allowed.map((s) => <option key={s} value={s}>{stageLabel(meta, s)}</option>)}
            </select>
          </Field>
          <Field label="Status">
            <select className="in" value={status} onChange={(e) => setStatus(e.target.value)}>
              {(meta.stages.find((s) => s.id === stage)?.statuses ?? []).filter((s) => s.id !== 'assigned' && s.id !== 'loaned').map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
            </select>
          </Field>
        </div>
        <Field label={stage === 'retired' ? 'Reason (required)' : 'Reason / note'}><textarea className="in" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} /></Field>
      </form>
    </Modal>
  );
}

// ---- contracts -----------------------------------------------------------------------------------------------------

export function ContractDialog({ contract, meta, onClose, onDone }: { contract: LcContract | null; meta: LcMeta; onClose: () => void; onDone: (msg: string, id: string) => void }) {
  const s = (v: string | number | null | undefined) => (v == null ? '' : String(v));
  const [f, setF] = useState({
    name: s(contract?.name), kind: contract?.kind ?? 'amc', vendor: s(contract?.vendor), reference: s(contract?.reference), startDate: s(contract?.startDate),
    endDate: s(contract?.endDate), cost: s(contract?.cost), currency: s(contract?.currency) || meta.currency, notifyDays: s(contract?.notifyDays ?? 90), notes: s(contract?.notes),
  });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = { name: f.name, kind: f.kind, vendor: f.vendor.trim() || null, reference: f.reference.trim() || null, startDate: day(f.startDate), endDate: day(f.endDate),
        cost: num(f.cost), currency: f.currency.trim() || null, notifyDays: num(f.notifyDays) ?? 90, notes: f.notes.trim() || null };
      const r = contract ? await api.put<{ id: string }>(`/lifecycle/contracts/${contract.id}`, body) : await api.post<{ id: string }>('/lifecycle/contracts', body);
      onDone(contract ? 'Contract saved.' : 'Contract added. Add the assets it covers from the list (select › Add to contract).', r.id);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={contract ? `Contract — ${contract.name}` : 'New contract'} onClose={onClose} footer={<Footer busy={busy} onClose={onClose} />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="grid-2 tight">
          <Field label="Name"><input className="in" value={f.name} onChange={set('name')} required maxLength={200} placeholder="Dell ProSupport 2026 — laptops" /></Field>
          <Field label="Type">
            <select className="in" value={f.kind} onChange={set('kind')}>{meta.contractKinds.map((k) => <option key={k} value={k}>{CONTRACT_KIND[k] ?? k}</option>)}</select>
          </Field>
          <Field label="Vendor"><input className="in" value={f.vendor} onChange={set('vendor')} maxLength={200} /></Field>
          <Field label="Contract number / reference"><input className="in" value={f.reference} onChange={set('reference')} maxLength={100} /></Field>
          <Field label="Starts"><input className="in" type="date" value={f.startDate} onChange={set('startDate')} /></Field>
          <Field label="Ends"><input className="in" type="date" value={f.endDate} onChange={set('endDate')} /></Field>
          <Field label="Cost"><input className="in" type="number" min={0} step="0.01" value={f.cost} onChange={set('cost')} /></Field>
          <Field label="Currency"><input className="in mono" value={f.currency} onChange={set('currency')} maxLength={3} /></Field>
          <Field label="Remind before the end (days)" hint="An alert (Integrations) is raised then and when it ends."><input className="in" type="number" min={0} max={365} value={f.notifyDays} onChange={set('notifyDays')} /></Field>
        </div>
        <Field label="Notes"><textarea className="in" rows={2} value={f.notes} onChange={set('notes')} maxLength={4000} /></Field>
      </form>
    </Modal>
  );
}

export function AddToContractDialog({ ids, onClose, onDone }: { ids: string[]; onClose: () => void; onDone: (msg: string) => void }) {
  const contracts = useApi(() => api.get<LcContract[]>('/lifecycle/contracts'), []);
  const [id, setId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ added: number }>(`/lifecycle/contracts/${id}/assets`, { add: ids });
      onDone(`${r.added} asset(s) added to the contract.`);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Add ${ids.length} asset(s) to a contract`} onClose={onClose} footer={<Footer busy={busy} onClose={onClose} label="Add" />}>
      <form id="lc-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {contracts.data && contracts.data.length === 0 && <Callout tone="info" icon="info">Create a contract first (Contracts tab).</Callout>}
        <Field label="Contract">
          <select className="in" value={id} onChange={(e) => setId(e.target.value)} required>
            <option value="">— Choose —</option>
            {(contracts.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.name} · {CONTRACT_KIND[c.kind] ?? c.kind}{c.endDate ? ` · until ${c.endDate}` : ''}</option>)}
          </select>
        </Field>
      </form>
    </Modal>
  );
}
