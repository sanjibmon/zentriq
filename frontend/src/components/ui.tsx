import type { ReactNode } from 'react';
import { ApiError } from '../api/client';
import { NAV, type NavItem } from '../app/nav';
import { Link, useRouter } from '../app/router';
import { Icon } from './Icon';
import type { IconName } from './icons';

/** The navigation entry (and its group) a path belongs to: the longest matching path prefix. */
function navFor(path: string): { group: string; item: NavItem } | null {
  let best: { group: string; item: NavItem } | null = null;
  for (const [group, items] of NAV)
    for (const item of items)
      if ((item.path === '/' ? path === '/' : path === item.path || path.startsWith(item.path + '/')) && (!best || item.path.length > best.item.path.length)) best = { group, item };
  return best;
}

export function PageHead({ title, desc, actions, crumbs = true, icon }: { title: string; desc?: ReactNode; actions?: ReactNode; crumbs?: boolean; icon?: IconName }) {
  const { path } = useRouter();
  const nav = navFor(path);
  const glyph = icon ?? nav?.item.icon;
  const section = nav && nav.item.label !== title ? nav.item : null;
  return (
    <>
      {crumbs && (
        <div className="crumbs">
          <Link to="/">Zentriq</Link>
          {nav && nav.group !== 'Overview' && <><span>›</span><span>{nav.group}</span></>}
          {section && <><span>›</span><Link to={section.path}>{section.label}</Link></>}
          <span>›</span>
          <span aria-current="page">{title}</span>
        </div>
      )}
      <div className="page-head">
        {glyph && <span className="page-icon" aria-hidden><Icon name={glyph} size={22} /></span>}
        <div className="page-title">
          <h1>{title}</h1>
          {desc && <p>{desc}</p>}
        </div>
        <div className="actions">{actions}</div>
      </div>
    </>
  );
}

export type Tone = 'ok' | 'warn' | 'crit' | 'info' | '';
export function Pill({ children, tone = '' }: { children: ReactNode; tone?: Tone }) {
  return (
    <span className={`pill ${tone}`}>
      <i />
      {children}
    </span>
  );
}

export function statusTone(s: string | null | undefined): Tone {
  switch ((s ?? '').toLowerCase()) {
    case 'succeeded': case 'success': case 'completed': case 'active': case 'trial': case 'licensed': case 'healthy': case 'ok':
      return 'ok';
    case 'running': case 'queued': case 'cancelling': case 'partial': case 'licensedgrace':
      return 'info';
    case 'failed': case 'dead': case 'denied': case 'failure': case 'trialexpired': case 'licenseexpired': case 'clockrollback': case 'disabled':
      return 'crit';
    case 'cancelled': case 'retired': case 'stale':
      return 'warn';
    default:
      return '';
  }
}

export function Panel({ title, sub, right, children, flush }: { title?: ReactNode; sub?: ReactNode; right?: ReactNode; children: ReactNode; flush?: boolean }) {
  return (
    <section className="panel">
      {title && (
        <div className="panel-h">
          <h3>{title}</h3>
          {sub && <span className="sub">{sub}</span>}
          {right && <div className="right">{right}</div>}
        </div>
      )}
      <div className={flush ? '' : 'panel-b'}>{children}</div>
    </section>
  );
}

export function Kpis({ items }: { items: { label: string; value: ReactNode; sub?: ReactNode; to?: string }[] }) {
  return (
    <div className="kpis">
      {items.map((k) => {
        const body = (
          <>
            <div className="l">{k.label}</div>
            <div className="v">{k.value}</div>
            {k.sub && <div className="s">{k.sub}</div>}
          </>
        );
        return k.to ? (
          <Link key={k.label} to={k.to} className="kpi">
            {body}
          </Link>
        ) : (
          <div key={k.label} className="kpi">
            {body}
          </div>
        );
      })}
    </div>
  );
}

export function Callout({ tone = 'info', icon = 'info', children }: { tone?: 'info' | 'warn' | 'crit' | 'ok'; icon?: IconName; children: ReactNode }) {
  return (
    <div className={`callout ${tone}`} role={tone === 'crit' ? 'alert' : undefined}>
      <Icon name={icon} />
      <div>{children}</div>
    </div>
  );
}

export function ErrorBox({ error }: { error: Error | null }) {
  if (!error) return null;
  const lic = error instanceof ApiError && error.status === 403;
  return (
    <Callout tone={lic ? 'warn' : 'crit'} icon={lic ? 'lock' : 'alert'}>
      {lic ? 'You do not have access to this page, or the feature is not covered by the current license. ' : ''}
      {error.message}
    </Callout>
  );
}

export function Loading({ what = 'Loading' }: { what?: string }) {
  return (
    <div className="empty" aria-busy="true">
      {what}…
    </div>
  );
}

export interface Column<T> {
  key: string;
  label: ReactNode;
  render: (row: T) => ReactNode;
  className?: string;
}

export function DataTable<T extends { id: string | number }>({ columns, rows, empty = 'No records.' }: { columns: Column<T>[]; rows: T[]; empty?: string }) {
  return (
    <div className="tbl-wrap">
      <table className="t">
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.key}>{c.label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="empty-cell">
                {empty}
              </td>
            </tr>
          ) : (
            rows.map((r) => (
              <tr key={r.id}>
                {columns.map((c) => (
                  <td key={c.key} className={c.className}>
                    {c.render(r)}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

export function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <div className="pager">
      <span className="muted">
        {from}–{to} of {total.toLocaleString()}
      </span>
      <button className="btn sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Previous
      </button>
      <button className="btn sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>
        Next
      </button>
    </div>
  );
}

const LABELS: Record<string, string> = {
  winrm: 'WinRM', ssh: 'SSH', snmp: 'SNMP', agent: 'Agent', smbios_uuid: 'SMBIOS UUID', mac: 'MAC address', agent_id: 'Agent ID',
  ad_object_guid: 'AD object GUID', hostname_fqdn: 'FQDN', ad: 'Active Directory', serial: 'Serial number', auth_failed: 'Credentials rejected',
  virtual: 'Virtual machine', not_collected: 'Not collected', network_device: 'Network device', virtual_host: 'Virtualization host',
};

export const fmt = {
  date: (s: string | null | undefined) => (s ? new Date(s).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—'),
  day: (s: string | null | undefined) => (s ? new Date(s).toLocaleDateString(undefined, { dateStyle: 'medium' }) : '—'),
  ago(s: string | null | undefined): string {
    if (!s) return 'never';
    const sec = Math.round((Date.now() - new Date(s).getTime()) / 1000);
    if (sec < 60) return 'just now';
    if (sec < 3600) return `${Math.round(sec / 60)} min ago`;
    if (sec < 86400) return `${Math.round(sec / 3600)} h ago`;
    return `${Math.round(sec / 86400)} d ago`;
  },
  bytes(n: number): string {
    const u = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
  },
  label: (s: string | null | undefined) => (s ? LABELS[s.toLowerCase()] ?? s.replace(/[_.]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()) : '—'),
};
