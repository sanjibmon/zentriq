import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill } from '../../components/ui';
import { errText } from '../deployment/types';
import { AllocateDialog, ConnectionDialog, LicenseDialog, ProductDialog, RenewDialog } from './LicenseDialogs';
import {
  ACTIVATION_LABEL, consumptionLabel, METRIC_LABEL, money, SEVERITY_TONE, STATUS_LABEL, TYPE_LABEL, type Activation, type Alert, type CloudSku, type CloudUser,
  type Compliance, type Connection, type License, type Settings, type Suggestion, type Summary,
} from './types';

type Tab = 'overview' | 'products' | 'licenses' | 'cloud' | 'activation' | 'discover' | 'alerts' | 'settings';
const TABS: [Tab, string][] = [
  ['overview', 'Overview'], ['products', 'Software Products'], ['licenses', 'Licences'], ['cloud', 'Cloud Licences'], ['activation', 'Windows & Office Activation'],
  ['discover', 'Discover'], ['alerts', 'Alerts'], ['settings', 'Settings'],
];

interface Ctx { tick: number; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }

export function LicensesPage() {
  const { can } = useAuth();
  const { path, query, navigate } = useRouter();
  const canManage = can('licenses.manage');
  const licenseRoute = /^\/modules\/licenses\/licenses\/([0-9a-f-]{36})$/i.exec(path)?.[1] ?? null;
  const tab: Tab = licenseRoute ? 'licenses' : TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview';
  const setTab = (t: Tab) => navigate(`/modules/licenses?tab=${t}`, true);
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const summary = useApi(() => api.get<Summary>('/sam/summary'), [tick]);
  const s = summary.data;
  const changed = (text: string) => { setMsg({ tone: 'info', text }); setTick((x) => x + 1); };
  const failed = (e: unknown) => setMsg({ tone: 'crit', text: errText(e) });
  const ctx: Ctx = { tick, canManage, changed, failed };
  const openAlerts = s ? Object.values(s.detail.alerts).reduce((a, n) => a + n, 0) : 0;

  return (
    <>
      <PageHead title="Software Licenses" icon="key" actions={<>
        <button className="btn" onClick={() => void api.post('/sam/evaluate').then(() => changed('Licence position evaluated.')).catch(failed)}><Icon name="retry" size={14} /> Evaluate Now</button>
        {canManage && <Link className="btn primary" to="/modules/licenses?tab=licenses&add=1"><Icon name="plus" size={14} /> Add Licence</Link>}
      </>} />
      {s && <Kpis items={[
        { label: 'Compliance', value: `${s.compliancePercent}%`, sub: `${s.compliant} of ${s.tracked} products covered`, to: '/modules/licenses?tab=products' },
        { label: 'Under-Licensed', value: (s.shortfall + s.unlicensed).toLocaleString(), sub: `${s.shortfallUnits.toLocaleString()} licences short · ${s.unlicensed} unlicensed`, to: '/modules/licenses?tab=products&status=issues' },
        { label: 'Expiring', value: s.detail.expiring30.toLocaleString(), sub: `In ${s.expiryDays} days · ${s.detail.expired} expired`, to: '/modules/licenses?tab=licenses&filter=expiring' },
        { label: 'Unused Licences', value: s.unusedUnits.toLocaleString(), sub: s.unusedValue > 0 ? `${money(s.unusedValue)} possible saving` : `${s.overLicensed} over-licensed products`, to: '/modules/licenses?tab=products&status=over_licensed' },
        { label: 'Cloud Licences', value: `${s.detail.cloudAssigned.toLocaleString()} / ${s.detail.cloudAvailable.toLocaleString()}`, sub: `Assigned · ${s.detail.cloudDisabledLicensed} on disabled users`, to: '/modules/licenses?tab=cloud' },
        { label: 'Not Activated', value: s.detail.notActivated.toLocaleString(), sub: `Windows / Office · ${s.detail.activationKnown} devices checked`, to: '/modules/licenses?tab=activation&filter=problems' },
      ]} />}
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>{l}
            {t === 'alerts' && openAlerts > 0 && <span className="cnt">{openAlerts}</span>}
          </button>
        ))}
      </div>
      <ErrorBox error={summary.error} />
      {tab === 'overview' && s && <OverviewTab s={s} />}
      {tab === 'products' && <ProductsTab key={query.get('status') ?? ''} {...ctx} initialStatus={query.get('status') ?? ''} />}
      {tab === 'licenses' && <LicensesTab key={(query.get('filter') ?? '') + (licenseRoute ?? '')} {...ctx} initialFilter={query.get('filter') ?? ''} openId={licenseRoute} openAdd={query.get('add') === '1'} />}
      {tab === 'cloud' && <CloudTab {...ctx} />}
      {tab === 'activation' && <ActivationTab key={query.get('filter') ?? ''} initialFilter={query.get('filter') ?? ''} />}
      {tab === 'discover' && <DiscoverTab {...ctx} />}
      {tab === 'alerts' && <AlertsTab {...ctx} scope="licenses" />}
      {tab === 'settings' && <SettingsTab {...ctx} />}
    </>
  );
}

// ---- overview -------------------------------------------------------------------------------------------------------

function OverviewTab({ s }: { s: Summary }) {
  const { navigate } = useRouter();
  const steps: [string, string, string][] = [
    ['Add Products', 'Pick from Discover, or add your own with the names they install under.', '/modules/licenses?tab=discover'],
    ['Record Licences', 'Perpetual, subscription, volume, OEM — with quantity, cost and dates.', '/modules/licenses?tab=licenses'],
    ['Connect Microsoft 365', 'Bought, assigned and expiring cloud licences, every 6 hours.', '/modules/licenses?tab=cloud'],
  ];
  return (
    <>
      <div className="grid-2">
        <Panel flush title="Needs Attention" sub="Under-licensed and unlicensed products">
          <DataTable rows={s.topIssues} onRow={(r) => navigate(`/modules/licenses/products/${r.id}`)} rowTitle="Open the product" empty="No licence shortfalls." columns={[
            { key: 'n', label: 'Product', render: (r) => <b>{r.name}</b> },
            { key: 's', label: 'Status', render: (r) => <StatusPill c={r} /> },
            { key: 'u', label: 'In Use · Owned', render: (r) => <>{(r.consumed ?? 0).toLocaleString()} · {r.unlimited ? '∞' : r.entitled.toLocaleString()}</> },
            { key: 'g', label: 'Gap', render: (r) => r.status === 'unlicensed' ? <Pill tone="crit">{r.consumed ?? r.devices}</Pill> : <Pill tone="crit">−{r.shortfall}</Pill> },
          ]} />
        </Panel>
        <Panel flush title="Renewals" sub="Subscriptions and terms ending next">
          <DataTable rows={s.detail.renewals} onRow={(r) => navigate(`/modules/licenses/licenses/${r.id}`)} rowTitle="Open the licence" empty="No end dates recorded." columns={[
            { key: 'n', label: 'Licence', className: 'wrap', render: (r) => <><b>{r.product}</b>{r.name !== r.product && <div className="faint" style={{ fontSize: 12 }}>{r.name}</div>}</> },
            { key: 'd', label: 'Ends', render: (r) => <DaysPill days={r.days} date={r.endDate} autoRenew={r.autoRenew} /> },
            { key: 'c', label: 'Cost', render: (r) => money(r.totalCost, r.currency) },
          ]} />
        </Panel>
      </div>
      <div className="grid-2">
        <Panel title="Getting Started">
          <ol className="mini-steps">
            {steps.map(([t, d, to], i) => <li key={t}><span className="n">{i + 1}</span><div><b>{t}</b><span className="muted" style={{ fontSize: 12.5 }}>{d}</span><div><Link to={to}>Open</Link></div></div></li>)}
          </ol>
        </Panel>
        <Panel title="Spend">
          <div className="stat-line">
            <span>Licences <b>{s.detail.licenses}</b></span>
            <span>Subscriptions <b>{s.detail.subscriptions}</b></span>
            <span>Perpetual and other <b>{s.detail.perpetual}</b></span>
          </div>
          <div className="stat-line">
            <span>Recorded spend <b>{money(s.detail.spend, s.detail.currencies.length === 1 ? s.detail.currencies[0] : null)}</b></span>
            <span>Renewing in 90 days <b>{money(s.detail.renewalSpend, s.detail.currencies.length === 1 ? s.detail.currencies[0] : null)}</b></span>
          </div>
          <div className="stat-line">
            <span>Shortfall cost <b>{money(s.shortfallCost)}</b></span>
            <span>Possible saving <b>{money(s.unusedValue)}</b></span>
          </div>
          {s.detail.currencies.length > 1 && <p className="faint" style={{ fontSize: 12 }}>Totals add several currencies ({s.detail.currencies.join(', ')}).</p>}
        </Panel>
      </div>
    </>
  );
}

export function StatusPill({ c }: { c: Compliance }) {
  const [l, t] = STATUS_LABEL[c.status] ?? [c.status, ''];
  return <Pill tone={t}>{l}</Pill>;
}

export function DaysPill({ days, date, autoRenew }: { days: number | null; date: string | null; autoRenew?: boolean }) {
  if (!date || days == null) return <>—</>;
  const tone = days < 0 ? 'crit' : days <= 7 ? 'crit' : days <= 30 ? 'warn' : '';
  return <span title={fmt.day(date)}><Pill tone={tone}>{days < 0 ? `Expired ${-days} d ago` : days === 0 ? 'Today' : `${days} d`}</Pill>{autoRenew && <span className="faint"> auto-renews</span>}<div className="faint" style={{ fontSize: 11.5 }}>{fmt.day(date)}</div></span>;
}

// ---- products --------------------------------------------------------------------------------------------------------

function ProductsTab({ tick, canManage, changed, initialStatus }: Ctx & { initialStatus: string }) {
  const { navigate } = useRouter();
  const [status, setStatus] = useState(initialStatus);
  const [q, setQ] = useState('');
  const [adding, setAdding] = useState(false);
  const data = useApi(() => api.get<Compliance[]>('/sam/products', { status, q }), [status, q, tick]);
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search products, publishers, categories…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search products" />
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="">All Products</option><option value="issues">Needs Attention</option><option value="shortfall">Under-Licensed</option><option value="unlicensed">Unlicensed</option>
          <option value="over_licensed">Over-Licensed</option><option value="compliant">Compliant</option><option value="not_measured">Not Measured</option><option value="unused">Not in Use</option>
        </select>
        {canManage && <Link className="btn" to="/modules/licenses?tab=discover"><Icon name="search" size={14} /> Discover</Link>}
        {canManage && <button className="btn primary" onClick={() => setAdding(true)}><Icon name="plus" size={14} /> Add Product</button>}
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} onRow={(r) => navigate(`/modules/licenses/products/${r.id}`)} rowTitle="Open the product"
          empty={data.loading ? 'Loading…' : 'No software products yet. Use Discover to add the commercial software found on your devices.'} columns={[
          { key: 'n', label: 'Product', className: 'wrap', render: (r) => <><b>{r.name}</b><div className="faint" style={{ fontSize: 12 }}>{[r.publisher, r.category].filter(Boolean).join(' · ')}</div></> },
          { key: 'm', label: 'Counted', render: (r) => r.kind === 'cloud' ? 'Cloud · per user' : METRIC_LABEL[r.metric] },
          { key: 'c', label: 'In Use', render: (r) => <>{r.consumed == null ? '—' : r.consumed.toLocaleString()} <span className="faint">{consumptionLabel(r)}</span></> },
          { key: 'e', label: 'Owned', render: (r) => (r.unlimited ? 'Unlimited' : r.entitled.toLocaleString()) },
          { key: 's', label: 'Position', render: (r) => <><StatusPill c={r} />{r.shortfall > 0 && <span className="faint"> −{r.shortfall}</span>}{r.unused > 0 && <span className="faint"> {r.unused} spare</span>}</> },
          { key: 'x', label: 'Next End', render: (r) => (r.nextEnd ? fmt.day(r.nextEnd) : '—') },
          { key: 'l', label: 'Licences', render: (r) => r.licenses },
        ]} />
      </Panel>
      {adding && <ProductDialog product={null} onClose={() => setAdding(false)} onSaved={(id) => { setAdding(false); changed('Product added.'); navigate(`/modules/licenses/products/${id}`); }} />}
    </>
  );
}

// ---- licences --------------------------------------------------------------------------------------------------------

function LicensesTab({ tick, canManage, changed, failed, initialFilter, openId, openAdd }: Ctx & { initialFilter: string; openId: string | null; openAdd: boolean }) {
  const { navigate } = useRouter();
  const confirm = useConfirm();
  const [filter, setFilter] = useState(initialFilter);
  const [q, setQ] = useState('');
  const [edit, setEdit] = useState<License | 'new' | null>(openAdd ? 'new' : null);
  const [renew, setRenew] = useState<License | null>(null);
  const [alloc, setAlloc] = useState<License | null>(null);
  const [shownKey, setShownKey] = useState<{ id: string; key: string } | null>(null);
  const data = useApi(() => api.get<License[]>('/sam/licenses', { filter, q }), [filter, q, tick]);
  const products = useApi(() => api.get<Compliance[]>('/sam/products'), [tick]);
  const detail = useApi(() => (openId ? api.get<{ license: License; allocations: { id: string; assetId: string | null; assetName: string | null; user: string | null; note: string | null; createdAt: string }[] }>(`/sam/licenses/${openId}`) : Promise.resolve(null)), [openId, tick]);
  const close = () => navigate('/modules/licenses?tab=licenses', true);
  async function remove(l: License) {
    if (!await confirm({ title: `Delete ${l.name}?`, message: 'The licence and its allocations are removed. Retire it instead to keep the history.', danger: true, confirmLabel: 'Delete' })) return;
    try { await api.del(`/sam/licenses/${l.id}`); close(); changed('Licence deleted.'); } catch (e) { failed(e); }
  }
  async function revealKey(l: License) {
    try { const r = await api.post<{ key: string }>(`/sam/licenses/${l.id}/key`); setShownKey({ id: l.id, key: r.key }); } catch (e) { failed(e); }
  }
  const d = detail.data;
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search licences, products, vendors, PO or invoice numbers…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search licences" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter">
          <option value="">All Licences</option><option value="active">Active</option><option value="expiring">Expiring Soon</option><option value="expired">Expired</option>
          <option value="subscription">Subscriptions</option><option value="perpetual">Perpetual and Other</option><option value="cloud">From Microsoft 365</option>
          <option value="maintenance">Support Ending</option><option value="retired">Retired</option>
        </select>
        {canManage && <button className="btn primary" onClick={() => setEdit('new')}><Icon name="plus" size={14} /> Add Licence</button>}
      </div>
      <ErrorBox error={data.error} />
      {d && (
        <Panel title={d.license.name} sub={`${d.license.product} · ${TYPE_LABEL[d.license.licenseType]}`} right={<div className="toolbar-row" style={{ margin: 0 }}>
          {canManage && <button className="btn sm" onClick={() => setEdit(d.license)}><Icon name="edit" size={12} /> Edit</button>}
          {canManage && d.license.endDate && <button className="btn sm" onClick={() => setRenew(d.license)}><Icon name="retry" size={12} /> Renew</button>}
          {canManage && <button className="btn sm" onClick={() => setAlloc(d.license)}><Icon name="users" size={12} /> Allocate</button>}
          {canManage && !d.license.synced && <button className="btn sm danger" onClick={() => void remove(d.license)}><Icon name="trash" size={12} /> Delete</button>}
          <button className="btn sm" onClick={close}><Icon name="x" size={12} /> Close</button>
        </div>}>
          <dl className="kv">
            <dt>Product</dt><dd><Link to={`/modules/licenses/products/${d.license.productId}`}>{d.license.product}</Link></dd>
            <dt>Quantity</dt><dd>{d.license.quantity ?? d.license.cloudQuantity ?? 'Unlimited'} · {METRIC_LABEL[d.license.metric]}{d.license.cloudConsumed != null && <> · {d.license.cloudConsumed} assigned in Microsoft 365</>}</dd>
            <dt>Dates</dt><dd>{[d.license.purchaseDate && `bought ${fmt.day(d.license.purchaseDate)}`, d.license.startDate && `from ${fmt.day(d.license.startDate)}`, d.license.endDate && `until ${fmt.day(d.license.endDate)}`,
              d.license.maintenanceEnd && `support until ${fmt.day(d.license.maintenanceEnd)}`].filter(Boolean).join(' · ') || '—'}{d.license.autoRenew && ' · renews automatically'}</dd>
            <dt>Purchase</dt><dd>{[d.license.vendor, d.license.reseller && `via ${d.license.reseller}`, d.license.poNumber && `PO ${d.license.poNumber}`, d.license.invoiceNumber && `invoice ${d.license.invoiceNumber}`, d.license.contractNumber && `contract ${d.license.contractNumber}`].filter(Boolean).join(' · ') || '—'}</dd>
            <dt>Cost</dt><dd>{money(d.license.totalCost, d.license.currency)}{d.license.unitCost != null && <> · {money(d.license.unitCost, d.license.currency)} each</>}{d.license.billing && <> · {fmt.label(d.license.billing)}</>}</dd>
            <dt>Rights</dt><dd>{[d.license.edition, d.license.version, d.license.downgradeRights && 'downgrade rights'].filter(Boolean).join(' · ') || '—'}</dd>
            <dt>Licence key</dt><dd>{d.license.hasKey ? <><span className="mono">{shownKey?.id === d.license.id ? shownKey.key : d.license.keyHint}</span>{canManage && shownKey?.id !== d.license.id && <> <button className="link-btn" onClick={() => void revealKey(d.license)}>Show</button></>}</> : '—'}</dd>
            <dt>Owner</dt><dd>{[d.license.owner, d.license.department].filter(Boolean).join(' · ') || '—'}</dd>
            {d.license.cloudSku && <><dt>Microsoft 365</dt><dd>{d.license.cloudConnection} · <span className="mono">{d.license.cloudSku}</span>{d.license.synced && ' · kept current by the sync'}</dd></>}
            {d.license.notes && <><dt>Notes</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{d.license.notes}</dd></>}
          </dl>
          {d.allocations.length > 0 && <>
            <h4 style={{ margin: '14px 0 6px' }}>Allocated To ({d.allocations.length})</h4>
            <DataTable rows={d.allocations} columns={[
              { key: 'w', label: 'Device / User', render: (a) => a.assetId ? <Link to={`/assets/${a.assetId}`}>{a.assetName}</Link> : a.user },
              { key: 'n', label: 'Note', render: (a) => a.note ?? '' },
              { key: 'd', label: 'Since', render: (a) => fmt.day(a.createdAt) },
              ...(canManage ? [{ key: 'x', label: '', render: (a: { id: string }) => <button className="btn sm ghost" onClick={() => void api.del(`/sam/allocations/${a.id}`).then(() => changed('Allocation removed.')).catch(failed)}><Icon name="x" size={12} /></button> }] : []),
            ]} />
          </>}
        </Panel>
      )}
      <Panel flush>
        <DataTable rows={data.data ?? []} onRow={(l) => navigate(`/modules/licenses/licenses/${l.id}`)} rowTitle="Open the licence" empty={data.loading ? 'Loading…' : 'No licences recorded.'} columns={[
          { key: 'n', label: 'Licence', className: 'wrap', render: (l) => <><b>{l.product}</b>{l.name !== l.product && <div className="faint" style={{ fontSize: 12 }}>{l.name}</div>}</> },
          { key: 't', label: 'Type', render: (l) => <>{TYPE_LABEL[l.licenseType]}{l.synced && <span className="faint"> · M365</span>}</> },
          { key: 'q', label: 'Quantity', render: (l) => <>{l.quantity ?? l.cloudQuantity ?? '∞'} <span className="faint">{METRIC_LABEL[l.metric].toLowerCase()}</span></> },
          { key: 'e', label: 'Ends', render: (l) => <DaysPill days={l.daysLeft} date={l.endDate} autoRenew={l.autoRenew} /> },
          { key: 'v', label: 'Vendor', render: (l) => l.vendor ?? '—' },
          { key: 'c', label: 'Cost', render: (l) => money(l.totalCost, l.currency) },
          { key: 's', label: 'Status', render: (l) => <Pill tone={l.status === 'expired' ? 'crit' : l.status === 'expiring' ? 'warn' : l.status === 'retired' ? '' : 'ok'}>{fmt.label(l.status)}</Pill> },
        ]} />
      </Panel>
      {edit && <LicenseDialog license={edit === 'new' ? null : edit} products={(products.data ?? []).map((p) => ({ id: p.id, name: p.name }))} onClose={() => setEdit(null)}
        onSaved={() => { setEdit(null); changed('Licence saved.'); }} />}
      {renew && <RenewDialog license={renew} onClose={() => setRenew(null)} onSaved={() => { setRenew(null); changed('Licence renewed.'); }} />}
      {alloc && <AllocateDialog license={alloc} onClose={() => setAlloc(null)} onSaved={() => { setAlloc(null); changed('Allocated.'); }} />}
    </>
  );
}

// ---- cloud licences --------------------------------------------------------------------------------------------------

function CloudTab({ tick, canManage, changed, failed }: Ctx) {
  const confirm = useConfirm();
  const data = useApi(() => api.get<{ connections: Connection[]; skus: CloudSku[] }>('/sam/cloud'), [tick]);
  const [edit, setEdit] = useState<Connection | 'new' | null>(null);
  const [users, setUsers] = useState<{ sku?: CloudSku; filter: string } | null>(null);
  const conns = data.data?.connections ?? [];
  const skus = useMemo(() => (data.data?.skus ?? []).map((k) => ({ ...k, id: `${k.connectionId}:${k.skuId}` })), [data.data]);
  async function remove(c: Connection) {
    if (!await confirm({ title: `Remove ${c.name}?`, message: 'Its cloud licences, users and synced licences are removed from Zentriq (nothing changes in Microsoft 365).', danger: true, confirmLabel: 'Remove' })) return;
    try { await api.del(`/sam/cloud/connections/${c.id}`); changed('Connection removed.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <ErrorBox error={data.error} />
      <Panel flush title="Microsoft 365 / Entra ID" right={canManage && <button className="btn sm primary" onClick={() => setEdit('new')}><Icon name="plus" size={12} /> Connect</button>}>
        <DataTable rows={conns} empty="Not connected. Connect a tenant to track bought, assigned and expiring Microsoft 365 licences." columns={[
          { key: 'n', label: 'Connection', render: (c) => <><b>{c.name}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{c.tenantId}</div></> },
          { key: 's', label: 'Last Sync', className: 'wrap', render: (c) => <>{c.lastStatus === 'failed' ? <Pill tone="crit">Failed</Pill> : c.lastStatus === 'succeeded' ? <Pill tone="ok">OK</Pill> : <Pill tone="info">Waiting</Pill>} {fmt.ago(c.lastSyncAt)}
            {c.lastError && <div className="faint" style={{ fontSize: 12 }}>{c.lastError}</div>}{c.lastNote && <div className="faint" style={{ fontSize: 12 }}>{c.lastNote}</div>}</> },
          { key: 'u', label: 'Users · Licence Types', render: (c) => <>{c.users.toLocaleString()} · {c.skus}</> },
          ...(canManage ? [{ key: 'x', label: '', render: (c: Connection) => <div className="toolbar-row" style={{ margin: 0 }}>
            <button className="btn sm" onClick={() => void api.post(`/sam/cloud/connections/${c.id}/sync`).then(() => changed('Sync started; it takes a minute or two.')).catch(failed)}><Icon name="retry" size={12} /> Sync Now</button>
            <button className="btn sm" onClick={() => setEdit(c)}><Icon name="edit" size={12} /></button>
            <button className="btn sm danger" onClick={() => void remove(c)}><Icon name="trash" size={12} /></button></div> }] : []),
        ]} />
      </Panel>
      <Panel flush title="Cloud Licences" sub="Bought (enabled) and assigned per licence type">
        <DataTable rows={skus} onRow={(k) => setUsers({ sku: k, filter: 'licensed' })} rowTitle="Show the users" empty="No cloud licences yet." columns={[
          { key: 'n', label: 'Licence', className: 'wrap', render: (k) => <><b>{k.name ?? k.skuPart}</b><div className="faint mono" style={{ fontSize: 11.5 }}>{k.skuPart}{conns.length > 1 ? ` · ${k.connection}` : ''}</div></> },
          { key: 'a', label: 'Assigned · Bought', render: (k) => <UseCell used={k.consumed} total={k.enabled + k.warning} /> },
          { key: 'f', label: 'Available', render: (k) => k.available ?? Math.max(0, k.enabled + k.warning - k.consumed) },
          { key: 'r', label: 'Reclaim', render: (k) => <>{(k.disabledAssigned ?? 0) > 0 && <button className="link-btn" onClick={(e) => { e.stopPropagation(); setUsers({ sku: k, filter: 'disabled' }); }}>{k.disabledAssigned} disabled</button>}
            {(k.inactiveAssigned ?? 0) > 0 && <> {' '}<button className="link-btn" onClick={(e) => { e.stopPropagation(); setUsers({ sku: k, filter: 'inactive' }); }}>{k.inactiveAssigned} inactive</button></>}</> },
          { key: 'e', label: 'Renews / Ends', render: (k) => k.nextLifecycle ? <>{fmt.day(k.nextLifecycle)}{k.isTrial && <Pill tone="warn">Trial</Pill>}</> : '—' },
          { key: 's', label: 'Status', render: (k) => <Pill tone={k.status === 'Enabled' ? 'ok' : k.status === 'Warning' ? 'warn' : k.status ? 'crit' : ''}>{k.status === 'Warning' ? 'Grace Period' : k.status ?? '—'}</Pill> },
          { key: 'p', label: '', render: (k) => k.productId ? <Link to={`/modules/licenses/products/${k.productId}`}>Product</Link> : null },
        ]} />
      </Panel>
      {users && <CloudUsers sku={users.sku} initialFilter={users.filter} onClose={() => setUsers(null)} />}
      {edit && <ConnectionDialog connection={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); changed('Connection saved; the first sync starts now.'); }} />}
    </>
  );
}

function UseCell({ used, total }: { used: number; total: number }) {
  const pct = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : 0;
  return (
    <div style={{ minWidth: 120 }}>
      <div className="meter"><i style={{ width: `${pct}%`, background: used > total ? 'var(--crit)' : pct >= 95 ? 'var(--warn)' : undefined }} /></div>
      <span style={{ fontSize: 12 }}>{used.toLocaleString()} of {total.toLocaleString()}</span>
    </div>
  );
}

function CloudUsers({ sku, initialFilter, onClose }: { sku?: CloudSku; initialFilter: string; onClose: () => void }) {
  const [filter, setFilter] = useState(initialFilter);
  const data = useApi(() => api.get<CloudUser[]>('/sam/cloud/users', { connectionId: sku?.connectionId, skuId: sku?.skuId, filter }), [sku, filter]);
  return (
    <Panel flush title={`Users${sku ? ` · ${sku.name ?? sku.skuPart}` : ''}`} right={<div className="toolbar-row" style={{ margin: 0 }}>
      <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Users">
        <option value="licensed">With This Licence</option><option value="disabled">Disabled Users With a Licence</option><option value="inactive">Inactive Users With a Licence</option>
      </select>
      <button className="btn sm" onClick={onClose}><Icon name="x" size={12} /> Close</button></div>}>
      <DataTable rows={(data.data ?? []).map((u) => ({ ...u, id: u.userId }))} empty={data.loading ? 'Loading…' : 'No users.'} columns={[
        { key: 'u', label: 'User', className: 'wrap', render: (u) => <><b>{u.displayName ?? u.upn}</b><div className="faint" style={{ fontSize: 12 }}>{u.upn}</div></> },
        { key: 'd', label: 'Department', render: (u) => u.department ?? '—' },
        { key: 'e', label: 'Account', render: (u) => u.enabled === false ? <Pill tone="crit">Disabled</Pill> : <Pill tone="ok">Enabled</Pill> },
        { key: 's', label: 'Last Sign-In', render: (u) => (u.lastSignIn ? fmt.ago(u.lastSignIn) : <span className="faint">not available</span>) },
        { key: 'l', label: 'Licences', className: 'wrap', render: (u) => <span style={{ fontSize: 12.5 }}>{u.licenses.join(', ')}</span> },
      ]} />
    </Panel>
  );
}

// ---- activation ------------------------------------------------------------------------------------------------------

function ActivationTab({ initialFilter }: { initialFilter: string }) {
  const [filter, setFilter] = useState(initialFilter || 'problems');
  const [q, setQ] = useState('');
  const data = useApi(() => api.get<{ rows: Activation[]; channels: Record<string, number> }>('/sam/activation', { filter: filter === 'all' ? null : filter, q }), [filter, q]);
  const rows = (data.data?.rows ?? []).map((r, i) => ({ ...r, id: `${r.assetId}:${r.product}:${i}` }));
  const channels = Object.entries(data.data?.channels ?? {}).sort((a, b) => b[1] - a[1]);
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search devices, products, channels…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search" />
        <select className="in" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter">
          <option value="problems">Not Activated or Not Genuine</option><option value="windows">Windows</option><option value="office">Office</option><option value="firmware">Firmware (OEM) Licences</option><option value="all">Everything</option>
        </select>
      </div>
      {channels.length > 0 && <div className="stat-line">{channels.map(([c, n]) => <span key={c}>{c} <b>{n}</b></span>)}</div>}
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={rows} empty={data.loading ? 'Loading…' : filter === 'problems' ? 'Every device reports Windows and Office activated.' : 'No activation data yet. It comes with the next inventory of Windows devices (Zentriq Agent or WinRM).'} columns={[
          { key: 'h', label: 'Device', render: (r) => <Link to={`/assets/${r.assetId}`}><b>{r.hostname}</b></Link> },
          { key: 'p', label: 'Product', className: 'wrap', render: (r) => <>{r.product}{r.description && <div className="faint" style={{ fontSize: 11.5 }}>{r.description}</div>}</> },
          { key: 'c', label: 'Channel', render: (r) => r.channel ?? '—' },
          { key: 's', label: 'Status', render: (r) => <Pill tone={r.activated ? 'ok' : r.status === 'unknown' ? '' : 'crit'}>{ACTIVATION_LABEL[r.status] ?? r.status}</Pill> },
          { key: 'k', label: 'Key', render: (r) => (r.partialKey ? <span className="mono">•••••-{r.partialKey}</span> : '—') },
          { key: 'g', label: 'Grace Left', render: (r) => (r.graceMinutes && !r.activated ? `${Math.round(r.graceMinutes / 1440)} d` : '—') },
          { key: 'd', label: 'Department', render: (r) => r.department ?? '—' },
          { key: 't', label: 'Checked', render: (r) => fmt.ago(r.collectedAt) },
        ]} />
      </Panel>
    </>
  );
}

// ---- discover --------------------------------------------------------------------------------------------------------

function DiscoverTab({ canManage, changed, failed }: Ctx) {
  const { navigate } = useRouter();
  const data = useApi(() => api.get<{ catalog: Suggestion[]; uncovered: { name: string; publisher: string | null; devices: number }[] }>('/sam/discover'), []);
  const [picked, setPicked] = useState<string[]>([]);
  const [own, setOwn] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function add() {
    setBusy(true);
    try { const r = await api.post<{ added: number }>('/sam/products/from-catalog', { keys: picked }); setPicked([]); changed(`${r.added} product(s) added. Record their licences next.`); navigate('/modules/licenses?tab=products'); }
    catch (e) { failed(e); } finally { setBusy(false); }
  }
  const cat = (data.data?.catalog ?? []).map((c) => ({ ...c, id: c.key }));
  return (
    <>
      <ErrorBox error={data.error} />
      <Panel flush title="Commercial Software Found" sub="Usually licensed; not yet tracked" right={canManage && picked.length > 0 && <button className="btn sm primary" disabled={busy} onClick={() => void add()}>Track {picked.length} Product(s)</button>}>
        <DataTable rows={cat} empty={data.loading ? 'Looking through the software inventory…' : 'Nothing new: all commercial software found is tracked.'} columns={[
          ...(canManage ? [{ key: 'x', label: '', render: (c: Suggestion) => <input type="checkbox" checked={picked.includes(c.key)} onChange={() => setPicked((p) => (p.includes(c.key) ? p.filter((x) => x !== c.key) : [...p, c.key]))} aria-label={`Track ${c.name}`} /> }] : []),
          { key: 'n', label: 'Product', className: 'wrap', render: (c) => <><b>{c.name}</b><div className="faint" style={{ fontSize: 12 }}>{[c.publisher, c.category].filter(Boolean).join(' · ')}</div>{c.notes && <div className="faint" style={{ fontSize: 11.5 }}>{c.notes}</div>}</> },
          { key: 'd', label: 'Devices', render: (c) => <b>{c.devices}</b> },
          { key: 'm', label: 'Installed As', className: 'wrap', render: (c) => <span style={{ fontSize: 12.5 }}>{c.matches.map((m) => `${m.name} (${m.devices})`).join(', ')}</span> },
          { key: 'l', label: 'Usually', render: (c) => `${fmt.label(c.license)} · ${METRIC_LABEL[c.metric].toLowerCase()}` },
        ]} />
      </Panel>
      <Panel flush title="Most Installed, Not Tracked" sub="Add the ones the organisation pays for">
        <DataTable rows={(data.data?.uncovered ?? []).map((u) => ({ ...u, id: u.name }))} columns={[
          { key: 'n', label: 'Installed As', className: 'wrap', render: (u) => u.name },
          { key: 'p', label: 'Publisher', className: 'wrap', render: (u) => u.publisher ?? '—' },
          { key: 'd', label: 'Devices', render: (u) => u.devices },
          ...(canManage ? [{ key: 'x', label: '', render: (u: { name: string }) => <button className="btn sm" onClick={() => setOwn(u.name)}><Icon name="plus" size={12} /> Track</button> }] : []),
        ]} />
      </Panel>
      {own && <ProductDialog product={{ id: '', name: own.replace(/\s+\d[\d.]*.*$/, ''), publisher: (data.data?.uncovered ?? []).find((u) => u.name === own)?.publisher ?? null, category: 'Other', namePatterns: [own.replace(/\s+\d[\d.]*.*$/, '') + '*'], excludePatterns: [], publisherPatterns: [],
        metric: 'device', kind: 'installed', needsLicense: true, webAppId: null, webAppName: null, cloudSkus: [], catalogKey: null, notes: null, createdBy: null, createdAt: '', updatedAt: '' }} isNew
        onClose={() => setOwn(null)} onSaved={(id) => { setOwn(null); changed('Product added.'); navigate(`/modules/licenses/products/${id}`); }} />}
    </>
  );
}

// ---- alerts (shared with Web Apps & Accounts) ------------------------------------------------------------------------

export function AlertsTab({ tick, canManage, changed, failed, scope }: Ctx & { scope: 'licenses' | 'webapps' }) {
  const { navigate } = useRouter();
  const [status, setStatus] = useState('open');
  const base = scope === 'webapps' ? '/webapps/alerts' : '/sam/alerts';
  const data = useApi(() => api.get<Alert[]>(base, { status }), [status, tick, base]);
  return (
    <>
      <div className="toolbar-row">
        <span className="hint grow">{scope === 'webapps' ? 'Unsanctioned apps in use, accounts of people who left, inactive accounts.' : 'Shortfalls, unlicensed software, expiring subscriptions, cloud overuse, activation.'} New alerts reach the bell, the e-mail recipients in Settings and integration channels.</span>
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Show">
          <option value="open">Open</option><option value="dismissed">Dismissed</option><option value="resolved">Resolved</option><option value="all">All</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} onRow={(a) => a.link && navigate(a.link)} rowTitle="Open" empty={data.loading ? 'Loading…' : 'No alerts.'} columns={[
          { key: 's', label: 'Severity', render: (a) => <Pill tone={SEVERITY_TONE[a.severity]}>{fmt.label(a.severity)}</Pill> },
          { key: 't', label: 'Alert', className: 'wrap', render: (a) => <><b>{a.title}</b>{a.detail && <div className="faint" style={{ fontSize: 12.5 }}>{a.detail}</div>}</> },
          { key: 'f', label: 'Since', render: (a) => fmt.ago(a.firstAt) },
          { key: 'r', label: 'State', render: (a) => (a.resolvedAt ? <span className="faint">Resolved {fmt.ago(a.resolvedAt)}</span> : a.dismissed ? <span className="faint">Dismissed by {a.dismissedBy}</span> : <Pill tone="warn">Open</Pill>) },
          ...(canManage ? [{ key: 'x', label: '', render: (a: Alert) => a.resolvedAt ? null : <button className="btn sm ghost" onClick={() => void api.post(`${base}/${a.id}/${a.dismissed ? 'restore' : 'dismiss'}`).then(() => changed(a.dismissed ? 'Alert restored.' : 'Alert dismissed.')).catch(failed)}>{a.dismissed ? 'Restore' : 'Dismiss'}</button> }] : []),
        ]} />
      </Panel>
    </>
  );
}

// ---- settings --------------------------------------------------------------------------------------------------------

export function SettingsTab({ canManage, changed, failed }: Ctx) {
  const data = useApi(() => api.get<Settings>('/sam/settings'), []);
  return data.data ? <SettingsForm initial={data.data} canManage={canManage} changed={changed} failed={failed} /> : <ErrorBox error={data.error} />;
}

function SettingsForm({ initial, canManage, changed, failed }: { initial: Settings; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }) {
  const [f, setF] = useState({ ...initial, emails: initial.notifyEmails.join('\n') });
  const [busy, setBusy] = useState(false);
  async function save() {
    setBusy(true);
    try {
      await api.put('/sam/settings', { notifyEmails: f.emails.split(/[\n,;]/).map((x) => x.trim()).filter(Boolean), notifyInApp: f.notifyInApp, expiryDays: f.expiryDays,
        unusedPercent: f.unusedPercent, inactiveDays: f.inactiveDays, webDiscovery: f.webDiscovery });
      changed('Settings saved.');
    } catch (e) { failed(e); } finally { setBusy(false); }
  }
  return (
    <div className="grid-2">
      <Panel title="Alerts and Notifications">
        <div className="form-v">
          <label className="row-check"><input type="checkbox" checked={f.notifyInApp} disabled={!canManage} onChange={(e) => setF({ ...f, notifyInApp: e.target.checked })} /> Show New Alerts in the Bell</label>
          <label>E-mail new alerts to (one address per line)
            <textarea className="in" rows={3} value={f.emails} disabled={!canManage} onChange={(e) => setF({ ...f, emails: e.target.value })} placeholder="it-assets@example.com" />
            <small className="faint">Uses the e-mail (SMTP) settings under System Settings. Teams, Slack and ticketing: Integrations &amp; Automation (events Software licence …, Web app risk).</small>
          </label>
          <label>Warn before a licence ends (days, unless the licence sets its own)<input className="in" type="number" min={1} max={365} value={f.expiryDays} disabled={!canManage} onChange={(e) => setF({ ...f, expiryDays: Number(e.target.value) })} /></label>
        </div>
      </Panel>
      <Panel title="Thresholds and Discovery">
        <div className="form-v">
          <label>Over-licensed when more than this share is unused (%)<input className="in" type="number" min={5} max={90} value={f.unusedPercent} disabled={!canManage} onChange={(e) => setF({ ...f, unusedPercent: Number(e.target.value) })} /></label>
          <label>Accounts and cloud licences are inactive after (days)<input className="in" type="number" min={7} max={730} value={f.inactiveDays} disabled={!canManage} onChange={(e) => setF({ ...f, inactiveDays: Number(e.target.value) })} /></label>
          <label className="row-check"><input type="checkbox" checked={f.webDiscovery} disabled={!canManage} onChange={(e) => setF({ ...f, webDiscovery: e.target.checked })} /> Find Web Apps in Use on Devices</label>
          <small className="faint" style={{ marginTop: -6 }}>Windows devices report the host names in their DNS cache with the inventory; only names of known web apps are kept.</small>
          {canManage && <div><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>}
        </div>
      </Panel>
    </div>
  );
}
