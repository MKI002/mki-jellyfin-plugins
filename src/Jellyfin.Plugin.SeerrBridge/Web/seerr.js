/*
 * Jellyseerr Bridge - Clientteil.
 *
 * Drei Aufgaben: Schaltflaeche in Kopfleiste und Seitenmenue, eingebettete
 * Ansicht unter #/jellyseerr, Suchvorschlaege auf der Suchseite.
 *
 * Grundhaltung beim Eingriff in eine fremde Oberflaeche: nichts nachbauen, was
 * schon da ist. Die Knoepfe werden deshalb aus einem vorhandenen Knopf GEKLONT
 * und nur umbeschriftet. Jellyfin 12 baut seine Kopfleiste mit Material-UI, und
 * dessen Klassennamen tragen einen Hash aus dem Bauvorgang (css-1f20jcn). Wer
 * die abschreibt, hat beim naechsten Jellyfin-Update einen unformatierten Link
 * in der Leiste stehen. Ein Klon erbt alles und ueberlebt das.
 */
(function () {
  'use strict';

  var ROUTE = '#/jellyseerr';
  var cfg = null;
  var beobachter = null;
  var suchTakt = null;
  var ssoVersucht = false;

  /* Beschriftungen je Sprache.

     Der Server ist einer, die Betrachter sind viele - die Sprache muss also vom
     Client kommen. Jellyfin setzt sie auf <html lang> (dort steht z. B. "en-us",
     sobald im Profil Englisch gewaehlt ist); fehlt das, entscheidet der Browser.
     Unbekannte Sprachen fallen auf Englisch zurueck statt auf Deutsch. */
  var SPRACHEN = {
    en: { titel: "Requests", vorschlaege: "Available via Jellyseerr", anfragen: "Request", laeuft: "requesting …", fertig: "Requested", vorhanden: "in your library", angefragt: "already requested", arbeit: "being fetched", teilweise: "partially available", fehler: "Did not work", oeffnen: "Open in Jellyseerr", rahmenLeer: "Jellyseerr cannot be shown here. Usually the address is wrong – an http address will not load inside an https page. Currently set:" },
    de: { titel: "Anfragen", vorschlaege: "Bei Jellyseerr verfügbar", anfragen: "Anfragen", laeuft: "wird angefragt …", fertig: "Angefragt", vorhanden: "in der Bibliothek", angefragt: "bereits angefragt", arbeit: "wird geholt", teilweise: "teilweise da", fehler: "Nicht geklappt", oeffnen: "In Jellyseerr öffnen", rahmenLeer: "Jellyseerr lässt sich hier nicht anzeigen. Meist stimmt die Adresse nicht – eine http-Adresse lädt in einer https-Seite nicht. Eingestellt ist:" },
    fr: { titel: "Demandes", vorschlaege: "Disponible via Jellyseerr", anfragen: "Demander", laeuft: "demande en cours …", fertig: "Demandé", vorhanden: "dans votre bibliothèque", angefragt: "déjà demandé", arbeit: "en cours de récupération", teilweise: "partiellement disponible", fehler: "Échec", oeffnen: "Ouvrir dans Jellyseerr", rahmenLeer: "Jellyseerr ne peut pas s’afficher ici. L’adresse est généralement en cause – une adresse http ne se charge pas dans une page https. Adresse actuelle :" },
    es: { titel: "Solicitudes", vorschlaege: "Disponible en Jellyseerr", anfragen: "Solicitar", laeuft: "solicitando …", fertig: "Solicitado", vorhanden: "en tu biblioteca", angefragt: "ya solicitado", arbeit: "descargando", teilweise: "parcialmente disponible", fehler: "No funcionó", oeffnen: "Abrir en Jellyseerr", rahmenLeer: "Jellyseerr no puede mostrarse aquí. Normalmente la dirección es incorrecta: una dirección http no carga dentro de una página https. Configurada:" },
    it: { titel: "Richieste", vorschlaege: "Disponibile su Jellyseerr", anfragen: "Richiedi", laeuft: "richiesta in corso …", fertig: "Richiesto", vorhanden: "nella tua libreria", angefragt: "già richiesto", arbeit: "in scaricamento", teilweise: "parzialmente disponibile", fehler: "Non riuscito", oeffnen: "Apri in Jellyseerr", rahmenLeer: "Jellyseerr non può essere mostrato qui. Di solito l’indirizzo è sbagliato: un indirizzo http non si carica in una pagina https. Impostato:" },
    nl: { titel: "Verzoeken", vorschlaege: "Beschikbaar via Jellyseerr", anfragen: "Aanvragen", laeuft: "aanvragen …", fertig: "Aangevraagd", vorhanden: "in je bibliotheek", angefragt: "al aangevraagd", arbeit: "wordt opgehaald", teilweise: "gedeeltelijk beschikbaar", fehler: "Niet gelukt", oeffnen: "Openen in Jellyseerr", rahmenLeer: "Jellyseerr kan hier niet worden getoond. Meestal klopt het adres niet: een http-adres laadt niet in een https-pagina. Ingesteld:" },
    pt: { titel: "Pedidos", vorschlaege: "Disponível via Jellyseerr", anfragen: "Pedir", laeuft: "a pedir …", fertig: "Pedido", vorhanden: "na sua biblioteca", angefragt: "já pedido", arbeit: "a obter", teilweise: "parcialmente disponível", fehler: "Não resultou", oeffnen: "Abrir no Jellyseerr", rahmenLeer: "O Jellyseerr não pode ser mostrado aqui. Normalmente o endereço está errado: um endereço http não carrega numa página https. Definido:" },
    pl: { titel: "Żądania", vorschlaege: "Dostępne przez Jellyseerr", anfragen: "Poproś", laeuft: "wysyłanie …", fertig: "Zgłoszono", vorhanden: "w Twojej bibliotece", angefragt: "już zgłoszone", arbeit: "pobieranie", teilweise: "częściowo dostępne", fehler: "Nie udało się", oeffnen: "Otwórz w Jellyseerr", rahmenLeer: "Nie można tu wyświetlić Jellyseerr. Zwykle adres jest błędny: adres http nie wczyta się na stronie https. Ustawiono:" },
    ru: { titel: "Запросы", vorschlaege: "Доступно через Jellyseerr", anfragen: "Запросить", laeuft: "запрос …", fertig: "Запрошено", vorhanden: "в вашей библиотеке", angefragt: "уже запрошено", arbeit: "загружается", teilweise: "частично доступно", fehler: "Не удалось", oeffnen: "Открыть в Jellyseerr", rahmenLeer: "Jellyseerr нельзя показать здесь. Обычно дело в адресе: http не загрузится внутри https. Задано:" },
    sv: { titel: "Förfrågningar", vorschlaege: "Tillgängligt via Jellyseerr", anfragen: "Begär", laeuft: "begär …", fertig: "Begärd", vorhanden: "i ditt bibliotek", angefragt: "redan begärd", arbeit: "hämtas", teilweise: "delvis tillgänglig", fehler: "Gick inte", oeffnen: "Öppna i Jellyseerr", rahmenLeer: "Jellyseerr kan inte visas här. Oftast är adressen fel: en http-adress laddas inte i en https-sida. Inställt:" },
    da: { titel: "Anmodninger", vorschlaege: "Tilgængelig via Jellyseerr", anfragen: "Anmod", laeuft: "anmoder …", fertig: "Anmodet", vorhanden: "i dit bibliotek", angefragt: "allerede anmodet", arbeit: "hentes", teilweise: "delvist tilgængelig", fehler: "Gik ikke", oeffnen: "Åbn i Jellyseerr", rahmenLeer: "Jellyseerr kan ikke vises her. Som regel er adressen forkert: en http-adresse indlæses ikke i en https-side. Indstillet:" },
    nb: { titel: "Forespørsler", vorschlaege: "Tilgjengelig via Jellyseerr", anfragen: "Be om", laeuft: "ber om …", fertig: "Forespúrt", vorhanden: "i biblioteket ditt", angefragt: "allerede forespúrt", arbeit: "hentes", teilweise: "delvis tilgjengelig", fehler: "Gikk ikke", oeffnen: "Åpne i Jellyseerr", rahmenLeer: "Jellyseerr kan ikke vises her. Som regel er adressen feil: en http-adresse lastes ikke i en https-side. Innstilt:" },
    fi: { titel: "Pyynnöt", vorschlaege: "Saatavilla Jellyseerrin kautta", anfragen: "Pyydä", laeuft: "pyydetään …", fertig: "Pyydetty", vorhanden: "kirjastossasi", angefragt: "jo pyydetty", arbeit: "haetaan", teilweise: "osittain saatavilla", fehler: "Ei onnistunut", oeffnen: "Avaa Jellyseerrissä", rahmenLeer: "Jellyseerriä ei voi näyttää tässä. Yleensä osoite on väärä: http-osoite ei lataudu https-sivulla. Asetettu:" },
    cs: { titel: "Žádosti", vorschlaege: "Dostupné přes Jellyseerr", anfragen: "Požádat", laeuft: "žádám …", fertig: "Vyžádáno", vorhanden: "ve vaší knihovně", angefragt: "již vyžádáno", arbeit: "stažuje se", teilweise: "částečně dostupné", fehler: "Nepovedlo se", oeffnen: "Otevřít v Jellyseerr", rahmenLeer: "Jellyseerr zde nelze zobrazit. Obvykle je špatně adresa: http se v https stránce nenačte. Nastaveno:" },
    hu: { titel: "Kérések", vorschlaege: "Elérhető a Jellyseerren", anfragen: "Kérés", laeuft: "kérés …", fertig: "Kérve", vorhanden: "a könyvtáradban", angefragt: "már kérve", arbeit: "letöltés alatt", teilweise: "részben elérhető", fehler: "Nem sikerült", oeffnen: "Megnyitás a Jellyseerrben", rahmenLeer: "A Jellyseerr itt nem jeleníthető meg. Általában a cím hibás: http cím nem tölt be https oldalon. Beállítva:" },
    tr: { titel: "İstekler", vorschlaege: "Jellyseerr üzerinden mevcut", anfragen: "İste", laeuft: "isteniyor …", fertig: "İstendi", vorhanden: "kütüphanenizde", angefragt: "zaten istendi", arbeit: "alınıyor", teilweise: "kısmen mevcut", fehler: "Olmadı", oeffnen: "Jellyseerr’de aç", rahmenLeer: "Jellyseerr burada gösterilemiyor. Genellikle adres yanlıştır: http adresi https sayfada yüklenmez. Ayarlı:" },
    uk: { titel: "Запити", vorschlaege: "Доступно через Jellyseerr", anfragen: "Запитати", laeuft: "запит …", fertig: "Запитано", vorhanden: "у вашій бібліотеці", angefragt: "вже запитано", arbeit: "завантажується", teilweise: "частково доступно", fehler: "Не вдалося", oeffnen: "Відкрити в Jellyseerr", rahmenLeer: "Jellyseerr не можна показати тут. Зазвичай справа в адресі: http не завантажиться в https. Задано:" },
    zh: { titel: "请求", vorschlaege: "可通过 Jellyseerr 获取", anfragen: "请求", laeuft: "请求中 …", fertig: "已请求", vorhanden: "已在媒体库", angefragt: "已请求过", arbeit: "下载中", teilweise: "部分可用", fehler: "未成功", oeffnen: "在 Jellyseerr 中打开", rahmenLeer: "无法在此显示 Jellyseerr。通常是地址有误：http 地址无法在 https 页面中加载。当前设置：" },
    ja: { titel: "リクエスト", vorschlaege: "Jellyseerr で入手可能", anfragen: "リクエスト", laeuft: "リクエスト中 …", fertig: "リクエスト済み", vorhanden: "ライブラリにあり", angefragt: "リクエスト済み", arbeit: "取得中", teilweise: "一部利用可能", fehler: "失敗しました", oeffnen: "Jellyseerr で開く", rahmenLeer: "ここに Jellyseerr を表示できません。多くはアドレスの問題です：http は https ページ内で読み込めません。現在の設定:" },
    ko: { titel: "요청", vorschlaege: "Jellyseerr에서 이용 가능", anfragen: "요청", laeuft: "요청 중 …", fertig: "요청됨", vorhanden: "라이브러리에 있음", angefragt: "이미 요청됨", arbeit: "가져오는 중", teilweise: "일부 이용 가능", fehler: "실패", oeffnen: "Jellyseerr에서 열기", rahmenLeer: "여기에 Jellyseerr를 표시할 수 없습니다. 보통 주소 문제입니다: http 주소는 https 페이지에서 로드되지 않습니다. 현재 설정:" },
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
    var tab = SPRACHEN[sprache()] || SPRACHEN.en;
    return tab[schluessel] || SPRACHEN.en[schluessel] || schluessel;
  }

  function basis() {
    try {
      if (window.ApiClient && window.ApiClient.serverAddress) {
        return window.ApiClient.serverAddress();
      }
    } catch (e) { /* egal */ }
    return '';
  }

  function kopfZeilen() {
    var h = { Accept: 'application/json' };
    try {
      if (window.ApiClient && window.ApiClient.accessToken) {
        var tk = window.ApiClient.accessToken();
        if (tk) { h.Authorization = 'MediaBrowser Token="' + tk + '"'; }
      }
    } catch (e) { /* egal */ }
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

  /* ---------------------------------------------------------------- Knoepfe */

  /* Ein vorhandener Bibliotheksknopf aus der Kopfleiste - Vorlage und
     zugleich Einhaengepunkt. */
  function vorbildKopf() {
    return document.querySelector(
      'a.MuiButton-root[href^="#/movies"], a.MuiButton-root[href^="#/tv?"],'
      + ' a.MuiButton-root[href^="#/livetv"], a.MuiButton-root[href^="#/home?"]');
  }

  /* Wolke mit Pfeil nach unten. Dasselbe Zeichen im Seitenmenue, dort ueber die
     Klasse cloud_download - die ist in Jellyfins Stylesheet definiert, weil der
     Client sie selbst benutzt. Nur solche Zeichen lassen sich per Klasse
     setzen; erfundene Namen ergaeben eine leere Flaeche. */
  var SYMBOL = 'M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0'
    + ' 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM17'
    + ' 13l-5 5-5-5h3V9h4v4h3z';

  /* Beschriftung der Schaltflaeche.

     Ein in den Einstellungen eingetragener Text gewinnt - wer bewusst etwas
     anderes hinschreibt, will das auch sehen. Ist das Feld leer (Standard),
     entscheidet die Sprache des Clients. */
  function beschriftung() {
    return (cfg && cfg.label) ? cfg.label : t('titel');
  }

  function kopfKnopfSetzen() {
    if (!cfg || !cfg.enabled || !cfg.showInHeader) { return; }

    var schon = document.querySelector('a[data-seerr-kopf]');
    if (schon) {
      // Die Sprache kann sich im laufenden Betrieb aendern - im Profil
      // umgestellt, ohne Neuladen. Dann muss die Beschriftung nachziehen.
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

    // Die Beschriftung ist ein blanker Textknoten hinter dem Symbol. Alle
    // Textknoten entfernen und einen eigenen anhaengen - dann bleibt die
    // Symbolhuelle unangetastet.
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
      // Jellyfin setzt das Symbol ueber die Klasse, nicht ueber den Inhalt.
      symbol.className = 'material-icons navMenuOptionIcon cloud_download';
    }
    var text = eintrag.querySelector('.navMenuOptionText');
    if (text) { text.textContent = beschriftung(); }

    liste.appendChild(eintrag);
  }

  /* ------------------------------------------------------- Eingebettete Ansicht */

  function kopfHoehe() {
    var kopf = document.querySelector('header.MuiAppBar-root, .skinHeader');
    var h = kopf ? Math.round(kopf.getBoundingClientRect().height) : 0;
    return h > 0 ? h : 56;
  }

  /* Welcher Pfad innerhalb von Jellyseerr soll gezeigt werden?

     Steht als Zusatz in der Adresse: #/jellyseerr?ziel=%2Fmovie%2F1234
     Nur Pfade werden akzeptiert, keine vollstaendigen Adressen - sonst koennte
     ein untergeschobener Link eine fremde Seite im Rahmen oeffnen, die dann
     aussieht, als gehoere sie zu Jellyfin. */
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
      // Beim Sprung von einem Treffer zum naechsten bleibt der Rahmen stehen
      // und bekommt nur eine neue Adresse - das ist schneller als ihn neu
      // aufzubauen und behaelt die Anmeldung.
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
    rahmen.setAttribute('title', 'Jellyseerr');
    // allow-same-origin ist noetig, damit Jellyseerr seine Sitzung behaelt;
    // ohne das waere der Rahmen bei jedem Aufruf abgemeldet.
    rahmen.setAttribute('allow', 'fullscreen');
    rahmen.setAttribute('data-ziel', ziel);

    // Wenn der Rahmen leer bleibt, ist die Ursache fast immer die Adresse:
    // eine http-Seite in einer https-Seite blockiert der Browser ersatzlos, und
    // eine reine LAN-Adresse ist von unterwegs nicht erreichbar. Ein leerer
    // blauer Kasten sagt das niemandem - dieser Hinweis schon.
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

    /* Erst die Anmeldung weiterreichen, dann laden.

       Die Reihenfolge ist wichtig: laedt der Rahmen zuerst, sieht man kurz die
       Anmeldemaske und muesste danach neu laden. Der Aufruf kostet einmal je
       Seitenaufruf einen Moment; danach steht die Sitzung 30 Tage. Schlaegt er
       fehl, wird trotzdem geladen - dann eben mit der gewohnten Anmeldemaske. */
    ssoDann(function () { rahmen.src = cfg.url + ziel; });
  }

  function ssoDann(weiter) {
    if (!cfg || !cfg.sso || ssoVersucht) { weiter(); return; }
    ssoVersucht = true;
    // Haengt der Server, nicht endlos warten - lieber die Anmeldemaske zeigen
    // als einen leeren Kasten.
    var zeit = setTimeout(weiter, 4000);
    holen('/SeerrBridge/sso')
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
    var aktiv = (location.hash || '').indexOf('jellyseerr') !== -1;
    if (aktiv) { ansichtZeigen(); } else { ansichtVerbergen(); }
  }

  /* ------------------------------------------------------------------ Suche */

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
    // Der sichtbare Seiteninhalt der Suchseite. Mehrere Kandidaten, weil
    // Jellyfin die Suchseite je nach Fassung anders aufhaengt.
    return document.querySelector('.searchResults')
      || document.querySelector('.mainAnimatedPage:not(.hide) .padded-left')
      || document.querySelector('.mainAnimatedPage:not(.hide)')
      || document.querySelector('#reactRoot > div:last-child');
  }

  /* Hinter Jellyfins eigene Ergebnisgruppen setzen.
     Die eigene Bibliothek gehoert zuerst auf die Seite - was man schon hat,
     will man nicht unter Vorschlaegen suchen. Jellyfin legt jede Gruppe
     ("Filme", "Serien") als eigenen Abschnitt an; hinter den letzten davon
     einzuhaengen ist zuverlaessiger als ein Anhaengen an den Behaelter, weil
     Jellyfin seine Gruppen nach uns fertigstellt. */
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
    // Jellyfins eigene Klassen mitnehmen statt eigene Abstaende erfinden:
    // der linke Rand steckt nicht im Abschnitt, sondern in "padded-left" an
    // Ueberschrift und Kachelreihe. Wer das nachbaut, trifft einen festen
    // Pixelwert, der auf schmalen Geraeten wieder falsch ist - Jellyfin
    // veraendert diesen Abstand je nach Fensterbreite.
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

  /* Adresse eines Treffers in Jellyseerr. Filme liegen unter /movie/<id>,
     Serien unter /tv/<id> - dieselbe Kennung von TMDB in beiden Faellen. */
  function trefferZiel(e) {
    return ROUTE + '?ziel='
      + encodeURIComponent('/' + (e.mediaType === 'tv' ? 'tv' : 'movie') + '/' + e.tmdbId);
  }

  function kachel(e) {
    // Ein echter Link, kein Klickfaenger: so funktionieren mittlere Maustaste,
    // Tastaturbedienung und die Vorschau der Zieladresse wie ueberall sonst.
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
        // Der Knopf sitzt in einem Link - ohne das hier wuerde ein Klick auf
        // "Anfragen" zusaetzlich die Detailseite oeffnen.
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
      // Die Ueberschrift wird beim Anlegen gesetzt, der Abschnitt aber
      // wiederverwendet. Ohne diese Zeile bliebe sie nach einem Sprachwechsel
      // in der alten Sprache stehen.
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
    }).catch(function () { /* stumm - die Suche darf daran nicht scheitern */ });
  }

  function sucheAnbinden() {
    if (!cfg || !cfg.search) { return; }
    var feld = suchFeld();
    if (!feld || feld.getAttribute('data-seerr-gebunden')) { return; }
    feld.setAttribute('data-seerr-gebunden', '1');
    feld.addEventListener('input', function () {
      var wert = feld.value;
      if (suchTakt) { clearTimeout(suchTakt); }
      // Bewusst traege: bei jedem Tastendruck zu suchen belastet Jellyseerr
      // und liefert Treffer zu halben Woertern.
      suchTakt = setTimeout(function () { suchen(wert); }, 450);
    });
  }

  /* ---------------------------------------------------------------- Anstoss */

  function auffrischen() {
    kopfKnopfSetzen();
    menuEintragSetzen();
    routePruefen();
    if (suchSeite()) {
      sucheAnbinden();
      // Jellyfin baut seine Gruppen bei jedem Tastendruck neu - danach steht
      // unser Abschnitt wieder falsch und muss erneut einsortiert werden.
      var a = document.getElementById('seerrTreffer');
      if (a) { einordnen(a); }
    }
  }

  function start() {
    fetch(basis() + '/SeerrBridge/config', { headers: { Accept: 'application/json' } })
      .then(function (r) { return r.json(); })
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

        // React baut die Kopfleiste bei jedem Seitenwechsel neu auf und wirft
        // Fremdes dabei heraus. Der Beobachter setzt es danach erneut - gedrosselt,
        // weil er sonst bei jeder Kleinigkeit im Baum feuert.
        var geplant = false;
        beobachter = new MutationObserver(function () {
          if (geplant) { return; }
          geplant = true;
          setTimeout(function () { geplant = false; auffrischen(); }, 120);
        });
        beobachter.observe(document.body, { childList: true, subtree: true });

        /* Zusaetzlich eine ruhige Nachschau im Sekundentakt.
           Der Beobachter allein reicht nicht: waehrend React eine Seite umbaut,
           feuert er mitten im Umbau. Faellt der Knopf dabei weg oder ist die
           Vorlage gerade nicht da, laeuft der Wiedereinbau ins Leere - und
           danach gibt es keine Aenderung mehr, die ihn erneut ausloesen wuerde.
           Der Knopf bliebe bis zum naechsten Klick verschwunden. Ein Blick pro
           Sekunde kostet eine Abfrage im Baum und macht das Ganze unabhaengig
           vom Zufall der Reihenfolge. Kurz genug, dass die Luecke nach einem
           Seitenwechsel nicht als Flackern auffaellt. */
        setInterval(auffrischen, 400);

        auffrischen();
      })
      .catch(function () { /* Plugin nicht eingerichtet - dann eben nichts */ });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
