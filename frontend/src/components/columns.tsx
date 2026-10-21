import { useEffect, useRef, useState, type ReactNode } from 'react';
import { DataTable, type Column } from './ui';
import { Icon } from './Icon';

/**
 * Show, hide and add columns of an inventory table. The choice is remembered per table in this browser.
 * Columns marked `optional` start hidden ("add column"); `fixed` columns (selection, actions) cannot be hidden.
 */
export function useColumns<T>(tableId: string, all: Column<T>[]): { columns: Column<T>[]; chooser: ReactNode } {
  const storageKey = `zq.columns.${tableId}`;
  const isFixed = (c: Column<T>) => c.fixed || c.key === 'sel' || c.key === 'x' || c.label === '';
  const name = (c: Column<T>) => c.title ?? (typeof c.label === 'string' ? c.label : c.key);
  const [chosen, setChosen] = useState<Record<string, boolean>>(() => {
    try { return JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Record<string, boolean>; } catch { return {}; }
  });
  const visible = (c: Column<T>) => isFixed(c) || (chosen[c.key] ?? !c.optional);
  const save = (next: Record<string, boolean>) => { setChosen(next); try { localStorage.setItem(storageKey, JSON.stringify(next)); } catch { /* storage unavailable */ } };
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);
  const choosable = all.filter((c) => !isFixed(c));
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
            <button type="button" className="linkish" onClick={() => save({})}>Reset</button>
          </div>
          {choosable.map((c) => (
            <label key={c.key} className="row-check">
              <input type="checkbox" checked={visible(c)} onChange={(e) => save({ ...chosen, [c.key]: e.target.checked })} />
              {name(c)}{c.optional && !visible(c) ? <span className="faint" style={{ fontSize: 11.5 }}> · add</span> : null}
            </label>
          ))}
        </div>
      )}
    </div>
  );
  return { columns: all.filter(visible), chooser };
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
