import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill } from '../../components/ui';
import { LinkDialog } from './CloudDialogs';
import { ChangeText, ProviderBadge } from './CloudPage';
import { disk, gb, kindLabel, SEV_TONE, stateLabel, stateTone, type CloudResource } from './types';

interface Detail {
  resource: CloudResource;
  asset: { id: string; hostname: string | null; ip: string | null; os: string | null; lastSeen: string | null; serial: string | null } | null;
  parent: { id: string; name: string | null; kind: string } | null;
  children: { id: string; name: string | null; kind: string; state: string | null; size: string | null; os: string | null; privateIp: string | null; diskGb: number | null; cpu: number | null; memoryMb: number | null; assetId: string | null }[];
  findings: { id: number; rule: string; severity: string; title: string; detail: string | null; firstSeen: string; lastSeen: string; resolvedAt: string | null; ignored: boolean; ignoredBy: string | null }[];
  changes: { id: number; at: string; change: 'created' | 'changed' | 'deleted' | 'restored'; detail: Record<string, unknown> }[];
}

function KV({ items }: { items: [string, ReactNode][] }) {
  return <dl className="kv-grid">{items.filter(([, v]) => v != null && v !== '').map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v}</dd></div>)}</dl>;
}

const PROP_LABEL: Record<string, string> = {
  subscriptionId: 'Subscription', type: 'Azure type', osType: 'OS type', priority: 'Priority', attached: 'Attached', encryption: 'Encryption', encrypted: 'Encrypted',
  associated: 'In use', subnets: 'Subnets', openPorts: 'Ports open to the internet', openRules: 'Rules open to the internet', attachedTo: 'Attached to',
  frontends: 'Front ends', backendPools: 'Back-end pools', publicAccess: 'Anonymous public access allowed', httpsOnly: 'HTTPS only', publicNetworkAccess: 'Public network access',
  minimumTlsVersion: 'Minimum TLS', kind: 'Kind', publiclyAccessible: 'Reachable from the internet', multiAz: 'Multi-AZ', nodes: 'Nodes', nodePools: 'Node pools',
  launchTime: 'Launched', availabilityZone: 'Availability zone', imageId: 'Image', volumes: 'Volumes', default: 'Default VPC', description: 'Description', scheme: 'Scheme',
  ha: 'HA', drs: 'DRS', hosts: 'Hosts', vms: 'Virtual machines', hardwareVersion: 'Hardware version', toolsRunning: 'VMware Tools running', fqdn: 'FQDN',
  freeGb: 'Free (GB)', freePercent: 'Free (%)', generation: 'Generation', configurationVersion: 'Configuration version', dynamicMemory: 'Dynamic memory',
  uptimeHours: 'Up for (hours)', disks: 'Virtual disks', snapshotCount: 'Checkpoints', snapshotOldestDays: 'Oldest checkpoint (days)', osVersion: 'OS version',
  model: 'Model', serial: 'Serial number', osEndOfSupport: 'OS out of support', address: 'Address', runningMemoryMb: 'Memory of running VMs (MB)', memoryCommitPercent: 'Memory given to VMs (%)',
};
function propValue(v: unknown): ReactNode {
  if (v === true) return 'yes';
  if (v === false) return 'no';
  if (Array.isArray(v)) return v.length === 0 ? '—' : v.map((x) => (x === 0 ? 'all ports' : String(x))).join(', ');
  if (v == null) return null;
  return String(v);
}

export function CloudResourcePage({ id }: { id: string }) {
  const { can } = useAuth();
  const [tick, setTick] = useState(0);
  const [msg, setMsg] = useState<string | null>(null);
  const [linking, setLinking] = useState(false);
  const d = useApi(() => api.get<Detail>(`/cloud/resources/${id}`), [id, tick]);
  if (d.error) return <><PageHead title="Cloud resource" /><ErrorBox error={d.error} /></>;
  if (!d.data) return <Loading />;
  const { resource: r, asset, parent, children, findings, changes } = d.data;
  const machine = r.kind === 'vm' || r.kind === 'host';
  const props = Object.entries(r.properties).filter(([, v]) => v !== null && v !== '' && !(Array.isArray(v) && v.length === 0));
  async function ignore(fid: number, ignored: boolean) {
    try { await api.post(`/cloud/findings/${fid}/ignore`, { ignored }); setTick((x) => x + 1); } catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <>
      <PageHead title={r.name ?? r.externalId} icon="cloud" desc={<><ProviderBadge provider={r.provider} full /> · {kindLabel(r.kind)} · {r.account}</>}
        actions={<Link className="btn" to="/cloud?tab=resources">← Resources</Link>} />
      {msg && <Callout icon="info">{msg}</Callout>}
      {r.deletedAt && <Callout tone="warn" icon="alert">Deleted at the provider — not seen since {fmt.date(r.deletedAt)}. Kept here for its history.</Callout>}
      <div className="grid-2">
        <Panel title="Resource">
          <KV items={[
            ['State', r.state ? <><Pill tone={stateTone(r.state)}>{stateLabel(r.state)}</Pill>{r.stateSince && <span className="faint"> since {fmt.day(r.stateSince)}</span>}</> : null],
            ['Type', kindLabel(r.kind)],
            ['Size / SKU', r.size],
            ['Operating system / engine', r.os],
            ['vCPU', r.cpu], ['Memory', r.memoryMb != null ? gb(r.memoryMb) : null], ['Storage', r.diskGb != null ? disk(r.diskGb) : null],
            ['Region / site', r.region], ['Group / VPC / cluster', r.group],
            ['Runs on', parent ? <Link to={`/cloud/resources/${parent.id}`}>{parent.name} <span className="faint">({kindLabel(parent.kind)})</span></Link> : r.parentId && r.kind !== 'vm' ? <span className="mono" style={{ fontSize: 11.5 }}>{r.parentId}</span> : null],
            ['Host name', r.hostname], ['Private IP', r.privateIp ? <span className="mono">{r.privateIp}</span> : null], ['Public IP', r.publicIp ? <span className="mono">{r.publicIp}</span> : null],
            ['MAC address', r.macs.length ? <span className="mono">{r.macs.join(', ')}</span> : null], ['BIOS UUID', r.biosUuid ? <span className="mono">{r.biosUuid}</span> : null],
            ['Department', r.department], ['Location', r.location],
            ['Created', r.createdAt ? fmt.date(r.createdAt) : null], ['First seen by Zentriq', fmt.date(r.firstSeen)], ['Last seen', fmt.ago(r.lastSeen)],
            ['Resource ID', <span className="mono cl-id">{r.externalId}</span>],
          ]} />
        </Panel>
        <div className="cl-stack">
          {machine && <Panel title="Inventoried device" right={can('cloud.manage') && <button className="btn sm" onClick={() => setLinking(true)}><Icon name="link" size={13} /> {asset ? 'Change' : 'Link'}</button>}>
            {asset ? <KV items={[
              ['Device', <Link to={`/assets/${asset.id}`}><b>{asset.hostname ?? asset.ip}</b></Link>],
              ['IP address', asset.ip], ['Operating system', asset.os], ['Serial number', asset.serial], ['Last seen', fmt.ago(asset.lastSeen)],
              ['Linked', r.assetLinkManual ? 'by hand' : 'automatically (BIOS UUID, instance ID, MAC or host name)'],
            ]} /> : <p className="muted">{r.assetLinkManual ? 'Unlinked by hand.' : 'Not matched to a device in the inventory: no agent and not found by a scan, or its identifiers differ.'}
              {r.state && stateTone(r.state) === 'ok' && !r.assetLinkManual && <> Deploy the agent or scan <span className="mono">{r.privateIp ?? r.hostname ?? 'it'}</span> to inventory its software and hardware.</>}</p>}
          </Panel>}
          <Panel title="Findings" flush>
            <DataTable rows={findings} empty="No findings." columns={[
              { key: 's', label: 'Severity', render: (f) => <Pill tone={f.resolvedAt ? 'ok' : SEV_TONE[f.severity]}>{f.resolvedAt ? 'resolved' : f.severity}</Pill> },
              { key: 't', label: 'Finding', className: 'wrap', render: (f) => <><b>{f.title}</b>{f.detail && <div className="faint" style={{ fontSize: 12 }}>{f.detail}</div>}{f.ignored && <div className="faint" style={{ fontSize: 11.5 }}>Ignored by {f.ignoredBy}</div>}</> },
              { key: 'd', label: 'Since', render: (f) => fmt.day(f.resolvedAt ?? f.firstSeen) },
              ...(can('cloud.manage') ? [{ key: 'x', label: '', render: (f: Detail['findings'][number]) => f.resolvedAt ? null :
                <button className="btn sm ghost" onClick={() => void ignore(f.id, !f.ignored)}>{f.ignored ? 'Reopen' : 'Ignore'}</button> }] : []),
            ]} />
          </Panel>
          {Object.keys(r.tags).length > 0 && <Panel title="Tags">
            <div className="cl-tags">{Object.entries(r.tags).map(([k, v]) => <span key={k} className="cl-tag"><b>{k}</b>={v}</span>)}</div>
          </Panel>}
        </div>
      </div>
      {children.length > 0 && <Panel title={r.kind === 'host' ? 'Virtual machines on this host' : 'Attached resources'} flush>
        <DataTable rows={children} columns={[
          { key: 'n', label: 'Name', render: (c) => <Link to={`/cloud/resources/${c.id}`}><b>{c.name ?? '—'}</b></Link> },
          { key: 'k', label: 'Type', render: (c) => kindLabel(c.kind) },
          { key: 's', label: 'State', render: (c) => c.state ? <Pill tone={stateTone(c.state)}>{stateLabel(c.state)}</Pill> : '—' },
          { key: 'os', label: 'OS', className: 'wrap', render: (c) => c.os ?? '—' },
          { key: 'spec', label: 'vCPU / memory', render: (c) => c.cpu || c.memoryMb ? `${c.cpu ?? '?'} · ${gb(c.memoryMb)}` : c.diskGb != null ? disk(c.diskGb) : '—' },
          { key: 'ip', label: 'IP', render: (c) => <span className="mono">{c.privateIp ?? '—'}</span> },
          { key: 'a', label: 'Device', render: (c) => c.assetId ? <Link to={`/assets/${c.assetId}`}>inventory</Link> : <span className="muted">—</span> },
        ]} />
      </Panel>}
      {props.length > 0 && <Panel title="Details from the platform">
        <KV items={props.map(([k, v]) => [PROP_LABEL[k] ?? k, propValue(v)] as [string, ReactNode])} />
      </Panel>}
      <Panel title="Change history" flush>
        <DataTable rows={changes} empty="No changes recorded." columns={[
          { key: 'at', label: 'When', render: (c) => fmt.date(c.at) },
          { key: 'c', label: 'Change', className: 'wrap', render: (c) => <ChangeText c={{ ...c, kind: r.kind }} /> },
        ]} />
      </Panel>
      {linking && <LinkDialog resourceId={r.id} name={r.name ?? r.externalId} current={asset ? { id: asset.id, name: asset.hostname ?? asset.ip ?? asset.id } : null}
        onClose={() => setLinking(false)} onDone={(m) => { setLinking(false); setMsg(m); setTick((x) => x + 1); }} />}
    </>
  );
}
