import { useState } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { Check, Combo, Field, Fields, Section } from '../../components/Form';
import { Modal } from '../../components/Modal';
import { Callout, DataTable } from '../../components/ui';
import { errText } from '../deployment/types';
import { useDepartments } from '../departments/Departments';
import { baseName, METRIC_LABEL, TYPE_LABEL, type Connection, type FoundSoftware, type License, type LicenseType, type Metric, type Product } from './types';

const lines = (s: string) => s.split(/[\n;]/).map((x) => x.trim()).filter(Boolean);
const num = (s: string) => (s.trim() === '' ? null : Number(s));

// ---- product ---------------------------------------------------------------------------------------------------------

export function ProductDialog({ product, isNew, onClose, onSaved }: { product: Product | null; isNew?: boolean; onClose: () => void; onSaved: (id: string) => void }) {
  const editing = product && !isNew ? product : null;
  const [f, setF] = useState({
    name: product?.name ?? '', publisher: product?.publisher ?? '', category: product?.category ?? 'Other', kind: product?.kind ?? 'installed',
    metric: (product?.metric ?? 'device') as Metric, needsLicense: product?.needsLicense ?? true,
    names: (product?.namePatterns ?? []).join('\n'), excludes: (product?.excludePatterns ?? []).join('\n'), publishers: (product?.publisherPatterns ?? []).join('\n'),
    skus: (product?.cloudSkus ?? []).join('\n'), webAppId: product?.webAppId ?? '', notes: product?.notes ?? '', exes: (product?.exePatterns ?? []).join('\n'),
  });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ name: string; publisher: string | null; devices: number }[] | null>(null);
  const apps = useApi(() => api.get<{ id: string; name: string }[]>('/webapps').catch(() => []), []);
  // Software and publishers found in the inventory, to pick from (anything else is typed in).
  const found = useApi(() => api.get<FoundSoftware>('/sam/inventory/software').catch(() => ({ software: [], publishers: [] }) as FoundSoftware), []);
  const softwareOptions = (found.data?.software ?? []).map((x) => ({ value: x.name, sub: x.publisher, count: x.devices }));
  const publisherOptions = (found.data?.publishers ?? []).map((x) => ({ value: x.name, count: x.devices }));
  function pickSoftware(name: string, publisher: string | null | undefined) {
    const base = baseName(name);
    setF((p) => ({ ...p, name: base, publisher: p.publisher || publisher || '', names: p.kind === 'installed' && !p.names.trim() ? `${base}*` : p.names }));
    setPreview(null);
  }
  const body = () => ({
    name: f.name, publisher: f.publisher || null, category: f.category || 'Other', kind: f.kind, metric: f.metric, needsLicense: f.needsLicense,
    namePatterns: lines(f.names), excludePatterns: lines(f.excludes), publisherPatterns: lines(f.publishers), cloudSkus: lines(f.skus),
    webAppId: f.webAppId || null, notes: f.notes || null, exePatterns: f.kind === 'installed' ? lines(f.exes) : [],
  });
  async function test() {
    setErr(null);
    try { setPreview(await api.post('/sam/products/preview', body())); } catch (e) { setErr(errText(e)); }
  }
  async function save() {
    setBusy(true); setErr(null);
    try {
      if (editing) { await api.put(`/sam/products/${editing.id}`, body()); onSaved(editing.id); }
      else { const r = await api.post<{ id: string }>('/sam/products', body()); onSaved(r.id); }
    } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={editing ? `Edit ${editing.name}` : 'Add Software Product'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !f.name.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Product">
          <Fields>
            <Field label="Name" required hint="Pick software found on your devices, or type a name that is not in the list.">
              <Combo value={f.name} onChange={(v) => setF({ ...f, name: v })} onPick={(o) => pickSoftware(o.value, o.sub)} options={softwareOptions} loading={found.loading}
                maxLength={160} autoFocus placeholder="e.g. Microsoft Visio" ariaLabel="Name" />
            </Field>
            <Field label="Publisher" optional>
              <Combo value={f.publisher} onChange={(v) => setF({ ...f, publisher: v })} options={publisherOptions} loading={found.loading} placeholder="e.g. Microsoft" ariaLabel="Publisher" />
            </Field>
            <Field label="Kind">
              <select className="in" value={f.kind} onChange={(e) => setF({ ...f, kind: e.target.value as 'installed' | 'cloud' })}>
                <option value="installed">Installed software (found in the inventory)</option>
                <option value="cloud">Cloud subscription (Microsoft 365, web app accounts)</option>
              </select>
            </Field>
            <Field label="Counted">
              <select className="in" value={f.metric} onChange={(e) => setF({ ...f, metric: e.target.value as Metric })}>
                {(Object.keys(METRIC_LABEL) as Metric[]).map((m) => <option key={m} value={m}>{METRIC_LABEL[m]}</option>)}
              </select>
            </Field>
            <Field label="Category" optional><input className="in" value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })} placeholder="e.g. Office" /></Field>
          </Fields>
          <Check checked={!f.needsLicense} onChange={(v) => setF({ ...f, needsLicense: !v })} hint="Installations are listed but never counted as unlicensed.">Free / open source (no licence needed)</Check>
        </Section>
        {f.kind === 'installed' ? <Section title="How It Is Found" sub="Names as they appear in the software inventory; * and ? are wildcards, one per line.">
          <Fields>
            <Field label="Installed as"><textarea className="in mono" rows={4} value={f.names} onChange={(e) => setF({ ...f, names: e.target.value })} placeholder={'Microsoft Visio*\nMicrosoft Office Visio*'} /></Field>
            <Field label="But not" optional><textarea className="in mono" rows={4} value={f.excludes} onChange={(e) => setF({ ...f, excludes: e.target.value })} placeholder="*Viewer*" /></Field>
            <Field label="From publisher" optional><textarea className="in mono" rows={2} value={f.publishers} onChange={(e) => setF({ ...f, publishers: e.target.value })} placeholder="Microsoft*" /></Field>
            <Field label="Used when running" optional hint="Program files that count as using it (software metering), one per line."><textarea className="in mono" rows={2} value={f.exes} onChange={(e) => setF({ ...f, exes: e.target.value })} placeholder={'visio.exe'} /></Field>
            <div className="ff"><span className="ff-l">&nbsp;</span><div><button type="button" className="btn" onClick={() => void test()}>Test Against Inventory</button></div></div>
          </Fields>
        </Section> : <Section title="How It Is Counted">
          <Fields>
            <Field label="Microsoft 365 SKUs" hint="One per line."><textarea className="in mono" rows={3} value={f.skus} onChange={(e) => setF({ ...f, skus: e.target.value })} placeholder={'SPE_E3\nVISIOCLIENT'} /></Field>
            <Field label="Web app" hint="Its active accounts are counted.">
              <select className="in" value={f.webAppId} onChange={(e) => setF({ ...f, webAppId: e.target.value })}>
                <option value="">—</option>
                {(apps.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
              </select>
            </Field>
          </Fields>
        </Section>}
        {preview && (
          <div>
            <b>{preview.reduce((a, p) => a + p.devices, 0).toLocaleString()} installation(s) match</b>
            <DataTable rows={preview.map((p) => ({ ...p, id: p.name }))} empty="Nothing installed matches." columns={[
              { key: 'n', label: 'Installed As', className: 'wrap', render: (p) => p.name },
              { key: 'p', label: 'Publisher', className: 'wrap', render: (p) => p.publisher ?? '—' },
              { key: 'd', label: 'Devices', render: (p) => p.devices },
            ]} />
          </div>
        )}
        <Section title="Notes"><textarea className="in" rows={2} aria-label="Notes" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} /></Section>
      </div>
    </Modal>
  );
}

// ---- licence ---------------------------------------------------------------------------------------------------------

export function LicenseDialog({ license, productId, products, onClose, onSaved }: {
  license: License | null; productId?: string; products: { id: string; name: string }[]; onClose: () => void; onSaved: () => void;
}) {
  const deps = useDepartments();
  const [f, setF] = useState({
    productId: license?.productId ?? productId ?? '', name: license?.name ?? '', licenseType: (license?.licenseType ?? 'perpetual') as LicenseType,
    metric: license && !license.metricFromProduct ? license.metric : '', quantity: license?.quantity?.toString() ?? '', unlimited: !!license && license.quantity == null && !license.cloudSku,
    edition: license?.edition ?? '', version: license?.version ?? '', downgradeRights: license?.downgradeRights ?? false, vendor: license?.vendor ?? '', reseller: license?.reseller ?? '',
    poNumber: license?.poNumber ?? '', invoiceNumber: license?.invoiceNumber ?? '', contractNumber: license?.contractNumber ?? '', purchaseDate: license?.purchaseDate ?? '',
    startDate: license?.startDate ?? '', endDate: license?.endDate ?? '', maintenanceEnd: license?.maintenanceEnd ?? '', autoRenew: license?.autoRenew ?? false,
    billing: license?.billing ?? '', unitCost: license?.unitCost?.toString() ?? '', totalCost: license?.totalCost?.toString() ?? '', currency: license?.currency ?? 'USD',
    notifyDays: (license?.notifyDays ?? 30).toString(), licenseKey: '', clearKey: false, departmentId: license?.departmentId ?? '', owner: license?.owner ?? '',
    status: license?.status === 'retired' ? 'retired' : 'active', notes: license?.notes ?? '',
  });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  // Versions installed of the chosen product and publishers found in the inventory, to pick from.
  const versions = useApi<{ version: string; devices: number }[]>(() => (f.productId ? api.get<{ version: string; devices: number }[]>('/sam/inventory/versions', { productId: f.productId }).catch(() => []) : Promise.resolve([])), [f.productId]);
  const found = useApi(() => api.get<FoundSoftware>('/sam/inventory/software').catch(() => ({ software: [], publishers: [] }) as FoundSoftware), []);
  const versionOptions = (versions.data ?? []).map((v) => ({ value: v.version, count: v.devices }));
  const vendorOptions = (found.data?.publishers ?? []).map((x) => ({ value: x.name, count: x.devices }));
  const sub = f.licenseType === 'subscription' || f.licenseType === 'trial';
  const free = f.licenseType === 'freeware' || f.licenseType === 'open_source';
  const set = (k: keyof typeof f, v: string | boolean) => setF({ ...f, [k]: v });
  async function save() {
    setBusy(true); setErr(null);
    const body = {
      productId: f.productId || null, name: f.name, licenseType: f.licenseType, metric: f.metric || null, quantity: f.unlimited || free ? null : num(f.quantity), unlimited: f.unlimited || free,
      edition: f.edition || null, version: f.version || null, downgradeRights: f.downgradeRights, vendor: f.vendor || null, reseller: f.reseller || null,
      poNumber: f.poNumber || null, invoiceNumber: f.invoiceNumber || null, contractNumber: f.contractNumber || null, purchaseDate: f.purchaseDate || null,
      startDate: f.startDate || null, endDate: f.endDate || null, maintenanceEnd: f.maintenanceEnd || null, autoRenew: f.autoRenew, billing: f.billing || null,
      unitCost: num(f.unitCost), totalCost: num(f.totalCost), currency: f.currency || 'USD', notifyDays: num(f.notifyDays) ?? 30,
      licenseKey: f.licenseKey || null, clearKey: f.clearKey, departmentId: f.departmentId || null, owner: f.owner || null, status: f.status, notes: f.notes || null,
    };
    try {
      if (license) await api.put(`/sam/licenses/${license.id}`, body); else await api.post('/sam/licenses', body);
      onSaved();
    } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={license ? `Edit ${license.name}` : 'Add Licence'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !f.name.trim() || !f.productId} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Licence">
          <Fields>
            <Field label="Software product" required>
              <select className="in" value={f.productId} onChange={(e) => { const p = products.find((x) => x.id === e.target.value); setF({ ...f, productId: e.target.value, name: f.name || p?.name || '' }); }} disabled={!!productId && !license}>
                <option value="">— Choose —</option>
                {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </Field>
            <Field label="Licence name" required><input className="in" value={f.name} onChange={(e) => set('name', e.target.value)} maxLength={200} placeholder="e.g. Visio Plan 2 · 2026 renewal" /></Field>
            <Field label="Type">
              <select className="in" value={f.licenseType} onChange={(e) => set('licenseType', e.target.value)}>
                {(Object.keys(TYPE_LABEL) as LicenseType[]).map((t) => <option key={t} value={t}>{TYPE_LABEL[t]}</option>)}
              </select>
            </Field>
            <Field label="Counted">
              <select className="in" value={f.metric} onChange={(e) => set('metric', e.target.value)}>
                <option value="">As the product</option>
                {(Object.keys(METRIC_LABEL) as Metric[]).map((m) => <option key={m} value={m}>{METRIC_LABEL[m]}</option>)}
              </select>
            </Field>
            {!free && <Field label="Quantity" hint={license?.cloudSku ? 'Taken from Microsoft 365.' : undefined}>
              <input className="in" type="number" min={0} value={f.quantity} disabled={f.unlimited || !!license?.cloudSku} onChange={(e) => set('quantity', e.target.value)} placeholder={f.unlimited ? 'Unlimited' : 'e.g. 50'} />
            </Field>}
            {!free && <div className="ff"><span className="ff-l">&nbsp;</span><Check checked={f.unlimited} onChange={(v) => set('unlimited', v)}>Unlimited (site licence)</Check></div>}
            <Field label="Edition" optional><input className="in" value={f.edition} onChange={(e) => set('edition', e.target.value)} placeholder="e.g. Professional" /></Field>
            <Field label="Version" optional hint={versionOptions.length > 0 ? 'Versions installed on your devices, or type another.' : undefined}>
              <Combo value={f.version} onChange={(v) => set('version', v)} options={versionOptions} loading={versions.loading} placeholder="e.g. 2024" ariaLabel="Version" />
            </Field>
          </Fields>
          <Check checked={f.downgradeRights} onChange={(v) => set('downgradeRights', v)} hint="Older versions installed on devices count against this licence.">Downgrade rights</Check>
          {license && <Fields><Field label="Status"><select className="in" value={f.status} onChange={(e) => set('status', e.target.value)}><option value="active">Active</option><option value="retired">Retired</option></select></Field></Fields>}
        </Section>
        <Section title="Term and Dates">
          <Fields cols={3}>
            <Field label="Start date" optional><input className="in" type="date" value={f.startDate} onChange={(e) => set('startDate', e.target.value)} /></Field>
            <Field label={sub ? 'Ends / renews on' : 'Term ends'} optional={!sub}><input className="in" type="date" value={f.endDate} onChange={(e) => set('endDate', e.target.value)} /></Field>
            <Field label="Warn before the end" hint="Days."><input className="in" type="number" min={0} max={365} value={f.notifyDays} onChange={(e) => set('notifyDays', e.target.value)} /></Field>
            {!sub && !free && <Field label="Support ends" optional hint="Maintenance / software assurance."><input className="in" type="date" value={f.maintenanceEnd} onChange={(e) => set('maintenanceEnd', e.target.value)} /></Field>}
            {sub && <Field label="Billing">
              <select className="in" value={f.billing} onChange={(e) => set('billing', e.target.value)}>
                <option value="">—</option><option value="monthly">Monthly</option><option value="quarterly">Quarterly</option><option value="annual">Annual</option><option value="multi_year">Multi-Year</option>
              </select>
            </Field>}
          </Fields>
          {sub && <Check checked={f.autoRenew} onChange={(v) => set('autoRenew', v)}>Renews automatically</Check>}
        </Section>
        <Section title="Purchase">
          <Fields cols={3}>
            <Field label="Purchase date" optional><input className="in" type="date" value={f.purchaseDate} onChange={(e) => set('purchaseDate', e.target.value)} /></Field>
            <Field label="Vendor" optional><Combo value={f.vendor} onChange={(v) => set('vendor', v)} options={vendorOptions} loading={found.loading} placeholder="e.g. Microsoft" ariaLabel="Vendor" /></Field>
            <Field label="Reseller" optional><input className="in" value={f.reseller} onChange={(e) => set('reseller', e.target.value)} /></Field>
            <Field label="PO number" optional><input className="in" value={f.poNumber} onChange={(e) => set('poNumber', e.target.value)} /></Field>
            <Field label="Invoice number" optional><input className="in" value={f.invoiceNumber} onChange={(e) => set('invoiceNumber', e.target.value)} /></Field>
            <Field label="Contract / agreement" optional><input className="in" value={f.contractNumber} onChange={(e) => set('contractNumber', e.target.value)} /></Field>
            <Field label="Unit cost" optional><input className="in" type="number" min={0} step="0.01" value={f.unitCost} onChange={(e) => set('unitCost', e.target.value)} placeholder="0.00" /></Field>
            <Field label="Total cost" optional hint="Empty: unit cost × quantity."><input className="in" type="number" min={0} step="0.01" value={f.totalCost} onChange={(e) => set('totalCost', e.target.value)} placeholder="0.00" /></Field>
            <Field label="Currency"><input className="in" value={f.currency} maxLength={3} onChange={(e) => set('currency', e.target.value.toUpperCase())} /></Field>
          </Fields>
        </Section>
        <Section title="Ownership and Key">
          <Fields>
            <Field label="Department">
              <select className="in" value={f.departmentId} onChange={(e) => set('departmentId', e.target.value)}>
                <option value="">— Not assigned —</option>
                {(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </Field>
            <Field label="Owner" optional><input className="in" value={f.owner} onChange={(e) => set('owner', e.target.value)} placeholder="Who manages it" /></Field>
            <Field label="Licence key" optional span hint={license?.hasKey ? `Stored (${license.keyHint}) — enter a new one to replace it.` : 'Stored encrypted; shown only to people with the licence permission.'}>
              <input className="in mono" value={f.licenseKey} onChange={(e) => set('licenseKey', e.target.value)} autoComplete="off" placeholder="XXXXX-XXXXX-XXXXX-XXXXX-XXXXX" />
            </Field>
          </Fields>
          {license?.hasKey && <Check checked={f.clearKey} onChange={(v) => set('clearKey', v)}>Remove the stored key</Check>}
          <Field label="Notes" optional><textarea className="in" rows={2} value={f.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
        </Section>
      </div>
    </Modal>
  );
}

export function RenewDialog({ license, onClose, onSaved }: { license: License; onClose: () => void; onSaved: () => void }) {
  const next = (() => {
    if (!license.endDate) return '';
    const d = new Date(license.endDate + 'T00:00:00');
    d.setMonth(d.getMonth() + (license.billing === 'monthly' ? 1 : license.billing === 'quarterly' ? 3 : 12));
    return d.toISOString().slice(0, 10);
  })();
  const [endDate, setEnd] = useState(next);
  const [quantity, setQty] = useState(license.quantity?.toString() ?? '');
  const [cost, setCost] = useState(license.totalCost?.toString() ?? '');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    try { await api.post(`/sam/licenses/${license.id}/renew`, { endDate, quantity: num(quantity), totalCost: num(cost), note: note || null }); onSaved(); }
    catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Renew ${license.name}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !endDate} onClick={() => void save()}>{busy ? 'Saving…' : 'Renew'}</button></>}>
      <div className="form-v">
        <label>New end date<input className="in" type="date" value={endDate} onChange={(e) => setEnd(e.target.value)} /></label>
        {!license.cloudSku && <label>Quantity<input className="in" type="number" min={0} value={quantity} onChange={(e) => setQty(e.target.value)} /></label>}
        <label>Total cost<input className="in" type="number" min={0} step="0.01" value={cost} onChange={(e) => setCost(e.target.value)} /></label>
        <label>Note (PO, invoice…)<input className="in" value={note} onChange={(e) => setNote(e.target.value)} /></label>
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
      </div>
    </Modal>
  );
}

export function AllocateDialog({ license, onClose, onSaved }: { license: License; onClose: () => void; onSaved: () => void }) {
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  const [users, setUsers] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const assets = useApi(() => (search ? api.get<{ items: { id: string; hostname: string | null; ip: string | null }[] }>('/assets', { q: search, pageSize: 50 }) : Promise.resolve({ items: [] })), [search]);
  async function save() {
    setBusy(true); setErr(null);
    try { await api.post(`/sam/licenses/${license.id}/allocations`, { assetIds: picked, users: lines(users) }); onSaved(); }
    catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={`Allocate ${license.name}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || (picked.length === 0 && !users.trim())} onClick={() => void save()}>{busy ? 'Saving…' : 'Allocate'}</button></>}>
      <div className="grid-2">
        <div className="form-v">
          <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q.trim()); }} style={{ margin: 0 }}>
            <input className="in grow" placeholder="Find devices by name or IP…" value={q} onChange={(e) => setQ(e.target.value)} />
            <button className="btn" type="submit">Search</button>
          </form>
          <div style={{ maxHeight: 260, overflow: 'auto' }}>
            {(assets.data?.items ?? []).map((a) => (
              <label key={a.id} className="row-check"><input type="checkbox" checked={picked.includes(a.id)} onChange={() => setPicked((p) => (p.includes(a.id) ? p.filter((x) => x !== a.id) : [...p, a.id]))} /> {a.hostname ?? a.ip ?? a.id}</label>
            ))}
          </div>
          {picked.length > 0 && <small>{picked.length} device(s) chosen</small>}
        </div>
        <label className="form-v">Users (one per line: e-mail or DOMAIN\user)
          <textarea className="in mono" rows={10} value={users} onChange={(e) => setUsers(e.target.value)} />
        </label>
      </div>
      {err && <Callout tone="crit" icon="alert">{err}</Callout>}
    </Modal>
  );
}

// ---- Microsoft 365 connection ----------------------------------------------------------------------------------------

export function ConnectionDialog({ connection, onClose, onSaved }: { connection: Connection | null; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ name: connection?.name ?? 'Microsoft 365', tenantId: connection?.tenantId ?? '', clientId: connection?.clientId ?? '', clientSecret: '',
    enabled: connection?.enabled ?? true, syncApps: connection?.syncApps ?? true });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    try {
      if (connection) await api.put(`/sam/cloud/connections/${connection.id}`, { ...f, clientSecret: f.clientSecret || null });
      else await api.post('/sam/cloud/connections', f);
      onSaved();
    } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={connection ? `Edit ${connection.name}` : 'Connect Microsoft 365'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save and Sync'}</button></>}>
      <div className="grid-2">
        <div className="form-v">
          <label>Name<input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></label>
          <label>Tenant ID<input className="in mono" value={f.tenantId} onChange={(e) => setF({ ...f, tenantId: e.target.value })} placeholder="00000000-0000-0000-0000-000000000000" /></label>
          <label>Application (client) ID<input className="in mono" value={f.clientId} onChange={(e) => setF({ ...f, clientId: e.target.value })} /></label>
          <label>Client secret value{connection?.hasSecret && <small className="faint">stored — leave empty to keep it</small>}
            <input className="in mono" type="password" autoComplete="new-password" value={f.clientSecret} onChange={(e) => setF({ ...f, clientSecret: e.target.value })} /></label>
          <label className="row-check"><input type="checkbox" checked={f.syncApps} onChange={(e) => setF({ ...f, syncApps: e.target.checked })} /> Read Single Sign-On Apps Into Web Apps &amp; Accounts</label>
          <label className="row-check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Sync Every 6 Hours</label>
        </div>
        <div>
          <b>App registration (Microsoft Entra admin center)</b>
          <ol style={{ paddingLeft: 18, fontSize: 13, lineHeight: 1.55 }}>
            <li>App registrations › New registration (single tenant).</li>
            <li>API permissions › Microsoft Graph › <b>Application</b>: Organization.Read.All, User.Read.All, Application.Read.All, GroupMember.Read.All; optional AuditLog.Read.All (last sign-in, needs Entra ID P1).</li>
            <li>Grant admin consent.</li>
            <li>Certificates &amp; secrets › New client secret: copy its <b>Value</b>.</li>
          </ol>
          <p className="faint" style={{ fontSize: 12.5 }}>Read only: Zentriq never changes the tenant. The secret is stored encrypted on the Zentriq server.</p>
        </div>
      </div>
      {err && <Callout tone="crit" icon="alert">{err}</Callout>}
    </Modal>
  );
}
