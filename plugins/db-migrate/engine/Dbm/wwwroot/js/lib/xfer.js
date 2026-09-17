/* Transfer math + formatting shared by the Execute and Final report views. Pure functions; also loaded by node --test.
 *
 * Every helper here exists because a number on those screens can be absent, and an absence that cannot say why it is absent is the
 * defect this milestone keeps producing: a blank cell, a 0 that means "unknown", a tick that means "not checked". So the rule below is
 * that an absence returns a word - "unknown", "not compared", "not run" - and never an empty string, a zero or a silent skip.
 */
(function (DBM) {
  'use strict';
  const X = {};
  const OPTION_KEYS = ['chunkSize', 'parallelism', 'errorMode', 'truncateTarget', 'tableLock', 'validateChecksums', 'fireTriggers', 'keepControlTable'];

  /* A check the engine put in the list but could not carry out. PreflightCheck has no flag for it (it is ok:false with the severity
   * ruling 120 gives it), so it is read from the sentence the engine wrote - which is also the sentence the screen shows. A miss
   * degrades to "failed"/"warning" with that same sentence in full; it never degrades to a tick or to a blank. */
  const NOT_RUN_OPENING = /^\s*not\s+(checked|counted|run|compared)\b/i;
  const NOT_RUN_REASON = /\bcould not be (verified|established|checked|counted|determined)\b/i;

  const GLYPHS = { ok: '✓', notrun: '?', warn: '!', err: '✕' };
  const CHECK_LABELS = { ok: 'passed', notrun: 'not run', warn: 'warning', err: 'failed' };

  function pad2(n) { return n < 10 ? '0' + n : String(n); }

  function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }

  function isNum(n) { return n !== null && n !== undefined && n !== '' && isFinite(Number(n)); }

  X.num = function (n) {
    if (!isNum(n)) return '—';
    return Math.round(Number(n)).toLocaleString('en-US');
  };

  /** A count nobody established says so in a word: "unknown" is a fact, an em dash is a shrug and a 0 is a lie. */
  X.numOrUnknown = function (n) { return isNum(n) ? X.num(n) : 'unknown'; };

  /** The label under a total. While some table has never been counted the sum below it is a floor, not a total. */
  X.totalText = function (total, tasksWithoutSource) {
    const missing = Number(tasksWithoutSource) || 0;
    if (missing > 0 && !(Number(total) > 0)) return 'of an unknown number of rows';
    return (missing > 0 ? 'of at least ' : 'of ') + X.num(total);
  };

  X.ratio = function (done, total) {
    if (!total || total <= 0) return done > 0 ? 1 : 0;
    return Math.max(0, Math.min(1, done / total));
  };

  X.pct = function (done, total) { return Math.floor(X.ratio(done, total) * 100); };

  /** A share as text. A non-zero count that rounds to 0 % is "<1%", never "0%" - three rejected rows are not none. */
  X.pctText = function (part, whole) {
    if (!isNum(part) || !(Number(whole) > 0)) return '—';
    const n = Number(part);
    const p = X.pct(n, whole);
    return p === 0 && n > 0 ? '<1%' : p + '%';
  };

  /** Rows/second between the oldest sample inside the window (default 10 s) and the newest one. */
  X.rate = function (samples, windowMs) {
    const w = windowMs || 10000;
    if (!samples || samples.length < 2) return 0;
    const last = samples[samples.length - 1];
    let first = samples[samples.length - 2];
    for (let i = 0; i < samples.length - 1; i++) {
      if (last.t - samples[i].t <= w) { first = samples[i]; break; }
    }
    const dt = (last.t - first.t) / 1000;
    return dt > 0 ? Math.max(0, (last.v - first.v) / dt) : 0;
  };

  X.pushSample = function (samples, t, v, max) {
    samples.push({ t: t, v: v });
    const cap = max || 120;
    while (samples.length > cap) samples.shift();
    return samples;
  };

  X.eta = function (done, total, rate) {
    if (!rate || rate <= 0 || !total) return null;
    return Math.round(Math.max(0, total - done) / rate);
  };

  /**
   * The ETA with the reason it is missing, because a "—" beside a moving bar reads as a glitch.
   * An estimate priced from a total that is short of whole tables reads as "nearly done" while one of them is still unread, so while
   * any task has no source count there is no ETA at all - the same rule ProgressOverall follows on the server.
   */
  X.etaInfo = function (done, total, rate, tasksWithoutSource) {
    const missing = Number(tasksWithoutSource) || 0;
    if (missing > 0) {
      return { text: '—', note: (missing === 1 ? '1 table has' : missing + ' tables have') + ' not been counted yet, so the rows left are unknown' };
    }
    if (!total || total <= 0) return { text: '—', note: 'the rows to move are unknown' };
    if (!rate || rate <= 0) return { text: '—', note: 'waiting for the first rows' };
    return { text: X.fmtEta(X.eta(done, total, rate)), note: '' };
  };

  X.fmtEta = function (sec) {
    if (sec === null || sec === undefined || !isFinite(sec)) return '—';
    const s = Math.max(0, Math.round(sec));
    if (s < 60) return s + 's';
    if (s < 3600) return Math.floor(s / 60) + 'm ' + pad2(s % 60) + 's';
    return Math.floor(s / 3600) + 'h ' + pad2(Math.floor((s % 3600) / 60)) + 'm';
  };

  X.fmtRate = function (r) {
    const v = Math.max(0, Number(r) || 0);
    if (v < 1000) return Math.round(v) + ' rows/s';
    if (v < 1e6) return (v / 1000).toFixed(1) + 'k rows/s';
    return (v / 1e6).toFixed(2) + 'M rows/s';
  };

  X.fmtDuration = function (startIso, endIso, nowMs) {
    if (!startIso) return '—';
    const start = Date.parse(startIso);
    const end = endIso ? Date.parse(endIso) : (nowMs || Date.now());
    return X.fmtEta((end - start) / 1000);
  };

  X.aggregate = function (tasks) {
    const a = { done: 0, total: 0, errors: 0, tasksDone: 0, tasksTotal: 0, running: 0, failed: 0, paused: 0 };
    (tasks || []).forEach(function (t) {
      a.done += t.rowsDone || 0;
      a.total += t.rowsSource || 0;
      a.errors += t.rowsError || 0;
      a.tasksTotal += 1;
      if (t.status === 'done') a.tasksDone += 1;
      if (t.status === 'running') a.running += 1;
      if (t.status === 'failed') a.failed += 1;
      if (t.status === 'paused') a.paused += 1;
    });
    return a;
  };

  /** How many tasks have no source count - the number that turns aggregate().total from a total into a floor. */
  X.tasksWithoutSource = function (tasks) {
    let n = 0;
    (tasks || []).forEach(function (t) { if (t.rowsSource === null || t.rowsSource === undefined) n += 1; });
    return n;
  };

  /** Returns a new task array with counters from a transfer_progress snapshot; rows without news are reused as-is. */
  X.mergeProgress = function (tasks, snapshot) {
    if (!snapshot || !snapshot.tasks) return tasks;
    const byId = {};
    snapshot.tasks.forEach(function (t) { byId[t.taskId] = t; });
    return tasks.map(function (t) {
      const u = byId[t.taskId];
      if (!u) return t;
      return Object.assign({}, t, {
        rowsDone: u.rowsDone,
        rowsError: u.rowsError,
        rowsSource: u.rowsSource === null || u.rowsSource === undefined ? t.rowsSource : u.rowsSource,
        status: u.status || t.status,
        rowsPerSec: u.rowsPerSec,
      });
    });
  };

  /**
   * Which controls are armed. Two things beyond the run's status decide it:
   * <ul>
   *   <li><b>stopping</b> ("pausing"/"cancelling"/"failing"): the run row still reads "running" for that whole window - a keyless task
   *       holds it until its table is done - so Pause must not offer itself a second time, while Cancel stays armed because escalating
   *       from a pause to a cancel is exactly what an operator waiting on a slow table wants.</li>
   *   <li><b>canResume === false</b>: the service has already said why it would refuse, so no armed Resume is offered. Undefined means
   *       nobody said, and the status decides.</li>
   * </ul>
   */
  X.controls = function (view) {
    const run = view && view.run;
    const s = run ? run.status : null;
    const active = !!(view && view.active);
    const stopping = view && view.stopping ? String(view.stopping) : '';
    const resumable = s === 'paused' || s === 'failed';
    return {
      pause: active && s === 'running' && !stopping,
      resume: !active && resumable && !(view && view.canResume === false),
      cancel: !!run && (active ? (s === 'running' && stopping !== 'cancelling' && stopping !== 'failing') : resumable),
      start: !!(view && view.canStart),
    };
  };

  /** "pausing…" while the run has been asked to stop and has not reached it yet; null when nothing was asked for. */
  X.stoppingText = function (view) {
    const s = view && view.stopping;
    return s ? String(s) + '…' : null;
  };

  X.checkTone = function (c) { return c.ok ? 'ok' : (c.severity === 'error' ? 'err' : 'warn'); };

  /** True for a check the engine listed but could not carry out (ruling 120/124). */
  X.notRun = function (c) {
    if (!c || c.ok) return false;
    const detail = String(c.detail || '');
    return NOT_RUN_OPENING.test(detail) || NOT_RUN_REASON.test(detail);
  };

  /** 'ok' | 'notrun' | 'err' | 'warn' - a not-run check is drawn as neither a pass nor a failure. */
  X.checkState = function (c) { return X.notRun(c) ? 'notrun' : X.checkTone(c); };

  X.checkGlyph = function (state) { return GLYPHS[state] || '!'; };

  X.checkLabel = function (state) { return CHECK_LABELS[state] || String(state); };

  X.preflightCounts = function (result) {
    const r = { errors: 0, warnings: 0 };
    ((result && result.checks) || []).forEach(function (c) {
      if (c.ok) return;
      if (c.severity === 'error') r.errors += 1; else r.warnings += 1;
    });
    return r;
  };

  /** preflightCounts plus the two numbers the checklist headline needs: how many passed, and how many never ran. */
  X.preflightSummary = function (result) {
    const checks = (result && result.checks) || [];
    const s = { total: checks.length, ok: 0, errors: 0, warnings: 0, notRun: 0 };
    checks.forEach(function (c) {
      if (c.ok) { s.ok += 1; return; }
      if (c.severity === 'error') s.errors += 1; else s.warnings += 1;
      if (X.notRun(c)) s.notRun += 1;
    });
    return s;
  };

  /** The one line above the checklist. "Ready to execute" never stands alone over a check that did not run. */
  X.preflightText = function (result) {
    if (!result) return 'Pre-flight has not run yet';
    const s = X.preflightSummary(result);
    const parts = [result.passed ? 'Ready to execute' : plural(s.errors, 'blocking problem', 'blocking problems')];
    if (s.warnings) parts.push(plural(s.warnings, 'warning', 'warnings'));
    // "including": a check that did not run is already counted above under the severity ruling 120 gave it, so naming it separately
    // must not read as a second, additional problem.
    if (s.notRun) parts.push('including ' + plural(s.notRun, 'check that did not run', 'checks that did not run'));
    return parts.join(', ');
  };

  X.countText = function (t) { return X.num(t.rowsDone) + ' / ' + X.numOrUnknown(t.rowsSource); };

  /**
   * What a row-count comparison established, in words. countMatch null means one thing only - no validation was recorded - and it is
   * neither a mismatch nor a blank; false with countCompared false means validation ran and could make no comparison.
   */
  X.countMatchInfo = function (t) {
    const v = t || {};
    if (v.countMatch === true) return { text: 'match', tone: 'ok', note: '' };
    if (v.countMatch === false && v.countCompared) return { text: 'mismatch', tone: 'err', note: v.countNote || '' };
    return { text: 'not compared', tone: 'warn', note: v.countNote || 'No validation was recorded for this task.' };
  };

  /** Across the report's tasks: how many counts were compared, matched, differed, and how many nobody compared. */
  X.reportCounts = function (tasks) {
    const c = { total: 0, compared: 0, matched: 0, mismatched: 0, notCompared: 0 };
    (tasks || []).forEach(function (t) {
      c.total += 1;
      if (t.countMatch === true) { c.compared += 1; c.matched += 1; }
      else if (t.countMatch === false && t.countCompared) { c.compared += 1; c.mismatched += 1; }
      else c.notCompared += 1;
    });
    return c;
  };

  /** Across the report's tasks: checksum columns compared, matched, and the bound columns no checksum could cover. */
  X.reportChecksums = function (tasks) {
    const c = { total: 0, matched: 0, notCompared: 0 };
    (tasks || []).forEach(function (t) {
      (t.checksums || []).forEach(function (s) { c.total += 1; if (s.match) c.matched += 1; });
      c.notCompared += t.checksumColumnsNotCompared || 0;
    });
    return c;
  };

  /** "5/5 matched, 1 not compared" - the shortfall belongs in the headline, not only in the notes several screens below it. */
  X.checksumHeadline = function (c) {
    const s = c || { total: 0, matched: 0, notCompared: 0 };
    const head = s.total > 0 ? s.matched + '/' + s.total + ' matched' : 'not computed';
    if (!s.notCompared) return head;
    return head + ', ' + (s.total > 0 ? s.notCompared + ' not compared' : plural(s.notCompared, 'column not compared', 'columns not compared'));
  };

  /** The same rule for row counts: "1/2 matched, 2 not compared", and "none compared" rather than "0/0". */
  X.countHeadline = function (c) {
    const s = c || { compared: 0, matched: 0, notCompared: 0 };
    const head = s.compared > 0 ? s.matched + '/' + s.compared + ' matched' : 'none compared';
    return s.notCompared ? head + ', ' + s.notCompared + ' not compared' : head;
  };

  X.checksumText = function (checksums, skipped) {
    if (checksums && checksums.length) {
      const ok = checksums.filter(function (c) { return c.match; }).length;
      return ok + '/' + checksums.length + ' match';
    }
    return skipped ? 'skipped' : '—';
  };

  /** One task's checksum cell, carrying the bound columns nobody could compare. */
  X.checksumDetail = function (t) {
    const task = t || {};
    const base = X.checksumText(task.checksums, task.checksumsSkipped);
    const missed = task.checksumColumnsNotCompared || 0;
    return missed ? base + ', ' + plural(missed, 'column not compared', 'columns not compared') : base;
  };

  /**
   * GET /api/transfer/errors answers a plain array, or - when the saved run cannot be read (ruling 135) - {rows, note}: an empty list
   * with the sentence saying which record is at fault. Both shapes end up here, so the drawer shows the sentence instead of a panel
   * that looks like "no rejected rows".
   */
  X.errorsPayload = function (r) {
    if (Array.isArray(r)) return { rows: r, note: '' };
    if (r && Array.isArray(r.rows)) return { rows: r.rows, note: r.note || '' };
    return { rows: [], note: (r && r.note) || '' };
  };

  /**
   * A refusal in the service's own words. The codes (busy, paused_run, not_ready, confirm_mismatch, preflight_failed, bad_options,
   * no_connection, not_running, not_resumable, not_cancellable, target_changed, target_unknown) are not re-worded here, and a code this
   * screen has never heard of is still shown rather than swallowed.
   */
  X.refusalText = function (e) {
    if (!e) return 'The request failed for a reason the server did not give.';
    const message = e.message ? String(e.message) : '';
    const details = e.details && e.details.length ? ' — ' + e.details.join('; ') : '';
    if (message) return message + details;
    if (e.code) return 'The server refused this (' + e.code + ').' + details;
    return 'The request failed for a reason the server did not give.' + details;
  };

  X.keyText = function (keyJson) {
    if (!keyJson) return '(no key)';
    try {
      const o = JSON.parse(keyJson);
      return Object.keys(o).map(function (k) { return k + '=' + o[k]; }).join(', ');
    } catch (e) {
      return String(keyJson);
    }
  };

  X.prettyJson = function (text) {
    try { return JSON.stringify(JSON.parse(text), null, 2); } catch (e) { return String(text); }
  };

  X.optionsKey = function (o) {
    return OPTION_KEYS.map(function (k) { return k + '=' + (o ? o[k] : undefined); }).join(';');
  };

  DBM.xfer = X;
})(globalThis.DBM = globalThis.DBM || {});
