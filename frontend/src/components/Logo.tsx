/**
 * The Zentriq mark — a "connected Z": the diagonal links two devices (nodes) and the dashed orbit stands for
 * continuous discovery. Same artwork as the installer icon (installer/assets/src/zentriq-mark.svg).
 */
export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  // Flat artwork, no fine detail: identical to installer/assets/src/zentriq-mark*.svg.
  return size < 26 ? (
    <svg className={className} width={size} height={size} viewBox="0 0 16 16" aria-hidden="true">
      <rect width="16" height="16" rx="3" fill="#0A6475" />
      <path d="M4 4h8L4 12h8" fill="none" stroke="#fff" strokeWidth="2" strokeLinejoin="miter" />
      <circle cx="12" cy="4" r="2" fill="#5EE6F2" />
      <circle cx="4" cy="12" r="2" fill="#5EE6F2" />
    </svg>
  ) : (
    <svg className={className} width={size} height={size} viewBox="0 0 64 64" aria-hidden="true">
      <rect width="64" height="64" rx="14" fill="#0A6475" />
      <path d="M16 18h28L20 46h28" fill="none" stroke="#fff" strokeWidth="7.5" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="46" cy="18" r="6.5" fill="#5EE6F2" />
      <circle cx="18" cy="46" r="6.5" fill="#5EE6F2" />
    </svg>
  );
}

export const PRODUCT = {
  name: 'Zentriq',
  full: 'Zentriq Web Console',
  tagline: 'Asset Intelligence',
  strap: 'Discover, inventory, secure and manage every device in your estate — self-hosted, on your own servers.',
};
