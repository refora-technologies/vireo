// Vireo: download buttons on videos and audio. Runs in the page and in the frames inside it (embedded players).
// Everything it draws lives in a closed shadow root, so the page's own styles and scripts cannot touch it.
// A frame without any video or audio stays idle: it only looks for one every couple of seconds, so ad frames cost nothing.
(() => {
  if (window.__vireoDl) return;
  window.__vireoDl = true;
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return;

  // Some players write the playlist in script and give the video a blob: address for it, so the playlist itself never
  // crosses the network. Hand Vireo the text of any small blob that turns out to be a playlist (#EXTM3U, or an <MPD>).
  try {
    const create = URL.createObjectURL;
    const watch = function (obj) {
      const url = create.apply(this, arguments);
      try {
        if (obj instanceof Blob && obj.size > 6 && obj.size < 4e6 && !/^(image|video|audio|font)\//.test(obj.type)) {
          obj.slice(0, 64).text().then(head => {
            const h = head.replace(/^\uFEFF/, '').trimStart();
            if (!h.startsWith('#EXTM3U') && !/<MPD[\s>]/.test(h) && !h.startsWith('<?xml')) return;
            return obj.text().then(text => {
              if (/^\s*(\uFEFF)?#EXTM3U/.test(text) || /<MPD[\s>]/.test(text.slice(0, 4096)))
                wv.postMessage({ t: 'blob-playlist', blob: url, page: location.href, text });
            });
          }).catch(() => {});
        }
      } catch (e) { /* never get in the page's way */ }
      return url;
    };
    Object.defineProperty(watch, 'toString', { value: () => create.toString() });
    URL.createObjectURL = watch;
  } catch (e) { /* a page that froze URL keeps it */ }

  // ---- Stream addresses written into the page itself ----
  // Many players carry their stream's address in the page before anything plays: in a script ("hls": "...m3u8"), in a
  // data-options attribute (ok.ru), in og:video / JSON-LD contentUrl metadata, or (Facebook) a whole DASH description. They
  // are handed to Vireo, which reads each one and shows only what really answers as a video.
  const sentLinks = new Set();
  const decode = s => s.replace(/\\u002[fF]/g, '/').replace(/\\\//g, '/').replace(/\\u0026/g, '&').replace(/&amp;/g, '&').replace(/&quot;/g, '"');
  const LINK = /https?:\/\/[^\s"'<>\\()]+?\.(?:m3u8|mpd|mp4|webm|m4v|mov|mkv)(?:\?[^\s"'<>\\()]*)?(?=["'\s<>\\),]|$)/gi;
  const PREVIEW = /thumb|preview|teaser|sprite|poster|trailer[-_]?small|\/static\/|\.gif/i;
  const scanPage = () => {
    try {
      const html = document.documentElement ? document.documentElement.outerHTML : '';
      if (!html || html.length > 12e6) return;
      const text = decode(html);
      const links = [];
      const add = (u, why) => { if (u && /^https?:/i.test(u) && !sentLinks.has(u) && links.length < 30) { sentLinks.add(u); links.push({ url: u, why }); } };
      // Metadata that names the video outright
      for (const m of document.querySelectorAll('meta[property="og:video"], meta[property="og:video:url"], meta[property="og:video:secure_url"], meta[name="twitter:player:stream"], meta[itemprop="contentUrl"]'))
        add(m.content, 'meta');
      for (const s of document.querySelectorAll('script[type="application/ld+json"]')) {
        const found = (s.textContent || '').match(/"contentUrl"\s*:\s*"([^"]+)"/g) || [];
        for (const f of found) add(decode(f.replace(/^"contentUrl"\s*:\s*"/, '').replace(/"$/, '')), 'json-ld');
      }
      // Addresses in scripts and attributes
      for (const m of text.matchAll(LINK)) {
        const u = m[0];
        if (/\.(mp4|webm|m4v|mov|mkv)/i.test(u) && PREVIEW.test(u)) continue; // hover previews and thumbnails of other videos
        add(u, 'page');
      }
      // A whole DASH description carried in the page (Facebook and others): Vireo reads it like a manifest.
      for (const m of html.matchAll(/"(?:dash_manifest_xml_string|video_dash_manifest|manifest_xml)"\s*:\s*"((?:\\.|[^"\\])*)"/g)) {
        let xml = ''; try { xml = JSON.parse('"' + m[1] + '"'); } catch (e) { continue; }
        if (/<MPD[\s>]/.test(xml.slice(0, 4000)) && !sentLinks.has(xml.slice(0, 200))) {
          sentLinks.add(xml.slice(0, 200));
          wv.postMessage({ t: 'blob-playlist', blob: null, page: location.href, text: xml });
        }
      }
      if (links.length) wv.postMessage({ t: 'page-links', page: location.href, links });
    } catch (e) { /* never in the page's way */ }
  };
  // Pages build themselves after loading: look a few times, then when something starts playing.
  for (const at of [1200, 3500, 8000, 20000]) setTimeout(scanPage, at);
  document.addEventListener('play', () => setTimeout(scanPage, 500), true);
  let lastHref = location.href;
  setInterval(() => { if (location.href !== lastHref) { lastHref = location.href; setTimeout(scanPage, 1500); } }, 1000);

  const run = () => {

  let mode = 'hover'; // hover | always | off
  const entries = new Map(); // media element -> { btn, shown, timer }
  let menuFor = null;
  let lastReport = '';

  // ---- the layer everything is drawn in ----
  const host = document.createElement('div');
  host.setAttribute('data-vireo', '');
  host.style.cssText = 'all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;';
  const root = host.attachShadow({ mode: 'closed' });
  // No innerHTML anywhere: sites like YouTube forbid it (Trusted Types), and strict style policies block <style> elements,
  // so the styling is a constructable stylesheet and the elements are built one call at a time.
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(`
      :host { all: initial; }
      * { box-sizing: border-box; font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
      .b {
        position: fixed; display: flex; align-items: center; gap: 7px; height: 34px; padding: 0 14px 0 5px; border-radius: 999px; border: 1px solid rgba(255,255,255,.14);
        background: rgba(15,23,22,.92); color: #EAF2F0; font-size: 12.5px; font-weight: 600; cursor: pointer;
        box-shadow: 0 6px 20px -6px rgba(0,0,0,.6);
        opacity: 0; transform: translateY(-3px); pointer-events: none; transition: opacity .15s ease, transform .2s cubic-bezier(.2,.8,.2,1), border-color .15s;
      }
      .b.on { opacity: 1; transform: none; pointer-events: auto; }
      .b:hover { border-color: rgba(61,219,185,.8); }
      .b:active { transform: scale(.97); }
      .dot { width: 24px; height: 24px; border-radius: 50%; background: #3DDBB9; display: grid; place-items: center; }
      .dot svg { width: 13px; height: 13px; stroke: #04241D; fill: none; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
      .menu {
        position: fixed; width: 300px; max-height: 70vh; overflow: auto; padding: 5px; border-radius: 14px; background: #1F2928; color: #EAF2F0;
        border: 1px solid rgba(255,255,255,.14); box-shadow: 0 16px 44px -10px rgba(0,0,0,.75); font-size: 13px;
        opacity: 0; transform: translateY(-4px); transform-origin: top right; transition: opacity .14s ease, transform .2s cubic-bezier(.2,.8,.2,1);
      }
      .menu.on { opacity: 1; transform: none; }
      .head { padding: 8px 10px 4px; font-size: 12px; font-weight: 600; color: #A9BAB6; }
      .title { padding: 4px 10px 6px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 10px; border: 0; border-radius: 6px; background: none; color: inherit; font: inherit; cursor: pointer; text-align: left; }
      .row:hover { background: #2D3A38; }
      .row b { min-width: 70px; font-weight: 600; font-family: "Cascadia Mono", Consolas, monospace; font-size: 12px; }
      .row.audio b { color: #A9BAB6; }
      .row span { flex: 1; color: #829591; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .row i { font-style: normal; color: #A9BAB6; font-size: 12px; font-family: "Cascadia Mono", Consolas, monospace; }
      .note { padding: 12px 12px 10px; color: #A9BAB6; line-height: 1.45; }
      .note.warn { color: #F2C46D; }
    `);
  root.adoptedStyleSheets = [sheet];
  const layer = document.createElement('div');
  root.appendChild(layer);
  const mk = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text) e.textContent = text; return e; };

  const attach = () => { if (!host.isConnected && document.documentElement) document.documentElement.appendChild(host); };

  const icon = () => {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    const path = document.createElementNS(ns, 'path');
    path.setAttribute('d', 'M12 4v11m0 0l-4.5-4.5M12 15l4.5-4.5M5 20h14');
    svg.appendChild(path);
    return svg;
  };

  // ---- finding the media on the page ----
  const eligible = el => {
    const r = el.getBoundingClientRect();
    if (el.tagName === 'AUDIO') return r.width >= 120 && r.height >= 20 && getComputedStyle(el).display !== 'none';
    return r.width >= 200 && r.height >= 110 && getComputedStyle(el).visibility !== 'hidden';
  };

  const report = () => {
    const els = [...document.querySelectorAll('video,audio')].filter(el => !el.closest('[data-vireo]')).map(el => {
      let src = el.currentSrc || el.src || '';
      if (!/^https?:/.test(src)) {
        // the first <source> the browser could fetch directly (a blob: player is found through the network instead)
        const s = [...el.querySelectorAll('source')].map(x => x.src).find(u => /^https?:/.test(u || ''));
        if (s) src = s;
      }
      return {
        src, poster: el.poster || '', duration: Number.isFinite(el.duration) ? el.duration : 0,
        w: el.videoWidth || 0, h: el.videoHeight || 0, audio: el.tagName === 'AUDIO',
      };
    });
    const key = JSON.stringify(els);
    // "page" is this frame's own address: a player frame's host often refuses anyone who does not name it as the referer.
    // The page's own preview picture stands in for a video with no poster (streams rarely have one).
    const og = window === window.top ? ((document.querySelector('meta[property="og:image"],meta[name="twitter:image"]') || {}).content || '') : '';
    const keyed = key + og;
    if (keyed !== lastReport) { lastReport = keyed; wv.postMessage({ t: 'els', page: location.href, og, items: els.slice(0, 20) }); }
  };

  const place = (el, e) => {
    const r = el.getBoundingClientRect();
    const w = e.btn.offsetWidth || 120;
    const left = Math.min(Math.max(4, r.right - w - 10), innerWidth - w - 4);
    const top = Math.min(Math.max(4, r.top + 10), innerHeight - 40);
    e.btn.style.left = left + 'px';
    e.btn.style.top = top + 'px';
  };

  const ensure = el => {
    let e = entries.get(el);
    if (e) return e;
    const btn = document.createElement('button');
    btn.className = 'b';
    const dot = mk('span', 'dot');
    dot.appendChild(icon());
    btn.append(dot, mk('span', '', 'Download'));
    btn.addEventListener('click', ev => { ev.stopPropagation(); ev.preventDefault(); openMenu(el); });
    btn.addEventListener('mouseenter', () => { e.over = true; });
    btn.addEventListener('mouseleave', () => { e.over = false; });
    layer.appendChild(btn);
    e = { btn, over: false, last: 0 };
    entries.set(el, e);
    return e;
  };

  const show = (el, on) => {
    const e = ensure(el);
    if (on) { attach(); place(el, e); }
    e.btn.classList.toggle('on', on);
  };

  const refresh = () => {
    if (mode === 'off') { for (const [, e] of entries) e.btn.classList.remove('on'); closeMenu(); return; }
    const now = performance.now();
    for (const el of document.querySelectorAll('video,audio')) {
      if (el.closest('[data-vireo]')) continue;
      if (!eligible(el)) { if (entries.has(el)) show(el, false); continue; }
      const e = ensure(el);
      const wanted = mode === 'always' || e.over || now - e.last < 1600 || menuFor === el;
      show(el, wanted);
    }
    for (const [el, e] of [...entries]) if (!el.isConnected) { e.btn.remove(); entries.delete(el); }
  };

  // The pointer is over a video, or over the player built around it (controls and overlays often sit on top of the video itself).
  // Looked at at most every 60 ms: a page can send hundreds of pointer moves a second.
  let lastLook = 0;
  const PLAYER = '[class*="player"], [id*="player"], [class*="video"], .vjs-tech, .art-video-player, .html5-video-player';
  document.addEventListener('mousemove', ev => {
    if (mode === 'off') return;
    const now = performance.now();
    if (now - lastLook < 60) return;
    lastLook = now;
    let hit = false;
    for (const el of document.elementsFromPoint(ev.clientX, ev.clientY)) {
      if ((el.tagName === 'VIDEO' || el.tagName === 'AUDIO') && eligible(el)) {
        ensure(el).last = now;
        hit = true;
        break;
      }
    }
    if (!hit) {
      for (const el of document.querySelectorAll('video,audio')) {
        if (!eligible(el)) continue;
        const r = el.getBoundingClientRect();
        const inside = (b, pad) => ev.clientX >= b.left - pad && ev.clientX <= b.right + pad && ev.clientY >= b.top - pad && ev.clientY <= b.bottom + pad;
        let near = inside(r, 4);
        if (!near) {
          // Only a container about as big as the video counts: "video-page" on <body> must not make the whole page a player.
          const parent = el.closest(PLAYER);
          if (parent && parent !== document.body && parent !== document.documentElement) {
            const pr = parent.getBoundingClientRect();
            near = pr.width * pr.height <= r.width * r.height * 3 && inside(pr, 0);
          }
        }
        if (near) { ensure(el).last = now; hit = true; break; }
      }
    }
    if (hit) refresh();
  }, { passive: true, capture: true });
  addEventListener('scroll', () => { closeMenu(); refresh(); }, { passive: true, capture: true });
  addEventListener('resize', refresh);
  document.addEventListener('keydown', ev => { if (ev.key === 'Escape') closeMenu(); }, true);
  document.addEventListener('mousedown', ev => { if (menuFor && !ev.composedPath().includes(host)) closeMenu(); }, true);
  for (const ev of ['loadedmetadata', 'play', 'durationchange']) document.addEventListener(ev, () => { report(); refresh(); }, true);
  new MutationObserver(() => { clearTimeout(refresh.t); refresh.t = setTimeout(() => { report(); refresh(); }, 250); })
    .observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
  setInterval(() => { report(); refresh(); }, 1500);

  // ---- the menu of qualities ----
  let menu = null;
  const closeMenu = () => {
    if (!menu) return;
    const m = menu;
    menu = null; menuFor = null;
    m.classList.remove('on');
    setTimeout(() => m.remove(), 200);
  };

  const openMenu = el => {
    if (menuFor === el) { closeMenu(); return; }
    closeMenu();
    menuFor = el;
    render({ note: 'Looking for the video…' });
    wv.postMessage({ t: 'dl-open', src: el.currentSrc || el.src || '' });
  };

  const render = data => {
    if (!menuFor) return;
    const e = entries.get(menuFor);
    if (!e) return;
    const fresh = !menu;
    if (!menu) { menu = document.createElement('div'); menu.className = 'menu'; layer.appendChild(menu); }
    menu.textContent = '';
    if (data.note) { const n = document.createElement('div'); n.className = 'note' + (data.warn ? ' warn' : ''); n.textContent = data.note; menu.appendChild(n); }
    for (const s of data.sections || []) {
      const h = document.createElement('div'); h.className = 'head'; h.textContent = s.head; menu.appendChild(h);
      if (s.title) { const t = document.createElement('div'); t.className = 'title'; t.textContent = s.title; menu.appendChild(t); }
      for (const c of s.choices) {
        const row = document.createElement('button');
        row.className = 'row' + (c.audio ? ' audio' : '');
        row.append(mk('b', '', c.label), mk('span', '', c.detail || ''), mk('i', '', c.size || ''));
        row.addEventListener('click', ev => { ev.stopPropagation(); wv.postMessage({ t: 'dl-pick', item: s.id, choice: c.id }); closeMenu(); });
        menu.appendChild(row);
      }
    }
    const r = e.btn.getBoundingClientRect();
    menu.style.left = Math.max(8, Math.min(r.right - 300, innerWidth - 308)) + 'px';
    const below = r.bottom + 8;
    menu.style.top = (below + 200 > innerHeight && r.top > 260 ? Math.max(8, r.top - 8 - menu.offsetHeight) : below) + 'px';
    if (fresh) requestAnimationFrame(() => menu && menu.classList.add('on'));
  };

  wv.addEventListener('message', ev => {
    const d = ev.data;
    if (!d) return;
    if (d.t === 'dl-config') { mode = d.mode; refresh(); }
    else if (d.t === 'dl-menu') render(d);
  });

  // Only present when Vireo runs its own self-test: lets the test drive the closed shadow root.
  if (window.__vireoSelfTest) window.__vireoTest = {
    hover() {
      const el = document.querySelector('video,audio'); if (!el) return false;
      const r = el.getBoundingClientRect();
      document.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + r.width / 2, clientY: r.top + r.height / 2, bubbles: true }));
      return true;
    },
    state() {
      const on = [...layer.querySelectorAll('.b.on')];
      return JSON.stringify({ buttons: on.length, label: on[0] ? on[0].textContent.trim() : '', box: on[0] ? on[0].getBoundingClientRect().toJSON() : null,
        menu: !!menu, rows: menu ? [...menu.querySelectorAll('.row')].map(r => r.textContent) : [], note: menu && menu.querySelector('.note') ? menu.querySelector('.note').textContent : '' });
    },
    press() { const b = layer.querySelector('.b.on'); if (!b) return false; b.click(); return true; },
    pick(i) { const r = menu && menu.querySelectorAll('.row')[i]; if (!r) return false; r.click(); return true; },
  };
  wv.postMessage({ t: 'dl-hello' });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => { attach(); report(); refresh(); });
  else { attach(); report(); refresh(); }
  };

  if (window === window.top) run();
  else {
    const look = setInterval(() => {
      if (document.getElementsByTagName('video').length || document.getElementsByTagName('audio').length) { clearInterval(look); run(); }
    }, 2000);
  }
})();
