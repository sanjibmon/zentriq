import { DashboardPage } from '../features/dashboard/DashboardPage';
import { AssetsPage } from '../features/assets/AssetsPage';
import { DiscoveryPage } from '../features/discovery/DiscoveryPage';
import { LicensingPage } from '../features/licensing/LicensingPage';
import { UsersPage } from '../features/users/UsersPage';
import { AuditPage } from '../features/audit/AuditPage';
import { SettingsPage } from '../features/settings/SettingsPage';
import { AgentsPage } from '../features/agents/AgentsPage';
import { SoftwarePage } from '../features/inventory/SoftwarePage';
import { HardwarePage } from '../features/inventory/HardwarePage';
import { NetworkPage } from '../features/network/NetworkPage';
import { NonItPage } from '../features/nonit/NonItPage';
import { AssetDetailPage } from '../features/inventory/AssetDetailPage';
import { ModulePage } from '../features/placeholder/ModulePage';
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

function Routes() {
  const { path } = useRouter();
  if (path === '/' || path === '/dashboard') return <DashboardPage />;
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
  if (path === '/network') return <NetworkPage />;
  if (path === '/non-it') return <NonItPage />;
  if (path === '/agents') return <AgentsPage />;
  if (path === '/activity') return <ActivityPage />;
  if (path === '/discovery') return <DiscoveryPage />;
  if (path === '/licensing') return <LicensingPage />;
  if (path === '/users') return <UsersPage />;
  if (path === '/audit') return <AuditPage />;
  if (path === '/settings') return <SettingsPage />;
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
