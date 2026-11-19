// Vireo: YouTube's own ads, removed the way Brave and uBlock Origin remove them. YouTube serves its ads from its own servers,
// so blocking addresses is not enough. Instead, in three layers:
//   1. The player's data loses its ad instructions before the player reads them (the data in the page, and every answer
//      fetched later), so it has no ads to play.
//   2. Ad boxes in the page (in the feed, banners, beside the video) are hidden.
//   3. If an ad gets through anyway (YouTube changes often), it is skipped when it can be, or played muted at 16x speed.
//      Never by jumping to its end: an ad stitched into the video itself would take the real video with it.
// Vireo adds this script only while blocking is on for YouTube (it is taken away when someone allows ads there).
// No innerHTML and no <style> elements: YouTube forbids them (Trusted Types, strict style rules).
(() => {
  const TEST = /*TEST*/false;
  const isYouTube = /(^|\.)(youtube\.com|youtube-nocookie\.com)$/.test(location.hostname)
    || (TEST && location.pathname.startsWith('/pages/yt-'));
  if (!isYouTube || window.__vireoYt) return;
  window.__vireoYt = true;
  const wv = window.chrome && window.chrome.webview;
  const stats = { removed: 0, skipped: 0 };
  Object.defineProperty(window, '__vireoYtStats', { value: stats });
  const tell = what => { try { if (wv && window === window.top) wv.postMessage({ t: 'yt-ad', what, n: stats.removed + stats.skipped }); } catch { /* not in Vireo */ } };

  // ---- 1. The player's data, without its ads ----
  const AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams', 'adBreakParams'];
  const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
  const strip = (o, depth = 0) => {
    if (!o || typeof o !== 'object' || depth > 3) return false;
    let hit = false;
    if (Array.isArray(o)) { for (const x of o) if (strip(x, depth + 1)) hit = true; return hit; }
    for (const k of AD_KEYS) if (has(o, k)) { delete o[k]; hit = true; }
    // "Ad blockers are not allowed on YouTube": the message comes in the same answer.
    const ui = o.auxiliaryUi && o.auxiliaryUi.messageRenderers;
    if (ui && has(ui, 'enforcementMessageViewModel')) { delete ui.enforcementMessageViewModel; hit = true; }
    if (o.playerResponse && strip(o.playerResponse, depth + 1)) hit = true;
    return hit;
  };
  const isPlayerData = o => o && typeof o === 'object' && !Array.isArray(o) && ('adPlacements' in o || 'playerAds' in o || 'adSlots' in o || 'playerResponse' in o || 'auxiliaryUi' in o);
  // Most parsed data is something else entirely (and some of it is long): only a short list holding player data is looked into.
  const looksLikePlayerData = o => isPlayerData(o) || (Array.isArray(o) && o.length <= 8 && o.some(isPlayerData));
  const clean = o => { try { if (looksLikePlayerData(o) && strip(o)) { stats.removed++; tell('removed'); } } catch { /* leave it */ } return o; };

  // The data written into the page itself.
  for (const name of ['ytInitialPlayerResponse']) {
    let value;
    try {
      Object.defineProperty(window, name, { configurable: true, enumerable: true, get: () => value, set: v => { value = clean(v); } });
    } catch { /* already defined by the page */ }
  }
  // Answers fetched later (a new video, the next one in a playlist): through JSON.parse and Response.json. Wrapped in a
  // Proxy so they still look like the browser's own functions to the page.
  try {
    JSON.parse = new Proxy(JSON.parse, { apply: (fn, self, args) => clean(Reflect.apply(fn, self, args)) });
    const json = Response.prototype.json;
    Response.prototype.json = new Proxy(json, { apply: (fn, self, args) => Reflect.apply(fn, self, args).then(clean) });
  } catch { /* an older engine */ }

  // ---- 2. Ad boxes in the page ----
  const css = `
    ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-promoted-sparkles-web-renderer, ytd-promoted-video-renderer,
    ytd-display-ad-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer, ytd-video-masthead-ad-v3-renderer,
    ytd-primetime-promo-renderer, ytd-companion-slot-renderer, ytd-action-companion-ad-renderer, ytd-player-legacy-desktop-watch-ads-renderer,
    #masthead-ad, #player-ads, .ytp-ad-overlay-container, .ytp-ad-image-overlay, .ytp-featured-product, ytd-merch-shelf-renderer,
    ytd-rich-item-renderer:has(ytd-ad-slot-renderer), ytd-rich-section-renderer:has(ytd-statement-banner-renderer),
    ytm-promoted-sparkles-web-renderer, ytm-companion-ad-renderer, ad-slot-renderer, ytd-enforcement-message-view-model
    { display: none !important; }`;
  const addSheet = () => {
    try {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(css);
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
    } catch { /* no constructable stylesheets */ }
  };
  if (document.documentElement) addSheet(); else document.addEventListener('DOMContentLoaded', addSheet, { once: true });

  // ---- 3. An ad that got through: skipped, or hurried past without sound ----
  let before = null;
  const tick = () => {
    const player = document.querySelector('.html5-video-player');
    const video = player && player.querySelector('video');
    // The "ad blockers are not allowed" box, if it still came: gone, with its dark backdrop, and the video plays on.
    const enforce = document.querySelector('ytd-enforcement-message-view-model');
    if (enforce) {
      (enforce.closest('tp-yt-paper-dialog') || enforce).remove();
      document.querySelectorAll('tp-yt-iron-overlay-backdrop').forEach(b => b.remove());
      if (video && video.paused) video.play().catch(() => {});
    }
    if (!player || !video) return;
    const ad = player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting');
    if (ad) {
      if (!before) { before = { muted: video.muted, rate: video.playbackRate }; stats.skipped++; tell('skipped'); }
      const skip = player.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
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
