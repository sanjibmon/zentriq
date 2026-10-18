import { useMemo, useState } from 'react';
import { useRouter } from '../../app/router';
import type { NetworkLink } from './types';

interface Node { key: string; label: string; assetId: string | null; kind: string; managed: boolean; degree: number; x: number; y: number }
interface Edge { a: string; b: string; label: string }

const KIND_COLOR: Record<string, string> = {
  switch: 'var(--accent)', router: '#7048e8', firewall: 'var(--crit)', access_point: 'var(--ok)', wireless_controller: 'var(--ok)',
  phone: 'var(--info)', other: 'var(--line-strong)',
};

function kindOf(type: string | null | undefined, caps: string[]): string {
  if (type && KIND_COLOR[type]) return type;
  if (caps.includes('router')) return 'router';
  if (caps.includes('wlan-ap')) return 'access_point';
  if (caps.includes('bridge')) return 'switch';
  if (caps.includes('phone')) return 'phone';
  return 'other';
}

/**
 * Network map from LLDP/CDP links: devices placed in rings by how connected they are (core in the centre, access at the edge),
 * links drawn between them. Click a device to open it.
 */
export function TopologyMap({ links }: { links: NetworkLink[] }) {
  const { navigate } = useRouter();
  const [hover, setHover] = useState<string | null>(null);
  const { nodes, edges, size } = useMemo(() => {
    const map = new Map<string, Node>();
    const node = (key: string, label: string, assetId: string | null, kind: string, managed: boolean) => {
      let n = map.get(key);
      if (!n) { n = { key, label, assetId, kind, managed, degree: 0, x: 0, y: 0 }; map.set(key, n); }
      else { if (managed) n.managed = true; if (n.kind === 'other' || (assetId && kind !== 'other' && n.kind !== kind && key === assetId)) n.kind = kind; }
      return n;
    };
    const seen = new Set<string>();
    const edges: Edge[] = [];
    for (const l of links) {
      const a = node(l.assetId, l.deviceName ?? l.assetId, l.assetId, kindOf(l.deviceType, []), true);
      const rkey = l.remoteAssetId ?? `n:${(l.remoteName ?? l.remoteChassisId ?? l.remoteAddress ?? '?').toLowerCase()}`;
      const b = node(rkey, l.remoteAssetName ?? l.remoteName ?? l.remoteChassisId ?? '?', l.remoteAssetId, kindOf(l.remoteDeviceType, l.capabilities), l.remoteManaged);
      const id = [a.key, b.key].sort().join('|');
      if (seen.has(id) || a.key === b.key) continue;
      seen.add(id);
      a.degree++; b.degree++;
      edges.push({ a: a.key, b: b.key, label: [l.localPort, l.remotePort].filter(Boolean).join(' ↔ ') });
    }
    const all = [...map.values()].sort((x, y) => y.degree - x.degree || x.label.localeCompare(y.label)).slice(0, 120);
    // Rings: the most connected devices in the centre.
    const rings: Node[][] = [];
    let i = 0;
    for (let r = 0; i < all.length; r++) {
      const cap = r === 0 ? Math.min(3, all.length) : 8 * r;
      rings.push(all.slice(i, i + cap));
      i += cap;
    }
    const step = 120;
    const size = Math.max(420, (rings.length - 1) * step * 2 + 200);
    const c = size / 2;
    rings.forEach((ring, r) => ring.forEach((n, k) => {
      if (r === 0 && ring.length === 1) { n.x = c; n.y = c; return; }
      const radius = r === 0 ? 50 : r * step;
      const ang = (2 * Math.PI * k) / ring.length - Math.PI / 2 + r * 0.3;
      n.x = c + radius * Math.cos(ang); n.y = c + radius * Math.sin(ang);
    }));
    const keep = new Set(all.map((n) => n.key));
    return { nodes: all, edges: edges.filter((e) => keep.has(e.a) && keep.has(e.b)), size };
  }, [links]);
  const pos = new Map(nodes.map((n) => [n.key, n]));

  return (
    <div className="topo">
      <svg viewBox={`0 0 ${size} ${size}`} role="img" aria-label="Network topology map">
        {edges.map((e) => {
          const a = pos.get(e.a)!; const b = pos.get(e.b)!;
          const on = hover === e.a || hover === e.b;
          return (
            <g key={`${e.a}|${e.b}`}>
              <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} className={on ? 'edge on' : 'edge'} />
              {on && e.label && <text x={(a.x + b.x) / 2} y={(a.y + b.y) / 2 - 4} className="edge-label">{e.label}</text>}
            </g>
          );
        })}
        {nodes.map((n) => (
          <g key={n.key} transform={`translate(${n.x},${n.y})`} className={n.assetId ? 'node link' : 'node'}
            onMouseEnter={() => setHover(n.key)} onMouseLeave={() => setHover(null)}
            onClick={() => n.assetId && navigate(`/assets/${n.assetId}?tab=network`)}>
            <title>{`${n.label}${n.assetId ? '' : ' — not in inventory'} · ${n.degree} link(s)`}</title>
            <rect x={-15} y={-11} width={30} height={22} rx={4} fill={KIND_COLOR[n.kind] ?? KIND_COLOR.other} opacity={n.assetId ? 1 : 0.45}
              strokeDasharray={n.managed ? undefined : '3 2'} />
            <text y={27} className="node-label">{n.label.length > 22 ? `${n.label.slice(0, 21)}…` : n.label}</text>
          </g>
        ))}
      </svg>
      <div className="topo-legend">
        {Object.entries({ switch: 'Switch', router: 'Router', firewall: 'Firewall', access_point: 'Access point', phone: 'IP phone', other: 'Other' }).map(([k, l]) => (
          <span key={k}><i style={{ background: KIND_COLOR[k] }} />{l}</span>
        ))}
        <span className="faint">Faded = not in inventory · dashed = not managed over SNMP{nodes.length >= 120 ? ' · showing the 120 most connected devices' : ''}</span>
      </div>
    </div>
  );
}
