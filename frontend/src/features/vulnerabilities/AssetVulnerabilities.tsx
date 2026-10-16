import { useState } from 'react';
import { api } from '../../api/client';
import type { Paged } from '../../api/types';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, Panel, Pill } from '../../components/ui';
import { cap, pct, prioTone, PRIORITY_HELP, sevTone, STATUS_LABEL, statusTone, type Finding, cvssText } from './types';
import { CveDialog } from './VulnDialogs';
import { RiskBar } from './VulnerabilitiesPage';

/** Open vulnerabilities on one device (asset detail tab). */
export function AssetVulnerabilities({ assetId }: { assetId: string }) {
  const data = useApi(() => api.get<Paged<Finding>>('/vulnerabilities/findings', { assetId, status: 'active', pageSize: 200 }), [assetId]);
  const [cve, setCve] = useState<string | null>(null);
  const now = Date.now();
  return (
    <>
      <ErrorBox error={data.error} />
      <Panel flush title="Open vulnerabilities" sub={data.data ? `${data.data.total} open or in progress · Windows updates and installed software` : ''}
        right={<Link className="btn sm" to={`/vulnerabilities?tab=findings&assetId=${assetId}&status=all`}>All findings for this device</Link>}>
        <DataTable rows={data.data?.items ?? []} empty={data.loading ? 'Loading…' : 'No known vulnerabilities in Windows or the installed software.'} columns={[
          { key: 'p', label: 'Priority', render: (f) => <span title={PRIORITY_HELP[f.priority]}><Pill tone={prioTone(f.priority)}>{f.priority}</Pill></span> },
          { key: 'r', label: 'Risk', render: (f) => <RiskBar v={f.risk} /> },
          { key: 'c', label: 'CVE', className: 'wrap', render: (f) => <><button className="linkish mono" onClick={() => setCve(f.cve)}>{f.cve}</button> {f.exploited && <Pill tone="crit">{f.kev ? 'KEV' : 'Exploited'}</Pill>}{f.title && <div className="faint clamp-2" style={{ fontSize: 11.5, maxWidth: 320 }}>{f.title}</div>}</> },
          { key: 's', label: 'CVSS', render: (f) => <Pill tone={sevTone(f.severity)}>{cvssText(f.cvss)} {cap(f.severity)}</Pill> },
          { key: 'e', label: 'EPSS', render: (f) => pct(f.epss) },
          { key: 'sw', label: 'Software / Windows', className: 'wrap', render: (f) => <>{f.software} <span className="mono faint">{f.version}</span></> },
          { key: 'fx', label: 'Fix', className: 'wrap', render: (f) => f.source === 'os' ? <>{f.kb ? <b>KB{f.kb}</b> : 'Windows update'}<div className="faint mono" style={{ fontSize: 11.5 }}>build {f.fixedVersion}</div></> : f.fixedVersion ?? '—' },
          { key: 'st', label: 'Status', className: 'wrap', render: (f) => <><Pill tone={statusTone(f.status)}>{STATUS_LABEL[f.status] ?? f.status}</Pill>{f.assignee && <div className="faint" style={{ fontSize: 11.5 }}>{f.assignee}</div>}</> },
          { key: 'd', label: 'Due', render: (f) => <span style={{ color: f.dueAt && new Date(f.dueAt).getTime() < now ? 'var(--crit)' : undefined }}>{fmt.day(f.dueAt)}</span> },
        ]} />
      </Panel>
      {cve && <CveDialog cve={cve} onClose={() => setCve(null)} />}
    </>
  );
}
