import { useRef, useState, type ReactNode } from 'react';

export interface Slice { key: string; label: string; value: number; color?: string }

const SLOTS = ['var(--s1)', 'var(--s2)', 'var(--s3)', 'var(--s4)', 'var(--s5)', 'var(--s6)', 'var(--s7)', 'var(--s8)'];
/** Colour follows the entity: the same key always gets the same slot (by first appearance in a stable order). */
export function slotColor(index: number, key?: string) { return key && /^other$/i.test(key) ? 'var(--s-other)' : SLOTS[index % SLOTS.length]!; }

function useTip() {
  const [tip, setTip] = useState<{ x: number; y: number; body: ReactNode } | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  const show = (e: { clientX: number; clientY: number }, body: ReactNode) => {
    const r = ref.current?.getBoundingClientRect();
    if (r) setTip({ x: e.clientX - r.left, y: e.clientY - r.top, body });
  };
  const el = tip && <div className="chart-tip" style={{ left: tip.x, top: tip.y }}>{tip.body}</div>;
  return { ref, show, hide: () => setTip(null), el };
}

const fmtN = (n: number) => n.toLocaleString();
const pct = (v: number, t: number) => (t ? `${Math.round((v / t) * 100)}%` : '0%');

/** Part-to-whole: donut with the total in the centre and a clickable legend (identity never by colour alone). */
export function Donut({ data, onSelect, centerLabel = 'total', size = 150 }: { data: Slice[]; onSelect?: (s: Slice) => void; centerLabel?: string; size?: number }) {
  const { ref, show, hide, el } = useTip();
  const [hover, setHover] = useState<string | null>(null);
  const total = data.reduce((a, d) => a + d.value, 0);
  if (total === 0) return <div className="chart-empty">No data yet.</div>;
  const r = size / 2, inner = r * 0.62, gap = data.length > 1 ? 0.012 : 0;
  let a0 = -Math.PI / 2;
  const arcs = data.map((d, i) => {
    const sweep = (d.value / total) * Math.PI * 2;
    const s = a0 + gap, e = a0 + sweep - gap;
    a0 += sweep;
    const big = e - s > Math.PI ? 1 : 0;
    const p = (ang: number, rad: number) => `${r + rad * Math.cos(ang)},${r + rad * Math.sin(ang)}`;
    const path = sweep >= Math.PI * 2 - 0.001
      ? `M${r},0A${r},${r} 0 1 1 ${r - 0.01},0ZM${r},${r - inner}A${inner},${inner} 0 1 0 ${r - 0.01},${r - inner}Z`
      : `M${p(s, r)}A${r},${r} 0 ${big} 1 ${p(e, r)}L${p(e, inner)}A${inner},${inner} 0 ${big} 0 ${p(s, inner)}Z`;
    return { d, path, color: d.color ?? slotColor(i, d.key) };
  });
  const focus = hover ? data.find((d) => d.key === hover) : null;
  return (
    <div className="donut-wrap">
      <div className="chart" ref={ref} onMouseLeave={() => { hide(); setHover(null); }}>
        <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={data.map((d) => `${d.label} ${d.value}`).join(', ')}>
          {arcs.map(({ d, path, color }) => (
            <path key={d.key} d={path} fill={color} fillRule="evenodd" className={onSelect ? 'hit' : ''} opacity={hover && hover !== d.key ? 0.35 : 1}
              tabIndex={onSelect ? 0 : -1} onClick={() => onSelect?.(d)} onKeyDown={(e) => e.key === 'Enter' && onSelect?.(d)}
              onMouseMove={(e) => { setHover(d.key); show(e, <><b>{d.label}</b> · {fmtN(d.value)} ({pct(d.value, total)})</>); }} />
          ))}
          <text x={r} y={r - 2} textAnchor="middle" style={{ fontSize: 24, fontWeight: 700, fill: 'var(--fg)' }}>{fmtN(focus ? focus.value : total)}</text>
          <text x={r} y={r + 16} textAnchor="middle" style={{ fontSize: 11.5, fill: 'var(--muted)' }}>{focus ? pct(focus.value, total) : centerLabel}</text>
        </svg>
        {el}
      </div>
      <div className="legend">
        {arcs.map(({ d, color }) => (
          <button key={d.key} type="button" className={hover === d.key ? 'on' : ''} onMouseEnter={() => setHover(d.key)} onMouseLeave={() => setHover(null)}
            onClick={() => onSelect?.(d)} disabled={!onSelect} title={onSelect ? `Show ${d.label}` : undefined}>
            <span className="sw" style={{ background: color }} /><span>{d.label}</span><span className="v">{fmtN(d.value)} · {pct(d.value, total)}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

/** Ranked magnitudes: horizontal bars, one hue, clickable rows. */
export function HBars({ data, onSelect, color = 'var(--s1)', max }: { data: Slice[]; onSelect?: (s: Slice) => void; color?: string; max?: number }) {
  const { ref, show, hide, el } = useTip();
  if (data.length === 0) return <div className="chart-empty">No data yet.</div>;
  const m = max ?? Math.max(1, ...data.map((d) => d.value));
  return (
    <div className="chart hbars" ref={ref} onMouseLeave={hide}>
      {data.map((d) => (
        <button key={d.key} type="button" onClick={() => onSelect?.(d)} disabled={!onSelect} onMouseMove={(e) => show(e, <><b>{d.label}</b> · {fmtN(d.value)}</>)}>
          <span className="lbl" title={d.label}>{d.label}</span>
          <span className="bar"><i style={{ width: `${Math.max(2, (d.value / m) * 100)}%`, background: d.color ?? color }} /></span>
          <span className="n">{fmtN(d.value)}</span>
        </button>
      ))}
      {el}
    </div>
  );
}

/** Change over time: area + line with a crosshair tooltip. */
export function TrendArea({ points, height = 170 }: { points: { label: string; total: number; added: number }[]; height?: number }) {
  const { ref, show, hide, el } = useTip();
  const [idx, setIdx] = useState<number | null>(null);
  const [w, setW] = useState(600);
  if (points.length === 0) return <div className="chart-empty">No data yet.</div>;
  const padL = 34, padB = 22, padT = 8, W = w, H = height;
  const max = Math.max(1, ...points.map((p) => p.total));
  const nice = Math.ceil(max / 4) * 4 || 4;
  const x = (i: number) => padL + (i / Math.max(1, points.length - 1)) * (W - padL - 6);
  const y = (v: number) => padT + (1 - v / nice) * (H - padT - padB);
  const line = points.map((p, i) => `${i ? 'L' : 'M'}${x(i)},${y(p.total)}`).join('');
  const area = `${line}L${x(points.length - 1)},${y(0)}L${x(0)},${y(0)}Z`;
  const ticks = [0, nice / 2, nice];
  return (
    <div className="chart" ref={(el2) => { (ref as { current: HTMLDivElement | null }).current = el2; if (el2 && Math.abs(el2.clientWidth - w) > 4) setW(el2.clientWidth); }}
      onMouseLeave={() => { hide(); setIdx(null); }}>
      <svg width="100%" height={H} viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" role="img" aria-label="Assets over the last 30 days"
        onMouseMove={(e) => {
          const r = (e.currentTarget as SVGSVGElement).getBoundingClientRect();
          const rel = ((e.clientX - r.left) / r.width) * W;
          const i = Math.max(0, Math.min(points.length - 1, Math.round(((rel - padL) / (W - padL - 6)) * (points.length - 1))));
          setIdx(i);
          const p = points[i]!;
          show(e, <><b>{p.label}</b><br />{fmtN(p.total)} assets{p.added ? ` · +${fmtN(p.added)} new` : ''}</>);
        }}>
        {ticks.map((t) => <g key={t}><line x1={padL} x2={W} y1={y(t)} y2={y(t)} stroke="var(--line)" strokeDasharray={t ? '3 4' : undefined} /><text x={padL - 6} y={y(t) + 4} textAnchor="end" style={{ fontSize: 10.5, fill: 'var(--muted)' }}>{fmtN(t)}</text></g>)}
        <path d={area} fill="var(--s1)" opacity={0.14} />
        <path d={line} fill="none" stroke="var(--s1)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
        {points.map((p, i) => p.added > 0 && <rect key={i} x={x(i) - 3} y={y(0) - Math.max(3, (p.added / nice) * (H - padT - padB))} width={6} height={Math.max(3, (p.added / nice) * (H - padT - padB))} rx={2} fill="var(--s3)" />)}
        {idx != null && <><line x1={x(idx)} x2={x(idx)} y1={padT} y2={y(0)} stroke="var(--line-strong)" /><circle cx={x(idx)} cy={y(points[idx]!.total)} r={4.5} fill="var(--s1)" stroke="var(--surface)" strokeWidth={2} /></>}
        {[0, Math.floor((points.length - 1) / 2), points.length - 1].map((i) => <text key={i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'} style={{ fontSize: 10.5, fill: 'var(--muted)' }}>{points[i]!.label}</text>)}
      </svg>
      {el}
    </div>
  );
}

/** Security controls: compliant / not compliant / unknown as a stacked bar per control. */
export function PostureBars({ rows, onSelect }: { rows: { key: string; yes: number; no: number; unknown: number }[]; onSelect?: (key: string) => void }) {
  const { ref, show, hide, el } = useTip();
  if (rows.every((r) => r.yes + r.no + r.unknown === 0)) return <div className="chart-empty">No Windows inventory yet.</div>;
  return (
    <div className="chart posture" ref={ref} onMouseLeave={hide}>
      {rows.map((r) => {
        const t = r.yes + r.no + r.unknown || 1;
        const tip = <><b>{r.key}</b><br />Yes {r.yes} · No {r.no} · Unknown {r.unknown}</>;
        return (
          <div className="row" key={r.key} onMouseMove={(e) => show(e, tip)} onClick={() => onSelect?.(r.key)} style={{ cursor: onSelect ? 'pointer' : 'default' }}>
            <span>{r.key}</span>
            <span className="stack" aria-label={`${r.key}: ${r.yes} yes, ${r.no} no, ${r.unknown} unknown`}>
              {r.yes > 0 && <span style={{ width: `${(r.yes / t) * 100}%`, background: 'var(--ok)' }} />}
              {r.no > 0 && <span style={{ width: `${(r.no / t) * 100}%`, background: 'var(--crit)' }} />}
              {r.unknown > 0 && <span style={{ width: `${(r.unknown / t) * 100}%`, background: 'var(--line-strong)' }} />}
            </span>
            <span className="pct" style={{ color: r.no > 0 ? 'var(--crit)' : 'var(--ok)' }}>{pct(r.yes, t)}</span>
          </div>
        );
      })}
      <div className="posture-legend">
        <span><i style={{ background: 'var(--ok)' }} />Compliant</span>
        <span><i style={{ background: 'var(--crit)' }} />Not compliant</span>
        <span><i style={{ background: 'var(--line-strong)' }} />Unknown</span>
      </div>
      {el}
    </div>
  );
}
