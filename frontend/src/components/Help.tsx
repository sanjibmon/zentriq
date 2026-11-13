import { Children, useState, type ReactNode } from 'react';
import { Icon } from './Icon';

// Help text: each kind of thing has its own look instead of bold for everything.
//   <HelpSteps><Step title="Scan">explanation…</Step></HelpSteps>   numbered steps
//   <UiPath>Settings › Security</UiPath>                             where to click (menus, tabs, pages)
//   <UiBtn>Save</UiBtn>                                              a button or option by name
//   <Val>ZentriqAgent-x64.msi</Val>                                  a value, file name or setting to type
//   <Command value="zentriqctl repair" shell="Command Prompt (Administrator)" />   a command to copy and run
//   <Note tone="tip|important">…</Note>                              a tip or something to watch out for

export function HelpSteps({ children, className }: { children: ReactNode; className?: string }) {
  return <ol className={`help-steps${className ? ` ${className}` : ''}`}>{children}</ol>;
}

export function Step({ title, children }: { title?: ReactNode; children?: ReactNode }) {
  return (
    <li>
      <div className="help-step">
        {title && <span className="help-step-t">{title}</span>}
        {children !== undefined && <div className="help-step-d">{children}</div>}
      </div>
    </li>
  );
}

/** "Settings › Security": segments separated by › in text (elements such as links stay as they are). */
export function UiPath({ children }: { children: ReactNode }) {
  const parts: { sep: boolean; node: ReactNode }[] = [];
  Children.forEach(children, (c) => {
    if (typeof c === 'string') {
      c.split('›').forEach((p, i) => {
        if (i > 0) parts.push({ sep: true, node: null });
        if (p.trim()) parts.push({ sep: false, node: p.trim() });
      });
    } else if (c !== null && c !== undefined && c !== false && c !== true) parts.push({ sep: false, node: c });
  });
  return (
    <span className="ui-path">
      {parts.map((p, i) => (p.sep ? <i key={i} aria-hidden="true">›</i> : <span key={i}>{p.node}</span>))}
    </span>
  );
}

export function UiBtn({ children }: { children: ReactNode }) {
  return <span className="ui-btn">{children}</span>;
}

export function Val({ children }: { children: ReactNode }) {
  return <code className="ui-val">{children}</code>;
}

export async function copyText(value: string) {
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    const t = document.createElement('textarea');
    t.value = value;
    document.body.appendChild(t);
    t.select();
    document.execCommand('copy');
    t.remove();
  }
}

/** A command to run, in a terminal-style box with where to run it and a Copy button. */
export function Command({ value, shell, label }: { value: string; shell?: ReactNode; label?: string }) {
  const [copied, setCopied] = useState(false);
  const prompt = typeof shell === 'string' && /linux|bash|root|sudo|terminal/i.test(shell) ? '$' : '>';
  return (
    <div className="help-cmd" role="group" aria-label={label ?? 'Command'}>
      <div className="help-cmd-h">
        <span><Icon name="terminal" size={12} /> {shell ?? 'Command'}</span>
        <button type="button" onClick={() => { void copyText(value).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1800); }); }}
          aria-label={`Copy ${label ?? 'command'}`}>
          <Icon name="copy" size={11} /> {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre><span className="help-cmd-p" aria-hidden="true">{prompt} </span>{value}</pre>
    </div>
  );
}

export function Note({ tone = 'tip', title, children }: { tone?: 'tip' | 'important'; title?: ReactNode; children: ReactNode }) {
  return (
    <div className={`help-note ${tone}`} role="note">
      <Icon name={tone === 'important' ? 'alert' : 'info'} size={15} />
      <div>{(title ?? (tone === 'important' ? 'Important' : null)) && <b>{title ?? 'Important'}</b>}{children}</div>
    </div>
  );
}
