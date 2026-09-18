/* DOM + formatting helpers. Classic script: attaches to globalThis.DBM (window.DBM in the browser); runs under Node for tests. */
(function (DBM) {
  'use strict';

  var ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) { return ESCAPES[c]; });
  }

  function appendChildren(el, children) {
    for (var i = 0; i < children.length; i++) {
      var c = children[i];
      if (c == null || c === false || c === true) continue;
      if (Array.isArray(c)) appendChildren(el, c);
      else if (typeof c === 'string' || typeof c === 'number') el.appendChild(document.createTextNode(String(c)));
      else el.appendChild(c);
    }
  }

  /**
   * h('div', {class: 'card', style: {'--v': 0.5}, on: {click: fn}, data: {id: 3}, html: trustedMarkup}, ...children)
   * Strings become text nodes (never parsed as HTML). null/false attributes and children are skipped.
   */
  function h(tag, attrs) {
    var el = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (key) {
        var value = attrs[key];
        if (value == null || value === false) return;
        if (key === 'class') {
          el.className = Array.isArray(value) ? value.filter(Boolean).join(' ') : value;
        } else if (key === 'style' && typeof value === 'object') {
          Object.keys(value).forEach(function (k) {
            if (k.indexOf('--') === 0) el.style.setProperty(k, String(value[k]));
            else el.style[k] = value[k];
          });
        } else if (key === 'on') {
          Object.keys(value).forEach(function (ev) { el.addEventListener(ev, value[ev]); });
        } else if (key === 'data') {
          Object.keys(value).forEach(function (k) { el.dataset[k] = value[k]; });
        } else if (key === 'html') {
          el.innerHTML = value;
        } else if (value === true) {
          el.setAttribute(key, '');
        } else {
          el.setAttribute(key, String(value));
        }
      });
    }
    appendChildren(el, Array.prototype.slice.call(arguments, 2));
    return el;
  }

  function clear(el) {
    while (el.firstChild) el.removeChild(el.firstChild);
    return el;
  }

  var numberFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });

  function pad(n) { return n < 10 ? '0' + n : String(n); }

  var fmt = {
    /** 1234567.891 → "1,234,567.89"; null → "—" */
    num: function (n) { return n == null || isNaN(n) ? '—' : numberFormat.format(Number(n)); },
    /** 0.734 → "73%"; digits = decimals */
    pct: function (v, digits) { return v == null || isNaN(v) ? '—' : (Number(v) * 100).toFixed(digits || 0) + '%'; },
    /** megabytes → "0.4 MB" | "12 MB" | "1.5 GB" | "2.3 TB" */
    mb: function (v) {
      if (v == null || isNaN(v)) return '—';
      v = Number(v);
      if (v >= 1024 * 1024) return (v / 1024 / 1024).toFixed(1) + ' TB';
      if (v >= 1024) return (v / 1024).toFixed(1) + ' GB';
      if (v >= 10) return Math.round(v) + ' MB';
      return v.toFixed(1) + ' MB';
    },
    /** milliseconds → "850 ms" | "12.3 s" | "4m 05s" | "1h 02m" */
    dur: function (ms) {
      if (ms == null || isNaN(ms)) return '—';
      ms = Math.max(0, Number(ms));
      if (ms < 1000) return Math.round(ms) + ' ms';
      var s = ms / 1000;
      if (s < 60) return s.toFixed(1) + ' s';
      var m = Math.floor(s / 60);
      if (m < 60) return m + 'm ' + pad(Math.floor(s % 60)) + 's';
      return Math.floor(m / 60) + 'h ' + pad(m % 60) + 'm';
    },
    /** ISO timestamp → local "2026-09-11 14:03" */
    ts: function (iso) {
      if (!iso) return '—';
      var d = new Date(iso);
      if (isNaN(d.getTime())) return String(iso);
      return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    },
    /** ISO timestamp → "just now" | "5 min ago" | "3 h ago" | "2 d ago" */
    rel: function (iso, now) {
      if (!iso) return '—';
      var t = new Date(iso).getTime();
      if (isNaN(t)) return String(iso);
      var s = Math.round(((now == null ? Date.now() : now) - t) / 1000);
      if (s < 45) return 'just now';
      var m = Math.round(s / 60);
      if (m < 60) return m + ' min ago';
      var hrs = Math.round(m / 60);
      if (hrs < 24) return hrs + ' h ago';
      return Math.round(hrs / 24) + ' d ago';
    },
  };

  function inline(text) {
    return text.split(/(`[^`]+`)/g).map(function (part) {
      if (part.length > 1 && part.charAt(0) === '`' && part.charAt(part.length - 1) === '`') {
        return '<code>' + esc(part.slice(1, -1)) + '</code>';
      }
      return esc(part).replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    }).join('');
  }

  /**
   * Tiny, safe Markdown subset for agent-written text: blank-line paragraphs, "-"/"*" bullet lists,
   * **bold** and `code`. Everything else is escaped. Returns an HTML string.
   */
  function mdLite(text) {
    var lines = String(text == null ? '' : text).replace(/\r\n?/g, '\n').split('\n');
    var out = [];
    var para = [];
    var list = [];
    function flushPara() {
      if (para.length) out.push('<p>' + para.map(inline).join(' ') + '</p>');
      para = [];
    }
    function flushList() {
      if (list.length) out.push('<ul>' + list.map(function (i) { return '<li>' + inline(i) + '</li>'; }).join('') + '</ul>');
      list = [];
    }
    lines.forEach(function (line) {
      var bullet = /^\s*[-*]\s+(.*)$/.exec(line);
      if (bullet) {
        flushPara();
        list.push(bullet[1]);
      } else if (!line.trim()) {
        flushPara();
        flushList();
      } else {
        flushList();
        para.push(line.trim());
      }
    });
    flushPara();
    flushList();
    return out.join('');
  }

  DBM.esc = esc;
  DBM.h = h;
  DBM.clear = clear;
  DBM.fmt = fmt;
  DBM.mdLite = mdLite;
})(globalThis.DBM = globalThis.DBM || {});
