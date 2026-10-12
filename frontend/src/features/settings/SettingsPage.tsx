import { useState } from 'react';
import { api } from '../../api/client';
import type { BackupFile, SystemInfo } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { CompanyProfile } from './CompanyProfile';
import { SecuritySettings } from './SecuritySettings';
import { Callout, DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill } from '../../components/ui';

export function SettingsPage() {
  const { can } = useAuth();
  const info = useApi(() => api.get<SystemInfo>('/system/info'), []);
  const backups = useApi(() => (can('backup.run') ? api.get<BackupFile[]>('/system/backups') : Promise.resolve(null)), []);
  const [msg, setMsg] = useState<string | null>(null);
  const i = info.data;

  async function backupNow() {
    await api.post('/system/backups');
    setMsg('Backup queued. The Zentriq maintenance task runs it within 5 minutes; it then appears in the list below.');
  }

  return (
    <>
      <PageHead title="System Settings" desc="Company profile, sign-in security, e-mail, installation, services, database and backups."
        actions={can('backup.run') && <button className="btn" onClick={() => void backupNow()}>Back up now</button>} />
      {msg && <Callout>{msg}</Callout>}
      <CompanyProfile />
      <SecuritySettings />
      <ErrorBox error={info.error} />
      {!i ? <Loading /> : (
        <div className="grid-2">
          <Panel title="Installation">
            <dl className="dl2">
              <dt>Version</dt><dd>{i.productVersion}</dd>
              <dt>Installation ID</dt><dd className="mono">{i.installationId}</dd>
              <dt>Initialized</dt><dd>{fmt.date(i.initializedAt)}</dd>
              <dt>Console URL</dt><dd>https://{i.https.hostName}{i.https.port === 443 ? '' : `:${i.https.port}`}/</dd>
              <dt>Database</dt><dd>PostgreSQL {i.postgresVersion} · {i.database.host}:{i.database.port}/{i.database.database}</dd>
              <dt>Schema</dt><dd>{i.schemaVersion} of {i.expectedSchemaVersion}</dd>
              <dt>Data folder</dt><dd className="mono">{i.paths.root}</dd>
            </dl>
          </Panel>
          <Panel title="Worker service" flush>
            <DataTable rows={i.workers.map((w) => ({ ...w, id: w.workerId }))} empty="No worker heartbeat recorded." columns={[
              { key: 'h', label: 'Worker', render: (w) => w.hostName },
              { key: 'v', label: 'Version', render: (w) => w.version },
              { key: 's', label: 'Health', render: (w) => <Pill tone={w.healthy ? 'ok' : 'crit'}>{w.healthy ? 'Healthy' : 'No heartbeat'}</Pill> },
              { key: 'b', label: 'Last heartbeat', render: (w) => fmt.ago(w.lastBeatAt) },
            ]} />
          </Panel>
        </div>
      )}
      {backups.data && (
        <Panel title="Backups" sub="Encrypted; restore with zentriqctl on the server" flush>
          <DataTable rows={backups.data.map((b) => ({ ...b, id: b.name }))} empty="No backups yet." columns={[
            { key: 'n', label: 'File', render: (b) => <span className="mono">{b.name}</span> },
            { key: 's', label: 'Size', render: (b) => fmt.bytes(b.sizeBytes) },
            { key: 'c', label: 'Created', render: (b) => fmt.date(b.createdAt) },
          ]} />
        </Panel>
      )}
      {i && (
        <Panel title="Installation history" flush>
          <DataTable rows={i.history.map((h, n) => ({ ...h, id: n }))} columns={[
            { key: 't', label: 'Time', render: (h) => fmt.date(h.occurredAt) },
            { key: 'a', label: 'Action', render: (h) => fmt.label(h.action) },
            { key: 'v', label: 'Version', render: (h) => h.toVersion ?? '' },
            { key: 'o', label: 'Outcome', render: (h) => <Pill tone={h.outcome === 'succeeded' ? 'ok' : 'crit'}>{fmt.label(h.outcome)}</Pill> },
          ]} />
        </Panel>
      )}
    </>
  );
}
