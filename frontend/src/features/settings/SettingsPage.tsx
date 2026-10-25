import { api } from '../../api/client';
import type { SystemInfo } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Link } from '../../app/router';
import { Icon } from '../../components/Icon';
import { CompanyProfile } from './CompanyProfile';
import { CompanyLogoPanel } from './CompanyLogoPanel';
import { SecuritySettings } from './SecuritySettings';
import { DataTable, ErrorBox, fmt, Loading, PageHead, Panel, Pill } from '../../components/ui';

export function SettingsPage() {
  const { can } = useAuth();
  const info = useApi(() => api.get<SystemInfo>('/system/info'), []);
  const i = info.data;

  return (
    <>
      <PageHead title="System Settings"
        actions={can('backup.run') && <Link className="btn" to="/backup"><Icon name="archive" size={14} /> Backup &amp; Restore</Link>} />
      <CompanyProfile />
      <CompanyLogoPanel canEdit={can('settings.manage')} />
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
