import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Check, Field, Fields, Section } from '../../components/Form';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill } from '../../components/ui';
import { errText } from '../deployment/types';
import { useDepartments } from '../departments/Departments';
import { AlertsTab } from '../licenses/LicensesPage';
import { money } from '../licenses/types';

export type AppStatus = 'sanctioned' | 'review' | 'unsanctioned' | 'blocked';
export interface WebApp {
  id: string; catalogKey: string | null; name: string; vendor: string | null; category: string; url: string | null; domains: string[]; status: AppStatus; owner: string | null;
  departmentId: string | null; department: string | null; sso: boolean; entraAppId: string | null; costPerUser: number | null; currency: string; renewalDate: string | null;
  notes: string | null; source: string; firstSeenAt: string | null; createdAt: string; devices30d: number; users30d: number; lastUsed: string | null;
  accounts: number; orphaned: number; inactive: number; admins: number; cost: number | null; productId: string | null;
}
export interface Account {
  id: string; appId: string; app: string; userName: string; displayName: string | null; email: string | null; role: string; status: string; source: string;
  lastLogin: string | null; directorySignIn: string | null; lastUsed: string | null; lastActivity: string | null; licenseTier: string | null; cost: number | null;
  department: string | null; notes: string | null; orphaned: boolean; orphanReason: string | null; inactive: boolean; directoryEnabled: boolean | null; personLeft: boolean | null;
}
interface Summary {
  apps: number; byStatus: Record<string, number>; byCategory: { name: string; count: number }[]; sso: number; new30d: number; inUse30d: number; unsanctionedInUse: number;
  usersSeen30d: number; devicesSeen30d: number; accounts: number; orphaned: number; inactive: number; admins: number; cost: number; inactiveCost: number;
  renewals: { id: string; name: string; renewalDate: string; days: number }[]; top: { id: string; name: string; status: AppStatus; users: number; devices: number }[];
  alerts: Record<string, number>; catalogVersion: string; discovery: boolean; inactiveDays: number;
}

export const APP_STATUS: Record<AppStatus, [string, 'ok' | 'warn' | 'crit' | 'info' | '']> = {
  sanctioned: ['Sanctioned', 'ok'], review: ['Under Review', 'info'], unsanctioned: ['Unsanctioned', 'warn'], blocked: ['Blocked', 'crit'],
};
const SOURCE: Record<string, string> = { manual: 'Added by hand', catalog: 'From the catalogue', usage: 'Found on devices', entra: 'Microsoft Entra SSO', m365: 'Microsoft 365', import: 'Imported' };

type Tab = 'overview' | 'apps' | 'accounts' | 'alerts';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['apps', 'Web Apps'], ['accounts', 'Accounts'], ['alerts', 'Alerts']];

export function WebAppsPage() {
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canManage = can('webapps.manage');
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview';
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const summary = useApi(() => api.get<Summary>('/webapps/summary'), [tick]);
  const s = summary.data;
  const ctx = { tick, canManage, changed, failed };
  const alerts = s ? Object.values(s.alerts).reduce((a, n) => a + n, 0) : 0;
  return (
    <>
      <PageHead title="Web Apps & Accounts" icon="globe" actions={canManage && <Link className="btn primary" to="/modules/webapps?tab=apps&add=1"><Icon name="plus" size={14} /> Add Web App</Link>} />
      {s && <Kpis items={[
        { label: 'Web Apps', value: s.apps.toLocaleString(), sub: `${s.inUse30d} in use (30 d) · ${s.sso} with SSO`, to: '/modules/webapps?tab=apps' },
        { label: 'Shadow IT', value: ((s.byStatus.review ?? 0) + (s.byStatus.unsanctioned ?? 0)).toLocaleString(), sub: `${s.byStatus.review ?? 0} to review · ${s.unsanctionedInUse} unsanctioned in use`, to: '/modules/webapps?tab=apps&filter=shadow' },
        { label: 'Accounts', value: s.accounts.toLocaleString(), sub: `${s.admins} administrators · ${s.usersSeen30d} users seen (30 d)`, to: '/modules/webapps?tab=accounts' },
        { label: 'Orphaned Accounts', value: s.orphaned.toLocaleString(), sub: 'Left or disabled, still active', to: '/modules/webapps?tab=accounts&filter=orphaned' },
        { label: 'Inactive Accounts', value: s.inactive.toLocaleString(), sub: s.inactiveCost > 0 ? `${money(s.inactiveCost)} to reclaim` : `No activity for ${s.inactiveDays} days`, to: '/modules/webapps?tab=accounts&filter=inactive' },
        { label: 'Spend', value: money(s.cost), sub: 'Active accounts × cost per user', to: '/modules/webapps?tab=apps' },
      ]} />}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/modules/webapps?tab=${t}`, true)}>{l}
            {t === 'alerts' && alerts > 0 && <span className="cnt">{alerts}</span>}
          </button>
        ))}
      </div>
      <ErrorBox error={summary.error} />
      {tab === 'overview' && s && <Overview s={s} />}
      {tab === 'apps' && <AppsTab key={query.get('filter') ?? ''} {...ctx} initialFilter={query.get('filter') ?? ''} openAdd={query.get('add') === '1'} />}
      {tab === 'accounts' && <AccountsTable key={query.get('filter') ?? ''} {...ctx} initialFilter={query.get('filter') ?? ''} />}
      {tab === 'alerts' && <AlertsTab {...ctx} scope="webapps" />}
    </>
  );
}

function Overview({ s }: { s: Summary }) {
  const { navigate } = useRouter();
  return (
    <>
      {!s.discovery && <Callout>Finding web apps on devices is off (Software Licenses › Settings). Apps come from Microsoft Entra single sign-on and from what you add.</Callout>}
      <div className="grid-2">
        <Panel flush title="Most Used" sub="Users and devices in the last 30 days">
          <DataTable rows={s.top} onRow={(a) => navigate(`/modules/webapps/${a.id}`)} rowTitle="Open the app" empty="No use seen yet. It comes with the next inventory of Windows devices." columns={[
            { key: 'n', label: 'Web App', render: (a) => <b>{a.name}</b> },
            { key: 's', label: 'Status', render: (a) => <StatusPill s={a.status} /> },
            { key: 'u', label: 'Users', render: (a) => a.users },
            { key: 'd', label: 'Devices', render: (a) => a.devices },
          ]} />
        </Panel>
        <Panel flush title="Renewals" sub="Web app subscriptions ending next">
          <DataTable rows={s.renewals} onRow={(a) => navigate(`/modules/webapps/${a.id}`)} rowTitle="Open the app" empty="No renewal dates recorded." columns={[
            { key: 'n', label: 'Web App', render: (a) => <b>{a.name}</b> },
            { key: 'd', label: 'Renews', render: (a) => <><Pill tone={a.days < 0 ? 'crit' : a.days <= 30 ? 'warn' : ''}>{a.days < 0 ? 'Passed' : `${a.days} d`}</Pill> <span className="faint">{fmt.day(a.renewalDate)}</span></> },
          ]} />
        </Panel>
      </div>
      <Panel title="By Category">
        <div className="stat-line">{s.byCategory.map((c) => <span key={c.name}>{c.name} <b>{c.count}</b></span>)}</div>
        <div className="stat-line">{(Object.keys(APP_STATUS) as AppStatus[]).map((k) => <span key={k}>{APP_STATUS[k][0]} <b>{s.byStatus[k] ?? 0}</b></span>)}<span>New (30 d) <b>{s.new30d}</b></span></div>
      </Panel>
    </>
  );
}

export function StatusPill({ s }: { s: AppStatus }) {
  const [l, t] = APP_STATUS[s] ?? [s, ''];
  return <Pill tone={t}>{l}</Pill>;
}

interface Ctx { tick: number; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }

function AppsTab({ tick, canManage, changed, failed, initialFilter, openAdd }: Ctx & { initialFilter: string; openAdd: boolean }) {
  const { navigate } = useRouter();
  const [status, setStatus] = useState('');
  const [filter, setFilter] = useState(initialFilter);
  const [q, setQ] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  const [edit, setEdit] = useState<WebApp | 'new' | null>(openAdd ? 'new' : null);
  const [catalog, setCatalog] = useState(false);
  const data = useApi(() => api.get<WebApp[]>('/webapps', { status, filter, q }), [status, filter, q, tick]);
  async function setAll(st: AppStatus) {
    try { await api.post('/webapps/status', { ids: picked, status: st }); setPicked([]); changed(`${picked.length} web app(s) marked ${APP_STATUS[st][0].toLowerCase()}.`); } catch (e) { failed(e); }
  }
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search web apps, vendors, owners, domains…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search web apps" />
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="">Every Status</option>{(Object.keys(APP_STATUS) as AppStatus[]).map((k) => <option key={k} value={k}>{APP_STATUS[k][0]}</option>)}
        </select>
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter">
          <option value="">All</option><option value="inuse">In Use (30 Days)</option><option value="shadow">Shadow IT in Use</option><option value="sso">With Single Sign-On</option>
          <option value="orphaned">With Orphaned Accounts</option><option value="inactive">With Inactive Accounts</option>
        </select>
        {canManage && picked.length > 0 && <>
          <button className="btn" onClick={() => void setAll('sanctioned')}>Sanction ({picked.length})</button>
          <button className="btn" onClick={() => void setAll('unsanctioned')}>Unsanction</button>
          <button className="btn danger" onClick={() => void setAll('blocked')}>Block</button>
        </>}
        {canManage && <button className="btn" onClick={() => setCatalog(true)}><Icon name="apps" size={14} /> From Catalogue</button>}
        {canManage && <button className="btn primary" onClick={() => setEdit('new')}><Icon name="plus" size={14} /> Add Web App</button>}
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} onRow={(a) => navigate(`/modules/webapps/${a.id}`)} rowTitle="Open the app" empty={data.loading ? 'Loading…' : 'No web apps yet.'} columns={[
          ...(canManage ? [{ key: 'x', label: '', render: (a: WebApp) => <input type="checkbox" checked={picked.includes(a.id)} onChange={() => setPicked((p) => (p.includes(a.id) ? p.filter((x) => x !== a.id) : [...p, a.id]))} aria-label={`Select ${a.name}`} /> }] : []),
          { key: 'n', label: 'Web App', className: 'wrap', render: (a) => <><b>{a.name}</b>{a.sso && <span className="faint"> · SSO</span>}<div className="faint" style={{ fontSize: 12 }}>{[a.vendor, a.category].filter(Boolean).join(' · ')}</div></> },
          { key: 's', label: 'Status', render: (a) => <StatusPill s={a.status} /> },
          { key: 'u', label: 'Users · Devices', render: (a) => <>{a.users30d} · {a.devices30d}{a.lastUsed && <div className="faint" style={{ fontSize: 11.5 }}>{fmt.ago(a.lastUsed)}</div>}</> },
          { key: 'a', label: 'Accounts', render: (a) => <>{a.accounts}{a.orphaned > 0 && <> <Pill tone="crit">{a.orphaned} orphaned</Pill></>}{a.inactive > 0 && <> <Pill tone="warn">{a.inactive} inactive</Pill></>}</> },
          { key: 'o', label: 'Owner', render: (a) => a.owner ?? a.department ?? '—' },
          { key: 'c', label: 'Cost', render: (a) => money(a.cost, a.currency) },
          { key: 'r', label: 'Renews', render: (a) => (a.renewalDate ? fmt.day(a.renewalDate) : '—') },
          { key: 'src', label: 'Found By', render: (a) => <span className="faint">{SOURCE[a.source] ?? a.source}</span> },
        ]} />
      </Panel>
      {edit && <AppDialog app={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={(id) => { setEdit(null); changed('Web app saved.'); navigate(`/modules/webapps/${id}`); }} />}
      {catalog && <CatalogDialog onClose={() => setCatalog(false)} onSaved={(n) => { setCatalog(false); changed(`${n} web app(s) added.`); }} />}
    </>
  );
}

export function AccountsTable({ tick, canManage, changed, failed, initialFilter, appId, onEdit }: Ctx & { initialFilter: string; appId?: string; onEdit?: (a: Account) => void }) {
  const [filter, setFilter] = useState(initialFilter);
  const [q, setQ] = useState('');
  const confirm = useConfirm();
  const data = useApi(() => api.get<Account[]>('/webapps/accounts', { appId, filter, q }), [appId, filter, q, tick]);
  async function remove(a: Account) {
    if (!await confirm({ title: `Remove the account of ${a.displayName ?? a.userName}?`, message: 'It is removed from Zentriq only; remove it in the app too.', danger: true, confirmLabel: 'Remove' })) return;
    try { await api.del(`/webapps/accounts/${a.id}`); changed('Account removed.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search users, e-mail addresses, apps…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search accounts" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter">
          <option value="">All Accounts</option><option value="active">Active</option><option value="orphaned">Orphaned (Left or Disabled)</option><option value="inactive">Inactive</option>
          <option value="admins">Administrators</option><option value="guests">Guests</option><option value="disabled">Disabled or Removed</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} empty={data.loading ? 'Loading…' : 'No accounts. Import the app\'s user list, connect Microsoft 365, or add accounts.'} columns={[
          { key: 'u', label: 'User', className: 'wrap', render: (a) => <><b>{a.displayName ?? a.userName}</b><div className="faint" style={{ fontSize: 12 }}>{a.userName}</div></> },
          ...(appId ? [] : [{ key: 'app', label: 'Web App', render: (a: Account) => <Link to={`/modules/webapps/${a.appId}`}>{a.app}</Link> }]),
          { key: 'r', label: 'Role', render: (a) => <>{fmt.label(a.role)}{a.licenseTier && <div className="faint" style={{ fontSize: 11.5 }}>{a.licenseTier}</div>}</> },
          { key: 's', label: 'Status', className: 'wrap', render: (a) => <>{a.orphaned ? <Pill tone="crit">Orphaned</Pill> : a.inactive ? <Pill tone="warn">Inactive</Pill> : <Pill tone={a.status === 'active' ? 'ok' : ''}>{fmt.label(a.status)}</Pill>}
            {a.orphanReason && <div className="faint" style={{ fontSize: 11.5 }}>{a.orphanReason}</div>}</> },
          { key: 'l', label: 'Last Activity', render: (a) => <span title={[a.lastLogin && `sign-in ${fmt.date(a.lastLogin)}`, a.directorySignIn && `directory ${fmt.date(a.directorySignIn)}`, a.lastUsed && `device ${fmt.date(a.lastUsed)}`].filter(Boolean).join(' · ')}>{a.lastActivity ? fmt.ago(a.lastActivity) : 'never seen'}</span> },
          { key: 'd', label: 'Department', render: (a) => a.department ?? '—' },
          { key: 'src', label: 'Source', render: (a) => <span className="faint">{SOURCE[a.source] ?? a.source}</span> },
          ...(canManage ? [{ key: 'x', label: '', render: (a: Account) => <div className="toolbar-row" style={{ margin: 0 }}>
            {onEdit && <button className="btn sm ghost" onClick={() => onEdit(a)} aria-label="Edit"><Icon name="edit" size={12} /></button>}
            <button className="btn sm ghost" onClick={() => void remove(a)} aria-label="Remove"><Icon name="trash" size={12} /></button></div> }] : []),
        ]} />
      </Panel>
    </>
  );
}

// ---- dialogs --------------------------------------------------------------------------------------------------------

export function AppDialog({ app, onClose, onSaved }: { app: WebApp | null; onClose: () => void; onSaved: (id: string) => void }) {
  const deps = useDepartments();
  const [f, setF] = useState({
    name: app?.name ?? '', vendor: app?.vendor ?? '', category: app?.category ?? 'Other', url: app?.url ?? '', domains: (app?.domains ?? []).join('\n'), status: app?.status ?? 'sanctioned',
    owner: app?.owner ?? '', departmentId: app?.departmentId ?? '', sso: app?.sso ?? false, costPerUser: app?.costPerUser?.toString() ?? '', currency: app?.currency ?? 'USD',
    renewalDate: app?.renewalDate ?? '', notes: app?.notes ?? '',
  });
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    const body = { ...f, domains: f.domains.split(/[\n,; ]/).map((x) => x.trim()).filter(Boolean), departmentId: f.departmentId || null, costPerUser: f.costPerUser === '' ? null : Number(f.costPerUser),
      renewalDate: f.renewalDate || null, vendor: f.vendor || null, url: f.url || null, owner: f.owner || null, notes: f.notes || null };
    try {
      if (app) { await api.put(`/webapps/${app.id}`, body); onSaved(app.id); }
      else { const r = await api.post<{ id: string }>('/webapps', body); onSaved(r.id); }
    } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={app ? `Edit ${app.name}` : 'Add Web App'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || !f.name.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Application">
          <Fields>
            <Field label="Name" required><input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} maxLength={120} autoFocus placeholder="e.g. Salesforce" /></Field>
            <Field label="Vendor" optional><input className="in" value={f.vendor} onChange={(e) => setF({ ...f, vendor: e.target.value })} placeholder="e.g. Salesforce, Inc." /></Field>
            <Field label="Status"><select className="in" value={f.status} onChange={(e) => setF({ ...f, status: e.target.value as AppStatus })}>{(Object.keys(APP_STATUS) as AppStatus[]).map((k) => <option key={k} value={k}>{APP_STATUS[k][0]}</option>)}</select></Field>
            <Field label="Category"><input className="in" value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })} placeholder="e.g. CRM" /></Field>
          </Fields>
        </Section>
        <Section title="Where It Is Used" sub="Devices are matched by these domains in their browser history and network connections.">
          <Fields>
            <Field label="Address" optional><input className="in" value={f.url} onChange={(e) => setF({ ...f, url: e.target.value })} placeholder="https://app.example.com" /></Field>
            <Field label="Domains" hint="One per line."><textarea className="in mono" rows={2} value={f.domains} onChange={(e) => setF({ ...f, domains: e.target.value })} placeholder="example.com" /></Field>
          </Fields>
          <Check checked={f.sso} onChange={(v) => setF({ ...f, sso: v })} hint="Users sign in with the company identity provider.">Single Sign-On</Check>
        </Section>
        <Section title="Ownership and Cost">
          <Fields>
            <Field label="Owner" optional><input className="in" value={f.owner} onChange={(e) => setF({ ...f, owner: e.target.value })} placeholder="Name of the person" /></Field>
            <Field label="Department"><select className="in" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}><option value="">— Not assigned —</option>{(deps.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
          </Fields>
          <Fields cols={3}>
            <Field label="Cost per user" optional><input className="in" type="number" min={0} step="0.01" value={f.costPerUser} onChange={(e) => setF({ ...f, costPerUser: e.target.value })} placeholder="0.00" /></Field>
            <Field label="Currency"><input className="in" maxLength={3} value={f.currency} onChange={(e) => setF({ ...f, currency: e.target.value.toUpperCase() })} /></Field>
            <Field label="Renewal date" optional><input className="in" type="date" value={f.renewalDate} onChange={(e) => setF({ ...f, renewalDate: e.target.value })} /></Field>
          </Fields>
          <Field label="Notes" optional><textarea className="in" rows={2} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} /></Field>
        </Section>
      </div>
    </Modal>
  );
}

function CatalogDialog({ onClose, onSaved }: { onClose: () => void; onSaved: (n: number) => void }) {
  const data = useApi(() => api.get<{ apps: { key: string; name: string; vendor: string | null; category: string }[] }>('/webapps/catalog'), []);
  const [picked, setPicked] = useState<string[]>([]);
  const [status, setStatus] = useState<AppStatus>('sanctioned');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setBusy(true); setErr(null);
    try { const r = await api.post<{ added: number }>('/webapps/from-catalog', { keys: picked, status }); onSaved(r.added); } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title="Add From the Catalogue" onClose={onClose}
      footer={<><select className="in" value={status} onChange={(e) => setStatus(e.target.value as AppStatus)} aria-label="Status">{(Object.keys(APP_STATUS) as AppStatus[]).map((k) => <option key={k} value={k}>{APP_STATUS[k][0]}</option>)}</select>
        <button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || picked.length === 0} onClick={() => void save()}>{busy ? 'Saving…' : `Add ${picked.length}`}</button></>}>
      <div className="check-grid" style={{ maxHeight: 420, overflow: 'auto' }}>
        {(data.data?.apps ?? []).map((a) => (
          <label key={a.key} className="row-check"><input type="checkbox" checked={picked.includes(a.key)} onChange={() => setPicked((p) => (p.includes(a.key) ? p.filter((x) => x !== a.key) : [...p, a.key]))} />
            {a.name} <span className="faint">· {a.category}</span></label>
        ))}
      </div>
      {err && <Callout tone="crit" icon="alert">{err}</Callout>}
    </Modal>
  );
}
