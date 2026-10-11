/** The Zentriq mark (same artwork as the installer icon and the prototype). */
export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  return (
    <svg className={className} width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <rect width="32" height="32" rx="7" fill="#0A7385" />
      <path d="M9 10h14l-14 12h14" stroke="#E9F7F9" strokeWidth="2.6" fill="none" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="23" cy="10" r="2.2" fill="#7EE0EC" />
    </svg>
  );
}

export const PRODUCT = {
  name: 'Zentriq',
  full: 'Zentriq Console',
  tagline: 'IT asset intelligence & operations',
  strap: 'Discover, inventory, secure and manage every device in your estate — self-hosted, on your own servers.',
};
