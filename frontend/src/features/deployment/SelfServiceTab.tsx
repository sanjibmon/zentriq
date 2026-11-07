import { useState } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { Check, Choices, Field, Fields, Section } from '../../components/Form';
import { Modal } from '../../components/Modal';
import { Callout, DataTable, ErrorBox, fmt, Panel, Pill } from '../../components/ui';
import { errText, type Group } from './types';

interface Offer {
  id: string; name: string; version: string | null; publisher: string | null; platform: 'windows' | 'linux'; size: number; enabled: boolean; approval: boolean;
  description: string | null; category: string | null; groupIds: string[]; installs: number; pending: number;
}
interface SsRequest {
  id: string; packageId: string; package: string; version: string | null; assetId: string; device: string | null; user: string | null; reason: string | null;
  status: 'pending' | 'approved' | 'denied' | 'installed'; createdAt: string; decidedAt: string | null; decidedBy: string | null; note: string | null; install: string | null;
}
interface TraySettings { enabled: boolean; softwareCenter: boolean; remoteHelp: boolean; companyName: string | null; supportText: string | null }
interface Data { packages: Offer[]; requests: SsRequest[]; settings: TraySettings }

/** Software Deployment › Self-Service: what the Software Center of the tray icon offers, requests to approve, the tray settings. */
export function SelfServiceTab({ tick, canManage, canRun, groups, changed, failed }: {
  tick: number; canManage: boolean; canRun: boolean; groups: Group[]; changed: (t: string) => void; failed: (e: unknown) => void;
}) {
  const { can } = useAuth();
  const data = useApi(() => api.get<Data>('/deployment/self-service'), [tick]);
  const [edit, setEdit] = useState<Offer | null>(null);
  const d = data.data;
  async function decide(r: SsRequest, approve: boolean) {
    try { await api.post(`/deployment/self-service/requests/${r.id}`, { approve }); changed(approve ? `${r.package} is being installed on ${r.device}.` : `Request for ${r.package} denied.`); }
    catch (e) { failed(e); }
  }
  return (
    <>
      <ErrorBox error={data.error} />
      {d && <TrayPanel settings={d.settings} canSave={can('settings.manage')} changed={changed} failed={failed} />}
      <Panel title="Requests Waiting for Approval" sub="Users asked for software that needs approval; approving installs it on their device at once" flush>
        <DataTable rows={(d?.requests ?? []).slice(0, 100)} empty={data.loading ? 'Loading…' : 'No requests yet.'} columns={[
          { key: 'p', label: 'Software', render: (r) => <><b>{r.package}</b>{r.version && <span className="faint"> {r.version}</span>}</> },
          { key: 'u', label: 'Requested By', render: (r) => <>{r.user ?? '—'}<div className="faint" style={{ fontSize: 11.5 }}><Link to={`/assets/${r.assetId}`}>{r.device}</Link></div></> },
          { key: 'r', label: 'Reason', className: 'wrap', render: (r) => <span style={{ fontSize: 12.5 }}>{r.reason ?? <span className="faint">—</span>}</span> },
          { key: 't', label: 'Asked', render: (r) => fmt.ago(r.createdAt) },
          { key: 's', label: 'Status', render: (r) => r.status === 'pending' ? <Pill tone="warn">Waiting</Pill> : r.status === 'denied' ? <Pill tone="crit">Denied</Pill>
            : <Pill tone={r.install === 'succeeded' ? 'ok' : r.install === 'failed' ? 'crit' : 'info'}>{r.install === 'succeeded' ? 'Installed' : r.install === 'failed' ? 'Install Failed' : 'Approved'}</Pill> },
          { key: 'x', label: '', render: (r) => r.status === 'pending' && canRun ? <div className="row-actions">
            <button className="btn sm primary" onClick={() => void decide(r, true)}>Approve</button>
            <button className="btn sm" onClick={() => void decide(r, false)}>Deny</button>
          </div> : r.decidedBy ? <span className="faint" style={{ fontSize: 11.5 }}>{r.decidedBy}</span> : null },
        ]} />
      </Panel>
      <Panel title="Software Center" sub="Packages users can install themselves from the tray icon. The agent installs them as SYSTEM: no administrator rights needed, even where Group Policy blocks user installations." flush>
        <DataTable rows={d?.packages ?? []} empty={data.loading ? 'Loading…' : <>No packages yet. Upload one in <Link to="/modules/deploy?tab=packages">Packages</Link>.</>} columns={[
          { key: 'n', label: 'Package', className: 'wrap', render: (p) => <><b>{p.name}</b>{p.version && <span className="faint"> {p.version}</span>}<div className="faint" style={{ fontSize: 11.5 }}>{[p.publisher, p.platform === 'linux' ? 'Linux' : 'Windows', fmt.bytes(p.size)].filter(Boolean).join(' · ')}</div></> },
          { key: 'o', label: 'Offered', render: (p) => p.enabled ? <Pill tone="ok">In Software Center</Pill> : <span className="faint">No</span> },
          { key: 'a', label: 'Approval', render: (p) => p.enabled ? (p.approval ? 'IT approves first' : 'Installs at once') : <span className="faint">—</span> },
          { key: 'c', label: 'Category', render: (p) => p.category ?? <span className="faint">—</span> },
          { key: 'w', label: 'Offered To', render: (p) => p.groupIds.length ? `${p.groupIds.length} device group(s)` : p.enabled ? 'All devices' : <span className="faint">—</span> },
          { key: 'i', label: 'Installs', render: (p) => <>{p.installs}{p.pending > 0 && <Pill tone="warn">{p.pending} waiting</Pill>}</> },
          { key: 'x', label: '', render: (p) => canManage ? <button className="btn sm" onClick={() => setEdit(p)}>{p.enabled ? 'Edit' : 'Offer'}</button> : null },
        ]} />
      </Panel>
      {edit && <OfferDialog offer={edit} groups={groups} onClose={() => setEdit(null)} onSaved={(t) => { setEdit(null); changed(t); }} />}
    </>
  );
}

function TrayPanel({ settings, canSave, changed, failed }: { settings: TraySettings; canSave: boolean; changed: (t: string) => void; failed: (e: unknown) => void }) {
  const [f, setF] = useState({ ...settings, companyName: settings.companyName ?? '', supportText: settings.supportText ?? '' });
  const [busy, setBusy] = useState(false);
  const dirty = JSON.stringify(f) !== JSON.stringify({ ...settings, companyName: settings.companyName ?? '', supportText: settings.supportText ?? '' });
  async function save() {
    setBusy(true);
    try { await api.put('/deployment/self-service/settings', { ...f, companyName: f.companyName || null, supportText: f.supportText || null }); changed('Tray icon settings saved; devices pick them up within 10 minutes.'); }
    catch (e) { failed(e); } finally { setBusy(false); }
  }
  return (
    <Panel title="Tray Icon on Windows Devices" sub="The Zentriq Agent shows an icon next to the clock for every signed-in user">
      <div className="form-v">
        <Choices>
          <Check checked={f.enabled} disabled={!canSave} onChange={(v) => setF({ ...f, enabled: v })}>Show the tray icon</Check>
          <Check checked={f.softwareCenter} disabled={!canSave || !f.enabled} onChange={(v) => setF({ ...f, softwareCenter: v })}>Software Center</Check>
          <Check checked={f.remoteHelp} disabled={!canSave || !f.enabled} onChange={(v) => setF({ ...f, remoteHelp: v })}>Get Remote Help</Check>
        </Choices>
        <Fields>
          <Field label="Company or IT team name" hint="Shown in the tray windows."><input className="in" value={f.companyName} maxLength={80} disabled={!canSave} onChange={(e) => setF({ ...f, companyName: e.target.value })} placeholder="e.g. Contoso IT" /></Field>
          <Field label="Other ways to reach IT" hint="Shown in Get Remote Help."><input className="in" value={f.supportText} maxLength={400} disabled={!canSave} onChange={(e) => setF({ ...f, supportText: e.target.value })} placeholder="e.g. Urgent? Call the service desk on 4711." /></Field>
        </Fields>
        {canSave ? <div><button className="btn primary" disabled={!dirty || busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button></div>
          : <small className="muted">Needs the System Settings permission.</small>}
      </div>
    </Panel>
  );
}

export function OfferDialog({ offer, groups, onClose, onSaved }: { offer: Offer; groups: Group[]; onClose: () => void; onSaved: (m: string) => void }) {
  const [f, setF] = useState({ enabled: true, approval: offer.approval, description: offer.description ?? '', category: offer.category ?? '', groupIds: offer.groupIds });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save(enabled: boolean) {
    setBusy(true); setError(null);
    try {
      await api.put(`/deployment/self-service/packages/${offer.id}`, { ...f, enabled, description: f.description || null, category: f.category || null });
      onSaved(enabled ? `${offer.name} is offered in the Software Center.` : `${offer.name} was removed from the Software Center.`);
    } catch (e) { setError(errText(e)); } finally { setBusy(false); }
  }
  const platformGroups = groups.filter((g) => !g.platform || g.platform === offer.platform);
  return (
    <Modal title={`Offer ${offer.name} in the Software Center`} onClose={onClose} footer={<>
      {offer.enabled && <button className="btn danger" style={{ marginRight: 'auto' }} disabled={busy} onClick={() => void save(false)}>Stop Offering</button>}
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" disabled={busy} onClick={() => void save(true)}>{busy ? 'Saving…' : 'Offer'}</button>
    </>}>
      <div className="form-v">
        {error && <Callout tone="crit" icon="alert">{error}</Callout>}
        <Section title="What Users See">
          <Fields>
            <Field label="Category" hint="Users can filter by it."><input className="in" value={f.category} maxLength={60} onChange={(e) => setF({ ...f, category: e.target.value })} placeholder="e.g. Productivity" /></Field>
            <Field label="Version"><input className="in" value={offer.version ?? '—'} disabled /></Field>
            <Field label="Description" span><textarea className="in" rows={2} maxLength={500} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="What it is for, in one or two sentences." /></Field>
          </Fields>
        </Section>
        <Section title="Who Gets It">
          <Check checked={f.approval} onChange={(v) => setF({ ...f, approval: v })} hint="Users send a request with a reason; it installs as soon as someone with the Run Deployments permission approves it.">IT approves each request first</Check>
          {platformGroups.length > 0 && <div className="ff">
            <span className="ff-l">Offer only to these device groups</span>
            <Choices>{platformGroups.map((g) => <Check key={g.id} checked={f.groupIds.includes(g.id)} onChange={(v) => setF({ ...f, groupIds: v ? [...f.groupIds, g.id] : f.groupIds.filter((x) => x !== g.id) })}>{g.name}</Check>)}</Choices>
            <small className="ff-h">None chosen: every device of the package's platform.</small>
          </div>}
        </Section>
      </div>
    </Modal>
  );
}
