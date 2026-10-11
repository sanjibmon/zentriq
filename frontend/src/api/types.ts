// Response shapes of the Zentriq API (camelCase JSON).

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface LicenseSummary {
  mode: string;
  summary: string;
  expiresAt: string | null;
  daysRemaining: number | null;
  featuresEnabled: boolean;
  modules: string[];
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  mfaEnabled: boolean;
  roles: { id: string; name: string }[];
  permissions: string[];
  license: LicenseSummary;
}

export interface SetupStatus {
  installed: boolean;
  initialized: boolean;
  productVersion: string | null;
  licenseMode: string;
  trialExpiresAt: string | null;
}

export interface LoginResult {
  ok?: boolean;
  mfaRequired?: boolean;
  mfaEnrollmentRequired?: boolean;
}

export interface Asset {
  id: string;
  hostname: string | null;
  fqdn: string | null;
  ip: string | null;
  serialNumber: string | null;
  category: string;
  workload: string;
  osFamily: string | null;
  osName: string | null;
  status: string;
  criticality: string;
  ownerName: string | null;
  source: string;
  inventoryStatus: string;
  firstSeenAt: string;
  lastSeenAt: string;
  lastInventoryAt: string | null;
}

export interface KeyCount {
  key: string;
  count: number;
}

export interface AssetSummary {
  total: number;
  retired: number;
  stale: number;
  byCategory: KeyCount[];
  byOs: KeyCount[];
  byInventory: KeyCount[];
  byCriticality: KeyCount[];
}

export interface Scan {
  id: string;
  number: number;
  name: string;
  scanType: string;
  status: string;
  totalTargets: number;
  processed: number;
  assetsCreated: number;
  assetsUpdated: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  counts: Record<string, number>;
}

export interface Job {
  id: string;
  jobType: string;
  status: string;
  attempts: number;
  maxAttempts: number;
  progressPct: number | null;
  progressText: string | null;
  lastError: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface WorkloadUsage {
  workload: string;
  billable: number;
  limit: number | null;
}

export interface LicensingStatus extends LicenseSummary {
  limits: Record<string, number>;
  usage: WorkloadUsage[];
  installationId: string | null;
  trial: { startedAt: string; expiresAt: string } | null;
  license: {
    licenseId: string;
    customerName: string;
    keyId: string;
    issuedAt: string;
    expiresAt: string;
    graceDays: number;
    modules: string[];
  } | null;
}

export interface UserRow {
  id: string;
  email: string;
  displayName: string;
  status: string;
  mfaEnabled: boolean;
  lastLoginAt: string | null;
  createdAt: string;
  locked: boolean;
  roles: { id: string; name: string }[];
}

export interface Role {
  id: string;
  name: string;
  description: string;
  builtIn: boolean;
  permissions: string[];
}

export interface AuditEvent {
  id: number;
  occurredAt: string;
  actorName: string;
  sourceIp: string | null;
  action: string;
  target: string | null;
  outcome: string;
  detail: unknown;
}

export interface SystemInfo {
  productVersion: string;
  installationId: string | null;
  initializedAt: string | null;
  schemaVersion: number;
  expectedSchemaVersion: number;
  postgresVersion: string | null;
  database: { host: string; port: number; database: string };
  https: { port: number; hostName: string };
  paths: { root: string; logs: string; backups: string };
  workers: { workerId: string; hostName: string; version: string; startedAt: string; lastBeatAt: string; healthy: boolean }[];
  history: { occurredAt: string; action: string; fromVersion: string | null; toVersion: string | null; outcome: string }[];
}

export interface BackupFile {
  name: string;
  sizeBytes: number;
  createdAt: string;
}
