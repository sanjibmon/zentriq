import { useState } from 'react';
import { api } from '../../api/client';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { DataTable, ErrorBox, fmt, Panel, Pill, type Tone } from '../../components/ui';
import { openRemote } from './remote';

interface HelpRequest {
  id: string; number: number; assetId: string; device: string | null; user: string | null; message: string; contact: string | null;
  status: 'open' | 'in_progress' | 'closed'; takenBy: string | null; note: string | null; createdAt: string; updatedAt: string; closedAt: string | null; remoteReady: boolean;
}

const STATUS: Record<HelpRequest['status'], [Tone, string]> = { open: ['warn', 'Open'], in_progress: ['info', 'In Progress'], closed: ['', 'Closed'] };

/** Help requests users send from the Zentriq tray icon (Get Remote Help). */
export function HelpRequests({ onMsg }: { onMsg: (m: { tone: 'ok' | 'crit' | 'info'; text: string } | null) => void }) {
  const [filter, setFilter] = useState('');
  const list = useApi(() => api.get<HelpRequest[]>('/remote/help-requests', { status: filter }), [filter]);
  const rows = list.data ?? [];
  async function setStatus(h: HelpRequest, status: HelpRequest['status']) {
    try { await api.post(`/remote/help-requests/${h.id}`, { status }); onMsg({ tone: 'ok', text: `Help request #${h.number} ${status === 'closed' ? 'closed' : status === 'in_progress' ? 'taken' : 'reopened'}.` }); list.reload(); }
    catch (e) { onMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }
  async function connect(h: HelpRequest) {
    if (h.status === 'open') await setStatus(h, 'in_progress');
    try { await openRemote({ assetId: h.assetId }, 'desktop'); } catch (e) { onMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }
  return (
    <Panel title="Help Requests" sub="Sent by users from the Zentriq tray icon (Get Remote Help) — the user is asked for consent when you connect" flush
      right={<select className="in sm" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Show">
        <option value="">Open and in progress</option><option value="closed">Closed</option><option value="all">All</option>
      </select>}>
      <ErrorBox error={list.error} />
      <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No help requests.'} columns={[
        { key: 'n', label: '#', render: (h) => <b className="mono">#{h.number}</b> },
        { key: 'u', label: 'User and Device', render: (h) => <><b>{h.user ?? '—'}</b><div className="faint" style={{ fontSize: 11.5 }}><Link to={`/assets/${h.assetId}`}>{h.device ?? 'Device'}</Link>{h.contact ? ` · ${h.contact}` : ''}</div></> },
        { key: 'm', label: 'Problem', className: 'wrap', render: (h) => <span style={{ fontSize: 13 }}>{h.message}</span> },
        { key: 's', label: 'Status', render: (h) => <><Pill tone={STATUS[h.status][0]}>{STATUS[h.status][1]}</Pill>{h.takenBy && <div className="faint" style={{ fontSize: 11.5 }}>{h.takenBy}</div>}</> },
        { key: 't', label: 'Sent', render: (h) => <span title={fmt.date(h.createdAt)}>{fmt.ago(h.createdAt)}</span> },
        { key: 'x', label: '', render: (h) => <div className="row-actions">
          {h.status !== 'closed' && h.remoteReady && <button className="btn sm primary" onClick={() => void connect(h)}><Icon name="remote" size={13} /> Connect</button>}
          {h.status === 'open' && <button className="btn sm" onClick={() => void setStatus(h, 'in_progress')}>Take</button>}
          {h.status !== 'closed' ? <button className="btn sm" onClick={() => void setStatus(h, 'closed')}>Close</button> : <button className="btn sm" onClick={() => void setStatus(h, 'open')}>Reopen</button>}
        </div> },
      ]} />
    </Panel>
  );
}
