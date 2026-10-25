import { useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, fmt, Pill } from '../../components/ui';
import { errText, KIND_HELP, type BackupFile, type BackupInfo, type Destination, type Kind } from './types';

function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label>{label}{children}{hint && <small className="muted">{hint}</small>}</label>;
}

const KINDS: { kind: Kind; title: string; icon: 'folder' | 'network' | 'upload' | 'terminal' | 'cloud' }[] = [
  { kind: 'folder', title: 'Local or attached disk', icon: 'folder' },
  { kind: 'smb', title: 'Network share (SMB)', icon: 'network' },
  { kind: 'ftp', title: 'FTP / FTPS', icon: 'upload' },
  { kind: 'sftp', title: 'SFTP', icon: 'terminal' },
  { kind: 'azure', title: 'Azure Blob Storage', icon: 'cloud' },
  { kind: 's3', title: 'Amazon S3 / compatible', icon: 'cloud' },
  { kind: 'gcs', title: 'Google Cloud Storage', icon: 'cloud' },
];

const NAMES: Record<Kind, string> = {
  folder: 'Backup disk E:', smb: 'NAS — backups share', ftp: 'FTP server', sftp: 'Linux backup server', azure: 'Azure — zentriq-backups',
  s3: 'S3 — zentriq-backups', gcs: 'Google Cloud — zentriq-backups',
};

/** Add or edit a backup destination; "Test" writes and deletes a small file there. */
export function DestinationDialog({ dest, onClose, onSaved }: { dest: Destination | null; onClose: () => void; onSaved: (msg: string) => void }) {
  const s = dest?.settings ?? {};
  const [kind, setKind] = useState<Kind>(dest?.kind ?? 'smb');
  const [f, setF] = useState({
    name: dest?.name ?? '', enabled: dest?.enabled ?? true, keep: String(dest?.keep ?? 30),
    path: s.path ?? '', host: s.host ?? '', port: s.port ? String(s.port) : '', user: s.user ?? '', bucket: s.bucket ?? '', region: s.region ?? '',
    endpoint: s.endpoint ?? '', tls: s.tls ?? 'explicit', passive: s.passive ?? true, authMode: s.authMode ?? 'key', pathStyle: s.pathStyle ?? false,
    hostKey: s.hostKey ?? '', secret: '', privateKey: '', passphrase: '',
  });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value } as typeof f);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null);
  const keep = dest?.hasSecret ? ' (leave empty to keep the saved one)' : '';

  function body() {
    const settings = {
      path: f.path, host: f.host, port: f.port ? Number(f.port) : null, user: f.user, bucket: f.bucket, region: f.region, endpoint: f.endpoint,
      tls: f.tls, passive: f.passive, authMode: f.authMode, pathStyle: f.pathStyle, hostKey: dest && !f.hostKey ? '' : (f.hostKey || null),
    };
    return {
      id: dest?.id ?? null, name: f.name.trim() || NAMES[kind], kind, enabled: f.enabled, keep: Number(f.keep), settings,
      password: f.secret || null, privateKey: f.privateKey || null, passphrase: f.passphrase || null,
    };
  }

  async function runTest() {
    setBusy(true); setTest(null); setError(null);
    try { setTest(await api.post<{ ok: boolean; message: string }>('/backup/destinations/test', body())); }
    catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const b = body();
      if (dest) await api.put(`/backup/destinations/${dest.id}`, b);
      else await api.post('/backup/destinations', b);
      onSaved(`Destination "${b.name}" saved. Backups are copied there after each backup.`);
    } catch (err) { setError(errText(err)); } finally { setBusy(false); }
  }

  const secretLabel = kind === 'azure' ? (f.authMode === 'sas' ? 'SAS token' : 'Storage account key') : kind === 's3' ? 'Secret access key' : 'Password';

  return (
    <Modal title={dest ? `Edit "${dest.name}"` : 'Add backup destination'} wide onClose={onClose}
      footer={<>
        <button type="button" className="btn" onClick={() => void runTest()} disabled={busy}><Icon name="play" size={13} /> Test</button>
        <span style={{ flex: 1 }} />
        <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="dest-form" className="btn primary" disabled={busy}>{busy ? 'Working…' : 'Save'}</button>
      </>}>
      <form id="dest-form" className="form-v" onSubmit={submit}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {test && <Callout tone={test.ok ? 'ok' : 'crit'} icon={test.ok ? 'info' : 'alert'}>{test.message}</Callout>}
        {!dest && <div className="dest-kinds" role="radiogroup" aria-label="Destination type">
          {KINDS.map((k) => <button type="button" role="radio" aria-checked={kind === k.kind} key={k.kind} className={kind === k.kind ? 'on' : ''} onClick={() => { setKind(k.kind); setTest(null); }}>
            <Icon name={k.icon} size={16} /> <span>{k.title}</span>
          </button>)}
        </div>}
        <p className="muted" style={{ margin: 0, fontSize: 12.5 }}>{KIND_HELP[kind]}</p>
        <div className="form-grid">
          <Field label="Name"><input className="in" value={f.name} onChange={set('name')} placeholder={NAMES[kind]} maxLength={100} /></Field>
          <Field label="Keep there" hint="Newest backups kept at this destination (0 = keep all). Older ones are deleted after each copy.">
            <input className="in" type="number" min={0} max={1000} value={f.keep} onChange={set('keep')} />
          </Field>
        </div>

        {kind === 'folder' && <Field label="Folder" hint="A full path on this server, for example E:\ZentriqBackups. It is created when missing.">
          <input className="in mono" value={f.path} onChange={set('path')} placeholder="E:\ZentriqBackups" required />
        </Field>}

        {kind === 'smb' && <>
          <Field label="Share and folder" hint={<>UNC path: <span className="mono">\\server\share</span> or <span className="mono">\\server\share\folder</span>.</>}>
            <input className="in mono" value={f.path} onChange={set('path')} placeholder="\\nas01\backups\zentriq" required />
          </Field>
          <div className="form-grid">
            <Field label="Account (optional)" hint="DOMAIN\user, or NAS\user for a NAS. Empty: the server's computer account."><input className="in" value={f.user} onChange={set('user')} placeholder="CORP\svc-zentriq-backup" autoComplete="off" /></Field>
            <Field label={`Password${keep}`}><input className="in" type="password" value={f.secret} onChange={set('secret')} autoComplete="new-password" disabled={!f.user} /></Field>
          </div>
        </>}

        {(kind === 'ftp' || kind === 'sftp') && <>
          <div className="form-grid">
            <Field label="Server"><input className="in" value={f.host} onChange={set('host')} placeholder={kind === 'ftp' ? 'ftp.corp.example.com' : 'backup01.corp.example.com'} required /></Field>
            <Field label="Port"><input className="in" type="number" min={1} max={65535} value={f.port} onChange={set('port')} placeholder={kind === 'ftp' ? '21' : '22'} /></Field>
            <Field label="Folder" hint={kind === 'sftp' ? 'Relative to the home folder, or absolute (/srv/backups/zentriq).' : 'Relative to the account\'s home folder.'}>
              <input className="in mono" value={f.path} onChange={set('path')} placeholder="zentriq" />
            </Field>
            <Field label="User name"><input className="in" value={f.user} onChange={set('user')} autoComplete="off" required /></Field>
            <Field label={`Password${kind === 'sftp' ? ' (or a private key below)' : ''}${keep}`}><input className="in" type="password" value={f.secret} onChange={set('secret')} autoComplete="new-password" /></Field>
            {kind === 'ftp' && <Field label="Security">
              <select className="in" value={f.tls} onChange={set('tls')}><option value="explicit">FTPS — FTP over TLS (recommended)</option><option value="none">Plain FTP (not encrypted in transit)</option></select>
            </Field>}
          </div>
          {kind === 'ftp' && <label className="row-check"><input type="checkbox" checked={f.passive} onChange={(e) => setF({ ...f, passive: e.target.checked })} /> Passive mode (works through most firewalls)</label>}
          {kind === 'sftp' && <>
            <Field label={`Private key (optional)${keep}`} hint="OpenSSH or PEM private key; used instead of, or besides, the password.">
              <textarea className="in mono" rows={4} value={f.privateKey} onChange={set('privateKey')} placeholder="-----BEGIN OPENSSH PRIVATE KEY-----" />
            </Field>
            <Field label="Key passphrase (optional)"><input className="in" type="password" value={f.passphrase} onChange={set('passphrase')} autoComplete="new-password" /></Field>
            {f.hostKey && <div className="row-check">Pinned host key: <span className="mono">{f.hostKey}</span>
              <button type="button" className="btn sm" onClick={() => setF({ ...f, hostKey: '' })}>Clear</button></div>}
          </>}
        </>}

        {kind === 'azure' && <>
          <div className="form-grid">
            <Field label="Storage account"><input className="in mono" value={f.user} onChange={set('user')} placeholder="zentriqbackups" required /></Field>
            <Field label="Container"><input className="in mono" value={f.bucket} onChange={set('bucket')} placeholder="zentriq" required /></Field>
            <Field label="Folder (optional)"><input className="in mono" value={f.path} onChange={set('path')} placeholder="server01" /></Field>
            <Field label="Sign in with">
              <select className="in" value={f.authMode} onChange={set('authMode')}><option value="key">Storage account key</option><option value="sas">SAS token</option></select>
            </Field>
          </div>
          <Field label={`${secretLabel}${keep}`}><input className="in mono" type="password" value={f.secret} onChange={set('secret')} autoComplete="new-password" /></Field>
          <Field label="Endpoint (optional)" hint="Only for sovereign clouds or private endpoints, for example https://account.blob.core.usgovcloudapi.net.">
            <input className="in mono" value={f.endpoint} onChange={set('endpoint')} placeholder="https://zentriqbackups.blob.core.windows.net" />
          </Field>
        </>}

        {kind === 's3' && <>
          <div className="form-grid">
            <Field label="Bucket"><input className="in mono" value={f.bucket} onChange={set('bucket')} placeholder="zentriq-backups" required /></Field>
            <Field label="Region"><input className="in mono" value={f.region} onChange={set('region')} placeholder="ap-south-1" /></Field>
            <Field label="Folder (optional)"><input className="in mono" value={f.path} onChange={set('path')} placeholder="server01" /></Field>
            <Field label="Access key ID"><input className="in mono" value={f.user} onChange={set('user')} autoComplete="off" required /></Field>
          </div>
          <Field label={`${secretLabel}${keep}`}><input className="in mono" type="password" value={f.secret} onChange={set('secret')} autoComplete="new-password" /></Field>
          <Field label="Service URL (S3-compatible storage only)" hint="Leave empty for Amazon S3. Examples: https://s3.eu-central-1.wasabisys.com, https://s3.us-west-004.backblazeb2.com, https://minio.corp.local:9000.">
            <input className="in mono" value={f.endpoint} onChange={set('endpoint')} placeholder="https://s3.wasabisys.com" />
          </Field>
          {f.endpoint && <label className="row-check"><input type="checkbox" checked={f.pathStyle} onChange={(e) => setF({ ...f, pathStyle: e.target.checked })} /> Path-style addresses (MinIO and some other services)</label>}
        </>}

        {kind === 'gcs' && <>
          <div className="form-grid">
            <Field label="Bucket"><input className="in mono" value={f.bucket} onChange={set('bucket')} placeholder="zentriq-backups" required /></Field>
            <Field label="Folder (optional)"><input className="in mono" value={f.path} onChange={set('path')} placeholder="server01" /></Field>
          </div>
          <Field label={`Service account JSON key${keep}`}><textarea className="in mono" rows={5} value={f.privateKey} onChange={set('privateKey')} placeholder='{"type": "service_account", …}' /></Field>
        </>}

        <label className="row-check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Copy every scheduled and manual backup here</label>
      </form>
    </Modal>
  );
}

/** Set or change the backup password. It is never shown again; keep it in the organisation's password manager. */
export function PasswordDialog({ isSet, onClose, onSaved }: { isSet: boolean; onClose: () => void; onSaved: (msg: string) => void }) {
  const [pw, setPw] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (pw !== confirm) { setError('The passwords do not match.'); return; }
    setBusy(true); setError(null);
    try { await api.put('/backup/password', { password: pw, confirm }); onSaved('Backup password saved. New backups can be opened with it on any Zentriq server.'); }
    catch (err) { setError(errText(err)); } finally { setBusy(false); }
  }
  return (
    <Modal title={isSet ? 'Change backup password' : 'Set backup password'} onClose={onClose}
      footer={<><button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="pw-form" className="btn primary" disabled={busy || pw.length < 10}>{busy ? 'Saving…' : 'Save password'}</button></>}>
      <form id="pw-form" className="form-v" onSubmit={submit}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <p className="muted" style={{ margin: 0 }}>With a password, a backup can be restored on a new server with just the file and the password — no recovery key file needed.
          Zentriq keeps only a key derived from it; nobody can read it back, so <b>store it in your password manager</b>. Backups made before a change keep the password they were made with.</p>
        <Field label="Password" hint="At least 10 characters; a passphrase of several words works well."><input className="in" type="password" value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="new-password" autoFocus /></Field>
        <Field label="Repeat the password"><input className="in" type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" /></Field>
      </form>
    </Modal>
  );
}

type Source = 'upload' | 'local' | 'destination';

/**
 * Restore: pick a backup (upload a file, one on this server, or one at a destination), open it with the password or a
 * recovery key when it comes from another installation, and confirm. The maintenance task does the restore.
 */
export function RestoreDialog({ destinations, initial, onClose, onStarted }: {
  destinations: Destination[]; initial?: { source: Source; file: string; destinationId?: string } | null; onClose: () => void; onStarted: (note: string) => void;
}) {
  const [source, setSource] = useState<Source>(initial?.source ?? 'upload');
  const [destId, setDestId] = useState(initial?.destinationId ?? destinations[0]?.id ?? '');
  const [file, setFile] = useState<string | null>(initial?.file ?? null);
  const [info, setInfo] = useState<BackupInfo | null>(null);
  const [uploading, setUploading] = useState<string | null>(null);
  const [pw, setPw] = useState('');
  const [recovery, setRecovery] = useState('');
  const [useKey, setUseKey] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const local = useApi(() => (source === 'local' ? api.get<BackupFile[]>('/backup/files') : Promise.resolve([] as BackupFile[])), [source]);
  const remote = useApi(() => (source === 'destination' && destId ? api.get<BackupFile[]>(`/backup/destinations/${destId}/files`) : Promise.resolve([] as BackupFile[])), [source, destId]);

  async function pickLocal(name: string) {
    setFile(name); setInfo(null); setError(null);
    try { setInfo((await api.get<{ backup: BackupInfo }>('/backup/restore/inspect', { file: name })).backup); }
    catch (e) { setError(errText(e)); }
  }

  async function upload(f: File) {
    setUploading(`Uploading ${f.name} (${fmt.bytes(f.size)})…`); setError(null); setFile(null); setInfo(null);
    try {
      const r = await api.upload<{ file: string; backup: BackupInfo }>('/backup/restore/upload', { name: f.name }, f);
      setFile(r.file); setInfo(r.backup);
    } catch (e) { setError(errText(e)); } finally { setUploading(null); }
  }

  // A backup from this installation opens with its own key; one from elsewhere needs its password or recovery key.
  const needsSecret = source === 'destination' ? null : info ? !info.sameInstallation : null;

  async function start(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ note: string }>('/backup/restore', {
        source, file, destinationId: source === 'destination' ? destId : null,
        password: useKey ? null : pw || null, recoveryKey: useKey ? recovery || null : null, confirm,
      });
      onStarted(r.note);
    } catch (err) { setError(errText(err)); } finally { setBusy(false); }
  }

  const files = source === 'local' ? local : remote;
  return (
    <Modal title="Restore from a backup" wide onClose={onClose}
      footer={<><button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="restore-form" className="btn danger" disabled={busy || !file || confirm !== 'RESTORE'}>{busy ? 'Starting…' : 'Restore'}</button></>}>
      <form id="restore-form" className="form-v" onSubmit={start}>
        <Callout tone="warn" icon="alert">Restoring <b>replaces all current Zentriq data</b> — devices, settings, users and history — with the backup. A safety backup of the current data is taken first.
          The console stops for a few minutes and comes back by itself; then sign in with an account from the backup.</Callout>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="seg" role="radiogroup" aria-label="Backup to restore">
          <button type="button" className={source === 'upload' ? 'on' : ''} onClick={() => { setSource('upload'); setFile(null); setInfo(null); }}>Upload a backup file</button>
          <button type="button" className={source === 'local' ? 'on' : ''} onClick={() => { setSource('local'); setFile(null); setInfo(null); }}>On this server</button>
          <button type="button" className={source === 'destination' ? 'on' : ''} disabled={destinations.length === 0} onClick={() => { setSource('destination'); setFile(null); setInfo(null); }}>From a destination</button>
        </div>

        {source === 'upload' && <Field label="Backup file (.zbak)" hint="After a rebuild: install Zentriq, sign in, and upload a backup from your destination here.">
          <input className="in" type="file" accept=".zbak" onChange={(e) => e.target.files?.[0] && void upload(e.target.files[0])} />
        </Field>}
        {uploading && <p className="muted" style={{ margin: 0 }}>{uploading}</p>}

        {source === 'destination' && <Field label="Destination">
          <select className="in" value={destId} onChange={(e) => { setDestId(e.target.value); setFile(null); }}>
            {destinations.map((d) => <option key={d.id} value={d.id}>{d.name} — {d.where}</option>)}
          </select>
        </Field>}

        {source !== 'upload' && <div className="restore-files">
          {files.error && <Callout tone="crit" icon="alert">{files.error.message}</Callout>}
          {files.loading && <p className="muted">Loading backups…</p>}
          {files.data && files.data.length === 0 && <p className="muted">No backups found there.</p>}
          {(files.data ?? []).slice(0, 100).map((b) => (
            <label key={b.name} className={`restore-file${file === b.name ? ' on' : ''}`}>
              <input type="radio" name="restore-file" checked={file === b.name} onChange={() => (source === 'local' ? void pickLocal(b.name) : (setFile(b.name), setInfo(null)))} />
              <span className="grow"><b>{fmt.date(b.createdAt)}</b> <span className="faint">{b.label ?? ''}{b.server ? ` · ${b.server}` : ''}</span><br /><span className="mono faint" style={{ fontSize: 11.5 }}>{b.name}</span></span>
              <span className="faint">{fmt.bytes(b.sizeBytes)}</span>
            </label>
          ))}
        </div>}

        {info && <dl className="dl2 restore-info">
          <dt>Created</dt><dd>{fmt.date(info.createdAt)} on <b>{info.machine}</b></dd>
          <dt>Zentriq version</dt><dd>{info.version}</dd>
          <dt>Contains</dt><dd>{info.contents ?? 'database and configuration'}</dd>
          <dt>Opens with</dt><dd>{info.sameInstallation ? <Pill tone="ok">This installation's key</Pill> : info.passwordProtected ? <Pill tone="info">Backup password</Pill> : <Pill tone="warn">Recovery key of {info.machine}</Pill>}
            {info.passwordProtected && info.sameInstallation && <span className="faint"> (or its password)</span>}</dd>
        </dl>}

        {file && needsSecret !== false && <>
          {source === 'destination' && <p className="muted" style={{ margin: 0 }}>If this backup was made by another installation (for example the server you are replacing), enter its backup password or recovery key. Leave both empty for a backup of this installation.</p>}
          {!useKey
            ? <Field label="Backup password"><input className="in" type="password" value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="off" /></Field>
            : <Field label="Recovery key" hint="The Key: line of backup-recovery-key.txt from the old server (or paste the whole file)."><textarea className="in mono" rows={3} value={recovery} onChange={(e) => setRecovery(e.target.value)} /></Field>}
          <button type="button" className="btn sm" style={{ alignSelf: 'flex-start' }} onClick={() => setUseKey(!useKey)}>{useKey ? 'Use the backup password instead' : 'Use a recovery key file instead'}</button>
        </>}

        {file && <Field label="Type RESTORE to confirm"><input className="in mono" value={confirm} onChange={(e) => setConfirm(e.target.value.toUpperCase())} placeholder="RESTORE" autoComplete="off" /></Field>}
      </form>
    </Modal>
  );
}
