// Draws Vireo's images from the one master mark (src/Vireo/Assets/vireo-mark.svg):
//   src/Vireo/Assets/vireo.ico          the app icon, at every size Windows asks for
//   installer/images/wizard-*.png       the installer's side picture, at every size Inno Setup's modern wizard uses
//   installer/images/small-*.png        the installer's corner mark
// Uses the Microsoft Edge that Windows already has (through playwright-core), so nothing else is downloaded.
// Run:  cd scripts && npm install && node make-images.mjs
import { chromium } from 'playwright-core';
import { mkdir, readFile, writeFile } from 'node:fs/promises';

const root = new URL('../', import.meta.url);
const svg = await readFile(new URL('src/Vireo/Assets/vireo-mark.svg', root), 'utf8');
const font = (await readFile(new URL('src/Vireo/Fonts/PlusJakartaSans-Bold.ttf', root))).toString('base64');
const fontMedium = (await readFile(new URL('src/Vireo/Fonts/PlusJakartaSans-Medium.ttf', root))).toString('base64');
const fonts = `@font-face{font-family:J;font-weight:700;src:url(data:font/ttf;base64,${font})}
@font-face{font-family:J;font-weight:500;src:url(data:font/ttf;base64,${fontMedium})}`;
const sized = (size) => svg.replace(/width="\d+" height="\d+"/, '').replace('<svg ', `<svg width="${size}" height="${size}" `);

const browser = await chromium.launch({ channel: 'msedge' });
const page = await browser.newPage({ deviceScaleFactor: 1 });

// ---- The app icon ----
// Every size Windows uses: the taskbar and Start at 100, 125, 150 and 200% scaling, Explorer views, the installer.
const iconSizes = [16, 20, 24, 30, 32, 36, 40, 48, 64, 72, 96, 128, 256];
const pngs = [];
for (const size of iconSizes) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<html><body style="margin:0;background:transparent">${sized(size)}</body></html>`);
  pngs.push(await page.locator('svg').screenshot({ omitBackground: true }));
}
const header = Buffer.alloc(6);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(iconSizes.length, 4);
let offset = 6 + 16 * iconSizes.length;
const entries = iconSizes.map((size, i) => {
  const e = Buffer.alloc(16);
  e[0] = size >= 256 ? 0 : size;
  e[1] = size >= 256 ? 0 : size;
  e.writeUInt16LE(1, 4);
  e.writeUInt16LE(32, 6);
  e.writeUInt32LE(pngs[i].length, 8);
  e.writeUInt32LE(offset, 12);
  offset += pngs[i].length;
  return e;
});
await writeFile(new URL('src/Vireo/Assets/vireo.ico', root), Buffer.concat([header, ...entries, ...pngs]));
await page.setViewportSize({ width: 256, height: 256 });
await page.setContent(`<html><body style="margin:0;background:transparent">${sized(256)}</body></html>`);
await writeFile(new URL('src/Vireo/Assets/vireo-256.png', root), await page.locator('svg').screenshot({ omitBackground: true }));

// ---- The installer's pictures (dark, like the browser's own frame) ----
await mkdir(new URL('installer/images/', root), { recursive: true });
const wizard = [[202, 386], [269, 515], [336, 643], [430, 824], [534, 1022]];
for (const [w, h] of wizard) {
  await page.setViewportSize({ width: w, height: h });
  await page.setContent(`<html><head><style>${fonts}
    html,body{margin:0;width:${w}px;height:${h}px;overflow:hidden}
    body{background:#0F1716;font-family:J,sans-serif;display:flex;flex-direction:column;align-items:center;color:#E8F1EF;position:relative}
    .glow{position:absolute;inset:0;background:radial-gradient(ellipse 90% 55% at 20% -5%,rgba(23,58,51,.95),transparent 70%),radial-gradient(ellipse 80% 40% at 100% 110%,rgba(61,219,185,.10),transparent 70%)}
    .mark{position:relative;margin-top:${Math.round(h * 0.25)}px}
    .name{position:relative;font-weight:700;font-size:${Math.round(w * 0.17)}px;letter-spacing:-.02em;margin-top:${Math.round(h * 0.03)}px}
    .line{position:relative;font-weight:500;font-size:${Math.round(w * 0.06)}px;color:#8FA5A0;margin-top:${Math.round(h * 0.012)}px}
    .by{position:absolute;bottom:${Math.round(h * 0.05)}px;font-weight:500;font-size:${Math.round(w * 0.05)}px;color:#5E7570}
  </style></head><body><div class="glow"></div><div class="mark">${sized(Math.round(w * 0.42))}</div>
    <div class="name">Vireo</div><div class="line">Fast, private, ad-free</div><div class="by">Refora Technologies</div></body></html>`);
  await page.evaluate(() => document.fonts.ready);
  await writeFile(new URL(`installer/images/wizard-${w}x${h}.png`, root), await page.screenshot());
}
// The corner mark sits in the wizard's header next to two lines of text: drawn smaller than its box, in the middle, so it
// lines up with the text and keeps clear of the header's edges (an edge-to-edge badge looked too big and too high).
for (const s of [58, 77, 97, 124, 159]) {
  const inner = Math.round(s * 0.7);
  await page.setViewportSize({ width: s, height: s });
  await page.setContent(`<html><body style="margin:0;background:transparent;width:${s}px;height:${s}px;display:grid;place-items:center">${sized(inner)}</body></html>`);
  await writeFile(new URL(`installer/images/small-${s}.png`, root), await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: s, height: s } }));
}
await browser.close();
console.log('Drew vireo.ico, vireo-256.png and the installer images.');
