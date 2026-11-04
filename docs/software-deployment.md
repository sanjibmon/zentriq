# Software Deployment

Software Deployment keeps the software on your devices to what the organisation allows, and installs software on
devices from the console:

* **Approved software** — the organisation's list, built from the **standard software catalogue** that ships with
  Zentriq, your own entries, and what is installed on your devices today.
* **Application control** — on every device with the Zentriq Agent (Windows and Linux), an installation of software
  that is not approved is reported (audit) or stopped with your message (block): *"This is unauthorized software.
  Please contact your administrator for further installation."* The user can **request approval** from the message.
* **Device groups** — devices by department, location or name, with their own approved software, their own mode and,
  on Windows, **WDAC** (Windows Defender Application Control).
* **Push deployment** — upload a package (.msi, .exe, .deb, .rpm) and install or uninstall it on devices, groups,
  departments or all devices, with the status of every device.

Because the Zentriq Agent is tamper-protected, a local administrator cannot stop or remove it — and so cannot turn
application control off — without a code from the console. The **remote support agent** is protected the same way.

Console: *Secure & remediate › Software Deployment*. Permissions: **deploy.view** (see everything), **deploy.manage**
(change the approved list, settings, groups and packages; decide requests), **deploy.run** (start deployments).
Licensed with the *Secure* section.

## Rolling it out

1. **Build the approved list.** *Software catalogue*: select what your organisation allows and *Approve* (for all
   devices or a device group). *Found on devices*: everything installed on your devices that is not approved yet —
   select and approve what you use. Add your own line-of-business software with *Your own software*.
2. **Audit mode** (*Overview › Mode: Audit*). Installations of software that is not approved continue and are listed
   under *Events*. Approve what you see (*Events › Approve…*). Nothing changes for users.
3. **Block mode** — first for a device group (*Device groups › Application control: Block*), then for everyone. Users
   see the message and can request approval; requests appear under *Requests* (and as the integration event
   *appcontrol.request*).

Changes reach online agents within seconds (live connection); devices that are offline get them when they connect.
Every agent keeps its policy, so it applies offline too.

## What is approved

A rule approves (or blocks) software by any combination of:

| | |
|---|---|
| Name | The name it installs under, or its installer's product name; wildcards `*` and `?`, several separated by `;` |
| Publisher | The installer's company name or the name in its **valid** code-signing certificate |
| Version range | From / up to version |
| SHA-256 | Exactly this installer file |
| Platform | Windows, Linux (package names) or both |
| Applies to | All devices, a device group, or one device; optionally until a date |

Block rules win over approvals. A catalogue entry becomes one rule per platform (e.g. Google Chrome: *Google
Chrome\*; Chrome\** from *Google LLC* on Windows; *google-chrome-stable* on Linux). Approving a request or an event
offers: this software from this publisher, this software, only this version, everything from the publisher, or only
this exact file — for all devices, a group or only that device.

**Always allowed** (built in): Windows components and updates, drivers, Microsoft runtimes (Visual C++, .NET, WebView2),
the Zentriq Agent and the remote support agent; on Linux also updates of installed packages, kernels, firmware and
libraries. *Software catalogue* and *Approved software › Test software…* show what a given name and publisher would get.

## The standard catalogue

About 200 well-known applications in 20 categories (browsers, office, PDF, communication, developer tools, runtimes,
databases, remote access, security, cloud tools, management agents, drivers and vendor tools, media, file sharing,
games…), each with the names and publishers it installs under on Windows, its Debian and RPM package names, its
website and its silent installation switches. It is updated with every Zentriq release; your own entries are kept.

## On Windows devices

The agent watches for installers started by signed-in users: Windows Installer packages (`msiexec /i`, double-clicked
.msi files), setup programs (by name and description, and by the installer framework found in the file: NSIS, Inno
Setup, WiX bundles, InstallShield, Advanced Installer, Squirrel and others) and per-user installers in user profiles.
A new installer is **held** while it is checked (product name, publisher, code-signing certificate, version, SHA-256):

* approved → it continues;
* audit mode → it continues and is reported;
* block mode → it and what it started are stopped, it is reported, and the user sees the message with *Request
  approval* (and a box for the reason). The request goes to the console through the agent.

Not checked: installations started by services (Windows Update, Configuration Manager, Intune, Zentriq deployments),
uninstallers, programs an allowed installer starts, and Windows components. Software that is installed in another way
(copied, portable, Store) is caught by the **inventory check**: at each inventory the server compares the software
installed since application control was turned on with the approved list, reports what is not approved (*Events ›
Found installed*) and — in block mode with *Remove software that is not approved…* on — uninstalls it through the
agent (*Removed*; integration event *appcontrol.blocked*). Software installed before application control was turned
on is never removed: approve it or remove it with a deployment.

On the device: `ZentriqAgent.exe --app-control` shows the mode, the policy and WDAC; `--app-control --test "<name>"`
what a name would get.

### WDAC (opt-in per device group)

For a group, *WDAC: Audit* or *Enforce* adds a Zentriq **Windows Defender Application Control** base policy: Windows'
own DefaultWindows policy (Windows, Microsoft-signed and Store components) plus the folders only administrators can
write to (Program Files, the Zentriq Agent). Windows itself then reports (audit: Event Viewer › Applications and
Services › Microsoft › Windows › CodeIntegrity) or refuses (enforce) programs run from anywhere else — downloads, user
profiles, temporary folders, USB drives — which also covers portable programs. Start in audit for a while. It needs
the ConfigCI module (Windows 10 / 11 Enterprise and Education, Windows Server); other editions report *Not
available* and keep the agent's checks. Windows 11 22H2 and later apply it at once (CiTool); earlier versions at the
next restart. *Off* removes the policy. A device in several groups gets the strictest setting.

## On Linux devices

The agent adds hooks to the package managers, so every package is checked **before** it is installed:

| Package manager | Distributions | |
|---|---|---|
| dpkg (and apt, which installs through dpkg) | Ubuntu, Debian, Linux Mint and relatives | Blocks |
| dnf 4 | RHEL 8 / 9 / 10, Rocky, AlmaLinux, Oracle Linux, CentOS Stream, Amazon Linux 2023 | Blocks |
| zypper | SUSE Linux Enterprise, openSUSE | Reported at once (libzypp does not let a plugin stop an installation); removed by the inventory check when automatic removal is on |
| dnf 5 (Fedora 41+), snap, flatpak, pip, `rpm -i`, copied files | | Reported by the inventory check |

A package that is not approved stops the installation with the message, for example:

```
Zentriq: This is unauthorized software. Please contact your administrator for further installation.
  Not approved: zip 3.0-13
  To ask for approval: zentriq-agent --request zip --reason "why you need it"
```

Any user can ask for approval with `zentriq-agent --request <package> [--version <v>] [--reason <text>]`.
`sudo zentriq-agent --app-control` shows the mode and the hooks. The hooks are removed with the agent.

## Device groups

A group is the devices of chosen departments, locations and individual devices, optionally only Windows or only Linux
(membership follows the devices: a laptop moved to another department leaves the group). A group can have its own
approved software, its own mode (*Off*, *Audit*, *Block* — or the organisation's) and WDAC. *Devices* lists the members.

## Packages and deployments

*Packages › Upload package*: the installer is stored on the Zentriq server (data folder `Deployment`). For setup
programs, enter their silent switches (NSIS `/S`, Inno Setup `/VERYSILENT /NORESTART`, InstallShield `/s`, WiX bundles
`/quiet`; the catalogue lists them for well-known software). The package is added to the approved software unless you
choose otherwise.

*Deploy…*: install or uninstall on chosen devices, device groups, departments or all devices of the platform. Each
agent downloads the package from the server over its own mutually authenticated connection, checks the SHA-256 and
runs it silently as SYSTEM / root (Windows Installer: `msiexec /i … /qn`; Linux: apt, dnf, zypper or yum, which also
install dependencies). Online devices start within seconds, the others when they connect (up to 7 days). *Deployments*
shows every device (waiting, received, done, failed with the installer's message, skipped: no agent, other platform or
an older agent); *Retry failed* and *Cancel waiting*. Failed deployments raise the integration event
*deployment.failed*; every deployment is in the audit log and the Activity log.

## Remote support agent tamper protection

While tamper protection is on (*Agents › Settings*) and *Protect the remote support agent* is on (*Software
Deployment › Overview*, on by default), the remote support agent is protected like the Zentriq Agent:

* **Windows** — administrators cannot stop, disable, reconfigure or delete the ZentriqRemoteSupport service, change its
  registry key or program files (its uninstaller fails); every two minutes the agent restores the startup type, starts
  it when it was stopped and undoes permission changes.
* **Linux** — `systemctl stop` of its unit is refused and systemd restarts it when it ends; every five minutes the
  agent enables and starts it again if it was disabled, masked or stopped.
* If it is removed anyway, the agent reports it as tampering and the server **installs it again** at the next check-in.

Installing and removing it from the console (*Remote Support*) keeps working: the agent lifts the protection first. A
pause of tamper protection with a code lifts it too. *Software Deployment › Devices* shows the state on every device.

## Tests

Unit and database tests (the decision, the catalogue, groups and policy, the inventory check), Linux containers (dpkg
with apt and with `dpkg -i`, dnf on Rocky Linux and Amazon Linux: approved package installs, unapproved refused
with the message, event recorded, request by a normal user, audit mode, hooks removed with the agent; zypper on openSUSE: reported) and the Windows
acceptance tests T82–T85 (catalogue and inventory approval, audit → block for a device group with the message and a
request from the desktop, approval by SHA-256, push installation with status, retry and uninstall, WDAC audit, remote
support agent protection). Results: [test report](test-report.md).
