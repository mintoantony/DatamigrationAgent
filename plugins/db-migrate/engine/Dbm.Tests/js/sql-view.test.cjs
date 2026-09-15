'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
vm.runInThisContext(fs.readFileSync(path.join(JS, 'lib', 'highlight.js'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(JS, 'views', 'sql.js'), 'utf8'));
const V = globalThis.DBM.sqlView;

/* The JS half of the listing mirror: the C# half (Unit/SqlGen/TaskListingMirrorTests.cs) asserts this same file against
   TaskListing.Build and SqlModule.ParseAnchor. */
const FIXTURE = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'task-listing.json'), 'utf8'));

const addresses = {
  target: 'app.Addresses',
  preSql: [],
  sourceQuery: 'SELECT\n    s.[ADDR_ID] AS [AddressId],\n    s.[CUST_ID] AS [CustomerId],\n    s.[LINE1] AS [Line1],\n' +
    '    s.[CITY] AS [City],\n    s.[ZIP] AS [PostalCode],\n    s.[CTRY_CD] AS [CountryCode],\n    s.[ADDR_ID] AS [__k0]\nFROM [dbo].[ADDR] AS s',
  postSql: [],
};

test('listing numbers lines like TaskListing (sample Addresses task: line 9 is FROM)', () => {
  const lines = V.listing(addresses);
  assert.equal(lines.length, 9);
  assert.deepEqual(lines[8], { no: 9, section: 'source', text: 'FROM [dbo].[ADDR] AS s' });
});

test('listing order is pre, source, staging, merge, post; empty sections skipped; CRLF normalised', () => {
  const task = { preSql: ['ALTER A;'], sourceQuery: 'SELECT\r\n1 AS [x]', stagingDdl: null, mergeSql: '', postSql: ['P1;', 'P2a\nP2b'] };
  const lines = V.listing(task);
  assert.deepEqual(lines.map(l => l.no), [1, 2, 3, 4, 5, 6]);
  assert.deepEqual(lines.map(l => l.section), ['pre', 'source', 'source', 'post', 'post', 'post']);
  assert.equal(lines[2].text, '1 AS [x]');
});

/* The fixture is loaded from disk: an empty or unreadable one would register zero mirror tests and still pass. The counts are
   literals on purpose (never derived from the file just read); raise them when a case is added, never lower them to make a
   trimmed fixture pass — the fixture is the only thing pinning TaskListing's and ParseAnchor's rules on both sides. */
const MIN_LISTING_CASES = 11;
const MIN_ANCHOR_CASES = 31;

test('the shared fixture holds at least the expected number of cases', () => {
  assert.ok(Array.isArray(FIXTURE.listing) && FIXTURE.listing.length >= MIN_LISTING_CASES, 'listing cases: ' + (FIXTURE.listing || []).length);
  assert.ok(Array.isArray(FIXTURE.anchors) && FIXTURE.anchors.length >= MIN_ANCHOR_CASES, 'anchor cases: ' + (FIXTURE.anchors || []).length);
  const names = FIXTURE.listing.map(c => c.name);
  assert.ok(names.some(n => /lone CR/.test(n)), 'a lone-CR case');
  assert.ok(names.some(n => /CRLF/.test(n)), 'a CRLF case');
});

assert.ok(FIXTURE.listing.length >= MIN_LISTING_CASES, 'fixture listing cases missing: the mirror tests below would not register');
for (const c of FIXTURE.listing) {
  test('listing mirrors C# TaskListing.Build (shared fixture): ' + c.name, () => {
    assert.deepEqual(V.listing(c.task), c.expected);
  });
}

/* Undo DBM.highlight's escaping and markup: what a line shows, as text. */
function shownText(html) {
  return html.replace(/<span class="tok-[a-z]+">/g, '').replace(/<\/span>/g, '')
    .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
}

/* The screen puts sqlLines(text)[i] beside listing line number i+1. If the highlighter split lines by a different rule than
   TaskListing (it once broke on a lone \r), every later line would show the wrong SQL beside its number — and a comment made
   on what the reviewer reads would anchor to another statement. Pinned for every fixture case, on the raw field texts (so
   CRLF and lone CR reach the highlighter unnormalised) and on the section blocks the view actually renders. */
test('pairing: sqlLines(text)[i] is listing line i+1 for every non-skipped text of every fixture case', () => {
  const H = globalThis.DBM.highlight;
  let pairs = 0;
  let sawCr = false;
  let sawCrLf = false;
  for (const c of FIXTURE.listing) {
    const t = c.task;
    const inputs = [].concat(
      (t.preSql || []).map(s => ['pre', s]), [['source', t.sourceQuery], ['staging', t.stagingDdl], ['merge', t.mergeSql]],
      (t.postSql || []).map(s => ['post', s]));
    let at = 0;
    for (const [section, text] of inputs) {
      if (text == null || text === '') continue;
      if (/\r(?!\n)/.test(text)) sawCr = true;
      if (/\r\n/.test(text)) sawCrLf = true;
      const html = H.sqlLines(text);
      const expected = c.expected.slice(at, at + html.length);
      assert.equal(expected.length, html.length, c.name + ': ' + JSON.stringify(text) + ' ran past the listing');
      html.forEach((line, i) => {
        assert.equal(expected[i].section, section, c.name);
        assert.equal(shownText(line), expected[i].text, c.name + ': line ' + expected[i].no);
        pairs++;
      });
      at += html.length;
    }
    assert.equal(at, c.expected.length, c.name + ': highlighter produced a different number of lines than TaskListing');

    const flat = [].concat(...V.sectionBlocks(t).map(b => b.lines));
    assert.deepEqual(flat.map(l => [l.no, shownText(l.html)]), c.expected.map(l => [l.no, l.text]), c.name + ' (sectionBlocks)');
  }
  assert.ok(sawCr && sawCrLf, 'the fixture must exercise a lone CR and a CRLF');
  assert.ok(pairs >= 40, 'pairs checked: ' + pairs);
});

test('parseAnchor mirrors C# SqlModule.ParseAnchor for every accepted and rejected form (shared fixture)', () => {
  assert.ok(FIXTURE.anchors.length >= MIN_ANCHOR_CASES);
  for (const c of FIXTURE.anchors) {
    assert.deepEqual(V.parseAnchor(c.anchor), { task: c.task, line: c.line }, 'anchor ' + JSON.stringify(c.anchor));
  }
  assert.deepEqual(V.parseAnchor(undefined), { task: null, line: null });
});

test('sectionBlocks pairs every listing line with its own highlighted line, even around a lone CR', () => {
  const task = { preSql: ['A;'], sourceQuery: "SELECT 'x\ry'\r\nFROM t\r", mergeSql: 'M;\n', postSql: ['P;'] };
  const blocks = V.sectionBlocks(task);
  assert.deepEqual(blocks.map(b => b.section), ['pre', 'source', 'merge', 'post']);
  const flat = [].concat(...blocks.map(b => b.lines));
  assert.deepEqual(flat.map(l => [l.no, l.text]), V.listing(task).map(l => [l.no, l.text]));
  assert.equal(flat[2].html, '<span class="tok-kw">FROM</span> t\r');
  assert.equal(flat[4].html, '');
  assert.equal(flat[5].html, 'P;');
});

test('splitStatements splits on GO lines only', () => {
  assert.deepEqual(V.splitStatements('A;\nGO\n\nB;\n  go  \nGOTO x;'), ['A;', 'B;', 'GOTO x;']);
});

test('joinStatements and splitStatements round-trip', () => {
  const list = ['ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];', 'UPDATE STATISTICS [a].[b];'];
  assert.deepEqual(V.splitStatements(V.joinStatements(list)), list);
});

test('editOps emits replace ops only for changed fields', () => {
  const task = { sourceQuery: 'SELECT 1 AS [a]', preSql: [], postSql: [] };
  const ops = V.editOps('T01', task, { sourceQuery: 'SELECT 2 AS [a]\n', preSql: 'X;\nGO\nY;', postSql: '', stagingDdl: '' });
  assert.deepEqual(ops, [
    { op: 'replace', path: '/tasks/T01/sourceQuery', value: 'SELECT 2 AS [a]' },
    { op: 'replace', path: '/tasks/T01/preSql', value: ['X;', 'Y;'] },
  ]);
});

test('editOps adds missing fields and removes cleared ones', () => {
  assert.deepEqual(V.editOps('T04', { sourceQuery: 'q' }, { stagingDdl: 'CREATE TABLE #stg (a int);' }),
    [{ op: 'add', path: '/tasks/T04/stagingDdl', value: 'CREATE TABLE #stg (a int);' }]);
  assert.deepEqual(V.editOps('T04', { sourceQuery: 'q', mergeSql: 'INSERT x;' }, { mergeSql: '  ' }),
    [{ op: 'remove', path: '/tasks/T04/mergeSql' }]);
});

/* T4.3 contract: an empty globalWarnings means "checked, nothing to report"; a missing one means nobody said, and must never
   render as clean. Every warning line counts, so a "not checked" line is never silence. */
test('reportSummary never calls a report clean unless globalWarnings is present and every list is empty', () => {
  const clean = { version: 3, ok: true, taskErrors: { T01: [] }, taskWarnings: { T01: [] }, globalErrors: [], globalWarnings: [] };
  assert.deepEqual(V.reportSummary(clean), { state: 'clean', errors: 0, warnings: 0, globalCoverage: 'checked' });

  const noGlobal = { version: 3, ok: true, taskErrors: {}, taskWarnings: {}, globalErrors: [] };
  assert.deepEqual(V.reportSummary(noGlobal), { state: 'unreported', errors: 0, warnings: 0, globalCoverage: 'unreported' });
  assert.equal(V.reportSummary(Object.assign({}, noGlobal, { globalWarnings: null })).state, 'unreported');

  const notChecked = Object.assign({}, clean, { globalWarnings: ['global preSql/postSql: not checked: single-task validation'] });
  assert.deepEqual(V.reportSummary(notChecked), { state: 'warnings', errors: 0, warnings: 1, globalCoverage: 'checked' });

  const taskWarn = Object.assign({}, clean, { taskWarnings: { T01: ['mergeSql: not checked: x', 'y'] } });
  assert.equal(V.reportSummary(taskWarn).warnings, 2);
  assert.equal(V.reportSummary(taskWarn).state, 'warnings');

  const bad = Object.assign({}, clean, { ok: false, taskErrors: { T01: ['e'] }, globalErrors: ['g'] });
  assert.deepEqual(V.reportSummary(bad), { state: 'errors', errors: 2, warnings: 0, globalCoverage: 'checked' });
  assert.equal(V.reportSummary(Object.assign({}, clean, { ok: false })).state, 'errors');
});

test('openCommentsByTask counts draft and open comments per task through parseAnchor', () => {
  const feedback = [
    { anchor: 'task:T01', status: 'open' },
    { anchor: 'sql:T01:3', status: 'draft' },
    { anchor: 'sql:T01:0', status: 'open' },
    { anchor: 'sql:T02:1', status: 'addressed' },
    { anchor: 'sql:T02:+1', status: 'open' },
    { anchor: null, status: 'open' },
  ];
  assert.deepEqual(V.openCommentsByTask(feedback), { T01: 2 });
  assert.deepEqual(V.openCommentsByTask(undefined), {});
});

/* ------------------------------------------------------------------ render over a minimal fake DOM */

function fakeDom() {
  function node(tag, attrs, kids) {
    const n = {
      tag, attrs: attrs || {}, children: [], value: '', disabled: !!(attrs && attrs.disabled),
      appendChild(c) { this.children.push(c); return c; },
      removeChild(c) { this.children.splice(this.children.indexOf(c), 1); return c; },
      get firstChild() { return this.children[0] || null; },
    };
    (kids || []).forEach(k => { if (k != null && k !== false) n.children.push(typeof k === 'string' ? { text: k } : k); });
    return n;
  }
  const h = (tag, attrs, ...kids) => node(tag, attrs, kids);
  const all = (root, pred, out = []) => { if (pred(root)) out.push(root); (root.children || []).forEach(c => all(c, pred, out)); return out; };
  const text = n => n.text != null ? n.text : ((n.attrs && n.attrs.html) || '') + (n.children || []).map(text).join('');
  return { h, all, text, root: node('main') };
}

function sampleCtx(overrides) {
  const plan = {
    preSql: ['ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];'], postSql: [], warnings: [], errors: [],
    order: ['T01', 'T02'],
    tasks: {
      T01: Object.assign({ mode: 'direct', keyColumns: ['__k0'], columns: [], dependsOn: [], warnings: [], errors: [], countSql: 'SELECT 1' }, addresses),
      T02: { target: 'app.Orders', mode: 'staging_merge', sourceQuery: 'SELECT 1 AS [a]', stagingDdl: 'CREATE TABLE #stg (a int);', mergeSql: 'M;',
        preSql: [], postSql: [], keyColumns: [], columns: [], dependsOn: ['T01'], warnings: ['mergeSql: not checked: why'], errors: [], countSql: '' },
    },
  };
  const anchors = [];
  const ctx = Object.assign({
    artifact: { version: 2, payload: plan }, versions: [{ version: 1, author: 'script' }, { version: 2, author: 'human' }],
    latestVersion: 2, version: 2, readOnly: false, feedback: [{ anchor: 'sql:T02:2', status: 'open' }],
    api: { url: p => p + '?t=x', get: () => new Promise(() => {}), post: () => new Promise(() => {}) },
    toast() {}, refresh() {}, commentable(el, anchor) { anchors.push(anchor); return el; },
  }, overrides || {});
  return { ctx, anchors, plan };
}

function withFakeDom(fn) {
  const dom = fakeDom();
  const saved = { h: globalThis.DBM.h, components: globalThis.DBM.components, fmt: globalThis.DBM.fmt, diff: globalThis.DBM.diff };
  globalThis.DBM.h = dom.h;
  globalThis.DBM.fmt = { num: n => String(n) };
  globalThis.DBM.diff = { lines: () => [] };
  globalThis.DBM.components = {
    kpi: (label, value) => dom.h('div', { class: 'kpi' }, label, value),
    emptyState: (title) => dom.h('div', { class: 'empty' }, title),
    reviewBar: () => dom.h('div', { class: 'review-bar' }),
  };
  try { return fn(dom); } finally { Object.assign(globalThis.DBM, saved); globalThis.DBM.views.sql.leave(); }
}

test('render: every listing line of the selected task is commentable as sql:<id>:<line>, the header as task:<id>', () => {
  withFakeDom(dom => {
    const { ctx, anchors } = sampleCtx();
    globalThis.DBM.views.sql.render(dom.root, ctx);
    assert.deepEqual(anchors, ['task:T01'].concat(V.listing(addresses).map(l => 'sql:T01:' + l.no)));
    const numbers = dom.all(dom.root, n => n.attrs && n.attrs.class === 'line' && n.attrs.data).map(n => n.attrs.data.line);
    assert.deepEqual(numbers, ['1', '2', '3', '4', '5', '6', '7', '8', '9']);
    const buttons = dom.all(dom.root, n => n.tag === 'button' || n.tag === 'a').map(dom.text);
    assert.ok(buttons.includes('Validate live'));
    assert.ok(buttons.includes('Download script pack'));
    assert.ok(buttons.includes('Edit SQL'));
    assert.ok(dom.text(dom.root).includes('1 comment'), 'T02 shows its open comment count');
  });
});

test('render: read-only hides Edit SQL; a standalone export (api null) has no server controls at all', () => {
  withFakeDom(dom => {
    globalThis.DBM.views.sql.render(dom.root, sampleCtx({ readOnly: true }).ctx);
    let controls = dom.all(dom.root, n => n.tag === 'button' || n.tag === 'a' || n.tag === 'select').map(dom.text);
    assert.ok(!controls.includes('Edit SQL'));
    assert.ok(controls.includes('Validate live'));

    const exported = sampleCtx({ readOnly: true, api: null, versions: [], feedback: [] });
    globalThis.DBM.views.sql.render(dom.root, exported.ctx);
    controls = dom.all(dom.root, n => n.tag === 'button' || n.tag === 'a' || n.tag === 'select').map(dom.text);
    assert.ok(!controls.some(t => /Validate|Download|Edit SQL|Compare/.test(t)), JSON.stringify(controls));
    assert.ok(dom.text(dom.root).includes('FK_Customers_PrimaryAddress'));
  });
});

test('render: a task warning that says "not checked" is shown verbatim', () => {
  withFakeDom(dom => {
    const { ctx } = sampleCtx();
    globalThis.DBM.views.sql.render(dom.root, ctx);
    const t02 = dom.all(dom.root, n => n.tag === 'button' && /app\.Orders/.test(dom.text(n)))[0];
    t02.attrs.on.click();
    assert.ok(dom.text(dom.root).includes('mergeSql: not checked: why'));
  });
});

/* Ruling Q4: "never collapsed or hidden" is a guarantee, not an intention. Far more warnings than any list would show, each
   list ending in a "not checked" line; every line must reach the rendered page. A "show first N, … and M more" fold fails this. */
test('render: validation and task warnings never collapse — the last "not checked" line of a long list is shown', async () => {
  const many = n => Array.from({ length: n }, (_, i) => 'warning ' + (i + 1) + ': varchar(500) -> nvarchar(200)');
  const lastGlobal = 'global preSql/postSql: not checked: single-task validation';
  const lastTask = 'T02: mergeSql: not checked: depends on an object created by an earlier task';
  const lastPlanTask = 'postSql: not checked: statement 40';
  const report = {
    version: 2, ok: true, taskErrors: { T01: [], T02: [] },
    taskWarnings: { T01: many(60), T02: many(60).concat([lastTask]) },
    globalErrors: [], globalWarnings: many(45).concat([lastGlobal]),
  };
  const dom = fakeDom();
  const saved = { h: globalThis.DBM.h, components: globalThis.DBM.components, fmt: globalThis.DBM.fmt, diff: globalThis.DBM.diff };
  globalThis.DBM.h = dom.h;
  globalThis.DBM.fmt = { num: n => String(n) };
  globalThis.DBM.diff = { lines: () => [] };
  globalThis.DBM.components = {
    kpi: (label, value) => dom.h('div', { class: 'kpi' }, label, value),
    emptyState: (title) => dom.h('div', { class: 'empty' }, title),
    reviewBar: () => dom.h('div', { class: 'review-bar' }),
  };
  try {
    const { ctx, plan } = sampleCtx();
    // whichever task the view has selected (view state outlives a render), its detail carries the long list
    Object.keys(plan.tasks).forEach(id => { plan.tasks[id].warnings = many(50).concat([lastPlanTask]); });
    const posted = Promise.resolve(report);
    ctx.api = Object.assign({}, ctx.api, { post: () => posted });
    globalThis.DBM.views.sql.render(dom.root, ctx);
    const validate = dom.all(dom.root, n => n.tag === 'button' && dom.text(n) === 'Validate live')[0];
    validate.attrs.on.click();
    await posted;
    await new Promise(r => setImmediate(r));

    const shown = dom.text(dom.root);
    assert.ok(shown.includes('Live validation'), 'the report card is rendered');
    assert.ok(shown.includes(lastGlobal), 'last global warning');
    assert.ok(shown.includes(lastTask), 'last task warning in the report');
    assert.ok(shown.includes(lastPlanTask), 'last warning in the task detail');
    const items = dom.all(dom.root, n => n.tag === 'li').map(dom.text);
    assert.equal(items.filter(t => /^(plan: )?warning \d+:/.test(t)).length, 45 + 60 + 60 + 50, 'every warning is its own list item');
    assert.ok(!/\bmore\b/.test(shown), 'no "… and N more" fold');
  } finally {
    Object.assign(globalThis.DBM, saved);
    globalThis.DBM.views.sql.leave();
  }
});

test('the view is registered', () => {
  assert.equal(globalThis.DBM.views.sql.title, 'SQL');
  assert.equal(typeof globalThis.DBM.views.sql.render, 'function');
});
