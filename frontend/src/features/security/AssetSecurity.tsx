import { useState } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, Panel, Pill } from '../../components/ui';
import { ScoreBar } from './SecurityPage';
import { RESULT_LABEL, resultTone, secSevTone, type DeviceSecurity } from './types';

/** Security configuration results of one device (device page, Security tab). */
export function AssetSecurity({ assetId }: { assetId: string }) {
  const d = useApi(() => api.get<DeviceSecurity>(`/security/devices/${assetId}`), [assetId]);
  const [baseline, setBaseline] = useState<string | null>(null);
  const [all, setAll] = useState(false);
  if (d.error) return <ErrorBox error={d.error} />;
  if (!d.data) return <p className="muted">Loading…</p>;
  const b = d.data.baselines.find((x) => x.id === baseline) ?? d.data.baselines[0];
  const rows = d.data.results.filter((r) => r.baselineId === b?.id && (all || r.status !== 'pass')).map((r) => ({ ...r, id: r.rule }));
  return (
    <>
      {d.data.settingsReported === false && <Callout tone="warn" icon="info">This device has not reported its security settings yet; many checks are unknown. Update the agent or collect inventory again.</Callout>}
      {!b ? <Panel><p className="muted" style={{ margin: 0 }}>Not assessed. Security configuration checks run on Windows devices with inventory; <Link to="/security">Security Configuration</Link>.</p></Panel> : <>
        <Panel title="Configuration score" right={d.data.baselines.length > 1 && <select className="in" value={b.id} onChange={(e) => setBaseline(e.target.value)} aria-label="Baseline">{d.data.baselines.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</select>}>
          <div style={{ display: 'flex', gap: 24, alignItems: 'center', flexWrap: 'wrap' }}>
            <div style={{ minWidth: 200 }}><ScoreBar v={b.score} /></div>
            <span><b style={{ color: b.failed ? 'var(--crit)' : undefined }}>{b.failed}</b> failed{b.failedHigh ? ` (${b.failedHigh} high)` : ''}</span>
            <span>{b.passed} passed</span>
            {b.unknown > 0 && <span>{b.unknown} unknown</span>}
            {b.exempt > 0 && <span>{b.exempt} exception(s)</span>}
            <span className="faint">checked {fmt.ago(b.evaluatedAt)}</span>
          </div>
        </Panel>
        <Panel flush title={all ? 'All checks' : 'Checks to fix'} right={<label className="check-inline"><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> Show passed</label>}>
          <DataTable rows={rows} empty="Every check passes." columns={[
            { key: 'r', label: 'Check', className: 'wrap', render: (r) => <><span className="mono">{r.rule}</span> {r.title}<div className="faint" style={{ fontSize: 11.5 }}>{r.category}</div></> },
            { key: 's', label: 'Result', render: (r) => <Pill tone={resultTone(r.status)}>{RESULT_LABEL[r.status] ?? r.status}</Pill> },
            { key: 'v', label: 'Severity', render: (r) => <Pill tone={secSevTone(r.severity)}>{r.severity}</Pill> },
            { key: 'a', label: 'Found', className: 'wrap', render: (r) => r.actual ?? '—' },
            { key: 'f', label: 'How to fix', className: 'wrap', render: (r) => r.status === 'pass' ? '' : <span className="clamp-2" title={r.remediation ?? ''} style={{ fontSize: 12 }}>{r.remediation}</span> },
          ]} />
        </Panel>
      </>}
    </>
  );
}
