import { useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { Asset } from '../../api/types';
import { Callout, Pill } from '../../components/ui';

type Field = 'manufacturer' | 'model' | 'serialNumber';

export type HwField = 'manufacturer' | 'model' | 'serialNumber';
type Field = HwField;

const PLACEHOLDERS = new Set(['to be filled by o.e.m.', 'to be filled by oem', 'default string', 'system serial number', 'system manufacturer', 'system product name',
  'system version', 'not specified', 'not applicable', 'none', 'n/a', 'na', 'oem', 'o.e.m.', 'chassis serial number', 'base board serial number',
  'invalid', 'unknown', 'empty', 'serial', '123456789', '0123456789', 'xxxxxxxxxx', 'type1productconfigid', 'type1family', 'sku']);
/** The value, or null when it is empty or a firmware placeholder ("To be filled by O.E.M.", "Default string", "0000000"). */
export function hwClean(v: string | null | undefined): string | null {
  const t = (v ?? '').trim();
  if (!t || PLACEHOLDERS.has(t.toLowerCase()) || /^(.)\1*$/.test(t)) return null;
  return t;
}

/** Small "Entered manually" marker shown next to a value an administrator typed in. */
export function ManualMark() {
  return <span title="Not reported by the device; entered manually in Zentriq"><Pill tone="info">Entered manually</Pill></span>;
}

/**
 * Manufacturer, model and serial number rows for the device Overview. Detected values are shown as they are; a value the
 * device does not report can be entered by hand ("Add manually"), and is remembered by the device's fixed identities so it
 * comes back after the agent or Windows is reinstalled.
 */
export function hardwareIdentityRows(a: Asset, reported: Record<Field, string | null | undefined>, canEdit: boolean, onEdit: () => void) {
  const manual = new Set(a.enteredManually ?? []);
  const detected: Record<Field, string | null> = {
    manufacturer: hwClean(reported.manufacturer) ?? (manual.has('manufacturer') ? null : hwClean(a.manufacturer)),
    model: hwClean(reported.model) ?? (manual.has('model') ? null : hwClean(a.model)),
    serialNumber: hwClean(reported.serialNumber) ?? (manual.has('serialNumber') ? null : hwClean(a.serialNumber)),
  };
  const entered: Record<Field, string | null> = { manufacturer: a.manualManufacturer ?? null, model: a.manualModel ?? null, serialNumber: a.manualSerial ?? null };
  const show = (f: Field, mono = false): ReactNode => {
    const det = detected[f];
    const v = det ?? entered[f];
    if (!v) {
      return canEdit
        ? <span><span className="muted">Not detected</span> <button type="button" className="btn sm" onClick={onEdit}>Add manually</button></span>
        : <span className="muted">Not detected</span>;
    }
    const isManual = !det;
    return <span>{mono ? <span className="mono">{v}</span> : v}{isManual && <> <ManualMark /></>}
      {isManual && canEdit && <button type="button" className="btn sm" style={{ marginLeft: 6 }} onClick={onEdit}>Edit</button>}</span>;
  };
  const rows: [string, ReactNode][] = [['Manufacturer', show('manufacturer')], ['Model', show('model')], ['Serial number', show('serialNumber', true)]];
  return { rows, detected };
}

export function HardwareIdentityDialog({ asset, detected, onClose, onSaved }: { asset: Asset; detected: Record<Field, string | null>; onClose: () => void; onSaved: (msg: string) => void }) {
  const [f, setF] = useState({ manufacturer: asset.manualManufacturer ?? '', model: asset.manualModel ?? '', serialNumber: asset.manualSerial ?? '' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent, clear = false) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = clear ? { manufacturer: null, model: null, serialNumber: null } : {
        manufacturer: detected.manufacturer ? null : f.manufacturer.trim() || null,
        model: detected.model ? null : f.model.trim() || null,
        serialNumber: detected.serialNumber ? null : f.serialNumber.trim() || null,
      };
      const r = await api.put<{ message: string }>(`/assets/${asset.id}/hardware-identity`, body);
      onSaved(r.message);
    } catch (x) { setError(x instanceof Error ? x.message : String(x)); } finally { setBusy(false); }
  }
  const input = (k: Field, label: string, mono = false) => (
    <label>{label}
      {detected[k]
        ? <input className={`in${mono ? ' mono' : ''}`} value={detected[k] ?? ''} disabled title="Reported by the device" />
        : <input className={`in${mono ? ' mono' : ''}`} value={f[k]} maxLength={200} onChange={(e) => setF({ ...f, [k]: e.target.value })} placeholder="Not detected — type it here" />}
      {detected[k] && <small className="faint">Detected from the device</small>}
    </label>
  );
  const hasManual = !!(asset.manualManufacturer || asset.manualModel || asset.manualSerial);
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="hwid-title">
      <form className="modal-card" onSubmit={(e) => void save(e)}>
        <header><h2 id="hwid-title">Hardware details — {asset.hostname ?? asset.ip ?? 'device'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {input('manufacturer', 'Manufacturer')}
          {input('model', 'Model')}
          {input('serialNumber', 'Serial number', true)}
          <small className="muted">
            Values the device reports always take priority. What you enter here fills the gaps and is remembered by the device's SMBIOS UUID and
            network adapters, so it is filled in again automatically if the agent or Windows is reinstalled.
          </small>
        </div>
        <footer>
          {hasManual && <button type="button" className="btn danger" disabled={busy} onClick={(e) => void save(e, true)} style={{ marginRight: 'auto' }}>Remove entered values</button>}
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}
