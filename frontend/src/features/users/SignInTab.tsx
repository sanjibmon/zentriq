import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../app/useApi';
import { useConfirm } from '../../components/Confirm';
import { Icon } from '../../components/Icon';
import { Callout, DataTable, ErrorBox, Panel, Pill } from '../../components/ui';
import { CopyBox } from '../../components/CopyBox';
import type { GroupRow, RoleRow } from './RoleDialogs';

interface LdapCfg { enabled: boolean; host: string; port: number; ldaps: boolean; verifyCertificate: boolean; dcName: string | null; baseDn: string | null; domain: string | null; createUsers: boolean }
interface EntraCfg { enabled: boolean; tenantId: string; clientId: string; createUsers: boolean; secretStored: boolean }
interface Mapping { id: string; provider: 'ldap' | 'entra'; groupRef: string; groupName: string | null; roleId: string | null; groupId?: string | null; zentriqGroup?: string | null }
interface SignInData { ldap: LdapCfg; entra: EntraCfg; redirectUri: string; mappings: Mapping[]; users: Record<string, number> }
interface TestResult { ok: boolean; message: string; groups?: { dn: string; name: string | null }[]; roles?: string[] }

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
const PROVIDER: Record<string, string> = { ldap: 'Active Directory', entra: 'Microsoft Entra ID' };

/** Users, Roles & Groups › Sign-in: Active Directory and Microsoft Entra ID sign-in, and directory groups → roles or Zentriq groups. */
export function SignInTab({ roles, groups = [], onChanged }: { roles: RoleRow[]; groups?: GroupRow[]; onChanged?: () => void }) {
  const [tick, setTick] = useState(0);
  const data = useApi(() => api.get<SignInData>('/auth/sign-in'), [tick]);
  const confirm = useConfirm();
  const [ldap, setLdap] = useState<LdapCfg | null>(null);
  const [entra, setEntra] = useState<EntraCfg | null>(null);
  const [entraKey, setEntraKey] = useState('');
  const [msg, setMsg] = useState<{ tone: 'ok' | 'crit'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [test, setTest] = useState({ username: '', password: '' });
  const [testResult, setTestResult] = useState<TestResult | null>(null);
  const [map, setMap] = useState<{ provider: 'ldap' | 'entra'; groupRef: string; groupName: string; roleId: string }>({ provider: 'ldap', groupRef: '', groupName: '', roleId: '' });
  const d = data.data;
  useEffect(() => { if (d) { setLdap(d.ldap); setEntra(d.entra); } }, [d]);

  async function run(f: () => Promise<unknown>, ok: string) {
    setBusy(true); setMsg(null);
    try { await f(); setMsg({ tone: 'ok', text: ok }); setTick((x) => x + 1); onChanged?.(); } catch (e) { setMsg({ tone: 'crit', text: errText(e) }); } finally { setBusy(false); }
  }
  async function runTest() {
    setBusy(true); setTestResult(null);
    try { setTestResult(await api.post<TestResult>('/auth/sign-in/ldap/test', test)); } catch (e) { setTestResult({ ok: false, message: errText(e) }); } finally { setBusy(false); setTest((t) => ({ ...t, password: '' })); }
  }
  async function removeMapping(m: Mapping) {
    if (!(await confirm({ title: 'Remove this group?', danger: true, confirmLabel: 'Remove', message: <>Members of <b>{m.groupName ?? m.groupRef}</b> lose {m.groupId ? <>the Zentriq group <b>{m.zentriqGroup}</b></> : <>the role <b>{roles.find((r) => r.id === m.roleId)?.name ?? m.roleId}</b></>} now (unless another mapped group gives it at their next sign-in).</> }))) return;
    await run(() => api.del(`/auth/sign-in/mappings/${m.id}`), 'Group removed.');
  }

  if (data.error) return <ErrorBox error={data.error} />;
  if (!d || !ldap || !entra) return <p className="muted">Loading…</p>;
  const roleName = (id: string | null) => roles.find((r) => r.id === id)?.name ?? id ?? '';
  return (
    <>
      {msg && <Callout tone={msg.tone} icon={msg.tone === 'crit' ? 'alert' : 'info'}>{msg.text}</Callout>}
      <Callout tone="info" icon="info">Local Zentriq accounts (e-mail and password, with two-step verification) keep working. Directory users get their roles from the
        directory groups mapped below; their directory password and policies (and Entra ID's multi-factor authentication) apply.
        Accounts: {d.users.local ?? 0} local · {d.users.ldap ?? 0} Active Directory · {d.users.entra ?? 0} Microsoft Entra ID.</Callout>
      <div className="grid-2">
        <Panel title="Active Directory (LDAP / LDAPS)" sub="Users sign in with DOMAIN\user or user@domain and their Windows password"
          right={<Pill tone={d.ldap.enabled ? 'ok' : ''}>{d.ldap.enabled ? 'On' : 'Off'}</Pill>}>
          <div className="form-v">
            <label className="row-check"><input type="checkbox" checked={ldap.enabled} onChange={(e) => setLdap({ ...ldap, enabled: e.target.checked })} /> Allow sign-in with Active Directory accounts</label>
            <div className="form-grid-2">
              <label>Domain controller or domain <input className="in mono" value={ldap.host} onChange={(e) => setLdap({ ...ldap, host: e.target.value })} placeholder="dc01.contoso.com" /></label>
              <label>Port <input className="in" type="number" value={ldap.port} onChange={(e) => setLdap({ ...ldap, port: Number(e.target.value) })} /></label>
              <label>Domain for plain user names <input className="in mono" value={ldap.domain ?? ''} onChange={(e) => setLdap({ ...ldap, domain: e.target.value })} placeholder="CONTOSO or contoso.com" /></label>
              <label>Search base (optional) <input className="in mono" value={ldap.baseDn ?? ''} onChange={(e) => setLdap({ ...ldap, baseDn: e.target.value })} placeholder="DC=contoso,DC=com" /></label>
            </div>
            <label className="row-check"><input type="checkbox" checked={ldap.ldaps} onChange={(e) => setLdap({ ...ldap, ldaps: e.target.checked, port: e.target.checked ? 636 : 389 })} /> LDAPS (TLS, port 636) — recommended; LDAP 389 uses Kerberos / NTLM signing and sealing</label>
            {ldap.ldaps && <label className="row-check"><input type="checkbox" checked={ldap.verifyCertificate} onChange={(e) => setLdap({ ...ldap, verifyCertificate: e.target.checked })} /> Check the domain controller's certificate</label>}
            {ldap.ldaps && ldap.verifyCertificate && /^\d+\.\d+\.\d+\.\d+$/.test(ldap.host.trim()) && <label>Domain controller name on its certificate <input className="in mono" value={ldap.dcName ?? ''} onChange={(e) => setLdap({ ...ldap, dcName: e.target.value })} /></label>}
            <label className="row-check"><input type="checkbox" checked={ldap.createUsers} onChange={(e) => setLdap({ ...ldap, createUsers: e.target.checked })} /> Create the Zentriq account at the first sign-in (members of mapped groups only)</label>
            <div><button className="btn primary" disabled={busy} onClick={() => void run(() => api.put('/auth/sign-in/ldap', ldap), 'Active Directory sign-in saved.')}>Save</button></div>
            <details>
              <summary>Test with an account</summary>
              <div className="form-grid-2" style={{ marginTop: 8 }}>
                <label>User name <input className="in mono" value={test.username} onChange={(e) => setTest({ ...test, username: e.target.value })} placeholder="CONTOSO\jane" autoComplete="off" /></label>
                <label>Password <input className="in" type="password" value={test.password} onChange={(e) => setTest({ ...test, password: e.target.value })} autoComplete="new-password" /></label>
              </div>
              <button className="btn sm" disabled={busy || !test.username || !test.password} onClick={() => void runTest()}>Test Sign-in</button>
              {testResult && <div style={{ marginTop: 8 }}>
                <Callout tone={testResult.ok ? 'ok' : 'crit'} icon={testResult.ok ? 'info' : 'alert'}>{testResult.message}</Callout>
                {!!testResult.groups?.length && <div className="tbl-wrap"><table className="t">
                  <thead><tr><th>Group</th><th /></tr></thead>
                  <tbody>{testResult.groups.map((g) => <tr key={g.dn}><td><b>{g.name ?? g.dn}</b><div className="faint mono" style={{ fontSize: 11 }}>{g.dn}</div></td>
                    <td><button className="btn sm" onClick={() => setMap({ provider: 'ldap', groupRef: g.dn, groupName: g.name ?? '', roleId: map.roleId })}>Map this group</button></td></tr>)}</tbody>
                </table></div>}
              </div>}
            </details>
          </div>
        </Panel>

        <Panel title="Microsoft Entra ID (single sign-on)" sub="A Sign In With Microsoft button on the sign-in page (OpenID Connect)"
          right={<Pill tone={d.entra.enabled ? 'ok' : ''}>{d.entra.enabled ? 'On' : 'Off'}</Pill>}>
          <div className="form-v">
            <label className="row-check"><input type="checkbox" checked={entra.enabled} onChange={(e) => setEntra({ ...entra, enabled: e.target.checked })} /> Allow Sign In With Microsoft</label>
            <ol className="faint" style={{ fontSize: 12.5, paddingLeft: 18, margin: 0 }}>
              <li>Entra admin center › App registrations › New registration (single tenant). Platform <b>Web</b>, redirect URI below.</li>
              <li>Token configuration › Add groups claim (Security groups, Group ID) — or define app roles and assign them.</li>
              <li>Certificates &amp; secrets › New client secret; copy its value here.</li>
            </ol>
            <CopyBox label="Redirect URI (one per console address you use)" value={d.redirectUri} />
            <div className="form-grid-2">
              <label>Directory (tenant) ID <input className="in mono" value={entra.tenantId} onChange={(e) => setEntra({ ...entra, tenantId: e.target.value })} placeholder="00000000-0000-0000-0000-000000000000" /></label>
              <label>Application (client) ID <input className="in mono" value={entra.clientId} onChange={(e) => setEntra({ ...entra, clientId: e.target.value })} /></label>
              <label>Client secret value <input className="in mono" type="password" value={entraKey} onChange={(e) => setEntraKey(e.target.value)} placeholder={d.entra.secretStored ? 'Stored — leave empty to keep it' : ''} autoComplete="new-password" /></label>
            </div>
            <label className="row-check"><input type="checkbox" checked={entra.createUsers} onChange={(e) => setEntra({ ...entra, createUsers: e.target.checked })} /> Create the Zentriq account at the first sign-in (members of mapped groups only)</label>
            <p className="faint" style={{ fontSize: 12, margin: 0 }}>People in more than 200 groups: add the Microsoft Graph application permission <span className="mono">GroupMember.Read.All</span> (admin consent) so Zentriq can read their groups.</p>
            <div><button className="btn primary" disabled={busy} onClick={() => void run(async () => { await api.put('/auth/sign-in/entra', { ...entra, clientSecret: entraKey || null }); setEntraKey(''); }, 'Microsoft Entra ID sign-in saved.')}>Save</button></div>
          </div>
        </Panel>
      </div>

      <Panel title="Directory Groups and Roles" sub="Members of a directory group get its role — or a Zentriq group with its roles and data scope — at each sign-in; leaving the group removes it" flush>
        <div className="toolbar-row" style={{ padding: '10px 14px', flexWrap: 'wrap' }}>
          <select className="in" value={map.provider} onChange={(e) => setMap({ ...map, provider: e.target.value as 'ldap' | 'entra' })}>
            <option value="ldap">Active Directory</option><option value="entra">Microsoft Entra ID</option>
          </select>
          <input className="in mono" style={{ flex: 2, minWidth: 220 }} value={map.groupRef} onChange={(e) => setMap({ ...map, groupRef: e.target.value })}
            placeholder={map.provider === 'ldap' ? 'CN=Zentriq Admins,OU=Groups,DC=contoso,DC=com' : 'Group object ID or app role value'} />
          <input className="in" style={{ flex: 1, minWidth: 140 }} value={map.groupName} onChange={(e) => setMap({ ...map, groupName: e.target.value })} placeholder="Name (shown here)" />
          <select className="in" value={map.roleId} onChange={(e) => setMap({ ...map, roleId: e.target.value })} aria-label="Gives">
            <option value="">Gives…</option>
            <optgroup label="Role">{roles.map((r) => <option key={r.id} value={`role:${r.id}`}>{r.name}</option>)}</optgroup>
            {groups.length > 0 && <optgroup label="Zentriq group">{groups.map((g) => <option key={g.id} value={`group:${g.id}`}>{g.name}</option>)}</optgroup>}
          </select>
          <button className="btn primary" disabled={busy || !map.groupRef.trim() || !map.roleId}
            onClick={() => void run(async () => {
              const [kind, target] = [map.roleId.slice(0, map.roleId.indexOf(':')), map.roleId.slice(map.roleId.indexOf(':') + 1)];
              await api.post('/auth/sign-in/mappings', { provider: map.provider, groupRef: map.groupRef, groupName: map.groupName || null, roleId: kind === 'role' ? target : null, groupId: kind === 'group' ? target : null });
              setMap({ ...map, groupRef: '', groupName: '' });
            }, 'Group mapped.')}><Icon name="plus" size={13} /> Add</button>
        </div>
        <DataTable rows={d.mappings} empty="No directory groups mapped yet. Directory users without a mapped group (or a role given in Zentriq) cannot sign in." columns={[
          { key: 'p', label: 'Directory', render: (m) => PROVIDER[m.provider] },
          { key: 'g', label: 'Group', className: 'wrap', render: (m) => <><b>{m.groupName ?? m.groupRef}</b>{m.groupName && <div className="faint mono" style={{ fontSize: 11 }}>{m.groupRef}</div>}</> },
          { key: 'r', label: 'Gives', render: (m) => m.groupId ? <><span className="faint">Group</span> <b>{m.zentriqGroup}</b></> : roleName(m.roleId) },
          { key: 'x', label: '', render: (m) => <button className="icon-btn" aria-label="Remove" onClick={() => void removeMapping(m)}><Icon name="trash" size={14} /></button> },
        ]} />
      </Panel>
    </>
  );
}
