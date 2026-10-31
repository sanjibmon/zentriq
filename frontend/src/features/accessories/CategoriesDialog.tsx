import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Modal } from '../../components/Modal';
import { Callout, Pill } from '../../components/ui';

export interface AccessoryCategory { id: string; name: string; builtIn: boolean; items: number }

const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : b?.title ?? (e instanceof Error ? e.message : String(e));
};

/** Adds a category; returns its name (or null when nothing was added). */
export async function addCategory(name: string): Promise<string> {
  const r = await api.post<{ name: string }>('/accessories/categories', { name: name.trim() });
  return r.name;
}

/** IT Accessories › Categories: add, rename and delete categories (delete only when no accessory uses it). */
export function CategoriesDialog({ canEdit, onClose }: { canEdit: boolean; onClose: (changed: boolean) => void }) {
  const list = useApi(() => api.get<AccessoryCategory[]>('/accessories/categories'), []);
  const confirm = useConfirm();
  const [name, setName] = useState('');
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [changed, setChanged] = useState(false);
  const [busy, setBusy] = useState(false);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); setChanged(true); list.reload(); } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  async function add(e: FormEvent) {
    e.preventDefault();
    if (!name.trim()) return;
    await run(async () => { await addCategory(name); setName(''); });
  }
  async function remove(c: AccessoryCategory) {
    if (!(await confirm({ title: `Delete the category ${c.name}?`, danger: true, confirmLabel: 'Delete', message: 'Categories in use by accessories cannot be deleted.' }))) return;
    await run(() => api.del(`/accessories/categories/${c.id}`));
  }

  return (
    <Modal title="Accessory categories" onClose={() => onClose(changed)} footer={<button className="btn" onClick={() => onClose(changed)}>Close</button>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {canEdit && (
          <form className="cat-add" onSubmit={add}>
            <input className="in" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="New category, e.g. Stylus pen" aria-label="New category" autoFocus />
            <button type="submit" className="btn primary" disabled={busy || !name.trim()}>Add category</button>
          </form>
        )}
        <div className="cat-list">
          {(list.data ?? []).map((c) => (
            <div key={c.id} className="cat-row">
              {editing?.id === c.id ? (
                <form className="cat-add" onSubmit={(e) => { e.preventDefault(); void run(async () => { await api.put(`/accessories/categories/${c.id}`, { name: editing.name }); setEditing(null); }); }}>
                  <input className="in sm" value={editing.name} maxLength={60} onChange={(e) => setEditing({ id: c.id, name: e.target.value })} autoFocus aria-label="Category name" />
                  <button type="submit" className="btn sm primary" disabled={busy || !editing.name.trim()}>Save</button>
                  <button type="button" className="btn sm" onClick={() => setEditing(null)}>Cancel</button>
                </form>
              ) : (
                <>
                  <span className="cat-name">{c.name}</span>
                  {c.builtIn && <Pill>Built-in</Pill>}
                  <span className="muted cat-count">{c.items} accessor{c.items === 1 ? 'y' : 'ies'}</span>
                  {canEdit && <span className="cat-actions">
                    <button className="btn sm" onClick={() => setEditing({ id: c.id, name: c.name })}>Rename</button>
                    <button className="btn sm" disabled={c.items > 0} title={c.items > 0 ? 'In use by accessories' : undefined} onClick={() => void remove(c)}>Delete</button>
                  </span>}
                </>
              )}
            </div>
          ))}
          {list.loading && !list.data && <p className="muted">Loading…</p>}
        </div>
        <small className="muted">Renaming a category renames it on its accessories as well.</small>
      </div>
    </Modal>
  );
}
