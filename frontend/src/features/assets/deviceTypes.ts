/** Device types (same keys as Zentriq.Inventory.DeviceTypes on the server). */
export const DEVICE_TYPES: { key: string; label: string; group: string }[] = [
  { key: 'desktop', label: 'Desktop', group: 'Computers' },
  { key: 'laptop', label: 'Laptop', group: 'Computers' },
  { key: 'tablet', label: 'Tablet', group: 'Computers' },
  { key: 'mobile', label: 'Mobile device', group: 'Computers' },
  { key: 'server', label: 'Physical server', group: 'Computers' },
  { key: 'virtual', label: 'Virtual machine', group: 'Computers' },
  { key: 'hypervisor', label: 'Hypervisor host', group: 'Computers' },
  { key: 'storage', label: 'Storage / NAS', group: 'Computers' },
  { key: 'switch', label: 'Network switch', group: 'Network' },
  { key: 'router', label: 'Router', group: 'Network' },
  { key: 'firewall', label: 'Firewall', group: 'Network' },
  { key: 'access_point', label: 'Wireless access point', group: 'Network' },
  { key: 'wireless_controller', label: 'Wireless controller', group: 'Network' },
  { key: 'load_balancer', label: 'Load balancer', group: 'Network' },
  { key: 'network', label: 'Network device', group: 'Network' },
  { key: 'printer', label: 'Printer', group: 'Printing' },
  { key: 'mfp', label: 'Multifunction printer', group: 'Printing' },
  { key: 'scanner', label: 'Scanner', group: 'Printing' },
  { key: 'ups', label: 'UPS / power', group: 'Other devices' },
  { key: 'ip_camera', label: 'IP camera / NVR', group: 'Other devices' },
  { key: 'ip_phone', label: 'IP phone', group: 'Other devices' },
  { key: 'iot', label: 'IoT device', group: 'Other devices' },
  { key: 'other', label: 'Other', group: 'Other devices' },
];

const LABEL: Record<string, string> = Object.fromEntries(DEVICE_TYPES.map((t) => [t.key, t.label]));

export function deviceTypeLabel(key: string | null | undefined): string | null {
  return key ? LABEL[key] ?? null : null;
}

export const DEVICE_TYPE_GROUPS = [...new Set(DEVICE_TYPES.map((t) => t.group))];
