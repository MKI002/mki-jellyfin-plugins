/* Example data for the MKI Password Policy demo: a household of made-up accounts in every password state. */
(function () {
  'use strict';
  var ID = 'ea705ff9-2793-4156-a253-3720f294ec89', DAY = 864e5;
  var CHANGE_URL = 'https://jellyfin.example.org/MkiPasswordPolicy/Change';
  var ADMIN = MkiDemo.adminId, KIDS = '7c41e9b2-3d5a-4f68-a1b0-c2d3e4f5a6b7';
  MkiDemo.plugin(ID, {
    DefaultProfile: 'standard', UserProfiles: [{ UserId: KIDS, Profile: 'none' }],
    MinLength: 10, RequireUpper: true, RequireLower: true, RequireDigit: true, RequireSymbol: false,
    ForbidUsername: true, BlockCommon: true, MaxRepeat: 0, ExemptAdministrators: false, ApplyToAdminChanges: true,
    ExpiryDays: 0, RequireChangeForNewUsers: true, NotifySessions: true, GraceSeconds: 20, NoticeDays: 3,
    NotifyText: '', PublicUrl: 'https://jellyfin.example.org', WebHints: true
  });

  var PRESETS = {
    basic: { MinLength: 8, RequireUpper: false, RequireLower: false, RequireDigit: true, RequireSymbol: false, ForbidUsername: true, BlockCommon: true, MaxRepeat: 0 },
    standard: { MinLength: 10, RequireUpper: true, RequireLower: true, RequireDigit: true, RequireSymbol: false, ForbidUsername: true, BlockCommon: true, MaxRepeat: 0 },
    strong: { MinLength: 12, RequireUpper: true, RequireLower: true, RequireDigit: true, RequireSymbol: true, ForbidUsername: true, BlockCommon: true, MaxRepeat: 3 },
    verystrong: { MinLength: 16, RequireUpper: true, RequireLower: true, RequireDigit: true, RequireSymbol: true, ForbidUsername: true, BlockCommon: true, MaxRepeat: 2 },
    passphrase: { MinLength: 20, RequireUpper: false, RequireLower: false, RequireDigit: false, RequireSymbol: false, ForbidUsername: true, BlockCommon: true, MaxRepeat: 3 }
  };
  var COMMON = ['password', '123456', '12345678', 'qwerty', 'letmein', 'welcome', 'iloveyou', 'monkey', 'dragon', 'football', 'sunshine', 'jellyfin', 'password1', 'admin', 'abc123'];

  function ago(days) { return new Date(Date.now() - days * DAY).toISOString(); }
  var users = [
    { Id: ADMIN, Name: 'admin', IsAdmin: true, verified: 'strong', changed: 40 },
    { Id: '2b8d4f6a-1c3e-4a5b-9d7f-0e2c4a6b8d1f', Name: 'Alex', verified: 'strong', changed: 12 },
    { Id: '5e7a9c1b-3d5f-4e6a-8b0c-2d4f6a8c0e1b', Name: 'Jordan', verified: 'basic', changed: 210, must: { reason: 'admin', notice: 5 }, reminded: ['Android TV', 'Jellyfin Web'] },
    { Id: KIDS, Name: 'Kids TV', nopw: true },
    { Id: '9a1c3e5b-7d9f-4b2a-8c4e-6f8a0b2d4c6e', Name: 'Robin', changed: 400 },
    { Id: '3f5b7d9a-1e3c-4d6f-a8b0-c2e4f6a8b0d2', Name: 'Sam', verified: 'basic', changed: 95 },
    { Id: '8e0a2c4d-6f8b-4a1c-9e3d-5b7f9a1c3e5d', Name: 'Guest', disabled: true, changed: 600 }
  ];

  function assigned(u, c) {
    var hit = (c.UserProfiles || []).filter(function (e) { return e && String(e.UserId).toLowerCase() === u.Id; })[0];
    return hit ? String(hit.Profile == null ? 'default' : hit.Profile) : 'default';
  }
  function rulesOf(name, c) {
    if (!name || name === 'none') return null;
    if (name === 'custom') return { MinLength: c.MinLength, RequireUpper: c.RequireUpper, RequireLower: c.RequireLower, RequireDigit: c.RequireDigit, RequireSymbol: c.RequireSymbol, ForbidUsername: c.ForbidUsername, BlockCommon: c.BlockCommon, MaxRepeat: c.MaxRepeat };
    return PRESETS[name] || null;
  }
  function covers(v, r) {
    return v.MinLength >= r.MinLength && ['RequireUpper', 'RequireLower', 'RequireDigit', 'RequireSymbol', 'ForbidUsername', 'BlockCommon'].every(function (k) { return !r[k] || v[k]; })
      && (!r.MaxRepeat || (v.MaxRepeat > 0 && v.MaxRepeat <= r.MaxRepeat));
  }
  function row(u, c) {
    var exempt = !!c.ExemptAdministrators && !!u.IsAdmin, a = assigned(u, c), eff = exempt ? 'none' : (a === 'default' ? c.DefaultProfile : a);
    var rules = rulesOf(eff, c), v = u.verified ? PRESETS[u.verified] : null;
    var status = u.nopw ? 'none' : !rules ? 'norules' : !v ? 'unknown' : covers(v, rules) ? 'ok' : 'older';
    return {
      Id: u.Id, Name: u.Name, IsAdmin: !!u.IsAdmin, IsDisabled: !!u.disabled, IsExempt: exempt, Profile: a, EffectiveProfile: eff,
      MustChange: !!u.must, Reason: u.must ? u.must.reason : null,
      Deadline: u.must && u.must.notice ? new Date(u.must.since + u.must.notice * DAY).toISOString() : null,
      RemindedOn: u.must ? (u.reminded || []) : [], LastChangedUtc: u.changed != null ? ago(u.changed) : null,
      PasswordStatus: status, VerifiedRules: v
    };
  }
  users.forEach(function (u) { if (u.must) u.must.since = Date.now() - DAY; });
  function find(ids) { ids = (ids || []).map(function (x) { return String(x).toLowerCase(); }); return users.filter(function (u) { return ids.indexOf(u.Id) >= 0; }); }

  MkiDemo.route('GET', /^MkiPasswordPolicy\/Users$/, function () {
    var c = MkiDemo.config(ID);
    return { Users: users.map(function (u) { return row(u, c); }), Presets: PRESETS, Common: COMMON, ChangeUrl: CHANGE_URL };
  });
  MkiDemo.route('POST', /^MkiPasswordPolicy\/Require$/, function (r) {
    var b = r.body || {}, c = MkiDemo.config(ID), days = Math.min(30, Math.max(0, b.NoticeDays == null ? c.NoticeDays : b.NoticeDays)), list;
    if (b.All || b.OnlyNotMeetingRules) {
      list = users.filter(function (u) {
        if (u.Id === ADMIN || (u.IsAdmin && !b.IncludeAdministrators)) return false;
        return !b.OnlyNotMeetingRules || ['unknown', 'older', 'none'].indexOf(row(u, c).PasswordStatus) >= 0;
      });
    } else list = find(b.UserIds);
    list.forEach(function (u) { u.must = { reason: 'admin', notice: days, since: Date.now() }; u.reminded = []; });
    return { Count: list.length };
  });
  MkiDemo.route('POST', /^MkiPasswordPolicy\/Release$/, function (r) {
    var b = r.body || {};
    (b.All ? users : find(b.UserIds)).forEach(function (u) { if (!u.nopw) u.must = null; });
  });
  MkiDemo.route('POST', /^MkiPasswordPolicy\/TemporaryPassword$/, function (r) {
    var u = find(r.body && r.body.UserIds)[0];
    if (!u || u.Id === ADMIN) throw MkiDemo.fail(400);
    u.nopw = false; u.verified = null; u.changed = 0; u.must = { reason: 'temporary', notice: 0, since: Date.now() };
    var words = ['Amber', 'Comet', 'Falcon', 'Harbor', 'Maple', 'Orbit', 'Pebble', 'Tundra'];
    var pw = words[Math.floor(Math.random() * 8)] + '-' + words[Math.floor(Math.random() * 8)].toLowerCase() + '-' + (100 + Math.floor(Math.random() * 900));
    return { Password: pw, ChangeUrl: CHANGE_URL };
  });

  /* the shared checker the settings page loads for "Try a password" */
  MkiDemo.serve('MkiPasswordPolicy/client.js', 'MkiPasswordPolicy/client.js');
})();
