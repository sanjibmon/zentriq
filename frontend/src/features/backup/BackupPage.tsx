import { useEffect, useState } from 'react';
import { api, ApiError } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { DestinationDialog, PasswordDialog, RestoreDialog } from './BackupDialogs';
import { errText, TRIGGERS, WEEKDAYS, type BackupOverview, type BackupRun, type Destination, type Schedule } from './types';

type Msg = { tone: 'ok' | 'crit' | 'info' | 'warn'; text: string } | null;

export function BackupPage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const admin = can('settings.manage');
  const tab = query.get('tab') === 'history' ? 'history' : 'overview';
  const ov = useApi(() => api.get<BackupOverview>('/backup'), []);
  const [msg, setMsg] = useState<Msg>(null);
  const [restoring, setRestoring] = useState<{ note: string; since: number } | null>(null);
  const [restoreOpen, setRestoreOpen] = useState<null | { source: 'local' | 'destination' | 'upload'; file: string; destinationId?: string } | 'new'>(null);

  // While a backup or restore is queued or running, refresh every 10 seconds.
  const running = ov.data?.running;
  useEffect(() => {
    if (!running || restoring) return;
    const t = setTimeout(ov.reload, 10_000);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running, ov.data, restoring]);

  async function backupNow() {
    setMsg(null);
    try { const r = await api.post<{ note: string }>('/backup/run'); setMsg({ tone: 'info', text: r.note }); ov.reload(); }
    catch (e) { setMsg({ tone: 'crit', text: errText(e) }); }
  }

  if (restoring) return <><PageHead title="Backup & Restore" /><RestoreProgress started={restoring} /></>;

  return (
    <>
      <PageHead title="Backup & Restore"
        actions={<>
          {admin && <button className="btn" onClick={() => setRestoreOpen('new')}><Icon name="retry" size={14} /> Restore…</button>}
          <button className="btn primary" onClick={() => void backupNow()} disabled={!!running}><Icon name="archive" size={14} /> Back up now</button>
        </>} />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'overview'} className={tab === 'overview' ? 'on' : ''} onClick={() => navigate('/backup', true)}>Backup settings</button>
        <button role="tab" aria-selected={tab === 'history'} className={tab === 'history' ? 'on' : ''} onClick={() => navigate('/backup?tab=history', true)}>History</button>
      </div>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {running && <Callout tone="info" icon="clock">{running.kind === 'restore' ? 'A restore' : 'A backup'} is {running.status === 'queued' ? 'waiting for the Zentriq maintenance task (starts within 5 minutes)' : `running${running.progress ? ` — ${running.progress}` : ''}`}; requested {fmt.ago(running.requestedAt)}.</Callout>}
      {ov.data?.lastRestore && <Callout tone="ok" icon="info">Restored on {fmt.date(ov.data.lastRestore.at)} from the backup of {fmt.date(ov.data.lastRestore.backupCreatedAt)} ({ov.data.lastRestore.fromMachine}, version {ov.data.lastRestore.fromVersion}){ov.data.lastRestore.by ? ` by ${ov.data.lastRestore.by}` : ''}.</Callout>}
      <ErrorBox error={ov.error} />
      {tab === 'overview'
        ? (!ov.data ? <Loading /> : <Overview data={ov.data} admin={admin} reload={ov.reload} setMsg={setMsg} onRestoreFrom={(d) => setRestoreOpen({ source: 'destination', file: '', destinationId: d.id })} />)
        : <History onRestore={(file) => setRestoreOpen({ source: 'local', file })} admin={admin} />}
      {restoreOpen && ov.data && <RestoreDialog destinations={ov.data.destinations}
        initial={restoreOpen === 'new' ? null : { ...restoreOpen, file: restoreOpen.file }}
        onClose={() => setRestoreOpen(null)} onStarted={(note) => { setRestoreOpen(null); setRestoring({ note, since: Date.now() }); }} />}
    </>
  );
}

function Overview({ data, admin, reload, setMsg, onRestoreFrom }: { data: BackupOverview; admin: boolean; reload: () => void; setMsg: (m: Msg) => void; onRestoreFrom: (d: Destination) => void }) {
  const confirm = useConfirm();
  const [editing, setEditing] = useState<Destination | 'new' | null>(null);
  const [pwOpen, setPwOpen] = useState(false);
  const [testing, setTesting] = useState<string | null>(null);

  async function test(d: Destination) {
    setTesting(d.id); setMsg(null);
    try { const r = await api.post<{ ok: boolean; message: string }>(`/backup/destinations/${d.id}/test`); setMsg({ tone: r.ok ? 'ok' : 'crit', text: `${d.name}: ${r.message}` }); reload(); }
    catch (e) { setMsg({ tone: 'crit', text: errText(e) }); } finally { setTesting(null); }
  }
  async function remove(d: Destination) {
    if (!await confirm({ title: `Remove "${d.name}"?`, danger: true, confirmLabel: 'Remove', message: 'Backups are no longer copied there. Backups already there are not deleted.' })) return;
    try { await api.del(`/backup/destinations/${d.id}`); reload(); } catch (e) { setMsg({ tone: 'crit', text: errText(e) }); }
  }
  async function removePassword() {
    if (!await confirm({ title: 'Remove the backup password?', danger: true, confirmLabel: 'Remove', message: 'New backups can then only be opened with this installation\'s recovery key. Backups already made keep their password.' })) return;
    try { await api.del('/backup/password'); setMsg({ tone: 'info', text: 'Backup password removed.' }); reload(); } catch (e) { setMsg({ tone: 'crit', text: errText(e) }); }
  }

  return (
    <>
      <div className="grid-2">
        <ScheduleCard data={data} admin={admin} onSaved={(t) => { setMsg({ tone: 'ok', text: t }); reload(); }} />
        <Panel title="Encryption" right={admin && <div style={{ display: 'flex', gap: 6 }}>
          <button className="btn sm" onClick={() => setPwOpen(true)}><Icon name="lock" size={13} /> {data.password.set ? 'Change password' : 'Set password'}</button>
          {data.password.set && <button className="btn sm danger" onClick={() => void removePassword()}>Remove</button>}
        </div>}>
          <div className="form-v">
            <div className="enc-row"><Icon name="lock" size={18} /><div>
              <b>Every backup is encrypted</b> (AES-256-GCM) and protected against changes. It holds the database, configuration, certificates,
              MFA keys, remote support data and report files — everything needed to rebuild this server.</div></div>
            {data.password.set
              ? <Callout tone="ok" icon="info">Backup password set{data.password.setAt ? ` on ${fmt.date(data.password.setAt)}` : ''}{data.password.setBy ? ` by ${data.password.setBy}` : ''}.
                  A new server can restore a backup with the file and this password.</Callout>
              : <Callout tone="warn" icon="alert">No backup password. Only this installation's recovery key opens the backups
                  (<span className="mono">Secrets\backup-recovery-key.txt</span>{data.keyFingerprint ? <>, key {data.keyFingerprint}</> : ''}) — keep it offline,
                  or set a password so a rebuilt server needs only the backup file and the password.</Callout>}
          </div>
        </Panel>
      </div>

      <Panel title="Destinations" sub="Each backup is also copied to these places. Keep at least one copy off this server — a disk failure, ransomware or a lost VM takes local backups with it."
        right={admin && <button className="btn sm primary" onClick={() => setEditing('new')}><Icon name="plus" size={13} /> Add destination</button>} flush>
        <DataTable rows={data.destinations} empty="No destinations yet: backups stay only in this server's Backups folder." columns={[
          { key: 'n', label: 'Destination', className: 'wrap', render: (d) => <><b>{d.name}</b>{!d.enabled && <> <Pill>Off</Pill></>}<div className="faint" style={{ fontSize: 12 }}>{d.kindTitle}</div></> },
          { key: 'w', label: 'Where', className: 'wrap', render: (d) => <span className="mono" style={{ fontSize: 12 }}>{d.where}</span> },
          { key: 'k', label: 'Keeps', render: (d) => d.keep === 0 ? 'All' : `${d.keep} newest` },
          { key: 's', label: 'Last copy / test', className: 'wrap', render: (d) => d.status
            ? <><Pill tone={d.status === 'ok' ? 'ok' : 'crit'}>{d.status === 'ok' ? 'OK' : 'Failed'}</Pill> <span className="faint">{fmt.ago(d.at)}</span>{d.message && <div className="faint" style={{ fontSize: 11.5 }}>{d.message}</div>}</>
            : <span className="muted">Not used yet</span> },
          { key: 'a', label: '', render: (d) => admin && <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
            <button className="btn sm" disabled={testing === d.id} onClick={() => void test(d)}>{testing === d.id ? 'Testing…' : 'Test'}</button>
            <button className="btn sm" onClick={() => onRestoreFrom(d)}>Backups…</button>
            <button className="btn sm" onClick={() => setEditing(d)}><Icon name="edit" size={13} /></button>
            <button className="btn sm danger" onClick={() => void remove(d)} aria-label={`Remove ${d.name}`}><Icon name="trash" size={13} /></button>
          </div> },
        ]} />
      </Panel>

      <Panel title="Disaster recovery" sub="Rebuilding after the server is lost">
        <ol className="dr-steps">
          <li>Install Zentriq on the new server (same or newer version) with the Standard or Advanced setup, and sign in with the new administrator.</li>
          <li>Open <b>Backup &amp; Restore › Restore…</b>: upload the latest backup, or add the same destination again and pick the backup there.</li>
          <li>Enter the <b>backup password</b> (or paste the old server's recovery key). Zentriq replaces the data and restarts by itself.</li>
          <li>Sign in with an account from the backup. Agents reconnect by themselves when the server keeps its name or IP address, or when the
            DNS name they use points to the new server; the certificate key, agent CA and MFA keys come back with the backup.</li>
        </ol>
      </Panel>

      {editing && <DestinationDialog dest={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={(t) => { setEditing(null); setMsg({ tone: 'ok', text: t }); reload(); }} />}
      {pwOpen && <PasswordDialog isSet={data.password.set} onClose={() => setPwOpen(false)} onSaved={(t) => { setPwOpen(false); setMsg({ tone: 'ok', text: t }); reload(); }} />}
    </>
  );
}

function ScheduleCard({ data, admin, onSaved }: { data: BackupOverview; admin: boolean; onSaved: (msg: string) => void }) {
  const [s, setS] = useState<Schedule>(data.schedule);
  const [keep, setKeep] = useState(String(data.keepLocal));
  const [exportsOn, setExports] = useState(data.includeExports);
  const [logsOn, setLogs] = useState(data.includeLogs);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = JSON.stringify(s) !== JSON.stringify(data.schedule) || keep !== String(data.keepLocal) || exportsOn !== data.includeExports || logsOn !== data.includeLogs;

  async function save() {
    setBusy(true); setError(null);
    try {
      const r = await api.put<{ scheduleText: string; nextRun: string | null }>('/backup/settings', { schedule: s, keepLocal: Number(keep), includeExports: exportsOn, includeLogs: logsOn });
      onSaved(`Schedule saved: ${r.scheduleText}${r.nextRun ? `; next backup ${fmt.date(r.nextRun)}` : ''}.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  const toggleDay = (d: number) => setS({ ...s, weekdays: s.weekdays.includes(d) ? s.weekdays.filter((x) => x !== d) : [...s.weekdays, d].sort() });

  return (
    <Panel title="Schedule" sub={data.schedule.enabled ? `${data.scheduleText} (server time)${data.nextRun ? ` · next ${fmt.date(data.nextRun)}` : ''}` : 'Scheduled backups are off'}
      right={admin && dirty && <button className="btn sm primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>}>
      <fieldset disabled={!admin} className="form-v plain">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <label className="row-check"><input type="checkbox" checked={s.enabled} onChange={(e) => setS({ ...s, enabled: e.target.checked })} /> Back up automatically</label>
        <div className="form-grid">
          <label>How often
            <select className="in" value={s.frequency} disabled={!s.enabled} onChange={(e) => setS({ ...s, frequency: e.target.value as Schedule['frequency'] })}>
              <option value="daily">Every day</option><option value="weekly">On chosen weekdays</option><option value="monthly">Once a month</option>
            </select>
          </label>
          <label>At (server time)<input className="in" type="time" value={s.time} disabled={!s.enabled} onChange={(e) => setS({ ...s, time: e.target.value })} /></label>
          {s.frequency === 'monthly' && <label>Day of the month
            <select className="in" value={s.day} disabled={!s.enabled} onChange={(e) => setS({ ...s, day: Number(e.target.value) })}>{Array.from({ length: 28 }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}</select>
          </label>}
          <label>Keep on this server<input className="in" type="number" min={0} max={1000} value={keep} onChange={(e) => setKeep(e.target.value)} /><small className="muted">Newest scheduled and manual backups (0 = all).</small></label>
        </div>
        {s.frequency === 'weekly' && <div className="day-chips" role="group" aria-label="Weekdays">
          {WEEKDAYS.map((d, i) => <button type="button" key={d} className={s.weekdays.includes(i) ? 'on' : ''} aria-pressed={s.weekdays.includes(i)} disabled={!s.enabled} onClick={() => toggleDay(i)}>{d}</button>)}
        </div>}
        <label className="row-check"><input type="checkbox" checked={exportsOn} onChange={(e) => setExports(e.target.checked)} /> Include report files (Exports folder)</label>
        <label className="row-check"><input type="checkbox" checked={logsOn} onChange={(e) => setLogs(e.target.checked)} /> Include logs of the last 30 days</label>
      </fieldset>
    </Panel>
  );
}

function History({ onRestore, admin }: { onRestore: (file: string) => void; admin: boolean }) {
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const runs = useApi(() => api.get<Paged<BackupRun>>('/backup/runs', { page, pageSize }), [page]);
  const busy = (runs.data?.items ?? []).some((r) => r.status === 'running');
  useEffect(() => {
    if (!busy) return;
    const t = setTimeout(runs.reload, 10_000);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [busy, runs.data]);

  return (
    <>
      <ErrorBox error={runs.error} />
      <Panel flush sub="Every backup with its copies. Files stay on this server as long as the retention keeps them.">
        <DataTable rows={runs.data?.items ?? []} empty={runs.loading ? 'Loading…' : 'No backups yet.'} columns={[
          { key: 't', label: 'Started', render: (r) => <>{fmt.date(r.startedAt)}<div className="faint" style={{ fontSize: 11.5 }}>{TRIGGERS[r.trigger]}{r.requestedBy ? ` · ${r.requestedBy}` : ''}</div></> },
          { key: 's', label: 'Result', render: (r) => <span title={r.error ?? ''}><Pill tone={r.status === 'succeeded' ? 'ok' : r.status === 'partial' ? 'warn' : r.status === 'running' ? 'info' : 'crit'}>
            {r.status === 'partial' ? 'Copy failed' : fmt.label(r.status)}</Pill></span> },
          { key: 'z', label: 'Size', render: (r) => r.sizeBytes ? fmt.bytes(r.sizeBytes) : '—' },
          { key: 'p', label: '', render: (r) => r.passwordProtected ? <span title="Opens with the backup password"><Icon name="lock" size={14} /></span> : null },
          { key: 'c', label: 'Copies', className: 'wrap', render: (r) => r.destinations.length === 0
            ? <span className="muted">{r.trigger === 'pre-upgrade' || r.trigger === 'pre-restore' ? 'Kept on this server' : '—'}</span>
            : <div className="copy-chips">{r.destinations.map((d) => <span key={d.id} title={d.message}><Pill tone={d.status === 'ok' ? 'ok' : 'crit'}>{d.name}</Pill></span>)}</div> },
          { key: 'e', label: 'Detail', className: 'wrap', render: (r) => r.error ? <span className="faint">{r.error}</span> : <span className="mono faint" style={{ fontSize: 11.5 }}>{r.file}</span> },
          { key: 'a', label: '', render: (r) => r.onServer && admin ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
            <a className="btn sm" href={`/api/v1/backup/files/${encodeURIComponent(r.file)}`} download><Icon name="down" size={13} /> Download</a>
            <button className="btn sm" onClick={() => onRestore(r.file)}>Restore…</button>
          </div> : null },
        ]} />
        {runs.data && <Pager page={page} pageSize={pageSize} total={runs.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}

/** After a restore was started: the console goes away while the data is replaced, then asks for a new sign-in. */
function RestoreProgress({ started }: { started: { note: string; since: number } }) {
  const [state, setState] = useState<'waiting' | 'down' | 'back' | 'failed'>('waiting');
  useEffect(() => {
    let stop = false;
    let polls = 0;
    async function poll() {
      polls++;
      try {
        const r = await api.get<BackupOverview>('/backup');
        if (stop) return;
        if (r.lastRestore && new Date(r.lastRestore.at).getTime() > started.since - 60_000) { setState('back'); return; }
        // The request ended without replacing the data (for example a wrong password): the job is gone and nothing changed.
        if (!r.running && polls > 1) { setState('failed'); return; }
      } catch (e) {
        if (stop) return;
        if (e instanceof ApiError && (e.status === 401 || e.status === 403)) { setState('back'); return; }
        setState('down');
      }
      if (!stop) setTimeout(() => void poll(), 10_000);
    }
    const t = setTimeout(() => void poll(), 5_000);
    return () => { stop = true; clearTimeout(t); };
  }, [started.since]);

  return (
    <Panel title="Restoring from backup">
      <div className="form-v">
        {(state === 'waiting' || state === 'down') && <Callout tone="info" icon="clock">{started.note}</Callout>}
        {state === 'waiting' && <p>Waiting for the Zentriq maintenance task to start the restore…</p>}
        {state === 'down' && <p><b>Restoring…</b> The console is stopped while the data is replaced. This page checks every 10 seconds.</p>}
        {state === 'failed' && <>
          <Callout tone="crit" icon="alert">The restore did not run; the current data is unchanged. The reason is in the notifications (bell) and in Jobs, or in the maintenance log on the server.</Callout>
          <button className="btn" style={{ alignSelf: 'flex-start' }} onClick={() => window.location.reload()}>Back to Backup &amp; Restore</button>
        </>}
        {state === 'back' && <>
          <Callout tone="ok" icon="info">The restore has finished. Sign in again with an account from the backup.</Callout>
          <button className="btn primary" style={{ alignSelf: 'flex-start' }} onClick={() => window.location.assign('/')}>Go to sign-in</button>
        </>}
      </div>
    </Panel>
  );
}
