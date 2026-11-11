import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link, useRouter } from '../../app/router';
import { useSticky } from '../../app/sticky';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Check, Field, Fields, Section } from '../../components/Form';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Kpis, PageHead, Pager, Panel, Pill } from '../../components/ui';
import { AddOnNotice, ModeChoice, Policies, useMessages, useSecurityPack, type AreaPolicy } from './common';

interface Settings { mode: 'off' | 'report' | 'enforce'; method: string; usedSpaceOnly: boolean; fixedDrives: boolean; rotateAfterView: boolean }
interface Overview { policies: AreaPolicy<Settings>[]; kpis: { devices: number; reporting: number; protected: number; encrypting: number; unprotected: number; noTpm: number; errors: number; keys: number; withoutKey: number } }
interface Volume { assetId: string; hostname: string; volume: string; kind: string | null; protection: string | null; status: string | null; percent: number | null; method: string | null; protectors: string[]; tpm: string | null; error: string | null; reportedAt: string; department: string | null; keys: number }
interface Key { id: string; assetId: string; hostname: string; volume: string; keyId: string; escrowedAt: string; retiredAt: string | null; viewedAt: string | null; viewedBy: string | null; rotateRequestedAt: string | null }

const MODE: Record<string, string> = { off: 'Off', report: 'Report', enforce: 'Enforce' };
const METHOD: Record<string, string> = { XtsAes256: 'XTS-AES 256', XtsAes128: 'XTS-AES 128', Aes256: 'AES-CBC 256', Aes128: 'AES-CBC 128' };
const PAGE = 50;
type Tab = 'devices' | 'keys' | 'policy';
const TABS: [Tab, string][] = [['devices', 'Devices'], ['keys', 'Recovery Keys'], ['policy', 'Policy']];

export function BitLockerPage() {
  const licensed = useSecurityPack();
  const { can } = useAuth();
  const { query, navigate } = useRouter();
  const tab: Tab = TABS.find(([t]) => t === query.get('tab'))?.[0] ?? 'devices';
  const m = useMessages();
  const data = useApi(() => (licensed ? api.get<Overview>('/security-pack/bitlocker') : Promise.resolve(null)), [licensed, m.tick]);
  const k = data.data?.kpis;
  return (
    <>
      <PageHead title="BitLocker Management" icon="disklock" />
      {!licensed && <AddOnNotice />}
      {licensed && <>
        <ErrorBox error={data.error} />
        {k && <Kpis items={[
          { label: 'System Drives Protected', value: `${k.protected} / ${k.reporting}`, sub: `${k.encrypting} encrypting now` },
          { label: 'Not Protected', value: k.unprotected.toLocaleString(), sub: `${k.noTpm} without a usable TPM · ${k.errors} with errors` },
          { label: 'Recovery Keys', value: k.keys.toLocaleString(), sub: k.withoutKey > 0 ? `${k.withoutKey} protected drive(s) without a key here` : 'Every protected drive has one' },
          { label: 'Devices', value: k.devices.toLocaleString(), sub: 'With the Zentriq Agent 1.1.1.127 or later' },
        ]} />}
        {m.view}
        <div className="tabs" role="tablist">
          {TABS.map(([t, l]) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => navigate(`/security-pack/bitlocker?tab=${t}`, true)}>{l}</button>)}
        </div>
        {tab === 'devices' && <VolumesTab />}
        {tab === 'keys' && <KeysTab canRecover={can('bitlocker.recover')} canManage={can('bitlocker.manage')} changed={m.changed} failed={m.failed} tick={m.tick} />}
        {tab === 'policy' && data.data && <PolicyTab policies={data.data.policies} canManage={can('bitlocker.manage')} changed={m.changed} failed={m.failed} />}
      </>}
    </>
  );
}

function VolumesTab() {
  const [filter, setFilter] = useSticky('BitLockerPage.filter', 'os');
  const [q, setQ] = useSticky('BitLockerPage.q', '');
  const [page, setPage] = useState(1);
  const data = useApi(() => api.get<Volume[]>('/security-pack/bitlocker/volumes', { filter: filter || null, q: q || null }), [filter, q]);
  const rows = (data.data ?? []).map((v) => ({ ...v, id: `${v.assetId}:${v.volume}` }));
  return (
    <>
      <div className="toolbar-row">
        <input className="in grow" placeholder="Search devices…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search" />
        <select className="in" value={filter} onChange={(e) => { setFilter(e.target.value); setPage(1); }} aria-label="Filter">
          <option value="os">System drives</option><option value="unprotected">Not protected</option><option value="protected">Protected</option><option value="notpm">No usable TPM</option><option value="errors">Errors</option><option value="">All drives</option>
        </select>
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <Pager top page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
        <DataTable rows={rows.slice((page - 1) * PAGE, page * PAGE)} empty={data.loading ? 'Loading…' : 'Nothing reported. Devices report BitLocker when the policy is Report or Enforce.'} columns={[
          { key: 'h', label: 'Device', render: (v) => <><Link to={`/assets/${v.assetId}`}><b>{v.hostname}</b></Link><div className="faint" style={{ fontSize: 12 }}>{v.department ?? ''}</div></> },
          { key: 'v', label: 'Drive', render: (v) => <>{v.volume} <span className="faint">{v.kind === 'os' ? 'system' : 'data'}</span></> },
          { key: 'p', label: 'Protection', render: (v) => <Pill tone={v.protection === 'on' ? 'ok' : v.status === 'encrypting' ? 'info' : 'crit'}>{v.protection === 'on' ? 'On' : v.status === 'encrypting' ? `Encrypting ${Math.round(v.percent ?? 0)} %` : 'Off'}</Pill> },
          { key: 's', label: 'Encryption', render: (v) => <>{fmt.label(v.status ?? '—')}{v.method && <div className="faint" style={{ fontSize: 12 }}>{v.method}</div>}</> },
          { key: 'k', label: 'Protectors', className: 'wrap', render: (v) => v.protectors.join(', ') || '—' },
          { key: 'r', label: 'Recovery Key Here', render: (v) => (v.keys > 0 ? <Pill tone="ok">Yes</Pill> : v.protection === 'on' ? <Pill tone="warn">No</Pill> : '—') },
          { key: 't', label: 'TPM', render: (v) => fmt.label(v.tpm ?? '—') },
          { key: 'e', label: 'Problem', className: 'wrap', render: (v) => (v.error ? <span style={{ color: 'var(--crit)' }}>{v.error}</span> : '—') },
          { key: 'd', label: 'Reported', render: (v) => fmt.ago(v.reportedAt) },
        ]} />
        <Pager page={page} pageSize={PAGE} total={rows.length} onPage={setPage} />
      </Panel>
    </>
  );
}

function KeysTab({ canRecover, canManage, changed, failed, tick }: { canRecover: boolean; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void; tick: number }) {
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const data = useApi(() => (q.trim().length >= 2 ? api.get<Key[]>('/security-pack/bitlocker/keys', { q: q.trim() }) : Promise.resolve([] as Key[])), [q, tick]);
  const [show, setShow] = useState<Key | null>(null);
  async function rotate(k: Key) {
    if (!await confirm({ title: 'Replace this recovery password?', message: `${k.hostname} ${k.volume}: the device adds a new recovery password, removes this one and sends the new one here at its next contact.`, confirmLabel: 'Replace' })) return;
    try { await api.post(`/security-pack/bitlocker/keys/${k.id}/rotate`); changed('The device replaces the recovery password at its next contact.'); } catch (e) { failed(e); }
  }
  return (
    <>
      <Callout icon="info">A locked laptop shows a <b>recovery key ID</b> on its BitLocker recovery screen. Type its first characters (or the device name) to find the 48-digit recovery password. Every view is recorded with its reason in the Audit Log, and by default the password is replaced once the device is back.</Callout>
      <div className="toolbar-row">
        <input className="in grow mono" placeholder="Recovery key ID (e.g. 1A2B3C4D) or device name" value={q} autoFocus onChange={(e) => setQ(e.target.value)} aria-label="Key ID or device" />
      </div>
      <ErrorBox error={data.error} />
      <Panel flush>
        <DataTable rows={data.data ?? []} empty={q.trim().length < 2 ? 'Type at least two characters.' : data.loading ? 'Searching…' : 'No recovery key matches.'} columns={[
          { key: 'i', label: 'Key ID', render: (k) => <span className="mono" style={{ fontSize: 12.5 }}>{k.keyId}</span> },
          { key: 'h', label: 'Device · Drive', render: (k) => <><b>{k.hostname}</b> {k.volume}</> },
          { key: 'e', label: 'Kept Since', render: (k) => fmt.date(k.escrowedAt) },
          { key: 's', label: 'Status', render: (k) => (k.retiredAt ? <Pill>No longer on the drive</Pill> : k.rotateRequestedAt ? <Pill tone="warn">To be replaced</Pill> : <Pill tone="ok">Current</Pill>) },
          { key: 'v', label: 'Last Shown', render: (k) => (k.viewedAt ? <>{fmt.ago(k.viewedAt)}<div className="faint" style={{ fontSize: 11.5 }}>{k.viewedBy}</div></> : '—') },
          { key: 'a', label: '', render: (k) => <span style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
            {canRecover && <button className="btn sm primary" onClick={() => setShow(k)}>Show Password</button>}
            {canManage && !k.retiredAt && !k.rotateRequestedAt && <button className="btn sm" onClick={() => void rotate(k)}>Replace</button>}
          </span> },
        ]} />
      </Panel>
      {show && <RevealDialog k={show} onClose={() => { setShow(null); changed('The view was recorded in the Audit Log.'); }} />}
    </>
  );
}

function RevealDialog({ k, onClose }: { k: Key; onClose: () => void }) {
  const [reason, setReason] = useState('');
  const [res, setRes] = useState<{ password: string; rotates: boolean } | null>(null);
  const [err, setErr] = useState<string | null>(null);
  async function reveal() {
    setErr(null);
    try { setRes(await api.post(`/security-pack/bitlocker/keys/${k.id}/reveal`, { reason })); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal title={`Recovery Password — ${k.hostname} ${k.volume}`} onClose={onClose}
      footer={res ? <button className="btn primary" onClick={onClose}>Done</button> : <><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" disabled={reason.trim().length < 5} onClick={() => void reveal()}>Show</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section>
          <dl className="kv"><dt>Key ID</dt><dd className="mono">{k.keyId}</dd></dl>
          {!res ? <Fields cols={1}><Field label="Why is it needed?" required hint="Recorded in the Audit Log with your name, e.g. the ticket number."><input className="in" value={reason} autoFocus onChange={(e) => setReason(e.target.value)} placeholder="Ticket 4711: laptop asks for the recovery key after a BIOS update" /></Field></Fields>
            : <>
              <div className="mono" style={{ fontSize: 20, letterSpacing: 1, padding: '14px 16px', background: 'var(--bg)', borderRadius: 10, textAlign: 'center', userSelect: 'all' }}>{res.password}</div>
              <p className="faint" style={{ marginTop: 10 }}>Read it to the user, eight groups of six digits. {res.rotates ? 'The device replaces it with a new one at its next contact; this one then stops working.' : ''}</p>
            </>}
        </Section>
      </div>
    </Modal>
  );
}

function PolicyTab({ policies, canManage, changed, failed }: { policies: AreaPolicy<Settings>[]; canManage: boolean; changed: (t: string) => void; failed: (e: unknown) => void }) {
  const [edit, setEdit] = useState<{ p: AreaPolicy<Settings> | null; groupId: string | null } | null>(null);
  return (
    <>
      <Callout icon="info"><b>Report</b> collects the BitLocker status and keeps the recovery passwords that exist. <b>Enforce</b> also turns BitLocker on for the system drive (TPM and a recovery password, without a restart) and, if chosen, fixed data drives — devices without a usable TPM are listed with the reason.</Callout>
      <Policies area="bitlocker" policies={policies} canManage={canManage} modeLabel={MODE} modeTone={(x) => (x === 'enforce' ? 'ok' : x === 'report' ? 'info' : '')}
        describe={(s) => <>{METHOD[s.method] ?? s.method}{s.usedSpaceOnly ? ', used space only' : ', whole drive'}{s.fixedDrives ? ' · data drives too' : ''}{s.rotateAfterView ? ' · new password after it was shown' : ''}</>}
        edit={(p, groupId) => setEdit({ p, groupId })} changed={changed} failed={failed} />
      {edit && <PolicyDialog policy={edit.p} groupId={edit.groupId} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); changed('Policy saved. Devices apply it within seconds when online.'); }} />}
    </>
  );
}

function PolicyDialog({ policy, groupId, onClose, onSaved }: { policy: AreaPolicy<Settings> | null; groupId: string | null; onClose: () => void; onSaved: () => void }) {
  const s = policy?.settings;
  const [f, setF] = useState({ mode: s?.mode ?? 'report', method: s?.method ?? 'XtsAes256', usedSpaceOnly: s?.usedSpaceOnly ?? true, fixedDrives: s?.fixedDrives ?? false, rotateAfterView: s?.rotateAfterView ?? true });
  const [err, setErr] = useState<string | null>(null);
  async function save() {
    try { await api.put('/security-pack/bitlocker/policy', { groupId, ...f }); onSaved(); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  }
  return (
    <Modal wide title={`BitLocker Policy — ${policy?.groupName ?? (groupId ? 'Device Group' : 'All Devices')}`} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn primary" onClick={() => void save()}>Save</button></>}>
      <div className="form-v">
        {err && <Callout tone="crit" icon="alert">{err}</Callout>}
        <Section title="Mode">
          <ModeChoice value={f.mode} onChange={(mode) => setF({ ...f, mode })} options={[['off', 'Off', 'Nothing collected'], ['report', 'Report', 'Status and recovery keys'], ['enforce', 'Enforce', 'Encrypt and keep the keys', 'ok']]} />
        </Section>
        <Section title="Encryption" sub="Applies to drives that are not encrypted yet; encrypted drives keep their method.">
          <Fields>
            <Field label="Method"><select className="in" value={f.method} onChange={(e) => setF({ ...f, method: e.target.value })}>{Object.entries(METHOD).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          </Fields>
          <Check checked={f.usedSpaceOnly} onChange={(v) => setF({ ...f, usedSpaceOnly: v })} hint="Much faster; right for new devices. Whole drive also covers deleted data.">Used space only</Check>
          <Check checked={f.fixedDrives} onChange={(v) => setF({ ...f, fixedDrives: v })} hint="Unlocked automatically together with the system drive.">Also encrypt fixed data drives</Check>
          <Check checked={f.rotateAfterView} onChange={(v) => setF({ ...f, rotateAfterView: v })} hint="A recovery password that was shown in the console is replaced at the device's next contact.">New recovery password after it was shown</Check>
        </Section>
      </div>
    </Modal>
  );
}
