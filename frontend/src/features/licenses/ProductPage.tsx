import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Panel, Pill } from '../../components/ui';
import { errText } from '../deployment/types';
import { LicenseDialog, ProductDialog } from './LicenseDialogs';
import { DaysPill, StatusPill } from './LicensesPage';
import { consumptionLabel, METRIC_LABEL, money, SEVERITY_TONE, TYPE_LABEL, type Installation, type License, type ProductDetail } from './types';

export function ProductPage({ id }: { id: string }) {
  const { can } = useAuth();
  const { navigate } = useRouter();
  const confirm = useConfirm();
  const canManage = can('licenses.manage');
  const [tick, setTick] = useState(0);
  const [edit, setEdit] = useState(false);
  const [addLicense, setAddLicense] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const data = useApi(() => api.get<ProductDetail>(`/sam/products/${id}`), [id, tick]);
  const inst = useApi(() => api.get<Installation[]>(`/sam/products/${id}/installations`), [id, tick]);
  const c = data.data?.compliance;
  const d = data.data?.detail;
  async function remove() {
    if (!d || !await confirm({ title: `Delete ${d.product.name}?`, message: 'The product and all its licences and allocations are removed.', danger: true, confirmLabel: 'Delete' })) return;
    try { await api.del(`/sam/products/${id}`); navigate('/modules/licenses?tab=products'); } catch (e) { setMsg(errText(e)); }
  }
  if (!c || !d) return <><PageHead title="Software Product" icon="key" /><ErrorBox error={data.error} /></>;
  const p = d.product;
  const devices = (inst.data ?? []).map((i) => ({ ...i, id: i.assetId }));
  return (
    <>
      <PageHead title={p.name} icon="key" actions={canManage && <>
        <button className="btn" onClick={() => setEdit(true)}><Icon name="edit" size={14} /> Edit Product</button>
        <button className="btn danger" onClick={() => void remove()}><Icon name="trash" size={14} /> Delete</button>
        <button className="btn primary" onClick={() => setAddLicense(true)}><Icon name="plus" size={14} /> Add Licence</button>
      </>} />
      <Kpis items={[
        { label: 'Position', value: <StatusPill c={c} />, sub: c.shortfall > 0 ? `${c.shortfall} licence(s) short${c.shortfallCost ? ` · ${money(c.shortfallCost)}` : ''}` : c.unused > 0 ? `${c.unused} spare${c.unusedValue ? ` · ${money(c.unusedValue)}` : ''}` : '—' },
        { label: 'In Use', value: c.consumed == null ? '—' : c.consumed.toLocaleString(), sub: consumptionLabel(c) + (c.kind === 'installed' ? ` · ${c.devices} devices, ${c.users} users` : '') },
        { label: 'Owned', value: c.unlimited ? 'Unlimited' : c.entitled.toLocaleString(), sub: `${c.validLicenses} valid licence(s) · ${METRIC_LABEL[c.metric].toLowerCase()}` },
        { label: 'Next End', value: c.nextEnd ? fmt.day(c.nextEnd) : '—', sub: c.expiring > 0 ? `${c.expiring} ending soon` : 'Subscriptions and terms' },
        { label: 'Recorded Cost', value: money(c.cost), sub: c.unitCost ? `${money(c.unitCost)} per licence on average` : '—' },
      ]} />
      {msg && <Callout tone="crit" icon="alert">{msg}</Callout>}
      {d.alerts.length > 0 && (
        <Panel flush title="Open Alerts">
          <DataTable rows={d.alerts} columns={[
            { key: 's', label: 'Severity', render: (a) => <Pill tone={SEVERITY_TONE[a.severity]}>{fmt.label(a.severity)}</Pill> },
            { key: 't', label: 'Alert', className: 'wrap', render: (a) => <><b>{a.title}</b>{a.detail && <div className="faint" style={{ fontSize: 12.5 }}>{a.detail}</div>}</> },
            { key: 'f', label: 'Since', render: (a) => fmt.ago(a.firstAt) },
          ]} />
        </Panel>
      )}
      <Panel flush title="Licences" sub={`${d.licenses.length} recorded`}>
        <DataTable rows={d.licenses} onRow={(l: License) => navigate(`/modules/licenses/licenses/${l.id}`)} rowTitle="Open the licence" empty="No licences recorded for this product." columns={[
          { key: 'n', label: 'Licence', className: 'wrap', render: (l) => <b>{l.name}</b> },
          { key: 't', label: 'Type', render: (l) => TYPE_LABEL[l.licenseType] },
          { key: 'q', label: 'Quantity', render: (l) => <>{l.quantity ?? l.cloudQuantity ?? '∞'} <span className="faint">{METRIC_LABEL[l.metric].toLowerCase()}</span></> },
          { key: 'e', label: 'Ends', render: (l) => <DaysPill days={l.daysLeft} date={l.endDate} autoRenew={l.autoRenew} /> },
          { key: 'c', label: 'Cost', render: (l) => money(l.totalCost, l.currency) },
          { key: 'a', label: 'Allocated', render: (l) => l.allocations },
          { key: 's', label: 'Status', render: (l) => <Pill tone={l.status === 'expired' ? 'crit' : l.status === 'expiring' ? 'warn' : l.status === 'retired' ? '' : 'ok'}>{fmt.label(l.status)}</Pill> },
        ]} />
      </Panel>
      <div className="grid-2">
        <Panel title="How Installations Are Found">
          <dl className="kv">
            <dt>Kind</dt><dd>{p.kind === 'cloud' ? 'Cloud subscription' : 'Installed software'}{!p.needsLicense && ' · no licence needed'}</dd>
            {p.kind === 'installed' && <><dt>Installed as</dt><dd className="mono">{p.namePatterns.join(' · ') || '—'}</dd>
              {p.excludePatterns.length > 0 && <><dt>But not</dt><dd className="mono">{p.excludePatterns.join(' · ')}</dd></>}
              {p.publisherPatterns.length > 0 && <><dt>Publisher</dt><dd className="mono">{p.publisherPatterns.join(' · ')}</dd></>}</>}
            {p.cloudSkus.length > 0 && <><dt>Microsoft 365</dt><dd className="mono">{p.cloudSkus.join(' · ')}</dd></>}
            {p.webAppId && <><dt>Web app</dt><dd><Link to={`/modules/webapps/${p.webAppId}`}>{p.webAppName}</Link></dd></>}
            {p.notes && <><dt>Notes</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{p.notes}</dd></>}
          </dl>
        </Panel>
        <Panel title={p.kind === 'cloud' ? 'Microsoft 365' : 'Versions'}>
          {p.kind === 'cloud' ? (
            <DataTable rows={d.cloud.map((k) => ({ ...k, id: `${k.connectionId}:${k.skuId}` }))} empty="No Microsoft 365 licence matches. Check the SKU part numbers." columns={[
              { key: 'n', label: 'Licence', render: (k) => <>{k.name ?? k.skuPart}<div className="faint mono" style={{ fontSize: 11.5 }}>{k.connection}</div></> },
              { key: 'u', label: 'Assigned · Bought', render: (k) => `${k.consumed} · ${k.enabled + k.warning}` },
              { key: 'e', label: 'Renews / Ends', render: (k) => (k.nextLifecycle ? fmt.day(k.nextLifecycle) : '—') },
              { key: 's', label: 'Status', render: (k) => k.status ?? '—' },
            ]} />
          ) : (
            <DataTable rows={d.versions.map((v) => ({ ...v, id: v.version }))} empty="Not installed on any device." columns={[
              { key: 'v', label: 'Version', render: (v) => <span className="mono">{v.version}</span> },
              { key: 'd', label: 'Devices', render: (v) => v.devices },
            ]} />
          )}
        </Panel>
      </div>
      {p.kind === 'installed' && (
        <Panel flush title="Installations" sub={`${devices.length.toLocaleString()} device(s)`}>
          <DataTable rows={devices} empty={inst.loading ? 'Loading…' : 'Not installed on any device.'} columns={[
            { key: 'h', label: 'Device', render: (i) => <Link to={`/assets/${i.assetId}?tab=software`}><b>{i.hostname}</b></Link> },
            { key: 's', label: 'Installed As', className: 'wrap', render: (i) => <>{i.software}{i.version && <span className="faint mono"> {i.version}</span>}</> },
            { key: 'u', label: 'User', render: (i) => i.user ?? '—' },
            { key: 'd', label: 'Department', render: (i) => i.department ?? '—' },
            { key: 'c', label: 'Cores', render: (i) => i.cores ?? '—' },
            { key: 'a', label: 'Licence', render: (i) => i.allocatedTo ?? <span className="faint">not allocated</span> },
            { key: 'l', label: 'Last Seen', render: (i) => fmt.ago(i.lastSeen) },
          ]} />
        </Panel>
      )}
      {edit && <ProductDialog product={p} onClose={() => setEdit(false)} onSaved={() => { setEdit(false); setTick((t) => t + 1); }} />}
      {addLicense && <LicenseDialog license={null} productId={p.id} products={[{ id: p.id, name: p.name }]} onClose={() => setAddLicense(false)} onSaved={() => { setAddLicense(false); setTick((t) => t + 1); }} />}
    </>
  );
}
