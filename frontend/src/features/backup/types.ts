// Shapes returned by /api/v1/backup (see backend BackupEndpoints.cs).

export type Kind = 'folder' | 'smb' | 'ftp' | 'sftp' | 'azure' | 's3' | 'gcs';

export interface Schedule { enabled: boolean; frequency: 'daily' | 'weekly' | 'monthly'; time: string; weekdays: number[]; day: number }

export interface TargetSettings {
  path?: string | null; host?: string | null; port?: number | null; user?: string | null; bucket?: string | null; region?: string | null;
  endpoint?: string | null; tls?: string | null; passive?: boolean; hostKey?: string | null; authMode?: string | null; pathStyle?: boolean;
}

export interface Destination {
  id: string; name: string; kind: Kind; kindTitle: string; enabled: boolean; keep: number; settings: TargetSettings; hasSecret: boolean;
  where: string; status: 'ok' | 'failed' | null; message: string | null; at: string | null;
}

export interface BackupOverview {
  schedule: Schedule; scheduleText: string; nextRun: string | null; keepLocal: number; includeExports: boolean; includeLogs: boolean;
  password: { set: boolean; setAt: string | null; setBy: string | null };
  keyFingerprint: string | null;
  destinations: Destination[];
  kinds: { kind: Kind; title: string }[];
  lastRestore: { at: string; file: string; backupCreatedAt: string; fromVersion: string; fromMachine: string; toMachine: string; by: string | null } | null;
  running: { jobId: string; kind: 'backup' | 'restore'; status: string; requestedAt: string; progress: string | null } | null;
}

export interface CopyResult { id: string; name: string; kind: Kind; status: 'ok' | 'failed'; message: string; deleted: number }

export interface BackupRun {
  id: string; trigger: 'schedule' | 'manual' | 'pre-upgrade' | 'pre-restore' | 'command'; label: string; file: string;
  status: 'running' | 'succeeded' | 'partial' | 'failed' | 'interrupted'; sizeBytes: number | null; passwordProtected: boolean;
  contents: { schema: number; databaseBytes: number; certificates: string[]; dataProtectionKeys: number; remoteSupportFiles: number; reportFiles: number; logFiles: number } | null;
  destinations: CopyResult[]; error: string | null; requestedBy: string | null; version: string | null; startedAt: string; finishedAt: string | null; onServer: boolean | null;
}

export interface BackupFile { name: string; sizeBytes: number; createdAt: string | null; server: string | null; label: string | null }

export interface BackupInfo {
  createdAt: string; version: string; machine: string; label: string; format: number; passwordProtected: boolean; keyFingerprint: string;
  installationId: string | null; contents: string | null; sameInstallation: boolean;
}

export const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

export const TRIGGERS: Record<BackupRun['trigger'], string> = {
  schedule: 'Scheduled', manual: 'Back up now', 'pre-upgrade': 'Before an update', 'pre-restore': 'Before a restore', command: 'Command line',
};

/** Short help per destination type (where to set it up, what the account needs). */
export const KIND_HELP: Record<Kind, string> = {
  folder: 'A folder on another disk of this server (a second drive, a USB disk, or a SAN volume). Keep it off the system drive, so a failed disk does not take the backups with it.',
  smb: 'A Windows share or NAS. With an account (DOMAIN\\user or NAS\\user) the share is connected for each copy; without one, the server\'s computer account is used — give DOMAIN\\SERVER$ change permission on the share.',
  ftp: 'An FTP server. Prefer FTPS (TLS): the server\'s certificate must be trusted by Windows. The folder is relative to the account\'s home folder and is created when missing.',
  sftp: 'An SSH server (Linux, NAS). Sign in with a password or a private key (OpenSSH or PEM). The server\'s host key is pinned at the first connection.',
  azure: 'A container in an Azure storage account. Use an account key (Storage account › Access keys) or a SAS token with read, write, delete and list permission. Tip: turn on soft delete or immutability on the container.',
  s3: 'An Amazon S3 bucket, or S3-compatible storage (Wasabi, Backblaze B2, MinIO, Cloudflare R2 …: enter its service URL). The access key needs s3:PutObject, s3:GetObject, s3:ListBucket and s3:DeleteObject on the bucket.',
  gcs: 'A Google Cloud Storage bucket. Create a service account with the Storage Object Admin role on the bucket and paste its JSON key file.',
};

export const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
