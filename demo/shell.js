/*
 * MKI plugin demos: a stand-in for the Jellyfin web client around a real settings page.
 *
 * build/demo.py puts each plugin's own configPage.html into demo/page.html and loads this file first, then the
 * plugin's mock (demo/mocks/<Plugin>.js). Nothing here talks to a server: ApiClient answers from the mock's example
 * data, saved settings live in memory only and are gone after a reload.
 */
(function () {
  'use strict';

  var routes = [], configs = {}, statics = {};

  function clone(v) { return v === undefined ? v : JSON.parse(JSON.stringify(v)); }
  function wait(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function lag() { return wait(90 + Math.round(Math.random() * 160)); }
  function fail(status, message) { var e = new Error(message || 'HTTP ' + status); e.status = status; e.message = message; return e; }

  function splitUrl(url) {
    var s = String(url || '').replace(/^api\//, ''), i = s.indexOf('?'), query = {};
    if (i >= 0) {
      s.slice(i + 1).split('&').forEach(function (p) {
        if (!p) return;
        var kv = p.split('=');
        query[decodeURIComponent(kv[0])] = decodeURIComponent((kv[1] || '').replace(/\+/g, ' '));
      });
      s = s.slice(0, i);
    }
    return { path: s, query: query };
  }

  /* Only what the settings pages and the kit use. */
  window.ApiClient = {
    getUrl: function (path, params) {
      path = String(path).replace(/^\//, '');
      if (statics[path]) return statics[path];
      var q = params ? Object.keys(params).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]); }).join('&') : '';
      return 'api/' + path + (q ? (path.indexOf('?') >= 0 ? '&' : '?') + q : '');
    },
    serverAddress: function () { return '.'; },
    accessToken: function () { return 'demo'; },
    getCurrentUserId: function () { return MkiDemo.adminId; },
    getCurrentUser: function () { return lag().then(function () { return { Id: MkiDemo.adminId, Name: 'admin', Policy: { IsAdministrator: true } }; }); },
    getPluginConfiguration: function (id) {
      return lag().then(function () {
        if (!configs[id]) throw fail(404);
        return clone(configs[id]);
      });
    },
    updatePluginConfiguration: function (id, cfg) {
      return lag().then(function () {
        configs[id] = clone(cfg);
        if (MkiDemo.onConfig) MkiDemo.onConfig(id, configs[id]);
        return {};
      });
    },
    getScheduledTasks: function () { return ApiClient.ajax({ type: 'GET', url: 'api/ScheduledTasks', dataType: 'json' }); },
    ajax: function (req) {
      var u = splitUrl(req.url), type = String(req.type || 'GET').toUpperCase(), body;
      try { body = req.data ? JSON.parse(req.data) : undefined; } catch (e) { body = req.data; }
      return lag().then(function () {
        for (var i = 0; i < routes.length; i++) {
          var r = routes[i], m = r.type === type && r.re.exec(u.path);
          if (!m) continue;
          var out = r.fn({ match: m, query: u.query, body: body, path: u.path });
          return Promise.resolve(out).then(function (v) {
            if (req.dataType === 'json' || req.dataType === 'text') return clone(v);
            return { ok: true, status: 204 };
          });
        }
        if (window.console) console.warn('MKI demo: no example data for', type, u.path);
        throw fail(404, 'Not part of the demo');
      });
    }
  };

  /* Plugin web scripts call the server with fetch: answer those from the same example data. */
  var realFetch = window.fetch && window.fetch.bind(window);
  window.fetch = function (url, init) {
    var u = splitUrl(String(url && url.url || url).replace(/^\.\//, '')), type = String(init && init.method || 'GET').toUpperCase();
    var hit = routes.some(function (r) { return r.type === type && r.re.test(u.path); });
    if (!hit) return realFetch ? realFetch(url, init) : Promise.reject(fail(404));
    return ApiClient.ajax({ type: type, url: 'api/' + String(url).replace(/^\.\//, ''), dataType: 'json', data: init && init.body }).then(function (v) {
      return new Response(JSON.stringify(v === undefined ? null : v), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }, function (e) { return new Response('{}', { status: e.status || 500 }); });
  };

  window.Dashboard = {
    navigate: function (to) { note('In Jellyfin this opens ' + to + '.'); return Promise.resolve(); },
    alert: function (o) { note(typeof o === 'string' ? o : (o && (o.message || o.text)) || ''); },
    showLoadingMsg: function () {}, hideLoadingMsg: function () {}
  };

  /* ---------- the mock API that demo/mocks/*.js fill ---------- */
  window.MkiDemo = {
    adminId: '6a1f0c2e-9b3d-4f5a-8c7e-1d2b3a4f5e6d',
    plugin: function (id, cfg) { configs[id] = clone(cfg); },
    config: function (id) { return configs[id]; },
    route: function (type, re, fn) { routes.push({ type: type, re: re, fn: fn }); },
    serve: function (path, file) { statics[path] = file; },
    fail: fail,
    wait: wait,
    ago: function (ms) { return new Date(Date.now() - ms).toISOString(); },
    onConfig: null,
    image: null
  };

  /* ---------- theme: Jellyfin's dark theme by default, light on request ---------- */
  var root = document.documentElement;
  function stored(v) {
    try { if (v === undefined) return localStorage.getItem('mki-demo-theme'); localStorage.setItem('mki-demo-theme', v); } catch (e) { return null; }
    return null;
  }
  var pick = (/[?&]theme=(dark|light)/.exec(location.search) || [])[1] || stored() || 'dark';
  root.setAttribute('data-theme', pick === 'light' ? 'light' : 'dark');

  function note(text) {
    var box = document.getElementById('demoNote');
    if (!box || !text) return;
    box.querySelector('span').textContent = text;
    box.hidden = false;
    clearTimeout(note.t);
    note.t = setTimeout(function () { box.hidden = true; }, 4200);
  }

  document.addEventListener('DOMContentLoaded', function () {
    var toggle = document.getElementById('demoTheme');
    function label() {
      var light = root.getAttribute('data-theme') === 'light';
      toggle.querySelector('.material-icons').textContent = light ? 'dark_mode' : 'light_mode';
      toggle.setAttribute('aria-label', light ? 'Switch to the dark theme' : 'Switch to the light theme');
      toggle.title = toggle.getAttribute('aria-label');
    }
    if (toggle) {
      label();
      toggle.addEventListener('click', function () {
        var next = root.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
        root.setAttribute('data-theme', next); stored(next); label();
      });
    }

    var menu = document.getElementById('demoMenu'), drawer = document.getElementById('demoDrawer');
    if (menu && drawer) {
      menu.addEventListener('click', function () {
        var open = !document.body.classList.contains('demo-drawer-open');
        document.body.classList.toggle('demo-drawer-open', open);
        menu.setAttribute('aria-expanded', String(open));
      });
      drawer.addEventListener('click', function (e) { if (e.target === drawer) { document.body.classList.remove('demo-drawer-open'); menu.setAttribute('aria-expanded', 'false'); } });
    }

    /* Links into the rest of Jellyfin's dashboard have nowhere to go here. */
    document.addEventListener('click', function (e) {
      var a = e.target && e.target.closest && e.target.closest('a[href^="#/"]');
      if (!a) return;
      e.preventDefault();
      var where = a.getAttribute('href').replace(/^#\//, '').split('?')[0];
      note('In Jellyfin this link opens ' + (/tasks/.test(where) ? 'the scheduled task' : /users/.test(where) ? 'the user\'s settings' : 'another dashboard page') + '. It is not part of the demo.');
    });

    /* Jellyfin shows the page once its scripts ran. */
    var page = document.querySelector('[data-role="page"]');
    if (page) page.dispatchEvent(new CustomEvent('pageshow', { bubbles: true }));
  });
})();
