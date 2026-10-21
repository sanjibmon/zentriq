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
| Freshness | as often as the scan schedule | live connection (online/offline within seconds), check-in every 5 min – 3 h and inventory every 30 min – 24 h (chosen at installation, changeable per agent), or *Collect now* (seconds) |
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

### Updating agents

*Agents › Update* (one device) or *Update N agent(s)* (all) – the agent fetches the package from this server over its
authenticated connection, verifies its SHA-256 and upgrades itself in place, keeping its enrollment. Agents installed
before this feature need the new MSI installed once (no enrollment code). Details: [upgrade.md](upgrade.md#agents).

### Deploying

1. *Agents › New enrollment code*: name, validity (1–365 days), optional maximum number of devices, the address
   agents should use (pre-filled with the address you opened the console with, e.g. `https://192.168.1.8:8444`; it
   must be reachable from the devices) and the suggested schedule (*check the connection every* 5 min – 3 h, *send
   inventory every* 30 min – 24 h). The **code has 20 letters and digits** (`AB12C-DE34F-GH56J-KM78N`; dashes optional,
   O/I/L are read as 0/1/1). The server address is given separately. While the code is active you can show it again
   (*Enrollment codes › Show code*, stored encrypted, viewing is audited). Revoked, expired and used-up codes can be
   deleted one by one or all at once (*Delete old codes*); devices enrolled with them are not affected.
2. Download the MSI from *Agents* (or `https://<server>/downloads/ZentriqAgent-x64.msi`) and install. The wizard asks for
   the **server**, the **code** and two drop-downs: *Check the connection every* (5, 15, 30 min, 1, 2, 3 h) and *Send
   inventory every* (30 min, 1, 2, 3, 6, 12, 24 h). Silently:
   ```
   msiexec /i ZentriqAgent-x64.msi /qn SERVERURL="https://zentriq.example.com:8444" ENROLLMENTCODE="AB12C-DE34F-GH56J-KM78N" CHECKININTERVAL=30 INVENTORYINTERVAL=180 [PROXYURL="http://proxy:8080"]
   ```
   `SERVERURL` accepts `server`, `server:8444` or `https://server:8444` (HTTPS only; port 8444 when none is given).
   Without the intervals the code's suggestion applies. Leave `PROXYURL` **empty** unless the device can reach the
   server only through an `http://` web proxy. Enroll later with
   `"C:\Program Files\Zentriq Agent\ZentriqAgent.exe" --enroll <code> --server <address> [--checkin 30] [--inventory 180]`.
   Old `ZQ1.…` codes from earlier builds still work until they expire.
3. The device appears under *Agents* within a minute and its inventory shortly after. If it does not, see
   `C:\ProgramData\Zentriq\Agent\Logs\agent.log` on the device; the *Agents* page lists the common causes.

**Why the code is short and still safe.** The code carries no server key. Before sending it, the agent asks the server
to prove that it knows the code: the server answers with an HMAC of a random challenge and the key pins it serves, keyed
with SHA-256 of the code. The agent accepts only if the key it actually saw in the TLS handshake is one of the proven
pins, so a device in the middle can neither learn the code nor pose as the server. The server stores only SHA-256 of
the code; 14 of the 20 characters are secret (70 bits) and enrollment requests are rate-limited.

**Mass deployment** with the tools you already use. *Asset Inventory › Deploy agent* lists the assets without an
agent, picks or creates an enrollment code and produces the ready-made content: a Group Policy startup script, the
Intune command-line arguments, the Configuration Manager install command. Zentriq does not push software itself.
* **Intune**: *Apps › Windows › Line-of-business app*, upload the MSI, arguments `SERVERURL="…" ENROLLMENTCODE="…" CHECKININTERVAL=30 INVENTORYINTERVAL=180`.
* **Configuration Manager**: application with the `msiexec /i … /qn SERVERURL=… ENROLLMENTCODE=…` command.
* **Group Policy**: a computer startup script running the same `msiexec` command from a share.

### Schedule, connection and last contact

* **Live connection** (agents from build 71 on): the agent keeps one connection to the server open at all times (a
  request on its mutually authenticated channel that the server holds for about 25 seconds and the agent renews at
  once). The device shows **Online** as soon as the agent connects, and **Offline** the moment the connection closes
  (agent stopped, device shut down or restarted, connection reset) — or within 45 seconds when it dies silently
  (cable pulled, Wi-Fi gone). Each gap is recorded as the device's *last outage*. Console requests (collect now,
  service actions, software removal, updates, remote support install) reach an online agent **within seconds**.
  *Agents* refreshes every 10 seconds and shows **Online**, or **Offline · how long** (time since the last contact).
  Agents of earlier versions are judged by their check-ins as before (update them from *Agents*). The IP address shown
  is the device's IPv4 address.
* Besides that, each agent **checks in** at its check-in interval (settings, certificate renewal, queued requests) and
  **sends its inventory** at its inventory interval: *every 5 min, 15 min, 30 min, 1 hour, 2 hours or 3 hours* and
  *every 30 min, 1, 2, 3, 6, 12 or 24 hours* (chosen at installation, in this order in the installer lists). Change them
  for selected agents or all agents with *Agents › Schedule*; defaults for new codes are in *Agents › Settings*.
* *IT Asset Inventory* shows **No contact** with the time of the last contact for an offline agent (later *Offline N
  days*); it refreshes every 30 seconds. When the agent reaches the server again, queued inventory is sent.
* **Lost connection**: the agent retries after 1, 2, 4, 8 and then every 15 minutes, and at once when the network
  changes or the device wakes from sleep. If the server does not answer at its usual address it tries the server's
  other names and IP addresses (the server sends them at every check-in) and then **looks for the server on the local
  network** (UDP broadcast to port 8444; the server answers with its addresses). It connects only to a server that
  presents the pinned key, and keeps using the address that answered — so an agent finds the server again after a
  DHCP change of the server's address. Setup adds the firewall rule for UDP 8444 (local subnet).

### Uninstall protection

* *Agents › Settings › Require an uninstall code to remove the agent* (on by default). Each device then keeps its
  protection data readable by **SYSTEM only** (`HKLM\SOFTWARE\Zentriq\Agent\Protection`).
* To remove protected agents, create the **uninstall code**: *Agents › Uninstall code*. It is **one random code for all
  devices** (16 characters, `ABCD-EFGH-JKMN-PQRS`), valid for **one hour**; creating a new one replaces the previous
  code, and *Cancel code* withdraws it. Creating it is audited. Online agents receive it within seconds over their live
  connection (only its hash, readable by SYSTEM), others when they next connect; agents of earlier versions need
  updating first. Then on the device, as administrator: `"C:\Program Files\Zentriq Agent\ZentriqAgent.exe" --uninstall <code>`, or
  *Apps › Zentriq Agent › Modify › Remove* and enter the code, or `msiexec /x {product} UNINSTALLCODE=<code>`.
  (`--uninstall` starts the removal from a temporary copy and returns at once; Windows Installer shows the progress and
  the result is logged in `%TEMP%\ZentriqAgent-uninstall.log`.)
* Without a valid code Windows Installer refuses the removal (the code is checked as SYSTEM against the device's key),
  and while protected administrators cannot stop, reconfigure or delete the agent service. Agent updates from the
  console keep working. Turning protection off removes the key and the restriction at the next check-in; a revoked
  agent drops its protection.
* This protects against users and casual removal. A determined local administrator can still take ownership of the
  registry or kill the process (the service restarts it); combine with your EDR/tamper protection where required.

### How it works

* Service `ZentriqAgent` (LocalSystem, delayed start, below-normal priority, restart on failure). Requires .NET Framework 4.8.
* On enrollment the agent creates an RSA-2048 key, sends only a certificate request, and stores the issued certificate
  in *Local Computer › Personal* with a non-exportable key. The enrollment code is deleted from the registry.
* The server certificate is accepted when it is publicly trusted for the name **or** matches the pinned key; pins are
  refreshed at every check-in (so a renewed self-signed certificate is picked up while the old one is still valid).
* *Revoke* in the console blocks the agent immediately (its certificate is no longer accepted) and the agent removes
  its certificate. Uninstall removes the certificate and state (logs are kept in `C:\ProgramData\Zentriq\Agent\Logs`).
* Troubleshooting: `ZentriqAgent.exe --status` (server, other addresses, schedule, offline since, protection), `--once`,
  `--collect-now`, `--collect inventory.json` (local only).

### Agent limitations (this release)

* The inventory agent runs on Windows (macOS and Linux agents are planned; remote support already works on all three). Needs .NET Framework 4.8 (not present by default on Server 2016/2019; Windows 10 1903+/11 have it).
* Inventory only – the agent does not deploy software, patch or provide remote control.
* If the server's self-signed certificate is replaced by a new self-signed one **after** the old one stopped working,
  agents must be re-enrolled; use a CA-issued certificate for internet-facing deployments.
* The enrollment code is readable by local administrators of the device between installation and first start of the
  service (seconds); it is limited by validity and use count and can be revoked.
