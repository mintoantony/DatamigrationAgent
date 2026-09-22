'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
vm.runInThisContext(fs.readFileSync(path.join(JS, 'lib', 'highlight.js'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(JS, 'lib', 'diff.js'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(JS, 'views', 'sql.js'), 'utf8'));
const V = globalThis.DBM.sqlView;
const REAL_DIFF = globalThis.DBM.diff;

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

/* ---- the OTHER numbering rule: the global pre/post card (Ruling 80) ----
   A task listing numbers task fields; the global card is one codeBlock over the statements joined by a GO line, so nothing above
   pins it. The engine (SqlValidator.FindBareCarriageReturns) reports a bare CR at a line of THIS card, and a reviewer reads that
   number off the screen. Both halves assert the same fixture: here over the shipped joinStatements/cardLines, in C# over
   TaskListing.Build of the joined text and FindBareCarriageReturns. */
const MIN_GLOBAL_CARD_CASES = 6;
assert.ok(Array.isArray(FIXTURE.globalCard) && FIXTURE.globalCard.length >= MIN_GLOBAL_CARD_CASES,
  'fixture globalCard cases missing: the mirror tests below would not register');

for (const c of FIXTURE.globalCard) {
  test('global card numbering mirrors the engine (shared fixture): ' + c.name, () => {
    const joined = V.joinStatements(c.statements);
    const lines = V.cardLines(joined);
    assert.equal(lines.length, c.lineCount, 'card line count');
    assert.deepEqual(lines.map((t, i) => (/\r/.test(t) ? i + 1 : 0)).filter(Boolean), c.crLines, 'card lines holding a bare CR');
    // codeBlock puts sqlLines(text)[i] beside card line i+1, so the two must split the joined text identically.
    assert.equal(globalThis.DBM.highlight.sqlLines(joined).length, c.lineCount, 'sqlLines split differently from cardLines');
    // 'reported' is the engine's answer, asserted for real on the C# side. Here it is held to the card it must point into:
    // one entry per lone CR in the statements, every entry a real card line, and every CR the card SHOWS reported.
    const loneCrs = c.statements.reduce((n, s) => n + (String(s).match(/\r(?!\n)/g) || []).length, 0);
    assert.equal(c.reported.length, loneCrs, 'one reported line per lone CR in the statements');
    for (const n of c.reported) assert.ok(n >= 1 && n <= c.lineCount, 'reported line ' + n + ' is not a card line');
    for (const n of c.crLines) assert.ok(c.reported.indexOf(n) >= 0, 'a CR visible on card line ' + n + ' must be reported');
  });
}

test('the shared fixture holds at least the expected number of cases', () => {
  assert.ok(Array.isArray(FIXTURE.listing) && FIXTURE.listing.length >= MIN_LISTING_CASES, 'listing cases: ' + (FIXTURE.listing || []).length);
  assert.ok(Array.isArray(FIXTURE.anchors) && FIXTURE.anchors.length >= MIN_ANCHOR_CASES, 'anchor cases: ' + (FIXTURE.anchors || []).length);
  assert.ok(Array.isArray(FIXTURE.globalCard) && FIXTURE.globalCard.length >= MIN_GLOBAL_CARD_CASES, 'global card cases: ' + (FIXTURE.globalCard || []).length);
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
  return html.replace(/<span class="tok-cr" title="[^"]*">␍<\/span>/g, '\r').replace(/<span class="tok-[a-z]+">/g, '').replace(/<\/span>/g, '')
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
  assert.equal(flat[2].html, '<span class="tok-kw">FROM</span> t<span class="tok-cr" title="bare carriage return: SQL Server treats this as a line break">␍</span>');
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
  assert.deepEqual(V.reportSummary(clean), { state: 'clean', errors: 0, warnings: 0, globalCoverage: 'checked', taskCoverage: 'checked' });

  const noGlobal = { version: 3, ok: true, taskErrors: {}, taskWarnings: {}, globalErrors: [] };
  assert.deepEqual(V.reportSummary(noGlobal), { state: 'unreported', errors: 0, warnings: 0, globalCoverage: 'unreported', taskCoverage: 'checked' });
  assert.equal(V.reportSummary(Object.assign({}, noGlobal, { globalWarnings: null })).state, 'unreported');

  const notChecked = Object.assign({}, clean, { globalWarnings: ['global preSql/postSql: not checked: single-task validation'] });
  assert.deepEqual(V.reportSummary(notChecked), { state: 'warnings', errors: 0, warnings: 1, globalCoverage: 'checked', taskCoverage: 'checked' });

  const taskWarn = Object.assign({}, clean, { taskWarnings: { T01: ['mergeSql: not checked: x', 'y'] } });
  assert.equal(V.reportSummary(taskWarn).warnings, 2);
  assert.equal(V.reportSummary(taskWarn).state, 'warnings');

  const bad = Object.assign({}, clean, { ok: false, taskErrors: { T01: ['e'] }, globalErrors: ['g'] });
  assert.deepEqual(V.reportSummary(bad), { state: 'errors', errors: 2, warnings: 0, globalCoverage: 'checked', taskCoverage: 'checked' });
  assert.equal(V.reportSummary(Object.assign({}, clean, { ok: false })).state, 'errors');
});

/* F4: the same absence rule for the task lists. Present in the contracted shape ({taskId: string[]}) or not reported. */
test('reportSummary: a missing or misshapen taskErrors or taskWarnings means task coverage is not reported, never clean', () => {
  const clean = { version: 3, ok: true, taskErrors: { T01: [] }, taskWarnings: { T01: [] }, globalErrors: [], globalWarnings: [] };
  for (const field of ['taskErrors', 'taskWarnings']) {
    for (const bad of [undefined, null, [], 'x', { T01: null }]) {
      const r = Object.assign({}, clean, { [field]: bad });
      if (bad === undefined) delete r[field];
      const s = V.reportSummary(r);
      assert.equal(s.taskCoverage, 'unreported', field + ' = ' + JSON.stringify(bad));
      assert.equal(s.state, 'unreported', field + ' = ' + JSON.stringify(bad));
    }
  }
  const g = Object.assign({}, clean);
  delete g.globalWarnings;
  assert.equal(V.reportSummary(g).state, 'unreported');
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
    let value = '';
    const n = {
      tag, attrs: attrs || {}, children: [], disabled: !!(attrs && attrs.disabled),
      // A browser <textarea> normalises line breaks on assignment: "\r\n" and a lone "\r" both read back as "\n".
      get value() { return value; },
      set value(v) { value = tag === 'textarea' ? String(v).replace(/\r\n?/g, '\n') : String(v); },
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

/* Every render test starts from a fresh view (leave() resets all view state, F6) and cleans up after itself, sync or async. */
async function withFakeDom(fn) {
  const dom = fakeDom();
  const saved = { h: globalThis.DBM.h, components: globalThis.DBM.components, fmt: globalThis.DBM.fmt, diff: globalThis.DBM.diff };
  globalThis.DBM.views.sql.leave();
  globalThis.DBM.h = dom.h;
  globalThis.DBM.fmt = { num: n => String(n) };
  globalThis.DBM.diff = REAL_DIFF;
  globalThis.DBM.components = {
    kpi: (label, value) => dom.h('div', { class: 'kpi' }, label, value),
    emptyState: (title) => dom.h('div', { class: 'empty' }, title),
    reviewBar: () => dom.h('div', { class: 'review-bar' }),
  };
  try { return await fn(dom); } finally { Object.assign(globalThis.DBM, saved); globalThis.DBM.views.sql.leave(); }
}

const tick = () => new Promise(r => setImmediate(r));
const click = (dom, pred) => {
  const b = dom.all(dom.root, n => (n.tag === 'button' || n.tag === 'a') && pred(dom.text(n)))[0];
  assert.ok(b, 'button not found');
  b.attrs.on.click();
};
const many = n => Array.from({ length: n }, (_, i) => 'warning ' + (i + 1) + ': varchar(500) -> nvarchar(200)');
const classes = (dom) => dom.all(dom.root, n => n.attrs && typeof n.attrs.class === 'string').map(n => n.attrs.class);

async function renderValidated(dom, report, overrides) {
  const s = sampleCtx(overrides);
  const posted = Promise.resolve(report);
  s.ctx.api = Object.assign({}, s.ctx.api, { post: () => posted });
  globalThis.DBM.views.sql.render(dom.root, s.ctx);
  click(dom, t => t === 'Validate live');
  await posted;
  await tick();
  return s;
}

test('render: every listing line of the selected task is commentable as sql:<id>:<line>, the header as task:<id>', async () => {
  await withFakeDom(dom => {
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

test('render: read-only hides Edit SQL; a standalone export (api null) has no server controls at all', async () => {
  await withFakeDom(dom => {
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

test('render: a task warning that says "not checked" is shown verbatim', async () => {
  await withFakeDom(dom => {
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
  const lastGlobal = 'global preSql/postSql: not checked: single-task validation';
  const lastTask = 'T02: mergeSql: not checked: depends on an object created by an earlier task';
  const lastPlanTask = 'postSql: not checked: statement 40';
  const report = {
    version: 2, ok: true, taskErrors: { T01: [], T02: [] },
    taskWarnings: { T01: many(60), T02: many(60).concat([lastTask]) },
    globalErrors: [], globalWarnings: many(45).concat([lastGlobal]),
  };
  await withFakeDom(async dom => {
    const s = sampleCtx();
    s.plan.tasks.T01.warnings = many(50).concat([lastPlanTask]);   // T01 is selected on a fresh view
    const posted = Promise.resolve(report);
    s.ctx.api = Object.assign({}, s.ctx.api, { post: () => posted });
    globalThis.DBM.views.sql.render(dom.root, s.ctx);
    click(dom, t => t === 'Validate live');
    await posted;
    await tick();

    const shown = dom.text(dom.root);
    assert.ok(shown.includes('Live validation'), 'the report card is rendered');
    assert.ok(shown.includes(lastGlobal), 'last global warning');
    assert.ok(shown.includes(lastTask), 'last task warning in the report');
    assert.ok(shown.includes(lastPlanTask), 'last warning in the task detail');
    const items = dom.all(dom.root, n => n.tag === 'li').map(dom.text);
    assert.equal(items.filter(t => /^(plan: )?warning \d+:/.test(t)).length, 45 + 60 + 60 + 50, 'every warning is its own list item');
    assert.ok(!/\bmore\b/.test(shown), 'no "… and N more" fold');
  });
});

/* F3 / P5: plan-level warnings (where stored "validate: … not checked" lines live between live runs) never fold either. */
test('render: Plan notes show every plan warning, the last "not checked" line included', async () => {
  const last = 'validate: global postSql: not checked: statement 41 depends on an object created by a task';
  await withFakeDom(dom => {
    const s = sampleCtx();
    s.plan.warnings = many(40).concat([last]);
    s.plan.errors = many(12).map(w => 'error ' + w);
    globalThis.DBM.views.sql.render(dom.root, s.ctx);
    const card = dom.all(dom.root, n => n.tag === 'section' && dom.text(n).startsWith('Plan notes'))[0];
    assert.ok(card, 'Plan notes card');
    assert.ok(dom.text(card).includes(last));
    const items = dom.all(card, n => n.tag === 'li').map(dom.text);
    assert.equal(items.length, 41 + 12);
    assert.equal(items[items.length - 1], last);
  });
});

/* F3 / P4 + F4: a response that does not say what it checked renders no clean state anywhere in the DOM. */
for (const missing of ['globalWarnings', 'taskErrors', 'taskWarnings']) {
  test('render: a validation response without ' + missing + ' shows "coverage not reported" and no clean state', async () => {
    await withFakeDom(async dom => {
      const report = { version: 2, ok: true, taskErrors: { T01: [], T02: [] }, taskWarnings: { T01: [], T02: [] }, globalErrors: [], globalWarnings: [] };
      delete report[missing];
      await renderValidated(dom, report);
      const shown = dom.text(dom.root);
      assert.ok(shown.includes('Live validation'), 'the report card is rendered');
      assert.ok(!classes(dom).some(c => /\bst-approved\b|\bis-clean\b/.test(c)), 'no green badge or clean card: ' + classes(dom).filter(c => /st-|is-/.test(c)));
      assert.ok(!/were checked|no errors or warnings/i.test(shown), 'no "checked" claim');
      assert.ok(shown.includes('coverage not reported'), 'badge or row says coverage not reported');
      const rows = dom.all(dom.root, n => n.tag === 'li').map(dom.text);
      const expectedRow = missing === 'globalWarnings' ? 'plan: global statements: coverage not reported' : 'tasks: task coverage not reported';
      assert.ok(rows.some(t => t.startsWith(expectedRow)), 'row "' + expectedRow + '" in ' + JSON.stringify(rows));
    });
  });
}

test('render: a complete, empty response is the only clean state', async () => {
  await withFakeDom(async dom => {
    await renderValidated(dom, { version: 2, ok: true, taskErrors: { T01: [], T02: [] }, taskWarnings: { T01: [], T02: [] }, globalErrors: [], globalWarnings: [] });
    assert.ok(classes(dom).some(c => /\bst-approved\b/.test(c)));
    assert.ok(dom.text(dom.root).includes('Every task and the global statements were checked'));
  });
});

/* F2: a verdict is only ever shown on the version it is about. */
test('render: a validation that resolves after a newer version is on screen is discarded', async () => {
  await withFakeDom(async dom => {
    let resolve;
    const pending = new Promise(r => { resolve = r; });
    const v1 = sampleCtx({ version: 1, latestVersion: 1, versions: [{ version: 1, author: 'script' }] });
    v1.ctx.artifact = { version: 1, payload: v1.plan };
    v1.ctx.api = Object.assign({}, v1.ctx.api, { post: () => pending });
    globalThis.DBM.views.sql.render(dom.root, v1.ctx);
    click(dom, t => t === 'Validate live');

    globalThis.DBM.views.sql.render(dom.root, sampleCtx().ctx);   // the agent saved v2 meanwhile
    resolve({ version: 1, ok: true, taskErrors: { T01: [], T02: [] }, taskWarnings: { T01: [], T02: [] }, globalErrors: [], globalWarnings: [] });
    await pending;
    await tick();

    const shown = dom.text(dom.root);
    assert.ok(!shown.includes('Live validation'), 'no v1 report on the v2 screen');
    assert.ok(!shown.includes('were checked'));
  });
});

test('render: a report for another version than the one shown is not displayed (viewing an older version)', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx({ version: 1, readOnly: true });
    s.ctx.artifact = { version: 1, payload: s.plan };
    const posted = Promise.resolve({ version: 2, ok: true, taskErrors: {}, taskWarnings: {}, globalErrors: [], globalWarnings: [] });
    s.ctx.api = Object.assign({}, s.ctx.api, { post: () => posted });
    globalThis.DBM.views.sql.render(dom.root, s.ctx);
    click(dom, t => t === 'Validate live');
    await posted;
    await tick();
    assert.ok(!dom.text(dom.root).includes('Live validation'));
  });
});

/* Open item 13: a revalidation of the same version that fails keeps the earlier verdict, marked as from an earlier check, and says
   the latest attempt failed - and the next successful check clears that mark. */
test('render: a failed revalidation of the same version keeps the earlier card, marked as earlier, with the failure', async () => {
  const clean = { version: 2, ok: true, taskErrors: { T01: [], T02: [] }, taskWarnings: { T01: [], T02: [] }, globalErrors: [], globalWarnings: [] };
  await withFakeDom(async dom => {
    const s = await renderValidated(dom, clean);
    assert.ok(!/earlier check/.test(dom.text(dom.root)), 'a fresh verdict is not marked as earlier');

    const failed = Promise.reject(Object.assign(new Error('The db-migrate server is not reachable.'), { code: 'network' }));
    failed.catch(() => {});
    s.ctx.api.post = () => failed;
    click(dom, t => t === 'Validate live');
    await failed.catch(() => {});
    await tick();

    const shown = dom.text(dom.root);
    assert.ok(shown.includes('Every task and the global statements were checked'), 'the earlier verdict on this same version is kept');
    assert.ok(/from an earlier check/.test(shown) && /latest validation.*failed/i.test(shown) && shown.includes('not reachable'),
      'after a failed revalidation of v2 the screen must say the card is from an earlier check and that the latest attempt failed '
      + '(with why); it shows: ' + shown.slice(0, 400));

    const ok = Promise.resolve(clean);
    s.ctx.api.post = () => ok;
    click(dom, t => t === 'Validate live');
    await ok;
    await tick();
    assert.ok(!/earlier check|latest validation.*failed/i.test(dom.text(dom.root)), 'a successful check clears the failure mark');
  });
});

/* F2 + F6: leave() resets every piece of view state — the report, the selected task, editing. */
test('render: leaving and returning shows no report and the default task, whatever was selected before', async () => {
  await withFakeDom(async dom => {
    await renderValidated(dom, { version: 2, ok: true, taskErrors: { T01: [], T02: [] }, taskWarnings: { T01: [], T02: [] }, globalErrors: [], globalWarnings: [] });
    assert.ok(dom.text(dom.root).includes('Live validation'));
    click(dom, t => /app\.Orders/.test(t));
    globalThis.DBM.views.sql.leave();

    const back = sampleCtx();
    globalThis.DBM.views.sql.render(dom.root, back.ctx);
    assert.ok(!dom.text(dom.root).includes('Live validation'), 'report cleared by leave()');
    assert.equal(back.anchors[0], 'task:T01', 'selection cleared by leave()');
  });
});

/* ------------------------------------------------------------------ F5: editing never rewrites what the user did not touch */

function applyOps(task, ops) {
  const out = JSON.parse(JSON.stringify(task));
  for (const o of ops) {
    const field = o.path.split('/')[3];
    if (o.op === 'remove') delete out[field]; else out[field] = o.value;
  }
  return out;
}

function openEditor(dom, s) {
  globalThis.DBM.views.sql.render(dom.root, s.ctx);
  click(dom, t => t === 'Edit SQL');
  return dom.all(dom.root, n => n.tag === 'textarea');
}

const areaFor = (dom, label) => dom.all(dom.root, n => n.tag === 'textarea' && n.attrs['aria-label'].startsWith(label))[0];

test('editor: a field holding a bare CR is read-only with a note; opening the editor marks nothing dirty', async () => {
  await withFakeDom(dom => {
    const s = sampleCtx();
    const t = s.plan.tasks.T01;
    t.sourceQuery = 'SELECT a -- note\rDELETE FROM app.Customers\r\nFROM t';
    t.preSql = ['/* keep\nGO\n*/ ALTER X;', ' '];
    t.postSql = ['UPDATE STATISTICS [app].[Addresses];'];
    let confirms = 0;
    const savedConfirm = globalThis.confirm;
    globalThis.confirm = () => { confirms++; return true; };
    try {
      openEditor(dom, s);
      assert.equal(areaFor(dom, 'Source query'), undefined, 'no textarea for the CR field');
      assert.ok(dom.text(dom.root).includes('contains a bare carriage return; edit it through the agent'));
      assert.equal(areaFor(dom, 'Task pre-load'), undefined, 'the GO-in-comment list does not round-trip: read-only (N1)');
      assert.ok(areaFor(dom, 'Task post-load'), 'a round-tripping list stays editable');

      const newer = sampleCtx({ version: 3, latestVersion: 3 }).ctx;
      assert.equal(globalThis.DBM.views.sql.holdRender(newer), false, 'an untouched editor is not dirty');
      click(dom, x => /app\.Orders/.test(x));
      assert.equal(confirms, 0, 'no discard prompt for an untouched editor');
    } finally { globalThis.confirm = savedConfirm; }
  });
});

test('editor: Save sends only the field the user edited — no evidence fields, and every other listing line byte-identical', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    const t = s.plan.tasks.T02;
    t.preSql = ['/* keep\nGO\n*/ ALTER X;', ' ', 'SET A\r\n  ON;'];
    t.postSql = ['P;\n'];
    t.stagingDdl = 'CREATE TABLE #stg (a int);\r\n';
    t.custom = true;
    t.errors = ['e'];
    let body = null;
    s.ctx.api = Object.assign({}, s.ctx.api, { post: (url, b) => { body = { url, b }; return new Promise(() => {}); } });
    globalThis.DBM.views.sql.render(dom.root, s.ctx);
    click(dom, x => /app\.Orders/.test(x));
    click(dom, x => x === 'Edit SQL');

    const src = areaFor(dom, 'Source query');
    src.value = 'SELECT 2 AS [a]';
    src.attrs.on.input();
    const form = dom.all(dom.root, n => n.tag === 'form')[0];
    form.attrs.on.submit({ preventDefault() {} });

    assert.ok(body, 'posted');
    assert.equal(body.url, '/api/edit/sql');
    assert.deepEqual(body.b.ops, [{ op: 'replace', path: '/tasks/T02/sourceQuery', value: 'SELECT 2 AS [a]' }]);
    const json = JSON.stringify(body.b);
    assert.ok(!/custom|"errors"|"warnings"|\/errors|\/warnings/.test(json), json);
    assert.deepEqual(Object.keys(body.b).sort(), ['baseVersion', 'ops', 'phase', 'responses', 'summary']);

    const before = V.listing(t).filter(l => l.section !== 'source');
    const after = V.listing(applyOps(t, body.b.ops)).filter(l => l.section !== 'source');
    assert.deepEqual(after.map(l => [l.section, l.text]), before.map(l => [l.section, l.text]));
  });
});

/* N1 (Ruling 70): edits are lossless. A list that does not survive splitStatements(joinStatements(list)) element for element is
   read-only; a net no-op edit is not dirty and sends nothing; a real edit to one statement leaves its siblings byte-identical. */
const LIST_NOTE = "this statement list can't be edited as one text without changing it; edit it through the agent";

function editorFor(dom, s, id) {
  globalThis.DBM.views.sql.render(dom.root, s.ctx);
  if (id !== 'T01') click(dom, x => new RegExp(s.plan.tasks[id].target.replace('.', '\\.')).test(x));
  click(dom, x => x === 'Edit SQL');
}

const type = (area, value) => { area.value = value; area.attrs.on.input(); };
const submit = dom => dom.all(dom.root, n => n.tag === 'form')[0].attrs.on.submit({ preventDefault() {} });

test('listRoundTrips: only a list that splitStatements(joinStatements(list)) gives back exactly', () => {
  assert.equal(V.listRoundTrips([]), true);
  assert.equal(V.listRoundTrips(undefined), true);
  assert.equal(V.listRoundTrips(['A;', 'UPDATE STATISTICS [a].[b];']), true);
  assert.equal(V.listRoundTrips(['/* keep\nGO\n*/ ALTER X;']), false, 'GO line inside a comment');
  assert.equal(V.listRoundTrips(['A;', ' ']), false, 'whitespace-only statement');
  assert.equal(V.listRoundTrips(['A;\n']), false, 'padded statement');
  assert.equal(V.listRoundTrips(['SET A\r\n  ON;']), false, 'CRLF');
  assert.equal(V.listRoundTrips(['']), false, 'empty statement');
});

test('N1: the re-reviewer\'s list (GO inside a comment, a whitespace statement) is read-only and a save sends no op for it', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    const t = s.plan.tasks.T01;
    t.preSql = ['/* keep\nGO\n*/ ALTER X;', ' '];
    t.postSql = ['A;', 'B;'];
    const posts = [];
    const toasts = [];
    s.ctx.toast = (m) => toasts.push(m);
    s.ctx.api = Object.assign({}, s.ctx.api, { post: (url, b) => { posts.push(b); return new Promise(() => {}); } });
    editorFor(dom, s, 'T01');

    assert.equal(areaFor(dom, 'Task pre-load'), undefined, 'no textarea for a list that does not round-trip');
    assert.ok(dom.text(dom.root).includes(LIST_NOTE));
    assert.ok(dom.text(dom.root).includes('/* keep'), 'the list is still shown');

    submit(dom);
    assert.equal(posts.length, 0, 'an untouched form sends nothing');
    type(areaFor(dom, 'Source query'), 'SELECT 1 AS [AddressId] FROM [dbo].[ADDR] AS s');
    submit(dom);
    assert.equal(posts.length, 1);
    assert.deepEqual(posts[0].ops.map(o => o.path), ['/tasks/T01/sourceQuery'], 'no preSql op');
  });
});

test('N1: typing x then Backspace in a round-tripping list is a no-op — not dirty, nothing sent', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    const t = s.plan.tasks.T01;
    t.preSql = ['ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];', 'UPDATE STATISTICS [a].[b];'];
    const posts = [];
    let confirms = 0;
    const savedConfirm = globalThis.confirm;
    globalThis.confirm = () => { confirms++; return true; };
    s.ctx.api = Object.assign({}, s.ctx.api, { post: (url, b) => { posts.push(b); return new Promise(() => {}); } });
    try {
      editorFor(dom, s, 'T01');
      const pre = areaFor(dom, 'Task pre-load');
      assert.ok(pre, 'a round-tripping list is editable');
      const opened = pre.value;
      type(pre, opened + 'x');
      type(pre, opened);
      assert.equal(globalThis.DBM.views.sql.holdRender(sampleCtx({ version: 3, latestVersion: 3 }).ctx), false, 'a net no-op is not dirty');
      submit(dom);
      assert.equal(posts.length, 0, 'a net no-op sends nothing');
      click(dom, x => /app\.Orders/.test(x));
      assert.equal(confirms, 0, 'no discard prompt after a net no-op');
    } finally { globalThis.confirm = savedConfirm; }
  });
});

test('N1: a real edit to one statement of a round-tripping list leaves its siblings byte-identical', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    const t = s.plan.tasks.T01;
    t.preSql = ['ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];', 'SELECT 1 /* a comment */;', 'UPDATE STATISTICS [a].[b];'];
    let body = null;
    s.ctx.api = Object.assign({}, s.ctx.api, { post: (url, b) => { body = b; return new Promise(() => {}); } });
    editorFor(dom, s, 'T01');
    const pre = areaFor(dom, 'Task pre-load');
    type(pre, pre.value.replace('SELECT 1 /* a comment */;', 'SELECT 2;'));
    submit(dom);
    assert.deepEqual(body.ops, [{ op: 'replace', path: '/tasks/T01/preSql', value: [t.preSql[0], 'SELECT 2;', t.preSql[2]] }]);
    const before = V.listing(t);
    const after = V.listing(applyOps(t, body.ops));
    assert.deepEqual(after.filter(l => !/SELECT/.test(l.text)).map(l => l.text), before.filter(l => !/SELECT/.test(l.text)).map(l => l.text));
    assert.equal(after.length, before.length, 'no line numbers shift');
  });
});

/* Open item 14, Ruling 205 (fallback): the engine has no statement splitter to mirror - it refuses ANY GO line inside a statement,
   comment or not (SqlValidator.GoLine) - so a GO line typed inside a comment or string cannot be stored in any form. The browser
   names such a line before posting instead of cutting the statement there. */
test('goSplitHazards names the GO separator lines that fall inside a comment or a string, and only those', () => {
  assert.deepEqual(V.goSplitHazards('/* note\nGO\n*/ ALTER X;'), [2], 'a GO line inside a block comment is not named');
  assert.deepEqual(V.goSplitHazards("SELECT 'a\n  go  \nb';"), [2], 'a GO line inside a string is not named');
  assert.deepEqual(V.goSplitHazards('/* outer /* inner */\nGO\n*/\nA;\nGO\nB;'), [2], 'nested comment: line 2 inside, line 5 a real separator');
  assert.deepEqual(V.goSplitHazards('A; -- a line comment ends here\nGO\nB;'), []);
  assert.deepEqual(V.goSplitHazards('A;\r\nGO\r\nB;'), []);
  assert.deepEqual(V.goSplitHazards('/* a */\nGO\nB;'), []);
  assert.deepEqual(V.goSplitHazards(''), []);
});

test('editor: a GO line typed inside a comment is named before posting, and nothing is sent', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    const posts = [];
    s.ctx.api = Object.assign({}, s.ctx.api, { post: (url, b) => { posts.push(b); return new Promise(() => {}); } });
    editorFor(dom, s, 'T01');
    type(areaFor(dom, 'Task pre-load'), 'ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];\nGO\n/* disabled for the load\nGO\n*/ UPDATE STATISTICS [a].[b];');
    submit(dom);
    const problems = dom.all(dom.root, n => n.attrs && n.attrs.class === 'sql-edit-problems')[0];
    assert.equal(posts.length, 0, 'the editor posted a statement list that splits inside a comment: ' + JSON.stringify(posts[0] && posts[0].ops));
    assert.match(dom.text(problems), /Task pre-load, line 4: this GO line is inside a comment or string/);

    type(areaFor(dom, 'Task pre-load'), 'ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];\nGO\n/* disabled for the load */ UPDATE STATISTICS [a].[b];');
    submit(dom);
    assert.equal(posts.length, 1, 'a clean list is posted');
    assert.deepEqual(posts[0].ops[0].value, ['ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];', '/* disabled for the load */ UPDATE STATISTICS [a].[b];']);
  });
});

/* F5: the diff compares listings line for line by the TaskListing rule, so a lone CR vs LF (and CR CR LF) is a difference. */
test('diffRows: lone CR vs LF and CR CR LF are differences, shown with a visible CR', () => {
  const rows = V.diffRows({ sourceQuery: 'a\rb' }, { sourceQuery: 'a\nb' });
  assert.deepEqual(rows, [{ op: 'del', text: 'a␍b' }, { op: 'add', text: 'a' }, { op: 'add', text: 'b' }]);
  assert.deepEqual(V.diffRows({ sourceQuery: 'x\r\r\ny' }, { sourceQuery: 'x\r\ny' }),
    [{ op: 'del', text: 'x␍' }, { op: 'add', text: 'x' }, { op: 'eq', text: 'y' }]);
  assert.deepEqual(V.diffRows(null, { sourceQuery: 'q' }), [{ op: 'add', text: 'q' }]);
  assert.deepEqual(V.diffRows({ sourceQuery: 'q\r\n' }, { sourceQuery: 'q\n' }), [{ op: 'eq', text: 'q' }, { op: 'eq', text: '' }]);
});

test('render: Compare with… on versions differing only in a lone CR shows the difference', async () => {
  await withFakeDom(async dom => {
    const s = sampleCtx();
    s.plan.tasks.T01.sourceQuery = 'SELECT 1\nFROM t';
    const old = JSON.parse(JSON.stringify(s.plan));
    old.tasks.T01.sourceQuery = 'SELECT 1\rFROM t';
    const got = Promise.resolve({ version: 1, payload: old });
    s.ctx.api = Object.assign({}, s.ctx.api, { get: () => got });
    globalThis.DBM.views.sql.render(dom.root, s.ctx);
    const picker = dom.all(dom.root, n => n.tag === 'select')[0];
    picker.attrs.on.change({ target: { value: '1' } });
    await got;
    await tick();
    const shown = dom.text(dom.root);
    assert.ok(!shown.includes('No differences'), 'a lone CR vs LF is a difference');
    assert.ok(shown.includes('SELECT 1␍FROM t'));
  });
});

/* Open item 10, Ruling 204: the screen states the same fact ApprovalBlockers and `dbm artifact sql` do, from the same stored field -
   positive evidence ("validation": {at, ok}) or NOT validated, whatever the warnings say. */
const CS_MODULE = fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'Core', 'SqlGen', 'SqlModule.cs'), 'utf8');
const NO_EVIDENCE = /public const string NoEvidence = "([^"]*)";/.exec(CS_MODULE)[1];

test('validationState mirrors SqlPlanPayload.NotValidatedReasons', () => {
  const skipped = 'live validation skipped: source connection, target connection missing';
  const at = '2026-09-18T09:30:00+00:00';
  assert.deepEqual(V.validationState({ warnings: [], validation: { at, ok: true } }), { validated: true, at, ok: true, reasons: [] });
  assert.deepEqual(V.validationState({ warnings: [] }), { validated: false, at: null, ok: null, reasons: [NO_EVIDENCE] },
    'no evidence and no marker (an old record) is NOT validated, with the engine\'s own sentence');
  assert.deepEqual(V.validationState({ warnings: ['x', skipped] }).reasons, [skipped]);
  assert.equal(V.validationState({ warnings: [skipped], validation: { at, ok: true } }).validated, false, 'evidence plus a marker is refused, as in C#');
});

test('render: a version with no validation evidence says Not validated, and a validated one says when', async () => {
  await withFakeDom(dom => {
    const s = sampleCtx();
    globalThis.DBM.views.sql.render(dom.root, s.ctx);   // sampleCtx's plan has no "validation" field
    let shown = dom.text(dom.root);
    assert.ok(shown.includes('Not validated') && shown.includes(NO_EVIDENCE),
      'a version the engine refuses to approve as not validated looks validated on screen: ' + shown.slice(0, 300));

    const ok = sampleCtx();
    ok.plan.validation = { at: '2026-09-18T09:30:00+00:00', ok: true };
    globalThis.DBM.views.sql.render(dom.root, ok.ctx);
    shown = dom.text(dom.root);
    assert.ok(shown.includes('Validated live 2026-09-18T09:30:00+00:00'), shown.slice(0, 300));
    assert.ok(!shown.includes('Not validated'));
  });
});

test('the view is registered', () => {
  assert.equal(globalThis.DBM.views.sql.title, 'SQL');
  assert.equal(typeof globalThis.DBM.views.sql.render, 'function');
});
