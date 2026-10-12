import { useId } from 'react';

/**
 * The Zentriq mark — a "connected Z": the diagonal links two devices (nodes) and the dashed orbit stands for
 * continuous discovery. Same artwork as the installer icon (installer/assets/src/zentriq-mark.svg).
 */
export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  const gid = `zq-bg-${useId().replace(/:/g, '')}`;
  const small = size < 26;
  return (
    <svg className={className} width={size} height={size} viewBox="0 0 64 64" aria-hidden="true">
      <defs>
        <linearGradient id={gid} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#13A3B4" />
          <stop offset="1" stopColor="#075468" />
        </linearGradient>
      </defs>
      {small ? (
        <>
          <rect width="64" height="64" rx="14" fill={`url(#${gid})`} />
          <path d="M15 17h31L18 47h31" fill="none" stroke="#fff" strokeWidth="9" strokeLinecap="round" strokeLinejoin="round" />
          <circle cx="47" cy="17" r="7.5" fill="#8BF0FA" stroke="#075468" strokeWidth="3" />
          <circle cx="17" cy="47" r="7.5" fill="#8BF0FA" stroke="#075468" strokeWidth="3" />
        </>
      ) : (
        <>
          <rect x="2" y="2" width="60" height="60" rx="15" fill={`url(#${gid})`} />
          <path d="M32 9.5a22.5 22.5 0 0 1 22.5 22.5M32 54.5A22.5 22.5 0 0 1 9.5 32" fill="none" stroke="#9FEAF2" strokeOpacity=".55" strokeWidth="2.2" strokeLinecap="round" strokeDasharray="1 5.2" />
          <path d="M18 20.5h25.5L20.5 43.5H46" fill="none" stroke="#fff" strokeWidth="6.2" strokeLinecap="round" strokeLinejoin="round" />
          <circle cx="44.5" cy="20.5" r="5.6" fill="#8BF0FA" stroke="#075468" strokeWidth="2.4" />
          <circle cx="19.5" cy="43.5" r="5.6" fill="#8BF0FA" stroke="#075468" strokeWidth="2.4" />
        </>
      )}
    </svg>
  );
}

export const PRODUCT = {
  name: 'Zentriq',
  full: 'Zentriq Web Console',
  tagline: 'IT asset intelligence & operations',
  strap: 'Discover, inventory, secure and manage every device in your estate — self-hosted, on your own servers.',
};
