# Security Configuration and Integrations

This guide covers two modules: **Security Configuration**, which checks Windows security settings against baselines, and **Integrations & Automation**, which sends Zentriq events to other systems and provides read-only API access. Both modules are new in 1.1.1 (build 46).

---

## Part 1: Security Configuration Assessment

### What it does

Zentriq reads the security settings of every Windows device from its latest inventory and compares them with one or more **baselines**. Each device gets a result for every check (pass, fail, unknown or exception) and a **score from 0 to 100**. Zentriq also tracks how many devices are compliant over time.

The module is **read-only**. Zentriq does not change any setting on a device. For each check it gives the reason the check matters and the Group Policy or Intune setting that fixes it. You apply the fix with your own tools, and Zentriq re-checks the device after its next inventory.

### Where the data comes from

Agents and agentless WinRM scans collect the following in the `security.hardening` section of the inventory:

| Area | Collected |
|---|---|
| Firewall | Domain, private and public profile (policy value first, then the local setting) |
| Accounts | UAC (EnableLUA, ConsentPromptBehaviorAdmin), inactivity lock, Guest and built-in Administrator status (SID -501 / -500) |
| Network | SMBv1 (server parameter and client driver), SMB signing required, LLMNR policy |
| Remote access | Remote Desktop on or off, Network Level Authentication, RDP port |
| Credential protection | WDigest UseLogonCredential, LSA protection (RunAsPPL), LmCompatibilityLevel, NoLMHash, Credential Guard running |
| Protocols | SCHANNEL TLS 1.0, TLS 1.1 and SSL 3.0 (server). When a value is not set, the Windows default for that build applies. |
| Updates and logging | Automatic updates turned off by policy, PowerShell script block logging |
| Defender | Real-time protection, signature age, tamper protection |
| Existing data | BitLocker, Secure Boot, TPM, antivirus, local administrators, services, installed updates, OS version and edition |

> **Update agents and re-inventory.** Devices whose inventory came from an earlier build do not have these settings, so most of their checks show as *unknown*. To fix this, update the agents (**Agents › Update**) or collect inventory again (**Dashboard › Collect all**). Agentless devices report the settings at their next scan. The Overview tab shows how many devices still need to report.

### The checks

There are 30 built-in checks. Each one references a CIS Controls v8 safeguard and matches common CIS Benchmark and Microsoft security baseline settings.

| ID | Check | Area | Severity | Applies to |
|---|---|---|---|---|
| SC-01 | System drive encrypted (BitLocker) | Encryption | High | Workstations |
| SC-02 | Firewall on for every network profile | Network | High | All |
| SC-03 | Antivirus active | Malware protection | High | All |
| SC-04 | Antivirus signatures up to date (≤ 3 days, adjustable) | Malware protection | Medium | All |
| SC-05 | Defender tamper protection on | Malware protection | Medium | All (when Defender is used) |
| SC-06 | Operating system still receives security updates (Microsoft lifecycle, edition-aware, LTSC) | Patching | High | All |
| SC-07 | Security updates installed recently (≤ 35 days, adjustable) | Patching | High | All |
| SC-08 | Automatic updates not turned off by policy | Patching | Medium | All |
| SC-09 | Secure Boot on | Hardening | Medium | All |
| SC-10 | TPM present | Hardening | Low | Physical workstations |
| SC-11 | User Account Control on (and administrators are prompted) | Accounts | High | All |
| SC-12 | Guest account disabled | Accounts | High | All |
| SC-13 | Built-in Administrator account disabled | Accounts | Medium | Workstations |
| SC-14 | Few local administrators (≤ 3, adjustable) | Accounts | Medium | All |
| SC-15 | SMB version 1 disabled | Network | High | All |
| SC-16 | SMB signing required | Network | Medium | Servers |
| SC-17 | Remote Desktop requires NLA | Remote access | High | All |
| SC-18 | Remote Desktop disabled on workstations (off by default) | Remote access | Low | Workstations |
| SC-19 | LLMNR disabled | Network | Medium | All |
| SC-20 | WDigest does not keep passwords in memory | Credential protection | High | All |
| SC-21 | LSA protection on | Credential protection | Medium | All |
| SC-22 | Only NTLMv2 accepted (level ≥ 5, adjustable) | Credential protection | Medium | All |
| SC-23 | LM password hashes not stored | Credential protection | Medium | All |
| SC-24 | Old TLS/SSL versions disabled for incoming connections | Encryption | Medium | Servers |
| SC-25 | AutoRun disabled | Hardening | Medium | All |
| SC-26 | Screen locks after inactivity (≤ 900 s, adjustable) | Accounts | Medium | Workstations |
| SC-27 | PowerShell script block logging on | Logging | Low | All |
| SC-28 | Print Spooler stopped on servers | Hardening | Medium | Servers |
| SC-29 | Remote Registry service disabled | Hardening | Low | All |
| SC-30 | Credential Guard running (off by default) | Credential protection | Low | Workstations |

A check that does not apply to a device is not stored. For example, SC-16 does not apply to a workstation, SC-10 does not apply to a VM, and no check applies to a Linux device. When the device did not report the needed data, the result is **unknown**.

### Score

`score = 100 × (weight of passed checks) / (weight of passed + failed checks)`

The weights are high = 3, medium = 2 and low = 1. Unknown results and exceptions do not count.

A device is **compliant** when it has no failed high-severity check. The overview shows a daily trend of the average score and the number of compliant devices. History is kept for two years.

### Baselines

The **Windows security baseline** is built in and covers every Windows device. It has 28 checks on; SC-18 and SC-30 are off. To create your own baseline, use **Baselines › New baseline**, or use **Copy** on an existing one. In a baseline you can:

- set which devices it applies to: servers, workstations or both;
- turn individual checks on or off;
- change the severity of a check, which changes its score weight;
- change thresholds such as the patch age, signature age, maximum number of local administrators, inactivity limit and minimum NTLM level.

Every enabled baseline is evaluated, and a device gets a score per baseline. The built-in baseline cannot be deleted, but it can be turned off.

### Exceptions

Use an exception when a check knowingly does not apply. A common example is a print server, which needs the Print Spooler. To add one, go to **Checks**, open a check, and choose **Exception…** for one device or **Exception for all devices…**.

- A reason of at least 5 characters is required.
- The expiry date can be up to two years away, or the exception can have no expiry.
- The exception is recorded in the audit log.

An exempt check shows as **Exception** on the device and does not count toward its score. Exceptions that have expired stop applying.

### Schedule and permissions

- **Schedule:** devices are evaluated every 30 minutes with their latest inventory. Administrators can also click **Evaluate now**.
- **`security.view`:** see results. All roles that can view assets get this permission.
- **`security.manage`:** edit baselines and exceptions, and start an evaluation. The Administrator role has this permission.

### Reports and dashboard

- **Reports:** there is a new **Security configuration** data source with one row per check, device and baseline. The **Assets** data source gains *Security score* and *Failed security checks*.
- **Built-in reports:** *Failed security checks*, *Security compliance by check* and *Security score per device*.
- **Dashboard widgets:** *Security configuration score* (devices per score band) and *Most failed security checks*.
- **Device page:** the **Security** tab shows the device's score, its failed checks and how to fix each one.

---

## Part 2: Integrations & Automation

Open **Integrations & Automation**. You need the `integrations.manage` permission, which the Administrator role has.

### Channels: where messages go

| Channel | Use it for | Notes |
|---|---|---|
| **Webhook** | SOAR, automation tools, custom apps | Sends a JSON POST (see below). Optional signing secret and an extra header such as `Authorization` or `X-Api-Key`. http:// is allowed for internal tools. |
| **Slack** | Chat notifications | Uses an incoming webhook URL (https). |
| **Microsoft Teams** | Chat notifications | Uses a Teams *Workflows* webhook URL ("Post to a channel when a webhook request is received"). The message is an Adaptive Card 1.4. |
| **E-mail** | Up to 50 addresses | Uses the SMTP settings in System Settings › E-mail. |
| **Syslog / SIEM** | Splunk, Microsoft Sentinel (AMA/CEF), QRadar, ArcSight, Graylog | RFC 5424 with facility local0, over UDP or TCP, in **CEF** or **JSON** format. |
| **ServiceNow** | Incident tickets | Uses the Table API (`/api/now/table/incident`, or another table). Urgency and impact follow the event severity. |
| **Jira** | Issues | Uses REST API v2 (`/rest/api/2/issue`). Authenticates with e-mail + API token (Cloud) or a personal access token (Data Center). |

Secrets such as webhook URLs with tokens, passwords, API tokens and signing secrets are **encrypted** with the installation credential key (AES-256-GCM). The API never returns them; the console only shows that a value is stored. Use **Send test** to check a channel. The result is written to the delivery log.

### Webhook format and signature

```http
POST /your/endpoint
Content-Type: application/json
X-Zentriq-Event: vulnerability.new
X-Zentriq-Delivery: 1234
X-Zentriq-Timestamp: 1760600000
X-Zentriq-Signature: sha256=<hex HMAC-SHA256(secret, "<timestamp>.<body>")>

{"id":1234,"type":"vulnerability.new","severity":"critical","title":"3 new vulnerabilities on SRV-FILE01 (1 known exploited)",
 "text":"CVE-… [critical, CVSS 9.8, exploited] …","occurredAt":"2026-10-16T09:30:00Z","link":"https://zentriq.example/assets/…",
 "asset":{"id":"…","hostname":"SRV-FILE01","ip":"10.0.0.5","category":"server","criticality":"High","os":"Windows Server 2022 Standard"},
 "data":{"findingIds":[…],"cves":[…],"count":3},"source":"Zentriq"}
```

To verify a delivery, compute `HMAC-SHA256(secret, timestamp + "." + rawBody)` and compare it with the signature using a constant-time comparison. Reject any timestamp that is more than 5 minutes old.

### CEF mapping (syslog)

`CEF:0|Zentriq|Zentriq|<version>|<event type>|<title>|<severity>|rt=<ms> externalId=<event id> msg=<text> dhost=<hostname> dst=<ip> cs1Label=link cs1=<console link> cs2Label=assetCriticality cs2=<criticality>`

The CEF severity is critical = 10, high = 8, medium = 5, low = 3 and info = 1. The syslog severity follows the same order: 2, 3, 4, 5, 6.

### Events

| Event | Raised when | Severity |
|---|---|---|
| `vulnerability.new` | New findings appear on a device. Each device gets one event per evaluation that lists every new finding. | Worst finding; known exploited = critical |
| `vulnerability.overdue` | Open findings pass their SLA due date (grouped per device) | Worst finding |
| `security.failed` | Configuration checks start failing on a device (grouped per device) | Worst check |
| `asset.discovered` | A device is seen for the first time | info |
| `asset.offline` | A device has not been seen for the configured number of hours (default 24) | medium |
| `agent.offline` | An agent has not checked in for the configured number of hours | medium |
| `scan.completed` / `scan.failed` | A discovery scan finishes or fails | info / medium |
| `job.failed` | A background job fails (report delivery, feed update, …) | medium |

The detectors run every minute. Each detector only looks at what changed since its last run, and events are de-duplicated. Because events are grouped per device, a large first evaluation does not send thousands of messages.

### Alert rules

An alert rule has these settings:

- an **event** type;
- a **minimum severity**;
- an optional **device category** and **criticality** filter;
- one or more **channels**;
- an optional **throttle**: at most one message per rule and device in N minutes. Messages held back by the throttle are logged as *throttled*.

For example:

- *New vulnerabilities, critical, servers only* → ServiceNow + Teams.
- *Security checks failing, everything* → Sentinel (syslog).
- *Agent not checking in, criticality Critical/High, throttle 240 min* → e-mail.

When a ServiceNow incident or Jira issue is created for vulnerability findings, its number is **written back** to the findings in the *Ticket* field and added to the finding history.

### Delivery log and retries

Every message is logged with the channel, the rule, the result and the HTTP status or ticket number.

- **Retries:** a failed send is retried after 1, 5, 15 and 60 minutes, and then marked *failed*.
- **Permanent errors:** errors such as 400, 401, 403 and 404 fail at once, and the channel shows the error.
- **Send again:** use **Send again** to queue a failed or throttled message one more time.
- **Retention:** the log is kept for 90 days.

### API tokens (read-only)

Go to **API access › New token**. Choose what the token can read, and when it expires (up to two years, or never).

- **Shown once:** the token is shown only when it is created. Only a SHA-256 hash is stored.
- **Read-only:** tokens can only make GET requests. A POST, PUT or DELETE with a token is refused.
- **Permissions:** a token has the permissions chosen when it was created, limited to the permissions its creator still has. A token stops working when its creator is disabled, when it is revoked, or when it expires.
- **Permissions that can be granted:** assets, vulnerabilities, security configuration, report data, audit log and jobs.

```bash
# Data of any saved report (id from the report's address in the console)
curl -H "Authorization: Bearer zq_…" "https://zentriq.example/api/v1/reports/<report-id>/data?format=csv" -o devices.csv
curl -H "Authorization: Bearer zq_…" "https://zentriq.example/api/v1/reports/<report-id>/data"     # JSON: columns + rows
```

```powershell
$h = @{ Authorization = "Bearer zq_…" }
Invoke-RestMethod -Headers $h "https://zentriq.example/api/v1/vulnerabilities/findings?status=active&pageSize=200"
Invoke-RestMethod -Headers $h "https://zentriq.example/api/v1/security/devices?pageSize=200"
```

Each report data download is written to the audit log (`report.data`) under the token's name.

### Network requirements

The **Zentriq worker** needs outbound access to the channel endpoints. These are the webhook hosts, `hooks.slack.com` or your Teams Workflows host, the SIEM collector port, your ServiceNow instance and your Jira site. Test messages are sent by the **API service**.

### Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Most checks *unknown* | The device has not reported its security settings. Update the agent or collect inventory again. |
| SC-06 *unknown* | The Windows version is not in the lifecycle table (for example an Insider build). |
| Channel shows *Failing · HTTP 401* | The user name, password or token is wrong or has expired. Edit the channel and type the value again. |
| Teams returns HTTP 400 | Use a *Workflows* webhook URL. Old Office 365 connector URLs are being retired by Microsoft. |
| No syslog messages arrive | Check the firewall between the Zentriq server and the collector, and the UDP/TCP choice. Use **Send test**. |
| No link in messages | Set the *Console URL* in System Settings › E-mail. |
