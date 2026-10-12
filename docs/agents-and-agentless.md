# Inventory collection: agentless scanning and the Zentriq Agent

Zentriq collects inventory two ways. Most organisations use both: **agentless** for servers, network gear and
everything on the LAN; the **agent** for laptops, branch offices and hosts that agentless access cannot reach.
Devices reported by both are merged into one asset (SMBIOS UUID → serial number → MAC → host name → IP).

| | Agentless (credentialed scan) | Zentriq Agent |
|---|---|---|
| Install on device | nothing | `ZentriqAgent-x64.msi` (Windows) |
| Network direction | server → device (WinRM 5985/5986, DCOM/RPC 135 + dynamic, SSH 22, SNMP 161/UDP) | device → server (HTTPS 8444, outbound only) |
| Works off the LAN / over the internet | no | yes (publish port 8444 or a reverse proxy) |
| Credentials stored in Zentriq | yes (encrypted) | none (per-device certificate) |
| Freshness | as often as the scan schedule | check-in every 15 min, full inventory every 12 h, or *Collect now* |
| Platforms | Windows, Linux, macOS, SNMP devices | Windows 10/11, Windows Server 2016+ |

## Agentless scanning

1. *Discovery & Scans › Credentials › Add credential*. Types: Windows domain / local account, SSH password / key,
   SNMP v2c / v3, Active Directory (LDAP bind). Optionally restrict each credential to the networks it belongs to.
2. *New scan*: targets (IP, CIDR, ranges, host names) **or** an Active Directory domain/OU, and the credentials to try.
3. For every responsive device the worker tries, in order: WinRM (HTTPS preferred when selected) → DCOM/WMI → SSH → SNMP.
   The first credential that works is used; a rejected credential is reported as *Credentials rejected* on the result.

What is collected: manufacturer, model, serial number, SMBIOS UUID, chassis, CPU, memory, disks and volumes
(BitLocker state), network adapters, OS and build, installed software (registry/dpkg/rpm, never `Win32_Product`),
installed updates, services, firewall/Secure Boot/TPM, antivirus (Security Center/Defender), logged-on user and local
administrators. SNMP: vendor, model, serial (ENTITY-MIB), firmware, interfaces and MACs.

### Requirements on targets

* **Windows (WinRM)** – WinRM enabled (`Enable-PSRemoting` or GPO), TCP 5985 (or 5986 with a certificate) allowed from
  the Zentriq server. The account must be a local administrator or have remote WMI read rights (root\cimv2 and the
  registry). With IP addresses, workgroup machines or local accounts over HTTP, add the targets to the server's
  WinRM `TrustedHosts` or use HTTPS (Kerberos needs host names). Local accounts additionally need
  `LocalAccountTokenFilterPolicy = 1` (UAC remote restrictions), otherwise access is denied.
* **Windows (DCOM fallback)** – TCP 135 plus the dynamic RPC range, "Windows Management Instrumentation" firewall
  rules enabled.
* **Linux / macOS** – SSH reachable; any user works for most data. Serial number and SMBIOS UUID are readable only by
  root on Linux (`/sys/class/dmi/id`) – without them, merging relies on MAC and host name. Commands are read-only; no
  `sudo` is used.
* **SNMP** – v3 (authPriv, SHA-256/AES) recommended; v2c only on trusted management networks.
* **Active Directory** – a domain account with read access; LDAPS (636) recommended, otherwise LDAP is signed and sealed.

### Limitations of agentless collection (in this release)

* **Reachability** – only devices the server can reach at scan time are inventoried. Laptops that are off, asleep,
  on VPN split-tunnel, at home or in another site without routing are missed until they are reachable. Use the agent
  for them.
* **Firewalls and hardening** – Windows Firewall, endpoint protection or network ACLs commonly block WinRM/RPC; each
  network path must be opened and kept open.
* **Credentials** – privileged credentials must be stored centrally (encrypted, scoped, audited). Password changes
  break collection until the credential is updated. Local accounts are subject to UAC remote restrictions; NTLM over
  HTTP requires `TrustedHosts` or HTTPS.
* **Point in time** – data is only as current as the last scan; changes between scans are not seen.
* **Linux without root** – serial number and SMBIOS UUID are not readable; macOS application versions are not
  collected (application names only).
* **SNMP** – identity and interfaces only (no installed software); v2c communities travel in clear text.
* **Active Directory** – a computer object is not proof that the device exists or is online; stale objects are skipped
  by the *inactive for N days* filter.
* **SSH host keys** are trusted on first use. If a host's key changes (rebuild or attack), collection from it stops
  until an administrator clears the key under *Credentials › SSH host keys*.
* **Load and noise** – scans generate authentication attempts that security tools may alert on; agree scan windows
  with the security team.
* **Not supported yet** – agentless collection from Windows Server Core over DCOM without the WMI firewall rules,
  macOS/Linux agents, cloud APIs.

## Zentriq Agent

### Server side

* The API listens on **8444/TCP** for agents (mutual TLS). Only `/api/agent/*` and `/downloads/*` are served on that
  port, so it can be published to the internet without exposing the console. Setup adds the firewall rule.
  Change the port in `C:\ProgramData\Zentriq\Config\zentriq.json` (`"Https": { "AgentPort": 8444, "AgentPublicUrl": "https://zq.example.com:8444" }`)
  and run `zentriqctl repair`.
* Each installation has its own **agent CA** (`Secrets\agent-ca.pfx`, readable by the API service only, included in
  backups). It issues one client certificate per agent (1 year, renewed automatically 30 days before expiry).

### Deploying

1. *Agents › New enrollment code*: name, validity (1–365 days), optional maximum number of devices and the address
   agents should use. The code (`ZQ1.…`) is shown once; it contains the server address, a one-time-readable secret and
   the server certificate's key pin.
2. Download the MSI from *Agents* (or `https://<server>/downloads/ZentriqAgent-x64.msi`) and install:
   ```
   msiexec /i ZentriqAgent-x64.msi /qn ENROLLMENTCODE="ZQ1...." [PROXYURL="http://proxy:8080"]
   ```
   Works with Intune (line-of-business app), Configuration Manager, Group Policy or interactively (the wizard asks
   for the code). Enroll later with `"C:\Program Files\Zentriq Agent\ZentriqAgent.exe" --enroll ZQ1....`.
3. The device appears under *Agents* within a minute and its inventory shortly after.

### How it works

* Service `ZentriqAgent` (LocalSystem, delayed start, below-normal priority, restart on failure). Requires .NET Framework 4.8.
* On enrollment the agent creates an RSA-2048 key, sends only a certificate request, and stores the issued certificate
  in *Local Computer › Personal* with a non-exportable key. The enrollment code is deleted from the registry.
* The server certificate is accepted when it is publicly trusted for the name **or** matches the pinned key; pins are
  refreshed at every check-in (so a renewed self-signed certificate is picked up while the old one is still valid).
* Every 15 minutes it checks in; every 12 hours (or on *Collect now*) it sends a gzip-compressed inventory. If the
  server is unreachable the inventory is queued and sent later; retries back off.
* *Revoke* in the console blocks the agent immediately (its certificate is no longer accepted) and the agent removes
  its certificate. Uninstall removes the certificate and state (logs are kept in `C:\ProgramData\Zentriq\Agent\Logs`).
* Troubleshooting: `ZentriqAgent.exe --status`, `--once`, `--collect-now`, `--collect inventory.json` (local only).

### Agent limitations (this release)

* Windows only; no Linux/macOS agent yet. Needs .NET Framework 4.8 (not present by default on Server 2016/2019).
* Inventory only – the agent does not deploy software, patch or provide remote control.
* If the server's self-signed certificate is replaced by a new self-signed one **after** the old one stopped working,
  agents must be re-enrolled; use a CA-issued certificate for internet-facing deployments.
* The enrollment code is readable by local administrators of the device between installation and first start of the
  service (seconds); it is limited by validity and use count and can be revoked.
