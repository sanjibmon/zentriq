// CI only: screenshots of the shell and of every window in src/dev/gallery.tsx, with the API answered here
// (a signed-in administrator, empty lists). Usage: node scripts/screens.mjs <dist-gallery dir> <output dir>
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { extname, join } from 'node:path';

const [dist, out] = process.argv.slice(2);
await mkdir(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.woff2': 'font/woff2' };
const server = createServer(async (req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  try {
    const file = join(dist, path === '/' ? 'gallery.html' : path);
    res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
    res.end(await readFile(file));
  } catch { res.writeHead(404); res.end(); }
}).listen(4175);

const modules = ['inventory', 'discovery', 'remote', 'deploy', 'patch', 'vuln', 'reports', 'lifecycle', 'accessories', 'nonit', 'network', 'licenses', 'webapps', 'cloud', 'security'];
const me = {
  id: 'u1', email: 'admin@example.com', displayName: 'Admin', mfaEnabled: true, roles: [{ id: 'admin', name: 'Administrator' }],
  permissions: ['assets.view', 'assets.manage', 'settings.manage', 'users.manage', 'audit.view', 'patch.view', 'patch.approve', 'patch.deploy', 'scripts.view',
    'scripts.manage', 'scripts.run', 'deploy.view', 'deploy.manage', 'remote.use', 'remote.manage', 'lifecycle.view', 'lifecycle.manage', 'accessories.view',
    'accessories.manage', 'nonit.view', 'nonit.manage', 'licenses.view', 'licenses.manage', 'webapps.view', 'webapps.manage', 'discovery.view', 'discovery.manage',
    'reports.view', 'vuln.view', 'network.view', 'cloud.view', 'security.view', 'activity.view', 'dashboard.view'],
  license: {
    mode: 'trial', summary: 'Trial', expiresAt: null, daysRemaining: 30, featuresEnabled: true, modules, edition: 'trial',
    sections: ['inventory', 'remote', 'secure', 'insight'].map((id) => ({ id, title: id, installed: true, enabled: true, standard: id !== 'remote' && id !== 'secure' })),
  },
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 980 }, deviceScaleFactor: 1 });
page.on('pageerror', (e) => console.log('page error:', e.message));
await page.route('**/api/**', (route) => {
  const url = new URL(route.request().url());
  const body = url.pathname.endsWith('/auth/me') ? me : url.pathname.endsWith('/lifecycle/meta') ? null : [];
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
});
const names = ['shell', 'add-device', 'add-accessory', 'add-it-assets', 'patch-policy', 'patch-deploy', 'new-script', 'new-schedule', 'license-product', 'license',
  'web-app', 'deploy-rule', 'deploy-package', 'deploy-group', 'non-it-asset', 'deploy-job', 'deploy-offer', 'deploy-decide'];
for (const n of names) {
  await page.goto(`http://127.0.0.1:4175/?d=${n}`);
  await page.waitForTimeout(700);
  await page.screenshot({ path: join(out, `${n}.png`), fullPage: false });
  const card = page.locator('.modal-card').first();
  if (await card.count()) {
    // The whole window, also the part below the fold.
    await card.evaluate((el) => { el.style.maxHeight = 'none'; el.closest('.modal-scrim').style.position = 'absolute'; });
    await card.screenshot({ path: join(out, `${n}-window.png`) });
  }
  console.log('screenshot', n);
}
await browser.close();
server.close();
