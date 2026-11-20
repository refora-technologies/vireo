// Screenshots of Vireo's website (desktop, laptop and phone) into out/site. The website lives beside this repository, in
// ../Website (or wherever VIREO_SITE says).
// Run:  cd scripts && node shoot-site.mjs
import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const folder = process.env.VIREO_SITE ?? fileURLToPath(new URL('../../Website/', import.meta.url));
const site = pathToFileURL(join(folder, 'public', 'index.html')).href;
const out = fileURLToPath(new URL('../out/site/', import.meta.url));
await mkdir(out, { recursive: true });
const b = await chromium.launch({ channel: 'msedge' });
for (const [name, w, h] of [['desktop', 1440, 900], ['laptop', 1280, 720], ['phone', 390, 844]]) {
  const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: name === 'phone' ? 2 : 1 });
  await p.goto(site);
  await p.evaluate(() => { document.querySelectorAll('.reveal').forEach(e => e.classList.add('seen')); return document.fonts.ready; });
  await p.waitForTimeout(3600);
  await p.screenshot({ path: out + name + '-top.png' });
  await p.screenshot({ path: out + name + '-full.png', fullPage: true });
  await p.close();
}
await b.close();
console.log('Shot the site.');
