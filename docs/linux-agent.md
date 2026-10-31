# Zentriq Agent for Linux

The Zentriq Agent for Linux does on Linux servers and desktops what the Windows agent does: it sends hardware,
software and security inventory to Zentriq on a schedule, keeps a live connection so the device shows online, and
carries out the actions an administrator requests in the console. It uses the same agent port, enrollment codes,
certificates and settings as the Windows agent.

## Supported systems

| | |
|---|---|
| Distributions | Ubuntu 20.04 and later, Debian 11 and later, Red Hat Enterprise Linux 8 / 9 / 10, Rocky Linux, AlmaLinux, Oracle Linux, CentOS Stream, Fedora, SUSE Linux Enterprise Server / Desktop 15, openSUSE Leap / Tumbleweed, Amazon Linux 2023, Linux Mint, Pop!_OS and other distributions with glibc 2.27 or later and systemd |
| Processors | x64 (x86_64) and ARM64 (aarch64) |
| Packages | `.deb` (Debian family), `.rpm` (Red Hat, Fedora and SUSE families), `.tar.gz` (any other distribution) |
| Not supported | musl-based distributions (Alpine), 32-bit systems, systems without systemd (the agent then runs only from the command line) |

The agent is self-contained: no .NET runtime or other software has to be installed. It runs as the `zentriq-agent`
systemd service, as root (it reads hardware and security settings and, on request, controls services and removes
packages). It only connects out to the Zentriq server's agent port.

Tested on every release in containers: Ubuntu 24.04 and 22.04, Debian 12 (deb and tar.gz), Rocky Linux 9,
AlmaLinux 9, Oracle Linux 9, Red Hat UBI 9, Fedora 41, openSUSE Leap 15.6 and Amazon Linux 2023; and end to end
against an installed Zentriq server (Ubuntu 24.04).

## Installing

1. *Agents › New enrollment code* (as for Windows). The code's panel shows a **Linux** command.
2. On the Linux device, run that command as root (or with `sudo`), for example:

   ```
   curl -fsSLk --pinnedpubkey 'sha256//<server key>' https://zentriq.example.com:8444/api/agent/v1/linux/install.sh \
     | sudo sh -s -- --server https://zentriq.example.com:8444 --code XXXXX-XXXXX-XXXXX-XXXXX --checkin 30 --inventory 180 --pin '<server key>'
   ```

   It downloads the installer and the package for the device (deb, rpm or tar.gz; x64 or ARM64) from the server's
   agent port, accepting only your server's key (the same key pinning the agent uses), checks the package's SHA-256,
   installs it, starts the service and enrolls. The device appears under *Agents* within a minute.

**With your own tools** (Ansible, Puppet, Salt, cloud-init, a golden image): download the packages from the
enrollment code's panel (or the release), install them with the package manager, then enroll once:

```
sudo apt install ./zentriq-agent-linux-x64.deb          # Ubuntu, Debian
sudo dnf install ./zentriq-agent-linux-x64.rpm           # RHEL, Rocky, AlmaLinux, Oracle, Fedora, Amazon Linux
sudo zypper install --allow-unsigned-rpm ./zentriq-agent-linux-x64.rpm   # SUSE
sudo zentriq-agent --enroll XXXXX-XXXXX-XXXXX-XXXXX --server https://zentriq.example.com:8444 [--checkin 30] [--inventory 180] [--proxy http://proxy:3128]
```

Or set `ZENTRIQ_ENROLLMENT_CODE` and `ZENTRIQ_SERVER` in the environment of the package installation and it enrolls
by itself. Other distributions: unpack `zentriq-agent-linux-x64.tar.gz` and run `sudo ./zentriq-agent/install.sh
--enroll <code> --server <address>`.

Files: program `/opt/zentriq-agent` (command `zentriq-agent`), state and certificate `/var/lib/zentriq-agent`
(root only), log `/var/log/zentriq-agent/agent.log` and the journal (`journalctl -u zentriq-agent`).

## What it identifies

The console shows on the device's *Overview*:

| | How it is decided |
|---|---|
| **Distribution** and its **family** | `/etc/os-release` (ID, ID_LIKE): ubuntu, debian, rhel, rocky, almalinux, ol, centos, fedora, sles, sled, opensuse-leap, amzn, … grouped into the debian, rhel, fedora, suse, arch and other families |
| **Version** | Name, version, codename; *LTS* for long-term support releases |
| **Edition** | E.g. *Ubuntu Server*, *Ubuntu Desktop*, *Red Hat Enterprise Linux Server*, *Fedora Workstation*, *SUSE Linux Enterprise Server*, *SUSE Linux Enterprise Desktop* |
| **Server or desktop** | In this order: what the distribution states (SLES / SLED, Fedora and RHEL variants, Ubuntu Core), server meta-packages (ubuntu-server, fedora-release-server), then whether a desktop is installed **and used** — a display manager, start into the graphical target, desktop sessions, an open graphical session — together with the chassis. A server distribution with a desktop on a server is *Server with GUI*; a laptop or desktop PC with a desktop is a desktop. A system without a desktop is a server. |
| **Desktop environment** | GNOME, KDE Plasma, Xfce, Cinnamon, MATE, LXQt, Budgie, … or none |
| **Kernel**, **package manager** | `uname -r`; dpkg or rpm |

On the device, `sudo zentriq-agent --identify` shows the same, and `zentriq-agent --collect inventory.json` writes
the full inventory without sending it.

The rest of the inventory is the same as for Windows devices: manufacturer, model, serial number, SMBIOS UUID and
chassis (from DMI), BIOS / UEFI, processor, memory, disks and volumes with encryption (LUKS), network interfaces,
installed packages (name, version, architecture, install date), services (systemd units), logged-on user,
administrators (root and members of sudo / wheel / admin), security products, firewall, Secure Boot, TPM, and the
Linux security settings below.

## Security settings and checks

Security Configuration assesses Linux devices with their own checks (the Windows checks do not apply to them):

| ID | Check | Applies to |
|---|---|---|
| SL-01 | SSH: root cannot sign in (PermitRootLogin no) | All |
| SL-02 | SSH: password sign-in turned off | All |
| SL-03 | Host firewall active (ufw, firewalld, nftables or iptables rules) | All |
| SL-04 | SELinux enforcing or AppArmor on | All |
| SL-05 | Automatic security updates (unattended-upgrades, dnf-automatic, SUSE online update) | All |
| SL-06 | No pending security updates (adjustable) | All |
| SL-07 | No reboot pending after updates | All |
| SL-08 | Only root has UID 0 | All |
| SL-09 | Password maximum age set (off by default) | All |
| SL-10 | No Telnet server | All |
| SL-11 | Time synchronised | All |
| SL-12 | Audit daemon running | Servers |
| SL-13 | Disk encrypted (LUKS) | Desktops |

The same settings are collected over SSH by agentless scans (with sudo for the root-only ones).

## Console actions

| Action | On Linux |
|---|---|
| Collect inventory now, schedule (check-in and inventory intervals) | As on Windows |
| Start / stop / restart a service | `systemctl` on the unit; the agent, SSH and core system services cannot be stopped from the console |
| Uninstall software | Removes the package with apt, dnf, zypper or yum; refused when other packages depend on it, and for packages the system or the agent needs |
| Update the agent (*Agents › Update* or automatic updates) | The agent downloads the package for its platform and format from the server, checks the SHA-256 and installs it; enrollment and settings are kept |
| Remote support (install / remove) | Installs the remote support agent for Linux (x64, ARM64) from your server |
| Pause / resume tamper protection, restart, disable | As on Windows |

## Tamper protection

While tamper protection is on (*Agents › Settings*):

* **Removing the package** (`apt remove`, `dnf remove`, `zypper remove`, `rpm -e`, the tar.gz uninstaller) is refused
  unless a code is given: `sudo zentriq-agent --uninstall <code>` with the code from *Agents › Agent code* (all
  devices, one hour) or the device's own code.
* **`systemctl stop` / `restart zentriq-agent`** is refused (systemd `RefuseManualStop`). `sudo zentriq-agent --stop
  <code>` stops it until the device restarts; `--disable <code>` keeps it off until `sudo zentriq-agent --enable`;
  `sudo zentriq-agent --restart` restarts it (no code needed).
* A **watchdog timer** (every 5 minutes) enables and starts the service again when it was disabled, masked or stopped
  without a code.
* Wrong codes (5 in 15 minutes lock it) and every undone change are reported to the console as tampering.

Root can always defeat protection on its own machine (for example by deleting the files); Zentriq reports what it sees.

## Commands on the device

| Command (as root) | |
|---|---|
| `zentriq-agent --status` | Enrollment, server, last check-in and inventory, protection |
| `zentriq-agent --identify` | What the agent detects: distribution, edition, server or desktop |
| `zentriq-agent --checkin-now` / `--collect-now` | Contact the server now (and send inventory) |
| `zentriq-agent --diagnose` | Service, enrollment, certificate and connection to the server |
| `zentriq-agent --collect <file>` | Write the inventory as JSON (nothing is sent) |
| `zentriq-agent --uninstall <code>` | Remove the agent |
| `zentriq-agent --stop <code>`, `--disable <code>`, `--enable`, `--restart`, `--protection` | Service control |
