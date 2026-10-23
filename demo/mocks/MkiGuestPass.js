/* Example data for the MKI Guest Pass demo: a weekend pass with two guests, a movie night that ended, and the
   libraries and ratings of a made-up server. Links and codes are made up here, in the browser. */
(function () {
  'use strict';
  var ID = '3ae42f4f-a017-405f-ab3d-02c3d876c5a2', MIN = 6e4, H = 36e5, DAY = 864e5;
  function at(ms) { return new Date(Date.now() + ms).toISOString(); }
  function ago(ms) { return at(-ms); }
  function guid() { return 'xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx'.replace(/x/g, function () { return (Math.random() * 16 | 0).toString(16); }); }
  function random(n, set) { var s = ''; for (var i = 0; i < n; i++) s += set.charAt(Math.random() * set.length | 0); return s; }
  function secret() { return random(43, 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'); }
  function code() { var c = random(8, 'ACDEFGHJKMNPQRTUVWXY34679'); return c.slice(0, 4) + '-' + c.slice(4); }
  /* Sunday 22:00 of this week, or of the next one late on a Sunday */
  function sunday() { var s = new Date(); s.setHours(22, 0, 0, 0); s.setDate(s.getDate() + (7 - s.getDay()) % 7); if (s - Date.now() < 3 * H) s.setDate(s.getDate() + 7); return s.toISOString(); }

  MkiDemo.plugin(ID, { DeleteAfterDays: 4, WarnHours: 2, ShowGuestBadge: true, ShowCodeLink: true, InviterName: 'Alex', PublicUrl: 'https://jellyfin.example.org', StreamsNeedSignIn: true });

  var libraries = [
    { Id: '0c6b6f8e-4c55-4a0f-9a51-2f1d3e7b9a10', Name: 'Movies', Type: 'movies' },
    { Id: '1d7c7a9f-5d66-4b1a-8b62-3a2e4f8cab21', Name: 'Shows', Type: 'tvshows' },
    { Id: '2e8d8bae-6e77-4c2b-9c73-4b3f5a9dbc32', Name: 'Kids', Type: 'movies' },
    { Id: '3f9e9cbf-7f88-4d3c-8d84-5c4a6baecd43', Name: 'Music', Type: 'music' },
    { Id: '4aafadc0-8a99-4e4d-9e95-6d5b7cbfde54', Name: 'Home videos', Type: 'homevideos' }
  ];
  var ratings = [
    { Score: 0, SubScore: null, Name: 'FSK 0 / G' }, { Score: 6, SubScore: null, Name: 'FSK 6 / PG' }, { Score: 12, SubScore: null, Name: 'FSK 12 / PG-13' },
    { Score: 16, SubScore: null, Name: 'FSK 16 / R' }, { Score: 18, SubScore: null, Name: 'FSK 18 / NC-17' }
  ];
  function libs(ids) { return ids.map(function (i) { return { Id: libraries[i].Id, Name: libraries[i].Name }; }); }
  var passes = [
    {
      Id: '7b1e2c3d-4f5a-4b6c-9d7e-8f9a0b1c2d3e', Name: 'Weekend at the lake', Libraries: libs([0, 1]), CreatedUtc: ago(20 * H), EndUtc: sunday(), Open: true, EndedEarly: false,
      MaxGuests: 3, Used: 2, DevicesPerGuest: 2, StreamsPerGuest: 1, Rating: 'FSK 16 / R', RemoteAccess: true, HasCode: true, CreatedByName: 'Alex',
      Guests: [
        { UserId: '9c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f', Name: 'Lena', UserName: 'Guest Lena', JoinedUtc: ago(19 * H), Remote: true, State: 'Active', LockedUtc: null, DeleteUtc: null, Devices: 2, DeviceList: [{ Name: 'Living room TV', App: 'Jellyfin Android TV', LastSeenUtc: ago(4 * MIN) }, { Name: 'Firefox', App: 'Jellyfin Web', LastSeenUtc: ago(5 * H) }], LastSeenUtc: ago(4 * MIN), Watching: 'Arrival' },
        { UserId: 'ad3e4f5a-6b7c-4d8e-9f0a-1b2c3d4e5f6a', Name: 'Tom', UserName: 'Guest Tom', JoinedUtc: ago(18 * H), Remote: false, State: 'Active', LockedUtc: null, DeleteUtc: null, Devices: 1, DeviceList: [{ Name: 'iPhone', App: 'Jellyfin Mobile (iOS)', LastSeenUtc: ago(3 * H) }], LastSeenUtc: ago(3 * H), Watching: null }
      ]
    },
    {
      Id: '8c2f3d4e-5a6b-4c7d-8e9f-a0b1c2d3e4f5', Name: 'Movie night', Libraries: libs([0, 2]), CreatedUtc: ago(3 * DAY), EndUtc: ago(2 * DAY + 2 * H), Open: false, EndedEarly: false,
      MaxGuests: 1, Used: 1, DevicesPerGuest: 1, StreamsPerGuest: 1, Rating: null, RemoteAccess: false, HasCode: false, CreatedByName: 'Alex',
      Guests: [
        { UserId: 'be4f5a6b-7c8d-4e9f-8a0b-2c3d4e5f6a7b', Name: 'Mia', UserName: 'Guest Mia', JoinedUtc: ago(3 * DAY - 20 * MIN), Remote: false, State: 'Locked', LockedUtc: ago(2 * DAY + 2 * H), DeleteUtc: at(2 * DAY - 2 * H), Devices: 0, DeviceList: [], LastSeenUtc: null, Watching: null }
      ]
    }
  ];
  function find(id) { return passes.filter(function (p) { return p.Id === id; })[0]; }
  function guest(id) { var hit = null; passes.forEach(function (p) { p.Guests.forEach(function (g) { if (g.UserId === id) hit = { p: p, g: g }; }); }); return hit; }
  function lock(g) { g.State = g.State === 'Active' ? 'Locked' : g.State; g.LockedUtc = new Date().toISOString(); g.DeleteUtc = at(((MkiDemo.config(ID) || {}).DeleteAfterDays || 0) * DAY); g.Devices = 0; g.DeviceList = []; g.Watching = null; }
  function shared(p) { return { Id: p.Id, Name: p.Name, EndUtc: p.EndUtc, MaxGuests: p.MaxGuests, Used: p.Used, From: p.CreatedByName, Token: secret(), Code: p.HasCode ? code() : null }; }

  MkiDemo.route('GET', /^MkiGuestPass\/Overview$/, function () {
    var now = Date.now();
    passes.forEach(function (p) { if (p.Open && new Date(p.EndUtc).getTime() <= now) { p.Open = false; p.Guests.forEach(function (g) { if (g.State === 'Active') lock(g); }); } });
    return {
      Now: new Date().toISOString(), ServerName: 'Demo server', Passes: passes, Libraries: libraries, Ratings: ratings,
      Limits: { MaxOpenPasses: 20, MaxGuestAccounts: 50, MaxGuestsPerPass: 10, MaxDevicesPerGuest: 5, MaxStreamsPerGuest: 5, MinMinutes: 10, MaxDays: 30 },
      OpenPasses: passes.filter(function (p) { return p.Open; }).length,
      GuestAccounts: passes.reduce(function (n, p) { return n + p.Guests.length; }, 0),
      CodesPausedUntil: null, QuickConnect: true, PasswordPolicyOutdated: false, WebClientProblem: null, UnknownProxy: false, DlnaInstalled: false
    };
  });
  MkiDemo.route('POST', /^MkiGuestPass\/Passes$/, function (r) {
    var b = r.body || {};
    if (!b.Name) throw MkiDemo.fail(400, 'Give the pass a name, for example "Weekend at the lake".');
    var rating = b.MaxRating == null ? null : ratings.filter(function (x) { return x.Score === b.MaxRating; })[0];
    var p = {
      Id: guid(), Name: String(b.Name).slice(0, 60), Libraries: libraries.filter(function (l) { return (b.Libraries || []).indexOf(l.Id) >= 0; }).map(function (l) { return { Id: l.Id, Name: l.Name }; }),
      CreatedUtc: new Date().toISOString(), EndUtc: b.EndUtc, Open: true, EndedEarly: false, MaxGuests: b.MaxGuests, Used: 0, DevicesPerGuest: b.DevicesPerGuest, StreamsPerGuest: b.StreamsPerGuest,
      Rating: rating ? rating.Name : null, RemoteAccess: !!b.RemoteAccess, HasCode: !!b.WithCode, CreatedByName: (MkiDemo.config(ID) || {}).InviterName || '', Guests: []
    };
    passes.unshift(p);
    return shared(p);
  });
  MkiDemo.route('POST', /^MkiGuestPass\/Passes\/([\w-]+)\/NewLink$/, function (r) {
    var p = find(r.match[1]); if (!p) throw MkiDemo.fail(404, 'That pass does not exist any more.');
    if (!p.Open) throw MkiDemo.fail(409, 'This pass has ended. Extend it first.');
    return shared(p);
  });
  MkiDemo.route('POST', /^MkiGuestPass\/Passes\/([\w-]+)\/Extend$/, function (r) {
    var p = find(r.match[1]); if (!p) throw MkiDemo.fail(404, 'That pass does not exist any more.');
    if (!p.Open && !r.body.Reopen) throw MkiDemo.fail(409, 'This pass has ended in the meantime. Nothing was changed; look again before opening it again.');
    p.EndUtc = r.body.EndUtc; p.Open = true; p.EndedEarly = false;
    p.Guests.forEach(function (g) { if (g.State === 'Locked') { g.State = 'Active'; g.LockedUtc = null; g.DeleteUtc = null; } });
    return { Done: true };
  });
  MkiDemo.route('POST', /^MkiGuestPass\/Passes\/([\w-]+)\/End$/, function (r) {
    var p = find(r.match[1]); if (!p) throw MkiDemo.fail(404, 'That pass does not exist any more.');
    p.EndUtc = new Date().toISOString(); p.Open = false; p.EndedEarly = true;
    p.Guests.forEach(function (g) { if (g.State === 'Active') lock(g); });
    return { Done: true };
  });
  MkiDemo.route('DELETE', /^MkiGuestPass\/Passes\/([\w-]+)$/, function (r) {
    var p = find(r.match[1]), seen = r.query && r.query.guests;
    if (p && seen != null && +seen !== p.Guests.length) throw MkiDemo.fail(409, 'Someone joined this pass in the meantime. Nothing was deleted; look again.');
    passes = passes.filter(function (p) { return p.Id !== r.match[1]; });
    return { Done: true };
  });
  MkiDemo.route('POST', /^MkiGuestPass\/Guests\/([\w-]+)\/End$/, function (r) {
    var hit = guest(r.match[1]); if (!hit) throw MkiDemo.fail(404, 'That guest does not exist any more.');
    lock(hit.g); hit.g.State = 'Ended';
    return { Done: true };
  });
  MkiDemo.route('DELETE', /^MkiGuestPass\/Guests\/([\w-]+)$/, function (r) {
    var hit = guest(r.match[1]); if (!hit) throw MkiDemo.fail(404, 'That guest does not exist any more.');
    hit.p.Guests = hit.p.Guests.filter(function (g) { return g !== hit.g; });
    return { Done: true };
  });
})();
