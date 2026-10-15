import { useEffect, useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { Asset, AssetInventory } from '../../api/types';
import { Link } from '../../app/router';
import { useAuth } from '../../app/auth';
import { Availability, typeLabel } from '../assets/AssetsPage';
import { ServiceActionDialog, type ControlInfo, type ServiceActionRow } from './ServiceControl';
import { useApi } from '../../app/useApi';
import { AssetVulnerabilities } from '../vulnerabilities/AssetVulnerabilities';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill, statusTone } from '../../components/ui';

const yesNo = (b: boolean | null | undefined) => (b == null ? '—' : b ? 'Yes' : 'No');

function KV({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="kv-grid">
      {items.map(([k, v]) => (
        <div key={k}><dt>{k}</dt><dd>{v ?? '—'}</dd></div>
      ))}
    </dl>
  );
}

type Tab = 'overview' | 'hardware' | 'software' | 'network' | 'updates' | 'security' | 'services' | 'vulnerabilities';

export function AssetDetailPage({ id }: { id: string }) {
  const asset = useApi(() => api.get<Asset>(`/assets/${id}`), [id]);
  const inv = useApi(() => api.get<AssetInventory>(`/assets/${id}/inventory`), [id]);
  const [tab, setTab] = useState<Tab>('overview');
  const [filter, setFilter] = useState('');
  const { can } = useAuth();
  const control = useApi(() => (can('assets.control') ? api.get<ControlInfo>(`/assets/${id}/control`) : Promise.resolve(null)), [id]);
  const actions = useApi(() => api.get<ServiceActionRow[]>(`/assets/${id}/actions`), [id]);
  const [pending, setPending] = useState<{ name: string; display: string; op: string } | null>(null);
  const [note, setNote] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const open = (actions.data ?? []).some((x) => ['queued', 'sent', 'running'].includes(x.status));
  useEffect(() => {
    if (!open) return;
    const t = setInterval(() => { actions.reload(); inv.reload(); }, 5000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reload() only bumps a counter
  }, [open]);
  async function collectNow() {
    setNote(null);
    try { setNote({ tone: 'info', text: (await api.post<{ message: string }>('/assets/collect', { assetIds: [id] })).message }); }
    catch (e) { setNote({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }
  const a = asset.data;
  const d = inv.data?.document ?? null;
  const sw = inv.data?.software ?? [];

  if (asset.error) return <ErrorBox error={asset.error} />;
  if (!a) return <Loading />;
  const tabs: [Tab, string, number?][] = [
    ['overview', 'Overview'], ['hardware', 'Hardware'], ['software', 'Software', sw.length], ['network', 'Network', d?.network.length],
    ['updates', 'Updates', d?.patches.length], ['security', 'Security'], ['services', 'Services', d?.services.length],
    ...(can('vuln.view') ? [['vulnerabilities', 'Vulnerabilities'] as [Tab, string]] : []),
  ];
  const f = filter.trim().toLowerCase();

  return (
    <>
      <PageHead title={a.hostname ?? a.ip ?? 'Asset'} desc={<>{typeLabel({ ...a, chassis: d?.device.chassis ?? a.chassis, isVirtual: d?.hardware.isVirtual ?? a.isVirtual })} · {a.osName ?? fmt.label(a.osFamily)} · <Pill tone={statusTone(a.status)}>{fmt.label(a.status)}</Pill> · <Availability a={a} long /></>}
        actions={<>
          {can('scans.run') && <button className="btn primary" onClick={() => void collectNow()}>Collect inventory now</button>}
          <Link className="btn" to="/assets">All assets</Link>
        </>} />
      {note && <Callout tone={note.tone} icon={note.tone === 'crit' ? 'alert' : 'info'}>{note.text}</Callout>}
      <ErrorBox error={inv.error} />
      {!inv.data?.document && !inv.loading && (
        <Callout>No detailed inventory yet. Install the Zentriq Agent on this device or scan it with a Windows, SSH or SNMP credential.</Callout>
      )}
      <div className="tabs" role="tablist">
        {tabs.map(([t, label, n]) => (
          <button key={t} className={tab === t ? 'on' : ''} onClick={() => { setTab(t); setFilter(''); }}>{label}{n != null && <span className="cnt">{n}</span>}</button>
        ))}
      </div>

      {tab === 'overview' && (
        <div className="grid-2">
          <Panel title="Device">
            <KV items={[
              ['Hostname', a.hostname], ['FQDN', d?.device.fqdn ?? a.fqdn], ['Domain / workgroup', d?.device.domain], ['IPv4 address', <span className="mono">{(a.ipv4 && a.ipv4.length ? a.ipv4 : a.ip ? [a.ip] : []).join(', ') || '—'}</span>],
              ...(a.ipv6 ? [['IPv6 address', <span className="mono">{a.ipv6.join(', ') || '—'}</span>] as [string, ReactNode]] : []),
              ['Manufacturer', d?.device.manufacturer ?? inv.data?.hardware?.vendor], ['Model', d?.device.model ?? inv.data?.hardware?.model],
              ['Serial number', <span className="mono">{d?.device.serialNumber ?? a.serialNumber ?? '—'}</span>], ['Type', typeLabel({ ...a, chassis: d?.device.chassis ?? a.chassis, isVirtual: d?.hardware.isVirtual ?? a.isVirtual })],
              ['Logged-on user', d?.users.loggedOn], ['Owner', a.ownerName], ['Criticality', a.criticality],
            ]} />
          </Panel>
          <Panel title="Operating system & collection">
            <KV items={[
              ['Operating system', (d?.os.name as string | null) ?? a.osName], ['Version', [d?.os.version, d?.os.build && `build ${d.os.build}`].filter(Boolean).join(' ') || null],
              ['Architecture', d?.os.architecture as string | null], ['Installed', fmt.day(d?.os.installDate as string | null)], ['Last boot', fmt.date(d?.os.lastBoot as string | null)],
              ['Inventory source', inv.data?.source ? fmt.label(inv.data.source) : null], ['Collected', fmt.date(inv.data?.collectedAt)],
              ['Agent', inv.data?.agent ? <><Pill tone={inv.data.agent.status === 'active' ? 'ok' : 'crit'}>{fmt.label(inv.data.agent.status)}</Pill> {inv.data.agent.version} · checked in {fmt.ago(inv.data.agent.lastCheckinAt)}</> : 'Not installed'],
              ['First seen', fmt.date(a.firstSeenAt)], ['Last contact', <><Availability a={a} long /> <span className="muted">{fmt.date(a.lastContactAt ?? a.lastSeenAt)}</span></>],
            ]} />
            {d && d.errors.length > 0 && <Callout tone="warn" icon="alert">Some sections could not be collected: {d.errors.join('; ')}</Callout>}
          </Panel>
          {inv.data && inv.data.identifiers.length > 0 && (
            <Panel title="Identifiers" sub="Used to merge records from agents and scans" flush>
              <DataTable rows={inv.data.identifiers.map((x, i) => ({ ...x, id: i }))} columns={[
                { key: 'k', label: 'Kind', render: (x) => fmt.label(x.kind) },
                { key: 'v', label: 'Value', render: (x) => <span className="mono">{x.value}</span> },
              ]} />
            </Panel>
          )}
        </div>
      )}

      {tab === 'hardware' && (
        <>
          <Panel title="System">
            <KV items={[
              ['Processor', d?.hardware.cpuName ?? inv.data?.hardware?.cpu], ['Cores / threads', [d?.hardware.cpuCores ?? inv.data?.hardware?.cpuCores, d?.hardware.cpuThreads].filter((x) => x != null).join(' / ') || null],
              ['Memory', d?.hardware.memoryBytes ? fmt.bytes(d.hardware.memoryBytes) : inv.data?.hardware?.ramGb != null ? `${inv.data.hardware.ramGb} GB` : null],
              ['BIOS', [d?.hardware.biosVendor, d?.hardware.biosVersion].filter(Boolean).join(' ') || null], ['BIOS date', fmt.day(d?.hardware.biosDate)],
              ['SMBIOS UUID', <span className="mono">{d?.device.smbiosUuid ?? inv.data?.hardware?.smbiosUuid ?? '—'}</span>],
              ['TPM', d?.hardware.tpmPresent == null ? null : d.hardware.tpmPresent ? `Present ${d.hardware.tpmVersion ?? ''}` : 'Not present'], ['Virtual machine', yesNo(d?.hardware.isVirtual)],
            ]} />
          </Panel>
          <div className="grid-2">
            <Panel title="Disks" flush>
              <DataTable rows={(d?.hardware.disks ?? []).map((x, i) => ({ ...x, id: i }))} empty="No disk data." columns={[
                { key: 'm', label: 'Model', className: 'wrap', render: (x) => x.model ?? '—' },
                { key: 's', label: 'Size', render: (x) => (x.sizeBytes ? fmt.bytes(x.sizeBytes) : '—') },
                { key: 't', label: 'Type', render: (x) => [x.mediaType, x.interface].filter(Boolean).join(' · ') || '—' },
                { key: 'n', label: 'Serial', render: (x) => <span className="mono">{x.serial ?? '—'}</span> },
              ]} />
            </Panel>
            <Panel title="Volumes" flush>
              <DataTable rows={(d?.hardware.volumes ?? []).map((x, i) => ({ ...x, id: i }))} empty="No volume data." columns={[
                { key: 'n', label: 'Volume', render: (x) => x.name ?? '—' },
                { key: 'f', label: 'File system', render: (x) => x.fileSystem ?? '—' },
                { key: 's', label: 'Size', render: (x) => (x.sizeBytes ? fmt.bytes(x.sizeBytes) : '—') },
                { key: 'u', label: 'Free', render: (x) => (x.freeBytes != null && x.sizeBytes ? `${fmt.bytes(x.freeBytes)} (${Math.round((x.freeBytes / x.sizeBytes) * 100)}%)` : '—') },
                { key: 'e', label: 'Encrypted', render: (x) => yesNo(x.encrypted) },
              ]} />
            </Panel>
          </div>
        </>
      )}

      {tab === 'software' && (
        <Panel flush title={`${sw.length} installed applications`} right={<input className="in" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter software" />}>
          <DataTable rows={sw.filter((s) => !f || s.name.toLowerCase().includes(f) || (s.publisher ?? '').toLowerCase().includes(f)).map((s, i) => ({ ...s, id: i }))} empty="No software collected." columns={[
            { key: 'n', label: 'Name', className: 'wrap', render: (s) => <b>{s.name}</b> },
            { key: 'v', label: 'Version', render: (s) => s.version ?? '—' },
            { key: 'p', label: 'Publisher', className: 'wrap', render: (s) => s.publisher ?? '—' },
            { key: 'd', label: 'Installed', render: (s) => fmt.day(s.installedOn) },
            { key: 'a', label: 'Arch', render: (s) => s.architecture ?? '—' },
          ]} />
        </Panel>
      )}

      {tab === 'network' && (
        <Panel flush>
          <DataTable rows={(d?.network ?? []).map((x, i) => ({ ...x, id: i }))} empty="No network data." columns={[
            { key: 'n', label: 'Adapter', className: 'wrap', render: (x) => x.name ?? '—' },
            { key: 'm', label: 'MAC', render: (x) => <span className="mono">{x.mac ?? '—'}</span> },
            { key: '4', label: 'IPv4', render: (x) => <span className="mono">{x.ipv4.join(', ') || '—'}</span> },
            ...(a.ipv6 ? [{ key: '6', label: 'IPv6', render: (x: { ipv6: string[] }) => <span className="mono">{x.ipv6.join(', ') || '—'}</span> }] : []),
            { key: 'g', label: 'Gateway', render: (x) => <span className="mono">{x.gateway ?? '—'}</span> },
            { key: 'd', label: 'DHCP', render: (x) => yesNo(x.dhcp) },
            { key: 's', label: 'Speed', render: (x) => (x.speedMbps ? `${x.speedMbps.toLocaleString()} Mbps` : '—') },
          ]} />
        </Panel>
      )}

      {tab === 'updates' && (
        <Panel flush>
          <DataTable rows={(d?.patches ?? []).map((x, i) => ({ ...x, id: i }))} empty="No update history collected." columns={[
            { key: 'i', label: 'Update', render: (x) => <span className="mono">{x.id}</span> },
            { key: 'd', label: 'Description', render: (x) => x.description ?? '—' },
            { key: 'o', label: 'Installed', render: (x) => fmt.day(x.installedOn) },
          ]} />
        </Panel>
      )}

      {tab === 'security' && (
        <div className="grid-2">
          <Panel title="Protection">
            <KV items={[
              ['Firewall enabled', yesNo(d?.security.firewallEnabled)], ['Disk encryption', yesNo(d?.security.diskEncryption)], ['Secure Boot', yesNo(d?.security.secureBoot)],
              ['TPM', yesNo(d?.hardware.tpmPresent)],
            ]} />
          </Panel>
          <Panel title="Antivirus" flush>
            <DataTable rows={(d?.security.antivirus ?? []).map((x, i) => ({ ...x, id: i }))} empty="No antivirus reported (Windows Server has no Security Center; Defender status is shown when available)." columns={[
              { key: 'n', label: 'Product', render: (x) => x.name },
              { key: 'e', label: 'Enabled', render: (x) => yesNo(x.enabled) },
              { key: 'u', label: 'Up to date', render: (x) => yesNo(x.upToDate) },
            ]} />
          </Panel>
          <Panel title="Local administrators" flush>
            <DataTable rows={(d?.users.localAdmins ?? []).map((x, i) => ({ name: x, id: i }))} empty="Not collected." columns={[{ key: 'n', label: 'Member', render: (x) => <span className="mono">{x.name}</span> }]} />
          </Panel>
        </div>
      )}

      {tab === 'vulnerabilities' && <AssetVulnerabilities assetId={id} />}

      {tab === 'services' && (
        <>
          {control.data?.note && <Callout tone="warn" icon="alert">{control.data.note}</Callout>}
          {(() => {
            const last = (actions.data ?? [])[0];
            return last ? (
              <Callout icon={last.status === 'failed' ? 'alert' : 'info'} tone={last.status === 'failed' ? 'crit' : last.status === 'succeeded' ? 'ok' : 'info'}>
                Last action: <b>{fmt.label(last.operation)}</b> <span className="mono">{last.target}</span> — {fmt.label(last.status)}{last.detail ? `: ${last.detail}` : ''} ({fmt.ago(last.requestedAt)}).{' '}
                <Link to={`/activity?assetId=${id}`}>Service activity log</Link>
              </Callout>
            ) : null;
          })()}
          <Panel flush right={<input className="in" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter services" />} title="Services"
            sub={<>{control.data ? (control.data.channel === 'agent' ? 'Start, stop and restart run through the Zentriq Agent · ' : 'Start, stop and restart run over WinRM/WMI with an administrator account · ') : ''}<Link to={`/activity?assetId=${id}`}>Activity log</Link></>}>
            <DataTable rows={(d?.services ?? []).filter((s) => !f || s.name.toLowerCase().includes(f) || (s.displayName ?? '').toLowerCase().includes(f)).map((x, i) => ({ ...x, id: i }))} empty="No services collected." columns={[
              { key: 'n', label: 'Service', className: 'wrap', render: (x) => <><b>{x.displayName ?? x.name}</b><div className="faint mono" style={{ fontSize: 11 }}>{x.name}</div></> },
              { key: 's', label: 'State', render: (x) => <Pill tone={/^running$/i.test(x.state ?? '') ? 'ok' : /pending/i.test(x.state ?? '') ? 'warn' : ''}>{x.state ?? '—'}</Pill> },
              { key: 'm', label: 'Start mode', render: (x) => x.startMode ?? '—' },
              ...(control.data && a.osFamily !== 'linux' ? [{
                key: 'x', label: '', render: (x: { name: string; displayName?: string | null; state?: string | null; startMode?: string | null }) => {
                  const running = /^running$/i.test(x.state ?? '');
                  const prot = control.data!.protectedServices.some((p) => p.toLowerCase() === x.name.toLowerCase());
                  const disabled = /^disabled$/i.test(x.startMode ?? '');
                  const go = (op: string) => setPending({ name: x.name, display: x.displayName ?? x.name, op });
                  return (
                    <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
                      {!running && <button className="btn sm" disabled={disabled} title={disabled ? 'Disabled service' : undefined} onClick={() => go('start')}>Start</button>}
                      {running && <button className="btn sm" disabled={prot} title={prot ? 'Protected: manage it on the device' : undefined} onClick={() => go('restart')}>Restart</button>}
                      {running && <button className="btn sm danger" disabled={prot} title={prot ? 'Protected: manage it on the device' : undefined} onClick={() => go('stop')}>Stop</button>}
                    </div>
                  );
                },
              }] : []),
            ]} />
          </Panel>
        </>
      )}
      {pending && control.data && (
        <ServiceActionDialog assetId={id} host={a.hostname ?? a.ip ?? 'device'} service={pending.name} display={pending.display} operation={pending.op} info={control.data}
          onClose={() => setPending(null)} onDone={(m) => { setPending(null); setNote({ tone: 'info', text: m }); actions.reload(); }} />
      )}
    </>
  );
}
