import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { Callout, ErrorBox, Panel } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';

interface AssistantConfig {
  enabled: boolean; provider: 'offline' | 'anthropic' | 'local'; anthropicModel: string; localUrl: string | null; localModel: string | null;
  liveData: boolean; audience: 'everyone' | 'admins'; keepDays: number; hasKey: boolean; keyHint: string | null;
}

const PROVIDERS: { id: AssistantConfig['provider']; label: string; hint: string }[] = [
  { id: 'offline', label: 'Documentation search (no AI model)', hint: 'Works on every server, also without internet. Answers with the matching documentation and, when allowed, facts from this server.' },
  { id: 'anthropic', label: 'Claude (Anthropic API, your own key)', hint: 'Best answers and diagnosis. Needs internet on port 443 to api.anthropic.com. Only the question, the documentation passages and the facts needed are sent; passwords, keys and codes are removed first.' },
  { id: 'local', label: 'Local model on your network (Ollama, LM Studio, vLLM)', hint: 'Nothing leaves your network. An OpenAI-compatible server, e.g. Ollama on http://10.0.0.20:11434 with llama3.1:8b or qwen2.5:14b (16–32 GB RAM, a GPU helps).' },
];

/** System Settings › Assistant: which AI answers, live data, who may use it. */
export function AssistantSettings() {
  const cfg = useApi(() => api.get<AssistantConfig>('/assistant/settings'), []);
  const [form, setForm] = useState<AssistantConfig | null>(null);
  const [apiKey, setApiKey] = useState('');
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (cfg.data) setForm(cfg.data); }, [cfg.data]);

  const set = <K extends keyof AssistantConfig>(k: K, v: AssistantConfig[K]) => setForm((f) => (f ? { ...f, [k]: v } : f));

  async function save(extra: { clearKey?: boolean } = {}) {
    if (!form) return;
    setBusy(true); setMsg(null);
    try {
      await api.put('/assistant/settings', { ...form, apiKey: apiKey || undefined, ...extra });
      setApiKey(''); cfg.reload(); setMsg({ tone: 'ok', text: 'Saved. The Assistant uses the new settings for the next question.' });
    } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setBusy(false); }
  }

  async function test() {
    setBusy(true); setMsg(null);
    try {
      const r = await api.post<{ ok: boolean; message: string }>('/assistant/settings/test');
      setMsg({ tone: r.ok ? 'ok' : 'crit', text: r.message });
    } catch (e) { setMsg({ tone: 'crit', text: errorText(e) }); } finally { setBusy(false); }
  }

  return (
    <Panel title="Zentriq Assistant">
      <ErrorBox error={cfg.error} />
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      {form && (
        <div className="form-v">
          <label className="row-check">
            <input type="checkbox" checked={form.enabled} onChange={(e) => set('enabled', e.target.checked)} />
            <span><b>Show the Assistant in the console</b><br /><small className="muted">The <b>Ask Zentriq</b> button at the bottom right of every page: answers about errors, configuration and how-to.</small></span>
          </label>
          <fieldset className="ai-providers">
            <legend>Who writes the answers</legend>
            {PROVIDERS.map((p) => (
              <label key={p.id} className={`ai-provider${form.provider === p.id ? ' on' : ''}`}>
                <input type="radio" name="ai-provider" checked={form.provider === p.id} onChange={() => set('provider', p.id)} />
                <span><b>{p.label}</b><br /><small className="muted">{p.hint}</small></span>
              </label>
            ))}
          </fieldset>
          {form.provider === 'anthropic' && (
            <div className="grid-2">
              <label>Anthropic API key {form.hasKey && <small className="muted">(stored {form.keyHint ?? ''}; leave empty to keep)</small>}
                <input className="in mono" type="password" autoComplete="off" value={apiKey} onChange={(e) => setApiKey(e.target.value)} placeholder={form.hasKey ? '••••••••' : 'sk-ant-…'} />
              </label>
              <label>Model
                <input className="in mono" value={form.anthropicModel} onChange={(e) => set('anthropicModel', e.target.value)} placeholder="claude-sonnet-5-5" />
              </label>
            </div>
          )}
          {form.provider === 'local' && (
            <div className="grid-2">
              <label>Model server address
                <input className="in mono" value={form.localUrl ?? ''} onChange={(e) => set('localUrl', e.target.value)} placeholder="http://10.0.0.20:11434" />
              </label>
              <label>Model name
                <input className="in mono" value={form.localModel ?? ''} onChange={(e) => set('localModel', e.target.value)} placeholder="llama3.1:8b" />
              </label>
              <label>API key (only if your server asks for one)
                <input className="in mono" type="password" autoComplete="off" value={apiKey} onChange={(e) => setApiKey(e.target.value)} placeholder={form.hasKey ? '••••••••' : 'optional'} />
              </label>
            </div>
          )}
          <label className="row-check">
            <input type="checkbox" checked={form.liveData} onChange={(e) => set('liveData', e.target.checked)} />
            <span><b>Let the Assistant check this server (read-only)</b><br /><small className="muted">Devices, agents, scans, license; server health and logs for administrators. Each user sees only what their role and data scope allow. Nothing is changed.</small></span>
          </label>
          <div className="grid-2">
            <label>Who can use it
              <select className="in" value={form.audience} onChange={(e) => set('audience', e.target.value as AssistantConfig['audience'])}>
                <option value="everyone">Every console user</option>
                <option value="admins">Administrators only</option>
              </select>
            </label>
            <label>Keep questions and answers (days)
              <input className="in" type="number" min={7} max={3650} value={form.keepDays} onChange={(e) => set('keepDays', Number(e.target.value))} style={{ maxWidth: 120 }} />
            </label>
          </div>
          <div className="row-actions">
            <button type="button" className="btn primary" disabled={busy} onClick={() => void save()}>Save</button>
            <button type="button" className="btn" disabled={busy} onClick={() => void test()}>Test connection</button>
            {form.hasKey && <button type="button" className="btn ghost" disabled={busy} onClick={() => void save({ clearKey: true })}>Remove key</button>}
          </div>
          <small className="muted">Every question is in the audit log (<code>assistant.ask</code>); questions and answers are kept for the days above.</small>
        </div>
      )}
    </Panel>
  );
}
