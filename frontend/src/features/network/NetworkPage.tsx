import { useMemo, useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart, ChartKindPicker, useChartKind } from '../../components/charts';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Panel, Pill } from '../../components/ui';
import { deviceTypeLabel } from '../assets/deviceTypes';
import { TopologyMap } from './Topology';
import {
  bpsLabel, MANAGEMENT, portState, speedLabel, type FirmwareRow, type Management, type NetworkDevice, type NetworkLink, type NetworkPort,
  type NetworkSummary, type NetworkVlan, type UnmanagedData,
} from './types';

type Tab = 'devices' | 'ports' | 'topology' | 'vlans' | 'unmanaged' | 'firmware';
const TABS: [Tab, string][] = [
  ['devices', 'Devices'], ['ports', 'Switch ports'], ['topology', 'Topology'], ['vlans', 'VLANs'], ['unmanaged', 'Unmanaged switches'], ['firmware', 'Firmware'],
];
const NET_TYPES = ['switch', 'router', 'firewall', 'access_point', 'wireless_controller', 'load_balancer', 'network'];
const typeName = (t: string | null | undefined) => deviceTypeLabel(t) ?? fmt.label(t ?? 'network');
const pct = (a: number, b: number) => (b ? Math.round((a / b) * 100) : 0);

export function NetworkPage() {
  const { query, navigate } = useRouter();
  const initial = (TABS.map(([t]) => t).find((t) => t === query.get('tab')) ?? 'devices') as Tab;
  const [tab, setTabState] = useState<Tab>(initial);
  const setTab = (t: Tab) => { setTabState(t); navigate(`/network?tab=${t}`); };
  const summary = useApi(() => api.get<NetworkSummary>('/network/summary'), []);
  const s = summary.data;

  return (
    <>
      <PageHead title="Network Inventory"
        actions={<>
          <Link className="btn" to="/reports">Reports</Link>
          <Link className="btn primary" to="/discovery?new=1">Scan network devices</Link>
        </>} />
      <ErrorBox error={summary.error} />
      {s && s.devices === 0 && (
        <Callout>
          No network devices yet. Run a <Link to="/discovery?new=1">discovery scan</Link> of your network ranges with an SNMP credential (SNMPv3 recommended, or v2c) — or leave
          <b> Identify printers, switches, routers…</b> on to try the read-only community <span className="mono">public</span>. Switches, routers and access points
          that the devices found report over LLDP/CDP are scanned too.
        </Callout>
      )}
      {s && (
        <Kpis items={[
          { label: 'Network devices', value: s.devices.toLocaleString(), sub: Object.entries(s.byType).sort((a, b) => b[1] - a[1]).slice(0, 3).map(([t, n]) => `${n} ${typeName(t).toLowerCase()}`).join(' · ') || '—', to: '/network?tab=devices' },
          { label: 'Managed switches', value: s.switchesManaged.toLocaleString(), sub: `${s.switchesWebOnly} web interface only · ${s.switchesNoManagement} no management` },
          { label: 'Unmanaged switches (suspected)', value: s.unmanagedSuspected.toLocaleString(), sub: 'several devices behind one access port', to: '/network?tab=unmanaged' },
          { label: 'Ports in use', value: `${pct(s.portsUp, s.portsTotal)}%`, sub: `${s.portsUp.toLocaleString()} of ${s.portsTotal.toLocaleString()} up · ${s.portsAdminDown} disabled`, to: '/network?tab=ports' },
          { label: 'PoE', value: s.poeBudgetWatts ? `${pct(s.poeUsedWatts, s.poeBudgetWatts)}%` : '—', sub: s.poeBudgetWatts ? `${s.poeUsedWatts} W of ${s.poeBudgetWatts} W · ${s.portsPoe} powered ports` : 'no PoE data' },
          { label: 'VLANs · links', value: `${s.vlans} · ${s.links}`, sub: 'VLANs · LLDP/CDP links', to: '/network?tab=topology' },
        ]} />
      )}
      {s && <Attention s={s} />}
      <div className="tabs" role="tablist">
        {TABS.map(([t, label]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{label}
            {t === 'unmanaged' && s && s.unmanagedSuspected > 0 && <span className="cnt">{s.unmanagedSuspected}</span>}
          </button>
        ))}
      </div>
      {tab === 'devices' && <DevicesTab summary={s} />}
      {tab === 'ports' && <PortsTab />}
      {tab === 'topology' && <TopologyTab />}
      {tab === 'vlans' && <VlansTab />}
      {tab === 'unmanaged' && <UnmanagedTab />}
      {tab === 'firmware' && <FirmwareTab />}
    </>
  );
}

/** What needs a look, each linked to the list behind it. */
function Attention({ s }: { s: NetworkSummary }) {
  const items: [number, string, string][] = [
    [s.componentsBad, 'fan, power supply or temperature alarm(s)', '/network?tab=devices'],
    [s.peersDown, 'BGP / OSPF neighbour(s) down', '/network?tab=devices'],
    [s.busyPorts, 'busy port(s) above 70 %', '/network?tab=ports&filter=busy'],
    [s.portsWithErrors, 'port(s) with errors', '/network?tab=ports&filter=errors'],
    [s.portsHalfDuplex, 'port(s) at half duplex', '/network?tab=ports&filter=halfduplex'],
    [s.blockingPorts, 'port(s) blocked by spanning tree', '/network?tab=ports&filter=blocking'],
    [s.highCpu, 'device(s) with CPU ≥ 80 %', '/network?tab=devices'],
    [s.highMemory, 'device(s) with memory ≥ 85 %', '/network?tab=devices'],
    [s.hotDevices, 'device(s) at 60 °C or more', '/network?tab=devices'],
    [s.staleDevices, 'device(s) not seen for 7 days', '/network?tab=devices'],
  ];
  const shown = items.filter(([n]) => n > 0);
  if (shown.length === 0) return null;
  return (
    <Callout tone="warn" icon="alert">
      {shown.map(([n, label, to], i) => <span key={label}>{i > 0 ? ' · ' : ''}<Link to={to}>{n} {label}</Link></span>)}
    </Callout>
  );
}

export function ManagementPill({ m }: { m: Management }) {
  const [label, tone] = MANAGEMENT[m] ?? [m, ''];
  return <Pill tone={tone}>{label}</Pill>;
}

function Meter({ value }: { value: number }) {
  return <span className="mini-meter" aria-hidden="true"><i style={{ width: `${Math.min(100, Math.max(0, value))}%` }} /></span>;
}

function DevicesTab({ summary }: { summary: NetworkSummary | null }) {
  const { query } = useRouter();
  const [type, setType] = useState(query.get('type') ?? '');
  const [mgmt, setMgmt] = useState(query.get('management') ?? '');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const data = useApi(() => api.get<NetworkDevice[]>('/network/devices', { type, management: mgmt, q: search }), [type, mgmt, search]);
  const rows = data.data ?? [];
  // Chart type per tile (share bar, bar, column, pie), remembered in this browser.
  const [typeKind, setTypeKind] = useChartKind('network.types', 'bar');
  const [mgmtKind, setMgmtKind] = useChartKind('network.management', 'share');
  const [vendorKind, setVendorKind] = useChartKind('network.vendors', 'bar');
  return (
    <>
      {summary && summary.devices > 0 && (
        <div className="grid-3">
          <Panel title="By type" right={<ChartKindPicker value={typeKind} onChange={setTypeKind} />}>
            <CategoryChart kind={typeKind} centerLabel="devices" data={Object.entries(summary.byType).map(([k, v]) => ({ key: k, label: typeName(k), value: v }))} onSelect={(x) => setType(x.key)} />
          </Panel>
          <Panel title="By management" sub="How each device can be inventoried" right={<ChartKindPicker value={mgmtKind} onChange={setMgmtKind} />}>
            <CategoryChart kind={mgmtKind} centerLabel="devices" data={(['snmp', 'web', 'none'] as Management[]).map((k) => ({ key: k, label: MANAGEMENT[k][0], value: summary.byManagement[k] ?? 0, color: k === 'snmp' ? 'var(--ok)' : k === 'web' ? 'var(--warn)' : 'var(--line-strong)' }))} onSelect={(x) => setMgmt(x.key)} />
          </Panel>
          <Panel title="By manufacturer" right={<ChartKindPicker value={vendorKind} onChange={setVendorKind} />}>
            <CategoryChart kind={vendorKind} centerLabel="devices" data={summary.byManufacturer.map((m) => ({ key: m.name, label: m.name, value: m.count }))} onSelect={(x) => { setQ(x.key === 'Unknown' ? '' : x.key); setSearch(x.key === 'Unknown' ? '' : x.key); }} />
          </Panel>
        </div>
      )}
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q); }}>
        <input className="in grow" placeholder="Search name, IP, manufacturer or model…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search network devices" />
        <select className="in" value={type} onChange={(e) => setType(e.target.value)} aria-label="Device type">
          <option value="">All types</option>
          {NET_TYPES.map((t) => <option key={t} value={t}>{typeName(t)}</option>)}
        </select>
        <select className="in" value={mgmt} onChange={(e) => setMgmt(e.target.value)} aria-label="Management">
          <option value="">Any management</option>
          {(['snmp', 'web', 'none'] as Management[]).map((m) => <option key={m} value={m}>{MANAGEMENT[m][0]}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No network devices match.'} columns={[
          { key: 'n', label: 'Device', render: (d) => <><Link to={`/assets/${d.id}?tab=network`}><b>{d.hostname ?? d.ip}</b></Link><div className="faint mono" style={{ fontSize: 11.5 }}>{d.ip}</div></> },
          { key: 't', label: 'Type', render: (d) => <>{typeName(d.deviceType)}{d.stackMembers && d.stackMembers > 1 ? <div className="faint" style={{ fontSize: 11.5 }}>stack of {d.stackMembers}</div> : null}</> },
          { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (d) => [d.manufacturer, d.model].filter(Boolean).join(' ') || '—' },
          { key: 'f', label: 'Firmware', className: 'wrap', render: (d) => <span className="mono" style={{ fontSize: 12 }}>{d.firmware ?? '—'}</span> },
          { key: 'g', label: 'Management', render: (d) => <><ManagementPill m={d.management} />{d.snmpVersion && <div className="faint" style={{ fontSize: 11.5 }}>SNMP {d.snmpVersion}</div>}</> },
          { key: 'p', label: 'Ports up', render: (d) => d.portsTotal ? <><span>{d.portsUp}/{d.portsTotal}</span> <Meter value={pct(d.portsUp, d.portsTotal)} /></> : '—' },
          { key: 'v', label: 'VLANs', render: (d) => d.vlans || '—' },
          { key: 'e', label: 'PoE', render: (d) => (d.poeBudgetWatts ? `${d.poeUsedWatts ?? 0}/${d.poeBudgetWatts} W` : '—') },
          { key: 'c', label: 'Health', render: (d) => <DeviceHealth d={d} /> },
          { key: 'u', label: 'Unmanaged behind', render: (d) => (d.unmanagedPorts ? <Link to="/network?tab=unmanaged"><Pill tone="warn">{d.unmanagedPorts}</Pill></Link> : '—') },
          { key: 's', label: 'Last seen', render: (d) => <span title={fmt.date(d.lastSeenAt)}>{fmt.ago(d.lastSeenAt)}</span> },
        ]} />
      </Panel>
      {rows.some((d) => d.management !== 'snmp') && (
        <p className="faint" style={{ fontSize: 12 }}>
          Devices without SNMP management show only what discovery could recognise (type, web interface). Add an SNMP credential (Settings → Credentials) and rescan to collect their ports,
          neighbours, VLANs and MAC address tables.
        </p>
      )}
    </>
  );
}

export function ConnectedCell({ p }: { p: Pick<NetworkPort, 'connected' | 'neighbor' | 'neighborAssetId' | 'macCount' | 'uplink' | 'unmanagedSuspect' | 'matches'> }) {
  const parts: ReactNode[] = [];
  if (p.neighbor) parts.push(<div key="n">{p.neighborAssetId ? <Link to={`/assets/${p.neighborAssetId}?tab=network`}>{p.neighbor}</Link> : p.neighbor} <span className="faint">(neighbour)</span></div>);
  if (!p.uplink || !p.neighbor) for (const c of p.connected.slice(0, p.uplink ? 2 : 4))
    parts.push(<div key={c.mac}><Link to={`/assets/${c.assetId}`}>{c.name ?? c.ip ?? c.mac}</Link>{c.ip && c.name !== c.ip ? <span className="faint mono"> {c.ip}</span> : null}</div>);
  for (const m of (p.matches ?? []).slice(0, 3)) parts.push(<div key={'m' + m.mac} className="mono" style={{ fontSize: 12 }}><mark>{m.mac}</mark>{m.ip ? ` ${m.ip}` : ''}</div>);
  const known = p.connected.length;
  if (p.macCount > known) parts.push(<div key="c" className="faint" style={{ fontSize: 11.5 }}>{p.macCount} device{p.macCount === 1 ? '' : 's'} learned{known ? `, ${known} in inventory` : ''}</div>);
  return parts.length ? <>{parts}</> : <span className="faint">—</span>;
}

function DeviceHealth({ d }: { d: NetworkDevice }) {
  const hot = (v: number | null, limit: number) => (v != null && v >= limit ? { color: 'var(--crit)', fontWeight: 600 } : undefined);
  const parts: ReactNode[] = [];
  if (d.cpuPercent != null) parts.push(<span key="c" style={hot(d.cpuPercent, 80)}>CPU {d.cpuPercent}%</span>);
  if (d.memoryPercent != null) parts.push(<span key="m" style={hot(d.memoryPercent, 85)}>Mem {d.memoryPercent}%</span>);
  if (d.temperatureC != null) parts.push(<span key="t" style={hot(d.temperatureC, 60)}>{Math.round(d.temperatureC)} °C</span>);
  return (
    <div style={{ fontSize: 12.5 }}>
      {parts.length ? parts.reduce<ReactNode[]>((acc, x, i) => (i ? [...acc, ' · ', x] : [x]), []) : <span className="faint">—</span>}
      {(d.componentsBad > 0 || d.peersDown > 0) && <div>{d.componentsBad > 0 && <Pill tone="crit">{d.componentsBad} alarm(s)</Pill>} {d.peersDown > 0 && <Pill tone="crit">{d.peersDown} peer(s) down</Pill>}</div>}
    </div>
  );
}

export function TrafficCell({ p }: { p: Pick<NetworkPort, 'inBps' | 'outBps' | 'utilPercent' | 'oper'> }) {
  if (p.oper !== 'up' || (p.inBps == null && p.outBps == null)) return <span className="faint">—</span>;
  const u = p.utilPercent ?? 0;
  return (
    <div style={{ fontSize: 12, minWidth: 120 }}>
      <div><span className="faint">in</span> {bpsLabel(p.inBps)} <span className="faint">out</span> {bpsLabel(p.outBps)}</div>
      {p.utilPercent != null && <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <span className="mini-meter" style={{ marginLeft: 0 }}><i style={{ width: `${Math.min(100, u)}%`, background: u >= 70 ? 'var(--crit)' : u >= 40 ? 'var(--warn)' : undefined }} /></span>
        <span style={u >= 70 ? { color: 'var(--crit)', fontWeight: 600 } : undefined}>{u}%</span>
      </div>}
    </div>
  );
}

function PortFlags({ p }: { p: NetworkPort }) {
  const errs = (p.inErrors ?? 0) + (p.outErrors ?? 0);
  return (
    <>
      {p.uplink && <Pill tone="info">Uplink</Pill>} {p.trunk && <Pill>Trunk</Pill>} {p.unmanagedSuspect && <Pill tone="warn">Unmanaged switch?</Pill>}
      {p.poe && <Pill tone="ok">PoE</Pill>} {errs > 0 && <Pill tone="crit">{errs.toLocaleString()} errors</Pill>}
      {p.oper === 'up' && p.duplex === 'half' && <Pill tone="warn">Half duplex</Pill>}
      {p.stpState === 'blocking' && <Pill tone="warn">STP blocking</Pill>} {p.lagIfIndex != null && <Pill>LAG member</Pill>}
      {((p.inDiscards ?? 0) + (p.outDiscards ?? 0)) > 0 && <Pill tone="warn">{((p.inDiscards ?? 0) + (p.outDiscards ?? 0)).toLocaleString()} discards</Pill>}
    </>
  );
}

const PORT_FILTERS: [string, string][] = [
  ['', 'All ports'], ['up', 'Connected (up)'], ['down', 'Not connected'], ['unused', 'Unused (no device ever learned)'], ['disabled', 'Disabled'],
  ['uplink', 'Uplinks'], ['unmanaged', 'Unmanaged switch suspected'], ['poe', 'PoE powered'], ['errors', 'With errors'], ['halfduplex', 'Half duplex'],
  ['busy', 'Busy (≥ 70 % of speed)'], ['blocking', 'Blocked by spanning tree'], ['discards', 'With discards'],
];

export function PortsTab({ assetId }: { assetId?: string }) {
  const { query } = useRouter();
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(query.get('q') ?? '');
  const [filter, setFilter] = useState(query.get('filter') ?? '');
  const data = useApi(() => api.get<NetworkPort[]>('/network/ports', { assetId, q: search, filter }), [assetId, search, filter]);
  const rows = useMemo(() => (data.data ?? []).map((p) => ({ ...p, id: `${p.assetId}:${p.ifIndex}` })), [data.data]);
  return (
    <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setSearch(q.trim()); }}>
        <input className="in grow" placeholder={assetId ? 'Filter by port, MAC address, IP address or device…' : 'Where is a device connected? Search a MAC address, IP address, device name or port…'} value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search ports" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Port filter">
          {PORT_FILTERS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : search || filter ? 'No ports match.' : 'No port data yet. Ports are collected from switches and routers over SNMP.'} columns={[
          ...(assetId ? [] : [{ key: 'd', label: 'Switch', render: (p: NetworkPort) => <Link to={`/assets/${p.assetId}?tab=network`}><b>{p.deviceName}</b></Link> }]),
          { key: 'p', label: 'Port', render: (p) => <><b className="mono">{p.name ?? p.ifIndex}</b>{(p.alias || (p.description && p.description !== p.name)) && <div className="faint" style={{ fontSize: 11.5 }}>{p.alias || p.description}</div>}</> },
          { key: 's', label: 'Status', render: (p) => { const [l, t] = portState(p); return <Pill tone={t}>{l}</Pill>; } },
          { key: 'v', label: 'Speed', render: (p) => <>{p.oper === 'up' ? speedLabel(p.speedMbps) : '—'}{p.oper === 'up' && p.duplex && p.duplex !== 'full' ? <span className="faint"> {p.duplex}</span> : null}</> },
          { key: 'l', label: 'VLAN', render: (p) => (p.trunk ? 'trunk' : p.vlan ?? '—') },
          { key: 't', label: 'Traffic', render: (p) => <TrafficCell p={p} /> },
          { key: 'c', label: 'Connected', className: 'wrap', render: (p) => <ConnectedCell p={p} /> },
          { key: 'f', label: '', render: (p) => <PortFlags p={p} /> },
        ]} />
      </Panel>
      {rows.length >= 3000 && <p className="faint" style={{ fontSize: 12 }}>Showing the first 3,000 ports. Narrow the search or open a switch.</p>}
    </>
  );
}

function TopologyTab() {
  const data = useApi(() => api.get<NetworkLink[]>('/network/neighbors'), []);
  const [view, setView] = useState<'map' | 'links'>('map');
  if (data.error) return <ErrorBox error={data.error} />;
  if (!data.data) return <Loading />;
  const rows = data.data.map((l, i) => ({ ...l, id: i }));
  if (rows.length === 0)
    return <Callout>No LLDP or CDP neighbours reported yet. Enable LLDP (or CDP) on your switches and routers and scan them with an SNMP credential to see how they are connected.</Callout>;
  return (
    <>
      <div className="toolbar-row">
        <div className="seg" role="group" aria-label="View">
          <button className={view === 'map' ? 'on' : ''} onClick={() => setView('map')}>Map</button>
          <button className={view === 'links' ? 'on' : ''} onClick={() => setView('links')}>Links</button>
        </div>
        <span className="faint" style={{ fontSize: 12.5 }}>{rows.length} link(s) reported over LLDP / CDP</span>
      </div>
      {view === 'map' ? <Panel><TopologyMap links={data.data} /></Panel> : (
        <Panel flush>
          <DataTable rows={rows} columns={[
            { key: 'd', label: 'Device', render: (l) => <Link to={`/assets/${l.assetId}?tab=network`}><b>{l.deviceName}</b></Link> },
            { key: 'lp', label: 'Local port', render: (l) => <span className="mono">{l.localPort ?? '—'}</span> },
            { key: 'r', label: 'Neighbour', className: 'wrap', render: (l) => <>{l.remoteAssetId ? <Link to={`/assets/${l.remoteAssetId}?tab=network`}>{l.remoteAssetName ?? l.remoteName}</Link> : (l.remoteName ?? l.remoteChassisId ?? '—')}
              {!l.remoteAssetId && <Pill tone="warn">Not in inventory</Pill>}{l.remotePlatform && <div className="faint" style={{ fontSize: 11.5 }}>{l.remotePlatform}</div>}</> },
            { key: 'rp', label: 'Neighbour port', render: (l) => <span className="mono">{l.remotePort ?? '—'}</span> },
            { key: 'a', label: 'Address', render: (l) => <span className="mono">{l.remoteAddress ?? '—'}</span> },
            { key: 'c', label: 'Capabilities', render: (l) => l.capabilities.map(fmt.label).join(', ') || '—' },
            { key: 'p', label: 'Protocol', render: (l) => l.protocol.toUpperCase() },
          ]} />
        </Panel>
      )}
    </>
  );
}

function VlansTab() {
  const data = useApi(() => api.get<NetworkVlan[]>('/network/vlans'), []);
  return (
    <>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} empty={data.loading ? 'Loading…' : 'No VLANs collected yet (Q-BRIDGE-MIB or Cisco VTP over SNMP).'} columns={[
          { key: 'i', label: 'VLAN', render: (v) => <b className="mono">{v.id}</b> },
          { key: 'n', label: 'Name', className: 'wrap', render: (v) => v.names?.join(' / ') || '—' },
          { key: 'd', label: 'Switches', render: (v) => v.devices },
          { key: 'a', label: 'Access ports', render: (v) => v.accessPorts },
          { key: 'm', label: 'Devices (MACs)', render: (v) => v.macs.toLocaleString() },
          { key: 'l', label: 'On', className: 'wrap', render: (v) => <span className="faint" style={{ fontSize: 12 }}>{v.deviceNames.join(', ')}</span> },
        ]} />
      </Panel>
    </>
  );
}

function UnmanagedTab() {
  const data = useApi(() => api.get<UnmanagedData>('/network/unmanaged'), []);
  if (data.error) return <ErrorBox error={data.error} />;
  if (!data.data) return <Loading />;
  const { inferred, notManageable } = data.data;
  return (
    <>
      <Panel flush title={`Suspected unmanaged switches (${inferred.length})`} sub="Two or more devices behind one access port, with no neighbour and no trunk">
        <DataTable rows={inferred.map((u) => ({ ...u, id: `${u.assetId}:${u.ifIndex}` }))} empty="None found. Every access port has at most one device." columns={[
          { key: 'd', label: 'Managed switch · port', render: (u) => <><Link to={`/assets/${u.assetId}?tab=network`}><b>{u.deviceName}</b></Link> <span className="mono">{u.port}</span>{u.alias && <div className="faint" style={{ fontSize: 11.5 }}>{u.alias}</div>}</> },
          { key: 'v', label: 'VLAN', render: (u) => u.vlan ?? '—' },
          { key: 'n', label: 'Devices behind', render: (u) => <Pill tone="warn">{u.macCount}</Pill> },
          { key: 'k', label: 'Known devices', className: 'wrap', render: (u) => u.connected.length ? u.connected.map((c) => <div key={c.mac}><Link to={`/assets/${c.assetId}`}>{c.name ?? c.ip}</Link> <span className="faint">{c.type ? typeName(c.type) : ''}</span></div>) : <span className="faint">none in inventory</span> },
          { key: 'm', label: 'MAC addresses', className: 'wrap', render: (u) => <span className="mono" style={{ fontSize: 11.5 }}>{(u.macs ?? []).slice(0, 8).map((m) => m.ip ? `${m.mac} (${m.ip})` : m.mac).join(', ')}{(u.macs?.length ?? 0) > 8 ? ' …' : ''}</span> },
        ]} />
      </Panel>
      <Panel flush title={`Switches without SNMP management (${notManageable.length})`} sub="Discovered, but ports and connections could not be read">
        <DataTable rows={notManageable} empty="All discovered switches are managed over SNMP." columns={[
          { key: 'n', label: 'Device', render: (d) => <><Link to={`/assets/${d.id}`}><b>{d.hostname ?? d.ip}</b></Link><div className="faint mono" style={{ fontSize: 11.5 }}>{d.ip}</div></> },
          { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (d) => [d.manufacturer, d.model].filter(Boolean).join(' ') || '—' },
          { key: 'g', label: 'Management', render: (d) => <ManagementPill m={d.management} /> },
          { key: 'h', label: 'What to do', className: 'wrap', render: (d) => <span className="faint" style={{ fontSize: 12 }}>{d.management === 'web'
            ? 'Smart / web-managed switch: enable SNMP on it (v3 recommended) and add the credential to the scan.'
            : 'No management interface answered: check SNMP is enabled and allowed from the Zentriq server.'}</span> },
          { key: 's', label: 'Last seen', render: (d) => fmt.ago(d.lastSeenAt) },
        ]} />
      </Panel>
    </>
  );
}

function FirmwareTab() {
  const data = useApi(() => api.get<FirmwareRow[]>('/network/firmware'), []);
  const rows = (data.data ?? []).map((r, i) => ({ ...r, id: i }));
  const models = new Map<string, number>();
  for (const r of rows) models.set(`${r.manufacturer}|${r.model}`, (models.get(`${r.manufacturer}|${r.model}`) ?? 0) + 1);
  return (
    <>
      <ErrorBox error={data.error} />
      <Panel flush sub="Different firmware on the same model is highlighted — usually a device to update">
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No network devices yet.'} columns={[
          { key: 'm', label: 'Manufacturer / model', className: 'wrap', render: (r) => <><b>{r.manufacturer}</b> {r.model}</> },
          { key: 'f', label: 'Firmware / software version', className: 'wrap', render: (r) => <><span className="mono">{r.firmware}</span> {(models.get(`${r.manufacturer}|${r.model}`) ?? 0) > 1 && <Pill tone="warn">mixed versions</Pill>}</> },
          { key: 'c', label: 'Devices', render: (r) => r.count },
          { key: 'd', label: 'Names', className: 'wrap', render: (r) => <span className="faint" style={{ fontSize: 12 }}>{r.devices.filter(Boolean).join(', ')}</span> },
        ]} />
      </Panel>
    </>
  );
}
