import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart, HBars } from '../../components/charts';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import {
  APPLIES_LABEL, RESULT_LABEL, resultTone, SCORE_COLOR, secSevTone,
  type BaselineDetail, type BaselineSummary, type DeviceScore, type RuleDetail, type SecException, type SecOverview, type SecRule,
} from './types';

type Tab = 'overview' | 'checks' | 'devices' | 'baselines' | 'exceptions';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['checks', 'Checks'], ['devices', 'Devices'], ['baselines', 'Baselines'], ['exceptions', 'Exceptions']];
const cap = (s: string) => (s ? s[0]!.toUpperCase() + s.slice(1) : s);
const err = (e: unknown) => (e instanceof Error ? e.message : String(e));

export function SecurityPage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const [baselineId, setBaselineId] = useState<string | null>(query.get('baseline'));
  const [rule, setRule] = useState<string | null>(query.get('rule'));
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit' | 'ok'; text: string } | null>(null);
  const go = (t: Tab) => navigate(`/security?tab=${t}`, true);
  const baselines = useApi(() => api.get<BaselineSummary[]>('/security/baselines'), []);

  async function evaluate() {
    setMsg(null);
    try { setMsg({ tone: 'info', text: (await api.post<{ message: string }>('/security/evaluate')).message }); }
    catch (e) { setMsg({ tone: 'crit', text: err(e) }); }
  }

  const picker = (baselines.data?.length ?? 0) > 1 && (tab === 'overview' || tab === 'checks' || tab === 'devices') && (
    <select className="in" aria-label="Baseline" value={baselineId ?? ''} onChange={(e) => setBaselineId(e.target.value || null)}>
      {baselines.data!.map((b) => <option key={b.id} value={b.id}>{b.name}{b.enabled ? '' : ' (off)'}</option>)}
    </select>
  );
  return (
    <>
      <PageHead title="Security Configuration" desc="Harden every Windows device against CIS-aligned baselines."
        actions={<>{picker}{can('security.manage') && <button className="btn primary" onClick={() => void evaluate()}><Icon name="retry" size={13} /> Evaluate now</button>}</>} />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => go(t)}>{l}</button>)}
      </div>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text} {msg.tone === 'info' && <Link to="/activity">Activity log</Link>}</Callout>}
      {tab === 'overview' && <Overview baselineId={baselineId} onRule={setRule} />}
      {tab === 'checks' && <Checks baselineId={baselineId} onRule={setRule} />}
      {tab === 'devices' && <Devices baselineId={baselineId} />}
      {tab === 'baselines' && <Baselines onChanged={baselines.reload} />}
      {tab === 'exceptions' && <Exceptions />}
      {rule && <RuleDrawer id={rule} baselineId={baselineId} onClose={() => setRule(null)} />}
    </>
  );
}

// ---- Overview ---------------------------------------------------------------------------------------

function Overview({ baselineId, onRule }: { baselineId: string | null; onRule: (id: string) => void }) {
  const { navigate } = useRouter();
  const o = useApi(() => api.get<SecOverview>('/security/overview', { baselineId }), [baselineId]);
  if (o.error) return <ErrorBox error={o.error} />;
  if (!o.data) return <p className="muted">Loading…</p>;
  const { kpi: k, coverage: c } = o.data;
  const band: Record<string, string> = { '0–49': 'var(--crit)', '50–69': '#e8590c', '70–89': 'var(--warn)', '90–100': 'var(--ok)' };
  return (
    <>
      {c.windowsDevices === 0 && <Callout tone="info" icon="info">No Windows device inventory yet. Install the agent or run an agentless scan; devices are checked every 30 minutes.</Callout>}
      {c.noSettingsData > 0 && <Callout tone="warn" icon="info">{c.noSettingsData} of {c.windowsDevices} Windows device(s) have not reported their security settings yet, so many checks show as <i>unknown</i>. Update their agent (Agents › Update) or collect inventory again.</Callout>}
      {c.windowsDevices > 0 && !o.data.lastEvaluation && <Callout tone="info" icon="info">Devices have not been evaluated yet. The first evaluation runs within 30 minutes, or click <b>Evaluate now</b>.</Callout>}
      <Kpis items={[
        { label: 'Average score', value: <span style={{ color: SCORE_COLOR(k.avgScore) }}>{k.avgScore ?? '—'}</span>, sub: `${k.devices.toLocaleString()} device(s) assessed`, to: '/security?tab=devices' },
        { label: 'Compliant devices', value: `${k.compliant.toLocaleString()} / ${k.devices.toLocaleString()}`, sub: 'no failed high-severity check' },
        { label: 'Failed checks', value: k.failedChecks.toLocaleString(), sub: `${k.failedHigh.toLocaleString()} high severity`, to: '/security?tab=checks' },
        { label: 'Score below 70', value: <span style={{ color: k.poor ? 'var(--crit)' : undefined }}>{k.poor.toLocaleString()}</span>, sub: `${k.good.toLocaleString()} device(s) at 90 or more`, to: '/security?tab=devices' },
        { label: 'Unknown results', value: k.unknown.toLocaleString(), sub: 'settings not reported' },
        { label: 'Exceptions', value: o.data.exceptions.toLocaleString(), sub: 'active, documented', to: '/security?tab=exceptions' },
      ]} />
      <div className="grid-3">
        <Panel title="Devices by score">
          {o.data.distribution.length === 0 ? <div className="chart-empty">No devices assessed yet.</div> :
            <CategoryChart kind="column" data={o.data.distribution.map((d) => ({ key: d.bucket, label: d.bucket, value: d.count, color: band[d.bucket] }))} onSelect={() => navigate('/security?tab=devices')} />}
        </Panel>
        <Panel title="Failed checks by area">
          <HBars data={o.data.byCategory.filter((x) => x.fail > 0).map((x) => ({ key: x.category, label: x.category, value: x.fail, color: 'var(--crit)' }))} onSelect={() => navigate('/security?tab=checks')} />
          {o.data.byCategory.every((x) => x.fail === 0) && <div className="chart-empty">No failed checks.</div>}
        </Panel>
        <Panel title="Score trend" sub="Average score, last 30 days">
          <ScoreTrend points={o.data.trend} />
        </Panel>
      </div>
      <div className="grid-2" style={{ marginTop: 14 }}>
        <Panel title="Most failed checks" flush>
          <DataTable rows={o.data.topFailing.map((r) => ({ ...r, id: r.rule }))} empty="No failing checks." columns={[
            { key: 'r', label: 'Check', className: 'wrap', render: (r) => <button className="linkish" onClick={() => onRule(r.rule)}><span className="mono">{r.rule}</span> {r.title}</button> },
            { key: 's', label: 'Severity', render: (r) => <Pill tone={secSevTone(r.severity)}>{cap(r.severity)}</Pill> },
            { key: 'f', label: 'Failing', render: (r) => `${r.failing} / ${r.assessed}` },
            { key: 'p', label: 'Compliance', render: (r) => <ScoreBar v={r.assessed ? Math.round(100 * (r.assessed - r.failing) / r.assessed) : null} /> },
          ]} />
        </Panel>
        <Panel title="Lowest-scoring devices" flush>
          <DataTable rows={o.data.worst.map((d) => ({ ...d, id: d.assetId }))} empty="No devices assessed yet." columns={[
            { key: 'h', label: 'Device', render: (d) => <Link to={`/assets/${d.assetId}?tab=security`}>{d.hostname ?? d.assetId}</Link> },
            { key: 's', label: 'Score', render: (d) => <ScoreBar v={d.score} /> },
            { key: 'f', label: 'Failed', render: (d) => <>{d.failed}{d.failedHigh > 0 && <span className="faint"> · {d.failedHigh} high</span>}</> },
            { key: 'c', label: 'Criticality', render: (d) => d.criticality },
          ]} />
        </Panel>
      </div>
      {o.data.lastEvaluation && <p className="faint" style={{ fontSize: 12 }}>Last evaluation {fmt.ago(o.data.lastEvaluation)} ({fmt.date(o.data.lastEvaluation)}). Devices are re-checked every 30 minutes with their latest inventory.</p>}
    </>
  );
}

export function ScoreBar({ v }: { v: number | null }) {
  return <span className="risk-bar" title={v == null ? 'Not scored' : `Score ${v} / 100`}><span className="track"><i style={{ width: `${v ?? 0}%`, background: SCORE_COLOR(v) }} /></span><b>{v ?? '—'}</b></span>;
}

function ScoreTrend({ points }: { points: SecOverview['trend'] }) {
  const pts = points.filter((p) => p.avgScore != null);
  if (pts.length === 0) return <div className="chart-empty">The trend starts after the first evaluation.</div>;
  const w = 300, h = 140, pad = 18;
  const x = (i: number) => pad + (i * (w - 2 * pad)) / Math.max(1, points.length - 1);
  const y = (v: number) => h - pad - ((v / 100) * (h - 2 * pad));
  const path = points.map((p, i) => (p.avgScore == null ? null : `${x(i)},${y(p.avgScore)}`)).filter(Boolean).join(' ');
  const last = pts[pts.length - 1]!;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="score-trend" role="img" aria-label={`Average score ${last.avgScore}`}>
      {[0, 50, 100].map((g) => <g key={g}><line x1={pad} x2={w - pad} y1={y(g)} y2={y(g)} stroke="var(--line)" /><text x={2} y={y(g) + 4} fontSize="9" fill="var(--muted)">{g}</text></g>)}
      <polyline points={path} fill="none" stroke="var(--accent)" strokeWidth="2" />
      {points.map((p, i) => p.avgScore != null && <circle key={p.day} cx={x(i)} cy={y(p.avgScore)} r="2.4" fill="var(--accent)"><title>{`${p.day}: ${p.avgScore} (${p.devices} devices, ${p.compliant} compliant)`}</title></circle>)}
    </svg>
  );
}

// ---- Checks ------------------------------------------------------------------------------------------

function Checks({ baselineId, onRule }: { baselineId: string | null; onRule: (id: string) => void }) {
  const r = useApi(() => api.get<{ baselineId: string | null; rules: SecRule[] }>('/security/rules', { baselineId }), [baselineId]);
  const [cat, setCat] = useState('');
  const [onlyFailing, setOnlyFailing] = useState(false);
  if (r.error) return <ErrorBox error={r.error} />;
  if (!r.data) return <p className="muted">Loading…</p>;
  const cats = [...new Set(r.data.rules.map((x) => x.category))];
  const rows = r.data.rules.filter((x) => (!cat || x.category === cat) && (!onlyFailing || x.fail > 0));
  return (
    <Panel flush title="Checks" sub={`${r.data.rules.filter((x) => x.enabled).length} of ${r.data.rules.length} on in this baseline`} right={<div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
      <select className="in" aria-label="Area" value={cat} onChange={(e) => setCat(e.target.value)}><option value="">All areas</option>{cats.map((c) => <option key={c}>{c}</option>)}</select>
      <label className="check-inline"><input type="checkbox" checked={onlyFailing} onChange={(e) => setOnlyFailing(e.target.checked)} /> Failing only</label>
    </div>}>
      <DataTable rows={rows} empty="No checks match." columns={[
        { key: 'id', label: 'Check', className: 'wrap', render: (x) => <button className="linkish" onClick={() => onRule(x.id)} style={{ textAlign: 'left' }}><span className="mono">{x.id}</span> {x.title}<div className="faint" style={{ fontSize: 11.5 }}>{x.category} · {APPLIES_LABEL[x.applies]} · {x.reference}</div></button> },
        { key: 's', label: 'Severity', render: (x) => <Pill tone={secSevTone(x.severity)}>{cap(x.severity)}</Pill> },
        { key: 'on', label: 'In baseline', render: (x) => x.enabled ? 'On' : <span className="faint">Off</span> },
        { key: 'p', label: 'Pass', render: (x) => x.pass || '' },
        { key: 'f', label: 'Fail', render: (x) => x.fail ? <b style={{ color: 'var(--crit)' }}>{x.fail}</b> : '' },
        { key: 'u', label: 'Unknown', render: (x) => x.unknown || '' },
        { key: 'e', label: 'Exception', render: (x) => x.exempt || '' },
        { key: 'c', label: 'Compliance', render: (x) => <ScoreBar v={x.pass + x.fail ? Math.round(100 * x.pass / (x.pass + x.fail)) : null} /> },
      ]} />
    </Panel>
  );
}

/** One check: why it matters, how to fix it (guidance only), the devices and the exceptions. */
function RuleDrawer({ id, baselineId, onClose }: { id: string; baselineId: string | null; onClose: () => void }) {
  const { can } = useAuth();
  const confirm = useConfirm();
  const [status, setStatus] = useState('fail');
  const d = useApi(() => api.get<RuleDetail>(`/security/rules/${encodeURIComponent(id)}`, { baselineId, status: status || undefined }), [id, baselineId, status]);
  const [exc, setExc] = useState<{ assetIds: string[] | null; label: string } | null>(null);
  const [note, setNote] = useState<string | null>(null);
  async function remove(x: SecException) {
    if (!(await confirm({ title: 'Remove this exception?', message: `${x.hostname ?? 'All devices'}: ${x.reason}`, confirmLabel: 'Remove', danger: true }))) return;
    await api.del(`/security/exceptions/${x.id}`);
    d.reload();
  }
  const r = d.data?.rule;
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="rule-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal wide">
        <div className="modal-h">
          <h2 id="rule-title"><span className="mono">{id}</span> {r?.title}</h2>
          {r && <Pill tone={secSevTone(r.severity)}>{cap(r.severity)}</Pill>}
          <span style={{ flex: 1 }} />
          <button className="btn sm" onClick={onClose}>Close</button>
        </div>
        <div className="modal-b">
          <ErrorBox error={d.error} />
          {note && <Callout tone="ok" icon="info">{note}</Callout>}
          {r && <>
            <dl className="kv-grid" style={{ margin: '0 0 12px' }}>
              <div><dt>Area</dt><dd>{r.category}</dd></div>
              <div><dt>Applies to</dt><dd>{APPLIES_LABEL[r.applies]}</dd></div>
              <div><dt>Reference</dt><dd>{r.reference}</dd></div>
            </dl>
            <h4 style={{ margin: '0 0 4px' }}>Why it matters</h4>
            <p style={{ marginTop: 0 }}>{r.rationale}</p>
            <h4 style={{ margin: '12px 0 4px' }}>How to fix it</h4>
            <div className="callout info" style={{ marginTop: 0 }}><Icon name="info" /><div>{r.remediation}<div className="faint" style={{ fontSize: 12, marginTop: 4 }}>Apply the setting with Group Policy, Intune or your configuration tool; Zentriq re-checks devices after their next inventory.</div></div></div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '14px 0 6px' }}>
              <h4 style={{ margin: 0 }}>Devices</h4>
              <div className="seg">
                {[['fail', 'Failing'], ['unknown', 'Unknown'], ['exempt', 'Exceptions'], ['pass', 'Passing'], ['', 'All']].map(([v, l]) =>
                  <button key={v} type="button" className={status === v ? 'on' : ''} onClick={() => setStatus(v!)}>{l}</button>)}
              </div>
              <span style={{ flex: 1 }} />
              {can('security.manage') && <button className="btn sm" onClick={() => setExc({ assetIds: null, label: 'all devices' })}>Exception for all devices…</button>}
            </div>
            <DataTable rows={d.data!.devices.map((x) => ({ ...x, id: x.assetId }))} empty="No devices in this list." columns={[
              { key: 'h', label: 'Device', render: (x) => <Link to={`/assets/${x.assetId}?tab=security`} onClick={onClose}>{x.hostname ?? x.assetId}</Link> },
              { key: 's', label: 'Result', render: (x) => <Pill tone={resultTone(x.status)}>{RESULT_LABEL[x.status] ?? x.status}</Pill> },
              { key: 'a', label: 'Found', className: 'wrap', render: (x) => <span className="clamp-2">{x.actual ?? '—'}</span> },
              { key: 'f', label: 'Failing since', render: (x) => x.failingSince ? fmt.day(x.failingSince) : '' },
              { key: 'x', label: '', render: (x) => can('security.manage') && x.status !== 'exempt' && x.status !== 'pass' ? <button className="btn sm" onClick={() => setExc({ assetIds: [x.assetId], label: x.hostname ?? 'this device' })}>Exception…</button> : null },
            ]} />
            {d.data!.exceptions.length > 0 && <>
              <h4 style={{ margin: '14px 0 6px' }}>Exceptions for this check</h4>
              <DataTable rows={d.data!.exceptions} columns={[
                { key: 'd', label: 'Scope', render: (x) => x.hostname ?? <i>All devices</i> },
                { key: 'r', label: 'Reason', className: 'wrap', render: (x) => x.reason },
                { key: 'e', label: 'Expires', render: (x) => x.expiresAt ? <>{fmt.day(x.expiresAt)}{x.expired && <Pill tone="warn">expired</Pill>}</> : 'No expiry' },
                { key: 'b', label: 'By', render: (x) => x.createdBy ?? '—' },
                { key: 'x', label: '', render: (x) => can('security.manage') ? <button className="btn sm" onClick={() => void remove(x)}>Remove</button> : null },
              ]} />
            </>}
          </>}
        </div>
      </div>
      {exc && <ExceptionDialog ruleId={id} assetIds={exc.assetIds} label={exc.label} baselineId={null} onClose={() => setExc(null)}
        onSaved={(m) => { setExc(null); setNote(m); d.reload(); }} />}
    </div>
  );
}

function ExceptionDialog({ ruleId, assetIds, label, baselineId, onClose, onSaved }: { ruleId: string; assetIds: string[] | null; label: string; baselineId: string | null; onClose: () => void; onSaved: (msg: string) => void }) {
  const [reason, setReason] = useState('');
  const [expires, setExpires] = useState(new Date(Date.now() + 180 * 86400000).toISOString().slice(0, 10));
  const [never, setNever] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ message: string }>('/security/exceptions', { ruleId, assetIds, baselineId, reason, expiresAt: never ? null : new Date(`${expires}T23:59:59`).toISOString() });
      onSaved(r.message);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="sx-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="sx-title">Exception: {ruleId} for {label}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>The check is shown as <b>Exception</b> instead of failed and leaves the score. Record why (business need, compensating control) — it is kept for audit.</p>
          <label>Reason / compensating control
            <textarea className="in" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} required minLength={5} maxLength={1000} placeholder="e.g. Print server — Print Spooler required; access limited by firewall" />
          </label>
          <label className="check-inline"><input type="checkbox" checked={never} onChange={(e) => setNever(e.target.checked)} /> No expiry date</label>
          {!never && <label>Review / expires on
            <input className="in" type="date" value={expires} min={new Date(Date.now() + 86400000).toISOString().slice(0, 10)} max={new Date(Date.now() + 730 * 86400000).toISOString().slice(0, 10)} onChange={(e) => setExpires(e.target.value)} required />
          </label>}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || reason.trim().length < 5}>{busy ? 'Saving…' : 'Record exception'}</button>
        </footer>
      </form>
    </div>
  );
}

// ---- Devices -----------------------------------------------------------------------------------------

function Devices({ baselineId }: { baselineId: string | null }) {
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState('worst');
  const [page, setPage] = useState(1);
  useEffect(() => { const t = setTimeout(() => { setSearch(q); setPage(1); }, 300); return () => clearTimeout(t); }, [q]);
  const d = useApi(() => api.get<Paged<DeviceScore>>('/security/devices', { baselineId, q: search, sort, page, pageSize: 50 }), [baselineId, search, sort, page]);
  return (
    <Panel flush title="Devices" sub={d.data ? `${d.data.total.toLocaleString()} assessed` : undefined} right={<div style={{ display: 'flex', gap: 8 }}>
      <input className="in" placeholder="Hostname or IP…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search devices" />
      <select className="in" value={sort} onChange={(e) => setSort(e.target.value)} aria-label="Sort"><option value="worst">Lowest score first</option><option value="best">Highest score first</option><option value="failed">Most failed</option><option value="host">Hostname</option></select>
    </div>}>
      <ErrorBox error={d.error} />
      <DataTable rows={(d.data?.items ?? []).map((x) => ({ ...x, id: x.assetId }))} empty={d.loading ? 'Loading…' : 'No Windows devices assessed yet.'} columns={[
        { key: 'h', label: 'Device', render: (x) => <Link to={`/assets/${x.assetId}?tab=security`}>{x.hostname ?? x.assetId}</Link> },
        { key: 'o', label: 'Operating system', render: (x) => x.os ?? '—' },
        { key: 'c', label: 'Category', render: (x) => fmt.label(x.category) },
        { key: 's', label: 'Score', render: (x) => <ScoreBar v={x.score} /> },
        { key: 'f', label: 'Failed', render: (x) => x.failed ? <b style={{ color: 'var(--crit)' }}>{x.failed}</b> : '0' },
        { key: 'fh', label: 'High', render: (x) => x.failedHigh || '' },
        { key: 'p', label: 'Passed', render: (x) => x.passed },
        { key: 'u', label: 'Unknown', render: (x) => x.unknown || '' },
        { key: 'e', label: 'Checked', render: (x) => <span title={fmt.date(x.evaluatedAt)}>{fmt.ago(x.evaluatedAt)}</span> },
      ]} />
      {d.data && <Pager page={page} pageSize={50} total={d.data.total} onPage={setPage} />}
    </Panel>
  );
}

// ---- Baselines ---------------------------------------------------------------------------------------

function Baselines({ onChanged }: { onChanged: () => void }) {
  const { can } = useAuth();
  const confirm = useConfirm();
  const list = useApi(() => api.get<BaselineSummary[]>('/security/baselines'), []);
  const [edit, setEdit] = useState<{ id: string | null; copyFrom?: string } | null>(null);
  const manage = can('security.manage');
  async function remove(b: BaselineSummary) {
    if (!(await confirm({ title: `Delete the baseline "${b.name}"?`, message: 'Its results and exceptions limited to it are removed.', danger: true }))) return;
    await api.del(`/security/baselines/${b.id}`);
    list.reload(); onChanged();
  }
  return (
    <>
      <Callout tone="info" icon="info">A baseline is a set of checks with your severities and thresholds, for all Windows devices or only servers or workstations. Every enabled baseline is evaluated; each device gets a score per baseline.</Callout>
      <Panel flush title="Baselines" right={manage && <button className="btn sm primary" onClick={() => setEdit({ id: null, copyFrom: list.data?.find((b) => b.builtIn)?.id })}><Icon name="plus" size={12} /> New baseline</button>}>
        <ErrorBox error={list.error} />
        <DataTable rows={list.data ?? []} empty="No baselines." columns={[
          { key: 'n', label: 'Name', className: 'wrap', render: (b) => <><b>{b.name}</b>{b.builtIn && <Pill tone="info">Built-in</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{b.description}</div></> },
          { key: 's', label: 'Scope', render: (b) => b.categories.length === 0 ? 'All Windows devices' : b.categories.map((c) => (c === 'server' ? 'Servers' : 'Workstations')).join(', ') },
          { key: 'r', label: 'Checks', render: (b) => b.rules },
          { key: 'd', label: 'Devices', render: (b) => b.devices },
          { key: 'a', label: 'Average score', render: (b) => <ScoreBar v={b.avgScore} /> },
          { key: 'e', label: 'Status', render: (b) => b.enabled ? <Pill tone="ok">On</Pill> : <Pill>Off</Pill> },
          { key: 'u', label: 'Changed', render: (b) => <span title={b.updatedBy ?? ''}>{fmt.ago(b.updatedAt)}</span> },
          { key: 'x', label: '', render: (b) => manage ? <div style={{ display: 'flex', gap: 6 }}>
            <button className="btn sm" onClick={() => setEdit({ id: b.id })}>Edit</button>
            <button className="btn sm" onClick={() => setEdit({ id: null, copyFrom: b.id })}>Copy</button>
            {!b.builtIn && <button className="btn sm" onClick={() => void remove(b)}>Delete</button>}
          </div> : <button className="btn sm" onClick={() => setEdit({ id: b.id })}>View</button> },
        ]} />
      </Panel>
      {edit && <BaselineEditor id={edit.id} copyFrom={edit.copyFrom} readOnly={!manage} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); list.reload(); onChanged(); }} />}
    </>
  );
}

function BaselineEditor({ id, copyFrom, readOnly, onClose, onSaved }: { id: string | null; copyFrom?: string; readOnly: boolean; onClose: () => void; onSaved: () => void }) {
  const src = id ?? copyFrom;
  const d = useApi(() => (src ? api.get<BaselineDetail>(`/security/baselines/${src}`) : Promise.resolve(null)), [src]);
  const [form, setForm] = useState<BaselineDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (d.data) setForm(id ? d.data : { ...d.data, id: '', name: `${d.data.name} (copy)`, builtIn: false, description: d.data.description });
  }, [d.data, id]);
  if (!form) return null;
  const set = (p: Partial<BaselineDetail>) => setForm({ ...form, ...p });
  const setRule = (rid: string, p: Partial<BaselineDetail['rules'][number]>) => set({ rules: form.rules.map((r) => (r.id === rid ? { ...r, ...p } : r)) });
  const toggleCat = (c: string) => set({ categories: form.categories.includes(c) ? form.categories.filter((x) => x !== c) : [...form.categories, c] });
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    const body = {
      name: form!.name, description: form!.description, enabled: form!.enabled, categories: form!.categories,
      rules: form!.rules.map((r) => ({ id: r.id, enabled: r.enabled, severity: r.severity === r.defaultSeverity ? null : r.severity, params: Object.fromEntries(r.params.filter((p) => p.value !== p.default).map((p) => [p.key, p.value])) })),
    };
    try {
      if (id) await api.put(`/security/baselines/${id}`, body); else await api.post('/security/baselines', body);
      onSaved();
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  const groups = [...new Set(form.rules.map((r) => r.category))];
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="bl-title">
      <form className="modal wide" onSubmit={save}>
        <div className="modal-h">
          <h2 id="bl-title">{readOnly ? form.name : id ? `Edit baseline` : 'New baseline'}</h2>
          <span style={{ flex: 1 }} />
          <button type="button" className="btn sm" onClick={onClose}>{readOnly ? 'Close' : 'Cancel'}</button>
          {!readOnly && <button type="submit" className="btn sm primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>}
        </div>
        <div className="modal-b">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <fieldset disabled={readOnly} style={{ border: 0, padding: 0, margin: 0 }}>
            <div className="form-grid-2">
              <label>Name<input className="in" value={form.name} onChange={(e) => set({ name: e.target.value })} required maxLength={120} /></label>
              <label>Description<input className="in" value={form.description ?? ''} onChange={(e) => set({ description: e.target.value })} maxLength={500} /></label>
            </div>
            <div style={{ display: 'flex', gap: 18, margin: '10px 0', flexWrap: 'wrap' }}>
              <span className="muted">Applies to:</span>
              <label className="check-inline"><input type="checkbox" checked={form.categories.length === 0 || form.categories.includes('server')} onChange={() => toggleCat('server')} /> Servers</label>
              <label className="check-inline"><input type="checkbox" checked={form.categories.length === 0 || form.categories.includes('endpoint')} onChange={() => toggleCat('endpoint')} /> Workstations</label>
              <label className="check-inline"><input type="checkbox" checked={form.enabled} onChange={(e) => set({ enabled: e.target.checked })} /> Baseline is on</label>
            </div>
            {groups.map((g) => (
              <div key={g} style={{ marginTop: 10 }}>
                <h4 style={{ margin: '6px 0' }}>{g}</h4>
                <div className="tbl-wrap"><table className="t">
                  <thead><tr><th style={{ width: 40 }}>On</th><th>Check</th><th>Applies to</th><th style={{ width: 130 }}>Severity</th><th>Settings</th></tr></thead>
                  <tbody>{form.rules.filter((r) => r.category === g).map((r) => (
                    <tr key={r.id} className={r.enabled ? '' : 'faint'}>
                      <td><input type="checkbox" aria-label={`Check ${r.id} on`} checked={r.enabled} onChange={(e) => setRule(r.id, { enabled: e.target.checked })} /></td>
                      <td className="wrap"><span className="mono">{r.id}</span> {r.title}</td>
                      <td>{APPLIES_LABEL[r.applies]}</td>
                      <td><select className="in sm" value={r.severity} onChange={(e) => setRule(r.id, { severity: e.target.value })} aria-label={`Severity of ${r.id}`}>
                        {['high', 'medium', 'low'].map((s) => <option key={s} value={s}>{cap(s)}{s === r.defaultSeverity ? ' (default)' : ''}</option>)}
                      </select></td>
                      <td>{r.params.map((p) => <label key={p.key} className="inline-num" title={`${p.min}–${p.max}`}>{p.label}
                        <input className="in sm" type="number" min={p.min} max={p.max} value={p.value} onChange={(e) => setRule(r.id, { params: r.params.map((x) => (x.key === p.key ? { ...x, value: Number(e.target.value) } : x)) })} style={{ width: 80 }} />
                      </label>)}</td>
                    </tr>
                  ))}</tbody>
                </table></div>
              </div>
            ))}
          </fieldset>
        </div>
      </form>
    </div>
  );
}

// ---- Exceptions --------------------------------------------------------------------------------------

function Exceptions() {
  const { can } = useAuth();
  const confirm = useConfirm();
  const list = useApi(() => api.get<SecException[]>('/security/exceptions'), []);
  async function remove(x: SecException) {
    if (!(await confirm({ title: 'Remove this exception?', message: <>{x.rule} {x.title} — {x.hostname ?? 'all devices'}</>, confirmLabel: 'Remove', danger: true }))) return;
    await api.del(`/security/exceptions/${x.id}`);
    list.reload();
  }
  return (
    <>
      <Callout tone="info" icon="info">Exceptions are added from a check (Checks › a check › Exception…). They are reviewed when they expire; expired exceptions no longer apply.</Callout>
      <Panel flush title="Exceptions">
        <ErrorBox error={list.error} />
        <DataTable rows={list.data ?? []} empty="No exceptions." columns={[
          { key: 'r', label: 'Check', className: 'wrap', render: (x) => <><span className="mono">{x.rule}</span> {x.title}</> },
          { key: 'd', label: 'Device', render: (x) => x.assetId ? <Link to={`/assets/${x.assetId}?tab=security`}>{x.hostname ?? x.assetId}</Link> : <i>All devices</i> },
          { key: 'b', label: 'Baseline', render: (x) => x.baseline ?? 'All' },
          { key: 'w', label: 'Reason', className: 'wrap', render: (x) => x.reason },
          { key: 'e', label: 'Expires', render: (x) => x.expiresAt ? <>{fmt.day(x.expiresAt)} {x.expired && <Pill tone="warn">expired</Pill>}</> : 'No expiry' },
          { key: 'c', label: 'Recorded', render: (x) => <span title={fmt.date(x.createdAt)}>{x.createdBy ?? '—'}, {fmt.day(x.createdAt)}</span> },
          { key: 'x', label: '', render: (x) => can('security.manage') ? <button className="btn sm" onClick={() => void remove(x)}>Remove</button> : null },
        ]} />
      </Panel>
    </>
  );
}
