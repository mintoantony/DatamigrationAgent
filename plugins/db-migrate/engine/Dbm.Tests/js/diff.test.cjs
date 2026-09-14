// node --test plugins/db-migrate/engine/Dbm.Tests/js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.DBM = globalThis.DBM || {};
const file = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'diff.js');
vm.runInThisContext(fs.readFileSync(file, 'utf8'), { filename: file });
const { lines, collapse, stats } = globalThis.DBM.diff;

const apply = (ops) => ops.filter((o) => o.op !== 'del').map((o) => o.text).join('\n');
const original = (ops) => ops.filter((o) => o.op !== 'add').map((o) => o.text).join('\n');

test('identical texts are all eq', () => {
  const ops = lines('a\nb', 'a\nb');
  assert.deepEqual(ops, [{ op: 'eq', text: 'a' }, { op: 'eq', text: 'b' }]);
});

test('a changed middle line is one del and one add', () => {
  const ops = lines('a\nb\nc', 'a\nB\nc');
  assert.deepEqual(ops.map((o) => o.op), ['eq', 'del', 'add', 'eq']);
  assert.deepEqual(stats(ops), { add: 1, del: 1 });
});

test('insertions and deletions reconstruct both sides', () => {
  const a = '{\n  "x": 1,\n  "y": 2,\n  "z": 3\n}';
  const b = '{\n  "w": 0,\n  "x": 1,\n  "z": 4\n}';
  const ops = lines(a, b);
  assert.equal(original(ops), a);
  assert.equal(apply(ops), b);
  assert.deepEqual(stats(ops), { add: 2, del: 2 });
});

test('empty inputs and CRLF are handled', () => {
  assert.deepEqual(lines('', ''), []);
  assert.deepEqual(lines('', 'a').map((o) => o.op), ['add']);
  assert.deepEqual(lines('a\r\nb', 'a\nb').map((o) => o.op), ['eq', 'eq']);
});

test('collapse keeps context around changes and counts skipped lines', () => {
  const a = Array.from({ length: 20 }, (_, i) => `line ${i}`).join('\n');
  const b = a.replace('line 10', 'LINE 10');
  const out = collapse(lines(a, b), 2);
  assert.deepEqual(out[0], { op: 'skip', count: 8 });
  assert.deepEqual(out.slice(1, 7).map((o) => o.op), ['eq', 'eq', 'del', 'add', 'eq', 'eq']);
  assert.deepEqual(out[7], { op: 'skip', count: 7 });
});
