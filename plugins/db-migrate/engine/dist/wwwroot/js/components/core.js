/* Shared UI components (C9). Everything renders with DBM.h, so text is never parsed as HTML. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components = DBM.components || {};

  var PHASE_TITLES = {
    setup: 'Setup', discovery: 'Discovery', analysis: 'Analysis', mapping: 'Mapping', sql: 'SQL',
    ready: 'Execute', transfer: 'Transfer', complete: 'Report',
  };
  var STATUS_LABELS = {
    pending: 'Pending', running: 'Running', drafting: 'Drafting', awaiting_review: 'Awaiting review',
    reworking: 'Reworking', approved: 'Approved', stale: 'Stale', queued: 'Queued', done: 'Done', failed: 'Failed',
    draft: 'Draft', open: 'Sent to Claude', addressed: 'Addressed', declined: 'Declined', paused: 'Paused',
    completed: 'Completed', cancelled: 'Cancelled',
  };

  DBM.phaseTitle = function (name) { return PHASE_TITLES[name] || name; };
  DBM.statusLabel = function (status) { return STATUS_LABELS[status] || (status ? String(status).replace(/_/g, ' ') : ''); };

  /* Inline SVG icons (trusted markup, 24x24 stroke icons). */
  var ICONS = {
    comment: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
    monitor: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>',
    check: '<path d="M20 6 9 17l-5-5"/>',
    x: '<path d="M18 6 6 18M6 6l12 12"/>',
    alert: '<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h16.9a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
    pause: '<rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/>',
    play: '<path d="M6 4l14 8-14 8z"/>',
    refresh: '<path d="M21 12a9 9 0 1 1-2.6-6.4L21 8"/><path d="M21 3v5h-5"/>',
    eye: '<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/>',
    eyeOff: '<path d="M17.9 17.9A10.1 10.1 0 0 1 12 20C5 20 1 12 1 12a18.5 18.5 0 0 1 5.1-5.9M9.9 4.2A9.1 9.1 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.2 3.2M1 1l22 22"/><path d="M14.1 14.1a3 3 0 1 1-4.2-4.2"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5M12 7v5l4 2"/>',
    database: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5"/><path d="M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
    clock: '<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>',
  };

  C.icon = function (name) {
    return h('span', { class: 'icon', 'aria-hidden': 'true', html: '<svg viewBox="0 0 24 24">' + (ICONS[name] || '') + '</svg>' });
  };

  C.badge = function (status, label) {
    return h('span', { class: 'badge st-' + status }, label || DBM.statusLabel(status));
  };

  /** Left phase list. opts: {current, onSelect(name)} */
  C.stepper = function (state, opts) {
    opts = opts || {};
    var list = h('ol', { class: 'stepper-list' });
    state.phases.forEach(function (p, i) {
      var active = p.name === opts.current;
      var status = DBM.statusLabel(p.status) + (p.currentVersion != null && p.status !== 'pending' ? ' · v' + p.currentVersion : '');
      list.appendChild(h('li', null,
        h('button', {
          type: 'button',
          class: ['step', 'st-' + p.status, active && 'is-active'],
          'aria-current': active ? 'step' : null,
          title: DBM.phaseTitle(p.name) + ': ' + status,
          on: { click: function () { if (opts.onSelect) opts.onSelect(p.name); } },
        },
        h('span', { class: 'step-dot', 'aria-hidden': 'true' }, p.status === 'approved' ? '✓' : String(i + 1)),
        h('span', { class: 'step-text' },
          h('span', { class: 'step-name' }, DBM.phaseTitle(p.name)),
          h('span', { class: 'step-status' }, status)))));
    });
    return list;
  };

  /** Top bar. opts: {live, theme ('system'|'light'|'dark'), onPause, onResume, onTheme, title} */
  C.topbar = function (state, opts) {
    opts = opts || {};
    var project = (state && state.project) || { name: opts.title || 'db-migrate', paused: false, agentOnline: false };
    var themeIcon = opts.theme === 'dark' ? 'moon' : opts.theme === 'light' ? 'sun' : 'monitor';
    return h('div', { class: 'topbar-inner' },
      h('div', { class: 'brand' },
        h('span', { class: 'brand-mark', 'aria-hidden': 'true' }, 'dbm'),
        h('span', { class: 'brand-name' }, project.name)),
      h('span', { class: 'spacer' }),
      state ? h('span', {
        class: ['pill', project.agentOnline ? 'pill-ok' : 'pill-off'],
        title: project.agentOnline ? 'Claude is connected and waiting for you' : 'Claude is not connected',
      }, h('span', { class: 'dot' }), h('span', { class: 'pill-label' }, project.agentOnline ? 'Agent online' : 'Agent offline')) : null,
      state && opts.live === false ? h('span', { class: 'pill pill-warn', title: 'Live updates interrupted' },
        h('span', { class: 'dot' }), h('span', { class: 'pill-label' }, 'Reconnecting…')) : null,
      state ? (project.paused
        ? h('button', { type: 'button', class: 'btn btn-primary btn-sm', on: { click: opts.onResume } }, C.icon('play'), 'Resume')
        : h('button', { type: 'button', class: 'btn btn-sm', on: { click: opts.onPause } }, C.icon('pause'), 'Pause')) : null,
      h('button', {
        type: 'button',
        class: 'btn btn-ghost btn-sm icon-btn',
        'aria-label': 'Theme: ' + (opts.theme || 'system') + ' (click to change)',
        title: 'Theme: ' + (opts.theme || 'system'),
        on: { click: opts.onTheme },
      }, C.icon(themeIcon)));
  };

  C.banner = function (kind, content, action) {
    var icon = kind === 'err' || kind === 'warn' ? 'alert' : 'info';
    return h('div', { class: 'banner banner-' + kind, role: kind === 'err' ? 'alert' : null },
      C.icon(icon), h('div', { class: 'spacer' }, content), action || null);
  };

  C.notice = function (kind, content) {
    var icon = kind === 'ok' ? 'check' : kind === 'info' ? 'info' : 'alert';
    return h('div', { class: 'notice notice-' + kind }, C.icon(icon), h('div', { class: 'wrap-anywhere' }, content));
  };

  C.kpi = function (label, value, sub) {
    return h('div', { class: 'kpi' },
      h('div', { class: 'kpi-l' }, label),
      h('div', { class: 'kpi-v' }, value == null ? '—' : value),
      sub ? h('div', { class: 'kpi-s' }, sub) : null);
  };

  /**
   * columns: [{key, label, num?, class?, render?(row) → node|string}]
   * opts: {compact, sticky, onRowClick(row), rowClass(row), empty, activeRow(row) → bool}
   */
  C.table = function (columns, rows, opts) {
    opts = opts || {};
    var thead = h('thead', null, h('tr', null, columns.map(function (c) {
      return h('th', { class: [c.num && 'num', c.class], scope: 'col' }, c.label);
    })));
    var tbody = h('tbody');
    if (!rows.length) {
      tbody.appendChild(h('tr', null, h('td', { colspan: columns.length, class: 'muted' }, opts.empty || 'Nothing to show.')));
    }
    rows.forEach(function (row) {
      var tr = h('tr', {
        class: [opts.onRowClick && 'tr-click', opts.rowClass && opts.rowClass(row), opts.activeRow && opts.activeRow(row) && 'is-active'],
        tabindex: opts.onRowClick ? '0' : null,
        on: opts.onRowClick ? {
          click: function () { opts.onRowClick(row); },
          keydown: function (e) { if (e.key === 'Enter') opts.onRowClick(row); },
        } : null,
      });
      columns.forEach(function (c) {
        var value = c.render ? c.render(row) : row[c.key];
        tr.appendChild(h('td', { class: [c.num && 'num', c.class] }, value == null ? '' : value));
      });
      tbody.appendChild(tr);
    });
    return h('div', { class: 'tbl-wrap' },
      h('table', { class: ['tbl', opts.compact && 'tbl-compact', opts.sticky && 'tbl-sticky'] }, thead, tbody));
  };

  /** opts: {label (string|true for %), kind: 'ok'|'warn'|'err', indeterminate} */
  C.progress = function (value, max, opts) {
    opts = opts || {};
    var ratio = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
    var bar = h('div', {
      class: ['bar', opts.kind && 'bar-' + opts.kind, opts.indeterminate && 'bar-indeterminate'],
      style: { '--v': ratio.toFixed(4) },
      role: 'progressbar',
      'aria-valuemin': '0',
      'aria-valuemax': String(max || 0),
      'aria-valuenow': opts.indeterminate ? null : String(value || 0),
    });
    if (!opts.label) return bar;
    return h('div', { class: 'progress' }, bar,
      h('span', { class: 'progress-label' }, opts.label === true ? DBM.fmt.pct(ratio) : opts.label));
  };

  /** Small line chart; values: numbers. Returns an SVG element. */
  C.sparkline = function (values, opts) {
    opts = opts || {};
    var w = opts.width || 120;
    var hh = opts.height || 28;
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'spark');
    svg.setAttribute('width', String(w));
    svg.setAttribute('height', String(hh));
    svg.setAttribute('viewBox', '0 0 ' + w + ' ' + hh);
    svg.setAttribute('aria-hidden', 'true');
    var vals = (values || []).filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (vals.length < 2) return svg;
    var max = Math.max.apply(null, vals);
    var min = Math.min.apply(null, vals.concat([0]));
    var span = max - min || 1;
    var pts = vals.map(function (v, i) {
      return (i * (w / (vals.length - 1))).toFixed(1) + ',' + (hh - 2 - ((v - min) / span) * (hh - 4)).toFixed(1);
    });
    var area = document.createElementNS(ns, 'polygon');
    area.setAttribute('class', 'spark-area');
    area.setAttribute('points', '0,' + hh + ' ' + pts.join(' ') + ' ' + w + ',' + hh);
    var line = document.createElementNS(ns, 'polyline');
    line.setAttribute('points', pts.join(' '));
    svg.appendChild(area);
    svg.appendChild(line);
    return svg;
  };

  /** modal({title, body (node|string), confirmText, cancelText, danger, requireText, requireCheck}) → Promise<bool>
   *  requireCheck: a label (node|string) for a checkbox that must be ticked before Confirm arms (ruling 186). */
  C.modal = function (o) {
    return new Promise(function (resolve) {
      var previous = document.activeElement;
      var input = o.requireText ? h('input', { class: 'input mono', type: 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Type ' + o.requireText + ' to confirm' }) : null;
      var check = o.requireCheck ? h('input', { type: 'checkbox', class: 'check modal-require-check' }) : null;
      var confirm = h('button', { type: 'button', class: ['btn', o.danger ? 'btn-danger' : 'btn-primary'], disabled: !!(o.requireText || o.requireCheck) }, o.confirmText || 'OK');
      function arm() { confirm.disabled = (!!input && input.value !== o.requireText) || (!!check && !check.checked); }
      var cancel = h('button', { type: 'button', class: 'btn' }, o.cancelText || 'Cancel');
      var titleId = 'modal-title-' + Date.now();
      var dialog = h('div', { class: 'modal', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId },
        h('div', { class: 'modal-h' }, h('h2', { class: 'h2', id: titleId }, o.title || 'Confirm')),
        h('div', { class: 'modal-b stack-sm' },
          typeof o.body === 'string' ? h('p', { style: { margin: '0' } }, o.body) : o.body,
          o.requireText ? h('label', { class: 'field' },
            h('span', { class: 'field-hint' }, 'Type ', h('code', null, o.requireText), ' to confirm'), input) : null,
          check ? h('label', { class: 'exe-choice' }, check, h('span', null, o.requireCheck)) : null),
        h('div', { class: 'modal-f' }, o.cancelText === null ? null : cancel, confirm));
      var backdrop = h('div', { class: 'modal-backdrop' }, dialog);

      function close(result) {
        document.removeEventListener('keydown', onKey, true);
        backdrop.remove();
        if (previous && previous.focus) previous.focus();
        resolve(result);
      }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); close(false); }
        if (e.key === 'Tab') {
          var focusables = dialog.querySelectorAll('button:not([disabled]), input');
          if (!focusables.length) return;
          var first = focusables[0];
          var last = focusables[focusables.length - 1];
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
      }
      if (input) {
        input.addEventListener('input', arm);
        input.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !confirm.disabled) close(true); });
      }
      if (check) check.addEventListener('change', arm);
      confirm.addEventListener('click', function () { close(true); });
      cancel.addEventListener('click', function () { close(false); });
      backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop) close(false); });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(backdrop);
      (input || confirm).focus();
    });
  };

  /** toast(message, kind: 'ok'|'err'|'warn'|'info') */
  C.toast = function (message, kind) {
    var host = document.getElementById('toasts');
    if (!host) return;
    kind = kind || 'info';
    var el = h('div', { class: 'toast toast-' + kind }, C.icon(kind === 'ok' ? 'check' : kind === 'info' ? 'info' : 'alert'), h('div', null, message));
    host.appendChild(el);
    setTimeout(function () { el.remove(); }, kind === 'err' ? 8000 : 4000);
  };

  /** The single right-hand drawer. open(title, content, {tabs}) ; close() ; isOpen() */
  C.drawer = (function () {
    var onCloseHandler = null;
    function el() { return document.getElementById('drawer'); }
    function onKey(e) { if (e.key === 'Escape') api.close(); }
    var api = {
      open: function (title, content, options) {
        var d = el();
        options = options || {};
        DBM.clear(d);
        d.appendChild(h('div', { class: 'drawer-h' },
          h('h2', { class: 'h2', id: 'drawer-title' }, title),
          h('button', { type: 'button', class: 'btn btn-ghost btn-sm icon-btn', 'aria-label': 'Close panel', on: { click: function () { api.close(); } } }, C.icon('x'))));
        if (options.tabs) d.appendChild(options.tabs);
        d.appendChild(h('div', { class: 'drawer-b' }, content));
        d.classList.add('drawer-open');
        d.setAttribute('aria-hidden', 'false');
        document.addEventListener('keydown', onKey);
        onCloseHandler = options.onClose || null;
        var focusTarget = d.querySelector('textarea, input, .drawer-b button') || d.querySelector('button');
        if (focusTarget && !options.keepFocus) focusTarget.focus();
      },
      close: function () {
        var d = el();
        if (!d.classList.contains('drawer-open')) return;
        d.classList.remove('drawer-open');
        d.setAttribute('aria-hidden', 'true');
        document.removeEventListener('keydown', onKey);
        if (onCloseHandler) { var fn = onCloseHandler; onCloseHandler = null; fn(); }
      },
      isOpen: function () { var d = el(); return !!d && d.classList.contains('drawer-open'); },
    };
    return api;
  })();

  C.emptyState = function (title, text, action) {
    return h('div', { class: 'empty' },
      C.icon('info'), h('div', { class: 'h3' }, title), text ? h('p', { class: 'muted', style: { margin: '0 0 12px' } }, text) : null, action || null);
  };

  /** Labelled form field. */
  C.field = function (label, control, hint) {
    return h('label', { class: 'field' }, h('span', { class: 'field-label' }, label), control, hint ? h('span', { class: 'field-hint' }, hint) : null);
  };

  /** Runs an async action while the button shows a spinner; returns the action's promise. */
  C.busy = function (button, action) {
    button.classList.add('is-loading');
    button.disabled = true;
    return Promise.resolve().then(action).finally(function () {
      button.classList.remove('is-loading');
      button.disabled = false;
    });
  };

  /** Human text for an ApiError (includes blocker details). */
  C.errorText = function (err) {
    if (!err) return 'Something went wrong.';
    var details = err.details && err.details.length ? ' ' + err.details.join(' · ') : '';
    return (err.message || String(err)) + details;
  };
})(window.DBM = window.DBM || {});
