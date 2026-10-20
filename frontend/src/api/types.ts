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
  /** MFA is mandatory and not set up yet: the session has no permissions until enrollment is confirmed. */
  mfaEnrollmentRequired?: boolean;
  roles: { id: string; name: string }[];
  permissions: string[];
  license: LicenseSummary;
}

export interface MfaEnrollment {
  secret: string;
  otpauthUri: string;
  qrSvg: string;
}

export interface SmtpConfig {
  enabled: boolean;
  host: string | null;
  port: number;
  security: 'starttls' | 'none';
  username: string | null;
  hasPassword: boolean;
  fromAddress: string | null;
  fromName: string | null;
  consoleUrl: string | null;
  defaultConsoleUrl: string;
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
  departmentId?: string | null;
  department?: string | null;
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
  rowVersion?: number;
  /** IPv4 addresses from the latest inventory (primary first). */
  ipv4?: string[] | null;
  /** Only present when System Settings › Display › Show IPv6 is on. */
  ipv6?: string[] | null;
  loggedOnUser?: string | null;
  chassis?: string | null;
  isVirtual?: boolean | null;
  agentId?: string | null;
  agentHealth?: 'online' | 'stale' | 'offline' | 'never' | null;
  inventorySource?: string | null;
  agentLastCheckinAt?: string | null;
  lastContactAt?: string | null;
  offlineDays?: number | null;
  online?: boolean | null;
  agentCheckinMinutes?: number | null;
  /** Device type key (see features/assets/deviceTypes.ts); deviceTypeManual when an administrator chose it. */
  deviceType?: string | null;
  deviceTypeManual?: boolean;
  manufacturer?: string | null;
  model?: string | null;
  /** How the device was recognised without credentials (web interface, SNMP, ports). */
  identifiedBy?: string[] | null;
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
  checkinMinutes: number;
  inventoryMinutes: number;
  online: boolean;
  lastOutage: { from: string; to: string | null } | null;
  uninstallProtected: boolean;
  /** The agent keeps a live connection (online/offline within seconds). */
  live?: boolean;
  status: string;
  health: 'online' | 'stale' | 'offline' | 'never' | 'revoked' | 'replaced';
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
  canSelfUpdate?: boolean;
  updateAvailable?: boolean;
  latestVersion?: string | null;
  update?: { status: string; target: string; detail: string | null; requestedAt: string; completedAt: string | null } | null;
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
  checkinMinutes: number;
  inventoryMinutes: number;
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
  /** ready = agent can uninstall; old = agent too old; none = no agent. */
  agent?: 'ready' | 'old' | 'none';
  lastUninstall?: { status: string; detail: string | null } | null;
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
  network: { name: string | null; mac: string | null; ipv4: string[]; ipv6: string[]; gateway: string | null; dhcp: boolean | null; speedMbps: number | null; alias?: string | null; status?: string | null }[];
  printer?: { pageCount: number | null; status: string | null; supplies: { name: string; levelPercent: number | null }[] } | null;
  ports?: { physical: number; up: number; routing: boolean | null; switching: boolean | null } | null;
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

export interface ScanResultRow {
  id: number;
  target: string;
  ip: string | null;
  hostname: string | null;
  status: string;
  openPorts: number[] | null;
  detail: string | null;
  assetId: string | null;
  assetCreated: boolean;
  durationMs: number | null;
  recordedAt: string;
  protocol: string | null;
  credentialId: string | null;
}

export interface DirectoryConnection {
  id: string;
  name: string;
  serverHost: string | null;
  dcName: string | null;
  port: number;
  ldaps: boolean;
  verifyCertificate: boolean;
  baseDn: string | null;
  username: string | null;
  inactiveDays: number;
  lastSyncAt: string | null;
  lastSyncStatus: string | null;
  lastError: string | null;
  counts: Record<string, number>;
  createdAt: string;
  lastValidatedAt: string | null;
  syncJobStatus: string | null;
}

export interface DirectoryTestResult {
  ok: boolean;
  message: string;
  defaultNamingContext: string | null;
  dnsHostName: string | null;
  sampleCount: number | null;
}

export interface DirectoryObject {
  id: string;
  objectClass: 'computer' | 'user' | 'group' | 'organizationalUnit' | string;
  name: string | null;
  samAccountName: string | null;
  displayName: string | null;
  mail: string | null;
  userPrincipalName: string | null;
  dnsHostName: string | null;
  operatingSystem: string | null;
  operatingSystemVersion: string | null;
  enabled: boolean | null;
  lastLogon: string | null;
  whenCreated: string | null;
  passwordLastSet: string | null;
  department: string | null;
  title: string | null;
  description: string | null;
  distinguishedName: string;
  parentDn: string | null;
  memberOf: string[];
  syncedAt: string;
  connectionId: string;
  assetId: string | null;
}

export interface KC { key: string; count: number }
export interface Overview {
  kpi: Record<'assets' | 'servers' | 'endpoints' | 'networkDevices' | 'inventoried' | 'withAgent' | 'agentsOnline' | 'agents' | 'virtual' | 'stale' | 'newLast7' | 'retired' | 'softwareTitles' | 'softwareInstalls' | 'scans30' | 'lowDisk', number> & { offline7?: number };
  byCategory: KC[]; byOs: KC[]; byInventory: KC[]; byCriticality: KC[]; byType: KC[]; byManufacturer: KC[]; coverage: KC[]; agentHealth: KC[];
  ram: KC[]; topSoftware: KC[]; scanResults: KC[]; seen: KC[]; cores: KC[]; cpu: KC[]; disk: KC[]; free: KC[]; byDepartment?: KC[];
  posture: { key: string; yes: number; no: number; unknown: number }[];
  trend: { day: string; new: number; total: number }[];
  generatedAt: string;
}

export interface AgentSettings { checkinMinutes: number; inventoryMinutes: number; uninstallProtection: boolean }
export interface AgentSettingsResponse { settings: AgentSettings; checkinChoices: number[]; inventoryChoices: number[] }
