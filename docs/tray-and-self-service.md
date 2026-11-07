# Tray icon, Software Center and Get Remote Help

From build 1.1.1.118 the Zentriq Agent on Windows shows an icon next to the clock for every signed-in user. Its
menu: **Software Center**, **Get Remote Help**, **Device Information** and **Check In Now**.

## How it works

* The agent service (SYSTEM) starts `ZentriqAgent.exe --tray` on the desktop of each signed-in user and keeps it
  running; it ends the trays when the service stops (so updates and uninstalls can replace the program).
* The tray runs with the user's rights and talks only to the agent service, over the local pipe `ZentriqAgentTray`
  (authenticated users may connect). The service identifies the user by the Windows session of the caller and asks
  the server on the user's behalf over the agent's mutually authenticated connection. The tray never talks to the
  server and holds no secret.
* Settings (*Software Deployment › Self-Service › Tray Icon on Windows Devices*, permission *settings.manage*): show
  the tray icon, Software Center, Get Remote Help, the company or IT team name, other ways to reach IT. Devices pick
  up changes within 10 minutes.

## Software Center (self-service)

* IT chooses which **packages** of Software Deployment are offered (*Self-Service* tab › *Offer*): category,
  description, *IT approves each request first*, and optionally only for some **device groups** (otherwise every
  device of the package's platform).
* Users open the Software Center from the tray, search or filter by category and click **Install** (or **Request**
  with a reason when IT approves first). Each application shows its state on this computer: available, installing,
  waiting for approval, installed, failed (with the reason), not approved.
* **No administrator rights are needed**: the installation is a normal deployment of the package to this one device,
  run by the agent as SYSTEM — also where Group Policy blocks installations by users (no local administrator, UAC,
  *Prohibit User Installs*). What still applies: Windows Installer turned off completely (*DisableMSI = 2*), WDAC
  or AppLocker rules that do not allow the installer.
* Requests appear in *Self-Service › Requests Waiting for Approval*; **Approve** (permission *deploy.run*) installs at
  once, **Deny** shows "Not approved" in the Software Center. The user gets a notification when an installation ends.
* Every installation is listed in *Deployments* and in the *Activity Log*.

## Get Remote Help

* The user describes the problem (and optionally a phone number or extension) and sends it; the request gets a
  number and appears in **Remote Support › Help Requests** with the user and the device.
* Technicians **Take** a request, **Connect** (the remote desktop of that device; the user is asked for consent as
  usual) and **Close** it. The user sees the state of their requests in the same window.

## Device Information

Computer name, signed-in user, IP address, operating system, agent version, server, last check-in and last
inventory, with **Copy** — for calls to the service desk. **Check In Now** contacts the server at once.

`ZentriqAgent.exe --tray-window store|help|info` opens one of the windows on its own (support, tests).

## Audit

`selfservice.install`, `selfservice.request`, `selfservice.refused` (by the device, with the user), `selfservice.offer`,
`selfservice.approve`, `selfservice.deny`, `tray.settings`, `help.request`, `help.status`.

## API

Console: `GET /api/v1/deployment/self-service`, `PUT /deployment/self-service/packages/{id}`,
`POST /deployment/self-service/requests/{id}`, `PUT /deployment/self-service/settings`, `GET /remote/help-requests`,
`POST /remote/help-requests/{id}`. Agent port: `GET /api/agent/v1/tray/config`, `GET /tray/catalog`,
`POST /tray/install`, `POST /tray/help`, `GET /tray/help`.

## Limits

Windows only (Linux devices have no tray icon). The Software Center offers uploaded packages of Software Deployment.

## Tests

PostgreSQL tests (offered packages per platform and device group, install at once without a second job, request and
approval, missing package file, help request numbers, settings) and the Windows acceptance test T94 (the tray on the
desktop of the signed-in user, installation from the Software Center, a request approved in the console, a help
request closed by a technician, device information, screenshots of the windows).
