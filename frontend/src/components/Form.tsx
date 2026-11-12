import { useMemo, useState, type ReactNode } from 'react';

/**
 * The layout of every add / edit window: sections with a heading, fields in one to three columns (the label above,
 * the box, a hint below; boxes side by side line up), check boxes with an explanation.
 */
export function Section({ title, sub, children }: { title?: string; sub?: ReactNode; children: ReactNode }) {
  return (
    <section className="fs">
      {(title || sub) && <div className="fs-h">{title && <h3>{title}</h3>}{sub && <p>{sub}</p>}</div>}
      {children}
    </section>
  );
}

export function Fields({ cols = 2, children }: { cols?: 1 | 2 | 3; children: ReactNode }) {
  return <div className={`fgrid c${cols}`}>{children}</div>;
}

export function Field({ label, hint, required, optional, span, children }: {
  label: ReactNode; hint?: ReactNode; required?: boolean; optional?: boolean; span?: boolean; children: ReactNode;
}) {
  return (
    <label className={`ff${span ? ' span' : ''}`}>
      <span className="ff-l">{label}{required && <i className="req" aria-hidden="true">*</i>}{optional && <span className="opt">optional</span>}</span>
      <span className="ff-c">{children}</span>
      {hint ? <small className="ff-h">{hint}</small> : <span aria-hidden="true" />}
    </label>
  );
}

export function Check({ checked, onChange, disabled, children, hint }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; children: ReactNode; hint?: ReactNode }) {
  return (
    <label className="fcheck row-check">
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span>{children}{hint && <small>{hint}</small>}</span>
    </label>
  );
}

/** Check boxes or radio buttons in a row (they wrap). */
export function Choices({ children }: { children: ReactNode }) {
  return <div className="fchecks">{children}</div>;
}

export interface ComboOption { value: string; sub?: string | null; count?: number | null }

/**
 * Text box with a list to pick from: typing filters the list, and a value that is not in it is kept as typed
 * (software found in the inventory, with manual entry for anything not found yet).
 */
export function Combo({ value, onChange, onPick, options, placeholder, maxLength, autoFocus, ariaLabel, countLabel = 'device', loading }: {
  value: string; onChange: (v: string) => void; onPick?: (o: ComboOption) => void; options: ComboOption[]; placeholder?: string; maxLength?: number;
  autoFocus?: boolean; ariaLabel?: string; countLabel?: string; loading?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState(false);
  const [at, setAt] = useState(-1);
  const q = typed ? value.trim().toLowerCase() : '';
  const matches = useMemo(() => {
    const out: ComboOption[] = [];
    for (const o of options) {
      if (q && !o.value.toLowerCase().includes(q) && !(o.sub ?? '').toLowerCase().includes(q)) continue;
      out.push(o);
      if (out.length >= 80) break;
    }
    return out;
  }, [options, q]);
  const total = useMemo(() => (q ? options.filter((o) => o.value.toLowerCase().includes(q) || (o.sub ?? '').toLowerCase().includes(q)).length : options.length), [options, q]);
  function pick(o: ComboOption) { onChange(o.value); onPick?.(o); setOpen(false); setTyped(false); setAt(-1); }
  return (
    <span className={`combo${open ? ' open' : ''}`}>
      <input className="in" role="combobox" aria-expanded={open} aria-autocomplete="list" aria-label={ariaLabel} value={value} placeholder={placeholder} maxLength={maxLength}
        autoFocus={autoFocus} autoComplete="off"
        onChange={(e) => { onChange(e.target.value); setTyped(true); setOpen(true); setAt(-1); }}
        onFocus={() => setOpen(true)} onMouseDown={() => setOpen(true)} onBlur={() => { setOpen(false); setAt(-1); }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setAt((i) => Math.min(i + 1, matches.length - 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setAt((i) => Math.max(i - 1, 0)); }
          else if (e.key === 'Enter' && open && at >= 0 && matches[at]) { e.preventDefault(); pick(matches[at]); }
          else if (e.key === 'Escape' && open) { e.stopPropagation(); setOpen(false); }
        }} />
      <button type="button" className="combo-toggle" tabIndex={-1} aria-label="Show the list" onMouseDown={(e) => { e.preventDefault(); setTyped(false); setOpen((v) => !v); (e.currentTarget.previousSibling as HTMLInputElement | null)?.focus(); }}>▾</button>
      {open && (
        <span className="combo-list" role="listbox" onMouseDown={(e) => e.preventDefault()}>
          {matches.map((o, i) => (
            <span key={o.value + '|' + (o.sub ?? '')} role="option" aria-selected={i === at} className={`combo-opt${i === at ? ' at' : ''}${o.value === value ? ' sel' : ''}`} onClick={(e) => { e.preventDefault(); pick(o); }}>
              <span className="combo-main"><b>{o.value}</b>{o.sub && <small>{o.sub}</small>}</span>
              {o.count != null && <small className="combo-count">{o.count.toLocaleString()} {countLabel}{o.count === 1 ? '' : 's'}</small>}
            </span>
          ))}
          {matches.length === 0 && <span className="combo-note">{loading ? 'Loading…' : options.length === 0 ? 'Nothing found in the inventory yet — type it in.' : 'Not in the list — it is added as typed.'}</span>}
          {total > matches.length && <span className="combo-note">{(total - matches.length).toLocaleString()} more — type to narrow the list.</span>}
        </span>
      )}
    </span>
  );
}
