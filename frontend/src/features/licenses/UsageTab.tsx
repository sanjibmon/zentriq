import { useState } from 'react';
import { api } from '../../api/client';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Pager, Panel, Pill } from '../../components/ui';
import { hours, money, type ProgramRun, type ProgramUse, type UsageApp, type UsageSummary } from './types';

const PAGE = 50;

/** Software metering: which programs people actually use, and the installations of licensed products nobody uses. */
export function UsageTab({ canManage }: { canManage: boolean }) {
  const { navigate } = useRouter();
  const [days, setDays] = useSticky('UsageTab.days', 30);
  const [q, setQ] = useSticky('UsageTab.q', '');
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<UsageApp | null>(null);
  const data = useApi(() => api.get<UsageSummary>('/sam/usage', { days, q: q || null }), [days, q]);
  const d = data.data;
  const apps = (d?.apps ?? []).map((a) => ({ ...a, id: a.exe.toLowerCase() }));
  const products = (d?.products ?? []).filter((p) => p.rules).map((p) => ({ ...p, id: p.id }));
  const noRules = (d?.products ?? []).filter((p) => !p.rules).length;
  const unused = products.reduce((a, p) => a + p.unused, 0);
  const saving = products.reduce((a, p) => a + (p.unitCost ? p.unitCost * p.unused : 0), 0);
  const shown = apps.slice((page - 1) * PAGE, page * PAGE);
  return (
    <>
      {d && !d.metering && <Callout tone="warn" icon="alert">Software metering is off. {canManage ? <Link to="/modules/licenses?tab=settings">Turn it on in Settings</Link> : 'An administrator can turn it on in Settings'} — devices then report the programs they run.</Callout>}
      <Kpis items={[
        { label: 'Metered Devices', value: (d?.meteredDevices ?? 0).toLocaleString(), sub: 'Zentriq Agent 1.1.1.120 or later' },
        { label: 'Programs Used', value: apps.length.toLocaleString(), sub: `In the last ${days} days` },
        { label: 'Unused Installations', value: unused.toLocaleString(), sub: `Licensed products, no use for ${d?.unusedDays ?? 90} days` },
        { label: 'Possible Saving', value: saving > 0 ? money(saving) : '—', sub: 'Unused installations × average licence cost' },
      ]} />
      <Panel flush title="Licensed Products" sub={`Installations used and unused (no use for ${d?.unusedDays ?? 90} days)${noRules > 0 ? ` · ${noRules} product(s) have no programs set — edit them to add the program files` : ''}`}>
        <DataTable rows={products} onRow={(p) => navigate(`/modules/licenses/products/${p.id}?usage=unused`)} rowTitle="Open the product"
          empty={data.loading ? 'Loading…' : 'No product with programs set is installed. Products added from Discover come with their programs; for your own, add the program files (e.g. visio.exe) to the product.'}
          columns={[
            { key: 'n', label: 'Product', className: 'wrap', render: (p) => <><b>{p.name}</b>{p.publisher && <span className="faint"> · {p.publisher}</span>}</> },
            { key: 'i', label: 'Installed', render: (p) => p.installed.toLocaleString() },
            { key: 'u', label: 'Used', render: (p) => p.used.toLocaleString() },
            { key: 'x', label: 'Unused', render: (p) => (p.unused > 0 ? <Pill tone="warn">{p.unused.toLocaleString()}</Pill> : '0') },
            { key: 'r', label: 'Too Early to Tell', render: (p) => (p.recent + p.notMetered).toLocaleString() },
            { key: 's', label: 'Possible Saving', render: (p) => (p.unitCost && p.unused ? money(p.unitCost * p.unused) : '—') },
          ]} />
      </Panel>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search programs, products, publishers…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search" />
        <select className="in" value={days} onChange={(e) => { setDays(Number(e.target.value)); setPage(1); }} aria-label="Period">
          <option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option><option value={365}>Last 12 months</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush title="Programs Used" sub="Time a program was running in a signed-in user's session, sampled every minute">
        <Pager top page={page} pageSize={PAGE} total={apps.length} onPage={setPage} />
        <DataTable rows={shown} onRow={setOpen} rowTitle="Who used it"
          empty={data.loading ? 'Loading…' : 'Nothing recorded yet. Devices report the programs they run with their next inventory after the agent update.'}
          columns={[
            { key: 'p', label: 'Program', className: 'wrap', render: (a) => <><b>{a.product ?? a.exe}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{a.exe}{a.publisher ? ` · ${a.publisher}` : ''}</div></> },
            { key: 'l', label: 'Licensed Product', className: 'wrap', render: (a) => (a.productId ? <Link to={`/modules/licenses/products/${a.productId}`}>{a.productName}</Link> : <span className="faint">—</span>) },
            { key: 'd', label: 'Devices', render: (a) => a.devices.toLocaleString() },
            { key: 'u', label: 'Users', render: (a) => a.users.toLocaleString() },
            { key: 't', label: 'Time Running', render: (a) => hours(a.minutes) },
            { key: 'n', label: 'Launches', render: (a) => a.launches.toLocaleString() },
            { key: 'w', label: 'Last Used', render: (a) => fmt.day(a.lastUsed) },
          ]} />
        <Pager page={page} pageSize={PAGE} total={apps.length} onPage={setPage} />
      </Panel>
      {open && <ProgramDialog app={open} days={days} onClose={() => setOpen(null)} />}
    </>
  );
}

function ProgramDialog({ app, days, onClose }: { app: UsageApp; days: number; onClose: () => void }) {
  const [view, setView] = useState<'devices' | 'runs'>('devices');
  const data = useApi(() => api.get<ProgramUse[]>('/sam/usage/program', { exe: app.exe, days }), [app.exe, days]);
  const runs = useApi(() => (view === 'runs' ? api.get<ProgramRun[]>('/sam/usage/sessions', { exe: app.exe, days }) : Promise.resolve(null)), [app.exe, days, view]);
  const rows = (data.data ?? []).map((r, i) => ({ ...r, id: `${r.assetId}:${r.user ?? ''}:${i}` }));
  const runRows = (runs.data ?? []).map((r, i) => ({ ...r, id: `${r.assetId}:${r.user ?? ''}:${r.startedAt}:${i}` }));
  return (
    <Modal wide title={`${app.product ?? app.exe} — last ${days} days`} onClose={onClose} footer={<button className="btn" onClick={onClose}>Close</button>}>
      <div className="seg" role="tablist" aria-label="View" style={{ marginBottom: 12 }}>
        <button type="button" role="tab" aria-selected={view === 'devices'} className={view === 'devices' ? 'on' : ''} onClick={() => setView('devices')}>By device and user</button>
        <button type="button" role="tab" aria-selected={view === 'runs'} className={view === 'runs' ? 'on' : ''} onClick={() => setView('runs')}>Each launch</button>
      </div>
      {view === 'devices' ? <>
        <ErrorBox error={data.error} />
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No use recorded.'} columns={[
          { key: 'h', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}?tab=experience`}><b>{r.hostname}</b></Link> },
          { key: 'u', label: 'User', render: (r) => r.user ?? '—' },
          { key: 't', label: 'Time Running', render: (r) => hours(r.minutes) },
          { key: 'd', label: 'Days Used', render: (r) => r.daysUsed },
          { key: 'n', label: 'Launches', render: (r) => r.launches },
          { key: 'l', label: 'Last Used', render: (r) => fmt.day(r.lastUsed) },
          { key: 'v', label: 'Version', render: (r) => <span className="mono">{r.version ?? '—'}</span> },
        ]} />
      </> : <>
        <ErrorBox error={runs.error} />
        <DataTable rows={runRows} empty={runs.loading ? 'Loading…' : 'No launch recorded yet. Agents from build 142 report each launch with their next inventory.'} columns={[
          { key: 's', label: 'Started', render: (r) => fmt.date(r.startedAt) },
          { key: 'e', label: 'Closed', render: (r) => (r.running ? <Pill tone="ok">Running</Pill> : fmt.date(r.endedAt)) },
          { key: 'm', label: 'Duration', render: (r) => (r.running ? <span className="muted">{hours(r.minutes)} so far</span> : hours(r.minutes)) },
          { key: 'h', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}?tab=experience`}><b>{r.hostname}</b></Link> },
          { key: 'u', label: 'User', render: (r) => r.user ?? '—' },
        ]} />
        <small className="muted">Times are sampled once a minute: a launch is accurate to about a minute, and programs open for less than a minute may not appear. Up to 500 launches, newest first.</small>
      </>}
    </Modal>
  );
}
