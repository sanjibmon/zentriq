import { ALL_NAV } from '../../app/nav';
import { Icon } from '../../components/Icon';
import { PageHead, Panel } from '../../components/ui';
import { Link } from '../../app/router';
import { useAuth } from '../../app/auth';
import type { SectionInfo } from '../../api/types';

/** Honest status page for modules that are part of the product plan but not implemented in this release. */
export function ModulePage({ id }: { id: string }) {
  const item = ALL_NAV.find((n) => n.id === id);
  return (
    <>
      <PageHead title={item?.label ?? 'Not found'} />
      <div className="module-card">
        <Panel>
          <div style={{ display: 'flex', gap: 14, alignItems: 'flex-start' }}>
            <Icon name={item?.icon ?? 'info'} size={28} />
            <div>
              <h3 style={{ margin: '2px 0 6px' }}>{item ? 'Coming in a later release' : 'This page does not exist'}</h3>
              <p className="muted" style={{ margin: 0 }}>
                {item
                  ? `${item.label} is part of the Zentriq platform roadmap but is not available in this version. Your license already includes it; it will appear here automatically after an upgrade.`
                  : 'Check the address, or use the navigation on the left.'}
              </p>
            </div>
          </div>
        </Panel>
      </div>
    </>
  );
}

/** A page of a section that is not installed on this server or not included in the license. */
export function SectionLockedPage({ section, label }: { section: SectionInfo; label: string }) {
  const { can } = useAuth();
  const notInstalled = !section.installed;
  return (
    <>
      <PageHead title={label} />
      <div className="module-card">
        <Panel>
          <div style={{ display: 'flex', gap: 14, alignItems: 'flex-start' }}>
            <span className="ico-tile xl" style={{ margin: 0 }}><Icon name="lock" size={28} /></span>
            <div>
              <h3 style={{ margin: '2px 0 6px' }}>{notInstalled ? `${section.title} is not installed` : `${section.title} needs the Advanced edition`}</h3>
              <p className="muted" style={{ margin: '0 0 10px' }}>
                {notInstalled
                  ? `This server was installed with the Standard installation, which does not include ${section.title}. Run Zentriq Setup on the server, choose Update › Advanced installation and tick ${section.title}. Your data and settings are kept.`
                  : `Your license is for the Standard edition (Overview, Discover & inventory, Insight & admin). ${section.title} is part of the Advanced edition. Contact your Zentriq partner to upgrade; the new license is imported on the Licensing page and the section is available at once.`}
              </p>
              {!notInstalled && can('licensing.manage') && <Link className="btn primary" to="/licensing">Open Licensing</Link>}
            </div>
          </div>
        </Panel>
      </div>
    </>
  );
}
