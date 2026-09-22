'use strict';
/*
 * Open item 9, Ruling 203: DBM.anchorLabel (components/review.js) is the THIRD copy of the sql:<taskId>:<line> grammar, after
 * C# SqlModule.ParseAnchor and DBM.sqlView.parseAnchor. It cannot delegate to the view's parser: an analysis or mapping export
 * (WebExport.ScriptOrder) ships review.js without views/sql.js. So it is held to the same shared fixture the other two are.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
D.install();
['lib/dom.js', 'components/core.js', 'components/review.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});
const DBM = globalThis.DBM;
const FIXTURE = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'task-listing.json'), 'utf8'));
const MIN_ANCHOR_CASES = 31;   // a literal on purpose, as in sql-view.test.cjs: a trimmed fixture must not pass quietly

test('anchorLabel reads sql:<taskId>:<line> exactly as SqlModule.ParseAnchor does (shared fixture)', () => {
  assert.ok(Array.isArray(FIXTURE.anchors) && FIXTURE.anchors.length >= MIN_ANCHOR_CASES, 'anchor cases: ' + (FIXTURE.anchors || []).length);
  const wrong = [];
  for (const c of FIXTURE.anchors) {
    const label = DBM.anchorLabel(c.anchor);
    const shown = 'anchor ' + JSON.stringify(c.anchor) + ' labelled ' + JSON.stringify(label);
    if (c.line !== null) {
      if (label !== 'Task ' + c.task + ', line ' + c.line) wrong.push(shown + ', but the engine reads task ' + JSON.stringify(c.task) + ' line ' + c.line);
    } else if (/, line /.test(label)) {
      wrong.push(shown + ', but the engine resolves no line here');
    }
  }
  assert.deepEqual(wrong, [], 'anchorLabel disagrees with SqlModule.ParseAnchor:\n' + wrong.join('\n'));
});

test('anchorLabel keeps the other anchor kinds and the fallback', () => {
  assert.equal(DBM.anchorLabel('task:T04'), 'Task T04');
  assert.equal(DBM.anchorLabel('tablemap:app.Orders'), 'Mapping of app.Orders');
  assert.equal(DBM.anchorLabel(null), 'General');
  assert.equal(DBM.anchorLabel('sql:T04:0', 'T04 line 0'), 'T04 line 0');
  assert.equal(DBM.anchorLabel('sql:T04:0'), 'sql:T04:0');
});
