import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { Credential, SshHostKey } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, Panel } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';

export const KINDS: Record<string, string> = {
  windows_domain: 'Windows (domain account)',
  windows_local: 'Windows (local account)',
  ssh_password: 'SSH – password',
  ssh_key: 'SSH – private key',
  snmp_v2c: 'SNMP v2c (community)',
  snmp_v3: 'SNMP v3',
  ldap_bind: 'Active Directory (LDAP bind)',
};

export function CredentialsPanel() {
  const { can } = useAuth();
  const manage = can('credentials.manage');
  const confirm = useConfirm();
  const creds = useApi(() => api.get<Credential[]>('/credentials'), []);
  const keys = useApi(() => (manage ? api.get<SshHostKey[]>('/credentials/ssh-host-keys') : Promise.resolve([])), [manage]);
  const [edit, setEdit] = useState<Credential | 'new' | null>(null);

  async function remove(c: Credential) {
    if (!(await confirm({ title: `Delete credential "${c.name}"?`, danger: true, message: 'The encrypted secret is destroyed. Scans, profiles and Active Directory scans that use it will no longer collect inventory with it.' }))) return;
    await api.del(`/credentials/${c.id}`);
    creds.reload();
  }
  async function forget(k: SshHostKey) {
    if (!(await confirm({ title: `Forget the SSH host key of ${k.host}?`, danger: true, confirmLabel: 'Forget key', message: 'The next scan will trust whatever key the host presents. Only do this after verifying the host was rebuilt.' }))) return;
    await api.del(`/credentials/ssh-host-keys/${encodeURIComponent(k.host)}`);
    keys.reload();
  }

  return (
    <>
      <Callout icon="lock">Credentials let scans log in to devices and collect full hardware and software inventory without an agent. Secrets are encrypted on the server (AES-256-GCM) and are never shown again or returned by the API. Use dedicated, least-privilege accounts and limit each credential to the networks it is meant for.</Callout>
      <ErrorBox error={creds.error} />
      <Panel title="Scan credentials" right={manage && <button className="btn primary sm" onClick={() => setEdit('new')}>Add credential</button>} flush>
        <DataTable rows={creds.data ?? []} empty={creds.loading ? 'Loading…' : 'No credentials. Without credentials, scans only find devices (ping, open ports, names).'} columns={[
          { key: 'n', label: 'Name', render: (c) => <><b>{c.name}</b>{c.description && <div className="faint" style={{ fontSize: 11.5 }}>{c.description}</div>}</> },
          { key: 'k', label: 'Type', render: (c) => KINDS[c.kind] ?? c.kind },
          { key: 'u', label: 'User', render: (c) => <span className="mono">{c.username ?? '—'}</span> },
          { key: 's', label: 'Allowed networks', className: 'wrap', render: (c) => c.allowedScope ? <span className="mono">{c.allowedScope}</span> : <span className="muted">Any approved scope</span> },
          { key: 'l', label: 'Last used', render: (c) => fmt.ago(c.lastUsedAt) },
          { key: 'r', label: 'Secret changed', render: (c) => fmt.day(c.rotatedAt) },
          { key: 'x', label: '', render: (c) => manage ? <div style={{ display: 'flex', gap: 6 }}><button className="btn sm" onClick={() => setEdit(c)}>Edit</button><button className="btn sm" onClick={() => void remove(c)}>Delete</button></div> : null },
        ]} />
      </Panel>
      {manage && (
        <Panel title="SSH host keys" sub="Trusted on first use; a changed key blocks collection until you forget it here" flush>
          <DataTable rows={(keys.data ?? []).map((k) => ({ ...k, id: k.host }))} empty="No SSH hosts contacted yet." columns={[
            { key: 'h', label: 'Host', render: (k) => <span className="mono">{k.host}</span> },
            { key: 't', label: 'Key type', render: (k) => k.keyType },
            { key: 'f', label: 'SHA-256 fingerprint', className: 'wrap', render: (k) => <span className="mono" style={{ fontSize: 11.5 }}>{k.fingerprint}</span> },
            { key: 's', label: 'Last seen', render: (k) => fmt.ago(k.lastSeenAt) },
            { key: 'x', label: '', render: (k) => <button className="btn sm" onClick={() => void forget(k)}>Forget</button> },
          ]} />
        </Panel>
      )}
      {edit && <CredentialDialog existing={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); creds.reload(); }} />}
    </>
  );
}

export function CredentialDialog({ existing, onClose, onSaved }: { existing: Credential | null; onClose: () => void; onSaved: () => void }) {
  const o = (existing?.options ?? {}) as Record<string, unknown>;
  const [name, setName] = useState(existing?.name ?? '');
  const [kind, setKind] = useState(existing?.kind ?? 'windows_domain');
  const [username, setUsername] = useState(existing?.username ?? '');
  const [description, setDescription] = useState(existing?.description ?? '');
  const [scope, setScope] = useState(existing?.allowedScope ?? '');
  const [password, setPassword] = useState('');
  const [privateKey, setPrivateKey] = useState('');
  const [passphrase, setPassphrase] = useState('');
  const [community, setCommunity] = useState('');
  const [authPassword, setAuthPassword] = useState('');
  const [privPassword, setPrivPassword] = useState('');
  const [port, setPort] = useState(o.port ? String(o.port) : '');
  const [https, setHttps] = useState(Boolean(o.https));
  const [skipCert, setSkipCert] = useState(Boolean(o.skipCertificateCheck));
  const [authProtocol, setAuthProtocol] = useState(String(o.authProtocol ?? 'sha256'));
  const [privProtocol, setPrivProtocol] = useState(String(o.privProtocol ?? 'aes'));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const win = kind.startsWith('windows');
  const ssh = kind.startsWith('ssh');
  const snmp = kind.startsWith('snmp');

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    const options: Record<string, unknown> = {};
    if (port) options.port = Number(port);
    if (win) { options.https = https; options.skipCertificateCheck = https && skipCert; }
    if (kind === 'snmp_v3') { options.authProtocol = authProtocol; options.privProtocol = privProtocol; }
    const secret = { password: password || null, privateKey: privateKey || null, passphrase: passphrase || null, community: community || null, authPassword: authPassword || null, privPassword: privPassword || null };
    const hasSecret = Object.values(secret).some(Boolean);
    const body = { name, kind, username: username || null, description: description || null, allowedScope: scope || null, options, secret: existing && !hasSecret ? null : secret };
    try {
      if (existing) await api.put(`/credentials/${existing.id}`, body);
      else await api.post('/credentials', body);
      onSaved();
    } catch (err) {
      const b = err instanceof ApiError ? (err.body as { errors?: Record<string, string[]> } | null) : null;
      setError(b?.errors ? Object.values(b.errors).flat().join(' ') : err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  const keep = existing ? 'Leave empty to keep the stored secret' : undefined;
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="cr-title">
      <form className="modal-card" onSubmit={save} autoComplete="off">
        <header><h2 id="cr-title">{existing ? 'Edit credential' : 'Add credential'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="form-grid">
            <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} /></label>
            <label>Type
              <select className="in" value={kind} onChange={(e) => setKind(e.target.value)} disabled={!!existing}>
                {Object.entries(KINDS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </label>
          </div>
          {kind !== 'snmp_v2c' && (
            <label>{kind === 'snmp_v3' ? 'SNMPv3 user' : 'User name'}
              <input className="in mono" value={username} onChange={(e) => setUsername(e.target.value)} required autoComplete="off"
                placeholder={kind === 'windows_domain' || kind === 'ldap_bind' ? 'CORP\\svc-zentriq or svc-zentriq@corp.example.com' : kind === 'windows_local' ? '.\\zentriq-scan' : 'zentriq'} />
            </label>
          )}
          {(win || kind === 'ssh_password' || kind === 'ldap_bind') && (
            <label>Password <input className="in" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required={!existing} autoComplete="new-password" placeholder={keep} /></label>
          )}
          {kind === 'ssh_key' && (
            <>
              <label>Private key (OpenSSH or PEM)
                <textarea className="in mono" rows={4} value={privateKey} onChange={(e) => setPrivateKey(e.target.value)} required={!existing} placeholder={keep ?? '-----BEGIN OPENSSH PRIVATE KEY-----'} />
              </label>
              <label>Key passphrase (optional) <input className="in" type="password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} autoComplete="new-password" /></label>
            </>
          )}
          {kind === 'snmp_v2c' && (
            <label>Community <input className="in" type="password" value={community} onChange={(e) => setCommunity(e.target.value)} required={!existing} autoComplete="new-password" placeholder={keep} /></label>
          )}
          {kind === 'snmp_v3' && (
            <div className="form-grid">
              <label>Authentication
                <select className="in" value={authProtocol} onChange={(e) => setAuthProtocol(e.target.value)}>
                  <option value="sha256">SHA-256</option><option value="sha1">SHA-1</option><option value="md5">MD5 (legacy)</option>
                </select>
              </label>
              <label>Auth password <input className="in" type="password" value={authPassword} onChange={(e) => setAuthPassword(e.target.value)} required={!existing} minLength={8} autoComplete="new-password" placeholder={keep} /></label>
              <label>Privacy
                <select className="in" value={privProtocol} onChange={(e) => setPrivProtocol(e.target.value)}>
                  <option value="aes">AES-128</option><option value="aes256">AES-256</option><option value="none">None (authNoPriv)</option>
                </select>
              </label>
              <label>Privacy password <input className="in" type="password" value={privPassword} onChange={(e) => setPrivPassword(e.target.value)} disabled={privProtocol === 'none'} autoComplete="new-password" placeholder={keep} /></label>
            </div>
          )}
          {(ssh || snmp) && (
            <label>Port <input className="in" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} placeholder={ssh ? '22' : '161'} /></label>
          )}
          {win && (
            <>
              <label className="row-check"><input type="checkbox" checked={https} onChange={(e) => setHttps(e.target.checked)} /> Prefer WinRM over HTTPS (port 5986) when both are open</label>
              {https && <label className="row-check"><input type="checkbox" checked={skipCert} onChange={(e) => setSkipCert(e.target.checked)} /> Accept self-signed WinRM certificates (not recommended)</label>}
              <small className="muted">Collection tries WinRM (5985/5986) first, then DCOM/WMI (135). The account needs local administrator rights or remote WMI read access. Local accounts additionally need LocalAccountTokenFilterPolicy=1 on the target.</small>
            </>
          )}
          <label>Allowed networks (optional)
            <input className="in mono" value={scope} onChange={(e) => setScope(e.target.value)} placeholder="10.20.0.0/16, 192.168.5.0/24" />
            <small className="muted">The credential is only ever sent to addresses inside these networks (and inside the approved scan scopes).</small>
          </label>
          <label>Description (optional) <input className="in" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} /></label>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !name.trim()}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}
