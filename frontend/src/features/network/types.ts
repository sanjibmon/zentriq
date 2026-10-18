export type Management = 'snmp' | 'web' | 'none';

export interface NetworkSummary {
  devices: number;
  byType: Record<string, number>;
  byManagement: Record<string, number>;
  byManufacturer: { name: string; count: number }[];
  switchesManaged: number; switchesWebOnly: number; switchesNoManagement: number; unmanagedSuspected: number;
  portsTotal: number; portsUp: number; portsAdminDown: number; portsWithErrors: number; portsHalfDuplex: number; portsPoe: number;
  poeBudgetWatts: number; poeUsedWatts: number; vlans: number; links: number; firmwareVersions: number; staleDevices: number; highCpu: number;
  highMemory: number; hotDevices: number; componentsBad: number; peersDown: number; routingPeers: number; busyPorts: number; blockingPorts: number;
  portsWithDiscards: number; transceivers: number;
  speeds: Record<string, number>;
}

export interface NetworkDevice {
  id: string; hostname: string | null; ip: string | null; deviceType: string; manufacturer: string | null; model: string | null;
  serialNumber: string | null; osName: string | null; firmware: string | null; location: string | null; lastBoot: string | null;
  management: Management; snmpVersion: string | null; portsTotal: number; portsUp: number; portsAdminDown: number;
  vlans: number; neighbors: number; macEntries: number; routing: boolean | null; routes: number | null; cpuPercent: number | null;
  poeBudgetWatts: number | null; poeUsedWatts: number | null; stpRoot: boolean | null; stackMembers: number | null;
  unmanagedPorts: number; edgePortsInUse: number; collectedAt: string | null; lastSeenAt: string; department: string | null;
  memoryPercent: number | null; memoryTotalMb: number | null; temperatureC: number | null; componentsBad: number; peersDown: number;
  busyPorts: number; routingPeers: number;
}

export interface ConnectedDevice { assetId: string; name: string | null; ip: string | null; mac: string; type: string | null }

export interface NetworkPort {
  assetId: string; deviceName: string | null; ifIndex: number; name: string | null; description: string | null; alias: string | null;
  type: string | null; admin: string | null; oper: string | null; speedMbps: number | null; duplex: string | null; vlan: number | null;
  trunk: boolean | null; poe: boolean | null; inErrors: number | null; outErrors: number | null; mac: string | null; ipv4: string[];
  macCount: number; connected: ConnectedDevice[]; neighbor: string | null; neighborAssetId: string | null; uplink: boolean; unmanagedSuspect: boolean;
  matches?: { mac: string; ip: string | null; vlan: number }[] | null;
  inBps?: number | null; outBps?: number | null; utilPercent?: number | null; inDiscards?: number | null; outDiscards?: number | null;
  stpState?: string | null; lagIfIndex?: number | null;
}

export interface NetworkLink {
  assetId: string; deviceName: string | null; deviceType?: string | null; protocol: 'lldp' | 'cdp'; localPort: string | null; remoteName: string | null; remotePort: string | null;
  remotePlatform: string | null; remoteDescription: string | null; remoteAddress: string | null; remoteChassisId: string | null;
  capabilities: string[]; remoteAssetId: string | null; remoteAssetName: string | null; remoteDeviceType: string | null; remoteManaged: boolean;
}

export interface NetworkVlan { id: number; names: string[] | null; devices: number; accessPorts: number; macs: number; deviceNames: string[] }

export interface UnmanagedPort {
  assetId: string; deviceName: string | null; ifIndex: number; port: string | null; alias: string | null; vlan: number | null; speedMbps: number | null;
  macCount: number; connected: ConnectedDevice[]; macs: { mac: string; ip: string | null }[] | null;
}
export interface UnmanagedData { inferred: UnmanagedPort[]; notManageable: NetworkDevice[] }

export interface FirmwareRow { manufacturer: string; model: string; firmware: string; count: number; devices: string[]; managed: number }

export interface AssetNetwork {
  device: NetworkDevice | null;
  interfaces: (Omit<NetworkPort, 'assetId' | 'deviceName'> & { mtu: number | null })[];
  neighbors: Pick<NetworkLink, 'protocol' | 'localPort' | 'remoteName' | 'remotePort' | 'remotePlatform' | 'remoteAddress' | 'capabilities' | 'remoteAssetId'>[];
  vlans: { id: number; name: string | null }[];
  modules: { class: string; name: string | null; model: string | null; serial: string | null; firmware: string | null }[];
  sensors: { name: string | null; type: string; value: number | null; unit: string | null; status: string | null }[];
  components: { kind: string; name: string | null; status: string | null }[];
  routingPeers: { protocol: string; address: string | null; remoteAs: number | null; state: string | null; up: boolean }[];
  connectedTo: { assetId: string; deviceName: string | null; port: string | null; ifIndex: number; vlan: number; mac: string; unmanagedSuspect: boolean }[];
}

export const MANAGEMENT: Record<Management, [string, 'ok' | 'warn' | '']> = {
  snmp: ['Managed (SNMP)', 'ok'],
  web: ['Web interface only', 'warn'],
  none: ['No management found', ''],
};

export function speedLabel(mbps: number | null | undefined): string {
  if (!mbps) return '—';
  if (mbps >= 1000) return `${mbps % 1000 === 0 ? mbps / 1000 : (mbps / 1000).toFixed(1)} Gbit/s`;
  return `${mbps} Mbit/s`;
}

export function portState(p: { admin: string | null; oper: string | null }): [string, 'ok' | 'warn' | 'crit' | ''] {
  if (p.admin === 'down') return ['Disabled', ''];
  if (p.oper === 'up') return ['Up', 'ok'];
  if (p.oper === 'dormant') return ['Dormant', 'warn'];
  return ['Down', 'warn'];
}

export function bpsLabel(bps: number | null | undefined): string {
  if (bps == null) return '—';
  if (bps >= 1e9) return `${(bps / 1e9).toFixed(1)} Gbit/s`;
  if (bps >= 1e6) return `${(bps / 1e6).toFixed(1)} Mbit/s`;
  if (bps >= 1e3) return `${Math.round(bps / 1e3)} kbit/s`;
  return `${bps} bit/s`;
}
