/* MKI Guest Pass for the Jellyfin web client. Only loaded while guests exist or a pass with a code is open.
   - On the sign-in page: "Have a guest code?", leading to the join page (only while a pass with a code is open), and
     for a guest who was just signed out in this window, that their pass ended, or a way back in and a way to make
     this browser forget them.
   - For a signed-in guest: a small badge with the end of the guest access. It opens a panel with "Sign in a TV"
     (Jellyfin's Quick Connect) and the server address for TV apps.
   - A guest who joined as on a shared computer: Jellyfin's "remember me" is turned off once they are signed in, so
     Jellyfin asks for a sign-in after this window closes.
   The script holds no data: it asks the server whether the signed-in user is a guest. Server text only ever reaches
   the page through textContent. */
(function () {
    'use strict';
    if (window.MkiGuestPass) {
        return;
    }
    window.MkiGuestPass = true;

    var script = document.currentScript || document.querySelector('script[data-mki-guest-pass-injected]');
    var CODE_LINK = !!script && script.getAttribute('data-code-link') === '1';
    var BASE = script && script.src ? script.src.replace(/\/MkiGuestPass\/client\.js.*$/i, '') : '';
    var EN = 'en-GB';
    var KEYS = 'mkiGuestPass';
    var SEEN = 'mkiGuestPass:seen';
    var SHARED = 'mkiGuestPass:shared';
    var me = null, meFor = '', asked = 0, asking = false, badge = null, panel = null, timer = 0;

    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
    function seen(v) { try { if (v === undefined) return JSON.parse(sessionStorage.getItem(SEEN) || 'null'); if (v === null) sessionStorage.removeItem(SEEN); else sessionStorage.setItem(SEEN, JSON.stringify(v)); } catch (e) { return null; } return null; }
    function store(session) { try { return session ? window.sessionStorage : window.localStorage; } catch (e) { return null; } }
    /* the join page's way back in: rejoin keys kept in this browser, or in this window only on a shared computer */
    function keys() {
        var out = [];
        [true, false].forEach(function (session) {
            try { (JSON.parse(store(session).getItem(KEYS) || '[]') || []).forEach(function (g) { if (g && g.key && g.name && !g.ended && new Date(g.end).getTime() > Date.now()) out.push(g); }); } catch (e) { }
        });
        return out;
    }
    function forgetKeys() {
        [true, false].forEach(function (session) { try { store(session).removeItem(KEYS); } catch (e) { } });
    }
    /* access ended: the keys stay, but are not offered until the join page sees the pass open again */
    function endKeys(passId) {
        [true, false].forEach(function (session) {
            try {
                var s = store(session), list = JSON.parse(s.getItem(KEYS) || '[]') || [];
                list.forEach(function (g) { if (g && (!passId || g.pass === passId)) g.ended = true; });
                if (list.length) s.setItem(KEYS, JSON.stringify(list));
            } catch (e) { }
        });
    }

    function addCss() {
        if (document.getElementById('mkigp-css')) return;
        var s = el('style'); s.id = 'mkigp-css';
        s.textContent = [
            '.mkigp-code{margin:1.2em 0 0;text-align:center}',
            '.mkigp-code a{display:inline-flex;align-items:center;gap:.5em;padding:.7em 1.1em;border-radius:999px;border:1px solid rgba(61,200,255,.45);color:inherit;text-decoration:none;font-weight:600;background:rgba(61,200,255,.08)}',
            '.mkigp-code a:hover{background:rgba(61,200,255,.16)}',
            '.mkigp-code a:focus-visible{background:rgba(61,200,255,.16);outline:3px solid #3DC8FF;outline-offset:3px}',
            '.mkigp-code a::before{content:"";width:.65em;height:.65em;border-radius:50%;background:linear-gradient(135deg,#FF3D71,#3DC8FF)}',
            '.mkigp-ended{margin:1.2em auto 0;max-width:28em;padding:.8em 1em;border-radius:12px;border:1px solid rgba(255,255,255,.16);background:rgba(255,255,255,.05);text-align:center;line-height:1.45}',
            '.mkigp-ended p{margin:0}',
            '.mkigp-again{display:flex;flex-wrap:wrap;justify-content:center;gap:.5em 1.2em;margin-top:.6em!important}',
            '.mkigp-again a,.mkigp-again button{padding:.2em 0;border:0;background:none;color:#3DC8FF;font:inherit;font-weight:600;text-decoration:underline;text-underline-offset:3px;cursor:pointer}',
            '.mkigp-again a:focus-visible,.mkigp-again button:focus-visible{outline:3px solid #3DC8FF;outline-offset:3px;border-radius:4px}',
            '.mkigp-badge{position:fixed;left:50%;bottom:calc(14px + env(safe-area-inset-bottom));transform:translateX(-50%);z-index:999;display:inline-flex;align-items:center;gap:.55em;max-width:calc(100vw - 24px);padding:.5em .85em .5em .95em;border-radius:999px;border:1px solid rgba(255,255,255,.16);background:rgba(20,20,28,.92);color:#fff;font-family:inherit;font-size:.86rem;font-weight:600;line-height:1.3;cursor:pointer;box-shadow:0 10px 30px -12px rgba(0,0,0,.8);-webkit-backdrop-filter:blur(10px);backdrop-filter:blur(10px)}',
            '.mkigp-badge::before{content:"";flex:none;width:.6em;height:.6em;border-radius:50%;background:linear-gradient(135deg,#FF3D71,#3DC8FF)}',
            '.mkigp-badge::after{content:"";flex:none;width:.4em;height:.4em;margin-left:.1em;border-right:2px solid currentColor;border-bottom:2px solid currentColor;transform:rotate(-45deg);opacity:.7}',
            '.mkigp-badge span{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.mkigp-badge.is-soon{border-color:rgba(255,181,71,.7);background:rgba(48,32,8,.94)}',
            '.mkigp-badge.is-soon::before{background:#ffb547}',
            '.mkigp-badge.is-over{border-color:rgba(255,138,138,.6);background:rgba(48,14,14,.94)}',
            '.mkigp-badge.is-over::before{background:#ff8a8a}',
            '.mkigp-badge:focus-visible{outline:3px solid #3DC8FF;outline-offset:3px}',
            '.mkigp-hidden{display:none!important}',
            '.mkigp-box{width:calc(100% - 32px);max-width:420px;max-height:calc(100vh - 32px);margin:auto auto 16px;padding:0;overflow:auto;border-radius:18px;border:1px solid rgba(255,255,255,.12);background:#16161e;color:#fff;box-shadow:0 30px 80px -30px #000;font-family:inherit}',
            '@media (min-width:600px){.mkigp-box{margin:auto}}',
            '.mkigp-box::backdrop{background:rgba(0,0,0,.55)}',
            '.mkigp-box::before{content:"";display:block;height:4px;background:linear-gradient(90deg,#FF3D71,#3DC8FF)}',
            '.mkigp-in{padding:18px 20px 20px}',
            '.mkigp-kick{margin:0 0 4px;font-size:.72rem;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#FF5C87}',
            '.mkigp-box h2{margin:0;font-size:1.3rem;line-height:1.25}',
            '.mkigp-box p{margin:.5em 0 0;color:rgba(255,255,255,.72);line-height:1.45}',
            '.mkigp-sec{margin:16px 0 0;padding:14px;border-radius:14px;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.08)}',
            '.mkigp-sec h3{margin:0;font-size:.95rem}',
            '.mkigp-steps{margin:.6em 0 0;padding-left:1.4em;display:grid;gap:.5em;color:rgba(255,255,255,.78);line-height:1.45}',
            '.mkigp-addr{display:flex;flex-wrap:wrap;align-items:center;gap:6px 8px;margin:.35em 0 0}',
            '.mkigp-row{display:flex;gap:8px;margin:.45em 0 0}',
            '.mkigp-row input{flex:1;min-width:0;padding:.7em .8em;border-radius:10px;border:1px solid rgba(255,255,255,.2);background:rgba(255,255,255,.06);color:#fff;font:inherit;font-size:1.05rem;letter-spacing:.2em;text-align:center;text-transform:uppercase}',
            '.mkigp-row input:focus{outline:none;border-color:#3DC8FF;box-shadow:0 0 0 3px rgba(61,200,255,.25)}',
            '.mkigp-btn{flex:none;padding:.7em 1.1em;border:0;border-radius:10px;font:inherit;font-weight:700;cursor:pointer;color:#0b0b0b;background:linear-gradient(90deg,#FF3D71,#3DC8FF)}',
            '.mkigp-btn[disabled]{opacity:.6;cursor:progress}',
            '.mkigp-copy{flex:none;padding:.35em .8em;border-radius:8px;border:1px solid rgba(255,255,255,.22);background:none;color:#fff;font:inherit;font-size:.85rem;font-weight:600;cursor:pointer}',
            '.mkigp-foot{display:flex;gap:10px;margin:16px 0 0}',
            '.mkigp-foot .mkigp-plain{flex:1;margin:0}',
            '.mkigp-plain{display:block;width:100%;margin:16px 0 0;padding:.75em;border-radius:10px;border:1px solid rgba(255,255,255,.18);background:none;color:#fff;font:inherit;font-weight:600;cursor:pointer}',
            '.mkigp-btn:focus-visible,.mkigp-plain:focus-visible,.mkigp-copy:focus-visible{outline:3px solid #3DC8FF;outline-offset:2px}',
            '.mkigp-say{margin:.6em 0 0;font-size:.9rem}',
            '.mkigp-say.is-ok{color:#3ddc84}.mkigp-say.is-bad{color:#ff8a8a}',
            '.mkigp-box code{font-family:ui-monospace,Menlo,Consolas,monospace;font-size:.88rem;color:#fff;overflow-wrap:anywhere}'
        ].join('\n');
        document.head.appendChild(s);
    }

    /* ---------- sign-in page ---------- */
    function loginExtras() {
        var page = document.querySelector('#loginPage:not(.hide)');
        if (!page) return;
        var anchor = page.querySelector('.readOnlyContent') || page.querySelector('form');
        if (!anchor || !anchor.parentNode) return;
        var was = seen();
        /* Jellyfin keeps earlier sign-in pages hidden in the page: a note there would be stale */
        Array.prototype.forEach.call(document.querySelectorAll('.mkigp-ended'), function (x) { if (!page.contains(x)) x.parentNode.removeChild(x); });
        var old = page.querySelector('.mkigp-ended'), sig = was ? JSON.stringify([was.end, was.cut || 0, keys().map(function (g) { return g.key; })]) : '';
        if (old && old.getAttribute('data-sig') !== sig) { old.parentNode.removeChild(old); old = null; }
        if (was && !old) {
            addCss();
            var n = el('div', 'mkigp-ended');
            n.setAttribute('role', 'status');
            n.setAttribute('data-sig', sig);
            if (new Date(was.end).getTime() <= Date.now()) {
                n.appendChild(el('p', null, 'Your guest pass ended ' + when(was.end) + '. Thanks for watching!'));
            } else {
                /* a way back only where the join page still knows this guest; the join page says so if the pass or the
                   guest was ended in the meantime */
                var back = keys();
                var shared = false;
                try { shared = !!sessionStorage.getItem(SHARED); } catch (e) { }
                n.appendChild(el('p', null, was.cut ? 'You were signed out of your guest access on this browser.'
                    : shared ? 'This is a shared computer, so Jellyfin asks again after a reload.' : 'You are signed out of your guest access.'));
                if (back.length) {
                    var row = el('p', 'mkigp-again'), go = el('a', null, back.length === 1 ? 'Continue as ' + back[0].name : 'Continue as a guest');
                    go.href = BASE + '/MkiGuestPass/Join';
                    var no = el('button', null, back.length === 1 ? 'Forget ' + back[0].name + ' on this browser' : 'Forget guests on this browser'); no.type = 'button';
                    no.addEventListener('click', function () {
                        forgetKeys();
                        n.textContent = '';
                        var done = el('p', null, 'Done: this browser does not know your guest access any more.');
                        done.tabIndex = -1; done.setAttribute('role', 'status');
                        n.appendChild(done);
                        n.setAttribute('data-sig', JSON.stringify([was.end, was.cut || 0, []]));
                        try { done.focus(); } catch (e) { }
                    });
                    row.appendChild(go); row.appendChild(no); n.appendChild(row);
                }
            }
            anchor.parentNode.insertBefore(n, anchor.nextSibling);
        }
        if (!CODE_LINK || page.querySelector('.mkigp-code')) return;
        addCss();
        var box = el('div', 'mkigp-code'), a = el('a', null, 'Have a guest code? Join as a guest');
        a.href = BASE + '/MkiGuestPass/Join';
        box.appendChild(a);
        var after = page.querySelector('.mkigp-ended') || anchor;
        after.parentNode.insertBefore(box, after.nextSibling);
    }

    /* ---------- the guest badge ---------- */
    function api() { var a = window.ApiClient; return a && a.accessToken && a.accessToken() && a.getCurrentUserId && a.getCurrentUserId() ? a : null; }

    /* joined as on a shared computer (in this window): from now on Jellyfin asks for a sign-in when it starts again */
    function sharedComputer(userId) {
        try {
            if (sessionStorage.getItem(SHARED) === userId) localStorage.setItem('enableAutoLogin', 'false');
        } catch (e) { }
    }

    function call(method, path) {
        var a = api();
        if (!a) return Promise.reject({ status: 401, text: '' });
        return fetch(a.getUrl(path), {
            method: method,
            headers: { 'Authorization': 'MediaBrowser Token="' + a.accessToken() + '"', 'Accept': 'application/json' },
            credentials: 'same-origin',
            cache: 'no-store'
        }).then(function (res) {
            return res.text().then(function (text) {
                if (res.ok) { var d = null; try { d = JSON.parse(text); } catch (e) { d = text; } return d; }
                throw { status: res.status, text: text };
            });
        }, function () { throw { status: 0, text: '' }; });
    }

    function when(iso) {
        var d = new Date(iso);
        var today = new Date(), tomorrow = new Date(); tomorrow.setDate(today.getDate() + 1);
        var time = d.toLocaleTimeString(EN, { hour: '2-digit', minute: '2-digit' });
        if (d.toDateString() === today.toDateString()) return 'today ' + time;
        if (d.toDateString() === tomorrow.toDateString()) return 'tomorrow ' + time;
        return d.toLocaleDateString(EN, { weekday: 'short', day: 'numeric', month: 'short' }) + ', ' + time;
    }
    function left(iso) {
        var m = Math.round((new Date(iso).getTime() - Date.now()) / 60000);
        if (m <= 0) return 'ending now';
        if (m < 60) return m + (m === 1 ? ' minute' : ' minutes') + ' left';
        var h = Math.round(m / 60);
        if (h < 48) return h + (h === 1 ? ' hour' : ' hours') + ' left';
        return Math.round(h / 24) + ' days left';
    }
    function over(g) { return new Date(g.EndUtc).getTime() <= Date.now(); }
    function soon(g) { return new Date(g.EndUtc).getTime() - Date.now() <= Math.max(0, g.WarnHours || 0) * 3600000; }
    function devices(n, of) { return 'signed in on ' + n + ' of ' + of + (of === 1 ? ' device' : ' devices'); }

    /* hidden while a video plays full screen and on pages without a signed-in user */
    function inPlayer() { return !!document.querySelector('#videoOsdPage:not(.hide), .videoPlayerContainer:not(.hide)'); }
    /* above Jellyfin's now-playing bar while music plays, never on top of its buttons */
    function barHeight() {
        var bar = document.querySelector('.nowPlayingBar:not(.hide):not(.nowPlayingBar-hidden)');
        if (!bar) return 0;
        var r = bar.getBoundingClientRect();
        return r.height > 0 && r.top < window.innerHeight ? Math.round(window.innerHeight - r.top) : 0;
    }

    function render() {
        if (!me || !me.Guest || !api()) { if (badge) badge.classList.add('mkigp-hidden'); return; }
        addCss();
        if (!badge) {
            badge = el('button', 'mkigp-badge'); badge.type = 'button';
            badge.appendChild(el('span'));
            badge.addEventListener('click', openPanel);
            document.body.appendChild(badge);
        }
        /* only real changes: the page watches itself for the sign-in page and the player */
        var ended = over(me), text = ended ? 'Guest access ended' : 'Guest · until ' + when(me.EndUtc);
        if (badge.firstChild.textContent !== text) badge.firstChild.textContent = text;
        badge.setAttribute('aria-label', ended ? 'Guest access ended. Open guest options' : 'Guest access until ' + when(me.EndUtc) + ', ' + left(me.EndUtc) + '. Open guest options, such as signing in a TV');
        badge.classList.toggle('is-soon', !ended && soon(me));
        badge.classList.toggle('is-over', ended);
        badge.classList.toggle('mkigp-hidden', inPlayer() || !!panel);
        var lift = barHeight(), bottom = lift ? 'calc(' + (lift + 10) + 'px + env(safe-area-inset-bottom))' : '';
        if (badge.style.bottom !== bottom) badge.style.bottom = bottom;
    }

    /* the web client's own sign-out, which also takes the user to the sign-in page */
    function signOut() {
        forgetShared();
        try { if (window.Dashboard && typeof window.Dashboard.logout === 'function') window.Dashboard.logout(); } catch (e) { }
    }
    /* on a shared computer nobody continues as this guest after a sign-out, also not in the same window */
    function forgetShared() {
        try {
            var a = window.ApiClient, id = a && a.getCurrentUserId && a.getCurrentUserId();
            if (id && sessionStorage.getItem(SHARED) === id) { sessionStorage.removeItem(KEYS); sessionStorage.removeItem(SHARED); }
        } catch (e) { }
    }
    /* Jellyfin's own Sign Out goes through ApiClient.logout */
    function hookLogout() {
        var a = window.ApiClient;
        if (!a || a.mkigpHooked || typeof a.logout !== 'function') return;
        var orig = a.logout;
        a.logout = function () { forgetShared(); return orig.apply(this, arguments); };
        a.mkigpHooked = true;
    }

    /* a guest is asked about again after a minute at the next page change, so an ended guest is not left waiting */
    function ask(force) {
        var a = api();
        var who = a ? (a.serverId && a.serverId()) + '|' + a.getCurrentUserId() : '';
        if (!who) { me = null; meFor = ''; render(); return; }
        var stale = !!(me && me.Guest && Date.now() - asked > 60000);
        if (asking || who === meFor && me && !stale && !force) { render(); return; }
        asking = true; asked = Date.now();
        call('GET', 'MkiGuestPass/Me').then(function (d) {
            asking = false; me = d && typeof d === 'object' ? d : null; meFor = who;
            /* remembered in this tab, so the sign-in page can say why the guest landed there */
            seen(me && me.Guest ? { end: me.EndUtc, pass: me.PassId } : null);
            if (me && me.Guest) { sharedComputer(a.getCurrentUserId()); hookLogout(); }
            render();
        }, function (e) {
            /* asked again at the next page change, never in a loop */
            asking = false; me = null; meFor = who; render();
            /* a guest whose access ended or was ended: Jellyfin itself would only show a spinner */
            if (e && e.status === 401 && seen()) { var was = seen(); was.cut = Date.now(); seen(was); endKeys(was.pass); signOut(); }
        });
    }

    function closePanel() {
        var p = panel;
        panel = null;
        if (p) {
            if (typeof p.close === 'function' && p.open) { try { p.close(); } catch (e) { } }
            if (p.parentNode) p.parentNode.removeChild(p);
        }
        document.removeEventListener('keydown', onKey, true);
        render();
        if (badge) badge.focus();
    }
    /* without a native dialog: Esc closes, and Tab stays inside the panel */
    function onKey(e) {
        if (!panel) return;
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePanel(); return; }
        if (e.key !== 'Tab' || typeof panel.showModal === 'function') return;
        var f = Array.prototype.filter.call(panel.querySelectorAll('button, input, [tabindex]:not([tabindex="-1"])'), function (x) { return !x.disabled && x.offsetParent !== null; });
        if (!f.length) return;
        var first = f[0], last = f[f.length - 1];
        if (e.shiftKey && (document.activeElement === first || !panel.contains(document.activeElement))) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && (document.activeElement === last || !panel.contains(document.activeElement))) { e.preventDefault(); first.focus(); }
    }
    function copyButton(text) {
        var b = el('button', 'mkigp-copy', 'Copy'); b.type = 'button';
        b.setAttribute('aria-label', 'Copy the server address');
        b.addEventListener('click', function () {
            var done = function (ok) { b.textContent = ok ? 'Copied' : 'Select it'; setTimeout(function () { b.textContent = 'Copy'; }, 2000); };
            if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(function () { done(true); }, function () { done(false); });
            else {
                var ta = el('textarea'); ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.top = '-1000px';
                (panel || document.body).appendChild(ta); ta.select();
                var ok = false; try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
                ta.parentNode.removeChild(ta); done(ok);
            }
        });
        return b;
    }

    function openPanel() {
        if (panel || !me || !me.Guest) return;
        var g = me, native = typeof window.HTMLDialogElement === 'function';
        panel = el(native ? 'dialog' : 'div', 'mkigp-box');
        var box = panel, inner = el('div', 'mkigp-in');
        box.setAttribute('aria-labelledby', 'mkigpTitle');
        if (!native) { box.setAttribute('role', 'dialog'); box.setAttribute('aria-modal', 'true'); box.style.position = 'fixed'; box.style.inset = '0'; box.style.zIndex = '1000'; }
        inner.appendChild(el('p', 'mkigp-kick', 'Guest pass'));
        var h = el('h2', null, 'Hi ' + g.Name); h.id = 'mkigpTitle'; inner.appendChild(h);
        inner.appendChild(el('p', null, over(g) ? 'Your guest access ended ' + when(g.EndUtc) + '. Thanks for watching!'
            : 'You are a guest on this server until ' + when(g.EndUtc) + ' (' + left(g.EndUtc) + '). Your guest account and everything in it are deleted afterwards.'));
        var count = el('p', null, '“' + g.Pass + '” · ' + devices(g.Devices, g.DevicesPerGuest) + '.');
        inner.appendChild(count);

        var tv = el('div', 'mkigp-sec'), address = BASE || location.origin;
        tv.appendChild(el('h3', null, 'Sign in a TV'));
        if (over(g)) {
            tv.appendChild(el('p', null, 'Not any more: your guest access has ended.'));
        } else if (!g.QuickConnect) {
            tv.appendChild(el('p', null, 'Quick Connect is turned off on this server, so TVs cannot be signed in with a guest pass. Ask the person who invited you.'));
        } else if (g.Devices >= g.DevicesPerGuest) {
            tv.appendChild(el('p', null, 'You are already signed in on as many devices as your pass allows. To sign in a TV, sign out of Jellyfin on another device first (in its menu or settings), or ask the person who invited you.'));
        } else {
            /* in the order a TV app asks: the address first, then Quick Connect */
            var steps = el('ol', 'mkigp-steps');
            var s1 = el('li'); s1.appendChild(document.createTextNode('In the Jellyfin app on the TV, enter the server address:'));
            var addr = el('div', 'mkigp-addr'); addr.appendChild(el('code', null, address)); addr.appendChild(copyButton(address)); s1.appendChild(addr);
            steps.appendChild(s1);
            steps.appendChild(el('li', null, 'Choose Quick Connect. The TV shows a code.'));
            var s3 = el('li'); s3.appendChild(document.createTextNode('Type that code here:'));
            var row = el('div', 'mkigp-row'), inp = el('input'), go = el('button', 'mkigp-btn', 'Sign in');
            inp.type = 'text'; inp.maxLength = 10; inp.autocomplete = 'off'; inp.setAttribute('aria-label', 'Code shown on the TV'); inp.setAttribute('autocapitalize', 'characters'); inp.setAttribute('spellcheck', 'false');
            go.type = 'button';
            row.appendChild(inp); row.appendChild(go); s3.appendChild(row);
            steps.appendChild(s3);
            tv.appendChild(steps);
            var say = el('p', 'mkigp-say'); say.setAttribute('role', 'status'); tv.appendChild(say);
            var send = function () {
                if (go.disabled) return;
                var c = inp.value.replace(/\s+/g, '');
                if (!/^[A-Za-z0-9]{4,10}$/.test(c)) { say.className = 'mkigp-say is-bad'; say.textContent = 'Type the code exactly as the TV shows it.'; inp.focus(); return; }
                go.disabled = true; say.className = 'mkigp-say'; say.textContent = 'Signing in the TV…';
                call('POST', 'QuickConnect/Authorize?code=' + encodeURIComponent(c)).then(function () {
                    var n = Math.min(g.DevicesPerGuest, g.Devices + 1), last = n >= g.DevicesPerGuest;
                    g.Devices = n; inp.value = '';
                    /* the last free device: no code field that can only fail */
                    go.disabled = last; inp.disabled = last;
                    say.className = 'mkigp-say is-ok';
                    say.textContent = 'Done. The TV signs in within a few seconds.' + (last ? ' That was the last device your pass allows: to sign in another one, sign out on a device first.' : '');
                    count.textContent = '“' + g.Pass + '” · ' + devices(n, g.DevicesPerGuest) + '.';
                    meFor = ''; ask();
                }, function (e) {
                    go.disabled = false;
                    say.className = 'mkigp-say is-bad';
                    say.textContent = e.status === 403 && e.text && e.text.length < 300 && e.text.charAt(0) !== '{' ? e.text
                        : e.status === 404 || e.status === 400 ? 'That code is not valid any more. Check the TV and try again.'
                        : e.status === 401 ? 'Your guest access has ended.'
                        : 'This did not work. Please try again.';
                });
            };
            go.addEventListener('click', send);
            inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') send(); });
        }
        if (over(g) || !g.QuickConnect || g.Devices >= g.DevicesPerGuest) {
            var a2 = el('div', 'mkigp-addr'); a2.appendChild(document.createTextNode('Server address: ')); a2.appendChild(el('code', null, address));
            tv.appendChild(a2);
        }
        inner.appendChild(tv);

        var foot = el('div', 'mkigp-foot');
        var out = el('button', 'mkigp-plain', 'Sign out'); out.type = 'button';
        out.addEventListener('click', function () { closePanel(); signOut(); });
        var close = el('button', 'mkigp-plain', 'Close'); close.type = 'button';
        close.addEventListener('click', closePanel);
        foot.appendChild(out); foot.appendChild(close);
        inner.appendChild(foot);
        box.appendChild(inner);
        /* a click on the dimmed page around the panel closes it */
        box.addEventListener('click', function (e) { if (e.target === box) closePanel(); });
        if (native) box.addEventListener('cancel', function (e) { e.preventDefault(); closePanel(); });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(box);
        if (native) { try { box.showModal(); } catch (e) { } }
        render();
        (box.querySelector('input') || close).focus();
    }

    /* ---------- when to look again ---------- */
    function later() {
        clearTimeout(timer);
        timer = setTimeout(function () { if (!api()) loginExtras(); ask(); }, 400);
    }
    document.addEventListener('viewshow', later);
    window.addEventListener('hashchange', later);
    window.addEventListener('popstate', later);
    window.addEventListener('resize', function () { if (badge) render(); });
    document.addEventListener('visibilitychange', function () { if (!document.hidden) { meFor = ''; later(); } });
    setInterval(function () { render(); }, 30000);
    setInterval(function () { if (me && me.Guest) ask(true); }, 60000);
    setInterval(function () { ask(true); }, 10 * 60000);
    if (window.MutationObserver) {
        /* the sign-in page, the player and the now-playing bar come and go without a viewshow in some versions */
        var pending = 0;
        new MutationObserver(function () {
            if (pending) return;
            pending = setTimeout(function () { pending = 0; if (!api()) loginExtras(); if (badge) render(); }, 300);
        }).observe(document.body || document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', later); else later();
})();
