import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { Credential } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Modal } from '../../components/Modal';
import { Callout } from '../../components/ui';
import { PROVIDERS, type CloudAccount, type Provider } from './types';

const err = (e: unknown) => (e instanceof Error ? e.message : String(e));
const list = (s: string) => s.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean);

function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label>{label}{children}{hint && <small className="muted">{hint}</small>}</label>;
}

const HELP: Record<Provider, ReactNode> = {
  azure: <>In the Azure portal: <b>Microsoft Entra ID › App registrations › New registration</b>; then <b>Certificates &amp; secrets › New client secret</b>.
    On each subscription: <b>Access control (IAM) › Add role assignment › Reader</b> for the app. Zentriq reads with Azure Resource Graph and changes nothing.</>,
  aws: <>In IAM: create a user for Zentriq, attach the <b>ReadOnlyAccess</b> policy (or <b>SecurityAudit</b> plus <b>ViewOnlyAccess</b>), and create an access key
    (<i>Application running outside AWS</i>). Zentriq signs requests with the key and changes nothing.</>,
  vmware: <>vCenter Server 6.5 or later. Create a user (for example <span className="mono">zentriq@vsphere.local</span>) and give it the <b>Read-only</b> role on the vCenter,
    with <i>Propagate to children</i>. ESXi hosts without vCenter are not supported — use the agent or a network scan for those.</>,
  hyperv: <>Each Hyper-V host (every node of a failover cluster). The saved Windows credential must be in <b>Hyper-V Administrators</b> (or an administrator) on the hosts,
    and WinRM (5985) or WMI/DCOM must be reachable from the Zentriq server — the same as an agentless Windows scan.</>,
};

export function AccountDialog({ account, onClose, onDone }: { account: CloudAccount | null; onClose: () => void; onDone: (msg: string) => void }) {
  const st = account?.settings ?? {};
  const [provider, setProvider] = useState<Provider>(account?.provider ?? 'azure');
  const [f, setF] = useState({
    name: account?.name ?? '', tenantId: st.tenantId ?? '', clientId: st.clientId ?? '', subscriptions: (st.subscriptions ?? []).join('\n'),
    accessKeyId: st.accessKeyId ?? '', regions: (st.regions ?? []).join(', '), host: st.host ?? '', port: st.port ? String(st.port) : '', username: st.username ?? '',
    skip: st.skipCertificateCheck ?? false, hosts: (st.hosts ?? []).join('\n'), secret: '', credentialId: account?.credentialId ?? '',
    enabled: account?.enabled ?? true, syncHours: String(account?.syncHours ?? 6),
  });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value } as typeof f);
  const creds = useApi(() => (provider === 'hyperv' ? api.get<Credential[]>('/credentials') : Promise.resolve([] as Credential[])), [provider]);
  const windows = (creds.data ?? []).filter((c) => c.kind === 'windows_domain' || c.kind === 'windows_local');
  useEffect(() => {
    if (provider === 'hyperv' && !f.credentialId && windows.length === 1) setF((x) => ({ ...x, credentialId: windows[0]!.id }));
  }, [provider, windows.length]); // eslint-disable-line react-hooks/exhaustive-deps
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const secretLabel = provider === 'azure' ? 'Client secret (value)' : provider === 'aws' ? 'Secret access key' : 'Password';

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = {
        name: f.name, provider, tenantId: f.tenantId, clientId: f.clientId, subscriptions: list(f.subscriptions), accessKeyId: f.accessKeyId, regions: list(f.regions),
        host: f.host, port: f.port ? Number(f.port) : null, username: f.username, skipCertificateCheck: f.skip, hosts: list(f.hosts),
        secret: f.secret || null, credentialId: f.credentialId || null, enabled: f.enabled, syncHours: Number(f.syncHours) || 6,
      };
      if (account) { await api.put(`/cloud/accounts/${account.id}`, body); onDone(`"${f.name}" saved.`); }
      else {
        await api.post('/cloud/accounts', body);
        onDone(f.enabled ? `"${f.name}" added. The first sync is running — resources appear in a few minutes.` : `"${f.name}" added (disabled).`);
      }
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }

  return (
    <Modal title={account ? `Connection — ${account.name}` : 'Add a cloud or virtualization connection'} wide onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="cl-form" className="btn primary" disabled={busy}>{busy ? 'Saving…' : account ? 'Save' : 'Add and sync'}</button>
    </>}>
      <form id="cl-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {!account && <div className="cl-pick" role="radiogroup" aria-label="Platform">
          {PROVIDERS.map((p) => (
            <button key={p.id} type="button" role="radio" aria-checked={provider === p.id} className={`cl-pick-btn cl-${p.id}${provider === p.id ? ' on' : ''}`} onClick={() => setProvider(p.id)}>
              <i /><b>{p.label}</b><small>{p.kind === 'cloud' ? 'Public cloud' : 'Virtualization'}</small>
            </button>
          ))}
        </div>}
        <Callout icon="info">{HELP[provider]}</Callout>
        <div className="grid-2 tight">
          <Field label="Name"><input className="in" value={f.name} onChange={set('name')} required maxLength={200} placeholder={provider === 'azure' ? 'Azure — production' : provider === 'aws' ? 'AWS — main account' : provider === 'vmware' ? 'vCenter — Pune DC' : 'Hyper-V cluster — HQ'} /></Field>
          <Field label="Sync every (hours)"><input className="in" type="number" min={1} max={168} value={f.syncHours} onChange={set('syncHours')} /></Field>
          {provider === 'azure' && <>
            <Field label="Directory (tenant) ID"><input className="in mono" value={f.tenantId} onChange={set('tenantId')} required placeholder="00000000-0000-0000-0000-000000000000" /></Field>
            <Field label="Application (client) ID"><input className="in mono" value={f.clientId} onChange={set('clientId')} required placeholder="00000000-0000-0000-0000-000000000000" /></Field>
          </>}
          {provider === 'aws' && <>
            <Field label="Access key ID"><input className="in mono" value={f.accessKeyId} onChange={set('accessKeyId')} required placeholder="AKIA…" /></Field>
            <Field label="Regions" hint="Empty = every enabled region."><input className="in mono" value={f.regions} onChange={set('regions')} placeholder="ap-south-1, us-east-1" /></Field>
          </>}
          {provider === 'vmware' && <>
            <Field label="vCenter host name or IP"><input className="in mono" value={f.host} onChange={set('host')} required placeholder="vcenter.corp.example.com" /></Field>
            <Field label="Port" hint="443 by default."><input className="in" type="number" min={1} max={65535} value={f.port} onChange={set('port')} placeholder="443" /></Field>
            <Field label="User"><input className="in" value={f.username} onChange={set('username')} required placeholder="zentriq@vsphere.local" autoComplete="off" /></Field>
          </>}
          {provider !== 'hyperv' && <Field label={secretLabel} hint={account?.hasSecret ? 'Saved (encrypted). Leave empty to keep it.' : 'Stored encrypted; never shown again.'}>
            <input className="in" type="password" value={f.secret} onChange={set('secret')} required={!account} autoComplete="new-password" />
          </Field>}
          {provider === 'hyperv' && <Field label="Windows credential" hint={windows.length === 0 && !creds.loading ? 'No Windows credential saved yet — add one in Discovery › Credentials.' : 'Saved credentials (Discovery › Credentials).'}>
            <select className="in" value={f.credentialId} onChange={set('credentialId')} required>
              <option value="">Choose…</option>
              {windows.map((c) => <option key={c.id} value={c.id}>{c.name}{c.username ? ` (${c.username})` : ''}</option>)}
            </select>
          </Field>}
        </div>
        {provider === 'azure' && <Field label="Subscription IDs" hint="Empty = every subscription the app has the Reader role on. One per line.">
          <textarea className="in mono" rows={2} value={f.subscriptions} onChange={set('subscriptions')} />
        </Field>}
        {provider === 'hyperv' && <Field label="Hyper-V hosts" hint="Host names or IP addresses, one per line (each node of a cluster).">
          <textarea className="in mono" rows={3} value={f.hosts} onChange={set('hosts')} required placeholder={'hv01.corp.example.com\nhv02.corp.example.com'} />
        </Field>}
        {provider === 'vmware' && <label className="chk"><input type="checkbox" checked={f.skip} onChange={(e) => setF({ ...f, skip: e.target.checked })} /> Skip the certificate check (vCenter with its own self-signed certificate)</label>}
        <label className="chk"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Enabled (synced on schedule)</label>
      </form>
    </Modal>
  );
}

interface AssetHit { id: string; hostname: string | null; ip: string | null; osName?: string | null }

/** Link a virtual machine or host to an inventoried device by hand, unlink it, or give it back to automatic matching. */
export function LinkDialog({ resourceId, name, current, onClose, onDone }: { resourceId: string; name: string; current: { id: string; name: string } | null; onClose: () => void; onDone: (m: string) => void }) {
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  const [picked, setPicked] = useState<AssetHit | null>(null);
  useEffect(() => { const t = setTimeout(() => setQ(text), 250); return () => clearTimeout(t); }, [text]);
  const hits = useApi(() => (q.trim().length >= 2 ? api.get<{ items: AssetHit[] }>('/assets', { q, pageSize: 20 }) : Promise.resolve(null)), [q]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(body: { assetId: string | null; automatic?: boolean }, msg: string) {
    setBusy(true); setError(null);
    try { await api.put(`/cloud/resources/${resourceId}/link`, body); onDone(msg); } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Inventoried device — ${name}`} onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="button" className="btn" disabled={busy} onClick={() => void save({ assetId: null, automatic: true }, 'Matching is automatic again.')}>Match automatically</button>
      {current && <button type="button" className="btn" disabled={busy} onClick={() => void save({ assetId: null }, 'Unlinked.')}>Unlink</button>}
      <button type="button" className="btn primary" disabled={busy || !picked} onClick={() => picked && void save({ assetId: picked.id }, `Linked to ${picked.hostname ?? picked.ip}.`)}>Link</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <p className="muted">Zentriq links machines by BIOS UUID, instance ID, MAC address, then host name with the same IP. Choose the device here when it cannot tell;
          a link set by hand is kept by later syncs.</p>
        {current && <p>Now linked to <b>{current.name}</b>.</p>}
        <Field label="Find the device (host name or IP)"><input className="in" value={text} onChange={(e) => { setText(e.target.value); setPicked(null); }} autoFocus placeholder="at least 2 characters" /></Field>
        <div className="cl-hits">
          {(hits.data?.items ?? []).map((a) => (
            <button key={a.id} type="button" className={picked?.id === a.id ? 'on' : ''} onClick={() => setPicked(a)}>
              <b>{a.hostname ?? a.ip ?? a.id}</b> <span className="faint">{a.ip}{a.osName ? ` · ${a.osName}` : ''}</span>
            </button>
          ))}
          {q.trim().length >= 2 && hits.data && hits.data.items.length === 0 && <p className="muted">No device matches.</p>}
        </div>
      </div>
    </Modal>
  );
}
