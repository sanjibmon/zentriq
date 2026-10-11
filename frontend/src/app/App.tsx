import { DashboardPage } from '../features/dashboard/DashboardPage';
import { AssetsPage } from '../features/assets/AssetsPage';
import { DiscoveryPage } from '../features/discovery/DiscoveryPage';
import { LicensingPage } from '../features/licensing/LicensingPage';
import { UsersPage } from '../features/users/UsersPage';
import { AuditPage } from '../features/audit/AuditPage';
import { SettingsPage } from '../features/settings/SettingsPage';
import { ModulePage } from '../features/placeholder/ModulePage';
import { LoginPage } from '../features/login/LoginPage';
import { Shell } from '../components/Shell';
import { LogoMark } from '../components/Logo';
import { AuthProvider, useAuth } from './auth';
import { Router, useRouter } from './router';

function Routes() {
  const { path } = useRouter();
  if (path === '/' || path === '/dashboard') return <DashboardPage />;
  if (path === '/assets') return <AssetsPage />;
  if (path === '/discovery') return <DiscoveryPage />;
  if (path === '/licensing') return <LicensingPage />;
  if (path === '/users') return <UsersPage />;
  if (path === '/audit') return <AuditPage />;
  if (path === '/settings') return <SettingsPage />;
  if (path.startsWith('/modules/')) return <ModulePage id={path.slice('/modules/'.length)} />;
  return <ModulePage id="" />;
}

function Gate() {
  const { me, checking } = useAuth();
  if (checking)
    return (
      <div className="boot" aria-busy="true">
        <LogoMark size={48} />
      </div>
    );
  if (!me) return <LoginPage />;
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
