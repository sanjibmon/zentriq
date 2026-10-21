# IT accessories

**IT Accessories** (next to Hardware Inventory) manages accessories that are bought in bulk and handed out: mouse,
keyboard, headset, webcam, docking station, adapters, cables, chargers and similar. Unlike Non-IT assets (one tagged
record per item), accessories are kept as **quantities in stock per location**, and every change is a **movement**.

## Setting up

1. **Locations** — stock is kept per location (store, office, floor). Add them under *Departments & locations*, or
   choose *＋ Add new location…* in any location list. The same locations are used for IT assets.
2. **Catalogue › Add accessory** — name, category (Mouse, Keyboard, Headset, Webcam, Docking station, Adapter / dongle,
   or your own), brand, model, SKU / part number / barcode, unit cost and:
   - **Reorder level** — when the quantity at a location falls to this number, the accessory is flagged *Reorder*
     (*Out of stock* at 0), counted on the dashboard and an **IT accessory stock low** event is raised.
   - **Returnable** — expected back when the person leaves (headset, docking station). Untick for consumables (cables,
     batteries); they are still recorded as issued but are not in the collect-back list.
   - **Track each unit by serial number** — for costly items: serial numbers are entered when receiving and picked when
     issuing, returning, transferring or writing off.

## Stock movements

Stock is never typed in directly: it always follows the movements, all of which are kept.

| Movement | Effect | Recorded |
|---|---|---|
| **Receive stock** | + quantity at the location | vendor, invoice / PO number, unit cost per line (updates the accessory's cost), serial numbers |
| **Issue** | − quantity, given to a person | person (directory users, logged-on users and earlier recipients are suggested), department, optional device, reason (new joiner, replacement, damaged, lost…), ticket |
| **Return** | + quantity; *Faulty* returns are kept apart and not issued again | person, condition (good / faulty) |
| **Transfer** | − at one location, + at another | reference |
| **Write off** | − usable or faulty units | reason (damaged, lost, stolen, scrapped…) |
| **Stock count** | sets the quantity to what was counted; the difference is booked | reason (not for serial-tracked items) |

Several accessories can be received or issued at once (for example a new-joiner kit: mouse, keyboard and headset).
An issue can never take the stock below zero ("Only 4 × USB mouse in stock at this location"); concurrent issues are
serialised. A person can return only what they hold.

## Pages

- **Stock** — every accessory at every location with *In stock*, *Faulty* and the reorder level; *Issue*, *Receive* and
  *More…* (transfer, write off, stock count) per row. Filters: location, category, *Only to reorder*.
- **Issued to people** — who holds what (issued minus returned), with department, serial numbers and the last issue
  date; *Return* per line. Search a name for the collect-back list when someone leaves; *Returnable only* hides
  consumables.
- **Movements** — the full history with date, movement, quantity, location (→ destination), person / department,
  reason, reference / vendor and who recorded it; filters for movement type, location, department and period.
- **Catalogue** — the accessories with stock per location; edit, deactivate (keeps the history) or delete (only without
  history).
- **Columns** on every table shows, hides or adds columns (value, unit cost, SKU, serial numbers, device, notes…).

## Dashboard, reports and alerts

- Dashboard: **IT accessories in stock** (by category, stock value, issued in 30 days) and **IT accessories to
  reorder**. Existing dashboards get them once (*Customize* to remove).
- Reports: data sources **IT accessory stock** and **IT accessory movements**; built-in reports *Accessory stock on
  hand*, *Accessories below reorder level*, *Accessories issued (30 days)* and *Accessory consumption by department*
  (export to Excel, CSV or PDF, schedule by e-mail).
- Integrations: event **IT accessory stock low** (low, or medium when out of stock) for e-mail, Teams, Slack, webhook,
  syslog, ServiceNow or Jira rules.

## Permissions and audit

Viewing needs `assets.view`; adding accessories and recording movements need `assets.manage`. Every movement and
catalogue change is in the audit log (`accessory.*`).
