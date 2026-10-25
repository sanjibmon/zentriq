import { useState } from 'react';
import { api } from '../../api/client';
import type { UserRow } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Callout, DataTable, ErrorBox, fmt, PageHead, Panel, Pill } from '../../components/ui';
import { useConfirm } from '../../components/Confirm';
import { useAuth } from '../../app/auth';
import { useRouter } from '../../app/router';
import { Icon } from '../../components/Icon';
import { UserDialog } from './UserDialogs';
import { GroupDialog, RoleDialog, type GroupRow, type RoleRow } from './RoleDialogs';
import { describe } from './access';
import { errorText } from '../discovery/DirectoryPanel';

type Tab = 'users' | 'roles' | 'groups';

export function UsersPage() {
  const { query, navigate } = useRouter();
  const tab: Tab = query.get('tab') === 'roles' ? 'roles' : query.get('tab') === 'groups' ? 'groups' : 'users';
  const users = useApi(() => api.get<UserRow[]>('/users'), []);
  const roles = useApi(() => api.get<RoleRow[]>('/roles'), []);
  const groups = useApi(() => api.get<GroupRow[]>('/groups'), []);
  const { me } = useAuth();
  const confirm = useConfirm();
  const [edit, setEdit] = useState<UserRow | 'new' | null>(null);
  const [roleEdit, setRoleEdit] = useState<{ role: RoleRow | null; copy?: boolean } | null>(null);
  const [groupEdit, setGroupEdit] = useState<GroupRow | 'new' | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const reloadAll = () => { users.reload(); roles.reload(); groups.reload(); };

  async function resetMfa(u: UserRow) {
    if (!(await confirm({ title: 'Reset two-step verification?', danger: true, confirmLabel: 'Reset MFA', message: <>{u.displayName} ({u.email}) will sign in with the password only and must set up the authenticator app again. Do this only after confirming the person's identity.</> }))) return;
    try { await api.post(`/users/${u.id}/mfa/reset`); setMsg({ tone: 'ok', text: `MFA reset for ${u.email}.` }); users.reload(); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }
  async function deleteRole(r: RoleRow) {
    if (!(await confirm({ title: `Delete the role "${r.name}"?`, danger: true, confirmLabel: 'Delete', message: 'The role must not be given to any user or group.' }))) return;
    try { await api.del(`/roles/${r.id}`); setMsg({ tone: 'ok', text: `Role "${r.name}" deleted.` }); roles.reload(); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }
  async function deleteGroup(g: GroupRow) {
    if (!(await confirm({ title: `Delete the group "${g.name}"?`, danger: true, confirmLabel: 'Delete', message: `Its ${g.members.length} member(s) lose the group's roles; their own roles stay.` }))) return;
    try { await api.del(`/groups/${g.id}`); setMsg({ tone: 'ok', text: `Group "${g.name}" deleted.` }); reloadAll(); } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); }
  }

  return (
    <>
      <PageHead title="Users, Roles & Groups"
        actions={tab === 'users' ? <button className="btn primary" onClick={() => setEdit('new')}><Icon name="plus" size={14} /> Add user</button>
          : tab === 'roles' ? <button className="btn primary" onClick={() => setRoleEdit({ role: null })}><Icon name="plus" size={14} /> New role</button>
          : <button className="btn primary" onClick={() => setGroupEdit('new')}><Icon name="plus" size={14} /> New group</button>} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="tabs" role="tablist">
        {([['users', 'Users'], ['roles', 'Roles'], ['groups', 'Groups']] as [Tab, string][]).map(([k, l]) => (
          <button key={k} role="tab" aria-selected={tab === k} className={tab === k ? 'on' : ''} onClick={() => navigate(k === 'users' ? '/users' : `/users?tab=${k}`, true)}>{l}</button>
        ))}
      </div>
      {tab === 'users' && <>
        <ErrorBox error={users.error} />
        <Panel flush>
          <DataTable rows={users.data ?? []} empty={users.loading ? 'Loading…' : 'No users.'} columns={[
            { key: 'n', label: 'Name', render: (u) => <><b>{u.displayName}</b><div className="faint" style={{ fontSize: 11.5 }}>{u.email}</div></> },
            { key: 'r', label: 'Roles', className: 'wrap', render: (u) => <>
              {u.roles.map((r) => r.name).join(', ') || <span className="muted">—</span>}
              {(u.groups ?? []).length > 0 && <div className="faint" style={{ fontSize: 11.5 }}>via {(u.groups ?? []).map((g) => `${g.name}${g.roles.length ? ` (${g.roles.join(', ')})` : ''}`).join('; ')}</div>}
            </> },
            { key: 'm', label: 'MFA', render: (u) => <Pill tone={u.mfaEnabled ? 'ok' : 'warn'}>{u.mfaEnabled ? 'Enrolled' : 'Not enrolled'}</Pill> },
            { key: 's', label: 'Status', render: (u) => <Pill tone={u.locked ? 'crit' : u.status === 'active' ? 'ok' : ''}>{u.locked ? 'Locked' : fmt.label(u.status)}</Pill> },
            { key: 'l', label: 'Last sign-in', render: (u) => fmt.ago(u.lastLoginAt) },
            { key: 'x', label: '', render: (u) => <div className="row-actions">
              <button className="btn sm" onClick={() => setEdit(u)}>Edit</button>
              {u.mfaEnabled && u.id !== me?.id && <button className="btn sm" onClick={() => void resetMfa(u)}>Reset MFA</button>}
            </div> },
          ]} />
        </Panel>
      </>}
      {tab === 'roles' && <>
        <ErrorBox error={roles.error} />
        <Panel flush sub="A role decides which areas a user sees and what they may change. Give users one or more roles, directly or through a group.">
          <DataTable rows={roles.data ?? []} empty={roles.loading ? 'Loading…' : 'No roles.'} columns={[
            { key: 'n', label: 'Role', className: 'wrap', render: (r) => <><b>{r.name}</b> {r.builtIn ? <Pill tone="info">Built-in</Pill> : <Pill>Custom</Pill>}<div className="faint" style={{ fontSize: 11.5 }}>{r.description}</div></> },
            { key: 'p', label: 'Access', className: 'wrap', render: (r) => r.id === 'admin' ? <span>Everything</span> : <ul className="access-list">{describe(r.permissions).map((d) => <li key={d}>{d}</li>)}</ul> },
            { key: 'u', label: 'Given to', render: (r) => <span className="faint">{r.users} user(s){r.groups ? ` · ${r.groups} group(s)` : ''}</span> },
            { key: 'a', label: '', render: (r) => <div className="row-actions">
              <button className="btn sm" onClick={() => setRoleEdit({ role: r })}>{r.builtIn ? 'View' : 'Edit'}</button>
              <button className="btn sm" onClick={() => setRoleEdit({ role: r, copy: true })}>Duplicate</button>
              {!r.builtIn && <button className="btn sm danger" onClick={() => void deleteRole(r)} aria-label={`Delete ${r.name}`}><Icon name="trash" size={13} /></button>}
            </div> },
          ]} />
        </Panel>
      </>}
      {tab === 'groups' && <>
        <ErrorBox error={groups.error} />
        <Panel flush sub="Members of a group get its roles in addition to their own — for example a Facilities team with the Non-IT Asset Manager role.">
          <DataTable rows={groups.data ?? []} empty={groups.loading ? 'Loading…' : 'No groups yet.'} columns={[
            { key: 'n', label: 'Group', className: 'wrap', render: (g) => <><b>{g.name}</b><div className="faint" style={{ fontSize: 11.5 }}>{g.description}</div></> },
            { key: 'r', label: 'Roles', className: 'wrap', render: (g) => g.roles.map((r) => r.name).join(', ') || <span className="muted">—</span> },
            { key: 'm', label: 'Members', className: 'wrap', render: (g) => g.members.length === 0 ? <span className="muted">—</span>
              : <span title={g.members.map((m) => m.email).join(', ')}>{g.members.slice(0, 4).map((m) => m.displayName).join(', ')}{g.members.length > 4 ? ` +${g.members.length - 4}` : ''}</span> },
            { key: 'a', label: '', render: (g) => <div className="row-actions">
              <button className="btn sm" onClick={() => setGroupEdit(g)}>Edit</button>
              <button className="btn sm danger" onClick={() => void deleteGroup(g)} aria-label={`Delete ${g.name}`}><Icon name="trash" size={13} /></button>
            </div> },
          ]} />
        </Panel>
      </>}
      {edit && <UserDialog existing={edit === 'new' ? null : edit} roles={roles.data ?? []} groups={groups.data ?? []}
        onClose={() => setEdit(null)} onSaved={() => { setEdit(null); reloadAll(); setMsg({ tone: 'ok', text: 'User saved.' }); }} />}
      {roleEdit && <RoleDialog role={roleEdit.role} copy={roleEdit.copy} onClose={() => setRoleEdit(null)} onSaved={(t) => { setRoleEdit(null); reloadAll(); setMsg({ tone: 'ok', text: t }); }} />}
      {groupEdit && <GroupDialog group={groupEdit === 'new' ? null : groupEdit} roles={roles.data ?? []} users={users.data ?? []}
        onClose={() => setGroupEdit(null)} onSaved={(t) => { setGroupEdit(null); reloadAll(); setMsg({ tone: 'ok', text: t }); }} />}
    </>
  );
}
