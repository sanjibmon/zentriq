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
  licensee: Licensee | null;
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

export interface Licensee {
  company: string;
  contactName: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  address: string | null;
  country: string | null;
  taxId: string | null;
}

export interface OrganizationProfile {
  id: string;
  name: string;
  legalName: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  stateRegion: string | null;
  postalCode: string | null;
  country: string | null;
  taxId: string | null;
  website: string | null;
  contactName: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  updatedAt: string;
}

export interface OrganizationResponse {
  profile: OrganizationProfile | null;
  licensee: Licensee | null;
}

export interface Credential {
  id: string;
  name: string;
  kind: string;
  username: string | null;
  description: string | null;
  allowedScope: string | null;
  options: Record<string, unknown>;
  createdAt: string;
  rotatedAt: string;
  lastUsedAt: string | null;
}

export interface SshHostKey {
  host: string;
  keyType: string;
  fingerprint: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface AgentRow {
  id: string;
  status: string;
  health: 'online' | 'stale' | 'offline' | 'never' | 'revoked';
  version: string;
  hostname: string | null;
  osName: string | null;
  lastIp: string | null;
  enrolledAt: string;
  lastCheckinAt: string | null;
  lastInventoryAt: string | null;
  certificateExpires: string | null;
  assetId: string | null;
  revokedAt: string | null;
  enrollmentCode: string | null;
}

export interface EnrollmentToken {
  id: string;
  name: string;
  serverUrl: string;
  maxUses: number | null;
  uses: number;
  expiresAt: string;
  revokedAt: string | null;
  createdAt: string;
  createdBy: string | null;
  state: string;
}

export interface AgentPackage {
  package: { url: string; size: number; version: string | null } | null;
  agentPort: number;
  defaultServerUrl: string;
  enrollmentAvailable: boolean;
}

export interface NewEnrollmentToken {
  id: string;
  name: string;
  serverUrl: string;
  expiresAt: string;
  maxUses: number | null;
  code: string;
  installCommand: string;
}

export interface SoftwareRow {
  name: string;
  publisher: string | null;
  installs: number;
  versions: number;
  latestVersions: string[];
}

export interface SoftwareInstall {
  assetId: string;
  hostname: string | null;
  ip: string | null;
  osName: string | null;
  version: string | null;
  installedOn: string | null;
  source: string;
}

export interface HardwareRow {
  assetId: string;
  hostname: string | null;
  category: string;
  osName: string | null;
  vendor: string | null;
  model: string | null;
  serialNumber: string | null;
  cpu: string | null;
  cpuCores: number | null;
  ramGb: number | null;
  chassis: string | null;
  diskGb: number;
  source: string | null;
  collectedAt: string;
}

/** Normalized inventory document (see backend Zentriq.Inventory.InventoryDocument). */
export interface InventoryDocument {
  source: string;
  collectedAt: string;
  collectorVersion: string | null;
  device: Record<string, string | null>;
  os: Record<string, string | boolean | null>;
  hardware: {
    cpuName: string | null; cpuCores: number | null; cpuThreads: number | null; memoryBytes: number | null;
    biosVendor: string | null; biosVersion: string | null; biosDate: string | null; tpmPresent: boolean | null; tpmVersion: string | null; isVirtual: boolean | null;
    disks: { model: string | null; serial: string | null; sizeBytes: number | null; mediaType: string | null; interface: string | null }[];
    volumes: { name: string | null; fileSystem: string | null; sizeBytes: number | null; freeBytes: number | null; encrypted: boolean | null }[];
  };
  network: { name: string | null; mac: string | null; ipv4: string[]; ipv6: string[]; gateway: string | null; dhcp: boolean | null; speedMbps: number | null }[];
  patches: { id: string; description: string | null; installedOn: string | null }[];
  services: { name: string; displayName: string | null; state: string | null; startMode: string | null }[];
  security: { antivirus: { name: string; enabled: boolean | null; upToDate: boolean | null }[]; firewallEnabled: boolean | null; diskEncryption: boolean | null; secureBoot: boolean | null };
  users: { loggedOn: string | null; localAdmins: string[] };
  errors: string[];
}

export interface AssetInventory {
  source: string | null;
  collectedAt: string | null;
  document: InventoryDocument | null;
  hardware: (Omit<HardwareRow, 'assetId' | 'hostname' | 'category' | 'osName' | 'diskGb'> & { cpuThreads: number | null; smbiosUuid: string | null }) | null;
  software: { name: string; publisher: string | null; version: string | null; installedOn: string | null; architecture: string | null; source: string }[];
  agent: { id: string; status: string; version: string; lastCheckinAt: string | null; lastInventoryAt: string | null } | null;
  identifiers: { kind: string; value: string }[];
}
