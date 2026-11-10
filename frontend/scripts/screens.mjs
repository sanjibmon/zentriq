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

const modules = ['securitypack', 'inventory', 'discovery', 'remote', 'deploy', 'patch', 'vuln', 'reports', 'lifecycle', 'accessories', 'nonit', 'network', 'licenses', 'webapps', 'cloud', 'security'];
const me = {
  id: 'u1', email: 'admin@example.com', displayName: 'Admin', mfaEnabled: true, roles: [{ id: 'admin', name: 'Administrator' }],
  permissions: ['assets.view', 'assets.manage', 'settings.manage', 'users.manage', 'audit.view', 'patch.view', 'patch.approve', 'patch.deploy', 'scripts.view',
    'scripts.manage', 'scripts.run', 'deploy.view', 'deploy.manage', 'remote.use', 'remote.manage', 'lifecycle.view', 'lifecycle.manage', 'accessories.view',
    'accessories.manage', 'nonit.view', 'nonit.manage', 'licenses.view', 'licenses.manage', 'webapps.view', 'webapps.manage', 'discovery.view', 'discovery.manage',
    'reports.view', 'privilege.view', 'privilege.manage', 'bitlocker.view', 'bitlocker.manage', 'bitlocker.recover', 'devicecontrol.view', 'devicecontrol.manage', 'vuln.view', 'network.view', 'cloud.view', 'security.view', 'activity.view', 'dashboard.view'],
  license: {
    mode: 'trial', summary: 'Trial', expiresAt: null, daysRemaining: 30, featuresEnabled: true, modules, edition: 'trial',
    sections: ['inventory', 'remote', 'secure', 'insight'].map((id) => ({ id, title: id, installed: true, enabled: true, standard: id !== 'remote' && id !== 'secure' })),
  },
};

// Sample answers for the pages in the gallery (everything else: empty lists).
const day = (n) => new Date(Date.now() - n * 86400000).toISOString().slice(0, 10);
const issue = (kind, severity, text, points) => ({ kind, severity, text, points });
const dev = (i, score, rating, issues, extra = {}) => ({ assetId: `00000000-0000-0000-0000-0000000000${10 + i}`, hostname: `LT-${1040 + i}`, os: 'Windows 11 Pro', department: ['Finance', 'Sales', 'Engineering'][i % 3],
  user: `CORP\\user${i}`, score, rating, bootMs: 18000 + i * 9000, logonMs: 9000, crashes: i % 4, hangs: i % 2, bugchecks: i === 0 ? 2 : 0, shutdowns: i === 0 ? 1 : 0, serviceFailures: 0,
  cpuHigh: 3, memHigh: 12, systemFree: 40 - i * 4, battery: 88, uptimeDays: 3 + i, issues, computedAt: new Date().toISOString(), ...extra });
const now = new Date().toISOString();
const samples = {
  '/api/v1/security-pack/privilege': { kpis: { devices: 248, reporting: 241, toRemove: 37, devicesWithExtraAdmins: 29, removed: 0, elevations: 112, refused: 9, pending: 3, grants: 2, rules: 14 },
    policies: [{ groupId: null, groupName: null, updatedBy: 'admin@example.com', updatedAt: now, settings: { mode: 'audit', allowedAdmins: ['CORP\\Workstation Admins', 'CORP\\it-*'], elevation: true, requests: true, maxAdminMinutes: 240 } },
      { groupId: 'g1', groupName: 'Developers', updatedBy: 'admin@example.com', updatedAt: now, settings: { mode: 'enforce', allowedAdmins: [], elevation: true, requests: true, maxAdminMinutes: 480 } }] },
  '/api/v1/security-pack/bitlocker': { kpis: { devices: 248, reporting: 236, protected: 214, encrypting: 6, unprotected: 16, noTpm: 4, errors: 2, keys: 231, withoutKey: 1 },
    policies: [{ groupId: null, groupName: null, updatedBy: 'admin@example.com', updatedAt: now, settings: { mode: 'enforce', method: 'XtsAes256', usedSpaceOnly: true, fixedDrives: false, rotateAfterView: true } }] },
  '/api/v1/security-pack/bitlocker/volumes': [
    { assetId: 'a1', hostname: 'LT-1040', volume: 'C:', kind: 'os', protection: 'on', status: 'encrypted', percent: 100, method: 'XTS-AES 256', protectors: ['TPM', 'Recovery password'], tpm: 'ready', error: null, reportedAt: now, department: 'Finance', keys: 1 },
    { assetId: 'a2', hostname: 'LT-1041', volume: 'C:', kind: 'os', protection: 'off', status: 'encrypting', percent: 42, method: 'XTS-AES 256', protectors: ['TPM', 'Recovery password'], tpm: 'ready', error: null, reportedAt: now, department: 'Sales', keys: 1 },
    { assetId: 'a3', hostname: 'PC-0207', volume: 'C:', kind: 'os', protection: 'off', status: 'decrypted', percent: 0, method: null, protectors: [], tpm: 'absent', error: 'No TPM: the system drive is encrypted only with a TPM.', reportedAt: now, department: 'Engineering', keys: 0 }],
  '/api/v1/security-pack/usb': { kpis: { devices: 248, usbDevices: 63, blocked: 12, readonly: 4, files: 318, filesBytes: 7340032000, allowed: 5 },
    policies: [{ groupId: null, groupName: null, updatedBy: 'admin@example.com', updatedAt: now, settings: { mode: 'readonly', auditFiles: true, message: 'USB drives are read-only. Ask IT for an approved encrypted drive.' } }] },
  '/api/v1/security-pack/usb/events': [
    { id: 1, at: now, assetId: 'a1', hostname: 'LT-1040', user: 'CORP\\ana', kind: 'file_written', deviceId: 'VID_0781&PID_5581\\4C530001231122108455', deviceName: 'SanDisk Ultra USB Device', path: 'E:\\Q3\\Budget 2027.xlsx', size: 284211, detail: null },
    { id: 2, at: now, assetId: 'a2', hostname: 'LT-1041', user: 'CORP\\raj', kind: 'blocked', deviceId: 'VID_0951&PID_1666\\E0D55EA574F6F3C0B9E1', deviceName: 'Kingston DataTraveler 3.0', path: null, size: null, detail: null }],
  '/api/v1/experience/summary': { devices: 248, score: 84, good: 196, fair: 37, poor: 15, bootMs: 31000, crashes: 64, hangs: 22, bugchecks: 5, bugcheckDevices: 3,
    trend: Array.from({ length: 30 }, (_, i) => ({ day: day(29 - i), score: 80 + Math.round(4 * Math.sin(i / 4)) + (i > 20 ? 2 : 0), devices: 240 })),
    issues: [{ kind: 'crash', devices: 41 }, { kind: 'boot', devices: 33 }, { kind: 'memory', devices: 19 }, { kind: 'disk', devices: 12 }, { kind: 'uptime', devices: 9 }, { kind: 'bugcheck', devices: 3 }],
    apps: [{ app: 'OUTLOOK.EXE', crashes: 18, hangs: 9, devices: 14, version: '16.0.18129', last: new Date().toISOString() }, { app: 'Teams.exe', crashes: 11, hangs: 4, devices: 9, version: '24295.605', last: new Date().toISOString() },
      { app: 'acad.exe', crashes: 6, hangs: 1, devices: 3, version: 'R24.3', last: new Date().toISOString() }] },
  '/api/v1/experience/devices': [
    dev(0, 41, 'poor', [issue('bugcheck', 'crit', '2 blue screen(s)', 30), issue('boot', 'crit', 'Slow start: 1 min 22 s until the desktop appears (median)', 12)]),
    dev(1, 66, 'fair', [issue('memory', 'crit', 'Memory 90 % or more in use 24 % of the time', 12), issue('crash', 'warn', '1 application crash(es)', 4)]),
    dev(2, 78, 'fair', [issue('disk', 'warn', 'System drive almost full: 8 % free', 8)]), dev(3, 100, 'good', [])],
  '/api/v1/experience/devices/00000000-0000-0000-0000-000000000042': {
    current: { ...dev(0, 58, 'poor', [issue('boot', 'crit', 'Slow start: 1 min 22 s until the desktop appears (median)', 12), issue('crash', 'crit', '3 application crash(es)', 12), issue('memory', 'warn', 'Memory 90 % or more in use 14 % of the time', 6), issue('disk', 'warn', 'System drive almost full: 9 % free', 8), issue('uptime', 'warn', 'Not restarted for 17 days', 2)]), bootMs: 82000, crashes: 3, bugchecks: 0, shutdowns: 0, systemFree: 9, uptimeDays: 17 },
    history: Array.from({ length: 14 }, (_, i) => ({ day: day(13 - i), score: 70 - i })),
    days: Array.from({ length: 7 }, (_, i) => ({ day: day(6 - i), cpuAvg: 22 + i, cpuHigh: i * 3, memAvg: 71 + i, memHigh: 20 + i * 9, samples: 480 })),
    events: [{ at: new Date().toISOString(), kind: 'crash', app: 'OUTLOOK.EXE', version: '16.0.18129', detail: 'Faulting module mso20win32client.dll, exception c0000005', ms: null, ms2: null },
      { at: new Date(Date.now() - 86400000).toISOString(), kind: 'boot', app: null, version: null, detail: null, ms: 82000, ms2: 31000 },
      { at: new Date(Date.now() - 2 * 86400000).toISOString(), kind: 'hang', app: 'EXCEL.EXE', version: '16.0.18129', detail: 'Stopped responding and was closed', ms: null, ms2: null }] },
  '/api/v1/sam/usage/device/00000000-0000-0000-0000-000000000042': { meteredSince: day(60), programs: [
    { exe: 'OUTLOOK.EXE', product: 'Microsoft Outlook', publisher: 'Microsoft Corporation', version: '16.0.18129', minutes: 5400, launches: 31, daysUsed: 21, lastUsed: day(0), users: ['CORP\\ana'] },
    { exe: 'acad.exe', product: 'AutoCAD', publisher: 'Autodesk, Inc.', version: 'R24.3', minutes: 95, launches: 2, daysUsed: 1, lastUsed: day(26), users: ['CORP\\ana'] }] },
  '/api/v1/sam/usage': { metering: true, unusedDays: 90, days: 30, meteredDevices: 231,
    apps: [{ exe: 'WINWORD.EXE', product: 'Microsoft Word', publisher: 'Microsoft Corporation', devices: 214, users: 220, minutes: 410000, launches: 9800, lastUsed: day(0), productId: 'p1', productName: 'Microsoft 365 Apps (installed)' },
      { exe: 'acad.exe', product: 'AutoCAD', publisher: 'Autodesk, Inc.', devices: 9, users: 9, minutes: 21000, launches: 260, lastUsed: day(1), productId: 'p2', productName: 'Autodesk AutoCAD' },
      { exe: 'Code.exe', product: 'Visual Studio Code', publisher: 'Microsoft Corporation', devices: 41, users: 41, minutes: 98000, launches: 1300, lastUsed: day(0), productId: null, productName: null }],
    products: [{ id: 'p2', name: 'Autodesk AutoCAD', publisher: 'Autodesk', rules: true, installed: 24, used: 9, unused: 13, recent: 2, notMetered: 0, unitCost: 1975 },
      { id: 'p3', name: 'Adobe Acrobat Pro / Standard', publisher: 'Adobe', rules: true, installed: 120, used: 71, unused: 38, recent: 6, notMetered: 5, unitCost: 239 },
      { id: 'p1', name: 'Microsoft 365 Apps (installed)', publisher: 'Microsoft', rules: true, installed: 231, used: 226, unused: 1, recent: 4, notMetered: 0, unitCost: null }] },
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 980 }, deviceScaleFactor: 1 });
page.on('pageerror', (e) => console.log('page error:', e.message));
await page.route('**/api/**', (route) => {
  const url = new URL(route.request().url());
  const body = url.pathname.endsWith('/auth/me') ? me : url.pathname.endsWith('/lifecycle/meta') ? null : samples[url.pathname] ?? [];
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
});
const names = ['shell', 'add-device', 'add-accessory', 'add-it-assets', 'patch-policy', 'patch-deploy', 'new-script', 'new-schedule', 'license-product', 'license',
  'web-app', 'deploy-rule', 'deploy-package', 'deploy-group', 'non-it-asset', 'deploy-job', 'deploy-offer', 'deploy-decide', 'experience', 'device-experience', 'license-usage', 'privilege', 'bitlocker', 'usb'];
for (const n of names) {
  await page.goto(`http://127.0.0.1:4175/?d=${n}`);
  await page.waitForTimeout(700);
  await page.screenshot({ path: join(out, `${n}.png`), fullPage: ['experience', 'device-experience', 'license-usage', 'privilege', 'bitlocker', 'usb'].includes(n) });
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
