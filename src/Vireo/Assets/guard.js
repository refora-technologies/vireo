// Vireo: stops the usual tricks that open windows nobody asked for. Runs in every page and frame.
// A window is only opened by a script when the person just clicked something that is plainly meant to open it
// (a link, a button). A click on empty page space, or on an invisible layer laid over the whole page, is not that.
(() => {
  if (window.__vireoGuard) return;
  window.__vireoGuard = true;
  const wv = window.chrome && window.chrome.webview;
  let on = true;

  // Sites whose sign-in and payment pop-ups are real.
  const TRUSTED = /(^|\.)(google|googleapis|gstatic|microsoft|microsoftonline|live|office|apple|facebook|fb|github|githubusercontent|paypal|stripe|discord|twitter|x|linkedin|amazon|okta|auth0|yahoo|steampowered|spotify|reddit|twitch|dropbox|slack|zoom|adyen|klarna|braintreegateway|recaptcha)\.(com|tv|net|me|co)$/i;

  let lastClick = { at: -1e9, target: null, trusted: false };
  const note = e => { lastClick = { at: performance.now(), target: e.target, trusted: e.isTrusted }; };
  for (const t of ['pointerdown', 'mousedown', 'mouseup', 'click', 'touchstart', 'keydown']) addEventListener(t, note, true);

  const INTERACTIVE = 'a[href],button,input[type=submit],input[type=button],input[type=image],summary,[role=button],[role=link],[role=menuitem]';

  const cross = url => {
    try { return new URL(url, location.href).origin !== location.origin; } catch { return false; }
  };
  const trusted = url => {
    try { return TRUSTED.test(new URL(url, location.href).hostname); } catch { return false; }
  };
  const block = (url, why) => {
    if (wv) wv.postMessage({ t: 'popup-blocked', url: String(url), why });
  };

  // A layer that covers (nearly) the whole view and cannot be seen: put there to catch the click.
  const hidesAClick = el => {
    for (let n = el; n && n.nodeType === 1 && n !== document.documentElement; n = n.parentElement) {
      const r = n.getBoundingClientRect();
      if (r.width < innerWidth * 0.7 || r.height < innerHeight * 0.7) continue;
      const s = getComputedStyle(n);
      const faint = parseFloat(s.opacity) < 0.06 || s.visibility === 'hidden' || (s.backgroundColor === 'rgba(0, 0, 0, 0)' && !n.textContent.trim() && !n.querySelector('img,video,canvas,svg'));
      if (faint && (s.position === 'fixed' || s.position === 'absolute')) return true;
    }
    return false;
  };

  const askedFor = () => {
    const sinceClick = performance.now() - lastClick.at;
    if (sinceClick > 1500 || !lastClick.trusted) return false;
    const el = lastClick.target;
    if (!el || !el.closest) return false;
    const control = el.closest(INTERACTIVE);
    return !!control && !hidesAClick(control);
  };

  // A click on a link opens that link. A script that opens a different window during that same click is riding on it (and,
  // because a click can open only one window, it would even stop the link itself from opening).
  const ridesOnALink = url => {
    if (performance.now() - lastClick.at > 1500 || !lastClick.target || !lastClick.target.closest) return false;
    const a = lastClick.target.closest('a[href]');
    if (!a) return false;
    try { return new URL(url, location.href).href !== new URL(a.href, location.href).href; } catch { return true; }
  };

  const realOpen = window.open;
  window.open = function (url, target, features) {
    if (on && url && cross(url) && !trusted(url) && (!askedFor() || ridesOnALink(url))) { block(url, 'script'); return null; }
    return realOpen.apply(this, arguments);
  };

  // A link made and clicked by a script (the other classic): the person did not click it.
  const wantsNewWindow = a => /^(_blank|_new)$/i.test(a.target || '') || (a.target && !/^_(self|parent|top)$/i.test(a.target));
  const realClick = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function () {
    if (on && this.href && wantsNewWindow(this) && cross(this.href) && !trusted(this.href) && !askedFor()) { block(this.href, 'script link'); return; }
    return realClick.apply(this, arguments);
  };
  const realDispatch = EventTarget.prototype.dispatchEvent;
  EventTarget.prototype.dispatchEvent = function (e) {
    if (on && this instanceof HTMLAnchorElement && e && e.type === 'click' && !e.isTrusted && this.href && wantsNewWindow(this) && cross(this.href) && !trusted(this.href) && !askedFor()) {
      block(this.href, 'script link'); return false;
    }
    return realDispatch.apply(this, arguments);
  };

  // An invisible sheet laid over a player (or the page) to catch the first click for an advert. Stopping the advert is not
  // enough: such a sheet usually removes itself only after its advert opened, so it would stay and swallow every click, and
  // the player under it would never start. So the sheet is made click-through, and the click goes on to what is under it.
  const passThrough = (layer, x, y) => {
    for (let n = layer; n && n.nodeType === 1 && n !== document.documentElement && n !== document.body; n = n.parentElement) {
      const r = n.getBoundingClientRect();
      if (r.width < innerWidth * 0.3 && r.height < innerHeight * 0.3 && n !== layer) break;
      n.style.setProperty('pointer-events', 'none', 'important');
    }
    const under = document.elementFromPoint(x, y);
    if (under && under !== layer && !layer.contains(under)) {
      try { under.click(); } catch (err) { /* the player listens for something else: the next click reaches it */ }
    }
  };
  addEventListener('click', e => {
    if (!on || !e.isTrusted) return;
    const a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (a && wantsNewWindow(a) && cross(a.href) && !trusted(a.href) && hidesAClick(a)) {
      e.preventDefault(); e.stopImmediatePropagation();
      block(a.href, 'invisible layer');
      passThrough(a, e.clientX, e.clientY);
    }
  }, true);

  if (wv) {
    wv.addEventListener('message', ev => { if (ev.data && ev.data.t === 'guard-config') on = !!ev.data.on; });
    wv.postMessage({ t: 'guard-hello' });
  }
})();
