import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill } from '../../components/ui';
import { errText } from '../deployment/types';
import { money, SEVERITY_TONE } from '../licenses/types';
import { AccountsTable, AppDialog, StatusPill, type Account, type WebApp } from './WebAppsPage';

interface Detail {
  app: WebApp;
  users: { user: string | null; devices: number; days: number; lastSeen: string }[];
  devices: { assetId: string; hostname: string; user: string | null; firstSeen: string; lastSeen: string; days: number; department: string | null }[];
  products: { id: string; name: string }[];
  alerts: { id: number; kind: string; severity: string; title: string; detail: string | null; firstAt: string }[];
}

type Tab = 'accounts' | 'users' | 'devices';

export function WebAppPage({ id }: { id: string }) {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const confirm = useConfirm();
  const canManage = can('webapps.manage');
  const tab: Tab = (['accounts', 'users', 'devices'] as Tab[]).find((t) => t === query.get('tab')) ?? 'accounts';
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [edit, setEdit] = useState(false);
  const [account, setAccount] = useState<Account | 'new' | null>(null);
  const [importing, setImporting] = useState(false);
  const data = useApi(() => api.get<Detail>(`/webapps/${id}`), [id, tick]);
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const d = data.data;
  if (!d) return <><PageHead title="Web App" icon="globe" /><ErrorBox error={data.error} /></>;
  const a = d.app;
  async function remove() {
    if (!await confirm({ title: `Delete ${a.name}?`, message: 'The web app, its accounts and its usage history are removed from Zentriq.', danger: true, confirmLabel: 'Delete' })) return;
    try { await api.del(`/webapps/${id}`); navigate('/modules/webapps?tab=apps'); } catch (e) { failed(e); }
  }
  async function setStatus(status: string) {
    try { await api.post('/webapps/status', { ids: [id], status }); changed('Status changed.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <PageHead title={a.name} icon="globe" actions={canManage && <>
        <select className="in" value={a.status} onChange={(e) => void setStatus(e.target.value)} aria-label="Status">
          <option value="sanctioned">Sanctioned</option><option value="review">Under Review</option><option value="unsanctioned">Unsanctioned</option><option value="blocked">Blocked</option>
        </select>
        <button className="btn" onClick={() => setEdit(true)}><Icon name="edit" size={14} /> Edit</button>
        <button className="btn danger" onClick={() => void remove()}><Icon name="trash" size={14} /> Delete</button>
      </>} />
      <Kpis items={[
        { label: 'Status', value: <StatusPill s={a.status} />, sub: [a.vendor, a.category].filter(Boolean).join(' · ') || '—' },
        { label: 'Users (30 D)', value: a.users30d.toLocaleString(), sub: `${a.devices30d} devices · last ${a.lastUsed ? fmt.ago(a.lastUsed) : 'never'}`, to: `/modules/webapps/${id}?tab=users` },
        { label: 'Accounts', value: a.accounts.toLocaleString(), sub: `${a.admins} administrators`, to: `/modules/webapps/${id}?tab=accounts` },
        { label: 'Orphaned · Inactive', value: `${a.orphaned} · ${a.inactive}`, sub: 'Accounts to remove', to: `/modules/webapps/${id}?tab=accounts` },
        { label: 'Cost', value: money(a.cost, a.currency), sub: a.costPerUser ? `${money(a.costPerUser, a.currency)} per user` : 'Set the cost per user' },
        { label: 'Renewal', value: a.renewalDate ? fmt.day(a.renewalDate) : '—', sub: d.products.length ? <>Licences: {d.products.map((p, i) => <span key={p.id}>{i > 0 && ', '}<Link to={`/modules/licenses/products/${p.id}`}>{p.name}</Link></span>)}</> : 'No licence product linked' },
      ]} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {d.alerts.length > 0 && (
        <Panel flush title="Open Alerts">
          <DataTable rows={d.alerts} columns={[
            { key: 's', label: 'Severity', render: (x) => <Pill tone={SEVERITY_TONE[x.severity]}>{fmt.label(x.severity)}</Pill> },
            { key: 't', label: 'Alert', className: 'wrap', render: (x) => <><b>{x.title}</b>{x.detail && <div className="faint" style={{ fontSize: 12.5 }}>{x.detail}</div>}</> },
            { key: 'f', label: 'Since', render: (x) => fmt.ago(x.firstAt) },
          ]} />
        </Panel>
      )}
      <Panel title="Details">
        <dl className="kv">
          <dt>Address</dt><dd>{a.url ? <a href={a.url} target="_blank" rel="noreferrer">{a.url}</a> : '—'}</dd>
          <dt>Domains</dt><dd className="mono">{a.domains.join(' · ') || '—'}</dd>
          <dt>Owner</dt><dd>{[a.owner, a.department].filter(Boolean).join(' · ') || '—'}</dd>
          <dt>Sign-in</dt><dd>{a.sso ? 'Single sign-on (Microsoft Entra ID)' : 'Own accounts'}</dd>
          <dt>Found</dt><dd>{a.firstSeenAt ? fmt.date(a.firstSeenAt) : fmt.date(a.createdAt)}</dd>
          {a.notes && <><dt>Notes</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{a.notes}</dd></>}
        </dl>
      </Panel>
      <div className="tabs" role="tablist">
        {([['accounts', 'Accounts'], ['users', 'Users Seen'], ['devices', 'Devices']] as [Tab, string][]).map(([t, l]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/modules/webapps/${id}?tab=${t}`, true)}>{l}</button>
        ))}
      </div>
      {tab === 'accounts' && <>
        {canManage && <div className="toolbar-row">
          <span className="hint grow">Import the user list exported from the app's admin console (CSV), or add accounts one by one.</span>
          <button className="btn" onClick={() => setImporting(true)}><Icon name="upload" size={14} /> Import CSV</button>
          <button className="btn primary" onClick={() => setAccount('new')}><Icon name="plus" size={14} /> Add Account</button>
        </div>}
        <AccountsTable tick={tick} canManage={canManage} changed={changed} failed={failed} initialFilter={query.get('filter') ?? ''} appId={id} onEdit={(x) => setAccount(x)} />
      </>}
      {tab === 'users' && (
        <Panel flush>
          <DataTable rows={d.users.map((u, i) => ({ ...u, id: `${u.user ?? ''}:${i}` }))} empty="No use seen on devices yet." columns={[
            { key: 'u', label: 'User', render: (u) => u.user ?? <span className="faint">No user signed in</span> },
            { key: 'd', label: 'Devices', render: (u) => u.devices },
            { key: 'n', label: 'Days Seen', render: (u) => u.days },
            { key: 'l', label: 'Last Seen', render: (u) => fmt.ago(u.lastSeen) },
          ]} />
        </Panel>
      )}
      {tab === 'devices' && (
        <Panel flush>
          <DataTable rows={d.devices.map((x, i) => ({ ...x, id: `${x.assetId}:${i}` }))} empty="No use seen on devices yet." columns={[
            { key: 'h', label: 'Device', render: (x) => <Link to={`/assets/${x.assetId}`}><b>{x.hostname}</b></Link> },
            { key: 'u', label: 'User', render: (x) => x.user ?? '—' },
            { key: 'dep', label: 'Department', render: (x) => x.department ?? '—' },
            { key: 'f', label: 'First Seen', render: (x) => fmt.date(x.firstSeen) },
            { key: 'l', label: 'Last Seen', render: (x) => fmt.ago(x.lastSeen) },
            { key: 'n', label: 'Days', render: (x) => x.days },
          ]} />
        </Panel>
      )}
      {edit && <AppDialog app={a} onClose={() => setEdit(false)} onSaved={() => { setEdit(false); changed('Web app saved.'); }} />}
      {account && <AccountDialog appId={id} account={account === 'new' ? null : account} onClose={() => setAccount(null)} onSaved={() => { setAccount(null); changed('Account saved.'); }} />}
      {importing && <ImportDialog appId={id} onClose={() => setImporting(false)} onSaved={(t) => { setImporting(false); changed(t); }} />}
    </>
  );
}

function AccountDialog({ appId, account, onClose, onSaved }: { appId: string; account: Account | null; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ userName: account?.userName ?? '', displayName: account?.displayName ?? '', email: account?.email ?? '', role: account?.role ?? 'user',
    status: account?.status ?? 'active', lastLogin: account?.lastLogin?.slice(0, 10) ?? '', licenseTier: account?.licenseTier ?? '', cost: account?.cost?.toString() ?? '',
    department: account?.department ?? '', notes: account?.notes ?? '' });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    const body = { ...f, displayName: f.displayName || null, email: f.email || null, lastLogin: f.lastLogin ? `${f.lastLogin}T00:00:00Z` : null, licenseTier: f.licenseTier || null,
      cost: f.cost === '' ? null : Number(f.cost), department: f.department || null, notes: f.notes || null };
    try { if (account) await api.put(`/webapps/accounts/${account.id}`, body); else await api.post(`/webapps/${appId}/accounts`, body); onSaved(); }
    catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={account ? `Edit ${account.displayName ?? account.userName}` : 'Add Account'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !f.userName.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></>}>
      <div className="form-grid-2">
        <label>User name or e-mail<input className="in" value={f.userName} onChange={(e) => setF({ ...f, userName: e.target.value })} autoFocus /></label>
        <label>Name<input className="in" value={f.displayName} onChange={(e) => setF({ ...f, displayName: e.target.value })} /></label>
        <label>E-mail<input className="in" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></label>
        <label>Role<select className="in" value={f.role} onChange={(e) => setF({ ...f, role: e.target.value })}><option value="owner">Owner</option><option value="admin">Administrator</option><option value="user">User</option><option value="guest">Guest</option><option value="service">Service Account</option></select></label>
        <label>Status<select className="in" value={f.status} onChange={(e) => setF({ ...f, status: e.target.value })}><option value="active">Active</option><option value="invited">Invited</option><option value="disabled">Disabled</option><option value="removed">Removed</option></select></label>
        <label>Last sign-in<input className="in" type="date" value={f.lastLogin} onChange={(e) => setF({ ...f, lastLogin: e.target.value })} /></label>
        <label>Plan / licence<input className="in" value={f.licenseTier} onChange={(e) => setF({ ...f, licenseTier: e.target.value })} /></label>
        <label>Cost<input className="in" type="number" min={0} step="0.01" value={f.cost} onChange={(e) => setF({ ...f, cost: e.target.value })} /></label>
        <label>Department<input className="in" value={f.department} onChange={(e) => setF({ ...f, department: e.target.value })} /></label>
        <label>Notes<input className="in" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} /></label>
      </div>
      {err && <Callout tone="crit" icon="alert">{err}</Callout>}
    </Modal>
  );
}

function ImportDialog({ appId, onClose, onSaved }: { appId: string; onClose: () => void; onSaved: (t: string) => void }) {
  const [csv, setCsv] = useState('');
  const [replace, setReplace] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    try { const r = await api.post<{ imported: number; removed: number }>(`/webapps/${appId}/accounts/import`, { csv, replace }); onSaved(`${r.imported} account(s) imported${r.removed ? `, ${r.removed} marked removed` : ''}.`); }
    catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title="Import Accounts" onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !csv.trim()} onClick={() => void save()}>{busy ? 'Importing…' : 'Import'}</button></>}>
      <div className="form-v">
        <p className="muted" style={{ margin: 0 }}>A CSV with a header row. Columns (any order): <span className="mono">user</span> or <span className="mono">email</span> (required), <span className="mono">name</span>, <span className="mono">role</span>,
          <span className="mono"> status</span>, <span className="mono">last login</span>, <span className="mono">plan</span>, <span className="mono">cost</span>, <span className="mono">department</span>. Exports from Google Workspace, Salesforce, Slack, Zoom, Atlassian and most admin consoles work as they are.</p>
        <label className="btn self-start" style={{ cursor: 'pointer' }}><Icon name="upload" size={14} /> Choose File
          <input type="file" accept=".csv,text/csv,text/plain" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void f.text().then(setCsv); }} /></label>
        <textarea className="in mono" rows={8} value={csv} onChange={(e) => setCsv(e.target.value)} placeholder={'email,name,role,last login\nanna@example.com,Anna Rao,admin,2026-10-02'} />
        <label className="row-check"><input type="checkbox" checked={replace} onChange={(e) => setReplace(e.target.checked)} /> The File Is the Full List: Mark Imported Accounts Not in It as Removed</label>
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
      </div>
    </Modal>
  );
}
