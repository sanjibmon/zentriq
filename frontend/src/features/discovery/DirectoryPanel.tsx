import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { DirectoryConnection, DirectoryObject, DirectoryTestResult, Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, Pager, Panel, Pill, statusTone } from '../../components/ui';

type ObjType = 'computers' | 'users' | 'groups' | 'ous';
const OBJ_LABEL: Record<ObjType, string> = { computers: 'Computers', users: 'Users', groups: 'Groups', ous: 'Organizational units' };
const COUNT_KEY: Record<ObjType, string> = { computers: 'computer', users: 'user', groups: 'group', ous: 'organizationalUnit' };

export function errorText(err: unknown): string {
  const body = err instanceof ApiError ? (err.body as { errors?: Record<string, string[]>; detail?: string } | null) : null;
  if (body?.errors) return Object.values(body.errors).flat().join(' ');
  if (body?.detail) return body.detail;
  return err instanceof Error ? err.message : String(err);
}

/** Active Directory: connections (DC address, name, credentials, domain/OU) and the synchronised objects. */
export function DirectoryPanel({ onScan }: { onScan: (connectionId: string) => void }) {
  const { can } = useAuth();
  const manage = can('directory.manage');
  const conns = useApi(() => api.get<DirectoryConnection[]>('/directory/connections'), []);
  const [edit, setEdit] = useState<DirectoryConnection | 'new' | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit' | 'info'; text: string } | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [type, setType] = useState<ObjType>('computers');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const list = conns.data ?? [];
  const current = list.find((c) => c.id === selected) ?? list[0] ?? null;
  const objects = useApi(
    () => current ? api.get<Paged<DirectoryObject>>('/directory/objects', { connectionId: current.id, type, q, page, pageSize: 50 }) : Promise.resolve(null),
    [current?.id, current?.lastSyncAt, type, q, page],
  );

  const busy = list.some((c) => c.syncJobStatus === 'queued' || c.syncJobStatus === 'running');
  useEffect(() => {
    if (!busy) return;
    const t = setInterval(conns.reload, 3000);
    return () => clearInterval(t);
  }, [busy, conns.reload]);

  async function sync(c: DirectoryConnection) {
    setMsg(null);
    try {
      await api.post(`/directory/connections/${c.id}/sync`);
      setMsg({ tone: 'info', text: `Reading "${c.name}" from ${c.serverHost}… The tables below update when it finishes.` });
      conns.reload();
    } catch (err) {
      setMsg({ tone: 'crit', text: errorText(err) });
    }
  }
  async function remove(c: DirectoryConnection) {
    if (!window.confirm(`Delete the connection "${c.name}" and the directory objects read with it? Assets are not deleted.`)) return;
    await api.del(`/directory/connections/${c.id}`);
    if (selected === c.id) setSelected(null);
    conns.reload();
  }

  return (
    <>
      <Callout icon="lock">Zentriq reads Active Directory with a read-only LDAP connection: organizational units, users, groups and computers. Nothing is changed in the directory. The password is encrypted on this server and never shown again. A normal domain user account is enough to read these objects.</Callout>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <ErrorBox error={conns.error} />
      <Panel title="Active Directory connections" right={manage && <button className="btn primary sm" onClick={() => setEdit('new')}>Add connection</button>} flush>
        <DataTable rows={list} empty={conns.loading ? 'Loading…' : 'No connection yet. Add one with the domain controller address, a user ID and password, and the domain or OU to read.'} columns={[
          { key: 'n', label: 'Name', render: (c) => <button className="link" onClick={() => setSelected(c.id)} style={{ fontWeight: current?.id === c.id ? 700 : 500 }}>{c.name}</button> },
          { key: 'dc', label: 'Domain controller', render: (c) => <><span className="mono">{c.serverHost}:{c.port}</span><div className="faint" style={{ fontSize: 11.5 }}>{c.dcName ?? ''}{c.ldaps ? ' · LDAPS' : ' · LDAP (signed/sealed)'}</div></> },
          { key: 'dn', label: 'Domain / OU', className: 'wrap', render: (c) => <span className="mono" style={{ fontSize: 12 }}>{c.baseDn}</span> },
          { key: 'u', label: 'User', render: (c) => <span className="mono">{c.username}</span> },
          { key: 'c', label: 'Objects', render: (c) => c.lastSyncAt ? `${c.counts.computer ?? 0} computers · ${c.counts.user ?? 0} users · ${c.counts.group ?? 0} groups · ${c.counts.organizationalUnit ?? 0} OUs` : '—' },
          { key: 's', label: 'Last sync', render: (c) => {
            const st = c.syncJobStatus === 'queued' || c.syncJobStatus === 'running' ? c.syncJobStatus : c.lastSyncStatus;
            return <><Pill tone={statusTone(st === 'succeeded' ? 'completed' : st)}>{st ? fmt.label(st) : 'Never'}</Pill>{c.lastSyncAt && <div className="faint" style={{ fontSize: 11.5 }}>{fmt.ago(c.lastSyncAt)}</div>}{c.lastError && <div style={{ fontSize: 11.5, color: 'var(--crit)', maxWidth: 280, whiteSpace: 'normal' }}>{c.lastError}</div>}</>;
          } },
        ]} />
      </Panel>
      {current && (
        <Panel title={`Directory objects — ${current.name}`} sub={current.lastSyncAt ? `Read ${fmt.date(current.lastSyncAt)}` : 'Click Sync now to read the directory'} flush
          right={<div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {manage && <button className="btn sm primary" disabled={current.syncJobStatus === 'queued' || current.syncJobStatus === 'running'} onClick={() => void sync(current)}>{current.syncJobStatus === 'queued' || current.syncJobStatus === 'running' ? 'Reading…' : 'Sync now'}</button>}
            {can('scans.run') && <button className="btn sm" onClick={() => onScan(current.id)} title="Scan the enabled computers in this domain/OU for hardware and software">Scan computers</button>}
            {manage && <button className="btn sm" onClick={() => setEdit(current)}>Edit</button>}
            {manage && <button className="btn sm" onClick={() => void remove(current)}>Delete</button>}
          </div>}>
          <div style={{ display: 'flex', gap: 10, alignItems: 'center', padding: '10px 14px', flexWrap: 'wrap' }}>
            <div className="seg" role="tablist">
              {(Object.keys(OBJ_LABEL) as ObjType[]).map((t) => (
                <button key={t} className={type === t ? 'on' : ''} onClick={() => { setType(t); setPage(1); }}>{OBJ_LABEL[t]} <span className="muted">{current.counts[COUNT_KEY[t]] ?? 0}</span></button>
              ))}
            </div>
            <input className="in" style={{ maxWidth: 260 }} placeholder="Search name, login, e-mail, DN…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
          </div>
          <ErrorBox error={objects.error} />
          <ObjectsTable type={type} rows={objects.data?.items ?? []} loading={objects.loading} />
          {objects.data && <Pager page={page} pageSize={50} total={objects.data.total} onPage={setPage} />}
        </Panel>
      )}
      {edit && <ConnectionDialog existing={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={(id, syncNow) => {
        setEdit(null);
        setSelected(id);
        conns.reload();
        if (syncNow) void api.post(`/directory/connections/${id}/sync`).then(conns.reload).catch((e: unknown) => setMsg({ tone: 'crit', text: errorText(e) }));
      }} />}
    </>
  );
}

function ObjectsTable({ type, rows, loading }: { type: ObjType; rows: DirectoryObject[]; loading: boolean }) {
  const empty = loading ? 'Loading…' : 'No objects of this type were read. Sync the connection, or check the domain / OU.';
  const ou = (o: DirectoryObject) => <span className="mono faint" style={{ fontSize: 11.5 }}>{o.parentDn}</span>;
  const state = (o: DirectoryObject) => o.enabled == null ? '—' : <Pill tone={o.enabled ? 'ok' : ''}>{o.enabled ? 'Enabled' : 'Disabled'}</Pill>;
  if (type === 'computers')
    return <DataTable rows={rows} empty={empty} columns={[
      { key: 'n', label: 'Computer', render: (o) => <><b>{o.assetId ? <Link to={`/assets/${o.assetId}`}>{o.name}</Link> : o.name}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{o.dnsHostName ?? ''}</div></> },
      { key: 'os', label: 'Operating system', render: (o) => <>{o.operatingSystem ?? '—'}<div className="faint" style={{ fontSize: 11.5 }}>{o.operatingSystemVersion ?? ''}</div></> },
      { key: 'e', label: 'Account', render: state },
      { key: 'l', label: 'Last logon', render: (o) => <span title={fmt.date(o.lastLogon)}>{fmt.ago(o.lastLogon)}</span> },
      { key: 'a', label: 'Inventory', render: (o) => o.assetId ? <Link to={`/assets/${o.assetId}`}>Asset</Link> : <span className="muted">Not scanned</span> },
      { key: 'ou', label: 'OU', className: 'wrap', render: ou },
    ]} />;
  if (type === 'users')
    return <DataTable rows={rows} empty={empty} columns={[
      { key: 'n', label: 'User', render: (o) => <><b>{o.displayName ?? o.name}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{o.samAccountName}{o.userPrincipalName ? ` · ${o.userPrincipalName}` : ''}</div></> },
      { key: 'm', label: 'E-mail', render: (o) => o.mail ?? '—' },
      { key: 'd', label: 'Department / title', render: (o) => [o.department, o.title].filter(Boolean).join(' · ') || '—' },
      { key: 'e', label: 'Account', render: state },
      { key: 'l', label: 'Last logon', render: (o) => <span title={fmt.date(o.lastLogon)}>{fmt.ago(o.lastLogon)}</span> },
      { key: 'p', label: 'Password set', render: (o) => fmt.day(o.passwordLastSet) },
      { key: 'g', label: 'Groups', render: (o) => <span title={o.memberOf.map((g) => g.split(',')[0]?.replace(/^CN=/i, '')).join('\n')}>{o.memberOf.length}</span> },
      { key: 'ou', label: 'OU', className: 'wrap', render: ou },
    ]} />;
  if (type === 'groups')
    return <DataTable rows={rows} empty={empty} columns={[
      { key: 'n', label: 'Group', render: (o) => <><b>{o.name}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{o.samAccountName}</div></> },
      { key: 'd', label: 'Description', className: 'wrap', render: (o) => o.description ?? '—' },
      { key: 'm', label: 'Mail', render: (o) => o.mail ?? '—' },
      { key: 'c', label: 'Created', render: (o) => fmt.day(o.whenCreated) },
      { key: 'ou', label: 'OU', className: 'wrap', render: ou },
    ]} />;
  return <DataTable rows={rows} empty={empty} columns={[
    { key: 'n', label: 'Organizational unit', render: (o) => <b>{o.name}</b> },
    { key: 'dn', label: 'Distinguished name', className: 'wrap', render: (o) => <span className="mono" style={{ fontSize: 12 }}>{o.distinguishedName}</span> },
    { key: 'd', label: 'Description', className: 'wrap', render: (o) => o.description ?? '—' },
    { key: 'c', label: 'Created', render: (o) => fmt.day(o.whenCreated) },
  ]} />;
}

function ConnectionDialog({ existing, onClose, onSaved }: { existing: DirectoryConnection | null; onClose: () => void; onSaved: (id: string, syncNow: boolean) => void }) {
  const [name, setName] = useState(existing?.name ?? '');
  const [serverHost, setServerHost] = useState(existing?.serverHost ?? '');
  const [dcName, setDcName] = useState(existing?.dcName ?? '');
  const [ldaps, setLdaps] = useState(existing?.ldaps ?? true);
  const [port, setPort] = useState(String(existing?.port ?? 636));
  const [verify, setVerify] = useState(existing?.verifyCertificate ?? true);
  const [baseDn, setBaseDn] = useState(existing?.baseDn ?? '');
  const [username, setUsername] = useState(existing?.username ?? '');
  const [password, setPassword] = useState('');
  const [inactive, setInactive] = useState(existing?.inactiveDays ?? 90);
  const [syncNow, setSyncNow] = useState(!existing);
  const [busy, setBusy] = useState<'test' | 'save' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [test, setTest] = useState<DirectoryTestResult | null>(null);

  const body = () => ({ name: name.trim(), serverHost: serverHost.trim(), dcName: dcName.trim() || null, port: Number(port) || null, ldaps, verifyCertificate: verify, baseDn: baseDn.trim(), username: username.trim(), password: password || null, inactiveDays: inactive });

  function toggleLdaps(v: boolean) {
    setLdaps(v);
    if (port === '636' || port === '389' || port === '') setPort(v ? '636' : '389');
  }
  async function runTest() {
    setBusy('test'); setError(null); setTest(null);
    try {
      const r = await api.post<DirectoryTestResult>(`/directory/connections/test${existing ? `?id=${existing.id}` : ''}`, body());
      setTest(r);
      if (r.ok && !baseDn.trim() && r.defaultNamingContext) setBaseDn(r.defaultNamingContext);
    } catch (err) { setError(errorText(err)); } finally { setBusy(null); }
  }
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy('save'); setError(null);
    try {
      const id = existing ? (await api.put<{ id: string }>(`/directory/connections/${existing.id}`, body())).id : (await api.post<{ id: string }>('/directory/connections', body())).id;
      onSaved(id, syncNow);
    } catch (err) { setError(errorText(err)); } finally { setBusy(null); }
  }
  const complete = name.trim() && serverHost.trim() && baseDn.trim() && username.trim() && (existing || password);

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="dc-title">
      <form className="modal-card" onSubmit={save} style={{ width: 'min(680px, 100%)' }}>
        <header><h2 id="dc-title">{existing ? `Edit ${existing.name}` : 'Add Active Directory connection'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {test && <Callout tone={test.ok ? 'ok' : 'crit'} icon={test.ok ? 'info' : 'alert'}>{test.message}{test.ok && test.defaultNamingContext ? ` Domain: ${test.defaultNamingContext}.` : ''}</Callout>}
          <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder="e.g. CORP domain" /></label>
          <div className="form-grid">
            <label>Domain controller IP address or host name
              <input className="in mono" value={serverHost} onChange={(e) => setServerHost(e.target.value)} required placeholder="192.168.1.2 or dc01.corp.example.com" />
            </label>
            <label>Domain controller name (FQDN)
              <input className="in mono" value={dcName} onChange={(e) => setDcName(e.target.value)} placeholder="dc01.corp.example.com" />
              <small className="muted">Used to check the LDAPS certificate when you connect by IP address.</small>
            </label>
            <label>User ID
              <input className="in mono" value={username} onChange={(e) => setUsername(e.target.value)} required autoComplete="off" placeholder="CORP\svc-zentriq or svc-zentriq@corp.example.com" />
            </label>
            <label>Password
              <input className="in" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" placeholder={existing ? 'Leave empty to keep the saved password' : ''} required={!existing} />
            </label>
            <label className="full">Domain or OU (distinguished name)
              <input className="in mono" value={baseDn} onChange={(e) => setBaseDn(e.target.value)} required placeholder="DC=corp,DC=example,DC=com  or  OU=Head Office,DC=corp,DC=example,DC=com" />
              <small className="muted">Everything under this container is read. Leave empty and click <i>Test connection</i> to fill in the whole domain.</small>
            </label>
            <label className="row-check"><input type="checkbox" checked={ldaps} onChange={(e) => toggleLdaps(e.target.checked)} /> Use LDAPS (encrypted, recommended)</label>
            <label>Port <input className="in" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} /></label>
            {ldaps && <label className="row-check full"><input type="checkbox" checked={verify} onChange={(e) => setVerify(e.target.checked)} /> Verify the domain controller certificate (turn off only if the DC uses a self-signed certificate this server does not trust)</label>}
            <label>Skip computers with no logon for (days) <input className="in" type="number" min={0} max={3650} value={inactive} onChange={(e) => setInactive(Number(e.target.value))} /><small className="muted">Used by “Scan computers”. 0 = scan all.</small></label>
            {!existing && <label className="row-check" style={{ alignSelf: 'end' }}><input type="checkbox" checked={syncNow} onChange={(e) => setSyncNow(e.target.checked)} /> Read the directory after saving</label>}
          </div>
          {!ldaps && <small className="muted">Without LDAPS, Zentriq signs and encrypts the LDAP session with Kerberos/NTLM (port 389). A user in CN=… form needs LDAPS.</small>}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={!!busy}>Cancel</button>
          <button type="button" className="btn" onClick={() => void runTest()} disabled={!!busy || !serverHost.trim() || !username.trim() || (!existing && !password)}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
          <button type="submit" className="btn primary" disabled={!!busy || !complete}>{busy === 'save' ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}
