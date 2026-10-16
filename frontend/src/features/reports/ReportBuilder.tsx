import { useEffect, useMemo, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { CategoryChart, CHART_KINDS, type ChartKind } from '../../components/charts';
import { Icon } from '../../components/Icon';
import { Callout, ErrorBox, PageHead, Panel, Pill } from '../../components/ui';
import { pinReportToDashboard } from '../dashboard/DashboardPage';
import { ScheduleDialog } from './ScheduleDialog';
import { AGGREGATES, aggKey, cellText, DATE_BUCKETS, errorText, OPERATORS, type DatasetCatalog, type DatasetColumn, type ReportAggregate, type ReportDetail, type ReportFilter, type ReportFormat, type ReportSpec, type RunResult } from './types';

const EMPTY: ReportSpec = { columns: [], filters: [], groupBy: null, sort: null };

/** Report designer: pick a data source, columns, filters, optional grouping and sort; preview, save, export, schedule. */
export function ReportBuilder({ id }: { id: string | null }) {
  const { navigate, query } = useRouter();
  const { can } = useAuth();
  const catalog = useApi(() => api.get<DatasetCatalog>('/reports/datasets'), []);
  const datasets = { data: catalog.data?.datasets, error: catalog.error };
  const saved = useApi(() => (id ? api.get<ReportDetail>(`/reports/${id}`) : Promise.resolve(null)), [id]);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [shared, setShared] = useState(false);
  const [dataset, setDataset] = useState(query.get('dataset') ?? 'assets');
  const [spec, setSpec] = useState<ReportSpec>(EMPTY);
  const [result, setResult] = useState<RunResult | null>(null);
  const [running, setRunning] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit' | 'info'; text: string } | null>(null);
  const [colFilter, setColFilter] = useState('');
  const [scheduling, setScheduling] = useState(false);
  const chart: ChartKind = CHART_KINDS.some(([k]) => k === spec.chart) ? spec.chart as ChartKind : 'bar';
  const setChart = (k: ChartKind) => setSpec((s) => ({ ...s, chart: k }));

  const ds = datasets.data?.find((d) => d.key === dataset) ?? null;
  const grouped = (spec.groupBy?.length ?? 0) > 0;
  const editable = !!saved.data && !saved.data.system && can('reports.design') && (saved.data.owned || can('settings.manage'));

  // Load a saved report into the editor and preview it.
  useEffect(() => {
    const r = saved.data;
    if (!r) return;
    setName(r.name); setDescription(r.description ?? ''); setShared(r.shared); setDataset(r.dataset);
    const s = { ...EMPTY, ...r.definition };
    setSpec(s);
    void run(r.dataset, s);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saved.data]);

  // New report: start with the dataset's first few columns.
  useEffect(() => {
    if (id || !ds || spec.columns.length > 0) return;
    setSpec({ ...EMPTY, columns: ds.columns.slice(0, 6).map((c) => c.key) });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ds, id]);

  const groups = useMemo(() => {
    const m = new Map<string, DatasetColumn[]>();
    for (const c of ds?.columns ?? []) {
      if (colFilter && !c.label.toLowerCase().includes(colFilter.toLowerCase())) continue;
      const g = c.group ?? 'Other';
      m.set(g, [...(m.get(g) ?? []), c]);
    }
    return [...m.entries()];
  }, [ds, colFilter]);
  const col = (k: string) => ds?.columns.find((c) => c.key === k);

  function changeDataset(k: string) {
    setDataset(k);
    const d = datasets.data?.find((x) => x.key === k);
    setSpec({ ...EMPTY, columns: d ? d.columns.slice(0, 6).map((c) => c.key) : [] });
    setResult(null);
  }
  const toggleCol = (k: string, on: boolean) => setSpec((s) => ({ ...s, columns: on ? [...s.columns, k] : s.columns.filter((x) => x !== k) }));
  const moveCol = (i: number, d: -1 | 1) => setSpec((s) => {
    const c = [...s.columns]; const j = i + d;
    if (j < 0 || j >= c.length) return s;
    [c[i], c[j]] = [c[j]!, c[i]!];
    return { ...s, columns: c };
  });
  const setFilter = (i: number, f: Partial<ReportFilter>) => setSpec((s) => ({ ...s, filters: s.filters.map((x, n) => (n === i ? { ...x, ...f } : x)) }));
  const setGroup = (i: number, k: string) => setSpec((s) => {
    const g = [...(s.groupBy ?? [])];
    if (k) g[i] = k; else g.splice(i);
    const groupBy = g.filter(Boolean);
    return { ...s, groupBy: groupBy.length ? groupBy : null, sort: null, aggregates: groupBy.length ? s.aggregates : null };
  });
  const aggregates = spec.aggregates ?? [];
  const setAgg = (i: number, a: Partial<ReportAggregate>) => setSpec((s) => ({ ...s, aggregates: (s.aggregates ?? []).map((x, n) => (n === i ? { ...x, ...a } : x)), sort: null }));
  const addAgg = () => ds && setSpec((s) => {
    const num = ds.columns.find((c) => c.type === 'number');
    const first: ReportAggregate = num ? { fn: 'avg', column: num.key } : { fn: 'count_distinct', column: ds.columns[0]!.key };
    return { ...s, aggregates: [...(s.aggregates ?? []), first] };
  });
  const groupedByDate = (spec.groupBy ?? []).some((k) => col(k)?.type === 'date');

  async function run(dsKey = dataset, sp = spec) {
    setRunning(true); setMsg(null);
    try { setResult(await api.post<RunResult>('/reports/run', { dataset: dsKey, definition: sp, limit: 500 })); }
    catch (e) { setResult(null); setMsg({ tone: 'crit', text: errorText(e) }); }
    finally { setRunning(false); }
  }

  async function save(asNew: boolean) {
    if (!name.trim()) { setMsg({ tone: 'crit', text: 'Enter a report name first.' }); return; }
    setBusy(true); setMsg(null);
    const body = { name: name.trim(), description: description.trim() || null, dataset, definition: spec, shared };
    try {
      if (!asNew && id && editable) { await api.put(`/reports/${id}`, body); setMsg({ tone: 'ok', text: 'Report saved.' }); saved.reload(); }
      else {
        const r = await api.post<{ id: string }>('/reports', asNew && saved.data && name.trim() === saved.data.name ? { ...body, name: `${body.name} (copy)` } : body);
        navigate(`/reports/${r.id}`, true);
      }
    } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setBusy(false); }
  }

  async function pin() {
    if (!id || !saved.data) return;
    setBusy(true); setMsg(null);
    try {
      const r = await pinReportToDashboard(id, saved.data.name, !grouped);
      setMsg({ tone: 'ok', text: r === 'added' ? 'Added to your dashboard. Change its place or chart type with Dashboard › Customize.' : 'This report is already on your dashboard.' });
    } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setBusy(false); }
  }

  async function exportAs(format: ReportFormat) {
    setBusy(true); setMsg(null);
    try { await api.downloadJson('/reports/export', { dataset, definition: spec, format, name: name.trim() || ds?.name }, `report.${format}`); }
    catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setBusy(false); }
  }

  const sort = spec.sort?.[0] ?? null;
  const aggLabel = (a: ReportAggregate) => `${AGGREGATES.find((x) => x.fn === a.fn)?.label ?? a.fn} ${col(a.column)?.label ?? a.column}`;
  const sortOptions = grouped
    ? [...(spec.groupBy ?? []).map((k) => ({ key: k, label: col(k)?.label ?? k })), { key: 'count', label: 'Count' }, ...aggregates.map((a) => ({ key: aggKey(a), label: aggLabel(a) }))]
    : (ds?.columns ?? []).map((c) => ({ key: c.key, label: c.label }));
  // Chart: count per group (the columns before "count" are the group labels).
  const countAt = result ? result.columns.findIndex((c) => c.key === 'count') : -1;
  const chartData = grouped && result && countAt > 0
    ? result.rows.slice(0, 25).map((r, i) => ({ key: `${i}`, label: r.slice(0, countAt).map((v, j) => cellText(v, result.columns[j]!.type)).join(' · '), value: Number(r[countAt] ?? 0) }))
    : [];

  if (id && saved.error) return <><PageHead title="Report" /><ErrorBox error={saved.error} /><Link to="/reports">Back to reports</Link></>;

  return (
    <>
      <PageHead title={id ? (saved.data?.name ?? 'Report') : 'New report'} desc={ds?.description}
        actions={<>
          <Link className="btn" to="/reports">All reports</Link>
          {can('reports.export') && <><button className="btn" disabled={busy} onClick={() => void exportAs('xlsx')}><Icon name="down" size={14} /> Excel</button>
            <button className="btn" disabled={busy} onClick={() => void exportAs('pdf')}><Icon name="down" size={14} /> PDF</button>
            <button className="btn" disabled={busy} onClick={() => void exportAs('csv')}><Icon name="down" size={14} /> CSV</button></>}
          {id && saved.data && <button className="btn" disabled={busy} onClick={() => void pin()} title="Show this report on your dashboard"><Icon name="plus" size={14} /> Add to dashboard</button>}
          {editable && <button className="btn" onClick={() => setScheduling(true)}><Icon name="clock" size={14} /> Schedule</button>}
          {can('reports.design') && id && <button className="btn" disabled={busy} onClick={() => void save(true)}>Save as…</button>}
          {can('reports.design') && (!id || editable) && <button className="btn primary" disabled={busy} onClick={() => void save(false)}>{busy ? 'Saving…' : 'Save'}</button>}
        </>} />
      {saved.data?.system && <Callout tone="info" icon="info">This is a built-in report. Change it freely and use <b>Save as…</b> to keep your own copy{can('settings.manage') ? '; administrators can schedule it from the report list' : ''}.</Callout>}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <ErrorBox error={datasets.error} />

      <div className="report-layout">
        <div className="report-side">
          <Panel title="Report">
            <div className="form-v">
              <label>Name <input className="in" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} placeholder="e.g. Laptops without BitLocker" /></label>
              <label>Description <input className="in" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} /></label>
              <label>Data source
                <select className="in" value={dataset} onChange={(e) => changeDataset(e.target.value)}>
                  {(datasets.data ?? []).map((d) => <option key={d.key} value={d.key}>{d.name}</option>)}
                </select>
              </label>
              {(can('dashboards.share') || can('settings.manage')) && <label className="row-check"><input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} /> Share with all users</label>}
            </div>
          </Panel>

          <Panel title="Layout">
            <div className="seg" role="radiogroup" aria-label="Report layout" style={{ marginBottom: 10 }}>
              <button type="button" className={!grouped ? 'on' : ''} onClick={() => setSpec((s) => ({ ...s, groupBy: null, sort: null }))}>Detail list</button>
              <button type="button" className={grouped ? 'on' : ''} onClick={() => !grouped && ds && setGroup(0, spec.columns[0] ?? ds.columns[0]!.key)}>Summary</button>
            </div>
            {grouped && <div className="form-v">
              {[0, 1, 2].filter((i) => i <= (spec.groupBy?.length ?? 0)).map((i) => (
                <label key={i}>{i === 0 ? 'Group by' : 'Then by'}
                  <select className="in" value={spec.groupBy?.[i] ?? ''} onChange={(e) => setGroup(i, e.target.value)}>
                    {i > 0 && <option value="">—</option>}
                    {(ds?.columns ?? []).map((c) => <option key={c.key} value={c.key}>{c.label}</option>)}
                  </select>
                </label>
              ))}
              {groupedByDate && <label>Dates by
                <select className="in" value={spec.dateBucket ?? ''} onChange={(e) => setSpec((s) => ({ ...s, dateBucket: e.target.value || null }))}>
                  <option value="">Exact date and time</option>
                  {DATE_BUCKETS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                </select>
              </label>}
              <div className="agg-list">
                <div className="col-group" style={{ margin: '4px 0' }}>Calculations per group</div>
                <p className="faint" style={{ margin: '0 0 6px', fontSize: 12 }}>Every group shows its count. Add totals, averages or highest values, e.g. average memory per model.</p>
                {aggregates.map((a, i) => {
                  const types = AGGREGATES.find((x) => x.fn === a.fn)?.types ?? [];
                  return <div key={i} className="filter-row">
                    <select className="in" aria-label="Calculation" value={a.fn} onChange={(e) => {
                      const fn = e.target.value as ReportAggregate['fn'];
                      const ok = AGGREGATES.find((x) => x.fn === fn)!.types;
                      const c = col(a.column);
                      setAgg(i, { fn, column: c && ok.includes(c.type) ? a.column : (ds?.columns.find((x) => ok.includes(x.type))?.key ?? a.column) });
                    }}>
                      {AGGREGATES.filter((x) => ds?.columns.some((c) => x.types.includes(c.type))).map((x) => <option key={x.fn} value={x.fn}>{x.label}</option>)}
                    </select>
                    <select className="in" aria-label="Column" value={a.column} onChange={(e) => setAgg(i, { column: e.target.value })}>
                      {(ds?.columns ?? []).filter((c) => types.includes(c.type)).map((c) => <option key={c.key} value={c.key}>{c.label}</option>)}
                    </select>
                    <button type="button" className="icon-btn" aria-label="Remove calculation" onClick={() => setSpec((s) => ({ ...s, aggregates: (s.aggregates ?? []).filter((_, n) => n !== i), sort: null }))}><Icon name="x" size={12} /></button>
                  </div>;
                })}
                <button type="button" className="btn sm" disabled={!ds || aggregates.length >= 6} onClick={addAgg}><Icon name="plus" size={12} /> Add calculation</button>
              </div>
            </div>}
            <div className="form-grid" style={{ marginTop: 10 }}>
              <label className="stack-label">Sort by
                <select className="in" value={sort?.column ?? ''} onChange={(e) => setSpec((s) => ({ ...s, sort: e.target.value ? [{ column: e.target.value, dir: sort?.dir ?? (e.target.value === 'count' ? 'desc' : 'asc') }] : null }))}>
                  <option value="">{grouped ? 'Count (high first)' : 'Default'}</option>
                  {sortOptions.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}
                </select>
              </label>
              <label className="stack-label">Direction
                <select className="in" value={sort?.dir ?? 'asc'} disabled={!sort} onChange={(e) => sort && setSpec((s) => ({ ...s, sort: [{ column: sort.column, dir: e.target.value as 'asc' | 'desc' }] }))}>
                  <option value="asc">Ascending</option><option value="desc">Descending</option>
                </select>
              </label>
            </div>
          </Panel>

          {!grouped && <Panel title="Columns" sub={`${spec.columns.length} selected`}>
            {spec.columns.length > 0 && <ol className="col-order">
              {spec.columns.map((k, i) => <li key={k}>
                <span>{col(k)?.label ?? k}</span>
                <button type="button" className="icon-btn" aria-label="Move up" disabled={i === 0} onClick={() => moveCol(i, -1)}>↑</button>
                <button type="button" className="icon-btn" aria-label="Move down" disabled={i === spec.columns.length - 1} onClick={() => moveCol(i, 1)}>↓</button>
                <button type="button" className="icon-btn" aria-label={`Remove ${col(k)?.label ?? k}`} onClick={() => toggleCol(k, false)}><Icon name="x" size={12} /></button>
              </li>)}
            </ol>}
            <input className="in" style={{ width: '100%', margin: '8px 0' }} placeholder="Find a column…" value={colFilter} onChange={(e) => setColFilter(e.target.value)} aria-label="Find a column" />
            <div className="col-pick">
              {groups.map(([g, cols]) => <div key={g}>
                <div className="col-group">{g}</div>
                {cols.map((c) => <label key={c.key} className="row-check"><input type="checkbox" checked={spec.columns.includes(c.key)} onChange={(e) => toggleCol(c.key, e.target.checked)} /> {c.label}</label>)}
              </div>)}
            </div>
          </Panel>}

          <Panel title="Filters" sub="All conditions must match" right={<button type="button" className="btn sm" disabled={!ds || spec.filters.length >= 25} onClick={() => ds && setSpec((s) => ({ ...s, filters: [...s.filters, { column: ds.columns[0]!.key, op: 'contains', value: '' }] }))}><Icon name="plus" size={12} /> Add</button>}>
            {spec.filters.length === 0 && <p className="muted" style={{ margin: 0 }}>No filters — every record is included.</p>}
            <div className="filter-list">
              {spec.filters.map((f, i) => {
                const c = col(f.column);
                const ops = OPERATORS.filter((o) => !c || o.types.includes(c.type));
                const op = ops.find((o) => o.op === f.op);
                return <div key={i} className="filter-row">
                  <select className="in" aria-label="Filter column" value={f.column} onChange={(e) => { const nc = col(e.target.value); const keep = OPERATORS.find((o) => o.op === f.op && nc && o.types.includes(nc.type)); setFilter(i, { column: e.target.value, op: keep ? f.op : 'eq', value: '' }); }}>
                    {(ds?.columns ?? []).map((x) => <option key={x.key} value={x.key}>{x.label}</option>)}
                  </select>
                  <select className="in" aria-label="Operator" value={f.op} onChange={(e) => setFilter(i, { op: e.target.value })}>
                    {ops.map((o) => <option key={o.op} value={o.op}>{o.label}</option>)}
                  </select>
                  {!op?.noValue && (c?.type === 'yesno' && (f.op === 'eq' || f.op === 'neq')
                    ? <select className="in" aria-label="Value" value={f.value ?? ''} onChange={(e) => setFilter(i, { value: e.target.value })}><option value="">—</option><option value="yes">Yes</option><option value="no">No</option></select>
                    : <input className="in" aria-label="Value" value={f.value ?? ''} onChange={(e) => setFilter(i, { value: e.target.value })}
                        type={c?.type === 'date' && !f.op.endsWith('_days') ? 'date' : c?.type === 'number' || f.op.endsWith('_days') ? 'number' : 'text'}
                        placeholder={f.op.endsWith('_days') ? 'days' : ''} />)}
                  <button type="button" className="icon-btn" aria-label="Remove filter" onClick={() => setSpec((s) => ({ ...s, filters: s.filters.filter((_, n) => n !== i) }))}><Icon name="x" size={12} /></button>
                </div>;
              })}
            </div>
          </Panel>
        </div>

        <div className="report-main">
          <Panel title="Preview" sub={result ? `${result.total.toLocaleString()} ${grouped ? 'group(s)' : 'row(s)'}${result.truncated ? ' · first 500 shown — export for all rows' : ''}` : 'Run the report to see the result'}
            right={<button className="btn primary sm" disabled={running || !ds} onClick={() => void run()}><Icon name="play" size={12} /> {running ? 'Running…' : 'Run'}</button>} flush>
            {grouped && result && result.rows.length > 0 && <div style={{ padding: '12px 14px', borderBottom: '1px solid var(--line)' }}>
              <div className="seg" style={{ marginBottom: 8 }} aria-label="Chart type (saved with the report)">{CHART_KINDS.map(([k, l]) => <button key={k} type="button" className={chart === k ? 'on' : ''} onClick={() => setChart(k)}>{l}</button>)}</div>
              <CategoryChart kind={chart} data={chartData} centerLabel="records" />
            </div>}
            <div className="tbl-wrap report-table">
              <table className="t">
                <thead><tr>{(result?.columns ?? []).map((c) => <th key={c.key} className={c.type === 'number' ? 'num' : ''}>{c.label}</th>)}</tr></thead>
                <tbody>
                  {!result && <tr><td className="muted" style={{ padding: 16 }}>{running ? 'Running…' : 'Choose columns and filters, then press Run.'}</td></tr>}
                  {result && result.rows.length === 0 && <tr><td className="muted" colSpan={result.columns.length} style={{ padding: 16 }}>No records match these filters.</td></tr>}
                  {result?.rows.map((r, i) => <tr key={i}>{r.map((v, j) => {
                    const t = result.columns[j]!.type;
                    return <td key={j} className={t === 'number' ? 'num' : ''}>{t === 'yesno' && v != null ? <Pill tone={v === 'yes' || v === true ? 'ok' : 'warn'}>{cellText(v, t)}</Pill> : cellText(v, t)}</td>;
                  })}</tr>)}
                </tbody>
              </table>
            </div>
          </Panel>
        </div>
      </div>
      {scheduling && saved.data && <ScheduleDialog report={saved.data} onClose={() => setScheduling(false)} onSaved={() => { setScheduling(false); setMsg({ tone: 'ok', text: 'Schedule saved.' }); saved.reload(); }} />}
    </>
  );
}
