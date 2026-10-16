import { useEffect, useState } from 'react';

const LOGO_CHANGED = 'zq-logo-changed';
/** Tell every <CompanyLogo> on the page to reload (after an upload or removal). */
export function notifyLogoChanged() { window.dispatchEvent(new Event(LOGO_CHANGED)); }

/** The company logo uploaded in System Settings; renders nothing when none is set. */
export function CompanyLogo({ className, height = 28 }: { className?: string; height?: number }) {
  const [v, setV] = useState(() => Date.now());
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const f = () => { setFailed(false); setV(Date.now()); };
    window.addEventListener(LOGO_CHANGED, f);
    return () => window.removeEventListener(LOGO_CHANGED, f);
  }, []);
  if (failed) return null;
  return <img className={className} src={`/api/v1/organization/logo?v=${v}`} alt="Company logo" style={{ height, maxWidth: height * 5, objectFit: 'contain' }} onError={() => setFailed(true)} />;
}
