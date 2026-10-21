import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable } from '../../components/ui';
import { DepartmentSelect, useAddDepartment, useDepartments, type Department } from '../departments/Departments';

export interface Location { id: string; name: string; code: string | null; description: string | null; createdAt: string; assets: number; accessories?: number }
export interface LocationList { items: Location[]; unassigned: number }

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : b?.title ?? (e instanceof Error ? e.message : String(e));
};

export function useLocations() {
  return useApi(() => api.get<LocationList>('/locations'), []);
}

export function useAddLocation() {
  const confirm = useConfirm();
  return async (): Promise<{ id: string; name: string } | null> => {
    const name = await confirm({ title: 'Add location', message: 'Name of the office, site, floor or store, for example "Head office – 3rd floor" or "Pune store".', confirmLabel: 'Add', input: { label: 'Name', type: 'text' } });
    if (!name) return null;
    return await api.post<{ id: string; name: string }>('/locations', { name });
  };
}

/** Dropdown of locations with "Add new…" at the end. value = location id, '' = none. */
export function LocationSelect({ value, onChange, list, onAdded, allowNone = true, noneLabel = '— Not assigned —', canAdd, id, extra }: {
  value: string; onChange: (id: string) => void; list: Location[]; onAdded: () => void; allowNone?: boolean; noneLabel?: string; canAdd: boolean; id?: string;
  extra?: [string, string][];
}) {
  const add = useAddLocation();
  const [error, setError] = useState<string | null>(null);
  async function pick(v: string) {
    setError(null);
    if (v !== '__add') { onChange(v); return; }
    try {
      const l = await add();
      if (l) { onAdded(); onChange(l.id); }
    } catch (e) { setError(errText(e)); }
  }
  return (
    <>
      <select id={id} className="in" value={value} onChange={(e) => void pick(e.target.value)} aria-label="Location">
        {extra?.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        {allowNone && <option value="">{noneLabel}</option>}
        {list.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        {canAdd && <option value="__add">＋ Add new location…</option>}
      </select>
      {error && <small style={{ color: 'var(--crit)' }}>{error}</small>}
    </>
  );
}

const KEEP = '__keep';

/** Set the department and / or the location of the selected devices. */
export function AssignOrgDialog({ assetIds, onClose, onDone }: { assetIds: string[]; onClose: () => void; onDone: (msg: string) => void }) {
  const deps = useDepartments();
  const locs = useLocations();
  const [dep, setDep] = useState(KEEP);
  const [loc, setLoc] = useState(KEEP);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const parts: string[] = [];
      if (dep !== KEEP) {
        const r = await api.post<{ updated: number; department: string | null }>('/assets/department', { assetIds, departmentId: dep || null });
        parts.push(r.department ? `department ${r.department}` : 'department cleared');
      }
      if (loc !== KEEP) {
        const r = await api.post<{ updated: number; location: string | null }>('/assets/location', { assetIds, locationId: loc || null });
        parts.push(r.location ? `location ${r.location}` : 'location cleared');
      }
      onDone(`${assetIds.length} device(s): ${parts.join(', ')}.`);
    } catch (x) { setError(errText(x)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="org-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="org-title">Department and location — {assetIds.length} device(s)</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Department / unit
            <DepartmentSelect value={dep} onChange={setDep} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd noneLabel="— None (clear) —" extra={[[KEEP, '— Keep as it is —']]} />
          </label>
          <label>Location
            <LocationSelect value={loc} onChange={setLoc} list={locs.data?.items ?? []} onAdded={locs.reload} canAdd noneLabel="— None (clear) —" extra={[[KEEP, '— Keep as it is —']]} />
          </label>
          <small className="muted">Department and location appear in the inventories, on the device page, in every device report and on the dashboard.</small>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || (dep === KEEP && loc === KEEP)}>{busy ? 'Saving…' : 'Save'}</button>
        </footer>
      </form>
    </div>
  );
}

/** Add, rename and delete departments and locations (one dialog, two tabs). */
export function ManageOrgDialog({ onClose, canEdit, initial = 'locations' }: { onClose: () => void; canEdit: boolean; initial?: 'departments' | 'locations' }) {
  const [tab, setTab] = useState(initial);
  const deps = useDepartments();
  const locs = useLocations();
  const confirm = useConfirm();
  const addDep = useAddDepartment();
  const addLoc = useAddLocation();
  const [error, setError] = useState<string | null>(null);
  async function run(f: () => Promise<unknown>) {
    setError(null);
    try { await f(); deps.reload(); locs.reload(); } catch (e) { setError(errText(e)); }
  }
  async function rename(kind: 'departments' | 'locations', d: Department | Location) {
    const name = await confirm({ title: `Rename "${d.name}"`, message: kind === 'locations' ? 'New name of the location.' : 'New name of the department / unit.', confirmLabel: 'Rename', input: { label: 'Name', type: 'text', defaultValue: d.name } });
    if (name && name !== d.name) await run(() => api.put(`/${kind}/${d.id}`, { name, code: d.code, description: d.description }));
  }
  async function remove(kind: 'departments' | 'locations', d: Department | Location) {
    if (!(await confirm({ title: `Delete "${d.name}"?`, message: d.assets ? `${d.assets} device(s) become unassigned.` : 'No devices are assigned to it.', danger: true }))) return;
    await run(() => api.del(`/${kind}/${d.id}`));
  }
  const list = tab === 'locations' ? locs : deps;
  const actions = (d: Department | Location) => canEdit ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
    <button className="btn sm" onClick={() => void rename(tab, d)}>Rename</button>
    <button className="btn sm" onClick={() => void remove(tab, d)}>Delete</button>
  </div> : null;
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="morg-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal-card" style={{ width: 'min(720px, 100%)' }}>
        <header><h2 id="morg-title">Departments and locations</h2></header>
        <div className="body">
          <div className="tabs" role="tablist" style={{ marginBottom: 12 }}>
            <button className={tab === 'locations' ? 'on' : ''} onClick={() => setTab('locations')}>Locations{locs.data && <span className="cnt">{locs.data.items.length}</span>}</button>
            <button className={tab === 'departments' ? 'on' : ''} onClick={() => setTab('departments')}>Departments{deps.data && <span className="cnt">{deps.data.items.length}</span>}</button>
          </div>
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {tab === 'locations'
            ? <DataTable rows={locs.data?.items ?? []} empty="No locations yet. Add your offices, sites or stores." columns={[
                { key: 'n', label: 'Location', render: (d) => <b>{d.name}</b> },
                { key: 'a', label: 'Devices', render: (d) => d.assets.toLocaleString() },
                { key: 's', label: 'Accessories in stock', render: (d) => (d.accessories ?? 0).toLocaleString() },
                { key: 'x', label: '', render: actions },
              ]} />
            : <DataTable rows={deps.data?.items ?? []} empty="No departments yet." columns={[
                { key: 'n', label: 'Department / unit', render: (d) => <b>{d.name}</b> },
                { key: 'a', label: 'Devices', render: (d) => d.assets.toLocaleString() },
                { key: 'x', label: '', render: actions },
              ]} />}
          {list.data && <p className="muted" style={{ fontSize: 12.5, marginBottom: 0 }}>{list.data.unassigned.toLocaleString()} active device(s) have no {tab === 'locations' ? 'location' : 'department'}.</p>}
        </div>
        <footer>
          {canEdit && <button type="button" className="btn" onClick={() => void run(async () => { await (tab === 'locations' ? addLoc() : addDep()); })}><Icon name="plus" size={12} /> Add {tab === 'locations' ? 'location' : 'department'}</button>}
          <span style={{ flex: 1 }} />
          <button type="button" className="btn primary" onClick={onClose}>Close</button>
        </footer>
      </div>
    </div>
  );
}
