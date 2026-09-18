(function (DBM) {
  'use strict';

  // =====================================================================
  // Pure helpers (no DOM). Exposed as DBM.mappingOps and unit-tested with
  // node --test (Dbm.Tests/js/mapping-ops.test.cjs).
  // =====================================================================

  var TABLE_PROPS = ['kind', 'sources', 'from', 'filter', 'confidence', 'method', 'rationale', 'candidates'];
  var COLUMN_PROPS = ['expr', 'sourceColumns', 'default', 'confidence', 'method', 'rationale', 'typeRisk', 'candidates'];
  var EDIT_FIELDS = ['expr', 'sourceColumns', 'default'];

  function escSeg(s) { return String(s).replace(/~/g, '~0').replace(/\//g, '~1'); }
  function pointer() {
    var parts = [];
    for (var i = 0; i < arguments.length; i++) parts.push(escSeg(arguments[i]));
    return '/' + parts.join('/');
  }
  function isNil(v) { return v === undefined || v === null; }
  function blank(v) { return isNil(v) || String(v).trim() === ''; }
  function clone(v) { return v === undefined ? undefined : JSON.parse(JSON.stringify(v)); }
  function stable(v) {
    if (Array.isArray(v)) return '[' + v.map(stable).join(',') + ']';
    if (v && typeof v === 'object') {
      return '{' + Object.keys(v).sort().filter(function (k) { return v[k] !== undefined; })
        .map(function (k) { return JSON.stringify(k) + ':' + stable(v[k]); }).join(',') + '}';
    }
    return JSON.stringify(isNil(v) ? null : v);
  }
  function same(a, b) { return stable(a) === stable(b); }
  function unionKeys(a, b) {
    var seen = {};
    Object.keys(a || {}).concat(Object.keys(b || {})).forEach(function (k) { seen[k] = true; });
    return Object.keys(seen).sort();
  }
  function orderedProps(known, a, b) {
    var all = unionKeys(a, b);
    var out = known.filter(function (k) { return all.indexOf(k) >= 0; });
    all.forEach(function (k) { if (out.indexOf(k) < 0) out.push(k); });
    return out;
  }
  function findKey(obj, key) {
    if (!obj) return undefined;
    if (Object.prototype.hasOwnProperty.call(obj, key)) return obj[key];
    var lower = String(key).toLowerCase();
    var keys = Object.keys(obj);
    for (var i = 0; i < keys.length; i++) if (keys[i].toLowerCase() === lower) return obj[keys[i]];
    return undefined;
  }
  function splitColumnKey(key) {
    var s = String(key);
    var i = s.lastIndexOf('.');
    return { table: s.slice(0, i), column: s.slice(i + 1) };
  }
  function quoteName(name) { return '[' + String(name).replace(/]/g, ']]') + ']'; }
  function parseSourceColumns(text) {
    return String(text || '').split(/[\s,;]+/).map(function (s) { return s.trim(); }).filter(Boolean);
  }

  function propOps(ops, base, before, after, known, skip) {
    orderedProps(known, before, after).forEach(function (k) {
      if (skip && skip.indexOf(k) >= 0) return;
      var b = before ? before[k] : undefined;
      var a = after ? after[k] : undefined;
      var path = base + '/' + escSeg(k);
      if (isNil(a) && isNil(b)) return;
      if (isNil(a)) ops.push({ op: 'remove', path: path });
      else if (isNil(b)) ops.push({ op: 'add', path: path, value: clone(a) });
      else if (!same(a, b)) ops.push({ op: 'replace', path: path, value: clone(a) });
    });
  }

  /** JSON-patch ops (add | replace | remove) that turn mapping payload `before` into `after`. */
  function diff(before, after) {
    var ops = [];
    var bt = (before && before.tables) || {};
    var at = (after && after.tables) || {};
    unionKeys(bt, at).forEach(function (t) {
      var base = pointer('tables', t);
      if (!(t in at)) { ops.push({ op: 'remove', path: base }); return; }
      if (!(t in bt)) { ops.push({ op: 'add', path: base, value: clone(at[t]) }); return; }
      propOps(ops, base, bt[t], at[t], TABLE_PROPS, ['columns']);
      var bc = bt[t].columns || {};
      var ac = at[t].columns || {};
      if (!bt[t].columns && at[t].columns) { ops.push({ op: 'add', path: base + '/columns', value: clone(ac) }); return; }
      unionKeys(bc, ac).forEach(function (c) {
        var cbase = base + '/columns/' + escSeg(c);
        if (!(c in ac)) ops.push({ op: 'remove', path: cbase });
        else if (!(c in bc)) ops.push({ op: 'add', path: cbase, value: clone(ac[c]) });
        else propOps(ops, cbase, bc[c], ac[c], COLUMN_PROPS);
      });
    });
    var bd = (before && before.drops) || {};
    var ad = (after && after.drops) || {};
    unionKeys(bd, ad).forEach(function (k) {
      var path = pointer('drops', k);
      if (!(k in ad)) ops.push({ op: 'remove', path: path });
      else if (!(k in bd)) ops.push({ op: 'add', path: path, value: clone(ad[k]) });
      else if (!same(bd[k], ad[k])) ops.push({ op: 'replace', path: path, value: clone(ad[k]) });
    });
    var bn = (before && before.notes) || [];
    var an = (after && after.notes) || [];
    if (!same(bn, an)) ops.push({ op: before && before.notes ? 'replace' : 'add', path: '/notes', value: clone(an) });
    return ops;
  }

  /** Number of edited entities (a column map, a table's own fields, a drop, the notes) behind a list of ops. */
  function changeCount(list) {
    var seen = {};
    (list || []).forEach(function (o) {
      var segs = String(o.path).split('/');
      var key;
      if (segs[1] === 'tables') key = segs[3] === 'columns' && segs.length > 4 ? segs.slice(0, 5).join('/') : segs.slice(0, 3).join('/');
      else if (segs[1] === 'drops') key = segs.slice(0, 3).join('/');
      else key = '/' + segs[1];
      seen[key] = true;
    });
    return Object.keys(seen).length;
  }

  function needsReview(item, autoAccept) {
    return !!item && (item.method === 'fuzzy' || item.method === 'vector') && (Number(item.confidence) || 0) < autoAccept;
  }
  function mentions(line, key) {
    var l = String(line).toLowerCase();
    var k = String(key).toLowerCase();
    return l.indexOf(k + ':') === 0 || l.indexOf(k + '.') === 0;
  }
  function tableStatus(key, map, blockers, autoAccept) {
    if (!map || (blockers || []).some(function (b) { return mentions(b, key); })) return 'blocker';
    if (map.kind === 'skip') return 'ok';
    if (needsReview(map, autoAccept)) return 'attention';
    var cols = map.columns || {};
    return Object.keys(cols).some(function (c) { return needsReview(cols[c], autoAccept); }) ? 'attention' : 'ok';
  }
  function columnStatus(tableKey, column, cm, blockers, autoAccept) {
    var prefix = (tableKey + '.' + column + ':').toLowerCase();
    if ((blockers || []).some(function (b) { return String(b).toLowerCase().indexOf(prefix) === 0; })) return 'blocker';
    return needsReview(cm, autoAccept) ? 'attention' : 'ok';
  }
  function attentionCount(payload, autoAccept) {
    var n = 0;
    var tables = (payload && payload.tables) || {};
    Object.keys(tables).forEach(function (k) {
      var m = tables[k];
      if (m.kind === 'skip') return;
      if (needsReview(m, autoAccept)) n++;
      var cols = m.columns || {};
      Object.keys(cols).forEach(function (c) { if (needsReview(cols[c], autoAccept)) n++; });
    });
    return n;
  }

  /** Source columns ("schema.table.column") neither referenced by a non-skip map nor dropped (mirrors MappingValidator). */
  function uncovered(context, payload) {
    var covered = {};
    var dropped = {};
    var tables = (payload && payload.tables) || {};
    Object.keys(tables).forEach(function (k) {
      var m = tables[k];
      if (m.kind === 'skip') return;
      var cols = m.columns || {};
      Object.keys(cols).forEach(function (c) {
        (cols[c].sourceColumns || []).forEach(function (sc) { covered[String(sc).toLowerCase()] = true; });
      });
    });
    Object.keys((payload && payload.drops) || {}).forEach(function (k) { dropped[k.toLowerCase()] = true; });
    var out = [];
    ((context && context.source) || []).forEach(function (s) {
      if (dropped[s.key.toLowerCase()]) return;
      s.columns.forEach(function (c) {
        var key = s.key + '.' + c.name;
        if (!covered[key.toLowerCase()] && !dropped[key.toLowerCase()]) out.push(key);
      });
    });
    return out;
  }

  /** Approval blockers with the same messages as MappingValidator.Blockers (C#). */
  function blockers(context, payload) {
    var out = [];
    var tables = (payload && payload.tables) || {};
    ((context && context.target) || []).forEach(function (t) {
      var map = findKey(tables, t.key);
      if (!map) { out.push(t.key + ': no table mapping (map a source table or set kind "skip")'); return; }
      if (map.kind === 'skip') return;
      if (!map.sources || !map.sources.length) out.push(t.key + ': no source table (choose one or set kind "skip")');
      t.columns.forEach(function (c) {
        if (c.nullable || c.identity || c.computed || c.rowversion || c.hasDefault) return;
        var cm = findKey(map.columns || {}, c.name);
        if (!cm || (blank(cm.expr) && blank(cm['default']))) {
          out.push(t.key + '.' + c.name + ': NOT NULL without default needs an expression or default');
        }
      });
    });
    var missing = {};
    uncovered(context, payload).forEach(function (key) {
      var p = splitColumnKey(key);
      (missing[p.table] = missing[p.table] || []).push(key);
    });
    ((context && context.source) || []).forEach(function (s) {
      var miss = missing[s.key];
      if (!miss) return;
      if (miss.length === s.columns.length) out.push('source table ' + s.key + ' is not mapped or dropped (' + miss.length + ' columns)');
      else miss.forEach(function (key) { out.push('source column ' + key + ' is not mapped or dropped'); });
    });
    return out;
  }

  function markHuman(item) { item.method = 'human'; item.confidence = 1; }

  /** Applies a human edit to one column map (creating it if needed); blank values remove the field. */
  function editColumn(payload, tableKey, column, fields) {
    var t = findKey(payload.tables, tableKey);
    if (!t) throw new Error('unknown target table ' + tableKey);
    t.columns = t.columns || {};
    var cm = t.columns[column] || (t.columns[column] = { sourceColumns: [] });
    Object.keys(fields).forEach(function (k) {
      var v = fields[k];
      if (k === 'sourceColumns') cm.sourceColumns = Array.isArray(v) ? v : parseSourceColumns(v);
      else if (blank(v)) delete cm[k];
      else cm[k] = v;
    });
    if (!cm.sourceColumns) cm.sourceColumns = [];
    markHuman(cm);
    return cm;
  }

  /** Puts the original column map back when the editable fields ended up unchanged (no spurious ops). */
  function restoreIfUnchanged(before, after, tableKey, column) {
    var bt = findKey((before && before.tables) || {}, tableKey);
    var at = findKey((after && after.tables) || {}, tableKey);
    var bc = bt && bt.columns && bt.columns[column];
    var ac = at && at.columns && at.columns[column];
    if (!ac) return false;
    function pick(c) { return EDIT_FIELDS.map(function (f) { return f === 'sourceColumns' ? (c.sourceColumns || []) : (blank(c[f]) ? null : c[f]); }); }
    if (bc && same(pick(bc), pick(ac))) { at.columns[column] = clone(bc); return true; }
    if (!bc && same(pick(ac), [null, [], null])) { delete at.columns[column]; return true; }
    return false;
  }

  function editTable(payload, tableKey, fields) {
    var t = findKey(payload.tables, tableKey);
    if (!t) throw new Error('unknown target table ' + tableKey);
    Object.keys(fields).forEach(function (k) {
      var v = fields[k];
      if (k === 'sources') t.sources = Array.isArray(v) ? v : parseSourceColumns(v);
      else if (blank(v)) delete t[k];
      else t[k] = v;
    });
    if (!t.sources) t.sources = [];
    markHuman(t);
    return t;
  }

  function setSource(payload, tableKey, sourceTable) {
    var t = findKey(payload.tables, tableKey);
    if (!t) t = payload.tables[tableKey] = { kind: 'direct', sources: [], confidence: 1, method: 'human', columns: {} };
    if (t.kind === 'skip') t.kind = 'direct';
    t.sources = [sourceTable];
    t.columns = t.columns || {};
    markHuman(t);
    return t;
  }

  function skipTable(payload, tableKey, reason) {
    payload.tables[tableKey] = { kind: 'skip', sources: [], confidence: 1, method: 'human', rationale: reason, columns: {} };
    return payload.tables[tableKey];
  }

  function dropSource(payload, key, reason) {
    if (blank(reason)) throw new Error('A drop needs a reason.');
    payload.drops = payload.drops || {};
    payload.drops[key] = { reason: String(reason).trim(), method: 'human' };
    return payload.drops[key];
  }

  function undropSource(payload, key) {
    if (payload.drops) delete payload.drops[key];
  }

  /** Maps source column `sourceKey` to a target column as s.[COL]; the target table must use that source table as Sources[0]
   *  (a table without sources adopts it). */
  function mapToTarget(payload, sourceKey, tableKey, column) {
    var sc = splitColumnKey(sourceKey);
    var t = findKey(payload.tables, tableKey);
    if (!t || !t.sources || !t.sources.length || t.kind === 'skip') t = setSource(payload, tableKey, sc.table);
    if (String(t.sources[0]).toLowerCase() !== sc.table.toLowerCase()) {
      throw new Error(sourceKey + ' is not in the primary source (' + t.sources[0] + ') of ' + tableKey);
    }
    return editColumn(payload, tableKey, column, { expr: 's.' + quoteName(sc.column), sourceColumns: [sourceKey], rationale: 'Mapped by hand in the mapping screen.' });
  }

  function useCandidate(payload, tableKey, column, candidateSource) {
    return mapToTarget(payload, candidateSource, tableKey, column);
  }

  /** Target columns a source column can be mapped to: writable columns of tables whose primary source is that table, or that
   *  have no source yet. */
  function mapTargetsFor(context, payload, sourceKey) {
    var srcTable = splitColumnKey(sourceKey).table.toLowerCase();
    var out = [];
    ((context && context.target) || []).forEach(function (t) {
      var map = findKey((payload && payload.tables) || {}, t.key);
      if (map && map.kind === 'skip') return;
      var primary = map && map.sources && map.sources.length ? String(map.sources[0]).toLowerCase() : null;
      if (primary !== null && primary !== srcTable) return;
      t.columns.forEach(function (c) {
        if (c.computed || c.rowversion) return;
        var cm = map && map.columns && findKey(map.columns, c.name);
        out.push({ table: t.key, column: c.name, type: c.type, mapped: !!(cm && !blank(cm.expr)) });
      });
    });
    return out;
  }

  DBM.mappingOps = {
    diff: diff,
    changeCount: changeCount,
    pointer: pointer,
    parseSourceColumns: parseSourceColumns,
    splitColumnKey: splitColumnKey,
    quoteName: quoteName,
    needsReview: needsReview,
    tableStatus: tableStatus,
    columnStatus: columnStatus,
    attentionCount: attentionCount,
    uncovered: uncovered,
    blockers: blockers,
    editColumn: editColumn,
    restoreIfUnchanged: restoreIfUnchanged,
    editTable: editTable,
    setSource: setSource,
    skipTable: skipTable,
    dropSource: dropSource,
    undropSource: undropSource,
    mapToTarget: mapToTarget,
    useCandidate: useCandidate,
    mapTargetsFor: mapTargetsFor
  };

  // =====================================================================
  // View (browser only). Everything below touches the DOM lazily.
  // =====================================================================

  var ops = DBM.mappingOps;
  var current = null;

  function flat(list, out) {
    list.forEach(function (c) {
      if (Array.isArray(c)) flat(c, out);
      else if (c !== null && c !== undefined && c !== false) out.push(typeof c === 'number' ? String(c) : c);
    });
    return out;
  }
  function el(tag, attrs) {
    return DBM.h.apply(null, [tag, attrs || {}].concat(flat(Array.prototype.slice.call(arguments, 2), [])));
  }
  function pct(v) { return Math.round((Number(v) || 0) * 100) + '%'; }

  function bar(v) {
    var n = Math.max(0, Math.min(1, Number(v) || 0));
    var b = el('span', { class: 'bar map-bar', role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': '1', 'aria-valuenow': n.toFixed(2), title: 'Confidence ' + pct(n) });
    b.style.setProperty('--v', n.toFixed(3));
    return el('span', { class: 'map-conf' }, b, el('span', { class: 'num small' }, pct(n)));
  }
  function methodTag(method) { return el('span', { class: 'tag map-m map-m-' + (method || 'none') }, method || '—'); }
  function statusPill(st) {
    return el('span', { class: 'map-st map-st-' + st }, st === 'ok' ? 'OK' : st === 'attention' ? 'Needs review' : 'Blocker');
  }
  function flagTag(text) { return el('span', { class: 'tag map-flag' }, text); }
  function autoGrow(ta) { ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight + 2, 240) + 'px'; }

  function contextFromPayload(payload) {
    var tables = (payload && payload.tables) || {};
    return {
      version: null, autoAccept: 0.85, candidate: 0.5, source: [], blockers: [], attention: [], uncovered: [],
      target: Object.keys(tables).sort().map(function (k) {
        return {
          key: k,
          columns: Object.keys(tables[k].columns || {}).map(function (c) {
            return { name: c, type: '', nullable: true, identity: false, computed: false, rowversion: false, hasDefault: false };
          })
        };
      })
    };
  }

  function loadContext(ctx, payload) {
    if (window.DBM_EXPORT || !ctx.api) return Promise.resolve(contextFromPayload(payload));
    var q = isNil(ctx.version) ? '' : '?version=' + encodeURIComponent(ctx.version);
    return ctx.api.get('/api/mapping/context' + q);
  }

  function localBlockers(view) {
    var cx = view.context;
    return cx.source && cx.source.length ? ops.blockers(cx, view.work) : (cx.blockers || []);
  }

  function render(root, ctx, opts) {
    var artifact = ctx.artifact || {};
    var payload = artifact.payload || { tables: {}, drops: {}, notes: [] };
    var phase = ((ctx.state && ctx.state.phases) || []).filter(function (p) { return p.name === 'mapping'; })[0] || {};
    var view = {
      ctx: ctx,
      before: clone(payload),
      work: clone(payload),
      context: null,
      expanded: {},
      filter: 'all',
      saving: false,
      held: null,
      bar: null,
      body: null,
      editable: !ctx.readOnly && phase.status === 'awaiting_review' && ctx.version === phase.currentVersion
    };
    // A re-render over this view (a comment posted, agent presence, any ctx.refresh) carries unsaved edits across, but only
    // onto the same base version and only into a view that is still editable. A version change never reaches here while
    // dirty: holdRender declines it. A first render (opts.rerender false) never inherits another instance's work.
    // `context` is carried too: it was fetched for this same version, and a carried view without it would report itself
    // clean (isDirty needs context) until a refetch returned — long enough for a second re-render to destroy the edits.
    var prev = opts && opts.rerender ? current : null;
    if (isDirty(prev)) {
      if (view.editable && prev.editable && ctx.version === prev.ctx.version) {
        view.context = prev.context;
        view.before = prev.before;
        view.work = prev.work;
        view.expanded = prev.expanded;
        view.filter = prev.filter;
        view.saving = prev.saving;
      } else {
        ctx.toast('Your unsaved mapping edits were discarded: this view is no longer editable.', 'warn');
      }
    }
    current = view;
    root.textContent = '';
    var page = el('div', { class: 'page map-page' });
    root.appendChild(page);
    view.page = page;
    if (DBM.components.reviewBar && !window.DBM_EXPORT) view.review = page.appendChild(DBM.components.reviewBar(ctx));
    view.body = el('div', { class: 'stack' }, el('div', { class: 'empty is-loading' }, 'Loading mapping…'));
    page.appendChild(view.body);
    if (view.context) { draw(view); return; }
    loadContext(ctx, payload).then(function (context) {
      view.context = context;
      draw(view);
    }).catch(function (e) {
      view.body.textContent = '';
      view.body.appendChild(DBM.components.emptyState('Mapping context unavailable', (e && e.message) || String(e)));
    });
  }

  function redraw(view) {
    var y = window.scrollY;
    draw(view);
    window.scrollTo(0, y);
  }

  function draw(view) {
    var ctx = view.ctx;
    var a = ctx.artifact || {};
    var blockersNow = localBlockers(view);
    view.bar = null;
    view.body.textContent = '';
    view.body.appendChild(el('div', { class: 'page-h' },
      el('div', { class: 'h1' }, 'Mapping'),
      el('div', { class: 'muted small' }, ['v' + (isNil(ctx.version) ? '?' : ctx.version), a.author, a.summary].filter(Boolean).join(' · '))));
    view.body.appendChild(kpis(view, blockersNow));
    if (!view.editable && !ctx.readOnly) {
      view.body.appendChild(el('p', { class: 'muted small map-note' },
        'Direct edits are available while the phase is awaiting review and you are viewing the latest version.'));
    }
    if (blockersNow.length) view.body.appendChild(blockersPanel(view, blockersNow));
    view.body.appendChild(tablesCard(view, blockersNow));
    view.body.appendChild(uncoveredCard(view));
    view.body.appendChild(dropsCard(view));
    if (view.editable) view.body.appendChild(saveBar(view));
  }

  function kpis(view, blockersNow) {
    var cx = view.context;
    var w = view.work;
    var K = DBM.components.kpi;
    var targets = cx.target || [];
    var tablesMapped = 0;
    var colsMapped = 0;
    var colsTotal = 0;
    targets.forEach(function (t) {
      var m = findKey(w.tables, t.key);
      if (m && m.kind !== 'skip' && m.sources && m.sources.length) tablesMapped++;
      t.columns.forEach(function (c) {
        if (c.computed || c.rowversion) return;
        colsTotal++;
        var cm = m && findKey(m.columns || {}, c.name);
        if (cm && !blank(cm.expr)) colsMapped++;
      });
    });
    return el('div', { class: 'grid-kpi' },
      K('Tables mapped', tablesMapped + ' / ' + targets.length, 'target tables with a source'),
      K('Columns mapped', colsMapped + ' / ' + colsTotal, 'writable target columns'),
      K('Needs review', String(ops.attentionCount(w, cx.autoAccept)), 'proposals below ' + pct(cx.autoAccept)),
      K('Blockers', String(blockersNow.length), blockersNow.length ? 'approval disabled' : 'nothing blocks approval'),
      K('Drops', String(Object.keys(w.drops || {}).length), 'source tables/columns not migrated'));
  }

  function blockerTarget(view, text) {
    if (String(text).indexOf('source ') === 0) return '#uncovered';
    var hit = (view.context.target || []).filter(function (t) { return mentions(text, t.key); })[0];
    return hit ? hit.key : null;
  }

  function jump(view, target) {
    if (target === '#uncovered') {
      var card = document.getElementById('map-uncovered');
      if (card) card.scrollIntoView({ block: 'start' });
      return;
    }
    view.expanded[target] = true;
    view.filter = 'all';
    draw(view);
    var sel = '[data-key="' + (window.CSS && CSS.escape ? CSS.escape(target) : target) + '"]';
    var row = view.body.querySelector(sel);
    if (row) {
      row.scrollIntoView({ block: 'center' });
      var btn = row.querySelector('button');
      if (btn) btn.focus();
    }
  }

  function blockersPanel(view, list) {
    return el('section', { class: 'card map-blockers', 'aria-label': 'Blockers' },
      el('div', { class: 'card-h row row-wrap' },
        el('span', { class: 'map-st map-st-blocker h3' }, 'Blockers'),
        el('span', { class: 'muted small' }, 'Approval stays disabled until each item is mapped, defaulted, skipped or dropped.')),
      el('div', { class: 'card-b' },
        el('ul', { class: 'map-issues' }, list.map(function (b) {
          var target = blockerTarget(view, b);
          return el('li', {}, target
            ? el('button', { class: 'btn btn-ghost btn-sm map-jump', on: { click: function () { jump(view, target); } } }, b)
            : el('span', { class: 'small' }, b));
        }))));
  }

  function tablesCard(view, blockersNow) {
    var cx = view.context;
    var rows = [];
    (cx.target || []).forEach(function (t) {
      var map = findKey(view.work.tables, t.key);
      var st = tableStatus(t.key, map, blockersNow, cx.autoAccept);
      if (view.filter !== 'all' && st !== view.filter) return;
      var open = !!view.expanded[t.key];
      var toggle = function () { view.expanded[t.key] = !view.expanded[t.key]; redraw(view); };
      var nameCell = el('td', { class: 'mono' }, t.key);
      view.ctx.commentable(nameCell, 'tablemap:' + t.key, t.key);
      var tr = el('tr', { class: 'tr-click map-row' + (open ? ' is-active' : ''), data: { key: t.key } },
        el('td', { class: 'map-toggle-cell' },
          el('button', {
            class: 'btn btn-ghost btn-sm map-toggle', 'aria-expanded': String(open),
            'aria-label': (open ? 'Hide' : 'Show') + ' columns of ' + t.key, on: { click: toggle }
          }, open ? '▾' : '▸')),
        nameCell,
        el('td', { class: 'mono small' }, map && map.kind === 'skip' ? el('span', { class: 'muted' }, 'skipped') :
          map && map.sources && map.sources.length ? map.sources.join(', ') : el('span', { class: 'muted' }, 'none')),
        el('td', {}, el('span', { class: 'pill map-kind' }, map ? map.kind : '—')),
        el('td', {}, map ? bar(map.confidence) : el('span', { class: 'muted small' }, '—')),
        el('td', {}, methodTag(map && map.method)),
        el('td', {}, statusPill(st)));
      tr.addEventListener('click', function (e) {
        if (!e.target.closest('button, a, input, textarea, select, .commentable button')) toggle();
      });
      rows.push(tr);
      if (open) rows.push(el('tr', { class: 'map-detail' }, el('td', { colspan: '7' }, columnsPanel(view, t, map, blockersNow))));
    });
    var filters = el('div', { class: 'row map-filter', role: 'group', 'aria-label': 'Filter tables' },
      [['all', 'All'], ['attention', 'Needs review'], ['blocker', 'Blockers']].map(function (f) {
        return el('button', {
          class: 'chip' + (view.filter === f[0] ? ' is-active' : ''), 'aria-pressed': String(view.filter === f[0]),
          on: { click: function () { view.filter = f[0]; redraw(view); } }
        }, f[1]);
      }));
    var head = el('thead', {}, el('tr', {},
      el('th', { scope: 'col' }, el('span', { class: 'map-sr' }, 'Expand')), el('th', { scope: 'col' }, 'Target'),
      el('th', { scope: 'col' }, 'Source'), el('th', { scope: 'col' }, 'Kind'), el('th', { scope: 'col' }, 'Confidence'),
      el('th', { scope: 'col' }, 'Method'), el('th', { scope: 'col' }, 'Status')));
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-sticky map-grid' }, head, el('tbody', {}, rows)))
      : DBM.components.emptyState('No tables match this filter', 'Choose "All" to see every target table.');
    return el('section', { class: 'card' },
      el('div', { class: 'card-h row row-wrap' }, el('div', { class: 'h3' }, 'Tables'), el('span', { class: 'spacer' }), filters),
      el('div', { class: 'card-b' }, body));
  }

  function columnsPanel(view, t, map, blockersNow) {
    var parts = [];
    if (!map || map.kind === 'skip' || !map.sources || !map.sources.length) parts.push(sourcePicker(view, t, map));
    if (map && map.kind === 'skip') {
      parts.push(el('p', { class: 'muted small' }, 'This target table is skipped' + (map.rationale ? ': ' + map.rationale : '.')));
      return el('div', { class: 'stack map-colpanel' }, parts);
    }
    if (map && map.sources && map.sources.length) parts.push(tableFields(view, t, map));
    var effective = map || { columns: {}, sources: [] };
    var head = el('thead', {}, el('tr', {}, ['Target column', 'Expression', 'Source columns', 'Default', 'Confidence', 'Method', 'Type risk', 'Candidates']
      .map(function (x) { return el('th', { scope: 'col' }, x); })));
    parts.push(el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-cols' }, head,
      el('tbody', {}, t.columns.map(function (c) { return columnRow(view, t, effective, c, blockersNow); })))));
    return el('div', { class: 'stack map-colpanel' }, parts);
  }

  function sourcePicker(view, t, map) {
    if (!view.editable) return el('p', { class: 'muted small' }, map && map.kind === 'skip' ? '' : 'No source table is mapped to this target yet.');
    if (map && map.kind === 'skip') {
      return el('div', { class: 'row' }, el('button', {
        class: 'btn btn-sm', on: { click: function () { editTable(view.work, t.key, { kind: 'direct', rationale: '' }); redraw(view); } }
      }, 'Stop skipping'));
    }
    var select = el('select', { class: 'select', 'aria-label': 'Source table for ' + t.key },
      el('option', { value: '' }, 'Choose a source table…'),
      (view.context.source || []).map(function (s) { return el('option', { value: s.key }, s.key + ' (' + DBM.fmt.num(s.rows) + ' rows)'); }));
    return el('div', { class: 'row row-wrap map-pick' },
      el('span', { class: 'small' }, 'No source table yet.'),
      select,
      el('button', {
        class: 'btn btn-sm btn-primary',
        on: { click: function () { if (!select.value) return; setSource(view.work, t.key, select.value); view.expanded[t.key] = true; redraw(view); } }
      }, 'Use as source'),
      el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { skipFlow(view, t.key); } } }, 'Skip table…'));
  }

  function tableFields(view, t, map) {
    if (!view.editable) {
      return el('div', { class: 'stack map-tablefields-ro' },
        map.from ? el('div', {}, el('span', { class: 'muted small' }, 'FROM '), el('code', { class: 'mono small' }, map.from)) : null,
        map.filter ? el('div', {}, el('span', { class: 'muted small' }, 'WHERE '), el('code', { class: 'mono small' }, map.filter)) : null,
        map.rationale ? el('div', { class: 'muted small' }, map.rationale) : null);
    }
    function field(label, control) { return el('label', { class: 'stack map-field' }, el('span', { class: 'small muted' }, label), control); }
    function input(value, placeholder, apply) {
      var i = el('input', { class: 'input mono', value: value, placeholder: placeholder, spellcheck: 'false', on: { input: function () { apply(i.value); updateBar(view); } } });
      return i;
    }
    var kind = el('select', { class: 'select', on: { change: function () { editTable(view.work, t.key, { kind: kind.value }); updateBar(view); } } },
      ['direct', 'merge', 'lookup'].map(function (k) { var o = el('option', { value: k }, k); if (map.kind === k) o.selected = true; return o; }));
    return el('div', { class: 'map-tablefields' },
      field('Kind', kind),
      field('Sources (first is alias s)', input((map.sources || []).join(', '), 'dbo.TABLE, dbo.LOOKUP', function (v) { editTable(view.work, t.key, { sources: v }); })),
      field('FROM clause (optional)', input(map.from || '', '[dbo].[A] AS s JOIN [dbo].[B] AS b ON b.[ID] = s.[B_ID]', function (v) { editTable(view.work, t.key, { from: v }); })),
      field('WHERE predicate (optional)', input(map.filter || '', 's.[DELETED] = 0', function (v) { editTable(view.work, t.key, { filter: v }); })),
      el('div', { class: 'small muted map-hint' }, 'Expressions use alias s for the first source; declare other aliases with JOINs in the FROM clause.'));
  }

  function columnRow(view, t, map, c, blockersNow) {
    var cm = findKey(map.columns || {}, c.name);
    var fixed = c.computed || c.rowversion;
    var st = fixed ? 'ok' : columnStatus(t.key, c.name, cm, blockersNow, view.context.autoAccept);
    var nameCell = el('td', { class: 'map-colname-cell', data: { label: 'Target column' } },
      el('div', { class: 'map-col-name' }, c.name),
      el('div', { class: 'map-col-type' }, (c.type || '') + (c.nullable ? '' : ' not null')),
      el('div', { class: 'map-col-flags' },
        c.identity ? flagTag('identity') : null, c.computed ? flagTag('computed') : null,
        c.rowversion ? flagTag('rowversion') : null, c.hasDefault ? flagTag('default') : null,
        st !== 'ok' ? statusPill(st) : null));
    view.ctx.commentable(nameCell, 'colmap:' + t.key + '.' + c.name, t.key + '.' + c.name);
    if (fixed) {
      return el('tr', { class: 'map-fixed' }, nameCell, el('td', { colspan: '7', class: 'muted small', data: { label: 'Note' } },
        c.computed ? 'Computed by the target; never written.' : 'Rowversion; generated by the server.'));
    }
    var exprCell;
    var srcCell;
    var defCell;
    if (view.editable) {
      var changed = function () { ops.restoreIfUnchanged(view.before, view.work, t.key, c.name); updateBar(view); };
      var ta = el('textarea', {
        class: 'textarea mono map-expr', rows: '1', spellcheck: 'false', 'aria-label': 'Expression for ' + c.name,
        placeholder: 'unmapped (e.g. s.[COLUMN])',
        on: { input: function () { editColumn(view.work, t.key, c.name, { expr: ta.value }); autoGrow(ta); changed(); } }
      }, (cm && cm.expr) || '');
      setTimeout(function () { autoGrow(ta); }, 0);
      var sc = el('input', {
        class: 'input mono map-srccols', value: ((cm && cm.sourceColumns) || []).join(', '), spellcheck: 'false',
        'aria-label': 'Source columns for ' + c.name, placeholder: 'schema.table.column, …',
        on: { input: function () { editColumn(view.work, t.key, c.name, { sourceColumns: sc.value }); changed(); } }
      });
      var df = el('input', {
        class: 'input mono map-default', value: (cm && cm['default']) || '', spellcheck: 'false',
        'aria-label': 'Default for ' + c.name, placeholder: 'e.g. 0 or N\'\'',
        on: { input: function () { editColumn(view.work, t.key, c.name, { 'default': df.value }); changed(); } }
      });
      exprCell = el('td', { data: { label: 'Expression' } }, ta);
      srcCell = el('td', { data: { label: 'Source columns' } }, sc);
      defCell = el('td', { data: { label: 'Default' } }, df);
    } else {
      exprCell = el('td', { data: { label: 'Expression' } }, cm && cm.expr ? el('code', { class: 'mono small map-code' }, cm.expr) : el('span', { class: 'muted small' }, 'unmapped'));
      srcCell = el('td', { class: 'mono small', data: { label: 'Source columns' } }, ((cm && cm.sourceColumns) || []).join(', ') || '—');
      defCell = el('td', { class: 'mono small', data: { label: 'Default' } }, (cm && cm['default']) || '—');
    }
    return el('tr', { class: 'map-colrow map-row-' + st }, nameCell, exprCell, srcCell, defCell,
      el('td', { data: { label: 'Confidence' } }, cm ? bar(cm.confidence) : el('span', { class: 'muted small' }, '—')),
      el('td', { data: { label: 'Method' } }, methodTag(cm && cm.method)),
      el('td', { data: { label: 'Type risk' } }, cm && cm.typeRisk ? el('span', { class: 'tag map-risk', title: cm.typeRisk }, cm.typeRisk) : el('span', { class: 'muted small' }, '—')),
      el('td', { data: { label: 'Candidates' } }, candidatesControl(view, t, map, c, cm)));
  }

  function candidatesControl(view, t, map, c, cm) {
    var list = (cm && cm.candidates) || [];
    if (!list.length) return el('span', { class: 'muted small' }, '—');
    var primary = map.sources && map.sources[0] ? String(map.sources[0]).toLowerCase() : '';
    var wrap;
    var btn;
    function onDoc(e) { if (wrap && !wrap.contains(e.target)) close(); }
    function onKey(e) { if (e.key === 'Escape') { close(); btn.focus(); } }
    function open() {
      pop.hidden = false;
      btn.setAttribute('aria-expanded', 'true');
      document.addEventListener('mousedown', onDoc);
      document.addEventListener('keydown', onKey);
      var first = pop.querySelector('button');
      if (first) first.focus();
    }
    function close() {
      pop.hidden = true;
      btn.setAttribute('aria-expanded', 'false');
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    }
    var pop = el('div', { class: 'map-pop', role: 'dialog', 'aria-label': 'Candidates for ' + c.name },
      el('div', { class: 'small muted' }, 'Other possible sources'),
      list.map(function (cand) {
        var usable = view.editable && splitColumnKey(cand.source).table.toLowerCase() === primary;
        return el('div', { class: 'map-pop-item' },
          el('div', { class: 'row' }, el('span', { class: 'mono small ellipsis', title: cand.source }, cand.source), el('span', { class: 'spacer' }), bar(cand.score)),
          el('div', { class: 'muted small' }, cand.why),
          usable ? el('div', { class: 'row' }, el('span', { class: 'spacer' }), el('button', {
            class: 'btn btn-sm', on: { click: function () { useCandidate(view.work, t.key, c.name, cand.source); close(); redraw(view); } }
          }, 'Use this')) : null);
      }));
    pop.hidden = true;
    btn = el('button', {
      class: 'btn btn-ghost btn-sm', 'aria-haspopup': 'dialog', 'aria-expanded': 'false', 'aria-label': list.length + ' candidates for ' + c.name,
      on: { click: function (e) { e.stopPropagation(); if (pop.hidden) open(); else close(); } }
    }, list.length + ' ▾');
    wrap = el('span', { class: 'map-popwrap' }, btn, pop);
    return wrap;
  }

  function uncoveredCard(view) {
    var cx = view.context;
    var list = ops.uncovered(cx, view.work);
    var head = el('div', { class: 'card-h row' }, el('div', { class: 'h3' }, 'Unmapped source columns'), el('span', { class: 'spacer' }), el('span', { class: 'chip' }, String(list.length)));
    if (!cx.source || !cx.source.length) {
      return el('section', { class: 'card', id: 'map-uncovered' }, head,
        el('div', { class: 'card-b' }, DBM.components.emptyState('Source catalog not available', 'This view only shows the mapping payload.')));
    }
    var byTable = {};
    list.forEach(function (key) { var p = splitColumnKey(key); (byTable[p.table] = byTable[p.table] || []).push(p.column); });
    var rows = [];
    cx.source.forEach(function (s) {
      var miss = byTable[s.key];
      if (!miss) return;
      var whole = miss.length === s.columns.length;
      rows.push(el('tr', { class: 'map-group-h' },
        el('td', { colspan: '2', class: 'mono' }, s.key + (whole ? ' — whole table unmapped' : '')),
        el('td', { class: 'map-actions' }, view.editable && whole
          ? el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { dropFlow(view, s.key, true); } } }, 'Drop table…') : null)));
      miss.forEach(function (col) {
        var key = s.key + '.' + col;
        var info = s.columns.filter(function (x) { return x.name === col; })[0] || {};
        var nameCell = el('td', { class: 'mono' }, col);
        view.ctx.commentable(nameCell, 'column:src:' + key, key);
        rows.push(el('tr', {}, nameCell,
          el('td', { class: 'mono small muted' }, (info.type || '') + (info.nullable === false ? ' not null' : '')),
          el('td', { class: 'map-actions' }, view.editable ? [
            el('button', { class: 'btn btn-sm', on: { click: function () { mapFlow(view, key); } } }, 'Map to…'),
            el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { dropFlow(view, key, false); } } }, 'Drop…')
          ] : null)));
      });
    });
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-uncovered' },
        el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, 'Source column'), el('th', { scope: 'col' }, 'Type'), el('th', { scope: 'col' }, 'Actions'))),
        el('tbody', {}, rows)))
      : DBM.components.emptyState('Every source column is mapped or dropped', 'Nothing on the source side blocks approval.');
    return el('section', { class: 'card', id: 'map-uncovered' }, head, el('div', { class: 'card-b' }, body));
  }

  function dropsCard(view) {
    var drops = view.work.drops || {};
    var keys = Object.keys(drops).sort();
    var rows = keys.map(function (k) {
      var d = drops[k];
      var cell = el('td', { class: 'mono' }, k);
      view.ctx.commentable(cell, (k.split('.').length > 2 ? 'column:src:' : 'table:src:') + k, k);
      return el('tr', {}, cell, el('td', { class: 'small' }, d.reason), el('td', {}, methodTag(d.method)),
        el('td', { class: 'map-actions' }, view.editable
          ? el('button', { class: 'btn btn-sm btn-ghost', on: { click: function () { undropSource(view.work, k); redraw(view); } } }, 'Undo') : null));
    });
    var body = rows.length
      ? el('div', { class: 'map-scroll' }, el('table', { class: 'tbl tbl-compact map-drops' },
        el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, 'Source'), el('th', { scope: 'col' }, 'Reason'), el('th', { scope: 'col' }, 'Method'), el('th', { scope: 'col' }, 'Actions'))),
        el('tbody', {}, rows)))
      : DBM.components.emptyState('No drops', 'Source tables and columns that are intentionally not migrated appear here.');
    return el('section', { class: 'card' },
      el('div', { class: 'card-h row' }, el('div', { class: 'h3' }, 'Drops'), el('span', { class: 'spacer' }), el('span', { class: 'chip' }, String(keys.length))),
      el('div', { class: 'card-b' }, body));
  }

  function dropFlow(view, key, wholeTable) {
    var input = el('input', { class: 'input', placeholder: 'Why is it safe not to migrate this?', 'aria-label': 'Reason for dropping ' + key });
    var body = el('div', { class: 'stack' },
      el('p', { class: 'small' }, wholeTable ? 'Drop every column of this source table:' : 'Drop this source column:'),
      el('code', { class: 'mono' }, key),
      input);
    DBM.components.modal({ title: wholeTable ? 'Drop source table' : 'Drop source column', body: body, confirmText: 'Drop' }).then(function (ok) {
      if (!ok) return;
      if (blank(input.value)) { view.ctx.toast('A drop needs a reason.', 'warn'); return; }
      dropSource(view.work, key, input.value);
      redraw(view);
    });
  }

  function skipFlow(view, tableKey) {
    var input = el('input', { class: 'input', placeholder: 'Why is this target table not loaded?', 'aria-label': 'Reason for skipping ' + tableKey });
    DBM.components.modal({ title: 'Skip target table', body: el('div', { class: 'stack' }, el('code', { class: 'mono' }, tableKey), input), confirmText: 'Skip' }).then(function (ok) {
      if (!ok) return;
      if (blank(input.value)) { view.ctx.toast('Skipping a table needs a reason.', 'warn'); return; }
      skipTable(view.work, tableKey, input.value.trim());
      redraw(view);
    });
  }

  function mapFlow(view, key) {
    var choices = mapTargetsFor(view.context, view.work, key);
    if (!choices.length) {
      view.ctx.toast('No target table uses ' + splitColumnKey(key).table + ' as its primary source. Pick it as the source of a target table first.', 'warn');
      return;
    }
    var select = el('select', { class: 'select', 'aria-label': 'Target column for ' + key },
      choices.map(function (o, i) {
        return el('option', { value: String(i) }, o.table + '.' + o.column + ' — ' + (o.type || '?') + (o.mapped ? ' (replaces current)' : ''));
      }));
    var body = el('div', { class: 'stack' },
      el('p', { class: 'small muted' }, 'The expression becomes s.' + quoteName(splitColumnKey(key).column) + '. Refine it afterwards in the column table.'),
      select);
    DBM.components.modal({ title: 'Map ' + key, body: body, confirmText: 'Map' }).then(function (ok) {
      if (!ok) return;
      var o = choices[Number(select.value)];
      try {
        mapToTarget(view.work, key, o.table, o.column);
        view.expanded[o.table] = true;
        redraw(view);
      } catch (e) {
        view.ctx.toast(e.message, 'err');
      }
    });
  }

  function saveBar(view) {
    var status = el('span', { class: 'small', 'aria-live': 'polite' });
    var discard = el('button', {
      class: 'btn btn-ghost', on: {
        click: function () {
          view.work = clone(view.before);
          redraw(view);   // updateBar sees a clean held view and releases the hold (loads the newer version)
        }
      }
    }, 'Discard');
    var save = el('button', { class: 'btn btn-primary', on: { click: function () { saveEdits(view); } } }, 'Save as new version');
    view.bar = { status: status, discard: discard, save: save };
    updateBar(view);
    return el('div', { class: 'toolbar map-savebar' }, status, el('span', { class: 'spacer' }), discard, save);
  }

  function updateBar(view) {
    if (!view.bar) return;
    var n = changeCount(diff(view.before, view.work));
    view.bar.status.textContent = (n ? n + ' unsaved change' + (n === 1 ? '' : 's') : 'No unsaved changes') +
      (n && view.held != null ? ' — cannot be saved: v' + view.held + ' is newer' : '');
    view.bar.status.classList.toggle('map-dirty', n > 0);
    view.bar.save.disabled = n === 0 || view.saving || view.held != null;   // held: the server would reject the stale baseVersion
    view.bar.save.title = view.held != null ? 'A newer version (v' + view.held + ') exists; these edits cannot be saved against it.' : '';
    view.bar.discard.disabled = n === 0 || view.saving;
    // A held view that has become clean — by Discard or by typing the edits back out — releases the hold itself instead of
    // waiting for an unrelated refresh: nothing is left to protect, and its disabled controls would otherwise strand the user.
    // Order matters: clear the hold, give the controls back (normal review bar, Save by the usual rule), then refresh — so
    // the screen is usable even if that refresh fails. Should edits be made again on this stale view, the next refresh
    // (or a rejected save, via failed()) re-engages the hold.
    if (n === 0 && view.held != null && view === current) {
      view.held = null;
      setReviewBar(view, null);
      updateBar(view);
      view.ctx.refresh();
    }
  }

  /** Swap the page's review bar for one built with (blocked reason | null for the normal bar). */
  function setReviewBar(view, blocked) {
    if (!view.review || view.review.parentNode !== view.page || !DBM.components.reviewBar) return;
    var bar = blocked ? DBM.components.reviewBar(view.ctx, { blocked: blocked }) : DBM.components.reviewBar(view.ctx);
    view.page.replaceChild(bar, view.review);
    view.review = bar;
  }

  function saveEdits(view) {
    var list = diff(view.before, view.work);
    if (!list.length || view.saving) return;
    view.saving = true;
    view.bar.save.classList.add('is-loading');
    updateBar(view);
    var n = changeCount(list);
    var sent = clone(view.work);
    var patch = {
      phase: 'mapping', baseVersion: view.ctx.version, ops: list, responses: [],
      summary: 'Human edit: ' + n + ' change' + (n === 1 ? '' : 's') + ' in the mapping screen'
    };
    // A re-render during the POST may have moved these edits into a new view instance (render carries `work` by reference).
    function live() { return current && current !== view && current.work === view.work ? [view, current] : [view]; }
    function failed(message) {
      live().forEach(function (v) {
        v.saving = false;
        if (v.bar) { v.bar.save.classList.remove('is-loading'); updateBar(v); }
      });
      view.ctx.toast('Not saved: ' + message, 'err');
      // The usual cause is a stale baseVersion: a newer version landed during the POST (its SSE refresh was held silently
      // while saving). Ask the shell again now, so holdRender engages the hold and disables Save/Approve immediately rather
      // than whenever some unrelated event next refreshes. On a same-version failure this is a carried re-render.
      view.ctx.refresh();
    }
    view.ctx.api.post('/api/edit/mapping', patch).then(function (res) {
      if (res && res.ok === false) { failed((res.errors || []).join('; ') || 'rejected'); return; }
      // What was sent is now server state: it is no longer an unsaved edit, so the refresh below (and the artifact_created
      // this save emits) must replace the view rather than be held for it. Edits typed during the POST stay dirty.
      live().forEach(function (v) { v.saving = false; v.before = clone(sent); });
      var open = (res && res.warnings) || [];
      view.ctx.toast('Saved as v' + res.version + (open.length ? ' — ' + open.length + ' open item' + (open.length === 1 ? '' : 's') : ''), 'ok');
      view.ctx.refresh();
    }).catch(function (e) {
      var details = e && e.details && e.details.length ? ' (' + e.details.join('; ') + ')' : '';
      failed(((e && e.message) || 'request failed') + details);
    });
  }

  // No context check: a view without context has before === work unless edits were carried, and carried edits bring their
  // context. Requiring context here was the mechanism of finding H1 (a carried view looked clean while refetching); if the
  // carry ever regresses, this stays true and the hold still protects the edits.
  function isDirty(view) { return !!view && diff(view.before, view.work).length > 0; }

  /**
   * app.js asks before replacing this view. Declines (returns true) only when unsaved edits would be destroyed by moving to
   * a different base version while staying on the latest one — i.e. a new version arrived underneath the edits. Those edits
   * cannot be carried onto it (the server rejects a stale baseVersion and replaying them could overwrite the newer version),
   * so the view stays on its base until the user discards. Navigating to an older version, or a view that becomes read-only,
   * is not held. Same-version re-renders are not held either: render() carries the edits across.
   */
  function holdRender(next) {
    var view = current;
    if (!isDirty(view) || !next || next.version === view.ctx.version || next.version !== next.latestVersion) return false;
    if (next.readOnly || !next.phaseRow || next.phaseRow.status !== 'awaiting_review') return false;   // no longer editable at all
    if (view.saving) return true;   // our own save produced the new version; its completion refreshes
    if (view.held !== next.version) {
      view.held = next.version;
      // The controls must agree with the warning: nothing on this stale view may act on a version it has not shown.
      updateBar(view);
      setReviewBar(view, 'A newer version (v' + next.version + ') exists. It must be loaded before this phase can be approved or ' +
        'sent back: click Discard below to drop your unsaved edits and load it.');
      view.ctx.toast('A newer mapping version (v' + next.version + ') was created. Your unsaved edits to v' + view.ctx.version +
        ' are still on screen but cannot be saved against it. Click Discard to drop them and load v' + next.version + '.', 'warn');
    }
    return true;
  }

  function onEvent(evt, ctx) {
    var type = evt && evt.type;
    var data = (evt && (evt.data || evt.payload)) || {};
    if (type !== 'artifact_created' || data.phase !== 'mapping') return;
    // Dirty: nothing to do here. The shell's refresh for this event asks holdRender, which keeps the edits and warns once.
    if (!isDirty(current)) ctx.refresh();
  }

  /** app.js calls this when another view replaces the mapping view (navigation, or the phase going stale). */
  function leave(nextCtx) {
    if (isDirty(current)) (nextCtx || current.ctx).toast('Your unsaved mapping edits were discarded: the mapping view was closed.', 'warn');
  }

  DBM.views = DBM.views || {};
  DBM.views.mapping = { title: 'Mapping', render: render, onEvent: onEvent, holdRender: holdRender, leave: leave };
})(window.DBM = window.DBM || {});
