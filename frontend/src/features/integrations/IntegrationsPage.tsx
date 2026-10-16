import { useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { Link, useRouter } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { CopyBox } from '../../components/CopyBox';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill, type Tone } from '../../components/ui';

// ---- types -------------------------------------------------------------------------------------------

interface Catalog { eventTypes: { type: string; name: string; description: string; perDevice: boolean }[]; severities: string[]; channelKinds: string[]; tokenPermissions: string[] }
interface ChannelSettings {
  url?: string | null; headerName?: string | null; recipients?: string[]; host?: string | null; port?: number | null; protocol?: string | null; format?: string | null;
  instanceUrl?: string | null; username?: string | null; table?: string | null; assignmentGroup?: string | null; category?: string | null; projectKey?: string | null;
  issueType?: string | null; auth?: string | null; labels?: string[]; secretHint?: string | null;
}
interface ChannelSecrets { url?: string; signingSecret?: string; headerValue?: string; password?: string }
interface Channel {
  id: string; kind: string; name: string; enabled: boolean; settings: ChannelSettings; secretsSet: Record<'url' | 'signingSecret' | 'headerValue' | 'password', boolean>;
  lastOkAt: string | null; lastErrorAt: string | null; lastError: string | null; updatedAt: string; rules: number; sent7d: number; failed7d: number;
}
interface Rule { id: string; name: string; enabled: boolean; eventType: string; minSeverity: string; conditions: { categories: string[]; criticality: string[] }; channelIds: string[]; throttleMinutes: number; lastFiredAt: string | null; sent7d: number }
interface Delivery {
  id: number; status: string; attempts: number; response: string | null; error: string | null; createdAt: string; sentAt: string | null; nextAttemptAt: string;
  eventId: number; eventType: string; severity: string; title: string; channelId: string | null; channel: string | null; kind: string | null; rule: string | null; hostname: string | null;
}
interface EventRow { id: number; type: string; severity: string; title: string; body: string; occurredAt: string; link: string | null; assetId: string | null; hostname: string | null; sent: number; deliveries: number }
interface Token { id: string; name: string; prefix: string; permissions: string[]; createdBy: string; createdAt: string; expiresAt: string | null; lastUsedAt: string | null; lastUsedIp: string | null; revokedAt: string | null }
interface IntOverview { channels: number; channelsEnabled: number; rules: number; rulesEnabled: number; sent24h: number; failed24h: number; queued: number; events24h: number; tokens: number }

const KIND: Record<string, { name: string; desc: string }> = {
  webhook: { name: 'Webhook', desc: 'Signed JSON POST to any URL (automation, SOAR, custom apps)' },
  slack: { name: 'Slack', desc: 'Message to a channel via an incoming webhook' },
  teams: { name: 'Microsoft Teams', desc: 'Adaptive Card via a Teams Workflows webhook' },
  email: { name: 'E-mail', desc: 'Message to a list of addresses (uses the SMTP settings)' },
  syslog: { name: 'Syslog / SIEM', desc: 'CEF or JSON over UDP/TCP (Splunk, Sentinel, QRadar, ArcSight…)' },
  servicenow: { name: 'ServiceNow', desc: 'Creates an incident (or a record in another table)' },
  jira: { name: 'Jira', desc: 'Creates an issue in a project (Cloud or Data Center)' },
};
const SEV_TONE: Record<string, Tone> = { critical: 'crit', high: 'crit', medium: 'warn', low: 'info', info: '' };
const DELIVERY_TONE: Record<string, Tone> = { sent: 'ok', failed: 'crit', queued: 'info', suppressed: '' };
const PERM_LABEL: Record<string, string> = {
  'assets.view': 'Assets & inventory', 'vuln.view': 'Vulnerabilities', 'security.view': 'Security configuration', 'reports.export': 'Report data (CSV / JSON)', 'audit.view': 'Audit log', 'jobs.view': 'Jobs',
};
const cap = (s: string) => (s ? s[0]!.toUpperCase() + s.slice(1) : s);
const err = (e: unknown) => (e instanceof Error ? e.message : String(e));

type Tab = 'overview' | 'channels' | 'rules' | 'log' | 'api';
const TABS: [Tab, string][] = [['overview', 'Overview'], ['channels', 'Channels'], ['rules', 'Alert rules'], ['log', 'Delivery log'], ['api', 'API access']];

export function IntegrationsPage() {
  const { query, navigate } = useRouter();
  const tab = (TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'overview') as Tab;
  const catalog = useApi(() => api.get<Catalog>('/integrations/catalog'), []);
  return (
    <>
      <PageHead title="Integrations & Automation" desc="Send Zentriq events to chat, e-mail, your SIEM and ticketing system, and give scripts and BI tools read-only API access." />
      <div className="tabs" role="tablist">
        {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/integrations?tab=${t}`, true)}>{l}</button>)}
      </div>
      <ErrorBox error={catalog.error} />
      {catalog.data && <>
        {tab === 'overview' && <Overview catalog={catalog.data} />}
        {tab === 'channels' && <Channels />}
        {tab === 'rules' && <Rules catalog={catalog.data} />}
        {tab === 'log' && <DeliveryLog />}
        {tab === 'api' && <ApiAccess catalog={catalog.data} />}
      </>}
    </>
  );
}

// ---- Overview ------------------------------------------------------------------------------------------

function Overview({ catalog }: { catalog: Catalog }) {
  const o = useApi(() => api.get<IntOverview>('/integrations/overview'), []);
  const events = useApi(() => api.get<Paged<EventRow>>('/integrations/events', { pageSize: 15 }), []);
  const settings = useApi(() => api.get<{ offlineHours: number; consoleUrl: string | null }>('/integrations/settings'), []);
  const [hours, setHours] = useState<string>('');
  const [saved, setSaved] = useState<string | null>(null);
  const k = o.data;
  async function saveHours(e: FormEvent) {
    e.preventDefault();
    try { await api.put('/integrations/settings', { offlineHours: Number(hours) }); setSaved('Saved.'); settings.reload(); } catch (x) { setSaved(err(x)); }
  }
  return (
    <>
      {k && k.channels === 0 && <Callout tone="info" icon="info">Start by adding a <Link to="/integrations?tab=channels">channel</Link> (for example a Teams or Slack webhook, your SIEM's syslog collector or ServiceNow), then an <Link to="/integrations?tab=rules">alert rule</Link> that says which events go there.</Callout>}
      {settings.data && !settings.data.consoleUrl && <Callout tone="warn" icon="info">Messages contain no link back to Zentriq until the console address is set in System Settings › E-mail (Console URL).</Callout>}
      {k && <Kpis items={[
        { label: 'Channels', value: `${k.channelsEnabled} / ${k.channels}`, sub: 'on / total', to: '/integrations?tab=channels' },
        { label: 'Alert rules', value: `${k.rulesEnabled} / ${k.rules}`, sub: 'on / total', to: '/integrations?tab=rules' },
        { label: 'Sent (24 h)', value: k.sent24h.toLocaleString(), sub: `${k.queued} waiting`, to: '/integrations?tab=log' },
        { label: 'Failed (24 h)', value: <span style={{ color: k.failed24h ? 'var(--crit)' : undefined }}>{k.failed24h.toLocaleString()}</span>, sub: 'after retries', to: '/integrations?tab=log' },
        { label: 'Events (24 h)', value: k.events24h.toLocaleString(), sub: 'found by the detectors' },
        { label: 'API tokens', value: k.tokens.toLocaleString(), sub: 'active, read-only', to: '/integrations?tab=api' },
      ]} />}
      <div className="grid-2">
        <Panel title="Events Zentriq can send" flush>
          <DataTable rows={catalog.eventTypes.map((e) => ({ ...e, id: e.type }))} columns={[
            { key: 'n', label: 'Event', className: 'wrap', render: (e) => <><b>{e.name}</b> <span className="mono faint" style={{ fontSize: 11 }}>{e.type}</span><div className="faint" style={{ fontSize: 12 }}>{e.description}</div></> },
          ]} />
        </Panel>
        <div>
          <Panel title="Detection settings">
            <form className="form-v" onSubmit={saveHours}>
              <label>Hours without contact before "Device not seen" / "Agent not checking in"
                <div style={{ display: 'flex', gap: 8 }}>
                  <input className="in" type="number" min={1} max={2160} value={hours || String(settings.data?.offlineHours ?? 24)} onChange={(e) => setHours(e.target.value)} style={{ width: 120 }} />
                  <button className="btn" type="submit">Save</button>
                </div>
                {saved && <small className="muted">{saved}</small>}
              </label>
              <small className="muted">Detectors run every minute. Per-device events group what was found in one run (for example all new vulnerabilities of a device), so a large evaluation sends one message per device rather than one per finding.</small>
            </form>
          </Panel>
          <Panel title="Recent events" flush>
            <ErrorBox error={events.error} />
            <DataTable rows={events.data?.items ?? []} empty="No events yet." columns={[
              { key: 't', label: 'Event', className: 'wrap', render: (e) => <span title={e.body}>{e.title}<div className="faint" style={{ fontSize: 11 }}>{e.type} · {fmt.ago(e.occurredAt)}</div></span> },
              { key: 's', label: 'Severity', render: (e) => <Pill tone={SEV_TONE[e.severity]}>{e.severity}</Pill> },
              { key: 'd', label: 'Sent', render: (e) => e.deliveries ? `${e.sent} / ${e.deliveries}` : <span className="faint">no rule</span> },
            ]} />
          </Panel>
        </div>
      </div>
    </>
  );
}

// ---- Channels -------------------------------------------------------------------------------------------

function Channels() {
  const confirm = useConfirm();
  const list = useApi(() => api.get<Channel[]>('/integrations/channels'), []);
  const [edit, setEdit] = useState<Channel | 'new' | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: ReactNode } | null>(null);
  const [testing, setTesting] = useState<string | null>(null);
  async function test(c: Channel) {
    setTesting(c.id); setMsg(null);
    try {
      const r = await api.post<{ ok: boolean; response: string | null; error: string | null }>(`/integrations/channels/${c.id}/test`);
      setMsg(r.ok ? { tone: 'ok', text: <>Test message sent to <b>{c.name}</b>{r.response ? ` (${r.response})` : ''}.</> } : { tone: 'crit', text: <>Test to <b>{c.name}</b> failed: {r.error}</> });
      list.reload();
    } catch (x) { setMsg({ tone: 'crit', text: err(x) }); } finally { setTesting(null); }
  }
  async function remove(c: Channel) {
    if (!(await confirm({ title: `Delete the channel "${c.name}"?`, message: c.rules ? `It is used by ${c.rules} alert rule(s); they stop sending to it.` : 'Its delivery history is removed too.', danger: true }))) return;
    await api.del(`/integrations/channels/${c.id}`);
    list.reload();
  }
  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <Panel flush title="Channels" sub="Where notifications go" right={<button className="btn sm primary" onClick={() => setEdit('new')}><Icon name="plus" size={12} /> Add channel</button>}>
        <ErrorBox error={list.error} />
        <DataTable rows={list.data ?? []} empty="No channels yet." columns={[
          { key: 'n', label: 'Name', className: 'wrap', render: (c) => <><b>{c.name}</b><div className="faint" style={{ fontSize: 11.5 }}>{KIND[c.kind]?.name ?? c.kind} · {describe(c)}</div></> },
          { key: 's', label: 'Status', className: 'wrap', render: (c) => !c.enabled ? <Pill>Off</Pill>
            : c.lastErrorAt && (!c.lastOkAt || c.lastErrorAt > c.lastOkAt) ? <span title={c.lastError ?? ''}><Pill tone="crit">Failing</Pill><div className="faint clamp-2" style={{ fontSize: 11, maxWidth: 260 }}>{c.lastError}</div></span>
            : c.lastOkAt ? <Pill tone="ok">OK · {fmt.ago(c.lastOkAt)}</Pill> : <Pill tone="info">Not used yet</Pill> },
          { key: 'r', label: 'Rules', render: (c) => c.rules },
          { key: 'd', label: 'Sent / failed (7 d)', render: (c) => <>{c.sent7d}{c.failed7d > 0 && <span style={{ color: 'var(--crit)' }}> / {c.failed7d}</span>}</> },
          { key: 'x', label: '', render: (c) => <div style={{ display: 'flex', gap: 6 }}>
            <button className="btn sm" disabled={testing === c.id || !c.enabled} onClick={() => void test(c)}>{testing === c.id ? 'Sending…' : 'Send test'}</button>
            <button className="btn sm" onClick={() => setEdit(c)}>Edit</button>
            <button className="btn sm" onClick={() => void remove(c)}>Delete</button>
          </div> },
        ]} />
      </Panel>
      {edit && <ChannelDialog channel={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); list.reload(); }} />}
    </>
  );
}

function describe(c: Channel): string {
  const s = c.settings;
  switch (c.kind) {
    case 'webhook': return s.url ?? '';
    case 'slack': case 'teams': return s.secretHint ?? 'webhook URL set';
    case 'email': return (s.recipients ?? []).join(', ');
    case 'syslog': return `${(s.protocol ?? 'udp').toUpperCase()} ${s.host}:${s.port ?? 514} · ${(s.format ?? 'cef').toUpperCase()}`;
    case 'servicenow': return `${s.instanceUrl} · ${s.table || 'incident'}`;
    case 'jira': return `${s.instanceUrl} · project ${s.projectKey}`;
    default: return '';
  }
}

function SecretInput({ label, set, value, onChange, onClear, placeholder, help }: { label: string; set: boolean; value: string; onChange: (v: string) => void; onClear?: () => void; placeholder?: string; help?: ReactNode }) {
  return (
    <label>{label}
      <input className="in" type="password" autoComplete="new-password" value={value} onChange={(e) => onChange(e.target.value)} placeholder={set ? '•••••• stored — type to replace' : placeholder} />
      {(help || (set && onClear)) && <small className="muted">{help} {set && onClear && <button type="button" className="linkish" onClick={onClear}>Remove stored value</button>}</small>}
    </label>
  );
}

function ChannelDialog({ channel, onClose, onSaved }: { channel: Channel | null; onClose: () => void; onSaved: () => void }) {
  const [kind, setKind] = useState(channel?.kind ?? 'teams');
  const [name, setName] = useState(channel?.name ?? '');
  const [enabled, setEnabled] = useState(channel?.enabled ?? true);
  const [s, setS] = useState<ChannelSettings>(channel?.settings ?? { protocol: 'udp', format: 'cef', table: 'incident', issueType: 'Task', auth: 'basic' });
  const [sec, setSec] = useState<ChannelSecrets>({});
  const [clear, setClear] = useState<string[]>([]);
  const [recipients, setRecipients] = useState((channel?.settings.recipients ?? []).join(', '));
  const [labels, setLabels] = useState((channel?.settings.labels ?? []).join(', '));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isSet = (k: keyof Channel['secretsSet']) => !!channel?.secretsSet[k] && !clear.includes(k);
  const S = (p: Partial<ChannelSettings>) => setS({ ...s, ...p });
  const X = (p: Partial<ChannelSecrets>) => setSec({ ...sec, ...p });
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    const settings: ChannelSettings = { ...s, recipients: recipients.split(/[,;\s]+/).filter(Boolean), labels: labels.split(/[,\s]+/).filter(Boolean), port: s.port ? Number(s.port) : null };
    const body = { kind, name, enabled, settings, secrets: Object.fromEntries(Object.entries(sec).filter(([, v]) => v)), clearSecrets: clear };
    try {
      if (channel) await api.put(`/integrations/channels/${channel.id}`, body); else await api.post('/integrations/channels', body);
      onSaved();
    } catch (x) {
      const b = (x as { body?: { errors?: Record<string, string[]> } }).body;
      setError(b?.errors ? Object.values(b.errors).flat().join(' ') : err(x));
    } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="ch-title">
      <form className="modal-card" style={{ width: 'min(640px, 100%)' }} onSubmit={save}>
        <header><h2 id="ch-title">{channel ? `Edit ${KIND[kind]?.name} channel` : 'Add channel'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {!channel && <label>Type
            <select className="in" value={kind} onChange={(e) => setKind(e.target.value)}>
              {Object.entries(KIND).map(([k, v]) => <option key={k} value={k}>{v.name} — {v.desc}</option>)}
            </select>
          </label>}
          <label>Name<input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder={`e.g. ${kind === 'teams' ? 'IT Security channel' : kind === 'syslog' ? 'Sentinel collector' : kind === 'servicenow' ? 'ServiceNow incidents' : 'Security team'}`} /></label>

          {kind === 'webhook' && <>
            <label>URL<input className="in" value={s.url ?? ''} onChange={(e) => S({ url: e.target.value })} required placeholder="https://automation.example.com/hooks/zentriq" /></label>
            <SecretInput label="Signing secret (HMAC-SHA256)" set={isSet('signingSecret')} value={sec.signingSecret ?? ''} onChange={(v) => X({ signingSecret: v })} onClear={() => setClear([...clear, 'signingSecret'])}
              help={<>Sent as <span className="mono">X-Zentriq-Signature: sha256=HMAC(secret, timestamp + "." + body)</span> with <span className="mono">X-Zentriq-Timestamp</span>.</>} />
            <div className="form-grid-2">
              <label>Extra header name (optional)<input className="in" value={s.headerName ?? ''} onChange={(e) => S({ headerName: e.target.value })} placeholder="Authorization or X-Api-Key" /></label>
              <SecretInput label="Header value" set={isSet('headerValue')} value={sec.headerValue ?? ''} onChange={(v) => X({ headerValue: v })} onClear={() => setClear([...clear, 'headerValue'])} />
            </div>
          </>}
          {(kind === 'slack' || kind === 'teams') && <SecretInput label={kind === 'slack' ? 'Incoming webhook URL' : 'Workflows webhook URL'} set={isSet('url')} value={sec.url ?? ''} onChange={(v) => X({ url: v })}
            placeholder={kind === 'slack' ? 'https://hooks.slack.com/services/…' : 'https://….logic.azure.com/workflows/… or https://….webhook.office.com/…'}
            help={kind === 'slack' ? 'Slack › Apps › Incoming Webhooks › Add to a channel. The URL is stored encrypted.'
              : 'Teams › channel › Workflows › "Post to a channel when a webhook request is received". The URL is stored encrypted.'} />}
          {kind === 'email' && <label>Recipients<textarea className="in" rows={2} value={recipients} onChange={(e) => setRecipients(e.target.value)} required placeholder="secops@example.com, it-team@example.com" /><small className="muted">Uses the SMTP server from System Settings › E-mail.</small></label>}
          {kind === 'syslog' && <>
            <div className="form-grid-2">
              <label>Server<input className="in" value={s.host ?? ''} onChange={(e) => S({ host: e.target.value })} required placeholder="siem-collector.example.local" /></label>
              <label>Port<input className="in" type="number" min={1} max={65535} value={s.port ?? 514} onChange={(e) => S({ port: Number(e.target.value) })} /></label>
              <label>Protocol<select className="in" value={s.protocol ?? 'udp'} onChange={(e) => S({ protocol: e.target.value })}><option value="udp">UDP</option><option value="tcp">TCP</option></select></label>
              <label>Format<select className="in" value={s.format ?? 'cef'} onChange={(e) => S({ format: e.target.value })}><option value="cef">CEF (ArcSight, Sentinel, QRadar, Splunk)</option><option value="json">JSON</option></select></label>
            </div>
            <small className="muted">RFC 5424 syslog, facility local0. Event fields map to CEF: dhost, dst, msg, externalId, cs1 = link, cs2 = asset criticality.</small>
          </>}
          {kind === 'servicenow' && <>
            <label>Instance URL<input className="in" value={s.instanceUrl ?? ''} onChange={(e) => S({ instanceUrl: e.target.value })} required placeholder="https://example.service-now.com" /></label>
            <div className="form-grid-2">
              <label>User name<input className="in" value={s.username ?? ''} onChange={(e) => S({ username: e.target.value })} required autoComplete="off" /></label>
              <SecretInput label="Password" set={isSet('password')} value={sec.password ?? ''} onChange={(v) => X({ password: v })} />
              <label>Table<input className="in" value={s.table ?? 'incident'} onChange={(e) => S({ table: e.target.value })} /></label>
              <label>Assignment group (optional)<input className="in" value={s.assignmentGroup ?? ''} onChange={(e) => S({ assignmentGroup: e.target.value })} placeholder="Name or sys_id" /></label>
              <label>Category (optional)<input className="in" value={s.category ?? ''} onChange={(e) => S({ category: e.target.value })} placeholder="e.g. security" /></label>
            </div>
            <small className="muted">Use an integration user with the itil role (or rest_api_explorer + create rights on the table). Urgency/impact follow the event severity; the incident number is written back to vulnerability findings.</small>
          </>}
          {kind === 'jira' && <>
            <label>Jira URL<input className="in" value={s.instanceUrl ?? ''} onChange={(e) => S({ instanceUrl: e.target.value })} required placeholder="https://example.atlassian.net" /></label>
            <div className="form-grid-2">
              <label>Authentication<select className="in" value={s.auth ?? 'basic'} onChange={(e) => S({ auth: e.target.value })}><option value="basic">E-mail + API token (Cloud)</option><option value="bearer">Personal access token (Data Center)</option></select></label>
              {s.auth !== 'bearer' && <label>Account e-mail<input className="in" value={s.username ?? ''} onChange={(e) => S({ username: e.target.value })} autoComplete="off" /></label>}
              <SecretInput label={s.auth === 'bearer' ? 'Personal access token' : 'API token'} set={isSet('password')} value={sec.password ?? ''} onChange={(v) => X({ password: v })} />
              <label>Project key<input className="in" value={s.projectKey ?? ''} onChange={(e) => S({ projectKey: e.target.value.toUpperCase() })} required placeholder="SEC" /></label>
              <label>Issue type<input className="in" value={s.issueType ?? 'Task'} onChange={(e) => S({ issueType: e.target.value })} /></label>
              <label>Labels (optional)<input className="in" value={labels} onChange={(e) => setLabels(e.target.value)} placeholder="security, vulnerability" /></label>
            </div>
          </>}
          <label className="row-check"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Channel is on</label>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}

// ---- Alert rules -------------------------------------------------------------------------------------------

function Rules({ catalog }: { catalog: Catalog }) {
  const confirm = useConfirm();
  const rules = useApi(() => api.get<Rule[]>('/integrations/rules'), []);
  const channels = useApi(() => api.get<Channel[]>('/integrations/channels'), []);
  const [edit, setEdit] = useState<Rule | 'new' | null>(null);
  const evName = (t: string) => catalog.eventTypes.find((e) => e.type === t)?.name ?? t;
  const chName = (id: string) => channels.data?.find((c) => c.id === id)?.name ?? '(deleted)';
  async function remove(r: Rule) {
    if (!(await confirm({ title: `Delete the rule "${r.name}"?`, message: 'Events of this type stop being sent through it.', danger: true }))) return;
    await api.del(`/integrations/rules/${r.id}`);
    rules.reload();
  }
  async function toggle(r: Rule) {
    await api.put(`/integrations/rules/${r.id}`, { ...r, enabled: !r.enabled });
    rules.reload();
  }
  return (
    <>
      {channels.data && channels.data.length === 0 && <Callout tone="info" icon="info">Add a <Link to="/integrations?tab=channels">channel</Link> first; a rule sends events to one or more channels.</Callout>}
      <Panel flush title="Alert rules" sub="Which events go where" right={<button className="btn sm primary" disabled={!channels.data?.length} onClick={() => setEdit('new')}><Icon name="plus" size={12} /> Add rule</button>}>
        <ErrorBox error={rules.error} />
        <DataTable rows={rules.data ?? []} empty="No alert rules yet." columns={[
          { key: 'n', label: 'Rule', className: 'wrap', render: (r) => <><b>{r.name}</b><div className="faint" style={{ fontSize: 11.5 }}>{evName(r.eventType)}{r.minSeverity !== 'info' ? ` · ${r.minSeverity} or higher` : ''}
            {r.conditions.categories.length > 0 ? ` · ${r.conditions.categories.map((c) => fmt.label(c)).join(', ')}` : ''}{r.conditions.criticality.length > 0 ? ` · criticality ${r.conditions.criticality.join(', ')}` : ''}</div></> },
          { key: 'c', label: 'Channels', className: 'wrap', render: (r) => r.channelIds.map(chName).join(', ') || '—' },
          { key: 't', label: 'Throttle', render: (r) => r.throttleMinutes ? `${r.throttleMinutes} min` : '—' },
          { key: 'l', label: 'Last sent', render: (r) => r.lastFiredAt ? fmt.ago(r.lastFiredAt) : 'never' },
          { key: 's', label: 'Sent (7 d)', render: (r) => r.sent7d },
          { key: 'e', label: 'Status', render: (r) => <button className="linkish" onClick={() => void toggle(r)} title="Turn on/off">{r.enabled ? <Pill tone="ok">On</Pill> : <Pill>Off</Pill>}</button> },
          { key: 'x', label: '', render: (r) => <div style={{ display: 'flex', gap: 6 }}>
            <button className="btn sm" onClick={() => setEdit(r)}>Edit</button>
            <button className="btn sm" onClick={() => void remove(r)}>Delete</button>
          </div> },
        ]} />
      </Panel>
      {edit && channels.data && <RuleDialog rule={edit === 'new' ? null : edit} catalog={catalog} channels={channels.data} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); rules.reload(); }} />}
    </>
  );
}

const CATEGORIES = ['server', 'endpoint', 'network_device', 'printer', 'iot', 'virtual_host', 'other'];

function RuleDialog({ rule, catalog, channels, onClose, onSaved }: { rule: Rule | null; catalog: Catalog; channels: Channel[]; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState<Omit<Rule, 'id' | 'lastFiredAt' | 'sent7d'>>(rule ?? {
    name: '', enabled: true, eventType: 'vulnerability.new', minSeverity: 'high', conditions: { categories: [], criticality: [] }, channelIds: channels.length === 1 ? [channels[0]!.id] : [], throttleMinutes: 0,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ev = catalog.eventTypes.find((e) => e.type === f.eventType);
  const toggle = <T,>(list: T[], v: T) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      if (rule) await api.put(`/integrations/rules/${rule.id}`, f); else await api.post('/integrations/rules', f);
      onSaved();
    } catch (x) {
      const b = (x as { body?: { errors?: Record<string, string[]> } }).body;
      setError(b?.errors ? Object.values(b.errors).flat().join(' ') : err(x));
    } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="rule-dlg-title">
      <form className="modal-card" style={{ width: 'min(640px, 100%)' }} onSubmit={save}>
        <header><h2 id="rule-dlg-title">{rule ? 'Edit alert rule' : 'Add alert rule'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Name<input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={120} placeholder="e.g. Exploited vulnerabilities on servers to ServiceNow" /></label>
          <div className="form-grid-2">
            <label>When<select className="in" value={f.eventType} onChange={(e) => setF({ ...f, eventType: e.target.value })}>
              {catalog.eventTypes.map((e) => <option key={e.type} value={e.type}>{e.name}</option>)}
            </select></label>
            <label>Minimum severity<select className="in" value={f.minSeverity} onChange={(e) => setF({ ...f, minSeverity: e.target.value })}>
              {catalog.severities.map((s) => <option key={s} value={s}>{cap(s)}{s === 'info' ? ' (everything)' : ' or higher'}</option>)}
            </select></label>
          </div>
          {ev && <small className="muted" style={{ marginTop: -6 }}>{ev.description}</small>}
          {ev?.perDevice && <>
            <div><div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Only for device categories <span className="faint">(none = all)</span></div>
              <div className="check-grid">{CATEGORIES.map((c) => <label key={c} className="check-inline"><input type="checkbox" checked={f.conditions.categories.includes(c)} onChange={() => setF({ ...f, conditions: { ...f.conditions, categories: toggle(f.conditions.categories, c) } })} /> {fmt.label(c)}</label>)}</div>
            </div>
            <div><div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Only for asset criticality <span className="faint">(none = all)</span></div>
              <div className="check-grid">{['Critical', 'High', 'Medium', 'Low'].map((c) => <label key={c} className="check-inline"><input type="checkbox" checked={f.conditions.criticality.includes(c)} onChange={() => setF({ ...f, conditions: { ...f.conditions, criticality: toggle(f.conditions.criticality, c) } })} /> {c}</label>)}</div>
            </div>
          </>}
          <div><div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Send to</div>
            <div className="check-list">{channels.map((c) => <label key={c.id}><input type="checkbox" checked={f.channelIds.includes(c.id)} onChange={() => setF({ ...f, channelIds: toggle(f.channelIds, c.id) })} /> {c.name} <span className="faint">({KIND[c.kind]?.name}{c.enabled ? '' : ', off'})</span></label>)}</div>
          </div>
          <label>Throttle (minutes)
            <input className="in" type="number" min={0} max={10080} value={f.throttleMinutes} onChange={(e) => setF({ ...f, throttleMinutes: Number(e.target.value) })} style={{ width: 140 }} />
            <small className="muted">At most one message per {ev?.perDevice ? 'device ' : ''}in this period; others are logged as throttled. 0 = no limit.</small>
          </label>
          <label className="row-check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Rule is on</label>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || f.channelIds.length === 0}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}

// ---- Delivery log -------------------------------------------------------------------------------------------

function DeliveryLog() {
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const d = useApi(() => api.get<Paged<Delivery>>('/integrations/deliveries', { status, page, pageSize: 50 }), [status, page]);
  const [msg, setMsg] = useState<string | null>(null);
  async function retry(x: Delivery) {
    try { setMsg((await api.post<{ message: string }>(`/integrations/deliveries/${x.id}/retry`)).message); d.reload(); } catch (e) { setMsg(err(e)); }
  }
  return (
    <>
      {msg && <Callout tone="info" icon="info">{msg}</Callout>}
      <Panel flush title="Delivery log" sub="Kept 90 days. Failed sends are retried after 1, 5, 15 and 60 minutes." right={
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Status">
          <option value="">All</option><option value="sent">Sent</option><option value="failed">Failed</option><option value="queued">Waiting / retrying</option><option value="suppressed">Throttled</option>
        </select>}>
        <ErrorBox error={d.error} />
        <DataTable rows={d.data?.items ?? []} empty={d.loading ? 'Loading…' : 'Nothing sent yet.'} columns={[
          { key: 'w', label: 'When', render: (x) => <span title={fmt.date(x.createdAt)}>{fmt.ago(x.createdAt)}</span> },
          { key: 'e', label: 'Event', className: 'wrap', render: (x) => <>{x.title}<div className="faint" style={{ fontSize: 11 }}>{x.eventType}{x.rule ? ` · rule ${x.rule}` : ''}</div></> },
          { key: 'v', label: 'Severity', render: (x) => <Pill tone={SEV_TONE[x.severity]}>{x.severity}</Pill> },
          { key: 'c', label: 'Channel', render: (x) => x.channel ? <>{x.channel} <span className="faint">({KIND[x.kind ?? '']?.name ?? x.kind})</span></> : '—' },
          { key: 's', label: 'Result', className: 'wrap', render: (x) => <><Pill tone={DELIVERY_TONE[x.status]}>{x.status === 'queued' && x.attempts > 0 ? `retry ${x.attempts}` : x.status === 'suppressed' ? 'throttled' : x.status}</Pill>
            {x.response && <span className="mono" style={{ fontSize: 11.5 }}> {x.response}</span>}
            {x.error && <div className="faint clamp-2" style={{ fontSize: 11, maxWidth: 320 }} title={x.error}>{x.error}</div>}</> },
          { key: 'x', label: '', render: (x) => (x.status === 'failed' || x.status === 'suppressed') && x.channelId ? <button className="btn sm" onClick={() => void retry(x)}>Send again</button> : null },
        ]} />
        {d.data && <Pager page={page} pageSize={50} total={d.data.total} onPage={setPage} />}
      </Panel>
    </>
  );
}

// ---- API access ------------------------------------------------------------------------------------------------

function ApiAccess({ catalog }: { catalog: Catalog }) {
  const confirm = useConfirm();
  const tokens = useApi(() => api.get<Token[]>('/integrations/tokens'), []);
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<{ token: string; expiresAt: string | null } | null>(null);
  const origin = window.location.origin;
  async function revoke(t: Token) {
    if (!(await confirm({ title: `Revoke the token "${t.name}"?`, message: 'Scripts and tools using it stop working immediately.', confirmLabel: 'Revoke', danger: true }))) return;
    await api.del(`/integrations/tokens/${t.id}`);
    tokens.reload();
  }
  return (
    <>
      {created && <Callout tone="ok" icon="info">
        <b>Copy the token now — it is not shown again.</b>
        <div style={{ marginTop: 6 }}><CopyBox label="API token" value={created.token} /></div>
        <small className="muted">{created.expiresAt ? `Expires ${fmt.day(created.expiresAt)}.` : 'Does not expire.'} Only a hash is stored on the server.</small>
      </Callout>}
      <Panel flush title="API tokens" sub="Read-only access for scripts, BI tools and SIEM pulls" right={<button className="btn sm primary" onClick={() => setCreating(true)}><Icon name="plus" size={12} /> New token</button>}>
        <ErrorBox error={tokens.error} />
        <DataTable rows={tokens.data ?? []} empty="No API tokens." columns={[
          { key: 'n', label: 'Name', className: 'wrap', render: (t) => <><b>{t.name}</b> <span className="mono faint" style={{ fontSize: 11 }}>{t.prefix}…</span><div className="faint" style={{ fontSize: 11.5 }}>{t.permissions.map((p) => PERM_LABEL[p] ?? p).join(', ')}</div></> },
          { key: 'c', label: 'Created', render: (t) => <span title={fmt.date(t.createdAt)}>{t.createdBy}, {fmt.day(t.createdAt)}</span> },
          { key: 'e', label: 'Expires', render: (t) => t.expiresAt ? fmt.day(t.expiresAt) : 'never' },
          { key: 'u', label: 'Last used', render: (t) => t.lastUsedAt ? <span title={t.lastUsedIp ?? ''}>{fmt.ago(t.lastUsedAt)}</span> : 'never' },
          { key: 's', label: 'Status', render: (t) => t.revokedAt ? <Pill>Revoked</Pill> : t.expiresAt && new Date(t.expiresAt) < new Date() ? <Pill tone="warn">Expired</Pill> : <Pill tone="ok">Active</Pill> },
          { key: 'x', label: '', render: (t) => !t.revokedAt ? <button className="btn sm" onClick={() => void revoke(t)}>Revoke</button> : null },
        ]} />
      </Panel>
      <Panel title="Using the API">
        <p style={{ marginTop: 0 }}>Send the token in the <span className="mono">Authorization</span> header. Tokens can only read (GET); they carry the permissions chosen when created, limited to what the creator still has, and stop working when the creator's account is disabled.</p>
        <p style={{ margin: '10px 0 0' }}><b>Data of a saved report</b> (any report you can open in Reports &amp; Analytics — copy its id from the address bar):</p>
        <pre className="code-block">{`curl -H "Authorization: Bearer zq_…" "${origin}/api/v1/reports/<report-id>/data?format=csv" -o devices.csv
curl -H "Authorization: Bearer zq_…" "${origin}/api/v1/reports/<report-id>/data"          # JSON rows`}</pre>
        <p style={{ margin: '10px 0 0' }}><b>PowerShell / Power BI</b></p>
        <pre className="code-block">{`$h = @{ Authorization = "Bearer zq_…" }
Invoke-RestMethod -Headers $h "${origin}/api/v1/vulnerabilities/findings?status=active&pageSize=200"
Invoke-RestMethod -Headers $h "${origin}/api/v1/security/devices?pageSize=200"
Invoke-RestMethod -Headers $h "${origin}/api/v1/assets?pageSize=200"`}</pre>
        <p className="faint" style={{ fontSize: 12, marginBottom: 0 }}>The full API description (OpenAPI) is at <a href="/api/openapi/v1.json" target="_blank" rel="noreferrer">/api/openapi/v1.json</a> (administrators). Every report download with a token is recorded in the audit log.</p>
      </Panel>
      {creating && <TokenDialog catalog={catalog} onClose={() => setCreating(false)} onCreated={(t) => { setCreating(false); setCreated(t); tokens.reload(); }} />}
    </>
  );
}

function TokenDialog({ catalog, onClose, onCreated }: { catalog: Catalog; onClose: () => void; onCreated: (t: { token: string; expiresAt: string | null }) => void }) {
  const [name, setName] = useState('');
  const [perms, setPerms] = useState<string[]>(['reports.export', 'assets.view']);
  const [days, setDays] = useState(365);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { onCreated(await api.post<{ token: string; expiresAt: string | null }>('/integrations/tokens', { name, permissions: perms, expiresInDays: days })); }
    catch (x) {
      const b = (x as { body?: { errors?: Record<string, string[]> } }).body;
      setError(b?.errors ? Object.values(b.errors).flat().join(' ') : err(x));
    } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="tok-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="tok-title">New API token</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Name<input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder="e.g. Power BI asset dashboard" /></label>
          <div><div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Can read</div>
            <div className="check-list" style={{ maxHeight: 'none' }}>{catalog.tokenPermissions.map((p) => <label key={p}><input type="checkbox" checked={perms.includes(p)} onChange={() => setPerms(perms.includes(p) ? perms.filter((x) => x !== p) : [...perms, p])} /> {PERM_LABEL[p] ?? p} <span className="mono faint" style={{ fontSize: 11 }}>{p}</span></label>)}</div>
          </div>
          <label>Expires after
            <select className="in" value={days} onChange={(e) => setDays(Number(e.target.value))}>
              <option value={30}>30 days</option><option value={90}>90 days</option><option value={365}>1 year</option><option value={730}>2 years</option><option value={0}>Never (not recommended)</option>
            </select>
          </label>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || perms.length === 0 || !name.trim()}>{busy ? 'Creating…' : 'Create token'}</button>
        </footer>
      </form>
    </div>
  );
}
