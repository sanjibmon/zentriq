import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, ApiError, setUnauthorizedHandler } from '../api/client';
import type { Me } from '../api/types';

interface AuthValue {
  me: Me | null;
  checking: boolean;
  refresh: () => Promise<Me | null>;
  logout: () => Promise<void>;
  can: (permission: string) => boolean;
}
const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [checking, setChecking] = useState(true);

  const refresh = useCallback(async () => {
    try {
      const m = await api.get<Me>('/auth/me');
      setMe(m);
      return m;
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) console.error(e);
      setMe(null);
      return null;
    } finally {
      setChecking(false);
    }
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(() => setMe(null));
    void refresh();
  }, [refresh]);

  const logout = useCallback(async () => {
    try {
      await api.post('/auth/logout');
    } finally {
      setMe(null);
    }
  }, []);

  const can = useCallback((p: string) => !!me?.permissions.includes(p), [me]);
  return <AuthContext.Provider value={{ me, checking, refresh, logout, can }}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const v = useContext(AuthContext);
  if (!v) throw new Error('useAuth outside AuthProvider');
  return v;
}
