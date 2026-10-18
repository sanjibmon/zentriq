# Network Inventory

**Discover & inventory → Network Inventory** lists the network equipment that discovery found. This covers switches (managed and unmanaged), routers, firewalls, wireless access points and controllers, and load balancers. For each one you see:

- what it is;
- how it can be managed;
- its ports and what is connected to each port;
- how it links to other devices;
- its VLANs, PoE, modules and firmware.

## How network devices are found

1. **Discovery scan.** Run a scan of your network ranges (Discovery & Scans → New scan).
   - With an **SNMP credential** (SNMPv3 authPriv recommended, or v2c), Zentriq reads the device's full network inventory.
   - With **Identify printers, switches, routers…** on, the read-only community `public` is tried, and the device's web page title is read.
2. **Follow network neighbours** (on by default). After the targets are scanned, Zentriq also scans the switches, routers and access points that the devices found report over LLDP/CDP.
   - This only reaches management addresses inside the approved scopes that are not excluded.
   - It goes up to 2 hops and adds at most 256 devices per scan.
3. **Device type.** Each device is typed from what it reports (sysDescr, sysObjectID vendor, sysServices, ipForwarding, BRIDGE-MIB, LLDP capabilities), its interfaces and its web page:
   - **Switch**: it learns MAC addresses (switching table), or LLDP says *bridge*. A layer-3 switch also routes.
   - **Router**: it forwards IP and has no switching table, or LLDP says *router*.
   - **Firewall**: identified by vendor (Fortinet, Palo Alto, Check Point, SonicWall, Sophos, WatchGuard, Barracuda, pfSense/OPNsense).
   - **Access point**: it has 802.11 radios and few wired ports, or LLDP says *WLAN access point*. Access points bridge, so without this rule they would look like switches.
   - You can always set the type by hand on the device page.

## Managed and unmanaged switches

| Management | Meaning |
|---|---|
| **Managed (SNMP)** | Full inventory is read over SNMP: ports, MAC table, VLANs, neighbours, PoE, modules. |
| **Web interface only** | Found and recognised, but SNMP doesn't answer. This is a smart or web-managed switch, or SNMP is disabled. Enable SNMP and add the credential. |
| **No management found** | Recognised as a switch, but no management interface answered. |
| **Unmanaged (suspected)** | Has no IP address. Inferred from the MAC address table of a managed switch (see below). |

Unmanaged switches cannot be scanned, so Zentriq finds them indirectly. A port on a managed switch is flagged **Unmanaged switch?** when all of these are true:

- it is an Ethernet access port with no LLDP/CDP neighbour that is network equipment (an IP phone does not count);
- it is not a trunk (only one VLAN learned) and carries none of the MAC addresses of known network equipment or virtualization hosts;
- it has **two or more** real devices learned on it.

Virtual NICs (VMware, Hyper-V, KVM/QEMU, VirtualBox, Xen) and randomised (locally administered) MAC addresses are not counted, so a hypervisor or a laptop running Docker is not reported. An unknown access point or hub behind a port shows up the same way.

## What is collected (standard MIBs)

| Area | MIBs |
|---|---|
| Ports: name, alias, type, admin/link status, speed, duplex, MTU, errors, last change | IF-MIB, EtherLike-MIB |
| VLANs, access VLAN per port, trunks | Q-BRIDGE-MIB; Cisco VTP / VLAN membership |
| MAC address table (which device is on which port) | Q-BRIDGE-MIB `dot1qTpFdbPort`, BRIDGE-MIB `dot1dTpFdbPort` |
| ARP table (IP of each MAC), routing, route count | IP-MIB, IP-FORWARD-MIB |
| Neighbours / topology | LLDP-MIB (incl. management address and capabilities), CISCO-CDP-MIB |
| Chassis, stack members, modules, power supplies, fans, serials, firmware | ENTITY-MIB |
| PoE budget and use, PoE per port | POWER-ETHERNET-MIB |
| CPU | CISCO-PROCESS-MIB, HOST-RESOURCES-MIB |
| Spanning-tree root | BRIDGE-MIB |
| Port traffic and utilisation (64-bit counters), discards | IF-MIB `ifHCIn/OutOctets`, `ifIn/OutDiscards` |
| Spanning-tree state per port (blocking, forwarding …), link aggregation membership | BRIDGE-MIB `dot1dStpPortState`, IEEE8023-LAG-MIB |
| Memory in use | HOST-RESOURCES-MIB (RAM), CISCO-MEMORY-POOL-MIB |
| Temperature, voltage, current, power, fan speed, transceiver optical levels (dBm) | ENTITY-SENSOR-MIB |
| Fans, power supplies and temperature alarms | CISCO-ENVMON-MIB |
| BGP and OSPF neighbours and their state | BGP4-MIB, OSPF-MIB |
| Transceivers (SFP, SFP+, QSFP …) with model and serial | ENTITY-MIB |

Reads are GET and bounded GETBULK walks only. The row limits are 8,192 MAC entries, 4,096 ARP entries and 1,024 ports per device. A v2c community that gets no answer is retried with SNMPv1 for older devices.

MAC address and ARP tables are stored in their own tables, not in the device document. Each new collection replaces them.

## Traffic and health

**Traffic.** Each collection stores the port counters. From the second collection on, Zentriq shows the average traffic in and out since the previous collection and the utilisation against the port speed. A port above 70 % counts as **busy**. Schedule the network scan regularly, for example every few hours, to keep these figures meaningful.

**Health.** These problems are counted on the page and the dashboard:

- fans, power supplies or temperatures not normal;
- BGP or OSPF neighbours that are not established;
- CPU at 80 % or more, memory at 85 % or more, or temperature at 60 °C or more;
- ports blocked by spanning tree, ports with errors or discards, and ports running half duplex.

## The Network Inventory page

- **Tiles**: network devices, managed switches, suspected unmanaged switches, ports in use, PoE use, VLANs and links. Below them is one line listing what needs a look (alarms, neighbours down, busy ports, errors, half duplex, spanning-tree blocks, high CPU, memory or temperature, devices not seen), each linked to the matching list.
- **Devices**: breakdown by type, management and manufacturer, plus a list with firmware, ports up, VLANs, PoE, CPU, unmanaged switches behind it, and last seen.
- **Switch ports**: the switch port map.
  - Search a **MAC address** (any notation), an **IP address**, a device name or a port to find where a device is connected.
  - Filters: up, not connected, unused, disabled, uplinks, unmanaged, PoE, errors, half duplex.
- **Topology**: a map of LLDP/CDP links, with the core in the centre. Devices not in the inventory are shown faded. A table of all links is also available.
- **VLANs**: each VLAN with its name, the switches that carry it, its access ports and the devices on it.
- **Unmanaged switches**: suspected unmanaged switches behind ports, with their MAC addresses and the known devices among them. It also lists switches that cannot be managed over SNMP, with what to do about each.
- **Firmware**: versions in use per manufacturer and model. Mixed versions of the same model are highlighted.

On a **device page**, the **Network** tab shows:

- for every device, **Connected to**: the switch and port where its MAC address was learned, with the VLAN, and whether it is behind an unmanaged switch;
- for network devices, a summary (CPU, memory, temperature, up time), the ports with traffic and connected devices, neighbours, health (sensors, fans, power supplies), routing neighbours, optical levels, VLANs, and modules and transceivers.

## Dashboard and reports

**Dashboard widgets.** Customize → Network devices, Switch management, Switch port usage, Network attention. The first three are added to existing dashboards automatically.

**Built-in reports.** Reports & Analytics provides these:

- Network device health
- Busiest switch ports

- Network device register
- Network devices by type and management
- Network firmware by model
- Switch port capacity
- Switch port map
- Suspected unmanaged switches
- Port errors and duplex mismatches
- Network topology (LLDP/CDP links)

The same data is available as report datasets for your own reports: **Network devices**, **Switch ports** and **Network links (LLDP/CDP)**.

## API

| Endpoint | Returns |
|---|---|
| `GET /api/v1/network/summary` | Tiles and breakdowns |
| `GET /api/v1/network/devices?type=&management=&q=` | Network devices |
| `GET /api/v1/network/ports?assetId=&q=&filter=` | Switch port map; `q` takes a MAC, an IP, a name or a port |
| `GET /api/v1/network/neighbors` | LLDP/CDP links |
| `GET /api/v1/network/vlans` | VLANs |
| `GET /api/v1/network/unmanaged` | `inferred` and `notManageable` |
| `GET /api/v1/network/firmware` | Firmware versions per model |
| `GET /api/v1/assets/{id}/network` | One device: ports, neighbours, VLANs, modules, `connectedTo` |

All endpoints require *assets.view*.

## Keeping it current

- The links between devices and switch ports are recalculated after each collection and at the end of each scan.
- They are also recalculated every 30 minutes, so a computer found later by an agent is linked to its port.
- Schedule a recurring discovery scan of the management ranges with the SNMP credential. Port, MAC and neighbour data is only as fresh as the last collection.
