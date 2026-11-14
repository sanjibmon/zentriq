import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { TrendArea } from '../../components/charts';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Panel, Pill } from '../../components/ui';
import { hours, type ProgramRun } from '../licenses/types';
import { ScorePill } from './ExperiencePage';
import { EVENT_LABEL, seconds, type DeviceExperience as Data } from './types';

interface Programs { meteredSince: string | null; programs: { exe: string; product: string | null; publisher: string | null; version: string | null; minutes: number; launches: number; daysUsed: number; lastUsed: string; users: string[] | null }[] }

const EVENT_TONE: Record<string, '' | 'warn' | 'crit' | 'info'> = { boot: 'info', crash: 'warn', hang: 'warn', bugcheck: 'crit', shutdown: 'crit', service: 'warn', oom: 'warn' };

/** Device page › Experience: the score, its problems, events, resources and the programs used on the device. */
export function DeviceExperience({ assetId }: { assetId: string }) {
  const { can } = useAuth();
  const data = useApi(() => api.get<Data>(`/experience/devices/${assetId}`), [assetId]);
  const programs = useApi(() => (can('licenses.view') ? api.get<Programs>(`/sam/usage/device/${assetId}`, { days: 30 }).catch(() => null) : Promise.resolve(null)), [assetId]);
  const runs = useApi(() => (can('licenses.view') ? api.get<ProgramRun[]>('/sam/usage/sessions', { assetId, days: 7, limit: 200 }).catch(() => null) : Promise.resolve(null)), [assetId]);
  const d = data.data;
  const c = d?.current;
  return (
    <>
      <ErrorBox error={data.error} />
      {d && !c && <Callout>No experience data yet. The Zentriq Agent 1.1.1.120 or later reports start-up time, crashes, blue screens and resource use with its inventory.</Callout>}
      {c && (
        <>
          <Kpis items={[
            { label: 'Score', value: <ScorePill score={c.score} rating={c.rating} />, sub: `Last 7 days · ${fmt.ago(c.computedAt)}` },
            { label: 'Start', value: seconds(c.bootMs), sub: c.logonMs ? `+ ${seconds(c.logonMs)} until ready` : 'Until the desktop appears' },
            { label: 'Crashes', value: c.crashes + c.hangs, sub: `${c.crashes} crashed · ${c.hangs} not responding` },
            { label: 'Blue Screens', value: c.bugchecks, sub: `${c.shutdowns} unexpected shutdown(s)` },
            { label: 'Free Disk', value: c.systemFree == null ? '—' : `${Math.round(c.systemFree)} %`, sub: 'System drive' },
            { label: 'Battery', value: c.battery == null ? '—' : `${Math.round(c.battery)} %`, sub: c.battery == null ? 'No battery' : 'Of the design capacity' },
          ]} />
          <Panel title="Problems" sub="Each costs points from 100">
            {c.issues.length === 0 ? <div className="faint">Nothing found — this device works well.</div> : (
              <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                {c.issues.map((i) => <li key={i.kind} style={{ display: 'flex', gap: 10, alignItems: 'center', padding: '4px 0' }}><Pill tone={i.severity}>−{i.points}</Pill>{i.text}</li>)}
              </ul>
            )}
          </Panel>
          <div className="grid-2">
            <Panel title="Score, Last 30 Days">
              <TrendArea points={d.history.map((h) => ({ label: fmt.day(h.day), total: h.score, added: 0 }))} unit="score" />
            </Panel>
            <Panel flush title="Processor and Memory" sub="Average load per day; minutes at 90 % or more">
              <DataTable rows={[...d.days].reverse().map((x) => ({ ...x, id: x.day }))} empty="No samples yet." columns={[
                { key: 'd', label: 'Day', render: (x) => fmt.day(x.day) },
                { key: 'c', label: 'Processor', render: (x) => <>{x.cpuAvg == null ? '—' : `${Math.round(x.cpuAvg)} %`}{x.cpuHigh > 0 && <span className="faint"> · {hours(x.cpuHigh)} high</span>}</> },
                { key: 'm', label: 'Memory', render: (x) => <>{x.memAvg == null ? '—' : `${Math.round(x.memAvg)} %`}{x.memHigh > 0 && <span className="faint"> · {hours(x.memHigh)} high</span>}</> },
                { key: 's', label: 'Measured', render: (x) => hours(x.samples) },
              ]} />
            </Panel>
          </div>
          <Panel flush title="Events" sub="Last 30 days">
            <DataTable rows={d.events.map((e, i) => ({ ...e, id: `${e.at}:${e.kind}:${i}` }))} empty="No starts, crashes or failures recorded." columns={[
              { key: 't', label: 'When', render: (e) => fmt.date(e.at) },
              { key: 'k', label: 'Event', render: (e) => <Pill tone={EVENT_TONE[e.kind] ?? ''}>{EVENT_LABEL[e.kind] ?? e.kind}</Pill> },
              { key: 'a', label: 'Application / Service', className: 'wrap', render: (e) => (e.app ? <>{e.app}{e.version && <span className="faint mono"> {e.version}</span>}</> : '—') },
              { key: 'd', label: 'Detail', className: 'wrap', render: (e) => (e.kind === 'boot' ? `${seconds(e.ms)} to the desktop${e.ms2 ? `, ${seconds(e.ms2)} more until ready` : ''}` : e.detail ?? '—') },
            ]} />
          </Panel>
        </>
      )}
      {programs.data && (
        <Panel flush title="Programs Used" sub={programs.data.meteredSince ? `Last 30 days · metered since ${fmt.day(programs.data.meteredSince)}` : 'Software metering has not reported from this device'}>
          <DataTable rows={programs.data.programs.map((p) => ({ ...p, id: p.exe.toLowerCase() }))} empty="No programs recorded." columns={[
            { key: 'p', label: 'Program', className: 'wrap', render: (p) => <><b>{p.product ?? p.exe}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{p.exe}{p.version ? ` · ${p.version}` : ''}</div></> },
            { key: 'u', label: 'Users', className: 'wrap', render: (p) => (p.users ?? []).join(', ') || '—' },
            { key: 't', label: 'Time Running', render: (p) => hours(p.minutes) },
            { key: 'd', label: 'Days Used', render: (p) => p.daysUsed },
            { key: 'n', label: 'Launches', render: (p) => p.launches },
            { key: 'l', label: 'Last Used', render: (p) => fmt.day(p.lastUsed) },
          ]} />
        </Panel>
      )}
      {runs.data && runs.data.length > 0 && (
        <Panel flush title="Recent Launches" sub="Each time a program was started and closed, last 7 days (sampled every minute)">
          <DataTable rows={runs.data.map((r, i) => ({ ...r, id: `${r.exe}:${r.user ?? ''}:${r.startedAt}:${i}` }))} columns={[
            { key: 'p', label: 'Program', className: 'wrap', render: (r) => <><b>{r.product ?? r.exe}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{r.exe}</div></> },
            { key: 'u', label: 'User', render: (r) => r.user ?? '—' },
            { key: 's', label: 'Started', render: (r) => fmt.date(r.startedAt) },
            { key: 'e', label: 'Closed', render: (r) => (r.running ? <Pill tone="ok">Running</Pill> : fmt.date(r.endedAt)) },
            { key: 'm', label: 'Duration', render: (r) => (r.running ? <span className="muted">{hours(r.minutes)} so far</span> : hours(r.minutes)) },
          ]} />
        </Panel>
      )}
    </>
  );
}
