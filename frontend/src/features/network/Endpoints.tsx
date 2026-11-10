import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { ChoosableTable } from '../../components/columns';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Panel, Pill } from '../../components/ui';
import { deviceTypeLabel } from '../assets/deviceTypes';

export interface Endpoint {
  mac: string; ip: string | null; vendor: string | null; kind: string | null; assetId: string | null; assetName: string | null; deviceType: string | null;
  switchId: string | null; switchName: string | null; port: string | null; ifIndex: number | null; vlan: number | null; behindUnmanaged: boolean;
  firstSeen: string; lastSeen: string; baseline: boolean; movedAt: string | null; prevSwitchName: string | null; prevPort: string | null;
  prevIp: string | null; ipChangedAt: string | null; acknowledged: boolean; note: string | null; ackBy: string | null; unknown: boolean;
}
interface VendorCount { vendor: string; count: number; unknown: number }
interface VendorStatus { prefixes: number; status: { updatedAt?: string; source?: string; prefixes?: number; error?: string; errorAt?: string } | null }
export interface Subnet {
  cidr: string; prefix: number; size: number; via: string[] | null; vlans: number[] | null; gateways: string[] | null; fromDevices: boolean | null;
  used: number; known: number; unknown: number; conflicts: number; lastSeen: string | null;
}
interface IpRow {
  ip: string; used: boolean; macs: string[] | null; sources: string[] | null; assetId: string | null; assetName: string | null; deviceType: string | null;
  vendor: string | null; kind: string | null; switchName: string | null; switchId: string | null; port: string | null; vlan: number | null; lastSeen: string | null; conflict: boolean;
}
interface Conflict { ip: string; macs: string[]; devices: { mac: string; vendor: string | null; assetId: string | null; assetName: string | null; port: string | null }[]; seenBy: string[] }

const FILTERS: [string, string][] = [
  ['', 'All Devices'], ['unknown', 'Unknown (Not in Inventory)'], ['known', 'Known'], ['new', 'New in 7 Days'], ['moved', 'Moved in 7 Days'],
  ['ipchanged', 'IP Changed in 7 Days'], ['unmanaged', 'Behind an Unmanaged Switch'], ['private', 'Randomised MAC (Phones, Laptops)'], ['stale', 'Not Seen for 7 Days'],
];
const KIND: Record<string, string> = { phone: 'IP phone', camera: 'Camera', printer: 'Printer', iot: 'IoT device', virtual: 'Virtual machine', network: 'Network equipment', private: 'Randomised MAC' };
const SOURCE_LABEL: Record<string, string> = { arp: 'ARP', inventory: 'Inventory', switch: 'Switch port' };
const kindLabel = (e: Endpoint) => e.assetId ? deviceTypeLabel(e.deviceType) ?? fmt.label(e.deviceType ?? 'device') : e.kind ? KIND[e.kind] ?? fmt.label(e.kind) : '—';

export function EndpointsTab() {
  const { can } = useAuth();
  const { query } = useRouter();
  const canManage = can('assets.manage');
  const [filter, setFilter] = useSticky('Endpoints.EndpointsTab.filter', query.get('filter') ?? '');
  const [q, setQ] = useSticky('Endpoints.EndpointsTab.q', query.get('q') ?? '');
  const [search, setSearch] = useSticky('Endpoints.EndpointsTab.search', query.get('q') ?? '');
  const [tick, setTick] = useState(0);
  const [picked, setPicked] = useState<string[]>([]);
  const [ack, setAck] = useState(false);
  const [vendorsOpen, setVendorsOpen] = useState(false);
  const data = useApi(() => api.get<Endpoint[]>('/network/endpoints', { filter, q: search }), [filter, search, tick]);
  const vendors = useApi(() => api.get<VendorCount[]>('/network/endpoints/vendors'), [tick]);
  const vstatus = useApi(() => api.get<VendorStatus>('/network/vendors/status'), [tick]);
  const rows = useMemo(() => (data.data ?? []).map((e) => ({ ...e, id: e.mac })), [data.data]);
  const toggle = (mac: string) => setPicked((p) => (p.includes(mac) ? p.filter((x) => x !== mac) : [...p, mac]));

  return (
    <>
      {vstatus.data && vstatus.data.prefixes < 1000 && (
        <Callout tone="warn" icon="alert">
          Device vendors are not available yet: the IEEE MAC address registry has not been downloaded ({vstatus.data.status?.error ?? 'the server downloads it weekly when it can reach standards-oui.ieee.org'}).
          {canManage && <> <button className="link-btn" onClick={() => setVendorsOpen(true)}>Update or import it</button>.</>}
        </Callout>
      )}
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q.trim()); }}>
        <input className="in grow" placeholder="Search a MAC address, IP address, vendor, device, switch or port…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search connected devices" />
        <select className="in" value={filter} onChange={(e) => { setFilter(e.target.value); setPicked([]); }} aria-label="Filter">
          {FILTERS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
        {canManage && picked.length > 0 && <button type="button" className="btn primary" onClick={() => setAck(true)}>Mark as Known ({picked.length})</button>}
        {canManage && <button type="button" className="btn" onClick={() => setVendorsOpen(true)} title="IEEE MAC address registry"><Icon name="retry" size={14} /> Vendor List</button>}
      </form>
      <ErrorBox error={data.error} />
      <div className="grid-2">
        <Panel flush title="Connected Devices" sub={`${rows.length.toLocaleString()} shown · every MAC address on an access port`}>
          <ChoosableTable tableId="network.endpoints" rows={rows} empty={data.loading ? 'Loading…' : 'No devices found on switch ports yet. They come from the MAC address tables of switches collected over SNMP.'} columns={[
            ...(canManage ? [{ key: 'x', label: '', fixed: true, render: (e: Endpoint) => <input type="checkbox" checked={picked.includes(e.mac)} onChange={() => toggle(e.mac)} aria-label={`Select ${e.mac}`} disabled={!e.unknown && !e.acknowledged} /> }] : []),
            { key: 'd', label: 'Device', render: (e) => e.assetId ? <Link to={`/network/devices/${e.assetId}`}><b>{e.assetName ?? e.ip ?? e.mac}</b></Link>
                : <><b>{e.acknowledged ? (e.note ?? 'Known device') : 'Unknown device'}</b>{e.acknowledged ? <span className="faint"> · marked by {e.ackBy}</span> : null}</> },
            { key: 'mac', label: 'MAC Address', render: (e) => <span className="mono">{e.mac}</span> },
            { key: 'ip', label: 'IP Address', render: (e) => <><span className="mono">{e.ip ?? '—'}</span>{e.prevIp && e.ipChangedAt && <div className="faint" style={{ fontSize: 11.5 }}>was {e.prevIp}</div>}</> },
            { key: 'v', label: 'Vendor', className: 'wrap', render: (e) => e.vendor ?? <span className="faint">Not registered</span> },
            { key: 'k', label: 'Type', render: (e) => kindLabel(e) },
            { key: 'sw', label: 'Switch · Port', render: (e) => <>{e.switchId ? <Link to={`/network/devices/${e.switchId}`}>{e.switchName}</Link> : e.switchName} <span className="mono">{e.port}</span>
                {e.behindUnmanaged && <div><Pill tone="warn">Behind unmanaged switch</Pill></div>}
                {e.movedAt && e.prevPort && <div className="faint" style={{ fontSize: 11.5 }}>moved {fmt.ago(e.movedAt)} from {e.prevSwitchName} {e.prevPort}</div>}</> },
            { key: 'vl', label: 'VLAN', render: (e) => e.vlan ?? '—' },
            { key: 'st', label: 'Status', render: (e) => e.unknown ? <Pill tone="warn">Unknown</Pill> : e.assetId ? <Pill tone="ok">In Inventory</Pill> : <Pill>Known</Pill> },
            { key: 'fs', label: 'First Seen', optional: true, render: (e) => e.baseline ? <span className="faint">before tracking</span> : fmt.date(e.firstSeen) },
            { key: 'ls', label: 'Last Seen', render: (e) => fmt.ago(e.lastSeen) },
          ]} />
        </Panel>
        <Panel title="Vendors" sub="Last 30 days">
          {(vendors.data ?? []).length === 0 ? <p className="faint">No data yet.</p> : (
            <DataTable rows={(vendors.data ?? []).map((v) => ({ ...v, id: v.vendor }))} onRow={(v) => setSearch(v.vendor === 'Not registered' ? '' : v.vendor)} rowTitle="Show these devices" columns={[
              { key: 'v', label: 'Vendor', className: 'wrap', render: (v) => v.vendor },
              { key: 'n', label: 'Devices', render: (v) => v.count },
              { key: 'u', label: 'Unknown', render: (v) => v.unknown > 0 ? <Pill tone="warn">{v.unknown}</Pill> : 0 },
            ]} />
          )}
          {vstatus.data && <p className="faint" style={{ fontSize: 12, margin: '10px 0 0' }}>{vstatus.data.prefixes.toLocaleString()} registered prefixes{vstatus.data.status?.updatedAt && <> · updated {fmt.ago(vstatus.data.status.updatedAt)}</>}</p>}
        </Panel>
      </div>
      {rows.length >= 5000 && <p className="faint" style={{ fontSize: 12 }}>Showing the first 5,000 devices. Narrow the search.</p>}
      {ack && <AckDialog macs={picked} onClose={() => setAck(false)} onDone={() => { setAck(false); setPicked([]); setTick((t) => t + 1); }} />}
      {vendorsOpen && <VendorsDialog status={vstatus.data} onClose={() => setVendorsOpen(false)} onDone={() => { setVendorsOpen(false); setTick((t) => t + 1); }} />}
    </>
  );
}

function AckDialog({ macs, onClose, onDone }: { macs: string[]; onClose: () => void; onDone: () => void }) {
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save(acknowledged: boolean) {
    setBusy(true); setErr(null);
    try { await api.post('/network/endpoints/acknowledge', { macs, note: note || null, acknowledged }); onDone(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Mark ${macs.length} Device(s) as Known`} onClose={onClose}
      footer={<><button className="btn" onClick={() => void save(false)} disabled={busy}>Mark as Unknown</button><span className="grow" /><button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn primary" onClick={() => void save(true)} disabled={busy}>{busy ? 'Saving…' : 'Mark as Known'}</button></>}>
      <div className="form-v">
        <p className="muted" style={{ margin: 0 }}>Known devices are not reported as unknown again. Devices in the inventory are always known.</p>
        <label>What is it? (optional)<input className="in" value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} placeholder="e.g. Meeting room display, badge reader" /></label>
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
      </div>
    </Modal>
  );
}

function VendorsDialog({ status, onClose, onDone }: { status: VendorStatus | null; onClose: () => void; onDone: () => void }) {
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'crit' | 'ok'; text: string } | null>(null);
  async function update() {
    setBusy(true); setMsg(null);
    try { const r = await api.post<{ prefixes: number }>('/network/vendors/update'); setMsg({ tone: 'ok', text: `${r.prefixes.toLocaleString()} prefixes loaded.` }); }
    catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  async function importFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    setBusy(true); setMsg(null);
    try {
      let total = 0;
      for (const f of Array.from(files)) total += (await api.post<{ prefixes: number }>('/network/vendors/import', { csv: await f.text() })).prefixes;
      setMsg({ tone: 'ok', text: `${total.toLocaleString()} prefixes imported.` });
    } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  return (
    <Modal title="MAC Address Vendors" onClose={onClose} footer={<button className="btn primary" onClick={onDone}>Done</button>}>
      <div className="form-v">
        <p className="muted" style={{ margin: 0 }}>
          Vendors come from the IEEE registration authority. The server downloads the registry every week when it can reach <span className="mono">standards-oui.ieee.org</span>;
          without internet access, download <span className="mono">oui.csv</span>, <span className="mono">mam.csv</span> and <span className="mono">oui36.csv</span> elsewhere and import them here.
        </p>
        {status && <div className="stat-line"><span><b>{status.prefixes.toLocaleString()}</b> prefixes</span>{status.status?.updatedAt && <span>Updated <b>{fmt.date(status.status.updatedAt)}</b> ({status.status.source})</span>}</div>}
        {status?.status?.error && <Callout tone="warn" icon="alert">{status.status.error}</Callout>}
        <div className="toolbar-row">
          <button className="btn primary" disabled={busy} onClick={() => void update()}><Icon name="retry" size={14} /> {busy ? 'Working…' : 'Download Now'}</button>
          <label className="btn" style={{ cursor: 'pointer' }}><Icon name="upload" size={14} /> Import CSV Files
            <input type="file" accept=".csv,text/csv" multiple hidden onChange={(e) => void importFiles(e.target.files)} disabled={busy} /></label>
        </div>
        {msg && <Callout tone={msg.tone === 'ok' ? 'ok' : 'crit'} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
      </div>
    </Modal>
  );
}

// ---- IP address management ------------------------------------------------------------------------------------

export function IpamTab() {
  const { query, navigate } = useRouter();
  const cidr = query.get('cidr');
  const subnets = useApi(() => api.get<Subnet[]>('/network/subnets'), []);
  const conflicts = useApi(() => api.get<Conflict[]>('/network/conflicts'), []);
  const rows = useMemo(() => (subnets.data ?? []).map((s) => ({ ...s, id: s.cidr })), [subnets.data]);
  const open = (c: string | null) => navigate(c ? `/network?tab=ipam&cidr=${encodeURIComponent(c)}` : '/network?tab=ipam', true);
  if (cidr) return <SubnetView cidr={cidr} onBack={() => open(null)} />;
  return (
    <>
      <ErrorBox error={subnets.error} />
      {(conflicts.data ?? []).length > 0 && (
        <Panel flush title="IP Address Conflicts" sub="One address, several devices (ARP tables of routers and switches)">
          <DataTable rows={(conflicts.data ?? []).map((c) => ({ ...c, id: c.ip }))} columns={[
            { key: 'ip', label: 'IP Address', render: (c) => <b className="mono">{c.ip}</b> },
            { key: 'd', label: 'Devices Using It', className: 'wrap', render: (c) => <div className="kv-list">{c.devices.map((d) => (
              <div key={d.mac}><span className="mono">{d.mac}</span> {d.assetId ? <Link to={`/network/devices/${d.assetId}`}>{d.assetName}</Link> : <span className="faint">{d.vendor ?? 'unknown vendor'}</span>}
                {d.port && d.port.trim() && <span className="faint"> · {d.port}</span>}</div>))}</div> },
            { key: 's', label: 'Seen By', className: 'wrap', render: (c) => <span className="faint">{c.seenBy.join(', ')}</span> },
          ]} />
        </Panel>
      )}
      <Panel flush title="Subnets" sub="From router and switch interfaces, and the discovery scopes">
        <DataTable rows={rows} onRow={(s) => open(s.cidr)} rowTitle="Show the addresses" empty={subnets.loading ? 'Loading…' : 'No subnets yet. They come from router and layer-3 switch interfaces (SNMP) and from the approved discovery scopes.'} columns={[
          { key: 'c', label: 'Subnet', render: (s) => <b className="mono">{s.cidr}</b> },
          { key: 'v', label: 'Interface · VLAN', className: 'wrap', render: (s) => <>{(s.via ?? []).join(', ') || <span className="faint">Discovery scope</span>}{s.vlans && s.vlans.length > 0 && <span className="faint"> · VLAN {s.vlans.join(', ')}</span>}</> },
          { key: 'g', label: 'Gateway', render: (s) => <span className="mono">{(s.gateways ?? []).join(', ') || '—'}</span> },
          { key: 'u', label: 'Used', render: (s) => <UseBar used={s.used} size={s.size} /> },
          { key: 'k', label: 'Known · Unknown', render: (s) => <>{s.known} · {s.unknown > 0 ? <Pill tone="warn">{s.unknown}</Pill> : 0}</> },
          { key: 'x', label: 'Conflicts', render: (s) => s.conflicts > 0 ? <Pill tone="crit">{s.conflicts}</Pill> : '—' },
          { key: 'l', label: 'Last Seen', render: (s) => (s.lastSeen ? fmt.ago(s.lastSeen) : '—') },
        ]} />
      </Panel>
    </>
  );
}

function UseBar({ used, size }: { used: number; size: number }) {
  const pct = size > 0 ? Math.min(100, Math.round((used / size) * 100)) : 0;
  return (
    <div style={{ minWidth: 130 }}>
      <div className="meter" title={`${used} of ${size}`}><i style={{ width: `${pct}%`, background: pct >= 90 ? 'var(--crit)' : pct >= 75 ? 'var(--warn)' : undefined }} /></div>
      <span style={{ fontSize: 12 }}>{used.toLocaleString()} of {size.toLocaleString()} ({pct}%)</span>
    </div>
  );
}

function SubnetView({ cidr, onBack }: { cidr: string; onBack: () => void }) {
  const [show, setShow] = useSticky<'all' | 'used' | 'free' | 'unknown' | 'conflict'>('Endpoints.SubnetView.show', 'all');
  const data = useApi(() => api.get<{ cidr: string; rows: IpRow[] }>('/network/subnets/ips', { cidr, all: true }), [cidr]);
  const all = data.data?.rows ?? [];
  const rows = all.filter((r) => show === 'all' || (show === 'used' && r.used) || (show === 'free' && !r.used) || (show === 'unknown' && r.used && !r.assetId) || (show === 'conflict' && r.conflict))
    .map((r) => ({ ...r, id: r.ip }));
  const used = all.filter((r) => r.used).length;
  return (
    <>
      <div className="toolbar-row">
        <button className="btn" onClick={onBack}><Icon name="chev" size={14} /> All Subnets</button>
        <b className="mono" style={{ fontSize: 15 }}>{cidr}</b>
        <span className="hint grow">{used.toLocaleString()} in use · {(all.length - used).toLocaleString()} free{all.length === used ? ' (only used addresses are listed for large networks)' : ''}</span>
        <select className="in" value={show} onChange={(e) => setShow(e.target.value as typeof show)} aria-label="Show">
          <option value="all">All Addresses</option><option value="used">In Use</option><option value="free">Free</option><option value="unknown">In Use, Not in Inventory</option><option value="conflict">Conflicts</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No addresses.'} columns={[
          { key: 'ip', label: 'IP Address', render: (r) => <b className="mono">{r.ip}</b> },
          { key: 's', label: 'Status', render: (r) => r.conflict ? <Pill tone="crit">Conflict</Pill> : !r.used ? <span className="faint">Free</span> : r.assetId ? <Pill tone="ok">In Use</Pill> : <Pill tone="warn">In Use · Unknown</Pill> },
          { key: 'd', label: 'Device', render: (r) => r.assetId ? <Link to={`/network/devices/${r.assetId}`}>{r.assetName}</Link> : r.used ? <span className="faint">{r.kind ? KIND[r.kind] ?? r.kind : 'Not in inventory'}</span> : null },
          { key: 'm', label: 'MAC Address', className: 'wrap', render: (r) => <span className="mono">{(r.macs ?? []).join(', ')}</span> },
          { key: 'v', label: 'Vendor', className: 'wrap', render: (r) => r.vendor ?? '' },
          { key: 'p', label: 'Switch · Port', render: (r) => r.switchName ? <>{r.switchId ? <Link to={`/network/devices/${r.switchId}`}>{r.switchName}</Link> : r.switchName} <span className="mono">{r.port}</span>{r.vlan ? <span className="faint"> · VLAN {r.vlan}</span> : null}</> : '' },
          { key: 'src', label: 'Seen In', render: (r) => (r.sources ?? []).map((x) => SOURCE_LABEL[x] ?? x).join(', ') },
          { key: 'l', label: 'Last Seen', render: (r) => (r.lastSeen ? fmt.ago(r.lastSeen) : '') },
        ]} />
      </Panel>
    </>
  );
}
