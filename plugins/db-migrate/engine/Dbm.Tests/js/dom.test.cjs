// node --test plugins/db-migrate/engine/Dbm.Tests/js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

globalThis.DBM = globalThis.DBM || {};
const file = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'dom.js');
vm.runInThisContext(fs.readFileSync(file, 'utf8'), { filename: file });
const DBM = globalThis.DBM;

test('esc escapes the five HTML-significant characters and nulls', () => {
  assert.equal(DBM.esc(`<a href="x">Tom & 'Jerry'</a>`), '&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;');
  assert.equal(DBM.esc(null), '');
  assert.equal(DBM.esc(42), '42');
});

test('fmt.num groups thousands and keeps two decimals', () => {
  assert.equal(DBM.fmt.num(1234567.891), '1,234,567.89');
  assert.equal(DBM.fmt.num(0), '0');
  assert.equal(DBM.fmt.num(null), '—');
});

test('fmt.pct, fmt.mb and fmt.dur', () => {
  assert.equal(DBM.fmt.pct(0.734), '73%');
  assert.equal(DBM.fmt.pct(0.734, 1), '73.4%');
  assert.equal(DBM.fmt.mb(0.42), '0.4 MB');
  assert.equal(DBM.fmt.mb(12.6), '13 MB');
  assert.equal(DBM.fmt.mb(1536), '1.5 GB');
  assert.equal(DBM.fmt.mb(3 * 1024 * 1024), '3.0 TB');
  assert.equal(DBM.fmt.dur(850), '850 ms');
  assert.equal(DBM.fmt.dur(12300), '12.3 s');
  assert.equal(DBM.fmt.dur(245000), '4m 05s');
  assert.equal(DBM.fmt.dur(3720000), '1h 02m');
});

test('fmt.ts is local "yyyy-mm-dd hh:mm" and fmt.rel is relative', () => {
  assert.match(DBM.fmt.ts('2026-09-11T10:30:00Z'), /^2026-09-1[01] \d\d:\d\d$/);
  const now = Date.parse('2026-09-11T12:00:00Z');
  assert.equal(DBM.fmt.rel('2026-09-11T11:59:30Z', now), 'just now');
  assert.equal(DBM.fmt.rel('2026-09-11T11:55:00Z', now), '5 min ago');
  assert.equal(DBM.fmt.rel('2026-09-11T09:00:00Z', now), '3 h ago');
  assert.equal(DBM.fmt.rel('2026-09-09T12:00:00Z', now), '2 d ago');
  assert.equal(DBM.fmt.rel(null), '—');
});

test('mdLite renders paragraphs, bullet lists, bold and code', () => {
  const html = DBM.mdLite('Two **critical** risks:\n- heap `dbo.AUDIT_LOG`\n* orphan rows\n\nSecond paragraph\ncontinues.');
  assert.equal(html,
    '<p>Two <strong>critical</strong> risks:</p>' +
    '<ul><li>heap <code>dbo.AUDIT_LOG</code></li><li>orphan rows</li></ul>' +
    '<p>Second paragraph continues.</p>');
});

test('mdLite escapes HTML everywhere, including inside code and bold', () => {
  const html = DBM.mdLite('<script>alert(1)</script> **<b>x</b>** `<i>y</i>`');
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('&lt;script&gt;'));
  assert.ok(html.includes('<strong>&lt;b&gt;x&lt;/b&gt;</strong>'));
  assert.ok(html.includes('<code>&lt;i&gt;y&lt;/i&gt;</code>'));
  assert.equal(DBM.mdLite(''), '');
  assert.equal(DBM.mdLite(null), '');
});
