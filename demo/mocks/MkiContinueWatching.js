/* Example data for the MKI Continue Watching Cleanup demo: a few users with half-watched videos of different ages. */
(function () {
  'use strict';
  var ID = 'bfc7b26a-b0fb-4040-944c-c9417ac73c54', DAY = 864e5, KEEP = 30;
  MkiDemo.plugin(ID, { EnableRemoveButton: true, EnableAutoCleanup: true, AutoCleanupDays: 30 });

  /* days since each resumable video was last played, per user */
  var users = [
    { Name: 'Alex', ages: [1, 3, 12, 41, 58, 95, 140] },
    { Name: 'Sam', ages: [2, 6, 33, 70] },
    { Name: 'Robin', ages: [0, 4, 9, 18, 27, 64, 120, 210, 365] },
    { Name: 'Kim', ages: [5, 22] },
    { Name: 'Jordan', ages: [45, 80, 150] },
    { Name: 'Guest', ages: [] }
  ];
  var runs = [
    { Id: 'c2a7e1f0-4b9d-4e3a-8f6b-2d1c0e9a7b5f', At: new Date(Date.now() - 1 * DAY + 3 * 36e5).toISOString(), Days: 30, Cleared: 4, Users: 2, Trigger: 'schedule', UndoneAt: null, Restored: 0, Finished: true, Partial: false, UndoIncomplete: false, cleared: [] },
    { Id: '9e4d2b1a-7c6f-4e0d-8b3a-5c7e9f1d2b4a', At: new Date(Date.now() - 2 * DAY + 3 * 36e5).toISOString(), Days: 30, Cleared: 0, Users: 0, Trigger: 'schedule', UndoneAt: null, Restored: 0, Finished: true, Partial: false, UndoIncomplete: false, cleared: [] },
    { Id: '5b8f3e2d-1a0c-4b7e-9d6f-2a4c8e1b3d5f', At: new Date(Date.now() - 6 * DAY + 3 * 36e5).toISOString(), Days: 45, Cleared: 2, Users: 1, Trigger: 'manual', UndoneAt: new Date(Date.now() - 6 * DAY + 4 * 36e5).toISOString(), Restored: 2, Finished: true, Partial: false, UndoIncomplete: false, cleared: [] }
  ];
  /* the run from yesterday cleared these; undo puts them back */
  runs[0].cleared = [{ user: 'Alex', age: 31 }, { user: 'Alex', age: 36 }, { user: 'Jordan', age: 31 }, { user: 'Jordan', age: 39 }];

  function clamp(d) { d = parseInt(d, 10); return Math.min(3650, Math.max(7, isNaN(d) ? MkiDemo.config(ID).AutoCleanupDays : d)); }
  function preview(d) {
    return users.map(function (u) { return { Name: u.Name, Total: u.ages.length, Stale: u.ages.filter(function (a) { return a >= d; }).length }; });
  }
  function hex() { var s = ''; for (var i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16); return s; }

  MkiDemo.route('GET', /^ScheduledTasks$/, function () {
    return [{ Name: 'Clean up Continue Watching (MKI)', Key: 'MkiContinueWatchingCleanup', Id: '8d2f4b6a-1c3e-4f5a-9b7d-0e2c4a6f8b1d', State: 'Idle', Triggers: [{ Type: 'DailyTrigger', TimeOfDayTicks: 4 * 36e9 }] }];
  });
  MkiDemo.route('GET', /^MkiContinueWatching\/Overview$/, function (r) {
    var d = clamp(r.query.days);
    return {
      Days: d, MinDays: 7, MaxDays: 3650, KeepDays: KEEP, Users: preview(d),
      Runs: runs.slice(0, 10).map(function (x) { var c = {}; Object.keys(x).forEach(function (k) { if (k !== 'cleared') c[k] = x[k]; }); return c; }),
      RemovedLast7Days: 3
    };
  });
  MkiDemo.route('POST', /^MkiContinueWatching\/Run$/, function () {
    var c = MkiDemo.config(ID);
    if (!c.EnableAutoCleanup) throw MkiDemo.fail(409, 'Auto cleanup is off. Turn it on first.');
    var d = clamp(c.AutoCleanupDays), cleared = [], who = {};
    users.forEach(function (u) {
      u.ages = u.ages.filter(function (a) { if (a >= d) { cleared.push({ user: u.Name, age: a }); who[u.Name] = 1; return false; } return true; });
    });
    var run = { Id: hex(), At: new Date().toISOString(), Days: d, Cleared: cleared.length, Users: Object.keys(who).length, Trigger: 'manual', UndoneAt: null, Restored: 0, Finished: true, Partial: false, UndoIncomplete: false, cleared: cleared };
    runs.unshift(run);
    var out = {}; Object.keys(run).forEach(function (k) { if (k !== 'cleared') out[k] = run[k]; });
    return out;
  });
  MkiDemo.route('POST', /^MkiContinueWatching\/UndoRun$/, function (r) {
    var run = runs.filter(function (x) { return x.Id === (r.body && r.body.RunId); })[0];
    if (!run) throw MkiDemo.fail(404, 'That cleanup run is no longer in the undo history.');
    if (run.UndoneAt) return { Restored: 0 };
    run.cleared.forEach(function (c) { users.forEach(function (u) { if (u.Name === c.user) u.ages.push(c.age); }); });
    run.UndoneAt = new Date().toISOString(); run.Restored = run.cleared.length;
    return { Restored: run.Restored };
  });
})();
