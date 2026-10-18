// Code 128 (set B) barcode as SVG: readable by every USB / Bluetooth / phone barcode scanner. Printable ASCII only.
const PATTERNS = [
  '212222', '222122', '222221', '121223', '121322', '131222', '122213', '122312', '132212', '221213', '221312', '231212', '112232', '122132',
  '122231', '113222', '123122', '123221', '223211', '221132', '221231', '213212', '223112', '312131', '311222', '321122', '321221', '312212',
  '322112', '322211', '212123', '212321', '232121', '111323', '131123', '131321', '112313', '132113', '132311', '211313', '231113', '231311',
  '112133', '112331', '132131', '113123', '113321', '133121', '313121', '211331', '231131', '213113', '213311', '213131', '311123', '311321',
  '331121', '312113', '312311', '332111', '314111', '221411', '431111', '111224', '111422', '121124', '121421', '141122', '141221', '112214',
  '112412', '122114', '122411', '142112', '142211', '241211', '221114', '413111', '241112', '134111', '111242', '121142', '121241', '114212',
  '124112', '124211', '411212', '421112', '421211', '212141', '214121', '412121', '111143', '111341', '131141', '114113', '114311', '411113',
  '411311', '113141', '114131', '311141', '411131', '211412', '211214', '211232', '2331112',
];
const START_B = 104;
const STOP = 106;

/** Bar widths (alternating bar / space, starting with a bar) for the text, or null if it has characters Code 128 B cannot hold. */
export function code128Widths(text: string): number[] | null {
  const codes: number[] = [START_B];
  for (const ch of text) {
    const c = ch.charCodeAt(0);
    if (c < 32 || c > 126) return null;
    codes.push(c - 32);
  }
  let sum = START_B;
  for (let i = 1; i < codes.length; i++) sum += codes[i]! * i;
  codes.push(sum % 103, STOP);
  return codes.flatMap((c) => PATTERNS[c]!.split('').map(Number));
}

/** SVG markup of the barcode with the text underneath (module = width of the narrowest bar in px). */
export function code128Svg(text: string, opts: { module?: number; height?: number; label?: boolean } = {}): string {
  const widths = code128Widths(text);
  if (!widths) return '';
  const m = opts.module ?? 2, h = opts.height ?? 56, quiet = 10 * m;
  const total = widths.reduce((a, w) => a + w, 0) * m + quiet * 2;
  let x = quiet;
  const bars: string[] = [];
  widths.forEach((w, i) => { if (i % 2 === 0) bars.push(`<rect x="${x}" y="0" width="${w * m}" height="${h}"/>`); x += w * m; });
  const esc = text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
  const label = opts.label === false ? '' : `<text x="${total / 2}" y="${h + 15}" text-anchor="middle" font-family="Consolas, monospace" font-size="13">${esc}</text>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${total}" height="${h + (opts.label === false ? 0 : 20)}" viewBox="0 0 ${total} ${h + (opts.label === false ? 0 : 20)}"><rect width="100%" height="100%" fill="#fff"/><g fill="#000">${bars.join('')}</g>${label}</svg>`;
}
