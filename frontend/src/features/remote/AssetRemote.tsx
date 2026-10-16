import { useState } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { Icon } from '../../components/Icon';
import { openRemote, type RemoteView } from './remote';

/** Remote desktop / terminal / files buttons on the device page (when the device has Zentriq Remote Support). */
export function AssetRemoteButtons({ assetId, onError }: { assetId: string; onError: (msg: string) => void }) {
  const r = useApi(() => api.get<{ installed: boolean; online: boolean; nodeId: string | null; lastConnectedAt: string | null }>(`/remote/assets/${assetId}`).catch(() => null), [assetId]);
  const [busy, setBusy] = useState(false);
  const s = r.data;
  if (!s?.installed) return null;
  const go = async (view: RemoteView) => {
    setBusy(true);
    try { await openRemote({ assetId, nodeId: s.nodeId }, view); } catch (e) { onError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  };
  return (
    <span className="btn-group" title={s.online ? 'Zentriq Remote Support' : 'The device is offline'}>
      <button className="btn" disabled={!s.online || busy} onClick={() => void go('desktop')}><Icon name="remote" size={13} /> Remote desktop</button>
      <button className="btn" disabled={!s.online || busy} onClick={() => void go('terminal')}>Terminal</button>
      <button className="btn" disabled={!s.online || busy} onClick={() => void go('files')}>Files</button>
    </span>
  );
}
