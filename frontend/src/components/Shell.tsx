import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useAuth } from '../app/auth';
import { NAV } from '../app/nav';
import { Link, useRouter } from '../app/router';
import { Icon } from './Icon';
import { LogoMark, PRODUCT } from './Logo';

function initials(name: string) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((x) => x[0]!.toUpperCase()).join('') || 'U';
}

export function Shell({ children }: { children: ReactNode }) {
  const { me, logout, can } = useAuth();
  const { path, navigate } = useRouter();
  const [collapsed, setCollapsed] = useState(false);
  const [menu, setMenu] = useState(false);
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
            <small>Web Console</small>
          </div>
        </div>
        <nav className="nav" aria-label="Primary">
          {NAV.map(([group, items]) => (
            <div key={group}>
              <div className="nav-group">{group}</div>
              {items
                .filter((i) => !i.permission || can(i.permission))
                .map((i) => (
                  <Link key={i.id} to={i.path} className={active(i.path) ? 'active' : ''} title={i.label}>
                    <Icon name={i.icon} />
                    <span>{i.label}</span>
                  </Link>
                ))}
            </div>
          ))}
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
          <span className="top-spacer" />
          <LicenseBadge mode={lic.mode} days={lic.daysRemaining} onClick={() => navigate('/licensing')} />
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
    : mode;
  const tone = mode === 'Licensed' ? 'ok' : mode === 'Trial' && (days ?? 0) > 3 ? 'info' : mode === 'Trial' || mode === 'LicensedGrace' ? 'warn' : 'crit';
  return (
    <button className={`health-pill lic-${tone}`} onClick={onClick} title="Licensing">
      <i />
      <span>{text}</span>
    </button>
  );
}
