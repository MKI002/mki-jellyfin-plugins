/*
 * Media Hero - home screen banner for Jellyfin 12
 *
 * Two parts:
 *   MediaHero.render(...)  draws a banner into any element. The configuration
 *                          page calls exactly this function for its preview -
 *                          so the preview cannot show anything other than
 *                          the real thing.
 *   The observer below     mounts the banner on the home screen.
 *
 * On speed: the server delivers the finished list in a single response. No
 * items are fetched one by one here, and exactly one image per visible slide
 * is held in memory - the next one is preloaded, the others are not. No timer
 * runs while the banner is not visible.
 */
(function () {
  'use strict';

  var WURZEL = 'mediahero-root';
  var zustand = null;

  /* UI labels. English only. */
  var TEXTE = {
    play: 'Play', details: 'Details', trailer: 'Trailer', favorit: 'Favorite',
    leer: 'Nothing to show – filters too strict?',
    eintrag: 'Item', stunde: 'h', minute: 'min',
  };

  function t(schluessel) {
    return TEXTE[schluessel] || schluessel;
  }

  // ----------------------------------------------------------------- Helpers
  function el(tag, klasse, text) {
    var n = document.createElement(tag);
    if (klasse) n.className = klasse;
    if (text != null) n.textContent = text;
    return n;
  }

  function basis() {
    try {
      if (window.ApiClient && ApiClient.serverAddress) return ApiClient.serverAddress();
    } catch (e) { /* ignore */ }
    return '';
  }

  function bildUrl(item, art, breite) {
    var tag = art === 'Logo' ? item.LogoTag
      : art === 'Primary' ? item.PosterTag
        : art === 'Thumb' ? item.ThumbTag : item.BackdropTag;
    if (!tag) return null;
    var u = basis() + '/Items/' + item.Id + '/Images/' + art + '/0'
      + '?fillWidth=' + breite + '&quality=' + (art === 'Logo' ? 90 : 80)
      + '&v=' + encodeURIComponent(tag);
    return u;
  }

  function minuten(m) {
    if (!m) return null;
    var h = Math.floor(m / 60), r = m % 60;
    return h > 0 ? h + ' ' + t('stunde') + ' ' + r + ' ' + t('minute')
                 : r + ' ' + t('minute');
  }

  function reduziert() {
    try {
      return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch (e) { return false; }
  }

  // ----------------------------------------------------------------- Drawing
  function render(ziel, items, d, optionen) {
    optionen = optionen || {};
    if (!ziel) return null;
    ziel.innerHTML = '';
    if (!items || !items.length) {
      ziel.appendChild(el('div', 'mh-leer', t('leer')));
      return null;
    }

    /* Shuffle per call - on a copy, so the list fetched from the server stays
       unchanged and a second call does not start from an already shuffled
       order. Deliberately not in the preview: there, only what you are
       currently adjusting should change. */
    if (d.shuffleOnLoad && optionen.vorschau !== true) {
      items = items.slice();
      for (var mi = items.length - 1; mi > 0; mi--) {
        var mj = Math.floor(Math.random() * (mi + 1));
        var mt = items[mi]; items[mi] = items[mj]; items[mj] = mt;
      }
    }

    var statisch = optionen.statisch === true
      || d.transition === 'None'
      || (d.respectReducedMotion && reduziert());

    var wrap = el('div', 'mh-wrap mh-layout-' + (d.layout || 'Fullwidth').toLowerCase());
    wrap.style.setProperty('--mh-h', (d.heightVh || 56) + 'vh');
    wrap.style.setProperty('--mh-maxh', (d.maxHeightPx || 720) + 'px');
    wrap.style.setProperty('--mh-radius', (d.cornerRadius || 0) + 'px');
    wrap.style.setProperty('--mh-scrim', ((d.scrimStrength == null ? 70 : d.scrimStrength) / 100));

    // Fine-grained sizes as CSS variables - keeps the stylesheet static while
    // everything stays adjustable.
    function mass(name, wert, standard, einheit) {
      wrap.style.setProperty(name, (wert == null ? standard : wert) + (einheit || ''));
    }
    mass('--mh-logo-w', d.logoMaxWidthPercent, 34, '%');
    mass('--mh-logo-h', d.logoMaxHeightPercent, 40, '%');
    mass('--mh-text-l', d.textLeftPercent, 4, '%');
    mass('--mh-text-b', d.textBottomPercent, 8, '%');
    mass('--mh-text-w', d.textWidthPercent, 52, '%');
    mass('--mh-gap', (d.elementGap == null ? 7 : d.elementGap) / 10, 0.7, 'em');
    mass('--mh-titel-s', (d.titleScale == null ? 100 : d.titleScale) / 100, 1, '');
    mass('--mh-text-s', (d.textScale == null ? 100 : d.textScale) / 100, 1, '');
    mass('--mh-knopf-s', (d.buttonScale == null ? 100 : d.buttonScale) / 100, 1, '');
    mass('--mh-fade', d.bottomFadePercent, 18, '%');
    wrap.setAttribute('data-fade', String(d.bottomFadePercent == null ? 18 : d.bottomFadePercent));

    var buehne = el('div', 'mh-buehne');
    wrap.appendChild(buehne);

    var folien = items.map(function (item, i) {
      var f = el('div', 'mh-folie mh-uebergang-' + (d.transition || 'Fade').toLowerCase());
      f.setAttribute('aria-hidden', i === 0 ? 'false' : 'true');

      var bild = el('div', 'mh-bild');
      f.appendChild(bild);
      f.appendChild(el('div', 'mh-scrim'));

      var text = el('div', 'mh-text');

      var logo = bildUrl(item, 'Logo', 520);
      if (d.showLogo && logo) {
        var img = el('img', 'mh-logo');
        img.alt = item.Name;
        // The first slide's logo is visible immediately - lazy loading would
        // mean it pops in afterwards.
        img.loading = i === 0 ? 'eager' : 'lazy';
        img.decoding = 'async';
        if (i === 0 && 'fetchPriority' in img) { img.fetchPriority = 'high'; }
        img.src = logo;
        text.appendChild(img);
      } else if (d.showTitleWhenNoLogo !== false) {
        text.appendChild(el('h2', 'mh-titel', item.Name));
      }

      var meta = el('div', 'mh-meta');
      if (d.showYear && item.Year) meta.appendChild(el('span', 'mh-chip', String(item.Year)));
      if (d.showRating && item.Rating) {
        meta.appendChild(el('span', 'mh-chip mh-stern', '★ ' + item.Rating.toFixed(1)));
      }
      if (d.showOfficialRating && item.OfficialRating) {
        meta.appendChild(el('span', 'mh-chip mh-fsk', item.OfficialRating));
      }
      if (d.showRuntime && item.RuntimeMinutes) {
        meta.appendChild(el('span', 'mh-chip', minuten(item.RuntimeMinutes)));
      }
      if (d.showGenres && item.Genres && item.Genres.length) {
        meta.appendChild(el('span', 'mh-chip', item.Genres.slice(0, 3).join(' · ')));
      }
      if (meta.childNodes.length) text.appendChild(meta);

      if (d.showOverview && item.Overview) {
        var p = el('p', 'mh-inhalt', item.Overview);
        p.style.setProperty('--mh-zeilen', d.overviewLines || 3);
        text.appendChild(p);
      }

      var knoepfe = el('div', 'mh-knoepfe');
      if (d.showPlayButton) {
        knoepfe.appendChild(knopf('▶  ' + t('play'), 'mh-primaer', function () {
          navigiere('#/details?id=' + item.Id + '&play=1', optionen);
        }, optionen));
      }
      if (d.showDetailsButton) {
        knoepfe.appendChild(knopf(t('details'), '', function () {
          navigiere('#/details?id=' + item.Id, optionen);
        }, optionen));
      }
      if (d.showTrailerButton) {
        knoepfe.appendChild(knopf(t('trailer'), '', function () {
          navigiere('#/details?id=' + item.Id, optionen);
        }, optionen));
      }
      if (d.showFavoriteButton) {
        knoepfe.appendChild(knopf('♡', 'mh-rund', function () { }, optionen));
      }
      if (knoepfe.childNodes.length) text.appendChild(knoepfe);

      f.appendChild(text);
      buehne.appendChild(f);
      return { el: f, bild: bild, item: item, geladen: false };
    });

    // Position indicator
    var anzeige = null;
    var art = d.indicator || 'Dots';
    if (art !== 'None' && items.length > 1) {
      anzeige = el('div', 'mh-anzeige mh-anzeige-' + art.toLowerCase());
      for (var i = 0; i < items.length; i++) {
        (function (n) {
          var punkt = el('button', 'mh-punkt');
          punkt.type = 'button';
          punkt.setAttribute('aria-label', t('eintrag') + ' ' + (n + 1));
          if (art === 'Numbers') punkt.textContent = String(n + 1);
          if (art === 'Thumbnails') {
            // Do NOT name this t: it would shadow the label function t() in
            // the whole enclosing scope (var hoisting).
            var vorschau = bildUrl(items[n], 'Thumb', 200)
              || bildUrl(items[n], 'Backdrop', 200);
            if (vorschau) punkt.style.backgroundImage = 'url("' + vorschau + '")';
          }
          punkt.addEventListener('click', function () { zeige(n, true); });
          anzeige.appendChild(punkt);
        })(i);
      }
      wrap.appendChild(anzeige);
    }

    ziel.appendChild(wrap);

    // ------------------------------------------------------------- Playback
    var aktuell = -1;
    var timer = null;
    var pausiert = false;

    function laden(n) {
      var f = folien[n];
      if (!f || f.geladen) return;
      var u = bildUrl(f.item, 'Backdrop', 1920) || bildUrl(f.item, 'Thumb', 1920);
      if (u) f.bild.style.backgroundImage = 'url("' + u + '")';
      f.geladen = true;
    }

    function zeige(n, manuell) {
      if (n === aktuell) return;
      n = (n + folien.length) % folien.length;
      folien.forEach(function (f, i) {
        f.el.classList.toggle('mh-aktiv', i === n);
        f.el.setAttribute('aria-hidden', i === n ? 'false' : 'true');
      });
      if (anzeige) {
        // Distance to the current slide as an attribute - the CSS turns it into
        // a small window of dots that moves along. Distant dots get width 0
        // and drop out of the flow instead of covering the banner with thirty
        // dots.
        Array.prototype.forEach.call(anzeige.children, function (k, i) {
          k.classList.toggle('mh-aktiv', i === n);
          k.setAttribute('data-fern', String(Math.min(Math.abs(i - n), 4)));
        });
      }
      aktuell = n;
      laden(n);
      laden((n + 1) % folien.length); // preload only the next slide
      if (manuell) neuStarten();
    }

    function weiter() { zeige(aktuell + 1); }

    function neuStarten() {
      if (timer) { clearInterval(timer); timer = null; }
      if (statisch || !d.autoplay || folien.length < 2 || optionen.ohneAblauf) return;
      timer = setInterval(function () { if (!pausiert) weiter(); },
        Math.max(2, d.intervalSeconds || 9) * 1000);
    }

    if (d.pauseOnHover !== false) {
      wrap.addEventListener('mouseenter', function () { pausiert = true; });
      wrap.addEventListener('mouseleave', function () { pausiert = false; });
    }

    if (d.enableSwipe !== false) {
      var x0 = null;
      wrap.addEventListener('touchstart', function (e) { x0 = e.touches[0].clientX; }, { passive: true });
      wrap.addEventListener('touchend', function (e) {
        if (x0 == null) return;
        var dx = e.changedTouches[0].clientX - x0;
        if (Math.abs(dx) > 45) zeige(aktuell + (dx < 0 ? 1 : -1), true);
        x0 = null;
      }, { passive: true });
    }

    var tasten = null;
    if (d.enableKeyboard !== false && !optionen.ohneAblauf) {
      tasten = function (e) {
        if (!document.body.contains(wrap)) return;
        if (e.key === 'ArrowRight') zeige(aktuell + 1, true);
        if (e.key === 'ArrowLeft') zeige(aktuell - 1, true);
      };
      document.addEventListener('keydown', tasten);
    }

    // Pause the timer while the banner is not visible - saves work in
    // background tabs and when scrolled down.
    var beobachter = null;
    if (window.IntersectionObserver && !optionen.ohneAblauf) {
      beobachter = new IntersectionObserver(function (e) {
        pausiert = !e[0].isIntersecting;
      }, { threshold: 0.15 });
      beobachter.observe(wrap);
    }

    zeige(0);
    neuStarten();

    return {
      zerstoeren: function () {
        if (timer) clearInterval(timer);
        if (tasten) document.removeEventListener('keydown', tasten);
        if (beobachter) beobachter.disconnect();
        ziel.innerHTML = '';
      },
      zeige: zeige,
    };
  }

  function knopf(text, klasse, aktion, optionen) {
    var b = el('button', 'mh-knopf ' + (klasse || ''), text);
    b.type = 'button';
    if (!optionen.ohneAblauf) b.addEventListener('click', aktion);
    return b;
  }

  function navigiere(hash, optionen) {
    if (optionen && optionen.ohneAblauf) return;
    window.location.hash = hash;
  }

  // ----------------------------------------------------------------- Mounting
  function istStartseite() {
    var h = window.location.hash || '';
    return h.indexOf('home') !== -1 || h === '' || h === '#/' || h === '#!/';
  }

  /*
   * The anchor the banner is inserted BEFORE.
   *
   * Important: not inside it. The web client builds the sections with
   *     e.innerHTML = h;  e.classList.add("homeSectionsContainer");
   * which immediately throws out any inserted child. As a sibling in front
   * of it, the banner survives every rebuild.
   */
  function anker() {
    var k = document.querySelector('.homeSectionsContainer')
      || document.querySelector('.homePage .sections')
      || document.querySelector('[data-type="home"] .sections');
    return k && k.parentNode ? k : null;
  }

  var daten = null;
  // Whose list `daten` is. After signing out and in as someone else in the same tab it must not be shown again.
  var datenFuer = '';
  var laeuft = false;
  var netzLaeuft = null;

  /* The browser-side cache.

     The reason: the fetch used to start only once Jellyfin's home screen was
     done - after about half a second - and then network, build and image came
     on top. The banner visibly popped in late.

     With a stored copy it is there instantly on the second visit, without
     waiting for a single request. The fresh list is fetched alongside and
     only redrawn if it really differs - otherwise it flickers on every visit.

     The key includes the user id: on a shared device nobody should briefly
     see the previous user's list. */
  var SPEICHER_TAGE = 7;

  /* The user id - even when ApiClient is not ready yet.

     This is exactly where the cache failed at first: on the early read
     ApiClient was not there yet, so the id was empty; on the later write it
     was set. Two different keys, never a hit. But the web client stores its
     credentials in local storage itself, and that is available from the very
     first moment. */
  function nutzerKennung() {
    try {
      if (window.ApiClient && ApiClient.getCurrentUserId) {
        var u = ApiClient.getCurrentUserId();
        if (u) return u;
      }
    } catch (e) { /* ignore */ }
    try {
      var roh = localStorage.getItem('jellyfin_credentials');
      if (roh) {
        var o = JSON.parse(roh);
        var srv = o && o.Servers && o.Servers[0];
        if (srv && srv.UserId) return srv.UserId;
      }
    } catch (e) { /* ignore */ }
    return '';
  }

  var SPEICHER_PRAEFIX = 'mediahero.liste.v1.';

  function speicherSchluessel(wer) {
    return SPEICHER_PRAEFIX + (wer == null ? nutzerKennung() : wer);
  }

  function ausSpeicher() {
    try {
      var roh = localStorage.getItem(speicherSchluessel());
      if (!roh) return null;
      var o = JSON.parse(roh);
      if (!o || !o.stand || !o.daten) return null;
      if (Date.now() - o.stand > SPEICHER_TAGE * 86400000) return null;
      return o.daten;
    } catch (e) { return null; }
  }

  function inSpeicher(j, wer) {
    try {
      if (!wer) return;
      // Only the current user's list stays on the device: on a shared one nobody can read another user's titles.
      for (var i = localStorage.length - 1; i >= 0; i--) {
        var k = localStorage.key(i);
        if (k && k.indexOf(SPEICHER_PRAEFIX) === 0 && k !== speicherSchluessel(wer)) localStorage.removeItem(k);
      }
      localStorage.setItem(speicherSchluessel(wer),
        JSON.stringify({ stand: Date.now(), daten: j }));
    } catch (e) { /* full or blocked - then do without */ }
  }

  /* Does the web client already have a token?

     Important, because requests sent too early otherwise go out
     unauthenticated and come back with 401 - measured at 190 ms, long before
     ApiClient was ready. That fetch was wasted and had to be repeated later.
     Better to wait a few milliseconds than to throw a request away. */
  function tokenDa() {
    try {
      return !!(window.ApiClient && ApiClient.accessToken && ApiClient.accessToken());
    } catch (e) { return false; }
  }

  function sobaldBereit() {
    return new Promise(function (fertig) {
      if (tokenDa()) { fertig(); return; }
      var n = 0;
      var uhr = setInterval(function () {
        // Give up after about three seconds and try anyway - without a
        // login there is nothing to show anyway.
        if (tokenDa() || ++n > 100) { clearInterval(uhr); fertig(); }
      }, 30);
    });
  }

  function vomServer() {
    if (netzLaeuft) return netzLaeuft;
    var fuer = '';
    netzLaeuft = sobaldBereit().then(function () {
      fuer = nutzerKennung();
      var kopf = {};
      try {
        if (window.ApiClient && ApiClient.accessToken()) {
          kopf['Authorization'] = 'MediaBrowser Token="' + ApiClient.accessToken() + '"';
        }
      } catch (e) { /* ignore */ }
      return fetch(basis() + '/MediaHero/items', { headers: kopf, credentials: 'same-origin' });
    })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        netzLaeuft = null;
        // Someone else signed in while the request was under way: this list is not theirs.
        if (!fuer || fuer !== nutzerKennung()) return null;
        if (j && j.Items && j.Items.length) { inSpeicher(j, fuer); vorladen(j); }
        return j;
      })
      .catch(function () { netzLaeuft = null; return null; });
    return netzLaeuft;
  }

  /* Announce the first image as soon as the list is known.

     The browser then loads it in parallel with everything else instead of
     only once the slide is in the DOM. The URL must match the one used when
     drawing character for character - otherwise it is loaded twice. */
  function vorladen(j) {
    try {
      if (!j || !j.Items || !j.Items.length) return;
      if (document.getElementById('mh-vorladen')) return;
      var u = bildUrl(j.Items[0], 'Backdrop', 1920) || bildUrl(j.Items[0], 'Thumb', 1920);
      if (!u) return;
      var l = document.createElement('link');
      l.id = 'mh-vorladen';
      l.rel = 'preload';
      l.as = 'image';
      l.href = u;
      if ('fetchPriority' in l) { l.fetchPriority = 'high'; }
      document.head.appendChild(l);
    } catch (e) { /* ignore */ }
  }

  function neuZeichnen() {
    var vorhanden = document.getElementById(WURZEL);
    if (vorhanden && vorhanden.parentNode) { vorhanden.parentNode.removeChild(vorhanden); }
    if (zustand) { zustand.zerstoeren(); zustand = null; }
    einhaengen();
  }

  function holen() {
    var wer = nutzerKennung();
    if (daten && datenFuer === wer) return Promise.resolve(daten);
    daten = null;
    datenFuer = wer;

    var alt = ausSpeicher();
    if (alt && alt.Items && alt.Items.length) {
      daten = alt;
      vorladen(alt);
      // Refresh alongside. Redraw only on a real change.
      vomServer().then(function (j) {
        if (!j || !j.Items || datenFuer !== wer) return;
        if (j.BuiltAtUnix && alt.BuiltAtUnix && j.BuiltAtUnix === alt.BuiltAtUnix) return;
        daten = j;
        neuZeichnen();
      });
      return Promise.resolve(alt);
    }

    return vomServer().then(function (j) {
      if (datenFuer !== wer) return null;
      daten = j;
      return j;
    });
  }

  function einhaengen() {
    if (laeuft) return;
    if (!istStartseite()) {
      if (zustand) { zustand.zerstoeren(); zustand = null; }
      kopfBeobachten(false);
      return;
    }
    var ort = anker();
    if (!ort) return;
    var vorhanden = document.getElementById(WURZEL);
    if (vorhanden && vorhanden.getAttribute('data-nutzer') !== nutzerKennung()) {
      // A banner drawn for the previous user of this tab.
      if (vorhanden.parentNode) vorhanden.parentNode.removeChild(vorhanden);
      if (zustand) { zustand.zerstoeren(); zustand = null; }
      vorhanden = null;
    }
    if (vorhanden) return;

    laeuft = true;
    holen().then(function (j) {
      laeuft = false;
      if (!j || !j.Display || j.Display.enabled === false) return;
      if (!istStartseite()) return;
      var ort2 = anker();
      if (!ort2 || document.getElementById(WURZEL)) return;

      var host = el('div', 'mh-host');
      host.id = WURZEL;
      host.setAttribute('data-nutzer', datenFuer);
      // as a sibling BEFORE it, not as a child - otherwise the next
      // innerHTML rebuild of the sections removes the banner
      ort2.parentNode.insertBefore(host, ort2);

      if (j.Display.customCss) {
        var s = document.getElementById('mh-eigenes-css') || el('style');
        s.id = 'mh-eigenes-css';
        s.textContent = j.Display.customCss;
        document.head.appendChild(s);
      }

      if (j.Display.blendIntoHeader !== false) {
        host.classList.add('mh-verschmolzen');
        kopfBeobachten(true);
      } else {
        kopfBeobachten(false);
      }

      zustand = render(host, j.Items, j.Display, {});
    });
  }

  /*
   * Pull the banner up under the header bar.
   *
   * The height is measured rather than guessed - it differs by window width
   * and Jellyfin version. The header is only transparent while the page is at
   * the very top; as soon as it scrolls, it gets its background back,
   * otherwise the navigation disappears into the image.
   */
  /*
   * Find the header bar without knowing its class name.
   *
   * Jellyfin 12 mixes the old UI with Material UI; depending on the view the
   * element is .skinHeader or .MuiAppBar-root. Instead of guessing, take
   * whatever measurably sticks to the top: wide, anchored at the top, fixed
   * position and of plausible height. That also survives future rewrites.
   */
  function kopfElement() {
    var kandidaten = document.querySelectorAll(
      '.skinHeader, header, .MuiAppBar-root, [class*="appBar"], [class*="skinHeader"]');
    var beste = null;
    for (var i = 0; i < kandidaten.length; i++) {
      var e = kandidaten[i];
      var r = e.getBoundingClientRect();
      var s = getComputedStyle(e);
      if (r.width < 400 || r.height < 24 || r.height > 200 || r.top > 4) continue;
      if (['fixed', 'absolute', 'sticky'].indexOf(s.position) === -1) continue;
      if (!beste || r.height > beste.h) beste = { e: e, h: r.height };
    }
    return beste ? beste.e : null;
  }

  var kopfMerker = null;

  function kopfMessen() {
    var kopf = kopfElement();
    if (kopfMerker && kopfMerker !== kopf) kopfMerker.classList.remove('mh-kopf-el');
    kopfMerker = kopf;
    var h = kopf ? Math.round(kopf.getBoundingClientRect().height) : 0;
    if (kopf) kopf.classList.add('mh-kopf-el');
    document.documentElement.style.setProperty('--mh-kopf', h + 'px');
    return h;
  }

  var kopfAktiv = false;

  function kopfBeobachten(an) {
    kopfAktiv = an;
    if (!an) {
      document.body.classList.remove('mh-kopf-frei');
      if (kopfMerker) { kopfMerker.classList.remove('mh-kopf-el'); kopfMerker = null; }
      return;
    }
    // The UI sometimes builds the header later than the banner; so measure
    // a few times instead of guessing once.
    [0, 150, 400, 900, 1800].forEach(function (t) {
      setTimeout(function () { if (kopfAktiv) { kopfMessen(); pruefeScroll(); } }, t);
    });
  }

  function scrollWert() {
    var k = document.querySelector('.mainAnimatedPage.skinBody')
      || document.querySelector('.skinBody')
      || document.scrollingElement;
    return Math.max(
      window.pageYOffset || 0,
      k && k.scrollTop ? k.scrollTop : 0);
  }

  function pruefeScroll() {
    if (!kopfAktiv) return;
    var oben = scrollWert() < 30;
    var host = document.getElementById(WURZEL);
    document.body.classList.toggle('mh-kopf-frei', oben && !!host);
  }

  function stil() {
    if (document.getElementById('mh-css')) return;
    var l = document.createElement('link');
    l.id = 'mh-css';
    l.rel = 'stylesheet';
    l.href = basis() + '/MediaHero/client.css';
    document.head.appendChild(l);
  }

  window.MediaHero = { render: render, bildUrl: bildUrl };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', starten);
  } else {
    starten();
  }

  function starten() {
    stil();
    /* Kick off immediately.

       This is the biggest single win: the fetch used to start only after
       Jellyfin's home screen had finished rendering, because einhaengen()
       bailed out earlier on the missing anchor. So Jellyfin's build and ours
       ran one after the other instead of in parallel. Now it is started
       here - by the time the home screen is up, the list is usually ready. */
    holen();
    einhaengen();
    window.addEventListener('hashchange', function () {
      /* This used to discard everything and refetch on the next build - which
         cost a request on every back-and-forth, during which the banner was
         missing. Now the existing one stays and is checked alongside. */
      vomServer().then(function (j) {
        if (!j || !j.Items) return;
        if (daten && datenFuer === nutzerKennung() && j.BuiltAtUnix && daten.BuiltAtUnix === j.BuiltAtUnix) return;
        daten = j;
        datenFuer = nutzerKennung();
        neuZeichnen();
      });
      setTimeout(einhaengen, 60);
    });
    // The web client rebuilds the home screen without changing the URL.
    // The observer fires on every DOM change. Without throttling that would be
    // hundreds of calls per page build - throttled it costs practically nothing.
    var bremse = null;
    ['scroll', 'wheel', 'touchmove'].forEach(function (e) {
      document.addEventListener(e, pruefeScroll, { passive: true, capture: true });
    });
    window.addEventListener('resize', function () { if (kopfAktiv) kopfMessen(); });

    var mo = new MutationObserver(function () {
      if (bremse) return;
      bremse = setTimeout(function () { bremse = null; einhaengen(); }, 120);
    });
    mo.observe(document.body, { childList: true, subtree: true });
  }
})();
