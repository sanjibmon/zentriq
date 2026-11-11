import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Check, Field, Fields, Section } from '../../components/Form';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { AddOnNotice, bytes, ModeChoice, Policies, useMessages, useSecurityPack, type AreaPolicy, type Group } from './common';

interface Settings { mode: 'off' | 'audit' | 'readonly' | 'block'; auditFiles: boolean; message: string | null }
interface Overview { policies: AreaPolicy<Settings>[]; kpis: { devices: number; usbDevices: number; blocked: number; readonly: number; files: number; filesBytes: number; allowed: number } }
interface Device { assetId: string; hostname: string; deviceId: string; name: string | null; vidPid: string | null; serial: string | null; firstSeen: string; lastSeen: string; access: string | null; files: number }
interface Ev { id: number; at: string; assetId: string; hostname: string; user: string | null; kind: string; deviceId: string | null; deviceName: string | null; path: string | null; size: number | null; detail: string | null }
interface Allowed { id: string; deviceId: string; name: string | null; access: 'full' | 'readonly'; groupId: string | null; groupName: string | null; note: string | null; createdBy: string | null; createdAt: string }

const MODE: Record<string, string> = { off: 'Off', audit: 'Audit', readonly: 'Read-only', block: 'Block' };
const ACCESS: Record<string, [string, '' | 'ok' | 'warn' | 'crit' | 'info']> = { full: ['Full access', 'ok'], readonly: ['Read-only', 'warn'], blocked: ['Blocked', 'crit'] };
const EVENT: Record<string, [string, '' | 'ok' | 'warn' | 'crit' | 'info']> = {
  connected: ['Connected', 'info'], blocked: ['Blocked', 'crit'], readonly: ['Made read-only', 'warn'], allowed: ['Allowed', 'ok'], failed: ['Failed', 'crit'],
  file_written: ['File copied / changed', ''], file_renamed: ['File renamed', ''], file_deleted: ['File deleted', ''],
};
const PAGE = 50;
type Tab = 'activity' | 'devices' | 'allowed' | 'policy';
const TABS: [Tab, string][] = [['activity', 'File & Device Activity'], ['devices', 'USB Devices'], ['allowed', 'Allowed Devices'], ['policy', 'Policy']];

export function UsbPage() {
  const licensed = useSecurityPack();
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canManage = can('devicecontrol.manage');
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'activity';
  const m = useMessages();
  const data = useApi(() => (licensed ? api.get<Overview>('/security-pack/usb') : Promise.resolve(null)), [licensed, m.tick]);
  const k = data.data?.kpis;
  const [allow, setAllow] = useState<Device | 'new' | null>(null);
  return (
    <>
      <PageHead title="USB Device Control" icon="usb" />
      {!licensed && <AddOnNotice />}
      {licensed && <>
        <ErrorBox error={data.error} />
        {k && <Kpis items={[
          { label: 'USB Drives Seen', value: k.usbDevices.toLocaleString(), sub: 'Last 30 days' },
          { label: 'Blocked', value: k.blocked.toLocaleString(), sub: `${k.readonly} made read-only (7 days)` },
          { label: 'Files Copied', value: k.files.toLocaleString(), sub: `${bytes(k.filesBytes)} to USB drives (7 days)` },
          { label: 'Allowed Devices', value: k.allowed.toLocaleString(), sub: `${k.devices} computer(s) with the Security Pack` },
        ]} />}
        {m.view}
        <div className="tabs" role="tablist">
          {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/security-pack/usb?tab=${t}`, true)}>{l}</button>)}
        </div>
        {tab === 'activity' && <ActivityTab />}
        {tab === 'devices' && <DevicesTab canManage={canManage} onAllow={(d) => setAllow(d)} />}
        {tab === 'allowed' && <AllowedTab canManage={canManage} tick={m.tick} changed={m.changed} failed={m.failed} onAdd={() => setAllow('new')} />}
        {tab === 'policy' && data.data && <PolicyTab policies={data.data.policies} canManage={canManage} changed={m.changed} failed={m.failed} />}
        {allow && <AllowDialog device={allow === 'new' ? null : allow} onClose={() => setAllow(null)} onSaved={() => { setAllow(null); m.changed('Allowed. Computers apply it within seconds when online.'); }} />}
      </>}
    </>
  );
}

function ActivityTab() {
  const [kind, setKind] = useSticky('UsbPage.activity.kind', 'files');
  const [q, setQ] = useSticky('UsbPage.activity.q', '');
  const [days, setDays] = useSticky('UsbPage.activity.days', 7);
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Ev[]>('/security-pack/usb/events', { kind: kind || null, q: q || null, days }), [kind, q, days]);
  const rows = data.data ?? [];
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search computers, users, files, devices…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search" />
        <select className="in" value={kind} onChange={(e) => { setKind(e.target.value); setPage(1); }} aria-label="Event">
          <option value="files">Files</option><option value="devices">Devices (connected, blocked …)</option><option value="">Everything</option>
        </select>
        <select className="in" value={days} onChange={(e) => { setDays(Number(e.target.value)); setPage(1); }} aria-label="Period">
          <option value={1}>Today</option><option value={7}>7 days</option><option value={30}>30 days</option><option value={120}>120 days</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <Pager top page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
        <DataTable rows={rows.slice((page - 1) * PAGE, page * PAGE)} empty={data.loading ? 'Loading…' : 'Nothing recorded. File copies are recorded when the policy is Audit, Read-only or Block with “Record files”.'} columns={[
          { key: 't', label: 'When', render: (e) => fmt.date(e.at) },
          { key: 'k', label: 'Event', render: (e) => { const [l, t] = EVENT[e.kind] ?? [e.kind, '']; return <Pill tone={t}>{l}</Pill>; } },
          { key: 'u', label: 'User · Computer', render: (e) => <><b>{e.user ?? '—'}</b><div className="faint" style={{ fontSize: 12 }}><Link to={`/assets/${e.assetId}`}>{e.hostname}</Link></div></> },
          { key: 'f', label: 'File', className: 'wrap', render: (e) => (e.path ? <span className="mono" style={{ fontSize: 12, wordBreak: 'break-all' }}>{e.path}</span> : '—') },
          { key: 's', label: 'Size', render: (e) => bytes(e.size) },
          { key: 'd', label: 'USB Device', className: 'wrap', render: (e) => <>{e.deviceName ?? '—'}<div className="faint mono" style={{ fontSize: 11 }}>{e.deviceId}</div>{e.detail && <div className="faint" style={{ fontSize: 11.5 }}>{e.detail}</div>}</> },
        ]} />
        <Pager page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
      </Panel>
    </>
  );
}

function DevicesTab({ canManage, onAllow }: { canManage: boolean; onAllow: (d: Device) => void }) {
  const [q, setQ] = useSticky('UsbPage.devices.q', '');
  const data = useApi(() => api.get<Device[]>('/security-pack/usb/devices', { q: q || null }), [q]);
  const rows = (data.data ?? []).map((d) => ({ ...d, id: `${d.assetId}:${d.deviceId}` }));
  return (
    <>
      <div className="toolbar-row"><input className="in grow" placeholder="Search computers, device names, IDs…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search" /></div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No USB storage seen yet.'} columns={[
          { key: 'n', label: 'Device', className: 'wrap', render: (d) => <><b>{d.name ?? 'USB drive'}</b><div className="faint mono" style={{ fontSize: 11, wordBreak: 'break-all' }}>{d.deviceId}</div></> },
          { key: 'h', label: 'Computer', render: (d) => <Link to={`/assets/${d.assetId}`}>{d.hostname}</Link> },
          { key: 'a', label: 'Access', render: (d) => { const [l, t] = ACCESS[d.access ?? ''] ?? [fmt.label(d.access ?? '—'), '']; return <Pill tone={t}>{l}</Pill>; } },
          { key: 'f', label: 'Files', render: (d) => d.files },
          { key: 's', label: 'Last Seen', render: (d) => fmt.ago(d.lastSeen) },
          { key: 'x', label: '', render: (d) => canManage && d.access !== 'full' && <button className="btn sm" onClick={() => onAllow(d)}>Allow…</button> },
        ]} />
      </Panel>
    </>
  );
}

function AllowedTab({ canManage, tick, changed, failed, onAdd }: { canManage: boolean; tick: number; changed: (t: string) => void; failed: (e: unknown) => void; onAdd: () => void }) {
  const confirm = useConfirm();
  const data = useApi(() => api.get<Allowed[]>('/security-pack/usb/allowed'), [tick]);
  async function remove(a: Allowed) {
    if (!await confirm({ title: `Stop allowing ${a.name ?? a.deviceId}?`, message: 'The policy applies to it again the next time it is connected (or at once when connected).', danger: true, confirmLabel: 'Remove' })) return;
    try { await api.del(`/security-pack/usb/allowed/${a.id}`); changed('Removed.'); } catch (e) { failed(e); }
  }
  return (
    <Panel flush title="Allowed Devices" sub="Exceptions to Block and Read-only — by vendor and product (VID/PID), with a serial number for one device, or by device instance ID"
      right={canManage && <button className="btn primary" onClick={onAdd}><Icon name="plus" size={14} /> Allow a Device</button>}>
      <DataTable rows={data.data ?? []} empty={data.loading ? 'Loading…' : 'No exceptions.'} columns={[
        { key: 'n', label: 'Device', className: 'wrap', render: (a) => <><b>{a.name ?? '—'}</b><div className="faint mono" style={{ fontSize: 11.5, wordBreak: 'break-all' }}>{a.deviceId}</div></> },
        { key: 'a', label: 'Access', render: (a) => <Pill tone={a.access === 'full' ? 'ok' : 'warn'}>{a.access === 'full' ? 'Full access' : 'Read-only'}</Pill> },
        { key: 'g', label: 'On', render: (a) => a.groupName ?? 'All computers' },
        { key: 'o', label: 'Note', className: 'wrap', render: (a) => a.note ?? '—' },
        { key: 'b', label: 'Added', render: (a) => <>{fmt.day(a.createdAt)}<div className="faint" style={{ fontSize: 11.5 }}>{a.createdBy}</div></> },
        { key: 'x', label: '', render: (a) => canManage && <button className="btn sm danger" onClick={() => void remove(a)}>Remove</button> },
      ]} />
    </Panel>
  );
}

function AllowDialog({ device, onClose, onSaved }: { device: Device | null; onClose: () => void; onSaved: () => void }) {
  const groups = useApi(() => api.get<Group[]>('/security-pack/usb/groups'), []);
  const [f, setF] = useState({ deviceId: device?.deviceId ?? '', name: device?.name ?? '', access: 'full', groupId: '', note: '', model: false });
  const [err, setErr] = useState<string | null>(null);
  const id = f.model && device?.vidPid ? device.vidPid : f.deviceId;
  async function save() {
    try { await api.post('/security-pack/usb/allowed', { deviceId: id, name: f.name || null, access: f.access, groupId: f.groupId || null, note: f.note || null }); onSaved(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal wide title="Allow a USB Device" onClose={onClose} footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={id.trim().length < 4} onClick={() => void save()}>Allow</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Device">
          <Fields>
            <Field label="Device ID" required span hint="VID_0781&PID_5581 (every drive of this model), VID_0781&PID_5581\4C530001231122108455 (this drive), or the device instance ID; * and ? are wildcards.">
              <input className="in mono" value={f.model && device?.vidPid ? device.vidPid : f.deviceId} disabled={f.model} onChange={(e) => setF({ ...f, deviceId: e.target.value })} placeholder="VID_0781&PID_5581\4C530001231122108455" />
            </Field>
            <Field label="Name" optional><input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Finance encrypted drives" /></Field>
            <Field label="On">
              <select className="in" value={f.groupId} onChange={(e) => setF({ ...f, groupId: e.target.value })}><option value="">All computers</option>{(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}</select>
            </Field>
          </Fields>
          {device?.vidPid && device.serial && <Check checked={f.model} onChange={(v) => setF({ ...f, model: v })} hint={`${device.vidPid}: every drive of this vendor and model, not only this one.`}>Allow the whole model</Check>}
        </Section>
        <Section title="Access">
          <ModeChoice value={f.access} onChange={(access) => setF({ ...f, access })} options={[['full', 'Full access', 'Read and write', 'ok'], ['readonly', 'Read-only', 'Files cannot be copied to it']]} />
          <Fields cols={1}><Field label="Note" optional><input className="in" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} /></Field></Fields>
        </Section>
      </div>
    </Modal>
  );
}

function PolicyTab({ policies, canManage, changed, failed }: { policies: AreaPolicy<Settings>[]; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }) {
  const [edit, setEdit] = useState<{ p: AreaPolicy<Settings> | null; groupId: string | null } | null>(null);
  return (
    <>
      <Policies area="usb" policies={policies} canManage={canManage} modeLabel={MODE} modeTone={(x) => (x === 'block' ? 'crit' : x === 'readonly' ? 'warn' : x === 'audit' ? 'info' : '')}
        describe={(s) => <>{s.auditFiles ? 'Files copied are recorded' : 'Files not recorded'}{s.message ? ` · message: “${s.message}”` : ''}</>}
        edit={(p, groupId) => setEdit({ p, groupId })} changed={changed} failed={failed} />
      {edit && <PolicyDialog policy={edit.p} groupId={edit.groupId} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); changed('Policy saved. Computers apply it within seconds when online.'); }} />}
    </>
  );
}

function PolicyDialog({ policy, groupId, onClose, onSaved }: { policy: AreaPolicy<Settings> | null; groupId: string | null; onClose: () => void; onSaved: () => void }) {
  const s = policy?.settings;
  const [f, setF] = useState({ mode: s?.mode ?? 'audit', auditFiles: s?.auditFiles ?? true, message: s?.message ?? '' });
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    try { await api.put('/security-pack/usb/policy', { groupId, mode: f.mode, auditFiles: f.auditFiles, message: f.message || null }); onSaved(); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal wide title={`USB Policy — ${policy?.groupName ?? (groupId ? 'Device Group' : 'All Devices')}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" onClick={() => void save()}>Save</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="USB Storage" sub="USB drives, card readers and phones in mass-storage mode. Keyboards, mice, headsets and other USB devices are not affected.">
          <ModeChoice value={f.mode} onChange={(mode) => setF({ ...f, mode })} options={[
            ['off', 'Off', 'Nothing checked'], ['audit', 'Audit', 'Record devices and files'], ['readonly', 'Read-only', 'Nothing copied to them', 'ok'], ['block', 'Block', 'Not usable', 'crit'],
          ]} />
          <Check checked={f.auditFiles} onChange={(v) => setF({ ...f, auditFiles: v })} hint="Name, size, user and time of each file written, renamed or deleted on a USB drive with access — never the content.">Record files copied to USB drives</Check>
          <Fields cols={1}><Field label="Message to the user" optional hint="Shown when a drive is blocked or made read-only."><input className="in" value={f.message} maxLength={500} onChange={(e) => setF({ ...f, message: e.target.value })} placeholder="USB drives are blocked. Ask IT for an approved encrypted drive." /></Field></Fields>
        </Section>
      </div>
    </Modal>
  );
}
