import { useState } from 'react';
import { api } from '../../api/client';
import type { Asset, Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill, statusTone, type Tone } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';
import { DeployAgentDialog } from './DeployAgentDialog';

const CATEGORIES = ['endpoint', 'server', 'virtual_host', 'network_device', 'printer', 'iot', 'other'];
const AGENT: Record<string, [string, Tone]> = { online: ['Online', 'ok'], stale: ['Not seen recently', 'warn'], offline: ['Offline', 'crit'], never: ['Never checked in', ''] };

export function typeLabel(a: { chassis?: string | null; category: string; isVirtual?: boolean | null }) {
  if (a.isVirtual || a.chassis === 'virtual') return 'Virtual machine';
  if (a.chassis === 'server') return 'Physical server';
  return a.chassis ? fmt.label(a.chassis) : fmt.label(a.category);
}

function Ips({ list, primary }: { list?: string[] | null; primary?: string | null }) {
  const all = list && list.length > 0 ? list : primary ? [primary] : [];
  if (all.length === 0) return <span className="muted">—</span>;
  return <span className="mono" title={all.join('\n')}>{all[0]}{all.length > 1 && <span className="muted"> +{all.length - 1}</span>}</span>;
}

/** "Online" for agents that checked in recently, otherwise how long the device has been out of contact. */
export function Availability({ a, long }: { a: Asset; long?: boolean }) {
  const last = a.lastContactAt ?? a.lastSeenAt;
  const days = a.offlineDays ?? Math.floor((Date.now() - new Date(last).getTime()) / 86400000);
  const when = fmt.date(last);
  if (a.online) return <span title={`Agent checked in ${when}`}><Pill tone="ok">Online</Pill></span>;
  if (days < 1) return <span title={when}>{fmt.ago(last)}</span>;
  const tone: Tone = days >= 30 ? 'crit' : days >= 7 ? 'crit' : 'warn';
  return (
    <span title={`Last contact ${when}${a.agentId ? ' (agent)' : ' (scan or inventory)'}`} className="offline-days">
      <Pill tone={tone}>Offline {days} day{days === 1 ? '' : 's'}</Pill>{long && <span className="muted"> · since {fmt.day(last)}</span>}
    </span>
  );
}

export function AssetsPage() {
  const { query } = useRouter();
  const { can } = useAuth();
  const confirm = useConfirm();
  const [q, setQ] = useState(query.get('q') ?? '');
  const [search, setSearch] = useState(query.get('q') ?? '');
  const [category, setCategory] = useState(query.get('category') ?? '');
  const [status, setStatus] = useState('');
  const [agent, setAgent] = useState(query.get('agent') ?? '');
  const [inventory, setInventory] = useState(query.get('inventory') ?? '');
  const [facet, setFacet] = useState<{ facet: string; value: string; label: string } | null>(
    query.get('facet') && query.get('value') != null ? { facet: query.get('facet')!, value: query.get('value')!, label: query.get('label') ?? `${query.get('facet')}: ${query.get('value')}` } : null);
  const [offline, setOffline] = useState(query.get('offlineDays') ?? '');
  const [page, setPage] = useState(1);
  const [picked, setPicked] = useState<string[]>([]);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [deploy, setDeploy] = useState(false);
  const [busy, setBusy] = useState(false);
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<Asset>>('/assets', { q: search, category, status, agent, inventory, facet: facet?.facet, value: facet?.value, offlineDays: offline, page, pageSize, sort: offline ? 'offline' : 'hostname' }), [search, category, status, agent, inventory, facet, offline, page]);
  const settings = useApi(() => (can('settings.manage') ? api.get<Record<string, unknown>>('/system/settings') : Promise.resolve(null)), []);
  const rows = data.data?.items ?? [];
  const showV6 = rows.some((r) => r.ipv6 != null) || settings.data?.['display.show_ipv6'] === true;
  const allPicked = rows.length > 0 && rows.every((r) => picked.includes(r.id));

  async function collect(all: boolean) {
    const n = all ? data.data?.total ?? 0 : picked.length;
    if (!(await confirm({ title: `Collect inventory now from ${all ? `all ${n}` : n} asset(s)?`, confirmLabel: 'Collect now', message: 'Assets with an agent report within about 2 minutes; the others are collected agentlessly with your stored credentials (a scan is started).' }))) return;
    setBusy(true); setMsg(null);
    try {
      const r = await api.post<{ message: string }>('/assets/collect', all ? { all: true } : { assetIds: picked });
      setMsg({ tone: 'info', text: r.message });
      setPicked([]);
    } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  async function toggleV6(on: boolean) {
    await api.put('/system/settings/display.show_ipv6', { value: on });
    settings.reload(); data.reload();
  }
  const reset = () => { setPage(1); setPicked([]); };

  return (
    <>
      <PageHead title="Asset Inventory" desc="Every device Zentriq has discovered, inventoried with the agent or agentlessly, or added manually."
        actions={<>
          {can('agents.manage') && <button className="btn" onClick={() => setDeploy(true)}>Deploy agent</button>}
          {can('scans.run') && (picked.length > 0
            ? <button className="btn primary" disabled={busy} onClick={() => void collect(false)}>Collect inventory now ({picked.length})</button>
            : <button className="btn primary" disabled={busy || !data.data?.total} onClick={() => void collect(true)}>Collect inventory now (all)</button>)}
        </>} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); reset(); setSearch(q); }}>
        <input className="in grow" placeholder="Search hostname, IP, logged-on user, serial, OS or owner…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search assets" />
        <select className="in" value={category} onChange={(e) => { reset(); setCategory(e.target.value); }} aria-label="Category">
          <option value="">All categories</option>
          {CATEGORIES.map((c) => <option key={c} value={c}>{fmt.label(c)}</option>)}
        </select>
        <select className="in" value={agent} onChange={(e) => { reset(); setAgent(e.target.value); }} aria-label="Agent">
          <option value="">Agent: any</option>
          <option value="with">With agent</option>
          <option value="without">Without agent</option>
        </select>
        <select className="in" value={inventory} onChange={(e) => { reset(); setInventory(e.target.value); }} aria-label="Inventory">
          <option value="">Inventory: any</option>
          <option value="success">Inventoried</option>
          <option value="missing">Not inventoried</option>
        </select>
        <select className="in" value={offline} onChange={(e) => { reset(); setOffline(e.target.value); }} aria-label="Offline for">
          <option value="">Last contact: any</option>
          <option value="1">Offline 1+ day</option>
          <option value="7">Offline 7+ days</option>
          <option value="30">Offline 30+ days</option>
          <option value="90">Offline 90+ days</option>
        </select>
        <select className="in" value={status} onChange={(e) => { reset(); setStatus(e.target.value); }} aria-label="Status">
          <option value="">Active</option>
          <option value="retired">Retired</option>
          <option value="all">All statuses</option>
        </select>
        <button className="btn" type="submit">Search</button>
        {can('settings.manage') && settings.data && (
          <label className="row-check" title="Show IPv6 addresses in the inventory (System Settings › Display)">
            <input type="checkbox" checked={settings.data['display.show_ipv6'] === true} onChange={(e) => void toggleV6(e.target.checked)} /> IPv6
          </label>
        )}
      </form>
      {facet && (
        <div style={{ marginBottom: 10 }}>
          <span className="filter-chip">Filtered from dashboard: {facet.label}<button type="button" aria-label="Clear dashboard filter" onClick={() => { reset(); setFacet(null); }}>×</button></span>
        </div>
      )}
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : 'No assets match. Run a discovery scan to find devices.'} columns={[
          ...(can('scans.run') ? [{
            key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all on this page" checked={allPicked} onChange={(e) => setPicked(e.target.checked ? rows.map((r) => r.id) : [])} />,
            render: (r: Asset) => <input type="checkbox" className="row-sel" aria-label={`Select ${r.hostname ?? r.ip}`} checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id))} />,
          }] : []),
          { key: 'h', label: 'Hostname', render: (r) => <><Link to={`/assets/${r.id}`}><b>{r.hostname ?? r.ip ?? '—'}</b></Link>{r.fqdn && r.fqdn.toLowerCase() !== r.hostname?.toLowerCase() && <div className="faint" style={{ fontSize: 11.5 }}>{r.fqdn}</div>}</> },
          { key: 'ip', label: 'IPv4 address', render: (r) => <Ips list={r.ipv4} primary={r.ip?.includes('.') ? r.ip : null} /> },
          ...(showV6 ? [{ key: 'ip6', label: 'IPv6 address', render: (r: Asset) => <Ips list={r.ipv6} /> }] : []),
          { key: 'u', label: 'Logged-on user', render: (r) => r.loggedOnUser ? <span title={r.loggedOnUser}>{r.loggedOnUser}</span> : <span className="muted">—</span> },
          { key: 't', label: 'Type', render: (r) => typeLabel(r) },
          { key: 'os', label: 'Operating system', render: (r) => r.osName ?? fmt.label(r.osFamily) },
          { key: 'inv', label: 'Inventory', render: (r) => <span title={r.lastInventoryAt ? `${fmt.label(r.inventorySource)} · ${fmt.date(r.lastInventoryAt)}` : undefined}><Pill tone={r.inventoryStatus === 'success' ? 'ok' : r.inventoryStatus === 'not_collected' ? '' : 'warn'}>{fmt.label(r.inventoryStatus)}</Pill></span> },
          { key: 'ls', label: 'Last contact', render: (r) => <><Availability a={r} />{r.agentHealth && !r.online && <div className="faint" style={{ fontSize: 11.5 }}>Agent: {AGENT[r.agentHealth]![0].toLowerCase()}</div>}</> },
          ...(status ? [{ key: 'st', label: 'Status', render: (r: Asset) => <Pill tone={statusTone(r.status)}>{fmt.label(r.status)}</Pill> }] : []),
        ]} />
        {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={(p) => { setPage(p); setPicked([]); }} />}
      </Panel>
      {deploy && <DeployAgentDialog onClose={() => setDeploy(false)} />}
    </>
  );
}
