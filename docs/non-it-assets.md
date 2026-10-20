# Non-IT Asset Inventory

**Discover & inventory → Non-IT Asset Inventory** keeps the register of everything the organisation owns that is not IT equipment: furniture, appliances, vehicles, machinery, tools, safety equipment and so on. Items are entered by hand, found by their barcode, or uploaded in bulk from an Excel form.

## Categories

There are about 60 built-in categories in 11 groups:

| Group | Examples |
|---|---|
| Furniture | Office chair, desk / workstation, conference table, storage cabinet / almirah, filing cabinet, locker |
| Electrical & appliances | Air conditioner, refrigerator, water dispenser / purifier, microwave, coffee machine, geyser, air purifier |
| Office equipment | Shredder, laminator / binding machine, franking machine, currency counter, whiteboard, attendance machine |
| Audio-visual | Projector, PA system, video-conference camera, microphone |
| Facility & building | Diesel generator, inverter / battery bank, lift, HVAC unit, water pump, electrical panel, solar system |
| Safety & security | Fire extinguisher, fire alarm panel, first-aid kit, safe, metal detector, emergency light |
| Vehicles | Car, two-wheeler, van / truck, forklift, bicycle |
| Tools & machinery | Power tools, hand tools, ladder, production machine, compressor, welding machine |
| Kitchen & pantry | Pantry equipment, utensils, dishwasher |
| Medical & lab | Medical equipment, laboratory instrument, weighing scale |
| Other | Artwork, signage, planters |

Tagline: *Every chair, vehicle, appliance and tool — tagged, tracked and valued.*

**Groups & categories tab** (build 62):

- **Add group** creates a group on its own (for example *Medical equipment*), before it has categories. Groups can be renamed (their categories move with them) and deleted when they have no categories.
- **Add category**: a name and a group (pick one, or *＋ New group…*). Each group card also has **+** to add a category straight into it. Names are matched without regard to case, so *furniture* joins *Furniture*.
- Categories can be renamed, moved to another group (*Move…*) and deleted when no asset uses them.

**While adding or editing an asset** you don't have to leave the form: the *Category* list ends with **＋ Add new category…** (name and group, existing or new) and the *Department* list with **＋ Add new department…**. The new entry is selected at once.

**Departments tab:** the departments shared with IT Asset Inventory, with the number of non-IT and IT assets in each; add, rename and delete. Clicking a non-IT count opens the asset list filtered to that department.

## What is recorded per asset

| Area | Fields |
|---|---|
| Item | Name, category, manufacturer, model, serial number, status, condition, quantity |
| Identification | Asset tag, barcode |
| Where and who | Location, department, assigned to |
| Purchase | Purchase date, vendor, cost per item, invoice and PO number |
| Depreciation | Useful life, salvage value |
| Warranty, AMC and maintenance | Warranty end, AMC vendor and end date, insurance end, last and next maintenance |
| Notes | Free text |

How some of these work:

- **Asset tag:** left empty, it is numbered automatically (NIT-000001, NIT-000002 …).
- **Barcode:** the code printed on the item's label. Left empty, the asset tag is used as the barcode.
- **Status:** In use, In store, Under repair, Under maintenance, Reserved, Lost / missing or Disposed. Disposed items stay on record with their disposal date.
- **Current value:** calculated with straight-line depreciation from the purchase date, cost, useful life and salvage value.

**Custom fields** (Custom fields tab) add your own details to every asset, such as cost centre, floor, fuel type or registration number. A field can be text, number, date, yes/no or a list of choices. Custom fields appear on the form, in the Excel form, in exports and on the asset details.

Every change is kept in the asset's **History**: what changed, from what to what, who changed it and when. Creating, changing, deleting and uploading assets are also written to the audit log.

## Barcodes

- **Find an item:** click **Scan barcode**. A USB or Bluetooth barcode scanner types into the box and presses Enter. Zentriq then opens the item whose barcode, asset tag or serial number matches.
- **Use a camera:** on devices whose browser can read barcodes from the camera (most phones and tablets in Chrome), a **Use camera** button is also offered.
- **New code:** if no item has the scanned code, Zentriq offers to add a new asset with that barcode.
- **Print labels:** open an item, or select several in the list, and choose **Print label(s)**. Labels are Code 128 barcodes with the item name and location, three per row, 63 × 34 mm.

## Bulk upload from Excel

1. **Bulk upload → Download Excel form.** The workbook has three sheets:
   - **Assets:** the columns, with required columns highlighted and drop-down lists for category, status, condition, department and list-type custom fields.
   - **Instructions:** what to enter in each column.
   - **Lists:** the values used by the drop-downs.
2. Fill one row per item and save. You can also upload a CSV file with the same headings.
3. **Choose the file and check it.** Every row is checked and problems are listed by spreadsheet row number, for example an unknown category, a date that can't be read, or an amount that isn't a number. Nothing is saved until the whole file is valid.
4. **Upload.** All rows are applied together, or none are.

Options:

- **Update assets whose tag already exists.** On by default. Only the cells filled in the file change; empty cells keep the stored values.
- **Create categories that don't exist yet.** New categories are created in the group "Custom".

**Export** downloads the filtered list as Excel or CSV, in the same columns as the form. The same file can be edited and uploaded again.

## Dashboard and reports

- **Page tiles:** assets and items, purchase and current value, cover ending in 90 days, maintenance due, disposed.
- **Page charts:** assets by group and by status, plus the items coming up for warranty, AMC, insurance and maintenance.
- **Dashboard widgets:** Non-IT assets (by group), Non-IT asset value (with status), and Non-IT: coming up. All three are added to existing dashboards automatically.

**Built-in reports** (Reports & Analytics):

- Non-IT asset register
- Non-IT assets by category
- Non-IT assets by location and department
- Non-IT warranty ending (90 days)
- Non-IT maintenance due (30 days)
- Non-IT asset value and depreciation
- Disposed and lost non-IT assets

The **Non-IT assets** data source holds about 35 columns for your own reports, including current value, age and days left.

## Permissions and API

- **Viewing** (assets.view): lists, details, export, the Excel form and reports.
- **Changing** (assets.manage): adding, editing, bulk changes, upload, deleting, groups, categories, departments and custom fields.

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/nonit/summary` | Tiles and breakdowns |
| `GET /api/v1/nonit/assets?q=&category=&group=&status=&department=&location=&due=&sort=&dir=&page=` | Asset list |
| `GET /api/v1/nonit/assets/{id}` | One asset, with history |
| `GET /api/v1/nonit/lookup?code=` | Find by barcode, asset tag or serial number |
| `POST /api/v1/nonit/assets` | Add an asset |
| `PUT /api/v1/nonit/assets/{id}` | Edit an asset |
| `POST /api/v1/nonit/assets/bulk` | Change several assets at once |
| `POST /api/v1/nonit/assets/delete` | Delete assets |
| `GET`, `POST`, `PUT`, `DELETE /api/v1/nonit/categories` | Categories |
| `GET`, `POST`, `PUT`, `DELETE /api/v1/nonit/groups` | Groups (rename moves the categories; delete only when empty) |
| `GET`, `POST`, `PUT`, `DELETE /api/v1/nonit/fields` | Custom fields |
| `GET /api/v1/nonit/template` | The Excel form |
| `POST /api/v1/nonit/import?dryRun=&update=&createCategories=` | Bulk upload; the request body is the file |
| `GET /api/v1/nonit/export?format=xlsx\|csv` | Export, with the same filters as the list |

Non-IT assets do not count towards the device limits of the license.
