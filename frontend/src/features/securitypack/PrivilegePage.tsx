import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Check, Field, Fields, Section } from '../../components/Form';
import { Icon } from '../../components/Icon';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { AddOnNotice, ModeChoice, Policies, useMessages, useSecurityPack, type AreaPolicy, type Group } from './common';

interface Settings { mode: 'off' | 'audit' | 'enforce'; allowedAdmins: string[]; elevation: boolean; requests: boolean; maxAdminMinutes: number }
interface Overview { policies: AreaPolicy<Settings>[]; kpis: { devices: number; reporting: number; toRemove: number; devicesWithExtraAdmins: number; removed: number; elevations: number; refused: number; pending: number; grants: number; rules: number } }
interface Admin { assetId: string; hostname: string; member: string; sid: string | null; kind: string | null; status: string; reportedAt: string }
interface Rule { id: string; name: string; kind: 'hash' | 'publisher' | 'path'; value: string; fileName: string | null; groupId: string | null; groupName: string | null; assetId: string | null; hostname: string | null; usesLeft: number | null; expiresAt: string | null; enabled: boolean; note: string | null; createdBy: string | null; createdAt: string; used: number }
interface Request { id: string; assetId: string; hostname: string; user: string; kind: 'elevate' | 'admin'; path: string | null; fileName: string | null; sha256: string | null; publisher: string | null; product: string | null; minutes: number | null; reason: string | null; status: string; decidedBy: string | null; decidedAt: string | null; decision: string | null; createdAt: string }
interface Grant { id: string; assetId: string; hostname: string; user: string; startsAt: string; endsAt: string; revokedAt: string | null; revokedBy: string | null; reason: string | null; createdBy: string | null; active: boolean }
interface Ev { id: number; at: string; assetId: string; hostname: string; user: string | null; kind: string; path: string | null; sha256: string | null; publisher: string | null; ruleName: string | null; detail: string | null }

const MODE: Record<string, string> = { off: 'Off', audit: 'Audit', enforce: 'Enforce' };
const ADMIN_STATUS: Record<string, [string, '' | 'ok' | 'warn' | 'crit' | 'info']> = {
  kept: ['Built-in / domain', ''], allowed: ['Allowed', 'ok'], granted: ['Rights for a while', 'info'], to_remove: ['Would be removed', 'warn'], removed: ['Removed', 'crit'],
};
const EVENT: Record<string, [string, '' | 'ok' | 'warn' | 'crit' | 'info']> = {
  elevated: ['Run as administrator', 'ok'], refused: ['Not approved', 'warn'], failed: ['Failed', 'crit'], admin_added: ['Rights given', 'info'],
  admin_removed: ['Rights ended', ''], member_removed: ['Administrator removed', 'crit'], member_kept: ['Kept', ''],
};
const PAGE = 50;
type Tab = 'policy' | 'admins' | 'rules' | 'requests' | 'grants' | 'activity';
const TABS: [Tab, string][] = [['policy', 'Policy'], ['admins', 'Local Administrators'], ['rules', 'Approved Applications'], ['requests', 'Requests'], ['grants', 'Rights for a While'], ['activity', 'Activity']];
const ago = (m: number) => (m < 60 ? `${m} min` : m % 60 === 0 ? `${m / 60} h` : `${Math.floor(m / 60)} h ${m % 60} min`);

export function PrivilegePage() {
  const licensed = useSecurityPack();
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const canManage = can('privilege.manage');
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'policy';
  const m = useMessages();
  const data = useApi(() => (licensed ? api.get<Overview>('/security-pack/privilege') : Promise.resolve(null)), [licensed, m.tick]);
  const k = data.data?.kpis;
  return (
    <>
      <PageHead title="Endpoint Privilege Management" icon="elevate" />
      {!licensed && <AddOnNotice />}
      {licensed && <>
        <ErrorBox error={data.error} />
        {k && <Kpis items={[
          { label: 'Devices', value: k.devices.toLocaleString(), sub: `${k.reporting} report their administrators` },
          { label: 'Extra Administrators', value: k.toRemove.toLocaleString(), sub: `On ${k.devicesWithExtraAdmins} device(s) · ${k.removed} removed in 30 days` },
          { label: 'Run as Administrator', value: k.elevations.toLocaleString(), sub: `${k.refused} not approved (7 days)` },
          { label: 'Requests Waiting', value: k.pending.toLocaleString(), sub: 'Applications and administrator rights' },
          { label: 'Rights for a While', value: k.grants.toLocaleString(), sub: `Active now · ${k.rules} approved application rule(s)` },
        ]} />}
        {m.view}
        <div className="tabs" role="tablist">
          {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/security-pack/privilege?tab=${t}`, true)}>{l}{t === 'requests' && k && k.pending > 0 && <span className="cnt">{k.pending}</span>}</button>)}
        </div>
        {tab === 'policy' && data.data && <PolicyTab policies={data.data.policies} canManage={canManage} changed={m.changed} failed={m.failed} />}
        {tab === 'admins' && <AdminsTab />}
        {tab === 'rules' && <RulesTab canManage={canManage} tick={m.tick} changed={m.changed} failed={m.failed} />}
        {tab === 'requests' && <RequestsTab canManage={canManage} tick={m.tick} changed={m.changed} failed={m.failed} />}
        {tab === 'grants' && <GrantsTab canManage={canManage} tick={m.tick} changed={m.changed} failed={m.failed} />}
        {tab === 'activity' && <ActivityTab />}
      </>}
    </>
  );
}

interface TabProps { canManage: boolean; tick: number; changed: (t: string) => void; failed: (e: unknown) => void }

function PolicyTab({ policies, canManage, changed, failed }: { policies: AreaPolicy<Settings>[]; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }) {
  const [edit, setEdit] = useState<{ p: AreaPolicy<Settings> | null; groupId: string | null } | null>(null);
  return (
    <>
      <Policies area="privilege" policies={policies} canManage={canManage} modeLabel={MODE} modeTone={(x) => (x === 'enforce' ? 'ok' : x === 'audit' ? 'warn' : '')}
        describe={(s) => <>{s.elevation ? 'Run as administrator with Zentriq' : 'No elevation'} · {s.requests ? `requests on, up to ${ago(s.maxAdminMinutes)}` : 'no requests'}{s.allowedAdmins.length > 0 && <> · stay administrators: <span className="mono">{s.allowedAdmins.join(', ')}</span></>}</>}
        edit={(p, groupId) => setEdit({ p, groupId })} changed={changed} failed={failed} />
      {edit && <PolicyDialog policy={edit.p} groupId={edit.groupId} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); changed('Policy saved. Devices apply it within seconds when online.'); }} />}
    </>
  );
}

function PolicyDialog({ policy, groupId, onClose, onSaved }: { policy: AreaPolicy<Settings> | null; groupId: string | null; onClose: () => void; onSaved: () => void }) {
  const s = policy?.settings;
  const [f, setF] = useState({ mode: s?.mode ?? 'audit', allowed: (s?.allowedAdmins ?? []).join('\n'), elevation: s?.elevation ?? true, requests: s?.requests ?? true, max: s?.maxAdminMinutes ?? 240 });
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function save() {
    setBusy(true); setErr(null);
    try {
      await api.put('/security-pack/privilege/policy', { groupId, mode: f.mode, allowedAdmins: f.allowed.split(/[\n,;]/).map((x) => x.trim()).filter(Boolean), elevation: f.elevation, requests: f.requests, maxAdminMinutes: f.max });
      onSaved();
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }
  return (
    <Modal wide title={`Privilege Policy — ${policy?.groupName ?? (groupId ? 'Device Group' : 'All Devices')}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Local Administrators">
          <ModeChoice value={f.mode} onChange={(mode) => setF({ ...f, mode })} options={[
            ['off', 'Off', 'Leave the group as it is'], ['audit', 'Audit', 'Report who would be removed'], ['enforce', 'Enforce', 'Remove everyone not allowed', 'crit'],
          ]} />
          <Fields cols={1}>
            <Field label="Stay administrators" optional hint="One per line: DOMAIN\name, a local account or group, or a SID; * and ? are wildcards (e.g. CORP\IT-Admins, LAPS*).">
              <textarea className="in mono" rows={4} value={f.allowed} onChange={(e) => setF({ ...f, allowed: e.target.value })} placeholder={'CORP\\Workstation Admins\nCORP\\it-*'} />
            </Field>
          </Fields>
        </Section>
        <Section title="For Standard Users">
          <Check checked={f.elevation} onChange={(v) => setF({ ...f, elevation: v })} hint="Explorer's menu and the tray icon get “Run as administrator with Zentriq”. Only applications you approve start, as the user, with administrator rights; no password is asked.">Run approved applications as administrator</Check>
          <Check checked={f.requests} onChange={(v) => setF({ ...f, requests: v })} hint="Users can ask for an application to be approved, or for administrator rights for a while, with a reason. You decide under Requests.">Users can send requests</Check>
          <Fields>
            <Field label="Administrator rights for at most">
              <select className="in" value={f.max} onChange={(e) => setF({ ...f, max: Number(e.target.value) })}>
                {[15, 30, 60, 120, 240, 480, 1440].map((x) => <option key={x} value={x}>{ago(x)}</option>)}
              </select>
            </Field>
          </Fields>
        </Section>
      </div>
    </Modal>
  );
}

function AdminsTab() {
  const [status, setStatus] = useSticky('PrivilegePage.admins.status', 'to_remove');
  const [q, setQ] = useSticky('PrivilegePage.admins.q', '');
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Admin[]>('/security-pack/privilege/admins', { status: status || null, q: q || null }), [status, q]);
  const rows = (data.data ?? []).map((a) => ({ ...a, id: `${a.assetId}:${a.member}` }));
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search devices or accounts…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search" />
        <select className="in" value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }} aria-label="Status">
          <option value="">Every Member</option>
          {Object.entries(ADMIN_STATUS).map(([k, [l]]) => <option key={k} value={k}>{l}</option>)}
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <Pager top page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
        <DataTable rows={rows.slice((page - 1) * PAGE, page * PAGE)} empty={data.loading ? 'Loading…' : 'Nothing reported. Devices report the Administrators group when the policy is Audit or Enforce.'} columns={[
          { key: 'h', label: 'Device', render: (a) => <Link to={`/assets/${a.assetId}`}><b>{a.hostname}</b></Link> },
          { key: 'm', label: 'Member', className: 'wrap', render: (a) => <>{a.member}<div className="faint mono" style={{ fontSize: 11 }}>{a.sid}</div></> },
          { key: 'k', label: 'Kind', render: (a) => fmt.label(a.kind ?? '—') },
          { key: 's', label: 'Status', render: (a) => { const [l, t] = ADMIN_STATUS[a.status] ?? [a.status, '']; return <Pill tone={t}>{l}</Pill>; } },
          { key: 'r', label: 'Reported', render: (a) => fmt.ago(a.reportedAt) },
        ]} />
        <Pager page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
      </Panel>
    </>
  );
}

function RulesTab({ canManage, tick, changed, failed }: TabProps) {
  const confirm = useConfirm();
  const data = useApi(() => api.get<Rule[]>('/security-pack/privilege/rules'), [tick]);
  const [edit, setEdit] = useState<Rule | 'new' | null>(null);
  async function remove(r: Rule) {
    if (!await confirm({ title: `Delete ${r.name}?`, message: 'Users can no longer run it as administrator.', danger: true, confirmLabel: 'Delete' })) return;
    try { await api.del(`/security-pack/privilege/rules/${r.id}`); changed('Rule deleted.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <Panel flush title="Approved Applications" sub="Applications standard users may run as administrator" right={canManage && <button className="btn primary" onClick={() => setEdit('new')}><Icon name="plus" size={14} /> Approve an Application</button>}>
        <DataTable rows={data.data ?? []} onRow={canManage ? (r) => setEdit(r) : undefined} rowTitle="Edit" empty={data.loading ? 'Loading…' : 'Nothing approved yet. Approve applications here or from a user’s request.'} columns={[
          { key: 'n', label: 'Application', className: 'wrap', render: (r) => <><b>{r.name}</b>{!r.enabled && <> <Pill>Off</Pill></>}{r.note && <div className="faint" style={{ fontSize: 12 }}>{r.note}</div>}</> },
          { key: 'k', label: 'Matched By', className: 'wrap', render: (r) => <>{r.kind === 'hash' ? 'File hash' : r.kind === 'publisher' ? 'Publisher' : 'Path'}<div className="faint mono" style={{ fontSize: 11.5, wordBreak: 'break-all' }}>{r.kind === 'hash' ? r.value.slice(0, 16) + '…' : r.value}{r.fileName ? ` · ${r.fileName}` : ''}</div></> },
          { key: 's', label: 'For', render: (r) => r.hostname ?? r.groupName ?? 'All devices' },
          { key: 'u', label: 'Uses', render: (r) => <>{r.used}{r.usesLeft != null && <span className="faint"> · {r.usesLeft} left</span>}</> },
          { key: 'e', label: 'Ends', render: (r) => (r.expiresAt ? fmt.day(r.expiresAt) : '—') },
          { key: 'a', label: '', render: (r) => canManage && <button className="btn sm danger" onClick={(e) => { e.stopPropagation(); void remove(r); }}>Delete</button> },
        ]} />
      </Panel>
      {edit && <RuleDialog rule={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); changed('Saved. Devices pick it up within seconds when online.'); }} />}
    </>
  );
}

function RuleDialog({ rule, onClose, onSaved }: { rule: Rule | null; onClose: () => void; onSaved: () => void }) {
  const groups = useApi(() => api.get<Group[]>('/security-pack/privilege/groups'), []);
  const [f, setF] = useState({ name: rule?.name ?? '', kind: rule?.kind ?? 'publisher', value: rule?.value ?? '', fileName: rule?.fileName ?? '', groupId: rule?.groupId ?? '', uses: rule?.usesLeft?.toString() ?? '', expires: rule?.expiresAt?.slice(0, 10) ?? '', enabled: rule?.enabled ?? true, note: rule?.note ?? '' });
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    setErr(null);
    const body = { name: f.name, kind: f.kind, value: f.value, fileName: f.kind === 'publisher' && f.fileName ? f.fileName : null, groupId: f.groupId || null, assetId: rule?.assetId ?? null,
      uses: f.uses ? Number(f.uses) : null, expiresAt: f.expires ? new Date(f.expires + 'T23:59:59').toISOString() : null, enabled: f.enabled, note: f.note || null };
    try { if (rule) await api.put(`/security-pack/privilege/rules/${rule.id}`, body); else await api.post('/security-pack/privilege/rules', body); onSaved(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal wide title={rule ? `Edit ${rule.name}` : 'Approve an Application'} onClose={onClose} footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.name.trim() || !f.value.trim()} onClick={() => void save()}>Save</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Application">
          <Fields>
            <Field label="Name" required><input className="in" value={f.name} autoFocus onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Wireshark" /></Field>
            <Field label="For">
              <select className="in" value={f.groupId} onChange={(e) => setF({ ...f, groupId: e.target.value })} disabled={!!rule?.assetId}>
                <option value="">{rule?.hostname ? `This device (${rule.hostname})` : 'All devices'}</option>
                {(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
              </select>
            </Field>
          </Fields>
        </Section>
        <Section title="Matched By" sub="A publisher rule covers every version the vendor signs; a hash rule exactly one file; a path rule what is in that place (only for folders users cannot write to, such as C:\Program Files).">
          <ModeChoice value={f.kind} onChange={(kind) => setF({ ...f, kind })} options={[['publisher', 'Publisher', 'Valid code signature'], ['hash', 'File hash', 'SHA-256 of one file'], ['path', 'Path', 'Full path, wildcards']]} />
          <Fields>
            <Field label={f.kind === 'publisher' ? 'Publisher (signature)' : f.kind === 'hash' ? 'SHA-256' : 'Path'} required span={f.kind !== 'publisher'}>
              <input className="in mono" value={f.value} onChange={(e) => setF({ ...f, value: e.target.value })} placeholder={f.kind === 'publisher' ? 'e.g. Wireshark Foundation' : f.kind === 'hash' ? '64 hexadecimal characters' : 'C:\\Program Files\\Vendor\\tool.exe'} />
            </Field>
            {f.kind === 'publisher' && <Field label="File name" optional hint="Only this program of the publisher (wildcards)."><input className="in mono" value={f.fileName} onChange={(e) => setF({ ...f, fileName: e.target.value })} placeholder="Wireshark*.exe" /></Field>}
          </Fields>
        </Section>
        <Section title="Limits">
          <Fields cols={3}>
            <Field label="Uses" optional hint="Empty: no limit."><input className="in" type="number" min={1} max={1000} value={f.uses} onChange={(e) => setF({ ...f, uses: e.target.value })} /></Field>
            <Field label="Ends" optional><input className="in" type="date" value={f.expires} onChange={(e) => setF({ ...f, expires: e.target.value })} /></Field>
            <Field label="Note" optional><input className="in" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} /></Field>
          </Fields>
          <Check checked={f.enabled} onChange={(v) => setF({ ...f, enabled: v })}>Active</Check>
        </Section>
      </div>
    </Modal>
  );
}

function RequestsTab({ canManage, tick, changed, failed }: TabProps) {
  const [status, setStatus] = useSticky('PrivilegePage.requests.status', 'pending');
  const data = useApi(() => api.get<Request[]>('/security-pack/privilege/requests', { status: status || null }), [status, tick]);
  const [decide, setDecide] = useState<Request | null>(null);
  return (
    <>
      <div className="toolbar-row">
        <select className="in" value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="pending">Waiting</option><option value="approved">Approved</option><option value="denied">Not approved</option><option value="">All</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} onRow={canManage ? (r) => r.status === 'pending' && setDecide(r) : undefined} rowTitle="Decide" empty={data.loading ? 'Loading…' : 'No requests.'} columns={[
          { key: 't', label: 'Sent', render: (r) => fmt.ago(r.createdAt) },
          { key: 'u', label: 'User · Device', render: (r) => <><b>{r.user}</b><div className="faint" style={{ fontSize: 12 }}>{r.hostname}</div></> },
          { key: 'w', label: 'Request', className: 'wrap', render: (r) => (r.kind === 'admin' ? <>Administrator rights for {ago(r.minutes ?? 60)}</> : <><b>{r.product ?? r.fileName}</b><div className="faint mono" style={{ fontSize: 11.5, wordBreak: 'break-all' }}>{r.path}</div>{r.publisher ? <div className="faint" style={{ fontSize: 12 }}>Signed by {r.publisher}</div> : <div className="faint" style={{ fontSize: 12 }}>Not signed</div>}</>) },
          { key: 'r', label: 'Reason', className: 'wrap', render: (r) => r.reason ?? '—' },
          { key: 's', label: 'Status', render: (r) => (r.status === 'pending' ? <Pill tone="warn">Waiting</Pill> : <><Pill tone={r.status === 'approved' ? 'ok' : 'crit'}>{r.status === 'approved' ? 'Approved' : 'Not approved'}</Pill><div className="faint" style={{ fontSize: 11.5 }}>{r.decision} · {r.decidedBy}</div></>) },
          { key: 'a', label: '', render: (r) => canManage && r.status === 'pending' && <button className="btn sm primary" onClick={(e) => { e.stopPropagation(); setDecide(r); }}>Decide</button> },
        ]} />
      </Panel>
      {decide && <DecideDialog req={decide} onClose={() => setDecide(null)} onDone={(t) => { setDecide(null); changed(t); }} failed={failed} />}
    </>
  );
}

function DecideDialog({ req, onClose, onDone, failed }: { req: Request; onClose: () => void; onDone: (t: string) => void; failed: (e: unknown) => void }) {
  const groups = useApi(() => api.get<Group[]>('/security-pack/privilege/groups'), []);
  const [f, setF] = useState({ approve: true, scope: 'once', groupId: '', minutes: req.minutes ?? 60, note: '' });
  async function save() {
    try {
      const r = await api.post<{ decision: string }>(`/security-pack/privilege/requests/${req.id}`, { approve: f.approve, scope: f.scope, groupId: f.groupId || null, minutes: f.minutes, note: f.note || null });
      onDone(`${r.decision}. The device applies it within seconds when online; the user gets a message.`);
    } catch (e) { failed(e); onClose(); }
  }
  return (
    <Modal wide title={req.kind === 'admin' ? `Administrator Rights for ${req.user}` : `Run ${req.fileName} as Administrator`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className={`btn ${f.approve ? 'primary' : 'danger'}`} disabled={f.approve && f.scope === 'group' && !f.groupId} onClick={() => void save()}>{f.approve ? 'Approve' : 'Do Not Approve'}</button></>}>
      <div className="form-v">
        <Section title="Request">
          <dl className="kv">
            <dt>User</dt><dd>{req.user} on {req.hostname}</dd>
            {req.kind === 'elevate' && <><dt>Program</dt><dd className="mono" style={{ wordBreak: 'break-all' }}>{req.path}</dd><dt>Signed by</dt><dd>{req.publisher ?? <Pill tone="warn">Not signed</Pill>}</dd><dt>SHA-256</dt><dd className="mono" style={{ wordBreak: 'break-all', fontSize: 12 }}>{req.sha256 ?? '—'}</dd></>}
            <dt>Reason</dt><dd>{req.reason ?? '—'}</dd>
          </dl>
        </Section>
        <Section title="Decision">
          <ModeChoice value={f.approve ? 'yes' : 'no'} onChange={(v) => setF({ ...f, approve: v === 'yes' })} options={[['yes', 'Approve', req.kind === 'admin' ? 'Administrator for a while' : 'This exact file (hash)', 'ok'], ['no', 'Do not approve', 'The user is told', 'crit']]} />
          {f.approve && req.kind === 'admin' && <Fields><Field label="For">
            <select className="in" value={f.minutes} onChange={(e) => setF({ ...f, minutes: Number(e.target.value) })}>{[15, 30, 60, 120, 240, 480, 1440].map((x) => <option key={x} value={x}>{ago(x)}</option>)}</select>
          </Field></Fields>}
          {f.approve && req.kind === 'elevate' && <Fields>
            <Field label="Where">
              <select className="in" value={f.scope} onChange={(e) => setF({ ...f, scope: e.target.value })}>
                <option value="once">Once, on this device</option><option value="device">Always, on this device</option><option value="group">A device group</option><option value="all">Every device</option>
              </select>
            </Field>
            {f.scope === 'group' && <Field label="Device group"><select className="in" value={f.groupId} onChange={(e) => setF({ ...f, groupId: e.target.value })}><option value="">Choose…</option>{(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}</select></Field>}
          </Fields>}
          <Fields cols={1}><Field label="Note" optional hint={f.approve ? undefined : 'Shown to the user.'}><input className="in" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} /></Field></Fields>
        </Section>
      </div>
    </Modal>
  );
}

function GrantsTab({ canManage, tick, changed, failed }: TabProps) {
  const confirm = useConfirm();
  const data = useApi(() => api.get<Grant[]>('/security-pack/privilege/grants'), [tick]);
  const [add, setAdd] = useState(false);
  async function revoke(g: Grant) {
    if (!await confirm({ title: `End the administrator rights of ${g.user}?`, message: `On ${g.hostname}. The agent removes the account from the Administrators group at once (online devices).`, danger: true, confirmLabel: 'End Now' })) return;
    try { await api.del(`/security-pack/privilege/grants/${g.id}`); changed('Ended.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <Panel flush title="Administrator Rights for a While" sub="The agent adds the user to the Administrators group and removes them when the time ends — also offline" right={canManage && <button className="btn primary" onClick={() => setAdd(true)}><Icon name="plus" size={14} /> Give Rights</button>}>
        <DataTable rows={data.data ?? []} empty={data.loading ? 'Loading…' : 'None in the last 90 days.'} columns={[
          { key: 'u', label: 'User · Device', render: (g) => <><b>{g.user}</b><div className="faint" style={{ fontSize: 12 }}>{g.hostname}</div></> },
          { key: 'f', label: 'From', render: (g) => fmt.date(g.startsAt) },
          { key: 't', label: 'Until', render: (g) => fmt.date(g.revokedAt ?? g.endsAt) },
          { key: 's', label: 'Status', render: (g) => (g.active ? <Pill tone="info">Active</Pill> : g.revokedAt ? <Pill>Ended early{g.revokedBy ? ` · ${g.revokedBy}` : ''}</Pill> : <Pill>Ended</Pill>) },
          { key: 'r', label: 'Reason', className: 'wrap', render: (g) => g.reason ?? '—' },
          { key: 'b', label: 'Given By', render: (g) => g.createdBy ?? '—' },
          { key: 'a', label: '', render: (g) => canManage && g.active && <button className="btn sm danger" onClick={() => void revoke(g)}>End Now</button> },
        ]} />
      </Panel>
      {add && <GrantDialog onClose={() => setAdd(false)} onSaved={() => { setAdd(false); changed('Rights given. The device applies them within seconds when online.'); }} />}
    </>
  );
}

function GrantDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [q, setQ] = useState('');
  const devices = useApi(() => (q.trim().length >= 2 ? api.get<{ items: { id: string; hostname: string | null; ip: string | null }[] }>('/assets', { q, pageSize: 20 }).then((r) => r.items) : Promise.resolve([])), [q]);
  const [f, setF] = useState({ assetId: '', host: '', user: '', minutes: 60, reason: '' });
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    try { await api.post('/security-pack/privilege/grants', { assetId: f.assetId, user: f.user, minutes: f.minutes, reason: f.reason || null }); onSaved(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal title="Give Administrator Rights" onClose={onClose} footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={!f.assetId || !f.user.trim()} onClick={() => void save()}>Give Rights</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section>
          <Fields cols={1}>
            <Field label="Device" required>
              {f.assetId ? <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><b>{f.host}</b><button className="btn sm" onClick={() => setF({ ...f, assetId: '', host: '' })}>Change</button></div>
                : <><input className="in" value={q} autoFocus onChange={(e) => setQ(e.target.value)} placeholder="Search by name or IP" />
                  {(devices.data ?? []).map((d) => <button key={d.id} type="button" className="btn sm" style={{ margin: '4px 4px 0 0' }} onClick={() => setF({ ...f, assetId: d.id, host: d.hostname ?? d.ip ?? d.id })}>{d.hostname ?? d.ip}</button>)}</>}
            </Field>
            <Field label="User" required hint="DOMAIN\name or the name of a local account."><input className="in" value={f.user} onChange={(e) => setF({ ...f, user: e.target.value })} placeholder="CORP\ana" /></Field>
            <Field label="For"><select className="in" value={f.minutes} onChange={(e) => setF({ ...f, minutes: Number(e.target.value) })}>{[15, 30, 60, 120, 240, 480, 1440].map((x) => <option key={x} value={x}>{ago(x)}</option>)}</select></Field>
            <Field label="Reason" optional><input className="in" value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} placeholder="e.g. ticket 4711" /></Field>
          </Fields>
        </Section>
      </div>
    </Modal>
  );
}

function ActivityTab() {
  const [kind, setKind] = useSticky('PrivilegePage.activity.kind', '');
  const [q, setQ] = useSticky('PrivilegePage.activity.q', '');
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Ev[]>('/security-pack/privilege/events', { kind: kind || null, q: q || null }), [kind, q]);
  const rows = data.data ?? [];
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search devices, users, programs…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search" />
        <select className="in" value={kind} onChange={(e) => { setKind(e.target.value); setPage(1); }} aria-label="Event">
          <option value="">Every Event</option>{Object.entries(EVENT).filter(([k]) => k !== 'member_kept').map(([k, [l]]) => <option key={k} value={k}>{l}</option>)}
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <Pager top page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
        <DataTable rows={rows.slice((page - 1) * PAGE, page * PAGE)} empty={data.loading ? 'Loading…' : 'Nothing yet.'} columns={[
          { key: 't', label: 'When', render: (e) => fmt.date(e.at) },
          { key: 'k', label: 'Event', render: (e) => { const [l, t] = EVENT[e.kind] ?? [e.kind, '']; return <Pill tone={t}>{l}</Pill>; } },
          { key: 'u', label: 'User · Device', render: (e) => <><b>{e.user ?? '—'}</b><div className="faint" style={{ fontSize: 12 }}>{e.hostname}</div></> },
          { key: 'p', label: 'Program', className: 'wrap', render: (e) => (e.path ? <><span className="mono" style={{ fontSize: 12, wordBreak: 'break-all' }}>{e.path}</span>{e.publisher && <div className="faint" style={{ fontSize: 12 }}>Signed by {e.publisher}</div>}</> : '—') },
          { key: 'd', label: 'Detail', className: 'wrap', render: (e) => <>{e.detail ?? '—'}{e.ruleName && <div className="faint" style={{ fontSize: 12 }}>Rule: {e.ruleName}</div>}</> },
        ]} />
        <Pager page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
      </Panel>
    </>
  );
}
