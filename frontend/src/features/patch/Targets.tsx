import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { useDepartments } from '../departments/Departments';
import { DevicePicker } from '../deployment/DeploymentDialogs';

/** Devices chosen for a job (sent as assetIds / groupIds / departmentIds / all). */
export interface Targets { assetIds: string[]; groupIds: string[]; departmentIds: string[]; all: boolean }

export interface TargetState { to: 'devices' | 'groups' | 'departments' | 'all'; assets: { id: string; name: string }[]; groupIds: string[]; departmentIds: string[] }

export const emptyTargets = (assets: { id: string; name: string }[] = []): TargetState => ({ to: 'devices', assets, groupIds: [], departmentIds: [] });

export function targetsOf(s: TargetState): Targets {
  return { all: s.to === 'all', assetIds: s.to === 'devices' ? s.assets.map((a) => a.id) : [], groupIds: s.to === 'groups' ? s.groupIds : [], departmentIds: s.to === 'departments' ? s.departmentIds : [] };
}

export function targetsEmpty(s: TargetState): boolean {
  return s.to === 'devices' ? s.assets.length === 0 : s.to === 'groups' ? s.groupIds.length === 0 : s.to === 'departments' ? s.departmentIds.length === 0 : false;
}

/** A stored target (from a policy or schedule) back into the picker. */
export function stateOf(t: Partial<Targets> | null | undefined, names: Record<string, string> = {}): TargetState {
  if (!t) return emptyTargets();
  if (t.all) return { ...emptyTargets(), to: 'all' };
  if ((t.groupIds ?? []).length) return { ...emptyTargets(), to: 'groups', groupIds: t.groupIds! };
  if ((t.departmentIds ?? []).length) return { ...emptyTargets(), to: 'departments', departmentIds: t.departmentIds! };
  return emptyTargets((t.assetIds ?? []).map((id) => ({ id, name: names[id] ?? id })));
}

export function describeTargets(t: Partial<Targets> | null | undefined): string {
  if (!t) return '—';
  if (t.all) return 'All devices with the Zentriq Agent';
  if ((t.groupIds ?? []).length) return `${t.groupIds!.length} device group(s)`;
  if ((t.departmentIds ?? []).length) return `${t.departmentIds!.length} department(s)`;
  return `${(t.assetIds ?? []).length} device(s)`;
}

interface GroupLite { id: string; name: string; devices: number }

/** Choose devices, device groups (Software Deployment), departments or all devices with the agent. */
export function TargetPicker({ value, onChange, allLabel = 'All devices with the Zentriq Agent' }: { value: TargetState; onChange: (v: TargetState) => void; allLabel?: string }) {
  const deps = useDepartments();
  const groups = useApi(() => api.get<GroupLite[]>('/deployment/groups').catch(() => [] as GroupLite[]), []);
  const set = (p: Partial<TargetState>) => onChange({ ...value, ...p });
  const toggle = (list: string[], id: string) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id]);
  const gl = groups.data ?? [];
  return (
    <>
      <label>On
        <select className="in" value={value.to} onChange={(e) => set({ to: e.target.value as TargetState['to'] })}>
          <option value="devices">Devices I choose</option>
          {gl.length > 0 && <option value="groups">Device groups</option>}
          <option value="departments">Departments</option>
          <option value="all">{allLabel}</option>
        </select>
      </label>
      {value.to === 'devices' && <>
        <DevicePicker onPick={(d) => { if (!value.assets.some((a) => a.id === d.id)) set({ assets: [...value.assets, d] }); }} />
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>{value.assets.map((a) => <span key={a.id} className="chip">{a.name}<button type="button" className="btn sm" aria-label={`Remove ${a.name}`} onClick={() => set({ assets: value.assets.filter((x) => x.id !== a.id) })}>✕</button></span>)}</div>
      </>}
      {value.to === 'groups' && <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px' }}>{gl.map((g) => <label key={g.id} className="row-check"><input type="checkbox" checked={value.groupIds.includes(g.id)} onChange={() => set({ groupIds: toggle(value.groupIds, g.id) })} /> {g.name} ({g.devices})</label>)}</div>}
      {value.to === 'departments' && <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 14px' }}>{(deps.data?.items ?? []).map((d) => <label key={d.id} className="row-check"><input type="checkbox" checked={value.departmentIds.includes(d.id)} onChange={() => set({ departmentIds: toggle(value.departmentIds, d.id) })} /> {d.name}</label>)}</div>}
    </>
  );
}
