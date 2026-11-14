// Console screenshots and colour-contrast check (axe-core) of every page, light and dark, for design review.
// Usage: node capture.mjs <baseUrl> <email> <outDir>   (password in ZQ_SCREENS_PW)
import { chromium } from 'playwright';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';

const [base, email, out] = process.argv.slice(2);
const pw = process.env.ZQ_SCREENS_PW;
const require = createRequire(import.meta.url);
const axeSource = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
mkdirSync(out, { recursive: true });

const PAGES = [
  ['dashboard', '/'], ['discovery', '/discovery'], ['assets', '/assets'], ['non-it', '/non-it'], ['hardware', '/hardware'],
  ['accessories', '/accessories'], ['software', '/software'], ['network', '/network'], ['experience', '/experience'],
  ['agents', '/agents'], ['activity', '/activity'], ['remote', '/remote'], ['cloud', '/cloud'], ['lifecycle', '/lifecycle'],
  ['patch', '/modules/patch'], ['scripts', '/modules/scripts'], ['vulnerabilities', '/vulnerabilities'], ['security', '/security'],
  ['deploy', '/modules/deploy'], ['licenses', '/modules/licenses'], ['webapps', '/modules/webapps'],
  ['privilege', '/security-pack/privilege'], ['bitlocker', '/security-pack/bitlocker'], ['usb', '/security-pack/usb'],
  ['reports', '/reports'], ['integrations', '/integrations'], ['licensing', '/licensing'], ['audit', '/audit'],
  ['users', '/users'], ['backup', '/backup'], ['settings', '/settings'], ['account', '/account'],
];
// Dialogs opened by a button on a page.
const DIALOGS = [
  ['dlg-new-scan', '/discovery', 'New scan'],
  ['dlg-cloud-add', '/cloud', 'Add connection'],
];

const report = [];
const browser = await chromium.launch();
for (const scheme of ['light', 'dark']) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, colorScheme: scheme });
  const page = await ctx.newPage();
  // Sign in through the login page.
  await page.goto(base + '/', { waitUntil: 'networkidle' });
  await page.screenshot({ path: `${out}/${scheme}-00-login.png`, fullPage: true });
  await page.fill('input[name=username]', email);
  await page.fill('input[name=password]', pw);
  await page.press('input[name=password]', 'Enter');
  await page.waitForTimeout(4000);

  const check = async (name) => {
    await page.addScriptTag({ content: axeSource });
    const r = await page.evaluate(async () => {
      // eslint-disable-next-line no-undef
      const res = await axe.run(document, { runOnly: ['color-contrast'] });
      return res.violations.flatMap((v) => v.nodes.map((n) => ({ target: n.target.join(' '), text: (n.html || '').slice(0, 140), msg: (n.any[0]?.message || '').slice(0, 200) })));
    });
    report.push({ scheme, page: name, count: r.length, items: r.slice(0, 25) });
  };

  for (const [name, path] of PAGES) {
    try {
      await page.goto(base + path, { waitUntil: 'networkidle', timeout: 60000 });
      await page.waitForTimeout(1500);
      await page.screenshot({ path: `${out}/${scheme}-${name}.png`, fullPage: true });
      await check(name);
    } catch (e) { report.push({ scheme, page: name, error: String(e).slice(0, 300) }); }
  }
  for (const [name, path, button] of DIALOGS) {
    try {
      await page.goto(base + path, { waitUntil: 'networkidle', timeout: 60000 });
      await page.getByRole('button', { name: button }).first().click();
      await page.waitForTimeout(1500);
      await page.screenshot({ path: `${out}/${scheme}-${name}.png`, fullPage: true });
      await check(name);
      // Cloud dialog: every provider tab / choice, if any.
      if (name === 'dlg-cloud-add') {
        const kinds = await page.locator('.modal-card select option').allTextContents().catch(() => []);
        let i = 0;
        for (const k of kinds.slice(0, 10)) {
          await page.locator('.modal-card select').first().selectOption({ label: k }).catch(() => {});
          await page.waitForTimeout(500);
          await page.screenshot({ path: `${out}/${scheme}-${name}-${++i}.png`, fullPage: true });
        }
      }
    } catch (e) { report.push({ scheme, page: name, error: String(e).slice(0, 300) }); }
  }
  await ctx.close();
}
await browser.close();
writeFileSync(`${out}/contrast.json`, JSON.stringify(report, null, 1));
const lines = report.map((r) => r.error ? `${r.scheme} ${r.page}: ERROR ${r.error}` : `${r.scheme} ${r.page}: ${r.count} contrast issue(s)` + r.items.map((i) => `\n    ${i.target} | ${i.msg}`).join(''));
writeFileSync(`${out}/contrast.txt`, lines.join('\n'));
console.log(lines.join('\n'));
