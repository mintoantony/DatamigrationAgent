/* SETUP: enter, test and save the two connection strings. Secrets live only in this tab's memory until saved. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var SIDES = [
    { key: 'src', title: 'Source', hint: 'The database you migrate from.' },
    { key: 'tgt', title: 'Target', hint: 'The existing database you migrate into.' },
  ];
  var local = {
    src: { editing: false, value: '', reveal: false, result: null },
    tgt: { editing: false, value: '', reveal: false, result: null },
  };

  function metaList(meta) {
    if (!meta) return null;
    return h('dl', { class: 'meta' },
      h('dt', null, 'Server'), h('dd', { class: 'mono' }, meta.server),
      h('dt', null, 'Database'), h('dd', { class: 'mono' }, meta.database),
      h('dt', null, 'Version'), h('dd', null, meta.version),
      h('dt', null, 'Edition'), h('dd', null, meta.edition),
      h('dt', null, 'Collation'), h('dd', { class: 'mono' }, meta.databaseCollation +
        (meta.serverCollation && meta.serverCollation !== meta.databaseCollation ? ' (server ' + meta.serverCollation + ')' : '')),
      h('dt', null, 'Compatibility'), h('dd', null, String(meta.compatLevel)),
      h('dt', null, 'Sign-in'), h('dd', null, meta.authSummary));
  }

  function sideCard(ctx, side) {
    var conn = (ctx.state.connections && ctx.state.connections[side.key]) || { saved: false };
    var st = local[side.key];
    var transfer = ctx.state.phases.filter(function (p) { return p.name === 'transfer'; })[0];
    var locked = transfer && transfer.status !== 'pending';

    if (conn.saved && !st.editing) {
      return h('section', { class: 'card setup-card', 'aria-label': side.title + ' connection' },
        h('div', { class: 'card-h' }, C.icon('database'), h('span', { class: 'spacer' }, side.title), C.badge('approved', 'Saved')),
        h('div', { class: 'card-b' },
          h('div', { class: 'setup-describe' }, conn.describe),
          metaList(conn.meta),
          locked ? h('p', { class: 'small muted' }, 'Connections are locked while a transfer exists.')
            : h('div', { class: 'row' }, h('button', {
              type: 'button',
              class: 'btn btn-sm',
              on: { click: function () { st.editing = true; st.result = null; ctx.refresh(); } },
            }, 'Change'))));
    }

    var input = h('input', {
      id: 'cs-' + side.key,
      class: 'input mono',
      type: st.reveal ? 'text' : 'password',
      autocomplete: 'off',
      spellcheck: 'false',
      placeholder: 'Server=…;Database=…;Integrated Security=true;TrustServerCertificate=true',
      'aria-describedby': 'cs-hint-' + side.key,
    });
    input.value = st.value;
    input.addEventListener('input', function () { st.value = input.value; });

    var reveal = h('button', {
      type: 'button',
      class: 'btn icon-btn',
      'aria-label': st.reveal ? 'Hide connection string' : 'Show connection string',
      'aria-pressed': st.reveal ? 'true' : 'false',
      on: { click: function () { st.reveal = !st.reveal; input.type = st.reveal ? 'text' : 'password'; DBM.clear(reveal).appendChild(C.icon(st.reveal ? 'eyeOff' : 'eye')); } },
    }, C.icon(st.reveal ? 'eyeOff' : 'eye'));

    var resultHost = h('div', { 'aria-live': 'polite' }, renderResult(st.result));
    var testBtn = h('button', { type: 'button', class: 'btn' }, 'Test');
    var saveBtn = h('button', { type: 'button', class: 'btn btn-primary' }, 'Save');

    function value() {
      var v = input.value.trim();
      if (!v) { input.focus(); C.toast('Paste a connection string first.', 'warn'); }
      return v;
    }

    testBtn.addEventListener('click', function () {
      var cs = value();
      if (!cs) return;
      C.busy(testBtn, function () {
        return ctx.api.post('/api/connections/' + side.key + '/test', { connectionString: cs }).then(function (r) {
          st.result = r.ok ? { ok: true, meta: r.meta } : { ok: false, error: r.error };
        }, function (err) {
          st.result = { ok: false, error: C.errorText(err) };
        }).then(function () {
          DBM.clear(resultHost).appendChild(renderResult(st.result));
        });
      });
    });

    saveBtn.addEventListener('click', function () {
      var cs = value();
      if (!cs) return;
      C.busy(saveBtn, function () {
        return ctx.api.post('/api/connections/' + side.key, { connectionString: cs }).then(function () {
          st.value = '';
          st.editing = false;
          st.reveal = false;
          st.result = null;
          C.toast(side.title + ' connection saved.', 'ok');
          ctx.refresh();
        }, function (err) {
          st.result = { ok: false, error: C.errorText(err) };
          DBM.clear(resultHost).appendChild(renderResult(st.result));
        });
      });
    });

    // A real <form> (Enter = Test) keeps password managers and screen readers happy; nothing is ever submitted.
    var form = h('form', { class: 'card-b', autocomplete: 'off', on: { submit: function (e) { e.preventDefault(); testBtn.click(); } } },
        h('div', { class: 'field' },
          h('label', { class: 'field-label', for: 'cs-' + side.key }, 'Connection string'),
          h('div', { class: 'input-group' }, input, reveal),
          h('span', { class: 'field-hint', id: 'cs-hint-' + side.key }, side.hint + ' SQL login, Windows integrated and every Microsoft Entra ID mode work as-is.')),
        h('div', { class: 'row-wrap' }, testBtn, saveBtn,
          conn.saved ? h('button', {
            type: 'button',
            class: 'btn btn-ghost',
            on: { click: function () { st.editing = false; st.value = ''; st.result = null; ctx.refresh(); } },
          }, 'Cancel') : null),
        resultHost);

    return h('section', { class: 'card setup-card', 'aria-label': side.title + ' connection' },
      h('div', { class: 'card-h' }, C.icon('database'), h('span', { class: 'spacer' }, side.title),
        conn.saved ? C.badge('stale', 'Editing') : C.badge('pending', 'Not set')),
      form);
  }

  function renderResult(result) {
    if (!result) return h('span');
    if (result.ok) {
      return h('div', { class: 'stack-sm' },
        C.notice('ok', 'Connected to ' + result.meta.server + ' / ' + result.meta.database + '.'),
        metaList(result.meta));
    }
    return C.notice('err', result.error || 'Connection failed.');
  }

  DBM.views = DBM.views || {};
  DBM.views.setup = {
    title: 'Setup',
    render: function (root, ctx) {
      var c = ctx.state.connections || {};
      var both = c.src && c.src.saved && c.tgt && c.tgt.saved;
      root.appendChild(h('div', { class: 'page' },
        h('div', { class: 'page-h' }, h('div', null,
          h('h1', { class: 'h1' }, 'Connect the two databases'),
          h('p', { class: 'muted' }, 'Connection strings are encrypted on this computer and never shown to Claude, logs or reports.'))),
        h('div', { class: 'grid-2' }, SIDES.map(function (side) { return sideCard(ctx, side); })),
        both ? h('div', { style: { marginTop: '16px' } }, C.notice('info',
          'Both databases are connected. Discovery runs automatically — the next phases appear on the left as they become ready.')) : null));
    },
  };
})(window.DBM = window.DBM || {});
