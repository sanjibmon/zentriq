# Zentriq Agent for macOS

From build 1.1.1.145 Macs run the Zentriq Agent: a launchd daemon that sends hardware, application and security
inventory to Zentriq, keeps a live connection so the Mac shows online in the console, and updates itself from the
server. It uses the same agent port (8444, mutual TLS), enrollment codes, certificates and schedules as the Windows and
Linux agents (it is the Linux agent's program built for macOS).

## Supported systems

| | |
|---|---|
| macOS | 12 Monterey or later (tested on the current release on every build) |
| Processors | Apple silicon (`zentriq-agent-macos-arm64.pkg`) and Intel (`zentriq-agent-macos-x64.pkg`) |
| Runs as | root, `/Library/LaunchDaemons/com.zentriq.agent.plist` (KeepAlive: restarted if it stops) |

## Installing

1. *Agents › New enrollment code*. The code's panel shows a **macOS** command.
2. On the Mac, in Terminal (an administrator account; sudo asks for the password), run that command, for example:

   ```
   curl -fsSLk --pinnedpubkey 'sha256//<server key>' https://zentriq.example.com:8444/api/agent/v1/macos/install.sh \
     | sudo sh -s -- --server https://zentriq.example.com:8444 --code XXXXX-XXXXX-XXXXX-XXXXX --checkin 30 --inventory 180 --pin '<server key>'
   ```

   It downloads the package for the Mac from the server's agent port (trusting only your server's key), checks its
   SHA-256, installs it with `installer` and enrolls. The Mac appears under *Agents* within a minute.

**MDM (Jamf, Intune, Kandji) or your own tools:** deploy the `.pkg` from the release (or from the code's panel), then
run once `sudo /Library/Zentriq/Agent/zentriq-agent --enroll <code> --server https://zentriq.example.com:8444`.
The packages carry an ad-hoc signature; a package opened in Finder from a browser download is blocked by Gatekeeper
until Zentriq signs it with a Developer ID certificate (planned). The one-line installer and MDM are not affected.

Files: program `/Library/Zentriq/Agent` (command `zentriq-agent`, also `/usr/local/bin/zentriq-agent`), state and
certificate `/Library/Application Support/Zentriq/Agent` (root only), log `/Library/Logs/Zentriq Agent/agent.log`.

## What it collects

Computer name, model, serial number, hardware UUID, chip or processor, memory, the system volume, network adapters
(MAC, IPv4), macOS name, version and build, last restart, signed-in user, **every application with its version,
publisher (from its code signature) and whether it came from the App Store**, FileVault, the application firewall,
System Integrity Protection (shown as Secure Boot) and the members of the *admin* group. Nothing personal (no
documents, browsing or keystrokes).

## What it does not do yet

Patching, scripts, software deployment and uninstall, application control, software metering and device experience,
service control and tamper protection are for Windows and Linux. The Mac agent does not announce those capabilities,
so the console does not offer them for Macs. Remote support for Macs works as before (quick support and install
links).

## Commands (on the Mac, with sudo)

| Command | |
|---|---|
| `zentriq-agent --status` | enrollment, server, last check-in and inventory |
| `zentriq-agent --checkin-now` / `--collect-now` | contact the server now (and send inventory) |
| `zentriq-agent --collect <file>` | write the inventory as JSON (nothing is sent) |
| `zentriq-agent --diagnose` | daemon, enrollment, certificate and the connection to the server |
| `zentriq-agent --restart` / `--stop` / `--enable` | the launchd daemon |
| `zentriq-agent --uninstall [<code>]` | remove the agent (or `sudo /Library/Zentriq/Agent/uninstall.sh`) |

## Tests

Every build: the packages are built and signed on a Mac and the macOS test (M01–M10) installs the package, checks the
daemon, version, signature, inventory (serial, hardware UUID, applications with versions), status, enrollment
handling, the log, reinstall (upgrade path) and removal.
