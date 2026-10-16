import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, Kpis, Pager, Panel, Pill, type Tone } from '../../components/ui';

export interface ChangeRow {
  id: number; assetId: string; hostname: string | null; os: string | null; department: string | null; detectedAt: string;
  kind: 'hardware' | 'software'; change: string; component: string; item: string; oldValue: string | null; newValue: string | null; source: string | null;
}
export interface ChangeDevice { assetId: string; hostname: string | null; department: string | null; changes: number; last: string; summary: string | null }
export interface ChangeSummary {
  days: number;
  kpi: { hardware: number; hardwareDevices: number; software: number; softwareDevices: number; installed: number; uninstalled: number; updated: number; windowsUpdates: number };
  hardwareDevices: ChangeDevice[]; softwareDevices: ChangeDevice[];
  byComponent: { kind: string; component: string; change: string; count: number }[];
  trend: { day: string; hardware: number; software: number }[];
}

export const CHANGE_LABEL: Record<string, string> = { added: 'Added', removed: 'Removed', changed: 'Changed', installed: 'Installed', uninstalled: 'Uninstalled', updated: 'Updated' };
export const CHANGE_TONE: Record<string, Tone> = { added: 'ok', installed: 'ok', removed: 'crit', uninstalled: 'crit', changed: 'warn', updated: 'info' };

export function ChangeText({ c }: { c: Pick<ChangeRow, 'oldValue' | 'newValue' | 'change'> }) {
  if (c.oldValue && c.newValue) return <span><span className="faint">{c.oldValue}</span> → <b>{c.newValue}</b></span>;
  if (c.newValue) return <span>{c.newValue}</span>;
  if (c.oldValue) return <span className="faint">{c.oldValue}</span>;
  return <span className="muted">—</span>;
}

/** Change history: all devices (inventory pages) or one device (device page). */
export function ChangesView({ kind, assetId, title }: { kind?: 'hardware' | 'software'; assetId?: string; title?: string }) {
  const [days, setDays] = useState(assetId ? 365 : 30);
  const [change, setChange] = useState('');
  const [k, setK] = useState<string>(kind ?? '');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  useEffect(() => { const t = setTimeout(() => { setSearch(q); setPage(1); }, 300); return () => clearTimeout(t); }, [q]);
  const data = useApi(() => api.get<Paged<ChangeRow>>('/inventory/changes', { kind: k || undefined, change: change || undefined, assetId, days, q: search, page, pageSize: 50 }), [k, change, assetId, days, search, page]);
  const opts = (kind ?? k) === 'hardware' ? ['added', 'removed', 'changed'] : (kind ?? k) === 'software' ? ['installed', 'uninstalled', 'updated'] : ['added', 'removed', 'changed', 'installed', 'uninstalled', 'updated'];
  return (
    <Panel flush title={title ?? 'Changes'} sub={data.data ? `${data.data.total.toLocaleString()} in the last ${days} days` : undefined} right={<div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
      {!assetId && <input className="in" placeholder="Device or item…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search changes" />}
      {!kind && <select className="in" value={k} onChange={(e) => { setK(e.target.value); setChange(''); setPage(1); }} aria-label="Hardware or software"><option value="">Hardware and software</option><option value="hardware">Hardware</option><option value="software">Software</option></select>}
      <select className="in" value={change} onChange={(e) => { setChange(e.target.value); setPage(1); }} aria-label="Change"><option value="">All changes</option>{opts.map((o) => <option key={o} value={o}>{CHANGE_LABEL[o]}</option>)}</select>
      <select className="in" value={days} onChange={(e) => { setDays(Number(e.target.value)); setPage(1); }} aria-label="Period"><option value={1}>Last 24 hours</option><option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option><option value={365}>Last year</option></select>
    </div>}>
      <ErrorBox error={data.error} />
      <DataTable rows={data.data?.items ?? []} empty={data.loading ? 'Loading…' : 'No changes found. Changes are recorded from the second inventory of a device onwards.'} columns={[
        { key: 'w', label: 'Detected', render: (c) => <span title={fmt.date(c.detectedAt)}>{fmt.date(c.detectedAt)}</span> },
        ...(!assetId ? [{ key: 'h', label: 'Device', render: (c: ChangeRow) => <><Link to={`/assets/${c.assetId}?tab=changes`}>{c.hostname ?? c.assetId}</Link>{c.department && <div className="faint" style={{ fontSize: 11.5 }}>{c.department}</div>}</> }] : []),
        { key: 'c', label: 'Change', render: (c) => <Pill tone={CHANGE_TONE[c.change]}>{CHANGE_LABEL[c.change] ?? c.change}</Pill> },
        ...(!kind ? [{ key: 'k', label: 'Type', render: (c: ChangeRow) => fmt.label(c.kind) }] : []),
        { key: 'p', label: 'Component', render: (c) => c.component },
        { key: 'i', label: 'Item', className: 'wrap', render: (c) => <b>{c.item}</b> },
        { key: 'v', label: 'Before → after', className: 'wrap', render: (c) => <ChangeText c={c} /> },
      ]} />
      {data.data && data.data.total > 50 && <Pager page={page} pageSize={50} total={data.data.total} onPage={setPage} />}
    </Panel>
  );
}

/** Devices with the most recent changes (dashboard widget and inventory pages). */
export function ChangedDevices({ rows, empty, compact }: { rows: ChangeDevice[]; empty: string; compact?: boolean }) {
  return (
    <DataTable rows={rows.slice(0, compact ? 8 : 15).map((r) => ({ ...r, id: r.assetId }))} empty={empty} columns={[
      { key: 'h', label: 'Device', render: (r) => <><Link to={`/assets/${r.assetId}?tab=changes`}><b>{r.hostname ?? r.assetId}</b></Link>{r.department && <div className="faint" style={{ fontSize: 11 }}>{r.department}</div>}</> },
      { key: 'n', label: 'Changes', render: (r) => r.changes },
      { key: 's', label: 'What changed', className: 'wrap', render: (r) => <span className="clamp-2" title={r.summary ?? ''} style={{ fontSize: 12 }}>{r.summary}</span> },
      { key: 'l', label: 'Last', render: (r) => <span title={fmt.date(r.last)}>{fmt.ago(r.last)}</span> },
    ]} />
  );
}

/** Summary + device list + full history for one kind (Hardware / Software inventory pages). */
export function ChangesTab({ kind }: { kind: 'hardware' | 'software' }) {
  const s = useApi(() => api.get<ChangeSummary>('/inventory/changes/summary', { days: 30 }), []);
  const k = s.data?.kpi;
  return (
    <>
      <ErrorBox error={s.error} />
      {k && (kind === 'hardware'
        ? <Kpis items={[
            { label: 'Hardware changes (30 d)', value: k.hardware.toLocaleString() },
            { label: 'Devices with changes', value: k.hardwareDevices.toLocaleString() },
            ...s.data!.byComponent.filter((c) => c.kind === 'hardware').slice(0, 4).map((c) => ({ label: `${c.component} ${CHANGE_LABEL[c.change]?.toLowerCase() ?? c.change}`, value: c.count.toLocaleString() })),
          ]} />
        : <Kpis items={[
            { label: 'Installed (30 d)', value: k.installed.toLocaleString(), sub: 'applications' },
            { label: 'Uninstalled', value: k.uninstalled.toLocaleString() },
            { label: 'Updated', value: k.updated.toLocaleString(), sub: 'new version or OS upgrade' },
            { label: 'Windows updates', value: k.windowsUpdates.toLocaleString(), sub: 'installed' },
            { label: 'Devices with changes', value: k.softwareDevices.toLocaleString() },
          ]} />)}
      {s.data && <Panel flush title="Changed devices" sub="Last 30 days, most recent first">
        <ChangedDevices rows={kind === 'hardware' ? s.data.hardwareDevices : s.data.softwareDevices} empty={`No ${kind} changes in the last 30 days.`} />
      </Panel>}
      <div style={{ height: 14 }} />
      <ChangesView kind={kind} title={kind === 'hardware' ? 'Hardware change history' : 'Software change history'} />
    </>
  );
}
