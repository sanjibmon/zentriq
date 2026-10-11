import { createContext, useCallback, useContext, useEffect, useState, type MouseEvent, type ReactNode } from 'react';

// Minimal path router (History API). The API serves index.html for every non-/api path.
interface RouterValue {
  path: string;
  query: URLSearchParams;
  navigate: (to: string, replace?: boolean) => void;
}
const RouterContext = createContext<RouterValue | null>(null);

export function Router({ children }: { children: ReactNode }) {
  const [loc, setLoc] = useState(() => ({ path: window.location.pathname, search: window.location.search }));
  useEffect(() => {
    const onPop = () => setLoc({ path: window.location.pathname, search: window.location.search });
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);
  const navigate = useCallback((to: string, replace = false) => {
    if (replace) window.history.replaceState(null, '', to);
    else window.history.pushState(null, '', to);
    const u = new URL(to, window.location.origin);
    setLoc({ path: u.pathname, search: u.search });
    window.scrollTo(0, 0);
  }, []);
  return <RouterContext.Provider value={{ path: loc.path, query: new URLSearchParams(loc.search), navigate }}>{children}</RouterContext.Provider>;
}

export function useRouter(): RouterValue {
  const v = useContext(RouterContext);
  if (!v) throw new Error('useRouter outside Router');
  return v;
}

export function Link({ to, className, children, title, onClick }: { to: string; className?: string; children: ReactNode; title?: string; onClick?: () => void }) {
  const { navigate } = useRouter();
  const go = (e: MouseEvent<HTMLAnchorElement>) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey) return;
    e.preventDefault();
    onClick?.();
    navigate(to);
  };
  return (
    <a href={to} className={className} onClick={go} title={title}>
      {children}
    </a>
  );
}
