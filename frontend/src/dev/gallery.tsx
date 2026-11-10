// Screenshot gallery (CI only, never shipped): renders the shell and the add / edit windows one at a time with the API
// answered by the screenshot script, so the layout can be checked on real pixels. Built with vite.gallery.config.ts.
import { StrictMode, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { AuthProvider, useAuth } from '../app/auth';
import { Router } from '../app/router';
import { ConfirmProvider } from '../components/Confirm';
import { Shell } from '../components/Shell';
import { PageHead, Panel } from '../components/ui';
import { AddDeviceDialog } from '../features/assets/DeviceManagement';
import { ItemDialog } from '../features/accessories/AccessoryDialogs';
import { StockDialog } from '../features/lifecycle/LifecycleDialogs';
import { DeployDialog as PatchDeployDialog, PolicyDialog } from '../features/patch/PatchPage';
import { ScheduleDialog, ScriptDialog } from '../features/scripts/ScriptsPage';
import { LicenseDialog, ProductDialog } from '../features/licenses/LicenseDialogs';
import { AppDialog } from '../features/webapps/WebAppsPage';
import { DecideDialog, DeployDialog, GroupDialog, PackageDialog, RuleDialog } from '../features/deployment/DeploymentDialogs';
import { OfferDialog } from '../features/deployment/SelfServiceTab';
import { AssetForm } from '../features/nonit/NonItDialogs';
import { ExperiencePage } from '../features/experience/ExperiencePage';
import { DeviceExperience } from '../features/experience/DeviceExperience';
import { UsageTab } from '../features/licenses/UsageTab';
import { PrivilegePage } from '../features/securitypack/PrivilegePage';
import { BitLockerPage } from '../features/securitypack/BitLockerPage';
import { UsbPage } from '../features/securitypack/UsbPage';
import '../styles/zentriq.css';
import '../styles/app.css';
import '../styles/modern.css';

const no = () => undefined;
const meta = {
  currency: 'INR', tags: { mode: 'auto', example: 'IT-00001' }, stages: [
    { id: 'planned', label: 'Planned', statuses: [{ id: 'requested', label: 'Requested' }] },
    { id: 'on_order', label: 'On Order', statuses: [{ id: 'ordered', label: 'Ordered' }] },
    { id: 'in_stock', label: 'In Stock', statuses: [{ id: 'new', label: 'New' }] },
  ],
} as never;

const DIALOGS: Record<string, () => ReactNode> = {
  'add-device': () => <AddDeviceDialog onClose={no} onDone={no} />,
  'add-accessory': () => <ItemDialog item={null} categories={['Keyboards', 'Mice', 'Headsets']} onClose={no} onSaved={no} />,
  'add-it-assets': () => <StockDialog meta={meta} onClose={no} onDone={no} />,
  'patch-policy': () => <PolicyDialog policy={null} onClose={no} onSaved={no} />,
  'patch-deploy': () => <PatchDeployDialog preset={{ allMissing: true, classifications: ['critical', 'security'] }} onClose={no} onDone={no} />,
  'new-script': () => <ScriptDialog script={null} onClose={no} onSaved={no} />,
  'new-schedule': () => <ScheduleDialog schedule={null} scripts={[]} onClose={no} onSaved={no} />,
  'license-product': () => <ProductDialog product={null} isNew onClose={no} onSaved={no} />,
  'license': () => <LicenseDialog license={null} products={[{ id: 'p1', name: 'Microsoft 365 E3' }]} onClose={no} onSaved={no} />,
  'web-app': () => <AppDialog app={null} onClose={no} onSaved={no} />,
  'deploy-rule': () => <RuleDialog rule={null} groups={[]} onClose={no} onSaved={no} />,
  'deploy-package': () => <PackageDialog pkg={null} onClose={no} onSaved={no} />,
  'deploy-group': () => <GroupDialog group={null} onClose={no} onSaved={no} />,
  'deploy-job': () => <DeployDialog pkg={{ id: 'p', name: '7-Zip', version: '24.08', platform: 'windows' } as never} groups={[]} onClose={no} onDone={no} />,
  'deploy-offer': () => <OfferDialog offer={{ id: 'p', name: '7-Zip', version: '24.08', publisher: 'Igor Pavlov', platform: 'windows', size: 1600000, enabled: false, approval: false, description: null, category: null, groupIds: [], installs: 0, pending: 0 }} groups={[{ id: 'g', name: 'Finance laptops', platform: null } as never]} onClose={no} onSaved={no} />,
  'deploy-decide': () => <DecideDialog title="Approve Software" item={{ name: 'Notepad++', version: '8.6', publisher: 'Notepad++ Team', platform: 'windows', device: 'PC-41' } as never} groups={[]} endpoint="/x" onClose={no} onDone={no} />,
  'non-it-asset': () => <AssetForm asset={null} categories={[]} groups={[]} departments={[]} fields={[]} onClose={no} onSaved={no} onListsChanged={no} />,
};

/** Whole pages, with sample data answered by the screenshot script. */
const PAGES: Record<string, () => ReactNode> = {
  'experience': () => <ExperiencePage />,
  'device-experience': () => <><PageHead title="LT-0042" /><DeviceExperience assetId="00000000-0000-0000-0000-000000000042" /></>,
  'license-usage': () => <><PageHead title="Software Licenses" /><UsageTab canManage /></>,
  'privilege': () => <PrivilegePage />,
  'bitlocker': () => <BitLockerPage />,
  'usb': () => <UsbPage />,
};

function View() {
  const { me } = useAuth();
  const d = new URLSearchParams(window.location.search).get('d') ?? 'shell';
  if (!me) return <p>Signing in…</p>;
  const page = <><PageHead title="IT Asset Inventory" /><Panel title="Devices"><p className="muted">Gallery page.</p></Panel></>;
  return <Shell>{PAGES[d]?.() ?? page}{DIALOGS[d]?.()}</Shell>;
}

export const GALLERY = Object.keys(DIALOGS);

createRoot(document.getElementById('root')!).render(
  <StrictMode><Router><AuthProvider><ConfirmProvider><View /></ConfirmProvider></AuthProvider></Router></StrictMode>,
);
