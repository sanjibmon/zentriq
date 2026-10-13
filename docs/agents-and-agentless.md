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
2. *New scan*: targets (IP, CIDR, ranges, host names) **or** the computers of an Active Directory connection, and the
   credentials to try. **Without a credential a scan only finds devices** (address, name, open ports, MAC on the local
   subnet); it cannot read hardware, software or network details. The dialog warns and asks for confirmation.
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
* **Active Directory** – see below.

### Preparing Windows computers for agentless inventory (Group Policy)

With Windows Firewall on (the default), Windows computers answer nothing that agentless inventory can use. For
domain computers, one GPO linked to the computers' OU enables it:

1. *Computer Configuration › Policies › Administrative Templates › Windows Components › Windows Remote Management (WinRM) › WinRM Service*:
   **Allow remote server management through WinRM** = Enabled, IPv4 filter = `*` (or the Zentriq server address).
2. *… › System Services*: **Windows Remote Management (WS-Management)** = Automatic.
3. *… › Windows Defender Firewall with Advanced Security › Inbound Rules*: new rule, TCP **5985**, *Remote IP address* =
   the Zentriq server only.
4. Give the scan account rights: a domain group that is in the computers' local *Administrators* group (GPO
   *Restricted Groups* / *Local Users and Groups*), or remote WMI/registry read rights.
5. `gpupdate /force` (or wait for the refresh), then scan by **host name** (Kerberos) – or by IP with WinRM HTTPS (5986).

For a single computer: `Enable-PSRemoting -Force` in an elevated PowerShell, then allow 5985 from the Zentriq server.

**Firewalled hosts are still found.** If no probed port answers, Zentriq checks the host with ARP (same subnet) and
ICMP ping. A host that answers is recorded as *Inventory unavailable* with the reason ("Host is up … every probed port
is blocked by a firewall") and, on the local subnet, its MAC address. To inventory it, open WinRM as above or install
the agent. Devices that block ping and are on another subnet cannot be detected without an agent.

## Active Directory

*Discovery & Scans › Active Directory › Add connection*:

| Field | Example |
|---|---|
| Domain controller IP address or host name | `192.168.1.2` or `dc01.corp.example.com` |
| Domain controller name (FQDN) | `dc01.corp.example.com` – used to check the LDAPS certificate when you connect by IP |
| User ID | `CORP\svc-zentriq` or `svc-zentriq@corp.example.com` (a `CN=…` DN works with LDAPS only) |
| Password | stored encrypted (AES-256-GCM); never shown again or returned by the API |
| Domain or OU (distinguished name) | `DC=corp,DC=example,DC=com` or `OU=Head Office,DC=corp,DC=example,DC=com` – leave empty and click *Test connection* to fill in the domain |
| LDAPS / port | LDAPS 636 (recommended; *Verify certificate* on) or LDAP 389, which is signed and sealed with Kerberos/NTLM |

*Test connection* signs in and reads the container without saving. *Sync now* reads, under that container,
**organizational units, users, groups and computers** (paged, read-only; nothing is written to AD):

* Computers: name, DNS name, operating system and version, enabled/disabled, last logon, OU – linked to the asset when
  it is in the inventory.
* Users: display name, login (sAMAccountName / UPN), e-mail, department, title, enabled/disabled, last logon,
  password last set, group memberships, OU.
* Groups: name, description, e-mail; OUs: name and DN.

Objects deleted from AD disappear at the next sync. *Scan computers* starts a scan of the enabled computers in that
container (skipping those with no logon for N days) with the inventory credentials you select. An ordinary domain user
can read these attributes; no administrator account is needed for AD itself.

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
   agents should use (pre-filled with the address you opened the console with, e.g. `https://192.168.1.8:8444` – it
   must be reachable from the devices). The code (`ZQ1.…`) contains the server address, a secret and the server
   certificate's key pin. While it is active you can show it again with *Enrollment codes › Show code* (stored
   encrypted on the server; viewing is audited). Codes created before this feature was added cannot be shown again; create a new one.
2. Download the MSI from *Agents* (or `https://<server>/downloads/ZentriqAgent-x64.msi`) and install:
   ```
   msiexec /i ZentriqAgent-x64.msi /qn ENROLLMENTCODE="ZQ1...." [PROXYURL="http://proxy:8080"]
   ```
   Leave `PROXYURL` (wizard: *Proxy*) **empty** unless the device can reach the server only through a web proxy. It
   must be an `http://` proxy address – never the Zentriq server address. The wizard rejects invalid values; the agent
   ignores an `https://` proxy or one that points at the server (logged as a warning).
   Works with Intune (line-of-business app), Configuration Manager, Group Policy or interactively (the wizard asks
   for the code). Enroll later with `"C:\Program Files\Zentriq Agent\ZentriqAgent.exe" --enroll ZQ1....`.
3. The device appears under *Agents* within a minute and its inventory shortly after. If it does not, see
   `C:\ProgramData\Zentriq\Agent\Logs\agent.log` on the device; the *Agents* page lists the common causes.

**Mass deployment** with the tools you already use. *Asset Inventory › Deploy agent* lists the assets without an
agent (CSV for building a collection or group), picks or creates an enrollment code and produces the ready-made
content: a Group Policy startup script, the Intune command-line arguments, the Configuration Manager install command.
Zentriq does not push software to computers itself.
* **Intune**: *Apps › Windows › Line-of-business app*, upload the MSI, command-line arguments `ENROLLMENTCODE="ZQ1…"`.
* **Configuration Manager**: application with the `msiexec /i … /qn ENROLLMENTCODE="ZQ1…"` command.
* **Group Policy**: a computer startup script running the same `msiexec` command from a share
  (GPO software installation cannot pass the code; use a script or an MST transform with `ENROLLMENTCODE`).

### How it works

* Service `ZentriqAgent` (LocalSystem, delayed start, below-normal priority, restart on failure). Requires .NET Framework 4.8.
* On enrollment the agent creates an RSA-2048 key, sends only a certificate request, and stores the issued certificate
  in *Local Computer › Personal* with a non-exportable key. The enrollment code is deleted from the registry.
* The server certificate is accepted when it is publicly trusted for the name **or** matches the pinned key; pins are
  refreshed at every check-in (so a renewed self-signed certificate is picked up while the old one is still valid).
* Every 2 minutes it checks in (and runs any service start/stop/restart requested in the console); every 12 hours (or on *Collect now*) it sends a gzip-compressed inventory. If the
  server is unreachable the inventory is queued and sent later; retries back off.
* *Revoke* in the console blocks the agent immediately (its certificate is no longer accepted) and the agent removes
  its certificate. Uninstall removes the certificate and state (logs are kept in `C:\ProgramData\Zentriq\Agent\Logs`).
* Troubleshooting: `ZentriqAgent.exe --status`, `--once`, `--collect-now`, `--collect inventory.json` (local only).

### Agent limitations (this release)

* Windows only; no Linux/macOS agent yet. Needs .NET Framework 4.8 (not present by default on Server 2016/2019; Windows 10 1903+/11 have it).
* Inventory only – the agent does not deploy software, patch or provide remote control.
* If the server's self-signed certificate is replaced by a new self-signed one **after** the old one stopped working,
  agents must be re-enrolled; use a CA-issued certificate for internet-facing deployments.
* The enrollment code is readable by local administrators of the device between installation and first start of the
  service (seconds); it is limited by validity and use count and can be revoked.
