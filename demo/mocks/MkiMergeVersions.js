/* Example data for the MKI Merge Versions demo: the plugin's two settings and its scheduled task. */
(function () {
  'use strict';
  var ID = 'b8e2f3a1-6c47-4d59-9f0e-7a1c2d3e4b5f', HOUR = 36e5, RUN_MS = 7000;
  MkiDemo.plugin(ID, { DryRun: false, FixPrimaryOrder: true });

  var started = Date.now() - 22 * 60000;
  var task = {
    Name: 'Merge movie versions (MKI)', Key: 'MkiMergeVersionsMovies', Id: '3f1c9a7e-5b2d-4c8e-9a6f-1b3d5c7e9a2b',
    Category: 'Library', Description: 'Merges movies that exist in several resolutions into one entry.',
    State: 'Idle', CurrentProgressPercentage: null, IsHidden: false,
    LastExecutionResult: { StartTimeUtc: new Date(started).toISOString(), EndTimeUtc: new Date(started + 2400).toISOString(), Status: 'Completed', Name: 'Merge movie versions (MKI)', Key: 'MkiMergeVersionsMovies', Id: '3f1c9a7e-5b2d-4c8e-9a6f-1b3d5c7e9a2b' },
    Triggers: [{ Type: 'StartupTrigger' }, { Type: 'IntervalTrigger', IntervalTicks: HOUR * 1e4 }]
  };
  var run = null, lines = [];

  function stamp(d) {
    function p(n) { return (n < 10 ? '0' : '') + n; }
    return '[' + d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) + '.000 +00:00] [INF] [42] Jellyfin.Plugin.MkiMergeVersions.MergeService: ';
  }
  var MOVIES = [['The Last Lighthouse', 3840, 1920], ['Paper Moon Harbor', 3840, 1280], ['A Quiet Orbit', 1920, 1280], ['Northbound Express', 3840, 1920]];
  function logRun(at, dry) {
    var t = new Date(at);
    lines.push(stamp(t) + 'MKI Merge: starting' + (dry ? ' (dry run)' : '') + ', 1 284 movies checked');
    MOVIES.forEach(function (m) {
      lines.push(stamp(t) + 'MKI Merge: ' + (dry ? 'would merge ' : 'merged ') + '"' + m[0] + '" [' + m[1] + 'px + ' + m[2] + 'px] -> primary entry ' + m[1] + 'px');
    });
    lines.push(stamp(t) + 'MKI Merge: done, ' + MOVIES.length + (dry ? ' groups would be merged' : ' groups merged') + ', 0 errors');
  }
  logRun(started, false);

  function tick() {
    if (!run) return;
    var done = Math.min(1, (Date.now() - run.at) / RUN_MS);
    if (task.State === 'Cancelling' || done >= 1) {
      var stopped = task.State === 'Cancelling';
      if (!stopped) logRun(Date.now(), MkiDemo.config(ID).DryRun === true);
      task.LastExecutionResult = { StartTimeUtc: new Date(run.at).toISOString(), EndTimeUtc: new Date().toISOString(), Status: stopped ? 'Cancelled' : 'Completed', Name: task.Name, Key: task.Key, Id: task.Id };
      task.State = 'Idle'; task.CurrentProgressPercentage = null; run = null;
    } else task.CurrentProgressPercentage = Math.round(done * 1000) / 10;
  }

  MkiDemo.route('GET', /^ScheduledTasks$/, function () { tick(); return [task]; });
  MkiDemo.route('GET', /^ScheduledTasks\/([\w-]+)$/, function (r) { tick(); if (r.match[1] !== task.Id) throw MkiDemo.fail(404); return task; });
  MkiDemo.route('POST', /^ScheduledTasks\/Running\/([\w-]+)$/, function () {
    if (!run) { run = { at: Date.now() }; task.State = 'Running'; task.CurrentProgressPercentage = 0; }
  });
  MkiDemo.route('DELETE', /^ScheduledTasks\/Running\/([\w-]+)$/, function () { if (run) task.State = 'Cancelling'; });
  MkiDemo.route('GET', /^System\/Logs$/, function () {
    return [{ Name: 'log_' + new Date().toISOString().slice(0, 10).replace(/-/g, '') + '.log', Size: 184320, DateModified: new Date().toISOString() }];
  });
  MkiDemo.route('GET', /^System\/Logs\/Log$/, function () { return lines.join('\n'); });
})();
