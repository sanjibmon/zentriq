import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { UserRow } from '../../api/types';
import { useAuth } from '../../app/auth';
import { Modal } from '../../components/Modal';
import { Callout } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';
import { AREAS } from './access';

export interface RoleRow { id: string; name: string; description: string; builtIn: boolean; permissions: string[]; users: number; groups: number; activeMembers: number }
export interface GroupRow {
  id: string; name: string; description: string; createdAt: string;
  roles: { id: string; name: string }[]; members: { id: string; displayName: string; email: string; status: string }[];
}

/** Create, edit or duplicate a role: what it can see and change, area by area. Built-in roles open read-only. */
export function RoleDialog({ role, copy, onClose, onSaved }: { role: RoleRow | null; copy?: boolean; onClose: () => void; onSaved: (msg: string) => void }) {
  const { can } = useAuth();
  const readOnly = !!role?.builtIn && !copy;
  const [name, setName] = useState(role ? (copy ? `${role.name} (copy)` : role.name) : '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [perms, setPerms] = useState<Set<string>>(new Set(role?.permissions ?? ['dashboard.view', 'reports.view']));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function toggle(code: string, on: boolean, area?: { view?: string; manage?: string }) {
    setPerms((p) => {
      const n = new Set(p);
      if (on) { n.add(code); if (area?.manage === code && area.view) n.add(area.view); }      // changing needs seeing
      else { n.delete(code); if (area?.view === code && area.manage) n.delete(area.manage); }
      return n;
    });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    const body = { name: name.trim(), description: description.trim(), permissions: [...perms] };
    try {
      if (role && !copy) await api.put(`/roles/${role.id}`, body); else await api.post('/roles', body);
      onSaved(`Role "${body.name}" saved. Users with it get the new rights at their next click.`);
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  const Box = ({ code, label, area }: { code?: string; label: string; area?: { view?: string; manage?: string } }) => !code ? <span className="faint">—</span> : (
    <label className="perm-box" title={can(code) || readOnly ? code : `${code} — you do not have this permission, so you cannot give it`}>
      <input type="checkbox" checked={perms.has(code)} disabled={readOnly || (!can(code) && !perms.has(code))} onChange={(e) => toggle(code, e.target.checked, area)} /> {label}
    </label>
  );

  return (
    <Modal title={readOnly ? role!.name : role && !copy ? `Edit role "${role.name}"` : copy ? `Duplicate "${role!.name}"` : 'New role'} wide onClose={onClose}
      footer={<>
        <button type="button" className="btn" onClick={onClose} disabled={busy}>{readOnly ? 'Close' : 'Cancel'}</button>
        {!readOnly && <button type="submit" form="role-form" className="btn primary" disabled={busy || !name.trim() || perms.size === 0}>{busy ? 'Saving…' : 'Save role'}</button>}
      </>}>
      <form id="role-form" className="form-v" onSubmit={submit}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {readOnly && <Callout icon="info">Built-in roles cannot be changed. Use <b>Duplicate</b> to make your own version.</Callout>}
        <div className="form-grid">
          <label>Name<input className="in" value={name} onChange={(e) => setName(e.target.value)} maxLength={80} disabled={readOnly} placeholder="e.g. Facilities — Non-IT assets" /></label>
          <label>Description<input className="in" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} disabled={readOnly} /></label>
        </div>
        <table className="t perm-table">
          <thead><tr><th>Area</th><th>See</th><th>Change</th><th>More</th></tr></thead>
          <tbody>
            {AREAS.map((a) => (
              <tr key={a.area}>
                <td><b>{a.area}</b><div className="faint" style={{ fontSize: 11.5 }}>{a.hint}</div></td>
                <td><Box code={a.view} label={a.viewLabel ?? 'View'} area={a} /></td>
                <td><Box code={a.manage} label={a.manageLabel ?? 'Change'} area={a} /></td>
                <td><div className="perm-extras">{(a.extras ?? []).map(([c, l]) => <Box key={c} code={c} label={l} />)}{!a.extras?.length && <span className="faint">—</span>}</div></td>
              </tr>
            ))}
          </tbody>
        </table>
        <small className="muted">Anything not ticked is hidden from the role: menus, pages, dashboard widgets, report data sources and API access. You can only give permissions you have yourself.</small>
      </form>
    </Modal>
  );
}

/** A group of users that get the same roles (for example "Facilities team" with the Non-IT Asset Manager role). */
export function GroupDialog({ group, roles, users, onClose, onSaved }: { group: GroupRow | null; roles: RoleRow[]; users: UserRow[]; onClose: () => void; onSaved: (msg: string) => void }) {
  const [name, setName] = useState(group?.name ?? '');
  const [description, setDescription] = useState(group?.description ?? '');
  const [picked, setPicked] = useState<string[]>(group?.roles.map((r) => r.id) ?? []);
  const [members, setMembers] = useState<string[]>(group?.members.map((m) => m.id) ?? []);
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const shown = users.filter((u) => !q || `${u.displayName} ${u.email}`.toLowerCase().includes(q.toLowerCase()));

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    const body = { name: name.trim(), description: description.trim(), roles: picked, members };
    try {
      if (group) await api.put(`/groups/${group.id}`, body); else await api.post('/groups', body);
      onSaved(`Group "${body.name}" saved.`);
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <Modal title={group ? `Edit group "${group.name}"` : 'New group'} wide onClose={onClose}
      footer={<><button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="submit" form="group-form" className="btn primary" disabled={busy || !name.trim()}>{busy ? 'Saving…' : 'Save group'}</button></>}>
      <form id="group-form" className="form-v" onSubmit={submit}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="form-grid">
          <label>Name<input className="in" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} placeholder="e.g. Facilities team" /></label>
          <label>Description<input className="in" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} /></label>
        </div>
        <div className="grid-2">
          <div>
            <div className="field-title">Roles of the group</div>
            <div className="check-list">
              {roles.map((r) => (
                <label key={r.id}><input type="checkbox" checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => (e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id)))} />
                  <span><b>{r.name}</b> <span className="muted">· {r.description}</span></span></label>
              ))}
            </div>
          </div>
          <div>
            <div className="field-title">Members ({members.length})</div>
            <input className="in" placeholder="Find a user…" value={q} onChange={(e) => setQ(e.target.value)} style={{ width: '100%', marginBottom: 6 }} aria-label="Find a user" />
            <div className="check-list">
              {shown.map((u) => (
                <label key={u.id}><input type="checkbox" checked={members.includes(u.id)} onChange={(e) => setMembers((m) => (e.target.checked ? [...m, u.id] : m.filter((x) => x !== u.id)))} />
                  <span><b>{u.displayName}</b> <span className="muted">· {u.email}</span></span></label>
              ))}
            </div>
          </div>
        </div>
        <small className="muted">Members get the group's roles in addition to their own. Changes apply at their next click; nobody is signed out.</small>
      </form>
    </Modal>
  );
}
