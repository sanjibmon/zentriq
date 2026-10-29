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

// Decorative "spider net": concentric rings joined by spokes, plus a few outer nodes linked into the web.
// Lines are 2 px or wider and fairly opaque because Burn stretches the splash with a plain StretchBlt at
// 125-200 % display scaling, which drops or smears hairlines (that is what made the old splash look faded).
function spiderNet(cx, cy, radius, rings, spokes, color, opts = {}) {
  const { lineOpacity = 0.55, width = 2, nodeR = 3.2, rot = -0.2, wobble = 0.08, seed = 7 } = opts;
  let x = seed;
  const rnd = () => ((x = (x * 9301 + 49297) % 233280) / 233280);
  const pts = [];
  for (let r = 1; r <= rings; r++) {
    const ring = [];
    for (let k = 0; k < spokes; k++) {
      const a = rot + (k / spokes) * Math.PI * 2;
      const rr = radius * (r / rings) * (1 + (rnd() - 0.5) * wobble);
      ring.push([cx + Math.cos(a) * rr, cy + Math.sin(a) * rr]);
    }
    pts.push(ring);
  }
  let g = '';
  const L = (a, b, o, w = width) => { g += `<line x1="${a[0].toFixed(1)}" y1="${a[1].toFixed(1)}" x2="${b[0].toFixed(1)}" y2="${b[1].toFixed(1)}" stroke="${color}" stroke-opacity="${o}" stroke-width="${w}" stroke-linecap="round"/>`; };
  for (let k = 0; k < spokes; k++) L([cx, cy], pts[rings - 1][k], lineOpacity * 0.9);
  pts.forEach((ring, i) => ring.forEach((p, k) => L(p, ring[(k + 1) % spokes], lineOpacity * (0.55 + 0.45 * (i + 1) / rings))));
  // outer nodes connected to the rim (the "network" reaching out)
  const outer = [];
  for (let k = 0; k < spokes; k += 2) {
    const a = rot + ((k + 0.5) / spokes) * Math.PI * 2;
    const rr = radius * (1.35 + rnd() * 0.35);
    const o = [cx + Math.cos(a) * rr, cy + Math.sin(a) * rr];
    outer.push(o);
    L(o, pts[rings - 1][k], lineOpacity * 0.7);
    L(o, pts[rings - 1][(k + 1) % spokes], lineOpacity * 0.5);
  }
  outer.forEach((o, i) => L(o, outer[(i + 1) % outer.length], lineOpacity * 0.35));
  let dots = `<circle cx="${cx}" cy="${cy}" r="${nodeR * 1.8}" fill="${color}"/>`;
  pts.forEach((ring, i) => ring.forEach((p, k) => {
    const big = (i === rings - 1 && k % 2 === 0) || (i === 0);
    dots += `<circle cx="${p[0].toFixed(1)}" cy="${p[1].toFixed(1)}" r="${big ? nodeR * 1.25 : nodeR * 0.8}" fill="${color}" fill-opacity="${big ? 1 : 0.85}"/>`;
  }));
  outer.forEach((o) => { dots += `<circle cx="${o[0].toFixed(1)}" cy="${o[1].toFixed(1)}" r="${nodeR * 1.5}" fill="${color}" fill-opacity="0.35"/><circle cx="${o[0].toFixed(1)}" cy="${o[1].toFixed(1)}" r="${nodeR}" fill="${color}"/>`; });
  return g + dots;
}
const svgLayer = (w, h, inner) => `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" style="position:absolute;inset:0">${inner}</svg>`;

const page = (w, h, body, bg = '#fff') => `<!doctype html><html><head><style>
  *{box-sizing:border-box} html,body{margin:0;width:${w}px;height:${h}px;overflow:hidden;background:${bg};font-family:Inter,'Segoe UI',sans-serif;-webkit-font-smoothing:antialiased}
  .wm{font-weight:700;letter-spacing:-.02em;color:#fff} .tag{color:#9FD8E0;font-weight:500;letter-spacing:.06em;text-transform:uppercase}
</style></head><body>${body}</body></html>`;

// Setup shows only the brand: "Zentriq" and the tagline.
const product = { server: { mark: MARK, title: 'Zentriq', tag: 'Asset Intelligence', bg: '#061C26', bg2: '#0E5566', accent: '#5EE6F2' },
                  agent: { mark: AGENT, title: 'Zentriq', tag: 'Asset Intelligence', bg: '#0C1B33', bg2: '#1F4A7A', accent: '#F59E0B' } };
// Wizard bitmaps are rendered at 2x: Windows scales them to the dialog, so they stay sharp at 150-200 % display scaling.
const S = 2;

function dialog(p) { // 493x312 (x2): brand panel on the left 164 px; the rest stays white for the wizard text
  return page(493 * S, 312 * S, `
    <div style="position:absolute;left:0;top:0;width:${164 * S}px;height:${312 * S}px;background:linear-gradient(170deg, ${p.bg} 0%, ${p.bg2} 100%)"></div>
    <div style="position:absolute;left:0;top:0;width:${164 * S}px;height:${312 * S}px;overflow:hidden">${svgLayer(164 * S, 312 * S, spiderNet(118 * S, 252 * S, 78 * S, 3, 10, p.accent, { lineOpacity: 0.45, width: 1.6 * S, nodeR: 2.4 * S, seed: 11 }))}</div>
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

function splash(p) { // 460x240 Burn splash screen. Burn stretches it by the display scale, so everything is bold.
  const W = 460, H = 240;
  return page(W, H, `
    <div style="position:absolute;inset:0;background:radial-gradient(120% 140% at 85% 60%, ${p.bg2} 0%, ${p.bg} 62%)"></div>
    ${svgLayer(W, H, spiderNet(372, 128, 84, 3, 12, p.accent, { lineOpacity: 0.6, width: 2, nodeR: 3.4, seed: 3 }))}
    <div style="position:absolute;left:0;top:0;bottom:0;width:250px;background:linear-gradient(90deg, ${p.bg} 55%, rgba(0,0,0,0))"></div>
    <div style="position:absolute;left:34px;top:62px"><img src="${dataUri(p.mark)}" width="72" height="72"></div>
    <div class="wm" style="position:absolute;left:34px;top:146px;font-size:44px;font-weight:800;line-height:1;text-shadow:0 2px 0 rgba(0,0,0,.25)">${p.title}</div>
    <div style="position:absolute;left:36px;top:196px;font-size:14px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:${p.accent}">${p.tag}</div>`, p.bg);
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
  await shot(splash(product.server), 460, 240, 'splash.png');
  await shot(dialog(product.agent), 493 * S, 312 * S, 'AgentDialog.png');
  await shot(banner(product.agent), 493 * S, 58 * S, 'AgentBanner.png');
  await b.close();
  console.log('rendered to ' + out);
})();
