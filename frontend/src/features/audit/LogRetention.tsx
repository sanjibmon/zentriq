import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Pager, Panel, Pill } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';

interface Schedule { enabled: boolean; everyMonths: 3 | 6 | 12; includeAudit: boolean; includeActivity: boolean }
interface Settings { auditRetentionDays: number; activityRetentionDays: number; archiveBeforeDelete: boolean; schedule: Schedule; destinationId: string | null }
interface LogInfo {
  settings: Settings; scheduleText: string; nextScheduled: string | null; auditCutoff: string | null; activityCutoff: string | null;
  audit: { rows: number; oldest: string | null }; activity: { rows: number; oldest: string | null };
  retentionChoices: number[]; destinations: { id: string; name: string; kind: string; enabled: boolean }[];
}
interface ArchiveRow {
  id: string; trigger: 'manual' | 'schedule' | 'before-delete'; from: string; to: string; includeAudit: boolean; includeActivity: boolean;
  fileName: string | null; size: number | null; sha256: string | null; auditRows: number | null; activityRows: number | null;
  status: 'queued' | 'running' | 'succeeded' | 'failed'; copy: { status: string; name?: string; message: string } | null; error: string | null;
  requestedBy: string | null; createdAt: string; finishedAt: string | null; available: boolean;
}

export function retentionText(days: number): string {
  if (days === 0) return 'Keep for ever';
  if (days % 365 === 0) return days === 365 ? '1 year' : `${days / 365} years`;
  return `${Math.round(days / 30)} months`;
}

const TRIGGER: Record<ArchiveRow['trigger'], string> = { manual: 'On request', schedule: 'Scheduled', 'before-delete': 'Before removal' };
const STATUS: Record<ArchiveRow['status'], ['ok' | 'crit' | 'warn' | '', string]> = { queued: ['warn', 'Waiting'], running: ['warn', 'Running'], succeeded: ['ok', 'Done'], failed: ['crit', 'Failed'] };

/** Audit Logs › Retention & export: how long logs are kept, scheduled exports, export now and the archive history. */
export function LogRetention() {
  const { can } = useAuth();
  const manage = can('settings.manage');
  const info = useApi(() => api.get<LogInfo>('/logs'), []);
  const [page, setPage] = useState(1);
  const archives = useApi(() => api.get<{ items: ArchiveRow[]; total: number }>('/logs/archives', { page, pageSize: 20 }), [page]);
  const [form, setForm] = useState<Settings | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [exporting, setExporting] = useState(false);
  const confirm = useConfirm();

  useEffect(() => { if (info.data) setForm(structuredClone(info.data.settings)); }, [info.data]);
  const waiting = (archives.data?.items ?? []).some((a) => a.status === 'queued' || a.status === 'running');
  useEffect(() => {
    if (!waiting) return;
    const t = setInterval(archives.reload, 10000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [waiting]);

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!form || !info.data) return;
    const shorter = form.auditRetentionDays !== 0 && (info.data.settings.auditRetentionDays === 0 || form.auditRetentionDays < info.data.settings.auditRetentionDays)
      || form.activityRetentionDays !== 0 && (info.data.settings.activityRetentionDays === 0 || form.activityRetentionDays < info.data.settings.activityRetentionDays);
    if (shorter && !(await confirm({
      title: 'Keep logs for a shorter time?', danger: !form.archiveBeforeDelete, confirmLabel: 'Save',
      message: <>Entries older than the new period are removed at the next maintenance run (within a few minutes).{form.archiveBeforeDelete ? ' They are exported to an archive first.' : <b> They are not archived first.</b>}</>,
    }))) return;
    setBusy(true); setMsg(null);
    try {
      await api.put('/logs/settings', { ...form, clearDestination: form.destinationId === null });
      setMsg({ tone: 'ok', text: 'Log retention saved.' });
      info.reload();
    } catch (err) { setMsg({ tone: 'crit', text: errorText(err) }); } finally { setBusy(false); }
  }

  async function remove(a: ArchiveRow) {
    if (!(await confirm({ title: 'Delete this archive?', danger: true, confirmLabel: 'Delete', message: <>{a.fileName ?? 'The history entry'} is deleted from the server. Copies at a destination are kept.</> }))) return;
    try { await api.del(`/logs/archives/${a.id}`); archives.reload(); } catch (err) { setMsg({ tone: 'crit', text: errorText(err) }); }
  }

  const d = info.data;
  const set = (patch: Partial<Settings>) => setForm((f) => (f ? { ...f, ...patch } : f));
  const setSchedule = (patch: Partial<Schedule>) => setForm((f) => (f ? { ...f, schedule: { ...f.schedule, ...patch } } : f));

  return (
    <>
      <ErrorBox error={info.error} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {d && form && (
        <form onSubmit={save}>
          <div className="grid-2">
            <Panel title="How long logs are kept" sub="Older entries are removed once a month, a whole month at a time. The audit log always keeps at least 90 days.">
              <div className="form-v">
                <label>Audit log
                  <select className="in" value={form.auditRetentionDays} disabled={!manage} onChange={(e) => set({ auditRetentionDays: Number(e.target.value) })}>
                    {d.retentionChoices.map((c) => <option key={c} value={c}>{retentionText(c)}{c === 365 ? ' (default)' : ''}</option>)}
                  </select>
                  <small className="muted">{d.audit.rows.toLocaleString()} entries{d.audit.oldest ? `, oldest ${fmt.day(d.audit.oldest)}` : ''}{d.auditCutoff ? ` · entries before ${fmt.day(d.auditCutoff)} are removed` : ''}</small>
                </label>
                <label>Activity log (device actions)
                  <select className="in" value={form.activityRetentionDays} disabled={!manage} onChange={(e) => set({ activityRetentionDays: Number(e.target.value) })}>
                    {d.retentionChoices.map((c) => <option key={c} value={c}>{retentionText(c)}{c === 365 ? ' (default)' : ''}</option>)}
                  </select>
                  <small className="muted">{d.activity.rows.toLocaleString()} entries{d.activity.oldest ? `, oldest ${fmt.day(d.activity.oldest)}` : ''}{d.activityCutoff ? ` · entries before ${fmt.day(d.activityCutoff)} are removed` : ''}</small>
                </label>
                <label className="row-check"><input type="checkbox" checked={form.archiveBeforeDelete} disabled={!manage} onChange={(e) => set({ archiveBeforeDelete: e.target.checked })} />
                  <span>Export entries to an archive before they are removed <span className="muted">(recommended)</span></span></label>
              </div>
            </Panel>
            <Panel title="Scheduled export" sub="Keeps a copy of the logs for as long as your organisation needs, outside the database.">
              <div className="form-v">
                <label className="row-check"><input type="checkbox" checked={form.schedule.enabled} disabled={!manage} onChange={(e) => setSchedule({ enabled: e.target.checked })} />
                  <span>Export the logs automatically</span></label>
                <label>Every
                  <select className="in" value={form.schedule.everyMonths} disabled={!manage || !form.schedule.enabled} onChange={(e) => setSchedule({ everyMonths: Number(e.target.value) as Schedule['everyMonths'] })}>
                    <option value={3}>3 months — each quarter (Jan–Mar, Apr–Jun …)</option>
                    <option value={6}>6 months — each half-year (Jan–Jun, Jul–Dec)</option>
                    <option value={12}>12 months — each calendar year</option>
                  </select>
                </label>
                <div className="row-actions">
                  <label className="row-check"><input type="checkbox" checked={form.schedule.includeAudit} disabled={!manage || !form.schedule.enabled} onChange={(e) => setSchedule({ includeAudit: e.target.checked })} /> <span>Audit log</span></label>
                  <label className="row-check"><input type="checkbox" checked={form.schedule.includeActivity} disabled={!manage || !form.schedule.enabled} onChange={(e) => setSchedule({ includeActivity: e.target.checked })} /> <span>Activity log</span></label>
                </div>
                <label>Also copy archives to
                  <select className="in" value={form.destinationId ?? ''} disabled={!manage} onChange={(e) => set({ destinationId: e.target.value || null })}>
                    <option value="">Only this server (Exports\LogArchives)</option>
                    {d.destinations.map((x) => <option key={x.id} value={x.id}>{x.name}{x.enabled ? '' : ' (disabled for backups)'}</option>)}
                  </select>
                  <small className="muted">Destinations are set up in Backup &amp; Restore (network share, SFTP, Azure, S3, Google Cloud …).</small>
                </label>
                {d.settings.schedule.enabled && d.nextScheduled && <small className="muted">Next export: after {fmt.day(d.nextScheduled)} ({d.scheduleText.toLowerCase()}).</small>}
              </div>
            </Panel>
          </div>
          {manage && <div className="row-actions" style={{ margin: '4px 0 16px' }}>
            <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
            <button className="btn" type="button" onClick={() => setForm(structuredClone(d.settings))} disabled={busy}>Undo changes</button>
          </div>}
        </form>
      )}
      <Panel flush title="Exported logs" sub="Each archive is a zip with audit.csv, activity.csv and a manifest with row counts and SHA-256 checksums."
        right={<button className="btn" onClick={() => setExporting(true)}><Icon name="down" size={14} /> Export now</button>}>
        <ErrorBox error={archives.error} />
        <DataTable rows={archives.data?.items ?? []} empty={archives.loading ? 'Loading…' : 'No log exports yet.'} columns={[
          { key: 'p', label: 'Period', render: (a) => <><b>{fmt.day(a.from)} – {fmt.day(new Date(new Date(a.to).getTime() - 1000).toISOString())}</b><div className="faint" style={{ fontSize: 11.5 }}>{[a.includeAudit && 'Audit', a.includeActivity && 'Activity'].filter(Boolean).join(' + ')}</div></> },
          { key: 't', label: 'Made', render: (a) => <>{TRIGGER[a.trigger]}<div className="faint" style={{ fontSize: 11.5 }}>{fmt.date(a.createdAt)}{a.requestedBy && a.trigger === 'manual' ? ` · ${a.requestedBy}` : ''}</div></> },
          { key: 's', label: 'Status', render: (a) => <Pill tone={STATUS[a.status][0]}>{STATUS[a.status][1]}</Pill> },
          { key: 'r', label: 'Entries', render: (a) => a.status !== 'succeeded' ? '' : <span className="faint">{[a.includeAudit && `${(a.auditRows ?? 0).toLocaleString()} audit`, a.includeActivity && `${(a.activityRows ?? 0).toLocaleString()} activity`].filter(Boolean).join(' · ')}{a.size != null ? ` · ${fmt.bytes(a.size)}` : ''}</span> },
          { key: 'c', label: 'Copy', className: 'wrap', render: (a) => a.error ? <span className="text-crit" style={{ fontSize: 12.5 }}>{a.error}</span>
            : a.copy ? <span style={{ fontSize: 12.5 }} className={a.copy.status === "ok" ? "" : "text-crit"}>{a.copy.message}</span> : <span className="faint">Server only</span> },
          { key: 'x', label: '', render: (a) => <div className="row-actions">
            {a.status === 'succeeded' && a.available && <a className="btn sm" href={`/api/v1/logs/archives/${a.id}/file`} download title={a.sha256 ? `SHA-256 ${a.sha256}` : undefined}><Icon name="down" size={13} /> Download</a>}
            {a.status === 'succeeded' && !a.available && <span className="faint">File removed</span>}
            {manage && a.status !== 'queued' && a.status !== 'running' && <button className="btn sm danger" onClick={() => void remove(a)} aria-label="Delete archive"><Icon name="trash" size={13} /></button>}
          </div> },
        ]} />
        {archives.data && archives.data.total > 20 && <Pager page={page} pageSize={20} total={archives.data.total} onPage={setPage} />}
      </Panel>
      {exporting && <ExportDialog onClose={() => setExporting(false)} onQueued={(t) => { setExporting(false); setMsg({ tone: 'ok', text: t }); archives.reload(); }} />}
    </>
  );
}

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
/** Local midnight of a yyyy-mm-dd date, as an ISO instant (the end date is inclusive, so the day after is sent). */
const startOf = (s: string, plusDays = 0) => { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d + plusDays).toISOString(); };

/** Export a period of the audit and / or activity log to an archive (made on the server). */
function ExportDialog({ onClose, onQueued }: { onClose: () => void; onQueued: (msg: string) => void }) {
  const { can } = useAuth();
  const now = new Date();
  const [from, setFrom] = useState(iso(new Date(now.getFullYear(), now.getMonth() - 3, 1)));
  const [to, setTo] = useState(iso(now));
  const [audit, setAudit] = useState(true);
  const [activity, setActivity] = useState(can('activity.view'));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ note: string }>('/logs/archives', { from: startOf(from), to: startOf(to, 1), includeAudit: audit, includeActivity: activity });
      onQueued(r.note);
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <Modal title="Export logs" onClose={onClose}
      footer={<><button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="log-export" className="btn primary" disabled={busy || (!audit && !activity) || !from || !to || to < from}>{busy ? 'Starting…' : 'Export'}</button></>}>
      <form id="log-export" className="form-v" onSubmit={submit}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="form-grid">
          <label>From<input className="in" type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></label>
          <label>To (inclusive)<input className="in" type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} /></label>
        </div>
        <div className="row-actions">
          <label className="row-check"><input type="checkbox" checked={audit} onChange={(e) => setAudit(e.target.checked)} /> <span>Audit log</span></label>
          {can('activity.view') && <label className="row-check"><input type="checkbox" checked={activity} onChange={(e) => setActivity(e.target.checked)} /> <span>Activity log</span></label>}
        </div>
        <small className="muted">The archive is made on the server within a few minutes, kept in Exports\LogArchives and copied to the chosen destination. For a quick spreadsheet of the list, use <b>Download CSV</b> on the log page instead.</small>
      </form>
    </Modal>
  );
}

/** "Download CSV" of a date range of the audit or activity log (streamed by the API). */
export function CsvExportDialog({ what, path, onClose }: { what: string; path: string; onClose: () => void }) {
  const now = new Date();
  const [from, setFrom] = useState(iso(new Date(now.getFullYear(), now.getMonth(), 1)));
  const [to, setTo] = useState(iso(now));
  const href = `/api/v1${path}?from=${encodeURIComponent(startOf(from))}&to=${encodeURIComponent(startOf(to, 1))}`;
  return (
    <Modal title={`Download the ${what} as CSV`} onClose={onClose}
      footer={<><button type="button" className="btn" onClick={onClose}>Close</button>
        <a className={`btn primary${!from || !to || to < from ? ' disabled' : ''}`} href={href} download onClick={() => setTimeout(onClose, 300)}><Icon name="down" size={14} /> Download</a></>}>
      <div className="form-v">
        <div className="form-grid">
          <label>From<input className="in" type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></label>
          <label>To (inclusive)<input className="in" type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} /></label>
        </div>
        <small className="muted">Times are in UTC. The download is recorded in the audit log.</small>
      </div>
    </Modal>
  );
}
