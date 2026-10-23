/* MKI Login Showcase: an animated showcase of library artwork behind the Jellyfin sign-in page.
   Only loaded while an administrator has the showcase turned on, and it asks the server again before it shows
   anything. The server hands out pictures by their position in the current pick: this script never learns item ids,
   paths or library names, and it gets titles and counts only when the administrator turned them on.
   The settings page draws its preview with the same code (MkiLoginShowcase.render). ES5 on purpose: the web client
   also runs on TV browsers. */
(function () {
    'use strict';
    if (window.MkiLoginShowcase) {
        return;
    }

    var script = document.currentScript;
    var BASE = script && script.src ? script.src.replace(/MkiLoginShowcase\/login\.js(?:[?#].*)?$/, '') : '';

    /* the neon colour pairs an administrator can pick */
    var ACCENTS = {
        Mki: ['#00E5FF', '#FF2BD6'],
        Jellyfin: ['#AA5CC3', '#00A4DC'],
        Sunset: ['#FFE45C', '#FF3D9E'],
        Lime: ['#B6FF3B', '#22D3EE']
    };
    var STYLES = { PosterWall: 1, Spotlight: 1, Horizon: 1, Mosaic: 1 };

    var CSS = [
        /* ---------- the drawing (also used by the settings page preview) ---------- */
        '.mkls{position:absolute;top:0;left:0;right:0;bottom:0;overflow:hidden;background:#05060B;color:#EEF1FF;pointer-events:none;',
        'opacity:0;transition:opacity .9s ease;font-family:inherit;-webkit-user-select:none;user-select:none}',
        '.mkls.is-ready{opacity:1}',
        '.mkls *{box-sizing:border-box}',
        '.mkls.is-still *,.mkls.is-still{animation:none!important;transition:none!important}',
        '.mkls.is-paused *{-webkit-animation-play-state:paused!important;animation-play-state:paused!important}',
        '.mkls-po{position:relative;overflow:hidden;background-color:#141726;background-size:cover;background-position:center;',
        'box-shadow:0 0 0 1px rgba(255,255,255,.06) inset}',
        '.mkls-vig{position:absolute;top:0;left:0;right:0;bottom:0;z-index:3}',

        /* poster wall */
        '.mkls-wall{position:absolute;display:flex;-webkit-transform:rotate(-12deg);transform:rotate(-12deg)}',
        '.mkls-colwrap{flex:none;overflow:visible}',
        '.mkls-col{will-change:transform;-webkit-animation:mkls-up 60s linear infinite;animation:mkls-up 60s linear infinite}',
        '.mkls-col.is-down{-webkit-animation-name:mkls-down;animation-name:mkls-down}',
        '@-webkit-keyframes mkls-up{from{-webkit-transform:translateY(0)}to{-webkit-transform:translateY(-50%)}}',
        '@keyframes mkls-up{from{transform:translateY(0)}to{transform:translateY(-50%)}}',
        '@-webkit-keyframes mkls-down{from{-webkit-transform:translateY(-50%)}to{-webkit-transform:translateY(0)}}',
        '@keyframes mkls-down{from{transform:translateY(-50%)}to{transform:translateY(0)}}',

        /* backdrop spotlight */
        '.mkls-bd{position:absolute;top:0;left:0;right:0;bottom:0;background-size:cover;background-position:center;opacity:0;',
        'transition:opacity 1.4s ease;will-change:opacity,transform}',
        '.mkls-bd.is-on{opacity:1;-webkit-animation:mkls-kb 10s linear forwards;animation:mkls-kb 10s linear forwards}',
        '.mkls.is-still .mkls-bd.is-on{-webkit-transform:scale(1.06);transform:scale(1.06)}',
        '@-webkit-keyframes mkls-kb{from{-webkit-transform:scale(1.04)}to{-webkit-transform:scale(1.16)}}',
        '@keyframes mkls-kb{from{transform:scale(1.04)}to{transform:scale(1.16)}}',
        '.mkls-cap{position:absolute;z-index:4;max-width:60%}',
        '.mkls-cap__k{display:block;text-transform:uppercase;letter-spacing:.16em;opacity:.7}',
        '.mkls-cap__t{display:block;font-weight:700;line-height:1.1;letter-spacing:-.01em;margin-top:.25em;text-shadow:0 0 1.2em rgba(0,0,0,.8);',
        'transition:opacity .6s ease,-webkit-transform .6s ease;transition:opacity .6s ease,transform .6s ease}',
        '.mkls-cap__t.is-out{opacity:0;-webkit-transform:translateY(.4em);transform:translateY(.4em)}',

        /* neon horizon */
        '.mkls-sun{position:absolute;border-radius:50%}',
        '.mkls-floor{position:absolute;overflow:hidden;-webkit-transform-origin:50% 0;transform-origin:50% 0;',
        '-webkit-mask-image:linear-gradient(#000 0,#000 30%,transparent 90%);mask-image:linear-gradient(#000 0,#000 30%,transparent 90%)}',
        '.mkls-grid{position:absolute;left:0;right:0;will-change:transform}',
        '.mkls-hz{position:absolute;left:0;right:0;height:2px;z-index:2}',
        '.mkls-belt{position:absolute;left:0;display:flex;z-index:2;will-change:transform;-webkit-animation:mkls-belt 50s linear infinite;animation:mkls-belt 50s linear infinite}',
        '.mkls-belt .mkls-po{flex:none}',
        '@-webkit-keyframes mkls-belt{from{-webkit-transform:translate(0,-100%)}to{-webkit-transform:translate(-50%,-100%)}}',
        '@keyframes mkls-belt{from{transform:translate(0,-100%)}to{transform:translate(-50%,-100%)}}',

        /* flip mosaic */
        '.mkls-tile{position:absolute;-webkit-transform-style:preserve-3d;transform-style:preserve-3d;',
        '-webkit-transition:-webkit-transform .9s cubic-bezier(.6,0,.3,1);transition:transform .9s cubic-bezier(.6,0,.3,1)}',
        '.mkls-tile.is-flipped{-webkit-transform:rotateY(180deg);transform:rotateY(180deg)}',
        '.mkls-tile .mkls-po{position:absolute;top:0;left:0;right:0;bottom:0;-webkit-backface-visibility:hidden;backface-visibility:hidden}',
        '.mkls-tile .mkls-po.is-back{-webkit-transform:rotateY(180deg);transform:rotateY(180deg)}',
        '.mkls-stat{position:absolute;z-index:4;letter-spacing:.08em;background:rgba(5,6,11,.75);border:1px solid rgba(255,255,255,.1);white-space:nowrap}',

        /* the sign-in card drawn by the settings page preview (the real page uses Jellyfin\'s own form) */
        '.mkls-card{position:absolute;z-index:5;display:flex;flex-direction:column;background:rgba(8,9,16,.74);',
        'box-shadow:0 0 0 1px rgba(255,255,255,.08),0 1.4em 3.4em rgba(0,0,0,.6)}',
        '.mkls-card.is-bare{background:transparent;box-shadow:none}',
        '.mkls-card__brand{display:flex;align-items:center;font-weight:700}',
        '.mkls-card__brand i{display:block;flex:none}',
        '.mkls-card__lbl{text-transform:uppercase;letter-spacing:.12em;opacity:.6}',
        '.mkls-card__field{display:flex;align-items:center;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.1);color:rgba(238,241,255,.5)}',
        '.mkls-card__btn{display:flex;align-items:center;justify-content:center;font-weight:700;color:#05060B}',
        '.mkls-card__qc{text-align:center;opacity:.6}',

        /* ---------- the real sign-in page while the showcase runs ---------- */
        '#mkiLoginShowcase{position:fixed;top:0;left:0;width:100%;height:100%;z-index:-1;pointer-events:none}',
        'html.mkils-on .backgroundContainer{background:transparent!important}',
        'html.mkils-on .backdropContainer{display:none!important}',
        'html.mkils-card #loginPage>div{position:relative;box-sizing:border-box;width:27em;max-width:calc(100% - 2em);margin:auto;',
        'padding:1.5em 1.7em 1.3em!important;border-radius:16px;background:rgba(8,9,16,.74);',
        '-webkit-backdrop-filter:blur(14px);backdrop-filter:blur(14px);box-shadow:0 0 0 1px rgba(255,255,255,.08),0 30px 80px rgba(0,0,0,.6)}',
        '@supports ((-webkit-mask-composite:xor) or (mask-composite:exclude)){',
        'html.mkils-card #loginPage>div::after{content:"";position:absolute;top:-1px;left:-1px;right:-1px;bottom:-1px;border-radius:inherit;padding:1px;pointer-events:none;',
        'background:linear-gradient(135deg,var(--mkils-a1),transparent 40%,transparent 60%,var(--mkils-a2));',
        '-webkit-mask:linear-gradient(#000 0 0) content-box,linear-gradient(#000 0 0);-webkit-mask-composite:xor;',
        'mask:linear-gradient(#000 0 0) content-box,linear-gradient(#000 0 0);mask-composite:exclude}}',
        'html.mkils-card #loginPage .manualLoginForm,html.mkils-card #loginPage .readOnlyContent{width:auto;max-width:none}',
        'html.mkils-card #loginPage .sectionTitle,html.mkils-card #loginPage .visualLoginForm h1{font-weight:700;letter-spacing:-.01em}',
        'html.mkils-card #loginPage .emby-input{background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.14);border-radius:8px}',
        'html.mkils-card #loginPage .emby-input:focus{border-color:var(--mkils-a1)}',
        'html.mkils-card #loginPage .button-submit{background:linear-gradient(90deg,var(--mkils-a1),var(--mkils-a2));color:#05060B;border-radius:8px;',
        'font-weight:700;box-shadow:0 0 22px var(--mkils-glow)}',
        'html.mkils-card #loginPage .button-submit:focus-visible,html.mkils-card #loginPage .button-submit.show-focus{outline:2px solid #fff;outline-offset:2px}',
        'html.mkils-card #loginPage .readOnlyContent .raised,html.mkils-card #loginPage .btnCancel{background:rgba(255,255,255,.08);border-radius:8px}',
        'html.mkils-card #loginPage .readOnlyContent{margin-bottom:0!important}',
        'html.mkils-card #loginPage .loginDisclaimerContainer{margin-top:1em}',
        /* user tiles: Jellyfin sizes them for the whole screen width, which is far too small inside the card */
        '@media (min-width:40em){html.mkils-card #loginPage .visualLoginForm .squareCard-scalable{width:33.333%}}',
        /* where the card sits: next to the picture, not on it */
        '@media (min-width:60em) and (min-aspect-ratio:5/4){',
        'html.mkils-card.mkils-spotlight #loginPage>div{margin-left:auto;margin-right:6vw}',
        'html.mkils-card.mkils-horizon #loginPage>div{margin-top:max(1em,calc(34vh - 108px));margin-bottom:auto;padding-top:1.1em!important;padding-bottom:1em!important}',
        'html.mkils-card.mkils-horizon #loginPage .inputContainer{margin-bottom:1em}',
        'html.mkils-card.mkils-horizon #loginPage .checkboxContainer{margin-bottom:1em}',
        'html.mkils-card.mkils-horizon #loginPage .sectionTitle,html.mkils-card.mkils-horizon #loginPage h1{margin-top:0;margin-bottom:.6em}',
        'html.mkils-card.mkils-mosaic #loginPage>div{margin-left:5vw;margin-right:auto;background:transparent;box-shadow:none;',
        '-webkit-backdrop-filter:none;backdrop-filter:none}',
        'html.mkils-card.mkils-mosaic #loginPage>div::after{display:none}',
        'html.mkils-card.mkils-mosaic #loginPage .emby-input{background:rgba(10,11,19,.92)}',
        'html.mkils-card.mkils-mosaic #loginPage .readOnlyContent .raised,html.mkils-card.mkils-mosaic #loginPage .btnCancel{background:rgba(26,28,40,.92)}}'
    ].join('');

    function injectCss() {
        if (document.getElementById('mkiLoginShowcaseCss')) return;
        var s = document.createElement('style');
        s.id = 'mkiLoginShowcaseCss';
        s.textContent = CSS;
        (document.head || document.documentElement).appendChild(s);
    }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }
    function px(n) { return Math.round(n * 10) / 10 + 'px'; }
    function rgb(hex) { var h = parseInt(hex.slice(1), 16); return ((h >> 16) & 255) + ',' + ((h >> 8) & 255) + ',' + (h & 255); }
    function cssUrl(u) { return 'url("' + String(u).replace(/["\\\n\r]/g, '') + '")'; }
    function num(n) { try { return Number(n).toLocaleString('en-GB'); } catch (e) { return String(n); } }
    function reducedMotion() { try { return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches); } catch (e) { return false; } }

    /* ---------- the drawing: render(host, data, opts) -> { stop() } ----------
       data: { Style, Accent, Motion, Items: [{ P, B, T }], Caption, Movies, Series }
       opts: { image(index, 'p'|'b') -> url, mockCard: true to draw a sign-in card (preview) } */
    function render(host, data, opts) {
        injectCss();
        opts = opts || {};
        var style = STYLES[data.Style] ? data.Style : 'PosterWall';
        var accent = ACCENTS[data.Accent] || ACCENTS.Mki;
        var a1 = accent[0], a2 = accent[1], r1 = rgb(a1), r2 = rgb(a2);
        var still = data.Motion === 'Still' || reducedMotion();
        var slow = data.Motion === 'Calm' ? 2 : 1;
        var items = (data.Items || []).slice(0, 200);
        var posters = [], backdrops = [];
        items.forEach(function (it, i) { if (it && it.P) posters.push(i); if (it && it.B) backdrops.push(i); });
        if (style === 'Spotlight' && !backdrops.length) style = 'PosterWall';
        if (style !== 'Spotlight' && !posters.length) style = backdrops.length ? 'Spotlight' : null;

        var stage = el('div', 'mkls mkls--' + (style || 'none').toLowerCase());
        stage.setAttribute('aria-hidden', 'true');
        if (still) stage.className += ' is-still';
        host.appendChild(stage);
        var timers = [], stopped = false, keyframes = null, preload = [];
        var W = 0, H = 0, resizeTimer = 0, flipTimer = 0, spotTimer = 0;

        function placeholder(i) {
            return 'linear-gradient(' + (120 + (i * 37) % 120) + 'deg,rgba(' + (i % 2 ? r1 : r2) + ',.28),rgba(' + (i % 2 ? r2 : r1) + ',.08))';
        }
        function poster(i, w, h) {
            var p = el('div', 'mkls-po');
            p.style.width = px(w); p.style.height = px(h);
            p.style.borderRadius = px(Math.max(3, w * 0.035));
            p.style.backgroundImage = cssUrl(opts.image(i, 'p')) + ',' + placeholder(i);
            return p;
        }
        function want(url) { if (preload.length < 10) { var im = new Image(); im.src = url; preload.push(im); } }
        function later(fn, ms) { var t = setTimeout(fn, ms); timers.push(t); return t; }
        function pickPosters(n, offset) { var out = []; for (var k = 0; k < n; k++) out.push(posters[(offset + k) % posters.length]); return out; }

        function measure() {
            var r = host.getBoundingClientRect();
            W = Math.max(1, r.width); H = Math.max(1, r.height);
        }

        /* ---------- A: poster wall: tilted columns drifting up and down ---------- */
        function wall() {
            var portrait = H > W * 1.1, cols = portrait ? 5 : W < 900 ? 7 : 9;
            var wallW = W * 1.3, wallH = H * 1.6, gap = Math.max(6, Math.min(W, H * 1.78) * 0.012);
            var colW = (wallW - gap * (cols - 1)) / cols, ph = colW * 1.5;
            var per = Math.ceil(wallH / (ph + gap)) + 1;
            var w = el('div', 'mkls-wall');
            w.style.left = px(-W * 0.15); w.style.top = px(-H * 0.3); w.style.width = px(wallW); w.style.height = px(wallH);
            for (var c = 0; c < cols; c++) {
                var wrap = el('div', 'mkls-colwrap'), col = el('div', 'mkls-col' + (c % 2 ? ' is-down' : ''));
                wrap.style.width = px(colW); if (c) wrap.style.marginLeft = px(gap);
                col.style.animationDuration = col.style.webkitAnimationDuration = ((50 + (c % 4) * 12) * slow) + 's';
                var set = pickPosters(per, c * per);
                set.concat(set).forEach(function (i) { var p = poster(i, colW, ph); p.style.marginBottom = px(gap); col.appendChild(p); });
                set.slice(0, 2).forEach(function (i) { want(opts.image(i, 'p')); });
                wrap.appendChild(col); w.appendChild(wrap);
            }
            stage.appendChild(w);
            vignette('radial-gradient(ellipse at 50% 50%,rgba(5,6,11,.62),rgba(5,6,11,.36) 45%,rgba(5,6,11,.82) 85%)');
            card('center');
        }

        /* ---------- B: backdrop spotlight: one slowly zooming backdrop at a time, crossfading ---------- */
        function spotlight() {
            var portrait = H > W * 1.1, layers = [el('div', 'mkls-bd'), el('div', 'mkls-bd')], at = 0, shown = 0;
            layers.forEach(function (l) { stage.appendChild(l); });
            vignette(portrait
                ? 'linear-gradient(rgba(5,6,11,.3),rgba(5,6,11,.55) 50%,rgba(5,6,11,.9))'
                : 'linear-gradient(90deg,rgba(5,6,11,.15),rgba(5,6,11,.35) 45%,rgba(5,6,11,.9) 80%),linear-gradient(transparent 55%,rgba(5,6,11,.85))');
            var cap = null, capT = null, unit = Math.min(W, H * 1.78) / 100;
            if (data.Caption || items.some(function (it) { return it && it.T; })) {
                cap = el('div', 'mkls-cap');
                cap.style.left = px(unit * 5); cap.style.bottom = px(unit * 5);
                var k = el('span', 'mkls-cap__k', data.Caption || '');
                k.style.fontSize = px(Math.max(10, unit * 1));
                capT = el('span', 'mkls-cap__t');
                capT.style.fontSize = px(Math.max(18, unit * 3.2));
                cap.appendChild(k); cap.appendChild(capT); stage.appendChild(cap);
            }
            var every = 8000 * slow;
            function showAt(n) {
                var i = backdrops[n % backdrops.length], layer = layers[shown % 2], other = layers[(shown + 1) % 2];
                var url = opts.image(i, 'b'), im = new Image(), done = false;
                function go() {
                    if (done || stopped) return; done = true;
                    layer.style.backgroundImage = cssUrl(url) + ',' + placeholder(i);
                    layer.classList.remove('is-on'); void layer.offsetWidth; layer.classList.add('is-on');
                    layer.style.zIndex = '2'; other.style.zIndex = '1';
                    other.classList.remove('is-on');
                    if (capT) {
                        capT.classList.add('is-out');
                        later(function () { capT.textContent = (items[i] && items[i].T) || ''; capT.classList.remove('is-out'); }, still ? 0 : 450);
                    }
                    shown++;
                    ready();
                }
                im.onload = go; im.onerror = go; im.src = url;
                later(go, 2500);
            }
            showAt(at);
            if (!still && backdrops.length > 1) {
                spotTimer = setInterval(function () { if (!document.hidden) showAt(++at); }, every);
            }
            card('right');
        }

        /* ---------- C: neon horizon: the MKI grid floor, posters gliding along the horizon ---------- */
        function horizon() {
            var portrait = H > W * 1.1, hz = portrait ? 0.9 : 0.34, unit = Math.min(W, H * 1.78) / 100;
            stage.style.background = 'linear-gradient(#070814 0%,#14072a ' + (hz * 100) + '%,#05060B ' + (hz * 100 + 0.2) + '%)';
            var sun = el('div', 'mkls-sun'), d = unit * 34;
            sun.style.width = px(d); sun.style.height = px(d); sun.style.left = px(W / 2 - d / 2); sun.style.top = px(H * hz - d * 0.62);
            sun.style.background = 'radial-gradient(circle,rgba(' + r2 + ',.45),transparent 65%)';
            stage.appendChild(sun);
            var floor = el('div', 'mkls-floor'), grid = el('div', 'mkls-grid'), cell = Math.max(18, unit * 4);
            floor.style.left = px(-W * 0.5); floor.style.width = px(W * 2); floor.style.top = px(H * hz); floor.style.height = px(H * 0.8);
            floor.style.transform = floor.style.webkitTransform = 'perspective(' + px(unit * 30) + ') rotateX(62deg)';
            grid.style.top = px(-cell); grid.style.height = px(H * 0.8 + cell * 2);
            grid.style.backgroundImage = 'linear-gradient(rgba(' + r1 + ',.7) 2px,transparent 2px),linear-gradient(90deg,rgba(' + r2 + ',.6) 2px,transparent 2px)';
            grid.style.backgroundSize = px(cell) + ' ' + px(cell);
            keyframes = el('style');
            var name = 'mkls-fl' + Math.floor(Math.random() * 1e9);
            keyframes.textContent = '@-webkit-keyframes ' + name + '{from{-webkit-transform:translateY(0)}to{-webkit-transform:translateY(' + px(cell) + ')}}' +
                '@keyframes ' + name + '{from{transform:translateY(0)}to{transform:translateY(' + px(cell) + ')}}';
            document.head.appendChild(keyframes);
            grid.style.animation = grid.style.webkitAnimation = name + ' ' + (2 * slow) + 's linear infinite';
            floor.appendChild(grid); stage.appendChild(floor);
            var line = el('div', 'mkls-hz');
            line.style.top = px(H * hz - 1);
            line.style.background = 'linear-gradient(90deg,transparent,' + a1 + ',' + a2 + ',transparent)';
            line.style.boxShadow = '0 0 ' + px(unit * 1.5) + ' ' + a2;
            var pw = portrait ? W * 0.19 : unit * 9, gap = unit * 1.4, belt = el('div', 'mkls-belt');
            var n = Math.ceil(W / (pw + gap)) + 2, set = pickPosters(n, 0);
            belt.style.top = px(H * hz); belt.style.paddingBottom = px(unit * 1.2);
            belt.style.animationDuration = belt.style.webkitAnimationDuration = (50 * slow * Math.max(1, n / 13)) + 's';
            set.concat(set).forEach(function (i) {
                var p = poster(i, pw, pw * 1.5);
                p.style.marginRight = px(gap);
                p.style.webkitBoxReflect = 'below ' + px(unit * 1.2) + ' linear-gradient(transparent 55%,rgba(255,255,255,.25))';
                belt.appendChild(p);
            });
            set.slice(0, 6).forEach(function (i) { want(opts.image(i, 'p')); });
            stage.appendChild(belt); stage.appendChild(line);
            card('below');
        }

        /* ---------- D: flip mosaic: a poster grid where single tiles flip to new titles ---------- */
        function mosaic() {
            var portrait = H > W * 1.1, cols = portrait ? 4 : W < 900 ? 7 : 10, gap = Math.max(3, Math.min(W, H * 1.78) * 0.005);
            var tw = (W - gap * (cols + 1)) / cols, th = tw * 1.5, rows = Math.ceil((H - gap) / (th + gap));
            var box = el('div', 'mkls-mo'), tiles = [], next = 0;
            box.style.position = 'absolute'; box.style.top = '0'; box.style.left = '0'; box.style.right = '0'; box.style.bottom = '0';
            box.style.perspective = box.style.webkitPerspective = px(W * 0.6);
            for (var r = 0; r < rows; r++) {
                for (var c = 0; c < cols; c++) {
                    var t = el('div', 'mkls-tile');
                    t.style.left = px(gap + c * (tw + gap)); t.style.top = px(gap + r * (th + gap));
                    t.style.width = px(tw); t.style.height = px(th);
                    var i = posters[next++ % posters.length], front = poster(i, tw, th), back = poster(posters[next % posters.length], tw, th);
                    back.className += ' is-back';
                    t.appendChild(front); t.appendChild(back); box.appendChild(t);
                    tiles.push({ el: t, c: c });
                    if (tiles.length <= 8) want(opts.image(i, 'p'));
                }
            }
            stage.appendChild(box);
            vignette(portrait
                ? 'linear-gradient(rgba(5,6,11,.5),rgba(5,6,11,.62) 50%,rgba(5,6,11,.9))'
                : 'linear-gradient(90deg,rgba(5,6,11,.96) 0,rgba(5,6,11,.9) 38%,rgba(5,6,11,.2) 62%)');
            if (data.Movies != null || data.Series != null) {
                var unit = Math.min(W, H * 1.78) / 100, s = el('div', 'mkls-stat');
                s.style.right = px(unit * 3); s.style.bottom = px(unit * 3); s.style.fontSize = px(Math.max(10, unit * 1));
                s.style.padding = px(unit * 0.6) + ' ' + px(unit * 1); s.style.borderRadius = px(unit * 0.5);
                var parts = [];
                if (data.Movies != null) parts.push([num(data.Movies), data.Movies === 1 ? ' movie' : ' movies']);
                if (data.Series != null) parts.push([num(data.Series), data.Series === 1 ? ' show' : ' shows']);
                parts.forEach(function (p, k) {
                    if (k) s.appendChild(document.createTextNode(' · '));
                    var em = el('em', null, p[0]); em.style.fontStyle = 'normal'; em.style.color = a1;
                    s.appendChild(em); s.appendChild(document.createTextNode(p[1]));
                });
                stage.appendChild(s);
            }
            if (!still && posters.length > 1) {
                var from = portrait ? 0 : Math.floor(cols * 0.4), last = null;
                var pool = tiles.filter(function (x) { return x.c >= from; });
                flipTimer = setInterval(function () {
                    if (document.hidden || !pool.length) return;
                    var t = pool[Math.floor(Math.random() * pool.length)];
                    if (t === last && pool.length > 1) return;
                    last = t;
                    var flipped = t.el.classList.contains('is-flipped'), hidden = t.el.children[flipped ? 0 : 1];
                    var i = posters[next++ % posters.length];
                    hidden.style.backgroundImage = cssUrl(opts.image(i, 'p')) + ',' + placeholder(i);
                    t.el.classList.toggle('is-flipped');
                }, 900 * slow);
            }
            card('left');
        }

        function vignette(bg) { var v = el('div', 'mkls-vig'); v.style.background = bg; stage.appendChild(v); }

        /* the preview's stand-in for Jellyfin's sign-in form: where the real one sits, in the neon card or as Jellyfin
           draws it (data.Card false, always in the middle) */
        function card(where) {
            if (!opts.mockCard) return;
            var plain = data.Card === false, portrait = H > W * 1.1, unit = Math.min(W, H * 1.78) / 100;
            if (portrait || plain) where = 'center';
            var top = where === 'below';
            var c = el('div', 'mkls-card' + (plain || where === 'left' ? ' is-bare' : ''));
            var w = portrait ? W * 0.8 : Math.max(150, unit * 27);
            c.style.width = px(w); c.style.padding = px(unit * (top ? 1.6 : 2.2)) + ' ' + px(unit * 2.2);
            c.style.borderRadius = px(unit * 1.2);
            if (!plain && where !== 'left') {
                c.style.border = '1px solid transparent';
                c.style.background = 'linear-gradient(rgba(8,9,16,.8),rgba(8,9,16,.8)) padding-box,linear-gradient(135deg,' + a1 + ',rgba(255,255,255,.08) 40%,rgba(255,255,255,.08) 60%,' + a2 + ') border-box';
            }
            if (where === 'left') c.style.paddingLeft = '0';
            var line = Math.max(14, unit * 2.8), gapv = unit * (top ? 0.7 : 1);
            var title = el('div', 'mkls-card__brand', 'Please sign in');
            title.style.fontSize = px(Math.max(11, unit * 1.9)); title.style.justifyContent = 'center';
            c.appendChild(title);
            function field(label, text) {
                var l = el('div', 'mkls-card__lbl', label); l.style.fontSize = px(Math.max(7, unit * 0.85)); l.style.marginTop = px(gapv); c.appendChild(l);
                var f = el('div', 'mkls-card__field', text);
                f.style.height = px(line); f.style.marginTop = px(unit * 0.4); f.style.borderRadius = px(unit * 0.6);
                f.style.paddingLeft = px(unit * 1); f.style.fontSize = px(Math.max(8, unit * 1.1));
                c.appendChild(f);
            }
            field('User', 'Name');
            field('Password', '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022');
            var b = el('div', 'mkls-card__btn', 'Sign In');
            b.style.height = px(line * 1.1); b.style.marginTop = px(gapv * 1.5); b.style.borderRadius = px(unit * 0.6);
            b.style.fontSize = px(Math.max(8, unit * 1.2));
            if (plain) { b.style.background = '#00A4DC'; b.style.color = '#fff'; }
            else { b.style.background = 'linear-gradient(90deg,' + a1 + ',' + a2 + ')'; b.style.boxShadow = '0 0 ' + px(unit * 2) + ' rgba(' + r1 + ',.4)'; }
            c.appendChild(b);
            var q = el('div', 'mkls-card__qc', 'Use Quick Connect'); q.style.fontSize = px(Math.max(7, unit * 1)); q.style.marginTop = px(gapv); c.appendChild(q);
            if (where === 'center') { c.style.left = px((W - w) / 2); c.style.top = '50%'; c.style.transform = 'translateY(-50%)'; }
            else if (where === 'right') { c.style.right = px(unit * 6); c.style.top = '50%'; c.style.transform = 'translateY(-50%)'; }
            else if (where === 'left') { c.style.left = px(unit * 5); c.style.top = '50%'; c.style.transform = 'translateY(-50%)'; }
            else { c.style.left = px((W - w) / 2); c.style.top = px(H * 0.34 + unit * 1.2); }
            stage.appendChild(c);
        }

        var isReady = false;
        function ready() { if (isReady || stopped) return; isReady = true; stage.classList.add('is-ready'); }

        function draw() {
            clearInterval(flipTimer); clearInterval(spotTimer); flipTimer = spotTimer = 0;
            timers.forEach(clearTimeout); timers = [];
            if (keyframes && keyframes.parentNode) keyframes.parentNode.removeChild(keyframes);
            keyframes = null;
            stage.textContent = '';
            stage.style.background = '';
            measure();
            if (!style) return;
            if (style === 'Spotlight') spotlight();
            else if (style === 'Horizon') horizon();
            else if (style === 'Mosaic') mosaic();
            else wall();
            if (style !== 'Spotlight') {
                /* fade in once the first pictures are there, or after a moment anyway */
                var left = preload.length;
                if (!left) ready();
                preload.forEach(function (im) {
                    function one() { if (--left <= 0) ready(); }
                    if (im.complete) one(); else { im.onload = one; im.onerror = one; }
                });
                later(ready, 1800);
            }
        }

        function onResize() {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                if (stopped) return;
                var r = host.getBoundingClientRect();
                if (Math.abs(r.width - W) > 2 || Math.abs(r.height - H) > 2) draw();
            }, 250);
        }
        function onVisibility() { stage.classList.toggle('is-paused', !!document.hidden); }

        draw();
        window.addEventListener('resize', onResize);
        document.addEventListener('visibilitychange', onVisibility);
        var ro = null;
        if (window.ResizeObserver) { ro = new ResizeObserver(onResize); ro.observe(host); }

        return {
            stop: function () {
                stopped = true;
                clearInterval(flipTimer); clearInterval(spotTimer); clearTimeout(resizeTimer);
                timers.forEach(clearTimeout);
                window.removeEventListener('resize', onResize);
                document.removeEventListener('visibilitychange', onVisibility);
                if (ro) ro.disconnect();
                if (keyframes && keyframes.parentNode) keyframes.parentNode.removeChild(keyframes);
                if (stage.parentNode) stage.parentNode.removeChild(stage);
            },
            style: style
        };
    }

    /* ---------- the sign-in page ---------- */
    var state = { data: null, at: 0, loading: null, ctrl: null, layer: null, refresh: 0, seq: 0 };

    function sameServer() {
        /* the web client can sign in to another server; only this server's sign-in page gets its showcase */
        var a = window.ApiClient;
        if (!a || !a.serverAddress || !BASE) return true;
        try {
            var mine = new URL(BASE, location.href), theirs = new URL(String(a.serverAddress() || '') + '/', location.href);
            return mine.origin === theirs.origin && mine.pathname.replace(/\/+$/, '') === theirs.pathname.replace(/\/+$/, '');
        } catch (e) {
            return true;
        }
    }

    function loginPage() {
        var p = document.getElementById('loginPage');
        return p && !p.classList.contains('hide') && p.offsetWidth > 0 ? p : null;
    }

    function load(force) {
        if (!force && state.data && Date.now() - state.at < 60000) return Promise.resolve(state.data);
        if (state.loading) return state.loading;
        state.loading = fetch(BASE + 'MkiLoginShowcase/Showcase', { credentials: 'omit', cache: 'no-store' }).then(function (r) {
            return r.ok ? r.json() : { On: false };
        }).then(function (d) {
            state.loading = null; state.data = d || { On: false }; state.at = Date.now();
            return state.data;
        }, function () {
            state.loading = null; state.data = { On: false }; state.at = Date.now();
            return state.data;
        });
        return state.loading;
    }

    function show(data) {
        hide();
        var root = document.documentElement, layer = el('div');
        layer.id = 'mkiLoginShowcase';
        layer.setAttribute('aria-hidden', 'true');
        document.body.insertBefore(layer, document.body.firstChild);
        var set = encodeURIComponent(String(data.Set || ''));
        var ctrl = render(layer, data, { image: function (i, kind) { return BASE + 'MkiLoginShowcase/Image/' + set + '/' + i + '/' + kind; } });
        if (!ctrl.style) { ctrl.stop(); layer.parentNode.removeChild(layer); return; }
        var accent = ACCENTS[data.Accent] || ACCENTS.Mki;
        root.style.setProperty('--mkils-a1', accent[0]);
        root.style.setProperty('--mkils-a2', accent[1]);
        root.style.setProperty('--mkils-glow', 'rgba(' + rgb(accent[0]) + ',.4)');
        root.classList.add('mkils-on', 'mkils-' + ctrl.style.toLowerCase());
        if (data.Card !== false) root.classList.add('mkils-card');
        state.ctrl = ctrl; state.layer = layer;
        /* a sign-in page left open (a TV in the living room) picks up the hourly new titles */
        clearTimeout(state.refresh);
        state.refresh = setTimeout(function () { if (loginPage()) load(true).then(function (d) { if (loginPage() && d && d.On) show(d); }); }, 30 * 60000);
    }

    function hide() {
        clearTimeout(state.refresh);
        if (state.ctrl) state.ctrl.stop();
        if (state.layer && state.layer.parentNode) state.layer.parentNode.removeChild(state.layer);
        state.ctrl = null; state.layer = null;
        var root = document.documentElement;
        ['mkils-on', 'mkils-card', 'mkils-posterwall', 'mkils-spotlight', 'mkils-horizon', 'mkils-mosaic'].forEach(function (c) { root.classList.remove(c); });
        ['--mkils-a1', '--mkils-a2', '--mkils-glow'].forEach(function (p) { root.style.removeProperty(p); });
    }

    function check() {
        var page = loginPage();
        if (page && !state.ctrl) {
            var seq = ++state.seq;
            if (!sameServer()) return;
            load(false).then(function (d) {
                if (seq !== state.seq || state.ctrl || !loginPage()) return;
                if (d && d.On && d.Items && d.Items.length) show(d);
            });
        } else if (!page && state.ctrl) {
            state.seq++;
            hide();
        }
    }

    window.MkiLoginShowcase = { render: render, accents: ACCENTS, version: 1 };

    function start() {
        injectCss();
        ['viewshow', 'pageshow', 'viewhide'].forEach(function (t) { document.addEventListener(t, function () { setTimeout(check, 0); }, true); });
        window.addEventListener('hashchange', function () { setTimeout(check, 50); });
        window.addEventListener('popstate', function () { setTimeout(check, 50); });
        /* the sign-in page can also appear without a navigation event (sign-out, server switch) */
        setInterval(check, 1000);
        check();
    }

    if (!BASE) {
        /* no address to ask (the script was not loaded from a server): drawing only */
        return;
    }
    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start);
})();
