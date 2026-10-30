import { useRef, useState } from 'react';
import { api, ApiError } from '../../api/client';
import type { LicensingStatus, WorkloadUsage } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Callout, DataTable, ErrorBox, fmt, Kpis, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';

type Msg = { tone: 'ok' | 'crit'; text: string } | null;

const METRIC_HELP: Record<string, string> = {
  devices: 'Endpoints, servers, network devices (switches, routers, access points, firewalls), printers and other devices',
  nonit_assets: 'Furniture, vehicles, equipment and other non-IT records that are not disposed',
  cloud_instances: 'Virtual machines read from cloud and virtualization connections',
  technicians: 'Active users with the right to start remote support sessions',
};

function Meter({ u, exact }: { u: WorkloadUsage; exact: boolean }) {
  if (u.limit == null) return <span className="muted">Unlimited</span>;
  if (u.limit === 0) return <span className="muted">Not included</span>;
  const pct = Math.min(100, (u.billable / u.limit) * 100);
  return (
    <div className="lic-meter" title={`${Math.round((u.billable / u.limit) * 100)}%`}>
      <div className={`progress${u.billable > u.limit ? ' over' : pct >= 90 ? ' near' : ''}`}><i style={{ width: `${pct}%` }} /></div>
      <small className="muted">{u.billable.toLocaleString()} of {u.limit.toLocaleString()}{!exact && u.billable > u.limit ? ' (within 5% grace)' : ''}</small>
    </div>
  );
}

const MODE_LABEL: Record<string, string> = {
  Trial: 'Trial', Licensed: 'Licensed', LicensedGrace: 'Licensed (grace period)', Free: 'Free tier', FreeOverLimit: 'Free tier – over limit',
  LeaseExpired: 'Check-in overdue', SupportExpired: 'Support expired', TrialExpired: 'Trial ended', LicenseExpired: 'License expired', ClockRollback: 'Clock moved back',
};

function planText(d: LicensingStatus): string {
  if (d.mode.startsWith('Trial')) return 'Trial (all sections)';
  if (d.mode.startsWith('Free')) return 'Free tier (Standard)';
  const ed = d.editionTitle ?? (d.edition === 'advanced' ? 'Advanced' : d.edition === 'standard' ? 'Standard' : 'Custom');
  return d.licenseType === 'perpetual' ? `${ed} · Perpetual` : d.licenseType === 'subscription' ? `${ed} · Subscription` : ed;
}

export function LicensingPage() {
  const { can, refresh } = useAuth();
  const confirm = useConfirm();
  const st = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const [msg, setMsg] = useState<Msg>(null);
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [serverUrl, setServerUrl] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const d = st.data;
  const manage = can('licensing.manage');

  async function run(name: string, fn: () => Promise<string>) {
    setMsg(null); setBusy(name);
    try {
      const text = await fn();
      setMsg({ tone: 'ok', text });
      st.reload();
      await refresh();
    } catch (e) {
      setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) });
    } finally { setBusy(null); }
  }

  const activate = () => run('activate', async () => (await api.post<{ message: string }>('/licensing/activate', { licenseKey: key })).message).then(() => setKey(''));
  const checkin = () => run('checkin', async () => (await api.post<{ message: string }>('/licensing/checkin')).message || 'Checked in.');
  async function deactivate() {
    if (!(await confirm({ title: 'Deactivate this server?', danger: true, confirmLabel: 'Deactivate',
      message: 'The license is released on the license server so the key can be activated on another server. This server falls back to the free tier (25 IT devices, 25 non-IT assets, 5 cloud instances, no remote support) until it is activated again. No data is deleted.' }))) return;
    await run('deactivate', async () => (await api.post<{ message: string }>('/licensing/deactivate')).message);
  }
  const saveServer = () => run('server', async () => { await api.put('/licensing/server', { url: serverUrl ?? '' }); setServerUrl(null); return 'License server address saved.'; });

  async function exportRequest() {
    setMsg(null);
    try {
      await api.download('/licensing/activation-request', 'zentriq-activation-request.json');
      setMsg({ tone: 'ok', text: 'Activation request downloaded. Upload it with your license key on the offline activation page (or send it to Zentriq) to get your license file (.zlic).' });
    } catch (e) {
      setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) });
    }
  }

  async function importLicense(file: File) {
    await run('import', async () => {
      try {
        await api.post('/licensing/import', { document: await file.text() });
        return 'License imported and activated.';
      } catch (e) {
        throw new Error(e instanceof ApiError ? `License rejected: ${e.message}` : String(e));
      } finally {
        if (fileRef.current) fileRef.current.value = '';
      }
    });
  }

  const exact = !!d && (d.mode.startsWith('Trial') || d.mode.startsWith('Free'));
  const metrics = d?.usage.filter((u) => u.metric !== false) ?? [];
  const types = d?.usage.filter((u) => u.metric === false) ?? [];
  const online = d?.online;
  const isOnline = !!online?.active;
  const offlinePage = (online?.serverUrl ?? 'https://license.zentriq.com').replace(/\/$/, '') + '/';

  return (
    <>
      <PageHead title="Licensing" desc="Your plan, what it covers and how much of it is in use. Activate online with a license key, or offline for servers without internet access." />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
      <ErrorBox error={st.error} />
      {!d ? <Loading /> : (
        <>
          {d.warning && <Callout tone="warn" icon="alert">{d.warning}</Callout>}
          {d.mode === 'FreeOverLimit' && <Callout tone="crit" icon="alert">This server uses more than the free tier allows, so licensed features are paused. Activate a license, or reduce the records below the free limits. No data has been deleted.</Callout>}
          {d.revoked && !d.license && <Callout tone="crit" icon="alert">License {d.revoked.licenseId} ({d.revoked.customerName}) is no longer valid on this server (revoked, suspended or deactivated at the license server). Contact Zentriq, or activate another license.</Callout>}
          <Kpis items={[
            { label: 'State', value: <Pill tone={statusTone(d.mode)}>{MODE_LABEL[d.mode] ?? fmt.label(d.mode)}</Pill> },
            { label: 'Plan', value: planText(d) },
            d.licenseType === 'perpetual'
              ? { label: 'Support until', value: fmt.day(d.supportUntil ?? null), sub: 'Versions released until then can be installed' }
              : { label: d.mode.startsWith('Trial') ? 'Trial ends' : d.mode.startsWith('Free') ? 'Ends' : 'Subscription ends', value: d.mode.startsWith('Free') ? 'No end' : fmt.day(d.expiresAt) },
            { label: 'Activation', value: d.activation === 'online' ? 'Online' : d.activation === 'offline' ? 'Offline (file)' : d.license ? 'License file' : '—',
              sub: d.leaseUntil ? `Next check-in needed by ${fmt.day(d.leaseUntil)}` : undefined },
          ]} />

          <div className="grid-2">
            <div className="lic-span"><Panel title="Usage" sub="What the plan counts. IT accessories are always unlimited." flush>
              <DataTable rows={metrics.map((u) => ({ ...u, id: u.workload }))} columns={[
                { key: 'w', label: 'Counted', render: (r) => <><b>{r.title || fmt.label(r.workload)}</b><div className="faint lic-help">{METRIC_HELP[r.workload]}</div></> },
                { key: 'b', label: 'In use', render: (r) => r.billable.toLocaleString() },
                { key: 'l', label: 'Allowed', render: (r) => <Meter u={r} exact={exact} /> },
              ]} />
              <div className="lic-foot muted"><small>
                IT accessories: unlimited.{types.length > 0 && <> IT devices by type: {types.filter((t) => t.billable > 0).map((t) => `${t.title || fmt.label(t.workload)} ${t.billable.toLocaleString()}`).join(' · ') || 'none yet'}.</>}
                {' '}Devices count when active and seen in the last 30 days; virtual machines from cloud connections count as cloud instances instead.
              </small></div>
            </Panel></div>

            <Panel title={isOnline ? 'Online activation' : 'Activate'} sub={isOnline ? `License server ${online?.serverUrl ?? ''}` : 'With the license key from your order'}>
              {isOnline ? (
                <>
                  <dl className="dl2">
                    <dt>License key</dt><dd className="mono">{online?.keyHint ?? '—'}</dd>
                    <dt>License</dt><dd className="mono">{online?.licenseId ?? '—'}</dd>
                    <dt>Activated</dt><dd>{fmt.date(online?.activatedAt ?? null)}{online?.activatedBy ? ` by ${online.activatedBy}` : ''}</dd>
                    <dt>Last check-in</dt><dd>{fmt.date(online?.lastCheckinAt ?? null)} {online?.lastStatus && <Pill tone={online.lastStatus === 'active' ? 'ok' : online.lastStatus === 'error' ? 'warn' : 'crit'}>{online.lastStatus}</Pill>}</dd>
                    {online?.lastMessage && <><dt>Message</dt><dd>{online.lastMessage}</dd></>}
                  </dl>
                  <small className="muted">The server checks in daily: renewals, upgrades and support extensions arrive automatically. Without a check-in the license keeps working until the lease ends, then for 30 more days.</small>
                  {manage && <div className="row-actions lic-actions">
                    <button className="btn" disabled={!!busy} onClick={() => void checkin()}>{busy === 'checkin' ? 'Checking in…' : 'Check in now'}</button>
                    <button className="btn danger" disabled={!!busy} onClick={() => void deactivate()}>Deactivate…</button>
                  </div>}
                </>
              ) : manage ? (
                <>
                  <label className="field"><span>License key</span>
                    <input className="in mono" value={key} placeholder="ZQL-XXXXX-XXXXX-XXXXX-XXXXX" onChange={(e) => setKey(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && key && void activate()} />
                  </label>
                  <div className="row-actions lic-actions">
                    <button className="btn primary" disabled={!key.trim() || !!busy} onClick={() => void activate()}>{busy === 'activate' ? 'Activating…' : 'Activate online'}</button>
                  </div>
                  <small className="muted">This server contacts {online?.serverUrl ?? 'the Zentriq license server'} over HTTPS. Subscriptions (1, 3 or 5 years) and perpetual licenses are both activated here.</small>
                </>
              ) : <p className="muted">Ask an administrator with the licensing right to activate a license.</p>}
            </Panel>

            {manage && (
              <Panel title="Offline activation" sub="For servers without internet access (air-gapped)">
                <ol className="lic-steps">
                  <li>Download the activation request from this server.</li>
                  <li>On a computer with internet access, open <span className="mono">{offlinePage}</span> and upload the request with your license key (or send it to Zentriq).</li>
                  <li>Import the license file (.zlic) you receive.</li>
                </ol>
                <div className="row-actions lic-actions">
                  <button className="btn" onClick={() => void exportRequest()}>Download activation request</button>
                  <button className="btn primary" disabled={!!busy} onClick={() => fileRef.current?.click()}>Import license file…</button>
                  <input ref={fileRef} type="file" accept=".zlic,.json,application/json" hidden onChange={(e) => e.target.files?.[0] && void importLicense(e.target.files[0])} />
                </div>
                <small className="muted">Offline licenses do not check in; import a new file after a renewal or upgrade.</small>
              </Panel>
            )}

            <Panel title="Details">
              <dl className="dl2">
                <dt>Summary</dt><dd>{d.summary}</dd>
                <dt>Installation ID</dt><dd className="mono">{d.installationId ?? '—'}</dd>
                {d.releaseDate && <><dt>This version released</dt><dd>{fmt.day(d.releaseDate)}</dd></>}
                {d.trial && <><dt>Trial</dt><dd>{fmt.day(d.trial.startedAt)} to {fmt.day(d.trial.expiresAt)}</dd></>}
                {d.license && <>
                  <dt>License</dt><dd className="mono">{d.license.licenseId}</dd>
                  <dt>Customer</dt><dd>{d.license.customerName}</dd>
                  {d.licenseType === 'perpetual'
                    ? <><dt>Type</dt><dd>Perpetual; support until {fmt.day(d.supportUntil ?? null)}</dd></>
                    : <><dt>Valid until</dt><dd>{fmt.day(d.license.expiresAt)} (+{d.license.graceDays} days grace)</dd></>}
                </>}
              </dl>
            </Panel>

            {d.plans && (
              <Panel title="Plans">
                <dl className="dl2">
                  <dt>Trial</dt><dd>14 days, all sections: {d.plans.trial.devices} IT devices, {d.plans.trial.nonit_assets} non-IT assets, {d.plans.trial.cloud_instances} cloud/VM instances, {d.plans.trial.technicians} remote support technician; accessories unlimited.</dd>
                  <dt>Free</dt><dd>Standard edition, no end: up to {d.plans.free.devices} IT devices and {d.plans.free.nonit_assets} non-IT assets, {d.plans.free.cloud_instances} cloud/VM instances, no remote support; accessories unlimited.</dd>
                  <dt>Licensed</dt><dd>Per IT device, non-IT packs ({d.plans.nonItPacks.map((p) => p.toLocaleString()).join(', ')} or unlimited), per cloud/VM instance and per remote support technician. Subscription for {d.plans.subscriptionYears.join(', ')} years, or perpetual with yearly support.</dd>
                </dl>
              </Panel>
            )}

            {d.sections && <div className="lic-span"><Panel title="Sections" sub="What is installed on this server and what your plan covers" flush>
              <DataTable rows={d.sections.map((x) => ({ ...x }))} columns={[
                { key: 't', label: 'Section', render: (r) => <><b>{r.title}</b> <span className="faint lic-help">{r.standard ? 'Standard' : 'Advanced'}</span></> },
                { key: 'i', label: 'Installed', render: (r) => r.installed ? <Pill tone="ok">Installed</Pill> : <Pill>Not installed</Pill> },
                { key: 'l', label: 'Available', render: (r) => !r.installed ? <span className="muted">—</span> : r.enabled ? <Pill tone="ok">Yes</Pill> : d.featuresEnabled ? <Pill tone="warn">Needs Advanced</Pill> : <Pill tone="crit">License required</Pill> },
              ]} />
              <div className="lic-foot muted">
                <small><b>Standard</b>: Overview, Discover &amp; inventory, Insight &amp; admin. <b>Advanced</b>: Standard plus Remote support, Infrastructure, Secure &amp; remediate. The trial includes every section. To add a section that is not installed, run Zentriq Setup on the server and choose <i>Update › Advanced installation</i>.</small>
              </div>
            </Panel></div>}

            {d.licensee && (
              <Panel title="Licensed to">
                <dl className="dl2">
                  <dt>Company</dt><dd><b>{d.licensee.company}</b></dd>
                  {d.licensee.contactName && <><dt>Contact</dt><dd>{d.licensee.contactName}</dd></>}
                  {d.licensee.contactEmail && <><dt>Email</dt><dd>{d.licensee.contactEmail}</dd></>}
                  {d.licensee.contactPhone && <><dt>Phone</dt><dd>{d.licensee.contactPhone}</dd></>}
                  {d.licensee.address && <><dt>Address</dt><dd>{d.licensee.address}{d.licensee.country ? `, ${d.licensee.country}` : ''}</dd></>}
                  {d.licensee.taxId && <><dt>Tax ID</dt><dd className="mono">{d.licensee.taxId}</dd></>}
                </dl>
                <small className="muted">From the signed license. Edit your company profile in System Settings.</small>
              </Panel>
            )}

            {manage && (
              <Panel title="License server" sub="Only change this when Zentriq asks you to">
                <label className="field"><span>Address</span>
                  <input className="in" value={serverUrl ?? online?.serverUrl ?? ''} onChange={(e) => setServerUrl(e.target.value)} placeholder="https://license.zentriq.com" />
                </label>
                <div className="row-actions">
                  <button className="btn" disabled={serverUrl == null || !!busy} onClick={() => void saveServer()}>Save</button>
                </div>
                <small className="muted">Outbound HTTPS (port 443) from this server to the license server is needed for online activation and check-ins. The server's proxy settings apply.</small>
              </Panel>
            )}
          </div>
        </>
      )}
    </>
  );
}
