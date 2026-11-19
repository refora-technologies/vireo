// Checks Vireo's website as Netlify will serve it: its public/ folder with the headers from its netlify.toml. Loads the
// pages in Edge and reports anything the security policy blocks, missing files, and how the rules files are served (type,
// caching, CORS: a page on another site must be able to read them too), and that the published rules are exactly the
// signed master copy in this repository.
// The website lives beside this repository, in ../vireo-website (or wherever VIREO_SITE says).
// Run:  cd scripts && node check-site.mjs
import { chromium } from 'playwright-core';
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const folder = process.env.VIREO_SITE ?? fileURLToPath(new URL('../../vireo-website/', import.meta.url));
const root = join(folder, 'public') + sep;
const toml = await readFile(join(folder, 'netlify.toml'), 'utf8');

// The [[headers]] blocks: { for, values }.
const rules = [];
for (const block of toml.split('[[headers]]').slice(1)) {
  const path = /for\s*=\s*"([^"]+)"/.exec(block)[1];
  const values = {};
  for (const m of block.matchAll(/^\s*([A-Za-z-]+)\s*=\s*"([^"]*)"\s*$/gm)) if (m[1] !== 'for') values[m[1]] = m[2];
  rules.push({ path, values });
}
const matches = (pattern, path) => new RegExp('^' + pattern.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*') + '$').test(path);
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png', '.woff2': 'font/woff2', '.json': 'application/json', '.txt': 'text/plain' };

const server = createServer(async (req, res) => {
  let path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (path.endsWith('/')) path += 'index.html';
  const file = normalize(join(root, path));
  let status = 200, target = file;
  try { if (!file.startsWith(root) || !(await stat(file)).isFile()) throw 0; } catch { status = 404; target = join(root, '404.html'); }
  const headers = { 'Content-Type': TYPES[extname(target)] ?? 'application/octet-stream' };
  for (const r of rules) if (matches(r.path, path)) Object.assign(headers, r.values);
  // Served here over plain http: "upgrade to https" would send every file to an https server that is not there.
  if (headers['Content-Security-Policy']) headers['Content-Security-Policy'] = headers['Content-Security-Policy'].replace(/;\s*upgrade-insecure-requests/, '');
  delete headers['Strict-Transport-Security'];
  res.writeHead(status, headers);
  res.end(await readFile(target));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;

let problems = 0;
const say = (ok, what) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`); if (!ok) problems++; };

const browser = await chromium.launch({ channel: 'msedge' });
const page = await browser.newPage();
const blocked = [];
page.on('console', m => { if (m.type() === 'error') blocked.push(m.text()); });
page.on('pageerror', e => blocked.push(e.message));
const failed = [];
page.on('response', r => { if (r.status() >= 400 && !r.url().endsWith('/no-such-page')) failed.push(`${r.status()} ${r.url()}`); });

await page.goto(base + '/', { waitUntil: 'networkidle' });
await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
await page.waitForTimeout(800);
const state = await page.evaluate(() => ({
  js: document.documentElement.classList.contains('js'),
  shown: [...document.querySelectorAll('.reveal')].filter(e => e.classList.contains('seen')).length,
  all: document.querySelectorAll('.reveal').length,
  font: document.fonts.check('600 16px Jakarta'),
}));
say(blocked.length === 0, `the home page runs under the security policy with nothing blocked${blocked.length ? ': ' + blocked.join(' | ') : ''}`);
say(failed.length === 0, `every file the page asks for exists${failed.length ? ': ' + failed.join(', ') : ''}`);
say(state.js && state.shown > 0, `site.js runs (sections fade in: ${state.shown} of ${state.all} seen after scrolling)`);
say(state.font, 'the site\'s own font loads');

const missing = await page.goto(base + '/no-such-page');
say(missing.status() === 404 && (await page.title()).startsWith('Not found'), 'an unknown address gets the 404 page');

// The rules files, read from a page on another site as a browser would (CORS), and their headers.
const elsewhere = createServer((_, res) => { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end('<!doctype html><title>another site</title>'); });
await new Promise(r => elsewhere.listen(0, '127.0.0.1', r));
const other = await browser.newPage();
await other.goto(`http://127.0.0.1:${elsewhere.address().port}/`); // another origin than the site
const fetched = await other.evaluate(async url => {
  const r = await fetch(url);
  return { status: r.status, type: r.headers.get('content-type'), cache: r.headers.get('cache-control'), version: (await r.json()).version };
}, base + '/rules/v1/youtube.json');
say(fetched.status === 200 && fetched.type.startsWith('application/json'), `rules/v1/youtube.json is served as JSON (version ${fetched.version})`);
say(/max-age=300/.test(fetched.cache ?? ''), `and cached for five minutes only (${fetched.cache})`);
say(true, 'and readable from a page on another site (CORS)');
try {
  const sig = await fetch(base + '/rules/v1/youtube.json.minisig');
  console.log(sig.status === 200 ? `PASS  its signature is published (${sig.headers.get('content-type')})` : 'NOTE  no signature published yet: Vireo ignores the file until it is signed');
} catch { /* no network */ }
// The published rules must be exactly the signed master copy in this repository (byte for byte, signature included).
for (const name of ['youtube.json', 'youtube.json.minisig']) {
  const published = await readFile(join(root, 'rules', 'v1', name)).catch(() => null);
  const master = await readFile(new URL(`../src/Vireo/Assets/rules/${name}`, import.meta.url)).catch(() => null);
  say(published !== null && master !== null && Buffer.compare(published, master) === 0, `rules/v1/${name} is exactly the master copy in the app repository`);
}

await browser.close();
server.close();
elsewhere.close();
console.log(problems ? `${problems} problem(s)` : 'All good.');
process.exit(problems ? 1 : 0);
