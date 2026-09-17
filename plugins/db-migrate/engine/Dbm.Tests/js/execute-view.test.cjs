'use strict';
/*
 * Ruling 141 (fix round 1). The Execute screen's rendering was protected by nothing: four mutations to this file - the checklist
 * filtered by a fixed name set, statusNote and the run-notes card dropped, the plan re-fetched on every progress event, and the
 * screen-reader space removed from a tag - all passed a green node suite because nothing loaded views/execute.js.
 *
 * These tests load the real view over the DOM stub and assert what an operator would read off the screen.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
const dom = D.install();
['lib/dom.js', 'lib/xfer.js', 'components/core.js', 'views/execute.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});
const VIEW = globalThis.DBM.views.execute;

/* ------------------------------------------------------------------ the server this screen reads */

function runningView(over) {
  return Object.assign({
    run: {
      id: 9, sqlVersion: 1, status: 'running', startedAt: '2026-09-17T09:00:00+00:00',
      options: { chunkSize: 100000, parallelism: 4, errorMode: 'skip' }, hasReport: false,
      notes: [
        'The checkpoint table dbo.__dbm_checkpoint already existed and was not created by this run; it was reused as found.',
        '3 rows were counted as rejected for app.Orders but none were recorded: the error-row insert failed.',
      ],
    },
    tasks: [
      { taskId: 'T01', target: 'app.Customers', ordinal: 0, status: 'done', rowsSource: 60000, rowsDone: 60000, rowsError: 0, dependsOn: [], keyless: false },
      { taskId: 'T02', target: 'app.Orders', ordinal: 1, status: 'running', rowsSource: 40000, rowsDone: 12000, rowsError: 3, dependsOn: [], keyless: false },
      {
        taskId: 'T03', target: 'app.AuditEvents', ordinal: 2, status: 'paused', rowsSource: null, rowsDone: 0, rowsError: 0,
        dependsOn: [], keyless: true,
        statusNote: 'it did not finish: the run failed, so the task was stopped and rolled back, not paused by an operator.',
        validationNote: 'the validation recorded for this task could not be read.',
      },
    ],
    totals: { rowsSource: 100000, rowsDone: 72000, rowsError: 3, tasksDone: 1, tasksTotal: 3, tasksWithoutSource: 1 },
    active: true, canStart: false, canResume: false, targetDatabase: 'ShopV2', sqlVersion: 1,
    defaults: { chunkSize: 100000, parallelism: 4, errorMode: 'stop', validateChecksums: true },
  }, over || {});
}

const CHECKS = [
  { name: 'sql_plan', ok: true, severity: 'info', detail: 'Approved SQL plan v1 (3 tasks).', notRun: false },
  // A name no code here knows: check names are open-ended (target_probe and source_probe arrived after this screen was designed).
  { name: 'quota_probe', ok: false, severity: 'error', detail: 'The target database is over its size quota.', notRun: false },
  {
    name: 'schema_drift', ok: false, severity: 'warning', notRun: true,
    detail: 'Not checked: comparing the schemas against the discovered catalogs needs both connections open.',
  },
];

function fakeApi(view, over) {
  const api = {
    gets: [], posts: [],
    get(url) {
      api.gets.push(url);
      if (url === '/api/transfer') return Promise.resolve(over && over.view ? over.view() : view);
      if (url.indexOf('/api/transfer/errors') === 0) return Promise.resolve((over && over.errors) || []);
      return Promise.reject(new Error('unexpected GET ' + url));
    },
    post(url, body) {
      api.posts.push({ url: url, body: body });
      if (url === '/api/transfer/preflight') return Promise.resolve((over && over.preflight) || { passed: false, at: '2026-09-17T09:00:00+00:00', checks: CHECKS });
      return Promise.resolve({ ok: true });
    },
    url(p) { return p; },
  };
  globalThis.DBM.api = api;
  return api;
}

/** Renders the view fresh (no carried-over state) and lets its first GET settle. */
async function mount(view, over) {
  const api = fakeApi(view, over);
  const root = new D.FakeNode('div');
  const toasts = [];
  VIEW.render(root, { toast: (m, k) => toasts.push(k + ': ' + m), export: null });
  await D.settle();
  await D.settle();
  return { root: root, api: api, toasts: toasts };
}

/* ------------------------------------------------------------------ M3: the checklist is not filtered */

test('every pre-flight check is listed by its own name, including one no label knows (kills M3)', async () => {
  const m = await mount(runningView({ canStart: true, active: false, run: null }));
  D.query(m.root, 'button').fire('click');          // "Run pre-flight"
  await D.settle();
  await D.settle();

  assert.deepEqual(D.texts(m.root, '.exe-check-name'),
    ['Approved SQL plan', 'Quota probe', 'Schemas unchanged since discovery not run']);
  // ...and each one still carries the engine's sentence, which is the only thing that says what was not checked and why.
  assert.deepEqual(D.texts(m.root, '.exe-check-detail'), CHECKS.map((c) => c.detail));
});

/* ------------------------------------------------------------------ M6 + F6: adjacent tags are readable aloud */

test('a tag never runs into the word before it (kills M6, and F6 one function away)', async () => {
  const m = await mount(runningView({ canStart: true, active: false, run: null }));
  D.query(m.root, 'button').fire('click');
  await D.settle();
  await D.settle();

  // A screen reader reads textContent, so "schema driftnot run" is what a missing text node sounds like.
  const notRun = D.texts(m.root, '.exe-check-name').filter((t) => /not run/.test(t));
  assert.deepEqual(notRun, ['Schemas unchanged since discovery not run']);
});

/* ------------------------------------------------------------------ M4: the notes are on the screen */

test('a task keeps its statusNote and validationNote beside its badge (kills M4a)', async () => {
  const m = await mount(runningView());
  const rows = D.queryAll(m.root, '.exe-tasks tbody tr');
  assert.equal(rows.length, 3);
  assert.deepEqual(D.texts(rows[2], '.exe-task-note'), [
    'it did not finish: the run failed, so the task was stopped and rolled back, not paused by an operator.',
    'the validation recorded for this task could not be read.',
  ]);
});

test('the run\'s own notes are rendered verbatim and in order (kills M4b)', async () => {
  const view = runningView();
  const m = await mount(view);
  assert.deepEqual(D.texts(m.root, '.rep-notes li'), view.run.notes);
});

/* ------------------------------------------------------------------ M5: what re-reads the plan */

test('a progress event never re-reads the view; a run change does, once (kills M5)', async () => {
  const m = await mount(runningView());
  assert.deepEqual(m.api.gets, ['/api/transfer']);

  for (let i = 0; i < 4; i++) {
    VIEW.onEvent({
      type: 'transfer_progress',
      data: {
        runId: 9, status: 'running',
        tasks: [{ taskId: 'T02', target: 'app.Orders', status: 'running', rowsDone: 20000 + i, rowsSource: 40000, rowsError: 3, rowsPerSec: 850 }],
        overall: { done: 80000 + i, total: 100000, rowsError: 3, rowsPerSec: 850, tasksWithoutSource: 1 },
      },
    });
  }
  // One frame for the whole burst, not one per event.
  assert.equal(dom.pendingFrames(), 1);
  dom.flushFrames();
  await D.settle(220);
  assert.deepEqual(m.api.gets, ['/api/transfer'], 'GET /api/transfer deserialises the approved plan artifact: progress must not ask for it');

  VIEW.onEvent({ type: 'transfer_task_changed', data: {} });
  VIEW.onEvent({ type: 'transfer_run_changed', data: {} });
  await D.settle(220);
  assert.deepEqual(m.api.gets, ['/api/transfer', '/api/transfer'], 'two change events 150 ms apart are one re-read');
});

test('a progress snapshot moves the numbers on the screen', async () => {
  const m = await mount(runningView());
  VIEW.onEvent({
    type: 'transfer_progress',
    data: {
      runId: 9, status: 'running',
      tasks: [{ taskId: 'T02', target: 'app.Orders', status: 'running', rowsDone: 31000, rowsSource: 40000, rowsError: 3, rowsPerSec: 850.4 }],
      overall: { done: 91000, total: 100000, rowsError: 3, rowsPerSec: 850.4, tasksWithoutSource: 1 },
    },
  });
  dom.flushFrames();
  const kpis = D.queryAll(m.root, '.kpi').map((k) => D.text(D.query(k, '.kpi-l')) + '=' + D.text(D.query(k, '.kpi-v')));
  assert.equal(kpis[0], 'Rows loaded=91,000');
  assert.equal(kpis[1], 'Throughput=850 rows/s');
  assert.equal(D.text(D.queryAll(m.root, '.exe-tasks tbody tr')[1]).indexOf('31,000 / 40,000') > 0, true);
});

/* ------------------------------------------------------------------ the absences, on screen */

test('an unknown source count reads "unknown" and the totals say "at least"', async () => {
  const m = await mount(runningView());
  assert.equal(D.text(D.queryAll(m.root, '.exe-tasks tbody tr')[2]).indexOf('0 / unknown') > 0, true);
  assert.equal(D.text(D.query(m.root, '.kpi .kpi-s')), 'of at least 100,000 · 72%');
});

/* ------------------------------------------------------------------ F7 / ruling 139: the flag, not the wording */

test('a check the engine ran and failed is drawn as failed, however its sentence reads', async () => {
  // The engine's own target_probe error: built with Err(), carrying the SQL fault, and worded exactly like a check that did not run.
  const probe = {
    name: 'target_probe', ok: false, severity: 'error', notRun: false,
    detail: "Some target tables could not be checked: app.Orders: Login failed for user 'dbm'.",
  };
  const m = await mount(runningView({ canStart: true, active: false, run: null }), {
    preflight: { passed: false, at: '2026-09-17T09:00:00+00:00', checks: [probe] },
  });
  D.query(m.root, 'button').fire('click');
  await D.settle();
  await D.settle();

  const icon = D.query(m.root, '.exe-check-icon');
  assert.equal(D.text(icon), '✕', 'an operator scanning for ✕ marks must find the check that names the cause');
  assert.equal(icon.getAttribute('aria-label'), 'failed');
  assert.equal(icon.classes().includes('is-notrun'), false);
  assert.equal(D.text(D.query(m.root, '.exe-check-name')), 'Target probe');
  assert.equal(D.text(D.query(m.root, '.card-b .badge')), '1 blocking problem');
});

test('the pre-flight headline counts what did not run without counting it twice', async () => {
  const m = await mount(runningView({ canStart: true, active: false, run: null }));
  D.query(m.root, 'button').fire('click');
  await D.settle();
  await D.settle();
  assert.equal(D.text(D.query(m.root, '.card-b .badge')), '1 blocking problem, 1 warning, including 1 check that did not run');
});
