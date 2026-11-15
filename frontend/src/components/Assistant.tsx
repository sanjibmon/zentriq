// Zentriq Assistant (build 145): the chat help in every console page. Answers come from the Zentriq documentation and,
// when allowed, from this installation's live state (read-only); with an AI model configured, the model writes them.
import { Fragment, useEffect, useRef, useState, type ReactNode } from 'react';
import { api, ApiError } from '../api/client';
import { Icon } from './Icon';

interface AssistantStatus { enabled: boolean; available: boolean; provider: string; providerLabel: string; liveData: boolean; suggestions: string[] }
interface AnswerSource { title: string; heading: string; anchor: string }
interface Answer { id: number | null; text: string; sources: AnswerSource[]; provider: string; tools: string[]; notice: string | null }
interface Msg { role: 'user' | 'assistant'; text: string; answer?: Answer; error?: boolean; rating?: number }

const KEY = 'zq.assistant.chat';

function loadChat(): Msg[] {
  try { const s = sessionStorage.getItem(KEY); return s ? (JSON.parse(s) as Msg[]).slice(-40) : []; } catch { return []; }
}
function saveChat(m: Msg[]) {
  try { sessionStorage.setItem(KEY, JSON.stringify(m.slice(-40))); } catch { /* storage unavailable */ }
}

export function AssistantLauncher() {
  const [status, setStatus] = useState<AssistantStatus | null>(null);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    let alive = true;
    api.get<AssistantStatus>('/assistant/status').then((s) => { if (alive) setStatus(s); }).catch(() => { /* not available */ });
    return () => { alive = false; };
  }, []);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);
  if (!status?.available) return null;
  return (
    <>
      {!open && (
        <button type="button" className="ai-fab" onClick={() => setOpen(true)} aria-label="Ask Zentriq Assistant" title="Ask Zentriq Assistant">
          <Icon name="chat" size={20} /><span>Ask Zentriq</span>
        </button>
      )}
      {open && <AssistantPanel status={status} onClose={() => setOpen(false)} />}
    </>
  );
}

function AssistantPanel({ status, onClose }: { status: AssistantStatus; onClose: () => void }) {
  const [msgs, setMsgs] = useState<Msg[]>(loadChat);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => { saveChat(msgs); listRef.current?.scrollTo({ top: listRef.current.scrollHeight, behavior: 'smooth' }); }, [msgs]);
  useEffect(() => { inputRef.current?.focus(); }, []);

  const ask = async (q: string) => {
    const question = q.trim();
    if (!question || busy) return;
    const history = msgs.filter((m) => !m.error).slice(-12).map((m) => ({ role: m.role, text: m.text }));
    setMsgs((m) => [...m, { role: 'user', text: question }]);
    setText('');
    setBusy(true);
    try {
      const a = await api.post<Answer>('/assistant/ask', { question, history });
      setMsgs((m) => [...m, { role: 'assistant', text: a.text, answer: a }]);
    } catch (e) {
      const msg = e instanceof ApiError
        ? (e.status === 429 ? 'Too many questions in a minute. Wait a moment and ask again.' : e.message)
        : 'The Assistant could not be reached.';
      setMsgs((m) => [...m, { role: 'assistant', text: msg, error: true }]);
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  };

  const rate = async (i: number, rating: number) => {
    const id = msgs[i]?.answer?.id;
    if (!id) return;
    setMsgs((m) => m.map((x, j) => (j === i ? { ...x, rating } : x)));
    try { await api.post(`/assistant/messages/${id}/rating`, { rating }); } catch { /* ignore */ }
  };

  return (
    <aside className="ai-panel" role="dialog" aria-label="Zentriq Assistant">
      <header className="ai-head">
        <span className="ai-mark"><Icon name="chat" size={16} /></span>
        <div className="ai-title">
          <b>Zentriq Assistant</b>
          <small>{status.providerLabel}{status.liveData ? ' · reads this server (read-only)' : ''}</small>
        </div>
        <button type="button" className="icon-btn" title="New conversation" aria-label="New conversation" onClick={() => setMsgs([])} disabled={busy || msgs.length === 0}><Icon name="retry" size={15} /></button>
        <button type="button" className="icon-btn" title="Close" aria-label="Close" onClick={onClose}><Icon name="x" size={15} /></button>
      </header>
      <div className="ai-list" ref={listRef}>
        {msgs.length === 0 && (
          <div className="ai-welcome">
            <p>Ask about an error, a setting or how to do something in Zentriq. {status.liveData ? 'I can also check devices, agents, scans and the server for you.' : ''}</p>
            <div className="ai-sugg">
              {status.suggestions.map((s) => <button key={s} type="button" onClick={() => void ask(s)}>{s}</button>)}
            </div>
          </div>
        )}
        {msgs.map((m, i) => (
          <div key={i} className={`ai-msg ${m.role}${m.error ? ' err' : ''}`}>
            {m.role === 'user' ? <div className="ai-bubble">{m.text}</div> : (
              <div className="ai-bubble">
                {m.answer?.notice && <div className="ai-notice"><Icon name="info" size={13} /> {m.answer.notice}</div>}
                <Markdown text={m.text} />
                {m.answer && m.answer.sources.length > 0 && (
                  <div className="ai-sources">
                    <span>Sources</span>
                    {m.answer.sources.map((s) => <span key={s.anchor} className="ai-src" title={s.anchor}>{s.title} › {s.heading}</span>)}
                  </div>
                )}
                {m.answer && (
                  <div className="ai-foot">
                    <small>{m.answer.provider}{m.answer.tools.length > 0 ? ' · checked this server' : ''}</small>
                    {m.answer.id && (
                      <span className="ai-rate">
                        <button type="button" aria-label="Helpful" title="Helpful" className={m.rating === 1 ? 'on' : ''} onClick={() => void rate(i, 1)}>▲</button>
                        <button type="button" aria-label="Not helpful" title="Not helpful" className={m.rating === -1 ? 'on' : ''} onClick={() => void rate(i, -1)}>▼</button>
                      </span>
                    )}
                  </div>
                )}
              </div>
            )}
          </div>
        ))}
        {busy && <div className="ai-msg assistant"><div className="ai-bubble ai-typing"><i /><i /><i /></div></div>}
      </div>
      <form className="ai-input" onSubmit={(e) => { e.preventDefault(); void ask(text); }}>
        <label htmlFor="ai-q" className="sr-only">Your question</label>
        <textarea id="ai-q" ref={inputRef} rows={2} value={text} maxLength={4000} placeholder="Ask a question or paste an error…"
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void ask(text); } }} />
        <button type="submit" className="btn primary" disabled={busy || !text.trim()} aria-label="Send"><Icon name="play" size={14} /></button>
      </form>
      <div className="ai-disclaimer">Answers can be wrong; check before changing production settings. Passwords and keys are removed before anything is sent.</div>
    </aside>
  );
}

// ---- a small, safe Markdown renderer (no HTML from the answer is ever inserted) --------------------------------

function inline(s: string, key: string): ReactNode[] {
  const out: ReactNode[] = [];
  const rx = /(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\([^)\s]+\))/g;
  let last = 0;
  let m: RegExpExecArray | null;
  let n = 0;
  while ((m = rx.exec(s)) !== null) {
    if (m.index > last) out.push(s.slice(last, m.index));
    const t = m[0];
    if (t.startsWith('`')) out.push(<code key={`${key}-${n++}`}>{t.slice(1, -1)}</code>);
    else if (t.startsWith('**')) out.push(<b key={`${key}-${n++}`}>{t.slice(2, -2)}</b>);
    else {
      const lm = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(t)!;
      const href = lm[2];
      const safe = href.startsWith('/') || href.startsWith('https://');
      out.push(safe ? <a key={`${key}-${n++}`} href={href} target={href.startsWith('/') ? undefined : '_blank'} rel="noreferrer">{lm[1]}</a> : lm[1]);
    }
    last = m.index + t.length;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}

export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: ReactNode[] = [];
  let i = 0;
  let k = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim().startsWith('```')) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith('```')) code.push(lines[i++]);
      i++;
      blocks.push(<pre key={k++} className="ai-code"><code>{code.join('\n')}</code></pre>);
      continue;
    }
    const h = /^(#{1,4})\s+(.*)$/.exec(line);
    if (h) { blocks.push(<div key={k++} className="ai-h">{inline(h[2], `h${k}`)}</div>); i++; continue; }
    if (/^\s*([-*•]|\d+[.)])\s+/.test(line)) {
      const ordered = /^\s*\d+[.)]\s+/.test(line);
      const items: string[] = [];
      while (i < lines.length && /^\s*([-*•]|\d+[.)])\s+/.test(lines[i])) {
        let item = lines[i].replace(/^\s*([-*•]|\d+[.)])\s+/, '');
        i++;
        while (i < lines.length && /^\s{2,}\S/.test(lines[i]) && !/^\s*([-*•]|\d+[.)])\s+/.test(lines[i])) item += ' ' + lines[i++].trim();
        items.push(item);
      }
      const li = items.map((it, j) => <li key={j}>{inline(it, `l${k}-${j}`)}</li>);
      blocks.push(ordered ? <ol key={k++}>{li}</ol> : <ul key={k++}>{li}</ul>);
      continue;
    }
    if (line.trim().startsWith('|')) {
      const rows: string[][] = [];
      while (i < lines.length && lines[i].trim().startsWith('|')) {
        const cells = lines[i].trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
        if (!cells.every((c) => /^:?-{2,}:?$/.test(c))) rows.push(cells);
        i++;
      }
      blocks.push(
        <div key={k++} className="ai-table"><table><tbody>
          {rows.map((r, j) => <tr key={j}>{r.map((c, x) => (j === 0 ? <th key={x}>{inline(c, `t${k}-${j}-${x}`)}</th> : <td key={x}>{inline(c, `t${k}-${j}-${x}`)}</td>))}</tr>)}
        </tbody></table></div>,
      );
      continue;
    }
    if (!line.trim()) { i++; continue; }
    const para: string[] = [];
    while (i < lines.length && lines[i].trim() && !/^(#{1,4})\s|^\s*([-*•]|\d+[.)])\s+|^\s*```|^\s*\|/.test(lines[i])) para.push(lines[i++]);
    blocks.push(<p key={k++}>{para.map((p, j) => <Fragment key={j}>{j > 0 && <br />}{inline(p, `p${k}-${j}`)}</Fragment>)}</p>);
  }
  return <div className="ai-md">{blocks}</div>;
}
