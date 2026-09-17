const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'xfer.js'), 'utf8'));
const X = globalThis.DBM.xfer;

test('num and pct format tabular figures', () => {
  assert.equal(X.num(1234567), '1,234,567');
  assert.equal(X.num(null), '—');
  assert.equal(X.pct(1, 3), 33);
  assert.equal(X.pct(2999, 3000), 99);          // never shows 100 before it is finished
  assert.equal(X.pct(3, 3), 100);
  assert.equal(X.pct(0, 0), 0);
  assert.equal(X.ratio(5, 0), 1);
  assert.equal(X.ratio(15, 10), 1);
});

test('rate uses the samples inside the window', () => {
  assert.equal(X.rate([]), 0);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 2000, v: 1000 }]), 500);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 20000, v: 100 }, { t: 22000, v: 1100 }]), 500);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 30000, v: 900 }]), 30);     // falls back to the previous sample
  assert.equal(X.rate([{ t: 0, v: 100 }, { t: 1000, v: 50 }]), 0);      // never negative
});

test('pushSample caps the history', () => {
  const s = [];
  for (let i = 0; i < 10; i++) X.pushSample(s, i * 1000, i, 4);
  assert.deepEqual(s.map((x) => x.v), [6, 7, 8, 9]);
});

test('eta and duration formatting', () => {
  assert.equal(X.eta(1000, 3000, 500), 4);
  assert.equal(X.eta(1000, 3000, 0), null);
  assert.equal(X.eta(4000, 3000, 10), 0);
  assert.equal(X.fmtEta(42), '42s');
  assert.equal(X.fmtEta(185), '3m 05s');
  assert.equal(X.fmtEta(3720), '1h 02m');
  assert.equal(X.fmtEta(null), '—');
  assert.equal(X.fmtRate(0), '0 rows/s');
  assert.equal(X.fmtRate(850.4), '850 rows/s');
  assert.equal(X.fmtRate(12345), '12.3k rows/s');
  assert.equal(X.fmtRate(2500000), '2.50M rows/s');
  assert.equal(X.fmtDuration('2026-09-11T12:00:00Z', '2026-09-11T12:03:05Z'), '3m 05s');
  assert.equal(X.fmtDuration('2026-09-11T12:00:00Z', null, Date.parse('2026-09-11T12:00:42Z')), '42s');
  assert.equal(X.fmtDuration(null, null), '—');
});

test('aggregate and mergeProgress combine task rows with live snapshots', () => {
  const tasks = [
    { taskId: 'T01', target: 'app.A', status: 'done', rowsDone: 100, rowsSource: 100, rowsError: 0, validation: { countMatch: true } },
    { taskId: 'T02', target: 'app.B', status: 'running', rowsDone: 10, rowsSource: 50, rowsError: 1 },
    { taskId: 'T03', target: 'app.C', status: 'pending', rowsDone: 0, rowsSource: null, rowsError: 0 },
  ];
  assert.deepEqual(X.aggregate(tasks), { done: 110, total: 150, errors: 1, tasksDone: 1, tasksTotal: 3, running: 1, failed: 0, paused: 0 });
  const merged = X.mergeProgress(tasks, { runId: 1, status: 'running', tasks: [{ taskId: 'T02', status: 'running', rowsDone: 40, rowsSource: 50, rowsError: 2, rowsPerSec: 12.5 }], overall: {} });
  assert.equal(merged[1].rowsDone, 40);
  assert.equal(merged[1].rowsError, 2);
  assert.equal(merged[1].rowsPerSec, 12.5);
  assert.equal(merged[0], tasks[0]);                        // untouched rows are reused
  assert.deepEqual(merged[0].validation, { countMatch: true });
  assert.equal(tasks[1].rowsDone, 10);                      // input not mutated
  assert.equal(X.countText(merged[1]), '40 / 50');
});

test('controls follow run status and activity', () => {
  assert.deepEqual(X.controls({ active: true, canStart: false, run: { status: 'running' } }), { pause: true, resume: false, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: false, canStart: false, run: { status: 'paused' } }), { pause: false, resume: true, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: { status: 'failed' } }), { pause: false, resume: true, cancel: true, start: true });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: { status: 'cancelled' } }), { pause: false, resume: false, cancel: false, start: true });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: null }), { pause: false, resume: false, cancel: false, start: true });
});

test('pre-flight tones and counts', () => {
  const checks = [
    { name: 'a', ok: true, severity: 'info', detail: '' },
    { name: 'b', ok: false, severity: 'warning', detail: '' },
    { name: 'c', ok: false, severity: 'error', detail: '' },
  ];
  assert.deepEqual(checks.map(X.checkTone), ['ok', 'warn', 'err']);
  assert.deepEqual(X.preflightCounts({ checks }), { errors: 1, warnings: 1 });
});

test('key, json, options and checksum text', () => {
  assert.equal(X.keyText('{"__k0":777,"__k1":"a"}'), '__k0=777, __k1=a');
  assert.equal(X.keyText(null), '(no key)');
  assert.equal(X.prettyJson('{"a":1}'), '{\n  "a": 1\n}');
  assert.equal(X.prettyJson('not json'), 'not json');
  assert.equal(X.optionsKey({ chunkSize: 5, parallelism: 2, errorMode: 'skip' }),
    'chunkSize=5;parallelism=2;errorMode=skip;truncateTarget=undefined;tableLock=undefined;validateChecksums=undefined;fireTriggers=undefined;keepControlTable=undefined');
  assert.equal(X.checksumText([{ match: true }, { match: false }], null), '1/2 match');
  assert.equal(X.checksumText([], 'rows were rejected'), 'skipped');
  assert.equal(X.checksumText(undefined, undefined), '—');
});

/* ------------------------------------------------------------------ T5.6 carry-forwards: absences that say why */

test('an unknown source count reads as "unknown", never as a blank or a zero', () => {
  assert.equal(X.countText({ rowsDone: 40, rowsSource: null }), '40 / unknown');
  assert.equal(X.countText({ rowsDone: 0, rowsSource: 0 }), '0 / 0');
  assert.equal(X.numOrUnknown(null), 'unknown');
  assert.equal(X.numOrUnknown(0), '0');
  assert.equal(X.numOrUnknown(1500), '1,500');
  // The floor, not the total, while some table has never been counted.
  assert.equal(X.totalText(150, 0), 'of 150');
  assert.equal(X.totalText(150, 2), 'of at least 150');
  assert.equal(X.totalText(0, 3), 'of an unknown number of rows');
  assert.equal(X.totalText(0, 0), 'of 0');
});

test('a not-run check is not a failed one and not a tick', () => {
  // Ruling 139: the engine's own flag, not the wording. PreflightCheck.NotRun is set in Preflight.NotRun(...) and nowhere else.
  const notRunTarget = {
    name: 'target_checks', ok: false, severity: 'warning', notRun: true,
    detail: 'Not checked: the target connection could not be opened, so nothing is known about the target’s tables.',
  };
  const notRunDrift = {
    name: 'schema_drift', ok: false, severity: 'error', notRun: true,
    detail: 'The schemas could not be verified against the discovered catalogs: timeout.',
  };
  const notCounted = {
    name: 'estimated_rows', ok: false, severity: 'warning', notRun: true,
    detail: 'Not counted: the source connection could not be opened, so the number of rows this run would move is unknown.',
  };
  const failedProbe = { name: 'target_probe', ok: false, severity: 'error', notRun: false, detail: 'The target checks stopped part-way: login failed.' };
  const passed = { name: 'sql_plan', ok: true, severity: 'info', notRun: false, detail: 'Approved SQL plan v3 (7 tasks).' };

  assert.equal(X.notRun(notRunTarget), true);
  assert.equal(X.notRun(notRunDrift), true);
  assert.equal(X.notRun(notCounted), true);
  assert.equal(X.notRun(failedProbe), false);
  assert.equal(X.notRun(passed), false);               // a passing check is never "not run"

  assert.equal(X.checkState(passed), 'ok');
  assert.equal(X.checkState(notRunTarget), 'notrun');
  assert.equal(X.checkState(notRunDrift), 'notrun');
  assert.equal(X.checkState(failedProbe), 'err');
  assert.equal(X.checkState({ name: 'target_rows', ok: false, severity: 'warning', detail: 'Target tables already contain rows: app.A.' }), 'warn');

  /* F7, the harm rather than the shape. This is the engine's real target_probe check, built with Err() and carrying the SQL fault
     itself; its sentence reads exactly like a check that did not run. Drawn as "not run" it tells an operator scanning for ✕ marks
     that nobody looked, when the engine looked and found the thing blocking the transfer. */
  const probeFault = {
    name: 'target_probe', ok: false, severity: 'error', notRun: false,
    detail: 'Some target tables could not be checked: app.Orders: Login failed for user \'dbm\'.',
  };
  assert.equal(X.notRun(probeFault), false);
  assert.equal(X.checkState(probeFault), 'err');
  assert.equal(X.checkGlyph(X.checkState(probeFault)), '✕');
  assert.equal(X.checkLabel(X.checkState(probeFault)), 'failed');
  // ...and the same sentence from the NotRun builder is still "not run": the flag decides, the wording never does.
  assert.equal(X.checkState(Object.assign({}, probeFault, { notRun: true, severity: 'warning' })), 'notrun');

  assert.equal(X.checkGlyph('ok'), '✓');
  assert.equal(X.checkGlyph('notrun'), '?');
  assert.equal(X.checkGlyph('warn'), '!');
  assert.equal(X.checkGlyph('err'), '✕');
  assert.equal(X.checkLabel('notrun'), 'not run');
  assert.equal(X.checkLabel('ok'), 'passed');
  assert.equal(X.checkLabel('warn'), 'warning');
  assert.equal(X.checkLabel('err'), 'failed');
});

test('the pre-flight summary counts the checks nobody ran', () => {
  const result = {
    passed: false,
    checks: [
      { name: 'sql_plan', ok: true, severity: 'info', detail: 'Approved SQL plan v3 (7 tasks).' },
      { name: 'target_connection', ok: false, severity: 'error', detail: 'Login failed for user.' },
      { name: 'target_checks', ok: false, severity: 'warning', notRun: true, detail: 'Not checked: the target connection could not be opened.' },
      { name: 'estimated_rows', ok: false, severity: 'error', notRun: true, detail: 'Not counted: the source estimate stopped before finishing.' },
    ],
  };
  assert.deepEqual(X.preflightSummary(result), { total: 4, ok: 1, errors: 2, warnings: 1, notRun: 2 });
  // "including": a not-run check already counts under its severity, so it must not read as an extra problem on top.
  assert.equal(X.preflightText(result), '2 blocking problems, 1 warning, including 2 checks that did not run');
  assert.equal(X.preflightText(null), 'Pre-flight has not run yet');
  assert.equal(X.preflightText({ passed: true, checks: [{ name: 'a', ok: true, severity: 'info', detail: '' }] }), 'Ready to execute');
  assert.equal(X.preflightText({
    passed: true,
    checks: [{ name: 'a', ok: true, severity: 'info', detail: '' }, { name: 'b', ok: false, severity: 'warning', detail: 'rows already there' }],
  }), 'Ready to execute, 1 warning');
  // Passed with a check nobody ran is not "ready" without saying so.
  assert.equal(X.preflightText({
    passed: true,
    checks: [{ name: 'b', ok: false, severity: 'warning', notRun: true, detail: 'Not checked: needs both connections open.' }],
  }), 'Ready to execute, 1 warning, including 1 check that did not run');
});

test('an ETA is withheld while a source count is unknown, and says why', () => {
  assert.deepEqual(X.etaInfo(1000, 3000, 500, 0), { text: '4s', note: '' });
  assert.deepEqual(X.etaInfo(1000, 3000, 500, 2),
    { text: '—', note: '2 tables have not been counted yet, so the rows left are unknown' });
  assert.deepEqual(X.etaInfo(1000, 3000, 0, 0), { text: '—', note: 'waiting for the first rows' });
  assert.deepEqual(X.etaInfo(1000, 0, 500, 0), { text: '—', note: 'the rows to move are unknown' });
});

test('"pausing" is its own state: pause is spent, cancel is not', () => {
  assert.equal(X.stoppingText({ stopping: 'pausing' }), 'pausing…');
  assert.equal(X.stoppingText({ stopping: 'cancelling' }), 'cancelling…');
  assert.equal(X.stoppingText({ stopping: 'failing' }), 'failing…');
  assert.equal(X.stoppingText({ stopping: 'gargling' }), 'gargling…');    // never swallowed, whatever the engine says
  assert.equal(X.stoppingText({}), null);
  // A keyless task keeps loading after Pause: the run still reads "running" and Pause must not offer itself again.
  assert.deepEqual(X.controls({ active: true, canStart: false, stopping: 'pausing', run: { status: 'running' } }),
    { pause: false, resume: false, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: true, canStart: false, stopping: 'cancelling', run: { status: 'running' } }),
    { pause: false, resume: false, cancel: false, start: false });
  // The service's own refusal wins over the status: a run that cannot be resumed shows no armed Resume.
  assert.deepEqual(X.controls({ active: false, canStart: false, canResume: false, run: { status: 'paused' } }),
    { pause: false, resume: false, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: false, canStart: false, canResume: true, run: { status: 'paused' } }),
    { pause: false, resume: true, cancel: true, start: false });
});

test('a count that was never compared is not a mismatch and not a blank', () => {
  assert.deepEqual(X.countMatchInfo({ countMatch: true }), { text: 'match', tone: 'ok', note: '' });
  assert.deepEqual(X.countMatchInfo({ countMatch: false, countCompared: true, countNote: '100 + 2 ≠ 103' }),
    { text: 'mismatch', tone: 'err', note: '100 + 2 ≠ 103' });
  assert.deepEqual(X.countMatchInfo({ countMatch: false, countCompared: false, countNote: 'the target could not be counted' }),
    { text: 'not compared', tone: 'warn', note: 'the target could not be counted' });
  assert.deepEqual(X.countMatchInfo({ countNote: 'no validation was recorded' }),
    { text: 'not compared', tone: 'warn', note: 'no validation was recorded' });
  assert.deepEqual(X.countMatchInfo({}),
    { text: 'not compared', tone: 'warn', note: 'No validation was recorded for this task.' });
  assert.deepEqual(X.countMatchInfo(null), { text: 'not compared', tone: 'warn', note: 'No validation was recorded for this task.' });
});

test('the report headline carries what was not compared', () => {
  const tasks = [
    { countMatch: true, countCompared: true, checksums: [{ match: true }, { match: true }], checksumColumnsNotCompared: 0 },
    { countMatch: false, countCompared: true, checksums: [{ match: false }], checksumColumnsNotCompared: 0 },
    { countMatch: false, countCompared: false, checksums: [], checksumColumnsNotCompared: 0 },
    { checksums: [{ match: true }, { match: true }], checksumColumnsNotCompared: 1 },
  ];
  assert.deepEqual(X.reportCounts(tasks), { total: 4, compared: 2, matched: 1, mismatched: 1, notCompared: 2 });
  assert.deepEqual(X.reportChecksums(tasks), { total: 5, matched: 4, notCompared: 1 });
  assert.equal(X.checksumHeadline({ total: 5, matched: 5, notCompared: 1 }), '5/5 matched, 1 not compared');
  assert.equal(X.checksumHeadline({ total: 5, matched: 5, notCompared: 0 }), '5/5 matched');
  assert.equal(X.checksumHeadline({ total: 5, matched: 4, notCompared: 2 }), '4/5 matched, 2 not compared');
  assert.equal(X.checksumHeadline({ total: 0, matched: 0, notCompared: 0 }), 'not computed');
  assert.equal(X.checksumHeadline({ total: 0, matched: 0, notCompared: 3 }), 'not computed, 3 columns not compared');
  assert.equal(X.countHeadline({ total: 4, compared: 2, matched: 1, mismatched: 1, notCompared: 2 }), '1/2 matched, 2 not compared');
  assert.equal(X.countHeadline({ total: 2, compared: 2, matched: 2, mismatched: 0, notCompared: 0 }), '2/2 matched');
  assert.equal(X.countHeadline({ total: 2, compared: 0, matched: 0, mismatched: 0, notCompared: 2 }), 'none compared, 2 not compared');
  // Per task, the skipped reason and the columns nobody could cover travel with the number.
  assert.equal(X.checksumDetail({ checksums: [{ match: true }], checksumColumnsNotCompared: 2 }), '1/1 match, 2 columns not compared');
  assert.equal(X.checksumDetail({ checksums: [], checksumsSkipped: 'rows were rejected' }), 'skipped');
  assert.equal(X.checksumDetail({ checksums: [] }), '—');
  assert.equal(X.checksumDetail({ checksums: [{ match: true }], checksumColumnsNotCompared: 1 }), '1/1 match, 1 column not compared');
});

test('the errors drawer reads both an array and a degraded answer', () => {
  assert.deepEqual(X.errorsPayload([{ id: 1 }]), { rows: [{ id: 1 }], note: '' });
  assert.deepEqual(X.errorsPayload([]), { rows: [], note: '' });
  assert.deepEqual(X.errorsPayload({ rows: [], note: 'The latest run could not be read.' }),
    { rows: [], note: 'The latest run could not be read.' });
  assert.deepEqual(X.errorsPayload(null), { rows: [], note: '' });
});

test('a refusal is shown in the service\'s own words, and an unknown code is never swallowed', () => {
  assert.equal(X.refusalText({ code: 'busy', message: 'A transfer is already running.', details: [] }), 'A transfer is already running.');
  assert.equal(X.refusalText({ code: 'preflight_failed', message: 'Pre-flight did not pass.', details: ['target_connection: login failed'] }),
    'Pre-flight did not pass. — target_connection: login failed');
  assert.equal(X.refusalText({ code: 'something_new', message: '' }), 'The server refused this (something_new).');
  assert.equal(X.refusalText({ message: 'boom' }), 'boom');
  assert.equal(X.refusalText(null), 'The request failed for a reason the server did not give.');
});

test('a share that rounds to nothing is not nothing', () => {
  assert.equal(X.pctText(3, 184500), '<1%');       // three rejected rows are not "0% of source"
  assert.equal(X.pctText(0, 100), '0%');
  assert.equal(X.pctText(50, 100), '50%');
  assert.equal(X.pctText(5, 0), '—');
  assert.equal(X.pctText(3, null), '—');
  assert.equal(X.pctText(null, 100), '—');
});

test('tasksWithoutSource counts the tables nobody has counted', () => {
  assert.equal(X.tasksWithoutSource([{ rowsSource: 1 }, { rowsSource: null }, { rowsSource: 0 }, {}]), 2);
  assert.equal(X.tasksWithoutSource([]), 0);
  assert.equal(X.tasksWithoutSource(null), 0);
});
