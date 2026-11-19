// Local server for test media: byte ranges, correct types, and ?throttle=KBps to slow a
// download down so progress can be seen and tested.

import { createReadStream, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The sample pages and media sit beside this file, wherever it is run from.
const root = resolve(dirname(fileURLToPath(import.meta.url)), 'media');
const port = Number(process.env.PORT ?? 4173);

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.m3u8': 'application/vnd.apple.mpegurl',
  '.mpd': 'application/dash+xml',
  '.ts': 'video/mp2t',
  '.m4s': 'video/iso.segment',
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
  '.mp3': 'audio/mpeg',
  '.jpg': 'image/jpeg',
  '.png': 'image/png',
  '.bin': 'application/octet-stream',
};

// A moving live stream made from live-src: a 4-piece window that advances every 2 s.
// Each viewer session (?s=...) has its own clock; without one, the clock restarts after 8 idle seconds.
const liveClocks = new Map();
let liveStart = Date.now();
let liveLast = 0;
function liveDyn(res, session, len) {
  if (session) {
    if (!liveClocks.has(session)) liveClocks.set(session, Date.now());
    liveStart = liveClocks.get(session);
  } else if (Date.now() - liveLast > 8000) {
    liveStart = Date.now();
  }
  liveLast = Date.now();
  const total = Math.min(30, Math.max(5, Number(len) || 30));
  const head = Math.min(total - 1, 3 + Math.floor((Date.now() - liveStart) / 2000));
  const first = Math.max(0, head - 3);
  const lines = ['#EXTM3U', '#EXT-X-VERSION:3', '#EXT-X-TARGETDURATION:2', `#EXT-X-MEDIA-SEQUENCE:${first}`];
  for (let i = first; i <= head; i++) lines.push('#EXTINF:2.000000,', `/live-src/l${String(i).padStart(3, '0')}.ts`);
  if (head === total - 1) lines.push('#EXT-X-ENDLIST');
  res.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl', 'cache-control': 'no-store' });
  res.end(`${lines.join('\n')}\n`);
}

let flakyServed = false;

function handler(req, res) {
  const url = new URL(req.url ?? '/', `http://localhost:${port}`);
  if (url.pathname === '/live-dyn/index.m3u8') return void liveDyn(res, url.searchParams.get('s'), url.searchParams.get('len'));
  // Every 8th piece of the long stream answers late, like a real network: pieces then finish
  // out of order and the downloader's look-ahead window fills up.
  const piece = /\/live-src\/l(\d{3})\.ts$/.exec(url.pathname);
  if (piece && Number(piece[1]) % 8 === 0 && !req.headers['x-no-delay']) {
    req.headers['x-no-delay'] = '1';
    return void setTimeout(() => handler(req, res), 1500);
  }
  // /flaky/ is the long stream, but piece 12 answers 401 once per playlist load (a token hiccup
  // in the middle of a download, as seen on real CDNs).
  if (url.pathname.startsWith('/flaky/')) {
    if (url.pathname.endsWith('.m3u8')) flakyServed = false;
    else if (url.pathname.endsWith('l012.ts') && !flakyServed) {
      flakyServed = true;
      return void res.writeHead(401).end('Unauthorized');
    }
    url.pathname = url.pathname.replace('/flaky/', '/live-src/');
    req.headers['x-no-delay'] = '1';
  }
  // /guarded/ serves the HLS test stream only to requests that look like the page's own player:
  // Referer from a test page, and no foreign Origin. Many real video servers do this.
  let pathname = url.pathname;
  if (pathname.startsWith('/guarded/')) {
    const allowed = `http://127.0.0.1:${port}`;
    const okReferer = (req.headers.referer ?? '').startsWith(`${allowed}/pages/`);
    const okOrigin = !req.headers.origin || req.headers.origin === allowed;
    if (!okReferer || !okOrigin) return void res.writeHead(403).end('Forbidden');
    pathname = pathname.replace('/guarded/', '/hls/');
  }
  // A big file of any size without one on disk: /generated/314572800.bin. Each byte comes from its position, so any range
  // the downloader asks for is the same every time.
  const generated = /^\/generated\/(\d+)\.bin$/.exec(pathname);
  if (generated) return void serveGenerated(req, res, url, Number(generated[1]));

  const path = normalize(join(root, decodeURIComponent(pathname)));
  if (!path.startsWith(root)) return void res.writeHead(403).end();
  let size;
  try {
    const st = statSync(path);
    if (!st.isFile()) throw new Error('not a file');
    size = st.size;
  } catch {
    return void res.writeHead(404).end('Not found');
  }

  const headers = {
    'content-type': TYPES[extname(path)] ?? 'application/octet-stream',
    'accept-ranges': 'bytes',
    'access-control-allow-origin': '*',
    'cache-control': 'no-store',
  };
  let start = 0;
  let end = size - 1;
  let status = 200;
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range ?? '');
  if (range) {
    start = range[1] ? Number(range[1]) : Math.max(0, size - Number(range[2]));
    end = range[1] && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    status = 206;
    headers['content-range'] = `bytes ${start}-${end}/${size}`;
  }
  headers['content-length'] = String(end - start + 1);
  res.writeHead(status, headers);
  if (req.method === 'HEAD') return void res.end();

  const kbps = Number(url.searchParams.get('throttle'));
  const stream = createReadStream(path, { start, end, highWaterMark: kbps ? Math.max(1024, (kbps * 1024) / 10) : 64 * 1024 });
  if (!kbps) return void stream.pipe(res);
  // Throttled: one chunk every 100 ms.
  stream.on('data', (chunk) => {
    stream.pause();
    res.write(chunk);
    setTimeout(() => stream.resume(), 100);
  });
  stream.on('end', () => res.end());
  res.on('close', () => stream.destroy());
}

// Serves `size` made-up bytes (byte i is i mod 251), with ranges and ?throttle=KBps like a real file.
function serveGenerated(req, res, url, size) {
  let start = 0;
  let end = size - 1;
  let status = 200;
  const headers = { 'content-type': 'application/octet-stream', 'accept-ranges': 'bytes', 'cache-control': 'no-store' };
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range ?? '');
  if (range) {
    start = range[1] ? Number(range[1]) : Math.max(0, size - Number(range[2]));
    end = range[1] && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    status = 206;
    headers['content-range'] = `bytes ${start}-${end}/${size}`;
  }
  headers['content-length'] = String(end - start + 1);
  res.writeHead(status, headers);
  if (req.method === 'HEAD') return void res.end();
  const kbps = Number(url.searchParams.get('throttle'));
  const chunk = kbps ? Math.max(1024, (kbps * 1024) / 10) : 64 * 1024;
  let at = start;
  let closed = false;
  res.on('close', () => { closed = true; });
  const next = () => {
    if (closed) return;
    if (at > end) return void res.end();
    const n = Math.min(chunk, end - at + 1);
    const buf = Buffer.allocUnsafe(n);
    for (let i = 0; i < n; i++) buf[i] = (at + i) % 251;
    at += n;
    const more = res.write(buf);
    if (kbps) setTimeout(next, 100);
    else if (more) setImmediate(next);
    else res.once('drain', next);
  };
  next();
}

createServer(handler).listen(port, '127.0.0.1', () => console.log(`Test media server on http://127.0.0.1:${port}`));
