import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill, type Tone } from '../../components/ui';
import { errText } from '../deployment/types';
import { describeTargets, emptyTargets, stateOf, TargetPicker, targetsEmpty, targetsOf, type TargetState, type Targets } from '../patch/Targets';

interface Settings { enabled: boolean; keepOutputDays: number }
interface Summary { scripts: number; own: number; schedules: number; runs7d: number; running: number; succeeded7d: number; failed7d: number; remediated7d: number; agents: number; olderAgents: number; settings: Settings }
type Language = 'powershell' | 'cmd' | 'bash' | 'sh' | 'python';
interface Script {
  id: string; name: string; description: string | null; category: string; platform: 'windows' | 'linux'; language: Language; content: string; sha256: string;
  parameters: string | null; timeoutSeconds: number; builtIn: boolean; createdAt: string; updatedAt: string; updatedBy: string | null; lastRunAt: string | null;
}
interface Run {
  id: string; createdAt: string; name: string; note: string | null; parameters: string | null; scriptId: string | null; remediation: boolean; schedule: string | null; createdBy: string | null;
  devices: number; skipped: number; waiting: number; running: number; succeeded: number; failed: number; compliant: number; remediated: number;
}
interface RunDevice {
  assetId: string; device: string | null; status: string; detail: string | null; actionId: string | null; requestedAt: string | null; completedAt: string | null;
  exitCode: number | null; stdout: string | null; stderr: string | null; outcome: string | null; durationMs: number | null;
}
interface Schedule {
  id: string; name: string; enabled: boolean; scriptId: string; script: string; detectScriptId: string | null; detectScript: string | null; parameters: string | null; targets: Targets;
  frequency: 'once' | 'hourly' | 'daily' | 'weekly'; days: number[]; atTime: string; runOnceAt: string | null; nextRunAt: string | null; lastRunAt: string | null; platform: string; lastRunId: string | null;
}

const LANG: Record<Language, string> = { powershell: 'PowerShell', cmd: 'Command Prompt', bash: 'Bash', sh: 'sh', python: 'Python' };
const LANGS_OF: Record<'windows' | 'linux', Language[]> = { windows: ['powershell', 'cmd', 'python'], linux: ['bash', 'sh', 'python'] };
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const STATUS: Record<string, [Tone, string]> = {
  queued: ['warn', 'Waiting for device'], sent: ['warn', 'Sent to agent'], running: ['warn', 'Running'], succeeded: ['ok', 'Done'], failed: ['crit', 'Failed'], expired: ['', 'Expired'], skipped: ['', 'Skipped'],
};
const OUTCOME: Record<string, [Tone, string]> = { ok: ['ok', 'Exit 0'], failed: ['crit', 'Failed'], compliant: ['ok', 'Compliant'], remediated: ['info', 'Remediated'], timeout: ['crit', 'Time Limit'] };

type Tab = 'library' | 'runs' | 'schedules' | 'settings';
const TABS: [Tab, string][] = [['library', 'Script Library'], ['runs', 'Runs'], ['schedules', 'Schedules & Remediation'], ['settings', 'Settings']];

interface Ctx { tick: number; canManage: boolean; canRun: boolean; scripts: Script[]; changed: (t: string) => void; failed: (e: unknown) => void; run: (s: Script, remediation?: boolean) => void }

export function ScriptsPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'library';
  const setTab = (t: Tab) => navigate(`/modules/scripts?tab=${t}`, true);
  const [tick, setTick] = useState(0);
  const summary = useApi(() => api.get<Summary>('/scripts/summary'), [tick]);
  const scripts = useApi(() => api.get<Script[]>('/scripts'), [tick]);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [run, setRun] = useState<{ script: Script; remediation: boolean } | null>(null);
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const ctx: Ctx = { tick, canManage: can('scripts.manage'), canRun: can('scripts.run'), scripts: scripts.data ?? [], changed, failed, run: (s, r) => setRun({ script: s, remediation: r === true }) };
  const s = summary.data;
  return (
    <>
      <PageHead title="Scripts & Remediation" icon="terminal" />
      {s && <Kpis items={[
        { label: 'Scripts', value: s.scripts.toLocaleString(), sub: `${s.own} of your own`, to: '/modules/scripts?tab=library' },
        { label: 'Runs (7 d)', value: s.runs7d.toLocaleString(), sub: `${s.running} running now`, to: '/modules/scripts?tab=runs' },
        { label: 'Succeeded (7 d)', value: s.succeeded7d.toLocaleString(), sub: `${s.failed7d} failed`, to: '/modules/scripts?tab=runs' },
        { label: 'Remediated (7 d)', value: s.remediated7d.toLocaleString(), sub: 'Problems fixed', to: '/modules/scripts?tab=runs' },
        { label: 'Schedules', value: s.schedules.toLocaleString(), sub: 'On', to: '/modules/scripts?tab=schedules' },
      ]} />}
      {s && !s.settings.enabled && <Callout tone="warn" icon="alert">Running scripts is turned off for the organisation (Settings).</Callout>}
      {s && s.olderAgents > 0 && <Callout tone="info">{s.olderAgents} agent(s) are older than build 114 and cannot run scripts. Update them in <Link to="/agents">Agents</Link>.</Callout>}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{l}</button>)}
      </div>
      <ErrorBox error={summary.error ?? scripts.error} />
      {tab === 'library' && <LibraryTab {...ctx} loading={scripts.loading} />}
      {tab === 'runs' && <RunsTab {...ctx} openId={query.get('run')} />}
      {tab === 'schedules' && <SchedulesTab {...ctx} />}
      {tab === 'settings' && s && <SettingsTab settings={s.settings} {...ctx} />}
      {run && <RunDialog script={run.script} remediation={run.remediation} scripts={scripts.data ?? []} onClose={() => setRun(null)} onDone={(t) => { setRun(null); changed(t); }} />}
    </>
  );
}

// ---- library ----------------------------------------------------------------------------------------------------------

function LibraryTab({ scripts, loading, canManage, canRun, changed, failed, run }: Ctx & { loading: boolean }) {
  const [edit, setEdit] = useState<{ script: Script | null; copy?: boolean } | null>(null);
  const [view, setView] = useState<Script | null>(null);
  const [q, setQ] = useState('');
  const [platform, setPlatform] = useState('');
  const confirm = useConfirm();
  const rows = scripts.filter((s) => (!platform || s.platform === platform) && (!q || `${s.name} ${s.description ?? ''} ${s.category}`.toLowerCase().includes(q.toLowerCase())));
  async function remove(s: Script) {
    if (!(await confirm({ title: `Delete the script "${s.name}"?`, danger: true, confirmLabel: 'Delete', message: 'Runs keep their history and output.' }))) return;
    try { await api.del(`/scripts/${s.id}`); changed(`Script "${s.name}" deleted.`); } catch (e) { failed(e); }
  }
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search scripts…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search scripts" />
        <select className="in" value={platform} onChange={(e) => setPlatform(e.target.value)} aria-label="Platform"><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select>
        {canManage && <button className="btn primary" onClick={() => setEdit({ script: null })}><Icon name="plus" size={14} /> New Script</button>}
      </div>
      <Panel flush>
        <DataTable rows={rows} onRow={setView} rowTitle="Show the script" empty={loading ? 'Loading…' : 'No scripts.'} columns={[
          { key: 'n', label: 'Script', className: 'wrap', render: (s) => <><b>{s.name}</b> {s.builtIn && <Pill tone="info">Ready-Made</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{s.description}</div></> },
          { key: 'c', label: 'Category', render: (s) => s.category },
          { key: 'p', label: 'Platform', render: (s) => <>{s.platform === 'linux' ? 'Linux' : 'Windows'}<div className="faint" style={{ fontSize: 11.5 }}>{LANG[s.language]}</div></> },
          { key: 't', label: 'Time Limit', render: (s) => s.timeoutSeconds >= 60 ? `${Math.round(s.timeoutSeconds / 60)} min` : `${s.timeoutSeconds} s` },
          { key: 'l', label: 'Last Run', render: (s) => s.lastRunAt ? fmt.ago(s.lastRunAt) : <span className="faint">Never</span> },
          { key: 'x', label: '', render: (s) => <div className="row-actions">
            {canRun && <button className="btn sm primary" onClick={() => run(s)}><Icon name="play" size={12} /> Run</button>}
            {canManage && !s.builtIn && <button className="btn sm" onClick={() => setEdit({ script: s })}>Edit</button>}
            {canManage && <button className="btn sm" onClick={() => setEdit({ script: s, copy: true })}>Duplicate</button>}
            {canManage && !s.builtIn && <button className="btn sm danger" aria-label={`Delete ${s.name}`} onClick={() => void remove(s)}><Icon name="trash" size={13} /></button>}
          </div> },
        ]} />
      </Panel>
      {view && <Modal wide title={view.name} onClose={() => setView(null)} footer={<>
        {canRun && <button className="btn primary" onClick={() => { const v = view; setView(null); run(v); }}>Run</button>}
        <button className="btn" onClick={() => setView(null)}>Close</button>
      </>}>
        <div className="form-v">
          <div className="faint" style={{ fontSize: 12.5 }}>{view.platform === 'linux' ? 'Linux' : 'Windows'} · {LANG[view.language]} · runs as {view.platform === 'linux' ? 'root' : 'SYSTEM'} · time limit {view.timeoutSeconds} s{view.parameters ? ` · parameters: ${view.parameters}` : ''}</div>
          {view.description && <div>{view.description}</div>}
          <pre className="code-block" style={{ maxHeight: 420 }}>{view.content}</pre>
          <div className="faint mono" style={{ fontSize: 11 }}>SHA-256 {view.sha256}{view.updatedBy ? ` · changed by ${view.updatedBy} ${fmt.ago(view.updatedAt)}` : ''}</div>
        </div>
      </Modal>}
      {edit && <ScriptDialog script={edit.script} copy={edit.copy} onClose={() => setEdit(null)} onSaved={(t) => { setEdit(null); changed(t); }} />}
    </>
  );
}

export function ScriptDialog({ script, copy, onClose, onSaved }: { script: Script | null; copy?: boolean; onClose: () => void; onSaved: (m: string) => void }) {
  const [f, setF] = useState({
    name: script ? (copy ? `${script.name} (copy)` : script.name) : '', description: script?.description ?? '', category: script?.category ?? 'General',
    platform: script?.platform ?? 'windows', language: script?.language ?? 'powershell', content: script?.content ?? '', parameters: script?.parameters ?? '', timeoutSeconds: script?.timeoutSeconds ?? 600,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const editing = script && !copy;
  async function save() {
    setBusy(true); setError(null);
    try {
      const body = { ...f, parameters: f.parameters || null, description: f.description || null };
      if (editing) await api.put(`/scripts/${script!.id}`, body); else await api.post('/scripts', body);
      onSaved(`Script "${f.name}" saved.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={editing ? 'Edit Script' : 'New Script'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || !f.name.trim() || !f.content.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="form-grid-2">
          <label>Name<input className="in" value={f.name} maxLength={120} onChange={(e) => setF({ ...f, name: e.target.value })} /></label>
          <label>Category<input className="in" value={f.category} maxLength={60} onChange={(e) => setF({ ...f, category: e.target.value })} placeholder="e.g. Maintenance" /></label>
          <label>Platform<select className="in" value={f.platform} onChange={(e) => { const p = e.target.value as 'windows' | 'linux'; setF({ ...f, platform: p, language: LANGS_OF[p].includes(f.language) ? f.language : LANGS_OF[p][0]! }); }}>
            <option value="windows">Windows</option><option value="linux">Linux</option></select></label>
          <label>Language<select className="in" value={f.language} onChange={(e) => setF({ ...f, language: e.target.value as Language })}>{LANGS_OF[f.platform].map((l) => <option key={l} value={l}>{LANG[l]}</option>)}</select></label>
        </div>
        <label>Description<input className="in" value={f.description} maxLength={1000} onChange={(e) => setF({ ...f, description: e.target.value })} /></label>
        <label>Script<textarea className="in mono" rows={16} spellCheck={false} value={f.content} onChange={(e) => setF({ ...f, content: e.target.value })} style={{ fontSize: 12.5, whiteSpace: 'pre' }}
          placeholder={f.language === 'powershell' ? "Get-Service Spooler" : f.language === 'cmd' ? '@echo off\nipconfig /all' : f.language === 'python' ? 'print("hello")' : 'df -h'} /></label>
        <div className="form-grid-2">
          <label>Default parameters (optional)<input className="in mono" value={f.parameters} maxLength={1000} onChange={(e) => setF({ ...f, parameters: e.target.value })} placeholder={f.language === 'powershell' ? '-Name Spooler' : 'cron'} /></label>
          <label>Time limit (seconds)<input className="in" type="number" min={10} max={14400} value={f.timeoutSeconds} onChange={(e) => setF({ ...f, timeoutSeconds: Number(e.target.value) })} /></label>
        </div>
        <small className="muted">Scripts run as {f.platform === 'linux' ? 'root' : 'SYSTEM'} without a window. Exit code 0 means success; for a detection script, 0 means the device is fine and any other code runs the fix. Output is kept up to 64 KB per device.</small>
      </div>
    </Modal>
  );
}

// ---- run ----------------------------------------------------------------------------------------------------------------

function RunDialog({ script, remediation, scripts, onClose, onDone }: { script: Script; remediation: boolean; scripts: Script[]; onClose: () => void; onDone: (m: string) => void }) {
  const [targets, setTargets] = useState<TargetState>(emptyTargets());
  const [parameters, setParameters] = useState(script.parameters ?? '');
  const [withDetect, setWithDetect] = useState(remediation);
  const detectOptions = scripts.filter((s) => s.platform === script.platform && s.id !== script.id);
  const [detectId, setDetectId] = useState(detectOptions.find((s) => s.category === 'Remediation' && s.name.startsWith('Detect'))?.id ?? detectOptions[0]?.id ?? '');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const confirm = useConfirm();
  async function go() {
    if (targets.to === 'all' && !(await confirm({ title: `Run ${script.name} on all devices?`, message: `Every ${script.platform === 'linux' ? 'Linux' : 'Windows'} device with the Zentriq Agent runs it as ${script.platform === 'linux' ? 'root' : 'SYSTEM'}.`, confirmLabel: 'Run' }))) return;
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ id: string; sent: number; skipped: number }>('/scripts/runs', { scriptId: script.id, detectScriptId: withDetect && detectId ? detectId : null, parameters: parameters || null, note: note || null, ...targetsOf(targets) });
      onDone(`${script.name} runs on ${r.sent} device(s)${r.skipped ? `; ${r.skipped} skipped (other platform, no agent or an older agent)` : ''}. Follow it in Runs.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Run ${script.name}`} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || targetsEmpty(targets) || (withDetect && !detectId)} onClick={() => void go()}>{busy ? 'Starting…' : 'Run'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="faint" style={{ fontSize: 12.5 }}>{script.platform === 'linux' ? 'Linux' : 'Windows'} · {LANG[script.language]} · runs as {script.platform === 'linux' ? 'root' : 'SYSTEM'} · time limit {script.timeoutSeconds} s</div>
        <label>Parameters<input className="in mono" value={parameters} maxLength={1000} onChange={(e) => setParameters(e.target.value)} /></label>
        <label className="row-check"><input type="checkbox" checked={withDetect} onChange={(e) => setWithDetect(e.target.checked)} disabled={detectOptions.length === 0} /> Remediation: run a detection script first and this script only where it finds a problem</label>
        {withDetect && <label>Detection script<select className="in" value={detectId} onChange={(e) => setDetectId(e.target.value)}>{detectOptions.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>}
        <TargetPicker value={targets} onChange={setTargets} allLabel={`All ${script.platform === 'linux' ? 'Linux' : 'Windows'} devices with the Zentriq Agent`} />
        <label>Note (optional)<input className="in" value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. ticket 4711" /></label>
        <small className="muted">Online devices run it within seconds, the others when they next connect (up to 24 hours). Every run is written to the audit log.</small>
      </div>
    </Modal>
  );
}

// ---- runs -----------------------------------------------------------------------------------------------------------------

function RunsTab({ tick, canRun, changed, failed, openId }: Ctx & { openId: string | null }) {
  const list = useApi(() => api.get<Run[]>('/scripts/runs'), [tick]);
  const rows = list.data ?? [];
  const [open, setOpen] = useState<string | null>(openId);
  const active = rows.some((r) => r.waiting + r.running > 0);
  useEffect(() => {
    if (!active) return;
    const t = setInterval(list.reload, 5000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [active]);
  const current = rows.find((r) => r.id === open);
  return (
    <>
      <ErrorBox error={list.error} />
      <Panel flush>
        <DataTable rows={rows} onRow={(r) => setOpen(r.id)} rowTitle="Show the output" empty={list.loading ? 'Loading…' : 'No runs yet. Choose Run on a script in the library.'} columns={[
          { key: 't', label: 'Started', render: (r) => <span title={fmt.date(r.createdAt)}>{fmt.ago(r.createdAt)}</span> },
          { key: 'n', label: 'Script', className: 'wrap', render: (r) => <><b>{r.name}</b> {r.remediation && <Pill tone="info">Remediation</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{r.schedule ? `Scheduled (${r.schedule})` : r.createdBy ?? ''}{r.parameters ? ` · ${r.parameters}` : ''}{r.note ? ` · ${r.note}` : ''}</div></> },
          { key: 'd', label: 'Devices', render: (r) => <>{r.devices - r.skipped}{r.skipped > 0 && <span className="faint"> (+{r.skipped} skipped)</span>}</> },
          { key: 's', label: 'Result', render: (r) => <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
            {r.waiting > 0 && <Pill tone="warn">{r.waiting} waiting</Pill>}{r.running > 0 && <Pill tone="warn">{r.running} running</Pill>}
            {r.remediation ? <>{r.compliant > 0 && <Pill tone="ok">{r.compliant} compliant</Pill>}{r.remediated > 0 && <Pill tone="info">{r.remediated} remediated</Pill>}</> : r.succeeded > 0 && <Pill tone="ok">{r.succeeded} done</Pill>}
            {r.failed > 0 && <Pill tone="crit">{r.failed} failed</Pill>}</div> },
          { key: 'x', label: '', render: (r) => canRun && r.waiting > 0 ? <button className="btn sm" onClick={async () => {
            try { const c = await api.post<{ cancelled: number }>(`/scripts/runs/${r.id}/cancel`); changed(`${c.cancelled} waiting device(s) cancelled.`); } catch (e) { failed(e); }
          }}>Cancel Waiting</button> : null },
        ]} />
      </Panel>
      {open && <RunDialogView id={open} title={current?.name ?? 'Run'} onClose={() => setOpen(null)} />}
    </>
  );
}

function RunDialogView({ id, title, onClose }: { id: string; title: string; onClose: () => void }) {
  const list = useApi(() => api.get<RunDevice[]>(`/scripts/runs/${id}`), [id]);
  const rows = (list.data ?? []).map((d) => ({ ...d, id: d.assetId }));
  const [show, setShow] = useState<string | null>(null);
  const pending = rows.some((r) => ['queued', 'sent', 'running'].includes(r.status));
  useEffect(() => {
    if (!pending) return;
    const t = setInterval(list.reload, 4000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [pending]);
  useEffect(() => { if (!show && rows.length === 1 && rows[0]!.stdout !== null) setShow(rows[0]!.id); }, [rows, show]);
  const current = rows.find((r) => r.id === show);
  return (
    <Modal wide title={title} onClose={onClose} footer={<button className="btn" onClick={onClose}>Close</button>}>
      <div className="form-v">
        <ErrorBox error={list.error} />
        <DataTable rows={rows} onRow={(r) => setShow(r.id)} rowTitle="Show the output" empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
          { key: 'd', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}><b>{r.device ?? '—'}</b></Link> },
          { key: 's', label: 'Status', render: (r) => <>{r.outcome ? <Pill tone={OUTCOME[r.outcome]?.[0] ?? ''}>{OUTCOME[r.outcome]?.[1] ?? r.outcome}</Pill> : <Pill tone={STATUS[r.status]?.[0] ?? ''}>{STATUS[r.status]?.[1] ?? r.status}</Pill>}</> },
          { key: 'e', label: 'Exit Code', render: (r) => r.exitCode ?? <span className="faint">—</span> },
          { key: 'm', label: 'Detail', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.detail}{r.durationMs !== null ? <span className="faint"> · {(r.durationMs / 1000).toFixed(1)} s</span> : null}</span> },
          { key: 'o', label: '', render: (r) => r.stdout !== null || r.stderr !== null ? <button className="btn sm" onClick={() => setShow(r.id)}>Output</button> : null },
        ]} />
        {current && <>
          <b style={{ fontSize: 13 }}>Output on {current.device}</b>
          <pre className="code-block" style={{ maxHeight: 360, whiteSpace: 'pre-wrap' }}>{current.stdout || '(no output)'}</pre>
          {current.stderr && <><b style={{ fontSize: 13 }}>Errors</b><pre className="code-block" style={{ maxHeight: 220, whiteSpace: 'pre-wrap', color: '#ffb4a9' }}>{current.stderr}</pre></>}
        </>}
      </div>
    </Modal>
  );
}

// ---- schedules ------------------------------------------------------------------------------------------------------------

function SchedulesTab({ tick, canManage, scripts, changed, failed }: Ctx) {
  const list = useApi(() => api.get<Schedule[]>('/scripts/schedules'), [tick]);
  const [edit, setEdit] = useState<Schedule | 'new' | null>(null);
  const confirm = useConfirm();
  async function remove(s: Schedule) {
    if (!(await confirm({ title: `Delete the schedule "${s.name}"?`, danger: true, confirmLabel: 'Delete', message: 'Runs it started stay in the history.' }))) return;
    try { await api.del(`/scripts/schedules/${s.id}`); changed(`Schedule "${s.name}" deleted.`); } catch (e) { failed(e); }
  }
  const when = (s: Schedule) => s.frequency === 'once' ? `Once, ${fmt.date(s.runOnceAt)}` : s.frequency === 'hourly' ? `Every hour at :${s.atTime.slice(3)}`
    : s.frequency === 'daily' ? `Every day at ${s.atTime}` : `${s.days.map((d) => DAYS[d]).join(', ')} at ${s.atTime}`;
  return (
    <>
      <Panel title="Schedules & Remediation" sub="Scheduled runs (server time); a remediation runs its detection script first and the fix only where it finds a problem"
        right={canManage ? <button className="btn primary sm" onClick={() => setEdit('new')}><Icon name="plus" size={13} /> New Schedule</button> : undefined} flush>
        <ErrorBox error={list.error} />
        <DataTable rows={list.data ?? []} empty={list.loading ? 'Loading…' : 'No schedules yet.'} columns={[
          { key: 'n', label: 'Schedule', className: 'wrap', render: (s) => <><b>{s.name}</b> {!s.enabled && <Pill>Off</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{describeTargets(s.targets)}</div></> },
          { key: 's', label: 'Runs', className: 'wrap', render: (s) => s.detectScript ? <>If <b>{s.detectScript}</b> finds a problem: <b>{s.script}</b></> : <b>{s.script}</b> },
          { key: 'w', label: 'When', render: (s) => <>{when(s)}<div className="faint" style={{ fontSize: 11.5 }}>{s.nextRunAt ? `Next ${fmt.date(s.nextRunAt)}` : 'No next run'}</div></> },
          { key: 'l', label: 'Last Run', render: (s) => s.lastRunAt ? (s.lastRunId ? <Link to={`/modules/scripts?tab=runs&run=${s.lastRunId}`}>{fmt.ago(s.lastRunAt)}</Link> : fmt.ago(s.lastRunAt)) : <span className="faint">Not yet</span> },
          { key: 'x', label: '', render: (s) => canManage ? <div className="row-actions">
            <button className="btn sm" onClick={() => setEdit(s)}>Edit</button>
            <button className="btn sm danger" aria-label={`Delete ${s.name}`} onClick={() => void remove(s)}><Icon name="trash" size={13} /></button>
          </div> : null },
        ]} />
      </Panel>
      {edit && <ScheduleDialog schedule={edit === 'new' ? null : edit} scripts={scripts} onClose={() => setEdit(null)} onSaved={(t) => { setEdit(null); changed(t); }} />}
    </>
  );
}

export function ScheduleDialog({ schedule, scripts, onClose, onSaved }: { schedule: Schedule | null; scripts: Script[]; onClose: () => void; onSaved: (m: string) => void }) {
  const [f, setF] = useState({
    name: schedule?.name ?? '', enabled: schedule?.enabled ?? true, scriptId: schedule?.scriptId ?? scripts[0]?.id ?? '', detectScriptId: schedule?.detectScriptId ?? '',
    parameters: schedule?.parameters ?? '', frequency: schedule?.frequency ?? 'daily', days: schedule?.days ?? [1], atTime: schedule?.atTime ?? '02:00',
    runOnceAt: schedule?.runOnceAt ? schedule.runOnceAt.slice(0, 16) : '',
  });
  const [targets, setTargets] = useState<TargetState>(stateOf(schedule?.targets));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const script = scripts.find((s) => s.id === f.scriptId);
  const detects = scripts.filter((s) => s.platform === script?.platform && s.id !== f.scriptId);
  async function save() {
    setBusy(true); setError(null);
    try {
      const body = { ...f, detectScriptId: f.detectScriptId || null, parameters: f.parameters || null, runOnceAt: f.frequency === 'once' && f.runOnceAt ? new Date(f.runOnceAt).toISOString() : null, ...targetsOf(targets) };
      if (schedule) await api.put(`/scripts/schedules/${schedule.id}`, body); else await api.post('/scripts/schedules', body);
      onSaved(`Schedule "${f.name}" saved.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={schedule ? 'Edit Schedule' : 'New Schedule'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || !f.name.trim() || !f.scriptId || targetsEmpty(targets) || (f.frequency === 'weekly' && f.days.length === 0) || (f.frequency === 'once' && !f.runOnceAt)} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <label>Name<input className="in" value={f.name} maxLength={120} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Keep the Print Spooler running" /></label>
        <div className="form-grid-2">
          <label>Script (the fix, for a remediation)<select className="in" value={f.scriptId} onChange={(e) => setF({ ...f, scriptId: e.target.value, detectScriptId: '' })}>
            {scripts.map((s) => <option key={s.id} value={s.id}>{s.name} ({s.platform === 'linux' ? 'Linux' : 'Windows'})</option>)}</select></label>
          <label>Detection script (optional)<select className="in" value={f.detectScriptId} onChange={(e) => setF({ ...f, detectScriptId: e.target.value })}>
            <option value="">None — always run the script</option>{detects.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
        </div>
        <label>Parameters<input className="in mono" value={f.parameters} maxLength={1000} onChange={(e) => setF({ ...f, parameters: e.target.value })} placeholder={script?.parameters ?? ''} /></label>
        <TargetPicker value={targets} onChange={setTargets} />
        <div className="form-grid-2">
          <label>Frequency<select className="in" value={f.frequency} onChange={(e) => setF({ ...f, frequency: e.target.value as Schedule['frequency'] })}>
            <option value="once">Once</option><option value="hourly">Every hour</option><option value="daily">Every day</option><option value="weekly">Every week</option></select></label>
          {f.frequency === 'once'
            ? <label>At<input className="in" type="datetime-local" value={f.runOnceAt} onChange={(e) => setF({ ...f, runOnceAt: e.target.value })} /></label>
            : <label>{f.frequency === 'hourly' ? 'Minute (the hour is ignored)' : 'At (server time)'}<input className="in" type="time" value={f.atTime} onChange={(e) => setF({ ...f, atTime: e.target.value })} /></label>}
        </div>
        {f.frequency === 'weekly' && <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px' }}>
          {DAYS.map((d, i) => <label key={d} className="row-check"><input type="checkbox" checked={f.days.includes(i)} onChange={() => setF({ ...f, days: f.days.includes(i) ? f.days.filter((x) => x !== i) : [...f.days, i].sort() })} /> {d}</label>)}
        </div>}
        <label className="row-check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> On</label>
      </div>
    </Modal>
  );
}

// ---- settings -------------------------------------------------------------------------------------------------------------

function SettingsTab({ settings, changed, failed }: Ctx & { settings: Settings }) {
  const { can } = useAuth();
  const canSave = can('settings.manage');
  const [f, setF] = useState(settings);
  const [busy, setBusy] = useState(false);
  const confirm = useConfirm();
  async function save() {
    if (!f.enabled && settings.enabled && !(await confirm({ title: 'Turn off scripts?', message: 'No script runs on any device until it is turned on again; scheduled runs are skipped.', confirmLabel: 'Turn Off', danger: true }))) return;
    setBusy(true);
    try { await api.put('/scripts/settings', f); changed('Script settings saved.'); } catch (e) { failed(e); } finally { setBusy(false); }
  }
  return (
    <div className="grid-2">
      <Panel title="Scripts">
        <div className="form-v">
          <label className="row-check"><input type="checkbox" checked={f.enabled} disabled={!canSave} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Allow scripts to run on devices</label>
          <label>Keep the output (days)<input className="in" type="number" min={7} max={3650} value={f.keepOutputDays} disabled={!canSave} onChange={(e) => setF({ ...f, keepOutputDays: Number(e.target.value) })} /></label>
          {canSave ? <div><button className="btn primary" disabled={busy || JSON.stringify(f) === JSON.stringify(settings)} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>
            : <small className="muted">Needs the System Settings permission.</small>}
        </div>
      </Panel>
      <Panel title="Security">
        <div style={{ fontSize: 13.5, display: 'grid', gap: 8 }}>
          <div>Scripts run as <b>SYSTEM</b> (Windows) or <b>root</b> (Linux). Only roles with <i>Run scripts</i> can start them, and only roles with <i>Manage scripts</i> can change the library.</div>
          <div>The agent checks every script against its SHA-256 from the server before it runs it, over the agent's mutually authenticated connection.</div>
          <div>Every change, run and schedule is written to the audit log with the script's SHA-256.</div>
        </div>
      </Panel>
    </div>
  );
}
