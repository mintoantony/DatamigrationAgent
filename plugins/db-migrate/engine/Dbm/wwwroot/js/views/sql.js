/* views/sql.js — SQL phase: global pre/post, tasks in execution order, highlighted SQL with line anchors,
   in-place editing, per-task version diff, live validation and the script-pack download.
   Pure helpers are exposed as DBM.sqlView for node tests (no DOM access at load time). */
(function (DBM) {
  'use strict';

  var SECTIONS = ['pre', 'source', 'staging', 'merge', 'post'];
  var SECTION_TITLE = { pre: 'Task pre-load', source: 'Source query', staging: 'Staging table', merge: 'Merge into target', post: 'Task post-load' };
  var SECTION_SIDE = { pre: 'target', source: 'source', staging: 'target', merge: 'target', post: 'target' };
  var INT_MAX = 2147483647;

  /* ---------- pure helpers ---------- */

  /* "\r\n" → "\n" only: a lone "\r" is not a line break (TaskListing rule 3). */
  function norm(s) { return String(s == null ? '' : s).replace(/\r\n/g, '\n'); }

  /**
   * MIRROR of Dbm.Core.SqlGen.TaskListing.Build — the meaning of a comment anchor sql:<taskId>:<line>. Its C# doc comment is the
   * shared definition; js/fixtures/task-listing.json is asserted against both implementations. Do not change one side alone.
   * Inputs in order: each task.preSql element → "pre", sourceQuery → "source", stagingDdl → "staging", mergeSql → "merge", each
   * task.postSql element → "post". Skip only null/undefined/"" (a whitespace-only text IS numbered). Replace "\r\n" with "\n"
   * (a lone "\r" stays), split on "\n" keeping empty pieces (a trailing newline numbers an empty last line), never trim.
   * Numbers are 1-based and continuous across every section and statement.
   */
  function listing(task) {
    var out = [];
    eachText(task, function (section, text) {
      norm(text).split('\n').forEach(function (line) { out.push({ no: out.length + 1, section: section, text: line }); });
    });
    return out;
  }

  /* The listing's inputs in order, skip rule applied: fn(section, rawText) for every text that contributes lines. */
  function eachText(task, fn) {
    function add(section, text) { if (text != null && text !== '') fn(section, text); }
    (task.preSql || []).forEach(function (s) { add('pre', s); });
    add('source', task.sourceQuery);
    add('staging', task.stagingDdl);
    add('merge', task.mergeSql);
    (task.postSql || []).forEach(function (s) { add('post', s); });
  }

  function listingText(task) { return listing(task).map(function (l) { return l.text; }).join('\n'); }

  /**
   * MIRROR of Dbm.Core.SqlGen.SqlModule.ParseAnchor (same shared fixture). "task:<id>" → {task: id, line: null} (the rest verbatim);
   * "sql:<id>:<line>" splits on the LAST ':' and needs a non-empty id and a line of plain ASCII digits only (no sign, spaces,
   * separators or other digit forms — C# NumberStyles.None) whose value is 1..2147483647 (C# int). Anything else → nulls.
   */
  function parseAnchor(anchor) {
    var none = { task: null, line: null };
    if (typeof anchor !== 'string') return none;
    if (anchor.indexOf('task:') === 0) return { task: anchor.slice(5), line: null };
    if (anchor.indexOf('sql:') !== 0) return none;
    var rest = anchor.slice(4);
    var colon = rest.lastIndexOf(':');
    var digits = rest.slice(colon + 1);
    if (colon <= 0 || !/^[0-9]+$/.test(digits)) return none;
    var value = Number(digits);
    return value >= 1 && value <= INT_MAX ? { task: rest.slice(0, colon), line: value } : none;
  }

  /* The listing grouped by section, each line carrying its highlighted HTML. Each stored text (one statement, or one query) is
     highlighted on its own, from the RAW text, so a comment or string spanning lines keeps its colour and sqlLines splits it by
     the same rule as the listing: html[i] belongs to that text's line i. Highlighting the already-split lines re-joined with
     "\n" would normalise twice ("x\r\r\ny" → "x\r\ny" → "x\ny") and drop a character the listing keeps. */
  function sectionBlocks(task) {
    var all = listing(task);
    var htmls = [];
    eachText(task, function (section, text) { htmls = htmls.concat(DBM.highlight.sqlLines(text)); });
    return SECTIONS.map(function (sec) {
      var lines = [];
      all.forEach(function (l, i) { if (l.section === sec) lines.push({ no: l.no, text: l.text, html: htmls[i] || '' }); });
      return lines.length ? { section: sec, lines: lines } : null;
    }).filter(Boolean);
  }

  function splitStatements(text) {
    return norm(text).split(/^[ \t]*GO[ \t]*$/im).map(function (s) { return s.trim(); }).filter(Boolean);
  }

  function joinStatements(list) { return (list || []).join('\nGO\n'); }

  /* Open item 14, Ruling 205: 1-based lines of `text` (CRLF normalised, as splitStatements reads it) that splitStatements would treat as
     a GO separator although they sit inside a comment or a string, as DBM.highlight.tokenize reads the whole text (the way SQL Server
     does: nested block comments, '' escapes). The engine cannot be mirrored here: it has no splitter, and it refuses ANY GO line inside
     a statement (SqlValidator.GoLine), comment or not, so such a line cannot be stored in any form. The editor names it instead of
     posting a statement cut in two. */
  function goSplitHazards(text) {
    var src = norm(text);
    var spans = [];
    var at = 0;
    DBM.highlight.tokenize(src).forEach(function (tok) {
      if ((tok.c === 'com' || tok.c === 'str' || tok.c === 'id') && /[\r\n]/.test(tok.v)) spans.push([at, at + tok.v.length]);
      at += tok.v.length;
    });
    var out = [];
    var start = 0;
    // Review N1: split on "\n" AND a lone "\r" - after norm() every break is one character, and splitStatements' /m reads both.
    src.split(/[\r\n]/).forEach(function (line, i) {
      if (/^[ \t]*GO[ \t]*$/i.test(line) && spans.some(function (s) { return s[0] < start && start < s[1]; })) out.push(i + 1);
      start += line.length + 1;
    });
    return out;
  }

  /* Ruling 70: a statement list is editable as one text only if splitting its joined text gives back exactly the stored list,
     element for element. Otherwise (a GO line inside a comment or string, a whitespace-only or padded statement, CRLF) a save
     would rewrite statements the user never touched and shift every later line number. */
  function listRoundTrips(list) {
    var stored = list || [];
    var back = splitStatements(joinStatements(stored));
    return back.length === stored.length && back.every(function (x, i) { return x === stored[i]; });
  }

  function trimEnd(s) { return s.replace(/\s+$/, ''); }

  /* Patch ops (JSON pointers into the SQL artifact) for the fields the user changed. */
  function editOps(id, task, edits) {
    var ops = [];
    var base = '/tasks/' + id + '/';
    ['sourceQuery', 'stagingDdl', 'mergeSql'].forEach(function (f) {
      if (!Object.prototype.hasOwnProperty.call(edits, f)) return;
      var before = trimEnd(norm(task[f]));
      var after = trimEnd(norm(edits[f]));
      if (after === before) return;
      if (!after && f !== 'sourceQuery') { if (task[f] != null) ops.push({ op: 'remove', path: base + f }); return; }
      ops.push({ op: task[f] == null ? 'add' : 'replace', path: base + f, value: after });
    });
    ['preSql', 'postSql'].forEach(function (f) {
      if (!Object.prototype.hasOwnProperty.call(edits, f)) return;
      var after = splitStatements(edits[f]);
      var before = (task[f] || []).map(function (s) { return norm(s).trim(); });
      if (JSON.stringify(after) === JSON.stringify(before)) return;
      ops.push({ op: task[f] == null ? 'add' : 'replace', path: base + f, value: after });
    });
    return ops;
  }

  var SKIPPED_PREFIX = 'live validation skipped: ';
  /* Verbatim copy of C# SqlModule.NoEvidence (sql-view.test.cjs reads the C# source and compares). */
  var NO_EVIDENCE = 'no live validation is recorded for this version; press Validate live on the SQL screen (or run `dbm sql validate`) while it awaits review to record one';
  /* Verbatim copy of C# SqlModule.ValidatedWithErrors (sql-view.test.cjs reads the C# source and compares). Sweep I review,
     follow-up (a): evidence with ok:false is not "validated" either - the same presence-of-marker shape Ruling 204 closed for the
     absent-evidence case. */
  var VALIDATED_WITH_ERRORS = 'the last live validation reported errors';

  /**
   * MIRROR of C# SqlPlanPayload.NotValidatedReasons (open item 10, Ruling 204; refined by sweep I review follow-up a): a version is
   * validated only when it carries the engine's evidence ("validation": {at, ok}) WITH ok true, and no stored "live validation
   * skipped" line. Reasons = those lines, else VALIDATED_WITH_ERRORS when evidence exists with ok false, else each stored
   * connection failure as "live validation could not connect: …" (review L3), else NO_EVIDENCE.
   * → {validated, at, ok, reasons}
   */
  function validationState(plan) {
    var skipped = ((plan && plan.warnings) || []).filter(function (w) { return typeof w === 'string' && w.indexOf(SKIPPED_PREFIX) === 0; });
    var v = plan && plan.validation;
    var evidence = !!v && typeof v === 'object' && typeof v.at === 'string';
    if (evidence && v.ok === true && !skipped.length) return { validated: true, at: v.at, ok: true, reasons: [] };
    if (skipped.length) return { validated: false, at: null, ok: null, reasons: skipped };
    if (evidence && v.ok !== true) return { validated: false, at: v.at, ok: false, reasons: [VALIDATED_WITH_ERRORS] };
    var failed = ((plan && plan.errors) || []).filter(function (e) {
      return typeof e === 'string' && (e.indexOf('source connection failed: ') === 0 || e.indexOf('target connection failed: ') === 0);
    }).map(function (e) { return 'live validation could not connect: ' + e; });
    return { validated: false, at: null, ok: null, reasons: failed.length ? failed : [NO_EVIDENCE] };
  }

  /* {taskId: string[]} — the contracted shape of taskErrors / taskWarnings. */
  function isTaskLists(map) {
    if (!map || typeof map !== 'object' || Array.isArray(map)) return false;
    return Object.keys(map).every(function (k) { return Array.isArray(map[k]); });
  }

  function countLists(map) {
    return isTaskLists(map) ? Object.keys(map).reduce(function (n, k) { return n + map[k].length; }, 0) : 0;
  }

  /**
   * Verdict of a POST /api/sql/validate report. A list that is absent (or not in its contracted shape) means nobody said what was
   * checked, so the verdict is 'unreported' and never 'clean': globalWarnings must be an array ([] = the global statements were
   * checked and are clean, T4.3), taskErrors and taskWarnings must each be {taskId: string[]}. Every warning line counts —
   * "not checked" lines included — so a statement that could not be checked is never shown as a pass. Counts only; warning text
   * is never inspected.
   * → {state: 'errors'|'unreported'|'warnings'|'clean', errors, warnings, globalCoverage, taskCoverage: 'checked'|'unreported'}
   */
  function reportSummary(r) {
    var globalReported = Array.isArray(r.globalWarnings);
    var tasksReported = isTaskLists(r.taskErrors) && isTaskLists(r.taskWarnings);
    var errors = (Array.isArray(r.globalErrors) ? r.globalErrors.length : 0) + countLists(r.taskErrors);
    var warnings = (globalReported ? r.globalWarnings.length : 0) + countLists(r.taskWarnings);
    var state = !r.ok || errors ? 'errors' : !globalReported || !tasksReported ? 'unreported' : warnings ? 'warnings' : 'clean';
    return {
      state: state, errors: errors, warnings: warnings,
      globalCoverage: globalReported ? 'checked' : 'unreported', taskCoverage: tasksReported ? 'checked' : 'unreported',
    };
  }

  /* A lone "\r" (not part of "\r\n") — a browser textarea turns it into "\n", so such a field cannot round-trip through one. */
  function hasBareCr(value) {
    var texts = Array.isArray(value) ? value : [value];
    return texts.some(function (s) { return typeof s === 'string' && /\r(?!\n)/.test(s); });
  }

  /* Line diff of two tasks' listings (TaskListing lines, so a lone CR vs LF is a difference). DBM.diff only ever sees one opaque,
     break-free key per distinct line, so its own line splitter has nothing to re-split; rows carry the real text with a bare CR
     made visible. → [{op: 'eq'|'add'|'del', text}] */
  function diffRows(oldTask, newTask) {
    var keys = Object.create(null);
    var texts = [];
    function keyed(task) {
      return (task ? listing(task) : []).map(function (l) {
        if (!(('=' + l.text) in keys)) { keys['=' + l.text] = 'L' + texts.length; texts.push(l.text); }
        return keys['=' + l.text];
      }).join('\n');
    }
    var a = keyed(oldTask);
    var b = keyed(newTask);
    return DBM.diff.lines(a, b).map(function (r) {
      return { op: r.op, text: texts[Number(r.text.slice(1))].replace(/\r/g, '␍') };
    });
  }

  /* {taskId: number of draft/open comments anchored on the task or one of its lines}, resolved with parseAnchor. */
  function openCommentsByTask(feedback) {
    var out = {};
    (feedback || []).forEach(function (f) {
      if (!f || (f.status !== 'draft' && f.status !== 'open')) return;
      var a = parseAnchor(f.anchor);
      if (a.task) out[a.task] = (out[a.task] || 0) + 1;
    });
    return out;
  }

  function findByTarget(plan, target) {
    if (!plan || !plan.tasks) return null;
    var ids = Object.keys(plan.tasks);
    for (var i = 0; i < ids.length; i++) if (plan.tasks[ids[i]].target === target) return plan.tasks[ids[i]];
    return null;
  }

  function plural(n, word) { return n + ' ' + word + (n === 1 ? '' : 's'); }

  /* ---------- context helpers (ctx from app.js: artifact = the version being shown) ---------- */

  function payloadOf(ctx) {
    var a = ctx && ctx.artifact;
    return a && a.payload && a.payload.tasks && a.payload.order ? a.payload : null;
  }

  function versionOf(ctx) {
    if (!ctx) return null;
    if (typeof ctx.version === 'number') return ctx.version;
    if (ctx.artifact && typeof ctx.artifact.version === 'number') return ctx.artifact.version;
    return typeof ctx.latestVersion === 'number' ? ctx.latestVersion : null;
  }

  function versionsOf(ctx) { return Array.isArray(ctx.versions) ? ctx.versions : []; }

  function exported() { return !!globalThis.DBM_EXPORT; }

  function live(ctx) { return !exported() && !!ctx.api; }

  /* app.js sets readOnly unless the phase awaits review and the current version is shown. */
  function canEdit(ctx) { return !ctx.readOnly && live(ctx); }

  function errorText(e, fallback) {
    if (DBM.components && DBM.components.errorText && e) return DBM.components.errorText(e);
    return (e && e.message) || fallback;
  }

  /* ---------- DOM helpers ---------- */

  function el(tag, attrs, kids) {
    var args = [tag, attrs || {}];
    (kids || []).forEach(function (k) {
      if (k === null || k === undefined || k === false) return;
      args.push(typeof k === 'number' ? String(k) : k);
    });
    return DBM.h.apply(null, args);
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  function list(cls, items) {
    return el('ul', { class: 'sql-msgs ' + cls }, items.map(function (m) { return el('li', {}, [m]); }));
  }

  /* The lines a code block numbers: CRLF normalised, split on \n, a lone \r left inside its line (never a break). This is the
     whole numbering rule of the GLOBAL pre/post card, which is codeBlock(joinStatements(list)) — the engine's
     SqlValidator.FindBareCarriageReturns reports bare carriage returns at these numbers. Exported for the shared fixture
     (Dbm.Tests/js/fixtures/task-listing.json, "globalCard"), which asserts it against the engine; nothing else calls it. */
  function cardLines(text) { return norm(text).split('\n'); }

  function codeBlock(text) {
    var lines = cardLines(text);
    var html = DBM.highlight.sqlLines(text);
    return el('pre', { class: 'code sql-code' }, lines.map(function (_, i) {
      return el('div', { class: 'line' }, [el('span', { class: 'ln' }, [String(i + 1)]), el('span', { html: html[i] || ' ' })]);
    }));
  }

  /* ---------- view state ---------- */

  // editor: {id, task, areas: {field: textarea}} while a task is being edited; drafts: {id, version, values} so a same-version
  // re-render (a comment posted, agent presence) rebuilds the editor with what was typed rather than the stored SQL.
  // Only fields the user typed into are in drafts.values (the input handler records them), so opening the editor dirties nothing
  // and an untouched field is never re-derived from its textarea (which would normalise a CR or re-split statements on GO).
  function freshUi() {
    return { selected: null, editing: null, editor: null, drafts: null, diff: null, report: null, reportFailure: null, busy: false, saving: false, forVersion: undefined, heldWarned: false };
  }
  var ui = freshUi();
  var mounted = null;

  function rerender() { if (mounted) render(mounted.root, mounted.ctx); }

  /* The edits the user actually made (Ruling 70): {field: textarea value} for editable fields whose value differs from the value
     the textarea opened with. A net no-op (type, then undo) is not an edit, and an untouched field is never re-derived. */
  function touchedEdits() {
    var e = ui.editor;
    var edits = {};
    if (!e || ui.editing !== e.id) return edits;
    Object.keys(e.areas).forEach(function (k) { if (e.areas[k].value !== e.opened[k]) edits[k] = e.areas[k].value; });
    return edits;
  }

  function isDirty() {
    var e = ui.editor;
    return !!e && ui.editing === e.id && Object.keys(touchedEdits()).length > 0;
  }

  function discard() { ui.editing = null; ui.editor = null; ui.drafts = null; ui.heldWarned = false; }

  function selectedId(plan) {
    if (ui.selected && plan.tasks[ui.selected]) return ui.selected;
    for (var i = 0; i < plan.order.length; i++) {
      var t = plan.tasks[plan.order[i]];
      if (t && t.errors && t.errors.length) return plan.order[i];
    }
    return plan.order[0] || null;
  }

  function select(id) {
    if (isDirty() && typeof globalThis.confirm === 'function' && !globalThis.confirm('Discard your unsaved SQL edits?')) return;
    ui.selected = id; discard(); ui.diff = null; rerender();
  }

  /* ---------- sections ---------- */

  function toolbar(ctx, plan) {
    if (!plan || !live(ctx)) return null;
    var attrs = { class: 'btn' + (ui.busy ? ' is-loading' : ''), type: 'button', disabled: ui.busy, on: { click: function () { runValidate(ctx); } } };
    return el('div', { class: 'toolbar row' }, [
      el('button', attrs, [ui.busy ? 'Validating…' : 'Validate live']),
      el('a', { class: 'btn btn-ghost', href: ctx.api.url('/api/export/sqlpack'), download: true, title: 'Script pack of the current version' }, ['Download script pack']),
    ]);
  }

  /* A verdict belongs to one version. The server validates its CURRENT version; a report is kept only while that is the version on
     screen when it arrives — a late response after a newer version rendered, or a report on the current version while an older one
     is viewed, is discarded rather than shown as the verdict on what the reviewer is reading. */
  function reportFits(report) {
    return !!report && mounted !== null && typeof report.version === 'number' && report.version === versionOf(mounted.ctx);
  }

  function runValidate(ctx) {
    ui.busy = true;
    rerender();
    // Rulings 210/211: the version on screen is named; a whole-plan pass over a version without evidence is stored by the server
    // as a new version (only while Sql awaits review), and the screen moves onto it.
    ctx.api.post('/api/sql/validate', { version: versionOf(ctx) }).then(function (report) {
      ui.busy = false;
      if (!mounted) return;   // the view was left while validating
      var toast = mounted.ctx.toast || ctx.toast;
      ui.reportFailure = null;
      if (report && report.stored === true) {
        ui.report = null;
        rerender();
        toast('Validation passed and was recorded: saved as v' + report.storedVersion + ' (no SQL change)', 'ok');
        (mounted.ctx.refresh || ctx.refresh)();
        return;
      }
      if (!reportFits(report)) {
        ui.report = null;
        rerender();
        toast('The validation result is for ' + (report && report.version != null ? 'v' + report.version : 'another version') +
          ', not the version on screen, so it is not shown. Validate again on the current version.', 'info');
        return;
      }
      ui.report = report;
      rerender();
      var s = reportSummary(report);
      var still = payloadOf(mounted.ctx) && !validationState(payloadOf(mounted.ctx)).validated;
      if (still && (s.state === 'clean' || s.state === 'warnings')) {
        // Never "passed" beside a card that says Not validated: this pass recorded nothing.
        toast('Validation passed, but nothing was recorded, so v' + report.version + ' is still not validated'
          + (report.storeNote ? ': ' + report.storeNote : '') + '.', 'warn');
      } else if (s.state === 'clean') toast('Validation passed', 'ok');
      else if (s.state === 'warnings') toast('Validation passed with ' + plural(s.warnings, 'warning') + ' — read them before approving', 'warn');
      else if (s.state === 'unreported') toast('Validation passed, but the server did not report what it checked', 'warn');
      else toast('Validation found ' + plural(s.errors, 'error'), 'err');
    }).catch(function (e) {
      ui.busy = false;
      // Open item 13: the verdict still on screen came from an earlier check of this same version (a verdict on another version
      // never survives, see reportFits); it stays, marked as earlier, next to why the latest attempt failed.
      ui.reportFailure = ui.report && reportFits(ui.report) ? errorText(e, 'Validation failed') : null;
      rerender();
      ctx.toast(errorText(e, 'Validation failed'), 'err');
    });
  }

  function kpis(plan) {
    var tasks = plan.order.map(function (id) { return plan.tasks[id]; }).filter(Boolean);
    var custom = tasks.filter(function (t) { return t.custom; }).length;
    var warnings = (plan.warnings || []).length + tasks.reduce(function (n, t) { return n + (t.warnings || []).length; }, 0);
    var errors = (plan.errors || []).length + tasks.reduce(function (n, t) { return n + (t.errors || []).length; }, 0);
    return el('div', { class: 'grid-kpi' }, [
      DBM.components.kpi('Tasks', DBM.fmt.num(tasks.length), 'in execution order'),
      DBM.components.kpi('Custom SQL', DBM.fmt.num(custom), custom ? 'edited by agent or human' : 'all generated'),
      DBM.components.kpi('Warnings', DBM.fmt.num(warnings), 'review before approval'),
      DBM.components.kpi('Errors', DBM.fmt.num(errors), errors ? 'block approval' : 'none'),
    ]);
  }

  var REPORT_BADGE = {
    clean: ['st-approved', 'ok'], warnings: ['st-stale', 'ok · warnings'], unreported: ['st-stale', 'ok · coverage not reported'], errors: ['st-failed', 'errors'],
  };

  function reportCard(plan) {
    var r = ui.report;
    var s = reportSummary(r);
    var rows = [];
    (Array.isArray(r.globalErrors) ? r.globalErrors : []).forEach(function (e) { rows.push(el('li', { class: 'sql-count-err' }, ['plan: ' + e])); });
    if (Array.isArray(r.globalWarnings)) r.globalWarnings.forEach(function (w) { rows.push(el('li', { class: 'sql-report-warn' }, ['plan: ' + w])); });
    else rows.push(el('li', { class: 'sql-report-warn' }, ['plan: global statements: coverage not reported (the response has no globalWarnings list, so the global pre-load/post-load statements may not have been checked)']));
    if (s.taskCoverage !== 'checked') {
      var absent = ['taskErrors', 'taskWarnings'].filter(function (f) { return !isTaskLists(r[f]); });
      rows.push(el('li', { class: 'sql-report-warn' }, ['tasks: task coverage not reported (the response has no usable ' + absent.join(' or ') + ', so tasks may not have been checked)']));
    }
    var te = isTaskLists(r.taskErrors) ? r.taskErrors : {};
    var tw = isTaskLists(r.taskWarnings) ? r.taskWarnings : {};
    var ids = plan.order.slice();
    Object.keys(te).concat(Object.keys(tw)).forEach(function (id) { if (ids.indexOf(id) < 0) ids.push(id); });
    ids.forEach(function (id) {
      var errs = te[id] || [];
      var warns = tw[id] || [];
      if (!errs.length && !warns.length) return;
      rows.push(el('li', { class: 'sql-report-task' }, [
        plan.tasks[id]
          ? el('button', { class: 'chip sql-chip-btn', type: 'button', on: { click: function () { select(id); } } }, [id])
          : el('span', { class: 'chip' }, [id]),
        errs.length ? list('sql-msgs-err', errs) : null,
        warns.length ? list('sql-msgs-warn', warns) : null,
      ]));
    });
    var badge = REPORT_BADGE[s.state];
    var failure = ui.reportFailure;
    return el('section', { class: 'card sql-report is-' + s.state + (failure ? ' is-earlier' : ''), 'aria-live': 'polite' }, [
      el('div', { class: 'card-h row row-wrap' }, [
        el('h3', { class: 'h3' }, ['Live validation' + (r.version != null ? ' · v' + r.version : '')]),
        el('span', { class: 'badge ' + badge[0] }, [badge[1]]),
        failure ? el('span', { class: 'badge st-stale' }, ['from an earlier check']) : null,
        s.errors || s.warnings ? el('span', { class: 'small muted' }, [plural(s.errors, 'error') + ', ' + plural(s.warnings, 'warning')]) : null,
        el('span', { class: 'spacer' }),
        el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { ui.report = null; ui.reportFailure = null; rerender(); } } }, ['Dismiss']),
      ]),
      el('div', { class: 'card-b' }, [failure ? el('p', { class: 'sql-count-err sql-report-failure' }, [
        'The latest validation of ' + (r.version != null ? 'v' + r.version : 'this version') + ' failed: ' + failure
        + ' The result below is from an earlier check of this same version.',
      ]) : null, s.state === 'clean'
        ? el('p', { class: 'muted' }, ['Every task and the global statements were checked: no errors or warnings.'])
        : el('ul', { class: 'sql-msgs sql-report-list' }, rows)]),
    ]);
  }

  /* Ruling 204: the stored version's own validation fact - the one ApprovalBlockers reads - never inferred from warnings alone.
     Follow-up (a): v.validated now implies v.ok (validationState never returns validated:true with ok:false), so a version whose
     last live run found errors shows the "Not validated" card below, VALIDATED_WITH_ERRORS among its reasons - not this line. */
  function validationNote(ctx, plan) {
    var v = validationState(plan);
    if (v.validated) {
      return el('p', { class: 'small muted sql-validated' }, ['Validated live ' + v.at]);
    }
    // Review L1: an approved version without evidence was approved before the evidence was kept; it does not "block" anything now.
    // Re-review N6: the version SHOWN is the approved one - not merely an approved phase.
    var approved = !!ctx.phaseRow && typeof ctx.phaseRow.approvedVersion === 'number' && ctx.phaseRow.approvedVersion === versionOf(ctx);
    return el('section', { class: 'card sql-not-validated', 'aria-live': 'polite' }, [
      el('div', { class: 'card-h row' }, [el('h3', { class: 'h3' }, ['Not validated']),
        el('span', { class: 'badge ' + (approved ? 'st-stale' : 'st-failed') }, [approved ? 'approved before validation evidence was kept' : 'blocks approval'])]),
      el('div', { class: 'card-b' }, [list('sql-msgs-warn', v.reasons)]),
    ]);
  }

  function globalCard(title, when, statements) {
    var body = statements && statements.length
      ? codeBlock(joinStatements(statements))
      : el('p', { class: 'muted small' }, ['None.']);
    return el('section', { class: 'card' }, [
      el('div', { class: 'card-h row' }, [el('h3', { class: 'h3' }, [title]), el('span', { class: 'small muted' }, [when])]),
      el('div', { class: 'card-b' }, [body]),
    ]);
  }

  function taskList(plan, activeId, comments) {
    return el('nav', { class: 'sql-tasks', 'aria-label': 'Tasks in execution order' }, plan.order.map(function (id) {
      var t = plan.tasks[id];
      if (!t) return null;
      var errs = (t.errors || []).length;
      var warns = (t.warnings || []).length;
      var active = id === activeId;
      return el('button', {
        class: 'sql-task' + (active ? ' is-active' : ''), type: 'button', 'aria-current': active ? 'true' : null,
        on: { click: function () { if (id !== activeId) select(id); } },
      }, [
        el('span', { class: 'sql-task-h' }, [el('span', { class: 'mono small' }, [id]), el('span', { class: 'ellipsis' }, [t.target])]),
        el('span', { class: 'sql-task-meta' }, [
          el('span', { class: 'tag' }, [t.mode === 'staging_merge' ? 'staging + merge' : 'direct']),
          t.custom ? el('span', { class: 'chip' }, ['custom']) : null,
          errs ? el('span', { class: 'pill sql-count-err' }, [plural(errs, 'error')]) : null,
          warns ? el('span', { class: 'pill sql-count-warn' }, [plural(warns, 'warning')]) : null,
          comments[id] ? el('span', { class: 'pill' }, [plural(comments[id], 'comment')]) : null,
          (t.dependsOn || []).length ? el('span', { class: 'small muted' }, ['after ' + t.dependsOn.join(', ')]) : null,
        ]),
      ]);
    }));
  }

  function taskDetail(ctx, plan, id, version) {
    var t = plan.tasks[id];
    var head = el('div', { class: 'card-h row row-wrap' }, [
      el('h2', { class: 'h2' }, [el('span', { class: 'mono' }, [id]), ' ', t.target]),
      el('span', { class: 'spacer' }),
      diffPicker(ctx, id, t, version),
      canEdit(ctx) && ui.editing !== id
        ? el('button', { class: 'btn btn-sm', type: 'button', on: { click: function () { discard(); ui.editing = id; ui.diff = null; rerender(); } } }, ['Edit SQL'])
        : null,
    ]);
    ctx.commentable(head, 'task:' + id, id + ' ' + t.target);

    var meta = el('div', { class: 'row-wrap sql-meta' }, [
      el('span', { class: 'tag' }, [t.mode === 'staging_merge' ? 'staging + merge' : 'direct']),
      t.identityInsert ? el('span', { class: 'tag' }, ['identity insert']) : null,
      el('span', { class: 'tag' }, [(t.keyColumns || []).length ? 'keys ' + t.keyColumns.join(', ') : 'no key · single transaction']),
      t.chunkSize ? el('span', { class: 'tag' }, ['chunk ' + DBM.fmt.num(t.chunkSize)]) : null,
      t.custom ? el('span', { class: 'chip' }, ['custom']) : null,
      (t.dependsOn || []).length ? el('span', { class: 'small muted' }, ['depends on']) : null,
    ].concat((t.dependsOn || []).map(function (dep) {
      return plan.tasks[dep]
        ? el('button', { class: 'chip sql-chip-btn', type: 'button', on: { click: function () { select(dep); } } }, [dep])
        : el('span', { class: 'chip' }, [dep]);
    })));

    var body = [meta];
    if ((t.errors || []).length) body.push(list('sql-msgs-err', t.errors));
    if ((t.warnings || []).length) body.push(list('sql-msgs-warn', t.warnings));
    body.push(bindings(t));
    if (ui.editing === id && canEdit(ctx)) body.push(editor(ctx, id, t, version));
    else if (ui.diff && ui.diff.id === id) body.push(diffView(version));
    else body.push(sections(ctx, id, t));
    body.push(el('details', { class: 'sql-count' }, [
      el('summary', { class: 'small muted' }, ['Validation count query · runs on SOURCE']),
      codeBlock(t.countSql || ''),
    ]));
    return el('section', { class: 'card sql-detail' }, [head, el('div', { class: 'card-b stack' }, body)]);
  }

  function bindings(t) {
    var rows = (t.columns || []).map(function (c) {
      return el('tr', {}, [el('td', { class: 'mono' }, [c.source]), el('td', { class: 'muted' }, ['→']), el('td', { class: 'mono' }, [c.target])]);
    });
    return el('details', {}, [
      el('summary', { class: 'small muted' }, [(t.columns || []).length + ' column bindings (source alias → target column)']),
      el('table', { class: 'tbl tbl-compact' }, [el('tbody', {}, rows)]),
    ]);
  }

  function sections(ctx, id, t) {
    var blocks = sectionBlocks(t);
    if (!blocks.length) return el('p', { class: 'muted' }, ['This task has no SQL.']);
    return el('div', { class: 'stack' }, blocks.map(function (b) {
      var pre = el('pre', { class: 'code sql-code' }, b.lines.map(function (l) {
        var row = el('div', { class: 'line', data: { line: String(l.no) } }, [
          el('span', { class: 'ln' }, [String(l.no)]),
          el('span', { html: l.html || ' ' }),
        ]);
        ctx.commentable(row, 'sql:' + id + ':' + l.no, id + ' line ' + l.no);
        return row;
      }));
      var side = SECTION_SIDE[b.section];
      return el('div', { class: 'sql-sec' }, [
        el('div', { class: 'sql-sec-h' }, [
          el('span', {}, [SECTION_TITLE[b.section]]),
          el('span', { class: 'sql-side sql-side-' + (side === 'source' ? 'src' : 'tgt') }, ['runs on ' + side.toUpperCase()]),
        ]),
        pre,
      ]);
    }));
  }

  function diffPicker(ctx, id, t, version) {
    var others = versionsOf(ctx).filter(function (v) { return v.version !== version; });
    if (!others.length || !live(ctx) || ui.editing === id) return null;
    var options = [el('option', { value: '' }, ['Compare with…'])].concat(others.slice().reverse().map(function (v) {
      return el('option', { value: String(v.version), selected: !!(ui.diff && ui.diff.id === id && ui.diff.with === v.version) }, ['v' + v.version + (v.author ? ' · ' + v.author : '')]);
    }));
    return el('select', {
      class: 'select btn-sm', 'aria-label': 'Compare this task with another version',
      on: {
        change: function (e) {
          var v = parseInt(e.target.value, 10);
          if (isNaN(v)) { ui.diff = null; rerender(); return; }
          ctx.api.get('/api/artifact/sql/' + v).then(function (res) {
            var old = findByTarget(res && res.payload, t.target);
            ui.diff = { id: id, with: v, missing: !old, rows: diffRows(old, t) };
            rerender();
          }).catch(function (err) { ctx.toast(errorText(err, 'Could not load v' + v), 'err'); });
        },
      },
    }, options);
  }

  function diffView(version) {
    var d = ui.diff;
    var changed = d.rows.some(function (r) { return r.op !== 'eq'; });
    return el('div', { class: 'stack' }, [
      el('div', { class: 'row' }, [
        el('span', { class: 'small muted' }, [d.missing ? 'Task not present in v' + d.with + '; everything is new.' : 'Changes from v' + d.with + ' to v' + version]),
        el('span', { class: 'spacer' }),
        el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { ui.diff = null; rerender(); } } }, ['Close diff']),
      ]),
      changed
        ? el('pre', { class: 'diff' }, d.rows.map(function (r) { return el('div', { class: 'diff-' + r.op }, [r.text]); }))
        : el('p', { class: 'muted' }, ['No differences in this task.']),
    ]);
  }

  function editor(ctx, id, t, version) {
    var fields = [['preSql', 'Task pre-load · TARGET · separate statements with a line containing only GO', joinStatements(t.preSql)],
      ['sourceQuery', 'Source query · runs on SOURCE · aliases = target columns, keys __k0…, no ORDER BY/TOP', t.sourceQuery || '']];
    if (t.mode === 'staging_merge' || t.stagingDdl || t.mergeSql) {
      fields.push(['stagingDdl', 'Staging table · TARGET · CREATE TABLE #stg (…)', t.stagingDdl || '']);
      fields.push(['mergeSql', 'Merge into target · TARGET · end MERGE with ;', t.mergeSql || '']);
    }
    fields.push(['postSql', 'Task post-load · TARGET · separate statements with a line containing only GO', joinStatements(t.postSql)]);

    var drafts = ui.drafts && ui.drafts.id === id && ui.drafts.version === version ? ui.drafts.values : null;
    // Evidence fields (custom, errors, warnings) are engine-set and a patch touching them is rejected: editOps only ever emits
    // ops for the five SQL fields below.
    ui.drafts = { id: id, version: version, values: drafts || {} };
    var areas = {};
    var opened = {};
    ui.editor = { id: id, task: t, areas: areas, opened: opened };
    var problems = el('div', { class: 'sql-edit-problems', 'aria-live': 'assertive' }, []);
    var submit = el('button', { class: 'btn btn-primary', type: 'submit', disabled: ui.saving }, ['Save as new version']);

    function save() {
      if (ui.saving) return;
      var edits = touchedEdits();
      var hazards = [];
      fields.forEach(function (f) {
        if ((f[0] === 'preSql' || f[0] === 'postSql') && Object.prototype.hasOwnProperty.call(edits, f[0])) {
          goSplitHazards(edits[f[0]]).forEach(function (n) {
            hazards.push(f[1].split(' · ')[0] + ', line ' + n + ': this GO line is inside a comment or string, so saving would cut the '
              + 'statement there (a line holding only GO always separates statements). Remove it or change that line, then save.');
          });
        }
      });
      if (hazards.length) { show(hazards); return; }   // Ruling 205: named before posting; nothing is sent
      var ops = editOps(id, t, edits);   // only fields the user typed into; untouched fields are never re-derived
      if (!ops.length) { ctx.toast('Nothing changed', 'info'); return; }
      ui.saving = true;
      submit.disabled = true;
      clear(problems);
      ctx.api.post('/api/edit/sql', { phase: 'sql', baseVersion: version, ops: ops, responses: [], summary: 'Edited ' + id + ' (' + t.target + ') in the UI' })
        .then(function (res) {
          ui.saving = false;
          if (res && res.ok === false) { show((res.errors || []).concat(res.warnings || [])); return; }
          discard();
          ctx.toast('Saved as v' + (res && res.version), 'ok');
          ctx.refresh();
        })
        .catch(function (e) {
          ui.saving = false;
          show(e && e.details && e.details.length ? e.details : [errorText(e, 'Save failed')]);
        });
    }

    function show(messages) {
      submit.disabled = false;
      clear(problems);
      problems.appendChild(list('sql-msgs-err', messages.length ? messages : ['The server rejected the edit.']));
    }

    var controls = fields.map(function (f) {
      if (hasBareCr(t[f[0]])) {
        // A browser textarea turns a bare CR into a line break, so this field cannot round-trip: shown, never sent.
        return el('div', { class: 'stack sql-edit-field sql-edit-locked' }, [
          el('span', { class: 'small muted' }, [f[1]]),
          el('p', { class: 'small sql-count-warn' }, ['Read-only: this field contains a bare carriage return; edit it through the agent.']),
          codeBlock(Array.isArray(t[f[0]]) ? joinStatements(t[f[0]]) : t[f[0]]),
        ]);
      }
      if ((f[0] === 'preSql' || f[0] === 'postSql') && !listRoundTrips(t[f[0]])) {
        return el('div', { class: 'stack sql-edit-field sql-edit-locked' }, [
          el('span', { class: 'small muted' }, [f[1]]),
          el('p', { class: 'small sql-count-warn' }, ["Read-only: this statement list can't be edited as one text without changing it; edit it through the agent."]),
          codeBlock(joinStatements(t[f[0]])),
        ]);
      }
      var initial = drafts && Object.prototype.hasOwnProperty.call(drafts, f[0]) ? drafts[f[0]] : f[2];
      var lines = norm(initial).split('\n').length;
      var ta = el('textarea', {
        class: 'textarea sql-edit', spellcheck: 'false', rows: String(Math.min(24, Math.max(4, lines + 1))), 'aria-label': f[1],
        on: {
          input: function () { ui.drafts.values[f[0]] = ta.value; },
          keydown: function (e) { if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) { e.preventDefault(); save(); } },
        },
      }, []);
      ta.value = f[2];
      opened[f[0]] = ta.value;   // what the textarea opened with, as the textarea itself reads it back
      ta.value = initial;
      areas[f[0]] = ta;
      return el('label', { class: 'stack sql-edit-field' }, [el('span', { class: 'small muted' }, [f[1]]), ta]);
    });

    return el('form', { class: 'stack sql-editor', on: { submit: function (e) { e.preventDefault(); save(); } } }, controls.concat([
      problems,
      el('div', { class: 'row row-wrap' }, [
        submit,
        el('button', { class: 'btn btn-ghost', type: 'button', on: { click: function () { discard(); rerender(); } } }, ['Cancel']),
        el('span', { class: 'small muted' }, ['Ctrl+S saves · the server validates live before storing']),
      ]),
    ]));
  }

  /* ---------- view ---------- */

  function render(root, ctx) {
    mounted = { root: root, ctx: ctx };
    var plan = payloadOf(ctx);
    var version = versionOf(ctx);
    if (ui.forVersion !== version) { discard(); ui.diff = null; ui.report = null; ui.reportFailure = null; ui.forVersion = version; }
    if (!canEdit(ctx)) discard();
    clear(root);

    var page = el('div', { class: 'page stack sql-page' }, [
      el('div', { class: 'page-h row row-wrap' }, [el('h1', { class: 'h1' }, ['Migration SQL']), el('span', { class: 'spacer' }), toolbar(ctx, plan)]),
    ]);
    root.appendChild(page);
    if (DBM.components.reviewBar) page.appendChild(DBM.components.reviewBar(ctx));
    if (!plan) {
      page.appendChild(DBM.components.emptyState('No SQL plan yet', 'The plan is generated as soon as the mapping is approved.'));
      return;
    }

    page.appendChild(kpis(plan));
    page.appendChild(validationNote(ctx, plan));
    if (ui.report) page.appendChild(reportCard(plan));
    if ((plan.errors || []).length || (plan.warnings || []).length) {
      page.appendChild(el('section', { class: 'card' }, [
        el('div', { class: 'card-h' }, [el('h3', { class: 'h3' }, ['Plan notes'])]),
        el('div', { class: 'card-b' }, [
          (plan.errors || []).length ? list('sql-msgs-err', plan.errors) : null,
          (plan.warnings || []).length ? list('sql-msgs-warn', plan.warnings) : null,
        ]),
      ]));
    }
    page.appendChild(globalCard('Global pre-load', 'runs on TARGET before the first task', plan.preSql));
    var id = selectedId(plan);
    page.appendChild(el('div', { class: 'sql-layout' }, [
      taskList(plan, id, openCommentsByTask(ctx.feedback)),
      id ? taskDetail(ctx, plan, id, version) : DBM.components.emptyState('No tasks', 'Every target table is skipped in the mapping.'),
    ]));
    page.appendChild(globalCard('Global post-load', 'runs on TARGET after the last task', plan.postSql));
  }

  /* Decline a shell re-render onto a NEWER version while SQL edits are unsaved: the edits cannot be carried onto it (the server
     rejects a stale baseVersion, and replaying them could overwrite the newer SQL). Same-version re-renders are not held — the
     editor is rebuilt from the drafts. Our own save is not held either: its completion refreshes. */
  function holdRender(next) {
    if (!mounted || ui.saving || !isDirty() || !next) return false;
    var v = versionOf(next);
    if (v === ui.forVersion || v !== next.latestVersion) return false;   // same version, or the user navigated to an older one
    if (!ui.heldWarned && mounted.ctx && mounted.ctx.toast) {
      ui.heldWarned = true;
      mounted.ctx.toast('A newer SQL version exists. Your unsaved edits are kept on screen, but saving them will be rejected — Cancel to load the new version.', 'info');
    }
    return true;
  }

  /* Leaving resets ALL view state (selection, report, editor, diff, version): a later visit starts fresh. */
  function leave(nextCtx) {
    if (isDirty() && nextCtx && nextCtx.toast) nextCtx.toast('Your unsaved SQL edits were discarded: the SQL view was closed.', 'info');
    ui = freshUi();
    mounted = null;
  }

  DBM.sqlView = {
    listing: listing, listingText: listingText, parseAnchor: parseAnchor, sectionBlocks: sectionBlocks, splitStatements: splitStatements,
    joinStatements: joinStatements, cardLines: cardLines, listRoundTrips: listRoundTrips, editOps: editOps, reportSummary: reportSummary,
    goSplitHazards: goSplitHazards, validationState: validationState,
    openCommentsByTask: openCommentsByTask,
    diffRows: diffRows,
  };
  DBM.views = DBM.views || {};
  DBM.views.sql = { title: 'SQL', render: render, holdRender: holdRender, leave: leave };
})(globalThis.DBM = globalThis.DBM || {});
