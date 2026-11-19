// Screenshots of reference sites for design study (output in out/refs, which git ignores).
// Run:  cd scripts && node look-at.mjs <url> <name> [<url> <name> ...]
import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const out = fileURLToPath(new URL('../out/refs/', import.meta.url));
await mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge' });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
const args = process.argv.slice(2);
for (let i = 0; i < args.length; i += 2) {
  const [url, name] = [args[i], args[i + 1]];
  await page.goto(url, { waitUntil: 'networkidle', timeout: 60000 }).catch(() => {});
  await page.waitForTimeout(2500);
  await page.screenshot({ path: out + name + '-top.png' });
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  for (const [k, y] of [[1, 900], [2, 1800], [3, 2900]]) {
    if (y >= height) break;
    await page.evaluate(top => window.scrollTo(0, top), y);
    await page.waitForTimeout(1200);
    await page.screenshot({ path: out + `${name}-${k}.png` });
  }
  const style = await page.evaluate(() => {
    const pick = el => { if (!el) return null; const s = getComputedStyle(el); return { font: s.fontFamily, size: s.fontSize, weight: s.fontWeight, color: s.color, bg: s.backgroundColor, ls: s.letterSpacing }; };
    return { body: pick(document.body), h1: pick(document.querySelector('h1')), h2: pick(document.querySelector('h2')), button: pick(document.querySelector('a[class*=button], button')) };
  });
  console.log(name, JSON.stringify(style));
}
await browser.close();
