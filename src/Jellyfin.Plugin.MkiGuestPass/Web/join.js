/* MKI Guest Pass: the page a guest opens from a pass link, a QR code or to type a code.
   The link's secret is the part after #, which the browser never sends to any server. It is read once, taken out of
   the address bar and kept only in this tab until the guest has joined. The guest's Jellyfin sign-in is written
   where the Jellyfin web client keeps its own, so the web client opens signed in. A rejoin key stays in this
   browser so the guest can come back without using another place on the pass. Server text only ever reaches the
   page through textContent. ES5 on purpose: guests open this on any phone. */
(function () {
    'use strict';

    var STORE = 'mkiGuestPass';
    var TAB = 'mkiGuestPass:link';
    var SHARED = 'mkiGuestPass:shared';
    var EN = 'en-GB';
    var view = document.getElementById('view');
    var path = location.pathname;
    var cut = path.toLowerCase().lastIndexOf('/mkiguestpass/');
    var base = cut >= 0 ? path.substring(0, cut) : '';
    var serverAddress = location.protocol + '//' + location.host + base;
    var token = null, code = null, peek = null, me = null;
    /* the kept guest whose Continue was just refused: Try again asks for them again */
    var again = null;

    /* ---------- small helpers ---------- */
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
    var ICONS = {
        clock: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 18a8 8 0 1 1 0-16 8 8 0 0 1 0 16Zm.5-13H11v6l5.2 3.2.8-1.3-4.5-2.7V7Z',
        film: 'M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V4h-4Z',
        shield: 'M12 1 3 5v6c0 5.5 3.8 10.7 9 12 5.2-1.3 9-6.5 9-12V5l-9-4Zm-1 16-4-4 1.4-1.4 2.6 2.6 5.6-5.6L18 10l-7 7Z',
        home: 'M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8h5Z',
        people: 'M16 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm-8 0a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm0 2c-2.3 0-7 1.2-7 3.5V19h14v-2.5C15 14.2 10.3 13 8 13Zm8 0c-.3 0-.6 0-1 .1 1.2.8 2 2 2 3.4V19h6v-2.5c0-2.3-4.7-3.5-7-3.5Z',
        tv: 'M21 3H3a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h5v2h8v-2h5a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2Zm0 14H3V5h18v12Z',
        alert: 'M1 21h22L12 2 1 21Zm12-3h-2v-2h2v2Zm0-4h-2v-4h2v4Z',
        error: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm1 15h-2v-2h2v2Zm0-4h-2V7h2v6Z',
        ok: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm-2 15-5-5 1.4-1.4 3.6 3.6 7.6-7.6L19 8l-9 9Z',
        ticket: 'M22 10V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v4a2 2 0 0 1 0 4v4a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-4a2 2 0 0 1 0-4Zm-9 7.5h-2v-2h2v2Zm0-4.5h-2v-2h2v2Zm0-4.5h-2v-2h2v2Z',
        spin: 'M12 4V1L8 5l4 4V6a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8Z'
    };
    function icon(name, size, cls) {
        var ns = 'http://www.w3.org/2000/svg', s = document.createElementNS(ns, 'svg'), p = document.createElementNS(ns, 'path');
        s.setAttribute('width', String(size || 20)); s.setAttribute('height', String(size || 20)); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true');
        if (cls) s.setAttribute('class', cls);
        p.setAttribute('fill', 'currentColor'); p.setAttribute('d', ICONS[name]); s.appendChild(p);
        return s;
    }
    function clear() { while (view.firstChild) view.removeChild(view.firstChild); }
    function big(name, ok) { var b = el('div', 'big' + (ok ? ' ok' : '')); b.appendChild(icon(name, 28)); return b; }
    /* "today 16:00", "tomorrow 16:00", else "Fri, 23 Oct, 16:00", in this device's time zone */
    function when(iso) {
        var d = new Date(iso), now = new Date(), t = d.toLocaleTimeString(EN, { hour: '2-digit', minute: '2-digit' });
        var tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
        if (d.toDateString() === now.toDateString()) return 'today ' + t;
        if (d.toDateString() === tomorrow.toDateString()) return 'tomorrow ' + t;
        return d.toLocaleDateString(EN, { weekday: 'short', day: 'numeric', month: 'short' }) + ', ' + t;
    }
    function cap(t) { return t.charAt(0).toUpperCase() + t.slice(1); }
    function left(iso) {
        var ms = new Date(iso).getTime() - Date.now();
        if (ms <= 0) return 'ended';
        var m = Math.round(ms / 60000);
        if (m < 60) return 'in ' + m + (m === 1 ? ' minute' : ' minutes');
        var h = Math.round(m / 60);
        if (h < 48) return 'in ' + h + (h === 1 ? ' hour' : ' hours');
        return 'in ' + Math.round(h / 24) + ' days';
    }
    /* session: this window only (a shared computer), otherwise this browser */
    function store(session) { try { return session ? window.sessionStorage : window.localStorage; } catch (e) { return null; } }
    function read(key, session) { try { return JSON.parse(store(session).getItem(key) || 'null'); } catch (e) { return null; } }
    function write(key, value, session) { try { store(session).setItem(key, JSON.stringify(value)); return true; } catch (e) { return false; } }
    function tab(value) { try { if (value === undefined) return sessionStorage.getItem(TAB); if (value === null) sessionStorage.removeItem(TAB); else sessionStorage.setItem(TAB, value); } catch (e) { return null; } return null; }

    function post(name, body) {
        return new Promise(function (resolve, reject) {
            var x = new XMLHttpRequest();
            x.open('POST', name, true);
            x.setRequestHeader('Content-Type', 'application/json');
            x.timeout = 20000;
            x.onload = function () {
                var data = null;
                try { data = JSON.parse(x.responseText); } catch (e) { data = null; }
                if (x.status >= 200 && x.status < 300 && data) resolve(data);
                else reject({ status: x.status, reason: data && data.Reason ? String(data.Reason) : null, message: data && data.Message ? String(data.Message) : 'The server did not answer as expected. Please try again.' });
            };
            x.onerror = x.ontimeout = function () { reject({ status: 0, message: 'The server could not be reached. Check your connection and try again.' }); };
            x.send(JSON.stringify(body));
        });
    }

    /* ---------- this browser's identity, the way the Jellyfin web client keeps it ---------- */
    function deviceId() {
        var id = null;
        try { id = localStorage.getItem('_deviceId2'); } catch (e) { id = null; }
        if (!id) {
            try { id = btoa(navigator.userAgent + '|' + new Date().getTime()).replace(/=/g, '1'); } catch (e) { id = 'mkigp' + new Date().getTime() + Math.floor(Math.random() * 1e9); }
            try { localStorage.setItem('_deviceId2', id); } catch (e) { }
        }
        return id;
    }
    function deviceName() {
        var ua = navigator.userAgent, n = 'Web Browser';
        if (/Edg\//.test(ua)) n = 'Edge';
        else if (/OPR\//.test(ua)) n = 'Opera';
        else if (/Firefox\//.test(ua)) n = 'Firefox';
        else if (/Chrome\//.test(ua)) n = 'Chrome';
        else if (/Safari\//.test(ua)) n = 'Safari';
        if (/iPad/.test(ua)) n += ' iPad'; else if (/iPhone/.test(ua)) n += ' iPhone'; else if (/Android/.test(ua)) n += ' Android';
        return n;
    }

    /* ---------- guests who joined in this browser before (only their rejoin key, name and end) ----------
       Kept in the browser, or on a shared computer only in this window (session), gone once it closes. */
    /* A pass that ended can be opened again until its accounts are deleted (up to 30 days), so a key stays that long;
       it is only offered while its pass is known to run, or when the link of that pass shows it running again. */
    function keptIn(session) {
        var list = read(STORE, session), now = Date.now();
        if (!list || !list.length) return [];
        return list.filter(function (g) { return g && g.key && g.name && new Date(g.end).getTime() > now - 30 * 864e5; })
            .map(function (g) { g.session = session; return g; }).slice(0, 5);
    }
    function plain(g) { return { key: g.key, name: g.name, pass: g.pass, title: g.title, end: g.end, from: g.from || undefined, ended: g.ended || undefined }; }
    function all() { return keptIn(true).concat(keptIn(false)); }
    function kept() { var now = Date.now(); return all().filter(function (g) { return !g.ended && new Date(g.end).getTime() > now; }).slice(0, 5); }
    function keep(g, session) {
        forget([g.key]);
        var list = keptIn(session);
        list.unshift(g);
        write(STORE, list.slice(0, 5).map(plain), session);
    }
    function forget(keys) { change(function (x) { return keys.indexOf(x.key) >= 0 ? null : x; }); }
    /* fn(entry) gives the entry to keep, changed or not, or null to drop it */
    function change(fn) {
        [true, false].forEach(function (session) {
            var list = keptIn(session), out = [], touched = false;
            list.forEach(function (x) { var y = fn(x); if (y !== x) touched = true; if (y) out.push(y); });
            if (touched) write(STORE, out.map(plain), session);
        });
    }
    /* what the server said about a pass: its end, and whether it runs */
    function passNews(passId, end, ended) {
        change(function (x) { if (x.pass !== passId) return x; var y = plain(x); y.end = end || x.end; y.ended = ended || undefined; return y; });
    }

    /* ---------- the hand-off to the web client ---------- */
    function signedIn(r) {
        var creds = read('jellyfin_credentials') || {};
        var servers = (creds.Servers || []).filter(function (s) {
            return s && s.Id !== r.ServerId && String(s.ManualAddress || '').toLowerCase() !== serverAddress.toLowerCase();
        });
        servers.unshift({
            ManualAddress: serverAddress,
            manualAddressOnly: true,
            LastConnectionMode: 2,
            Id: r.ServerId,
            Name: r.ServerName || 'Jellyfin',
            DateLastAccessed: new Date().getTime(),
            AccessToken: r.AccessToken,
            UserId: r.UserId
        });
        creds.Servers = servers;
        var ok = write('jellyfin_credentials', creds);
        /* the web client signs in from the stored credentials only with this on; on a shared computer client.js turns
           it off again once signed in, so Jellyfin asks for a sign-in after this window closes */
        try { localStorage.setItem('enableAutoLogin', 'true'); } catch (e) { ok = false; }
        try { if (r.shared) sessionStorage.setItem(SHARED, r.UserId); else sessionStorage.removeItem(SHARED); } catch (e) { ok = ok && !r.shared; }
        if (r.RejoinKey) keep({ key: r.RejoinKey, name: r.Name, pass: r.PassId, title: r.Pass, end: r.EndUtc, from: r.From }, !!r.shared);
        if (r.PassId) passNews(r.PassId, r.EndUtc, false);
        tab(null);
        if (!ok) { problem('Your browser does not allow this page to store your sign-in (private mode or blocked storage). Allow it for this site, or open the link in another browser.', null); return; }
        clear();
        view.appendChild(big('ok', true));
        view.appendChild(heading('You’re in, ' + r.Name + '!'));
        view.appendChild(el('p', 'lede', 'Opening Jellyfin, signed in as Guest ' + r.Name + '…'));
        var go = el('a', 'link', 'Not opening? Open Jellyfin'); go.href = base + '/web/index.html';
        view.appendChild(go);
        focusHeading();
        setTimeout(function () { location.replace(base + '/web/index.html'); }, 600);
    }

    /* ---------- the person already signed in to Jellyfin in this browser, if any ---------- */
    function whoIsHere() {
        var creds = read('jellyfin_credentials');
        var s = creds && creds.Servers && creds.Servers.filter(function (x) { return x && x.AccessToken && String(x.ManualAddress || '').toLowerCase() === serverAddress.toLowerCase(); })[0];
        if (!s) return Promise.resolve(null);
        return new Promise(function (resolve) {
            var x = new XMLHttpRequest();
            x.open('GET', base + '/Users/Me', true);
            x.setRequestHeader('Authorization', 'MediaBrowser Token="' + String(s.AccessToken).replace(/"/g, '') + '"');
            x.timeout = 8000;
            x.onload = function () { var d = null; try { d = JSON.parse(x.responseText); } catch (e) { d = null; } resolve(x.status === 200 && d && d.Name ? String(d.Name) : null); };
            x.onerror = x.ontimeout = function () { resolve(null); };
            x.send();
        });
    }

    /* ---------- screens ---------- */
    function busy(b, on, text) {
        b.disabled = on;
        while (b.firstChild) b.removeChild(b.firstChild);
        if (on) b.appendChild(icon('spin', 20, 'spin'));
        b.appendChild(el('span', null, text));
    }
    function error(text) {
        var m = el('p', 'msg'); m.setAttribute('role', 'alert');
        m.appendChild(icon('error', 18)); m.appendChild(el('span', null, text));
        return m;
    }
    function half(li) { li.className = 'half'; return li; }
    function fact(name, label, value, sub) {
        var li = el('li');
        li.appendChild(icon(name, 20));
        var t = el('div');
        t.appendChild(el('small', null, label)); t.appendChild(el('span', null, value));
        if (sub) t.appendChild(el('em', null, sub));
        li.appendChild(t);
        return li;
    }
    /* in the order a TV app asks for things: the address first, then Quick Connect */
    function tvHint() {
        var box = el('div', 'tv'), h = el('p', 'h');
        h.appendChild(icon('tv', 18)); h.appendChild(el('span', null, 'Watching on a TV?'));
        box.appendChild(h);
        var ol = el('ol');
        ol.appendChild(el('li', null, 'Join here first.'));
        var a = el('li'); a.appendChild(document.createTextNode('In the Jellyfin app on the TV, enter the server address ')); a.appendChild(el('code', null, serverAddress)); a.appendChild(document.createTextNode('.'));
        ol.appendChild(a);
        ol.appendChild(el('li', null, 'Choose Quick Connect. The TV shows a code.'));
        ol.appendChild(el('li', null, 'On this device, tap the Guest badge at the bottom of the Jellyfin page, choose Sign in a TV and type that code.'));
        box.appendChild(ol);
        return box;
    }
    /* the screen's heading takes the focus, so a screen reader starts reading the new screen */
    function heading(text) { var h = el('h1', null, text); h.tabIndex = -1; return h; }
    function focusHeading() { var h = view.querySelector('h1'); if (h) { try { h.focus({ preventScroll: true }); } catch (e) { h.focus(); } } }

    /* reason: "ended", "removed" and "full" are a normal end, not a failure, and get their own words */
    var PROBLEMS = { ended: ['clock', 'This guest pass has ended'], removed: ['clock', 'Your guest access has ended'], full: ['people', 'This pass is full'], paused: ['clock', 'Guest access is paused'], home: ['home', 'Only on the home network'], update: ['alert', 'Guests cannot join right now'] };
    function host() { return peek && peek.From ? peek.From : again && again.from ? again.from : 'the person who invited you'; }
    /* what the guest can do next, so nobody is left on a page without a way on */
    function nextStep(reason, status) {
        if (reason === 'ended') return 'Ask ' + host() + ' to open it again: this link then works again.';
        if (reason === 'removed') return 'If you still need access, ask ' + host() + ' for a new invitation.';
        if (reason === 'full') return 'Ask ' + host() + ' for a new pass with a free place.';
        if (reason === 'paused') return 'Jellyfin is starting up, or guest access was paused on the server. Try again in a minute.';
        if (reason === 'update') return 'Let ' + host() + ' know: the Guest Pass settings in the Jellyfin dashboard say what to update. Afterwards tap Try again; the link stays the same.';
        if (status === 404) return token ? 'Check that you opened the whole link from your message. If it still does not work, ask ' + host() + ' for a new one.' : 'Ask ' + host() + ' for a new link or code.';
        return null;
    }
    /* worth another go without a new link: the network, a pause, a wait, the home network */
    function retryable(reason, status) { return reason === 'paused' || reason === 'home' || reason === 'update' || (reason === 'ended' && !!(token || code || again)) || (!reason && (status === 0 || status === 429 || status >= 500)); }
    /* asks the server again for the link or code in use; without one, back to the guests kept here or code entry */
    function tryAgain(b) {
        if (b) { if (b.disabled) return; busy(b, true, 'Checking…'); }
        if (!token && !code) {
            if (again) { rejoin(again).then(null, function (e) { if (!retryable(e.reason, e.status)) again = null; problem(e.message, null, e.reason || 'ended', e.status); }); return; }
            if (kept().length) backScreen(null); else codeScreen();
            return;
        }
        post('Peek', token ? { Token: token } : { Code: code }).then(opened, function (e) {
            if (e.status === 404 && token) tab(null);
            problem(e.message, null, e.reason, e.status);
        });
    }
    function retryButton() { var b = el('button', 'btn'); b.type = 'button'; busy(b, false, 'Try again'); b.addEventListener('click', function () { tryAgain(b); }); return b; }
    /* a pass for the home network only, opened from elsewhere: the two ways in */
    function homeHelp(titled) {
        var box = el('div', 'next'), h = el('p', 'h'), who = peek && peek.From ? peek.From : again && again.from, whose = who ? who + '’s' : 'your host’s';
        h.appendChild(icon('home', 18)); h.appendChild(el('span', null, 'This pass works on the home network only'));
        if (titled) box.appendChild(h);
        box.appendChild(el('p', null, 'Jellyfin does not see this device on ' + whose + ' home network right now.'));
        var ul = el('ul'), a = el('li'), b = el('li');
        a.appendChild(el('b', null, 'At ' + whose + ' home? ')); a.appendChild(document.createTextNode('Connect this device to the Wi-Fi there, not mobile data, then tap Try again.'));
        b.appendChild(el('b', null, 'Somewhere else? ')); b.appendChild(document.createTextNode('Ask ' + host() + ' to allow this pass from anywhere, then tap Try again. The link stays the same.'));
        ul.appendChild(a); ul.appendChild(b); box.appendChild(ul);
        box.appendChild(retryButton());
        return box;
    }
    function problem(text, after, reason, status) {
        var kind = PROBLEMS[reason] || (status === 404 && token ? ['alert', 'This link does not work'] : status === 404 && code ? ['alert', 'This code does not work'] : ['alert', 'This did not work']);
        /* a full pass stays full: its link is no use in this tab any more, code entry is. An ended one can be opened
           again, so its link stays for Try again and a reload */
        if (reason === 'full') tab(null);
        /* codes are refused just the same while a plugin needs an update */
        if (reason === 'update') after = 'nocode';
        foot(false);
        clear();
        view.appendChild(big(kind[0]));
        view.appendChild(heading(kind[1]));
        /* the home network box says it all, in the guest's words */
        if (reason !== 'home') view.appendChild(el('p', 'lede', text));
        var next = nextStep(reason, status);
        if (reason === 'home') view.appendChild(homeHelp(false));
        else {
            if (next) view.appendChild(el('p', 'lede', next));
            if (retryable(reason, status)) view.appendChild(retryButton());
        }
        var links = el('div', 'links');
        if (after !== 'nocode') {
            var c = el('button', 'link', 'Enter a guest code'); c.type = 'button';
            c.addEventListener('click', function () { token = null; tab(null); codeScreen(); });
            links.appendChild(c);
        }
        /* guests of an ended pass cannot continue; of a full one they still can */
        var others = kept().filter(function (g) { return !(reason === 'ended' && peek && g.pass === peek.Id) && !(again && g.key === again.key); });
        if (others.length) {
            var k = el('button', 'link', 'Joined before? Continue'); k.type = 'button';
            k.addEventListener('click', function () { backScreen(null, reason === 'ended' && peek ? peek.Id : null); });
            links.appendChild(k);
        }
        if (links.firstChild) view.appendChild(links);
        focusHeading();
    }

    function foot(on) { var f = document.getElementById('foot'); if (f) f.hidden = !on; }
    function codeScreen(message) {
        foot(true);
        clear();
        view.appendChild(big('ticket'));
        view.appendChild(el('p', 'kicker', 'Guest pass'));
        view.appendChild(heading('Enter your guest code'));
        view.appendChild(el('p', 'lede', 'The 8 characters you were given, for example K7QM-4XPA.'));
        var f = el('div', 'field'), lab = el('label', null, 'Guest code'), inp = el('input', 'code');
        lab.htmlFor = 'gpCode'; inp.id = 'gpCode'; inp.type = 'text'; inp.autocomplete = 'off'; inp.maxLength = 12;
        inp.setAttribute('autocapitalize', 'characters'); inp.setAttribute('spellcheck', 'false'); inp.setAttribute('inputmode', 'text'); inp.placeholder = 'XXXX-XXXX';
        f.appendChild(lab); f.appendChild(inp); view.appendChild(f);
        var msg = el('div'); view.appendChild(msg);
        if (message) msg.appendChild(error(message));
        var b = el('button', 'btn'); b.type = 'button'; busy(b, false, 'Continue'); view.appendChild(b);
        view.appendChild(el('p', 'hint', 'Got a link or QR code instead? Open it again from your message: no code needed then.'));
        if (kept().length) {
            var k = el('button', 'link', 'Joined before? Continue as ' + kept()[0].name); k.type = 'button';
            k.addEventListener('click', function () { backScreen(); });
            view.appendChild(k);
        }
        function go() {
            /* one check at a time: a repeated Enter must not count as another wrong code */
            if (b.disabled) return;
            var v = inp.value.replace(/[\s.-]/g, '').toUpperCase();
            if (v.length !== 8) { inp.setAttribute('aria-invalid', 'true'); msg.textContent = ''; msg.appendChild(error('A guest code has 8 characters.')); inp.focus(); return; }
            inp.removeAttribute('aria-invalid');
            busy(b, true, 'Checking…'); inp.readOnly = true;
            post('Peek', { Code: v }).then(function (p) { code = v; token = null; opened(p); }, function (e) {
                inp.readOnly = false;
                busy(b, false, 'Continue'); msg.textContent = ''; msg.appendChild(error(e.message)); inp.setAttribute('aria-invalid', 'true'); inp.focus();
            });
        }
        b.addEventListener('click', go);
        inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') go(); });
        inp.addEventListener('input', function () { inp.removeAttribute('aria-invalid'); });
        inp.focus();
    }

    function inviteScreen() {
        var p = peek;
        foot(true);
        clear();
        view.appendChild(el('p', 'kicker', 'Guest pass'));
        view.appendChild(heading(p.From ? p.From + ' invited you' : 'You’re invited'));
        var blocked = p.HomeOnly && !p.CanJoinHere;
        view.appendChild(el('p', 'lede', '“' + p.Name + '”' + (p.ServerName ? ' on ' + p.ServerName : '') + '.' +
            (blocked || p.Ended || p.PlacesLeft <= 0 ? '' : ' Type your first name below and Jellyfin opens right here, already signed in. No password, nothing to install.')));
        if (p.Ended) { problem('“' + p.Name + '” ended ' + when(p.EndUtc) + '.', null, 'ended'); return; }
        if (p.PlacesLeft <= 0) { problem(p.MaxGuests === 1 ? 'This guest pass has already been used.' : 'All ' + p.MaxGuests + ' places on this guest pass are taken.', null, 'full'); return; }
        if (p.NeedsUpdate) { problem('A plugin on this server needs an update before guests can join.', null, 'update'); return; }
        /* what to do comes first, in view on a phone; the facts below say what the pass is */
        if (blocked) view.appendChild(homeHelp(true));
        var facts = el('ul', 'facts');
        facts.appendChild(fact('clock', 'Access until', cap(when(p.EndUtc)), left(p.EndUtc) === 'ended' ? null : 'Ends ' + left(p.EndUtc) + ', on its own'));
        facts.appendChild(fact('film', 'Libraries', (p.Libraries || []).join(', ') || 'None'));
        if (p.Rating) facts.appendChild(fact('shield', 'Rating', 'Up to ' + p.Rating, 'Titles without a rating are hidden'));
        /* the smaller facts side by side, so "Start watching" stays in view on a phone */
        if (p.HomeOnly) facts.appendChild(half(fact('home', 'Where', 'Home network only', p.CanJoinHere ? 'You are on it now' : null)));
        if (p.DevicesPerGuest) facts.appendChild(half(fact('tv', 'Devices', p.DevicesPerGuest === 1 ? 'One at a time' : p.DevicesPerGuest + ' at once', 'A TV counts as one')));
        if (p.MaxGuests > 1) facts.appendChild(half(fact('people', 'Places', p.PlacesLeft + ' of ' + p.MaxGuests + ' left')));
        /* an odd one out gets the whole row */
        var halves = facts.querySelectorAll('li.half');
        if (halves.length % 2) halves[halves.length - 1].className = '';
        view.appendChild(facts);
        if (blocked) {
            focusHeading();
            return;
        }
        if (me) {
            var n = el('p', 'note'); n.appendChild(icon('alert', 18));
            n.appendChild(el('span', null, 'This browser is signed in to Jellyfin as ' + me + '. Joining signs ' + me + ' out here, on this browser only.'));
            view.appendChild(n);
        }
        var f = el('div', 'field'), lab = el('label', null, 'Your first name'), inp = el('input');
        lab.htmlFor = 'gpName'; inp.id = 'gpName'; inp.type = 'text'; inp.maxLength = 20; inp.autocomplete = 'given-name'; inp.setAttribute('autocapitalize', 'words'); inp.setAttribute('enterkeyhint', 'go');
        var hint = el('p', 'hint', 'Shown as “Guest …” on this server. Letters, numbers, spaces, - ’ and . only.'); hint.id = 'gpNameHint'; inp.setAttribute('aria-describedby', 'gpNameHint');
        f.appendChild(lab); f.appendChild(inp); f.appendChild(hint); view.appendChild(f);
        /* a shared or public computer: Jellyfin forgets the guest once this window closes, and so does this page */
        var sh = el('label', 'check'), shc = el('input');
        shc.type = 'checkbox'; shc.id = 'gpShared'; shc.setAttribute('aria-describedby', 'gpSharedHint');
        sh.appendChild(shc); sh.appendChild(el('span', null, 'Shared or public computer'));
        view.appendChild(sh);
        var shh = el('p', 'hint check-hint', 'Jellyfin does not keep you signed in here: a reload asks you to continue, and once this window is closed the next person starts at the sign-in page.'); shh.id = 'gpSharedHint';
        view.appendChild(shh);
        var msg = el('div'); view.appendChild(msg);
        var b = el('button', 'btn'); b.type = 'button'; busy(b, false, 'Start watching'); view.appendChild(b);
        view.appendChild(tvHint());
        function go() {
            if (b.disabled) return;
            var name = inp.value.replace(/\s+/g, ' ').trim();
            if (!name) { inp.setAttribute('aria-invalid', 'true'); msg.textContent = ''; msg.appendChild(error('Type your first name.')); inp.focus(); return; }
            inp.removeAttribute('aria-invalid');
            busy(b, true, 'Creating your guest account…'); inp.disabled = true; shc.disabled = true;
            var body = { Name: name, DeviceId: deviceId(), DeviceName: deviceName() };
            if (token) body.Token = token; else body.Code = code;
            post('Redeem', body).then(function (r) { r.shared = shc.checked; signedIn(r); }, function (e) {
                inp.disabled = false; shc.disabled = false; busy(b, false, 'Start watching');
                if (e.reason === 'ended' || e.reason === 'full' || e.reason === 'home' || e.reason === 'update' || e.status === 410) { problem(e.message, null, e.reason || 'ended', e.status); return; }
                msg.textContent = ''; msg.appendChild(error(e.message));
                if (e.status === 400) { inp.setAttribute('aria-invalid', 'true'); inp.focus(); }
            });
        }
        b.addEventListener('click', go);
        inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') go(); });
        inp.addEventListener('input', function () { inp.removeAttribute('aria-invalid'); });
        inp.focus();
    }

    /* signs a kept guest in again. A pass that ended can be opened again: its key stays, only not offered until then */
    function rejoin(g) {
        return post('Rejoin', { RejoinKey: g.key, DeviceId: deviceId(), DeviceName: deviceName() }).then(function (r) {
            again = null; r.RejoinKey = g.key; r.shared = g.session; signedIn(r);
        }, function (e) {
            if (e.status === 410 && e.reason === 'ended') passNews(g.pass, null, true);
            else if (e.status === 404 || e.status === 410 && e.reason !== 'paused') { forget([g.key]); again = null; }
            throw e;
        });
    }

    /* passId: only the guests of that pass (the link or code just used), with a way to join as someone new.
       without: every guest kept here except those of the pass "without". */
    function backScreen(passId, without) {
        var list = kept().filter(function (g) { return (!passId || g.pass === passId) && (!without || g.pass !== without); });
        if (!list.length) { codeScreen(); return; }
        clear();
        view.appendChild(el('p', 'kicker', 'Guest pass'));
        view.appendChild(heading(list.length === 1 ? 'Welcome back, ' + list[0].name : 'Welcome back'));
        view.appendChild(el('p', 'lede', list.length === 1 ? '“' + (list[0].title || 'Guest pass') + '”, until ' + when(list[0].end) + '.' : 'Who is watching?'));
        var msg = el('div');
        var who = el('div', 'who'), buttons = [];
        list.forEach(function (g, i) {
            var b = el('button', 'btn' + (i ? ' btn--plain' : '')); b.type = 'button';
            var label = list.length === 1 ? 'Continue watching' : 'Continue as ' + g.name;
            busy(b, false, label);
            b.addEventListener('click', function () {
                /* one sign-in at a time, whichever button */
                if (buttons.some(function (x) { return x.disabled; })) return;
                buttons.forEach(function (x) { x.disabled = true; });
                busy(b, true, 'Signing you in…');
                rejoin(g).then(null, function (e) {
                    buttons.forEach(function (x) { x.disabled = false; });
                    busy(b, false, label);
                    if (e.reason === 'update' || (e.status === 410 || e.reason === 'home') && list.length === 1) {
                        again = e.reason === 'ended' || retryable(e.reason, 0) ? g : null;
                        problem(e.message, null, e.reason || 'ended', e.status);
                        return;
                    }
                    msg.textContent = ''; msg.appendChild(error(e.message));
                });
            });
            buttons.push(b);
            who.appendChild(b);
        });
        view.appendChild(who);
        view.appendChild(msg);
        var links = el('div', 'links');
        if (passId && peek && peek.PlacesLeft > 0) {
            var n = el('button', 'link', 'Someone else? Join as a new guest'); n.type = 'button';
            n.addEventListener('click', function () { inviteScreen(); });
            links.appendChild(n);
        } else if (!passId) {
            var other = el('button', 'link', 'Use a guest code instead'); other.type = 'button';
            other.addEventListener('click', function () { token = null; tab(null); codeScreen(); });
            links.appendChild(other);
        }
        var f = el('button', 'link', list.length === 1 ? 'Forget ' + list[0].name + ' on this browser' : 'Forget these on this browser'); f.type = 'button';
        f.addEventListener('click', function () {
            forget(list.map(function (g) { return g.key; }));
            if (passId && peek) inviteScreen(); else codeScreen();
        });
        links.appendChild(f);
        view.appendChild(links);
        focusHeading();
    }

    /* A pass this browser joined before: continue instead of using another place. */
    function opened(p) {
        peek = p; again = null;
        document.getElementById('server').textContent = p.ServerName && p.ServerName.trim().toLowerCase() !== 'jellyfin' ? ' · ' + p.ServerName : '';
        passNews(p.Id, p.EndUtc, !!p.Ended);
        var mine = kept().filter(function (g) { return g.pass === p.Id; });
        if (mine.length && !p.Ended) backScreen(p.Id); else inviteScreen();
    }

    /* ---------- start ---------- */
    /* the logo comes from this Jellyfin server itself (its web client's app icon); without it the name stays */
    var logo = document.getElementById('jfLogo');
    if (logo) {
        if (logo.complete && !logo.naturalWidth) logo.hidden = true;
        logo.addEventListener('error', function () { logo.hidden = true; });
    }
    var hash = (location.hash || '').replace(/^#/, '');
    if (hash) {
        if (/^[A-Za-z0-9_-]{43}$/.test(hash)) tab(hash);
        try { history.replaceState(null, '', location.pathname + location.search); } catch (e) { }
    }
    token = tab();
    /* another guest link opened while this page shows: only the hash changes, so start again with it */
    window.addEventListener('hashchange', function () { location.reload(); });
    whoIsHere().then(function (name) {
        me = name;
        if (token) {
            tryAgain(null);
        } else if (hash) {
            problem('This guest link is incomplete. Open it again from the message you got, or enter the code.', null);
        } else if (kept().length) {
            backScreen();
        } else {
            codeScreen();
        }
    });
})();
