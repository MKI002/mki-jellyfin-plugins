/* Example data for the MKI Media Hero demo: made-up titles with drawn artwork, filtered by the saved settings. */
(function () {
  'use strict';
  var ID = '6f3a1d84-2b57-4c19-9a0e-7d51c8e4b2f6', BRIDGE = 'b7d41e02-9c58-4f6a-8d13-5a2c7e9f4610';
  MkiDemo.plugin(ID, {
    Libraries: [
      { Id: '4d6f8a0b-2c4e-4f6a-8b0d-1e3f5a7c9e0b', Name: 'Movies', Enabled: true, Count: 8 },
      { Id: '6f8a0c2e-4a6c-4e8a-9c1e-3a5c7e9a1c3e', Name: 'Shows', Enabled: true, Count: 5 },
      { Id: '8a0c2e4a-6c8e-4a0c-8e2a-5c7e9a1c3e5a', Name: 'Kids', Enabled: false, Count: 10 },
      { Id: '0c2e4a6c-8e0a-4c2e-9a4c-7e9a1c3e5a7c', Name: 'Music', Enabled: false, Count: 10 }
    ],
    Sources: [
      { Source: 'RecentlyAdded', Take: 12, Enabled: true, MinYear: 0 },
      { Source: 'TopRated', Take: 6, Enabled: true, MinYear: 0 },
      { Source: 'JellyseerrTrending', Take: 8, Enabled: false, MinYear: 0 }
    ],
    ShuffleResult: true, ShuffleOnLoad: false, IncludeItemTypes: ['Movie', 'Series'],
    TitleWhitelist: [], TitleBlacklist: [], GenreWhitelist: [], GenreBlacklist: ['Horror'], TagWhitelist: [], TagBlacklist: [],
    OfficialRatingBlacklist: [], MinCommunityRating: 0, MinProductionYear: 0, RequireBackdrop: true, RequireLogo: false,
    ExcludeWatched: false, MinBackdropWidth: 0, JellyseerrUrl: '', JellyseerrApiKey: '', JellyseerrOnlyInLibrary: true,
    JellyseerrMinPopularity: 0, Layout: 'Fullwidth', Transition: 'Fade', Indicator: 'Dots', HeightVh: 56, MaxHeightPx: 720,
    CornerRadius: 12, ScrimStrength: 70, BlendIntoHeader: true, BottomFadePercent: 18, LogoMaxWidthPercent: 34,
    LogoMaxHeightPercent: 40, TextLeftPercent: 4, TextBottomPercent: 8, TextWidthPercent: 52, ElementGap: 7, TitleScale: 100,
    TextScale: 100, ButtonScale: 100, ShowLogo: true, ShowTitleWhenNoLogo: true, ShowOverview: true, OverviewLines: 3,
    ShowYear: true, ShowRating: true, ShowRuntime: true, ShowGenres: true, ShowOfficialRating: true, ShowPlayButton: true,
    ShowDetailsButton: true, ShowTrailerButton: false, ShowFavoriteButton: false, Autoplay: true, IntervalSeconds: 9,
    PauseOnHover: true, EnableKeyboard: true, EnableSwipe: true, RespectReducedMotion: true, CacheMinutes: 30,
    HomeOnly: true, Enabled: true, CustomCss: ''
  });

  /* name, type, year, rating, age rating, minutes, genres, two artwork colours, logo or not */
  var CATALOGUE = [
    ['The Last Lighthouse', 'Movie', 2024, 7.8, 'PG-13', 118, ['Drama', 'Mystery'], '#0f3b57', '#f2a65a', 1],
    ['Northbound Express', 'Movie', 2022, 7.0, 'PG-13', 104, ['Thriller', 'Action'], '#2b1a3f', '#ff5a6e', 1],
    ['A Quiet Orbit', 'Movie', 2026, 7.9, 'PG', 126, ['Science Fiction', 'Drama'], '#081a33', '#5bc0ff', 1],
    ['The Lighthouse Keepers', 'Series', 2021, 8.1, 'TV-14', 52, ['Drama'], '#16302b', '#9fe3c2', 1],
    ['Paper Moon Harbor', 'Movie', 2018, 6.6, 'PG', 97, ['Comedy', 'Romance'], '#3a2416', '#ffd27a', 0],
    ['Orbit Station Seven', 'Series', 2017, 7.1, 'TV-PG', 44, ['Science Fiction', 'Adventure'], '#101d3a', '#b48cff', 1],
    ['Ember Valley', 'Movie', 2023, 7.3, 'PG-13', 111, ['Adventure', 'Family'], '#3b1608', '#ff8f3c', 1],
    ['North of Nowhere', 'Series', 2020, 8.4, 'TV-MA', 58, ['Crime', 'Drama'], '#1c2326', '#c9d6df', 1],
    ['The Hollow Signal', 'Movie', 2025, 6.2, 'R', 101, ['Horror', 'Mystery'], '#120b14', '#9b2c4f', 1],
    ['Glass Garden', 'Series', 2024, 7.6, 'TV-14', 47, ['Drama', 'Fantasy'], '#0e2e22', '#6ff0b0', 0],
    ['Midnight Ferry', 'Movie', 2019, 6.9, 'PG-13', 93, ['Thriller'], '#0b1830', '#7aa7ff', 1],
    ['Saltwater Kids', 'Series', 2022, 7.4, 'TV-G', 24, ['Family', 'Comedy'], '#0b3440', '#ffe066', 1],
    ['Copper Skies', 'Movie', 2021, 7.2, 'PG', 108, ['Western', 'Adventure'], '#2e1a0e', '#e39b5b', 1],
    ['Static Hearts', 'Movie', 2016, 5.9, 'PG-13', 95, ['Romance', 'Drama'], '#2a0f26', '#ff7ac8', 0]
  ];
  var OVERVIEW = 'A made-up title for this demo. In your library this line shows the real overview, cut to the number of lines you choose.';
  var built = Math.floor(Date.now() / 1000) - 600, order = null;

  function idOf(i) { var h = ('00000000' + ((i + 1) * 2654435761 >>> 0).toString(16)).slice(-8); return h + '-1a2b-4c3d-8e4f-' + ('000000000000' + (i + 1)).slice(-12); }
  var ITEMS = CATALOGUE.map(function (c, i) {
    return {
      Id: idOf(i), Name: c[0], Overview: OVERVIEW, Type: c[1], Year: c[2], Rating: c[3], OfficialRating: c[4], RuntimeMinutes: c[5],
      Genres: c[6], BackdropTag: 'b' + i, LogoTag: c[9] ? 'l' + i : null, PosterTag: 'p' + i, ThumbTag: 't' + i, PrimaryColor: c[8],
      Source: 'RecentlyAdded', PlayId: idOf(i), _c1: c[7], _c2: c[8], _days: i * 3 + 1
    };
  });

  function lower(a) { return (a || []).map(function (x) { return String(x).toLowerCase(); }); }
  function build() {
    var c = MkiDemo.config(ID), types = c.IncludeItemTypes || [], gw = lower(c.GenreWhitelist), gb = lower(c.GenreBlacklist);
    var tw = lower(c.TitleWhitelist), tb = lower(c.TitleBlacklist), ob = lower(c.OfficialRatingBlacklist), out = [], seen = {};
    var pool = ITEMS.filter(function (it) {
      var g = lower(it.Genres), n = it.Name.toLowerCase();
      if (types.length && types.indexOf(it.Type) < 0) return false;
      if (gw.length && !g.some(function (x) { return gw.indexOf(x) >= 0; })) return false;
      if (g.some(function (x) { return gb.indexOf(x) >= 0; })) return false;
      if (tb.some(function (x) { return n.indexOf(x) >= 0; })) return false;
      if (ob.indexOf(String(it.OfficialRating).toLowerCase()) >= 0) return false;
      if (c.MinCommunityRating && it.Rating < c.MinCommunityRating) return false;
      if (c.MinProductionYear && it.Year < c.MinProductionYear) return false;
      if (c.RequireLogo && !it.LogoTag) return false;
      return true;
    });
    function add(list, source, take, minYear) {
      list.filter(function (it) { return !seen[it.Id] && (!minYear || it.Year >= minYear); }).slice(0, take).forEach(function (it) {
        var x = JSON.parse(JSON.stringify(it)); x.Source = source; seen[it.Id] = 1; out.push(x);
      });
    }
    if (tw.length) add(pool.filter(function (it) { return tw.indexOf(it.Name.toLowerCase()) >= 0; }), 'Whitelist', 99, 0);
    (c.Sources || []).forEach(function (s) {
      if (!s.Enabled) return;
      var list = pool.slice();
      if (s.Source === 'TopRated') list.sort(function (a, b) { return b.Rating - a.Rating; });
      else if (s.Source === 'RecentlyReleased') list.sort(function (a, b) { return b.Year - a.Year; });
      else if (s.Source === 'Random' || /^Jellyseerr/.test(s.Source)) list.sort(function (a, b) { return (a.Id < b.Id ? -1 : 1) * (s.Source === 'Random' ? 1 : -1); });
      else if (s.Source === 'Favorites') list = list.filter(function (it, i) { return i % 3 === 0; });
      else if (s.Source === 'NextUp') list = list.filter(function (it) { return it.Type === 'Series'; });
      add(list, s.Source, s.Take, s.MinYear);
    });
    if (c.ShuffleResult) {
      if (!order) order = out.map(function () { return Math.random(); });
      out = out.map(function (it, i) { return { it: it, k: order[i] == null ? Math.random() : order[i] }; }).sort(function (a, b) { return a.k - b.k; }).map(function (x) { return x.it; });
    }
    return out;
  }

  MkiDemo.route('GET', /^MediaHero\/items$/, function () { return { Items: build(), BuiltAtUnix: built, Display: null }; });
  MkiDemo.route('POST', /^MediaHero\/refresh$/, function () { built = Math.floor(Date.now() / 1000); order = null; });
  MkiDemo.route('GET', /^Library\/VirtualFolders$/, function () {
    return [
      { Name: 'Movies', ItemId: '4d6f8a0b-2c4e-4f6a-8b0d-1e3f5a7c9e0b', CollectionType: 'movies', Locations: [] },
      { Name: 'Shows', ItemId: '6f8a0c2e-4a6c-4e8a-9c1e-3a5c7e9a1c3e', CollectionType: 'tvshows', Locations: [] },
      { Name: 'Kids', ItemId: '8a0c2e4a-6c8e-4a0c-8e2a-5c7e9a1c3e5a', CollectionType: 'movies', Locations: [] },
      { Name: 'Music', ItemId: '0c2e4a6c-8e0a-4c2e-9a4c-7e9a1c3e5a7c', CollectionType: 'music', Locations: [] }
    ];
  });
  MkiDemo.route('GET', /^Genres$/, function () {
    var names = {};
    ITEMS.forEach(function (it) { it.Genres.forEach(function (g) { names[g] = 1; }); });
    return { Items: Object.keys(names).sort().map(function (n) { return { Name: n }; }) };
  });
  MkiDemo.route('GET', /^Plugins$/, function () {
    return [{ Id: ID, Name: 'MKI Media Hero', Status: 'Active' }, { Id: BRIDGE, Name: 'MKI Seerr Bridge', Status: 'Active' }];
  });
  MkiDemo.route('GET', /^Plugins\/[\w-]+\/Configuration$/, function () {
    return { ServerUrl: 'http://seerr:5055', PublicUrl: 'https://requests.example.org', ApiKey: 'demo.key.not-real' };
  });

  /* The banner script and its stylesheet come from the plugin itself. */
  MkiDemo.serve('MediaHero/client.js', 'MediaHero/client.js');

  /* Drawn artwork instead of real images: a gradient scene for backdrops and thumbs, the title for logos. */
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  function svg(w, h, body) { return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '" viewBox="0 0 ' + w + ' ' + h + '">' + body + '</svg>'); }
  function scene(it, w, h) {
    var a = it._c1, b = it._c2, seed = it.Name.length * 37 + it.Year;
    function r(n) { seed = (seed * 9301 + 49297) % 233280; return seed / 233280 * n; }
    var stars = '';
    for (var i = 0; i < 28; i++) stars += '<circle cx="' + r(w).toFixed(0) + '" cy="' + r(h * 0.5).toFixed(0) + '" r="' + (1 + r(2)).toFixed(1) + '" fill="#fff" opacity="' + (0.2 + r(0.5)).toFixed(2) + '"/>';
    var sun = '<circle cx="' + (w * (0.55 + r(0.3))).toFixed(0) + '" cy="' + (h * (0.3 + r(0.15))).toFixed(0) + '" r="' + (h * 0.17).toFixed(0) + '" fill="' + b + '" opacity=".85"/>';
    function ridge(y, amp, col, op) {
      var d = 'M0 ' + h + ' L0 ' + y, x = 0;
      while (x < w) { x += w / 7; d += ' L' + x.toFixed(0) + ' ' + (y - r(amp)).toFixed(0); }
      return '<path d="' + d + ' L' + w + ' ' + h + 'Z" fill="' + col + '" opacity="' + op + '"/>';
    }
    return '<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + a + '"/><stop offset=".62" stop-color="' + b + '" stop-opacity=".55"/><stop offset="1" stop-color="' + a + '"/></linearGradient></defs>' +
      '<rect width="100%" height="100%" fill="' + a + '"/><rect width="100%" height="100%" fill="url(#g)"/>' + stars + sun +
      ridge(h * 0.72, h * 0.18, a, 0.75) + ridge(h * 0.84, h * 0.12, '#000', 0.45);
  }
  MkiDemo.image = function (item, art, width) {
    var it = ITEMS.filter(function (x) { return x.Id === item.Id; })[0] || ITEMS[0];
    if (art === 'Logo') {
      if (!item.LogoTag) return null;
      return svg(1000, 300, '<text x="0" y="150" dominant-baseline="middle" font-family="Georgia, \'Times New Roman\', serif" font-weight="700" font-size="' + Math.min(130, Math.round(1300 / it.Name.length)) + '" letter-spacing="2" fill="#fff" stroke="' + it._c2 + '" stroke-width="2">' + esc(it.Name.toUpperCase()) + '</text>');
    }
    if (art === 'Primary') return svg(600, 900, scene(it, 600, 900));
    return svg(1920, 1080, scene(it, 1920, 1080));
  };
})();
