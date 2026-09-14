'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.window = globalThis;
vm.runInThisContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'views', 'mapping.js'), 'utf8'),
  { filename: 'mapping.js' });
const ops = globalThis.DBM.mappingOps;

function payload() {
  return {
    tables: {
      'app.Customers': {
        kind: 'direct', sources: ['dbo.CUST'], confidence: 0.84, method: 'exact',
        columns: {
          CustomerId: { expr: 's.[CUST_ID]', sourceColumns: ['dbo.CUST.CUST_ID'], confidence: 1, method: 'exact' },
          FirstName: { sourceColumns: [], confidence: 0.73, method: 'vector', candidates: [{ source: 'dbo.CUST.CUST_NM', score: 0.73, why: 'name 0.45' }] },
          Email: { expr: 's.[EMAIL_ADDR]', sourceColumns: ['dbo.CUST.EMAIL_ADDR'], confidence: 0.8, method: 'vector' }
        }
      },
      'app.AuditEvents': { kind: 'direct', sources: [], confidence: 0.3, method: 'vector', columns: {} }
    },
    drops: { 'dbo.TMP_IMPORT': { reason: 'empty table with no matching target', method: 'vector' } },
    notes: []
  };
}

function context() {
  return {
    autoAccept: 0.85,
    source: [
      { key: 'dbo.CUST', rows: 1000, columns: [
        { name: 'CUST_ID', type: 'int', nullable: false }, { name: 'CUST_NM', type: 'varchar(100)', nullable: false },
        { name: 'EMAIL_ADDR', type: 'varchar(120)', nullable: true }, { name: 'FAX_NO', type: 'varchar(30)', nullable: true }] },
      { key: 'dbo.AUDIT_LOG', rows: 5000, columns: [{ name: 'LOG_TS', type: 'datetime', nullable: false }, { name: 'USR', type: 'varchar(50)', nullable: false }] },
      { key: 'dbo.TMP_IMPORT', rows: 0, columns: [{ name: 'X', type: 'int', nullable: true }] }
    ],
    target: [
      { key: 'app.Customers', columns: [
        { name: 'CustomerId', type: 'int', nullable: false, identity: true, computed: false, rowversion: false, hasDefault: false },
        { name: 'FirstName', type: 'nvarchar(50)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: false },
        { name: 'Email', type: 'nvarchar(120)', nullable: true, identity: false, computed: false, rowversion: false, hasDefault: false },
        { name: 'CreatedAt', type: 'datetime2(0)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: true },
        { name: 'DisplayName', type: 'nvarchar(101)', nullable: false, identity: false, computed: true, rowversion: false, hasDefault: false }] },
      { key: 'app.AuditEvents', columns: [
        { name: 'EventTime', type: 'datetime2(3)', nullable: false, identity: false, computed: false, rowversion: false, hasDefault: false }] }
    ]
  };
}

// Minimal RFC 6902 subset (same semantics as the engine's JsonPatch) used to check that diff() output round-trips.
function applyOps(doc, list) {
  const out = JSON.parse(JSON.stringify(doc));
  for (const o of list) {
    const segs = o.path.split('/').slice(1).map((s) => s.replace(/~1/g, '/').replace(/~0/g, '~'));
    let node = out;
    for (let i = 0; i < segs.length - 1; i++) {
      if (!(segs[i] in node)) node[segs[i]] = {};
      node = node[segs[i]];
    }
    const last = segs[segs.length - 1];
    if (o.op === 'remove') { assert.ok(last in node, 'remove of missing ' + o.path); delete node[last]; }
    else if (o.op === 'replace') { assert.ok(last in node, 'replace of missing ' + o.path); node[last] = o.value; }
    else node[last] = o.value;
  }
  return out;
}

test('diff of identical payloads is empty', () => {
  assert.deepEqual(ops.diff(payload(), payload()), []);
});

test('changing an expression is one replace op on the column field', () => {
  const after = payload();
  after.tables['app.Customers'].columns.Email.expr = 'LOWER(s.[EMAIL_ADDR])';
  assert.deepEqual(ops.diff(payload(), after), [
    { op: 'replace', path: '/tables/app.Customers/columns/Email/expr', value: 'LOWER(s.[EMAIL_ADDR])' }
  ]);
});

test('setting a missing field is add, clearing a field is remove', () => {
  const after = payload();
  after.tables['app.Customers'].columns.FirstName.expr = 'LEFT(s.[CUST_NM], 10)';
  delete after.tables['app.Customers'].columns.Email.expr;
  assert.deepEqual(ops.diff(payload(), after), [
    { op: 'remove', path: '/tables/app.Customers/columns/Email/expr' },
    { op: 'add', path: '/tables/app.Customers/columns/FirstName/expr', value: 'LEFT(s.[CUST_NM], 10)' }
  ]);
});

test('whole column maps and tables are added and removed as objects', () => {
  const after = payload();
  after.tables['app.Customers'].columns.Phone = { expr: 's.[PHONE_NO]', sourceColumns: ['dbo.CUST.PHONE_NO'], confidence: 1, method: 'human' };
  delete after.tables['app.Customers'].columns.CustomerId;
  delete after.tables['app.AuditEvents'];
  after.tables['app.Products'] = { kind: 'skip', sources: [], confidence: 1, method: 'human', columns: {} };
  const list = ops.diff(payload(), after);
  assert.deepEqual(list.map((o) => o.op + ' ' + o.path), [
    'remove /tables/app.AuditEvents',
    'remove /tables/app.Customers/columns/CustomerId',
    'add /tables/app.Customers/columns/Phone',
    'add /tables/app.Products'
  ]);
});

test('drops are added, replaced and removed; pointer segments are escaped', () => {
  const before = payload();
  before.drops['dbo.A/B~C'] = { reason: 'odd name', method: 'human' };
  const after = payload();
  after.drops['dbo.CUST.FAX_NO'] = { reason: 'no fax in ShopV2', method: 'human' };
  after.drops['dbo.TMP_IMPORT'] = { reason: 'staging table', method: 'human' };
  assert.deepEqual(ops.diff(before, after), [
    { op: 'remove', path: '/drops/dbo.A~1B~0C' },
    { op: 'add', path: '/drops/dbo.CUST.FAX_NO', value: { reason: 'no fax in ShopV2', method: 'human' } },
    { op: 'replace', path: '/drops/dbo.TMP_IMPORT', value: { reason: 'staging table', method: 'human' } }
  ]);
  assert.equal(ops.pointer('tables', 'a/b', 'x~y'), '/tables/a~1b/x~0y');
});

test('notes are replaced as a whole', () => {
  const after = payload();
  after.notes = ['check the orphan orders'];
  assert.deepEqual(ops.diff(payload(), after), [{ op: 'replace', path: '/notes', value: ['check the orphan orders'] }]);
});

test('diff output applied to before yields after', () => {
  const before = payload();
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'FirstName', { expr: "LEFT(s.[CUST_NM], CHARINDEX(' ', s.[CUST_NM] + ' ') - 1)", sourceColumns: 'dbo.CUST.CUST_NM' });
  ops.setSource(after, 'app.AuditEvents', 'dbo.AUDIT_LOG');
  ops.mapToTarget(after, 'dbo.AUDIT_LOG.LOG_TS', 'app.AuditEvents', 'EventTime');
  ops.dropSource(after, 'dbo.CUST.FAX_NO', 'no fax');
  ops.undropSource(after, 'dbo.TMP_IMPORT');
  ops.editTable(after, 'app.Customers', { filter: 's.[CUST_ID] > 0' });
  assert.deepEqual(applyOps(before, ops.diff(before, after)), after);
});

test('editColumn records a human decision and removes blank fields', () => {
  const p = payload();
  const cm = ops.editColumn(p, 'app.Customers', 'Email', { expr: '', 'default': "N''" });
  assert.equal(cm.expr, undefined);
  assert.equal(cm['default'], "N''");
  assert.equal(cm.method, 'human');
  assert.equal(cm.confidence, 1);
  assert.deepEqual(ops.editColumn(p, 'app.Customers', 'Notes', { sourceColumns: 'dbo.CUST.NOTES, dbo.CUST.CUST_ID' }).sourceColumns,
    ['dbo.CUST.NOTES', 'dbo.CUST.CUST_ID']);
  assert.throws(() => ops.editColumn(p, 'app.Nope', 'X', { expr: '1' }), /unknown target table/);
});

test('restoreIfUnchanged drops edits that end where they started', () => {
  const before = payload();
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 'x' });
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 's.[EMAIL_ADDR]' });
  assert.equal(ops.restoreIfUnchanged(before, after, 'app.Customers', 'Email'), true);
  assert.deepEqual(ops.diff(before, after), []);
  ops.editColumn(after, 'app.Customers', 'Phone', { expr: '' });
  assert.equal(ops.restoreIfUnchanged(before, after, 'app.Customers', 'Phone'), true);
  assert.equal('Phone' in after.tables['app.Customers'].columns, false);
});

test('mapToTarget writes s.[COL] for the primary source and adopts a source for unpaired tables', () => {
  const p = payload();
  const cm = ops.mapToTarget(p, 'dbo.CUST.CUST_NM', 'app.Customers', 'FirstName');
  assert.equal(cm.expr, 's.[CUST_NM]');
  assert.deepEqual(cm.sourceColumns, ['dbo.CUST.CUST_NM']);
  ops.mapToTarget(p, 'dbo.AUDIT_LOG.USR', 'app.AuditEvents', 'UserName');
  assert.deepEqual(p.tables['app.AuditEvents'].sources, ['dbo.AUDIT_LOG']);
  assert.throws(() => ops.mapToTarget(p, 'dbo.AUDIT_LOG.LOG_TS', 'app.Customers', 'CreatedAt'), /not in the primary source/);
  assert.equal(ops.quoteName('a]b'), '[a]]b]');
});

test('useCandidate maps the candidate column', () => {
  const p = payload();
  ops.useCandidate(p, 'app.Customers', 'FirstName', 'dbo.CUST.CUST_NM');
  assert.equal(p.tables['app.Customers'].columns.FirstName.expr, 's.[CUST_NM]');
});

test('mapTargetsFor offers writable columns of tables fed by that source or without a source', () => {
  const choices = ops.mapTargetsFor(context(), payload(), 'dbo.CUST.FAX_NO');
  assert.deepEqual(choices.map((c) => c.table + '.' + c.column), [
    'app.Customers.CustomerId', 'app.Customers.FirstName', 'app.Customers.Email', 'app.Customers.CreatedAt', 'app.AuditEvents.EventTime'
  ]);
  assert.equal(choices.find((c) => c.column === 'Email').mapped, true);
  assert.deepEqual(ops.mapTargetsFor(context(), payload(), 'dbo.AUDIT_LOG.USR').map((c) => c.table), ['app.AuditEvents']);
});

test('uncovered honours column maps, drops, table drops and skip maps', () => {
  const p = payload();
  assert.deepEqual(ops.uncovered(context(), p), ['dbo.CUST.CUST_NM', 'dbo.CUST.FAX_NO', 'dbo.AUDIT_LOG.LOG_TS', 'dbo.AUDIT_LOG.USR']);
  ops.dropSource(p, 'dbo.AUDIT_LOG', 'history stays behind');
  ops.dropSource(p, 'dbo.cust.fax_no', 'no fax');
  assert.deepEqual(ops.uncovered(context(), p), ['dbo.CUST.CUST_NM']);
  p.tables['app.Customers'].kind = 'skip';
  assert.equal(ops.uncovered(context(), p).length, 3);
});

test('blockers mirror MappingValidator messages', () => {
  const p = payload();
  assert.deepEqual(ops.blockers(context(), p), [
    'app.Customers.FirstName: NOT NULL without default needs an expression or default',
    'app.AuditEvents: no source table (choose one or set kind "skip")',
    'app.AuditEvents.EventTime: NOT NULL without default needs an expression or default',
    'source column dbo.CUST.CUST_NM is not mapped or dropped',
    'source column dbo.CUST.FAX_NO is not mapped or dropped',
    'source table dbo.AUDIT_LOG is not mapped or dropped (2 columns)'
  ]);
  delete p.tables['app.AuditEvents'];
  assert.equal(ops.blockers(context(), p)[1], 'app.AuditEvents: no table mapping (map a source table or set kind "skip")');
  ops.skipTable(p, 'app.AuditEvents', 'not migrated');
  assert.equal(ops.blockers(context(), p).some((b) => b.startsWith('app.AuditEvents')), false);
});

test('statuses and attention follow the auto-accept band', () => {
  const p = payload();
  const b = ops.blockers(context(), p);
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], b, 0.85), 'blocker');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.85), 'attention');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.75), 'attention');
  assert.equal(ops.tableStatus('app.Customers', p.tables['app.Customers'], [], 0.7), 'ok');
  assert.equal(ops.tableStatus('app.Missing', undefined, [], 0.85), 'blocker');
  assert.equal(ops.columnStatus('app.Customers', 'FirstName', p.tables['app.Customers'].columns.FirstName, b, 0.85), 'blocker');
  assert.equal(ops.columnStatus('app.Customers', 'Email', p.tables['app.Customers'].columns.Email, b, 0.85), 'attention');
  assert.equal(ops.needsReview({ method: 'agent', confidence: 0.1 }, 0.85), false);
  assert.equal(ops.attentionCount(p, 0.85), 3);
});

test('changeCount counts edited entities, not ops', () => {
  const after = payload();
  ops.editColumn(after, 'app.Customers', 'Email', { expr: 'LOWER(s.[EMAIL_ADDR])' });
  ops.dropSource(after, 'dbo.CUST.FAX_NO', 'no fax');
  ops.editTable(after, 'app.Customers', { filter: 's.[CUST_ID] > 0' });
  const list = ops.diff(payload(), after);
  assert.equal(list.length, 7);
  assert.equal(ops.changeCount(list), 3);
  assert.equal(ops.changeCount([]), 0);
});

test('parseSourceColumns splits on commas, semicolons and whitespace', () => {
  assert.deepEqual(ops.parseSourceColumns(' dbo.A.X, dbo.A.Y;dbo.B.Z\n'), ['dbo.A.X', 'dbo.A.Y', 'dbo.B.Z']);
  assert.deepEqual(ops.parseSourceColumns(''), []);
});

test('the view registers itself', () => {
  assert.equal(globalThis.DBM.views.mapping.title, 'Mapping');
  assert.equal(typeof globalThis.DBM.views.mapping.render, 'function');
});
