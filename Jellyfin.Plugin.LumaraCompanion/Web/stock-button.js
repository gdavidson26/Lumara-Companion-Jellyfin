// Lumara Companion for Jellyfin: button added to the standard Jellyfin web app.
// Licensed under the GNU General Public License v3.0 (part of the GPL plugin).
//
// data-lumara-mode="open": Side-by-side mode, the button opens Lumara Web.
// data-lumara-mode="back": Replace mode, for someone who chose the standard app;
//                          the button switches this browser back to Lumara.
//
// Where it goes: the legacy layout has a `.headerRight` toolbar, so the button
// sits there with the other header icons. The newer layout (default since
// Jellyfin 10.9) draws its app bar with React; inserting nodes into React's
// tree breaks when React re-renders, so there the button is a small pill fixed
// to the corner of the page instead, attached to <body>, hidden during video.
(function () {
  var script = document.currentScript;
  if (!script) return;
  var mode = script.getAttribute('data-lumara-mode') || 'open';
  var base = script.getAttribute('data-lumara-base') || '';
  var label = mode === 'back' ? 'Back to Lumara' : 'Open Lumara';
  var DOT = '<span style="display:inline-block;width:10px;height:10px;border-radius:50%;background:#F2B66D;box-shadow:0 0 8px rgba(242,182,109,.8)"></span>';

  function go() {
    if (mode === 'back') {
      // Forget the "use the standard app" choice for this browser.
      document.cookie = 'lumara-ui=lumara; path=' + (base || '/') + '; max-age=31536000; samesite=lax';
      window.location.href = base + '/web/';
    } else {
      window.location.href = base + '/Lumara/Web/';
    }
  }

  function make(id, cls) {
    var btn = document.createElement('button');
    btn.id = id;
    btn.type = 'button';
    btn.className = cls;
    btn.title = label;
    btn.setAttribute('aria-label', label);
    btn.addEventListener('click', go);
    return btn;
  }

  function visible(el) {
    return !!el && el.offsetParent !== null;
  }

  function legacyHeader() {
    var header = document.querySelector('.skinHeader .headerRight');
    if (!visible(header)) return false;
    if (!document.getElementById('lumara-open')) {
      var btn = make('lumara-open', 'headerButton headerButtonRight paper-icon-button-light');
      btn.style.cssText = 'display:inline-flex;align-items:center;justify-content:center;';
      btn.innerHTML = DOT;
      header.insertBefore(btn, header.firstChild);
    }
    return true;
  }

  function pill() {
    var btn = document.getElementById('lumara-pill');
    if (!btn) {
      btn = make('lumara-pill', '');
      // Just the dot until hovered or focused, so it never sits on top of
      // buttons in long pages; the label slides out when wanted.
      btn.style.cssText = 'position:fixed;left:16px;bottom:16px;z-index:999;display:none;align-items:center;' +
        'height:34px;padding:0 11px;border:1px solid rgba(255,255,255,.14);border-radius:17px;cursor:pointer;' +
        'background:rgba(20,21,26,.82);color:#f4efe6;font:500 13px/1 system-ui,sans-serif;' +
        'box-shadow:0 6px 20px rgba(0,0,0,.35);';
      btn.innerHTML = DOT + '<span class="lumara-pill-label" style="display:inline-block;overflow:hidden;white-space:nowrap;' +
        'max-width:0;margin-left:0;transition:max-width .2s ease,margin-left .2s ease">' + label + '</span>';
      var text = btn.lastChild;
      var open = function () { text.style.maxWidth = '160px'; text.style.marginLeft = '8px'; };
      var close = function () { text.style.maxWidth = '0'; text.style.marginLeft = '0'; };
      btn.addEventListener('mouseenter', open);
      btn.addEventListener('focus', open);
      btn.addEventListener('mouseleave', close);
      btn.addEventListener('blur', close);
      document.body.appendChild(btn);
    }
    return btn;
  }

  function playingVideo() {
    return /^#\/(video|queue)/.test(window.location.hash) || visible(document.querySelector('.videoPlayerContainer'));
  }

  // The standard app builds its header after load and swaps layouts and pages
  // without reloading, so re-check now and then. Cheap: a few selector lookups.
  function update() {
    var p = pill();
    var inHeader = legacyHeader();
    p.style.display = inHeader || playingVideo() ? 'none' : 'inline-flex';
  }

  update();
  setInterval(update, 1000);
  window.addEventListener('hashchange', update);
})();
