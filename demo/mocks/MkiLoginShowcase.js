/* Example data for the MKI Login Showcase demo: made-up libraries and titles with drawn artwork, picked by the saved
   settings the way the server picks them. */
(function () {
  'use strict';
  var ID = 'f61ac908-2f39-419b-b7d3-302f7930455b';
  var LIBS = [
    { Id: '3b7e2a10-5c4d-4e6f-9a8b-1c2d3e4f5a6b', Name: 'Movies', Type: 'movies' },
    { Id: '5d9a4c32-7e6f-4a8b-8c0d-3e4f5a6b7c8d', Name: 'Shows', Type: 'tvshows' },
    { Id: '7f1c6e54-9a8b-4c0d-9e2f-5a6b7c8d9e0f', Name: 'Kids', Type: 'movies', Looks: 'kids' },
    { Id: '9b3e8a76-1c0d-4e2f-8a4b-7c8d9e0f1a2b', Name: 'After Dark', Type: 'movies', Looks: 'adult' }
  ];
  MkiDemo.plugin(ID, {
    Enabled: true, Style: 'Horizon', LibraryIds: [LIBS[0].Id, LIBS[1].Id], MaxRating: 12, IncludeUnrated: false, Pick: 'Random',
    ShowTitles: false, ShowCounts: false, Accent: 'Mki', Motion: 'Normal', NeonCard: true
  });

  /* two made-up users shown on the sign-in page, so the preview draws user tiles */
  MkiDemo.route('GET', /^Users\/Public$/, function () { return [{ Name: 'Alex' }, { Name: 'Sam' }]; });

  MkiDemo.route('GET', /^Localization\/ParentalRatings$/, function () {
    var list = [{ Name: 'Unrated' }];
    [['G', 0], ['TV-Y', 0], ['TV-G', 0], ['PG', 10], ['TV-PG', 10], ['PG-13', 13], ['TV-14', 14], ['R', 17], ['TV-MA', 17], ['NC-17', 18]].forEach(function (r) {
      list.push({ Name: r[0], Value: r[1], RatingScore: { score: r[1] } });
    });
    return list;
  });

  /* name, library (index), kind, year, rating score (null: unrated), two artwork colours */
  var CATALOGUE = [
    ['The Last Lighthouse', 0, 'Movie', 2024, 13, '#0f3b57', '#f2a65a'], ['Northbound Express', 0, 'Movie', 2022, 13, '#2b1a3f', '#ff5a6e'],
    ['A Quiet Orbit', 0, 'Movie', 2026, 10, '#081a33', '#5bc0ff'], ['Paper Moon Harbor', 0, 'Movie', 2018, 10, '#3a2416', '#ffd27a'],
    ['Ember Valley', 0, 'Movie', 2023, 10, '#3b1608', '#ff8f3c'], ['Glass Tide', 0, 'Movie', 2021, 0, '#06323a', '#38f2d6'],
    ['Velvet Archive', 0, 'Movie', 2019, 13, '#2a0f33', '#e05cff'], ['Signal Fires', 0, 'Movie', 2025, 17, '#330a0a', '#ff4d4d'],
    ['Copper Skies', 0, 'Movie', 2016, 10, '#2e1a06', '#ffb347'], ['Midnight Atlas', 0, 'Movie', 2020, 0, '#0a1440', '#7a8cff'],
    ['Hollow Point', 0, 'Movie', 2017, 17, '#1a1a1a', '#c0c0c0'], ['The Cartographers', 0, 'Movie', 2022, 10, '#0d2b1d', '#7cf5a8'],
    ['Lunar Protocol', 0, 'Movie', 2024, 13, '#120a2e', '#b48cff'], ['Saltwater Kings', 0, 'Movie', 2015, null, '#03263a', '#4fc3f7'],
    ['Orbit Station Seven', 1, 'Series', 2017, 10, '#101d3a', '#b48cff'], ['North of Nowhere', 1, 'Series', 2020, 17, '#1c2326', '#c9d6df'],
    ['The Lighthouse Keepers', 1, 'Series', 2021, 14, '#16302b', '#9fe3c2'], ['Static Monsoon', 1, 'Series', 2023, 10, '#0b2a33', '#3fe0ff'],
    ['Crimson Lantern', 1, 'Series', 2019, 10, '#3a0a14', '#ff6b8a'], ['Neon Harbor', 1, 'Series', 2025, 0, '#200a33', '#ff2bd6'],
    ['Little Comets', 2, 'Movie', 2022, 0, '#13305c', '#ffe45c'], ['The Paper Dragons', 2, 'Movie', 2019, 0, '#3a1050', '#7cf5a8'],
    ['Puddle Jumpers', 2, 'Movie', 2024, 0, '#0a3a2a', '#9fe3ff'], ['Afterglow Club', 3, 'Movie', 2020, 1000, '#2a0010', '#ff3d71']
  ];
  var ITEMS = CATALOGUE.map(function (c, i) {
    var n = String(i + 1);
    while (n.length < 4) n = '0' + n;
    return { Id: 'c0ffee00-0000-4000-8000-00000000' + n, Name: c[0], Lib: LIBS[c[1]].Id, Kind: c[2], Year: c[3], Score: c[4], _c1: c[5], _c2: c[6], Backdrop: i % 3 !== 2 };
  });

  var set = '', built = 0, order = [];
  function shuffle() {
    set = Math.random().toString(16).slice(2, 12);
    built = Date.now();
    order = ITEMS.map(function (x) { return { x: x, r: Math.random() }; }).sort(function (a, b) { return a.r - b.r; }).map(function (o) { return o.x; });
  }
  shuffle();
  function allowed(it, cfg) {
    if (it.Score == null) return !!cfg.IncludeUnrated;
    return it.Score <= cfg.MaxRating;
  }
  /* the page saves ids without dashes, as the server does */
  function norm(s) { return String(s || '').replace(/-/g, '').toLowerCase(); }
  function chosenIds(cfg) { return (cfg.LibraryIds || []).map(norm); }
  function picked(cfg) {
    var ids = chosenIds(cfg);
    var list = order.filter(function (it) { return ids.indexOf(norm(it.Lib)) >= 0 && allowed(it, cfg); });
    if (cfg.Pick === 'Recent') list = list.slice().sort(function (a, b) { return b.Year - a.Year; });
    return list;
  }
  var RATING = { 0: 'G', 10: 'PG', 13: 'PG-13', 14: 'TV-14', 17: 'R', 1000: 'XXX' };
  function overview() {
    var cfg = MkiDemo.config(ID), ids = chosenIds(cfg);
    /* as on the server: Backdrop Spotlight gets backdrops, the other styles posters */
    var spot = cfg.Style === 'Spotlight';
    var items = picked(cfg).filter(function (it) { return !spot || it.Backdrop; }).slice(0, spot ? 12 : 48);
    var chosen = ITEMS.filter(function (it) { return ids.indexOf(norm(it.Lib)) >= 0; });
    return {
      Libraries: LIBS.map(function (l) {
        var mine = ITEMS.filter(function (it) { return it.Lib === l.Id; });
        return { Id: l.Id, Name: l.Name, Type: l.Type, Selected: ids.indexOf(norm(l.Id)) >= 0, Looks: l.Looks || null, Titles: mine.length, Shown: mine.filter(function (it) { return allowed(it, cfg); }).length };
      }),
      Set: set, Style: cfg.Style, Built: new Date(built).toISOString(), NextPick: new Date(built + 36e5).toISOString(), MaxPosters: 48, MaxBackdrops: 12,
      Items: items.map(function (it) {
        return { Id: it.Id, Name: it.Name, Year: it.Year, Rating: it.Score == null ? null : RATING[it.Score] || null,
          PosterTag: spot ? null : 'p' + it.Id.slice(-4), BackdropTag: spot ? 'b' + it.Id.slice(-4) : null };
      }),
      Movies: cfg.ShowCounts ? chosen.filter(function (it) { return it.Kind === 'Movie' && allowed(it, cfg); }).length : null,
      Series: cfg.ShowCounts ? chosen.filter(function (it) { return it.Kind === 'Series' && allowed(it, cfg); }).length : null,
      WebClient: cfg.Enabled ? 'added' : 'off'
    };
  }
  /* a saved change makes a new pick, as on the server */
  MkiDemo.onConfig = function (id) { if (id === ID) shuffle(); };
  MkiDemo.route('GET', /^MkiLoginShowcase\/Overview$/, overview);
  MkiDemo.route('POST', /^MkiLoginShowcase\/Shuffle$/, function () { shuffle(); return overview(); });
  MkiDemo.serve('MkiLoginShowcase/login.js', 'MkiLoginShowcase/login.js');

  /* Drawn artwork instead of real images: a poster with the title, a wide scene for backdrops. */
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  function svg(w, h, body) { return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '" viewBox="0 0 ' + w + ' ' + h + '">' + body + '</svg>'); }
  function scene(it, w, h) {
    var a = it._c1, b = it._c2, seed = it.Name.length * 37 + it.Year;
    function r(n) { seed = (seed * 9301 + 49297) % 233280; return seed / 233280 * n; }
    var sun = '<circle cx="' + (w * (0.3 + r(0.4))).toFixed(0) + '" cy="' + (h * (0.3 + r(0.15))).toFixed(0) + '" r="' + (Math.min(w, h) * 0.2).toFixed(0) + '" fill="' + b + '" opacity=".85"/>';
    function ridge(y, amp, col, op) {
      var d = 'M0 ' + h + ' L0 ' + y, x = 0;
      while (x < w) { x += w / 6; d += ' L' + x.toFixed(0) + ' ' + (y - r(amp)).toFixed(0); }
      return '<path d="' + d + ' L' + w + ' ' + h + 'Z" fill="' + col + '" opacity="' + op + '"/>';
    }
    return '<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + a + '"/><stop offset=".6" stop-color="' + b + '" stop-opacity=".6"/><stop offset="1" stop-color="' + a + '"/></linearGradient></defs>' +
      '<rect width="100%" height="100%" fill="' + a + '"/><rect width="100%" height="100%" fill="url(#g)"/>' + sun +
      ridge(h * 0.7, h * 0.16, a, 0.8) + ridge(h * 0.84, h * 0.1, '#000', 0.5);
  }
  MkiDemo.image = function (item, art) {
    var it = ITEMS.filter(function (x) { return x.Id === item.Id; })[0] || ITEMS[0];
    if (art === 'Backdrop') return svg(1920, 1080, scene(it, 1920, 1080));
    var size = Math.min(64, Math.round(1000 / it.Name.length));
    return svg(600, 900, scene(it, 600, 900) +
      '<text x="300" y="790" text-anchor="middle" font-family="Arial, Helvetica, sans-serif" font-weight="800" font-size="' + size + '" letter-spacing="2" fill="#fff">' + esc(it.Name.toUpperCase()) + '</text>');
  };
})();
