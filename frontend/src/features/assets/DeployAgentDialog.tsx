import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { Asset, EnrollmentToken, NewEnrollmentToken, Paged } from '../../api/types';
import { useApi } from '../../app/useApi';
import { CopyBox } from '../../components/CopyBox';
import { Callout } from '../../components/ui';
import { errorText } from '../discovery/DirectoryPanel';
import { Command, HelpSteps, Note, Step, UiBtn, UiPath, Val } from '../../components/Help';

type Tool = 'intune' | 'sccm' | 'gpo' | 'manual';

function download(name: string, text: string, type = 'text/plain') {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type }));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

/**
 * Agent rollout helper: picks an enrollment code and produces ready-to-use deployment content for the tools
 * administrators already use (Intune, Configuration Manager, Group Policy), plus the list of assets that still
 * have no agent. Zentriq itself does not push software to computers.
 */
export function DeployAgentDialog({ onClose }: { onClose: () => void }) {
  const tokens = useApi(() => api.get<EnrollmentToken[]>('/agents/enrollment-tokens'), []);
  const missing = useApi(() => api.get<Paged<Asset>>('/assets', { agent: 'without', pageSize: 500, sort: 'hostname' }), []);
  const [tokenId, setTokenId] = useState('');
  const [code, setCode] = useState<NewEnrollmentToken | null>(null);
  const [tool, setTool] = useState<Tool>('gpo');
  const [share, setShare] = useState('\\\\fileserver\\software\\Zentriq\\ZentriqAgent-x64.msi');
  const [error, setError] = useState<string | null>(null);
  const active = (tokens.data ?? []).filter((t) => t.state === 'active');

  useEffect(() => { if (!tokenId && active.length > 0) setTokenId(active[0]!.id); }, [active, tokenId]);
  useEffect(() => {
    if (!tokenId) { setCode(null); return; }
    setError(null);
    api.get<NewEnrollmentToken>(`/agents/enrollment-tokens/${tokenId}/code`).then(setCode).catch((e: unknown) => { setCode(null); setError(errorText(e)); });
  }, [tokenId]);

  async function createCode() {
    setError(null);
    try {
      // No address: the server uses its name (not the IP this browser happens to use), so devices keep finding it when
      // its IP address changes.
      const t = await api.post<NewEnrollmentToken>('/agents/enrollment-tokens', { name: `Rollout ${new Date().toLocaleDateString()}`, validDays: 30, maxUses: null, serverUrl: null });
      tokens.reload();
      setTokenId(t.id);
    } catch (e) { setError(errorText(e)); }
  }

  const c = code?.code ?? 'ZQ1.…';
  const msiArgs = `/qn ENROLLMENTCODE="${c}"`;
  const gpoScript = `# Zentriq Agent - computer startup script (Group Policy)
# Computer Configuration > Policies > Windows Settings > Scripts (Startup/Shutdown) > Startup > PowerShell Scripts
# Runs as SYSTEM at computer startup on the computers in the linked OU. Installs once, then does nothing.
$ErrorActionPreference = 'Stop'
if (Get-Service -Name ZentriqAgent -ErrorAction SilentlyContinue) { return }
$Source = '${share.replace(/'/g, "''")}'   # share readable by "Domain Computers"
$Msi = Join-Path $env:TEMP 'ZentriqAgent-x64.msi'
Copy-Item -Path $Source -Destination $Msi -Force
$Log = Join-Path $env:TEMP 'ZentriqAgent-install.log'
$p = Start-Process -FilePath msiexec.exe -ArgumentList '/i', ('"' + $Msi + '"'), '/qn', 'ENROLLMENTCODE=${c}', '/l*v', ('"' + $Log + '"') -Wait -PassThru
Remove-Item $Msi -Force -ErrorAction SilentlyContinue
exit $p.ExitCode
`;
  const csv = 'hostname,ip,os,last_seen\n' + (missing.data?.items ?? []).map((a) => [a.hostname ?? '', a.ip ?? '', a.osName ?? '', a.lastSeenAt].map((v) => `"${String(v).replace(/"/g, '""')}"`).join(',')).join('\n');
  const windowsMissing = (missing.data?.items ?? []).filter((a) => a.osFamily === 'windows' || a.osFamily == null);

  return (
    <div className="modal-scrim" role="dialog" aria-modal="true" aria-labelledby="da-title">
      <div className="modal-card" style={{ width: 'min(820px, 100%)' }}>
        <header><h2 id="da-title">Deploy the Zentriq Agent</h2></header>
        <div className="body form-v">
          <Callout icon="info">
            <b>{missing.data ? missing.data.total : '…'}</b> active asset(s) have no agent{windowsMissing.length ? ` (${windowsMissing.length} Windows or unknown)` : ''}.
            Roll the agent out with the tool you already use — the agent then reports hardware, software, services and logged-on users every 12 hours and on demand, even off the network.
            {' '}<button type="button" className="link" onClick={() => download('assets-without-agent.csv', csv, 'text/csv')}>Download the list (CSV)</button> to target a collection or group.
          </Callout>
          {error && <Callout tone="crit" icon="alert">{error}</Callout>}
          <div className="form-grid">
            <label>Enrollment code
              <select className="in" value={tokenId} onChange={(e) => setTokenId(e.target.value)}>
                {active.length === 0 && <option value="">No active code</option>}
                {active.map((t) => <option key={t.id} value={t.id}>{t.name} — {t.serverUrl} · {t.uses}{t.maxUses ? `/${t.maxUses}` : ''} used · expires {new Date(t.expiresAt).toLocaleDateString()}</option>)}
              </select>
            </label>
            <div style={{ alignSelf: 'end' }}><button type="button" className="btn" onClick={() => void createCode()}>New code for this rollout</button></div>
          </div>
          {code && <small className="muted">Devices will connect to <Val>{code.serverUrl}</Val> — it must be reachable from them (TCP 8444).</small>}
          <div className="seg" role="tablist">
            {([['gpo', 'Group Policy'], ['intune', 'Microsoft Intune'], ['sccm', 'Configuration Manager'], ['manual', 'Manual / script']] as [Tool, string][]).map(([k, l]) => (
              <button key={k} type="button" className={tool === k ? 'on' : ''} onClick={() => setTool(k)}>{l}</button>
            ))}
          </div>
          {tool === 'gpo' && (
            <>
              <HelpSteps>
                <Step title="Put the installer on a share">Copy <a href="/downloads/ZentriqAgent-x64.msi" download>ZentriqAgent-x64.msi</a> to a share that <Val>Domain Computers</Val> can read.</Step>
                <Step title="Download the startup script">Enter that path below and download the script.</Step>
                <Step title="Add it to a GPO">In Group Policy Management, create a GPO on the computers' OU, open <UiPath>Computer Configuration › Policies › Windows Settings › Scripts › Startup › PowerShell Scripts</UiPath> and add the script.</Step>
                <Step title="Restart the computers">They install the agent at their next restart and appear under <UiPath>Agents</UiPath> a minute later. To apply the policy at once:
                  <Command value="gpupdate /force" shell="Command Prompt (Administrator)" label="Apply Group Policy now" /></Step>
              </HelpSteps>
              <label>MSI on the share <input className="in mono" value={share} onChange={(e) => setShare(e.target.value)} /></label>
              <div><button type="button" className="btn primary" disabled={!code} onClick={() => download('Install-ZentriqAgent.ps1', gpoScript)}>Download startup script</button></div>
            </>
          )}
          {tool === 'intune' && (
            <HelpSteps>
              <Step title="Add the app">In the Intune admin center open <UiPath>Apps › Windows › Add › Line-of-business app</UiPath> and upload <a href="/downloads/ZentriqAgent-x64.msi" download>ZentriqAgent-x64.msi</a>.</Step>
              <Step title="Enter the command-line arguments">In <UiPath>App information › Command-line arguments</UiPath>:<CopyBox value={`ENROLLMENTCODE="${c}"`} label="Intune command-line arguments" rows={2} /></Step>
              <Step title="Assign it">Assign it as <UiBtn>Required</UiBtn> to your device group. Devices install it at their next Intune sync.</Step>
            </HelpSteps>
          )}
          {tool === 'sccm' && (
            <HelpSteps>
              <Step title="Create the application">Type Windows Installer, from <a href="/downloads/ZentriqAgent-x64.msi" download>ZentriqAgent-x64.msi</a> on your content share.</Step>
              <Step title="Set the installation program"><Command value={`msiexec /i "ZentriqAgent-x64.msi" ${msiArgs}`} shell="Installation program" label="Install command" /></Step>
              <Step title="Deploy it">Detection: the Windows Installer product code (the wizard fills it in). Deploy as <UiBtn>Required</UiBtn> to a collection built from the CSV above.</Step>
            </HelpSteps>
          )}
          {tool === 'manual' && (
            <>
              <p style={{ margin: 0 }}>On the device, in the folder with the MSI:</p>
              <Command value={`msiexec /i ZentriqAgent-x64.msi ${msiArgs}`} shell="Command Prompt (Administrator)" label="Install command" />
            </>
          )}
          <Note tone="important">Treat the enrollment code like a password: anyone who has it can enroll a device until it expires or reaches its device limit. Revoke it under <UiPath>Agents › Enrollment codes</UiPath> when the rollout is done.</Note>
        </div>
        <footer><button type="button" className="btn" onClick={onClose}>Close</button></footer>
      </div>
    </div>
  );
}
