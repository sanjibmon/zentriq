# Cloud & Infrastructure

Zentriq reads your public cloud and virtualization platforms with a **read-only** account and shows every virtual machine,
host and cloud resource next to the devices in your inventory. Nothing is started, stopped or changed on the platforms.

Module: **Cloud & Infrastructure** (navigation group *Infrastructure*, Standard and Advanced editions, Standard installation).

## Supported platforms

| Platform | What Zentriq needs | What is read |
|---|---|---|
| **Microsoft Azure** | An app registration (tenant ID, client ID, client secret) with the **Reader** role on the subscriptions. Leave *Subscription IDs* empty to read every subscription the app can see. | Azure Resource Graph: virtual machines (power state), managed disks, public IPs, virtual networks, network security groups, load balancers, storage accounts, Azure SQL databases, PostgreSQL / MySQL flexible servers, AKS clusters, function apps; network interfaces give each VM its MAC and IP addresses. |
| **Amazon Web Services** | An IAM user access key with **ReadOnlyAccess** (or SecurityAudit + ViewOnlyAccess). Leave *Regions* empty for every enabled region. | Per region: EC2 instances, EBS volumes, Elastic IPs, security groups, VPCs, RDS databases, load balancers (ELBv2), EKS clusters, Lambda functions; S3 buckets once. Requests are signed with Signature Version 4. |
| **Google Cloud** | A service account **JSON key** with the **Viewer** role on the projects (or Compute Viewer, Cloud SQL Viewer, Kubernetes Engine Viewer, Storage Object Viewer). Leave *Project IDs* empty for every project it can see (or the key's own project). | Per project: Compute Engine instances (status and since when), persistent disks, external addresses, VPC networks, firewall rules, forwarding rules (load balancers), Cloud Storage buckets, Cloud SQL instances, GKE clusters, Cloud Functions. An API that is not enabled in a project is a warning. Sign-in is a JWT signed with the key (RS256). |
| **VMware vCenter** | vCenter Server 6.5 or later, a user with the **Read-only** role (propagated). Optionally skip the certificate check for a self-signed vCenter certificate. | Clusters (HA / DRS), ESXi hosts, datastores (capacity, free space), virtual machines with their host, BIOS UUID, network adapters, disks and — with VMware Tools running — guest host name, IP and operating system. |
| **Microsoft Hyper-V** | The host names (every node of a cluster) and a saved **Windows credential** (Discovery › Credentials) in *Hyper-V Administrators* or an administrator. WinRM (5985) or WMI/DCOM must be reachable, as for an agentless Windows scan. | The host (model, serial, OS, memory, CPUs) and each VM: state and since when, vCPU, startup memory, MAC addresses, virtual disks, checkpoints, BIOS GUID, generation, and guest FQDN, IPv4 and OS from the integration services. |

Secrets (client secret, secret access key, JSON key, passwords, API token secrets) are encrypted with the credential key (AES-GCM) and are
write-only: the API never returns them. Editing a connection without typing a new secret keeps the saved one. Hyper-V uses a
saved Windows credential instead of its own secret.

| **Nutanix AHV** | Prism Central (all registered clusters) or Prism Element, port 9440, a user with the **Viewer** role. | v3 REST API: clusters, AHV hosts (model, serial, hypervisor, cores, memory), VMs (power state, vCPU, memory, disks, MAC and IP, categories as tags). The VM UUID is its SMBIOS UUID. |
| **Proxmox VE** | An **API token** (`user@realm!tokenname` and its secret) with the **PVEAuditor** role on `/`; any node of the cluster, port 8006. | Nodes, storage (capacity, free space), QEMU VMs and LXC containers: state, vCPU, memory, disk, MACs, SMBIOS UUID, tags, snapshots; with the QEMU guest agent the IP address and OS. |
| **XCP-ng / Citrix Hypervisor (XenServer)** | The **pool master** over HTTPS; root or a pool user with the **Read Only** role. | XAPI JSON-RPC: pool, hosts (version, CPUs, memory, model, serial), storage repositories (free space), VMs (power state, vCPU, memory, MACs, snapshots, tags; guest tools: IP, OS). A Xen VM's UUID is its SMBIOS UUID. |
| **oVirt / Red Hat Virtualization / Oracle Linux Virtualization Manager** | The engine (`https://engine/ovirt-engine`), a user such as `zentriq@internal` with a read-only role (ReadOnlyAdmin). | REST API v4: clusters, hosts (OS, CPUs, memory, model, serial, BIOS UUID), data storage domains (free space), VMs (status and since when, vCPU, memory, MACs, snapshots; guest agent: IP, FQDN, OS). |

For the host-based platforms (vCenter, Nutanix, Proxmox, XCP-ng, oVirt) tick *Skip the certificate check* when the server
uses its own self-signed certificate, or install its root certificate on the Zentriq server.

## Syncs

* A connection is synced when it is added, every *n* hours (1–168, default 6) by the worker, and on **Sync now**.
* One sync = one background job (`cloud.sync`); the job log lists what was read and every warning.
* **Partial**: a region, a service or a host could not be read (permission missing, region not enabled, host unreachable).
  Everything else is saved; resources of the parts that were not read are kept (never marked deleted by a partial read).
* **Failed**: Zentriq could not sign in or reach the platform at all. The connection shows the reason (wrong secret,
  expired secret, unknown tenant, key not valid, certificate not trusted, host unreachable, Hyper-V role not installed…)
  and an integration event *Cloud connection failed* is raised.

## Resources, changes and the inventory

* Every resource has its connection, type, region / site, group (resource group, VPC, cluster), state and **since when**,
  size / SKU, OS or engine, host name, IPs, vCPU, memory, storage, tags and the platform's own details.
* **Changes** are recorded per sync: new, changed (state, size, OS, IPs, vCPU, memory, storage, host) with the old and new
  values, deleted (only after a complete read) and back again.
* **Linking to the inventory**: virtual machines and hosts are matched to inventoried devices (agent or scan) by
  BIOS UUID (as reported, and with the first three groups byte-swapped as Windows shows Azure and VMware machines),
  cloud instance ID, MAC address, then host name with the same IP. A link set or removed by hand (resource page ›
  Inventoried device) is kept by later syncs; *Match automatically* gives it back. Running VMs without a link are machines
  with no agent and no scan — the overview counts them.
* **Department and location from tags**: a tag `department`, `dept`, `costcenter` / `cost-center` (or `location`, `site`,
  `office`) whose value equals a Zentriq department or location name (or code) fills that column.
* Cloud resources **do not count** towards the device licence.

## Findings

| Finding | Severity | When |
|---|---|---|
| All ports open to the internet | critical | Inbound allow rule for every port from `0.0.0.0/0`, `::/0`, `*` or `Internet`. |
| RDP / SSH open to the internet | high | Inbound 3389 or 22 allowed from any address. |
| Storage open to the public | high | Anonymous public access allowed (Azure storage account). |
| Datastore almost full | high < 10 % free, medium < 20 % | VMware datastores, Proxmox storage, XCP-ng storage repositories, oVirt storage domains. |
| Unattached disk | medium | A managed disk / EBS volume not attached to anything (still billed). |
| Stopped but still billed | medium | Azure VM stopped from inside the guest but not deallocated. |
| Storage accepts unencrypted HTTP | medium | Secure transfer not required. |
| Database reachable from the internet / storage not encrypted | medium | RDS *PubliclyAccessible*, flexible server public network access; storage encryption off. |
| Disk not encrypted | medium | EBS volume without encryption. |
| Host memory over-committed | medium | Memory of running VMs above 150 % of the host's memory (Hyper-V, Nutanix, Proxmox, XCP-ng, oVirt). |
| Host operating system out of support | medium | Hyper-V host on Windows Server 2012 R2 or older. |
| Public IP not in use | low | Reserved but not attached (billed while idle). |
| Virtual machine switched off for a long time | low | Off, deallocated, terminated, halted or down for 30 days or more. |
| Old checkpoint / snapshot | low | A checkpoint / snapshot 7 days old or more (Hyper-V, Proxmox, XCP-ng, oVirt). |
| No tags | info | Azure / AWS / Google Cloud VM, disk, storage or database without any tag or label. |

A finding is resolved by itself when a later sync no longer sees the condition, or when the resource is deleted. **Ignore**
hides a finding (who ignored it is recorded); **Reopen** brings it back. New findings (except *info*) raise the integration
event *Cloud or virtualization finding*, which alert rules can send to e-mail, Teams, Slack or a webhook.

## Console

**Cloud & Infrastructure** (`/cloud`):

* **Overview** — virtual machines (running / stopped), share linked to the inventory, open findings by severity, capacity
  (vCPU, memory, storage), hosts, changes in 7 days, by platform, top findings, resource types, recent changes.
* **Resources** — search and filter by platform, type, state, linked / not linked, with findings, deleted; column chooser.
* **Virtualization** — hosts with their VMs and memory given to running VMs, datastores with free space, clusters.
* **Findings** — open / ignored / resolved, by severity, finding and platform; ignore and reopen.
* **Connections** — add, edit, sync now, delete; last sync, status, error and warnings, signed-in identity.
* **Changes** — history over 1 day to 1 year.
* **Resource page** (`/cloud/resources/{id}`) — all details, the inventoried device (link / unlink / automatic), findings,
  tags, the VMs on a host, platform details and change history.

Permissions: viewing needs *assets.view*; adding / editing / deleting connections *credentials.manage*; **Sync now**
*scans.run*; linking devices and ignoring findings *assets.manage*.

## Reports

Datasets **Cloud and virtual resources** (`cloud_resources`) and **Cloud and virtualization findings** (`cloud_findings`),
with built-in reports *Cloud and virtual resources*, *Virtual machines by platform*, *Virtual machines without inventory* and
*Open cloud and virtualization findings*.

## API

All under `/api/v1/cloud` (module *cloud*):

| Method | Path | |
|---|---|---|
| GET | `/meta`, `/summary` | Platforms, types, rules; overview figures. |
| GET / POST | `/accounts` | List (no secrets) / add (queues the first sync). |
| PUT / DELETE | `/accounts/{id}` | Edit (secret optional) / delete with its resources. |
| POST | `/accounts/{id}/sync` | Queue a sync (409 when one is queued or running). |
| GET | `/resources?provider=&kind=&accountId=&state=&linked=&q=&findings=&deleted=&sort=&page=&pageSize=` | Resources. |
| GET | `/resources/{id}` | Resource with device, host, children, findings and changes. |
| PUT | `/resources/{id}/link` | `{ "assetId": "…" }`, `{ "assetId": null }` (unlink) or `{ "automatic": true }`. |
| GET | `/findings?status=open|ignored|resolved|all&severity=&provider=&rule=&q=` | Findings. |
| POST | `/findings/{id}/ignore` | `{ "ignored": true|false }`. |
| GET | `/changes?days=&change=&kind=&accountId=` | Change history. |

## Firewall

The Zentriq server needs outbound HTTPS (through the system proxy if one is set) to `login.microsoftonline.com` and
`management.azure.com` for Azure; `oauth2.googleapis.com` and `*.googleapis.com` for Google Cloud; `sts.amazonaws.com`, `s3.amazonaws.com` and `*.<region>.amazonaws.com` for AWS; the vCenter
on 443 (or its port), Prism on 9440, Proxmox on 8006, the XCP-ng pool master and the oVirt engine on 443; and WinRM 5985 or RPC/DCOM to the Hyper-V hosts.
