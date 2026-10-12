import { useState } from 'react';

/** Read-only value with a Copy button (enrollment codes, install commands). */
export function CopyBox({ value, label, rows = 1 }: { value: string; label: string; rows?: number }) {
  const [copied, setCopied] = useState(false);
  async function copy() {
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
    setCopied(true);
    setTimeout(() => setCopied(false), 1800);
  }
  return (
    <div className="copybox">
      <textarea className="in mono" readOnly rows={rows} value={value} aria-label={label} onFocus={(e) => e.currentTarget.select()} />
      <button type="button" className="btn sm" onClick={() => void copy()}>{copied ? 'Copied' : 'Copy'}</button>
    </div>
  );
}
