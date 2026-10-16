import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { CategoryChart, type ChartKind } from '../../components/charts';
import { cellText, errorText, type ReportDetail, type RunResult } from '../reports/types';

/**
 * A saved report on the dashboard: summary reports are drawn as a chart (count per group), detail reports as a
 * short table. The report is run with the viewer's permissions; a report they cannot open shows a message.
 */
export function ReportWidget({ reportId, chart, tick }: { reportId: string; chart?: ChartKind; tick: number }) {
  const [report, setReport] = useState<ReportDetail | null>(null);
  const [result, setResult] = useState<RunResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const r = await api.get<ReportDetail>(`/reports/${reportId}`);
        const res = await api.post<RunResult>('/reports/run', { dataset: r.dataset, definition: r.definition, limit: 25 });
        if (alive) { setReport(r); setResult(res); setError(null); }
      } catch (e) {
        if (alive) setError(errorText(e));
      }
    })();
    return () => { alive = false; };
  }, [reportId, tick]);

  if (error) return <div className="chart-empty">This report is not available: {error}</div>;
  if (!report || !result) return <div className="chart-empty">Loading…</div>;
  if (result.rows.length === 0) return <div className="chart-empty">No records match this report.</div>;

  const countAt = result.columns.findIndex((c) => c.key === 'count');
  if (countAt > 0) {
    const kind = (chart ?? report.definition.chart ?? 'bar') as ChartKind;
    const data = result.rows.slice(0, 12).map((r, i) => ({
      key: String(i),
      label: r.slice(0, countAt).map((v, j) => cellText(v, result.columns[j]!.type)).join(' · '),
      value: Number(r[countAt] ?? 0),
    }));
    return <CategoryChart kind={kind} data={data} centerLabel="records" />;
  }

  const cols = result.columns.slice(0, 4);
  return (
    <div className="tbl-wrap">
      <table className="t">
        <thead><tr>{cols.map((c) => <th key={c.key} className={c.type === 'number' ? 'num' : ''}>{c.label}</th>)}</tr></thead>
        <tbody>
          {result.rows.slice(0, 8).map((r, i) => (
            <tr key={i}>{cols.map((c, j) => <td key={c.key} className={c.type === 'number' ? 'num' : ''}>{cellText(r[j], c.type)}</td>)}</tr>
          ))}
        </tbody>
      </table>
      {result.total > 8 && <p className="faint" style={{ fontSize: 12, margin: '6px 0 0' }}><Link to={`/reports/${reportId}`}>All {result.total.toLocaleString()} rows</Link></p>}
    </div>
  );
}
