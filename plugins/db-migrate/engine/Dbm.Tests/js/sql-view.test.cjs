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
      assert.ok(areaFor(dom, 'Task pre-load') && areaFor(dom, 'Task post-load'), 'other fields stay editable');

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

test('the view is registered', () => {
  assert.equal(globalThis.DBM.views.sql.title, 'SQL');
  assert.equal(typeof globalThis.DBM.views.sql.render, 'function');
});
