import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Panel, Pill } from '../../components/ui';
import { UiPath } from '../../components/Help';

export interface WarrantyProvider {
  vendor: 'dell' | 'lenovo' | 'hp'; label: string; enabled: boolean; configured: boolean; clientId: string | null; apiUrl: string | null; defaultUrl: string;
  lastOkAt: string | null; lastError: string | null; lastErrorAt: string | null; updatedAt: string | null; updatedBy: string | null;
}
export interface WarrantyVendorStatus { vendor: string; devices: number; found: number; notFound: number; errors: number; noKey: number; notChecked: number; lastCheckedAt: string | null }
export interface WarrantyOverview {
  settings: { auto: boolean; refreshDays: number; overwriteManual: boolean };
  providers: WarrantyProvider[]; vendors: WarrantyVendorStatus[]; requested: boolean;
}
export interface WarrantyLookup {
  vendor?: string; serial?: string; status: 'found' | 'not_found' | 'error' | 'no_key' | null; checkedAt?: string; product?: string | null; shipDate?: string | null;
  warrantyStart?: string | null; warrantyEnd?: string | null; serviceLevel?: string | null; message?: string | null; applied?: boolean;
  entitlements?: { name: string | null; start: string | null; end: string | null; type: string | null }[];
}
interface LookupResult { queued: boolean; message: string; found?: number; notFound?: number; errors?: number }

const LABEL: Record<string, string> = { dell: 'Dell', lenovo: 'Lenovo', hp: 'HP' };
const HELP: Record<string, ReactNode> = {
  dell: <><UiPath>Dell TechDirect › APIs › Warranty API</UiPath>: request a key, then use its Client ID and Client Secret.</>,
  lenovo: <>Ask your Lenovo account team (or the Lenovo Support API program) for a warranty API Client ID. No secret is needed.</>,
  hp: <><UiPath>HP Developer Portal (developers.hp.com) › HP Warranty API › Get credentials</UiPath> (approval by HP). HP keys expire after 90 days by default.</>,
};

export async function lookUpWarranty(assetIds: string[] | 'all'): Promise<string> {
  const r = await api.post<LookupResult>('/lifecycle/warranty/lookup', assetIds === 'all' ? { all: true } : { assetIds });
  return r.message || (r.queued ? 'Warranty lookup started.' : 'Done.');
}

const STATUS: Record<string, [string, 'ok' | 'warn' | 'crit' | 'info']> = {
  found: ['Found', 'ok'], not_found: ['Not known to the manufacturer', 'warn'], error: ['Lookup failed', 'crit'], no_key: ['No API key', 'info'],
};

/** Asset Lifecycle › Settings: the vendor API keys and the lookup settings. */
export function WarrantyKeysPanel() {
  const { can } = useAuth();
  const canEdit = can('settings.manage');
  const [tick, setTick] = useState(0);
  const data = useApi(() => api.get<WarrantyOverview>('/lifecycle/warranty'), [tick]);
  const [editing, setEditing] = useState<string | null>(null);
  const [form, setForm] = useState({ clientId: '', secret: '', apiUrl: '', enabled: true });
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [testSerial, setTestSerial] = useState('');
  const d = data.data;
  function edit(p: WarrantyProvider) { setEditing(p.vendor); setForm({ clientId: p.clientId ?? '', secret: '', apiUrl: p.apiUrl ?? '', enabled: p.enabled }); setMsg(null); }
  async function save(vendor: string, remove = false) {
    setBusy(true); setMsg(null);
    try {
      await api.put(`/lifecycle/warranty/providers/${vendor}`, remove ? { enabled: false, clientId: '' } : { enabled: form.enabled, clientId: form.clientId.trim(), secret: form.secret || null, apiUrl: form.apiUrl.trim() || null });
      setEditing(null); setTick((x) => x + 1); setMsg({ tone: 'ok', text: remove ? `${LABEL[vendor]} key removed.` : `${LABEL[vendor]} key saved.` });
    } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  async function test(vendor: string) {
    setBusy(true); setMsg(null);
    try { const r = await api.post<{ ok: boolean; message: string }>(`/lifecycle/warranty/providers/${vendor}/test`, { serial: testSerial.trim() || null }); setMsg({ tone: r.ok ? 'ok' : 'crit', text: r.message }); setTick((x) => x + 1); }
    catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  async function saveSettings(next: WarrantyOverview['settings']) {
    try { await api.put('/lifecycle/warranty/settings', next); setTick((x) => x + 1); } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }
  if (data.error) return <ErrorBox error={data.error} />;
  if (!d) return null;
  return (
    <Panel title="Online Warranty Lookup" sub="Zentriq reads the warranty of Dell, Lenovo and HP devices from the manufacturer by serial number and fills the warranty of each asset. Dates you enter by hand are kept.">
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tbl-wrap"><table className="t">
        <thead><tr><th>Manufacturer</th><th>API Key</th><th>Status</th><th>Devices</th><th /></tr></thead>
        <tbody>{d.providers.map((p) => {
          const v = d.vendors.find((x) => x.vendor === p.vendor);
          return (
            <tr key={p.vendor}>
              <td><b>{p.label}</b></td>
              <td>{p.configured ? <><Pill tone={p.enabled ? 'ok' : ''}>{p.enabled ? 'On' : 'Off'}</Pill> <span className="mono faint">{p.clientId?.slice(0, 6)}…</span></> : <span className="muted">Not entered</span>}</td>
              <td className="wrap">{p.lastError ? <span style={{ color: 'var(--crit)' }}>{p.lastError}</span> : p.lastOkAt ? <span className="faint">Worked {fmt.ago(p.lastOkAt)}</span> : '—'}</td>
              <td>{v ? <>{v.devices.toLocaleString()} <span className="faint">· {v.found} found{v.notChecked ? ` · ${v.notChecked} not checked` : ''}{v.notFound ? ` · ${v.notFound} unknown` : ''}</span></> : 0}</td>
              <td style={{ whiteSpace: 'nowrap' }}>{canEdit && <>
                <button className="btn sm" onClick={() => edit(p)}><Icon name="edit" size={13} /> {p.configured ? 'Change' : 'Enter Key'}</button>{' '}
                {p.configured && <button className="btn sm" disabled={busy} onClick={() => void test(p.vendor)}>Test</button>}
              </>}</td>
            </tr>
          );
        })}</tbody>
      </table></div>
      {canEdit && <div className="toolbar-row" style={{ marginTop: 8 }}>
        <label className="muted" style={{ fontSize: 12.5 }}>Test with serial number <input className="in mono" style={{ width: 150 }} value={testSerial} onChange={(e) => setTestSerial(e.target.value)} placeholder="optional" /></label>
      </div>}
      {editing && (() => {
        const p = d.providers.find((x) => x.vendor === editing)!;
        return (
          <div style={{ marginTop: 10, padding: 12, border: "1px solid var(--line)", borderRadius: 8 }}>
            <h3 style={{ marginTop: 0 }}>{p.label} API Key</h3>
            <p className="faint" style={{ fontSize: 12.5 }}>{HELP[p.vendor]}</p>
            <div className="form-grid-2">
              <label className="field"><span>Client ID</span><input className="in mono" value={form.clientId} onChange={(e) => setForm({ ...form, clientId: e.target.value })} autoComplete="off" /></label>
              {p.vendor !== 'lenovo' && <label className="field"><span>Client Secret</span><input className="in mono" type="password" value={form.secret} onChange={(e) => setForm({ ...form, secret: e.target.value })} placeholder={p.configured ? 'Stored — leave empty to keep it' : ''} autoComplete="new-password" /></label>}
              <label className="field"><span>API Address</span><input className="in mono" value={form.apiUrl} onChange={(e) => setForm({ ...form, apiUrl: e.target.value })} placeholder={p.defaultUrl} /></label>
              <label className="check-inline"><input type="checkbox" checked={form.enabled} onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Look up {p.label} devices</label>
            </div>
            <p className="faint" style={{ fontSize: 12 }}>The secret is stored encrypted on the Zentriq server and is never shown again. The server needs internet access to the address above (or a proxy).</p>
            <div style={{ display: 'flex', gap: 6 }}>
              <button className="btn primary" disabled={busy || !form.clientId.trim()} onClick={() => void save(p.vendor)}>Save</button>
              <button className="btn" onClick={() => setEditing(null)}>Cancel</button>
              {p.configured && <button className="btn danger" style={{ marginLeft: 'auto' }} disabled={busy} onClick={() => void save(p.vendor, true)}>Remove Key</button>}
            </div>
          </div>
        );
      })()}
      <div className="form-grid-2" style={{ marginTop: 12 }}>
        <label className="check-inline"><input type="checkbox" checked={d.settings.auto} disabled={!canEdit} onChange={(e) => void saveSettings({ ...d.settings, auto: e.target.checked })} /> Look up new devices automatically and check again every</label>
        <label className="field" style={{ maxWidth: 160 }}><span>Days</span>
          <select className="in" value={d.settings.refreshDays} disabled={!canEdit} onChange={(e) => void saveSettings({ ...d.settings, refreshDays: Number(e.target.value) })}>
            {[7, 14, 30, 60, 90, 180, 365].map((n) => <option key={n} value={n}>{n} days</option>)}
          </select></label>
        <label className="check-inline"><input type="checkbox" checked={d.settings.overwriteManual} disabled={!canEdit} onChange={(e) => void saveSettings({ ...d.settings, overwriteManual: e.target.checked })} /> Replace warranty dates entered by hand or imported</label>
      </div>
    </Panel>
  );
}

/** Asset Lifecycle › Warranty & Contracts: lookup status per manufacturer and Look Up Warranty for every device. */
export function WarrantyLookupPanel({ onMsg }: { onMsg: (m: string) => void }) {
  const { can } = useAuth();
  const [tick, setTick] = useState(0);
  const data = useApi(() => api.get<WarrantyOverview>('/lifecycle/warranty'), [tick]);
  const [busy, setBusy] = useState(false);
  const d = data.data;
  if (!d) return null;
  const keys = d.providers.filter((p) => p.configured && p.enabled);
  async function all() {
    setBusy(true);
    try { onMsg(await lookUpWarranty('all')); setTick((x) => x + 1); } catch (e) { onMsg(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Panel title="Manufacturer Warranty" sub={keys.length ? `Looked up online for ${keys.map((k) => k.label).join(', ')} devices` : 'Enter a Dell, Lenovo or HP API key under Settings to look warranties up online'}
      right={can('lifecycle.manage') && <button className="btn sm primary" disabled={busy || keys.length === 0 || d.requested} onClick={() => void all()}><Icon name="retry" size={13} /> {d.requested ? 'Lookup Running…' : 'Look Up Warranty'}</button>} flush>
      <DataTable rows={d.vendors.map((v) => ({ ...v, id: v.vendor }))} empty="No Dell, Lenovo or HP devices with a serial number yet." columns={[
        { key: 'v', label: 'Manufacturer', render: (v) => <b>{LABEL[v.vendor] ?? v.vendor}</b> },
        { key: 'd', label: 'Devices', render: (v) => v.devices.toLocaleString() },
        { key: 'f', label: 'Warranty Found', render: (v) => v.found.toLocaleString() },
        { key: 'n', label: 'Unknown', render: (v) => v.notFound ? <Pill tone="warn">{v.notFound}</Pill> : '0' },
        { key: 'e', label: 'Failed / No Key', render: (v) => v.errors + v.noKey ? <Pill tone="crit">{v.errors + v.noKey}</Pill> : '0' },
        { key: 'c', label: 'Not Checked', render: (v) => v.notChecked.toLocaleString() },
        { key: 'l', label: 'Last Lookup', render: (v) => v.lastCheckedAt ? fmt.ago(v.lastCheckedAt) : '—' },
      ]} />
    </Panel>
  );
}

/** Device › Lifecycle: what the manufacturer reported, with Look Up Warranty. */
export function WarrantyCard({ assetId, onChanged }: { assetId: string; onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const [tick, setTick] = useState(0);
  const w = useApi(() => api.get<WarrantyLookup>(`/lifecycle/warranty/assets/${assetId}`), [assetId, tick]);
  const [busy, setBusy] = useState(false);
  async function lookup() {
    setBusy(true);
    try { const m = await lookUpWarranty([assetId]); setTick((x) => x + 1); onChanged(m); } catch (e) { onChanged(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  const d = w.data;
  const st = d?.status ? STATUS[d.status] : null;
  return (
    <Panel title="Manufacturer Warranty" sub={d?.checkedAt ? `${LABEL[d.vendor ?? ''] ?? d.vendor} · serial ${d.serial} · checked ${fmt.ago(d.checkedAt)}` : 'Read from Dell, Lenovo or HP by serial number'}
      right={can('lifecycle.manage') && <button className="btn sm" disabled={busy} onClick={() => void lookup()}><Icon name="retry" size={13} /> {busy ? 'Looking Up…' : 'Look Up Warranty'}</button>} flush={!!d?.entitlements?.length}>
      {!d?.status && <p className="muted" style={{ margin: 0 }}>Not looked up yet.</p>}
      {d?.status && <div style={{ padding: d.entitlements?.length ? '10px 14px 0' : 0 }}>
        <p style={{ margin: '0 0 8px' }}>{st && <Pill tone={st[1]}>{st[0]}</Pill>} {d.product && <b>{d.product}</b>}
          {d.warrantyEnd && <> · until <b>{fmt.day(d.warrantyEnd)}</b>{d.serviceLevel ? ` (${d.serviceLevel})` : ''}</>}
          {d.shipDate && <span className="faint"> · shipped {fmt.day(d.shipDate)}</span>}
          {d.status === 'found' && !d.applied && <span className="faint"> · not copied to the asset (dates entered by hand are kept — see Settings)</span>}
        </p>
        {d.message && d.status !== 'found' && <p className="faint" style={{ margin: '0 0 8px' }}>{d.message}</p>}
      </div>}
      {!!d?.entitlements?.length && <DataTable rows={d.entitlements.map((e, i) => ({ ...e, id: i }))} columns={[
        { key: 'n', label: 'Cover', className: 'wrap', render: (e) => e.name ?? '—' },
        { key: 't', label: 'Type', render: (e) => e.type ?? '—' },
        { key: 's', label: 'Start', render: (e) => e.start ? fmt.day(e.start) : '—' },
        { key: 'e', label: 'End', render: (e) => e.end ? <>{fmt.day(e.end)} {e.end < new Date().toISOString().slice(0, 10) ? <Pill tone="crit">Ended</Pill> : null}</> : '—' },
      ]} />}
    </Panel>
  );
}
