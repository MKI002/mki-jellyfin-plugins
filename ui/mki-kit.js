/* MKI kit runtime: shared behaviour of the MKI plugin settings pages ("Control Deck").
   Source of truth: ui/mki-kit.js. build/kit.py copies it into every configPage.html between the MKI-KIT markers,
   inside the page's own script, so nothing here is global and each page gets its own instance.

   Usage (inside the page script):
     var kit = MkiKit(page, { pluginId: '...', onLoad: fn(cfg), onSaved: fn(batch), confirm: { Key: fn(to, from) -> Promise<bool> } });

   Rules this runtime keeps:
   - Everything is looked up inside `page`. Listeners are bound once, when the page script runs; pageshow only loads data.
   - Settings save themselves: switches and pickers 350 ms after the last change (one batch), text when the field is left.
     Only the changed keys are written, on top of a fresh copy of the server's configuration, one save at a time.
   - A value that fails validation is never saved. A failed save keeps the value on screen and offers Retry.
   - Server text only ever reaches the page through textContent or attributes. ES5 on purpose. */
function MkiKit(page, o) {
  'use strict';
  o = o || {};
  var kit = { page: page };
  var EN = 'en-GB';
  var disposers = [];

  /* ---------- small helpers ---------- */
  function $(s, root) { return (root || page).querySelector(s); }
  function $$(s, root) { return Array.prototype.slice.call((root || page).querySelectorAll(s)); }
  function byId(id) { return id ? page.querySelector('[id="' + String(id).replace(/"/g, '') + '"]') : null; }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function ic(name, cls) { var s = el('span', 'material-icons mk-ic' + (cls ? ' ' + cls : ''), name); s.setAttribute('aria-hidden', 'true'); return s; }
  function fill(node, parts) {
    node.textContent = '';
    (Array.isArray(parts) ? parts : [parts]).forEach(function (p) {
      if (p == null || p === false) return;
      node.appendChild(typeof p === 'string' || typeof p === 'number' ? document.createTextNode(String(p)) : p);
    });
    return node;
  }
  function btn(text, cls, icon) { var b = el('button', 'mk-btn' + (cls ? ' ' + cls : '')); b.type = 'button'; if (icon) b.appendChild(ic(icon)); b.appendChild(el('span', null, text)); return b; }
  function count(obj) { return Object.keys(obj).length; }
  function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }
  function listen(target, type, fn, opt) { target.addEventListener(type, fn, opt); disposers.push(function () { target.removeEventListener(type, fn, opt); }); }
  function hhmm(d) { return d.toLocaleTimeString(EN, { hour: '2-digit', minute: '2-digit' }); }
  function day(d) { return d.toLocaleDateString(EN, { weekday: 'short', day: 'numeric', month: 'short' }); }
  function dayTime(d) {
    var now = new Date(), y = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
    if (d.toDateString() === now.toDateString()) return 'today ' + hhmm(d);
    if (d.toDateString() === y.toDateString()) return 'yesterday ' + hhmm(d);
    return day(d) + ', ' + hhmm(d);
  }
  function span(ms) {
    var s = Math.round(Math.abs(ms) / 1000);
    if (s < 60) return s + ' s';
    var m = Math.round(s / 60); if (m < 60) return m + ' min';
    var h = Math.floor(m / 60), r = m % 60; if (h < 48) return h + ' h' + (r ? ' ' + r + ' min' : '');
    return Math.round(h / 24) + ' days';
  }
  function plural(n, one, many) { return n + ' ' + (n === 1 ? one : (many || one + 's')); }
  function num(n) { return Number(n).toLocaleString(EN); }
  function param(name) {
    var q = (location.search || '') + '&' + ((location.hash || '').split('?')[1] || ''), m = new RegExp('[?&]' + name + '=([^&#]*)').exec(q);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }
  function store(key, val) {
    try { if (val === undefined) return sessionStorage.getItem('mki:' + page.id + ':' + key); sessionStorage.setItem('mki:' + page.id + ':' + key, val); } catch (e) { return null; }
  }

  /* ---------- theme: Jellyfin's own palette first, background luminance as fallback ---------- */
  function parseColor(s) {
    s = String(s || '').trim();
    var m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(s);
    if (m) { var h = m[1].length === 3 ? m[1].replace(/./g, '$&$&') : m[1]; return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16), 1]; }
    m = /rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,\/]\s*([\d.]+%?))?/.exec(s);
    if (!m) return null;
    var a = m[4] == null ? 1 : (/%$/.test(m[4]) ? parseFloat(m[4]) / 100 : +m[4]);
    return [+m[1], +m[2], +m[3], a];
  }
  function tone() {
    var c = parseColor(getComputedStyle(document.documentElement).getPropertyValue('--jf-palette-background-default'));
    for (var n = page; !c && n && n.nodeType === 1; n = n.parentElement) { var b = parseColor(getComputedStyle(n).backgroundColor); if (b && b[3] > 0.5) c = b; }
    if (!c) c = parseColor(getComputedStyle(document.body).backgroundColor) || [16, 16, 16, 1];
    var l = (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255;
    page.setAttribute('data-tone', l > 0.5 ? 'light' : 'dark');
  }
  /* the sticky bar stops under Jellyfin's fixed header */
  function stickyTop() {
    var top = 0;
    Array.prototype.forEach.call(document.querySelectorAll('.MuiAppBar-root, .skinHeader'), function (h) {
      var cs = getComputedStyle(h), r = h.getBoundingClientRect();
      if ((cs.position === 'fixed' || cs.position === 'sticky') && cs.visibility !== 'hidden' && r.top <= 0 && r.bottom > top && r.bottom < 240) top = Math.round(r.bottom);
    });
    page.style.setProperty('--mk-sticky-top', top + 'px');
  }
  tone();
  if (window.MutationObserver) {
    var mo = new MutationObserver(tone);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class', 'style'] });
    disposers.push(function () { mo.disconnect(); });
  }
  listen(window, 'resize', function () { stickyTop(); tabOverflow(); });

  /* ---------- tabs (role=tablist, arrow keys, ?tab= deep link, remembered per visit) ---------- */
  var tabs = $$('.mk-tab[role="tab"]'), tabList = $('.mk-tabs');
  function tabOverflow() { if (tabList) tabList.classList.toggle('is-overflow', tabList.scrollWidth > tabList.clientWidth + 2); }
  function selectTab(t, focus) {
    if (!t) return;
    tabs.forEach(function (x) {
      var on = x === t; x.setAttribute('aria-selected', String(on)); x.tabIndex = on ? 0 : -1;
      var p = byId(x.getAttribute('aria-controls')); if (p) p.hidden = !on;
    });
    if (focus) t.focus();
    var tl = t.parentElement, tr = t.getBoundingClientRect(), lr = tl.getBoundingClientRect();
    if (tr.left < lr.left || tr.right > lr.right) tl.scrollLeft += (tr.left - lr.left) - (lr.width - tr.width) / 2;
    store('tab', t.getAttribute('data-tab') || '');
    if (o.onTab) o.onTab(t.getAttribute('data-tab'));
  }
  function tabByName(name) { return tabs.filter(function (x) { return x.getAttribute('data-tab') === name; })[0]; }
  function tabOf(node) {
    var p = node && node.closest('[role="tabpanel"]');
    return p ? tabs.filter(function (x) { return x.getAttribute('aria-controls') === p.id; })[0] : null;
  }
  tabs.forEach(function (t, i) {
    t.addEventListener('click', function () { selectTab(t); });
    t.addEventListener('keydown', function (e) {
      var j = e.key === 'ArrowRight' ? i + 1 : e.key === 'ArrowLeft' ? i - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : null;
      if (j == null) return; e.preventDefault(); selectTab(tabs[(j + tabs.length) % tabs.length], true);
    });
  });
  if (tabs.length) selectTab(tabByName(param('tab')) || tabByName(store('tab')) || tabs.filter(function (x) { return x.getAttribute('aria-selected') === 'true'; })[0] || tabs[0]);
  /* in-page links: <button data-goto="cardId"> switches tab if needed and brings the card into view */
  function reveal(node, focusNode) {
    if (!node) return;
    var t = tabOf(node); if (t && t.getAttribute('aria-selected') !== 'true') selectTab(t);
    var deck = node.closest('.mk-deck'); if (deck) deck.classList.add('is-open');
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    var f = focusNode || node; if (f.tabIndex < 0 && !f.hasAttribute('tabindex')) f.setAttribute('tabindex', '-1');
    try { f.focus({ preventScroll: true }); } catch (e) { f.focus(); }
    node.classList.remove('is-found'); void node.offsetWidth; node.classList.add('is-found');
  }
  page.addEventListener('click', function (e) {
    var g = e.target.closest && e.target.closest('[data-goto]'); if (!g || !page.contains(g)) return;
    e.preventDefault(); reveal(byId(g.getAttribute('data-goto')));
  });

  /* ---------- deck folds on phones ---------- */
  var deck = $('.mk-deck');
  if (deck) {
    var foot = $('.mk-deck__foot', deck), tg = el('button', 'mk-btn mk-btn--sm mk-btn--ghost mk-deck__toggle');
    tg.type = 'button'; tg.appendChild(ic('expand_more')); tg.appendChild(el('span', null, 'Show status'));
    var setDeck = function (open) {
      deck.classList.toggle('is-open', open); tg.setAttribute('aria-expanded', String(open));
      tg.lastChild.textContent = open ? 'Hide status' : 'Show status'; tg.firstChild.textContent = open ? 'expand_less' : 'expand_more';
    };
    tg.addEventListener('click', function () { var open = !deck.classList.contains('is-open'); setDeck(open); store('deck', open ? '1' : '0'); });
    setDeck(store('deck') === '1');
    if (foot) foot.appendChild(tg); else deck.appendChild(tg);
  }

  /* ---------- sliders: range <-> number box, fill, default tick, reset ---------- */
  function rangeSync(r) {
    var min = +r.min || 0, max = +r.max || 100, v = +r.value;
    r.style.setProperty('--p', ((v - min) / ((max - min) || 1) * 100) + '%');
    var wrap = r.closest('.mk-range'); if (!wrap) return;
    var box = $('[data-sync-for="' + r.id + '"]', wrap); if (box && document.activeElement !== box) box.value = r.value;
    var out = $('[data-out-for="' + r.id + '"]', wrap); if (out) out.textContent = r.value + (r.getAttribute('data-unit') ? ' ' + r.getAttribute('data-unit') : '');
    if (r.hasAttribute('data-default')) wrap.classList.toggle('is-changed', String(v) !== r.getAttribute('data-default'));
  }
  $$('.mk-range input[type="range"]').forEach(function (r) {
    var wrap = r.closest('.mk-range');
    if (r.hasAttribute('data-default')) {
      var d = +r.getAttribute('data-default'), min = +r.min || 0, max = +r.max || 100, tr = $('.mk-range__track', wrap);
      if (tr) { var t = el('i', 'mk-tick'); t.style.left = ((d - min) / ((max - min) || 1) * 100) + '%'; t.title = 'Default: ' + d; tr.appendChild(t); }
      var rs = $('.mk-reset', wrap);
      if (rs) rs.addEventListener('click', function () { r.value = d; rangeSync(r); r.dispatchEvent(new Event('input', { bubbles: true })); r.dispatchEvent(new Event('change', { bubbles: true })); });
    }
    r.addEventListener('input', function () { rangeSync(r); });
    var box = $('[data-sync-for="' + r.id + '"]', wrap);
    if (box) {
      box.addEventListener('input', function () { var n = +box.value; if (box.value !== '' && n >= +r.min && n <= +r.max) { r.value = n; rangeSync(r); r.dispatchEvent(new Event('input', { bubbles: true })); } });
      box.addEventListener('change', function (e) {
        e.stopPropagation();
        var st = +r.step || 1, n = Math.round(Math.min(+r.max, Math.max(+r.min, +box.value || 0)) / st) * st;
        box.value = n; r.value = n; rangeSync(r); r.dispatchEvent(new Event('change', { bubbles: true }));
      });
    }
    rangeSync(r);
  });

  /* ---------- steppers: [-] value [+]; the value saves once the clicking stops ---------- */
  $$('.mk-stepper').forEach(function (s) {
    var inp = $('input', s), timer;
    $$('button', s).forEach(function (b) {
      b.addEventListener('click', function () {
        var step = +(b.getAttribute('data-step') || 1), lo = inp.min === '' ? -Infinity : +inp.min, hi = inp.max === '' ? Infinity : +inp.max;
        inp.value = Math.min(hi, Math.max(lo, (+inp.value || 0) + step));
        inp.dispatchEvent(new Event('input', { bubbles: true }));
        clearTimeout(timer); timer = setTimeout(function () { inp.dispatchEvent(new Event('change', { bubbles: true })); }, 450);
      });
    });
  });

  /* ---------- list editors (allow/block lists). Entries are rendered with textContent only. ---------- */
  function tagsSet(box, list) {
    box._tags = (list || []).slice();
    $$('.mk-tag', box).forEach(function (t) { t.parentNode.removeChild(t); });
    var input = $('input', box), kind = box.classList.contains('mk-tags--block') ? 'block' : box.classList.contains('mk-tags--allow') ? 'check' : null;
    box._tags.forEach(function (txt, i) {
      var t = el('span', 'mk-tag'); if (kind) t.appendChild(ic(kind));
      t.appendChild(el('span', null, txt));
      var x = el('button'); x.type = 'button'; x.setAttribute('aria-label', 'Remove ' + txt); x.appendChild(ic('close', 'mk-ic--sm'));
      x.addEventListener('click', function () { var l = box._tags.slice(); l.splice(i, 1); tagsSet(box, l); box.dispatchEvent(new Event('change', { bubbles: true })); input.focus(); });
      t.appendChild(x); box.insertBefore(t, input);
    });
    box.dispatchEvent(new Event('mk-tags', { bubbles: true }));
  }
  $$('.mk-tags').forEach(function (box) {
    var input = $('input', box);
    tagsSet(box, []);
    box.addEventListener('click', function (e) { if (e.target === box) input.focus(); });
    function add(raw) {
      var parts = String(raw).split(/\n/).map(function (s) { return s.trim(); }).filter(Boolean), l = box._tags.slice(), changed = false;
      parts.forEach(function (p) { if (l.map(function (x) { return x.toLowerCase(); }).indexOf(p.toLowerCase()) < 0) { l.push(p); changed = true; } });
      input.value = ''; if (changed) { tagsSet(box, l); box.dispatchEvent(new Event('change', { bubbles: true })); }
    }
    box._add = add;
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); if (input.value.trim()) add(input.value); }
      else if (e.key === 'Backspace' && !input.value && box._tags.length) { var l = box._tags.slice(); l.pop(); tagsSet(box, l); box.dispatchEvent(new Event('change', { bubbles: true })); }
    });
    input.addEventListener('change', function (e) { e.stopPropagation(); });
    input.addEventListener('input', function (e) { e.stopPropagation(); });
    input.addEventListener('paste', function (e) { var t = (e.clipboardData || window.clipboardData).getData('text'); if (/\n/.test(t)) { e.preventDefault(); add(t); } });
    input.addEventListener('blur', function () { if (input.value.trim()) add(input.value); });
    var sug = box.id && $('.mk-suggest[data-for="' + box.id + '"]');
    if (sug) $$('button', sug).forEach(function (b) { b.addEventListener('click', function () { add(b.getAttribute('data-value') || b.textContent); }); });
  });

  /* ---------- secrets: the eye button only ever touches a field of this page ---------- */
  $$('[data-reveal]').forEach(function (b) {
    b.addEventListener('click', function () {
      var f = byId(b.getAttribute('data-reveal')); if (!f) return;
      var show = f.type === 'password';
      f.type = show ? 'text' : 'password'; b.setAttribute('aria-pressed', String(show)); b.setAttribute('aria-label', show ? 'Hide' : 'Show');
      var i = $('.mk-ic', b); if (i) i.textContent = show ? 'visibility_off' : 'visibility';
    });
  });

  /* ---------- validation ---------- */
  var RULES = {
    url: function (v) {
      if (!v) return null;
      if (!/^https?:\/\//i.test(v)) return 'Start with http:// or https://';
      try { var u = new URL(v); if (!u.hostname) return 'That is not a complete address'; } catch (e) { return 'That is not a valid address'; }
      return null;
    },
    lang: function (v) { return /^[a-z]{2}$/i.test(v) ? null : 'Use a two-letter code such as en, de or fr'; },
    domain: function (v) { if (!v) return null; return /^\.?([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$/i.test(v) ? null : 'Use a domain such as .example.org, without http:// or a path'; },
    css: function (v) { var a = (v.match(/\{/g) || []).length, b = (v.match(/\}/g) || []).length; return a === b ? null : (a > b ? (a - b) + ' "{" not closed' : (b - a) + ' "}" too many'); },
    int: function (v, e) {
      if (v === '' || !/^-?\d+$/.test(v)) return 'Enter a whole number';
      var n = +v; if ((e.min !== '' && n < +e.min) || (e.max !== '' && n > +e.max)) return 'Between ' + e.min + ' and ' + e.max;
      return null;
    }
  };
  if (o.rules) Object.keys(o.rules).forEach(function (k) { RULES[k] = o.rules[k]; });
  function msgFor(c) {
    var id = c.getAttribute('data-msg'); if (id) return byId(id);
    var f = c.closest('.mk-field, .mk-row'); return f ? $('.mk-msg[data-auto]', f) : null;
  }
  function validate(c) {
    var rule = c.getAttribute('data-validate'); if (!rule || !RULES[rule]) return true;
    var err = RULES[rule](String(c.value).trim(), c), m = msgFor(c);
    c.setAttribute('aria-invalid', err ? 'true' : 'false');
    if (m && (err || m.getAttribute('data-kit') === 'err')) {
      m.textContent = ''; m.className = 'mk-msg' + (err ? ' mk-msg--bad' : '');
      if (err) { m.setAttribute('data-kit', 'err'); m.appendChild(ic('error')); m.appendChild(el('span', null, err + '. Not saved.')); }
      else m.removeAttribute('data-kit');
    }
    return !err;
  }

  /* ---------- reading and writing controls ---------- */
  function keyEls(k) { return $$('[data-key="' + k + '"]'); }
  /* single-line text is saved without leading or trailing spaces (data-notrim keeps them) */
  function trim(e) { if (e && e.tagName === 'INPUT' && /^(text|url|password|search|email|tel)$/.test(e.type) && !e.hasAttribute('data-notrim') && e.value !== e.value.trim()) e.value = e.value.trim(); }
  function isText(e) { return e && (e.tagName === 'TEXTAREA' || (e.tagName === 'INPUT' && /^(text|url|password|search|number|email|tel)$/.test(e.type))); }
  function copyJson(v) { return v == null ? null : JSON.parse(JSON.stringify(v)); }
  function read(k) {
    var els = keyEls(k), e = els[0]; if (!e) return undefined;
    /* data-type="json": the page keeps a structured value (e.g. a list of objects) on the element as mkValue */
    if (e.getAttribute('data-type') === 'json') return copyJson(e.mkValue);
    if (e.classList.contains('mk-tags')) return (e._tags || []).slice();
    if (e.type === 'radio') { var c = els.filter(function (x) { return x.checked; })[0]; return c ? c.value : null; }
    if (e.type === 'checkbox') return els.length > 1 ? els.filter(function (x) { return x.checked; }).map(function (x) { return x.value; }) : e.checked;
    return e.value;
  }
  function write(k, v) {
    var els = keyEls(k), e = els[0]; if (!e) return;
    if (e.getAttribute('data-type') === 'json') e.mkValue = copyJson(v);
    else if (e.classList.contains('mk-tags')) tagsSet(e, v || []);
    else if (e.type === 'radio') els.forEach(function (x) { x.checked = x.value === String(v); });
    else if (e.type === 'checkbox') { if (els.length > 1) els.forEach(function (x) { x.checked = (v || []).indexOf(x.value) >= 0; }); else e.checked = !!v; }
    else if (e.tagName === 'SELECT' && v != null && !$$('option', e).some(function (op) { return op.value === String(v); })) {
      /* a stored value the page has no option for keeps its own option instead of falling back to the first one */
      var op = el('option', null, (e.getAttribute('data-other') || '{v}').replace('{v}', String(v))); op.value = String(v); op.setAttribute('data-kit', 'other'); e.appendChild(op); e.value = String(v);
    }
    else { e.value = v == null ? '' : v; if (e.type === 'range') rangeSync(e); }
    if (isText(e) && e.hasAttribute('data-validate')) validate(e);
    e.dispatchEvent(new CustomEvent('mk-restored', { bubbles: true }));
  }
  function typeOf(k) {
    var e = keyEls(k)[0], t = e && e.getAttribute('data-type'); if (t) return t;
    var v = cfg ? cfg[k] : undefined;
    if (Array.isArray(v)) return v.length && typeof v[0] === 'number' ? 'number[]' : v.length && v[0] !== null && typeof v[0] === 'object' ? 'json' : 'string[]';
    if (v !== null && typeof v === 'object') return 'json';
    if (typeof v === 'boolean') return 'bool';
    if (typeof v === 'number') return 'number';
    return 'string';
  }
  function toServer(k, v) {
    switch (typeOf(k)) {
      case 'bool': return v === true || v === 'true';
      case 'number': return v === '' || v == null || isNaN(+v) ? (cfg && typeof cfg[k] === 'number' ? cfg[k] : 0) : +v;
      case 'number[]': return (v || []).map(Number);
      case 'string[]': return (v || []).map(String);
      case 'json': return copyJson(v);
      default: return v == null ? '' : String(v);
    }
  }
  function toDom(k, v) {
    var els = keyEls(k), e = els[0]; if (!e) return v;
    if (e.getAttribute('data-type') === 'json') return copyJson(v);
    if (e.classList.contains('mk-tags') || (e.type === 'checkbox' && els.length > 1)) return (v || []).map(String);
    if (e.type === 'checkbox') return v === true || v === 'true';
    return v == null ? '' : String(v);
  }
  function labelOf(k) {
    var e = keyEls(k)[0]; if (!e) return k;
    if (e.getAttribute('data-label')) return e.getAttribute('data-label');
    var row = e.closest('.mk-row, .mk-field'), l = row && $('.mk-row__label, .mk-field__label', row);
    return l ? l.textContent.trim() : (e.getAttribute('aria-label') || k);
  }
  function show(k, v) {
    if (o.show) { var custom = o.show(k, toServer(k, v)); if (custom != null) return custom; }
    var els = keyEls(k), e = els[0];
    if (v === true) return 'On'; if (v === false) return 'Off';
    if (Array.isArray(v)) return v.length ? (v.length > 3 || typeof v[0] === 'object' ? plural(v.length, 'entry', 'entries') : v.join(', ')) : 'none';
    if (v !== null && typeof v === 'object') return 'changed';
    if (e && e.type === 'radio') {
      var r = els.filter(function (x) { return x.value === String(v); })[0], lb = r && r.labels && r.labels[0];
      var dn = lb && (lb.hasAttribute('data-name') ? lb : lb.querySelector('[data-name]'));
      return dn ? dn.getAttribute('data-name') : lb ? lb.textContent.trim() : String(v);
    }
    if (e && e.tagName === 'SELECT') { var op = $$('option', e).filter(function (x) { return x.value === String(v); })[0]; return op ? op.textContent : String(v); }
    var s = String(v); if (!s) return 'empty';
    if (e && (e.getAttribute('data-secret') === '1' || e.type === 'password')) return '••••••';
    if (s.length > 34) s = s.slice(0, 32) + '…';
    return s + (e && e.getAttribute('data-unit') ? ' ' + e.getAttribute('data-unit') : '');
  }
  function allKeys() { var seen = {}; $$('[data-key]').forEach(function (e) { seen[e.getAttribute('data-key')] = 1; }); return Object.keys(seen); }

  /* ---------- save chip + "Changes this visit" (built by the kit into .mk-bar) ---------- */
  var bar = $('.mk-bar'), save = null, saveText = null, saveIcon = null, pop = null, logList = null, logEmpty = null, revertBtn = null;
  if (bar) {
    var wrap = el('div', 'mk-savewrap');
    save = el('button', 'mk-save'); save.type = 'button';
    save.setAttribute('aria-haspopup', 'dialog'); save.setAttribute('aria-expanded', 'false');
    save.title = 'Every change saves itself. Open for this visit\'s changes and undo.';
    saveIcon = ic('cloud_done'); saveText = el('span', 'mk-save__text', 'Loading…');
    save.appendChild(saveIcon); save.appendChild(saveText);
    var live = el('span', 'mk-sr'); live.setAttribute('aria-live', 'polite'); save.appendChild(live);
    pop = el('div', 'mk-pop'); pop.hidden = true; pop.setAttribute('role', 'dialog'); pop.setAttribute('aria-label', 'Changes this visit');
    pop.appendChild(el('div', 'mk-pop__title', 'Changes this visit'));
    logEmpty = el('p', 'mk-log__empty', 'Nothing changed yet. Every change saves itself and shows up here with an undo.');
    logList = el('ul', 'mk-log');
    pop.appendChild(logEmpty); pop.appendChild(logList);
    var pf = el('div', 'mk-pop__foot');
    revertBtn = btn('Put everything back', 'mk-btn--sm', 'history'); revertBtn.disabled = true;
    pf.appendChild(revertBtn); pop.appendChild(pf);
    wrap.appendChild(save); wrap.appendChild(pop); bar.appendChild(wrap);
  }
  var lastAnnounce = '';
  function setState(state, text) {
    if (!save) return;
    save.setAttribute('data-state', state);
    saveText.textContent = text; save.setAttribute('aria-label', text + '. Show the changes made during this visit.');
    saveIcon.textContent = ({ saved: 'cloud_done', saving: 'sync', editing: 'edit', invalid: 'warning', error: 'cloud_off', warn: 'report', loading: 'hourglass_empty' })[state] || 'cloud_done';
    saveIcon.classList.toggle('mk-spin', state === 'saving');
    if (state !== 'saving' && state !== 'editing' && text !== lastAnnounce) { lastAnnounce = text; live.textContent = text; }
  }

  /* ---------- the save engine ---------- */
  var pluginId = o.pluginId, cfg = null, loaded = false, loading = null;
  var committed = {}, opened = {}, pending = {}, invalid = {}, warns = {};
  var flushTimer = null, inFlight = null, sending = {}, again = false, failed = false, failToast = null, lastSaved = null, openedAt = null, log = [];

  function restState() {
    if (!loaded) return;
    if (failed) return setState('error', 'Not saved · the server did not answer');
    var n = count(invalid);
    if (n) return setState('invalid', n === 1 ? '1 field needs a fix · not saved' : n + ' fields need a fix · not saved');
    if (inFlight || flushTimer || count(pending)) return setState('saving', 'Saving…');
    var a = document.activeElement;
    if (a && page.contains(a) && isText(a) && a.hasAttribute('data-key') && !same(read(a.getAttribute('data-key')), base(a.getAttribute('data-key')))) return setState('editing', 'Editing · saves when you leave the field');
    var w = Object.keys(warns);
    if (w.length) return setState('warn', 'Saved · ' + (w.length === 1 ? warns[w[0]] : w.length + ' warnings'));
    setState('saved', 'All changes saved' + (lastSaved ? ' · ' + hhmm(lastSaved) : ''));
  }
  /* what the server has, or will have once the save on its way lands */
  function base(k) { return Object.prototype.hasOwnProperty.call(sending, k) ? sending[k] : committed[k]; }
  /* a failed save is over once nothing is waiting to be sent any more */
  function settleFailed() {
    if (!failed || count(pending)) return;
    failed = false; if (failToast) { dropToast(failToast); failToast = null; } renderLog();
  }
  /* Undo only while the change is still what the server has; a later change of the same key would be lost */
  function current(k, to) { return same(committed[k], to) && !pending[k] && !Object.prototype.hasOwnProperty.call(sending, k); }
  function queue(k, opt) {
    var v = read(k);
    /* back to that value: nothing to send */
    if (same(v, base(k))) {
      delete pending[k]; settleFailed();
      restState(); return;
    }
    pending[k] = { from: pending[k] ? pending[k].from : base(k), to: v, undo: !!(opt && opt.undo) };
    setState('saving', 'Saving…');
    clearTimeout(flushTimer); flushTimer = setTimeout(flush, 350);
  }
  function flush() {
    clearTimeout(flushTimer); flushTimer = null;
    if (inFlight) { again = true; return; }
    var keys = Object.keys(pending);
    if (!keys.length) { settleFailed(); restState(); return; }
    var batch = keys.map(function (k) { return { k: k, from: pending[k].from, to: pending[k].to, undo: pending[k].undo }; });
    setState('saving', 'Saving…');
    sending = {}; batch.forEach(function (b) { sending[b.k] = b.to; });
    inFlight = ApiClient.getPluginConfiguration(pluginId).then(function (c) {
      batch.forEach(function (b) { c[b.k] = toServer(b.k, b.to); });
      return ApiClient.updatePluginConfiguration(pluginId, c).then(function () { return c; });
    }).then(function (c) {
      inFlight = null; sending = {}; failed = false; cfg = c; lastSaved = new Date();
      if (failToast) { dropToast(failToast); failToast = null; }
      batch.forEach(function (b) {
        committed[b.k] = b.to;
        if (pending[b.k] && same(pending[b.k].to, b.to)) delete pending[b.k];
        else if (pending[b.k]) pending[b.k].from = b.to;
        log.unshift({ k: b.k, from: b.from, to: b.to, t: lastSaved, undo: b.undo });
      });
      renderLog();
      if (o.onSaved) o.onSaved(batch.map(function (b) { return { key: b.k, from: toServer(b.k, b.from), to: toServer(b.k, b.to), undo: b.undo }; }));
      savedToast(batch);
      if (again || count(pending)) { again = false; clearTimeout(flushTimer); flushTimer = setTimeout(flush, 120); }
      restState();
    }, function () {
      inFlight = null; sending = {}; again = false; failed = true;
      restState(); renderLog();
      if (failToast) dropToast(failToast);
      failToast = toast('Could not save ' + keys.map(labelOf).join(', ') + '. Your change is still on the page and is not active yet.', { bad: true, action: 'Retry', onAction: function () { failToast = null; flush(); }, sticky: true });
    });
  }
  function savedToast(batch) {
    var custom = o.toastFor && o.toastFor(batch.map(function (b) { return { key: b.k, from: toServer(b.k, b.from), to: toServer(b.k, b.to), undo: b.undo }; }));
    if (custom === false) return;
    if (custom) { toast(custom.text, custom); return; }
    if (batch.length === 1) {
      var b0 = batch[0];
      toast((b0.undo ? 'Undone: ' : 'Saved: ') + labelOf(b0.k) + ' → ' + show(b0.k, b0.to), b0.undo ? {} : { action: 'Undo', onAction: function () { undoIfCurrent(b0.k, b0.from, b0.to); } });
    } else toast(batch.length + ' changes saved', { action: 'Show', onAction: openPop });
  }
  function undoIfCurrent(k, from, to) {
    if (current(k, to)) { undoTo(k, from); return; }
    toast('Not undone: ' + labelOf(k) + ' has changed again since. Undo the newer change first (Changes this visit).', { bad: true, icon: 'history', action: 'Show', onAction: openPop });
  }
  /* an undo asks the same question as the change itself (e.g. going live, locking an account) */
  function undoTo(k, v) {
    var go = function () { write(k, v); delete invalid[k]; queue(k, { undo: true }); };
    var hook = o.confirm && o.confirm[k];
    if (hook && !same(v, base(k))) Promise.resolve(hook(toServer(k, v), toServer(k, base(k)))).then(function (ok) { if (ok) go(); });
    else go();
  }
  function onChange(e) {
    if (!loaded) return;
    var c = e.target.closest && e.target.closest('[data-key]'); if (!c || !page.contains(c) || c.hasAttribute('data-nosave')) return;
    var k = c.getAttribute('data-key');
    trim(c);
    if (c.hasAttribute('data-validate') && !validate(c)) { invalid[k] = 1; delete pending[k]; settleFailed(); restState(); return; }
    delete invalid[k];
    var hook = o.confirm && o.confirm[k];
    /* the page is gone (a field left focused loses it now): a change that would need a question is put back, never asked */
    if (hook && away) { if (quiet(k)) queue(k); else write(k, pending[k] ? pending[k].to : base(k)); restState(); return; }
    if (hook && !same(read(k), base(k))) {
      var to = read(k), answer = hook(toServer(k, to), toServer(k, base(k)));
      if (answer === true) { queue(k); return; }
      /* a save can land while the question is open: yes saves what was asked about, no puts back the last
         accepted value (a change still waiting to be sent, or what the server has) so the screen matches */
      Promise.resolve(answer).then(function (ok) {
        if (ok) { write(k, to); queue(k); } else { write(k, pending[k] ? pending[k].to : base(k)); restState(); }
      });
      return;
    }
    queue(k);
  }
  page.addEventListener('change', onChange);
  page.addEventListener('input', function (e) {
    if (!loaded) return;
    var c = e.target.closest && e.target.closest('[data-key]'); if (!c || !isText(c)) return;
    var k = c.getAttribute('data-key');
    if (c.hasAttribute('data-validate')) { if (validate(c)) delete invalid[k]; else invalid[k] = 1; }
    restState();
  });
  page.addEventListener('focusout', function () { setTimeout(restState, 0); });
  /* Esc in a text field puts the saved value back */
  page.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    var c = e.target; if (!isText(c) || !c.hasAttribute('data-key')) return;
    var k = c.getAttribute('data-key'); if (same(read(k), base(k))) return;
    e.preventDefault(); e.stopPropagation();
    write(k, base(k)); delete pending[k]; delete invalid[k]; validate(c); settleFailed(); restState();
  });

  /* leaving the page: a valid draft in the focused field is saved, waiting changes go out at once.
     No question can be asked while the page goes away, and a hidden page must not keep a modal dialog open (it would
     block the whole web client): open dialogs are cancelled, and a draft of a key with a question is put back unsaved. */
  var away = false;
  /* o.quiet[k](to, from): true when this change of a key with a question surely needs none. It must only look at the values
     (no dialogs, no requests); without it, a draft of such a key is not saved when the page is left. */
  function quiet(k) { var q = o.quiet && o.quiet[k]; return !!q && q(toServer(k, read(k)), toServer(k, base(k))) === true; }
  function leave(unloading) {
    /* the browser may still keep the page (the admin can choose to stay), so an unload only saves */
    if (unloading !== true) { away = true; openDialogs.slice().forEach(function (f) { f(); }); }
    var a = document.activeElement;
    if (a && page.contains(a) && isText(a) && a.hasAttribute('data-key') && loaded) {
      var k = a.getAttribute('data-key');
      trim(a);
      if (!same(read(k), base(k))) {
        var ok = validate(a) && (!(o.confirm && o.confirm[k]) || quiet(k));
        if (ok) { delete invalid[k]; queue(k); }
        else if (o.confirm && o.confirm[k]) { write(k, pending[k] ? pending[k].to : base(k)); delete invalid[k]; }
      }
    }
    if (count(pending)) flush();
    closePop();
  }
  page.addEventListener('viewbeforehide', function () { leave(); });
  listen(window, 'beforeunload', function (e) {
    leave(true);
    if (count(pending) || inFlight || count(invalid) || failed) { e.preventDefault(); e.returnValue = ''; }
  });

  function setLoad(state, text) {
    page.setAttribute('data-load', state);
    var gated = $$('.mk-main, .mk-master, [data-needs-load]');
    gated.forEach(function (g) { if (state === 'ready') g.removeAttribute('inert'); else g.setAttribute('inert', ''); });
    if (!loadbar) return;
    loadbar.hidden = state === 'ready';
    loadbar.classList.toggle('is-bad', state === 'error');
    fill(loadbar, state === 'error'
      ? [ic('cloud_off'), el('span', 'mk-loadbar__text', text || 'Could not load the settings from the server. Nothing can be changed until they load.'), retryBtn]
      : [ic('hourglass_empty', 'mk-spin'), el('span', 'mk-loadbar__text', 'Loading the settings…')]);
    if (state !== 'ready') setState(state === 'error' ? 'error' : 'loading', state === 'error' ? 'Settings not loaded' : 'Loading…');
  }
  var loadbar = el('div', 'mk-loadbar'); loadbar.setAttribute('role', 'status');
  var retryBtn = btn('Try again', 'mk-btn--sm', 'refresh'); retryBtn.addEventListener('click', function () { load(); });
  if (deck) { var head = $('.mk-deck__head', deck); if (head && head.nextSibling) deck.insertBefore(loadbar, head.nextSibling); else deck.appendChild(loadbar); }
  else if (bar) bar.parentNode.insertBefore(loadbar, bar);

  function load() {
    if (loading) return loading;
    setLoad('loading');
    var extra = o.preload ? Promise.resolve(o.preload()) : Promise.resolve();
    loading = Promise.all([ApiClient.getPluginConfiguration(pluginId), extra]).then(function (res) {
      loading = null;
      var c = res[0];
      cfg = c; loaded = true;
      allKeys().forEach(function (k) {
        if (Object.prototype.hasOwnProperty.call(c, k)) write(k, toDom(k, c[k]));
        committed[k] = read(k); opened[k] = committed[k];
      });
      pending = {}; invalid = {}; failed = false; log = []; openedAt = new Date(); lastSaved = null;
      setLoad('ready'); restState(); renderLog();
      if (o.onLoad) o.onLoad(cfg, res[1]);
    }, function (err) {
      loading = null;
      setLoad('error', o.loadError ? o.loadError(err) : null);
      return null;
    });
    return loading;
  }
  page.addEventListener('pageshow', function () {
    away = false;
    tone(); stickyTop(); setTimeout(tabOverflow, 0);
    var busy = count(pending) || inFlight || count(invalid) || failed;
    if (!loaded || !busy) load();
    if (o.onShow) o.onShow();
  });

  /* ---------- history popover ---------- */
  function renderLog() {
    if (!pop) return;
    logList.textContent = '';
    var rows = [];
    Object.keys(pending).forEach(function (k) {
      if (!failed) return;
      var li = el('li', 'is-pending'); li.appendChild(ic('cloud_off', 'mk-ic--sm'));
      var t = el('span', 'mk-log__what'); t.appendChild(el('b', null, labelOf(k))); t.appendChild(document.createTextNode(' ' + show(k, pending[k].from) + ' → ' + show(k, pending[k].to) + ' · not saved'));
      li.appendChild(t); var r = btn('Retry', 'mk-btn--sm mk-btn--ghost', 'refresh'); r.addEventListener('click', function () { flush(); }); li.appendChild(r);
      rows.push(li);
    });
    log.slice(0, 15).forEach(function (x) {
      var li = el('li', x.action ? 'is-action' : null);
      li.appendChild(el('time', null, hhmm(x.t)));
      var t = el('span', 'mk-log__what');
      if (x.action) { t.appendChild(el('b', null, x.text)); var bd = el('span', 'mk-badge', 'Action'); bd.title = 'Actions cannot be undone here'; t.appendChild(bd); li.appendChild(t); }
      else {
        t.appendChild(el('b', null, labelOf(x.k))); t.appendChild(document.createTextNode(' ' + show(x.k, x.from) + ' → ' + show(x.k, x.to)));
        li.appendChild(t);
        if (current(x.k, x.to)) { var b = btn('Undo', 'mk-btn--sm mk-btn--ghost', 'undo'); b.addEventListener('click', function () { undoTo(x.k, x.from); }); li.appendChild(b); }
      }
      rows.push(li);
    });
    rows.forEach(function (r) { logList.appendChild(r); });
    logEmpty.hidden = rows.length > 0;
    var changed = allKeys().some(function (k) { return !same(committed[k], opened[k]); });
    revertBtn.disabled = !changed;
    revertBtn.lastChild.textContent = openedAt ? 'Put everything back as it was at ' + hhmm(openedAt) : 'Put everything back';
  }
  function openPop() { if (!pop) return; renderLog(); pop.hidden = false; save.setAttribute('aria-expanded', 'true'); }
  function closePop() { if (!pop || pop.hidden) return; pop.hidden = true; save.setAttribute('aria-expanded', 'false'); }
  if (save) {
    save.addEventListener('click', function (e) {
      e.stopPropagation();
      /* "needs a fix" or a warning: the chip takes you to the field */
      var bad = Object.keys(invalid)[0] || (!failed && Object.keys(warns)[0]), f = bad && keyEls(bad)[0];
      if (f && pop.hidden) { reveal(f.closest('.mk-row, .mk-field') || f, f); return; }
      if (pop.hidden) openPop(); else closePop();
    });
    listen(document, 'click', function (e) { if (pop && !pop.hidden && !pop.contains(e.target) && e.target !== save) closePop(); });
    pop.addEventListener('keydown', function (e) { if (e.key === 'Escape') { e.stopPropagation(); closePop(); save.focus(); } });
    revertBtn.addEventListener('click', function () {
      closePop();
      confirmBox({ title: 'Put everything back as it was at ' + hhmm(openedAt) + '?', body: 'Every setting you changed during this visit returns to the value it had when you opened the page. Actions you ran are not undone.', ok: 'Put it back', icon: 'history', tone: 'warn' })
        .then(function (ok) {
          if (!ok) return;
          /* plain keys go back at once; keys with a question (going live, locking accounts) still ask, one after another */
          var chain = Promise.resolve();
          allKeys().forEach(function (k) {
            if (same(committed[k], opened[k]) && same(read(k), opened[k])) return;
            var hook = o.confirm && o.confirm[k];
            if (hook) chain = chain.then(function () { return Promise.resolve(hook(toServer(k, opened[k]), toServer(k, base(k)))).then(function (y) { if (y) { write(k, opened[k]); delete invalid[k]; queue(k, { undo: true }); } }); });
            else { write(k, opened[k]); delete invalid[k]; queue(k, { undo: true }); }
          });
        });
    });
  }

  /* ---------- toasts ---------- */
  var toasts = el('div', 'mk-toasts'); toasts.setAttribute('aria-live', 'polite'); page.appendChild(toasts);
  function dropToast(t) { if (t && t.parentNode) t.parentNode.removeChild(t); }
  function toast(text, opt) {
    opt = opt || {};
    var t = el('div', 'mk-toast' + (opt.bad ? ' mk-toast--bad' : '')); t.setAttribute('role', opt.bad ? 'alert' : 'status');
    t.appendChild(ic(opt.icon || (opt.bad ? 'error' : 'check_circle'))); t.appendChild(el('span', 'mk-toast__text', text));
    if (opt.action) { var b = el('button', null, opt.action); b.type = 'button'; b.addEventListener('click', function () { dropToast(t); opt.onAction(); }); t.appendChild(b); }
    if (opt.sticky) { var x = el('button'); x.type = 'button'; x.setAttribute('aria-label', 'Dismiss'); x.appendChild(ic('close', 'mk-ic--sm')); x.addEventListener('click', function () { dropToast(t); if (t === failToast) failToast = null; }); t.appendChild(x); }
    /* at most three at a time; the oldest goes first, a failed-save notice stays */
    var old = Array.prototype.filter.call(toasts.children, function (x) { return x !== failToast; });
    while (old.length > 1 + (failToast ? 0 : 1)) dropToast(old.shift());
    toasts.appendChild(t);
    if (!opt.sticky) setTimeout(function () { dropToast(t); }, opt.timeout || (opt.action ? 8000 : 3800));
    return t;
  }

  /* ---------- dialogs (native <dialog>, built fresh for every question) ---------- */
  var openDialogs = [];
  function dialog(d) {
    return new Promise(function (resolve) {
      /* the page was left while the question was on its way (a check or request still running): never open a modal on a
         hidden page, it would block the whole web client; the dialog counts as cancelled */
      if (away) { resolve(d.cancelValue === undefined ? null : d.cancelValue); return; }
      var dlg = el('dialog', 'mk-dialog'), result = null, done = false;
      var head = el('div', 'mk-dialog__head'), icw = el('div', 'mk-dialog__icon mk-dialog__icon--' + (d.tone || 'accent'));
      icw.appendChild(ic(d.icon || 'help', 'mk-ic--lg')); head.appendChild(icw);
      var title = el('h2', 'mk-dialog__title', d.title); title.id = page.id + '-dlg-' + Date.now(); head.appendChild(title);
      dlg.setAttribute('aria-labelledby', title.id);
      var body = el('div', 'mk-dialog__body');
      if (d.body) (Array.isArray(d.body) ? d.body : [d.body]).forEach(function (p) { body.appendChild(typeof p === 'string' ? el('p', null, p) : p); });
      if (d.node) body.appendChild(d.node);
      var foot = el('div', 'mk-dialog__foot'), focusBtn = null;
      (d.buttons || []).forEach(function (b) {
        var x = btn(b.text, b.kind === 'primary' ? 'mk-btn--primary' : b.kind === 'danger' ? 'mk-btn--danger' : b.kind === 'ghost' ? 'mk-btn--ghost' : '', b.icon);
        x.addEventListener('click', function () { finish(b.value); });
        if (b.value === d.focus) focusBtn = x;
        foot.appendChild(x);
      });
      dlg.appendChild(head); dlg.appendChild(body); dlg.appendChild(foot);
      function finish(v) { if (done) return; done = true; result = v; openDialogs = openDialogs.filter(function (f) { return f !== cancel; }); if (dlg.open) dlg.close(); if (dlg.parentNode) dlg.parentNode.removeChild(dlg); resolve(v); }
      /* leaving the page cancels the dialog, also a locked one */
      function cancel() { finish(d.cancelValue === undefined ? null : d.cancelValue); }
      openDialogs.push(cancel);
      /* a locked dialog (a one-time password) cannot be closed by Esc or by anything but its own buttons */
      dlg.addEventListener('cancel', function (e) { if (d.locked) e.preventDefault(); });
      dlg.addEventListener('close', function () {
        if (done) return;
        if (d.locked && dlg.isConnected) { try { dlg.showModal(); } catch (e) { } return; }
        finish(d.cancelValue === undefined ? null : d.cancelValue);
      });
      page.appendChild(dlg);
      dlg.showModal();
      if (d.onOpen) d.onOpen(dlg, finish);
      (focusBtn || foot.firstChild || dlg).focus();
    });
  }
  function confirmBox(c) {
    var buttons = c.okOnly ? [] : [{ value: false, text: c.cancel || 'Cancel' }];
    buttons.push({ value: true, text: c.ok || 'OK', kind: c.danger ? 'danger' : 'primary', icon: c.okIcon });
    return dialog({ title: c.title, body: c.body, node: c.node, icon: c.icon, tone: c.tone, buttons: buttons, focus: c.okOnly || c.focusOk ? true : false, cancelValue: false })
      .then(function (v) { return v === true; });
  }

  /* ---------- clipboard: works on plain http too (most home servers) ---------- */
  function copy(text, what) {
    return new Promise(function (resolve) {
      function fallback() {
        var ta = el('textarea'); ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.top = '-1000px'; ta.style.opacity = '0';
        var host = $('dialog[open]') || page; host.appendChild(ta); ta.select();
        var ok = false; try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
        host.removeChild(ta); resolve(ok);
      }
      if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(function () { resolve(true); }, fallback);
      else fallback();
    }).then(function (ok) {
      toast(ok ? (what || 'Text') + ' copied' : 'Could not copy. Select the text and press Ctrl+C.', ok ? { icon: 'content_copy' } : { bad: true });
      return ok;
    });
  }

  /* ---------- actions ---------- */
  function busy(b, promise) {
    b.disabled = true; b.classList.add('is-busy');
    var i = $('.mk-ic', b), old = i && i.textContent; if (i) i.textContent = 'sync';
    var end = function () { b.disabled = false; b.classList.remove('is-busy'); if (i) i.textContent = old; };
    return Promise.resolve(promise).then(function (v) { end(); return v; }, function (e) { end(); throw e; });
  }
  /* resolves true once every waiting change is saved, false if a save failed (actions then refuse to run) */
  function whenSaved() {
    return new Promise(function (resolve) {
      (function check() {
        if (failed) return resolve(false);
        if (flushTimer) flush();
        if (!inFlight && !flushTimer && !count(pending)) return resolve(true);
        setTimeout(check, 100);
      })();
    });
  }
  function logAction(text) { log.unshift({ action: true, text: text, t: new Date() }); renderLog(); }
  function api(type, path, data, json) {
    var req = { type: type, url: ApiClient.getUrl(path) };
    if (json !== false) req.dataType = 'json';
    if (data !== undefined) { req.data = JSON.stringify(data); req.contentType = 'application/json'; }
    return ApiClient.ajax(req);
  }

  /* ---------- find a setting (optional: a .mk-find box on the page) ---------- */
  var finder = $('.mk-find');
  if (finder) {
    var fin = $('.mk-find__input', finder), flist = el('ul', 'mk-find__list'); flist.hidden = true; flist.setAttribute('role', 'listbox'); finder.appendChild(flist);
    fin.setAttribute('role', 'combobox'); fin.setAttribute('aria-expanded', 'false'); fin.setAttribute('autocomplete', 'off');
    var hits = [], act = -1;
    var index = function () {
      return $$('.mk-row, .mk-field, [data-find]').filter(function (r) { return !r.closest('.mk-find') && !r.closest('.mk-dialog'); }).map(function (r) {
        var l = $('.mk-row__label, .mk-field__label', r) || r, t = tabOf(r), key = $('[data-key]', r) || (r.hasAttribute('data-key') ? r : null);
        var label = (r.getAttribute('data-find-label') || l.textContent || '').replace(/\s+/g, ' ').trim();
        return { row: r, label: label, where: t ? t.textContent.replace(/\s*\d+\s*$/, '').trim() : '', text: (label + ' ' + (r.getAttribute('data-find') || '') + ' ' + (key ? key.getAttribute('data-key') : '')).toLowerCase(), ctl: key || $('input, select, textarea, button', r) };
      }).filter(function (x) { return x.label; });
    };
    var pick = function (h) { flist.hidden = true; fin.setAttribute('aria-expanded', 'false'); fin.value = ''; reveal(h.row, h.ctl && !h.ctl.disabled ? h.ctl : null); };
    var renderHits = function () {
      flist.textContent = '';
      if (!hits.length) { flist.appendChild(el('li', 'mk-find__none', 'No setting matches. Try another word.')); return; }
      hits.forEach(function (h, i) {
        var li = el('li'), b = el('button'); b.type = 'button'; b.setAttribute('role', 'option'); if (i === act) b.className = 'is-active';
        b.appendChild(el('span', null, h.label)); if (h.where) b.appendChild(el('small', null, h.where));
        b.addEventListener('mousedown', function (e) { e.preventDefault(); }); b.addEventListener('click', function () { pick(h); });
        li.appendChild(b); flist.appendChild(li);
      });
    };
    fin.addEventListener('input', function () {
      var q = fin.value.trim().toLowerCase();
      if (!q) { flist.hidden = true; fin.setAttribute('aria-expanded', 'false'); return; }
      var words = q.split(/\s+/);
      hits = index().filter(function (x) { return words.every(function (w) { return x.text.indexOf(w) >= 0; }); }).slice(0, 8);
      act = hits.length ? 0 : -1; renderHits(); flist.hidden = false; fin.setAttribute('aria-expanded', 'true');
    });
    fin.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); if (!hits.length) return; act = (act + (e.key === 'ArrowDown' ? 1 : -1) + hits.length) % hits.length; renderHits(); }
      else if (e.key === 'Enter') { e.preventDefault(); if (hits[act]) pick(hits[act]); }
      else if (e.key === 'Escape') { e.stopPropagation(); fin.value = ''; flist.hidden = true; fin.setAttribute('aria-expanded', 'false'); }
    });
    fin.addEventListener('blur', function () { setTimeout(function () { flist.hidden = true; fin.setAttribute('aria-expanded', 'false'); }, 150); });
  }

  /* ---------- clean up when Jellyfin throws the view away ---------- */
  page.addEventListener('viewdestroy', function () { disposers.forEach(function (d) { d(); }); disposers = []; });

  setLoad('loading');

  kit.$ = $; kit.$$ = $$; kit.byId = byId; kit.el = el; kit.ic = ic; kit.fill = fill; kit.btn = btn;
  kit.hhmm = hhmm; kit.day = day; kit.dayTime = dayTime; kit.span = span; kit.plural = plural; kit.num = num; kit.param = param; kit.store = store;
  kit.toast = toast; kit.dropToast = dropToast; kit.confirm = confirmBox; kit.dialog = dialog; kit.copy = copy;
  kit.read = read; kit.write = write; kit.validate = validate; kit.rules = RULES; kit.label = labelOf; kit.show = show;
  /* a copy for structured values, so a page can never change what the kit counts as saved */
  kit.saved = function (k) { var v = cfg ? cfg[k] : undefined; return v !== null && typeof v === 'object' ? copyJson(v) : v; };
  kit.value = function (k) { return toServer(k, read(k)); };
  kit.config = function () { return cfg; };
  kit.loaded = function () { return loaded; };
  kit.reload = load;
  /* undo a saved change from a custom toast (toastFor): asks the key's question like the kit's own Undo */
  kit.undo = function (k, from, to) { undoIfCurrent(k, toDom(k, from), toDom(k, to)); };
  kit.whenSaved = whenSaved; kit.flush = flush; kit.busy = busy; kit.logAction = logAction; kit.api = api;
  kit.warn = function (k, text) { if (text) warns[k] = text; else delete warns[k]; restState(); };
  kit.selectTab = function (name) { selectTab(tabByName(name)); };
  kit.reveal = reveal; kit.rangeSync = rangeSync; kit.tagsSet = tagsSet;
  kit.state = function () { return { loaded: loaded, failed: failed, invalid: count(invalid), pending: count(pending) + (inFlight ? 1 : 0) }; };
  return kit;
}
