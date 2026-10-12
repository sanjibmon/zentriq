import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { Asset, AssetInventory } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
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

type Tab = 'overview' | 'hardware' | 'software' | 'network' | 'updates' | 'security' | 'services';

export function AssetDetailPage({ id }: { id: string }) {
  const asset = useApi(() => api.get<Asset>(`/assets/${id}`), [id]);
  const inv = useApi(() => api.get<AssetInventory>(`/assets/${id}/inventory`), [id]);
  const [tab, setTab] = useState<Tab>('overview');
  const [filter, setFilter] = useState('');
  const a = asset.data;
  const d = inv.data?.document ?? null;
  const sw = inv.data?.software ?? [];

  if (asset.error) return <ErrorBox error={asset.error} />;
  if (!a) return <Loading />;
  const tabs: [Tab, string, number?][] = [
    ['overview', 'Overview'], ['hardware', 'Hardware'], ['software', 'Software', sw.length], ['network', 'Network', d?.network.length],
    ['updates', 'Updates', d?.patches.length], ['security', 'Security'], ['services', 'Services', d?.services.length],
  ];
  const f = filter.trim().toLowerCase();

  return (
    <>
      <PageHead title={a.hostname ?? a.ip ?? 'Asset'} desc={<>{fmt.label(a.category)} · {a.osName ?? fmt.label(a.osFamily)} · <Pill tone={statusTone(a.status)}>{fmt.label(a.status)}</Pill></>}
        actions={<Link className="btn" to="/assets">All assets</Link>} />
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
              ['Hostname', a.hostname], ['FQDN', d?.device.fqdn ?? a.fqdn], ['Domain / workgroup', d?.device.domain], ['IP address', <span className="mono">{a.ip ?? '—'}</span>],
              ['Manufacturer', d?.device.manufacturer ?? inv.data?.hardware?.vendor], ['Model', d?.device.model ?? inv.data?.hardware?.model],
              ['Serial number', <span className="mono">{d?.device.serialNumber ?? a.serialNumber ?? '—'}</span>], ['Type', fmt.label(d?.device.chassis ?? a.category)],
              ['Logged-on user', d?.users.loggedOn], ['Owner', a.ownerName], ['Criticality', a.criticality],
            ]} />
          </Panel>
          <Panel title="Operating system & collection">
            <KV items={[
              ['Operating system', (d?.os.name as string | null) ?? a.osName], ['Version', [d?.os.version, d?.os.build && `build ${d.os.build}`].filter(Boolean).join(' ') || null],
              ['Architecture', d?.os.architecture as string | null], ['Installed', fmt.day(d?.os.installDate as string | null)], ['Last boot', fmt.date(d?.os.lastBoot as string | null)],
              ['Inventory source', inv.data?.source ? fmt.label(inv.data.source) : null], ['Collected', fmt.date(inv.data?.collectedAt)],
              ['Agent', inv.data?.agent ? <><Pill tone={inv.data.agent.status === 'active' ? 'ok' : 'crit'}>{fmt.label(inv.data.agent.status)}</Pill> {inv.data.agent.version} · checked in {fmt.ago(inv.data.agent.lastCheckinAt)}</> : 'Not installed'],
              ['First seen', fmt.date(a.firstSeenAt)], ['Last seen', fmt.date(a.lastSeenAt)],
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

      {tab === 'services' && (
        <Panel flush right={<input className="in" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter services" />} title="Services">
          <DataTable rows={(d?.services ?? []).filter((s) => !f || s.name.toLowerCase().includes(f) || (s.displayName ?? '').toLowerCase().includes(f)).map((x, i) => ({ ...x, id: i }))} empty="No services collected." columns={[
            { key: 'n', label: 'Service', className: 'wrap', render: (x) => <><b>{x.displayName ?? x.name}</b><div className="faint mono" style={{ fontSize: 11 }}>{x.name}</div></> },
            { key: 's', label: 'State', render: (x) => <Pill tone={x.state === 'Running' || x.state === 'running' ? 'ok' : ''}>{x.state ?? '—'}</Pill> },
            { key: 'm', label: 'Start mode', render: (x) => x.startMode ?? '—' },
          ]} />
        </Panel>
      )}
    </>
  );
}
