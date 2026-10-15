import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, ErrorBox, fmt, Pill } from '../../components/ui';
import { cap, errorText, pct, sevTone, type CveDetail, type Finding, cvssText } from './types';

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
          {c && <><Pill tone={sevTone(c.severity)}>{cap(c.severity)} {c.cvss != null ? cvssText(c.cvss) : ''}</Pill>{c.kev && <Pill tone="crit">CISA KEV</Pill>}</>}
          <span style={{ flex: 1 }} />
          <button className="btn sm" onClick={onClose}>Close</button>
        </div>
        <div className="modal-b">
          <ErrorBox error={d.error} />
          {!c && !d.error && <p className="muted">Loading…</p>}
          {c && <>
            <p style={{ marginTop: 0, lineHeight: 1.5 }}>{c.description ?? 'No description.'}</p>
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
              <div><dt>Published</dt><dd>{fmt.day(c.publishedAt)}</dd></div>
              <div><dt>Last modified</dt><dd>{fmt.day(c.modifiedAt)}</dd></div>
            </dl>
            <h4 style={{ margin: '14px 0 6px' }}>Affected products</h4>
            {d.data!.products.length === 0 ? <p className="muted">No product configuration recorded.</p> :
              <div className="tbl-wrap"><table className="t"><thead><tr><th>Vendor</th><th>Product</th><th>Affected versions</th></tr></thead>
                <tbody>{d.data!.products.map((p, i) => <tr key={i}><td>{p.vendor}</td><td>{p.product}</td><td>{range(p)}</td></tr>)}</tbody></table></div>}
            <h4 style={{ margin: '14px 0 6px' }}>Devices {hosts.data ? `(${hosts.data.total})` : ''}</h4>
            {hosts.data && hosts.data.items.length === 0 ? <p className="muted">Not found on any device.</p> :
              <div className="tbl-wrap"><table className="t"><thead><tr><th>Device</th><th>Software</th><th>Fixed in</th><th>Status</th><th>Due</th></tr></thead>
                <tbody>{(hosts.data?.items ?? []).map((f) => <tr key={f.id}>
                  <td><Link to={`/assets/${f.assetId}`} onClick={onClose}>{f.hostname ?? f.assetId}</Link></td>
                  <td>{f.software} {f.version}</td><td>{f.fixedVersion ?? '—'}</td>
                  <td><Pill tone={f.status === 'open' ? 'crit' : f.status === 'fixed' ? 'ok' : 'warn'}>{fmt.label(f.status)}</Pill></td>
                  <td>{f.status === 'open' ? fmt.day(f.dueAt) : '—'}</td>
                </tr>)}</tbody></table></div>}
            <h4 style={{ margin: '14px 0 6px' }}>References</h4>
            <ul className="ref-list">
              <li><a href={c.nvdUrl} target="_blank" rel="noreferrer noopener">NVD — {c.cve}</a></li>
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
