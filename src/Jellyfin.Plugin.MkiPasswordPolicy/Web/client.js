/* MKI Password Policy: change dialog, sign-in hook and live password hints for the Jellyfin web client. */
(function () {
    'use strict';
    if (window.MkiPasswordPolicy) {
        return;
    }

    var script = document.currentScript;
    var BASE = script && script.src ? script.src.replace(/\/MkiPasswordPolicy\/client\.js.*$/i, '') : '';
    // "link": the plugin has nothing to enforce, so only the admin shortcut on user pages runs.
    var FULL = !script || script.getAttribute('data-mode') !== 'link';
    var AUTH_URL = /\/Users\/(AuthenticateByName|AuthenticateWithQuickConnect|[^\/?]+\/Authenticate)(\?|$)/i;

    var T = {
        title: 'Set a new password',
        admin: 'Your administrator requires a new password before you can continue.',
        expired: 'Your password has expired. Please set a new one.',
        'new': 'Please set your own password for your new account.',
        standalone: 'Set a new password here when your administrator asks for one. Afterwards you can sign in again in every Jellyfin app.',
        username: 'Username',
        current: 'Current password',
        newPw: 'New password',
        confirm: 'Repeat new password',
        next: 'Continue',
        save: 'Save password',
        cancel: 'Cancel',
        mismatch: 'The two new passwords do not match.',
        rulesFail: 'The new password does not meet all rules yet.',
        failed: 'That did not work.',
        done: 'Done! Your new password is saved.',
        doneStandalone: 'Done! You can now sign in to every Jellyfin app with your new password.',
        doneRelogin: 'Done! Sign in with your new password now.',
        len: 'At least {n} characters',
        upper: 'An uppercase letter',
        lower: 'A lowercase letter',
        digit: 'A digit',
        symbol: 'A symbol',
        noUser: 'Does not contain the username',
        common: 'Not a common, easy to guess password',
        repeat: 'At most {n} identical characters in a row',
        rulesTitle: 'Your password must have:',
        shortTitle: 'MKI Password Policy',
        shortProfile: 'Profile',
        shortStatus: 'Password',
        shortRequest: 'New password',
        shortPending: 'Requested, not set yet',
        shortDue: 'Requested, keeps access until ',
        shortNone: 'Not requested',
        shortSameAsAll: ' (same as all users)',
        shortExempt: 'Exempt (administrator)',
        shortDisabled: 'This account is disabled in Jellyfin. Rules and requests still apply once it is enabled again.',
        shortReset: 'Jellyfin\u2019s \u201cReset password\u201d removes the password completely: the account can then be signed in to without one.',
        shortOpen: 'Open in MKI Password Policy',
        profiles: { none: 'No rules', basic: 'Basic', standard: 'Standard', strong: 'Strong', verystrong: 'Very strong', passphrase: 'Passphrase', custom: 'Custom profile' },
        statuses: { ok: 'Meets the rules', older: 'Older rules', unknown: 'Unknown', none: 'No password', norules: 'No rules' },
        strength: ['Very weak', 'Weak', 'Fair', 'Strong', 'Very strong']
    };

    function prepare(r) {
        if (!r) return null;
        r.commonSet = {};
        (r.Common || []).forEach(function (w) { r.commonSet[w.toLowerCase()] = true; });
        return r;
    }

    function jsonOrError(res) {
        return res.json().catch(function () { return {}; }).then(function (body) {
            if (!res.ok) throw new Error((body && body.Message) || T.failed);
            return body;
        });
    }

    function post(path, data) {
        return originalFetch(BASE + path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'omit',
            cache: 'no-store',
            body: JSON.stringify(data)
        });
    }

    // Rules of an account with an open change request; needs its current password.
    function changeRules(username, current) {
        return post('/MkiPasswordPolicy/ChangeRules', { Username: username, CurrentPassword: current }).then(jsonOrError).then(prepare);
    }

    // Rules for a signed-in user's own password (or, for admins, another user's or a new user's).
    function myRules(query) {
        if (!window.ApiClient) return Promise.resolve(null);
        return window.ApiClient.ajax({
            type: 'GET',
            url: window.ApiClient.getUrl('MkiPasswordPolicy/MyRules', query || {}),
            dataType: 'json'
        }).then(prepare, function () { return null; });
    }

    // Same checks as Services/PasswordRules.cs.
    function isCommon(pw, set) {
        var low = pw.toLowerCase();
        if (set[low]) {
            return true;
        }
        var core = low.replace(/^[^\p{L}]+/u, '').replace(/[^\p{L}]+$/u, '');
        return core.length > 0 && !!set[core];
    }

    function longestRun(pw) {
        var best = 0, run = 0, chars = Array.from(pw);
        for (var i = 0; i < chars.length; i++) {
            run = i > 0 && chars[i] === chars[i - 1] ? run + 1 : 1;
            best = Math.max(best, run);
        }
        return best;
    }

    function check(pw, username, r) {
        pw = pw || '';
        if (!r || r.Active === false) return [];
        var items = [{ ok: pw.length >= r.MinLength, text: T.len.replace('{n}', r.MinLength) }];
        if (r.RequireUpper) items.push({ ok: /\p{Lu}/u.test(pw), text: T.upper });
        if (r.RequireLower) items.push({ ok: /\p{Ll}/u.test(pw), text: T.lower });
        if (r.RequireDigit) items.push({ ok: /\p{Nd}/u.test(pw), text: T.digit });
        if (r.RequireSymbol) items.push({ ok: /[^\p{L}\p{Nd}]/u.test(pw), text: T.symbol });
        if (r.ForbidUsername && username && username.length >= 3) {
            items.push({ ok: pw.toLowerCase().indexOf(username.toLowerCase()) < 0, text: T.noUser });
        }
        if (r.BlockCommon) items.push({ ok: pw.length > 0 && !isCommon(pw, r.commonSet), text: T.common });
        if (r.MaxRepeat > 0) items.push({ ok: longestRun(pw) <= r.MaxRepeat, text: T.repeat.replace('{n}', r.MaxRepeat) });
        return items;
    }

    function strength(pw) {
        if (!pw) return 0;
        var classes = [/\p{Ll}/u, /\p{Lu}/u, /\p{Nd}/u, /[^\p{L}\p{Nd}]/u].filter(function (re) { return re.test(pw); }).length;
        var score = (pw.length >= 8) + (pw.length >= 12) + (pw.length >= 16) + (classes >= 3) + (classes >= 4) - (longestRun(pw) > 2);
        return Math.max(0, Math.min(4, score));
    }

    var CSS = [
        // Highest possible layer: Jellyfin's own loading spinner (.docspinner) sits at 9999999.
        '.mkipw-back{position:fixed;inset:0;z-index:2147483647;background:rgba(0,0,0,.75);display:flex;align-items:center;justify-content:center;padding:16px;box-sizing:border-box}',
        '.mkipw-box{background:#202020;color:#eee;border-radius:12px;max-width:440px;width:100%;max-height:100%;overflow:auto;padding:24px;box-sizing:border-box;font-family:inherit;box-shadow:0 10px 40px rgba(0,0,0,.6)}',
        '.mkipw-standalone .mkipw-box{box-shadow:none}',
        '.mkipw-box h2{margin:0 0 8px;font-size:1.4em;font-weight:500}',
        '.mkipw-box p{margin:0 0 16px;color:#bbb;line-height:1.4}',
        '.mkipw-box label{display:block;margin:12px 0 4px;font-size:.9em;color:#ccc}',
        '.mkipw-box input{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:6px;border:1px solid #444;background:#2c2c2c;color:#fff;font-size:1em}',
        '.mkipw-box input:focus{outline:none;border-color:#00a4dc}',
        'body:has(.mkipw-back) .docspinner{display:none!important}',
        '.mkipw-short{border-left:3px solid #00a4dc;background:rgba(0,164,220,.08);padding:12px 16px;margin:2em 0 1em;border-radius:4px}',
        '.mkipw-short h3{margin:0 0 8px}',
        '.mkipw-short table{border-collapse:collapse;margin:0 0 8px}',
        '.mkipw-short td{padding:2px 16px 2px 0}',
        '.mkipw-short p{margin:6px 0;opacity:.8;font-size:.9em}',
        '.mkipw-short button{margin:8px 0 0}',
        '.mkipw-rules-title{margin:16px 0 0;font-size:.9em;color:#ccc}',
        '.mkipw-hints{list-style:none;margin:8px 0 0;padding:0;font-size:.85em}',
        '.mkipw-hints li{margin:3px 0;color:#d66}',
        '.mkipw-hints li:before{content:"\\2717  "}',
        '.mkipw-hints li.ok{color:#5c5}',
        '.mkipw-hints li.ok:before{content:"\\2713  "}',
        '.mkipw-meter{height:4px;border-radius:2px;background:#444;margin-top:8px;overflow:hidden}',
        '.mkipw-meter div{height:100%;width:0;transition:width .2s,background .2s}',
        '.mkipw-meter-label{font-size:.8em;color:#999;margin-top:2px}',
        '.mkipw-err{color:#f66;margin-top:12px;min-height:1em}',
        '.mkipw-ok{color:#5c5;margin-top:12px}',
        '.mkipw-buttons{display:flex;gap:8px;margin-top:20px}',
        '.mkipw-buttons button{flex:1;padding:12px;border:0;border-radius:6px;font-size:1em;cursor:pointer;background:#333;color:#eee}',
        '.mkipw-buttons button.mkipw-primary{background:#00a4dc;color:#fff}',
        '.mkipw-buttons button:disabled{opacity:.5;cursor:default}',
        '.mkipw-inline{margin:.5em 0 1em}',
        '.mkipw-inline .mkipw-hints li{margin:2px 0}'
    ].join('\n');

    function addCss() {
        if (document.getElementById('mkipw-css')) return;
        var s = document.createElement('style');
        s.id = 'mkipw-css';
        s.textContent = CSS;
        document.head.appendChild(s);
    }

    function el(tag, attrs, text) {
        var e = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) { e.setAttribute(k, attrs[k]); });
        if (text) e.textContent = text;
        return e;
    }

    function renderHints(list, meter, label, pw, username, rules) {
        var items = check(pw, username, rules);
        list.innerHTML = '';
        items.forEach(function (it) {
            list.appendChild(el('li', it.ok ? { 'class': 'ok' } : {}, it.text));
        });
        var s = strength(pw);
        var colors = ['#c33', '#e63', '#eb3', '#7c3', '#3b5'];
        meter.firstChild.style.width = pw ? (20 + s * 20) + '%' : '0';
        meter.firstChild.style.background = colors[s];
        label.textContent = pw ? T.strength[s] : '';
        return items.every(function (it) { return it.ok; });
    }

    // opts: { username, current, reason, standalone, relogin, container }. Resolves with the new password, or null when cancelled.
    // Step 1 asks for the account and its current password (skipped when the sign-in already gave them),
    // step 2 for the new password with the account's own rules.
    function openDialog(opts) {
        opts = opts || {};
        addCss();
        return new Promise(function (resolve) {
            var back = el('div', { 'class': 'mkipw-back' + (opts.standalone ? ' mkipw-standalone' : '') });
            var box = el('form', { 'class': 'mkipw-box', autocomplete: 'off' });
            back.appendChild(box);
            var username = opts.username || '';
            var current = opts.current;
            var rules = null;
            var step = 1;

            function close(value) {
                current = null;
                if (back.parentNode) back.parentNode.removeChild(back);
                resolve(value);
            }

            function head() {
                box.innerHTML = '';
                box.appendChild(el('h2', null, T.title));
                box.appendChild(el('p', null, opts.standalone ? T.standalone : (T[opts.reason] || T.admin)));
            }

            function field(labelText, type, value, autocomplete) {
                box.appendChild(el('label', null, labelText));
                var input = el('input', { type: type, autocomplete: autocomplete });
                if (value) input.value = value;
                box.appendChild(input);
                return input;
            }

            function footer(primaryText) {
                var msg = el('div', { 'class': 'mkipw-err' });
                box.appendChild(msg);
                var buttons = el('div', { 'class': 'mkipw-buttons' });
                var primary = el('button', { type: 'submit', 'class': 'mkipw-primary' }, primaryText);
                buttons.appendChild(primary);
                if (!opts.standalone) {
                    var cancel = el('button', { type: 'button' }, T.cancel);
                    cancel.addEventListener('click', function () { close(null); });
                    buttons.appendChild(cancel);
                }
                box.appendChild(buttons);
                return { msg: msg, primary: primary };
            }

            var ui;
            function showCredentials(error) {
                step = 1;
                head();
                var userIn = field(T.username, 'text', username, 'username');
                var curIn = field(T.current, 'password', '', 'current-password');
                ui = footer(T.next);
                ui.userIn = userIn;
                ui.curIn = curIn;
                if (error) ui.msg.textContent = error;
                (username ? curIn : userIn).focus();
            }

            function showNewPassword() {
                step = 2;
                head();
                var newIn = field(T.newPw, 'password', '', 'new-password');
                var meter = el('div', { 'class': 'mkipw-meter' });
                meter.appendChild(el('div'));
                box.appendChild(meter);
                var meterLabel = el('div', { 'class': 'mkipw-meter-label' });
                box.appendChild(meterLabel);
                if (check('', username, rules).length) box.appendChild(el('p', { 'class': 'mkipw-rules-title' }, T.rulesTitle));
                var hints = el('ul', { 'class': 'mkipw-hints' });
                box.appendChild(hints);
                var confIn = field(T.confirm, 'password', '', 'new-password');
                ui = footer(T.save);
                ui.newIn = newIn;
                ui.confIn = confIn;
                ui.update = function () { return renderHints(hints, meter, meterLabel, newIn.value, username, rules); };
                newIn.addEventListener('input', ui.update);
                ui.update();
                newIn.focus();
            }

            function loadRulesThen(error) {
                return changeRules(username, current).then(function (r) {
                    rules = r;
                    showNewPassword();
                }, function (e) {
                    current = null;
                    showCredentials(error || e.message);
                });
            }

            box.addEventListener('submit', function (e) {
                e.preventDefault();
                ui.msg.className = 'mkipw-err';
                ui.msg.textContent = '';
                if (step === 1) {
                    username = ui.userIn.value.trim();
                    current = ui.curIn.value;
                    ui.primary.disabled = true;
                    loadRulesThen();
                    return;
                }
                if (!ui.update()) {
                    ui.msg.textContent = T.rulesFail;
                    return;
                }
                if (ui.newIn.value !== ui.confIn.value) {
                    ui.msg.textContent = T.mismatch;
                    return;
                }
                ui.primary.disabled = true;
                var pw = ui.newIn.value;
                post('/MkiPasswordPolicy/Change', {
                    Username: username,
                    CurrentPassword: current,
                    NewPassword: pw
                }).then(jsonOrErrorEmpty).then(function () {
                    current = null;
                    if (opts.standalone || opts.relogin) {
                        head();
                        var done = el('div', { 'class': 'mkipw-ok' }, opts.standalone ? T.doneStandalone : T.doneRelogin);
                        box.appendChild(done);
                        if (opts.relogin) setTimeout(function () { close(pw); }, 2500);
                        return;
                    }
                    close(pw);
                }, function (err) {
                    ui.msg.textContent = err.message;
                    ui.primary.disabled = false;
                });
            });

            (opts.container || document.body).appendChild(back);
            if (username && current != null) {
                head();
                loadRulesThen();
            } else {
                showCredentials();
            }
        });
    }

    function jsonOrErrorEmpty(res) {
        if (res.status === 204) return null;
        return jsonOrError(res);
    }

    function parseBody(body) {
        try {
            return typeof body === 'string' ? JSON.parse(body) : null;
        } catch (e) {
            return null;
        }
    }

    var originalFetch = window.fetch.bind(window);

    // Sign-in hooks, only while the plugin is active (data-mode="full").
    function installSignInHooks() {
        // Sign-in hook for fetch: show the dialog, then repeat the sign-in with the new password so the user lands in the app.
        window.fetch = function (input, init) {
            var url = typeof input === 'string' ? input : (input && input.url) || '';
            var result = originalFetch(input, init);
            if (!AUTH_URL.test(url)) {
                return result;
            }
            return result.then(function (res) {
                if (res.status !== 401 || res.headers.get('X-Mki-Password-Change') !== 'required') {
                    return res;
                }
                var creds = parseBody(init && init.body) || {};
                return openDialog({
                    username: creds.Username,
                    current: creds.Username ? (creds.Pw || '') : null,
                    reason: res.headers.get('X-Mki-Password-Reason')
                }).then(function (pw) {
                    if (pw == null || !creds.Username) {
                        return res;
                    }
                    creds.Pw = pw;
                    return originalFetch(input, Object.assign({}, init, { body: JSON.stringify(creds) }));
                });
            });
        };

        // Sign-in hook for XMLHttpRequest (used by the newer SDK): the request cannot be repeated, so ask to sign in again.
        var xhrOpen = XMLHttpRequest.prototype.open;
        var xhrSend = XMLHttpRequest.prototype.send;
        XMLHttpRequest.prototype.open = function (method, url) {
            this.__mkipwAuth = AUTH_URL.test(String(url));
            return xhrOpen.apply(this, arguments);
        };
        XMLHttpRequest.prototype.send = function (body) {
            if (this.__mkipwAuth) {
                var xhr = this;
                xhr.addEventListener('load', function () {
                    if (xhr.status === 401 && xhr.getResponseHeader('X-Mki-Password-Change') === 'required') {
                        var creds = parseBody(body) || {};
                        openDialog({
                            username: creds.Username,
                            current: creds.Username ? (creds.Pw || '') : null,
                            reason: xhr.getResponseHeader('X-Mki-Password-Reason'),
                            relogin: true
                        });
                    }
                });
            }
            return xhrSend.apply(this, arguments);
        };
    }

    if (FULL) {
        installSignInHooks();
    }

    // Live hints under the password fields of the profile page and the "new user" page.
    var bypass = false;
    function targetUserId() {
        // Older web clients use ?userId=, Jellyfin 12 uses #/dashboard/users/<id>/<tab>.
        var m = /[?&]userId=([0-9a-f-]{32,36})/i.exec(location.href) || /\/users\/([0-9a-f-]{32,36})(?=[\/?#]|$)/i.exec(location.hash);
        return m ? m[1] : null;
    }

    function attachHints(input, form, getUsername, isNewUser) {
        if (input.__mkipw) return;
        input.__mkipw = true;
        var query = isNewUser ? { newUser: true } : (targetUserId() ? { userId: targetUserId() } : {});
        myRules(query).then(function (rules) {
            if (!rules || !rules.Active || !rules.WebHints) return;
            addCss();
            var wrap = el('div', { 'class': 'mkipw-inline' });
            var meter = el('div', { 'class': 'mkipw-meter' });
            meter.appendChild(el('div'));
            var label = el('div', { 'class': 'mkipw-meter-label' });
            var list = el('ul', { 'class': 'mkipw-hints' });
            wrap.appendChild(meter);
            wrap.appendChild(label);
            wrap.appendChild(list);
            var anchor = input.closest('.inputContainer') || input;
            anchor.parentNode.insertBefore(wrap, anchor.nextSibling);
            function update() { renderHints(list, meter, label, input.value, getUsername(), rules); }
            input.addEventListener('input', update);
            update();

            // Ask the server before Jellyfin saves, so a refusal shows the real reason.
            form.addEventListener('submit', function (e) {
                if (bypass || !input.value || !window.ApiClient) return;
                e.preventDefault();
                e.stopImmediatePropagation();
                var data = isNewUser
                    ? { NewUsername: getUsername(), Password: input.value }
                    : { UserId: targetUserId(), Password: input.value };
                window.ApiClient.ajax({
                    type: 'POST',
                    url: window.ApiClient.getUrl('MkiPasswordPolicy/Validate'),
                    data: JSON.stringify(data),
                    contentType: 'application/json',
                    dataType: 'json'
                }).then(function (r) {
                    if (r && r.Ok === false) {
                        list.scrollIntoView({ block: 'center' });
                        alert(r.Message || T.rulesFail);
                        return;
                    }
                    resubmit(form);
                }, function () { resubmit(form); });
            }, true);
        });
    }

    function resubmit(form) {
        bypass = true;
        try {
            if (form.requestSubmit) {
                form.requestSubmit();
            } else {
                form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
            }
        } finally {
            bypass = false;
        }
    }

    // Admin shortcut on a user's Password tab: what the plugin knows about this user, and a link to its row.
    var shortcutFor = null;
    function shortcut(form) {
        var m = /\/dashboard\/users\/([0-9a-f-]{32,36})\/password/i.exec(location.hash);
        if (!m || !window.ApiClient || document.querySelector('.mkipw-short[data-user="' + m[1] + '"]') || shortcutFor === m[1]) return;
        var id = m[1];
        shortcutFor = id;
        window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('MkiPasswordPolicy/Users'), dataType: 'json' }).then(function (d) {
            var norm = id.replace(/-/g, '').toLowerCase();
            var u = (d && d.Users || []).filter(function (x) { return String(x.Id).replace(/-/g, '').toLowerCase() === norm; })[0];
            if (!u || !document.body.contains(form) || document.querySelector('.mkipw-short[data-user="' + id + '"]')) return;
            addCss();
            var box = el('div', { 'class': 'mkipw-short', 'data-user': id });
            box.appendChild(el('h3', null, T.shortTitle));
            var table = el('table');
            function row(k, v) {
                var tr = el('tr');
                tr.appendChild(el('td', null, k));
                tr.appendChild(el('td', null, v));
                table.appendChild(tr);
            }
            var profil = u.IsExempt ? T.shortExempt
                : (T.profiles[u.EffectiveProfile] || u.EffectiveProfile) + (u.Profile === 'default' ? T.shortSameAsAll : '');
            row(T.shortProfile, profil);
            row(T.shortStatus, T.statuses[u.PasswordStatus] || u.PasswordStatus);
            row(T.shortRequest, !u.MustChange ? T.shortNone
                : u.Deadline && new Date(u.Deadline) > new Date() ? T.shortDue + new Date(u.Deadline).toLocaleString() : T.shortPending);
            box.appendChild(table);
            if (u.IsDisabled) box.appendChild(el('p', null, T.shortDisabled));
            box.appendChild(el('p', null, T.shortReset));
            var open = el('button', { type: 'button', 'class': 'raised emby-button' }, T.shortOpen);
            open.addEventListener('click', function () {
                location.hash = '#/configurationpage?name=' + encodeURIComponent(T.shortTitle) + '&user=' + id;
            });
            box.appendChild(open);
            form.parentNode.insertBefore(box, form.nextSibling);
            shortcutFor = null;
        }, function () { /* Not an administrator: no shortcut, and no retry for this user. */ });
    }

    function scan() {
        var pwForm = document.querySelector('form.updatePasswordForm');
        if (pwForm) {
            shortcut(pwForm);
        }
        if (!FULL) {
            return;
        }
        var pwInput = pwForm && pwForm.querySelector('#txtNewPassword');
        if (pwInput) {
            attachHints(pwInput, pwForm, function () { return ''; }, false);
        }
        var newForm = document.querySelector('form.newUserProfileForm');
        var newInput = newForm && newForm.querySelector('#txtPassword');
        if (newInput) {
            attachHints(newInput, newForm, function () {
                var u = newForm.querySelector('#txtUsername');
                return u ? u.value.trim() : '';
            }, true);
        }
    }

    window.MkiPasswordPolicy = { open: openDialog, check: check, prepare: prepare };

    function start() {
        var standalone = document.getElementById('mkipw-standalone');
        if (standalone) {
            openDialog({ standalone: true, container: standalone });
            return;
        }
        new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
        scan();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
