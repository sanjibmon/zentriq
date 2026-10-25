import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import type { Role, UserRow } from '../../api/types';
import { Callout } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';

/** Add a user, or edit name/roles/status and set a new password. */
export function UserDialog({ existing, roles, groups = [], onClose, onSaved }: { existing: UserRow | null; roles: Role[]; groups?: { id: string; name: string; roles: { name: string }[] }[]; onClose: () => void; onSaved: () => void }) {
  const [email, setEmail] = useState(existing?.email ?? '');
  const [name, setName] = useState(existing?.displayName ?? '');
  const [picked, setPicked] = useState<string[]>(existing?.roles.map((r) => r.id) ?? ['viewer'].filter((r) => roles.some((x) => x.id === r)));
  const [inGroups, setInGroups] = useState<string[]>(existing?.groups?.map((g) => g.id) ?? []);
  const [password, setPassword] = useState('');
  const [status, setStatus] = useState(existing?.status ?? 'active');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      if (existing) await api.patch(`/users/${existing.id}`, { displayName: name, roles: picked, status, newPassword: password || null, groups: inGroups });
      else await api.post('/users', { email: email.trim(), displayName: name.trim(), password, roles: picked, groups: inGroups });
      onSaved();
    } catch (err) { setError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="u-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="u-title">{existing ? `Edit ${existing.displayName}` : 'Add user'}</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>E-mail (user name) <input className="in" type="email" value={email} onChange={(e) => setEmail(e.target.value)} disabled={!!existing} required /></label>
          <label>Display name <input className="in" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></label>
          <label>{existing ? 'New password (leave empty to keep)' : 'Password'}
            <input className="in" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required={!existing} />
            <small className="muted">At least 12 characters with three of: upper case, lower case, digits, symbols. {existing ? 'Setting a password also unlocks the account and signs the user out everywhere.' : ''}</small>
          </label>
          <div>
            <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Roles</div>
            <div className="check-list">
              {roles.map((r) => (
                <label key={r.id}><input type="checkbox" checked={picked.includes(r.id)} onChange={(e) => setPicked((p) => e.target.checked ? [...p, r.id] : p.filter((x) => x !== r.id))} /><span><b>{r.name}</b> <span className="muted">· {r.description}</span></span></label>
              ))}
            </div>
          </div>
          {groups.length > 0 && <div>
            <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 5 }}>Groups</div>
            <div className="check-list">
              {groups.map((g) => (
                <label key={g.id}><input type="checkbox" checked={inGroups.includes(g.id)} onChange={(e) => setInGroups((p) => e.target.checked ? [...p, g.id] : p.filter((x) => x !== g.id))} />
                  <span><b>{g.name}</b> <span className="muted">· {g.roles.map((r) => r.name).join(', ') || 'no roles'}</span></span></label>
              ))}
            </div>
          </div>}
          {existing && (
            <label>Status
              <select className="in" value={status} onChange={(e) => setStatus(e.target.value)}><option value="active">Active</option><option value="disabled">Disabled</option></select>
            </label>
          )}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || (picked.length === 0 && inGroups.length === 0) || !name.trim() || (!existing && (!email.trim() || !password))}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}
