import { useEffect, useRef, useState, type ReactNode } from 'react';
import { DataTable, type Column } from './ui';
import { Icon } from './Icon';

/** The choosable column keys in the saved order; columns not in the saved order keep their place from the page. */
export function orderKeys(keys: string[], saved: string[]): string[] {
  const known = saved.filter((k) => keys.includes(k));
  const out = [...known];
  keys.forEach((k, i) => { if (!known.includes(k)) out.splice(Math.min(i, out.length), 0, k); });
  return out;
}

/**
 * Show, hide, add and rearrange columns of a table. The choice is remembered per table in this browser.
 * Columns marked `optional` start hidden ("add column"); `fixed` columns (selection, actions) cannot be hidden or moved.
 * Columns are moved by dragging them in the list or with the arrow buttons.
 */
export function useColumns<T>(tableId: string, all: Column<T>[]): { columns: Column<T>[]; chooser: ReactNode } {
  const storageKey = `zq.columns.${tableId}`;
  const isFixed = (c: Column<T>) => c.fixed || c.key === 'sel' || c.key === 'x' || c.label === '';
  const name = (c: Column<T>) => c.title ?? (typeof c.label === 'string' ? c.label : c.key);
  const [chosen, setChosen] = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Record<string, boolean>; } catch { return {}; }
  });
  const [order, setOrder] = useState<string[]>(() => {
    try { const v = JSON.parse(localStorage.getItem(storageKey + '.order') ?? '[]') as unknown; return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []; } catch { return []; }
  });
  const visible = (c: Column<T>) => isFixed(c) || (chosen[c.key] ?? !c.optional);
  const save = (next: Record<string, boolean>) => { setChosen(next); try { localStorage.setItem(storageKey, JSON.stringify(next)); } catch { /* storage unavailable */ } };
  const saveOrder = (next: string[]) => { setOrder(next); try { localStorage.setItem(storageKey + '.order', JSON.stringify(next)); } catch { /* storage unavailable */ } };
  const [dragKey, setDragKey] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);
  const free = all.filter((c) => !isFixed(c));
  const keys = orderKeys(free.map((c) => c.key), order);
  const choosable = keys.map((k) => free.find((c) => c.key === k)!);
  const move = (key: string, to: number) => {
    const next = keys.filter((k) => k !== key);
    next.splice(Math.max(0, Math.min(to, next.length)), 0, key);
    saveOrder(next);
  };
  // Fixed columns keep their place: leading ones (selection) first, trailing ones (actions) last.
  const firstFree = all.findIndex((c) => !isFixed(c));
  const lead = all.filter((c, i) => isFixed(c) && (firstFree < 0 || i < firstFree));
  const trail = all.filter((c, i) => isFixed(c) && firstFree >= 0 && i > firstFree);
  const hiddenCount = choosable.filter((c) => !visible(c)).length;
  const chooser = (
    <div className="col-chooser" ref={ref}>
      <button type="button" className="btn" aria-expanded={open} onClick={() => setOpen(!open)} title="Show, hide or add columns">
        <Icon name="columns" size={13} /> Columns{hiddenCount > 0 ? <span className="cnt">{choosable.length - hiddenCount}/{choosable.length}</span> : null}
      </button>
      {open && (
        <div className="col-pop" role="dialog" aria-label="Columns">
          <div className="col-pop-head">
            <b>Columns</b>
            <button type="button" className="linkish" onClick={() => { save({}); saveOrder([]); }}>Reset</button>
          </div>
          <div className="faint" style={{ fontSize: 11.5, margin: '-2px 0 6px' }}>Tick to show · drag or use the arrows to rearrange</div>
          {choosable.map((c, i) => (
            <div key={c.key} className={`col-row${dragKey === c.key ? ' dragging' : ''}`} draggable
              onDragStart={(e) => { setDragKey(c.key); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', c.key); }}
              onDragOver={(e) => { if (dragKey && dragKey !== c.key) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; } }}
              onDrop={(e) => { e.preventDefault(); if (dragKey && dragKey !== c.key) move(dragKey, i); setDragKey(null); }}
              onDragEnd={() => setDragKey(null)}>
              <span className="col-grip" aria-hidden="true">⋮⋮</span>
              <label className="row-check">
                <input type="checkbox" checked={visible(c)} onChange={(e) => save({ ...chosen, [c.key]: e.target.checked })} />
                {name(c)}{c.optional && !visible(c) ? <span className="faint" style={{ fontSize: 11.5 }}> · add</span> : null}
              </label>
              <span className="col-move">
                <button type="button" className="icon-btn" disabled={i === 0} aria-label={`Move ${name(c)} up`} title="Move up (further left in the table)" onClick={() => move(c.key, i - 1)}>↑</button>
                <button type="button" className="icon-btn" disabled={i === choosable.length - 1} aria-label={`Move ${name(c)} down`} title="Move down (further right in the table)" onClick={() => move(c.key, i + 1)}>↓</button>
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
  return { columns: [...lead, ...choosable.filter(visible), ...trail], chooser };
}

/** A table with a "Columns" button above it (for tables whose page has no toolbar of its own). */
export function ChoosableTable<T extends { id: string | number }>({ tableId, columns, rows, empty, tools }: {
  tableId: string; columns: Column<T>[]; rows: T[]; empty?: string; tools?: ReactNode;
}) {
  const { columns: visible, chooser } = useColumns(tableId, columns);
  return (
    <>
      <div className="tbl-tools">{tools}<span style={{ marginLeft: 'auto' }}>{chooser}</span></div>
      <DataTable rows={rows} columns={visible} empty={empty} />
    </>
  );
}
