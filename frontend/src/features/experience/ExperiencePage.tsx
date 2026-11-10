import { useState } from 'react';
import { api } from '../../api/client';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { HBars, TrendArea } from '../../components/charts';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { ISSUE_LABEL, RATING_LABEL, RATING_TONE, ratingOf, seconds, type ExperienceRow, type ExperienceSummary, type Rating } from './types';

const PAGE = 50;

export function ScorePill({ score, rating }: { score: number; rating?: Rating }) {
  const r = rating ?? ratingOf(score);
  return <Pill tone={RATING_TONE[r]}>{score} · {RATING_LABEL[r]}</Pill>;
}

/** Device Experience: how well the devices work for the people who use them. */
export function ExperiencePage() {
  const { navigate, query } = useRouter();
  const [rating, setRating] = useSticky('ExperiencePage.rating', query.get('rating') ?? '');
  const [issue, setIssue] = useSticky('ExperiencePage.issue', query.get('issue') ?? '');
  const [q, setQ] = useSticky('ExperiencePage.q', '');
  const [page, setPage] = useState(1);
  const summary = useApi(() => api.get<ExperienceSummary>('/experience/summary'), []);
  const list = useApi(() => api.get<ExperienceRow[]>('/experience/devices', { rating: rating || null, issue: issue || null, q: q || null }), [rating, issue, q]);
  const s = summary.data;
  const rows = (list.data ?? []).map((r) => ({ ...r, id: r.assetId }));
  const shown = rows.slice((page - 1) * PAGE, page * PAGE);
  const set = (f: () => void) => { f(); setPage(1); };
  return (
    <>
      <PageHead title="Device Experience" icon="gauge" desc="How well each device works for the people who use it: start-up time, crashes, blue screens, resources, disk and battery over the last 7 days." />
      <ErrorBox error={summary.error} />
      {s && s.devices === 0 && <Callout>No device has reported yet. Devices with the Zentriq Agent 1.1.1.120 or later report with their next inventory; update older agents under <Link to="/agents">Agents</Link>.</Callout>}
      {s && s.devices > 0 && (
        <Kpis items={[
          { label: 'Experience Score', value: s.score ?? '—', sub: s.score != null ? `${RATING_LABEL[ratingOf(s.score)]} · average of ${s.devices.toLocaleString()} devices` : '—' },
          { label: 'Good · Fair · Poor', value: `${s.good} · ${s.fair} · ${s.poor}`, sub: 'From 80 good, from 60 fair' },
          { label: 'Median Start', value: seconds(s.bootMs), sub: 'Until the desktop appears' },
          { label: 'Application Crashes', value: (s.crashes + s.hangs).toLocaleString(), sub: `${s.crashes} crashed · ${s.hangs} stopped responding (7 days)` },
          { label: 'Blue Screens', value: s.bugchecks.toLocaleString(), sub: `On ${s.bugcheckDevices} device(s) in 7 days` },
        ]} />
      )}
      {s && s.devices > 0 && (
        <div className="grid-2">
          <Panel title="Score, Last 30 Days" sub="Average score of the devices that reported each day">
            <TrendArea points={s.trend.map((t) => ({ label: fmt.day(t.day), total: t.score, added: 0 }))} unit="score" />
          </Panel>
          <Panel title="Most Common Problems" sub="Devices with each problem — click to list them">
            <HBars data={s.issues.map((i) => ({ key: i.kind, label: ISSUE_LABEL[i.kind] ?? i.kind, value: i.devices }))} onSelect={(x) => set(() => { setIssue(x.key); setRating(''); })} />
          </Panel>
        </div>
      )}
      {s && s.apps.length > 0 && (
        <Panel flush title="Applications That Crash Most" sub="Last 7 days">
          <DataTable rows={s.apps.map((a) => ({ ...a, id: a.app }))} columns={[
            { key: 'a', label: 'Application', className: 'wrap', render: (a) => <><b>{a.app}</b>{a.version && <span className="faint mono"> {a.version}</span>}</> },
            { key: 'c', label: 'Crashed', render: (a) => a.crashes },
            { key: 'h', label: 'Stopped Responding', render: (a) => a.hangs },
            { key: 'd', label: 'Devices', render: (a) => a.devices },
            { key: 'l', label: 'Last', render: (a) => fmt.ago(a.last) },
          ]} />
        </Panel>
      )}
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search devices or users…" value={q} onChange={(e) => set(() => setQ(e.target.value))} aria-label="Search" />
        <select className="in" value={rating} onChange={(e) => set(() => setRating(e.target.value))} aria-label="Rating">
          <option value="">Every Rating</option><option value="poor">Poor (below 60)</option><option value="fair">Fair (60–79)</option><option value="good">Good (80 or more)</option>
        </select>
        <select className="in" value={issue} onChange={(e) => set(() => setIssue(e.target.value))} aria-label="Problem">
          <option value="">Every Problem</option>
          {Object.entries(ISSUE_LABEL).map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        {(rating || issue || q) && <button className="btn" onClick={() => set(() => { setRating(''); setIssue(''); setQ(''); })}>Clear</button>}
      </div>
      <ErrorBox error={list.error} />
      <Panel flush title="Devices" sub={`${rows.length.toLocaleString()} device(s), lowest score first`}>
        <Pager top page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
        <DataTable rows={shown} onRow={(r) => navigate(`/assets/${r.assetId}?tab=experience`)} rowTitle="Open the device"
          empty={list.loading ? 'Loading…' : 'No device matches.'}
          columns={[
            { key: 'h', label: 'Device', render: (r) => <><b>{r.hostname}</b><div className="faint" style={{ fontSize: 11.5 }}>{r.user ?? r.os ?? ''}</div></> },
            { key: 's', label: 'Score', render: (r) => <ScorePill score={r.score} rating={r.rating} /> },
            { key: 'i', label: 'Main Problem', className: 'wrap', render: (r) => (r.issues[0] ? r.issues[0].text : <span className="faint">None</span>) },
            { key: 'b', label: 'Start', render: (r) => seconds(r.bootMs) },
            { key: 'c', label: 'Crashes', render: (r) => r.crashes + r.hangs },
            { key: 'x', label: 'Blue Screens', render: (r) => r.bugchecks },
            { key: 'f', label: 'Free Disk', render: (r) => (r.systemFree == null ? '—' : `${Math.round(r.systemFree)} %`) },
            { key: 'u', label: 'Up For', render: (r) => (r.uptimeDays == null ? '—' : `${Math.round(r.uptimeDays)} d`) },
            { key: 'd', label: 'Department', render: (r) => r.department ?? '—' },
          ]} />
        <Pager page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
      </Panel>
    </>
  );
}
