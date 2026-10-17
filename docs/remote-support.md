# Remote Support

Remote Support is in the console under **Remote management › Remote Support**. It is no longer listed under *Secure & remediate*. Technicians can open a remote desktop, a terminal or a file browser on a device, with the user's consent. Every session is logged.

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

1. The technician clicks **Desktop**, **Terminal** or **Files** in Zentriq.
2. The API checks the permission and makes sure the technician has a personal account in the engine (`zq-<user id>`).
3. The API grants that account rights on the device group. "Remote control" is the default; full rights come with `remote.unattended`.
4. The API returns a sign-in link that is encrypted with a key known only to the server (AES-256-GCM, `AppSecrets.RemoteLoginKey`) and is **valid for 2 minutes**.
5. The browser opens the device straight away in a new tab.

Each session is recorded in the session history and the audit log, with user, device, view and source IP.

## Permissions

| Permission | Allows |
|---|---|
| `remote.start` | See Remote Support; open desktop, terminal or files on a device |
| `remote.manage` | Install or remove the remote agent, download the installer, consent settings, match devices |
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

## Consent (Deployment & settings)

These settings apply to all devices immediately. All are on by default:

- **Ask the user** to allow each desktop, terminal or files session.
- **Notify the user** when a session starts.
- **Privacy bar** on the device's screen, with a disconnect button.

## Remote access from outside the network

Devices connect to `https://<server host>:8446/`. Technicians are sent to port 8446 on the same address they used to open the console (for example `https://192.168.1.8:8446/`), unless `Remote.PublicHost` is set in `zentriq.json` (then run Repair). To support devices outside the LAN, publish port 8446.

The console's self-signed certificate covers the server's names and its IPv4 addresses. Once a technician's PC trusts it (import it into *Trusted Root Certification Authorities*, or replace it with a CA-issued certificate), both the console and remote sessions open without warnings.

## Troubleshooting

- **The session tab shows a certificate error without a way to continue:** the browser has HSTS for the server name (it trusted the console under that name) but did not trust the remote support certificate. Builds before 52 used a separate certificate on port 8446; upgrading fixes it. With build 52 or later, trust the console certificate on the technician PC or open the console by the address the certificate covers.

- **Status "Not running":** check service `ZentriqRemote` and `<data>\Logs\remote-*.log`. Running Repair from Setup reconfigures it.
- **"Install" not offered:** the Zentriq Agent on the device is older than this release. Update the agent first (Agents › Update).
- **A device is shown twice:** its host name differs from the asset. Use the matching option on the device row.

## Tests

- **Unit tests:** sign-in cookie format (matches MeshCentral), server configuration, rights and consent flags.
- **Live test (ci-quick, real MeshCentral):** the control channel, group, user and consent management, a signed-in session link (a forged link is refused), the agent installer and sync.
- **Windows acceptance tests:**
  - **T59:** service and health; branded sign-in page and installer; the remote agent connects; a session link opens the signed-in device view.
  - **T60:** removing and reinstalling the remote agent through the Zentriq Agent from the console.
