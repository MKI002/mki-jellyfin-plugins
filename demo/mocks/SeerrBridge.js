/* Example data for the MKI Seerr Bridge demo: a Seerr server that answers and a small made-up catalogue. */
(function () {
  'use strict';
  var ID = 'b7d41e02-9c58-4f6a-8d13-5a2c7e9f4610';
  /* Seerr on a subdomain of wherever the demo is served, so the page's same-site and cookie checks pass */
  var host = location.hostname || 'jellyfin.example.org', https = location.protocol === 'https:';
  MkiDemo.plugin(ID, {
    Enabled: true, PublicUrl: (https ? 'https' : 'http') + '://requests.' + host, ServerUrl: 'http://seerr:5055',
    ApiKey: 'demo.key.not-real', Label: 'Requests', ShowInHeader: true, ShowInDrawer: true, EnableSearch: true,
    SearchLimit: 8, HideAvailable: true, AllowRequests: true, MapUsers: true, Language: 'en',
    LegacyAuthHeader: true, EnableSso: true, CookieDomain: ''
  });

  var CATALOGUE = [
    { title: 'The Last Lighthouse', mediaType: 'movie', year: '2024', rating: 7.8, status: 'vorhanden' },
    { title: 'The Lighthouse Keepers', mediaType: 'tv', year: '2021', rating: 8.1, status: 'teilweise' },
    { title: 'Lighthouse at the End of the Map', mediaType: 'movie', year: '2019', rating: 6.9, status: 'neu' },
    { title: 'Lights over Harbor Bay', mediaType: 'tv', year: '2025', rating: 7.2, status: 'angefragt' },
    { title: 'Light Years Apart', mediaType: 'movie', year: '2023', rating: 7.4, status: 'in Arbeit' },
    { title: 'Northbound Express', mediaType: 'movie', year: '2022', rating: 7.0, status: 'vorhanden' },
    { title: 'North of Nowhere', mediaType: 'tv', year: '2020', rating: 8.4, status: 'neu' },
    { title: 'Paper Moon Harbor', mediaType: 'movie', year: '2018', rating: 6.6, status: 'neu' },
    { title: 'A Quiet Orbit', mediaType: 'movie', year: '2026', rating: 7.9, status: 'neu' },
    { title: 'Orbit Station Seven', mediaType: 'tv', year: '2017', rating: 7.1, status: 'vorhanden' },
    { title: 'The Orbiting Garden', mediaType: 'movie', year: '2025', rating: 0, status: 'neu' }
  ];
  var REQUESTABLE = { neu: true, teilweise: true };

  function configured(c) { return !!String(c.ServerUrl || c.PublicUrl || '').trim() && !!String(c.ApiKey || '').trim(); }

  MkiDemo.route('GET', /^SeerrBridge\/check$/, function () {
    var c = MkiDemo.config(ID);
    return configured(c) ? { ok: true, text: 'Connected, Seerr 2.7.3' } : { ok: false, text: 'Address or API key missing' };
  });
  MkiDemo.route('GET', /^SeerrBridge\/search$/, function (r) {
    var c = MkiDemo.config(ID), q = String(r.query.q || '').trim().toLowerCase();
    if (!c.Enabled || !c.EnableSearch || !configured(c) || !q) return { items: [] };
    var words = q.split(/\s+/);
    var hits = CATALOGUE.filter(function (it) {
      var t = it.title.toLowerCase();
      return words.every(function (w) { return t.indexOf(w) >= 0 || t.indexOf(w.replace(/s$/, '')) >= 0; });
    });
    if (!hits.length) hits = CATALOGUE.filter(function (it) { return it.title.toLowerCase().indexOf(q.slice(0, 3)) >= 0; });
    if (c.HideAvailable) hits = hits.filter(function (it) { return it.status !== 'vorhanden'; });
    return {
      items: hits.slice(0, Math.min(40, Math.max(1, c.SearchLimit || 8))).map(function (it) {
        return { title: it.title, mediaType: it.mediaType, year: it.year, rating: it.rating, status: it.status, requestable: !!c.AllowRequests && !!REQUESTABLE[it.status] };
      })
    };
  });
  MkiDemo.route('GET', /^QuickConnect\/Enabled$/, function () { return true; });
  MkiDemo.route('GET', /^System\/Configuration$/, function () { return { EnableLegacyAuthorization: false, ServerName: 'Demo' }; });
})();
