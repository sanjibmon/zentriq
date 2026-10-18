export interface NonItAsset {
  id: string; assetTag: string; barcode: string | null; name: string; categoryId: string | null; category: string | null; group: string | null;
  manufacturer: string | null; model: string | null; serialNumber: string | null; status: string; condition: string | null; quantity: number;
  location: string | null; departmentId: string | null; department: string | null; assignedTo: string | null;
  purchaseDate: string | null; vendor: string | null; purchaseCost: number | null; invoiceNumber: string | null; poNumber: string | null;
  warrantyUntil: string | null; amcVendor: string | null; amcUntil: string | null; insuranceUntil: string | null;
  lastMaintenance: string | null; nextMaintenance: string | null; usefulLifeYears: number | null; salvageValue: number | null;
  disposedOn: string | null; notes: string | null; custom: Record<string, string>; currentValue: number | null;
  createdAt: string; createdBy: string | null; updatedAt: string; updatedBy: string | null; rowVersion: number;
  history?: { at: string; actor: string | null; action: string; changes: Record<string, { from: unknown; to: unknown } | unknown> }[];
}

export interface NonItCategory { id: string; name: string; group: string; builtin: boolean; count: number }
export interface NonItField { id: string; key: string; label: string; type: 'text' | 'number' | 'date' | 'yesno' | 'list'; options: string[]; sort: number }
export interface KCount { key: string; count: number; id?: string; value?: number }

export interface NonItSummary {
  total: number; items: number; disposed: number; purchaseValue: number; currentValue: number; addedLast30: number;
  warranty90: number; amc90: number; insurance90: number; maintenance30: number; maintenanceOverdue: number; expired: number;
  byGroup: KCount[]; byCategory: KCount[]; byStatus: KCount[]; byCondition: KCount[]; byDepartment: KCount[]; byLocation: KCount[];
  upcoming: { id: string; assetTag: string; name: string; what: string; date: string }[];
}

export interface ImportResult {
  rows: number; created: number; updated: number; skipped: number; errors: { row: number; message: string }[];
  newCategories: string[]; unknownColumns: string[]; dryRun: boolean; ok: boolean;
}

export const STATUSES: [string, string, 'ok' | 'warn' | 'crit' | 'info' | ''][] = [
  ['in_use', 'In use', 'ok'], ['in_store', 'In store', 'info'], ['under_repair', 'Under repair', 'warn'], ['under_maintenance', 'Under maintenance', 'warn'],
  ['reserved', 'Reserved', 'info'], ['lost', 'Lost / missing', 'crit'], ['disposed', 'Disposed', ''],
];
export const CONDITIONS: [string, string][] = [['new', 'New'], ['good', 'Good'], ['fair', 'Fair'], ['poor', 'Poor'], ['damaged', 'Damaged']];
export const statusLabel = (s: string) => STATUSES.find(([k]) => k === s)?.[1] ?? s;
export const statusTone = (s: string) => STATUSES.find(([k]) => k === s)?.[2] ?? '';
export const money = (n: number | null | undefined) => (n == null ? '—' : n.toLocaleString(undefined, { maximumFractionDigits: 2 }));
export const day = (s: string | null | undefined) => (s ? new Date(`${s.slice(0, 10)}T00:00:00`).toLocaleDateString(undefined, { dateStyle: 'medium' }) : '—');
export function daysUntil(s: string | null | undefined): number | null {
  if (!s) return null;
  const d = new Date(`${s.slice(0, 10)}T00:00:00`).getTime();
  const t = new Date(); t.setHours(0, 0, 0, 0);
  return Math.round((d - t.getTime()) / 86400000);
}
