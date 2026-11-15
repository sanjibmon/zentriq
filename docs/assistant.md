# Zentriq Assistant

From build 1.1.1.146 every console page has an **Ask Zentriq** button (bottom right). The Assistant answers questions
about Zentriq — an error message, a setting, how to do something — from the Zentriq documentation and, when allowed,
from this installation's live state. It never changes anything: it tells you what to change and where.

## Who writes the answers (System Settings › Zentriq Assistant)

| Option | What it needs | What leaves the server |
|---|---|---|
| **Documentation search** (default) | Nothing | Nothing. The documentation is built into the server; answers quote the matching sections and add facts from this server. Works on air-gapped servers. |
| **Claude** (Anthropic API) | Your Anthropic API key (console.anthropic.com › API keys), internet on TCP 443 to `api.anthropic.com` (the server's proxy settings apply) | The question, the conversation so far, the most relevant documentation passages and the results of the read-only checks the model asks for. Passwords, keys, tokens, private keys, enrollment and license codes are removed first. |
| **Local model** | An OpenAI-compatible server on your network: Ollama (`http://host:11434`), LM Studio (`http://host:1234`), vLLM, LocalAI; a model that supports tool calls (for example `llama3.1:8b`, `qwen2.5:14b`); 16–32 GB RAM, a GPU helps | Nothing outside your network. |

*Test connection* sends a one-word request to the model. If the model cannot be reached or refuses the key, each
answer falls back to the documentation search and says why. The key is stored encrypted (AES-256-GCM with the
installation's credential key) and is never shown again; *Remove key* deletes it.

## Live data (read-only)

With *Let the Assistant check this server* on (default), the Assistant can look at:

| Check | Who | What |
|---|---|---|
| License | everyone | mode, edition, sections, days left, limits, warnings |
| Find devices | users with *assets.view* | status, last contact, inventory, the agent (version, platform, last check-in) and, for one device, its latest scan result with the reason inventory failed |
| Agent problems | *assets.view* | agents that have not checked in for a day, counts and versions |
| Recent scans | *assets.view* | the latest scans, results per status, reasons for failures |
| Server health | administrators (*settings.manage*) with access to all devices | worker heartbeat, background jobs failed in 24 hours, the latest errors in the API and Worker logs |

Each user sees only what their role and data scope allow (device checks run on the user's own scoped connection).
With the documentation search, devices named in the question (`LAPTOP-104`, an IP address) and words such as *error*,
*failed*, *offline*, *scan* or *license* add these facts to the answer. With a model, the model decides which checks
it needs.

## Who can use it, history and audit

* *Who can use it*: every console user (default) or administrators only. *Show the Assistant* off hides it.
* 20 questions a minute per user.
* Every question is in the audit log (`assistant.ask`, with the provider and the checks used); settings changes as
  `assistant.settings`, tests as `assistant.test`.
* Questions and answers are kept 365 days by default (7–3650), with the user's ▲/▼ rating. Users with *audit.view*
  read them with `GET /api/v1/assistant/history`.

## Limits

* Answers can be wrong: check before changing production settings. The documentation search quotes the documentation;
  it does not reason about combinations.
* The conversation is kept in the browser tab only (until *New conversation* or the tab is closed).

## API

`GET /api/v1/assistant/status`, `POST /assistant/ask {question, history:[{role, text}]}` → `{id, text, sources, provider,
tools, notice}`, `POST /assistant/messages/{id}/rating {rating: 1|-1}`; with *settings.manage*: `GET|PUT
/assistant/settings`, `POST /assistant/settings/test`; with *audit.view*: `GET /assistant/history`.

## Tests

Unit tests (`Release145Tests`): the documentation is built in and split into sections; the search finds the right
document for typical questions; secrets are removed; Claude and a local model call a tool and answer with its result;
a refused key is explained; the system prompt carries the documentation and the rules. Windows acceptance test T103:
an offline answer with sources, live facts for a named device, settings validation, a write-only key, the fallback when
the model is not reachable, history and audit.
