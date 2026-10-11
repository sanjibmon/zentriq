import { useState } from 'react';
import { api } from '../../api/client';
import type { Role, UserRow } from '../../api/types';
import { useApi } from '../../app/useApi';
import { DataTable, ErrorBox, fmt, PageHead, Panel, Pill } from '../../components/ui';

export function UsersPage() {
  const [tab, setTab] = useState<'users' | 'roles'>('users');
  const users = useApi(() => api.get<UserRow[]>('/users'), []);
  const roles = useApi(() => api.get<Role[]>('/roles'), []);
  return (
    <>
      <PageHead title="Users, Roles & Sites" desc="Who can sign in to Zentriq and what each role may do. Permissions are enforced by the server for every request." />
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'users'} className={tab === 'users' ? 'on' : ''} onClick={() => setTab('users')}>Users</button>
        <button role="tab" aria-selected={tab === 'roles'} className={tab === 'roles' ? 'on' : ''} onClick={() => setTab('roles')}>Roles</button>
      </div>
      {tab === 'users' ? (
        <>
          <ErrorBox error={users.error} />
          <Panel flush>
            <DataTable rows={users.data ?? []} empty={users.loading ? 'Loading…' : 'No users.'} columns={[
              { key: 'n', label: 'Name', render: (u) => <><b>{u.displayName}</b><div className="faint" style={{ fontSize: 11.5 }}>{u.email}</div></> },
              { key: 'r', label: 'Roles', render: (u) => u.roles.map((r) => r.name).join(', ') },
              { key: 'm', label: 'MFA', render: (u) => <Pill tone={u.mfaEnabled ? 'ok' : 'warn'}>{u.mfaEnabled ? 'Enrolled' : 'Not enrolled'}</Pill> },
              { key: 's', label: 'Status', render: (u) => <Pill tone={u.locked ? 'crit' : u.status === 'active' ? 'ok' : ''}>{u.locked ? 'Locked' : fmt.label(u.status)}</Pill> },
              { key: 'l', label: 'Last sign-in', render: (u) => fmt.ago(u.lastLoginAt) },
            ]} />
          </Panel>
        </>
      ) : (
        <>
          <ErrorBox error={roles.error} />
          <Panel flush>
            <DataTable rows={roles.data ?? []} empty={roles.loading ? 'Loading…' : 'No roles.'} columns={[
              { key: 'n', label: 'Role', render: (r) => <><b>{r.name}</b>{r.builtIn && <span className="muted"> · built-in</span>}<div className="faint" style={{ fontSize: 11.5 }}>{r.description}</div></> },
              { key: 'p', label: 'Permissions', className: 'wrap', render: (r) => r.permissions.join(', ') },
            ]} />
          </Panel>
        </>
      )}
    </>
  );
}
