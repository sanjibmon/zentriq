// Renders the installer/brand artwork from the SVG sources with headless Chromium (Playwright).
// Developer tool only (not part of the build): node render.js <outDir>; then python3 build-assets.py <outDir>.
const path = require('path');
const fs = require('fs');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const out = process.argv[2] || path.join(__dirname, 'out');
fs.mkdirSync(out, { recursive: true });
const svg = (n) => fs.readFileSync(path.join(__dirname, n), 'utf8');
const MARK = svg('zentriq-mark.svg'), SMALL = svg('zentriq-mark-small.svg'), AGENT = svg('zentriq-agent.svg');
const dataUri = (s) => 'data:image/svg+xml;base64,' + Buffer.from(s).toString('base64');

// Decorative network: nodes and links (deterministic).
function network(w, h, seed, color, opacity) {
  let x = seed;
  const rnd = () => ((x = (x * 9301 + 49297) % 233280) / 233280);
  const nodes = Array.from({ length: 14 }, () => [rnd() * w, rnd() * h]);
  let lines = '';
  nodes.forEach((a, i) => nodes.slice(i + 1).forEach((b) => {
    const d = Math.hypot(a[0] - b[0], a[1] - b[1]);
    if (d < w * 0.42) lines += `<line x1="${a[0]}" y1="${a[1]}" x2="${b[0]}" y2="${b[1]}" stroke="${color}" stroke-opacity="${opacity * (1 - d / (w * 0.42))}" stroke-width="1"/>`;
  }));
  const dots = nodes.map(([cx, cy], i) => `<circle cx="${cx}" cy="${cy}" r="${i % 4 === 0 ? 3 : 1.8}" fill="${color}" fill-opacity="${i % 4 === 0 ? opacity * 2.2 : opacity * 1.6}"/>`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" style="position:absolute;inset:0">${lines}${dots}</svg>`;
}

const page = (w, h, body, bg = '#fff') => `<!doctype html><html><head><style>
  *{box-sizing:border-box} html,body{margin:0;width:${w}px;height:${h}px;overflow:hidden;background:${bg};font-family:Inter,'Segoe UI',sans-serif;-webkit-font-smoothing:antialiased}
  .wm{font-weight:700;letter-spacing:-.02em;color:#fff} .tag{color:#9FD8E0;font-weight:500;letter-spacing:.06em;text-transform:uppercase}
</style></head><body>${body}</body></html>`;

// Setup shows only the brand: "Zentriq" and the tagline.
const product = { server: { mark: MARK, title: 'Zentriq', tag: 'IT Asset Intelligence', bg: '#0A2E3A', accent: '#5EE6F2' },
                  agent: { mark: AGENT, title: 'Zentriq', tag: 'IT Asset Intelligence', bg: '#10243F', accent: '#F59E0B' } };
// Wizard bitmaps are rendered at 2x: Windows scales them to the dialog, so they stay sharp at 150-200 % display scaling.
const S = 2;

function dialog(p) { // 493x312 (x2): brand panel on the left 164 px; the rest stays white for the wizard text
  return page(493 * S, 312 * S, `
    <div style="position:absolute;left:0;top:0;width:${164 * S}px;height:${312 * S}px;background:${p.bg}"></div>
    <div style="position:absolute;left:${22 * S}px;top:${34 * S}px"><img src="${dataUri(p.mark)}" width="${56 * S}" height="${56 * S}"></div>
    <div class="wm" style="position:absolute;left:${22 * S}px;top:${104 * S}px;font-size:${28 * S}px;line-height:1">${p.title}</div>
    <div style="position:absolute;left:${23 * S}px;top:${138 * S}px;font-size:${10.5 * S}px;font-weight:600;letter-spacing:.02em;color:#CFEFF4">${p.tag}</div>
    <div style="position:absolute;left:${22 * S}px;top:${158 * S}px;width:${28 * S}px;height:${3 * S}px;background:${p.accent}"></div>`);
}

function banner(p) { // 493x58 (x2): title/description text sits on the left; mark at the right
  return page(493 * S, 58 * S, `
    <div style="position:absolute;right:${14 * S}px;top:${11 * S}px"><img src="${dataUri(p.mark)}" width="${36 * S}" height="${36 * S}"></div>
    <div style="position:absolute;left:0;right:0;bottom:0;height:${2 * S}px;background:${p.bg}"></div>`);
}

function splash(p) { // 420x220 Burn splash screen (shown at its native size)
  return page(420, 220, `
    <div style="position:absolute;inset:0;background:${p.bg}"></div>
    <div style="position:absolute;left:40px;top:62px"><img src="${dataUri(p.mark)}" width="96" height="96"></div>
    <div class="wm" style="position:absolute;left:156px;top:76px;font-size:42px;line-height:1">${p.title}</div>
    <div style="position:absolute;left:158px;top:128px;font-size:15px;font-weight:600;letter-spacing:.02em;color:#CFEFF4">${p.tag}</div>
    <div style="position:absolute;left:158px;top:154px;width:40px;height:4px;background:${p.accent}"></div>`, p.bg);
}

const icon = (s, size) => page(size, size, `<img src="${dataUri(s)}" width="${size}" height="${size}" style="display:block">`, 'transparent');

(async () => {
  const b = await chromium.launch();
  async function shot(html, w, h, file, transparent = false) {
    const pg = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
    await pg.setContent(html);
    await pg.waitForTimeout(150);
    await pg.screenshot({ path: path.join(out, file), omitBackground: transparent });
    await pg.close();
  }
  for (const s of [16, 20, 24]) await shot(icon(SMALL, s), s, s, `mark-${s}.png`, true);
  for (const s of [32, 40, 48, 64, 96, 128, 256, 512]) await shot(icon(MARK, s), s, s, `mark-${s}.png`, true);
  for (const s of [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]) await shot(icon(AGENT, s), s, s, `agent-${s}.png`, true);
  await shot(dialog(product.server), 493 * S, 312 * S, 'WixUIDialog.png');
  await shot(banner(product.server), 493 * S, 58 * S, 'WixUIBanner.png');
  await shot(splash(product.server), 420, 220, 'splash.png');
  await shot(dialog(product.agent), 493 * S, 312 * S, 'AgentDialog.png');
  await shot(banner(product.agent), 493 * S, 58 * S, 'AgentBanner.png');
  await b.close();
  console.log('rendered to ' + out);
})();
