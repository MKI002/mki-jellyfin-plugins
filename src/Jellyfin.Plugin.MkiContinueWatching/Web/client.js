/* MKI Continue Watching Cleanup: a Remove button on the Continue Watching cards of the Jellyfin web client.
   Only loaded while an administrator has the Remove button turned on, and it asks the server again before it shows
   anything. A user only ever removes items from their own Continue Watching: the server takes the user from the
   session. Removing clears the resume position, nothing else; Undo puts it back. */
(function () {
    'use strict';
    if (window.MkiContinueWatching) {
        return;
    }
    window.MkiContinueWatching = true;

    var T = {
        remove: 'Remove from Continue Watching',
        removed: 'Removed from Continue Watching',
        undo: 'Undo',
        restored: 'Back in Continue Watching',
        notRestored: 'It could not be put back: it was played again in the meantime.',
        failed: 'Could not remove it. Please try again.',
        off: 'Removing is turned off on this server.',
        undoFailed: 'Could not put it back. Please try again.'
    };
    var MARK = 'data-mkicw';
    var state = { on: null, user: null, checked: 0, ids: null, idsAt: 0, loading: null, timer: 0 };

    var css = [
        '.mkicw-remove .mkicw-ic{font-size:1.4em}',
        '.mkicw-x{position:absolute;top:.4em;left:.4em;z-index:3;width:2.2em;height:2.2em;margin:0;padding:0;border:0;border-radius:50%;',
        'display:none;align-items:center;justify-content:center;cursor:pointer;color:#fff;background:rgba(0,0,0,.62);',
        'box-shadow:0 0 0 1px rgba(255,255,255,.28);-webkit-tap-highlight-color:transparent}',
        '.mkicw-x .mkicw-ic{font-size:1.25em}',
        '.mkicw-x:focus-visible,.mkicw-remove:focus-visible{outline:2px solid #00a4dc;outline-offset:2px}',
        '@media (hover:none){.mkicw-x{display:flex}.mkicw-remove{display:none!important}}',
        '.layout-tv .mkicw-x{display:none!important}',
        '.mkicw-gone{transition:opacity .2s ease,max-width .25s ease .15s,margin .25s ease .15s,padding .25s ease .15s;opacity:0;max-width:0!important;margin-left:0!important;margin-right:0!important;padding:0!important;overflow:hidden}',
        '.mkicw-busy{opacity:.45;pointer-events:none}',
        '.mkicw-toast{position:fixed;left:50%;bottom:calc(1.5em + env(safe-area-inset-bottom,0px));transform:translate(-50%,1em);z-index:99999;',
        'display:flex;align-items:center;gap:1em;max-width:calc(100vw - 2em);box-sizing:border-box;padding:.7em .8em .7em 1.1em;border-radius:.5em;',
        'background:#202020;color:#fff;box-shadow:0 6px 24px rgba(0,0,0,.45);font-size:.95em;line-height:1.35;opacity:0;transition:opacity .2s,transform .2s}',
        '.mkicw-toast.is-on{opacity:1;transform:translate(-50%,0)}',
        '.mkicw-toast button{flex:none;margin:0;border:0;border-radius:.3em;padding:.45em .8em;font:inherit;font-weight:600;color:#00a4dc;background:transparent;cursor:pointer;text-transform:uppercase;letter-spacing:.04em}',
        '.mkicw-toast button:hover,.mkicw-toast button:focus-visible{background:rgba(0,164,220,.16);outline:none}'
    ].join('');

    function api() { return window.ApiClient; }
    function userId() { var a = api(); try { return a && a.getCurrentUserId && a.accessToken && a.accessToken() ? a.getCurrentUserId() : null; } catch (e) { return null; } }

    function request(type, path, body) {
        var a = api();
        var opts = { type: type, url: a.getUrl(path), dataType: 'json' };
        if (body) { opts.data = JSON.stringify(body); opts.contentType = 'application/json'; }
        return a.ajax(opts);
    }

    /* ---------- is the button turned on for this server? asked again after a sign-in and every few minutes ---------- */
    function checkStatus() {
        var u = userId();
        if (!u) { state.on = null; state.user = null; return Promise.resolve(false); }
        if (state.user === u && state.on !== null && Date.now() - state.checked < 300000) return Promise.resolve(state.on);
        if (state.loading) return state.loading;
        state.loading = request('GET', 'MkiContinueWatching/Status').then(function (r) {
            state.on = !!(r && r.RemoveButton); state.user = u; state.checked = Date.now(); state.ids = null;
            return state.on;
        }, function () { state.on = false; state.user = u; state.checked = Date.now(); return false; });
        state.loading.then(function () { state.loading = null; }, function () { state.loading = null; });
        return state.loading;
    }

    /* ---------- the user's own Continue Watching ids, to tell that row from the others ---------- */
    function resumeIds(force) {
        if (!force && state.ids && Date.now() - state.idsAt < 20000) return Promise.resolve(state.ids);
        var u = userId();
        return api().ajax({
            type: 'GET', dataType: 'json',
            url: api().getUrl('UserItems/Resume', { userId: u, MediaTypes: 'Video', Limit: 100, EnableImages: false, EnableUserData: false, EnableTotalRecordCount: false })
        }).then(function (r) {
            var ids = {};
            ((r && r.Items) || []).forEach(function (i) { ids[i.Id] = true; });
            state.ids = ids; state.idsAt = Date.now();
            return ids;
        });
    }

    /* The Continue Watching row: a home section whose every card has a resume position and is in the user's own
       resume list. "Next Up" and "Recently added" rows always hold cards without a resume position. */
    function sections() {
        return Array.prototype.slice.call(document.querySelectorAll('.homeSectionsContainer .verticalSection, #indexPage .verticalSection')).filter(function (s) {
            var cards = s.querySelectorAll('.card[data-id]');
            if (!cards.length) return false;
            for (var i = 0; i < cards.length; i++) if (!cards[i].hasAttribute('data-positionticks')) return false;
            return true;
        });
    }

    function scan() {
        state.timer = 0;
        if (!userId()) return;
        var found = sections();
        if (!found.length) return;
        checkStatus().then(function (on) {
            if (!on) return;
            var todo = [];
            found.forEach(function (s) {
                Array.prototype.forEach.call(s.querySelectorAll('.card[data-id]'), function (c) { if (!c.hasAttribute(MARK)) todo.push(c); });
            });
            if (!todo.length) return;
            var needFresh = state.ids && todo.some(function (c) { return !state.ids[c.getAttribute('data-id')]; });
            return resumeIds(needFresh).then(function (ids) {
                found.forEach(function (s) {
                    var cards = s.querySelectorAll('.card[data-id]');
                    for (var i = 0; i < cards.length; i++) if (!ids[cards[i].getAttribute('data-id')]) return;
                    Array.prototype.forEach.call(cards, decorate);
                });
            });
        }).then(null, function () { /* the next change on the page tries again */ });
    }

    function icon(name) { var s = document.createElement('span'); s.className = 'material-icons mkicw-ic'; s.setAttribute('aria-hidden', 'true'); s.textContent = name; return s; }

    function decorate(card) {
        if (card.hasAttribute(MARK)) return;
        card.setAttribute(MARK, '1');
        var id = card.getAttribute('data-id');
        /* hover row next to Jellyfin's own Mark played / Favorite / More buttons */
        var row = card.querySelector('.cardOverlayButton-br');
        if (row) {
            var b = document.createElement('button');
            b.type = 'button';
            b.className = 'cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light mkicw-remove';
            b.setAttribute('data-action', 'none');
            b.title = T.remove; b.setAttribute('aria-label', T.remove);
            var i1 = icon('remove_circle_outline'); i1.className += ' cardOverlayButtonIcon cardOverlayButtonIcon-hover';
            b.appendChild(i1);
            var more = row.querySelector('[data-action="menu"]');
            row.insertBefore(b, more || null);
            bind(b, card, id);
        }
        /* touch screens have no hover: a small round button on the picture */
        var box = card.querySelector('.cardScalable');
        if (box) {
            var x = document.createElement('button');
            x.type = 'button'; x.className = 'mkicw-x';
            x.title = T.remove; x.setAttribute('aria-label', T.remove);
            x.appendChild(icon('close'));
            box.appendChild(x);
            bind(x, card, id);
        }
    }

    function bind(button, card, id) {
        button.addEventListener('click', function (e) {
            e.preventDefault(); e.stopPropagation();
            remove(card, id);
        }, true);
    }

    function remove(card, id) {
        if (card.classList.contains('mkicw-busy')) return;
        card.classList.add('mkicw-busy');
        request('POST', 'MkiContinueWatching/Remove', { ItemId: id }).then(function () {
            var section = card.closest('.verticalSection');
            var width = card.offsetWidth;
            card.style.maxWidth = width + 'px';
            void card.offsetWidth;
            card.classList.remove('mkicw-busy');
            card.classList.add('mkicw-gone');
            if (state.ids) delete state.ids[id];
            setTimeout(function () {
                card.hidden = true;
                if (section && !section.querySelector('.card[data-id]:not([hidden])')) section.style.display = 'none';
            }, 420);
            toast(T.removed, T.undo, function () { undo(card, section, id); });
        }, function (err) {
            card.classList.remove('mkicw-busy');
            toast(err && err.status === 403 ? T.off : T.failed);
            if (err && err.status === 403) state.checked = 0;
        });
    }

    function undo(card, section, id) {
        request('POST', 'MkiContinueWatching/Restore', { ItemId: id }).then(function (r) {
            if (!r || !r.Restored) { toast(T.notRestored); return; }
            state.ids = null;
            if (section) section.style.display = '';
            card.hidden = false;
            card.classList.remove('mkicw-gone');
            card.style.maxWidth = '';
            toast(T.restored);
        }, function () { toast(T.undoFailed); });
    }

    /* ---------- one toast at a time, with an optional action ---------- */
    var toastEl = null, toastTimer = 0;
    function toast(text, action, onAction) {
        if (toastEl) toastEl.remove();
        clearTimeout(toastTimer);
        var t = document.createElement('div');
        t.className = 'mkicw-toast'; t.setAttribute('role', 'status'); t.setAttribute('aria-live', 'polite');
        var s = document.createElement('span'); s.textContent = text; t.appendChild(s);
        if (action) {
            var b = document.createElement('button'); b.type = 'button'; b.textContent = action;
            b.addEventListener('click', function () { hide(); onAction(); });
            t.appendChild(b);
        }
        document.body.appendChild(t);
        toastEl = t;
        requestAnimationFrame(function () { t.classList.add('is-on'); });
        toastTimer = setTimeout(hide, action ? 8000 : 4000);
        function hide() {
            clearTimeout(toastTimer);
            t.classList.remove('is-on');
            setTimeout(function () { t.remove(); if (toastEl === t) toastEl = null; }, 250);
        }
    }

    function schedule() { if (!state.timer) state.timer = setTimeout(scan, 250); }

    function start() {
        var style = document.createElement('style');
        style.textContent = css;
        document.head.appendChild(style);
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', schedule);
        schedule();
    }

    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start);
})();
