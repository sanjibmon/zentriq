import type { ReactNode } from 'react';

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
