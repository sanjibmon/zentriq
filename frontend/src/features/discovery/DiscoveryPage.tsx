import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { Credential, DirectoryConnection, Paged, Scan, ScanResultRow } from '../../api/types';
import { CredentialDialog, CredentialsPanel, KINDS } from './CredentialsPanel';
import { DirectoryPanel, errorText } from './DirectoryPanel';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill, statusTone } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';

export function DiscoveryPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const confirm = useConfirm();
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState(query.get('new') === '1');
  const initialTab = query.get('tab');
  const [tab, setTab] = useState<'scans' | 'directory' | 'credentials'>(initialTab === 'credentials' ? 'credentials' : initialTab === 'directory' ? 'directory' : 'scans');
  const [adConnection, setAdConnection] = useState<string | null>(null);
  const [detail, setDetail] = useState<Scan | null>(null);
  const [picked, setPicked] = useState<string[]>([]);
  const [note, setNote] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
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
  const finished = (sc: Scan) => !['queued', 'running', 'cancelling'].includes(sc.status);
  async function removeOne(sc: Scan) {
    if (!(await confirm({ title: `Delete scan "${sc.name || `#${sc.number}`}"?`, danger: true, message: 'The scan, its per-device results and its log are deleted. Devices it found stay in the asset inventory.' }))) return;
    try { await api.del(`/discovery/scans/${sc.id}`); setNote({ tone: 'ok', text: 'Scan deleted.' }); } catch (e) { setNote({ tone: 'crit', text: errorText(e) }); }
    setPicked((p) => p.filter((x) => x !== sc.id));
    scans.reload();
  }
  async function removePicked() {
    if (!(await confirm({ title: `Delete ${picked.length} selected scan(s)?`, danger: true, message: 'The scans, their per-device results and logs are deleted. Devices they found stay in the asset inventory.' }))) return;
    try { const r = await api.post<{ deleted: number }>('/discovery/scans/delete', { ids: picked }); setNote({ tone: 'ok', text: `${r.deleted} scan(s) deleted.` }); } catch (e) { setNote({ tone: 'crit', text: errorText(e) }); }
    setPicked([]);
    scans.reload();
  }
  async function removeOld() {
    const v = await confirm({ title: 'Delete old scans', danger: true, message: 'Finished scans older than the number of days you enter are deleted with their results and logs. Devices they found stay in the asset inventory.', input: { label: 'Older than (days)', defaultValue: '30', type: 'number', min: 1 } });
    if (v === null) return;
    const days = Number(v);
    if (!Number.isInteger(days) || days < 1) { setNote({ tone: 'crit', text: 'Enter a whole number of days (1 or more).' }); return; }
    try { const r = await api.post<{ deleted: number }>('/discovery/scans/delete', { olderThanDays: days }); setNote({ tone: 'ok', text: `${r.deleted} scan(s) older than ${days} days deleted.` }); } catch (e) { setNote({ tone: 'crit', text: errorText(e) }); }
    setPage(1);
    scans.reload();
  }
  const pageRows = scans.data?.items ?? [];
  const selectable = pageRows.filter(finished);
  const allPicked = selectable.length > 0 && selectable.every((r) => picked.includes(r.id));

  return (
    <>
      <PageHead title="Discovery & Scans" desc="Find every device on your network."
        actions={can('scans.run') && <button className="btn primary" onClick={() => setOpen(true)}>New scan</button>} />
      <div className="tabs" role="tablist">
        <button className={tab === 'scans' ? 'on' : ''} onClick={() => setTab('scans')}>Scans</button>
        <button className={tab === 'directory' ? 'on' : ''} onClick={() => setTab('directory')}>Active Directory</button>
        <button className={tab === 'credentials' ? 'on' : ''} onClick={() => setTab('credentials')}>Credentials</button>
      </div>
      {tab === 'credentials' ? <CredentialsPanel /> : tab === 'directory' ? <DirectoryPanel onScan={(id) => { setAdConnection(id); setOpen(true); }} /> : <>
      <ErrorBox error={scans.error} />
      {note && <Callout tone={note.tone} icon={note.tone === 'crit' ? 'alert' : 'info'}>{note.text}</Callout>}
      <Panel flush right={can('scans.run') && <div style={{ display: 'flex', gap: 6 }}>
          {picked.length > 0 && <button className="btn sm" onClick={() => void removePicked()}>Delete selected ({picked.length})</button>}
          <button className="btn sm" onClick={() => void removeOld()}>Delete old scans…</button>
        </div>} title="Scan history">
        <DataTable rows={pageRows} empty={scans.loading ? 'Loading…' : 'No scans yet. Start one with New scan.'} columns={[
          ...(can('scans.run') ? [{ key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all finished scans" checked={allPicked} onChange={(e) => setPicked(e.target.checked ? selectable.map((r) => r.id) : [])} />, render: (r: Scan) => finished(r) ? <input type="checkbox" className="row-sel" aria-label={`Select scan ${r.number}`} checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id))} /> : null }] : []),
          { key: 'n', label: 'Scan', render: (r) => <><button className="link" onClick={() => setDetail(r)} title="Show the result for each device"><b>{r.name || `Scan #${r.number}`}</b></button><div className="faint" style={{ fontSize: 11.5 }}>#{r.number} · {fmt.label(r.scanType)}</div></> },
          { key: 's', label: 'Status', render: (r) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> },
          { key: 'p', label: 'Progress', render: (r) => <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><div className="progress"><i style={{ width: `${r.totalTargets ? (r.processed / r.totalTargets) * 100 : 0}%` }} /></div><span className="muted">{r.processed}/{r.totalTargets}</span></div> },
          { key: 'r', label: 'Results', className: 'wrap', render: (r) => Object.entries(r.counts).map(([k, v]) => `${fmt.label(k)} ${v}`).join(' · ') || '—' },
          { key: 'a', label: 'Assets', render: (r) => `${r.assetsCreated} new · ${r.assetsUpdated} updated` },
          { key: 't', label: 'Started', render: (r) => fmt.date(r.startedAt ?? r.createdAt) },
          { key: 'x', label: '', render: (r) => <div style={{ display: 'flex', gap: 6 }}>
            {can('scans.run') && ['queued', 'running'].includes(r.status) && <button className="btn sm" onClick={() => void cancel(r.id)}>Cancel</button>}
            {can('scans.run') && finished(r) && <button className="btn sm" onClick={() => void removeOne(r)}>Delete</button>}
          </div> },
        ]} />
        {scans.data && <Pager page={page} pageSize={25} total={scans.data.total} onPage={setPage} />}
      </Panel>
      </>}
      {open && <NewScanDialog adConnection={adConnection} onClose={() => { setOpen(false); setAdConnection(null); navigate('/discovery', true); }} onStarted={() => { setOpen(false); setAdConnection(null); setTab('scans'); navigate('/discovery', true); scans.reload(); }} />}
      {detail && <ScanDetail scan={detail} onClose={() => { setDetail(null); scans.reload(); }} />}
    </>
  );
}

const RESULT_TONE: Record<string, 'ok' | 'warn' | 'crit' | ''> = {
  success: 'ok', reachable_unauthenticated: 'warn', inventory_unavailable: 'warn', auth_failed: 'crit', inventory_failed: 'crit', unreachable: '', skipped: '', cancelled: '',
};

/** Per-host results of one scan: what responded, what was collected and why not. */
function ScanDetail({ scan, onClose }: { scan: Scan; onClose: () => void }) {
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const results = useApi(() => api.get<Paged<ScanResultRow>>(`/discovery/scans/${scan.id}/results`, { status, q, page, pageSize: 50 }), [scan.id, status, q, page]);
  const info = useApi(() => api.get<{ scan: Scan; log: { occurredAt: string; level: string; message: string }[] }>(`/discovery/scans/${scan.id}`), [scan.id]);
  const s = info.data?.scan ?? scan;
  const noCreds = (s.counts.reachable_unauthenticated ?? 0) > 0 || (s.counts.inventory_unavailable ?? 0) > 0;
  return (
    <div className="drawer-scrim" role="dialog" aria-modal="true" aria-labelledby="sd-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="drawer">
        <header><h2 id="sd-title">{s.name || `Scan #${s.number}`}</h2><Pill tone={statusTone(s.status)}>{fmt.label(s.status)}</Pill><button className="btn sm" onClick={onClose}>Close</button></header>
        <div className="body">
          <dl className="kv-grid">
            <div><dt>Type</dt><dd>{fmt.label(s.scanType)}</dd></div>
            <div><dt>Started</dt><dd>{fmt.date(s.startedAt ?? s.createdAt)}</dd></div>
            <div><dt>Finished</dt><dd>{fmt.date(s.finishedAt)}</dd></div>
            <div><dt>Targets</dt><dd>{s.processed} / {s.totalTargets}</dd></div>
            <div><dt>Assets</dt><dd>{s.assetsCreated} new · {s.assetsUpdated} updated</dd></div>
          </dl>
          {s.error && <Callout tone="crit" icon="alert">{s.error}</Callout>}
          {noCreds && (
            <Callout tone="warn" icon="alert">
              Some devices responded but no hardware/software inventory was collected. Inventory needs a credential that can log in to them (Windows: WinRM on TCP 5985 or DCOM/WMI; Linux: SSH; network devices: SNMP),
              or the Zentriq Agent installed on the device. Open each row below for the exact reason. Windows hosts with the firewall on usually need WinRM enabled
              (Group Policy: <i>Allow remote server management through WinRM</i> and a firewall rule for TCP 5985 from this server) — or deploy the agent with Intune, Configuration Manager or Group Policy.
            </Callout>
          )}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <select className="in" style={{ maxWidth: 240 }} value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
              <option value="">All results</option>
              {Object.entries(s.counts).map(([k, v]) => <option key={k} value={k}>{fmt.label(k)} ({v})</option>)}
            </select>
            <input className="in" style={{ maxWidth: 260 }} placeholder="Search address, name or reason…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
          </div>
          <ErrorBox error={results.error} />
          <Panel flush>
            <DataTable rows={results.data?.items ?? []} empty={results.loading ? 'Loading…' : 'No results.'} columns={[
              { key: 't', label: 'Device', render: (r) => <><b className="mono">{r.ip ?? r.target}</b><div className="faint" style={{ fontSize: 11.5 }}>{r.hostname && r.hostname !== r.ip ? r.hostname : ''}</div></> },
              { key: 's', label: 'Result', render: (r) => <Pill tone={RESULT_TONE[r.status] ?? ''}>{fmt.label(r.status)}</Pill> },
              { key: 'p', label: 'Open ports', render: (r) => <span className="mono">{r.openPorts?.length ? r.openPorts.join(', ') : '—'}</span> },
              { key: 'm', label: 'Collected via', render: (r) => r.protocol ? fmt.label(r.protocol) : '—' },
              { key: 'd', label: 'Details', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.detail ?? '—'}</span> },
              { key: 'a', label: 'Asset', render: (r) => r.assetId ? <Link to={`/assets/${r.assetId}`}>{r.assetCreated ? 'New asset' : 'Open'}</Link> : '—' },
            ]} />
            {results.data && <Pager page={page} pageSize={50} total={results.data.total} onPage={setPage} />}
          </Panel>
          {(info.data?.log.length ?? 0) > 0 && (
            <div>
              <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 6 }}>Scan log</div>
              <div className="log-box">{info.data!.log.map((l) => `${fmt.date(l.occurredAt)}  ${l.level.toUpperCase().padEnd(5)}  ${l.message}`).join('\n')}</div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function NewScanDialog({ adConnection, onClose, onStarted }: { adConnection: string | null; onClose: () => void; onStarted: () => void }) {
  const { can } = useAuth();
  const [name, setName] = useState('');
  const [type, setType] = useState<'list' | 'ad'>(adConnection ? 'ad' : 'list');
  const [targets, setTargets] = useState('');
  const [exclude, setExclude] = useState('');
  const [credIds, setCredIds] = useState<string[]>([]);
  const [connId, setConnId] = useState(adConnection ?? '');
  const [inactive, setInactive] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [addCred, setAddCred] = useState(false);
  const [confirmNoCreds, setConfirmNoCreds] = useState(false);
  const creds = useApi(() => api.get<Credential[]>('/credentials'), []);
  const conns = useApi(() => api.get<DirectoryConnection[]>('/directory/connections'), []);
  const collectCreds = (creds.data ?? []).filter((c) => c.kind !== 'ldap_bind');
  const conn = (conns.data ?? []).find((c) => c.id === connId) ?? null;

  useEffect(() => {
    if (!connId && conns.data?.length === 1) setConnId(conns.data[0]!.id);
  }, [conns.data, connId]);

  async function start(e: FormEvent) {
    e.preventDefault();
    if (credIds.length === 0 && !confirmNoCreds) { setConfirmNoCreds(true); return; }
    setBusy(true);
    setError(null);
    try {
      await api.post('/discovery/scans', {
        name: name.trim() || null, scanType: type, targetsText: type === 'list' ? targets : null, excludeText: exclude, credentialIds: credIds,
        directory: type === 'ad' ? { connectionId: connId, ldaps: conn?.ldaps ?? true, inactiveDays: inactive ?? conn?.inactiveDays ?? null } : null,
      });
      onStarted();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const ready = type === 'list' ? targets.trim() !== '' : connId !== '';
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="ns-title">
      <form className="modal-card" onSubmit={start} style={{ width: 'min(680px, 100%)' }}>
        <header><h2 id="ns-title">New scan</h2></header>
        <div className="body form-v">
          <Callout>Scan only networks you are authorized to administer. Targets must fall inside the approved scan scopes set by an administrator.</Callout>
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Head office LAN" maxLength={120} /></label>
          <div className="seg" role="radiogroup" aria-label="Target source">
            <button type="button" className={type === 'list' ? 'on' : ''} onClick={() => setType('list')}>IP addresses &amp; networks</button>
            <button type="button" className={type === 'ad' ? 'on' : ''} onClick={() => setType('ad')}>Active Directory computers</button>
          </div>
          {type === 'list' ? (
            <label>Targets
              <textarea className="in" rows={4} value={targets} onChange={(e) => setTargets(e.target.value)} required placeholder={'10.20.0.0/24\n10.20.1.10-10.20.1.50\nsrv-app01.corp.example.com'} />
              <small className="muted">IP addresses, CIDR networks, ranges or host names — one per line.</small>
            </label>
          ) : (conns.data?.length ?? 0) === 0 && !conns.loading ? (
            <Callout tone="warn" icon="alert">No Active Directory connection yet. Add one on the <b>Active Directory</b> tab (domain controller IP/name, user ID and password, domain or OU), then come back.</Callout>
          ) : (
            <div className="form-grid">
              <label>Active Directory connection
                <select className="in" value={connId} onChange={(e) => setConnId(e.target.value)} required>
                  <option value="">Select…</option>
                  {(conns.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.name} — {c.serverHost}</option>)}
                </select>
              </label>
              <label>Skip computers with no logon for (days) <input className="in" type="number" min={0} max={3650} value={inactive ?? conn?.inactiveDays ?? 90} onChange={(e) => setInactive(Number(e.target.value))} /></label>
              {conn && <small className="muted full">Enabled computer accounts under <span className="mono">{conn.baseDn}</span> are read from {conn.serverHost} and each is then scanned. Directory presence alone does not prove a computer is online.</small>}
            </div>
          )}
          <label>Exclusions (optional)
            <textarea className="in" rows={2} value={exclude} onChange={(e) => setExclude(e.target.value)} placeholder="10.20.0.1" />
          </label>
          <div>
            <div style={{ display: 'flex', alignItems: 'center', marginBottom: 5 }}>
              <div style={{ fontSize: 13, fontWeight: 600, flex: 1 }}>Credentials to collect hardware, software and network inventory</div>
              {can('credentials.manage') && <button type="button" className="btn sm" onClick={() => setAddCred(true)}>Add credential</button>}
            </div>
            {collectCreds.length === 0 ? (
              <small className="muted">No credentials yet. Add a Windows domain/local admin account (WinRM/WMI), an SSH account or an SNMP community.</small>
            ) : (
              <div className="check-list">
                {collectCreds.map((c) => (
                  <label key={c.id}>
                    <input type="checkbox" checked={credIds.includes(c.id)} onChange={(e) => { setConfirmNoCreds(false); setCredIds((ids) => (e.target.checked ? [...ids, c.id] : ids.filter((x) => x !== c.id))); }} />
                    <span><b>{c.name}</b> <span className="muted">· {KINDS[c.kind] ?? c.kind}{c.allowedScope ? ` · ${c.allowedScope}` : ''}</span></span>
                  </label>
                ))}
              </div>
            )}
            {credIds.length === 0 && (
              <Callout tone="warn" icon="alert">
                {confirmNoCreds ? <><b>No credential selected.</b> This scan will only find which devices respond (addresses, names, open ports). It will not collect hardware, software or network details. Click <b>Start discovery only</b> to continue, or select a credential.</>
                  : <>Without a credential the scan only finds devices — it cannot collect hardware, software or network details.</>}
              </Callout>
            )}
          </div>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !ready}>{busy ? 'Starting…' : confirmNoCreds && credIds.length === 0 ? 'Start discovery only' : 'Start scan'}</button>
        </footer>
      </form>
      {addCred && <CredentialDialog existing={null} onClose={() => setAddCred(false)} onSaved={() => { setAddCred(false); creds.reload(); }} />}
    </div>
  );
}
