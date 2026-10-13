import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';
import { Icon } from './Icon';

export interface ConfirmOptions {
  title: string;
  message: ReactNode;
  /** Label of the confirming button, e.g. "Delete". */
  confirmLabel?: string;
  /** Destructive actions get the caution styling and a red button. */
  danger?: boolean;
  /** Ask for a number (e.g. days) instead of a plain yes/no. */
  input?: { label: string; defaultValue?: string; type?: 'number' | 'text'; min?: number };
  /** Require typing this word before the button is enabled (for bulk or irreversible actions). */
  typeToConfirm?: string;
}

type Resolver = (value: string | null) => void;
const Ctx = createContext<((o: ConfirmOptions) => Promise<string | null>) | null>(null);

/** Styled caution dialog used before every delete, revoke, uninstall or other irreversible action. */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [opts, setOpts] = useState<ConfirmOptions | null>(null);
  const [value, setValue] = useState('');
  const [typed, setTyped] = useState('');
  const resolver = useRef<Resolver | null>(null);
  const ask = useCallback((o: ConfirmOptions) => new Promise<string | null>((resolve) => {
    resolver.current = resolve;
    setValue(o.input?.defaultValue ?? '');
    setTyped('');
    setOpts(o);
  }), []);
  const close = (v: string | null) => { resolver.current?.(v); resolver.current = null; setOpts(null); };
  const blocked = !!opts && ((opts.typeToConfirm && typed.trim().toUpperCase() !== opts.typeToConfirm.toUpperCase()) || (opts.input && value.trim() === ''));

  return (
    <Ctx.Provider value={ask}>
      {children}
      {opts && (
        <div className="modal-scrim" role="alertdialog" aria-modal="true" aria-labelledby="cf-title" aria-describedby="cf-body" onKeyDown={(e) => e.key === 'Escape' && close(null)}>
          <form className="modal-card confirm-card" onSubmit={(e) => { e.preventDefault(); if (!blocked) close(opts.input ? value.trim() : 'yes'); }}>
            <header className={opts.danger ? 'danger' : ''}>
              {opts.danger && <span className="cf-icon"><Icon name="alert" /></span>}
              <h2 id="cf-title">{opts.title}</h2>
            </header>
            <div className="body form-v" id="cf-body">
              <div>{opts.message}</div>
              {opts.danger && <div className="callout crit" style={{ margin: 0 }}><Icon name="alert" /><div><b>Caution:</b> this cannot be undone.</div></div>}
              {opts.input && (
                <label>{opts.input.label}
                  <input className="in" autoFocus type={opts.input.type ?? 'text'} min={opts.input.min} value={value} onChange={(e) => setValue(e.target.value)} />
                </label>
              )}
              {opts.typeToConfirm && (
                <label><span>Type <b className="mono">{opts.typeToConfirm}</b> to confirm</span>
                  <input className="in mono" autoFocus value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" />
                </label>
              )}
            </div>
            <footer>
              <button type="button" className="btn" onClick={() => close(null)} autoFocus={!opts.input && !opts.typeToConfirm}>Cancel</button>
              <button type="submit" className={`btn ${opts.danger ? 'danger-solid' : 'primary'}`} disabled={!!blocked}>{opts.confirmLabel ?? (opts.danger ? 'Delete' : 'OK')}</button>
            </footer>
          </form>
        </div>
      )}
    </Ctx.Provider>
  );
}

/** const confirm = useConfirm(); if (!(await confirm({...}))) return; */
export function useConfirm() {
  const c = useContext(Ctx);
  if (!c) throw new Error('useConfirm outside ConfirmProvider');
  return c;
}
