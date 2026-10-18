import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, fmt, Panel, Pill } from '../../components/ui';
import { ConnectedCell, ManagementPill, TrafficCell } from './NetworkPage';
import { portState, speedLabel, type AssetNetwork as AssetNetworkData } from './types';

const yesNo = (b: boolean | null | undefined) => (b == null ? '—' : b ? 'Yes' : 'No');

function KV({ items }: { items: [string, ReactNode][] }) {
  return <dl className="kv-grid">{items.map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v ?? '—'}</dd></div>)}</dl>;
}

/**
 * Network section of a device page: the switch port(s) this device is connected to (for every device) and,
 * for switches, routers, firewalls and access points, their ports, neighbours, VLANs and modules.
 */
export function AssetNetwork({ assetId }: { assetId: string }) {
  const data = useApi(() => api.get<AssetNetworkData>(`/assets/${assetId}/network`), [assetId]);
  const [all, setAll] = useState(false);
  const n = data.data;
  if (!n) return null;
  const dev = n.device;
  const physical = n.interfaces.filter((i) => i.type === 'ethernet' || i.type === 'wireless' || i.type === 'lag');
  const ports = all ? n.interfaces : physical;

  return (
    <>
      {n.connectedTo.length > 0 && (
        <Panel title="Connected to" sub="Switch port where this device's MAC address was learned">
          {n.connectedTo.map((c) => (
            <div key={`${c.assetId}:${c.ifIndex}:${c.mac}`} style={{ marginBottom: 4 }}>
              <Link to={`/assets/${c.assetId}?tab=network`}><b>{c.deviceName}</b></Link> port <b className="mono">{c.port ?? c.ifIndex}</b>
              {c.vlan ? <span className="faint"> · VLAN {c.vlan}</span> : null} <span className="faint mono"> · {c.mac}</span>
              {c.unmanagedSuspect && <> <Pill tone="warn">through an unmanaged switch</Pill></>}
            </div>
          ))}
        </Panel>
      )}
      {dev && (
        <>
          <div className="grid-2">
            <Panel title="Network device">
              <KV items={[
                ['Management', <><ManagementPill m={dev.management} />{dev.snmpVersion ? <span className="faint"> SNMP {dev.snmpVersion}</span> : null}</>],
                ['Firmware / software', <span className="mono">{dev.firmware ?? '—'}</span>],
                ['Ports', dev.portsTotal ? `${dev.portsUp} up of ${dev.portsTotal}${dev.portsAdminDown ? ` · ${dev.portsAdminDown} disabled` : ''}` : null],
                ['VLANs', dev.vlans || null], ['MAC addresses learned', dev.macEntries ? dev.macEntries.toLocaleString() : null],
                ['IP routing', yesNo(dev.routing)], ...(dev.routes ? [['Routes', dev.routes.toLocaleString()] as [string, ReactNode]] : []),
                ['PoE', dev.poeBudgetWatts ? `${dev.poeUsedWatts ?? 0} W used of ${dev.poeBudgetWatts} W` : null],
                ['CPU', dev.cpuPercent != null ? `${dev.cpuPercent}%` : null],
                ['Memory', dev.memoryPercent != null ? `${dev.memoryPercent}% used${dev.memoryTotalMb ? ` of ${dev.memoryTotalMb >= 1024 ? `${(dev.memoryTotalMb / 1024).toFixed(1)} GB` : `${dev.memoryTotalMb} MB`}` : ''}` : null],
                ['Temperature', dev.temperatureC != null ? `${Math.round(dev.temperatureC)} °C` : null],
                ['Up since', dev.lastBoot ? `${fmt.date(dev.lastBoot)} (${fmt.ago(dev.lastBoot).replace(' ago', '')})` : null],
                ['Spanning-tree root', yesNo(dev.stpRoot)], ...(dev.stackMembers && dev.stackMembers > 1 ? [['Stack members', dev.stackMembers] as [string, ReactNode]] : []),
                ['Collected', fmt.date(dev.collectedAt)],
              ]} />
              {dev.management !== 'snmp' && <p className="faint" style={{ fontSize: 12 }}>Ports, neighbours and VLANs are read over SNMP. Enable SNMP on this device and scan it with an SNMP credential.</p>}
            </Panel>
            {n.neighbors.length > 0 && (
              <Panel title="Neighbours" sub="LLDP / CDP" flush>
                <DataTable rows={n.neighbors.map((x, i) => ({ ...x, id: i }))} columns={[
                  { key: 'l', label: 'Port', render: (x) => <span className="mono">{x.localPort ?? '—'}</span> },
                  { key: 'r', label: 'Neighbour', className: 'wrap', render: (x) => <>{x.remoteAssetId ? <Link to={`/assets/${x.remoteAssetId}?tab=network`}>{x.remoteName}</Link> : x.remoteName ?? '—'}
                    {x.remotePlatform && <div className="faint" style={{ fontSize: 11.5 }}>{x.remotePlatform}</div>}</> },
                  { key: 'p', label: 'Their port', render: (x) => <span className="mono">{x.remotePort ?? '—'}</span> },
                  { key: 'a', label: 'Address', render: (x) => <span className="mono">{x.remoteAddress ?? '—'}</span> },
                  { key: 'c', label: '', render: (x) => <span className="faint">{x.protocol.toUpperCase()}</span> },
                ]} />
              </Panel>
            )}
          </div>
          {n.interfaces.length > 0 && (
            <Panel flush title={`Ports (${physical.length})`} right={n.interfaces.length > physical.length && (
              <label className="row-check"><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> <span>Show all {n.interfaces.length} interfaces (VLAN, tunnel, loopback)</span></label>)}>
              <DataTable rows={ports.map((p) => ({ ...p, id: p.ifIndex }))} columns={[
                { key: 'p', label: 'Port', render: (p) => <><b className="mono">{p.name ?? p.ifIndex}</b>{(p.alias || (p.description && p.description !== p.name)) && <div className="faint" style={{ fontSize: 11.5 }}>{p.alias || p.description}</div>}</> },
                { key: 's', label: 'Status', render: (p) => { const [l, t] = portState(p); return <Pill tone={t}>{l}</Pill>; } },
                { key: 'v', label: 'Speed', render: (p) => <>{p.oper === 'up' ? speedLabel(p.speedMbps) : '—'}{p.oper === 'up' && p.duplex && p.duplex !== 'full' ? <span className="faint"> {p.duplex}</span> : null}</> },
                { key: 'l', label: 'VLAN', render: (p) => (p.trunk ? 'trunk' : p.vlan ?? '—') },
                { key: 't', label: 'Traffic', render: (p) => <TrafficCell p={p} /> },
                { key: 'i', label: 'IP', render: (p) => <span className="mono">{p.ipv4.join(', ') || ''}</span> },
                { key: 'c', label: 'Connected', className: 'wrap', render: (p) => <ConnectedCell p={p} /> },
                { key: 'f', label: '', render: (p) => <>{p.uplink && <Pill tone="info">Uplink</Pill>} {p.unmanagedSuspect && <Pill tone="warn">Unmanaged switch?</Pill>} {p.poe && <Pill tone="ok">PoE</Pill>}
                  {((p.inErrors ?? 0) + (p.outErrors ?? 0)) > 0 && <Pill tone="crit">{((p.inErrors ?? 0) + (p.outErrors ?? 0)).toLocaleString()} errors</Pill>}
                  {p.stpState === 'blocking' && <Pill tone="warn">STP blocking</Pill>} {p.lagIfIndex != null && <Pill>LAG member</Pill>}</> },
              ]} />
            </Panel>
          )}
          {(n.sensors.length > 0 || n.components.length > 0 || n.routingPeers.length > 0) && (
            <div className="grid-2">
              {(n.sensors.length > 0 || n.components.length > 0) && (
                <Panel flush title="Health" sub="Sensors, fans and power supplies">
                  <DataTable rows={[
                    ...n.components.map((c, i) => ({ id: `c${i}`, kind: fmt.label(c.kind), name: c.name, value: '', status: c.status })),
                    ...n.sensors.filter((x) => x.type !== 'optical').map((x, i) => ({ id: `s${i}`, kind: fmt.label(x.type), name: x.name, value: x.value != null ? `${x.value} ${x.unit ?? ''}` : '', status: x.status })),
                  ]} columns={[
                    { key: 'k', label: 'Kind', render: (r) => r.kind },
                    { key: 'n', label: 'Name', className: 'wrap', render: (r) => r.name ?? '—' },
                    { key: 'v', label: 'Reading', render: (r) => <span className="num">{r.value || '—'}</span> },
                    { key: 's', label: 'State', render: (r) => r.status ? <Pill tone={r.status === 'normal' || r.status === 'ok' ? 'ok' : r.status === 'notPresent' ? '' : 'crit'}>{fmt.label(r.status)}</Pill> : '—' },
                  ]} />
                </Panel>
              )}
              {n.routingPeers.length > 0 && (
                <Panel flush title={`Routing neighbours (${n.routingPeers.length})`} sub="BGP and OSPF">
                  <DataTable rows={n.routingPeers.map((x, i) => ({ ...x, id: i }))} columns={[
                    { key: 'p', label: 'Protocol', render: (x) => x.protocol.toUpperCase() },
                    { key: 'a', label: 'Neighbour', render: (x) => <span className="mono">{x.address ?? '—'}</span> },
                    { key: 'r', label: 'Remote AS', render: (x) => x.remoteAs ?? '—' },
                    { key: 's', label: 'State', render: (x) => <Pill tone={x.up ? 'ok' : 'crit'}>{fmt.label(x.state)}</Pill> },
                  ]} />
                </Panel>
              )}
            </div>
          )}
          {n.sensors.some((x) => x.type === 'optical') && (
            <Panel flush title="Optical levels" sub="Transceiver receive / transmit power">
              <DataTable rows={n.sensors.filter((x) => x.type === 'optical').map((x, i) => ({ ...x, id: i }))} columns={[
                { key: 'n', label: 'Sensor', className: 'wrap', render: (x) => x.name ?? '—' },
                { key: 'v', label: 'Level', render: (x) => <span className="num">{x.value != null ? `${x.value} dBm` : '—'}</span> },
                { key: 's', label: 'State', render: (x) => x.status ? <Pill tone={x.status === 'ok' ? 'ok' : 'crit'}>{fmt.label(x.status)}</Pill> : '—' },
              ]} />
            </Panel>
          )}
          <div className="grid-2">
            {n.vlans.length > 0 && (
              <Panel flush title={`VLANs (${n.vlans.length})`}>
                <DataTable rows={n.vlans} columns={[
                  { key: 'i', label: 'VLAN', render: (v) => <b className="mono">{v.id}</b> },
                  { key: 'n', label: 'Name', render: (v) => v.name ?? '—' },
                  { key: 'p', label: 'Access ports', render: (v) => physical.filter((p) => p.vlan === v.id && !p.trunk).length },
                ]} />
              </Panel>
            )}
            {n.modules.length > 0 && (
              <Panel flush title="Modules, transceivers, power supplies & fans" sub="From the device's hardware inventory">
                <DataTable rows={n.modules.map((m, i) => ({ ...m, id: i }))} columns={[
                  { key: 'c', label: 'Kind', render: (m) => fmt.label(m.class) },
                  { key: 'n', label: 'Name / model', className: 'wrap', render: (m) => [m.name, m.model].filter(Boolean).join(' · ') || '—' },
                  { key: 's', label: 'Serial', render: (m) => <span className="mono">{m.serial ?? '—'}</span> },
                  { key: 'f', label: 'Firmware', render: (m) => <span className="mono">{m.firmware ?? '—'}</span> },
                ]} />
              </Panel>
            )}
          </div>
        </>
      )}
    </>
  );
}
