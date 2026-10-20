import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../../api/client';
import type { Paged } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { CopyBox } from '../../components/CopyBox';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { InternetSetup } from './InternetSetup';
import { TrustPanel } from './TrustPanel';
import {
  formatCode, openQuick, openRemote, VIEW_LABEL,
  type QuickSession, type RemoteDevice, type RemoteSession, type RemoteSettings, type RemoteStatus, type RemoteView,
} from './remote';

type Tab = 'quick' | 'devices' | 'sessions' | 'setup';
const TABS: [Tab, string][] = [['quick', 'Quick support'], ['devices', 'Unattended devices'], ['sessions', 'History'], ['setup', 'Settings']];
const err = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : e instanceof Error ? e.message : String(e);
};
type Msg = { tone: 'info' | 'crit' | 'ok'; text: string } | null;

export function RemotePage() {
  const { query, navigate } = useRouter();
  const { can } = useAuth();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'quick') as Tab;
  const status = useApi(() => api.get<RemoteStatus>('/remote/status'), []);
  const settings = useApi(() => api.get<RemoteSettings>('/remote/settings'), []);
  const [msg, setMsg] = useState<Msg>(null);
  const [creating, setCreating] = useState(false);
  const s = status.data;
  return (
    <>
      <PageHead title="Remote Support" desc="Help anyone, anywhere — see the screen, take control and move files, always with the user's consent."
        actions={s?.running && <button className="btn primary" onClick={() => { setCreating(true); if (tab !== 'quick') navigate('/remote?tab=quick', true); }}><Icon name="plus" size={13} /> New support session</button>} />
      <div className="tabs" role="tablist">
        {TABS.filter(([t]) => t !== 'setup' || can('remote.manage')).map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/remote?tab=${t}`, true)}>{l}</button>)}
      </div>
      <ErrorBox error={status.error} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {s && !s.installed && <Callout tone="warn" icon="info">{s.message}</Callout>}
      {s && s.installed && !s.running && <Callout tone="crit" icon="alert">Remote support is installed but not running: {s.message}</Callout>}
      {s && tab === 'quick' && <Quick status={s} allowTerminal={settings.data?.allowTerminal ?? true} creating={creating} setCreating={setCreating} onMsg={setMsg} onChanged={status.reload} />}
      {s && tab === 'devices' && <Devices status={s} allowTerminal={settings.data?.allowTerminal ?? true} onMsg={setMsg} onChanged={status.reload} />}
      {tab === 'sessions' && <Sessions />}
      {s && tab === 'setup' && <Setup status={s} onChanged={() => { status.reload(); settings.reload(); }} />}
    </>
  );
}

/** Remote desktop + file transfer side by side, extra options in a small menu. */
function SessionButtons({ online, allowTerminal, onOpen, primary = 'Remote desktop' }: {
  online: boolean; allowTerminal: boolean; onOpen: (v: 'desktop' | 'files' | 'terminal' | 'device') => void; primary?: string;
}) {
  const [menu, setMenu] = useState(false);
  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(false);
    window.addEventListener('click', close);
    return () => window.removeEventListener('click', close);
  }, [menu]);
  const why = online ? undefined : 'Not connected';
  return (
    <div className="rs-actions">
      <button className="btn sm primary" disabled={!online} title={why} onClick={() => onOpen('desktop')}><Icon name="monitor" size={13} /> {primary}</button>
      <button className="btn sm" disabled={!online} title={why} onClick={() => onOpen('files')}><Icon name="folder" size={13} /> Files</button>
      <span className="rs-more">
        <button className="btn sm icon-only" disabled={!online} aria-label="More session options" aria-expanded={menu} onClick={(e) => { e.stopPropagation(); setMenu(!menu); }}><Icon name="more" size={14} /></button>
        {menu && <div className="rs-menu" role="menu">
          <button role="menuitem" onClick={() => onOpen('device')}><Icon name="chat" size={13} /> Chat &amp; device details</button>
          {allowTerminal && <button role="menuitem" onClick={() => onOpen('terminal')}><Icon name="terminal" size={13} /> Terminal</button>}
        </div>}
      </span>
    </div>
  );
}

// ---- Quick support --------------------------------------------------------------------------------------------------

const QUICK_STATE: Record<QuickSession['status'], [string, 'ok' | 'warn' | 'info' | '' | 'crit']> = {
  waiting: ['Waiting for the user', 'info'], connected: ['Connected', 'ok'], ended: ['Ended', ''], expired: ['Expired', ''],
};

function Quick({ status, allowTerminal, creating, setCreating, onMsg, onChanged }: {
  status: RemoteStatus; allowTerminal: boolean; creating: boolean; setCreating: (b: boolean) => void; onMsg: (m: Msg) => void; onChanged: () => void;
}) {
  const { can } = useAuth();
  const confirm = useConfirm();
  const list = useApi(() => api.get<{ items: QuickSession[]; warning: string | null }>('/remote/quick'), []);
  const [created, setCreated] = useState<{ session: QuickSession; email: string | null; invitation: { subject: string; text: string } } | null>(null);
  const items = list.data?.items ?? [];
  const active = items.filter((q) => q.status === 'waiting' || q.status === 'connected');
  const past = items.filter((q) => q.status === 'ended' || q.status === 'expired');
  // Watch active sessions: the person joins within a minute or two of opening the link.
  const reload = useRef(list.reload);
  reload.current = list.reload;
  useEffect(() => {
    if (active.length === 0) return;
    const t = setInterval(() => reload.current(), 15000);
    return () => clearInterval(t);
  }, [active.length]);

  async function open(q: QuickSession, v: 'desktop' | 'files' | 'terminal' | 'device') {
    onMsg(null);
    try { await openQuick(q.id, v); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  async function end(q: QuickSession) {
    if (!(await confirm({ title: `End session ${formatCode(q.code)}?`, message: 'The support app on the device disconnects and the link stops working.', confirmLabel: 'End session', danger: true }))) return;
    try { await api.post(`/remote/quick/${q.id}/end`); list.reload(); onChanged(); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }

  return (
    <>
      <Kpis items={[
        { label: 'Active sessions', value: active.length.toLocaleString(), sub: `${active.filter((q) => q.status === 'waiting').length} waiting for the user` },
        { label: 'Sessions (30 days)', value: status.quick30d.toLocaleString(), sub: 'on-demand help' },
        { label: 'Unattended devices', value: status.devices.toLocaleString(), sub: `${status.online.toLocaleString()} online now`, to: '/remote?tab=devices' },
        { label: 'Internet access', value: status.internet?.usable ? (status.internet.applied ? 'On' : 'Starting') : 'Not set up', sub: status.internet ? `${status.internet.host}:${status.internet.port}${status.internet.verified ? ' · verified' : status.internet.confirmed ? ' · confirmed' : ''}` : 'public address needed', to: can('remote.manage') ? '/remote?tab=setup' : undefined },
      ]} />
      {!status.internet?.usable && <Callout tone="info" icon="globe">To help people at home or on the road, remote support needs a public address (domain name or public IP) that reaches this server over HTTPS. Zentriq checks it before creating internet links — set it under <Link to="/remote?tab=setup">Settings › Internet access</Link> or when you create a session. People on the office network can be helped now.</Callout>}
      {list.data?.warning && <Callout tone="warn" icon="alert">{list.data.warning}</Callout>}
      <ErrorBox error={list.error} />

      <div className="rs-steps">
        <div><span>1</span><b>Create a session</b><p>Give it the person's name; e-mail the invitation or copy the link into chat.</p></div>
        <div><span>2</span><b>They open the link</b><p>They run the small support app on Windows (nothing is installed), macOS or Linux.</p></div>
        <div><span>3</span><b>They approve, you help</b><p>Screen, control and files only after they click Allow. They can disconnect at any time.</p></div>
      </div>

      {active.length > 0 ? <div className="rs-cards">{active.map((q) => (
        <article key={q.id} className={`rs-card ${q.status}`}>
          <header>
            <div>
              <h3>{q.customer ?? 'Support session'}</h3>
              <div className="faint">Session <b className="mono">{formatCode(q.code)}</b> · by {q.createdBy} · {fmt.ago(q.createdAt)}</div>
            </div>
            <Pill tone={q.status === 'connected' && !q.online ? 'warn' : QUICK_STATE[q.status][1]}>{q.status === 'connected' && !q.online ? 'App closed' : QUICK_STATE[q.status][0]}</Pill>
          </header>
          {q.status === 'waiting'
            ? <div className="rs-wait"><span className="rs-pulse" aria-hidden /> Waiting for {q.customer ?? 'the user'} to open the link — valid until {fmt.date(q.expiresAt)}</div>
            : <div className="rs-dev"><Icon name="monitor" size={16} /><div><b>{q.deviceName}</b><div className="faint">{[q.deviceOs, q.deviceIp].filter(Boolean).join(' · ')}</div></div></div>}
          {q.note && <p className="rs-note">{q.note}</p>}
          <footer>
            {q.status === 'connected' && <SessionButtons online={q.online} allowTerminal={allowTerminal} onOpen={(v) => void open(q, v)} />}
            {q.status === 'waiting' && <InviteButtons q={q} />}
            <button className="btn sm danger" style={{ marginLeft: 'auto' }} onClick={() => void end(q)}>End</button>
          </footer>
        </article>))}
      </div> : !list.loading && <div className="rs-empty">
        <Icon name="remote" size={28} />
        <b>No active sessions</b>
        <p>Start one when someone asks for help — they get a link, you get their screen once they allow it.</p>
        <button className="btn primary" disabled={!status.running} onClick={() => setCreating(true)}><Icon name="plus" size={13} /> New support session</button>
      </div>}

      {past.length > 0 && <Panel flush title="Recent sessions" sub="Last 7 days">
        <DataTable rows={past} columns={[
          { key: 'c', label: 'Session', render: (q) => <><b>{q.customer ?? '—'}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{formatCode(q.code)}</div></> },
          { key: 'd', label: 'Device', className: 'wrap', render: (q) => q.deviceName ? <>{q.deviceName}<div className="faint" style={{ fontSize: 11.5 }}>{[q.deviceOs, q.deviceIp].filter(Boolean).join(' · ')}</div></> : <span className="faint">Nobody joined</span> },
          { key: 'b', label: 'Technician', render: (q) => q.createdBy },
          { key: 't', label: 'Started', render: (q) => fmt.date(q.createdAt) },
          { key: 's', label: 'Result', className: 'wrap', render: (q) => <><Pill tone={QUICK_STATE[q.status][1]}>{QUICK_STATE[q.status][0]}</Pill><div className="faint" style={{ fontSize: 11.5 }}>{q.endReason}</div></> },
        ]} />
      </Panel>}

      {creating && <NewSession status={status} onInternetSaved={onChanged} onClose={() => setCreating(false)} onCreated={(r) => { setCreating(false); setCreated(r); list.reload(); onChanged(); }} />}
      {created && <Created result={created} onClose={() => setCreated(null)} />}
    </>
  );
}

function mailto(q: QuickSession, invitation: { subject: string; text: string }) {
  return `mailto:${encodeURIComponent(q.email ?? '')}?subject=${encodeURIComponent(invitation.subject)}&body=${encodeURIComponent(invitation.text)}`;
}

function InviteButtons({ q }: { q: QuickSession }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="rs-actions">
      <button className="btn sm" onClick={() => void navigator.clipboard?.writeText(q.inviteUrl).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1600); })}><Icon name="link" size={13} /> {copied ? 'Copied' : 'Copy link'}</button>
      {q.email && <a className="btn sm" href={mailto(q, { subject: `Remote support session ${formatCode(q.code)}`, text: `Open this link to let IT help you: ${q.inviteUrl}` })}><Icon name="mail" size={13} /> E-mail</a>}
    </div>
  );
}

function NewSession({ status, onClose, onCreated, onInternetSaved }: {
  status: RemoteStatus; onClose: () => void; onInternetSaved: () => void;
  onCreated: (r: { session: QuickSession; email: string | null; invitation: { subject: string; text: string } }) => void;
}) {
  const { can } = useAuth();
  const [f, setF] = useState({ customer: '', email: '', note: '', hours: 1, sendEmail: true, lan: false });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [inetOk, setInetOk] = useState(!!status.internet?.usable);
  const needInternet = !f.lan && !inetOk;
  async function create() {
    setBusy(true); setError(null);
    try { onCreated(await api.post('/remote/quick', { ...f, sendEmail: f.sendEmail && !!f.email.trim() })); }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) setInetOk(false);
      setError(err(e));
    } finally { setBusy(false); }
  }
  return (
    <Modal title="New support session" onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || needInternet} onClick={() => void create()}>{busy ? 'Creating…' : 'Create session'}</button></>}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <div className="form-v">
        <div className="seg rs-where" role="radiogroup" aria-label="Where is the person?">
          <button type="button" role="radio" aria-checked={!f.lan} className={!f.lan ? 'on' : ''} onClick={() => setF({ ...f, lan: false })}><Icon name="globe" size={13} /> Anywhere (internet)</button>
          <button type="button" role="radio" aria-checked={f.lan} className={f.lan ? 'on' : ''} onClick={() => setF({ ...f, lan: true })}><Icon name="network" size={13} /> On the office network</button>
        </div>
        {!f.lan && (inetOk
          ? <small className="muted"><Icon name="shield" size={12} /> The link uses <span className="mono">https://{status.internet?.host}:{status.internet?.port}</span> (checked).</small>
          : can('remote.manage')
            ? <div className="rs-need">
                <b>A public address is needed</b>
                <p>Enter the public domain name or public IP that reaches this server. Zentriq checks it before creating the link.</p>
                <InternetSetup compact host={status.internet?.host} port={status.internet?.port} enginePort={status.port} onSaved={() => { setInetOk(true); onInternetSaved(); }} />
              </div>
            : <Callout tone="warn" icon="alert">Internet access is not set up. Ask an administrator to set a public address under Remote Support › Settings, or choose “On the office network”.</Callout>)}
        <label className="fld"><span>Who needs help?</span><input className="in" autoFocus placeholder="Name (optional)" value={f.customer} onChange={(e) => setF({ ...f, customer: e.target.value })} /></label>
        <label className="fld"><span>E-mail</span><input className="in" type="email" placeholder="name@example.com (optional)" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></label>
        {f.email.trim() && <label className="row-check"><input type="checkbox" checked={f.sendEmail} onChange={(e) => setF({ ...f, sendEmail: e.target.checked })} /> Send the invitation by e-mail now</label>}
        <label className="fld"><span>Problem / note</span><textarea className="in" rows={2} placeholder="e.g. Outlook does not start" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} /></label>
        <label className="fld"><span>Link valid for</span>
          <select className="in" value={f.hours} onChange={(e) => setF({ ...f, hours: Number(e.target.value) })}>
            <option value={1}>1 hour</option><option value={4}>4 hours</option><option value={24}>1 day</option><option value={72}>3 days</option>
          </select></label>
        <small className="muted">The person is asked to allow every request (screen, files, terminal) and sees a bar with a disconnect button. Each session has its own one-time link. Works on Windows, macOS and Linux.</small>
      </div>
    </Modal>
  );
}

function Created({ result, onClose }: { result: { session: QuickSession; email: string | null; invitation: { subject: string; text: string } }; onClose: () => void }) {
  const q = result.session;
  return (
    <Modal title={`Session ${formatCode(q.code)} is ready`} onClose={onClose}
      footer={<>{q.email && result.email !== 'sent' && <a className="btn" href={mailto(q, result.invitation)}><Icon name="mail" size={13} /> Open in mail app</a>}<button className="btn primary" onClick={onClose}>Done</button></>}>
      {result.email === 'sent' && <Callout tone="ok" icon="info">Invitation e-mailed to {q.email}.</Callout>}
      {result.email && result.email !== 'sent' && <Callout tone="warn" icon="alert">{result.email}</Callout>}
      <div className="form-v">
        <p style={{ margin: 0 }}>Send this link to {q.customer ?? 'the person'} (chat, e-mail or text). The session appears as <b>Connected</b> here once they run the app.</p>
        <CopyBox label="Invitation link" value={q.inviteUrl} rows={2} />
        <CopyBox label="Invitation message" value={result.invitation.text} rows={8} />
      </div>
    </Modal>
  );
}

// ---- Unattended devices ---------------------------------------------------------------------------------------------

function Devices({ status, allowTerminal, onMsg, onChanged }: { status: RemoteStatus; allowTerminal: boolean; onMsg: (m: Msg) => void; onChanged: () => void }) {
  const { can } = useAuth();
  const confirm = useConfirm();
  const [filter, setFilter] = useState('');
  const [q, setQ] = useState('');
  const [search, setSearch] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  useEffect(() => { const t = setTimeout(() => setSearch(q), 300); return () => clearTimeout(t); }, [q]);
  const list = useApi(() => api.get<RemoteDevice[]>('/remote/devices', { q: search, filter }), [search, filter]);
  const manage = can('remote.manage');
  const rows = list.data ?? [];

  async function open(d: RemoteDevice, view: RemoteView) {
    onMsg(null);
    try { await openRemote({ assetId: d.assetId, nodeId: d.nodeId }, view); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  async function deploy(op: 'install' | 'uninstall', assetIds: string[]) {
    if (op === 'uninstall' && !(await confirm({ title: `Remove Zentriq Remote Support from ${assetIds.length} device(s)?`, message: 'Remote sessions to these devices will no longer be possible until it is installed again.', confirmLabel: 'Remove', danger: true }))) return;
    try {
      const r = await api.post<{ message: string }>('/remote/deploy', { assetIds, operation: op });
      onMsg({ tone: 'info', text: r.message });
      setPicked([]);
      setTimeout(list.reload, 1500);
    } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  async function sync() {
    try { onMsg({ tone: 'ok', text: (await api.post<{ message: string }>('/remote/sync')).message }); list.reload(); onChanged(); } catch (e) { onMsg({ tone: 'crit', text: err(e) }); }
  }
  const pickable = rows.filter((d) => d.assetId);
  const toggle = (id: string) => setPicked(picked.includes(id) ? picked.filter((x) => x !== id) : [...picked, id]);

  return (
    <>
      <Kpis items={[
        { label: 'With remote support', value: status.devices.toLocaleString(), sub: `${status.online.toLocaleString()} online now` },
        { label: 'Ready to install', value: status.readyToInstall.toLocaleString(), sub: 'Zentriq Agent can install it' },
        { label: 'Sessions (24 h)', value: status.sessions24h.toLocaleString(), sub: status.lastSync ? `synced ${fmt.ago(status.lastSync)}` : 'not synced yet', to: '/remote?tab=sessions' },
      ]} />
      <Panel flush title="Unattended devices" sub="Installed agent — connect any time, signed out or in, standard or admin users, UAC prompts included" right={<div className="rs-toolbar">
        <input className="in" placeholder="Name or IP…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search devices" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Show">
          <option value="">All</option><option value="online">Online</option><option value="installed">Remote support installed</option>
          <option value="missing">Not installed</option><option value="unmapped">Not matched to an asset</option>
        </select>
        {manage && picked.length > 0 && <>
          <button className="btn sm primary" onClick={() => void deploy('install', picked)}>Install ({picked.length})</button>
          <button className="btn sm" onClick={() => void deploy('uninstall', picked)}>Remove ({picked.length})</button>
        </>}
        {manage && <button className="btn sm" onClick={() => void sync()} title="Read the device list from the remote support server now"><Icon name="retry" size={12} /> Refresh</button>}
      </div>}>
        <ErrorBox error={list.error} />
        <DataTable rows={rows} empty={list.loading ? 'Loading…' : 'No devices.'} columns={[
          ...(manage ? [{ key: 'sel', label: <input type="checkbox" aria-label="Select all" checked={pickable.length > 0 && picked.length === pickable.length} onChange={(e) => setPicked(e.target.checked ? pickable.map((d) => d.assetId!) : [])} />,
            render: (d: RemoteDevice) => d.assetId ? <input type="checkbox" aria-label={`Select ${d.name}`} checked={picked.includes(d.assetId)} onChange={() => toggle(d.assetId!)} /> : null }] : []),
          { key: 'n', label: 'Device', className: 'wrap', render: (d) => <div className="rs-devcell">
            <span className={`rs-dot ${d.nodeId ? (d.online ? 'on' : 'off') : 'none'}`} title={!d.nodeId ? 'Not installed' : d.online ? 'Online' : 'Offline'} />
            <div>{d.assetId ? <Link to={`/assets/${d.assetId}`}>{d.hostname ?? d.name}</Link> : <b>{d.name}</b>}
              <div className="faint" style={{ fontSize: 11.5 }}>{[d.os, d.ip, d.department].filter(Boolean).join(' · ')}</div>
              {d.nodeId && !d.assetId && <div className="faint" style={{ fontSize: 11.5 }}>Not matched to an asset (different host name)</div>}</div></div> },
          { key: 's', label: 'Status', render: (d) => !d.nodeId ? (d.lastAction && d.lastAction.status !== 'succeeded'
            ? <span title={d.lastAction.detail ?? ''}><Pill tone={d.lastAction.status === 'failed' ? 'crit' : 'info'}>{d.lastAction.status === 'failed' ? 'Install failed' : 'Installing'}</Pill></span> : <Pill>Not installed</Pill>)
            : d.online ? <Pill tone="ok">Online</Pill> : <span title={d.lastConnectedAt ? fmt.date(d.lastConnectedAt) : ''}><Pill tone="warn">Offline</Pill>{d.lastConnectedAt && <div className="faint" style={{ fontSize: 11 }}>seen {fmt.ago(d.lastConnectedAt)}</div>}</span> },
          { key: 'x', label: '', render: (d) => d.nodeId
            ? <SessionButtons online={d.online} allowTerminal={allowTerminal} primary="Connect" onOpen={(v) => void open(d, v)} />
            : manage && d.assetId ? (d.canInstall
              ? <button className="btn sm" onClick={() => void deploy('install', [d.assetId!])}>Install</button>
              : <span className="faint" style={{ fontSize: 12 }}>{d.hasAgent ? 'Update the Zentriq Agent to install' : 'Use the installer or install link'}</span>) : null },
        ]} />
      </Panel>
      <p className="faint" style={{ fontSize: 12 }}>Sessions open in a new browser tab. The first time, your browser may ask you to trust the remote support certificate (see Settings).</p>
    </>
  );
}

// ---- History ------------------------------------------------------------------------------------------------------

function Sessions() {
  const [page, setPage] = useState(1);
  const d = useApi(() => api.get<Paged<RemoteSession>>('/remote/sessions', { page, pageSize: 50 }), [page]);
  return (
    <Panel flush title="Session history" sub="Every remote session opened from Zentriq (also in the activity log)">
      <ErrorBox error={d.error} />
      <DataTable rows={d.data?.items ?? []} empty={d.loading ? 'Loading…' : 'No remote sessions yet.'} columns={[
        { key: 't', label: 'When', render: (s) => <span title={fmt.date(s.at)}>{fmt.date(s.at)}</span> },
        { key: 'u', label: 'Technician', render: (s) => s.user },
        { key: 'v', label: 'Session', render: (s) => VIEW_LABEL[s.view] ?? s.view },
        { key: 'd', label: 'Device', render: (s) => s.assetId ? <Link to={`/assets/${s.assetId}`}>{s.device ?? s.assetId}</Link> : s.device ?? '—' },
        { key: 'i', label: 'From', render: (s) => <span className="mono">{s.ip ?? '—'}</span> },
      ]} />
      {d.data && <Pager page={page} pageSize={50} total={d.data.total} onPage={setPage} />}
    </Panel>
  );
}

// ---- Settings -------------------------------------------------------------------------------------------------------

function Setup({ status, onChanged }: { status: RemoteStatus; onChanged: () => void }) {
  const { can } = useAuth();
  const settings = useApi(() => api.get<RemoteSettings>('/remote/settings'), []);
  const [form, setForm] = useState<RemoteSettings | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [link, setLink] = useState<{ url: string; expiresAt: string } | null>(null);
  useEffect(() => { if (settings.data) setForm(settings.data); }, [settings.data]);
  async function save(text = 'Saved. Changes apply to the next session.') {
    if (!form) return;
    setBusy(true); setMsg(null);
    try { await api.put('/remote/settings', form); setMsg({ tone: 'ok', text }); settings.reload(); onChanged(); }
    catch (e) { setMsg({ tone: 'crit', text: err(e) }); } finally { setBusy(false); }
  }
  async function download() {
    setMsg(null);
    try {
      const res = await fetch('/api/v1/remote/installer', { credentials: 'same-origin' });
      if (!res.ok) { const j = await res.json().catch(() => null) as { title?: string } | null; throw new Error(j?.title ?? res.statusText); }
      const url = URL.createObjectURL(await res.blob());
      const a = document.createElement('a'); a.href = url; a.download = 'ZentriqRemoteSupport.exe'; a.click();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    } catch (e) { setMsg({ tone: 'crit', text: err(e) }); }
  }
  async function installLink() {
    setMsg(null);
    try { setLink(await api.post('/remote/install-link', { hours: 72 })); } catch (e) { setMsg({ tone: 'crit', text: err(e) }); }
  }
  const inet = status.internet;
  async function clearInternet() {
    try { await api.del('/remote/internet'); setMsg({ tone: 'ok', text: 'Internet access removed. Invitation links work on the office network only.' }); onChanged(); }
    catch (e) { setMsg({ tone: 'crit', text: err(e) }); }
  }
  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="grid-2">
        <Panel title="Consent and privacy" sub="Unattended devices. Quick support sessions always ask.">
          {!form ? <p className="muted">Loading…</p> : <div className="form-v">
            <label className="row-check"><input type="checkbox" checked={form.prompt} onChange={(e) => setForm({ ...form, prompt: e.target.checked })} /> Ask the user to allow each session (screen, files, terminal)</label>
            <label className="row-check"><input type="checkbox" checked={form.notify} onChange={(e) => setForm({ ...form, notify: e.target.checked })} /> Notify the user when a session starts</label>
            <label className="row-check"><input type="checkbox" checked={form.privacyBar} onChange={(e) => setForm({ ...form, privacyBar: e.target.checked })} /> Show a bar with a disconnect button during remote desktop</label>
            <label className="row-check"><input type="checkbox" checked={form.allowTerminal} onChange={(e) => setForm({ ...form, allowTerminal: e.target.checked })} /> Allow technicians to open a terminal (command line)</label>
            {!form.prompt && <Callout tone="warn" icon="alert">Without consent, technicians can connect without the user approving it. Use this only where your policy allows unattended access (for example servers) and users are informed.</Callout>}
            <div><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>
          </div>}
        </Panel>

        <Panel title="Internet access" sub="Public address for people outside your network (HTTPS only)"
          right={inet ? <Pill tone={inet.usable ? (inet.applied ? 'ok' : 'info') : 'warn'}>{inet.usable ? (inet.applied ? (inet.verified ? 'Verified' : 'Confirmed') : 'Applying…') : 'Check failed'}</Pill> : <Pill>Not set up</Pill>}>
          {inet && <div className="rs-current">
            <span className="mono">https://{inet.host}:{inet.port}</span>
            {inet.checkedAt && <small className="muted">checked {fmt.ago(inet.checkedAt)}</small>}
            <button className="btn sm ghost" onClick={() => void clearInternet()}>Remove</button>
            {inet.message && <small className="muted" style={{ flexBasis: '100%' }}>{inet.message}</small>}
          </div>}
          <InternetSetup host={inet?.host} port={inet?.port} enginePort={status.port} onSaved={() => { setMsg({ tone: 'ok', text: 'Internet address checked and saved. Remote support uses it within a minute.' }); onChanged(); }} />
        </Panel>
      </div>

      <Panel title="Install for unattended access">
        <div className="rs-install">
          <div>
            <h4><Icon name="cycle" size={14} /> With the Zentriq Agent</h4>
            <p>Select devices on <Link to="/remote?tab=devices">Unattended devices</Link> and click <b>Install</b> — done at their next check-in.</p>
          </div>
          <div>
            <h4><Icon name="down" size={14} /> Windows installer</h4>
            <p>Run as administrator, or deploy with Group Policy or Intune.</p>
            <button className="btn sm" disabled={!status.running} onClick={() => void download()}>Download ZentriqRemoteSupport.exe</button>
            <CopyBox label="Silent install command" value="ZentriqRemoteSupport.exe -fullinstall" />
          </div>
          <div>
            <h4><Icon name="link" size={14} /> Install link — Windows, macOS, Linux</h4>
            <p>A page where the user picks their system and installs the agent; valid for 3 days. Good for laptops outside the office.</p>
            {link ? <CopyBox label="Install link" value={link.url} rows={2} /> : <button className="btn sm" disabled={!status.running} onClick={() => void installLink()}>Create install link</button>}
          </div>
        </div>
      </Panel>

      <TrustPanel />

      <details className="rs-advanced">
        <summary>Advanced</summary>
        <dl className="kv-grid">
          <div><dt>Status</dt><dd>{status.running ? <Pill tone="ok">Running</Pill> : status.installed ? <Pill tone="crit">Not running</Pill> : <Pill>Not installed</Pill>}</dd></div>
          <div><dt>Address (LAN)</dt><dd className="mono">{status.publicUrl ?? '—'}</dd></div>
          <div><dt>Windows service</dt><dd className="mono">ZentriqRemote</dd></div>
          <div><dt>Engine</dt><dd>MeshCentral (open source, Apache-2.0), bundled and configured by Zentriq</dd></div>
        </dl>
        <p className="faint" style={{ fontSize: 12 }}>Technicians sign in through Zentriq with a link valid for 2 minutes; there is no separate password. Permissions: <span className="mono">remote.start</span> (sessions), <span className="mono">remote.manage</span> (install, settings), <span className="mono">remote.unattended</span> (full remote console).</p>
        {can('remote.unattended') && status.running && <button className="btn sm" onClick={() => openRemote({}, 'console').catch((e) => setMsg({ tone: 'crit', text: err(e) }))}><Icon name="remote" size={13} /> Open full remote console</button>}
      </details>
    </>
  );
}
