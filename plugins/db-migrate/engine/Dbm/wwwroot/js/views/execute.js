/* Execute screen (phases Ready + Transfer): pre-flight, options, typed confirmation, live progress, pause/resume/cancel.
 *
 * Two rules run through the whole file. The first: the screen shows whatever GET /api/transfer says on load and follows SSE from
 * there, so a run started from the CLI or by an agent in a terminal is on screen exactly as if this tab had pressed the button.
 * The second: every sentence the view carries - cannotStart, cannotResume, stopping, planNote, the run's notes, a task's statusNote -
 * is rendered next to the number or the button it is about. A disabled control with nothing beside it, and a blank cell where the
 * engine sent a reason, are the defects this screen exists to avoid.
 */
(function (DBM) {
  'use strict';
  DBM.views = DBM.views || {};
  const h = DBM.h;
  const X = DBM.xfer;

  /* Friendly names for the checks we know of today. The list is not a filter: any name the engine adds later is rendered by its own
   * name (ruling: check names are open-ended - target_probe and source_probe arrived after this screen was designed). */
  const CHECK_LABELS = {
    sql_plan: 'Approved SQL plan',
    plan_valid: 'Plan validation',
    source_connection: 'Source connection',
    target_connection: 'Target connection',
    source_probe: 'Source probe',
    target_probe: 'Target probe',
    target_checks: 'Target checks',
    schema_drift: 'Schemas unchanged since discovery',
    target_tables: 'Target tables exist',
    insert_permission: 'INSERT permission',
    identity_insert_permission: 'Identity insert permission',
    truncate_permission: 'Truncate permission',
    control_table: 'Checkpoint table',
    target_rows: 'Target row counts',
    estimated_rows: 'Estimated volume',
    keyless_tasks: 'Tasks without a key',
    chunk_keys: 'Chunk keys unique',
  };

  const LOG_LIMIT = 200;          // lines kept in memory
  const LOG_SHOWN = 80;           // lines in the DOM: a run of millions of rows must not grow it without bound
  const ERROR_ROWS = 200;         // rejected rows fetched into the drawer at a time
  const SPARK_POINTS = 60;

  let S = null;
  /* Survives a re-render by the app shell (a transfer_run_changed refresh rebuilds the view): the log tail, the rate history and the
   * options the operator typed are this screen's own state and are not on the server to be fetched back. */
  let KEEP = null;

  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); }
  function errText(e) { return X.refusalText(e); }
  function label(name) {
    return CHECK_LABELS[name] || String(name || '').replace(/_/g, ' ').replace(/^./, function (c) { return c.toUpperCase(); });
  }
  function payloadOf(evt) {
    if (!evt) return {};
    const p = evt.data !== undefined ? evt.data : evt.payload;
    if (typeof p === 'string') { try { return JSON.parse(p); } catch (e) { return {}; } }
    return p || {};
  }
  function setBar(el, ratio) {
    const v = Math.max(0, Math.min(1, ratio || 0));
    el.style.setProperty('--v', v.toFixed(4));
    el.setAttribute('aria-valuenow', String(Math.round(v * 100)));
  }
  function bar(ratio, extra) {
    const el = h('div', { class: 'bar' + (extra ? ' ' + extra : ''), role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100' });
    setBar(el, ratio);
    return el;
  }
  function kpi(label_, value, sub) {
    const v = h('div', { class: 'kpi-v num' }, value);
    const s = h('div', { class: 'kpi-s' }, sub || '');
    return { el: h('div', { class: 'kpi' }, h('div', { class: 'kpi-l' }, label_), v, s), v: v, s: s };
  }
  function ts(iso) { return iso ? (DBM.fmt && DBM.fmt.ts ? DBM.fmt.ts(iso) : String(iso)) : '—'; }

  function render(root, ctx, opts) {
    const keep = (opts && opts.rerender !== false && KEEP) || null;
    S = {
      root: root, ctx: ctx, data: null,
      options: keep ? keep.options : null,
      optionsTouched: keep ? keep.optionsTouched : false,
      preflight: keep ? keep.preflight : null,
      preflightKey: keep ? keep.preflightKey : null,
      samples: keep ? keep.samples : [],
      rateHistory: keep ? keep.rateHistory : [],
      log: keep ? keep.log : [],
      runId: keep ? keep.runId : null,
      rowEls: {}, live: null, logEl: null, logEmpty: true, logFrame: 0, busy: false, reloadTimer: 0, frame: 0, sparkAt: 0, lastOverall: null,
    };
    KEEP = S;
    clear(root);
    root.appendChild(h('div', { class: 'page exe-page' }, h('div', { class: 'empty' }, 'Loading transfer…')));
    reload();
  }

  function leave() {
    if (!S) return;
    clearTimeout(S.reloadTimer);
    if (S.frame) cancelAnimationFrame(S.frame);
    if (S.logFrame) cancelAnimationFrame(S.logFrame);
  }

  function reload() {
    const mine = S;
    if (S.ctx.export) { paint(); return Promise.resolve(); }
    return DBM.api.get('/api/transfer').then(function (v) {
      if (S !== mine) return;
      S.data = v;
      if (!S.options) S.options = Object.assign({}, v.defaults || {});
      // A pre-flight belongs to the options it was taken for; the server's own last result is shown until this tab runs one.
      if (!S.preflight && v.preflight) { S.preflight = v.preflight; S.preflightKey = null; }
      const id = v.run ? v.run.id : null;
      if (id !== S.runId) { S.runId = id; S.samples = []; S.rateHistory = []; S.lastOverall = null; }
      paint();
    }).catch(function (e) {
      if (S !== mine) return;
      S.ctx.toast('Could not load the transfer: ' + errText(e), 'err');
      if (!S.data) {
        clear(S.root);
        S.root.appendChild(h('div', { class: 'page exe-page' },
          DBM.components.notice('err', 'The transfer could not be loaded: ' + errText(e))));
      }
    });
  }

  function scheduleReload() {
    clearTimeout(S.reloadTimer);
    S.reloadTimer = setTimeout(reload, 150);
  }

  function paint() {
    const d = S.data;
    if (!d) return;
    const run = d.run;
    const stopping = X.stoppingText(d);
    const page = h('div', { class: 'page exe-page' },
      h('div', { class: 'page-h row' },
        h('div', { class: 'stack' },
          h('div', { class: 'h1' }, 'Execute transfer'),
          h('div', { class: 'muted small' },
            'Target ', h('span', { class: 'mono' }, d.targetDatabase || 'unknown'),
            // SqlVersion is null when no plan is approved - never 0, which is a real version here.
            d.sqlVersion === null || d.sqlVersion === undefined ? ' · no approved SQL plan' : ' · SQL plan v' + d.sqlVersion,
            ' · ' + d.tasks.length + (d.tasks.length === 1 ? ' task' : ' tasks'))),
        h('div', { class: 'spacer' }),
        DBM.components.badge(run ? run.status : 'pending', stopping ? stopping : null)));
    S.rowEls = {};
    S.live = null;
    S.logEl = null;

    if (d.planNote) page.appendChild(DBM.components.notice('warn', d.planNote));
    // Ruling 132, route 3: active with no readable run. This is never drawn as "no run" or as a clean idle screen - all three
    // sentences the view carries are shown, because nothing at all is known about what that run is doing to the target.
    if (!run && d.active) {
      page.appendChild(DBM.components.notice('err', h('div', { class: 'stack-sm' },
        h('strong', {}, 'A transfer is running, but its saved record cannot be read.'),
        d.cannotStart ? h('div', {}, d.cannotStart) : null,
        d.cannotResume ? h('div', {}, d.cannotResume) : null)));
    }
    if (run) page.appendChild(liveSection());
    if (!S.ctx.export && d.canStart) {
      if (run) page.appendChild(h('div', { class: 'h2 exe-section-title' }, 'Start a new run'));
      page.appendChild(h('div', { class: 'exe-grid' }, preflightCard(), optionsCard()));
    } else if (!S.ctx.export && d.cannotStart) {
      /* The reason goes where the button would have been, run or no run. With a run present this used to be skipped entirely, so a
         target_changed or target_unknown refusal drew as a clean completed screen: no Execute button, and nothing saying the engine
         was refusing rather than finished. A missing control that cannot say why it is missing is this project's defect exactly. */
      page.appendChild(h('section', { class: 'card' },
        h('div', { class: 'card-h' }, h('div', { class: 'h3' }, run ? 'A new run cannot be started' : 'Cannot start a transfer yet')),
        h('div', { class: 'card-b' }, h('div', { class: 'wrap-anywhere exe-cannot-start' }, d.cannotStart))));
    }
    if (!run) page.appendChild(tasksCard());
    clear(S.root);
    S.root.appendChild(page);
  }

  /* ---------- pre-flight + options ---------- */

  function preflightCard() {
    const pf = S.preflight;
    const list = h('ul', { class: 'exe-checks' });
    if (pf) pf.checks.forEach(function (c) { list.appendChild(checkItem(c)); });
    const btn = h('button', { class: 'btn', type: 'button', on: { click: runPreflight } }, pf ? 'Re-run pre-flight' : 'Run pre-flight');
    if (S.busy === 'preflight') { btn.disabled = true; btn.classList.add('is-loading'); btn.textContent = 'Checking…'; }
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Pre-flight checklist'), h('div', { class: 'spacer' }), btn),
      h('div', { class: 'card-b stack' }, preflightSummary(pf), list));
  }

  function preflightSummary(pf) {
    if (!pf) return h('div', { class: 'muted small' },
      'Checks connectivity, schema drift, the approved SQL version, permissions, existing target rows and the volume to move.');
    const s = X.preflightSummary(pf);
    const tone = pf.passed ? (s.warnings || s.notRun ? 'st-paused' : 'st-done') : 'st-failed';
    return h('div', { class: 'stack-sm' },
      h('div', { class: 'row' },
        h('span', { class: 'badge ' + tone }, X.preflightText(pf)), ' ',
        h('span', { class: 'muted small' }, 'checked ' + (DBM.fmt && DBM.fmt.rel ? DBM.fmt.rel(pf.at) : pf.at))),
      s.notRun ? h('div', { class: 'muted small' },
        (s.notRun === 1 ? 'One check' : s.notRun + ' checks') + ' could not be carried out. What they would have caught is unknown, '
        + 'not absent — each one says below what stopped it.') : null);
  }

  function checkItem(c) {
    const state = X.checkState(c);
    return h('li', { class: 'exe-check' },
      h('span', { class: 'exe-check-icon is-' + state, role: 'img', 'aria-label': X.checkLabel(state) }, X.checkGlyph(state)),
      h('div', {},
        h('div', { class: 'exe-check-name' }, label(c.name),
          // The space is a text node, not CSS: without it a screen reader reads "schema driftnot run".
          state === 'notrun' ? [' ', h('span', { class: 'tag exe-tag' }, 'not run')] : null),
        // The engine's own sentence, in full: it is the only thing that says what was not checked and why.
        h('div', { class: 'exe-check-detail' }, c.detail || '(the engine gave no detail for this check)')));
  }

  function runPreflight() {
    S.busy = 'preflight';
    paint();
    const key = X.optionsKey(S.options);
    DBM.api.post('/api/transfer/preflight', { options: S.options })
      .then(function (r) { S.preflight = r; S.preflightKey = key; })
      .catch(function (e) { S.ctx.toast('Pre-flight could not run: ' + errText(e), 'err'); })
      .then(function () { S.busy = false; paint(); });
  }

  function optionsCard() {
    const o = S.options;
    const set = function (k, v) { o[k] = v; S.optionsTouched = true; updateExecuteState(); };
    const numberField = function (id, label_, key, min, max, step, hint) {
      const input = h('input', { class: 'input', type: 'number', id: id, min: String(min), max: String(max), step: String(step), value: String(o[key]),
        on: { change: function (e) { const v = Math.min(max, Math.max(min, parseInt(e.target.value, 10) || min)); e.target.value = String(v); set(key, v); } } });
      return h('div', { class: 'exe-field' }, h('label', { for: id }, label_), input, h('div', { class: 'muted small' }, hint));
    };
    const radio = function (value, label_, hint) {
      const input = h('input', { type: 'radio', name: 'exe-errmode', value: value, on: { change: function () { set('errorMode', value); } } });
      input.checked = o.errorMode === value;
      return h('label', { class: 'exe-choice' }, input,
        h('span', { class: 'stack' }, h('span', {}, label_), ' ', h('span', { class: 'muted small' }, hint)));
    };
    const check = function (key, label_, hint, danger) {
      const input = h('input', { type: 'checkbox', class: 'check', on: { change: function (e) { set(key, e.target.checked); } } });
      input.checked = !!o[key];
      return h('label', { class: 'exe-choice' }, input,
        h('span', { class: 'stack' }, h('span', {}, label_), ' ', h('span', { class: 'small ' + (danger ? 'exe-danger-note' : 'muted') }, hint)));
    };
    S.execBtn = h('button', { class: 'btn btn-primary', type: 'button', on: { click: confirmAndStart } }, 'Execute…');
    S.execHint = h('div', { class: 'muted small wrap-anywhere' });
    const card = h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Options')),
      h('div', { class: 'card-b exe-form' },
        h('div', { class: 'grid-2' },
          numberField('exe-chunk', 'Chunk size (rows)', 'chunkSize', 1, 10000000, 1000, 'Rows per target transaction and checkpoint.'),
          numberField('exe-par', 'Parallel tasks', 'parallelism', 1, 32, 1, 'Independent tables load at the same time.')),
        h('fieldset', { class: 'exe-fieldset' }, h('legend', { class: 'exe-legend' }, 'When a row is rejected'),
          radio('stop', 'Stop at the first bad row', 'The chunk is rolled back; fix the data and resume.'),
          radio('skip', 'Skip and log bad rows', 'Bad rows are isolated by bisection, logged and counted; the rest loads.')),
        h('fieldset', { class: 'exe-fieldset' }, h('legend', { class: 'exe-legend' }, 'Advanced'),
          check('truncateTarget', 'Truncate target first', 'Deletes every existing row in the ' + S.data.tasks.length + ' target tables before loading.', true),
          check('validateChecksums', 'Validate column checksums', 'Compares per-column checksums when a target table started empty.'),
          check('tableLock', 'Table lock', 'Faster bulk loads; blocks other readers of the target tables.'),
          check('fireTriggers', 'Fire target triggers', 'Off by default: the plan’s pre/post scripts handle trigger logic.'),
          check('keepControlTable', 'Keep the checkpoint table', 'Leaves dbo.__dbm_checkpoint in the target after completion.')),
        h('div', { class: 'toolbar' }, S.execBtn, S.execHint)));
    updateExecuteState();
    return card;
  }

  function updateExecuteState() {
    if (!S.execBtn) return;
    const d = S.data;
    const pf = S.preflight;
    let hint = '';
    let enabled = !!(d && d.canStart) && !S.busy;
    // The service's own reason comes first: it is the one that knows about a target whose saved details will not parse.
    if (d && !d.canStart) { enabled = false; hint = d.cannotStart || 'The server will not start a transfer right now.'; }
    else if (!d.targetDatabase) {
      // requireText compares against the target database name; with no name there is nothing to confirm against.
      enabled = false;
      hint = d.cannotStart || 'The target database name is unknown, so the typed confirmation cannot be checked. Re-test the target connection on Setup.';
    } else if (!pf) { enabled = false; hint = 'Run pre-flight first.'; }
    else if (!pf.passed) { enabled = false; hint = 'Resolve the blocking pre-flight problems first.'; }
    else if (S.preflightKey && S.preflightKey !== X.optionsKey(S.options)) hint = 'Options changed since pre-flight; it re-runs automatically when you execute.';
    else {
      const s = X.preflightSummary(pf);
      if (s.notRun) hint = (s.notRun === 1 ? '1 check' : s.notRun + ' checks') + ' could not be carried out; read them before you start.';
    }
    S.execBtn.disabled = !enabled;
    S.execBtn.classList.toggle('is-disabled', !enabled);
    S.execHint.textContent = hint;
  }

  /** Ruling 186: the non-empty target tables the last pre-flight counted, when this start would load into them without truncating. */
  function nonEmptyTargets() {
    const pf = S.preflight;
    if (!pf || S.options.truncateTarget) return [];
    return pf.nonEmptyTargets || [];
  }

  function confirmAndStart() {
    const d = S.data;
    const o = S.options;
    const nonEmpty = nonEmptyTargets();
    const keyless = nonEmpty.filter(function (t) { return t.keyless; });
    const body = h('div', { class: 'stack' },
      h('p', {}, 'Load ', h('strong', {}, d.tasks.length + ' tables'), ' into ', h('span', { class: 'mono' }, d.targetDatabase), '.'),
      h('ul', { class: 'small' },
        h('li', {}, 'Chunk size ' + X.num(o.chunkSize) + ', ' + o.parallelism + ' parallel tasks'),
        h('li', {}, o.errorMode === 'skip' ? 'Bad rows are skipped and logged' : 'Stops at the first bad row'),
        o.truncateTarget ? h('li', { class: 'exe-danger-note' }, 'All existing rows in the target tables are deleted first') : null),
      nonEmpty.length ? h('div', { class: 'stack-sm exe-nonempty' },
        h('p', { class: 'exe-danger-note' }, 'These target tables already hold rows, and this new run loads every table from the start'
          + (keyless.length ? ' - the ones without a key get their rows a second time:' : ':')),
        h('ul', { class: 'small' }, ...nonEmpty.map(function (t) {
          return h('li', { class: t.keyless ? 'exe-danger-note' : '' }, h('span', { class: 'mono' }, t.target), ' ' + X.num(t.rows) + ' rows - ',
            t.keyless ? 'no primary key or unique index: rows will be loaded again (duplicates)' : 'keyed: rows whose keys are already there are rejected');
        })),
        h('p', { class: 'small muted' }, 'To start clean instead, cancel and tick Truncate target first.')) : null,
      h('p', { class: 'small muted' }, 'Type the target database name to confirm.'));
    DBM.components.modal({
      title: 'Start the transfer?', body: body, confirmText: 'Start transfer', requireText: d.targetDatabase, danger: nonEmpty.length > 0,
      requireCheck: nonEmpty.length ? 'Load into the ' + nonEmpty.length + (nonEmpty.length === 1 ? ' table' : ' tables') + ' that already hold rows'
        + (keyless.length ? ', including ' + keyless.map(function (t) { return t.target; }).join(', ') + ' (duplicates)' : '') : null,
    })
      .then(function (ok) {
        if (!ok) return null;
        S.busy = 'start';
        updateExecuteState();
        // confirmTarget must equal the target database name exactly, case included: the name is sent as the service reported it.
        const req = { options: o, confirmTarget: d.targetDatabase };
        if (nonEmpty.length) req.confirmNonEmpty = nonEmpty.map(function (t) { return t.target; });
        return DBM.api.post('/api/transfer/start', req)
          .then(function (r) {
            S.samples = [];
            S.rateHistory = [];
            S.log = [];
            S.ctx.toast('Transfer run #' + r.runId + ' started', 'ok');
          })
          .catch(function (e) {
            S.ctx.toast(errText(e), 'err');
            // Ruling 186: the server counted rows this screen had not seen (a restart re-runs pre-flight). Re-run it here, so the next
            // Execute shows the tables and asks for the confirmation by name.
            if (e && e.code === 'target_not_empty') S.preflight = null;
          })
          .then(function () { S.busy = false; if (!S.preflight) { runPreflight(); return null; } return reload(); });
      });
  }

  /* ---------- live run ---------- */

  function liveSection() {
    const d = S.data;
    const run = d.run;
    const agg = X.aggregate(d.tasks);
    const missing = d.totals && d.totals.tasksWithoutSource !== undefined ? d.totals.tasksWithoutSource : X.tasksWithoutSource(d.tasks);
    const rate = X.rate(S.samples);
    const wrap = h('div', { class: 'stack' });
    const banner = runBanner(run, d);
    if (banner) wrap.appendChild(banner);
    const target = runTargetLine(run);
    if (target) wrap.appendChild(target);
    if (run.notes && run.notes.length) wrap.appendChild(notesCard(run.notes));

    const eta = X.etaInfo(agg.done + agg.errors, agg.total, rate, missing);
    const kDone = kpi('Rows loaded', X.num(agg.done), doneSub(agg, missing));
    const kRate = kpi('Throughput', d.active ? X.fmtRate(rate) : '—', d.active ? 'last 10 seconds' : 'the run is not moving');
    /* The sub-label is rewritten on every update, never only when there is something to say: an explanation that outlives the
       absence it explained is worse than none. A tab opened on a live transfer used to read "TIME LEFT 32s / waiting for the first
       rows" while a quarter of a million rows a second were moving, and an operator then has to decide which half to believe. */
    const startedAt = 'started ' + ts(run.startedAt);
    const kEta = kpi('Time left', d.active ? eta.text : '—', d.active ? (eta.note || startedAt) : startedAt);
    const kErr = kpi('Rejected rows', X.num(agg.errors),
      run.options && run.options.errorMode === 'skip' ? 'skipped and logged' : 'stop on first error');
    const overall = bar(X.ratio(agg.done + agg.errors, agg.total), 'exe-bar-lg');
    const spark = h('div', { class: 'exe-spark', 'aria-label': 'Throughput over time' });
    if (S.rateHistory.length > 1) spark.appendChild(DBM.components.sparkline(S.rateHistory.map(function (s) { return s.v; })));
    S.live = { done: kDone, rate: kRate, eta: kEta, err: kErr, bar: overall, spark: spark, missing: missing, startedAt: startedAt };

    wrap.appendChild(h('section', { class: 'card' },
      h('div', { class: 'card-b stack' },
        h('div', { class: 'grid-kpi' }, kDone.el, kRate.el, kEta.el, kErr.el),
        h('div', { class: 'exe-live-head' }, h('div', { class: 'exe-overall' }, overall), spark),
        controlsBar())));
    wrap.appendChild(tasksCard());
    wrap.appendChild(logCard());
    return wrap;
  }

  function doneSub(agg, missing) {
    const total = X.totalText(agg.total, missing);
    return agg.total > 0 ? total + ' · ' + X.pct(agg.done + agg.errors, agg.total) + '%' : total;
  }

  /** Item 47: the target this run actually loaded into (never credentials), recorded once at start - not the same thing as the
   *  server's current Setup target, which can move after the run started. A run saved before this field existed carries neither
   *  property, and the compact line is silent rather than reading "Loading into undefined". */
  function runTargetLine(run) {
    const db = run.targetDatabase, srv = run.targetServer;
    if (!db && !srv) return null;
    const text = db && srv ? 'Loading into ' + db + ' on ' + srv : db ? 'Loading into ' + db : 'Loading into a database on ' + srv;
    return h('div', { class: 'muted small exe-run-target' }, text);
  }

  function runBanner(run, d) {
    const stopping = X.stoppingText(d);
    if (stopping) {
      // A keyless task ignores a pause until its table is done, so the run row still reads "running" for this whole window.
      return h('div', { class: 'exe-banner is-warn', role: 'status' }, h('strong', {}, stopping.charAt(0).toUpperCase() + stopping.slice(1) + ' '),
        'The run stops when the chunks in flight commit. A task without a key finishes its table first, so this can take a while.');
    }
    if (run.status === 'completed') {
      const notes = run.notes && run.notes.length;
      // Open item 32: the step is named as the stepper labels it, so the two can never disagree again.
      const report = DBM.phaseTitle ? DBM.phaseTitle('complete') : 'Report';
      return h('div', { class: 'exe-banner ' + (notes ? 'is-warn' : 'is-ok'), role: 'status' },
        h('strong', {}, notes ? 'Transfer completed, with notes. ' : 'Transfer completed. '),
        notes ? 'Read the notes below before you treat this run as clean.'
          : 'Validation finished — open ' + report + ' in the stepper for the final report.',
        // Ruling 185: the way to another run is through the plan.
        h('div', { class: 'small muted' }, 'To run again with a changed plan, reopen Analysis, Mapping or SQL; once SQL is approved '
          + 'again, Execute starts a new run and this run’s report stays on the ' + report + ' screen.'));
    }
    if (run.status === 'failed') {
      const failed = d.tasks.filter(function (t) { return t.status === 'failed'; });
      const msg = run.error || (failed.length ? failed[0].taskId + ' ' + failed[0].target + ': ' + failed[0].error : 'See the log.');
      return h('div', { class: 'exe-banner is-err', role: 'alert' }, h('strong', {}, 'Transfer failed. '), msg,
        h('div', { class: 'small muted' }, (d.canResume
          ? 'Resume continues from the last committed checkpoint and loads nothing twice. Cancel abandons the run. '
          : 'Cancel abandons the run. ')
          + 'A new run below loads every table from the start: it needs Truncate target first, or a confirmation naming the tables '
          + 'that already hold rows.'),
        inForce(run));
    }
    if (run.status === 'paused') {
      return h('div', { class: 'exe-banner is-warn', role: 'status' }, h('strong', {}, 'Paused. '),
        'Committed chunks are safe; Resume continues exactly where each task stopped.', inForce(run));
    }
    if (run.status === 'cancelled') {
      return h('div', { class: 'exe-banner', role: 'status' }, h('strong', {}, 'Cancelled. '),
        'Rows already committed remain in the target, and a cancelled run cannot be resumed. A new run loads every table from the '
        + 'start: choose Truncate target first, or confirm by name the tables that already hold rows - a table with no key would get '
        + 'its rows twice. To change the plan first, reopen Analysis, Mapping or SQL.');
    }
    return null;
  }

  /** Ruling 184: the plan's pre-load statements a paused or failed run keeps in force in the target, named - Resume expects them,
   *  and only Cancel (which runs the post-load SQL) or a completed run undoes them. */
  function inForce(run) {
    const list = run.preSqlInForce || [];
    if (!list.length) return null;
    return h('div', { class: 'stack-sm exe-inforce' },
      h('div', { class: 'small' }, 'Still in force in the target (the plan’s pre-load SQL; Resume expects it, Cancel restores it):'),
      h('ul', { class: 'small mono' }, ...list.map(function (s) { return h('li', { class: 'wrap-anywhere' }, s); })));
  }

  /** The run's own notes, word for word and in order: a control table that was not ours, rows counted but never recorded, a
   *  validator that could not run. A completed run with notes is not a clean one, and this is the only place that shows it. */
  function notesCard(notes) {
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Notes from this run')),
      h('div', { class: 'card-b' }, h('ul', { class: 'rep-notes' }, ...notes.map(function (n) { return h('li', {}, n); }))));
  }

  function controlsBar() {
    const d = S.data;
    const c = X.controls(d);
    const stopping = X.stoppingText(d);
    const tb = h('div', { class: 'toolbar' });
    if (S.ctx.export) return tb;
    const status = d.run ? d.run.status : null;
    if (c.pause) tb.appendChild(h('button', { class: 'btn', type: 'button', on: { click: function () { act('pause'); } } }, 'Pause'));
    else if (d.active && status === 'running') {
      tb.appendChild(h('button', { class: 'btn is-disabled', type: 'button', disabled: true, title: stopping || '' }, stopping || 'Pause'));
    }
    if (c.resume) tb.appendChild(h('button', { class: 'btn btn-primary', type: 'button', on: { click: function () { act('resume'); } } }, 'Resume'));
    else if (!d.active && (status === 'paused' || status === 'failed')) {
      // A Resume the service would refuse is still shown - disabled, with the service's own reason beside it.
      tb.appendChild(h('button', { class: 'btn is-disabled', type: 'button', disabled: true }, 'Resume'));
    }
    if (c.cancel) tb.appendChild(h('button', { class: 'btn btn-danger', type: 'button', on: { click: confirmCancel } }, 'Cancel run'));
    if (!c.resume && !d.active && (status === 'paused' || status === 'failed') && d.cannotResume) {
      tb.appendChild(h('span', { class: 'muted small wrap-anywhere' }, d.cannotResume));
    } else if (stopping) {
      tb.appendChild(h('span', { class: 'muted small' }, 'Already ' + stopping + ' Cancel abandons the rest of the run.'));
    } else if (d.active) {
      tb.appendChild(h('span', { class: 'muted small' }, 'Pause and cancel take effect after the current chunks commit.'));
    }
    return tb;
  }

  function act(what) {
    DBM.api.post('/api/transfer/' + what, {})
      .then(function () {
        S.ctx.toast(what === 'pause' ? 'Pausing after the current chunks…' : what === 'resume' ? 'Resuming from the last checkpoints' : 'Cancelling…', 'info');
        scheduleReload();
      })
      .catch(function (e) { S.ctx.toast(errText(e), 'err'); });
  }

  function confirmCancel() {
    DBM.components.modal({
      title: 'Cancel this run?',
      body: h('p', {}, 'Running tasks stop after their current chunk. Rows already committed stay in the target and the run cannot be resumed. '
        + 'The plan’s post-load SQL then runs to re-enable what its pre-load SQL disabled; the run’s notes say what was restored.'),
      confirmText: 'Cancel run',
      danger: true,
    }).then(function (ok) { if (ok) act('cancel'); });
  }

  function tasksCard() {
    const d = S.data;
    const tbody = h('tbody', {});
    d.tasks.forEach(function (t, i) { tbody.appendChild(taskRow(t, i)); });
    const agg = X.aggregate(d.tasks);
    const heads = ['#', 'Task', 'Target', 'Status', 'Progress', 'Rows/s', 'Rejected', 'Time'];
    const body = h('div', { class: 'card-b exe-table-wrap' });
    if (!d.tasks.length) {
      // An empty task list with nothing beside it reads as a plan with nothing in it.
      body.appendChild(h('div', { class: 'muted wrap-anywhere' },
        d.planNote || 'No tasks: there is no approved SQL plan to execute yet.'));
    } else {
      body.appendChild(h('table', { class: 'tbl tbl-compact exe-tasks' },
        h('thead', {}, h('tr', {}, ...heads.map(function (c) { return h('th', { scope: 'col' }, c); }))),
        tbody));
    }
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Tasks in execution order'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, agg.tasksDone + ' of ' + agg.tasksTotal + ' done')),
      body);
  }

  function taskRow(t, i) {
    const prog = bar(X.ratio((t.rowsDone || 0) + (t.rowsError || 0), t.rowsSource));
    const count = h('span', { class: 'num small' }, X.countText(t));
    const rate = h('td', { class: 'num' }, t.status === 'running' ? X.fmtRate(t.rowsPerSec || 0) : '—');
    const errCell = h('td', { class: 'num' }, errorsControl(t));
    S.rowEls[t.taskId] = { prog: prog, count: count, rate: rate, errCell: errCell, errors: t.rowsError || 0 };
    return h('tr', {},
      h('td', { class: 'num muted' }, String(i + 1)),
      h('td', { class: 'mono' }, t.taskId),
      h('td', { class: 'mono ellipsis', title: t.target }, t.target),
      h('td', {},
        DBM.components.badge(t.status),
        // The space is a text node, not the CSS margin: without it this reads aloud as "Runningno key", and a keyless task carries
        // the most operationally significant property on the screen - it cannot checkpoint and it holds a pause.
        t.keyless ? [' ', h('span', { class: 'tag exe-tag', title: 'Loads in one transaction' }, 'no key')] : null,
        // A task reading "paused" under a cancelled or failed run was not paused by an operator: the note says which it is.
        t.statusNote ? h('div', { class: 'exe-task-note' }, t.statusNote) : null,
        t.validationNote ? h('div', { class: 'exe-task-note' }, t.validationNote) : null),
      h('td', {}, h('div', { class: 'exe-task-progress' }, prog, count)),
      rate,
      errCell,
      h('td', { class: 'small' }, t.error
        ? h('span', { class: 'sev-high exe-task-error', title: t.error }, t.error)
        : X.fmtDuration(t.startedAt, t.endedAt)));
  }

  function errorsControl(t) {
    if (!t.rowsError) return h('span', { class: 'muted' }, '0');
    return h('button', { class: 'btn btn-ghost btn-sm exe-err-btn', type: 'button', title: 'Show rejected rows',
      on: { click: function () { openErrors(t); } } }, X.num(t.rowsError));
  }

  function logCard() {
    S.logEl = h('pre', { class: 'code exe-log', 'aria-live': 'polite' });
    paintLog();
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Log'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, 'last ' + LOG_SHOWN + ' lines')),
      h('div', { class: 'card-b' }, S.logEl));
  }

  function logLine(l) {
    const time = l.t.toTimeString().slice(0, 8);
    return h('div', { class: 'exe-log-line lvl-' + l.level }, time + '  ' + (l.level + '     ').slice(0, 5) + '  ' + l.message);
  }

  /**
   * One line in, one line out. It used to rebuild the whole tail per message - clear the &lt;pre&gt;, build up to 80 divs again and
   * force a layout with scrollTop = scrollHeight - which cost 7.9 seconds of main thread over 5,000 log events, reachable with a
   * small chunk size over millions of rows or with errorMode "skip" logging every rejected row. The bound itself was never the
   * problem; the repaint was. The scroll is batched behind one frame for the same reason transfer_progress is.
   */
  function pushLog(level, message) {
    const line = { t: new Date(), level: level, message: message };
    S.log.push(line);
    if (S.log.length > LOG_LIMIT) S.log.shift();
    if (!S.logEl) return;
    if (S.logEmpty) { clear(S.logEl); S.logEmpty = false; }
    S.logEl.appendChild(logLine(line));
    while (S.logEl.children.length > LOG_SHOWN) S.logEl.removeChild(S.logEl.firstChild);
    scrollLogSoon();
  }

  function scrollLogSoon() {
    if (S.logFrame) return;
    const el = S.logEl;
    S.logFrame = requestAnimationFrame(function () {
      S.logFrame = 0;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  function paintLog() {
    clear(S.logEl);
    S.logEmpty = !S.log.length;
    if (S.logEmpty) { S.logEl.appendChild(h('span', { class: 'muted' }, 'Live messages appear here while the transfer runs.')); return; }
    S.log.slice(-LOG_SHOWN).forEach(function (l) { S.logEl.appendChild(logLine(l)); });
    scrollLogSoon();
  }

  /* ---------- rejected rows drawer ---------- */

  function openErrors(t) {
    const list = h('div', { class: 'stack' }, h('div', { class: 'muted' }, 'Loading…'));
    DBM.components.drawer.open('Rejected rows · ' + t.taskId + ' ' + t.target, list);
    DBM.api.get('/api/transfer/errors?task=' + encodeURIComponent(t.taskId) + '&limit=' + ERROR_ROWS)
      .then(function (answer) {
        const p = X.errorsPayload(answer);
        clear(list);
        // Ruling 135: when the saved run cannot be read the endpoint answers an empty list with a sentence. Showing the panel empty
        // would contradict the screen it opened over.
        if (p.note) list.appendChild(DBM.components.notice('warn', p.note));
        if (!p.rows.length) {
          if (!p.note) {
            list.appendChild(h('div', { class: 'muted' }, t.rowsError
              ? X.num(t.rowsError) + ' rows were counted as rejected for this task, but none were recorded — see the run notes.'
              : 'No rejected rows recorded for this task.'));
          }
          return;
        }
        if (p.rows.length >= ERROR_ROWS && t.rowsError > p.rows.length) {
          list.appendChild(h('div', { class: 'muted small' }, 'Showing the first ' + p.rows.length + ' of ' + X.num(t.rowsError) + '.'));
        }
        p.rows.forEach(function (r) {
          list.appendChild(h('div', { class: 'exe-err-item stack' },
            h('div', { class: 'row' }, h('span', { class: 'mono small' }, X.keyText(r.keyJson)), h('div', { class: 'spacer' }),
              h('span', { class: 'muted small' }, ts(r.ts))),
            h('div', { class: 'small' }, r.error),
            r.rowJson ? h('pre', { class: 'code exe-row-json' }, X.prettyJson(r.rowJson)) : null));
        });
      })
      .catch(function (e) { clear(list); list.appendChild(h('div', { class: 'sev-high wrap-anywhere' }, errText(e))); });
  }

  /* ---------- live updates ---------- */

  function onProgress(p) {
    if (!S.data || !S.data.run || p.runId !== S.data.run.id) return;
    S.data.tasks = X.mergeProgress(S.data.tasks, p);
    const now = Date.now();
    if (p.overall) {
      X.pushSample(S.samples, now, p.overall.done);
      X.pushSample(S.rateHistory, now, p.overall.rowsPerSec || 0, SPARK_POINTS);
      S.lastOverall = p.overall;
    }
    // One frame per burst: transfer_progress arrives every 250 ms per run and this screen must not re-layout per event.
    if (!S.frame) S.frame = requestAnimationFrame(function () { S.frame = 0; updateLive(); });
  }

  function updateLive() {
    const d = S.data;
    if (!d || !S.live) return;
    const agg = X.aggregate(d.tasks);
    const o = S.lastOverall || {};
    const total = o.total !== undefined ? o.total : agg.total;
    const errors = o.rowsError !== undefined ? o.rowsError : agg.errors;
    const done = o.done !== undefined ? o.done : agg.done;
    const rate = o.rowsPerSec !== undefined ? o.rowsPerSec : X.rate(S.samples);
    const missing = o.tasksWithoutSource !== undefined ? o.tasksWithoutSource : X.tasksWithoutSource(d.tasks);
    // etaSec is absent from the snapshot exactly when the server withheld it (a table nobody has counted). Computing one here from
    // the same short total would put back the "nearly done" the server refused to say.
    const eta = X.etaInfo(done + errors, total, rate, missing);
    if (eta.text !== '—' && o.etaSec !== undefined && o.etaSec !== null) eta.text = X.fmtEta(o.etaSec);
    S.live.done.v.textContent = X.num(done);
    S.live.done.s.textContent = doneSub({ done: done, errors: errors, total: total }, missing);
    S.live.rate.v.textContent = X.fmtRate(rate);
    S.live.eta.v.textContent = eta.text;
    S.live.eta.s.textContent = eta.note || S.live.startedAt;
    S.live.err.v.textContent = X.num(errors);
    setBar(S.live.bar, X.ratio(done + errors, total));
    if (Date.now() - S.sparkAt > 1000 && S.rateHistory.length > 1) {
      S.sparkAt = Date.now();
      clear(S.live.spark);
      S.live.spark.appendChild(DBM.components.sparkline(S.rateHistory.map(function (s) { return s.v; })));
    }
    d.tasks.forEach(function (t) {
      const r = S.rowEls[t.taskId];
      if (!r) return;
      setBar(r.prog, X.ratio((t.rowsDone || 0) + (t.rowsError || 0), t.rowsSource));
      r.count.textContent = X.countText(t);
      r.rate.textContent = t.status === 'running' ? X.fmtRate(t.rowsPerSec || 0) : '—';
      if ((t.rowsError || 0) !== r.errors) {
        r.errors = t.rowsError || 0;
        clear(r.errCell);
        r.errCell.appendChild(errorsControl(t));
      }
    });
  }

  function onEvent(evt) {
    if (!S || !S.data) return;
    const type = evt && (evt.type || evt.event);
    const p = payloadOf(evt);
    // transfer_progress carries the snapshot to merge in place; only a task or run change re-reads the view (which deserialises the
    // approved plan artifact), and that is debounced.
    if (type === 'transfer_progress') onProgress(p);
    else if (type === 'transfer_task_changed' || type === 'transfer_run_changed') scheduleReload();
    else if (type === 'log') pushLog(p.level || 'info', p.message || '');
  }

  const view = { title: 'Execute', render: render, onEvent: onEvent, leave: leave };
  DBM.views.ready = view;
  DBM.views.transfer = view;
  DBM.views.execute = view;
})(window.DBM = window.DBM || {});
