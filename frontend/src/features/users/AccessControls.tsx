import { useState } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';

export interface Named { id: string; name: string }
/** Data scope: devices in any of these locations, departments or device groups. All empty: every device. */
export interface Scope { locations: Named[]; departments: Named[]; deviceGroups: Named[] }
export interface ScopeIds { locations: string[]; departments: string[]; deviceGroups: string[] }
export interface SigninHours { days: number[]; from: string; to: string }
export interface AccessRulesValue { expiresAt: string | null; hours: SigninHours | null; networks: string[] }

export const emptyScope: ScopeIds = { locations: [], departments: [], deviceGroups: [] };
export const scopeIds = (s?: Scope | null): ScopeIds => s
  ? { locations: s.locations.map((x) => x.id), departments: s.departments.map((x) => x.id), deviceGroups: s.deviceGroups.map((x) => x.id) }
  : emptyScope;
export const scopeCount = (s?: Scope | ScopeIds | null) => s ? s.locations.length + s.departments.length + s.deviceGroups.length : 0;

/** "All devices", or the parts of the scope in words. */
export function scopeText(s?: Scope | null): string {
  if (!s || scopeCount(s) === 0) return 'All devices';
  const parts = [
    s.locations.length ? `Locations: ${s.locations.map((x) => x.name).join(', ')}` : '',
    s.departments.length ? `Departments: ${s.departments.map((x) => x.name).join(', ')}` : '',
    s.deviceGroups.length ? `Device groups: ${s.deviceGroups.map((x) => x.name).join(', ')}` : '',
  ].filter(Boolean);
  return parts.join(' · ');
}

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function Column({ title, items, picked, onChange, disabled }: { title: string; items: Named[]; picked: string[]; onChange: (ids: string[]) => void; disabled?: boolean }) {
  const [q, setQ] = useState('');
  const shown = items.filter((i) => !q || i.name.toLowerCase().includes(q.toLowerCase()));
  return (
    <div className="scope-col">
      <div className="field-title">{title} {picked.length > 0 && <span className="muted">({picked.length})</span>}</div>
      {items.length > 8 && <input className="in" placeholder="Find…" value={q} onChange={(e) => setQ(e.target.value)} aria-label={`Find ${title.toLowerCase()}`} style={{ width: '100%', marginBottom: 6 }} />}
      <div className="check-list" style={{ maxHeight: 180 }}>
        {shown.length === 0 && <span className="faint" style={{ padding: 6 }}>{items.length === 0 ? 'None defined yet' : 'No match'}</span>}
        {shown.map((i) => (
          <label key={i.id}><input type="checkbox" disabled={disabled} checked={picked.includes(i.id)}
            onChange={(e) => onChange(e.target.checked ? [...picked, i.id] : picked.filter((x) => x !== i.id))} /><span>{i.name}</span></label>
        ))}
      </div>
    </div>
  );
}

/** Pick the devices a role or group is limited to. Nothing picked: all devices. */
export function ScopePicker({ value, onChange, disabled }: { value: ScopeIds; onChange: (v: ScopeIds) => void; disabled?: boolean }) {
  const opts = useApi(() => api.get<Scope>('/access/scope-options'), []);
  const o = opts.data ?? { locations: [], departments: [], deviceGroups: [] };
  return (
    <div>
      <div className="scope-grid">
        <Column title="Locations" items={o.locations} picked={value.locations} disabled={disabled} onChange={(ids) => onChange({ ...value, locations: ids })} />
        <Column title="Departments" items={o.departments} picked={value.departments} disabled={disabled} onChange={(ids) => onChange({ ...value, departments: ids })} />
        <Column title="Device groups" items={o.deviceGroups} picked={value.deviceGroups} disabled={disabled} onChange={(ids) => onChange({ ...value, deviceGroups: ids })} />
      </div>
      <small className="muted">
        {scopeCount(value) === 0
          ? 'Nothing picked: all devices.'
          : 'Only devices in one of the picked locations, departments or device groups, and everything that belongs to them (software, patches, vulnerabilities, remote sessions, reports).'}
      </small>
    </div>
  );
}

/** Account expiry, allowed sign-in hours and networks for one user. */
export function AccessRulesEditor({ value, onChange }: { value: AccessRulesValue; onChange: (v: AccessRulesValue) => void }) {
  const local = value.expiresAt ? toLocalInput(value.expiresAt) : '';
  const hours = value.hours;
  const [netText, setNetText] = useState(value.networks.join('\n'));
  return (
    <div className="form-v" style={{ gap: 10 }}>
      <label>Access expires
        <span className="row" style={{ gap: 8 }}>
          <input className="in" type="datetime-local" value={local} onChange={(e) => onChange({ ...value, expiresAt: e.target.value ? new Date(e.target.value).toISOString() : null })} aria-label="Access expires" />
          {local && <button type="button" className="btn sm" onClick={() => onChange({ ...value, expiresAt: null })}>Never</button>}
        </span>
        <small className="muted">Empty: never. After this time the user cannot sign in and open sessions end (contractors, temporary staff).</small>
      </label>
      <div>
        <label className="row" style={{ gap: 6 }}>
          <input type="checkbox" checked={!!hours} onChange={(e) => onChange({ ...value, hours: e.target.checked ? { days: [1, 2, 3, 4, 5], from: '08:00', to: '20:00' } : null })} />
          <span>Limit sign-in hours</span>
        </label>
        {hours && (
          <div className="row wrap" style={{ gap: 10, marginTop: 6 }}>
            <span className="row" style={{ gap: 4 }}>
              {DAYS.map((d, i) => (
                <label key={d} className="day-chip"><input type="checkbox" checked={hours.days.includes(i)}
                  onChange={(e) => onChange({ ...value, hours: { ...hours, days: e.target.checked ? [...hours.days, i].sort() : hours.days.filter((x) => x !== i) } })} />{d}</label>
              ))}
            </span>
            <label className="row" style={{ gap: 6 }}>from <input className="in" type="time" value={hours.from} onChange={(e) => onChange({ ...value, hours: { ...hours, from: e.target.value } })} aria-label="From" /></label>
            <label className="row" style={{ gap: 6 }}>to <input className="in" type="time" value={hours.to} onChange={(e) => onChange({ ...value, hours: { ...hours, to: e.target.value } })} aria-label="To" /></label>
          </div>
        )}
        <small className="muted">In the Zentriq server's time. A range past midnight (22:00–06:00) is allowed. Outside the hours the user cannot sign in and open sessions end.</small>
      </div>
      <label>Allowed networks
        <textarea className="in" rows={3} value={netText} placeholder={'192.168.10.0/24\n10.20.0.0/16'}
          onChange={(e) => { setNetText(e.target.value); onChange({ ...value, networks: e.target.value.split(/[\n,;]+/).map((x) => x.trim()).filter(Boolean) }); }} />
        <small className="muted">One address or range per line. Empty: any network. Behind a reverse proxy, the proxy must pass the client address.</small>
      </label>
    </div>
  );
}

/** Short badges for the user list. */
export function accessBadges(a?: { expiresAt: string | null; hours: SigninHours | null; networks: string[]; expired?: boolean }): string[] {
  if (!a) return [];
  const out: string[] = [];
  if (a.expired) out.push('Expired');
  else if (a.expiresAt) out.push(`Until ${new Date(a.expiresAt).toLocaleDateString()}`);
  if (a.hours) out.push(`${a.hours.from}–${a.hours.to}`);
  if (a.networks.length) out.push(`${a.networks.length} network${a.networks.length > 1 ? 's' : ''}`);
  return out;
}

function toLocalInput(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
