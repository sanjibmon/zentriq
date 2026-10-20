# Remote Support

Remote Support is in the console under **Remote management › Remote Support**. Tagline: *Help anyone, anywhere — see the screen, take control and move files, always with the user's consent.* Every session is logged.

The page has four tabs (build 62):

| Tab | What it is for |
|---|---|
| **Quick support** (default) | On-demand help for anyone, in the office or over the internet, in the way hosted tools such as Zoho Assist work: create a session, send the link, the person runs a small app and approves |
| **Unattended devices** | Devices with the installed remote agent: connect at any time (consent settings apply) |
| **History** | Every session opened from Zentriq |
| **Settings** | Consent and privacy, terminal on/off, internet access, installing the agent, trusting the server, advanced |

Session buttons are the same everywhere: **Remote desktop** (or **Connect**) and **Files** (file transfer), plus a small menu with **Chat & device details** and **Terminal** (only when allowed in Settings).

## Quick support (on demand, anywhere)

1. Click **New support session**. Optionally enter the person's name, e-mail and the problem; choose how long the link is valid (1 hour to 3 days).
2. Zentriq creates a **device group for this session only** (*Quick support 482 913 077*) with consent always on (the person must allow screen, files and terminal; they are notified and see a bar with a disconnect button), and an **invitation link** (`https://<address>:8446/agentinvite?c=…`).
3. Send the link: Zentriq can e-mail it (when e-mail is set up in System Settings), or copy the link or the ready-made message into chat, or open it in your mail app.
4. The person opens the link and runs **Zentriq Remote Support**:
   - **Windows:** a run-only app — nothing is installed; it disconnects when they close it.
   - **macOS / Linux:** the page offers the agent for their system (a small package / install command).
5. The session shows **Connected** with the device name, OS and address within about 15 seconds. Click **Remote desktop** or **Files**.
6. Click **End** when done: the session's group is deleted and the app disconnects. If someone runs an old copy of the app later, the engine restores the group; Zentriq removes quick support groups that belong to no active session at its next check (within 2 minutes), so nobody can be reached through an ended session.

Sessions end by themselves when nobody joins before the link expires (*Expired*), when the app has been closed for 30 minutes, or after 12 hours. The Worker checks every 2 minutes; the page checks every 15 seconds while sessions are active. Ended sessions stay listed for 7 days and every remote desktop / files / terminal session is in History and the audit log (`remote.quick_create`, `remote.session`, `remote.quick_end`).

**Admin rights on the person's PC:** the run-only Windows app runs with the person's own rights. To answer UAC prompts, ask them to start it with *Run as administrator*, or use an **unattended** device (the installed agent runs as a service and shows the secure desktop, the sign-in screen and UAC prompts).

## Internet access (public address, checked)

Zentriq is installed on your premises, so people outside your network can only reach it through a **public address**:
your public IP or a domain name, published on HTTPS. Links for people outside are created **only** for an address that
Zentriq has checked:

1. In **Settings › Internet access** (or directly in *New support session* when none is set) enter the public domain
   name or public IP and the HTTPS port (default 8446), then **Check**.
2. Zentriq checks from the server that:
   - the name resolves, and **every address is public** (10.x, 172.16–31.x, 192.168.x, 100.64.x carrier NAT, link-local
     and loopback addresses are refused — "not reachable from the internet");
   - an **HTTPS** connection to that address and port reaches **this server's remote support** (same certificate).
     Plain HTTP and port 80 are not accepted.
3. If the connection fails although the address is public, the router may not support *NAT loopback* (connecting to its
   own public address from inside). Open the address from outside (for example a phone on mobile data); when it works,
   tick *I opened … from outside the network* and save. Private addresses can never be confirmed.
4. **Check and use this address** saves it; the ZentriqRemote service applies it within a minute (MeshCentral
   `agentAliasDNS` / `agentAliasPort`). Invitation links, install links and new agent installers then use it.

Router/firewall: forward TCP **8446** (or the public port you chose) to the server's port 8446 — only remote support is
published, not the console or the agent port. For no browser warning outside, use a certificate from a public CA that
includes the name (the engine uses the console certificate).

Without a checked address, *New support session › Anywhere (internet)* asks for one (administrators) or explains that it
is missing; **On the office network** creates a link with the server's LAN address. The API refuses internet links with
HTTP 409 (`internet-required`).

## Zentriq branding, Windows / macOS / Linux

* The invitation page people open is Zentriq's own page (`installer/remote/views/agentinvite.handlebars`, installed as a
  MeshCentral page override): Zentriq logo and wording, only **Windows** (x64 and ARM64), **macOS** and **Linux**
  (x64, ARM64) — no Android, Assistant, BSD or other platforms. It picks the visitor's system automatically.
  - Quick support (run only): Windows downloads the app (*Connect*, nothing installed); macOS and Linux get a one-line
    Terminal command that runs the agent with `-connect` until it is stopped.
  - Install links: Windows installer, macOS package, Linux install script.
* When the installer is built, `installer/remote/brand.js` replaces the MeshCentral product name with Zentriq in the
  pages technicians see (identifiers, file names and licence texts are kept), removes the translated page copies (so
  every page is the branded English one) and uses the Zentriq icon. The Windows agent is branded through the engine
  configuration (name, service, icon, file information, consent texts).

## Install for unattended access

Settings › *Install for unattended access* offers three ways: through the Zentriq Agent (select devices, **Install**), the Windows installer (`ZentriqRemoteSupport.exe -fullinstall`), and an **install link** valid for 3 days whose page offers Windows, macOS and Linux — useful for laptops outside the office. The installer bundles the Windows, macOS (universal, Apple silicon, Intel) and Linux (x64, ARM64) agents.

## Engine

Zentriq does not build its own remote-control stack. It bundles **MeshCentral 1.2.5** (open source, Apache-2.0, https://github.com/Ylianst/MeshCentral) and runs it on **Node.js 22** (MIT), with Zentriq branding. Both are downloaded and verified when the installer is built, so the server never downloads anything at run time:

- The Node.js zip is checked against the SHA-256 published by nodejs.org.
- node.exe must carry a valid OpenJS Foundation signature.
- Exact npm versions are pinned in `installer/remote/package.json`.

| Part | Where |
|---|---|
| Windows service | `ZentriqRemote` (`Zentriq.Worker.exe --remote-host`), account `NT SERVICE\ZentriqRemote`, restarts automatically |
| Program files | `<install>\remote\node`, `<install>\remote\app` |
| Data | `<data>\Remote\data` (config, database, certificates), `files`, `backups`; only SYSTEM, Administrators and the service can access it |
| Port | **8446/TCP**, HTTPS with the **same certificate as the console** (Setup copies it to the engine at install, repair and upgrade; if the console certificate's key cannot be exported, the engine uses its own). Setup adds the firewall rule. Devices connect outbound to it. |
| Logs | `<data>\Logs\remote-*.log` |

### Branding

- Sign-in and session pages are titled "Zentriq – Remote Support" and use the Zentriq logo and a dark theme.
- The device agent is named **Zentriq Remote Support**: service `ZentriqRemoteSupport`, file name, icon and company in the version info.
- Consent prompts and notifications say "Zentriq Remote Support".
- New accounts, password sign-in, two-factor setup, plug-ins, self-update and framing are switched off. Only Zentriq signs technicians in.

## How sign-in works

There is no second password:

1. The technician clicks **Remote desktop**, **Files** or **Terminal** in Zentriq.
2. The API checks the permission and makes sure the technician has a personal account in the engine (`zq-<user id>`).
3. The API grants that account rights on the device group (the Zentriq group, or the quick support session's group): remote desktop, files and chat; terminal only when *Allow technicians to open a terminal* is on; never Intel AMT. Full rights come with `remote.unattended`.
4. The API returns a sign-in link that is encrypted with a key known only to the server (AES-256-GCM, `AppSecrets.RemoteLoginKey`) and is **valid for 2 minutes**.
5. The browser opens the device straight away in a new tab.

Each session is recorded in the session history and the audit log, with user, device, view and source IP.

## Permissions

| Permission | Allows |
|---|---|
| `remote.start` | See Remote Support; create and end quick support sessions; open desktop, files or terminal on a device |
| `remote.manage` | Install or remove the remote agent, download the installer, create install links, settings (consent, terminal, internet access), match devices |
| `remote.unattended` | Full rights and the full remote console |

All three permissions are given to the Administrator role.

## Installing the remote agent on devices

- **Devices with the Zentriq Agent (1.1.1 build 51 or later):**
  1. Open Remote Support › Devices.
  2. Select the devices and click **Install**.
  3. The agent downloads the installer from the server over mTLS, checks its SHA-256 and runs `-fullinstall`.
  4. The result appears in *Last request* and in the Activity log.

  **Remove** works the same way.
- **Other Windows devices:** open Deployment & settings, click **Download ZentriqRemoteSupport.exe** and run `ZentriqRemoteSupport.exe -fullinstall` as administrator (or deploy it with GPO or Intune). Remove it with `-fulluninstall`.

Every 2 minutes the Worker syncs the engine's device list to Zentriq. Devices are matched to assets by host name; you can match one by hand if the names differ.

## Consent (Settings)

These settings apply to all unattended devices immediately (quick support sessions always ask). All are on by default:

- **Ask the user** to allow each desktop, terminal or files session.
- **Notify the user** when a session starts.
- **Privacy bar** on the device's screen, with a disconnect button.
- **Allow technicians to open a terminal** (command line). Remote desktop and file transfer are always available.

## Server address and certificate (nothing is tied to a fixed IP)

- **Technicians** are sent to port 8446 on the same address they used to open the console (for example `https://192.168.1.8:8446/` today, `https://10.0.5.20:8446/` after the server moves), unless `Remote.PublicHost` is set in `zentriq.json` (then run Repair).
- **Devices** (Zentriq Agent and remote agent) connect to the server by **name** (`Https.HostName`, by default the server's DNS name), so they keep finding it when its IP address changes. New enrollment codes from *Deploy agent* use the name, not the address of the browser.
- **Certificate:** Setup creates a **Zentriq Local CA** once (`Secrets\https-ca.pfx`, trusted on the server) and issues the console certificate from it for the server's names, `localhost` and all its IPv4 addresses. The API checks every 5 minutes and when the network changes; when the server has a new name or address it issues a new certificate with the **same key** (agents pin the key) and serves it at once. The remote support service switches to it within a minute.
- **Technician PCs trust the CA once** (Remote Support › Deployment & settings › *Trust this server*: download, or `Import-Certificate -FilePath .\Zentriq-Local-CA.cer -CertStoreLocation Cert:\LocalMachine\Root`, or Group Policy). After that the console and remote sessions open without warnings at any address the server gets.
- A customer certificate (thumbprint in Setup) is used as is and not re-issued; remote support uses it too when its key can be exported.

- **Browser origins:** the engine only accepts sessions from pages it knows (protection against other websites using a technician's session). It accepts every name and IPv4 address of the server, kept current by the ZentriqRemote service; a host name the server does not know (a DNS alias) is added the first time a technician uses it (that session waits up to a minute while the engine restarts).

To support devices outside the LAN, see *Internet access* above (and publish 8444 if Zentriq Agents should also report from outside).

## Antivirus

The installer ships only the MeshCentral files Zentriq uses: the Windows, macOS and Linux agents and their core scripts (`installer/remote/agents-keep.txt`). Android agents, MeshCmd, MeshCentral Router and Satellite are left out. The installer is not code-signed yet; if Microsoft Defender or another product still blocks it, note the threat name and file from *Protection history* (`Get-MpThreatDetection`) — sign the build or submit the file as a false positive.

## Troubleshooting

- **"Invalid origin in HTTP request" in the session tab:** builds 51–54 accepted sessions only from the engine's certificate name. Fixed in build 55.
- **The session tab shows a certificate error without a way to continue:** the browser has HSTS for the server name (it trusted the console under that name) but did not trust the remote support certificate. Builds before 52 used a separate certificate on port 8446; upgrading fixes it. From build 53, install the Zentriq Local CA once on the technician PC.

- **Status "Not running":** check service `ZentriqRemote` and `<data>\Logs\remote-*.log`. Running Repair from Setup reconfigures it.
- **"Install" not offered:** the Zentriq Agent on the device is older than this release. Update the agent first (Agents › Update).
- **A device is shown twice:** its host name differs from the asset. Use the matching option on the device row.

## Tests

- **Unit tests:** sign-in cookie format (matches MeshCentral), server configuration, rights and consent flags, technician rights (terminal on/off), quick support codes and invitation text, internet address parsing and how it is applied to the engine.
- **Live test (ci-quick, real MeshCentral):** the control channel, group, user and consent management, a signed-in session link (a forged link is refused), the agent installer and sync; a quick support session (own group, public run-only invitation page, Windows/Linux/macOS downloads, technician link, end removes the group and the link), the install link, invitation address with an internet name.
- **Windows acceptance tests:**
  - **T59:** service and health; branded sign-in page and installer; the remote agent connects; a session link opens the signed-in device view.
  - **T60:** removing and reinstalling the remote agent through the Zentriq Agent from the console.
  - **T65:** quick support end to end: create a session, the invitation page is run-only, the support app (`connect`) joins the session's group, the technician's desktop link opens, the session is in History; ending it removes the group and the link; install link page.
