import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { Asset } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Callout, fmt, Pill } from '../../components/ui';
import { DepartmentSelect, useDepartments } from '../departments/Departments';
import { DEVICE_TYPES, DEVICE_TYPE_GROUPS, deviceTypeLabel } from './deviceTypes';

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : b?.title ?? (e instanceof Error ? e.message : String(e));
};

/** Device type dropdown grouped by Computers / Network / Printing / Other devices. */
export function DeviceTypeSelect({ value, onChange, automatic, id, ariaLabel = 'Device type', emptyLabel = '— Choose —' }: {
  value: string; onChange: (v: string) => void; automatic?: string | null; id?: string; ariaLabel?: string; emptyLabel?: string;
}) {
  return (
    <select id={id} className="in" value={value} onChange={(e) => onChange(e.target.value)} aria-label={ariaLabel}>
      {automatic !== undefined && <option value="">Automatic{automatic ? ` (detected: ${deviceTypeLabel(automatic) ?? fmt.label(automatic)})` : ''}</option>}
      {automatic === undefined && <option value="">{emptyLabel}</option>}
      {DEVICE_TYPE_GROUPS.map((g) => (
        <optgroup key={g} label={g}>
          {DEVICE_TYPES.filter((t) => t.group === g).map((t) => <option key={t.key} value={t.key}>{t.label}</option>)}
        </optgroup>
      ))}
    </select>
  );
}

/** Device page: the device type, detected automatically or chosen by an administrator. */
export function DeviceTypeField({ asset, canEdit, onSaved }: { asset: Asset; canEdit: boolean; onSaved: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const label = deviceTypeLabel(asset.deviceType) ?? (asset.chassis ? fmt.label(asset.chassis) : fmt.label(asset.category));
  if (!canEdit) return <>{label}{asset.deviceTypeManual && <span className="faint"> (set manually)</span>}</>;
  async function save(v: string) {
    setBusy(true); setError(null);
    try { await api.put(`/assets/${asset.id}/device-type`, { deviceType: v || null }); onSaved(); }
    catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <>
      <DeviceTypeSelect value={asset.deviceTypeManual ? asset.deviceType ?? '' : ''} automatic={asset.deviceTypeManual ? null : asset.deviceType ?? asset.chassis ?? null}
        onChange={(v) => void save(v)} />
      {busy && <small className="muted"> Saving…</small>}
      {error && <small style={{ color: 'var(--crit)' }}>{error}</small>}
    </>
  );
}

/** Add a device by hand (devices that cannot be discovered, or before they are). */
export function AddDeviceDialog({ onClose, onDone }: { onClose: () => void; onDone: (a: Asset) => void }) {
  const deps = useDepartments();
  const [f, setF] = useState({ hostname: '', ip: '', macAddress: '', serialNumber: '', deviceType: '', manufacturer: '', model: '', osName: '', criticality: 'Medium', ownerName: '', departmentId: '' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const a = await api.post<Asset>('/assets', {
        hostname: f.hostname || null, ip: f.ip || null, macAddress: f.macAddress || null, serialNumber: f.serialNumber || null,
        deviceType: f.deviceType || null, manufacturer: f.manufacturer || null, model: f.model || null, osName: f.osName || null,
        criticality: f.criticality, ownerName: f.ownerName || null, departmentId: f.departmentId || null,
        osFamily: ['switch', 'router', 'firewall', 'access_point', 'wireless_controller', 'load_balancer', 'network'].includes(f.deviceType) ? 'network' : null,
      });
      onDone(a);
    } catch (x) { setError(errText(x)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="add-dev-title">
      <form className="modal-card wide" onSubmit={save}>
        <header><h2 id="add-dev-title">Add device</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="grid-2 tight">
            <label>Host name <input className="in" value={f.hostname} onChange={set('hostname')} maxLength={255} placeholder="e.g. PRN-FLOOR2" autoFocus /></label>
            <label>IP address <input className="in mono" value={f.ip} onChange={set('ip')} placeholder="e.g. 192.168.1.40" /></label>
            <label>Device type <DeviceTypeSelect value={f.deviceType} onChange={(v) => setF({ ...f, deviceType: v })} /></label>
            <label>MAC address <input className="in mono" value={f.macAddress} onChange={set('macAddress')} placeholder="00:1A:2B:3C:4D:5E" /></label>
            <label>Manufacturer <input className="in" value={f.manufacturer} onChange={set('manufacturer')} maxLength={200} /></label>
            <label>Model <input className="in" value={f.model} onChange={set('model')} maxLength={200} /></label>
            <label>Serial number <input className="in mono" value={f.serialNumber} onChange={set('serialNumber')} maxLength={200} /></label>
            <label>Operating system / firmware <input className="in" value={f.osName} onChange={set('osName')} maxLength={200} /></label>
            <label>Department / unit
              <DepartmentSelect value={f.departmentId} onChange={(v) => setF({ ...f, departmentId: v })} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd />
            </label>
            <label>Owner <input className="in" value={f.ownerName} onChange={set('ownerName')} maxLength={200} /></label>
            <label>Criticality
              <select className="in" value={f.criticality} onChange={set('criticality')}>{['Critical', 'High', 'Medium', 'Low'].map((c) => <option key={c}>{c}</option>)}</select>
            </label>
          </div>
          <small className="muted">A host name or an IP address is required. When a scan or an agent finds this device later, its details are added to this record (matched by name, address, serial number or MAC address).</small>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !f.deviceType || (!f.hostname.trim() && !f.ip.trim())}>{busy ? 'Adding…' : 'Add device'}</button>
        </footer>
      </form>
    </div>
  );
}

interface DupMember { id: string; hostname: string | null; ip: string | null; serial: string | null; mac: string | null; source: string; inventoryStatus: string; lastSeenAt: string; firstSeenAt: string; deviceType: string | null; hasAgent: boolean }
interface DupGroup { keep: DupMember; merge: DupMember[]; reasons: string[] }
interface DupList { auto: boolean; devices: number; groups: DupGroup[] }

export function useDuplicates() {
  return useApi(() => api.get<DupList>('/assets/duplicates'), []);
}

/** Devices recorded more than once: review, merge (the newest information is kept), automatic clean-up. */
export function DuplicatesDialog({ onClose, onChanged, canEdit, canSettings }: { onClose: () => void; onChanged: () => void; canEdit: boolean; canSettings: boolean }) {
  const dups = useDuplicates();
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  async function merge(keepIds: string[] | null) {
    setBusy(true); setMsg(null);
    try {
      const r = await api.post<{ message: string }>('/assets/duplicates/merge', { keepIds });
      setMsg({ tone: 'ok', text: r.message }); dups.reload(); onChanged();
    } catch (e) { setMsg({ tone: 'crit', text: errText(e) }); } finally { setBusy(false); }
  }
  async function setAuto(auto: boolean) {
    setMsg(null);
    try { await api.put('/assets/duplicates/settings', { auto }); dups.reload(); }
    catch (e) { setMsg({ tone: 'crit', text: errText(e) }); }
  }
  const row = (m: DupMember, kept: boolean) => (
    <tr key={m.id}>
      <td>{kept ? <Pill tone="ok">Keep</Pill> : <Pill tone="warn">Merge</Pill>}</td>
      <td><Link to={`/assets/${m.id}`}>{m.hostname ?? m.ip ?? m.id.slice(0, 8)}</Link><div className="faint" style={{ fontSize: 12 }}>{deviceTypeLabel(m.deviceType) ?? '—'}</div></td>
      <td className="mono">{m.ip ?? '—'}</td>
      <td className="mono" style={{ fontSize: 12 }}>{m.serial ?? '—'}<br />{m.mac ?? ''}</td>
      <td>{fmt.label(m.source)}{m.hasAgent ? ' · agent' : ''}<div className="faint" style={{ fontSize: 12 }}>{m.inventoryStatus === 'success' ? 'inventory collected' : 'not inventoried'}</div></td>
      <td>{fmt.ago(m.lastSeenAt)}</td>
    </tr>
  );
  const d = dups.data;
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="dup-title">
      <div className="modal-card wide">
        <header><h2 id="dup-title">Duplicate devices</h2></header>
        <div className="body form-v">
          {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
          <p style={{ margin: 0 }}>
            Records with the same name, serial number or network adapter are one device. Merging keeps one record with the <b>newest</b> address, name and
            last-seen time; when an older record holds the collected inventory, that record is kept so its hardware and software history stays.
          </p>
          <label className="row-check">
            <input type="checkbox" checked={d?.auto ?? true} disabled={!canSettings || !d} onChange={(e) => void setAuto(e.target.checked)} />
            Merge duplicates automatically (every 30 minutes)
          </label>
          {!d ? <p className="muted">Loading…</p> : d.groups.length === 0 ? <Callout tone="ok" icon="check">No duplicate devices.</Callout> : (
            <div className="tbl-wrap" style={{ maxHeight: 420, overflow: 'auto' }}>
              <table className="t">
                <thead><tr><th /><th>Device</th><th>IP</th><th>Serial / MAC</th><th>Source</th><th>Last seen</th></tr></thead>
                {d.groups.map((g) => (
                  <tbody key={g.keep.id} className="group">
                    <tr><td colSpan={6} className="faint" style={{ fontSize: 12 }}>
                      {g.reasons.join(' · ')}
                      {canEdit && <button className="btn sm" style={{ float: 'right' }} disabled={busy} onClick={() => void merge([g.keep.id])}>Merge these {g.merge.length + 1}</button>}
                    </td></tr>
                    {row(g.keep, true)}
                    {g.merge.map((m) => row(m, false))}
                  </tbody>
                ))}
              </table>
            </div>
          )}
        </div>
        <footer>
          <button className="btn" onClick={onClose}>Close</button>
          {canEdit && d && d.groups.length > 0 && <button className="btn primary" disabled={busy} onClick={() => void merge(null)}>{busy ? 'Merging…' : `Merge all (${d.devices} duplicate record${d.devices === 1 ? '' : 's'})`}</button>}
        </footer>
      </div>
    </div>
  );
}

/** Permanently delete devices (with confirmation). Returns true when deleted. */
export function useDeleteDevices() {
  const confirm = useConfirm();
  return async (ids: string[], name?: string): Promise<string | null> => {
    const what = ids.length === 1 ? `"${name ?? 'this device'}"` : `${ids.length} devices`;
    const ok = await confirm({
      title: `Delete ${what}?`,
      message: 'Its inventory, change history and vulnerability findings are deleted permanently. If a scan or an agent reports the device again, it is added again as a new record. To keep the history, set the status to retired instead.',
      confirmLabel: 'Delete', danger: true,
    });
    if (!ok) return null;
    const r = ids.length === 1 ? await api.del<{ message: string }>(`/assets/${ids[0]}`) : await api.post<{ message: string }>('/assets/delete', { ids });
    return r?.message ?? 'Deleted.';
  };
}
