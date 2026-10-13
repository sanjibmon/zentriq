/**
 * Sign-in artwork: discovery radar around the Zentriq hub, the devices it finds (laptop, desktop, server, virtual
 * machine, switch, printer, cloud) and the inventory each one reports. Pure SVG, decorative only.
 */
const DEVICES: { x: number; y: number; icon: string; label: string; card?: [string, string] }[] = [
  { x: 92, y: 96, icon: 'laptop', label: 'Laptop', card: ['CPU i7 · 16 GB', '142 apps'] },
  { x: 420, y: 84, icon: 'server', label: 'Server', card: ['Windows Server', 'Services 214'] },
  { x: 470, y: 252, icon: 'vm', label: 'Virtual machine' },
  { x: 388, y: 392, icon: 'cloud', label: 'Cloud' },
  { x: 150, y: 392, icon: 'printer', label: 'Printer' },
  { x: 54, y: 252, icon: 'switch', label: 'Switch', card: ['SNMP · 48 ports', 'Firmware 15.2'] },
  { x: 268, y: 46, icon: 'desktop', label: 'Desktop' },
];

function Icon({ kind }: { kind: string }) {
  const s = { fill: 'none', stroke: '#CFF6FA', strokeWidth: 1.8, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const };
  switch (kind) {
    case 'laptop': return <g {...s}><rect x={-12} y={-9} width={24} height={15} rx={2} /><path d="M-16 9h32" /></g>;
    case 'desktop': return <g {...s}><rect x={-13} y={-11} width={26} height={17} rx={2} /><path d="M-5 11h10M0 6v5" /></g>;
    case 'server': return <g {...s}><rect x={-11} y={-13} width={22} height={11} rx={2} /><rect x={-11} y={1} width={22} height={11} rx={2} /><circle cx={6} cy={-7.5} r={1} fill="#5EE6F2" /><circle cx={6} cy={6.5} r={1} fill="#5EE6F2" /></g>;
    case 'vm': return <g {...s}><rect x={-12} y={-12} width={24} height={24} rx={3} strokeDasharray="3 3" /><rect x={-6} y={-6} width={12} height={12} rx={2} /></g>;
    case 'cloud': return <g {...s}><path d="M-12 7h22a6 6 0 0 0 0-12 9 9 0 0 0-17-2 6 6 0 0 0-5 14z" /></g>;
    case 'printer': return <g {...s}><rect x={-12} y={-4} width={24} height={11} rx={2} /><path d="M-7 -4v-7h14v7M-7 7v5h14v-5" /></g>;
    default: return <g {...s}><rect x={-14} y={-6} width={28} height={12} rx={2} /><path d="M-9 0h2M-4 0h2M1 0h2M6 0h2" /></g>;
  }
}

export function BrandArt() {
  const cx = 268, cy = 236;
  return (
    <svg className="brand-art" viewBox="0 0 540 460" role="img" aria-label="Zentriq discovers devices on your network and collects their inventory">
      <defs>
        <radialGradient id="ba-glow" cx="50%" cy="50%" r="50%">
          <stop offset="0%" stopColor="#38B3C4" stopOpacity="0.35" />
          <stop offset="100%" stopColor="#38B3C4" stopOpacity="0" />
        </radialGradient>
        <linearGradient id="ba-sweep" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#5EE6F2" stopOpacity="0" />
          <stop offset="100%" stopColor="#5EE6F2" stopOpacity="0.32" />
        </linearGradient>
      </defs>
      <circle cx={cx} cy={cy} r={210} fill="url(#ba-glow)" />
      {[70, 125, 180].map((r) => <circle key={r} cx={cx} cy={cy} r={r} fill="none" stroke="#5EE6F2" strokeOpacity={0.18} strokeDasharray={r === 180 ? '2 6' : undefined} />)}
      <circle className="ba-pulse" cx={cx} cy={cy} r={70} fill="none" stroke="#5EE6F2" strokeWidth={2} />
      <g className="ba-sweep" style={{ transformOrigin: `${cx}px ${cy}px` }}>
        <path d={`M${cx},${cy}L${cx + 180},${cy}A180,180 0 0 0 ${cx + 180 * Math.cos(-0.7)},${cy + 180 * Math.sin(-0.7)}Z`} fill="url(#ba-sweep)" />
      </g>
      {DEVICES.map((d, i) => (
        <g key={d.label}>
          <path d={`M${cx},${cy}Q${(cx + d.x) / 2 + (i % 2 ? 26 : -26)},${(cy + d.y) / 2} ${d.x},${d.y}`} fill="none" stroke="#5EE6F2" strokeOpacity={0.45} strokeWidth={1.4} className="ba-link" style={{ animationDelay: `${i * 0.45}s` }} />
          <circle cx={d.x} cy={d.y} r={25} fill="#0E2A36" stroke="#38B3C4" strokeOpacity={0.8} strokeWidth={1.5} />
          <g transform={`translate(${d.x} ${d.y})`}><Icon kind={d.icon} /></g>
          <text x={d.x} y={d.y + 40} textAnchor="middle" className="ba-label">{d.label}</text>
          {d.card && (
            <g transform={`translate(${d.x + (d.x > cx ? -128 : 30)} ${d.y + (d.y < cy ? 18 : -62)})`} className="ba-card" style={{ animationDelay: `${0.6 + i * 0.5}s` }}>
              <rect width={104} height={42} rx={7} fill="#0B222C" stroke="#38B3C4" strokeOpacity={0.55} />
              <rect x={9} y={10} width={4} height={22} rx={2} fill="#5EE6F2" />
              <text x={20} y={19} className="ba-card-t">{d.card[0]}</text>
              <text x={20} y={33} className="ba-card-s">{d.card[1]}</text>
            </g>
          )}
        </g>
      ))}
      <g transform={`translate(${cx - 34} ${cy - 34})`}>
        <rect width={68} height={68} rx={16} fill="#0A6475" stroke="#5EE6F2" strokeOpacity={0.6} strokeWidth={2} />
        <path d="M17 19h30L21 49h30" transform="translate(0 0)" fill="none" stroke="#fff" strokeWidth={7.5} strokeLinecap="round" strokeLinejoin="round" />
        <circle cx={49} cy={19} r={6.5} fill="#5EE6F2" />
        <circle cx={19} cy={49} r={6.5} fill="#5EE6F2" />
      </g>
    </svg>
  );
}
