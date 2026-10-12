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
import { AssetDetailPage } from '../features/inventory/AssetDetailPage';
import { ModulePage } from '../features/placeholder/ModulePage';
import { LoginPage } from '../features/login/LoginPage';
import { ResetPasswordPage } from '../features/login/ResetPasswordPage';
import { AccountPage, MfaRequiredPage } from '../features/account/AccountPage';
import { Shell } from '../components/Shell';
import { LogoMark } from '../components/Logo';
import { AuthProvider, useAuth } from './auth';
import { Router, useRouter } from './router';

function Routes() {
  const { path } = useRouter();
  if (path === '/' || path === '/dashboard') return <DashboardPage />;
  if (path === '/assets') return <AssetsPage />;
  const assetMatch = /^\/assets\/([0-9a-f-]{36})$/i.exec(path);
  if (assetMatch) return <AssetDetailPage id={assetMatch[1]!} />;
  if (path === '/software') return <SoftwarePage />;
  if (path === '/hardware') return <HardwarePage />;
  if (path === '/agents') return <AgentsPage />;
  if (path === '/discovery') return <DiscoveryPage />;
  if (path === '/licensing') return <LicensingPage />;
  if (path === '/users') return <UsersPage />;
  if (path === '/audit') return <AuditPage />;
  if (path === '/settings') return <SettingsPage />;
  if (path === '/account') return <AccountPage />;
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
        <Gate />
      </AuthProvider>
    </Router>
  );
}
