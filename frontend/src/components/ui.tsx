import type { CSSProperties, MouseEvent as ReactMouseEvent, ReactNode } from 'react';
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

/** Short words that stay lowercase inside a title ("Add a Cloud or Virtualization Connection"). */
const MINOR = new Set(['a', 'an', 'and', 'or', 'nor', 'but', 'the', 'of', 'to', 'in', 'on', 'for', 'with', 'by', 'at', 'as', 'from', 'per', 'vs', 'via']);

/** Title Case for labels in tiles, tabs, panel headers, dialog titles and column headers: words start with a capital
 *  ("Found on devices" → "Found on Devices"), short words (a, and, or, of, to…) stay lowercase except at the start or
 *  after a colon or dash; acronyms (WDAC, IPv4), units (24 h), file names and e-mail addresses stay as they are. */
export function titleCase(text: string): string {
  return text.replace(/[A-Za-z][A-Za-z'’]*/g, (w, at: number) => {
    if (/[A-Z]/.test(w.slice(1))) return w;                       // WDAC, IPv4, macOS
    if (w.length === 1 && w !== 'a') return w;                       // units: 24 h, 7 d
    const prev = text.slice(0, at);
    if (/[-.@/]$/.test(prev) || /\d$/.test(prev)) return w;           // up-to-date, e.g., file.exe, 10x
    const startOfPhrase = prev.trim() === '' || /[:—–(·|]\s*$/.test(prev);
    if (!startOfPhrase && MINOR.has(w.toLowerCase())) return w.toLowerCase();
    return w[0].toUpperCase() + w.slice(1);
  });
}
export const tc = (x: ReactNode) => (typeof x === 'string' ? titleCase(x) : x);

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
    case 'succeeded': case 'success': case 'completed': case 'active': case 'trial': case 'licensed': case 'healthy': case 'ok': case 'free':
      return 'ok';
    case 'running': case 'queued': case 'cancelling': case 'partial': case 'licensedgrace':
      return 'info';
    case 'failed': case 'dead': case 'denied': case 'failure': case 'trialexpired': case 'licenseexpired': case 'clockrollback': case 'disabled': case 'freeoverlimit': case 'leaseexpired': case 'supportexpired':
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
          <h3>{tc(title)}</h3>
          {sub && <span className="sub">{sub}</span>}
          {right && <div className="right">{right}</div>}
        </div>
      )}
      <div className={flush ? '' : 'panel-b'}>{children}</div>
    </section>
  );
}

export function Kpis({ items }: { items: { label: string; value: ReactNode; sub?: ReactNode; to?: string }[] }) {
  // Balanced rows: up to 6 tiles in one row, more split evenly (7-8 → 4 a row), never one tile alone on a row.
  const n = items.length;
  const cols = n <= 6 ? n : Math.ceil(n / Math.ceil(n / 6));
  return (
    <div className="kpis" style={{ '--kpi-cols': cols } as CSSProperties}>
      {items.map((k) => {
        const body = (
          <>
            <div className="l">{titleCase(k.label)}</div>
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
  /** Column chooser: not shown until the viewer adds it. */
  optional?: boolean;
  /** Column chooser: always shown (selection boxes, actions). */
  fixed?: boolean;
  /** Name in the column chooser when the label is not plain text. */
  title?: string;
}

/** A click on the row itself (not on a link, button or box inside it). */
const rowClick = <T,>(onRow: ((row: T) => void) | undefined, row: T) => onRow && ((e: ReactMouseEvent) => {
  if ((e.target as HTMLElement).closest('a, button, input, select, textarea, label')) return;
  onRow(row);
});

export function DataTable<T extends { id: string | number }>({ columns, rows, empty = 'No records.', onRow, rowTitle }: {
  columns: Column<T>[]; rows: T[]; empty?: string;
  /** Makes rows clickable (e.g. to open the details). */
  onRow?: (row: T) => void; rowTitle?: string;
}) {
  return (
    <div className="tbl-wrap">
      <table className="t">
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.key}>{tc(c.label)}</th>
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
              <tr key={r.id} className={onRow ? 'clickable' : undefined} title={onRow ? rowTitle : undefined} onClick={rowClick(onRow, r)}
                tabIndex={onRow ? 0 : undefined} onKeyDown={onRow ? (e) => { if (e.key === 'Enter' && e.target === e.currentTarget) onRow(r); } : undefined}>
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

/**
 * Page navigation. Lists show it above and below the table; the one above (<c>top</c>) appears only when there is more than
 * one page.
 */
export function Pager({ page, pageSize, total, onPage, top }: { page: number; pageSize: number; total: number; onPage: (p: number) => void; top?: boolean }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (top && pages <= 1) return null;
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <div className={`pager${top ? ' pager-top' : ''}`} role="navigation" aria-label={top ? 'Pages (top)' : 'Pages'}>
      <span className="muted">
        {from.toLocaleString()}–{to.toLocaleString()} of {total.toLocaleString()}{pages > 1 && <> · page {page} of {pages.toLocaleString()}</>}
      </span>
      {pages > 2 && <button className="btn sm" disabled={page <= 1} onClick={() => onPage(1)} aria-label="First page">First</button>}
      <button className="btn sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Previous
      </button>
      <button className="btn sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>
        Next
      </button>
      {pages > 2 && <button className="btn sm" disabled={page >= pages} onClick={() => onPage(pages)} aria-label="Last page">Last</button>}
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
