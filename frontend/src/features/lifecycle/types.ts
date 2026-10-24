export type Stage = 'planned' | 'on_order' | 'in_stock' | 'in_use' | 'in_maintenance' | 'missing' | 'retired' | 'disposed';

export interface LcAsset {
  id: string; assetTag: string | null; name: string | null; hostname: string | null; serial: string | null; manufacturer: string | null; model: string | null;
  class: string; classLabel: string | null; deviceType: string | null; category: string; osName: string | null; assetStatus: string;
  stage: Stage; status: string; stageChangedAt: string; stageReason: string | null; awaitingFirstSighting: boolean;
  assignedTo: string | null; assignedEmail: string | null; assignedAt: string | null; expectedReturn: string | null;
  departmentId: string | null; department: string | null; locationId: string | null; location: string | null;
  acquisition: string; purchaseDate: string | null; inServiceDate: string | null; purchaseCost: number | null; currency: string | null; vendor: string | null;
  invoiceNumber: string | null; poNumber: string | null; depreciationMethod: string | null; usefulLifeMonths: number | null; salvageValue: number | null;
  depreciationCustom: boolean; bookValue: number | null; lessor: string | null; leaseStart: string | null; leaseEnd: string | null; leaseMonthlyCost: number | null;
  warrantyProvider: string | null; warrantyType: string | null; warrantyStart: string | null; warrantyEnd: string | null; warrantyDays: number | null;
  disposedOn: string | null; disposalMethod: string | null; disposalVendor: string | null; disposalValue: number | null;
  sanitizationMethod: string | null; sanitizedBy: string | null; sanitizedOn: string | null; certificateRef: string | null;
  notes: string | null; updatedAt: string; updatedBy: string | null; rowVersion: number; ageMonths: number | null; refreshYears: number; refreshDue: boolean;
  lastSeenAt: string; firstSeenAt: string; source: string;
}

export interface LcEvent {
  id: number; at: string; actor: string | null; kind: string; fromStage: string | null; toStage: string | null; fromStatus: string | null; toStatus: string | null;
  person: string | null; reason: string | null; detail?: Record<string, unknown>; assetId?: string; assetTag?: string | null; name?: string | null;
}

export interface LcContract {
  id: string; name: string; kind: string; vendor: string | null; reference: string | null; startDate: string | null; endDate: string | null;
  cost?: number | null; currency?: string | null; notifyDays?: number; notes?: string | null; daysLeft?: number | null; assets?: number;
}

export interface LcDetail {
  asset: LcAsset; events: LcEvent[]; contracts: LcContract[]; osEndOfSupport: boolean | null; next: Stage[];
  schedule: { year: number; opening: number; depreciation: number; closing: number }[] | null;
}

export interface LcPolicy { class: string; label: string; tagPrefix: string; usefulLifeMonths: number; depreciationMethod: string; salvagePercent: number; refreshYears: number }

export interface LcMeta {
  stages: { id: Stage; label: string; statuses: { id: string; label: string }[]; next: Stage[] }[];
  policies: LcPolicy[]; currency: string; notSeenDays: number; autoTag: boolean;
  sanitizationMethods: { id: string; label: string }[]; depreciationMethods: { id: string; label: string }[];
  contractKinds: string[]; acquisitions: string[];
}

export interface LcSummary {
  stages: Partial<Record<Stage, number>>; total: number; purchaseValue: number; bookValue: number; withCost: number;
  warranty30: number; warranty90: number; warrantyExpired: number; noWarranty: number; contracts90: number; loansOverdue: number;
  refreshDue: number; unassigned: number; untagged: number; currency: string;
  byClass: { label: string; class: string; n: number; bookValue: number }[];
  recent: LcEvent[];
}

export interface LcPerson { person: string; email: string | null; department: string | null; assets: number; loans: number; overdue: number; value: number; accessories: number }

export interface LcRefresh {
  candidates: { id: string; assetTag: string | null; name: string | null; model: string | null; classLabel: string | null; assignedTo: string | null; osName: string | null;
    ageMonths: number | null; warrantyEnd: string | null; dueOn: string | null; purchaseCost: number | null; bookValue: number | null; reasons: string[] }[];
  forecast: { year: number; devices: number; estimatedCost: number; withoutCost: number }[];
}

export const STAGE_TONE: Record<Stage, '' | 'ok' | 'info' | 'warn' | 'crit'> = {
  planned: '', on_order: 'info', in_stock: 'info', in_use: 'ok', in_maintenance: 'warn', missing: 'crit', retired: '', disposed: '',
};

export const STAGE_ORDER: Stage[] = ['planned', 'on_order', 'in_stock', 'in_use', 'in_maintenance', 'missing', 'retired', 'disposed'];

export function stageLabel(meta: LcMeta | null | undefined, stage: string | null | undefined): string {
  if (!stage) return '—';
  return meta?.stages.find((s) => s.id === stage)?.label ?? stage.replace(/_/g, ' ');
}
export function statusLabel(meta: LcMeta | null | undefined, status: string | null | undefined): string {
  if (!status) return '—';
  for (const s of meta?.stages ?? []) { const x = s.statuses.find((y) => y.id === status); if (x) return x.label; }
  return status.replace(/_/g, ' ');
}

export function money(n: number | null | undefined, currency = 'INR'): string {
  if (n == null) return '—';
  try { return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: 0 }).format(n); }
  catch { return `${currency} ${Math.round(n).toLocaleString()}`; }
}

export const KIND_LABEL: Record<string, string> = {
  created: 'Added', stage: 'Stage changed', assigned: 'Assigned', returned: 'Returned', updated: 'Details updated', disposed: 'Disposed',
  auto: 'Automatic', seen_again: 'Seen again after retirement', contract: 'Contract',
};

export const CONTRACT_KIND: Record<string, string> = {
  warranty: 'Warranty', amc: 'AMC', support: 'Support', lease: 'Lease', insurance: 'Insurance', other: 'Other',
};
