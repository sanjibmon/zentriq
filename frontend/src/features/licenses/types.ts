export type Metric = 'device' | 'user' | 'core' | 'concurrent' | 'site';
export type LicenseType = 'perpetual' | 'subscription' | 'volume' | 'oem' | 'retail' | 'enterprise' | 'trial' | 'freeware' | 'open_source';
export type Status = 'compliant' | 'shortfall' | 'unlicensed' | 'over_licensed' | 'not_measured' | 'not_required' | 'unused';

export interface Compliance {
  id: string; name: string; publisher: string | null; category: string; metric: Metric; kind: 'installed' | 'cloud'; needsLicense: boolean; webAppId: string | null;
  cloudSkus: string[]; devices: number; users: number; cores: number; cloudConsumed: number | null; cloudUnits: number | null; accounts: number | null;
  licenses: number; validLicenses: number; unlimited: boolean; entitled: number; nextEnd: string | null; cloudNextLifecycle: string | null;
  cost: number | null; unitCost: number | null; expiring: number; allocations: number; consumed: number | null; status: Status;
  shortfall: number; unused: number; shortfallCost: number | null; unusedValue: number | null;
}

export interface Renewal { id: string; name: string; product: string; productId: string; endDate: string; autoRenew: boolean; licenseType: LicenseType; quantity: number | null; totalCost: number | null; currency: string; vendor: string | null; days: number }

export interface Summary {
  products: number; tracked: number; compliant: number; shortfall: number; unlicensed: number; overLicensed: number; notMeasured: number;
  shortfallUnits: number; unusedUnits: number; shortfallCost: number; unusedValue: number; compliancePercent: number; topIssues: Compliance[]; expiryDays: number;
  detail: {
    licenses: number; subscriptions: number; perpetual: number; expiring30: number; expired: number; maintenanceEnding: number; spend: number; renewalSpend: number;
    currencies: string[]; cloudConnections: number; cloudFailed: number; cloudAssigned: number; cloudAvailable: number; cloudDisabledLicensed: number; cloudInactiveLicensed: number;
    notActivated: number; activationKnown: number; alerts: Record<string, number>; renewals: Renewal[];
  };
}

export interface License {
  id: string; productId: string; product: string; publisher: string | null; name: string; licenseType: LicenseType; metric: Metric; metricFromProduct: boolean;
  quantity: number | null; cloudQuantity: number | null; cloudConsumed: number | null; edition: string | null; version: string | null; downgradeRights: boolean;
  vendor: string | null; reseller: string | null; poNumber: string | null; invoiceNumber: string | null; contractNumber: string | null; purchaseDate: string | null;
  startDate: string | null; endDate: string | null; maintenanceEnd: string | null; autoRenew: boolean; billing: string | null; unitCost: number | null; totalCost: number | null;
  currency: string; notifyDays: number; keyHint: string | null; hasKey: boolean; departmentId: string | null; department: string | null; owner: string | null;
  cloudConnectionId: string | null; cloudConnection: string | null; cloudSku: string | null; synced: boolean; notes: string | null; createdBy: string | null;
  createdAt: string; updatedAt: string; allocations: number; daysLeft: number | null; status: 'active' | 'expiring' | 'expired' | 'retired';
}

export interface Product {
  id: string; name: string; publisher: string | null; category: string; namePatterns: string[]; excludePatterns: string[]; publisherPatterns: string[]; exePatterns?: string[]; metric: Metric;
  kind: 'installed' | 'cloud'; needsLicense: boolean; webAppId: string | null; webAppName: string | null; cloudSkus: string[]; catalogKey: string | null; notes: string | null;
  createdBy: string | null; createdAt: string; updatedAt: string;
}

export interface CloudSku {
  connectionId: string; connection: string; skuId: string; skuPart: string; name: string | null; enabled: number; warning: number; suspended: number; consumed: number;
  available?: number; status: string | null; nextLifecycle: string | null; isTrial: boolean; syncedAt: string; disabledAssigned?: number; inactiveAssigned?: number; productId?: string | null;
}

export interface Alert {
  id: number; kind: string; severity: 'info' | 'low' | 'medium' | 'high' | 'critical'; title: string; detail: string | null; link: string | null;
  productId: string | null; licenseId: string | null; assetId: string | null; appId: string | null; firstAt: string; lastAt: string;
  resolvedAt: string | null; notifiedAt: string | null; dismissed: boolean; dismissedBy: string | null;
}

export interface ProductDetail {
  compliance: Compliance;
  detail: {
    product: Product; licenses: License[]; versions: { version: string; devices: number }[]; cloud: CloudSku[];
    alerts: { id: number; kind: string; severity: Alert['severity']; title: string; detail: string | null; firstAt: string }[];
  };
  unusedDays?: number;
}

export interface Installation { assetId: string; hostname: string; software: string; version: string | null; user: string | null; os: string | null; department: string | null; lastSeen: string | null; cores: number | null; allocatedTo: string | null;
  lastUsed?: string | null; minutesRecent?: number; usedBy?: string[] | null; meteredSince?: string | null; usage?: UsageState; canUninstall?: boolean }

export interface Connection {
  id: string; name: string; provider: 'm365'; tenantId: string; clientId: string; hasSecret: boolean; enabled: boolean; syncApps: boolean;
  lastSyncAt: string | null; lastStatus: string | null; lastError: string | null; lastNote: string | null; users: number; skus: number;
}

export interface CloudUser { connectionId: string; userId: string; upn: string; displayName: string | null; mail: string | null; department: string | null; enabled: boolean | null; lastSignIn: string | null; licenses: string[] }

export interface Activation {
  assetId: string; hostname: string; os: string | null; department: string | null; product: string; application: string; channel: string | null; status: string;
  partialKey: string | null; graceMinutes: number | null; description: string | null; activated: boolean; collectedAt: string; hasKey?: boolean;
}

export interface Settings { notifyEmails: string[]; notifyInApp: boolean; expiryDays: number; unusedPercent: number; inactiveDays: number; webDiscovery: boolean; metering: boolean; unusedDays: number; productKeys?: boolean }

export interface Suggestion {
  key: string; name: string; publisher: string | null; category: string; metric: Metric; license: string; namePatterns: string[]; excludePatterns: string[];
  publisherPatterns: string[]; notes: string | null; matches: { name: string; devices: number }[]; devices: number;
}

export const METRIC_LABEL: Record<Metric, string> = { device: 'Per Device', user: 'Per User', core: 'Per Core', concurrent: 'Concurrent Users', site: 'Site / Unlimited' };
export const TYPE_LABEL: Record<LicenseType, string> = {
  perpetual: 'Perpetual', subscription: 'Subscription', volume: 'Volume', oem: 'OEM', retail: 'Retail', enterprise: 'Enterprise Agreement', trial: 'Trial', freeware: 'Freeware', open_source: 'Open Source',
};
export const STATUS_LABEL: Record<Status, [string, 'ok' | 'warn' | 'crit' | 'info' | '']> = {
  compliant: ['Compliant', 'ok'], shortfall: ['Under-Licensed', 'crit'], unlicensed: ['Unlicensed', 'crit'], over_licensed: ['Over-Licensed', 'info'],
  not_measured: ['Not Measured', ''], not_required: ['No Licence Needed', ''], unused: ['Not in Use', ''],
};
export const SEVERITY_TONE: Record<string, 'ok' | 'warn' | 'crit' | 'info' | ''> = { critical: 'crit', high: 'crit', medium: 'warn', low: 'info', info: '' };
export const ACTIVATION_LABEL: Record<string, string> = {
  licensed: 'Activated', unlicensed: 'Not Activated', oob_grace: 'Grace Period', oot_grace: 'Grace (Hardware Change)', non_genuine_grace: 'Not Genuine',
  notification: 'Notification Mode', extended_grace: 'Extended Grace', embedded: 'In Firmware', unknown: 'Unknown',
};

export const money = (v: number | null | undefined, currency?: string | null) =>
  v == null ? '—' : currency && /^[A-Z]{3}$/.test(currency)
    ? new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: v >= 1000 ? 0 : 2 }).format(v)
    : v.toLocaleString(undefined, { maximumFractionDigits: 2 });

export const consumptionLabel = (c: Compliance) =>
  c.kind === 'cloud' ? 'assigned' : c.metric === 'user' ? 'users' : c.metric === 'core' ? 'cores' : c.metric === 'concurrent' ? 'installed (use not measured)' : 'installed';

/** Use of an installation (software metering, build 120). */
export type UsageState = 'used' | 'unused' | 'new' | 'not_metered' | 'no_rules';
export const USAGE_LABEL: Record<UsageState, string> = { used: 'Used', unused: 'Unused', new: 'Metering for less than the period', not_metered: 'Not metered', no_rules: 'No programs set' };
export const USAGE_TONE: Record<UsageState, '' | 'ok' | 'warn' | 'crit' | 'info'> = { used: 'ok', unused: 'warn', new: '', not_metered: '', no_rules: '' };

export interface UsageApp { exe: string; product: string | null; publisher: string | null; devices: number; users: number; minutes: number; launches: number; lastUsed: string; productId: string | null; productName: string | null }
export interface UsageProduct { id: string; name: string; publisher: string | null; rules: boolean; installed: number; used: number; unused: number; recent: number; notMetered: number; unitCost: number | null }
export interface UsageSummary { metering: boolean; unusedDays: number; days: number; meteredDevices: number; apps: UsageApp[]; products: UsageProduct[] }
/** One run of a program: started, closed (null while running). */
export interface ProgramRun { assetId: string; hostname: string; user: string | null; exe: string; product: string | null; startedAt: string; endedAt: string | null; lastSeenAt: string; running: boolean; minutes: number }
export interface ProgramUse { assetId: string; hostname: string; user: string | null; minutes: number; launches: number; daysUsed: number; lastUsed: string; version: string | null; path: string }

/** Minutes as "3 h 20 min". */
export function hours(min: number | null | undefined): string {
  const m = Math.round(min ?? 0);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  return h >= 100 ? `${h.toLocaleString()} h` : `${h} h ${m % 60} min`;
}

/** A product key found on a device (the key itself is fetched on request). */
export interface DeviceKey {
  id: string; assetId: string; hostname: string; department: string | null; product: string; publisher: string | null;
  application: 'windows' | 'office' | 'firmware' | 'other'; source: string; keyHint: string; note: string | null; firstSeenAt: string; lastSeenAt: string;
}
export interface FoundSoftware { software: { name: string; publisher: string | null; devices: number }[]; publishers: { name: string; devices: number }[] }

/** "7-Zip 23.01 (x64)" → "7-Zip": the name without a trailing version or architecture. */
export function baseName(name: string): string {
  let n = name.trim();
  for (let i = 0; i < 3; i++) {
    n = n.replace(/\s*\((?:[^)]*\b(?:x64|x86|64-bit|32-bit|64 bit|32 bit)\b[^)]*|[a-z]{2}-[a-z]{2}|[^)]*\d+\.\d+[^)]*)\)\s*$/i, '').trim();
    n = n.replace(/\s+(?:-\s+)?(?:version\s+)?v?\d+(?:\.\d+)+[\w.\-+]*$/i, '').trim();
  }
  return n || name.trim();
}
