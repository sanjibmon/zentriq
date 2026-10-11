import { ALL_NAV } from '../../app/nav';
import { Icon } from '../../components/Icon';
import { PageHead, Panel } from '../../components/ui';

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
