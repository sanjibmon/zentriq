import { useRef, useState, type ReactNode } from 'react';
import { api, ApiError } from '../../api/client';
import type { LicensingStatus, OrganizationResponse, WorkloadUsage } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';
import { HelpSteps, Step, UiBtn, UiPath, Val } from '../../components/Help';
import { Icon } from '../../components/Icon';

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


const DAY = 86_400_000;

/** Days left of a period, and how much of it is left (0-1). */
function remaining(from: string | null | undefined, to: string | null | undefined): { days: number; part: number } | null {
  if (!to) return null;
  const end = new Date(to).getTime();
  const start = from ? new Date(from).getTime() : end - 365 * DAY;
  const now = Date.now();
  const days = Math.max(0, Math.ceil((end - now) / DAY));
  const part = end > start ? Math.min(1, Math.max(0, (end - now) / (end - start))) : 0;
  return { days, part };
}

function Ring({ part, tone, children }: { part: number; tone: string; children: ReactNode }) {
  const r = 52, c = 2 * Math.PI * r;
  return (
    <div className={`lic-ring ${tone}`}>
      <svg viewBox="0 0 120 120" aria-hidden="true">
        <circle cx="60" cy="60" r={r} className="track" />
        <circle cx="60" cy="60" r={r} className="bar" strokeDasharray={`${c * part} ${c}`} transform="rotate(-90 60 60)" />
      </svg>
      <div className="lic-ring-c">{children}</div>
    </div>
  );
}

/** The license at a glance: who it is for, edition and state, what it covers, and how long it runs. */
function LicenseHero({ d, org }: { d: LicensingStatus; org: string | null }) {
  const trial = d.mode.startsWith('Trial');
  const free = d.mode.startsWith('Free');
  const perpetual = d.licenseType === 'perpetual';
  const who = d.licensee?.company ?? d.license?.customerName ?? org ?? 'This organization';
  const period = trial ? remaining(d.trial?.startedAt, d.trial?.expiresAt ?? d.expiresAt)
    : perpetual ? remaining(d.license?.issuedAt, d.supportUntil)
    : free ? null : remaining(d.license?.issuedAt, d.expiresAt);
  const tone = statusTone(d.mode);
  const ringTone = !period ? 'ok' : period.days <= 14 ? 'crit' : period.days <= 45 ? 'warn' : 'ok';
  const edition = trial ? 'Trial' : free ? 'Free tier' : `${d.editionTitle ?? 'Custom'} edition`;
  const contact = [d.licensee?.contactName, d.licensee?.contactEmail, d.licensee?.contactPhone].filter(Boolean).join(' · ');
  const facts: [string, ReactNode][] = [
    [trial ? 'Trial ends' : perpetual ? 'License' : free ? 'Runs' : 'Valid until', trial ? fmt.day(d.trial?.expiresAt ?? d.expiresAt) : perpetual ? 'Perpetual' : free ? 'No end' : fmt.day(d.expiresAt)],
    ...(perpetual ? [['Support until', fmt.day(d.supportUntil ?? null)] as [string, ReactNode]] : []),
    ['Type', trial ? '14-day evaluation' : free ? 'Free tier' : perpetual ? 'Perpetual + yearly support' : d.licenseType === 'subscription' ? 'Subscription' : 'License file'],
    ['Activation', d.activation === 'online' ? 'Online (daily check-in)' : d.activation === 'offline' ? 'Offline (file)' : d.license ? 'License file' : '—'],
    ...(d.license ? [['License ID', <span className="mono">{d.license.licenseId}</span>] as [string, ReactNode]] : []),
    ...(d.installationId ? [['Installation ID', <span className="mono">{d.installationId}</span>] as [string, ReactNode]] : []),
  ];
  return (
    <section className={`lic-hero tone-${tone}`} aria-label="License">
      <div className="lic-hero-main">
        <div className="lic-hero-eyebrow">{trial ? 'Evaluation for' : free ? 'Free tier for' : 'Licensed to'}</div>
        <h2 className="lic-hero-name">{who}</h2>
        {contact && <div className="lic-hero-contact">{contact}</div>}
        {d.licensee?.address && <div className="lic-hero-contact">{d.licensee.address}{d.licensee.country ? `, ${d.licensee.country}` : ''}</div>}
        <div className="lic-hero-badges">
          <span className="lic-edition"><Icon name="key" size={14} /> {edition}</span>
          <Pill tone={tone}>{MODE_LABEL[d.mode] ?? fmt.label(d.mode)}</Pill>
        </div>
        {d.sections && (
          <div className="lic-hero-sections" aria-label="Sections">
            {d.sections.map((x) => {
              const on = x.installed && x.enabled;
              return (
                <span key={x.id} className={`lic-sec${on ? ' on' : ''}`} title={!x.installed ? 'Not installed on this server' : x.enabled ? 'Included' : x.offer === 'addon' ? 'Add-on, not included' : 'Needs the Advanced edition'}>
                  <Icon name={on ? 'check' : 'lock'} size={12} /> {x.title}{x.offer === 'addon' && <em>add-on</em>}
                </span>
              );
            })}
          </div>
        )}
      </div>
      <div className="lic-hero-side">
        {period ? (
          <Ring part={period.part} tone={ringTone}><b>{period.days.toLocaleString()}</b><small>{period.days === 1 ? 'day left' : 'days left'}{perpetual ? ' of support' : ''}</small></Ring>
        ) : (
          <Ring part={1} tone="ok"><b>∞</b><small>{free ? 'no end' : 'perpetual'}</small></Ring>
        )}
        <dl className="lic-hero-facts">
          {facts.map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v}</dd></div>)}
        </dl>
      </div>
    </section>
  );
}

export function LicensingPage() {
  const { can, refresh } = useAuth();
  const confirm = useConfirm();
  const st = useApi(() => api.get<LicensingStatus>('/licensing/status'), []);
  const [msg, setMsg] = useState<Msg>(null);
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const org = useApi(() => api.get<OrganizationResponse>('/organization').catch(() => null), []);
  const fileRef = useRef<HTMLInputElement>(null);
  const offRef = useRef<HTMLInputElement>(null);
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

  async function exportRequest() {
    setMsg(null);
    try {
      await api.download('/licensing/activation-request', 'zentriq-activation-request.json');
      setMsg({ tone: 'ok', text: 'Activation request downloaded. Upload it with your license key on the offline activation page (or send it to Zentriq) to get your license file (.zlic).' });
    } catch (e) {
      setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) });
    }
  }

  async function importLicense(file: File, ref = fileRef) {
    await run('import', async () => {
      try {
        // A license file from Zentriq is validated by the Zentriq license server first; a file for this server
        // (offline activation, renewal) is imported directly.
        const r = await api.post<{ message?: string; validated?: boolean }>('/licensing/import', { document: await file.text() });
        return r.message ?? 'License imported and activated.';
      } catch (e) {
        if (e instanceof ApiError && (e.body as { reason?: string } | null)?.reason === 'LicenseServerUnreachable')
          throw new Error(`${e.message} For a server without internet access, use Offline activation below with the same license file.`);
        throw new Error(e instanceof ApiError ? `License not activated: ${e.message}` : String(e));
      } finally {
        if (ref.current) ref.current.value = '';
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
      <PageHead title="Licensing" />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
      <ErrorBox error={st.error} />
      {!d ? <Loading /> : (
        <>
          {d.warning && <Callout tone="warn" icon="alert">{d.warning}</Callout>}
          {d.mode === 'FreeOverLimit' && <Callout tone="crit" icon="alert">This server uses more than the free tier allows, so licensed features are paused. Activate a license, or reduce the records below the free limits. No data has been deleted.</Callout>}
          {d.revoked && !d.license && <Callout tone="crit" icon="alert">License {d.revoked.licenseId} ({d.revoked.customerName}) is no longer valid on this server (revoked, suspended or deactivated at the license server). Contact Zentriq, or activate another license.</Callout>}
          <LicenseHero d={d} org={org.data?.profile?.name ?? null} />

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

            <Panel title={isOnline ? 'Activation' : 'Activate'} sub={isOnline ? `Validated by the license server ${online?.serverUrl ?? ''}` : 'With the license file you received from Zentriq'}>
              {isOnline ? (
                <>
                  <dl className="dl2">
                    <dt>Activated with</dt><dd className="mono">{online?.keyHint ?? '—'}</dd>
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
                  <HelpSteps>
                    <Step title="Import the license file">Choose <UiBtn>Import license file</UiBtn> and select the <Val>.zlic</Val> file from Zentriq.</Step>
                    <Step title="Validation">Zentriq checks its signature and asks the Zentriq license server to validate it (license active, not revoked, an activation free).</Step>
                    <Step title="Activated">The license is activated for this server and refreshed by a daily check-in.</Step>
                  </HelpSteps>
                  <div className="row-actions lic-actions">
                    <button className="btn primary" disabled={!!busy} onClick={() => fileRef.current?.click()}>{busy === 'import' ? 'Validating…' : 'Import license file…'}</button>
                    <input ref={fileRef} type="file" accept=".zlic,.json,application/json" hidden onChange={(e) => e.target.files?.[0] && void importLicense(e.target.files[0])} />
                  </div>
                  <details className="lic-key">
                    <summary className="muted">Activate with a license key instead</summary>
                    <label className="field"><span>License key</span>
                      <input className="in mono" value={key} placeholder="ZQL-XXXXX-XXXXX-XXXXX-XXXXX" onChange={(e) => setKey(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && key && void activate()} />
                    </label>
                    <button className="btn" disabled={!key.trim() || !!busy} onClick={() => void activate()}>{busy === 'activate' ? 'Activating…' : 'Activate with key'}</button>
                  </details>
                  <small className="muted">This server contacts {online?.serverUrl ?? 'the Zentriq license server'} over HTTPS (port 443). Without internet access, use Offline activation.</small>
                </>
              ) : <p className="muted">Ask an administrator with the licensing right to activate a license.</p>}
            </Panel>

            {manage && (
              <Panel title="Offline activation" sub="For servers without internet access (air-gapped)">
                <HelpSteps>
                  <Step title="Download the activation request">From this server.</Step>
                  <Step title="Activate on a computer with internet access">Open <Val>{offlinePage}</Val> and upload the request with the license file from Zentriq (or send both to Zentriq).</Step>
                  <Step title="Import the license file you receive back">It is made for this server.</Step>
                </HelpSteps>
                <div className="row-actions lic-actions">
                  <button className="btn" onClick={() => void exportRequest()}>Download activation request</button>
                  <button className="btn primary" disabled={!!busy} onClick={() => offRef.current?.click()}>Import license file…</button>
                  <input ref={offRef} type="file" accept=".zlic,.json,application/json" hidden onChange={(e) => e.target.files?.[0] && void importLicense(e.target.files[0], offRef)} />
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


            {d.sections && <div className="lic-span"><Panel title="Sections" sub="What is installed on this server and what your plan covers" flush>
              <DataTable rows={d.sections.map((x) => ({ ...x }))} columns={[
                { key: 't', label: 'Section', render: (r) => <b>{r.title}</b> },
                { key: 'o', label: 'Licensed with', render: (r) => r.offer === 'addon' ? <Pill tone="info">Add-on</Pill> : r.offer === 'standard' || r.standard ? <span className="muted">Standard and Advanced</span> : <span className="muted">Advanced</span> },
                { key: 'i', label: 'Installed', render: (r) => r.installed ? <Pill tone="ok">Installed</Pill> : <Pill>Not installed</Pill> },
                { key: 'l', label: 'Available', render: (r) => !r.installed ? <span className="muted">—</span> : r.enabled ? <Pill tone="ok">Yes</Pill> : d.featuresEnabled ? <Pill tone="warn">{r.offer === 'addon' ? 'Needs the add-on' : 'Needs Advanced'}</Pill> : <Pill tone="crit">License required</Pill> },
              ]} />
              <div className="lic-foot muted">
                <small><b>Standard</b> Overview, Discovery &amp; Inventory, Security Pack, Insight &amp; Admin. <b>Advanced</b> Standard plus Infrastructure and Secure &amp; Remediate. <b>Remote Support</b> is an add-on for either edition. The trial includes every section. To add a section that is not installed, run Zentriq Setup on the server and choose <UiPath>Update › Advanced installation</UiPath>.</small>
              </div>
            </Panel></div>}


          </div>
        </>
      )}
    </>
  );
}
