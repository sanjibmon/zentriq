import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useAuth } from '../app/auth';
import { GROUP_ICON, NAV, SECTION_OF_GROUP } from '../app/nav';
import { Link, useRouter } from '../app/router';
import { Icon } from './Icon';
import { LogoMark, PRODUCT } from './Logo';
import { CompanyLogo } from './CompanyLogo';
import { api } from '../api/client';

function initials(name: string) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((x) => x[0]!.toUpperCase()).join('') || 'U';
}

export function Shell({ children }: { children: ReactNode }) {
  const { me, logout, can } = useAuth();
  const { path, navigate } = useRouter();
  const [collapsed, setCollapsed] = useState(false);
  const [menu, setMenu] = useState(false);
  // One module open at a time: the module of the current page opens by itself; clicking another module closes this one.
  const groupOf = (p: string) => NAV.find(([, items]) => items.some((i) => (i.path === '/' ? p === '/' : p === i.path || p.startsWith(i.path + '/'))))?.[0] ?? null;
  const [open, setOpen] = useState<string | null>(() => groupOf(path));
  const routeGroup = groupOf(path);
  useEffect(() => { if (routeGroup) setOpen(routeGroup); }, [routeGroup]);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const close = (e: MouseEvent) => menuRef.current && !menuRef.current.contains(e.target as Node) && setMenu(false);
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, []);

  if (!me) return null;
  const lic = me.license;
  const active = (p: string) => (p === '/' ? path === '/' : path === p || path.startsWith(p + '/'));

  return (
    <div className={`app${collapsed ? ' collapsed' : ''}`}>
      <aside className="rail">
        <div className="brand">
          <LogoMark className="brand-mark" />
          <div className="brand-name">
            {PRODUCT.name}
            <small>Asset Intelligence</small>
          </div>
        </div>
        <nav className="nav" aria-label="Primary">
          {NAV.map(([group, items]) => {
            const sec = lic.sections?.find((x) => x.id === SECTION_OF_GROUP[group]);
            if (sec && !sec.installed && !items.some((i) => i.admin)) return null;   // not installed on this server
            const locked = !!sec && lic.featuresEnabled && !sec.enabled;
            const shown = items.filter((i) => (!i.permission || can(i.permission)) && (i.admin || !sec || sec.installed));
            if (shown.length === 0) return null;
            const addOn = (i: (typeof shown)[number]) => !!i.module && lic.featuresEnabled && !lic.modules.includes('*') && !lic.modules.includes(i.module);
            const groupAddOn = shown.length > 0 && shown.every(addOn);
            const link = (i: (typeof shown)[number], mod = false) => {
              const off = (locked && !i.admin) || addOn(i);
              return (
                <Link key={i.id} to={i.path} className={`${mod ? 'nav-mod ' : ''}${active(i.path) ? 'active' : ''}${off ? ' locked' : ''}`} title={addOn(i) ? `${i.label} — a separately licensed add-on` : locked && !i.admin ? `${i.label} — requires the Advanced edition` : i.label}>
                  <Icon name={mod ? GROUP_ICON[group] ?? i.icon : i.icon} />
                  <span>{i.label}</span>
                  {off && <Icon name="lock" size={12} className="nav-lock-ico" />}
                </Link>
              );
            };
            // A module with one page is a link of its own; the menu collapsed to icons shows every page.
            if (shown.length === 1 && !collapsed) return <div key={group} className="nav-sec">{link(shown[0]!, true)}</div>;
            const isOpen = collapsed || open === group;
            const here = shown.some((i) => active(i.path));
            return (
              <div key={group} className={`nav-sec${isOpen ? ' open' : ''}`}>
                {!collapsed && <button type="button" className={`nav-head${here ? ' here' : ''}`} aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : group)}>
                  <Icon name={GROUP_ICON[group] ?? 'apps'} />
                  <span>{group}</span>
                  {locked ? <span className="nav-lock" title="Not included in your license">Advanced</span> : groupAddOn && <span className="nav-lock" title="A separately licensed add-on">Add-on</span>}
                  <Icon name="chev" size={14} className="nav-chev" />
                </button>}
                {isOpen && <div className="nav-items">{shown.map((i) => link(i))}</div>}
              </div>
            );
          })}
        </nav>
        <div className="rail-foot">
          <Icon name="lock" size={14} />
          <span>Self-hosted</span>
        </div>
      </aside>

      <div className="main-col">
        <header className="topbar">
          <button className="icon-btn" onClick={() => setCollapsed((c) => !c)} title="Collapse navigation" aria-label="Toggle navigation">
            <Icon name="menu" />
          </button>
          <CompanyLogo className="top-logo" height={30} />
          <span className="top-spacer" />
          <LicenseBadge mode={lic.mode} days={lic.daysRemaining} onClick={() => navigate('/licensing')} />
          <Bell onOpen={(link) => link && navigate(link)} />
          <div className="user-menu" ref={menuRef}>
            <button className="avatar" onClick={() => setMenu((m) => !m)} title={me.displayName} aria-haspopup="menu" aria-expanded={menu}>
              {initials(me.displayName)}
            </button>
            {menu && (
              <div className="popover" role="menu" style={{ right: 0 }}>
                <div style={{ padding: '8px 10px' }}>
                  <b>{me.displayName}</b>
                  <div className="muted" style={{ fontSize: 12.5 }}>{me.email}</div>
                  <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>{me.roles.map((r) => r.name).join(', ')}</div>
                </div>
                <button className="pop-item" role="menuitem" onClick={() => { setMenu(false); navigate('/account'); }}>
                  <Icon name="lock" /> My account &amp; MFA{!me.mfaEnabled && <span className="muted" style={{ marginLeft: 6, fontSize: 11.5 }}>(off)</span>}
                </button>
                <button className="pop-item" role="menuitem" onClick={() => { setMenu(false); void logout(); }}>
                  <Icon name="x" /> Sign out
                </button>
              </div>
            )}
          </div>
        </header>
        {!lic.featuresEnabled && (
          <div className="license-strip" role="alert">
            <Icon name="alert" size={14} /> {lic.summary} Your data stays available; backup, restore and license import keep working.{' '}
            <Link to="/licensing">Manage license</Link>
          </div>
        )}
        <main className="page">{children}</main>
      </div>
    </div>
  );
}

function LicenseBadge({ mode, days, onClick }: { mode: string; days: number | null; onClick: () => void }) {
  const text =
    mode === 'Trial' ? `Trial · ${days ?? 0} day${days === 1 ? '' : 's'} left`
    : mode === 'Licensed' ? 'Licensed'
    : mode === 'LicensedGrace' ? `License grace · ${days ?? 0} d`
    : mode === 'TrialExpired' ? 'Trial expired'
    : mode === 'LicenseExpired' ? 'License expired'
    : mode === 'ClockRollback' ? 'Clock problem'
    : mode === 'Free' ? 'Free edition'
    : mode === 'FreeOverLimit' ? 'Over free limits'
    : mode === 'LeaseExpired' ? 'License not refreshed'
    : mode === 'SupportExpired' ? 'Support expired'
    : mode;
  const tone = mode === 'Licensed' ? 'ok' : mode === 'Free' ? 'info' : mode === 'Trial' && (days ?? 0) > 3 ? 'info' : mode === 'Trial' || mode === 'LicensedGrace' ? 'warn' : 'crit';
  return (
    <button className={`health-pill lic-${tone}`} onClick={onClick} title="Licensing">
      <i />
      <span>{text}</span>
    </button>
  );
}

interface Note { id: number; kind: 'info' | 'ok' | 'warn' | 'crit'; title: string; body: string; link: string | null; createdAt: string; read: boolean }

/** Notifications (licence, web app, backup and agent alerts) with the unread count. */
function Bell({ onOpen }: { onOpen: (link: string | null) => void }) {
  const [notes, setNotes] = useState<Note[]>([]);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    let alive = true;
    const load = () => { api.get<Note[]>('/system/notifications').then((n) => { if (alive) setNotes(n); }).catch(() => { /* not signed in or offline */ }); };
    load();
    const t = setInterval(load, 60000);
    const close = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false);
    document.addEventListener('mousedown', close);
    return () => { alive = false; clearInterval(t); document.removeEventListener('mousedown', close); };
  }, []);
  const unread = notes.filter((n) => !n.read).length;
  async function markRead() {
    try { await api.post('/system/notifications/read'); setNotes((n) => n.map((x) => ({ ...x, read: true }))); } catch { /* ignore */ }
  }
  return (
    <div className="user-menu" ref={ref}>
      <button className="icon-btn bell" onClick={() => setOpen((o) => !o)} title="Notifications" aria-label={`Notifications${unread ? ` (${unread} unread)` : ''}`} aria-haspopup="menu" aria-expanded={open}>
        <Icon name="bell" />
        {unread > 0 && <span className="bell-count">{unread > 99 ? '99+' : unread}</span>}
      </button>
      {open && (
        <div className="popover notes" role="menu" style={{ right: 0 }}>
          <div className="notes-h"><b>Notifications</b>{unread > 0 && <button className="link-btn" onClick={() => void markRead()}>Mark All as Read</button>}</div>
          {notes.length === 0 ? <div className="faint" style={{ padding: '10px 12px' }}>Nothing new.</div> : (
            <div className="notes-list">
              {notes.map((n) => (
                <button key={n.id} className={`note ${n.kind}${n.read ? '' : ' unread'}`} role="menuitem" onClick={() => { setOpen(false); onOpen(n.link); }}>
                  <i />
                  <span><b>{n.title}</b>{n.body && <span className="faint">{n.body}</span>}<small className="faint">{new Date(n.createdAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</small></span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
