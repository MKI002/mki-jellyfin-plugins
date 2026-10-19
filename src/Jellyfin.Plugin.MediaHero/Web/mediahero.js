/*
 * Media Hero - Startseiten-Leiste fuer Jellyfin 12
 *
 * Aufbau in zwei Teilen:
 *   MediaHero.render(...)  zeichnet eine Leiste in ein beliebiges Element.
 *                          Die Konfigurationsseite ruft genau diese Funktion
 *                          fuer ihre Vorschau auf - deshalb kann die Vorschau
 *                          gar nichts anderes zeigen als das Original.
 *   Der Beobachter unten   haengt die Leiste auf der Startseite ein.
 *
 * Zur Geschwindigkeit: der Server liefert die fertige Liste in einer einzigen
 * Antwort. Hier werden keine Eintraege einzeln nachgeladen, und es haengt genau
 * ein Bild je sichtbarer Folie im Speicher - das naechste wird vorgeladen, alle
 * anderen nicht. Kein Zeitgeber laeuft, solange die Leiste nicht zu sehen ist.
 */
(function () {
  'use strict';

  var WURZEL = 'mediahero-root';
  var zustand = null;

  /*
   * Beschriftungen.
   *
   * Die Sprache kommt vom Client, nicht vom Server: derselbe Server bedient
   * Leute mit unterschiedlichen Einstellungen. Jellyfin setzt die aktive
   * Sprache auf das <html lang>-Attribut; fehlt das, entscheidet der Browser.
   * Unbekannte Sprachen fallen auf Englisch zurueck, nicht auf Deutsch.
   */
  var SPRACHEN = {
    en: {
      play: 'Play', details: 'Details', trailer: 'Trailer', favorit: 'Favorite',
      leer: 'Nothing to show – filters too strict?',
      eintrag: 'Item', stunde: 'h', minute: 'min',
    },
    de: {
      play: 'Abspielen', details: 'Details', trailer: 'Trailer', favorit: 'Favorit',
      leer: 'Keine Einträge – Filter zu streng?',
      eintrag: 'Eintrag', stunde: 'Std.', minute: 'Min.',
    },
    fr: {
      play: 'Lecture', details: 'Détails', trailer: 'Bande-annonce', favorit: 'Favori',
      leer: 'Rien à afficher – filtres trop stricts ?',
      eintrag: 'Élément', stunde: 'h', minute: 'min',
    },
    es: {
      play: 'Reproducir', details: 'Detalles', trailer: 'Tráiler', favorit: 'Favorito',
      leer: 'Nada que mostrar – ¿filtros demasiado estrictos?',
      eintrag: 'Elemento', stunde: 'h', minute: 'min',
    },
    it: {
      play: 'Riproduci', details: 'Dettagli', trailer: 'Trailer', favorit: 'Preferito',
      leer: 'Niente da mostrare – filtri troppo restrittivi?',
      eintrag: 'Elemento', stunde: 'h', minute: 'min',
    },
    nl: {
      play: 'Afspelen', details: 'Details', trailer: 'Trailer', favorit: 'Favoriet',
      leer: 'Niets te tonen – filters te streng?',
      eintrag: 'Item', stunde: 'u', minute: 'min',
    },
  };

  function sprache() {
    var l = '';
    try {
      l = document.documentElement.getAttribute('lang')
        || (navigator.language || navigator.userLanguage || '');
    } catch (e) { /* egal */ }
    l = String(l).toLowerCase().split('-')[0];
    return SPRACHEN[l] ? l : 'en';
  }

  function t(schluessel) {
    var s = SPRACHEN[sprache()];
    return (s && s[schluessel]) || SPRACHEN.en[schluessel] || schluessel;
  }

  // ----------------------------------------------------------------- Hilfen
  function el(tag, klasse, text) {
    var n = document.createElement(tag);
    if (klasse) n.className = klasse;
    if (text != null) n.textContent = text;
    return n;
  }

  function basis() {
    try {
      if (window.ApiClient && ApiClient.serverAddress) return ApiClient.serverAddress();
    } catch (e) { /* egal */ }
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

  // ----------------------------------------------------------------- Zeichnen
  function render(ziel, items, d, optionen) {
    optionen = optionen || {};
    if (!ziel) return null;
    ziel.innerHTML = '';
    if (!items || !items.length) {
      ziel.appendChild(el('div', 'mh-leer', t('leer')));
      return null;
    }

    /* Pro Aufruf mischen - auf einer Kopie, damit die vom Server geholte
       Liste unveraendert bleibt und ein zweiter Aufruf nicht auf einer bereits
       verwuerfelten Reihenfolge aufsetzt. In der Vorschau bewusst nicht: dort
       soll sich beim Einstellen nur das aendern, was man gerade verstellt. */
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

    // Feinmasse als CSS-Variablen - so bleibt das Stylesheet statisch und
    // trotzdem alles einstellbar.
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
        // Das Logo der ersten Folie ist sofort sichtbar - traege zu laden
        // hiesse, es erst nachtraeglich einblenden zu lassen.
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

    // Anzeige der Position
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
            // NICHT t nennen: das wuerde die Uebersetzungsfunktion t() im
            // gesamten umgebenden Bereich verdecken (var-Hoisting).
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

    // ------------------------------------------------------------- Ablauf
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
        // Abstand zur aktuellen Folie als Attribut - das CSS macht daraus ein
        // Fenster aus wenigen Punkten, das mitwandert. Weit entfernte Punkte
        // bekommen Breite 0 und verschwinden dadurch aus dem Fluss, statt die
        // Leiste mit dreissig Punkten zu pflastern.
        Array.prototype.forEach.call(anzeige.children, function (k, i) {
          k.classList.toggle('mh-aktiv', i === n);
          k.setAttribute('data-fern', String(Math.min(Math.abs(i - n), 4)));
        });
      }
      aktuell = n;
      laden(n);
      laden((n + 1) % folien.length); // nur die naechste Folie vorladen
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

    // Zeitgeber anhalten, solange die Leiste nicht sichtbar ist - spart Arbeit
    // im Hintergrundtab und beim Herunterscrollen.
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

  // ----------------------------------------------------------------- Einhaengen
  function istStartseite() {
    var h = window.location.hash || '';
    return h.indexOf('home') !== -1 || h === '' || h === '#/' || h === '#!/';
  }

  /*
   * Der Anker, VOR den die Leiste gesetzt wird.
   *
   * Wichtig: nicht hinein. Der Web-Client baut die Abschnitte mit
   *     e.innerHTML = h;  e.classList.add("homeSectionsContainer");
   * auf und wirft damit jedes eingefuegte Kind sofort wieder raus. Als
   * Geschwister davor ueberlebt die Leiste jeden Neuaufbau.
   */
  function anker() {
    var k = document.querySelector('.homeSectionsContainer')
      || document.querySelector('.homePage .sections')
      || document.querySelector('[data-type="home"] .sections');
    return k && k.parentNode ? k : null;
  }

  var daten = null;
  var laeuft = false;
  var netzLaeuft = null;

  /* Der Zwischenspeicher im Browser.

     Der Grund: frueher begann der Abruf erst, wenn Jellyfins Startseite
     fertig war - also nach einer halben Sekunde -, und danach kamen Netz,
     Aufbau und Bild obendrauf. Die Leiste poppte sichtbar nach.

     Mit einer gespeicherten Fassung steht sie beim zweiten Besuch sofort, ohne
     eine einzige Anfrage abzuwarten. Die frische Liste wird daneben geholt und
     nur dann neu gezeichnet, wenn sie sich wirklich unterscheidet - sonst
     flackert es bei jedem Aufruf.

     Der Schluessel traegt die Benutzerkennung: auf einem gemeinsam genutzten
     Geraet soll niemand kurz die Liste des Vorgaengers sehen. */
  var SPEICHER_TAGE = 7;

  /* Die Benutzerkennung - auch dann, wenn ApiClient noch nicht bereit ist.

     Genau daran scheiterte der Zwischenspeicher zuerst: beim fruehen Lesen war
     ApiClient noch nicht da, die Kennung also leer; beim spaeteren Schreiben
     war sie gesetzt. Zwei verschiedene Schluessel, nie ein Treffer. Der
     Webclient legt seine Zugangsdaten aber selbst im lokalen Speicher ab, und
     der steht vom ersten Augenblick an zur Verfuegung. */
  function nutzerKennung() {
    try {
      if (window.ApiClient && ApiClient.getCurrentUserId) {
        var u = ApiClient.getCurrentUserId();
        if (u) return u;
      }
    } catch (e) { /* egal */ }
    try {
      var roh = localStorage.getItem('jellyfin_credentials');
      if (roh) {
        var o = JSON.parse(roh);
        var srv = o && o.Servers && o.Servers[0];
        if (srv && srv.UserId) return srv.UserId;
      }
    } catch (e) { /* egal */ }
    return '';
  }

  function speicherSchluessel() {
    return 'mediahero.liste.v1.' + nutzerKennung();
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

  function inSpeicher(j) {
    try {
      localStorage.setItem(speicherSchluessel(),
        JSON.stringify({ stand: Date.now(), daten: j }));
    } catch (e) { /* voll oder gesperrt - dann eben ohne */ }
  }

  /* Ist der Webclient schon so weit, dass er ein Token hat?

     Wichtig, weil frueh losgeschickte Anfragen sonst unangemeldet hinausgehen
     und mit 401 zurueckkommen - gemessen nach 190 ms, also lange bevor
     ApiClient bereit war. Der Abruf war damit verschenkt und musste spaeter
     wiederholt werden. Lieber ein paar Millisekunden warten als eine Anfrage
     wegwerfen. */
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
        // Nach etwa drei Sekunden aufgeben und es trotzdem versuchen - ohne
        // Anmeldung gibt es ohnehin nichts anzuzeigen.
        if (tokenDa() || ++n > 100) { clearInterval(uhr); fertig(); }
      }, 30);
    });
  }

  function vomServer() {
    if (netzLaeuft) return netzLaeuft;
    netzLaeuft = sobaldBereit().then(function () {
      var kopf = {};
      try {
        if (window.ApiClient && ApiClient.accessToken()) {
          kopf['Authorization'] = 'MediaBrowser Token="' + ApiClient.accessToken() + '"';
        }
      } catch (e) { /* egal */ }
      return fetch(basis() + '/MediaHero/items', { headers: kopf, credentials: 'same-origin' });
    })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        netzLaeuft = null;
        if (j && j.Items && j.Items.length) { inSpeicher(j); vorladen(j); }
        return j;
      })
      .catch(function () { netzLaeuft = null; return null; });
    return netzLaeuft;
  }

  /* Das erste Bild anmelden, sobald die Liste bekannt ist.

     Der Browser laedt es dann parallel zu allem anderen, statt erst wenn die
     Folie im Baum haengt. Die Adresse muss auf das Zeichen genau dieselbe sein
     wie spaeter beim Zeichnen - sonst laedt er es zweimal. */
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
    } catch (e) { /* egal */ }
  }

  function neuZeichnen() {
    var vorhanden = document.getElementById(WURZEL);
    if (vorhanden && vorhanden.parentNode) { vorhanden.parentNode.removeChild(vorhanden); }
    if (zustand) { zustand.zerstoeren(); zustand = null; }
    einhaengen();
  }

  function holen() {
    if (daten) return Promise.resolve(daten);

    var alt = ausSpeicher();
    if (alt && alt.Items && alt.Items.length) {
      daten = alt;
      vorladen(alt);
      // Daneben auffrischen. Nur bei echter Aenderung neu zeichnen.
      vomServer().then(function (j) {
        if (!j || !j.Items) return;
        if (j.BuiltAtUnix && alt.BuiltAtUnix && j.BuiltAtUnix === alt.BuiltAtUnix) return;
        daten = j;
        neuZeichnen();
      });
      return Promise.resolve(alt);
    }

    return vomServer().then(function (j) { daten = j; return j; });
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
    if (document.getElementById(WURZEL)) return;

    laeuft = true;
    holen().then(function (j) {
      laeuft = false;
      if (!j || !j.Display || j.Display.enabled === false) return;
      if (!istStartseite()) return;
      var ort2 = anker();
      if (!ort2 || document.getElementById(WURZEL)) return;

      var host = el('div', 'mh-host');
      host.id = WURZEL;
      // als Geschwister DAVOR, nicht als Kind - sonst raeumt der naechste
      // innerHTML-Neuaufbau der Abschnitte die Leiste weg
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
   * Die Leiste unter die Kopfleiste ziehen.
   *
   * Die Hoehe wird gemessen statt geraten - sie unterscheidet sich je nach
   * Fensterbreite und Jellyfin-Fassung. Der Kopf wird nur durchsichtig,
   * solange ganz oben steht; sobald gescrollt wird, bekommt er seinen
   * Hintergrund zurueck, sonst verschwindet die Navigation im Bild.
   */
  /*
   * Die Kopfleiste suchen, ohne ihren Klassennamen zu kennen.
   *
   * Jellyfin 12 mischt die alte Oberflaeche mit Material UI; je nach Ansicht
   * heisst das Element .skinHeader oder .MuiAppBar-root. Statt zu raten wird
   * genommen, was messbar oben klebt: breit, oben angesetzt, fest positioniert
   * und von plausibler Hoehe. Das ueberlebt auch kuenftige Umbauten.
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
    // Der Kopf wird von der Oberflaeche teils spaeter aufgebaut als die Leiste;
    // deshalb ein paar Mal nachmessen statt einmalig zu raten.
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
    /* Sofort anstossen.

       Das ist der groesste Einzelgewinn: der Abruf lief frueher erst an,
       nachdem Jellyfins Startseite fertig gerendert war, weil einhaengen()
       vorher am fehlenden Anker abbrach. Damit lagen Jellyfins Aufbau und
       unserer hintereinander statt nebeneinander. Angestossen wird er jetzt
       hier - wenn die Startseite dann steht, liegt die Liste meist schon
       bereit. */
    holen();
    einhaengen();
    window.addEventListener('hashchange', function () {
      /* Frueher wurde hier alles verworfen und beim naechsten Aufbau neu
         geholt - das kostete bei jedem Hin und Her eine Anfrage, waehrend der
         die Leiste fehlte. Jetzt bleibt das Vorhandene stehen und wird
         daneben geprueft. */
      vomServer().then(function (j) {
        if (!j || !j.Items) return;
        if (daten && j.BuiltAtUnix && daten.BuiltAtUnix === j.BuiltAtUnix) return;
        daten = j;
        neuZeichnen();
      });
      setTimeout(einhaengen, 60);
    });
    // Der Web-Client baut die Startseite nach, ohne die Adresse zu aendern.
    // Der Beobachter feuert bei jeder Aenderung im Baum. Ohne Bremse waere das
    // hunderte Aufrufe je Seitenaufbau - gedrosselt kostet es praktisch nichts.
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
