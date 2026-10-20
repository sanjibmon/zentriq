import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import { Callout } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';

export interface ControlInfo {
  channel: 'agent' | 'agentless';
  hasAgent: boolean;
  agentCapable: boolean;
  agentOnline: boolean;
  agentVersion: string | null;
  needsCredential: boolean;
  credentials: { id: string; name: string; username: string | null }[];
  protectedServices: string[];
  note: string | null;
}

export interface ServiceActionRow {
  id: string; channel: string; target: string; operation: string; status: string; detail: string | null; resultState: string | null;
  requestedAt: string; completedAt: string | null; requestedBy: string | null;
}

const VERB: Record<string, string> = { start: 'Start', stop: 'Stop', restart: 'Restart' };

/** Confirms a service action; asks for a device credential only when the device has no capable agent. */
export function ServiceActionDialog({ assetId, host, service, display, operation, info, onClose, onDone }: {
  assetId: string; host: string; service: string; display: string; operation: string; info: ControlInfo; onClose: () => void; onDone: (message: string) => void;
}) {
  const [mode, setMode] = useState<'stored' | 'typed'>(info.credentials.length > 0 ? 'stored' : 'typed');
  const [credId, setCredId] = useState(info.credentials[0]?.id ?? '');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const agent = info.channel === 'agent';

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = agent ? {} : mode === 'stored' ? { credentialId: credId } : { username: username.trim(), password };
      const r = await api.post<{ message: string }>(`/assets/${assetId}/services/${encodeURIComponent(service)}/${operation}`, body);
      onDone(r.message);
    } catch (err) {
      const b = err instanceof ApiError ? (err.body as { credentialRequired?: boolean; title?: string } | null) : null;
      setError(b?.credentialRequired ? b.title ?? 'A credential is required.' : errorText(err));
    } finally { setBusy(false); }
  }

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="sa-title">
      <form className="modal-card" onSubmit={submit}>
        <header><h2 id="sa-title">{VERB[operation]} “{display}” on {host}?</h2></header>
        <div className="body form-v">
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          {operation !== 'start' && <Callout tone="warn" icon="alert">{operation === 'stop' ? 'Stopping' : 'Restarting'} a service interrupts whatever depends on it on {host}. The action is recorded in the audit log.</Callout>}
          {agent ? (
            <p style={{ margin: 0 }}>The Zentriq Agent on {host} will do this at its next check-in (set by its check-in interval){info.agentOnline ? '' : ' — the agent has not checked in recently, so it runs when the device is back online'}. No device credential is needed.</p>
          ) : (
            <>
              <p style={{ margin: 0 }}>{info.note ?? `${host} has no Zentriq Agent, so Zentriq connects to it over WinRM (TCP 5985) or DCOM/WMI with an administrator account on the device.`}</p>
              {info.credentials.length > 0 && (
                <div className="seg" role="radiogroup">
                  <button type="button" className={mode === 'stored' ? 'on' : ''} onClick={() => setMode('stored')}>Stored credential</button>
                  <button type="button" className={mode === 'typed' ? 'on' : ''} onClick={() => setMode('typed')}>Enter an account</button>
                </div>
              )}
              {mode === 'stored' && info.credentials.length > 0 ? (
                <label>Credential
                  <select className="in" value={credId} onChange={(e) => setCredId(e.target.value)}>
                    {info.credentials.map((c) => <option key={c.id} value={c.id}>{c.name}{c.username ? ` (${c.username})` : ''}</option>)}
                  </select>
                </label>
              ) : (
                <div className="form-grid">
                  <label>User name <input className="in mono" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="CORP\admin or .\Administrator" autoComplete="off" required /></label>
                  <label>Password <input className="in" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" required /></label>
                  <small className="muted full">Used for this action only: encrypted on the server until the action has run, then deleted. Never shown or logged.</small>
                </div>
              )}
            </>
          )}
        </div>
        <footer>
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className={`btn ${operation === 'start' ? 'primary' : 'danger'}`} disabled={busy || (!agent && mode === 'typed' && (!username.trim() || !password)) || (!agent && mode === 'stored' && !credId)}>
            {busy ? 'Sending…' : VERB[operation]}
          </button>
        </footer>
      </form>
    </div>
  );
}
