import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Paged, SoftwareInstall, SoftwareRow } from '../../api/types';
import { Link } from '../../app/router';
import { useAuth } from '../../app/auth';
import { useConfirm } from '../../components/Confirm';
import { useApi } from '../../app/useApi';
import { ChangesTab } from './Changes';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Pager, Panel, Pill } from '../../components/ui';

export function SoftwarePage() {
  const initialQ = new URLSearchParams(window.location.search).get('q') ?? '';
  const [q, setQ] = useState(initialQ);
  const [search, setSearch] = useState(initialQ);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const [tab, setTab] = useState<'all' | 'changes'>(new URLSearchParams(window.location.search).get('tab') === 'changes' ? 'changes' : 'all');
  const pageSize = 50;
  const data = useApi(() => api.get<Paged<SoftwareRow>>('/inventory/software', { q: search, page, pageSize }), [search, page]);
  const installs = useApi(() => (selected ? api.get<SoftwareInstall[]>('/inventory/software/installs', { name: selected }) : Promise.resolve(null)), [selected]);
  const { can } = useAuth();
  const confirm = useConfirm();
  const canUninstall = can('software.uninstall');
  const [picked, setPicked] = useState<string[]>([]);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const list = installs.data ?? [];
  const ready = list.filter((r) => r.agent === 'ready');
  const pending = list.some((r) => r.lastUninstall && ['queued', 'sent', 'running'].includes(r.lastUninstall.status));
  useEffect(() => { setPicked([]); setMsg(null); }, [selected]);
  useEffect(() => {
    if (!pending) return;
    const t = setInterval(installs.reload, 6000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload only bumps a counter
  }, [pending]);

  async function uninstall(all: boolean) {
    if (!selected) return;
    const targets = all ? list : list.filter((r) => picked.includes(r.assetId));
    const n = targets.length;
    const noAgent = targets.filter((r) => r.agent !== 'ready').length;
    const ok = await confirm({
      title: all ? `Uninstall ${selected} from all ${n} device(s)?` : `Uninstall ${selected} from ${n} device(s)?`,
      danger: true, confirmLabel: 'Uninstall',
      typeToConfirm: all && n > 1 ? 'UNINSTALL' : undefined,
      message: <>
        <p style={{ marginTop: 0 }}>The Zentriq Agent on each device removes the application silently at its next check-in (set by its check-in interval). Users may lose unsaved work in that application, and some uninstallers restart services.</p>
        {noAgent > 0 && <p style={{ marginBottom: 0 }}>{noAgent} of these device(s) have no agent (or an outdated one) and will be skipped.</p>}
      </>,
    });
    if (!ok) return;
    try {
      const r = await api.post<{ message: string }>('/software/uninstall', all ? { name: selected, all: true } : { name: selected, assetIds: picked });
      setMsg({ tone: 'info', text: r.message });
      setPicked([]);
      installs.reload();
    } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }

  return (
    <>
      <PageHead title="Software Inventory" />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'all'} className={tab === 'all' ? 'on' : ''} onClick={() => setTab('all')}>Installed software</button>
        <button role="tab" aria-selected={tab === 'changes'} className={tab === 'changes' ? 'on' : ''} onClick={() => setTab('changes')}>Changes</button>
      </div>
      {tab === 'changes' ? <ChangesTab kind="software" /> : <>
      <form className="toolbar-row" onSubmit={(e) => { e.preventDefault(); setPage(1); setSearch(q); }}>
        <input className="in grow" placeholder="Search product or publisher…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search software" />
        <button className="btn" type="submit">Search</button>
      </form>
      <ErrorBox error={data.error} />
      <div className="grid-2" style={{ gridTemplateColumns: selected ? 'minmax(0,2fr) minmax(0,3fr)' : '1fr' }}>
        <Panel flush>
          <DataTable rows={(data.data?.items ?? []).map((r) => ({ ...r, id: r.name }))} empty={data.loading ? 'Loading…' : 'No software inventory yet. Install agents or run a scan with credentials.'} columns={[
            { key: 'n', label: 'Product', render: (r) => <button className="linkish" onClick={() => setSelected(r.name)}><b>{r.name}</b></button> },
            { key: 'p', label: 'Publisher', render: (r) => r.publisher ?? '—' },
            { key: 'i', label: 'Installs', render: (r) => r.installs.toLocaleString() },
            { key: 'v', label: 'Versions', className: 'wrap', render: (r) => r.versions > 0 ? <span title={r.latestVersions.join(', ')}>{r.versions} ({r.latestVersions.slice(0, 2).join(', ')}{r.versions > 2 ? '…' : ''})</span> : '—' },
          ]} />
          {data.data && <Pager page={page} pageSize={pageSize} total={data.data.total} onPage={setPage} />}
        </Panel>
        {selected && (
          <Panel title={selected} sub={installs.data ? `${list.length} device(s) · ${ready.length} can be uninstalled remotely` : ''} flush
            right={<div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
              {canUninstall && picked.length > 0 && <button className="btn sm danger" onClick={() => void uninstall(false)}>Uninstall from selected ({picked.length})</button>}
              {canUninstall && ready.length > 0 && <button className="btn sm danger" onClick={() => void uninstall(true)}>Uninstall from all</button>}
              <button className="btn sm" onClick={() => setSelected(null)}>Close</button>
            </div>}>
            {msg && <div style={{ padding: '10px 12px 0' }}><Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text} <Link to="/activity?kind=uninstall">Activity log</Link></Callout></div>}
            <ErrorBox error={installs.error} />
            <DataTable rows={list.map((r, i) => ({ ...r, id: `${r.assetId}-${i}` }))} empty={installs.loading ? 'Loading…' : 'None.'} columns={[
              ...(canUninstall ? [{
                key: 'sel', label: <input type="checkbox" className="row-sel" aria-label="Select all devices that can be uninstalled" checked={ready.length > 0 && ready.every((r) => picked.includes(r.assetId))} onChange={(e) => setPicked(e.target.checked ? ready.map((r) => r.assetId) : [])} />,
                render: (r: SoftwareInstall) => r.agent === 'ready'
                  ? <input type="checkbox" className="row-sel" aria-label={`Select ${r.hostname ?? r.ip}`} checked={picked.includes(r.assetId)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.assetId] : p.filter((x) => x !== r.assetId))} />
                  : null,
              }] : []),
              { key: 'h', label: 'Device', render: (r) => <><Link to={`/assets/${r.assetId}`}>{r.hostname ?? r.ip ?? r.assetId}</Link><div className="faint" style={{ fontSize: 11.5 }}>{r.osName ?? ''}</div></> },
              { key: 'v', label: 'Version', render: (r) => r.version ?? '—' },
              { key: 'd', label: 'Installed', render: (r) => fmt.day(r.installedOn) },
              { key: 'a', label: 'Remote uninstall', render: (r) => r.lastUninstall
                ? <span title={r.lastUninstall.detail ?? ''}><Pill tone={r.lastUninstall.status === 'succeeded' ? 'ok' : r.lastUninstall.status === 'failed' ? 'crit' : 'warn'}>{r.lastUninstall.status === 'failed' ? 'Failed' : r.lastUninstall.status === 'succeeded' ? 'Removed' : 'Pending'}</Pill></span>
                : r.agent === 'ready' ? (canUninstall ? <button className="btn sm danger" onClick={() => { void (async () => { const ok = await confirm({ title: `Uninstall ${selected} from ${r.hostname ?? r.ip}?`, danger: true, confirmLabel: 'Uninstall', message: 'The Zentriq Agent removes the application silently at its next check-in (set by its check-in interval). The user may lose unsaved work in it.' }); if (!ok) return; try { const res = await api.post<{ message: string }>('/software/uninstall', { name: selected, assetIds: [r.assetId] }); setMsg({ tone: 'info', text: res.message }); installs.reload(); } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } })(); }}>Uninstall</button> : <span className="muted">Agent</span>)
                : <span className="muted" title={r.agent === 'old' ? 'Update the agent to the latest version' : 'Install the Zentriq Agent to uninstall remotely'}>{r.agent === 'old' ? 'Agent update needed' : 'No agent'}</span> },
            ]} />
          </Panel>
        )}
      </div>
      </>}
    </>
  );
}
