// Vireo: music and videos on YouTube keep playing while their tab is hidden, the window is minimized, or nobody has
// touched the page for a while (as Brave, Mozilla's video-bg-play and similar scripts do it):
//   - the page is always told it is visible (players pause or lower quality when they think nobody is looking);
//   - YouTube's "last activity" time is kept fresh, and its "Video paused. Continue watching?" box, if it still comes,
//     is answered and playback resumed.
// Changes nothing else on the page; on any other site it does nothing.
(() => {
  const TEST = /*TEST*/false;
  const here = location.hostname;
  const isYouTube = /(^|\.)(youtube\.com|youtube-nocookie\.com)$/.test(here) || (TEST && location.pathname.startsWith('/pages/yt-'));
  if (!isYouTube || window.__vireoPlayOn) return;
  window.__vireoPlayOn = true;

  // ---- Always visible ----
  try {
    const visible = { configurable: true, get: () => 'visible' };
    const shown = { configurable: true, get: () => false };
    Object.defineProperty(Document.prototype, 'visibilityState', visible);
    Object.defineProperty(Document.prototype, 'hidden', shown);
    Object.defineProperty(Document.prototype, 'webkitVisibilityState', visible);
    Object.defineProperty(Document.prototype, 'webkitHidden', shown);
  } catch { /* the engine would not allow it */ }
  // The "you switched away" events never reach the page's own listeners (this one is added first, on the capture path).
  for (const type of ['visibilitychange', 'webkitvisibilitychange']) {
    window.addEventListener(type, e => e.stopImmediatePropagation(), true);
    document.addEventListener(type, e => e.stopImmediatePropagation(), true);
  }

  // ---- "Are you still watching?" ----
  const fresh = () => { try { window._lact = Date.now(); } catch { /* not there */ } };
  setInterval(fresh, 60_000);
  setInterval(() => {
    const dialog = document.querySelector('yt-confirm-dialog-renderer, ytmusic-you-there-renderer');
    if (!dialog || !dialog.offsetParent) return;
    const text = (dialog.textContent || '').toLowerCase();
    if (!/continue watching|still watching|still there|are you there/.test(text) && !dialog.matches('ytmusic-you-there-renderer')) return;
    // The button itself first (a list of selectors would match its wrapper first, and a click there does nothing).
    const yes = dialog.querySelector('#confirm-button button') || dialog.querySelector('#confirm-button') || dialog.querySelector('button');
    if (yes) yes.click();
    const video = document.querySelector('video');
    if (video && video.paused) video.play().catch(() => {});
    fresh();
  }, 1000);
})();
