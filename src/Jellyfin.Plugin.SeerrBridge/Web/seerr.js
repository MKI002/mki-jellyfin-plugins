/*
 * Seerr Bridge - client part.
 *
 * Three jobs: a button in the header and side menu, an embedded view under
 * #/seerr, and search suggestions on the search page.
 *
 * Ground rule when touching someone else's UI: do not rebuild what is already
 * there. The buttons are therefore CLONED from an existing button and only
 * relabelled. Jellyfin 12 builds its header with Material UI, whose class names
 * carry a build hash (css-1f20jcn). Copying those leaves an unstyled link in
 * the header after the next Jellyfin update. A clone inherits everything and
 * survives that.
 */
(function () {
  'use strict';

  var ROUTE = '#/seerr';
  var cfg = null;
  var beobachter = null;
  var suchTakt = null;
  var ssoVersucht = false;

  /* UI strings. English only, regardless of the client's language. */
  var TEXT = { titel: "Requests", vorschlaege: "Available via Seerr", anfragen: "Request", laeuft: "requesting …", fertig: "Requested", vorhanden: "in your library", angefragt: "already requested", arbeit: "being fetched", teilweise: "partially available", fehler: "Did not work", oeffnen: "Open in Seerr", rahmenLeer: "Seerr cannot be shown here. Usually the address is wrong – an http address will not load inside an https page. Currently set:" };

  function t(schluessel) {
    return TEXT[schluessel] || schluessel;
  }

  function basis() {
    try {
      if (window.ApiClient && window.ApiClient.serverAddress) {
        return window.ApiClient.serverAddress();
      }
    } catch (e) { /* ignore */ }
    return '';
  }

  function kopfZeilen() {
    var h = { Accept: 'application/json' };
    try {
      if (window.ApiClient && window.ApiClient.accessToken) {
        var tk = window.ApiClient.accessToken();
        if (tk) { h.Authorization = 'MediaBrowser Token="' + tk + '"'; }
      }
    } catch (e) { /* ignore */ }
    return h;
  }

  function holen(pfad, optionen) {
    optionen = optionen || {};
    optionen.headers = Object.assign(kopfZeilen(), optionen.headers || {});
    return fetch(basis() + pfad, optionen).then(function (r) {
      if (!r.ok) { throw new Error('HTTP ' + r.status); }
      return r.json();
    });
  }

  /* ---------------------------------------------------------------- Buttons */

  /* An existing library button in the header - both the template and the
     insertion point. */
  function vorbildKopf() {
    return document.querySelector(
      'a.MuiButton-root[href^="#/movies"], a.MuiButton-root[href^="#/tv?"],'
      + ' a.MuiButton-root[href^="#/livetv"], a.MuiButton-root[href^="#/home?"]');
  }

  /* Cloud with a down arrow. Same icon in the side menu, there via the
     cloud_download class - it is defined in Jellyfin's stylesheet because the
     client uses it itself. Only such icons can be set via class; made-up names
     would render as an empty box. */
  var SYMBOL = 'M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0'
    + ' 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM17'
    + ' 13l-5 5-5-5h3V9h4v4h3z';

  /* Button label.

     A text entered in the settings wins - whoever deliberately sets something
     else wants to see it. If the field is empty (default), the English
     default label is used. */
  function beschriftung() {
    return (cfg && cfg.label) ? cfg.label : t('titel');
  }

  function kopfKnopfSetzen() {
    if (!cfg || !cfg.enabled || !cfg.showInHeader) { return; }

    var schon = document.querySelector('a[data-seerr-kopf]');
    if (schon) {
      // The label can change while running (e.g. settings changed without a
      // reload). Then the button has to follow.
      var soll = beschriftung();
      if (schon.textContent.trim() !== soll) {
        Array.prototype.slice.call(schon.childNodes).forEach(function (n) {
          if (n.nodeType === 3) { schon.removeChild(n); }
        });
        schon.appendChild(document.createTextNode(soll));
      }
      return;
    }

    var vorbild = vorbildKopf();
    if (!vorbild || !vorbild.parentElement) { return; }

    var knopf = vorbild.cloneNode(true);
    knopf.setAttribute('href', ROUTE);
    knopf.setAttribute('data-seerr-kopf', '1');
    knopf.removeAttribute('aria-current');

    var pfad = knopf.querySelector('svg path');
    if (pfad) { pfad.setAttribute('d', SYMBOL); }
    var svg = knopf.querySelector('svg');
    if (svg) { svg.setAttribute('data-testid', 'SeerrIcon'); }

    // The label is a bare text node after the icon. Remove all text nodes
    // and append our own - that leaves the icon wrapper untouched.
    Array.prototype.slice.call(knopf.childNodes).forEach(function (n) {
      if (n.nodeType === 3) { knopf.removeChild(n); }
    });
    knopf.appendChild(document.createTextNode(beschriftung()));

    vorbild.parentElement.appendChild(knopf);
  }

  function menuEintragSetzen() {
    if (!cfg || !cfg.enabled || !cfg.showInDrawer) { return; }

    var schon = document.querySelector('a[data-seerr-menu]');
    if (schon) {
      var text = schon.querySelector('.navMenuOptionText');
      if (text && text.textContent !== beschriftung()) {
        text.textContent = beschriftung();
      }
      return;
    }

    var liste = document.querySelector('.libraryMenuOptions');
    var vorbild = liste && liste.querySelector('a.lnkMediaFolder');
    if (!liste || !vorbild) { return; }

    var eintrag = vorbild.cloneNode(true);
    eintrag.setAttribute('href', ROUTE);
    eintrag.setAttribute('data-seerr-menu', '1');
    eintrag.removeAttribute('data-itemid');

    var symbol = eintrag.querySelector('.navMenuOptionIcon');
    if (symbol) {
      // Jellyfin sets the icon via the class, not via the content.
      symbol.className = 'material-icons navMenuOptionIcon cloud_download';
    }
    var text = eintrag.querySelector('.navMenuOptionText');
    if (text) { text.textContent = beschriftung(); }

    liste.appendChild(eintrag);
  }

  /* ---------------------------------------------------------- Embedded view */

  function kopfHoehe() {
    var kopf = document.querySelector('header.MuiAppBar-root, .skinHeader');
    var h = kopf ? Math.round(kopf.getBoundingClientRect().height) : 0;
    return h > 0 ? h : 56;
  }

  /* Which path inside Seerr should be shown?

     Passed as a parameter in the address: #/seerr?ziel=%2Fmovie%2F1234
     Only paths are accepted, no full URLs - otherwise a planted link could
     open a foreign page in the frame that then looks like part of Jellyfin. */
  function zielAusHash() {
    var h = location.hash || '';
    var i = h.indexOf('?');
    if (i < 0) { return '/'; }
    var z = '';
    try {
      z = new URLSearchParams(h.slice(i + 1)).get('ziel') || '';
    } catch (e) { z = ''; }
    if (!z || z.charAt(0) !== '/' || z.charAt(1) === '/') { return '/'; }
    return z;
  }

  function ansichtZeigen() {
    var ziel = zielAusHash();
    var vorhanden = document.getElementById('seerrAnsicht');
    if (vorhanden) {
      vorhanden.style.top = kopfHoehe() + 'px';
      // When jumping from one result to the next the frame stays and only
      // gets a new address - faster than rebuilding it, and it keeps the
      // sign-in.
      var f = vorhanden.querySelector('iframe');
      if (f && f.getAttribute('data-ziel') !== ziel) {
        f.setAttribute('data-ziel', ziel);
        f.src = cfg.url + ziel;
      }
      return;
    }

    var huelle = document.createElement('div');
    huelle.id = 'seerrAnsicht';
    huelle.className = 'sb-ansicht';
    huelle.style.top = kopfHoehe() + 'px';

    var rahmen = document.createElement('iframe');
    rahmen.className = 'sb-rahmen';
    rahmen.setAttribute('title', 'Seerr');
    // allow-same-origin is needed so Seerr keeps its session; without it
    // the frame would be signed out on every visit.
    rahmen.setAttribute('allow', 'fullscreen');
    rahmen.setAttribute('data-ziel', ziel);

    // If the frame stays empty, the cause is almost always the address: the
    // browser silently blocks an http page inside an https page, and a
    // LAN-only address is not reachable from outside. An empty blue box tells
    // nobody that - this hint does.
    var geladen = false;
    rahmen.addEventListener('load', function () { geladen = true; });
    setTimeout(function () {
      if (geladen || !huelle.parentNode) { return; }
      var hinweis = document.createElement('div');
      hinweis.className = 'sb-notfall';
      hinweis.innerHTML = '<p></p><p class="sb-adresse"></p><p></p>';
      hinweis.querySelector('p').textContent = t('rahmenLeer');
      hinweis.querySelector('.sb-adresse').textContent = cfg.url;
      var link = document.createElement('a');
      link.href = cfg.url;
      link.target = '_blank';
      link.rel = 'noopener';
      link.textContent = t('oeffnen');
      hinweis.lastElementChild.appendChild(link);
      huelle.appendChild(hinweis);
    }, 6000);

    huelle.appendChild(rahmen);

    document.body.appendChild(huelle);
    document.body.classList.add('sb-aktiv');

    /* Pass on the sign-in first, then load.

       The order matters: if the frame loads first, the login form flashes up
       and a reload would be needed afterwards. The call costs a moment once
       per page load; after that the session lasts 30 days. If it fails, the
       frame loads anyway - just with the usual login form. */
    ssoDann(function () { rahmen.src = cfg.url + ziel; });
  }

  function ssoDann(weiter) {
    if (!cfg || !cfg.sso || ssoVersucht) { weiter(); return; }
    ssoVersucht = true;
    // If the server hangs, do not wait forever - better show the login form
    // than an empty box.
    var zeit = setTimeout(weiter, 4000);
    holen('/SeerrBridge/sso', { method: 'POST' })
      .then(function () { clearTimeout(zeit); weiter(); })
      .catch(function () { clearTimeout(zeit); weiter(); });
  }

  function ansichtVerbergen() {
    var h = document.getElementById('seerrAnsicht');
    if (h && h.parentNode) { h.parentNode.removeChild(h); }
    document.body.classList.remove('sb-aktiv');
  }

  function routePruefen() {
    if (!cfg || !cfg.enabled) { return; }
    var aktiv = (location.hash || '').indexOf('#/seerr') !== -1;
    if (aktiv) { ansichtZeigen(); } else { ansichtVerbergen(); }
  }

  /* ----------------------------------------------------------------- Search */

  function suchFeld() {
    var kandidaten = document.querySelectorAll(
      'input[type="search"], input.searchfields-txtSearch, #searchTextInput,'
      + ' .searchFields input, .searchfields input, input[placeholder]');
    for (var i = 0; i < kandidaten.length; i++) {
      var e = kandidaten[i];
      if (e.offsetParent !== null && e.type !== 'password') { return e; }
    }
    return null;
  }

  function suchSeite() {
    return (location.hash || '').indexOf('#/search') === 0;
  }

  function zielBehaelter() {
    // The visible content of the search page. Several candidates, because
    // Jellyfin mounts the search page differently depending on version.
    return document.querySelector('.searchResults')
      || document.querySelector('.mainAnimatedPage:not(.hide) .padded-left')
      || document.querySelector('.mainAnimatedPage:not(.hide)')
      || document.querySelector('#reactRoot > div:last-child');
  }

  /* Place after Jellyfin's own result groups.
     The user's own library comes first - nobody wants to hunt for what they
     already have among suggestions. Jellyfin creates each group ("Movies",
     "Shows") as its own section; inserting after the last of them is more
     reliable than appending to the container, because Jellyfin finishes its
     groups after us. */
  function einordnen(a) {
    var gruppen = document.querySelectorAll(
      '.mainAnimatedPage:not(.hide) .verticalSection, .searchResults .verticalSection');
    var letzte = null;
    for (var i = gruppen.length - 1; i >= 0; i--) {
      if (gruppen[i] !== a && !a.contains(gruppen[i])) { letzte = gruppen[i]; break; }
    }

    if (letzte && letzte.parentNode) {
      if (letzte.nextSibling !== a) {
        letzte.parentNode.insertBefore(a, letzte.nextSibling);
      }
      return true;
    }

    var ziel = zielBehaelter();
    if (!ziel) { return false; }
    if (ziel.lastElementChild !== a) { ziel.appendChild(a); }
    return true;
  }

  function abschnitt() {
    var vorhanden = document.getElementById('seerrTreffer');
    if (vorhanden) { return vorhanden; }

    var a = document.createElement('div');
    a.id = 'seerrTreffer';
    // Reuse Jellyfin's own classes instead of inventing spacing: the left
    // margin is not on the section but in "padded-left" on the heading and
    // tile row. Rebuilding it means a fixed pixel value that is wrong again on
    // narrow devices - Jellyfin varies this spacing with window width.
    a.className = 'sb-abschnitt verticalSection';
    a.innerHTML =
      '<h2 class="sb-ueberschrift sectionTitle sectionTitle-cards padded-left padded-right"></h2>'
      + '<div class="sb-reihe padded-left padded-right"></div>';
    a.querySelector('.sb-ueberschrift').textContent = t('vorschlaege');
    return einordnen(a) ? a : null;
  }

  function zustandText(z) {
    if (z === 'vorhanden') { return t('vorhanden'); }
    if (z === 'angefragt') { return t('angefragt'); }
    if (z === 'in Arbeit') { return t('arbeit'); }
    if (z === 'teilweise') { return t('teilweise'); }
    return '';
  }

  /* Address of a result in Seerr. Movies live under /movie/<id>, shows
     under /tv/<id> - the same TMDB id in both cases. */
  function trefferZiel(e) {
    return ROUTE + '?ziel='
      + encodeURIComponent('/' + (e.mediaType === 'tv' ? 'tv' : 'movie') + '/' + e.tmdbId);
  }

  function kachel(e) {
    // A real link, not a click handler: middle-click, keyboard navigation and
    // the link target preview work like everywhere else.
    var k = document.createElement('a');
    k.className = 'sb-kachel';
    k.setAttribute('href', trefferZiel(e));
    k.setAttribute('title', e.overview || e.title);

    var bild = document.createElement('div');
    bild.className = 'sb-bild';
    if (e.poster) { bild.style.backgroundImage = 'url("' + e.poster + '")'; }
    k.appendChild(bild);

    var titel = document.createElement('div');
    titel.className = 'sb-titel';
    titel.textContent = e.title + (e.year ? ' (' + e.year + ')' : '');
    titel.title = e.overview || '';
    k.appendChild(titel);

    var unten = document.createElement('div');
    unten.className = 'sb-unten';

    if (e.requestable) {
      var knopf = document.createElement('button');
      knopf.type = 'button';
      knopf.className = 'sb-knopf';
      knopf.textContent = t('anfragen');
      knopf.addEventListener('click', function (ereignis) {
        // The button sits inside a link - without this a click on
        // "Request" would also open the detail page.
        ereignis.preventDefault();
        ereignis.stopPropagation();
        knopf.disabled = true;
        knopf.textContent = t('laeuft');
        holen('/SeerrBridge/request', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ MediaType: e.mediaType, TmdbId: e.tmdbId }),
        }).then(function (r) {
          knopf.textContent = r && r.ok ? t('fertig') : (r && r.text) || t('fehler');
          knopf.classList.add(r && r.ok ? 'sb-ok' : 'sb-fehler');
        }).catch(function () {
          knopf.textContent = t('fehler');
          knopf.classList.add('sb-fehler');
        });
      });
      unten.appendChild(knopf);
    } else {
      var hinweis = document.createElement('span');
      hinweis.className = 'sb-hinweis';
      hinweis.textContent = zustandText(e.status);
      unten.appendChild(hinweis);
    }

    k.appendChild(unten);
    return k;
  }

  function suchen(begriff) {
    if (!cfg || !cfg.search || !suchSeite()) { return; }
    if (!begriff || begriff.length < 2) {
      var alt = document.getElementById('seerrTreffer');
      if (alt && alt.parentNode) { alt.parentNode.removeChild(alt); }
      return;
    }

    holen('/SeerrBridge/search?q=' + encodeURIComponent(begriff)).then(function (d) {
      var a = abschnitt();
      if (!a) { return; }
      // The heading is set on creation, but the section is reused. This keeps
      // the heading text in sync if it ever changes.
      var ueber = a.querySelector('.sb-ueberschrift');
      if (ueber && ueber.textContent !== t('vorschlaege')) {
        ueber.textContent = t('vorschlaege');
      }
      var reihe = a.querySelector('.sb-reihe');
      reihe.innerHTML = '';
      var items = (d && d.items) || [];
      if (!items.length) {
        a.parentNode.removeChild(a);
        return;
      }

      items.forEach(function (e) { reihe.appendChild(kachel(e)); });

      einordnen(a);
    }).catch(function () { /* silent - search must not fail because of this */ });
  }

  function sucheAnbinden() {
    if (!cfg || !cfg.search) { return; }
    var feld = suchFeld();
    if (!feld || feld.getAttribute('data-seerr-gebunden')) { return; }
    feld.setAttribute('data-seerr-gebunden', '1');
    feld.addEventListener('input', function () {
      var wert = feld.value;
      if (suchTakt) { clearTimeout(suchTakt); }
      // Deliberately slow: searching on every keystroke loads Seerr and
      // returns results for half-typed words.
      suchTakt = setTimeout(function () { suchen(wert); }, 450);
    });
  }

  /* ---------------------------------------------------------------- Startup */

  function auffrischen() {
    kopfKnopfSetzen();
    menuEintragSetzen();
    routePruefen();
    if (suchSeite()) {
      sucheAnbinden();
      // Jellyfin rebuilds its groups on every keystroke - afterwards our
      // section is out of place and has to be re-positioned.
      var a = document.getElementById('seerrTreffer');
      if (a) { einordnen(a); }
    }
  }

  /* The settings are only given to signed-in users, so wait for the web client's
     token first. Checking costs nothing; on the sign-in page it simply keeps waiting. */
  function angemeldet() {
    try {
      return !!(window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken());
    } catch (e) { return false; }
  }

  function start() {
    if (!angemeldet()) { setTimeout(start, 1000); return; }
    holen('/SeerrBridge/config')
      .then(function (c) {
        cfg = c;
        if (!cfg || !cfg.enabled) { return; }

        var stil = document.createElement('link');
        stil.rel = 'stylesheet';
        stil.href = basis() + '/SeerrBridge/client.css';
        document.head.appendChild(stil);

        window.addEventListener('hashchange', auffrischen);
        window.addEventListener('resize', function () {
          var h = document.getElementById('seerrAnsicht');
          if (h) { h.style.top = kopfHoehe() + 'px'; }
        });

        // React rebuilds the header on every page change and throws out
        // anything foreign. The observer re-adds it afterwards - throttled,
        // because otherwise it fires on every little change in the tree.
        var geplant = false;
        beobachter = new MutationObserver(function () {
          if (geplant) { return; }
          geplant = true;
          setTimeout(function () { geplant = false; auffrischen(); }, 120);
        });
        beobachter.observe(document.body, { childList: true, subtree: true });

        /* Additionally, a quiet periodic re-check.
           The observer alone is not enough: while React rebuilds a page, it
           fires mid-rebuild. If the button disappears then or the template is
           missing at that moment, re-adding it does nothing - and afterwards
           no further change triggers it again. The button would stay gone until
           the next click. A periodic check costs one DOM query and makes this
           independent of timing. Short enough that the gap after a page change
           does not show as flicker. */
        setInterval(auffrischen, 400);

        auffrischen();
      })
      .catch(function () {
        /* Not configured, or the token was no longer valid: try again after the next sign-in. */
        setTimeout(start, 30000);
      });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
