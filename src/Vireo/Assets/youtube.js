// Vireo: YouTube's own ads, removed the way Brave and uBlock Origin remove them. YouTube serves its ads from its own servers,
// so blocking addresses is not enough. Instead, in three layers:
//   1. The player's data loses its ad instructions before the player reads them (the data in the page, and every answer
//      fetched later), so it has no ads to play.
//   2. Ad boxes in the page (in the feed, banners, beside the video) are hidden.
//   3. If an ad gets through anyway (YouTube changes often), it is skipped when it can be, or played muted at 16x speed.
//      Never by jumping to its end: an ad stitched into the video itself would take the real video with it.
// What to look for (key names, selectors) is data, not code: RULES below is Vireo's rules file (Assets/rules/youtube.json,
// or a newer signed copy Vireo downloaded and checked), checked again by Vireo before it is written in here.
// Vireo adds this script only while blocking is on for YouTube (it is taken away when someone allows ads there).
// No innerHTML and no <style> elements: YouTube forbids them (Trusted Types, strict style rules).
(() => {
  const TEST = /*TEST*/false;
  const RULES = /*RULES*/null;
  if (!RULES) return;
  const isYouTube = /(^|\.)(youtube\.com|youtube-nocookie\.com)$/.test(location.hostname)
    || (TEST && location.pathname.startsWith('/pages/yt-'));
  if (!isYouTube || window.__vireoYt) return;
  window.__vireoYt = true;
  const wv = window.chrome && window.chrome.webview;
  const stats = { removed: 0, skipped: 0, rules: RULES.version };
  Object.defineProperty(window, '__vireoYtStats', { value: stats });
  const tell = what => { try { if (wv && window === window.top) wv.postMessage({ t: 'yt-ad', what, n: stats.removed + stats.skipped }); } catch { /* not in Vireo */ } };

  // ---- 1. The player's data, without its ads ----
  const AD_KEYS = RULES.playerDataKeys;
  const MESSAGE_KEYS = RULES.messageKeys;
  const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
  const strip = (o, depth = 0) => {
    if (!o || typeof o !== 'object' || depth > 3) return false;
    let hit = false;
    if (Array.isArray(o)) { for (const x of o) if (strip(x, depth + 1)) hit = true; return hit; }
    for (const k of AD_KEYS) if (has(o, k)) { delete o[k]; hit = true; }
    // "Ad blockers are not allowed on YouTube": the message comes in the same answer.
    const ui = o.auxiliaryUi && o.auxiliaryUi.messageRenderers;
    if (ui) for (const k of MESSAGE_KEYS) if (has(ui, k)) { delete ui[k]; hit = true; }
    if (o.playerResponse && strip(o.playerResponse, depth + 1)) hit = true;
    return hit;
  };
  const isPlayerData = o => o && typeof o === 'object' && !Array.isArray(o) && ('playerResponse' in o || 'auxiliaryUi' in o || AD_KEYS.some(k => k in o));
  // Most parsed data is something else entirely (and some of it is long): only a short list holding player data is looked into.
  const looksLikePlayerData = o => isPlayerData(o) || (Array.isArray(o) && o.length <= 8 && o.some(isPlayerData));
  const clean = o => { try { if (looksLikePlayerData(o) && strip(o)) { stats.removed++; tell('removed'); } } catch { /* leave it */ } return o; };

  // The data written into the page itself.
  let initial;
  try {
    Object.defineProperty(window, 'ytInitialPlayerResponse', { configurable: true, enumerable: true, get: () => initial, set: v => { initial = clean(v); } });
  } catch { /* already defined by the page */ }
  // Answers fetched later (a new video, the next one in a playlist): through JSON.parse and Response.json. Wrapped in a
  // Proxy so they still look like the browser's own functions to the page.
  try {
    JSON.parse = new Proxy(JSON.parse, { apply: (fn, self, args) => clean(Reflect.apply(fn, self, args)) });
    Response.prototype.json = new Proxy(Response.prototype.json, { apply: (fn, self, args) => Reflect.apply(fn, self, args).then(clean) });
  } catch { /* an older engine */ }

  // ---- 2. Ad boxes in the page ----
  const addSheet = () => {
    try {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(RULES.hide.join(',\n') + ' { display: none !important; }');
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
    } catch { /* no constructable stylesheets, or a selector this engine does not know */ }
  };
  if (document.documentElement) addSheet(); else document.addEventListener('DOMContentLoaded', addSheet, { once: true });

  // ---- 3. An ad that got through: skipped, or hurried past without sound ----
  const SKIP = RULES.skipButtons.join(', ');
  let before = null;
  const tick = () => {
    const player = document.querySelector(RULES.player);
    const video = player && player.querySelector('video');
    // The "ad blockers are not allowed" box, if it still came: gone, with its dark backdrop, and the video plays on.
    const enforce = document.querySelector(RULES.enforcement);
    if (enforce) {
      (enforce.closest(RULES.enforcementDialog) || enforce).remove();
      document.querySelectorAll(RULES.enforcementBackdrop).forEach(b => b.remove());
      if (video && video.paused) video.play().catch(() => {});
    }
    if (!player || !video) return;
    const ad = RULES.adPlayerClasses.some(c => player.classList.contains(c));
    if (ad) {
      if (!before) { before = { muted: video.muted, rate: video.playbackRate }; stats.skipped++; tell('skipped'); }
      const skip = player.querySelector(SKIP);
      if (skip) skip.click();
      video.muted = true;
      if (video.playbackRate !== 16) video.playbackRate = 16;
    } else if (before) {
      video.muted = before.muted;
      video.playbackRate = before.rate;
      before = null;
    }
  };
  setInterval(tick, 300);
})();
