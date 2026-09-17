'use strict';
/*
 * Ruling 141 (fix round 1): the Final report screen over the DOM stub. The report is where a run's absences are settled, so what
 * is asserted here is what an operator reads: "not compared" rather than a blank or a cross, a headline that carries the columns
 * nobody could cover, the notes word for word, and no customer row anywhere on the page.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
D.install();
['lib/dom.js', 'lib/xfer.js', 'components/core.js', 'views/report.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});
const VIEW = globalThis.DBM.views.complete;

function report(over) {
  return Object.assign({
    runId: 9, status: 'completed', startedAt: '2026-09-17T09:00:00+00:00', endedAt: '2026-09-17T09:12:30+00:00',
    durationSec: 750, rowsSource: 184500, rowsLoaded: 184497, rowsError: 3, rowsPerSec: 246, tasksWithoutSource: 1,
    tasks: [
      {
        taskId: 'T01', target: 'app.Customers', status: 'done', rowsSource: 120000, rowsLoaded: 120000, rowsError: 0,
        durationSec: 300, countMatch: true, countCompared: true,
        checksums: [{ column: 'Id', match: true, source: 98123, target: 98123 }, { column: 'Name', match: true, source: 44120, target: 44120 }],
        errorSamples: [],
      },
      {
        taskId: 'T02', target: 'app.Orders', status: 'done', rowsSource: 64500, rowsLoaded: 64497, rowsError: 3, durationSec: 420,
        countMatch: false, countCompared: true, countNote: '64497 loaded + 3 rejected = 64500, but the target holds 64496.',
        checksums: [{ column: 'Id', match: true, source: 771, target: 771 }, { column: 'CustomerId', match: false, source: 9931, target: 9930 }],
        checksumColumnsNote: 'Total and PlacedAt could not be compared: BINARY_CHECKSUM is not value-faithful for money and datetime2.',
        checksumColumnsNotCompared: 2,
        errorSamples: [{ key: '{"Id":88213}', error: "Cannot insert the value NULL into column 'CustomerId'." }],
        errorSamplesNote: '3 rows were counted as rejected but only 1 was recorded.',
      },
      {
        taskId: 'T03', target: 'app.AuditEvents', status: 'done', rowsSource: null, rowsLoaded: 0, rowsError: 0, durationSec: 30,
        rowsSourceNote: 'never established: the run took no source snapshot for this task',
        countMatch: false, countCompared: false, countNote: 'the source row count is unknown, so nothing could be compared',
        checksums: [], checksumsSkipped: 'the target table was not empty when the task started', errorSamples: [],
      },
    ],
    notes: ['The checkpoint table dbo.__dbm_checkpoint already existed and was reused as found.', 'Row counts do not match for: app.Orders.'],
    options: { chunkSize: 100000, parallelism: 4, errorMode: 'skip', truncateTarget: true, validateChecksums: true },
  }, over || {});
}

function mount(payload, ctx) {
  const root = new D.FakeNode('div');
  VIEW.render(root, Object.assign({ artifact: { version: 1, payload: payload }, export: null, toast() {} }, ctx || {}));
  return root;
}

function kpis(root) {
  return D.queryAll(root, '.kpi').map((k) => D.text(D.query(k, '.kpi-l')) + ' = ' + D.text(D.query(k, '.kpi-v')) + ' / ' + D.text(D.query(k, '.kpi-s')));
}

test('the KPIs carry what was not compared, and a share that rounds to nothing is not nothing', () => {
  assert.deepEqual(kpis(mount(report())), [
    'Rows loaded = 184,497 / of at least 184,500 source rows',
    // Three rejected rows out of 184,500 round to 0 %: the one number that must never read as "none".
    'Rejected = 3 / <1% of source · logged',
    'Duration = 12m 30s / 246 rows/s',
    'Row counts = 1/2 matched, 1 not compared / 1 of 3 tasks were not compared',
    'Checksums = 3/4 matched, 2 not compared / 1 differ',
  ]);
});

test('a count nobody compared is "not compared", never a blank and never a cross', () => {
  const root = mount(report());
  const rows = D.queryAll(root, '.rep-tasks tbody tr.tr-click');
  assert.deepEqual(rows.map((r) => D.text(r.children[6])), ['match', 'mismatch', 'not compared']);
  assert.deepEqual(rows.map((r) => D.text(r.children[7])), ['2/2 match', '1/2 match, 2 columns not compared', 'skipped']);
  // A source count nobody established says so in a word, with the flag that points at the sentence in the detail.
  assert.deepEqual(rows.map((r) => D.text(r.children[3])), ['120,000', '64,500', 'unknown*']);
});

test('an unknown duration is not a zero, and a rate nobody could price is not 0 rows/s', () => {
  const root = mount(report({ durationSec: null, rowsPerSec: null }));
  assert.equal(kpis(root)[2], 'Duration = unknown / throughput unknown');
});

test('expanding a task shows every note the engine attached to it', () => {
  const root = mount(report());
  const row = D.queryAll(root, '.rep-tasks tbody tr.tr-click')[1];
  const detail = D.queryAll(root, '.rep-detail')[1];
  assert.equal(detail.hidden, true);
  row.fire('click');
  assert.equal(detail.hidden, false);
  assert.equal(row.getAttribute('aria-expanded'), 'true');
  const text = D.text(detail);
  assert.ok(text.indexOf('64497 loaded + 3 rejected = 64500, but the target holds 64496.') >= 0, 'the countNote');
  assert.ok(text.indexOf('BINARY_CHECKSUM is not value-faithful') >= 0, 'the columns nobody could compare');
  assert.ok(text.indexOf('3 rows were counted as rejected but only 1 was recorded.') >= 0, 'the errorSamplesNote');
});

test('a sample\'s key does not run into its message, nor a target into its count (F6, the same defect)', () => {
  const root = mount(report());
  // Twice on the page: once in the task's detail row, once in the Rejected row samples card. Both are read aloud.
  assert.deepEqual(D.texts(root, '.rep-samples li'), [
    "Id=88213 Cannot insert the value NULL into column 'CustomerId'.",
    "Id=88213 Cannot insert the value NULL into column 'CustomerId'.",
  ]);
  assert.equal(D.text(D.query(root, '.card-b .row')), 'app.Orders 3 rejected rows; 1 sample below');
});

test('the notes are rendered verbatim and in order', () => {
  const r = report();
  assert.deepEqual(D.texts(mount(r), '.rep-notes li'), r.notes);
});

test('the rejected rows stay on the server: the report carries counts, keys and messages only', () => {
  const root = mount(report());
  const page = D.text(root);
  assert.ok(page.indexOf('3 rejected rows; 1 sample below') >= 0, 'the count and what is shown instead of the rows');
  assert.ok(page.indexOf('{"Id":88213}'.replace(/[{}"]/g, '')) < 0 || page.indexOf('Id=88213') >= 0, 'keys are shown decoded');
  assert.ok(page.indexOf('CustomerId":null') < 0, 'no rejected row body anywhere on the page');
});

test('the sum-is-not-identity caveat is shown where the checksums are, not only in the notes', () => {
  assert.ok(D.text(D.query(mount(report()), '.rep-caveat')).indexOf('equal sums do not prove identical rows') >= 0);
  // ...and not claimed where no checksum ran.
  const none = report({ tasks: report().tasks.map((t) => Object.assign({}, t, { checksums: [], checksumColumnsNotCompared: 0 })) });
  assert.equal(D.query(mount(none), '.rep-caveat'), null);
});

test('a run with no stored report says which kind of nothing it is (ruling 117)', async () => {
  globalThis.DBM.api = {
    get() {
      return Promise.resolve({
        run: { id: 9, status: 'failed', startedAt: '2026-09-17T09:00:00+00:00', endedAt: '2026-09-17T09:08:00+00:00', hasReport: false,
          error: "app.Orders stopped at chunk 13: Cannot insert duplicate key row in object 'app.Orders'.",
          notes: ['3 rows were counted as rejected for app.Orders but none were recorded.'] },
        tasks: [], totals: {}, active: false, canStart: true,
      });
    },
  };
  const root = new D.FakeNode('div');
  VIEW.render(root, { artifact: null, export: null, toast() {} });
  await D.settle();
  await D.settle();
  const text = D.text(root);
  assert.ok(text.indexOf('Run #9 failed, so no final report was written for it.') >= 0, 'which kind of nothing');
  assert.ok(text.indexOf("app.Orders stopped at chunk 13") >= 0, 'the run\'s own error');
  assert.deepEqual(D.texts(root, '.rep-notes li'), ['3 rows were counted as rejected for app.Orders but none were recorded.']);
  assert.equal(D.queryAll(root, 'button').filter((b) => D.text(b) === 'Export HTML').length, 0, 'nothing to export');
});

test('the export has no Export button and asks the server for nothing', () => {
  globalThis.DBM.api = null;
  const root = new D.FakeNode('div');
  VIEW.render(root, { artifact: { payload: report() }, export: { project: 'p' }, toast() {} });
  assert.equal(D.queryAll(root, 'button').filter((b) => D.text(b) === 'Export HTML').length, 0);
  assert.equal(kpis(root)[0], 'Rows loaded = 184,497 / of at least 184,500 source rows');
});
