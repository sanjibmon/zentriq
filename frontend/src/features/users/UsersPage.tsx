import { useState } from 'react';
import { api } from '../../api/client';
import type { Role, UserRow } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Panel, Pill } from '../../components/ui';
import { useAuth } from '../../app/auth';
import { UserDialog } from './UserDialogs';
import { errorText } from '../discovery/DirectoryPanel';

export function UsersPage() {
  const [tab, setTab] = useState<'users' | 'roles'>('users');
  const users = useApi(() => api.get<UserRow[]>('/users'), []);
  const roles = useApi(() => api.get<Role[]>('/roles'), []);
  const { me } = useAuth();
  const [edit, setEdit] = useState<UserRow | 'new' | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);

  async function resetMfa(u: UserRow) {
    if (!window.confirm(`Reset two-step verification for ${u.displayName}? They can sign in with the password only and must set up the authenticator app again.`)) return;
    try { await api.post(`/users/${u.id}/mfa/reset`); setMsg({ tone: 'ok', text: `MFA reset for ${u.email}.` }); users.reload(); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }
  return (
    <>
      <PageHead title="Users, Roles & Sites" desc="Who can sign in to Zentriq and what each role may do. Permissions are enforced by the server for every request."
        actions={<button className="btn primary" onClick={() => setEdit('new')}>Add user</button>} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
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
              { key: 'x', label: '', render: (u) => <div style={{ display: 'flex', gap: 6 }}>
                <button className="btn sm" onClick={() => setEdit(u)}>Edit</button>
                {u.mfaEnabled && u.id !== me?.id && <button className="btn sm" onClick={() => void resetMfa(u)}>Reset MFA</button>}
              </div> },
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
      {edit && <UserDialog existing={edit === 'new' ? null : edit} roles={roles.data ?? []} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); users.reload(); setMsg({ tone: 'ok', text: 'User saved.' }); }} />}
    </>
  );
}
