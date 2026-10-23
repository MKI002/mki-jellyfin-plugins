/* MKI Screen Fit: tells the server how large this device's screen is, so it can play the Full HD version of a movie
   on a Full HD screen and keep transcodes no larger than the screen. Sends two numbers for the signed-in user's own
   device and nothing else; it shows nothing and changes nothing in the web client. Only loaded while an
   administrator has a part of the plugin turned on. */
(function () {
    'use strict';
    if (window.MkiScreenFit) {
        return;
    }
    window.MkiScreenFit = true;

    var RESEND = 6 * 3600 * 1000;
    var state = { key: '', at: 0, busy: false, timer: 0 };

    function api() { return window.ApiClient; }

    /* TV browsers and TV app shells draw their interface at 1080p even on a 4K panel, so their numbers say
       nothing about the panel. They are reported as unknown and the administrator decides per device. */
    function onTv() {
        return /Web0S|webOS|Tizen|SMART-TV|SmartTV|HbbTV|NetCast|VIDAA|AFT[A-Z0-9]|CrKey|BRAVIA|Android TV|GoogleTV/i.test(navigator.userAgent || '');
    }

    function measure() {
        var s = window.screen, dpr = window.devicePixelRatio || 1;
        if (!s || !(s.width > 0) || !(s.height > 0)) return null;
        var w = Math.round(s.width * dpr), h = Math.round(s.height * dpr);
        /* Firefox's fingerprinting protection reports the window as the screen, at a pixel ratio of 1. A real
           screen matches that only in a full-screen window, where the app shells (desktop, Android, iOS) are
           trusted and a plain browser is not. */
        var hidden = dpr === 1 && s.width === window.innerWidth && s.height === window.innerHeight &&
            window.outerWidth === window.innerWidth && !window.NativeShell;
        return { Width: Math.max(w, h), Height: Math.min(w, h), Reliable: !hidden && !onTv() };
    }

    function send(force) {
        var a = api();
        if (state.busy || !a || !a.accessToken || !a.accessToken() || !a.getCurrentUserId || !a.getCurrentUserId()) return;
        var m = measure();
        if (!m) return;
        var key = [a.serverId && a.serverId(), a.getCurrentUserId(), a.deviceId && a.deviceId(), m.Width, m.Height, m.Reliable].join('|');
        if (!force && key === state.key && Date.now() - state.at < RESEND) return;
        state.busy = true;
        a.ajax({ type: 'POST', url: a.getUrl('MkiScreenFit/Screen'), data: JSON.stringify(m), contentType: 'application/json' }).then(function () {
            state.key = key; state.at = Date.now(); state.busy = false;
        }, function () {
            /* tried again at the next check, never in a loop */
            state.key = key; state.at = Date.now() - RESEND + 5 * 60 * 1000; state.busy = false;
        });
    }

    function later() {
        clearTimeout(state.timer);
        state.timer = setTimeout(function () { send(false); }, 1500);
    }

    /* a moved window, a new monitor, a sign-in or a returning tab: report again when the numbers changed */
    window.addEventListener('resize', later);
    document.addEventListener('visibilitychange', function () { if (!document.hidden) later(); });
    document.addEventListener('viewshow', later);
    setInterval(function () { send(false); }, 60000);
    later();
})();
