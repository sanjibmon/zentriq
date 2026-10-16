# Departments, change history and console options (build 47)

## Departments / units

After discovery, administrators group devices by department, business unit, location or any other grouping they use.

**Where to assign**

- **Asset Inventory:** tick devices and click **Assign department (n)**. Choose a department, or **＋ Add new department…** at the end of the list, or *None* to clear it.
- **Device page:** the **Overview** tab has a *Department / unit* dropdown that also offers *Add new…*.
- **Asset Inventory › Departments:** see every department with its device count, and add, rename or delete them. Deleting a department leaves its devices unassigned.

**Where the department shows up**

- **Asset list:** a *Department* column and a *Department* filter, including *Not assigned*.
- **Dashboard:** the *Assets by department* chart. Click a bar to open those devices.
- **Reports:** every device-based data source has a **Department / unit** column (assets, hardware, software, vulnerabilities, security configuration and changes). You can filter and group by it.
- **Built-in reports:** *Assets by department* and *Asset register by department*.

**Permissions:** assigning and editing need the `assets.manage` permission. Viewing needs `assets.view`.

## Hardware and software change history

From the second inventory of a device onwards, Zentriq compares each new inventory with the previous one and records what changed.

| Hardware | Software |
|---|---|
| Memory added or removed (e.g. 8 GB → 16 GB) | Application installed (with version) |
| Disk added or removed (identified by serial number) | Application uninstalled |
| Processor or core count changed | Application updated (old → new version) |
| Manufacturer / model or serial number changed (board or device replaced) | Windows update (KB) installed |
| BIOS / firmware version changed | Operating system upgraded (e.g. 23H2 → 24H2; monthly cumulative updates are listed as Windows updates instead) |
| TPM added or removed | |
| Physical network adapter added or removed (VPN and virtual adapters are ignored) | |

**Protection against false changes.** Nothing is recorded in these cases:

- the first inventory of a device;
- when the collection method changed (for example agentless → agent);
- for a value that one of the two inventories did not report;
- when the software list suddenly lost more than half of its entries, which points to an incomplete collection rather than mass uninstallation.

**Where changes show up**

- **Hardware Inventory › Changes** and **Software Inventory › Changes:** totals for the last 30 days, the changed devices (device-wise, most recent first) and the full history, which you can filter.
- **Device page › Changes:** that device's history, up to one year.
- **Dashboard:** the *Hardware changes* and *Software changes* widgets list the changed devices and what changed. On a dashboard that was saved earlier, they are added once; remove them with **Customize** if you don't want them.
- **Reports:** a new data source, *Hardware & software changes*, and the built-in reports *Hardware changes (30 days)* and *Software changes (30 days)*.
- **Alerts:** new events `hardware.changed` and `software.changed`, one message per device per collection. Use them in **Integrations & Automation › Alert rules**, for example to e-mail the asset team when hardware changes on servers.

**Retention:** the history is kept for 365 days.

## Other changes

- **Vulnerability Management › Overview:** each chart tile has a selector: *Donut, Pie, Bar* or *Column*. *Aging* also offers *Stacked*. The choice is remembered in the browser.
- **Security Configuration:** the page header is now the title and a one-line tagline.
- **Licensing › Workload usage:** the columns are now **Entitled**, **Used**, **Available** and **Utilization**. The column previously called "Billable" is now "Used".
- **System Settings › Company logo:** upload a PNG, JPEG or WebP image of up to 1 MB. It is shown in the console header and on the sign-in page.
  - SVG is refused because it can contain scripts.
  - Use **Remove logo** to go back to no logo.
  - Changing the logo needs the `settings.manage` permission.
