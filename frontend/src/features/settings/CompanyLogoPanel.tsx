import { useRef, useState } from 'react';
import { api } from '../../api/client';
import { CompanyLogo, notifyLogoChanged } from '../../components/CompanyLogo';
import { Callout, Panel } from '../../components/ui';

/** Upload / replace / remove the company logo (PNG, JPEG or WebP, up to 1 MB). */
export function CompanyLogoPanel({ canEdit }: { canEdit: boolean }) {
  const file = useRef<HTMLInputElement>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  async function upload() {
    const f = file.current?.files?.[0];
    if (!f) return;
    setMsg(null);
    if (f.size > 1024 * 1024) { setMsg({ tone: 'crit', text: 'The image is larger than 1 MB.' }); return; }
    if (!['image/png', 'image/jpeg', 'image/webp'].includes(f.type)) { setMsg({ tone: 'crit', text: 'Use a PNG, JPEG or WebP image.' }); return; }
    setBusy(true);
    try {
      const res = await fetch('/api/v1/organization/logo', { method: 'PUT', credentials: 'same-origin', headers: { 'X-Zentriq-Request': '1', 'Content-Type': 'application/octet-stream' }, body: f });
      if (!res.ok) {
        let text = res.statusText;
        try { const j = (await res.json()) as { title?: string; errors?: Record<string, string[]> }; text = j.errors ? Object.values(j.errors).flat().join(' ') : j.title ?? text; } catch { /* not JSON */ }
        throw new Error(text);
      }
      setMsg({ tone: 'ok', text: 'Logo saved. It appears in the console header and on the sign-in page.' });
      if (file.current) file.current.value = '';
      notifyLogoChanged();
    } catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); } finally { setBusy(false); }
  }
  async function remove() {
    setMsg(null);
    try { await api.del('/organization/logo'); notifyLogoChanged(); setMsg({ tone: 'ok', text: 'Logo removed.' }); }
    catch (e) { setMsg({ tone: 'crit', text: e instanceof Error ? e.message : String(e) }); }
  }
  return (
    <Panel title="Company logo" sub="Shown in the console header and on the sign-in page">
      <div className="form-v">
        {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
        <div className="logo-preview"><CompanyLogo height={56} /><span className="muted" style={{ fontSize: 12.5 }}>Current logo (empty when none is set)</span></div>
        {canEdit && <>
          <label>Image file (PNG, JPEG or WebP, up to 1 MB; a wide logo on a transparent background works best)
            <input ref={file} className="in" type="file" accept="image/png,image/jpeg,image/webp" onChange={() => void upload()} disabled={busy} />
          </label>
          <div><button type="button" className="btn" onClick={() => void remove()}>Remove logo</button></div>
        </>}
      </div>
    </Panel>
  );
}
