// Typed client for the Zentriq REST API (/api/v1). Same-origin cookie session; every state-changing
// request carries the anti-CSRF header the API requires.

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown = null,
  ) {
    super(message);
  }
}

type Query = Record<string, string | number | boolean | undefined | null>;

function url(path: string, query?: Query): string {
  const u = new URL(`/api/v1${path}`, window.location.origin);
  if (query) for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== '') u.searchParams.set(k, String(v));
  return u.pathname + u.search;
}

let onUnauthorized: (() => void) | null = null;
/** Called when any request returns 401 (session expired) so the app can show the sign-in page. */
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn;
}

async function request<T>(method: string, path: string, opts: { query?: Query; body?: unknown; allow401?: boolean } = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-Zentriq-Request'] = '1';
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(url(path, opts.query), {
    method,
    headers,
    credentials: 'same-origin',
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }
  if (!res.ok) {
    if (res.status === 401 && !opts.allow401) onUnauthorized?.();
    const title = typeof data === 'object' && data && 'title' in data ? String((data as { title: unknown }).title) : res.statusText || 'Request failed';
    throw new ApiError(res.status, title, data);
  }
  return data as T;
}

export const api = {
  get: <T>(path: string, query?: Query) => request<T>('GET', path, { query }),
  post: <T>(path: string, body?: unknown, allow401 = false) => request<T>('POST', path, { body: body ?? {}, allow401 }),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, { body: body ?? {} }),
  patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, { body: body ?? {} }),
  del: <T>(path: string) => request<T>('DELETE', path),
  /** POST with a JSON body that returns a file (report exports); saves it in the browser. */
  async downloadJson(path: string, body: unknown, fallbackName: string): Promise<void> {
    const res = await fetch(url(path), { method: 'POST', headers: { 'X-Zentriq-Request': '1', 'Content-Type': 'application/json' }, credentials: 'same-origin', body: JSON.stringify(body) });
    if (!res.ok) {
      const text = await res.text();
      let title = res.statusText;
      try { const j = JSON.parse(text) as { title?: string; errors?: Record<string, string[]> }; title = j.errors ? Object.values(j.errors).flat().join(' ') : j.title ?? title; } catch { /* not JSON */ }
      throw new ApiError(res.status, title);
    }
    const cd = res.headers.get('Content-Disposition') ?? '';
    const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/.exec(cd)?.[1] ?? fallbackName;
    saveBlob(await res.blob(), decodeURIComponent(name));
  },
  /** Uploads a file as the raw request body (feed imports). */
  upload: <T>(path: string, query: Query, file: Blob) =>
    fetch(url(path, query), { method: 'POST', headers: { 'X-Zentriq-Request': '1', 'Content-Type': 'application/octet-stream' }, credentials: 'same-origin', body: file })
      .then(async (res) => {
        const text = await res.text();
        let data: unknown = null;
        try { data = text ? JSON.parse(text) : null; } catch { data = text; }
        if (!res.ok) throw new ApiError(res.status, typeof data === 'object' && data && 'title' in data ? String((data as { title: unknown }).title) : res.statusText, data);
        return data as T;
      }),
  /** POST that returns a file (e.g. the activation request) and saves it in the browser. */
  async download(path: string, fallbackName: string): Promise<void> {
    const res = await fetch(url(path), { method: 'POST', headers: { 'X-Zentriq-Request': '1' }, credentials: 'same-origin' });
    if (!res.ok) throw new ApiError(res.status, (await res.text()) || res.statusText);
    const cd = res.headers.get('Content-Disposition') ?? '';
    const name = /filename="?([^";]+)"?/.exec(cd)?.[1] ?? fallbackName;
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  },
};

function saveBlob(blob: Blob, name: string) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}
