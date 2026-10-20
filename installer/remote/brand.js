// Zentriq branding for the bundled MeshCentral engine (Apache-2.0). Run when the installer is built (and in CI):
//   node brand.js <app folder>      (the folder that contains node_modules/meshcentral)
// - Installs Zentriq's own pages (meshcentral-web/views: the invitation / download page people see).
// - Replaces the MeshCentral product name in the remaining pages technicians see with "Zentriq"; identifiers,
//   file names and URLs are left alone, so the engine keeps working. Translations are removed (English only,
//   so every page is the branded one).
// - Uses the Zentriq icon as the browser tab icon.
// Licence and notice files are kept unchanged.
'use strict';
const fs = require('fs');
const path = require('path');

const app = path.resolve(process.argv[2] || '.');
const mc = path.join(app, 'node_modules', 'meshcentral');
if (!fs.existsSync(path.join(mc, 'meshcentral.js'))) { console.error('meshcentral not found in ' + app); process.exit(1); }
const src = __dirname;

// 1. Zentriq pages (engine override folder: <app>/meshcentral-web/views).
const views = path.join(app, 'meshcentral-web', 'views');
fs.mkdirSync(views, { recursive: true });
for (const f of fs.readdirSync(path.join(src, 'views'))) fs.copyFileSync(path.join(src, 'views', f), path.join(views, f));

// 2. English only.
for (const d of [path.join(mc, 'views', 'translations'), path.join(mc, 'emails', 'translations')])
  if (fs.existsSync(d)) fs.rmSync(d, { recursive: true, force: true });

// 3. Product name in visible text. Order matters (longer names first).
const rules = [
  [/MeshCentral Assistant/g, 'Zentriq Remote Support app'],
  [/MeshCentral Router/g, 'Zentriq Router'],
  [/MeshCentral Satellite/g, 'Zentriq Satellite'],
  [/MeshCentral Server/g, 'Zentriq Remote Support server'],
  [/MeshCentral Session Player/g, 'Zentriq Session Player'],
  // A bare product name: not part of an identifier, a file name, a path or a URL.
  [/(?<![A-Za-z0-9_$.\/\\-])MeshCentral(?![A-Za-z0-9_$.\/\\-])/g, 'Zentriq'],
  [/Mesh Agent/g, 'Zentriq agent'],
  [/mesh agent/g, 'Zentriq agent'],
  [/Meshcentral/g, 'Zentriq'],
];
let files = 0, changes = 0;
function brand(file) {
  const before = fs.readFileSync(file, 'utf8');
  let after = before;
  for (const [re, to] of rules) after = after.replace(re, () => { changes++; return to; });
  if (after !== before) { fs.writeFileSync(file, after); files++; }
}
for (const f of fs.readdirSync(path.join(mc, 'views'))) if (f.endsWith('.handlebars')) brand(path.join(mc, 'views', f));
for (const f of ['agent-desktop-0.0.2.js', 'themes/theme-switcher.js'].map((x) => path.join(mc, 'public', 'scripts', x))) if (fs.existsSync(f)) brand(f);
// Linux install script messages.
for (const f of ['meshinstall-linux.sh', 'meshinstall-initd.sh'].map((x) => path.join(mc, 'agents', x))) if (fs.existsSync(f)) brand(f);

// 4. Browser tab icon.
const ico = path.join(src, 'remote-agent.ico');
if (fs.existsSync(ico)) fs.copyFileSync(ico, path.join(mc, 'public', 'favicon.ico'));

console.log(`Zentriq branding applied: ${fs.readdirSync(views).length} page(s) installed, ${changes} name(s) replaced in ${files} file(s).`);
