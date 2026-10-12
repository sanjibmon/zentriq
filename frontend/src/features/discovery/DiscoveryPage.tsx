import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { Credential, Paged, Scan } from '../../api/types';
import { CredentialsPanel, KINDS } from './CredentialsPanel';
import { useAuth } from '../../app/auth';
import { useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill, statusTone } from '../../components/ui';

export function DiscoveryPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState(query.get('new') === '1');
  const [tab, setTab] = useState<'scans' | 'credentials'>(query.get('tab') === 'credentials' ? 'credentials' : 'scans');
  const scans = useApi(() => api.get<Paged<Scan>>('/discovery/scans', { page, pageSize: 25, status: 'all' }), [page]);

  // Live progress while scans are running.
  const running = scans.data?.items.some((s) => ['queued', 'running', 'cancelling'].includes(s.status));
  useEffect(() => {
    if (!running) return;
    const t = setInterval(scans.reload, 4000);
    return () => clearInterval(t);
  }, [running, scans.reload]);

  async function cancel(id: string) {
    await api.post(`/discovery/scans/${id}/cancel`);
    scans.reload();
  }

  return (
    <>
      <PageHead title="Discovery & Scans" desc="Find devices on authorized networks. Scans run in the Zentriq Worker service and continue if you close the browser."
        actions={can('scans.run') && <button className="btn primary" onClick={() => setOpen(true)}>New scan</button>} />
      <div className="tabs" role="tablist">
        <button className={tab === 'scans' ? 'on' : ''} onClick={() => setTab('scans')}>Scans</button>
        <button className={tab === 'credentials' ? 'on' : ''} onClick={() => setTab('credentials')}>Credentials</button>
      </div>
      {tab === 'credentials' ? <CredentialsPanel /> : <>
      <ErrorBox error={scans.error} />
      <Panel flush>
        <DataTable rows={scans.data?.items ?? []} empty={scans.loading ? 'Loading…' : 'No scans yet. Start one with New scan.'} columns={[
          { key: 'n', label: 'Scan', render: (r) => <><b>{r.name || `Scan #${r.number}`}</b><div className="faint" style={{ fontSize: 11.5 }}>#{r.number} · {fmt.label(r.scanType)}</div></> },
          { key: 's', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
          { key: 'p', label: 'Progress', render: (r) => <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><div className="progress"><i style={{ width: `${r.totalTargets ? (r.processed / r.totalTargets) * 100 : 0}%` }} /></div><span className="muted">{r.processed}/{r.totalTargets}</span></div> },
          { key: 'r', label: 'Results', className: 'wrap', render: (r) => Object.entries(r.counts).map(([k, v]) => `${fmt.label(k)} ${v}`).join(' · ') || '—' },
          { key: 'a', label: 'Assets', render: (r) => `${r.assetsCreated} new · ${r.assetsUpdated} updated` },
          { key: 't', label: 'Started', render: (r) => fmt.date(r.startedAt ?? r.createdAt) },
          { key: 'x', label: '', render: (r) => can('scans.run') && ['queued', 'running'].includes(r.status) ? <button className="btn sm" onClick={() => void cancel(r.id)}>Cancel</button> : r.error ? <span className="muted" title={r.error}>Error</span> : null },
        ]} />
        {scans.data && <Pager page={page} pageSize={25} total={scans.data.total} onPage={setPage} />}
      </Panel>
      </>}
      {open && <NewScanDialog onClose={() => { setOpen(false); navigate('/discovery', true); }} onStarted={() => { setOpen(false); navigate('/discovery', true); scans.reload(); }} />}
    </>
  );
}

function NewScanDialog({ onClose, onStarted }: { onClose: () => void; onStarted: () => void }) {
  const [name, setName] = useState('');
  const [type, setType] = useState<'list' | 'ad'>('list');
  const [targets, setTargets] = useState('');
  const [exclude, setExclude] = useState('');
  const [credIds, setCredIds] = useState<string[]>([]);
  const [server, setServer] = useState('');
  const [baseDn, setBaseDn] = useState('');
  const [ldaps, setLdaps] = useState(true);
  const [dirCred, setDirCred] = useState('');
  const [inactive, setInactive] = useState(90);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const creds = useApi(() => api.get<Credential[]>('/credentials'), []);
  const collectCreds = (creds.data ?? []).filter((c) => c.kind !== 'ldap_bind');
  const dirCreds = (creds.data ?? []).filter((c) => c.kind === 'ldap_bind' || c.kind === 'windows_domain');

  async function start(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.post('/discovery/scans', {
        name: name.trim() || null, scanType: type, targetsText: type === 'list' ? targets : null, excludeText: exclude, credentialIds: credIds,
        directory: type === 'ad' ? { server, baseDn, ldaps, credentialId: dirCred, inactiveDays: inactive } : null,
      });
      onStarted();
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as { errors?: Record<string, string[]> } | null) : null;
      setError(body?.errors ? Object.values(body.errors).flat().join(' ') : err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  const ready = type === 'list' ? targets.trim() !== '' : server.trim() !== '' && baseDn.trim() !== '' && dirCred !== '';
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="ns-title">
      <form className="modal-card" onSubmit={start} style={{ width: 'min(640px, 100%)' }}>
        <header><h2 id="ns-title">New scan</h2></header>
        <div className="body form-v">
          <Callout>Scan only networks you are authorized to administer. Targets must fall inside the approved scan scopes set by an administrator.</Callout>
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Head office LAN" maxLength={120} /></label>
          <div className="seg" role="radiogroup" aria-label="Target source">
            <button type="button" className={type === 'list' ? 'on' : ''} onClick={() => setType('list')}>IP addresses &amp; networks</button>
            <button type="button" className={type === 'ad' ? 'on' : ''} onClick={() => setType('ad')}>Active Directory</button>
          </div>
          {type === 'list' ? (
            <label>Targets
              <textarea className="in" rows={4} value={targets} onChange={(e) => setTargets(e.target.value)} required placeholder={'10.20.0.0/24\n10.20.1.10-10.20.1.50\nsrv-app01.corp.example.com'} />
              <small className="muted">IP addresses, CIDR networks, ranges or host names — one per line.</small>
            </label>
          ) : (
            <>
              <div className="form-grid">
                <label>Domain controller <input className="in mono" value={server} onChange={(e) => setServer(e.target.value)} placeholder="dc01.corp.example.com" /></label>
                <label>Directory credential
                  <select className="in" value={dirCred} onChange={(e) => setDirCred(e.target.value)}>
                    <option value="">Select…</option>
                    {dirCreds.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                  </select>
                </label>
                <label className="full">Domain or OU (distinguished name) <input className="in mono" value={baseDn} onChange={(e) => setBaseDn(e.target.value)} placeholder="OU=Workstations,DC=corp,DC=example,DC=com" /></label>
                <label>Skip computers inactive for (days) <input className="in" type="number" min={1} max={3650} value={inactive} onChange={(e) => setInactive(Number(e.target.value))} /></label>
                <label className="row-check" style={{ alignSelf: 'end' }}><input type="checkbox" checked={ldaps} onChange={(e) => setLdaps(e.target.checked)} /> Use LDAPS (636)</label>
              </div>
              <small className="muted">Enabled computer accounts under this container are read from the directory (paged LDAP, signed and sealed) and each is then scanned. Directory presence alone does not prove a computer is online.</small>
            </>
          )}
          <label>Exclusions (optional)
            <textarea className="in" rows={2} value={exclude} onChange={(e) => setExclude(e.target.value)} placeholder="10.20.0.1" />
          </label>
          <div>
            <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Credentials for inventory (optional)</div>
            {collectCreds.length === 0 ? (
              <small className="muted">No credentials yet — the scan will find devices but not collect hardware/software. Add credentials on the Credentials tab.</small>
            ) : (
              <div className="check-list">
                {collectCreds.map((c) => (
                  <label key={c.id}>
                    <input type="checkbox" checked={credIds.includes(c.id)} onChange={(e) => setCredIds((ids) => (e.target.checked ? [...ids, c.id] : ids.filter((x) => x !== c.id)))} />
                    <span><b>{c.name}</b> <span className="muted">· {KINDS[c.kind] ?? c.kind}{c.allowedScope ? ` · ${c.allowedScope}` : ''}</span></span>
                  </label>
                ))}
              </div>
            )}
          </div>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !ready}>{busy ? 'Starting…' : 'Start scan'}</button>
        </footer>
      </form>
    </div>
  );
}
