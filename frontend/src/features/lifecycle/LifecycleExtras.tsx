import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, Panel, Pill } from '../../components/ui';
import { DepartmentSelect, useDepartments } from '../departments/Departments';
import { LocationSelect, useLocations } from '../locations/Locations';
import type { LcField, LcImportResult, LcMeta, LcPerson } from './types';

const err = (e: unknown) => (e instanceof Error ? e.message : String(e));

function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label>{label}{children}{hint && <small className="muted">{hint}</small>}</label>;
}

// ---- bulk upload ----------------------------------------------------------------------------------------------------

export type ImportKind = 'assets' | 'people' | 'contracts';
const KIND_TEXT: Record<ImportKind, { title: string; what: string; match: string }> = {
  assets: {
    title: 'Bulk upload IT assets', what: 'IT asset',
    match: 'A row whose asset tag or serial number is already in Zentriq updates that asset (only the filled cells change; a new stage or person is recorded in its history). Other rows are added to the register — In stock unless you give a stage.',
  },
  people: { title: 'Bulk upload people', what: 'person', match: 'A row with an employee ID, e-mail or name already in the directory updates that person.' },
  contracts: { title: 'Bulk upload contracts', what: 'contract', match: 'A row with a contract name already in Zentriq updates that contract. Asset tags in the last column are added to it.' },
};

/** Download the Excel template, fill it, check it (nothing saved), then upload all rows at once. */
export function ImportDialog({ kind, onClose, onDone }: { kind: ImportKind; onClose: () => void; onDone: (msg: string) => void }) {
  const t = KIND_TEXT[kind];
  const [file, setFile] = useState<File | null>(null);
  const [update, setUpdate] = useState(true);
  const [create, setCreate] = useState(true);
  const [check, setCheck] = useState<LcImportResult | null>(null);
  const [done, setDone] = useState<LcImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function run(dryRun: boolean) {
    if (!file) return;
    setBusy(true); setError(null);
    try {
      const r = await api.upload<LcImportResult>(`/lifecycle/import/${kind}`, { dryRun, update, createMissing: create, name: file.name }, file);
      if (dryRun) setCheck(r);
      else { setDone(r); if (r.ok) onDone(`Uploaded ${r.rows} row(s): ${r.created} new, ${r.updated} updated${r.skipped ? `, ${r.skipped} skipped` : ''}.`); }
    } catch (e) { setError(err(e)); } finally { setBusy(false); }
  }
  const result = done ?? check;
  const reset = () => { setCheck(null); setDone(null); };
  return (
    <Modal wide title={t.title} onClose={onClose}
      footer={done?.ok ? <button className="btn primary" onClick={onClose}>Close</button> : <>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn" disabled={!file || busy} onClick={() => void run(true)}>{busy && !check ? 'Checking…' : 'Check file'}</button>
        <button className="btn primary" disabled={!file || busy || !check || !check.ok} onClick={() => void run(false)}>
          {check?.ok ? `Upload ${check.created + check.updated} row(s)` : 'Upload'}
        </button>
      </>}>
      <ol className="steps-v">
        <li>
          <b>Download the template</b>
          <p className="muted">An Excel workbook with the columns, an example row, drop-down lists (device types, stages, departments, locations, people{kind === 'assets' ? ', your custom fields' : ''}) and instructions.</p>
          <a className="btn sm" href={`/api/v1/lifecycle/import/${kind}/template`} download><Icon name="down" size={14} /> Download Excel template</a>
        </li>
        <li>
          <b>Fill one row per {t.what} and save</b>
          <p className="muted">{t.match} Excel (.xlsx) or CSV with the same headings.</p>
        </li>
        <li>
          <b>Choose the file and check it</b>
          <div className="toolbar-row" style={{ marginTop: 6 }}>
            <input type="file" accept=".xlsx,.csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); reset(); }} aria-label="Excel or CSV file" />
          </div>
          <label className="row-check"><input type="checkbox" checked={update} onChange={(e) => { setUpdate(e.target.checked); reset(); }} /> <span>Update {t.what === 'person' ? 'people' : t.what + 's'} that already exist (otherwise skip them)</span></label>
          {kind !== 'contracts' && <label className="row-check"><input type="checkbox" checked={create} onChange={(e) => { setCreate(e.target.checked); reset(); }} /> <span>Create departments and locations that don't exist yet</span></label>}
        </li>
      </ol>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      {result && (
        <div style={{ marginTop: 12 }}>
          {result.ok
            ? <Callout tone="ok" icon="info">{done ? 'Uploaded' : 'Ready to upload'}: {result.rows} row(s) — {result.created} new, {result.updated} updated{result.skipped ? `, ${result.skipped} skipped` : ''}.
                {result.newDepartments.length > 0 && <> New departments: {result.newDepartments.join(', ')}.</>}
                {result.newLocations.length > 0 && <> New locations: {result.newLocations.join(', ')}.</>}
                {kind === 'assets' && result.newPeople.length > 0 && <> Added to People: {result.newPeople.slice(0, 10).join(', ')}{result.newPeople.length > 10 ? ' …' : ''}.</>}</Callout>
            : <Callout tone="crit" icon="alert">{result.errors.length} problem(s) in {result.rows} row(s). Nothing was saved — fix the file and check it again.</Callout>}
          {result.unknownColumns.length > 0 && <p className="faint" style={{ fontSize: 12.5 }}>Columns not used: {result.unknownColumns.join(', ')}</p>}
          {result.errors.length > 0 && (
            <div className="tbl-wrap" style={{ maxHeight: 260, overflow: 'auto' }}>
              <table className="t"><thead><tr><th>Row</th><th>Problem</th></tr></thead>
                <tbody>{result.errors.map((e, i) => <tr key={i}><td className="mono">{e.row}</td><td className="wrap">{e.message}</td></tr>)}</tbody></table>
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}

// ---- people ---------------------------------------------------------------------------------------------------------

interface Suggest { name: string; email: string | null; department: string | null; employeeId: string | null }

/** Name box for assignments: suggests people from the directory (and fills the e-mail); any new name is accepted and added to People. */
export function LcPersonInput({ id, value, onChange, onPicked }: { id: string; value: string; onChange: (v: string) => void; onPicked?: (p: Suggest) => void }) {
  const [q, setQ] = useState(value);
  useEffect(() => { const t = setTimeout(() => setQ(value), 200); return () => clearTimeout(t); }, [value]);
  const list = useApi(() => api.get<Suggest[]>('/lifecycle/people/suggest', { q }), [q]);
  const known = (list.data ?? []).find((p) => p.name.toLowerCase() === value.trim().toLowerCase());
  return (
    <>
      <input className="in" list={id} value={value} placeholder="Name — pick from People or type a new one" autoComplete="off"
        onChange={(e) => {
          onChange(e.target.value);
          const m = (list.data ?? []).find((p) => p.name.toLowerCase() === e.target.value.trim().toLowerCase());
          if (m) onPicked?.(m);
        }} />
      <datalist id={id}>{(list.data ?? []).map((p) => <option key={p.name} value={p.name}>{[p.employeeId, p.department, p.email].filter(Boolean).join(' · ')}</option>)}</datalist>
      {value.trim() && !known && !list.loading && <small className="muted">New person — added to People when saved.</small>}
    </>
  );
}

export function PersonDialog({ person, onClose, onDone }: { person: LcPerson | null; onClose: () => void; onDone: (msg: string) => void }) {
  const deps = useDepartments();
  const locs = useLocations();
  const s = (v: string | null | undefined) => v ?? '';
  const [f, setF] = useState({
    name: s(person?.person), email: s(person?.email), employeeId: s(person?.employeeId), title: s(person?.title), phone: s(person?.phone),
    manager: s(person?.manager), notes: s(person?.notes), status: person?.status ?? 'active',
  });
  const [dep, setDep] = useState(person?.departmentId ?? '');
  const [loc, setLoc] = useState(person?.locationId ?? '');
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value } as typeof f);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = {
        name: f.name, email: f.email.trim() || null, employeeId: f.employeeId.trim() || null, title: f.title.trim() || null, phone: f.phone.trim() || null,
        departmentId: dep || null, locationId: loc || null, manager: f.manager.trim() || null, status: f.status, notes: f.notes.trim() || null,
      };
      if (person?.id) await api.put(`/lifecycle/people/${person.id}`, body); else await api.post('/lifecycle/people', body);
      onDone(person?.id ? `${f.name} saved.` : `${f.name} added to People.`);
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={person?.id ? `Person — ${person.person}` : 'Add a person'} wide onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="lc-person-form" className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <form id="lc-person-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {person?.id && person.assets > 0 && f.name.trim() !== person.person && <Callout icon="info">Renaming also renames the {person.assets} IT asset assignment(s).</Callout>}
        <div className="grid-3 tight">
          <Field label="Name"><input className="in" value={f.name} onChange={set('name')} required maxLength={200} autoFocus /></Field>
          <Field label="E-mail"><input className="in" type="email" value={f.email} onChange={set('email')} maxLength={254} /></Field>
          <Field label="Employee ID"><input className="in mono" value={f.employeeId} onChange={set('employeeId')} maxLength={50} /></Field>
          <Field label="Job title"><input className="in" value={f.title} onChange={set('title')} maxLength={200} /></Field>
          <Field label="Phone"><input className="in" value={f.phone} onChange={set('phone')} maxLength={50} /></Field>
          <Field label="Manager"><input className="in" value={f.manager} onChange={set('manager')} maxLength={200} /></Field>
          <Field label="Department"><DepartmentSelect value={dep} onChange={setDep} list={deps.data?.items ?? []} onAdded={deps.reload} canAdd /></Field>
          <Field label="Location"><LocationSelect value={loc} onChange={setLoc} list={locs.data?.items ?? []} onAdded={locs.reload} canAdd /></Field>
          <Field label="Status">
            <select className="in" value={f.status} onChange={set('status')}><option value="active">Active</option><option value="left">Left the organisation</option></select>
          </Field>
        </div>
        <Field label="Notes"><textarea className="in" rows={2} value={f.notes} onChange={set('notes')} maxLength={4000} /></Field>
      </form>
    </Modal>
  );
}

// ---- asset tags -----------------------------------------------------------------------------------------------------

const TOKENS: [string, string][] = [['{PREFIX}', 'class prefix (LAP, DSK …)'], ['{ORG}', 'organisation code'], ['{LOC}', 'location code'], ['{YYYY}', 'year'], ['{YY}', 'year, 2 digits'], ['{MM}', 'month'], ['{SEQ:5}', 'running number, 5 digits']];
const PRESETS = ['{PREFIX}-{SEQ:6}', '{ORG}-{PREFIX}-{SEQ:5}', '{ORG}/{YYYY}/{SEQ:5}', '{ORG}-{LOC}-{PREFIX}-{SEQ:4}', 'IT{YY}-{SEQ:5}'];

/** Live preview of a tag format (mirrors the server's rendering for the sample). */
function preview(format: string, org: string, start: number) {
  const now = new Date();
  let out = format.replace(/\{(PREFIX|ORG|LOC|YYYY|YY|MM|SEQ(?::(\d{1,2}))?)\}/g, (_m, t: string, n?: string) => {
    switch (t) {
      case 'PREFIX': return 'LAP';
      case 'ORG': return org.replace(/[^A-Za-z0-9._-]/g, '');
      case 'LOC': return 'PUNE';
      case 'YYYY': return String(now.getFullYear());
      case 'YY': return String(now.getFullYear() % 100).padStart(2, '0');
      case 'MM': return String(now.getMonth() + 1).padStart(2, '0');
      default: return String(start).padStart(Number(n ?? 6), '0');
    }
  });
  out = out.replace(/--/g, '-').replace(/\/\//g, '/').replace(/^[-/._]+|[-/._]+$/g, '');
  return out;
}

export function TagSettingsPanel({ meta, onSaved }: { meta: LcMeta; onSaved: () => void }) {
  const { can } = useAuth();
  const canEdit = can('settings.manage');
  const [t, setT] = useState({ mode: meta.tags.mode, format: meta.tags.format, org: meta.tags.org, start: String(meta.tags.start), discovered: meta.tags.discovered });
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  async function save() {
    setBusy(true); setMsg(null);
    try {
      await api.put('/lifecycle/settings', { tagMode: t.mode, tagFormat: t.format, tagOrg: t.org, tagStart: Number(t.start) || 1, tagDiscovered: t.discovered });
      setMsg({ tone: 'ok', text: 'Asset tag settings saved. New tags follow this format; existing tags stay as they are.' });
      onSaved();
    } catch (e) { setMsg({ tone: 'crit', text: err(e) }); } finally { setBusy(false); }
  }
  const auto = t.mode === 'auto';
  return (
    <Panel title="Asset tags" sub="Your organisation's own tags: typed by hand, or given automatically in your format">
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <div className="lc-tagmode" role="radiogroup" aria-label="Asset tags">
        <button type="button" role="radio" aria-checked={auto} className={auto ? 'on' : ''} disabled={!canEdit} onClick={() => setT({ ...t, mode: 'auto' })}>
          <b>Automatic, in our format</b><small>Every new asset (added, uploaded{t.discovered ? ', or found by an agent or scan' : ''}) gets the next tag. Typed or uploaded tags are kept.</small>
        </button>
        <button type="button" role="radio" aria-checked={!auto} className={!auto ? 'on' : ''} disabled={!canEdit} onClick={() => setT({ ...t, mode: 'manual' })}>
          <b>Manual — we use our own tags</b><small>Zentriq never gives a tag by itself. Type tags on the asset, when adding stock, or in the bulk upload.</small>
        </button>
      </div>
      <div className="form-v" style={{ marginTop: 12 }}><div className="grid-3 tight">
        <Field label="Tag format" hint={<>Preview: <b className="mono">{preview(t.format, t.org, Number(t.start) || 1)}</b> (a laptop at location PUNE)</>}>
          <input className="in mono" value={t.format} onChange={(e) => setT({ ...t, format: e.target.value })} disabled={!canEdit} maxLength={60} list="lc-tag-presets" />
          <datalist id="lc-tag-presets">{PRESETS.map((p) => <option key={p} value={p} />)}</datalist>
        </Field>
        <Field label="Organisation code {ORG}" hint="Up to 10 letters and digits, e.g. ACME."><input className="in mono" value={t.org} onChange={(e) => setT({ ...t, org: e.target.value.toUpperCase() })} disabled={!canEdit} maxLength={10} /></Field>
        <Field label="Start numbers at" hint="For each new prefix / year / location. Existing numbering continues."><input className="in" type="number" min={1} max={99999999} value={t.start} onChange={(e) => setT({ ...t, start: e.target.value })} disabled={!canEdit} /></Field>
      </div></div>
      <p className="muted" style={{ fontSize: 12.5, margin: '6px 0' }}>Tokens: {TOKENS.map(([k, v]) => <span key={k} style={{ marginRight: 10 }}><span className="mono">{k}</span> {v}</span>)}. Letters, digits and . _ / - in between. Class prefixes are set in the table below; location codes on Locations.</p>
      <label className="row-check"><input type="checkbox" checked={t.discovered} disabled={!canEdit || !auto} onChange={(e) => setT({ ...t, discovered: e.target.checked })} /> <span>Also tag devices found by agents and scans (hourly)</span></label>
      {canEdit && <div style={{ marginTop: 10 }}><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save tag settings'}</button></div>}
    </Panel>
  );
}

// ---- custom fields --------------------------------------------------------------------------------------------------

const TYPE_LABEL: Record<LcField['type'], string> = { text: 'Text', number: 'Number', date: 'Date', list: 'List of values', yesno: 'Yes / No' };

export function CustomFieldsPanel({ meta, onSaved }: { meta: LcMeta; onSaved: () => void }) {
  const { can } = useAuth();
  const canEdit = can('settings.manage');
  const confirm = useConfirm();
  const [edit, setEdit] = useState<LcField | 'new' | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function remove(f: LcField) {
    if (!(await confirm({ title: 'Delete field', message: `Delete "${f.label}"? Its values on every asset are removed too.`, confirmLabel: 'Delete', danger: true }))) return;
    try { await api.del(`/lifecycle/fields/${f.id}`); onSaved(); } catch (e) { setError(err(e)); }
  }
  return (
    <Panel title="Custom fields" sub="Your own columns on IT assets (cost centre, insurance number, owner team …): shown on the asset, in Details and in the bulk upload template" flush
      right={canEdit && <button className="btn sm" onClick={() => setEdit('new')}><Icon name="plus" size={13} /> Add field</button>}>
      {error && <Callout tone="crit" icon="alert">{error}</Callout>}
      <DataTable rows={meta.fields} empty="No custom fields yet." columns={[
        { key: 'l', label: 'Field', render: (f) => <><b>{f.label}</b>{f.required && <> <Pill tone="info">required</Pill></>}</> },
        { key: 't', label: 'Type', render: (f) => TYPE_LABEL[f.type] },
        { key: 'o', label: 'Values', className: 'wrap', render: (f) => f.options.length ? f.options.join(', ') : '—' },
        ...(canEdit ? [{ key: 'x', label: '', render: (f: LcField) => <span style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
          <button className="btn sm" onClick={() => setEdit(f)}><Icon name="edit" size={13} /></button>
          <button className="btn sm ghost" aria-label={`Delete ${f.label}`} onClick={() => void remove(f)}><Icon name="trash" size={13} /></button></span> }] : []),
      ]} />
      {edit && <FieldDialog field={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onDone={() => { setEdit(null); onSaved(); }} />}
    </Panel>
  );
}

function FieldDialog({ field, onClose, onDone }: { field: LcField | null; onClose: () => void; onDone: () => void }) {
  const [label, setLabel] = useState(field?.label ?? '');
  const [type, setType] = useState<LcField['type']>(field?.type ?? 'text');
  const [options, setOptions] = useState((field?.options ?? []).join('\n'));
  const [required, setRequired] = useState(field?.required ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const body = { label, type, options: options.split(/[\n,]+/).map((x) => x.trim()).filter(Boolean), required, sort: field?.sort ?? null };
      if (field) await api.put(`/lifecycle/fields/${field.id}`, body); else await api.post('/lifecycle/fields', body);
      onDone();
    } catch (x) { setError(err(x)); } finally { setBusy(false); }
  }
  return (
    <Modal title={field ? `Field — ${field.label}` : 'Add a custom field'} onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button type="submit" form="lc-field-form" className="btn primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <form id="lc-field-form" className="form-v" onSubmit={(e) => void submit(e)}>
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <Field label="Name"><input className="in" value={label} onChange={(e) => setLabel(e.target.value)} required maxLength={60} autoFocus placeholder="Cost centre" /></Field>
        <Field label="Type">
          <select className="in" value={type} onChange={(e) => setType(e.target.value as LcField['type'])}>
            {(Object.keys(TYPE_LABEL) as LcField['type'][]).map((k) => <option key={k} value={k}>{TYPE_LABEL[k]}</option>)}
          </select>
        </Field>
        {type === 'list' && <Field label="Values" hint="One per line."><textarea className="in" rows={4} value={options} onChange={(e) => setOptions(e.target.value)} required /></Field>}
        <label className="row-check"><input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} /> <span>Required (asked in Details and the bulk upload)</span></label>
      </form>
    </Modal>
  );
}

/** Inputs for the custom fields (Details dialog). Values are kept as text and checked by the server. */
export function CustomFieldInputs({ fields, values, onChange }: { fields: LcField[]; values: Record<string, string>; onChange: (v: Record<string, string>) => void }) {
  if (fields.length === 0) return null;
  const set = (k: string, v: string) => onChange({ ...values, [k]: v });
  return (
    <div className="grid-3 tight">
      {fields.map((f) => (
        <Field key={f.key} label={f.label + (f.required ? ' *' : '')}>
          {f.type === 'list' ? <select className="in" value={values[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)} required={f.required}>
              <option value="">—</option>{f.options.map((o) => <option key={o} value={o}>{o}</option>)}</select>
            : f.type === 'yesno' ? <select className="in" value={values[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)} required={f.required}>
              <option value="">—</option><option value="Yes">Yes</option><option value="No">No</option></select>
            : <input className="in" type={f.type === 'date' ? 'date' : f.type === 'number' ? 'number' : 'text'} step="any" value={values[f.key] ?? ''}
                onChange={(e) => set(f.key, e.target.value)} required={f.required} maxLength={500} />}
        </Field>
      ))}
    </div>
  );
}

/** Stored custom values as text for the form / display. */
export function customText(v: string | number | boolean | null | undefined): string {
  if (v === true) return 'Yes';
  if (v === false) return 'No';
  return v == null ? '' : String(v);
}
