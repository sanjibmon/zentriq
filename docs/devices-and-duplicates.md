# Devices without an agent, device types and duplicates

This page covers the **IT Asset Inventory** page (formerly *Asset Inventory*).

## Navigation (build 56)

The sidebar is ordered: Overview · Discover & inventory · Remote management · Infrastructure · Secure & remediate (now including *Software Licenses* and *Web Apps & Accounts*) · Insight & admin.

## Device types

Every device has a **device type**. A type is detected automatically or chosen by an administrator. The types are:

| Group | Types |
|---|---|
| Computers | Desktop, Laptop, Tablet, Mobile device, Physical server, Virtual machine, Hypervisor host, Storage / NAS |
| Network | Network switch, Router, Firewall, Wireless access point, Wireless controller, Load balancer, Network device |
| Printing | Printer, Multifunction printer, Scanner |
| Other devices | UPS / power, IP camera / NVR, IP phone, IoT device, Other |

The type also sets the device's category (endpoint, server, network device, printer, IoT), which is used for licensing and dashboards.

**Where you see and change it:**

- **IT Asset Inventory:** the *Type* column, and the *All device types* filter.
- **Device page › Overview › Device type:**
  - Choosing a type sets it manually. Discovery and inventory never overwrite a manual choice.
  - Choosing *Automatic (detected: …)* goes back to detection.
- **Reports and the dashboard:** the "Device type" column and chart use the same value.

## How devices without an agent are recognised

A discovery scan probes the usual ports. It also always probes ports that identify devices that cannot run an agent: 9100, 515 and 631 (printing), 23, 8080, 8443, 4443 and 10443 (web management), 554 (cameras), 5060 (IP phones), 8291 (MikroTik), 902 (VMware hosts) and 3260 (iSCSI storage).

Evidence is used in this order, strongest first:

1. **SNMP:** the scan's SNMP credentials, plus the read-only factory community `public`. Using `public` is the scan option *Identify printers, switches, routers, firewalls…*, which is on by default. It is not tried on Windows computers. SNMP collects:
   - **Every device:** name, manufacturer (vendor and ENTITY-MIB), model, serial number, firmware, location and contact, uptime, interfaces (MAC address, speed, link up/down, description) and IP addresses.
   - **Printers and multifunction devices (Printer-MIB):** page count, status (idle, printing and so on), and each supply with its level in %. The device page shows supplies as bars, with low levels in red.
   - **Switches, routers and firewalls:** physical ports, how many are connected, whether the device switches (bridge table) and whether it routes (IP forwarding). It is classified as a switch, router or firewall from these and from its vendor.
2. **The web interface:** the page title, *Server* header and certificate names of the device's management page. Zentriq reads these without signing in. About 80 product families are recognised: HP, Canon, Kyocera, Xerox, Ricoh, Epson and Brother printers; Fujitsu scanners; FortiGate, Palo Alto, SonicWall, Sophos, pfSense/OPNsense, WatchGuard, Check Point and Cisco ASA firewalls; MikroTik, Ubiquiti and DrayTek routers; Cisco, HPE Aruba, Netgear and D-Link switches; UniFi, Aruba, Ruckus and Omada access points; Synology, QNAP and TrueNAS storage; Hikvision, Dahua and Axis cameras; Yealink, Poly and Grandstream phones; APC and Eaton UPS units; ESXi and Proxmox hosts; F5 and NetScaler load balancers. When the title contains a model (for example "FortiGate-60F"), it becomes the device's model.
3. **Open ports:** a weak first guess. For example, 9100 means a printer and 554 means a camera.

The scan result shows what was recognised and why, for example: *Recognised as Firewall (Fortinet FortiGate-60F): web interface mentions "fortigate"*. The device page shows the same evidence under *Recognised by*.

A computer with collected inventory (agent or credentialed scan) keeps its type. A web page on it never re-types it.

## Adding and deleting devices

- **Add device** (IT Asset Inventory) takes:
  - Host name and/or IP address (one is required)
  - Device type (required)
  - MAC address, manufacturer, model, serial number, OS or firmware, department, owner and criticality

  When a scan or an agent finds the device later, its details are added to this record. It is matched by name, address or MAC address; a matching serial number is merged by the duplicate clean-up.
- **Delete:** select devices and click **Delete (n)**, or use **Delete** on the device page. This permanently removes the device, its inventory, change history and findings, and any records merged into it. A device that is still on the network comes back as a new record at the next scan. To keep the history instead, retire the device.

## Duplicates

Two active records are the same device when they share a **host name**, a **serial number** or a **network adapter (MAC)**. Some matches are not treated as duplicates:

- **Generic serial numbers**, such as "To be filled by O.E.M.".
- **Locally administered or multicast MAC addresses**, such as VPN or Npcap adapters and randomised Wi-Fi addresses, because these repeat across devices.
- **Two records with different serial numbers.**
- **Two records whose agents are both checking in.** These are two machines with the same name.

**Which record is kept:** the newest information wins. The kept record takes the most recent IP address, name and last-seen time. It also fills in a serial number, MAC address, department, owner, model or manual device type from the others if it lacks them. When an older record holds the device's collected inventory, that record is the one kept, so hardware and software history is not lost. Merged records become *duplicate*: they are hidden and point to the kept record. The kept record also takes over their identifiers, agent registration, remote support link, actions and change history.

**Automatic:** duplicates are merged every 30 minutes, which is on by default. Each merge is in the audit log as `asset.merge` by `system:duplicates`. To review first, turn it off in **IT Asset Inventory › Duplicates** (requires *settings.manage*).

**Manual:** **IT Asset Inventory › Duplicates (n)** lists each group with the reason (same name, same serial, same adapter) and which record is kept. You can **Merge these** for one group or **Merge all**.

## Manufacturer, model and serial number entered manually (build 78)

Some devices do not report these values. White-box PCs and some boards report placeholders such as "To be filled by O.E.M.", "Default string", "System Serial Number" or "0000000". Zentriq treats placeholders as **not detected**.

- **Detected values always win.** A value the device reports is shown as it is and cannot be overwritten.
- **Missing values can be typed in.** Open the device. On **Overview**, a missing Manufacturer, Model or Serial number shows **Not detected · Add manually**, for users with *assets.manage*. A typed value is marked **Entered manually**, and **Edit** changes it. **Remove entered values** clears all three.
- **Where the entered values appear:** the device page, the IT Asset Inventory search, Hardware Inventory (marked "✎ manual", searchable, and in the manufacturer filter) and Reports. Reports have a column for whether the details were entered manually.
- **Remembered by the machine, not the record.** What you enter is also saved as a hardware profile, keyed by the device's fixed identities: the **SMBIOS UUID** from the motherboard and the **MAC addresses** of its network adapters. If the agent or Windows is reinstalled, or the device is deleted and found again, the next inventory recognises the identities and fills the values in again automatically.
  - **Safeguards:** the profile is not applied when the device reports a *different* SMBIOS UUID, for example a USB network adapter moved to another machine. Placeholder or shared UUIDs (cloned VMs) are not used as an identity.
  - **Merges:** a duplicate merge keeps the entered values.
  - **Audit:** each change is in the audit log as `asset.hardware_identity`.

## API

| Method | Path | |
|---|---|---|
| GET | `/api/v1/assets?deviceType=firewall` | filter by type (`unknown` = not detected) |
| GET | `/api/v1/assets/device-types` | the list above with categories |
| POST | `/api/v1/assets` | add (`deviceType`, `macAddress`, `manufacturer`, `model`, `departmentId` …) |
| PUT | `/api/v1/assets/{id}/hardware-identity` | `{ manufacturer, model, serialNumber }`; `null` clears; returns the number of identities linked |
| PUT | `/api/v1/assets/{id}/device-type` | `{ "deviceType": "mfp" }`, or `null` for automatic |
| DELETE | `/api/v1/assets/{id}` · POST `/api/v1/assets/delete` `{ids}` | permanent deletion |
| GET | `/api/v1/assets/duplicates` | groups, reasons, automatic setting |
| POST | `/api/v1/assets/duplicates/merge` | `{ keepIds: [...] }` or `{}` for all |
| PUT | `/api/v1/assets/duplicates/settings` | `{ auto: true \| false }` |

## Tests

- **Unit tests:**
  - **Recognition from text:** 20 product families from web interface text.
  - **Ports and evidence:** port guesses, and that web evidence outranks ports, plus model from the page title.
  - **SNMP:** switch, router, firewall and printer, with ports, supplies, page count and model.
  - **Device types:** type to category mapping.
  - **Duplicates:** which record is kept.
- **PostgreSQL tests:**
  - **Duplicates:** groups by name, serial and MAC. Locally administered MACs and same-name/different-serial pairs are not grouped. A merge keeps the newest address, and deleting the kept device removes the merged records too.
  - **Device type:** a manual device type survives inventory.
- **Windows acceptance test T62:**
  - A printer web page on port 8080 of the test server is recognised by a scan, and the inventoried computer keeps its own type.
  - Add device (multifunction printer, MAC).
  - A duplicate is found and merged, keeping the newest address.
  - Device type set manually, then back to automatic.
  - The automatic setting is turned off and on.
  - Delete (2 records).
- **Manual hardware details (build 78):**
  - **Unit test:** placeholders are not values.
  - **PostgreSQL test:** entered values come back on a new record with the same SMBIOS UUID/MAC; not applied when only the MAC matches but the UUID differs; clearing removes the profile.
  - **Windows acceptance test T70:** save, identities linked, detected values keep priority, search by the entered serial, 201 characters refused, removed.
