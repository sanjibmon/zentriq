import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable } from '../../components/ui';

export interface Department { id: string; name: string; code: string | null; description: string | null; createdAt: string; assets: number; nonItAssets?: number }
export interface DepartmentList { items: Department[]; unassigned: number }

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]> } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : e instanceof Error ? e.message : String(e);
};

export function useDepartments() {
  return useApi(() => api.get<DepartmentList>('/departments'), []);
}

/** Creates a department and returns it (name is asked in a small dialog). */
export function useAddDepartment() {
  const confirm = useConfirm();
  return async (): Promise<{ id: string; name: string } | null> => {
    const name = await confirm({ title: 'Add department / unit', message: 'Name of the department, business unit, location or group that devices belong to.', confirmLabel: 'Add', input: { label: 'Name', type: 'text' } });
    if (!name) return null;
    return await api.post<{ id: string; name: string }>('/departments', { name });
  };
}

/**
 * Dropdown of departments with "Add new…" at the end. value = department id, '' = none.
 */
export function DepartmentSelect({ value, onChange, list, onAdded, allowNone = true, noneLabel = '— Not assigned —', canAdd, id }: {
  value: string; onChange: (id: string) => void; list: Department[]; onAdded: () => void; allowNone?: boolean; noneLabel?: string; canAdd: boolean; id?: string;
}) {
  const add = useAddDepartment();
  const [error, setError] = useState<string | null>(null);
  async function pick(v: string) {
    setError(null);
    if (v !== '__add') { onChange(v); return; }
    try {
      const d = await add();
      if (d) { onAdded(); onChange(d.id); }
    } catch (e) { setError(errText(e)); }
  }
  return (
    <>
      <select id={id} className="in" value={value} onChange={(e) => void pick(e.target.value)} aria-label="Department">
        {allowNone && <option value="">{noneLabel}</option>}
        {list.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        {canAdd && <option value="__add">＋ Add new department…</option>}
      </select>
      {error && <small style={{ color: 'var(--crit)' }}>{error}</small>}
    </>
  );
}

/** Assign the selected assets to a department (or clear it). */
export function AssignDepartmentDialog({ assetIds, onClose, onDone }: { assetIds: string[]; onClose: () => void; onDone: (msg: string) => void }) {
  const deps = useDepartments();
  const [dep, setDep] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ updated: number; department: string | null }>('/assets/department', { assetIds, departmentId: dep || null });
      onDone(r.department ? `${r.updated} asset(s) assigned to ${r.department}.` : `Department cleared on ${r.updated} asset(s).`);
    } catch (x) { setError(errText(x)); } finally { setBusy(false); }
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="dep-title">
      <form className="modal-card" onSubmit={save}>
        <header><h2 id="dep-title">Assign department — {assetIds.length} asset(s)</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <label>Department / unit
            <DepartmentSelect value={dep} onChange={setDep} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd noneLabel="— None (clear) —" />
          </label>
          <small className="muted">The department appears in the asset list, on the device page, in every device report (column "Department / unit") and on the dashboard.</small>
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy}>{busy ? 'Saving…' : dep ? 'Assign' : 'Clear department'}</button>
        </footer>
      </form>
    </div>
  );
}

/** Add, rename and delete departments. */
export function ManageDepartmentsDialog({ onClose, canEdit }: { onClose: () => void; canEdit: boolean }) {
  const deps = useDepartments();
  const confirm = useConfirm();
  const add = useAddDepartment();
  const [error, setError] = useState<string | null>(null);
  async function run(f: () => Promise<unknown>) {
    setError(null);
    try { await f(); deps.reload(); } catch (e) { setError(errText(e)); }
  }
  async function rename(d: Department) {
    const name = await confirm({ title: `Rename "${d.name}"`, message: 'New name of the department / unit.', confirmLabel: 'Rename', input: { label: 'Name', type: 'text', defaultValue: d.name } });
    if (name && name !== d.name) await run(() => api.put(`/departments/${d.id}`, { name, code: d.code, description: d.description }));
  }
  async function remove(d: Department) {
    if (!(await confirm({ title: `Delete "${d.name}"?`, message: d.assets ? `${d.assets} asset(s) become unassigned.` : 'No assets are assigned to it.', danger: true }))) return;
    await run(() => api.del(`/departments/${d.id}`));
  }
  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="mdep-title" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal-card" style={{ width: 'min(680px, 100%)' }}>
        <header><h2 id="mdep-title">Departments / units</h2></header>
        <div className="body">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <DataTable rows={deps.data?.items ?? []} empty="No departments yet." columns={[
            { key: 'n', label: 'Name', render: (d) => <b>{d.name}</b> },
            { key: 'a', label: 'Assets', render: (d) => d.assets.toLocaleString() },
            { key: 'x', label: '', render: (d) => canEdit ? <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
              <button className="btn sm" onClick={() => void rename(d)}>Rename</button>
              <button className="btn sm" onClick={() => void remove(d)}>Delete</button>
            </div> : null },
          ]} />
          {deps.data && <p className="muted" style={{ fontSize: 12.5, marginBottom: 0 }}>{deps.data.unassigned.toLocaleString()} active asset(s) are not assigned to a department.</p>}
        </div>
        <footer>
          {canEdit && <button type="button" className="btn" onClick={() => void run(async () => { await add(); })}><Icon name="plus" size={12} /> Add department</button>}
          <span style={{ flex: 1 }} />
          <button type="button" className="btn primary" onClick={onClose}>Close</button>
        </footer>
      </div>
    </div>
  );
}
