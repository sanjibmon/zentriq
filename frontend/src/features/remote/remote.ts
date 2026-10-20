import { api } from '../../api/client';

export type RemoteView = 'desktop' | 'terminal' | 'files' | 'device' | 'console';

export interface RemoteStatus {
  installed: boolean; running: boolean; message: string | null; publicUrl: string | null; port: number;
  devices: number; online: number; unmapped: number; sessions24h: number; lastSync: string | null; readyToInstall: number;
  quickActive: number; quick30d: number;
  internet: { host: string; port: number; applied: boolean; usable: boolean; verified: boolean; confirmed: boolean; checkedAt: string | null; message: string | null } | null;
}

export interface RemoteDevice {
  id: string; nodeId: string | null; name: string; os: string | null; ip: string | null; online: boolean; lastConnectedAt: string | null;
  assetId: string | null; hostname: string | null; category: string | null; department: string | null; hasAgent: boolean; canInstall: boolean;
  lastAction: { status: string; detail: string | null } | null;
}

export interface RemoteSession { id: number; at: string; user: string; view: string; device: string | null; assetId: string | null; ip: string | null }
export interface RemoteSettings {
  prompt: boolean; notify: boolean; privacyBar: boolean; allowTerminal: boolean; internetHost: string | null; internetPort: number | null;
}

export interface QuickSession {
  id: number; code: string; inviteUrl: string; customer: string | null; email: string | null; note: string | null; createdBy: string; createdAt: string;
  expiresAt: string; status: 'waiting' | 'connected' | 'ended' | 'expired'; nodeId: string | null; deviceName: string | null; deviceOs: string | null;
  deviceIp: string | null; online: boolean; connectedAt: string | null; lastSeenAt: string | null; endedAt: string | null; endedBy: string | null; endReason: string | null;
}

export const formatCode = (c: string) => (c.length === 9 ? `${c.slice(0, 3)} ${c.slice(3, 6)} ${c.slice(6)}` : c);

/** Opens a quick support session's device (same pop-up handling as {@link openRemote}). */
export async function openQuick(id: number, view: 'desktop' | 'files' | 'terminal' | 'device'): Promise<void> {
  const w = window.open('about:blank', '_blank');
  try {
    if (w) w.document.title = 'Zentriq Remote Support';
    const r = await api.post<{ url: string }>(`/remote/quick/${id}/open`, { view });
    if (w) { w.opener = null; w.location.href = r.url; } else window.location.href = r.url;
  } catch (e) {
    w?.close();
    throw e;
  }
}

export const VIEW_LABEL: Record<string, string> = { desktop: 'Remote desktop', terminal: 'Terminal', files: 'File transfer', device: 'Chat & details', console: 'Remote console' };

/**
 * Opens a remote session in a new tab. The tab is opened synchronously (so pop-up blockers allow it) and then sent
 * to the short-lived sign-in link Zentriq creates for this technician.
 */
export async function openRemote(target: { assetId?: string | null; nodeId?: string | null }, view: RemoteView): Promise<void> {
  const w = window.open('about:blank', '_blank');
  try {
    if (w) w.document.title = 'Zentriq Remote Support';
    const r = await api.post<{ url: string }>('/remote/sessions', { assetId: target.assetId ?? null, nodeId: target.nodeId ?? null, view });
    if (w) { w.opener = null; w.location.href = r.url; } else window.location.href = r.url;
  } catch (e) {
    w?.close();
    throw e;
  }
}
