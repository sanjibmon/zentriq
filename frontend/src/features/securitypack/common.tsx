import { useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useAuth } from '../../app/auth';
import { Link } from '../../app/router';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Modal } from '../../components/Modal';
import { Section } from '../../components/Form';
import { Callout, DataTable, fmt, Panel, Pill } from '../../components/ui';
import { errText } from '../deployment/types';

export type Area = 'privilege' | 'bitlocker' | 'usb';
export interface AreaPolicy<S> { groupId: string | null; groupName: string | null; settings: S; updatedBy: string | null; updatedAt: string }
export interface Group { id: string; name: string; platform: string | null }

/** The Security Pack is licensed (or the trial). */
export function useSecurityPack(): boolean {
  const { me } = useAuth();
  const m = me?.license.modules ?? [];
  return m.includes('*') || m.includes('securitypack');
}

export function AddOnNotice() {
  return (
    <Callout tone="warn" icon="lock">
      <b>Requires the Advanced edition.</b> Endpoint privilege management, BitLocker management and USB device control are part of the Advanced
      edition. Your license does not include them; ask your Zentriq partner (<Link to="/licensing">Licensing</Link>).
    </Callout>
  );
}

export function ModeChoice<T extends string>({ value, options, onChange, disabled }: { value: T; options: [T, string, string, string?][]; onChange: (v: T) => void; disabled?: boolean }) {
  return (
    <div className="seg2" role="radiogroup" style={{ gridTemplateColumns: `repeat(${options.length}, 1fr)` }}>
      {options.map(([v, label, hint, tone]) => (
        <button key={v} type="button" role="radio" disabled={disabled} aria-checked={value === v} className={value === v ? `on ${tone ?? ''}` : ''} onClick={() => onChange(v)}>
          {label}<small>{hint}</small>
        </button>
      ))}
    </div>
  );
}

/** The policy for all devices and the device-group policies of one area, with Edit and Add for a group. */
export function Policies<S extends { mode: string }>({ area, policies, canManage, describe, modeLabel, modeTone, edit, changed, failed }: {
  area: Area; policies: AreaPolicy<S>[]; canManage: boolean; describe: (s: S) => ReactNode; modeLabel: Record<string, string>; modeTone: (m: string) => '' | 'ok' | 'warn' | 'crit' | 'info';
  edit: (p: AreaPolicy<S> | null, groupId: string | null) => void; changed: (t: string) => void; failed: (e: unknown) => void;
}) {
  const confirm = useConfirm();
  const groups = useApi(() => api.get<Group[]>(`/security-pack/${area}/groups`), [area]);
  const [pick, setPick] = useState(false);
  const all = policies.find((p) => p.groupId === null) ?? null;
  const rows = [
    { id: 'all', groupId: null as string | null, label: 'All devices', p: all },
    ...policies.filter((p) => p.groupId).map((p) => ({ id: p.groupId!, groupId: p.groupId, label: p.groupName ?? 'Group', p })),
  ];
  const free = (groups.data ?? []).filter((g) => !policies.some((p) => p.groupId === g.id));
  async function remove(groupId: string, name: string) {
    if (!await confirm({ title: `Remove the policy of ${name}?`, message: 'Its devices follow the policy for all devices again.', danger: true, confirmLabel: 'Remove' })) return;
    try { await api.del(`/security-pack/${area}/policy/${groupId}`); changed('Policy removed.'); } catch (e) { failed(e); }
  }
  return (
    <Panel flush title="Policies"
      right={canManage && free.length > 0 && <button className="btn" onClick={() => setPick(true)}>Add Policy for a Device Group</button>}>
      <DataTable rows={rows} empty="No policy." columns={[
        { key: 'g', label: 'Devices', render: (r) => <b>{r.label}</b> },
        { key: 'm', label: 'Mode', render: (r) => (r.p ? <Pill tone={modeTone(r.p.settings.mode)}>{modeLabel[r.p.settings.mode] ?? r.p.settings.mode}</Pill> : <Pill>Off</Pill>) },
        { key: 'd', label: 'Settings', className: 'wrap', render: (r) => (r.p ? describe(r.p.settings) : <span className="faint">Not set up yet</span>) },
        { key: 'u', label: 'Changed', render: (r) => (r.p ? <>{fmt.ago(r.p.updatedAt)}{r.p.updatedBy && <div className="faint" style={{ fontSize: 11.5 }}>{r.p.updatedBy}</div>}</> : '—') },
        { key: 'a', label: '', render: (r) => canManage && <span style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
          <button className="btn sm" onClick={() => edit(r.p, r.groupId)}>{r.p ? 'Edit' : 'Set Up'}</button>
          {r.groupId && <button className="btn sm danger" onClick={() => void remove(r.groupId!, r.label)}>Remove</button>}
        </span> },
      ]} />
      {pick && (
        <Modal title="Policy for a Device Group" onClose={() => setPick(false)} footer={<button className="btn" onClick={() => setPick(false)}>Cancel</button>}>
          <Section sub={<>Device groups come from Software Deployment (by department, location or device). <Link to="/modules/deploy?tab=groups">Manage device groups</Link></>}>
            <DataTable rows={free} onRow={(g) => { setPick(false); edit(null, g.id); }} rowTitle="Set up its policy" columns={[
              { key: 'n', label: 'Device group', render: (g) => <b>{g.name}</b> },
              { key: 'p', label: 'Platform', render: (g) => fmt.label(g.platform ?? 'any') },
            ]} />
          </Section>
        </Modal>
      )}
    </Panel>
  );
}

export function useMessages() {
  const [msg, setMsg] = useState<{ tone: 'info' | 'crit'; text: string } | null>(null);
  const [tick, setTick] = useState(0);
  return {
    msg, tick,
    changed: (text: string) => { setMsg({ tone: 'info', text }); setTick((t) => t + 1); },
    failed: (e: unknown) => setMsg({ tone: 'crit', text: errText(e) }),
    view: msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>,
  };
}

export const bytes = (n: number | null | undefined) => (n == null ? '—' : fmt.bytes(n));
