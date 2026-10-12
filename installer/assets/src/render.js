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

const product = { server: { mark: MARK, title: 'Zentriq', sub: 'Web Console', tag: 'IT asset intelligence', g1: '#0B3A4A', g2: '#062430', accent: '#13A3B4' },
                  agent: { mark: AGENT, title: 'Zentriq', sub: 'Agent', tag: 'Device inventory', g1: '#173A63', g2: '#0B1C33', accent: '#F59E0B' } };

function dialog(p) { // 493x312: art on the left 164 px; the rest stays white for the wizard text
  return page(493, 312, `
    <div style="position:absolute;left:0;top:0;width:164px;height:312px;background:linear-gradient(160deg,${p.g1},${p.g2});overflow:hidden">
      ${network(164, 312, p === product.agent ? 7 : 3, '#8BF0FA', 0.16)}
      <div style="position:absolute;left:22px;top:28px"><img src="${dataUri(p.mark)}" width="60" height="60"></div>
      <div class="wm" style="position:absolute;left:22px;top:100px;font-size:26px;line-height:1">${p.title}</div>
      <div style="position:absolute;left:22px;top:131px;font-size:15px;font-weight:600;color:#DDF7FA">${p.sub}</div>
      <div class="tag" style="position:absolute;left:22px;top:156px;font-size:9.5px">${p.tag}</div>
      <div style="position:absolute;left:22px;bottom:22px;right:22px;height:3px;border-radius:2px;background:linear-gradient(90deg,${p.accent},transparent)"></div>
    </div>`);
}

function banner(p) { // 493x58: title/description text sits on the left; mark at the right
  return page(493, 58, `
    <div style="position:absolute;right:0;top:0;width:150px;height:58px;background:linear-gradient(90deg,#fff,${p.g1}14)"></div>
    <div style="position:absolute;right:14px;top:9px"><img src="${dataUri(p.mark)}" width="40" height="40"></div>
    <div style="position:absolute;left:0;right:0;bottom:0;height:3px;background:linear-gradient(90deg,${p.accent},${p.g1})"></div>`);
}

function splash(p) { // 420x220 Burn splash screen
  return page(420, 220, `
    <div style="position:absolute;inset:0;background:radial-gradient(120% 140% at 0% 0%,${p.g1},${p.g2})">${network(420, 220, 11, '#8BF0FA', 0.13)}</div>
    <div style="position:absolute;left:36px;top:48px"><img src="${dataUri(p.mark)}" width="84" height="84"></div>
    <div class="wm" style="position:absolute;left:138px;top:54px;font-size:36px;line-height:1">${p.title}</div>
    <div style="position:absolute;left:139px;top:96px;font-size:18px;font-weight:600;color:#DDF7FA">${p.sub}</div>
    <div class="tag" style="position:absolute;left:139px;top:124px;font-size:10.5px">${p.tag} &amp; operations</div>
    <div style="position:absolute;left:36px;bottom:26px;font-size:12px;color:#BFE7EC">Starting setup…</div>
    <div style="position:absolute;left:0;right:0;bottom:0;height:5px;background:linear-gradient(90deg,${p.accent},#8BF0FA)"></div>`, p.g2);
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
  await shot(dialog(product.server), 493, 312, 'WixUIDialog.png');
  await shot(banner(product.server), 493, 58, 'WixUIBanner.png');
  await shot(splash(product.server), 420, 220, 'splash.png');
  await shot(dialog(product.agent), 493, 312, 'AgentDialog.png');
  await shot(banner(product.agent), 493, 58, 'AgentBanner.png');
  await b.close();
  console.log('rendered to ' + out);
})();
