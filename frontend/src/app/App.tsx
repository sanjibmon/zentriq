import { useEffect } from 'react';
import { DashboardPage } from '../features/dashboard/DashboardPage';
import { AssetsPage } from '../features/assets/AssetsPage';
import { DiscoveryPage } from '../features/discovery/DiscoveryPage';
import { LicensingPage } from '../features/licensing/LicensingPage';
import { UsersPage } from '../features/users/UsersPage';
import { AuditPage } from '../features/audit/AuditPage';
import { SettingsPage } from '../features/settings/SettingsPage';
import { BackupPage } from '../features/backup/BackupPage';
import { AgentsPage } from '../features/agents/AgentsPage';
import { SoftwarePage } from '../features/inventory/SoftwarePage';
import { HardwarePage } from '../features/inventory/HardwarePage';
import { NetworkPage } from '../features/network/NetworkPage';
import { NonItPage } from '../features/nonit/NonItPage';
import { AssetDetailPage } from '../features/inventory/AssetDetailPage';
import { AccessoriesPage } from '../features/accessories/AccessoriesPage';
import { LifecycleAssetPage, LifecyclePage } from '../features/lifecycle/LifecyclePage';
import { HandoverPage, LabelsPage } from '../features/lifecycle/PrintPages';
import { CloudPage } from '../features/cloud/CloudPage';
import { CloudResourcePage } from '../features/cloud/CloudResourcePage';
import { ModulePage, SectionLockedPage } from '../features/placeholder/ModulePage';
import { ALL_NAV, navOfPath } from './nav';
import { ReportsPage } from '../features/reports/ReportsPage';
import { ReportBuilder } from '../features/reports/ReportBuilder';
import { VulnerabilitiesPage } from '../features/vulnerabilities/VulnerabilitiesPage';
import { SecurityPage } from '../features/security/SecurityPage';
import { IntegrationsPage } from '../features/integrations/IntegrationsPage';
import { RemotePage } from '../features/remote/RemotePage';
import { ActivityPage } from '../features/activity/ActivityPage';
import { LoginPage } from '../features/login/LoginPage';
import { ResetPasswordPage } from '../features/login/ResetPasswordPage';
import { AccountPage, MfaRequiredPage } from '../features/account/AccountPage';
import { Shell } from '../components/Shell';
import { LogoMark } from '../components/Logo';
import { AuthProvider, useAuth } from './auth';
import { Router, useRouter } from './router';
import { ConfirmProvider } from '../components/Confirm';

/** The start page for users without the dashboard: the first area they may open. */
function FirstAllowed() {
  const { can } = useAuth();
  const { navigate } = useRouter();
  const first = ALL_NAV.find((i) => i.ready && i.path !== '/' && (!i.permission || can(i.permission)));
  useEffect(() => { if (first) navigate(first.path, true); }, [first, navigate]);
  return first ? null : <NoAccess />;
}

function NoAccess() {
  return <div className="empty-state" style={{ padding: 40 }}><h2>No access</h2><p className="muted">Your role does not include this page. Ask an administrator if you need it.</p></div>;
}

function Routes() {
  const { path } = useRouter();
  const { me, can } = useAuth();
  // Sections not installed on this server, or not in the license, show what is needed instead of the page.
  const nav = navOfPath(path);
  const sec = nav && !nav.item.admin ? me?.license.sections?.find((x) => x.id === nav.section) : undefined;
  if (sec && (!sec.installed || (me!.license.featuresEnabled && !sec.enabled))) return <SectionLockedPage section={sec} label={nav!.item.label} />;
  if (path === '/' || path === '/dashboard') return can('dashboard.view') ? <DashboardPage /> : <FirstAllowed />;
  // Pages outside the user's roles (opened by address or an old link).
  if (nav?.item.permission && !can(nav.item.permission)) return <NoAccess />;
  if (path === '/assets') return <AssetsPage />;
  const assetMatch = /^\/assets\/([0-9a-f-]{36})$/i.exec(path);
  if (assetMatch) return <AssetDetailPage id={assetMatch[1]!} />;
  // Devices opened from Hardware / Software Inventory stay in that section.
  const hwMatch = /^\/hardware\/([0-9a-f-]{36})$/i.exec(path);
  if (hwMatch) return <AssetDetailPage id={hwMatch[1]!} section={{ path: '/hardware', label: 'All hardware', tab: 'hardware' }} />;
  const swMatch = /^\/software\/devices\/([0-9a-f-]{36})$/i.exec(path);
  if (swMatch) return <AssetDetailPage id={swMatch[1]!} section={{ path: '/software', label: 'All software', tab: 'software' }} />;
  if (path === '/software') return <SoftwarePage />;
  if (path === '/hardware') return <HardwarePage />;
  if (path === '/accessories') return <AccessoriesPage />;
  if (path === '/lifecycle' || path === '/modules/lifecycle') return <LifecyclePage />;
  if (path === '/lifecycle/labels') return <LabelsPage />;
  if (path === '/lifecycle/handover') return <HandoverPage />;
  const lcMatch = /^\/lifecycle\/assets\/([0-9a-f-]{36})$/i.exec(path);
  if (lcMatch) return <LifecycleAssetPage id={lcMatch[1]!} />;
  if (path === '/cloud' || path === '/modules/cloud') return <CloudPage />;
  const cloudMatch = /^\/cloud\/resources\/([0-9a-f-]{36})$/i.exec(path);
  if (cloudMatch) return <CloudResourcePage key={cloudMatch[1]} id={cloudMatch[1]!} />;
  if (path === '/network') return <NetworkPage />;
  if (path === '/non-it') return <NonItPage />;
  if (path === '/agents') return <AgentsPage />;
  if (path === '/activity') return <ActivityPage />;
  if (path === '/discovery') return <DiscoveryPage />;
  if (path === '/licensing') return <LicensingPage />;
  if (path === '/users') return <UsersPage />;
  if (path === '/audit') return <AuditPage />;
  if (path === '/settings') return <SettingsPage />;
  if (path === '/backup') return <BackupPage />;
  if (path === '/account') return <AccountPage />;
  if (path === '/reports' || path === '/modules/reports') return <ReportsPage />;
  if (path === '/reports/new') return <ReportBuilder id={null} />;
  const reportMatch = /^\/reports\/([0-9a-f-]{36})$/i.exec(path);
  if (reportMatch) return <ReportBuilder key={reportMatch[1]} id={reportMatch[1]!} />;
  if (path === '/vulnerabilities' || path === '/modules/vuln') return <VulnerabilitiesPage />;
  if (path === '/security' || path === '/modules/config') return <SecurityPage />;
  if (path === '/integrations' || path === '/modules/integrations') return <IntegrationsPage />;
  if (path === '/remote' || path === '/modules/remote') return <RemotePage />;
  if (path.startsWith('/modules/')) return <ModulePage id={path.slice('/modules/'.length)} />;
  return <ModulePage id="" />;
}

function Gate() {
  const { me, checking } = useAuth();
  const { path } = useRouter();
  if (path === '/reset-password') return <ResetPasswordPage />;
  if (checking)
    return (
      <div className="boot" aria-busy="true">
        <LogoMark size={48} />
      </div>
    );
  if (!me) return <LoginPage />;
  if (me.mfaEnrollmentRequired) return <MfaRequiredPage />;
  return (
    <Shell>
      <Routes />
    </Shell>
  );
}

export function App() {
  return (
    <Router>
      <AuthProvider>
        <ConfirmProvider>
          <Gate />
        </ConfirmProvider>
      </AuthProvider>
    </Router>
  );
}
