import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, ErrorBox, fmt, Pill } from '../../components/ui';
import { useAuth } from '../../app/auth';
import { cap, cvssParts, errorText, pct, prioTone, sevTone, STATUS_LABEL, statusTone, type Assignee, type CveDetail, type Finding, type FindingDetail, cvssText } from './types';

/** CVE details: CVSS, CISA KEV and EPSS data, affected product ranges and the devices where it is open. */
export function CveDialog({ cve, onClose }: { cve: string; onClose: () => void }) {
  const d = useApi(() => api.get<CveDetail>(`/vulnerabilities/cves/${encodeURIComponent(cve)}`), [cve]);
  const hosts = useApi(() => api.get<{ items: Finding[]; total: number }>('/vulnerabilities/findings', { cve, status: 'all', pageSize: 100 }), [cve]);
  const c = d.data?.cve;
  const range = (p: CveDetail['products'][number]) => {
    if (p.version && p.version !== '*' && p.version !== '-') return `= ${p.version}`;
    const lo = p.startIncl ? `≥ ${p.startIncl}` : p.startExcl ? `> ${p.startExcl}` : '';
    const hi = p.endExcl ? `< ${p.endExcl}` : p.endIncl ? `≤ ${p.endIncl}` : '';
    return [lo, hi].filter(Boolean).join(' and ') || 'all versions';
  };
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="cve-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal wide">
        <div className="modal-h">
          <h2 id="cve-title" className="mono">{cve}</h2>
          {c && <><Pill tone={sevTone(c.severity)}>{cap(c.severity)} {c.cvss != null ? cvssText(c.cvss) : ''}</Pill>{c.kev && <Pill tone="crit">CISA KEV</Pill>}{c.msExploited && !c.kev && <Pill tone="crit">Exploited</Pill>}</>}
          <span style={{ flex: 1 }} />
          <button className="btn sm" onClick={onClose}>Close</button>
        </div>
        <div className="modal-b">
          <ErrorBox error={d.error} />
          {!c && !d.error && <p className="muted">Loading…</p>}
          {c && <>
            {c.title && <p style={{ marginTop: 0, fontWeight: 600 }}>{c.title}</p>}
            <p style={{ marginTop: c.title ? 4 : 0, lineHeight: 1.5 }}>{c.description && c.description !== c.title ? c.description : c.title ? '' : 'No description.'}</p>
            {c.msExploited && !c.kev && <Callout tone="crit" icon="alert"><b>Microsoft reports this vulnerability as exploited.</b> Treat it like a known-exploited vulnerability: priority P1.</Callout>}
            {c.kev && <Callout tone="crit" icon="alert">
              <b>Known to be exploited in the wild</b> (CISA KEV, added {fmt.day(c.kevAdded)}; federal due date {fmt.day(c.kevDue)}).
              {c.kevRansomware === 'Known' && <> Used in ransomware campaigns.</>}
              {c.kevAction && <div style={{ marginTop: 4 }}>Required action: {c.kevAction}</div>}
            </Callout>}
            <dl className="kv-grid" style={{ margin: '12px 0' }}>
              <div><dt>CVSS {c.cvssVersion ?? ''}</dt><dd>{cvssText(c.cvss)} {c.severity && `(${cap(c.severity)})`}</dd></div>
              <div><dt>Vector</dt><dd className="mono" style={{ fontSize: 12 }}>{c.vector ?? '—'}</dd></div>
              <div><dt>EPSS (30-day exploit probability)</dt><dd>{pct(c.epss, 2)} {c.epssPercentile != null && <span className="muted">· {pct(c.epssPercentile, 0)} percentile</span>}</dd></div>
              <div><dt>Weakness</dt><dd>{c.cwes.length ? c.cwes.join(', ') : '—'}</dd></div>
              {c.msSeverity && <div><dt>Microsoft severity</dt><dd>{c.msSeverity}{c.msDisclosed && <span className="muted"> · publicly disclosed</span>}</dd></div>}
              <div><dt>Published</dt><dd>{fmt.day(c.publishedAt)}</dd></div>
              <div><dt>Last modified</dt><dd>{fmt.day(c.modifiedAt)}</dd></div>
            </dl>
            {cvssParts(c.vector).length > 0 && <div className="cvss-parts">
              {cvssParts(c.vector).map(([k, v]) => <span key={k}><small>{k}</small><b>{v}</b></span>)}
            </div>}
            {d.data!.windows.length > 0 && <>
              <h4 style={{ margin: '14px 0 6px' }}>Windows updates that fix it</h4>
              <div className="tbl-wrap"><table className="t"><thead><tr><th>Windows version</th><th>Update</th><th>Fixed in build</th><th>Released</th></tr></thead>
                <tbody>{d.data!.windows.map((w, i) => <tr key={i}><td>{w.product}</td>
                  <td>{w.kb ? (w.kbUrl ? <a href={w.kbUrl} target="_blank" rel="noreferrer noopener">KB{w.kb}</a> : `KB${w.kb}`) : '—'}{w.restart ? <span className="faint"> · restart</span> : ''}</td>
                  <td className="mono">{w.fixedBuild ?? '—'}</td><td>{w.month}</td></tr>)}</tbody></table></div>
            </>}
            {(d.data!.products.length > 0 || d.data!.windows.length === 0) && <h4 style={{ margin: '14px 0 6px' }}>Affected products</h4>}
            {d.data!.windows.length > 0 && d.data!.products.length === 0 ? null : d.data!.products.length === 0 ? <p className="muted">No product configuration recorded.</p> :
              <div className="tbl-wrap"><table className="t"><thead><tr><th>Vendor</th><th>Product</th><th>Affected versions</th></tr></thead>
                <tbody>{d.data!.products.map((p, i) => <tr key={i}><td>{p.vendor}</td><td>{p.product}</td><td>{range(p)}</td></tr>)}</tbody></table></div>}
            <h4 style={{ margin: '14px 0 6px' }}>Devices {hosts.data ? `(${hosts.data.total})` : ''}</h4>
            {hosts.data && hosts.data.items.length === 0 ? <p className="muted">Not found on any device.</p> :
              <div className="tbl-wrap"><table className="t"><thead><tr><th>Device</th><th>Software</th><th>Fixed in</th><th>Status</th><th>Due</th></tr></thead>
                <tbody>{(hosts.data?.items ?? []).map((f) => <tr key={f.id}>
                  <td><Link to={`/assets/${f.assetId}`} onClick={onClose}>{f.hostname ?? f.assetId}</Link></td>
                  <td>{f.software} {f.version}</td><td>{f.kb ? `KB${f.kb} · ` : ''}{f.fixedVersion ?? '—'}</td>
                  <td><Pill tone={statusTone(f.status)}>{STATUS_LABEL[f.status] ?? f.status}</Pill></td>
                  <td>{f.status === 'open' || f.status === 'in_progress' ? fmt.day(f.dueAt) : '—'}</td>
                </tr>)}</tbody></table></div>}
            <h4 style={{ margin: '14px 0 6px' }}>References</h4>
            <ul className="ref-list">
              {c.msrcUrl && <li><a href={c.msrcUrl} target="_blank" rel="noreferrer noopener">Microsoft Security Update Guide: {c.cve}</a></li>}
              <li><a href={c.nvdUrl} target="_blank" rel="noreferrer noopener">NVD: {c.cve}</a></li>
              {c.references.slice(0, 15).map((u) => <li key={u}><a href={u} target="_blank" rel="noreferrer noopener">{u}</a></li>)}
            </ul>
          </>}
        </div>
      </div>
    </div>
  );
}

/** Risk acceptance (with expiry), false positive, or re-open. Reason is kept for audit. */
export function ExceptionDialog({ ids, initial, onClose, onSaved }: { ids: number[]; initial: 'accepted' | 'false_positive' | 'open'; onClose: () => void; onSaved: (n: number) => void }) {
  const [status, setStatus] = useState(initial);
  const [reason, setReason] = useState('');
  const def = new Date(Date.now() + 90 * 86400000).toISOString().slice(0, 10);
  const max = new Date(Date.now() + 365 * 86400000).toISOString().slice(0, 10);
  const [until, setUntil] = useState(def);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ updated: number }>('/vulnerabilities/findings/status', {
        ids, status, reason: status === 'open' ? null : reason, until: status === 'accepted' ? new Date(`${until}T23:59:59`).toISOString() : null,
      });
      onSaved(r.updated);
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="exc-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="exc-title">{ids.length} finding(s)</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="seg">
            <button type="button" className={status === 'accepted' ? 'on' : ''} onClick={() => setStatus('accepted')}>Accept risk</button>
            <button type="button" className={status === 'false_positive' ? 'on' : ''} onClick={() => setStatus('false_positive')}>False positive</button>
            <button type="button" className={status === 'open' ? 'on' : ''} onClick={() => setStatus('open')}>Re-open</button>
          </div>
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>
            {status === 'accepted' && 'The findings stop counting as open until the expiry date, then re-open automatically if the software is still vulnerable.'}
            {status === 'false_positive' && 'Use when the software is not actually affected (for example, the vulnerable component is not installed or the product was mis-identified).'}
            {status === 'open' && 'Removes the exception; the findings count as open again with their original due date.'}
          </p>
          {status !== 'open' && <label>Reason / compensating control
            <textarea className="in" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} required minLength={5} maxLength={1000} placeholder="e.g. Isolated lab network; vendor patch scheduled for next maintenance window" />
          </label>}
          {status === 'accepted' && <label>Accept until
            <input className="in" type="date" value={until} min={new Date(Date.now() + 86400000).toISOString().slice(0, 10)} max={max} onChange={(e) => setUntil(e.target.value)} required />
            <small className="muted">At most one year; the decision is recorded in the audit log.</small>
          </label>}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || (status !== 'open' && reason.trim().length < 5)}>{busy ? 'Saving…' : 'Apply'}</button>
        </footer>
      </form>
    </div>
  );
}

/** Assign to a person, set a ticket reference, or add a note, for one or more findings. */
export function WorkflowDialog({ ids, action, onClose, onSaved }: { ids: number[]; action: 'assign' | 'ticket' | 'note'; onClose: () => void; onSaved: (n: number) => void }) {
  const people = useApi(() => (action === 'assign' ? api.get<Assignee[]>('/vulnerabilities/assignees') : Promise.resolve([] as Assignee[])), [action]);
  const { me } = useAuth();
  const [assignee, setAssignee] = useState<string>(me?.id ?? '');
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ updated: number }>('/vulnerabilities/findings/workflow', {
        ids, action, assigneeId: action === 'assign' ? (assignee || null) : null, ticket: action === 'ticket' ? text : null, note: action === 'note' ? text : null,
      });
      onSaved(r.updated);
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }
  const title = action === 'assign' ? 'Assign' : action === 'ticket' ? 'Ticket reference' : 'Add a note';
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="wf-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="wf-title">{title}: {ids.length} finding(s)</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {action === 'assign' && <label>Assign to
            <select className="in" value={assignee} onChange={(e) => setAssignee(e.target.value)}>
              <option value="">Nobody (unassign)</option>
              {(people.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.name}{p.email ? ` (${p.email})` : ''}</option>)}
            </select>
          </label>}
          {action === 'ticket' && <label>Ticket or change number
            <input className="in" value={text} onChange={(e) => setText(e.target.value)} maxLength={100} placeholder="e.g. INC0012345 or CHG-2026-118 (leave empty to remove)" />
            <small className="muted">A reference to your service desk; searchable in the findings list and shown in reports.</small>
          </label>}
          {action === 'note' && <label>Note
            <textarea className="in" rows={4} value={text} onChange={(e) => setText(e.target.value)} maxLength={2000} required placeholder="e.g. Patch scheduled for Saturday's maintenance window" />
          </label>}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || (action === 'note' && text.trim().length === 0)}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}

/** One finding: what it is, how to fix it, who works on it, and its history (status changes, assignment, notes). */
export function FindingDrawer({ id, onClose, onChanged, onCve }: { id: number; onClose: () => void; onChanged: () => void; onCve: (c: string) => void }) {
  const d = useApi(() => api.get<FindingDetail>(`/vulnerabilities/findings/${id}`), [id]);
  const { can } = useAuth();
  const [dialog, setDialog] = useState<'assign' | 'ticket' | 'note' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const f = d.data?.finding;
  async function start() {
    setError(null);
    try { await api.post('/vulnerabilities/findings/workflow', { ids: [id], action: 'start' }); d.reload(); onChanged(); }
    catch (e) { setError(errorText(e)); }
  }
  const manage = can('vuln.manage');
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="fd-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal wide">
        <div className="modal-h">
          <h2 id="fd-title">{f ? <><button className="linkish mono" onClick={() => onCve(f.cve)}>{f.cve}</button> on {f.hostname ?? f.assetId}</> : 'Finding'}</h2>
          {f && <><Pill tone={prioTone(f.priority)}>{f.priority}</Pill><Pill tone={statusTone(f.status)}>{STATUS_LABEL[f.status] ?? f.status}</Pill></>}
          <span style={{ flex: 1 }} />
          <button className="btn sm" onClick={onClose}>Close</button>
        </div>
        <div className="modal-b">
          <ErrorBox error={d.error} />
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {!f && !d.error && <p className="muted">Loading…</p>}
          {f && <>
            {f.title && <p style={{ margin: '0 0 4px', fontWeight: 600 }}>{f.title}</p>}
            {f.description && f.description !== f.title && <p className="clamp-3" style={{ marginTop: 0, lineHeight: 1.5 }} title={f.description}>{f.description}</p>}
            <Callout tone={f.status === 'fixed' ? 'ok' : 'info'} icon="info"><b>{f.status === 'fixed' ? 'Fixed.' : 'How to fix:'}</b> {f.status === 'fixed' ? `Detected as fixed ${fmt.day(f.fixedAt)}.` : f.remediation}</Callout>
            <dl className="kv-grid" style={{ margin: '12px 0' }}>
              <div><dt>{f.source === 'os' ? 'Windows' : 'Software'}</dt><dd>{f.software} <span className="mono faint">{f.version}</span></dd></div>
              <div><dt>{f.source === 'os' ? 'Fixed by' : 'Fixed in'}</dt><dd>{f.kb ? `KB${f.kb} · build ` : ''}{f.fixedVersion ?? '—'}</dd></div>
              <div><dt>Severity</dt><dd><Pill tone={sevTone(f.severity)}>{cap(f.severity)} {cvssText(f.cvss)}</Pill>{f.exploited && <> <Pill tone="crit">Exploited</Pill></>}</dd></div>
              <div><dt>Risk score</dt><dd>{f.risk} / 100 <span className="muted">· device criticality {f.criticality ?? 'Medium'}</span></dd></div>
              <div><dt>EPSS</dt><dd>{pct(f.epss, 2)}</dd></div>
              <div><dt>First seen</dt><dd>{fmt.day(f.firstSeenAt)} <span className="muted">· last seen {fmt.ago(f.lastSeenAt)}</span></dd></div>
              <div><dt>Due</dt><dd style={{ color: f.dueAt && new Date(f.dueAt).getTime() < Date.now() && f.status !== 'fixed' ? 'var(--crit)' : undefined }}>{fmt.day(f.dueAt)}{f.kevDue ? <span className="muted"> · CISA {fmt.day(f.kevDue)}</span> : ''}</dd></div>
              <div><dt>Assigned to</dt><dd>{f.assignee ?? <span className="muted">Nobody</span>}</dd></div>
              <div><dt>Ticket</dt><dd>{f.ticket ?? <span className="muted">—</span>}</dd></div>
              {f.exceptionReason && <div><dt>Exception</dt><dd>{f.exceptionReason}{f.exceptionUntil ? ` (until ${fmt.day(f.exceptionUntil)})` : ''}</dd></div>}
            </dl>
            {manage && f.status !== 'fixed' && <div className="toolbar-row" style={{ margin: '0 0 12px' }}>
              {f.status === 'open' && <button className="btn sm primary" onClick={() => void start()}>Start remediation</button>}
              <button className="btn sm" onClick={() => setDialog('assign')}>Assign…</button>
              <button className="btn sm" onClick={() => setDialog('ticket')}>Ticket…</button>
              <button className="btn sm" onClick={() => setDialog('note')}>Add note…</button>
              <Link className="btn sm" to={`/assets/${f.assetId}`} onClick={onClose}>Open device</Link>
            </div>}
            <h4 style={{ margin: '14px 0 6px' }}>History</h4>
            {d.data!.events.length === 0 ? <p className="muted">No changes yet. Detected {fmt.date(f.firstSeenAt)}.</p> :
              <ol className="timeline">
                {d.data!.events.map((e, i) => <li key={i} className={`ev-${e.kind}`}>
                  <span className="when" title={fmt.date(e.at)}>{fmt.ago(e.at)}</span>
                  <span className="what">{e.text}</span>
                  <span className="who">{e.by ?? (e.kind === 'system' ? 'Zentriq' : '')}</span>
                </li>)}
                <li className="ev-system"><span className="when" title={fmt.date(f.firstSeenAt)}>{fmt.ago(f.firstSeenAt)}</span><span className="what">Detected.</span><span className="who">Zentriq</span></li>
              </ol>}
          </>}
        </div>
      </div>
      {dialog && <WorkflowDialog ids={[id]} action={dialog} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); d.reload(); onChanged(); }} />}
    </div>
  );
}

