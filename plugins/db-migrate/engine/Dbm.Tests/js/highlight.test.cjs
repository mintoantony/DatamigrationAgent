'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'highlight.js'), 'utf8'));
const H = globalThis.DBM.highlight;

test('keywords, functions, bracketed identifiers and numbers', () => {
  const html = H.sql('SELECT LEFT(s.[CUST_NM], 3) AS [First] FROM [dbo].[CUST] AS s');
  assert.equal(html,
    '<span class="tok-kw">SELECT</span> <span class="tok-fn">LEFT</span>(s.<span class="tok-id">[CUST_NM]</span>, ' +
    '<span class="tok-num">3</span>) <span class="tok-kw">AS</span> <span class="tok-id">[First]</span> ' +
    '<span class="tok-kw">FROM</span> <span class="tok-id">[dbo]</span>.<span class="tok-id">[CUST]</span> ' +
    '<span class="tok-kw">AS</span> s');
});

test('a script tag inside a string literal comes out escaped', () => {
  const html = H.sql("SELECT '<script>alert(1)</script>' AS x");
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('<span class="tok-str">&#39;&lt;script&gt;alert(1)&lt;/script&gt;&#39;</span>'));
});

test('doubled quotes stay inside one string; N prefix belongs to the string', () => {
  const html = H.sql("SELECT N'O''Brien', 'x'");
  assert.ok(html.includes('<span class="tok-str">N&#39;O&#39;&#39;Brien&#39;</span>'));
  assert.ok(html.includes('<span class="tok-str">&#39;x&#39;</span>'));
});

test('line comments end at the newline and are escaped', () => {
  const lines = H.sqlLines("SELECT 1 -- it's <b>bold</b>\nFROM t");
  assert.equal(lines[0], '<span class="tok-kw">SELECT</span> <span class="tok-num">1</span> <span class="tok-com">-- it&#39;s &lt;b&gt;bold&lt;/b&gt;</span>');
  assert.equal(lines[1], '<span class="tok-kw">FROM</span> t');
});

test('block comments (nested) spanning lines are reopened on every line', () => {
  const lines = H.sqlLines('/* a /* b */\n c */ SELECT');
  assert.deepEqual(lines, ['<span class="tok-com">/* a /* b */</span>', '<span class="tok-com"> c */</span> <span class="tok-kw">SELECT</span>']);
});

test('brackets with an escaped closing bracket are one identifier', () => {
  assert.equal(H.sql('[a]]b] + 1'), '<span class="tok-id">[a]]b]</span> <span class="tok-op">+</span> <span class="tok-num">1</span>');
});

test('variables and temp tables are identifiers; <> is one operator', () => {
  assert.equal(H.sql('@x <> #stg'), '<span class="tok-id">@x</span> <span class="tok-op">&lt;&gt;</span> <span class="tok-id">#stg</span>');
});

test('a function name without a parenthesis is a keyword or plain text', () => {
  assert.equal(H.sql('nvarchar(max)'), '<span class="tok-kw">nvarchar</span>(<span class="tok-kw">max</span>)');
  assert.equal(H.sql('LEFT JOIN'), '<span class="tok-kw">LEFT</span> <span class="tok-kw">JOIN</span>');
  assert.equal(H.sql('MAX (x)'), '<span class="tok-fn">MAX</span> (x)');
});

test('unterminated strings and brackets do not throw', () => {
  assert.equal(H.sql("SELECT '<x"), '<span class="tok-kw">SELECT</span> <span class="tok-str">&#39;&lt;x</span>');
  assert.equal(H.sql('[open'), '<span class="tok-id">[open</span>');
});

test('line count is preserved; CRLF normalised; null is empty', () => {
  assert.equal(H.sqlLines('a\r\n\r\nb').length, 3);
  assert.equal(H.sql(null), '');
  assert.equal(H.sql(undefined), '');
});

/* The SQL view pairs sqlLines(section)[i] with TaskListing line i; TaskListing keeps a lone \r inside its line, so the
   highlighter must not break a line there or every later line of the section shows the wrong text beside its number. */
test('a lone CR is not a line break (same line split as TaskListing)', () => {
  const lines = H.sqlLines("SELECT 'a\rb'\r\n-- c\rd\nFROM t\r");
  assert.equal(lines.length, 3);
  assert.equal(lines[2], '<span class="tok-kw">FROM</span> t\r');
});

test('tokens concatenate back to the input', () => {
  const src = "SELECT N'x''y' /* c */ -- d\n, [a]]b], 0x1F, 1.5e3 FROM t WHERE a<>b";
  assert.equal(H.tokenize(src).map(t => t.v).join(''), src);
});
