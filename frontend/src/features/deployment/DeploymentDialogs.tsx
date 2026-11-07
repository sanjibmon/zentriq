import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Asset, Paged } from '../../api/types';
import { useApi } from '../../app/useApi';
import { Check, Choices, Field, Fields, Section } from '../../components/Form';
import { Modal } from '../../components/Modal';
import { Callout, fmt } from '../../components/ui';
import { useDepartments } from '../departments/Departments';
import { useLocations } from '../locations/Locations';
import { errText, type Group, type Mode, type Package, type Rule, type Wdac } from './types';

// ---- a rule ----------------------------------------------------------------------------------------------------

export function RuleDialog({ rule, groups, onClose, onSaved }: { rule: Rule | null; groups: Group[]; onClose: () => void; onSaved: (m: string) => void }) {
  const [f, setF] = useState({
    action: rule?.action ?? 'allow', label: rule?.label ?? '', name: rule?.name ?? '', publisher: rule?.publisher ?? '', versionMin: rule?.versionMin ?? '',
    versionMax: rule?.versionMax ?? '', sha256: rule?.sha256 ?? '', platform: rule?.platform ?? '', groupId: rule?.groupId ?? '', note: rule?.note ?? '',
    expiresAt: rule?.expiresAt?.slice(0, 10) ?? '',
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (k: keyof typeof f, v: string) => setF({ ...f, [k]: v });
  async function save() {
    setBusy(true); setError(null);
    const body = {
      ...f, label: f.label || null, name: f.name || null, publisher: f.publisher || null, versionMin: f.versionMin || null, versionMax: f.versionMax || null,
      sha256: f.sha256 || null, platform: f.platform || null, groupId: f.groupId || null, assetId: rule?.assetId ?? null, note: f.note || null,
      expiresAt: f.expiresAt ? new Date(f.expiresAt + 'T23:59:59').toISOString() : null,
    };
    try {
      if (rule) await api.put(`/deployment/rules/${rule.id}`, body); else await api.post('/deployment/rules', body);
      onSaved(`${f.label || f.name || f.publisher || 'Rule'} ${f.action === 'allow' ? 'approved' : 'blocked'}.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={rule ? `Edit ${rule.label}` : 'Approve or block software'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || (!f.name.trim() && !f.publisher.trim() && !f.sha256.trim())} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <Section title="Decision">
          <div className="seg2" role="radiogroup" aria-label="Decision">
            <button type="button" role="radio" aria-checked={f.action === 'allow'} className={f.action === 'allow' ? 'on ok' : ''} onClick={() => set('action', 'allow')}>Approve</button>
            <button type="button" role="radio" aria-checked={f.action === 'block'} className={f.action === 'block' ? 'on crit' : ''} onClick={() => set('action', 'block')}>Block <small>even when an approval matches</small></button>
          </div>
        </Section>
        <Section title="Which Software" sub="Every part you fill in must match. Name and publisher accept several values separated by “;”; * and ? are wildcards. A publisher matches the company name of the installer or the name in its valid code-signing certificate.">
          <Fields>
            <Field label="Software name"><input className="in" value={f.name} autoFocus onChange={(e) => set('name', e.target.value)} placeholder="e.g. Notepad++*" /></Field>
            <Field label="Publisher or signing certificate"><input className="in" value={f.publisher} onChange={(e) => set('publisher', e.target.value)} placeholder="e.g. Notepad++ Team" /></Field>
            <Field label="From version" optional><input className="in" value={f.versionMin} onChange={(e) => set('versionMin', e.target.value)} placeholder="Any" /></Field>
            <Field label="Up to version" optional><input className="in" value={f.versionMax} onChange={(e) => set('versionMax', e.target.value)} placeholder="Any" /></Field>
            <Field label="Installer SHA-256" optional span hint="Exactly this file."><input className="in mono" value={f.sha256} onChange={(e) => set('sha256', e.target.value)} placeholder="64 hexadecimal characters" /></Field>
          </Fields>
        </Section>
        <Section title="Where and How Long">
          <Fields>
            <Field label="Platform"><select className="in" value={f.platform} onChange={(e) => set('platform', e.target.value)}><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux (package name)</option></select></Field>
            <Field label="For"><select className="in" value={f.groupId} onChange={(e) => set('groupId', e.target.value)} disabled={!!rule?.assetId}>
              <option value="">{rule?.assetId ? `Device ${rule.device}` : 'All devices'}</option>{groups.map((g) => <option key={g.id} value={g.id}>Group {g.name}</option>)}</select></Field>
            <Field label="Until" optional hint="Empty: no end date."><input className="in" type="date" value={f.expiresAt} onChange={(e) => set('expiresAt', e.target.value)} /></Field>
            <Field label="Shown as" optional><input className="in" value={f.label} onChange={(e) => set('label', e.target.value)} placeholder={f.name || 'Name in the list'} /></Field>
            <Field label="Note" optional span><input className="in" value={f.note} onChange={(e) => set('note', e.target.value)} placeholder="e.g. approved by IT security, ticket 1234" /></Field>
          </Fields>
        </Section>
      </div>
    </Modal>
  );
}

// ---- approve a request or an event ---------------------------------------------------------------------------

export function DecideDialog({ title, item, groups, endpoint, onClose, onDone }: {
  title: string; item: { name: string; publisher: string | null; version: string | null; sha256: string | null; device: string | null; platform: string | null };
  groups: Group[]; endpoint: string; onClose: () => void; onDone: (m: string) => void;
}) {
  const [scope, setScope] = useState<'all' | 'group' | 'device'>('all');
  const [groupId, setGroupId] = useState(groups[0]?.id ?? '');
  const [match, setMatch] = useState(item.publisher && item.platform !== 'linux' ? 'name-publisher' : 'name');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function go() {
    setBusy(true); setError(null);
    try {
      await api.post(endpoint, { scope, groupId: scope === 'group' ? groupId : null, match, note: note || null });
      onDone(`${item.name} approved for ${scope === 'all' ? 'all devices' : scope === 'device' ? item.device : 'the group ' + (groups.find((g) => g.id === groupId)?.name ?? '')}. The device can install it now.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={title} onClose={onClose} footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy || (scope === 'group' && !groupId)} onClick={() => void go()}>Approve</button></>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <div className="pick-head"><b>{item.name}</b>{item.version && ` ${item.version}`} <span className="muted">{item.publisher ?? ''}</span></div>
        <Fields>
          <Field label="Approve for">
            <select className="in" value={scope} onChange={(e) => setScope(e.target.value as typeof scope)}>
              <option value="all">All devices</option>
              {groups.length > 0 && <option value="group">A device group</option>}
              {item.device && <option value="device">Only {item.device}</option>}
            </select>
          </Field>
          {scope === 'group' ? <Field label="Group"><select className="in" value={groupId} onChange={(e) => setGroupId(e.target.value)}>{groups.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}</select></Field> : <span />}
          <Field label="Match" span>
            <select className="in" value={match} onChange={(e) => setMatch(e.target.value)}>
              {item.publisher && item.platform !== 'linux' && <option value="name-publisher">This software from this publisher (any version)</option>}
              <option value="name">This software (any version)</option>
              {item.version && <option value="name-version">Only version {item.version}</option>}
              {item.publisher && <option value="publisher">Everything from {item.publisher}</option>}
              {item.sha256 && <option value="hash">Only this exact file (SHA-256)</option>}
            </select>
          </Field>
          <Field label="Note" optional span><input className="in" value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. ticket 1234" /></Field>
        </Fields>
      </div>
    </Modal>
  );
}

// ---- a device group --------------------------------------------------------------------------------------------

export function GroupDialog({ group, onClose, onSaved }: { group: Group | null; onClose: () => void; onSaved: (m: string) => void }) {
  const deps = useDepartments();
  const locs = useLocations();
  const [f, setF] = useState({
    name: group?.name ?? '', description: group?.description ?? '', departmentIds: group?.departmentIds ?? [], locationIds: group?.locationIds ?? [],
    assetIds: group?.assetIds ?? [], platform: group?.platform ?? '', appMode: (group?.appMode ?? '') as Mode | '', wdacMode: (group?.wdacMode ?? 'off') as Wdac,
  });
  const [devices, setDevices] = useState<{ id: string; name: string }[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const toggle = (k: 'departmentIds' | 'locationIds', id: string) => setF({ ...f, [k]: f[k].includes(id) ? f[k].filter((x) => x !== id) : [...f[k], id] });
  useEffect(() => {
    if (!group?.assetIds.length) return;
    void api.get<{ id: string; hostname: string | null }[]>(`/deployment/groups/${group.id}/devices`).then((l) =>
      setDevices(group.assetIds.map((id) => ({ id, name: l.find((d) => d.id === id)?.hostname ?? id }))));
  }, [group]);
  async function save() {
    setBusy(true); setError(null);
    const body = { ...f, platform: f.platform || null, appMode: f.appMode || null };
    try {
      if (group) await api.put(`/deployment/groups/${group.id}`, body); else await api.post('/deployment/groups', body);
      onSaved(`Group ${f.name} saved; its devices receive the change within seconds.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={group ? `Edit ${group.name}` : 'New device group'} wide onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || !f.name.trim() || f.departmentIds.length + f.locationIds.length + f.assetIds.length === 0} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <Section title="Group">
          <Fields>
            <Field label="Name" required><input className="in" value={f.name} autoFocus onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Finance laptops" /></Field>
            <Field label="Description" optional><input className="in" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} /></Field>
          </Fields>
        </Section>
        <Section title="Members" sub="Devices in any of the chosen departments or locations, plus the devices you add. At least one is needed.">
          <div className="ff"><span className="ff-l">Departments</span>
            <Choices>
              {(deps.data?.items ?? []).map((d) => <Check key={d.id} checked={f.departmentIds.includes(d.id)} onChange={() => toggle('departmentIds', d.id)}>{d.name}</Check>)}
              {deps.data?.items.length === 0 && <span className="muted">No departments yet.</span>}
            </Choices>
          </div>
          <div className="ff"><span className="ff-l">Locations</span>
            <Choices>
              {(locs.data?.items ?? []).map((l) => <Check key={l.id} checked={f.locationIds.includes(l.id)} onChange={() => toggle('locationIds', l.id)}>{l.name}</Check>)}
              {locs.data?.items.length === 0 && <span className="muted">No locations yet.</span>}
            </Choices>
          </div>
          <div className="ff"><span className="ff-l">Devices</span>
            <DevicePicker onPick={(d) => { if (!f.assetIds.includes(d.id)) { setF({ ...f, assetIds: [...f.assetIds, d.id] }); setDevices([...devices, d]); } }} />
            {f.assetIds.length > 0 && <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {f.assetIds.map((id) => <span key={id} className="chip">{devices.find((d) => d.id === id)?.name ?? id}
                <button type="button" className="btn sm" aria-label="Remove" onClick={() => setF({ ...f, assetIds: f.assetIds.filter((x) => x !== id) })}>✕</button></span>)}
            </div>}
          </div>
        </Section>
        <Section title="Application Control" sub="A device in several groups gets the strictest setting.">
          <Fields cols={3}>
            <Field label="Only devices running"><select className="in" value={f.platform} onChange={(e) => setF({ ...f, platform: e.target.value })}><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select></Field>
            <Field label="Application control"><select className="in" value={f.appMode} onChange={(e) => setF({ ...f, appMode: e.target.value as Mode | '' })}>
              <option value="">As for the organisation</option><option value="off">Off for this group</option><option value="audit">Audit</option><option value="block">Block</option></select></Field>
            <Field label="Windows WDAC"><select className="in" value={f.wdacMode} onChange={(e) => setF({ ...f, wdacMode: e.target.value as Wdac })}>
              <option value="off">Off</option><option value="audit">Audit — logs what it would block</option><option value="enforce">Enforce — only Windows and installed programs</option></select></Field>
          </Fields>
          <small className="ff-h">WDAC lets Windows itself refuse programs outside Program Files (downloads, user profiles, USB drives) — portable programs too. Run it in audit for a while first; Windows editions without the ConfigCI module report “not available”.</small>
        </Section>
      </div>
    </Modal>
  );
}

export function DevicePicker({ onPick }: { onPick: (d: { id: string; name: string }) => void }) {
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  useEffect(() => { const t = setTimeout(() => setQ(text), 250); return () => clearTimeout(t); }, [text]);
  const list = useApi(() => (q.trim().length >= 2 ? api.get<Paged<Asset>>('/assets', { q, pageSize: 20 }) : Promise.resolve(null)), [q]);
  const options = (list.data?.items ?? []).map((a) => ({ id: a.id, name: a.hostname ?? a.ip ?? a.id }));
  return (
    <>
      <input className="in" list="dep-devices" value={text} placeholder="Type a hostname to add a device" autoComplete="off"
        onChange={(e) => { setText(e.target.value); const m = options.find((o) => o.name.toLowerCase() === e.target.value.trim().toLowerCase()); if (m) { onPick(m); setText(''); } }} />
      <datalist id="dep-devices">{options.map((o) => <option key={o.id} value={o.name} />)}</datalist>
    </>
  );
}

// ---- a package -------------------------------------------------------------------------------------------------

interface Uploaded { uploadId: string; fileName: string; sha256: string; size: number; format: string; platform: string; suggestedName: string | null; suggestedVersion: string | null; installArgs: string | null; uninstallArgs: string | null }

export function PackageDialog({ pkg, onClose, onSaved }: { pkg: Package | null; onClose: () => void; onSaved: (m: string) => void }) {
  const [up, setUp] = useState<Uploaded | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [f, setF] = useState({
    name: pkg?.name ?? '', version: pkg?.version ?? '', publisher: pkg?.publisher ?? '', installArgs: pkg?.installArgs ?? '', uninstallArgs: pkg?.uninstallArgs ?? '',
    detectName: pkg?.detectName ?? '', notes: pkg?.notes ?? '', approve: true, active: pkg?.active ?? true,
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const format = pkg?.format ?? up?.format;
  async function upload(file: File) {
    setError(null); setProgress(`Uploading ${file.name} (${fmt.bytes(file.size)})…`);
    try {
      const r = await api.upload<Uploaded>('/deployment/packages/upload', { fileName: file.name }, file);
      setUp(r);
      setF((x) => ({ ...x, name: x.name || r.suggestedName || '', version: x.version || r.suggestedVersion || '', installArgs: r.installArgs ?? '', uninstallArgs: r.uninstallArgs ?? '',
        detectName: x.detectName || (r.platform === 'linux' ? r.suggestedName ?? '' : '') }));
      setProgress(null);
    } catch (e) { setError(errText(e)); setProgress(null); }
  }
  async function save() {
    setBusy(true); setError(null);
    const body = { ...f, uploadId: up?.uploadId, version: f.version || null, publisher: f.publisher || null, installArgs: f.installArgs || null, uninstallArgs: f.uninstallArgs || null,
      detectName: f.detectName || null, notes: f.notes || null };
    try {
      if (pkg) await api.put(`/deployment/packages/${pkg.id}`, body); else await api.post('/deployment/packages', body);
      onSaved(`${f.name} ${pkg ? 'saved' : 'added'}.${!pkg && f.approve ? ' It is on the approved list.' : ''}`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={pkg ? `Edit ${pkg.name}` : 'Upload a package'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || !f.name.trim() || (!pkg && !up)} onClick={() => void save()}>{busy ? 'Saving…' : pkg ? 'Save' : 'Add package'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        {!pkg && <Section title="Installer" sub="MSI or EXE for Windows, DEB or RPM for Linux. It is checked and stored on the server.">
          <label className={`dropzone${up ? ' done' : ''}`}>
            <input type="file" accept=".msi,.exe,.deb,.rpm" disabled={!!progress} onChange={(e) => { const file = e.target.files?.[0]; if (file) void upload(file); }} />
            <span className="dz-t">{up ? up.fileName : 'Choose an installer…'}</span>
            <span className="dz-s">{up ? `${up.format.toUpperCase()} · ${fmt.bytes(up.size)} · SHA-256 ${up.sha256.slice(0, 16)}…` : '.msi  .exe  .deb  .rpm'}</span>
          </label>
          {progress && <Callout tone="info" icon="upload">{progress}</Callout>}
        </Section>}
        {(pkg || up) && <>
          <Section title="Software">
            <Fields>
              <Field label="Software name" required><input className="in" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></Field>
              <Field label="Version" optional><input className="in" value={f.version} onChange={(e) => setF({ ...f, version: e.target.value })} /></Field>
              <Field label="Publisher" optional><input className="in" value={f.publisher} onChange={(e) => setF({ ...f, publisher: e.target.value })} /></Field>
              <Field label={format === 'deb' || format === 'rpm' ? 'Package name' : 'Installs under the name'} hint="For uninstall and the approved software list."><input className="in" value={f.detectName} onChange={(e) => setF({ ...f, detectName: e.target.value })} placeholder={f.name} /></Field>
            </Fields>
          </Section>
          {(format === 'msi' || format === 'exe') && <Section title="Silent Installation">
            <Fields>
              <Field label="Install switches" span={format !== 'msi'} hint={format === 'msi' ? 'msiexec /i <package> plus these (properties such as ALLUSERS=1 too).' : 'Setup programs install silently only with their own switch: NSIS /S, Inno Setup /VERYSILENT /NORESTART, InstallShield /s, WiX bundles /quiet.'}>
                <input className="in mono" value={f.installArgs} onChange={(e) => setF({ ...f, installArgs: e.target.value })} placeholder={format === 'msi' ? '/qn /norestart' : 'e.g. /S'} /></Field>
              {format === 'msi' && <Field label="Uninstall switches"><input className="in mono" value={f.uninstallArgs} onChange={(e) => setF({ ...f, uninstallArgs: e.target.value })} placeholder="/qn /norestart" /></Field>}
            </Fields>
          </Section>}
          <Section title="Options">
            <Field label="Notes" optional><input className="in" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} /></Field>
            {!pkg && <Check checked={f.approve} onChange={(v) => setF({ ...f, approve: v })} hint="So it is not reported or removed once installed.">Add it to the approved software</Check>}
            {pkg && <Check checked={f.active} onChange={(v) => setF({ ...f, active: v })}>Can be deployed</Check>}
          </Section>
        </>}
      </div>
    </Modal>
  );
}

// ---- a deployment -----------------------------------------------------------------------------------------------

export function DeployDialog({ pkg, groups, onClose, onDone }: { pkg: Package; groups: Group[]; onClose: () => void; onDone: (m: string) => void }) {
  const deps = useDepartments();
  const [operation, setOperation] = useState<'install' | 'uninstall'>('install');
  const [to, setTo] = useState<'devices' | 'groups' | 'departments' | 'all'>('devices');
  const [assets, setAssets] = useState<{ id: string; name: string }[]>([]);
  const [groupIds, setGroupIds] = useState<string[]>([]);
  const [departmentIds, setDepartmentIds] = useState<string[]>([]);
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const none = to === 'devices' ? assets.length === 0 : to === 'groups' ? groupIds.length === 0 : to === 'departments' ? departmentIds.length === 0 : false;
  async function go() {
    setBusy(true); setError(null);
    try {
      const r = await api.post<{ sent: number; skipped: number }>('/deployment/jobs', {
        packageId: pkg.id, operation, note: note || null, all: to === 'all',
        assetIds: to === 'devices' ? assets.map((a) => a.id) : [], groupIds: to === 'groups' ? groupIds : [], departmentIds: to === 'departments' ? departmentIds : [],
      });
      onDone(`${operation === 'install' ? 'Installing' : 'Uninstalling'} ${pkg.name} on ${r.sent} device(s)${r.skipped ? `; ${r.skipped} skipped (no agent, other platform or an older agent)` : ''}. Follow it in Deployments.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  return (
    <Modal title={`Deploy ${pkg.name}${pkg.version ? ' ' + pkg.version : ''}`} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy || none} onClick={() => void go()}>{busy ? 'Starting…' : operation === 'install' ? 'Install' : 'Uninstall'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <Section title="Action">
          <div className="seg2" role="radiogroup" aria-label="Action">
            <button type="button" role="radio" aria-checked={operation === 'install'} className={operation === 'install' ? 'on' : ''} onClick={() => setOperation('install')}>Install</button>
            <button type="button" role="radio" aria-checked={operation === 'uninstall'} className={operation === 'uninstall' ? 'on crit' : ''} onClick={() => setOperation('uninstall')}>Uninstall</button>
          </div>
        </Section>
        <Section title="Devices" sub="Online devices start within seconds; the others when they next connect (up to 7 days). Devices of the other platform, without the Zentriq Agent or with an older agent are skipped and listed.">
          <Field label="Apply to">
            <select className="in" value={to} onChange={(e) => setTo(e.target.value as typeof to)}>
              <option value="devices">Devices I choose</option>
              {groups.length > 0 && <option value="groups">Device groups</option>}
              <option value="departments">Departments</option>
              <option value="all">All {pkg.platform === 'linux' ? 'Linux' : 'Windows'} devices with the Zentriq Agent</option>
            </select>
          </Field>
          {to === 'devices' && <>
            <DevicePicker onPick={(d) => { if (!assets.some((a) => a.id === d.id)) setAssets([...assets, d]); }} />
            {assets.length > 0 && <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>{assets.map((a) => <span key={a.id} className="chip">{a.name}<button type="button" className="btn sm" aria-label="Remove" onClick={() => setAssets(assets.filter((x) => x.id !== a.id))}>✕</button></span>)}</div>}
          </>}
          {to === 'groups' && <Choices>{groups.map((g) => <Check key={g.id} checked={groupIds.includes(g.id)} onChange={() => setGroupIds(groupIds.includes(g.id) ? groupIds.filter((x) => x !== g.id) : [...groupIds, g.id])}>{g.name} ({g.devices})</Check>)}</Choices>}
          {to === 'departments' && <Choices>{(deps.data?.items ?? []).map((d) => <Check key={d.id} checked={departmentIds.includes(d.id)} onChange={() => setDepartmentIds(departmentIds.includes(d.id) ? departmentIds.filter((x) => x !== d.id) : [...departmentIds, d.id])}>{d.name}</Check>)}</Choices>}
          <Field label="Note" optional><input className="in" value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. change 4711" /></Field>
        </Section>
      </div>
    </Modal>
  );
}
