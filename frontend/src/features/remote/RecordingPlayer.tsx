import { useEffect, useMemo, useRef, useState } from 'react';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout } from '../../components/ui';

/**
 * Player for recorded remote sessions (.mcrec files of the remote support engine). The file is a list of blocks: a
 * 16-byte header (type 1 metadata / 2 data / 3 end, flags — 1 binary, 2 from the technician —, size, time in
 * milliseconds) and the data. Remote desktop: the screen stream (screen size and JPEG tiles) is drawn on a canvas;
 * terminal: the text output; files: the file commands.
 */
interface Block { type: number; flags: number; time: number; data: Uint8Array }
interface Parsed { meta: Record<string, unknown>; blocks: Block[]; start: number; end: number }

export function parseRecording(buf: ArrayBuffer): Parsed {
  const view = new DataView(buf);
  const bytes = new Uint8Array(buf);
  const blocks: Block[] = [];
  let meta: Record<string, unknown> = {};
  let off = 0;
  while (off + 16 <= buf.byteLength) {
    const type = view.getInt16(off);
    const flags = view.getInt16(off + 2);
    const size = view.getInt32(off + 4);
    const time = view.getUint16(off + 10) * 4294967296 + view.getUint32(off + 12);
    if (size < 0 || off + 16 + size > buf.byteLength) break;
    const data = bytes.subarray(off + 16, off + 16 + size);
    off += 16 + size;
    if (type === 1) { try { meta = JSON.parse(new TextDecoder().decode(data)) as Record<string, unknown>; } catch { /* keep empty */ } }
    blocks.push({ type, flags, time, data });
    if (type === 3) break;
  }
  if (blocks.length === 0 || meta.magic !== 'MeshCentralRelaySession') throw new Error('This is not a remote session recording.');
  const times = blocks.map((b) => b.time).filter((t) => t > 0);
  return { meta, blocks, start: Math.min(...times), end: Math.max(...times) };
}

const clock = (ms: number) => {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60;
  return `${h > 0 ? `${h}:` : ''}${String(m).padStart(h > 0 ? 2 : 1, '0')}:${String(ss).padStart(2, '0')}`;
};

/** Draws remote desktop frames: screen size (7) and JPEG tiles (3), also inside jumbo packets (27). */
class DesktopRenderer {
  constructor(private canvas: HTMLCanvasElement) {}
  reset() { const c = this.canvas.getContext('2d'); if (c) { c.fillStyle = '#111'; c.fillRect(0, 0, this.canvas.width, this.canvas.height); } }
  feed(d: Uint8Array) {
    let off = 0;
    while (off + 4 <= d.length) {
      const dv = new DataView(d.buffer, d.byteOffset + off, d.length - off);
      let cmd = dv.getUint16(0);
      let size = dv.getUint16(2);
      let body = d.subarray(off, off + size);
      if (cmd === 27 && size === 8 && d.length - off >= 8) {
        const total = dv.getUint32(4);
        body = d.subarray(off + 8, Math.min(d.length, off + 8 + total));
        cmd = body.length >= 2 ? (body[0] << 8) | body[1] : 0;
        size = 8 + total;
      }
      if (size < 4) break;
      this.command(cmd, body);
      off += size;
    }
  }
  private command(cmd: number, b: Uint8Array) {
    if (b.length < 8) return;
    const x = (b[4] << 8) | b[5], y = (b[6] << 8) | b[7];
    if (cmd === 7) {
      if (x > 0 && y > 0 && x <= 16384 && y <= 16384 && (this.canvas.width !== x || this.canvas.height !== y)) { this.canvas.width = x; this.canvas.height = y; this.reset(); }
    } else if (cmd === 3) {
      let start = 8;
      while (start < Math.min(b.length - 1, 24) && !(b[start] === 0xff && b[start + 1] === 0xd8)) start++;
      if (b[start] !== 0xff) return;
      const blob = new Blob([b.slice(start).buffer as ArrayBuffer], { type: 'image/jpeg' });
      createImageBitmap(blob).then((img) => { this.canvas.getContext('2d')?.drawImage(img, x, y); img.close(); }).catch(() => undefined);
    }
  }
}

const stripAnsi = (s: string) => s.replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '').replace(/\x1b\][^\x07]*\x07/g, '').replace(/\x1b[()][A-Z0-9]/g, '');

export function RecordingPlayer({ id, title, onClose }: { id: number; title: string; onClose: () => void }) {
  const [rec, setRec] = useState<Parsed | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pos, setPos] = useState(0);          // milliseconds from the start
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(1);
  const canvas = useRef<HTMLCanvasElement>(null);
  const renderer = useRef<DesktopRenderer | null>(null);
  const applied = useRef(0);                  // index of the next block to apply
  const [text, setText] = useState('');

  useEffect(() => {
    let gone = false;
    fetch(`/api/v1/remote/recordings/${id}/file`, { credentials: 'same-origin' })
      .then(async (r) => {
        if (!r.ok) { const j = await r.json().catch(() => null) as { title?: string } | null; throw new Error(j?.title ?? `HTTP ${r.status}`); }
        return r.arrayBuffer();
      })
      .then((b) => { if (!gone) setRec(parseRecording(b)); })
      .catch((e: unknown) => { if (!gone) setError(e instanceof Error ? e.message : String(e)); });
    return () => { gone = true; };
  }, [id]);

  const protocol = Number(rec?.meta.protocol ?? 0);
  const desktop = protocol === 2;
  const duration = rec ? rec.end - rec.start : 0;

  // Applies the blocks up to the position (from the start when going back).
  useEffect(() => {
    if (!rec) return;
    if (desktop && !renderer.current && canvas.current) renderer.current = new DesktopRenderer(canvas.current);
    const until = rec.start + pos;
    let i = applied.current;
    if (i > 0 && rec.blocks[i - 1] && rec.blocks[i - 1].time > until) { i = 0; renderer.current?.reset(); setText(''); }
    let add = '';
    for (; i < rec.blocks.length && rec.blocks[i].time <= until; i++) {
      const b = rec.blocks[i];
      if (b.type !== 2) continue;
      const fromTech = (b.flags & 2) !== 0;
      if (desktop) { if (!fromTech && (b.flags & 1)) renderer.current?.feed(b.data); }
      else if (protocol === 1) { if (!fromTech) add += new TextDecoder().decode(b.data); }
      else if (fromTech) add += new TextDecoder().decode(b.data) + '\n';
    }
    applied.current = i;
    if (add) setText((t) => (t + stripAnsi(add)).slice(-200000));
  }, [rec, pos, desktop, protocol]);

  useEffect(() => {
    if (!playing || !rec) return;
    let last = performance.now();
    const t = setInterval(() => {
      const now = performance.now();
      setPos((p) => { const n = p + (now - last) * speed; if (n >= duration) { setPlaying(false); return duration; } return n; });
      last = now;
    }, 100);
    return () => clearInterval(t);
  }, [playing, speed, rec, duration]);

  const who = useMemo(() => String(rec?.meta.username ?? '').replace(/-rec$/, ''), [rec]);
  return (
    <Modal title={title} wide onClose={onClose} footer={<>
      <a className="btn" href={`/api/v1/remote/recordings/${id}/file?download=true`}><Icon name="down" size={13} /> Download</a>
      <button className="btn" onClick={onClose}>Close</button>
    </>}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      {!rec && !error && <p className="muted">Loading the recording…</p>}
      {rec && <>
        <p className="faint" style={{ marginTop: 0, fontSize: 12.5 }}>
          {protocol === 2 ? 'Remote desktop' : protocol === 1 ? 'Terminal' : protocol === 5 ? 'Files' : 'Session'} · {String(rec.meta.nodename ?? '')} · technician {who || '—'} ·{' '}
          {new Date(rec.start).toLocaleString()} · {clock(duration)}. Playback is recorded in the audit log.
        </p>
        <div className="rec-stage">
          {desktop ? <canvas ref={canvas} width={1024} height={768} className="rec-canvas" /> : <pre className="rec-text">{text || (protocol === 5 ? 'No file actions yet.' : '')}</pre>}
        </div>
        <div className="rec-controls">
          <button className="btn sm primary" onClick={() => { if (pos >= duration) setPos(0); setPlaying((p) => !p); }}><Icon name={playing ? 'stop' : 'play'} size={13} /> {playing ? 'Pause' : 'Play'}</button>
          <input type="range" min={0} max={Math.max(1, duration)} step={100} value={pos} onChange={(e) => setPos(Number(e.target.value))} aria-label="Position" style={{ flex: 1 }} />
          <span className="mono faint">{clock(pos)} / {clock(duration)}</span>
          <select className="in" value={speed} onChange={(e) => setSpeed(Number(e.target.value))} aria-label="Speed">
            {[1, 2, 4, 8, 16].map((s) => <option key={s} value={s}>{s}×</option>)}
          </select>
        </div>
      </>}
    </Modal>
  );
}
