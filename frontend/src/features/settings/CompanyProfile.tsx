import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../../api/client';
import type { OrganizationProfile, OrganizationResponse } from '../../api/types';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { Callout, ErrorBox, fmt, Loading, Panel } from '../../components/ui';

type Form = Omit<OrganizationProfile, 'id' | 'updatedAt'>;
const EMPTY: Form = { name: '', legalName: '', addressLine1: '', addressLine2: '', city: '', stateRegion: '', postalCode: '', country: '', taxId: '', website: '', contactName: '', contactEmail: '', contactPhone: '' };

const FIELDS: [keyof Form, string, string?][] = [
  ['name', 'Company name'], ['legalName', 'Registered legal name'],
  ['contactName', 'Primary contact'], ['contactEmail', 'Contact email', 'email'], ['contactPhone', 'Contact phone', 'tel'], ['website', 'Website', 'url'],
  ['addressLine1', 'Address line 1'], ['addressLine2', 'Address line 2'], ['city', 'City'], ['stateRegion', 'State / region'],
  ['postalCode', 'Postal code'], ['country', 'Country'], ['taxId', 'Tax ID (e.g. GSTIN / VAT)'],
];

/** Company (customer) profile. Filled from a production license's signed licensee details and editable by administrators. */
export function CompanyProfile() {
  const { can } = useAuth();
  const edit = can('settings.manage');
  const org = useApi(() => api.get<OrganizationResponse>('/organization'), []);
  const [form, setForm] = useState<Form>(EMPTY);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);

  useEffect(() => {
    const p = org.data?.profile;
    if (p) setForm(Object.fromEntries(Object.keys(EMPTY).map((k) => [k, (p as unknown as Record<string, string | null>)[k] ?? ''])) as unknown as Form);
  }, [org.data]);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMsg(null);
    try {
      await api.put('/organization', Object.fromEntries(Object.entries(form).map(([k, v]) => [k, (v as string).trim() || null])));
      setMsg({ tone: 'ok', text: 'Company profile saved.' });
      org.reload();
    } catch (err) {
      const b = err instanceof ApiError ? (err.body as { errors?: Record<string, string[]> } | null) : null;
      setMsg({ tone: 'crit', text: b?.errors ? Object.values(b.errors).flat().join(' ') : err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  }

  const l = org.data?.licensee;
  return (
    <Panel title="Company profile" sub={org.data?.profile ? `Updated ${fmt.date(org.data.profile.updatedAt)}` : undefined}>
      <ErrorBox error={org.error} />
      {!org.data ? <Loading /> : (
        <form className="form-v" onSubmit={save}>
          {l ? (
            <Callout tone="ok" icon="key">Licensed to <b>{l.company}</b>{l.contactName ? ` · ${l.contactName}` : ''}{l.contactEmail ? ` (${l.contactEmail})` : ''}. Empty fields below were filled from the license; you can edit them.</Callout>
          ) : (
            <Callout>When you import a production license, the company and contact details it carries are filled in here automatically.</Callout>
          )}
          {msg && <Callout tone={msg.tone} icon={msg.tone === 'ok' ? 'info' : 'alert'}>{msg.text}</Callout>}
          <div className="form-grid">
            {FIELDS.map(([k, label, type]) => (
              <label key={k}>{label}
                <input className="in" type={type ?? 'text'} value={form[k] ?? ''} required={k === 'name'} maxLength={k === 'name' ? 200 : 300} disabled={!edit}
                  onChange={(e) => setForm((f) => ({ ...f, [k]: e.target.value }))} />
              </label>
            ))}
          </div>
          {edit && <div><button className="btn primary" type="submit" disabled={busy || !form.name.trim()}>{busy ? 'Saving…' : 'Save profile'}</button></div>}
        </form>
      )}
    </Panel>
  );
}
