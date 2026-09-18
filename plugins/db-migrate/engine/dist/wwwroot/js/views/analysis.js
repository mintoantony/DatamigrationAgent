/* Analysis view (T2.8): KPIs, source/target summaries, narrative, findings, tables with drill-down drawer, FK graph, export. */
(function (DBM) {
  'use strict';

  var SEVERITIES = ['critical', 'high', 'medium', 'low', 'info'];
  var GRAPH_MAX = 150;
  var ui = { sev: 'all', q: '', tab: 'src', tableQ: '', graph: 'tgt', graphFit: false };   // survives re-renders
  var tableCache = {};

  // ---------- small helpers ----------

  function el(tag, attrs) {
    var node = DBM.h(tag, attrs || {});
    for (var i = 2; i < arguments.length; i++) add(node, arguments[i]);
    return node;
  }
  function add(node, kid) {
    if (kid == null || kid === false) return;
    if (Array.isArray(kid)) { kid.forEach(function (k) { add(node, k); }); return; }
    node.appendChild(typeof kid === 'string' || typeof kid === 'number' ? document.createTextNode(String(kid)) : kid);
  }
  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }
  function num(n) { return n == null ? '—' : Number(n).toLocaleString('en-US'); }
  function mb(v) {
    if (v == null) return '—';
    if (v === 0) return '0 MB';
    if (v < 1) return v.toFixed(2) + ' MB';
    if (v < 1024) return v.toFixed(1) + ' MB';
    return (v / 1024).toFixed(2) + ' GB';
  }
  function pct(r) { return r === 0 ? '0%' : r < 0.01 ? '<1%' : Math.round(r * 100) + '%'; }
  function minutes(m) { return m == null ? '—' : m < 1 ? '< 1 min' : m < 60 ? m.toFixed(1) + ' min' : (m / 60).toFixed(1) + ' h'; }
  function cap(s) { return s.charAt(0).toUpperCase() + s.slice(1); }
  function isExport() { return typeof window !== 'undefined' && !!window.DBM_EXPORT; }
  function exportData() { return isExport() ? window.DBM_EXPORT.payload : null; }
  function api(ctx) { return (ctx && ctx.api) || DBM.api; }
  function toast(ctx, msg, kind) {
    if (ctx && typeof ctx.toast === 'function') ctx.toast(msg, kind);
    else if (DBM.components && DBM.components.toast) DBM.components.toast(msg, kind);
  }
  function commentable(ctx, node, anchor, label) {
    if (!isExport() && ctx && typeof ctx.commentable === 'function') ctx.commentable(node, anchor, label);
    return node;
  }
  function tag(text, extra) { return el('span', { class: 'tag' + (extra ? ' ' + extra : '') }, text); }
  function sevBadge(sev) { return el('span', { class: 'badge sev-' + sev }, sev); }
  function bar(value, label) {
    var b = el('span', { class: 'bar ana-bar', role: 'img', 'aria-label': label });
    b.style.setProperty('--v', String(Math.max(0, Math.min(1, value))));
    return b;
  }
  function th(text, cls) { return el('th', { scope: 'col', class: cls || '' }, text); }

  function payloadOf(ctx) {
    var d = exportData();
    if (d && d.artifact) return d.artifact.payload;
    var a = ctx && ctx.artifact;
    if (!a) return null;
    if (a.payload) return a.payload;
    if (a.current && a.current.payload) return a.current.payload;
    return a.findings ? a : null;
  }
  function artifactMeta(ctx) {
    var d = exportData();
    if (d && d.artifact) return d.artifact;
    var a = ctx && ctx.artifact;
    if (!a) return null;
    return a.current || a;
  }
  function typeDisplay(c) {
    var t = c.dataType;
    if (['char', 'varchar', 'nchar', 'nvarchar', 'binary', 'varbinary'].indexOf(t) >= 0) return t + '(' + (c.maxLength === -1 ? 'max' : c.maxLength) + ')';
    if (t === 'decimal' || t === 'numeric') return t + '(' + c.precision + ',' + c.scale + ')';
    if (t === 'datetime2' || t === 'datetimeoffset' || t === 'time') return t + '(' + c.scale + ')';
    if (t === 'timestamp') return 'rowversion';
    return t;
  }
  function countBySeverity(findings) {
    var counts = { critical: 0, high: 0, medium: 0, low: 0, info: 0 };
    Object.keys(findings || {}).forEach(function (id) { counts[findings[id].severity] = (counts[findings[id].severity] || 0) + 1; });
    return counts;
  }
  function anchorTarget(anchor) {
    var m = /^(table|column):(src|tgt):(.+)$/.exec(anchor || '');
    if (!m) return null;
    if (m[1] === 'table') return { side: m[2], key: m[3] };
    var i = m[3].lastIndexOf('.');
    return { side: m[2], key: m[3].slice(0, i), column: m[3].slice(i + 1) };
  }
  function findingsByTable(findings) {
    var rank = { critical: 0, high: 1, medium: 2, low: 3, info: 4 };
    var map = {};
    Object.keys(findings || {}).forEach(function (id) {
      var f = findings[id], t = anchorTarget(f.anchor);
      if (!t) return;
      var k = t.side + ':' + t.key, e = map[k] || (map[k] = { count: 0, worst: 'info' });
      e.count++;
      if (rank[f.severity] < rank[e.worst]) e.worst = f.severity;
    });
    return map;
  }

  // mdLite: paragraphs, "- " / "* " bullets, **bold**, `code` — builds DOM, never HTML strings.
  function inline(text) {
    var out = [], re = /(\*\*[^*]+\*\*|`[^`]+`)/g, last = 0, m;
    text = String(text || '');
    while ((m = re.exec(text))) {
      if (m.index > last) out.push(text.slice(last, m.index));
      out.push(m[0].charAt(0) === '`' ? el('code', { class: 'mono' }, m[0].slice(1, -1)) : el('strong', {}, m[0].slice(2, -2)));
      last = re.lastIndex;
    }
    if (last < text.length) out.push(text.slice(last));
    return out;
  }
  function md(text) {
    var root = el('div', { class: 'ana-md' }), para = [], list = null;
    function flush() { if (para.length) { root.appendChild(el('p', {}, inline(para.join(' ')))); para = []; } }
    String(text || '').split(/\r?\n/).forEach(function (line) {
      var bullet = /^\s*[-*]\s+(.*)$/.exec(line);
      if (bullet) { flush(); if (!list) { list = el('ul', { class: 'ana-list' }); root.appendChild(list); } list.appendChild(el('li', {}, inline(bullet[1]))); return; }
      list = null;
      if (!line.trim()) { flush(); return; }
      para.push(line.trim());
    });
    flush();
    return root;
  }
  function list(tagName, items) {
    var l = el(tagName, { class: 'ana-list' });
    (items || []).forEach(function (t) { l.appendChild(el('li', {}, inline(t))); });
    return l;
  }

  // ---------- data ----------

  function loadCatalog(ctx) {
    var d = exportData();
    if (d) return Promise.resolve(d.catalog || { src: [], tgt: [] });
    return Promise.all([api(ctx).get('/api/catalog/src'), api(ctx).get('/api/catalog/tgt')])
      .then(function (r) { return { src: r[0] || [], tgt: r[1] || [] }; });
  }
  function loadTable(ctx, side, key) {
    var d = exportData();
    if (d) return Promise.resolve(d.tables && d.tables[side] ? d.tables[side][key] || null : null);
    var k = side + ':' + key;
    if (tableCache[k]) return Promise.resolve(tableCache[k]);
    return api(ctx).get('/api/catalog/' + side + '/table/' + encodeURIComponent(key)).then(function (t) { tableCache[k] = t; return t; });
  }

  // ---------- actions ----------

  function rediscover(ctx) {
    DBM.components.modal({
      title: 'Re-run discovery?',
      body: 'Both databases are extracted and profiled again. Analysis and every later phase become stale and need a new review.',
      confirmText: 'Re-run discovery'
    }).then(function (ok) {
      if (!ok) return null;
      return api(ctx).post('/api/rediscover').then(function () {
        toast(ctx, 'Discovery started — the analysis refreshes when it finishes.', 'ok');
        if (ctx && ctx.refresh) ctx.refresh();
      });
    }).catch(function (err) { toast(ctx, (err && err.message) || 'Could not start discovery', 'err'); });
  }
  function exportHtml(ctx, button) {
    button.classList.add('is-loading');
    button.disabled = true;
    api(ctx).post('/api/export/analysis').then(function (r) {
      toast(ctx, 'Saved ' + r.path, 'ok');
      var a = document.createElement('a');
      a.href = r.download;
      a.download = r.file;
      document.body.appendChild(a);
      a.click();
      a.remove();
    }).catch(function (err) {
      toast(ctx, 'Export failed: ' + ((err && err.message) || err), 'err');
    }).then(function () {
      button.classList.remove('is-loading');
      button.disabled = false;
    });
  }

  // ---------- sections ----------

  function header(ctx, p) {
    var meta = artifactMeta(ctx);
    var actions = el('div', { class: 'toolbar ana-actions' });
    if (!isExport()) {
      actions.appendChild(el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { rediscover(ctx); } } }, 'Re-run discovery'));
      actions.appendChild(el('button', { class: 'btn btn-sm', type: 'button', on: { click: function (ev) { exportHtml(ctx, ev.currentTarget); } } }, 'Export HTML'));
    }
    var sub = p.source.database + ' → ' + p.target.database;
    if (meta && meta.version != null) sub += ' · v' + meta.version + (meta.author ? ' by ' + meta.author : '');
    if (isExport() && exportData().exportedAt) sub += ' · exported ' + exportData().exportedAt.slice(0, 16).replace('T', ' ') + ' UTC';
    return el('div', { class: 'page-h ana-head' },
      el('div', { class: 'ana-title' }, el('h1', { class: 'h1' }, 'Analysis'), el('p', { class: 'muted small' }, sub)),
      el('div', { class: 'spacer' }),
      actions);
  }

  function kpiRow(p) {
    var c = countBySeverity(p.findings), k = DBM.components.kpi;
    var total = Object.keys(p.findings || {}).length;
    return el('div', { class: 'grid-kpi ana-kpis' },
      k('Tables', num(p.source.tables) + ' → ' + num(p.target.tables), num(p.source.columns) + ' → ' + num(p.target.columns) + ' columns'),
      k('Source rows', num(p.source.rows), mb(p.source.sizeMb)),
      k('Findings', num(total), c.critical + c.high ? c.critical + ' critical · ' + c.high + ' high' : 'no critical or high'),
      k('Est. transfer', minutes(p.estimates.estimatedMinutes), num(p.estimates.totalRows) + ' rows'),
      k('Target triggers', num(p.target.triggers), num(p.target.views) + ' views · ' + num(p.target.procedures) + ' procs'));
  }

  function dbCard(title, side, db, other) {
    var rows = [
      ['Server', db.server, 'mono'],
      ['Database', db.database, 'mono'],
      ['Version', db.version + (db.productVersion ? ' (' + db.productVersion + ')' : '')],
      ['Edition', db.edition],
      ['Collation', db.collation, 'mono', db.collation !== other.collation ? 'differs' : null],
      ['Compat level', String(db.compatLevel), 'num', side === 'tgt' && db.compatLevel < other.compatLevel ? 'lower than source' : null],
      ['Objects', num(db.tables) + ' tables · ' + num(db.columns) + ' columns · ' + num(db.views) + ' views · ' + num(db.procedures) + ' procs · ' + num(db.functions) + ' functions · ' + num(db.triggers) + ' triggers'],
      ['Data', num(db.rows) + ' rows · ' + mb(db.sizeMb)]
    ];
    var dl = el('dl', { class: 'ana-kv' });
    rows.forEach(function (r) {
      dl.appendChild(el('dt', {}, r[0]));
      dl.appendChild(el('dd', {}, el('span', { class: r[2] || '' }, r[1] == null || r[1] === '' ? '—' : r[1]), r[3] ? tag(r[3], 'ana-warn') : null));
    });
    return el('section', { class: 'card ana-db', 'aria-label': title + ' database' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, title), el('span', { class: 'pill' }, side)),
      el('div', { class: 'card-b' }, dl));
  }

  function narrativeCard(ctx, p) {
    var n = p.narrative, body = el('div', { class: 'card-b ana-narr' });
    if (!n) {
      body.appendChild(el('div', { class: 'empty' },
        el('p', {}, 'The schema-analyst has not written the summary yet.'),
        el('p', { class: 'muted small' }, 'It is drafted automatically while Claude is running the /db-migrate loop; otherwise run /db-migrate resume.')));
    } else {
      body.appendChild(md(n.summary));
      if (n.risks && n.risks.length) body.appendChild(el('div', { class: 'ana-sub' }, el('h3', { class: 'h3' }, 'Risks'), list('ul', n.risks)));
      if (n.recommendations && n.recommendations.length) body.appendChild(el('div', { class: 'ana-sub' }, el('h3', { class: 'h3' }, 'Recommendations'), list('ol', n.recommendations)));
    }
    var card = el('section', { class: 'card ana-narrative' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Summary & recommendations'), el('span', { class: 'muted small' }, n ? 'schema-analyst' : 'pending')),
      body);
    return commentable(ctx, card, 'narrative', 'Narrative');
  }

  function findingItem(ctx, id, f, drawer) {
    var t = anchorTarget(f.anchor);
    var obj = t
      ? el('button', { type: 'button', class: 'btn btn-ghost btn-sm mono ana-obj', title: 'Open ' + t.key, on: { click: function () { drawer.open(t.side, t.key, t.column); } } }, f.object)
      : el('span', { class: 'mono small ana-obj' }, f.object);
    var item = el('article', { class: 'ana-finding', role: 'listitem', data: { sev: f.severity } },
      el('div', { class: 'ana-finding-h' },
        sevBadge(f.severity),
        el('span', { class: 'mono small muted' }, id),
        el('span', { class: 'small ana-rule' }, f.rule + ' · ' + f.title),
        el('span', { class: 'pill' }, f.side),
        el('span', { class: 'spacer' }),
        obj),
      el('p', { class: 'ana-msg' }, f.message),
      f.commentary ? el('div', { class: 'ana-commentary' }, el('span', { class: 'small muted' }, 'Analyst'), el('p', {}, inline(f.commentary))) : null);
    return commentable(ctx, item, 'finding:' + id, 'Finding ' + id);
  }

  function findingsCard(ctx, p, drawer) {
    var entries = Object.keys(p.findings || {}).map(function (id) { return { id: id, f: p.findings[id] }; });
    var counts = countBySeverity(p.findings);
    var chips = el('div', { class: 'row-wrap ana-chips', role: 'group', 'aria-label': 'Filter by severity' });
    var search = el('input', { class: 'input ana-search', type: 'search', placeholder: 'Filter findings', 'aria-label': 'Filter findings' });
    var listNode = el('div', { class: 'ana-findings', role: 'list' });
    search.value = ui.q;
    if (ui.sev !== 'all' && !counts[ui.sev]) ui.sev = 'all';

    function renderChips() {
      clear(chips);
      ['all'].concat(SEVERITIES).forEach(function (s) {
        var n = s === 'all' ? entries.length : counts[s];
        if (s !== 'all' && !n) return;
        chips.appendChild(el('button', {
          type: 'button', class: 'chip' + (s === 'all' ? '' : ' ana-chip-' + s) + (ui.sev === s ? ' is-active' : ''),
          'aria-pressed': ui.sev === s ? 'true' : 'false',
          on: { click: function () { ui.sev = s; renderChips(); renderList(); } }
        }, (s === 'all' ? 'All' : cap(s)) + ' ', el('span', { class: 'num' }, String(n))));
      });
    }
    function renderList() {
      clear(listNode);
      var q = ui.q.trim().toLowerCase();
      var shown = entries.filter(function (e) {
        if (ui.sev !== 'all' && e.f.severity !== ui.sev) return false;
        return !q || (e.id + ' ' + e.f.rule + ' ' + e.f.title + ' ' + e.f.object + ' ' + e.f.message).toLowerCase().indexOf(q) >= 0;
      });
      if (!shown.length) { listNode.appendChild(el('p', { class: 'muted ana-none' }, entries.length ? 'No findings match the filter.' : 'No findings — nothing stood out.')); return; }
      shown.forEach(function (e) { listNode.appendChild(findingItem(ctx, e.id, e.f, drawer)); });
    }
    search.addEventListener('input', function () { ui.q = search.value; renderList(); });
    renderChips();
    renderList();
    return el('section', { class: 'card ana-findings-card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Findings'), el('span', { class: 'muted small' }, num(entries.length) + ' from rule checks')),
      el('div', { class: 'card-b stack' }, el('div', { class: 'toolbar ana-filter' }, chips, el('span', { class: 'spacer' }), search), listNode));
  }

  function sideTabs(current, counts, onPick, label) {
    var tabs = el('div', { class: 'row ana-tabs', role: 'tablist', 'aria-label': label });
    [['src', 'Source'], ['tgt', 'Target']].forEach(function (s) {
      tabs.appendChild(el('button', {
        type: 'button', role: 'tab', class: 'chip' + (current === s[0] ? ' is-active' : ''),
        'aria-selected': current === s[0] ? 'true' : 'false',
        on: { click: function () { onPick(s[0]); } }
      }, s[1] + (counts ? ' ' + counts[s[0]] : '')));
    });
    return tabs;
  }

  function tablesCard(ctx, p, cat, drawer) {
    var byTable = findingsByTable(p.findings);
    var head = el('div', { class: 'toolbar ana-filter' });
    var filter = el('input', { class: 'input ana-search', type: 'search', placeholder: 'Filter tables', 'aria-label': 'Filter tables' });
    var wrap = el('div', { class: 'ana-tbl-wrap' });
    filter.value = ui.tableQ;
    filter.addEventListener('input', function () { ui.tableQ = filter.value; renderTable(); });

    function renderHead() {
      clear(head);
      head.appendChild(sideTabs(ui.tab, { src: cat.src.length, tgt: cat.tgt.length }, function (s) { ui.tab = s; renderHead(); renderTable(); }, 'Catalog side'));
      head.appendChild(el('span', { class: 'spacer' }));
      head.appendChild(filter);
    }
    function renderTable() {
      clear(wrap);
      var q = ui.tableQ.trim().toLowerCase();
      var rows = (cat[ui.tab] || []).filter(function (t) { return !q || t.key.toLowerCase().indexOf(q) >= 0; });
      if (!rows.length) { wrap.appendChild(el('p', { class: 'muted' }, 'No tables match.')); return; }
      var tbody = el('tbody');
      rows.forEach(function (t) {
        var side = ui.tab, fx = byTable[side + ':' + t.key];
        function open(ev) {
          if (ev && ev.target && ev.target.closest && ev.target.closest('button')) return;   // e.g. the comment button
          drawer.open(side, t.key);
        }
        var tr = el('tr', {
          class: 'tr-click', tabindex: 0, 'aria-label': 'Open ' + t.key,
          on: { click: open, keydown: function (ev) { if (ev.key === 'Enter') open(); } }
        },
          el('td', { class: 'mono ana-key' }, t.key),
          el('td', { class: 'num' }, num(t.rows)),
          el('td', { class: 'num' }, mb(t.sizeMb)),
          el('td', { class: 'num' }, String(t.columns)),
          el('td', {}, t.hasPk ? el('span', { class: 'small' }, 'PK') : tag('no PK', 'ana-warn')),
          el('td', { class: 'num' }, t.fkOut + ' / ' + t.fkIn),
          el('td', { class: 'num' }, t.triggers ? String(t.triggers) : '—'),
          el('td', { class: 'ana-flags' }, t.isHeap ? tag('heap') : null,
            fx ? el('span', { class: 'badge sev-' + fx.worst, title: fx.count + ' finding(s), worst ' + fx.worst }, String(fx.count)) : null));
        commentable(ctx, tr, 'table:' + side + ':' + t.key, 'Table ' + t.key);
        tbody.appendChild(tr);
      });
      wrap.appendChild(el('table', { class: 'tbl tbl-compact tbl-sticky ana-tbl' },
        el('thead', {}, el('tr', {}, th('Table'), th('Rows', 'num'), th('Size', 'num'), th('Cols', 'num'), th('Key'), th('FK out / in', 'num'), th('Triggers', 'num'), th('Flags'))),
        tbody));
    }
    renderHead();
    renderTable();
    return el('section', { class: 'card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Tables'), el('span', { class: 'muted small' }, 'click a row for columns and profiles')),
      el('div', { class: 'card-b stack' }, head, wrap));
  }

  function graphCard(ctx, cat, drawer) {
    var head = el('div', { class: 'toolbar ana-filter' });
    var host = el('div', { class: 'ana-graph' });
    var fitBtn = el('button', { type: 'button', class: 'btn btn-ghost btn-sm', 'aria-pressed': ui.graphFit ? 'true' : 'false', on: { click: function () { ui.graphFit = !ui.graphFit; fitBtn.setAttribute('aria-pressed', ui.graphFit ? 'true' : 'false'); fitBtn.textContent = ui.graphFit ? 'Actual size' : 'Fit all'; draw(); } } }, ui.graphFit ? 'Actual size' : 'Fit all');

    function renderHead() {
      clear(head);
      head.appendChild(sideTabs(ui.graph, null, function (s) { ui.graph = s; renderHead(); draw(); }, 'Graph side'));
      head.appendChild(el('span', { class: 'spacer' }));
      head.appendChild(el('span', { class: 'muted small ana-hint' }, 'drag to pan · click a table · dashed = cycle edge'));
      head.appendChild(fitBtn);
    }
    function draw() {
      clear(host);
      var all = cat[ui.graph] || [];
      if (!all.length) { host.appendChild(el('p', { class: 'muted' }, 'No tables.')); return; }
      var shown = all.slice().sort(function (a, b) { return (b.fkIn + b.fkOut) - (a.fkIn + a.fkOut) || (a.key < b.key ? -1 : 1); }).slice(0, GRAPH_MAX);
      var keys = {};
      shown.forEach(function (t) { keys[t.key] = true; });
      var edges = [];
      shown.forEach(function (t) { (t.refs || []).forEach(function (r) { if (keys[r]) edges.push({ from: t.key, to: r }); }); });
      var svg = DBM.graph.fkSvg(shown.map(function (t) {
        return { id: t.key, label: t.key, sub: num(t.rows) + ' rows' + (t.isHeap ? ' · heap' : ''), cls: t.hasPk ? '' : 'fkg-warn' };
      }), edges, { label: (ui.graph === 'src' ? 'Source' : 'Target') + ' foreign-key graph', onClick: function (id) { drawer.open(ui.graph, id); } });
      if (ui.graphFit) svg.dbmFit(true);
      host.appendChild(svg);
      if (all.length > shown.length) host.appendChild(el('p', { class: 'muted small' }, 'Showing the ' + GRAPH_MAX + ' most connected of ' + num(all.length) + ' tables.'));
    }
    renderHead();
    draw();
    return el('section', { class: 'card' },
      el('div', { class: 'card-h' }, el('h2', { class: 'h3' }, 'Foreign-key graph'), el('span', { class: 'muted small' }, 'arrows point from child to parent')),
      el('div', { class: 'card-b stack' }, head, host));
  }

  // ---------- drawer ----------

  function profileBlock(p) {
    if (!p.sampledRows) return el('p', { class: 'small muted' }, 'No rows sampled (empty table).');
    var nullR = p.nulls / p.sampledRows, distR = p.distinct == null ? null : p.distinct / p.sampledRows;
    return el('div', { class: 'ana-prof' },
      el('div', { class: 'ana-metric' }, el('span', { class: 'small muted' }, 'null'), bar(nullR, 'null ' + pct(nullR)), el('span', { class: 'num small' }, pct(nullR))),
      distR == null ? null : el('div', { class: 'ana-metric' }, el('span', { class: 'small muted' }, 'distinct'), bar(distR, 'distinct ratio ' + distR.toFixed(2)), el('span', { class: 'num small' }, distR.toFixed(2))),
      el('div', { class: 'row-wrap ana-facts small' },
        p.semanticClass ? tag(p.semanticClass, 'ana-class') : null,
        p.maxLen != null ? el('span', { class: 'muted' }, 'len ≤ ' + p.maxLen + (p.avgLen != null ? ' (avg ' + p.avgLen.toFixed(1) + ')' : '')) : null,
        p.min != null ? el('span', { class: 'muted mono ellipsis' }, 'min ' + p.min) : null,
        p.max != null ? el('span', { class: 'muted mono ellipsis' }, 'max ' + p.max) : null),
      p.samples && p.samples.length ? el('div', { class: 'row-wrap ana-samples' }, p.samples.map(function (s) { return el('code', { class: 'mono small' }, s); })) : null,
      p.topPatterns && p.topPatterns.length ? el('div', { class: 'small muted ana-patterns' }, 'patterns ', p.topPatterns.map(function (x) { return el('code', { class: 'mono' }, x); })) : null);
  }

  function columnRow(ctx, side, key, d, c, focus) {
    var t = d.table, pk = (d.bestKey || []);
    var flags = [];
    if (!c.isNullable) flags.push(tag('NOT NULL'));
    if (pk.indexOf(c.name) >= 0) flags.push(tag('key', 'ana-pk'));
    if (c.isIdentity) flags.push(tag('identity'));
    if (c.isComputed) flags.push(tag('computed'));
    if (c.isRowVersion) flags.push(tag('rowversion'));
    (t.foreignKeys || []).forEach(function (fk) {
      var i = fk.columns.indexOf(c.name);
      if (i >= 0) flags.push(tag('FK → ' + fk.refSchema + '.' + fk.refTable + '.' + fk.refColumns[i], fk.isNotTrusted || fk.isDisabled ? 'ana-warn' : ''));
    });
    if (c.defaultDefinition) flags.push(tag('default ' + c.defaultDefinition));
    var row = el('div', { class: 'ana-col' + (focus ? ' is-active' : ''), data: { col: c.name } },
      el('div', { class: 'ana-col-h' }, el('span', { class: 'mono ana-col-name' }, c.name), el('span', { class: 'mono small muted' }, typeDisplay(c))),
      flags.length ? el('div', { class: 'row-wrap ana-flags' }, flags) : null,
      c.profile ? profileBlock(c.profile) : null);
    return commentable(ctx, row, 'column:' + side + ':' + key + '.' + c.name, 'Column ' + key + '.' + c.name);
  }

  function tableDetail(ctx, side, d, focusColumn) {
    var t = d.table, box = el('div', { class: 'stack ana-detail' });
    box.appendChild(el('div', { class: 'row-wrap ana-facts' },
      tag(num(t.rows) + ' rows'), tag(mb(t.sizeMb)),
      d.bestKey ? tag('key ' + d.bestKey.join(', '), 'ana-pk') : tag('no key', 'ana-warn'),
      d.isHeap ? tag('heap') : null,
      t.triggerCount ? tag(t.triggerCount + ' trigger' + (t.triggerCount > 1 ? 's' : '') + ((t.triggerNames || []).length ? ': ' + t.triggerNames.join(', ') : ''), 'ana-warn') : null,
      t.temporalType ? tag('temporal ' + t.temporalType) : null));
    if (t.description) box.appendChild(el('p', { class: 'muted' }, t.description));
    var fkOut = (t.foreignKeys || []).map(function (fk) {
      return el('li', {}, el('span', { class: 'mono' }, fk.name), ' ', fk.columns.join(', ') + ' → ' + fk.refSchema + '.' + fk.refTable + '(' + fk.refColumns.join(', ') + ')',
        fk.isNotTrusted ? tag('untrusted', 'ana-warn') : null, fk.isDisabled ? tag('disabled', 'ana-warn') : null);
    });
    var fkIn = (d.referencedBy || []).map(function (r) { return el('li', {}, el('span', { class: 'mono' }, r.table), ' via ' + r.foreignKey + ' (' + r.columns.join(', ') + ')'); });
    if (fkOut.length || fkIn.length) {
      box.appendChild(el('div', { class: 'ana-rel' },
        fkOut.length ? el('div', {}, el('h3', { class: 'h3' }, 'References'), el('ul', { class: 'ana-list small' }, fkOut)) : null,
        fkIn.length ? el('div', {}, el('h3', { class: 'h3' }, 'Referenced by'), el('ul', { class: 'ana-list small' }, fkIn)) : null));
    }
    box.appendChild(el('h3', { class: 'h3' }, 'Columns (' + t.columns.length + ')'));
    var cols = el('div', { class: 'ana-cols' });
    t.columns.forEach(function (c) { cols.appendChild(columnRow(ctx, side, d.key, d, c, c.name === focusColumn)); });
    box.appendChild(cols);
    return box;
  }

  function createDrawer(ctx) {
    var node = el('aside', { class: 'drawer ana-drawer', role: 'dialog', 'aria-label': 'Table details', 'aria-hidden': 'true' });
    var lastFocus = null;
    function close() {
      node.classList.remove('drawer-open');
      node.setAttribute('aria-hidden', 'true');
      if (lastFocus && lastFocus.focus) lastFocus.focus();
    }
    function open(side, key, column) {
      lastFocus = document.activeElement;
      clear(node);
      var closeBtn = el('button', { type: 'button', class: 'btn btn-ghost btn-sm', 'aria-label': 'Close details', on: { click: close } }, '✕');
      var body = el('div', { class: 'ana-drawer-b' }, el('p', { class: 'muted' }, 'Loading…'));
      node.appendChild(el('div', { class: 'ana-drawer-h' },
        el('div', { class: 'ana-title' }, el('span', { class: 'pill' }, side), el('h2', { class: 'h2 mono ellipsis', title: key }, key)),
        el('span', { class: 'spacer' }), closeBtn));
      node.appendChild(body);
      node.classList.add('drawer-open');
      node.setAttribute('aria-hidden', 'false');
      closeBtn.focus();
      loadTable(ctx, side, key).then(function (d) {
        clear(body);
        if (!d) { body.appendChild(el('p', { class: 'muted' }, 'Table not found in the catalog.')); return; }
        body.appendChild(commentable(ctx, el('div', { class: 'ana-table-anchor' }, tableDetail(ctx, side, d, column)), 'table:' + side + ':' + d.key, 'Table ' + d.key));
        if (column) {
          var hit = body.querySelector('.ana-col.is-active');
          if (hit && hit.scrollIntoView) hit.scrollIntoView({ block: 'center' });
        }
      }, function (err) {
        clear(body);
        body.appendChild(el('p', { class: 'muted' }, 'Could not load the table: ' + ((err && err.message) || err)));
      });
    }
    node.addEventListener('keydown', function (ev) { if (ev.key === 'Escape') { ev.stopPropagation(); close(); } });
    return { node: node, open: open, close: close };
  }

  // ---------- view ----------

  function render(root, ctx) {
    clear(root);
    tableCache = {};
    var page = el('div', { class: 'page ana-page' });
    root.appendChild(page);
    if (!isExport() && DBM.components && DBM.components.reviewBar) page.appendChild(DBM.components.reviewBar(ctx));
    var p = payloadOf(ctx);
    if (!p) {
      page.appendChild(DBM.components.emptyState('No analysis yet', 'Discovery and the rule checks run automatically once both connections are saved.'));
      return;
    }
    var drawer = createDrawer(ctx);
    page.appendChild(header(ctx, p));
    page.appendChild(kpiRow(p));
    page.appendChild(el('div', { class: 'grid-2 ana-dbs' }, dbCard('Source', 'src', p.source, p.target), dbCard('Target', 'tgt', p.target, p.source)));
    page.appendChild(narrativeCard(ctx, p));
    page.appendChild(findingsCard(ctx, p, drawer));
    var lower = el('div', { class: 'stack' }, el('section', { class: 'card is-loading' }, el('div', { class: 'card-b muted' }, 'Loading catalog…')));
    page.appendChild(lower);
    root.appendChild(drawer.node);
    loadCatalog(ctx).then(function (cat) {
      clear(lower);
      lower.appendChild(tablesCard(ctx, p, cat, drawer));
      lower.appendChild(graphCard(ctx, cat, drawer));
    }, function (err) {
      clear(lower);
      lower.appendChild(DBM.components.emptyState('Catalog unavailable', (err && err.message) || String(err)));
    });
  }

  function onEvent(evt, ctx) {
    if (evt && evt.type === 'drift_detected') toast(ctx, 'Schema changed since discovery — re-run discovery before approving.', 'warn');
  }

  DBM.views = DBM.views || {};
  DBM.views.analysis = { title: 'Analysis', render: render, onEvent: onEvent };
})(window.DBM = window.DBM || {});
