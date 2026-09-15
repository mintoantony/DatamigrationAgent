'use strict';
// Finding A1 (task 3.5): unsaved mapping edits must survive re-renders. These tests load the real app.js shell and the real
// mapping view over a minimal fake DOM and a stub API/SSE, then drive them the way the browser does.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');

/* ------------------------------------------------------------------ fake DOM */

class FakeNode {
  constructor(tag) {
    this.tagName = tag;
    this.children = [];
    this.attributes = {};
    this.dataset = {};
    this.listeners = {};
    this.className = '';
    this.parentNode = null;
    this.text = '';
    this.hidden = false;
    this.disabled = false;
    this.value = '';
    this.style = { setProperty() {} };
    this.classList = { add() {}, remove() {}, toggle() {}, contains() { return false; } };
  }
  appendChild(c) { c.parentNode = this; this.children.push(c); return c; }
  removeChild(c) { this.children.splice(this.children.indexOf(c), 1); c.parentNode = null; return c; }
  get firstChild() { return this.children[0] || null; }
  set textContent(v) { this.children = []; this.text = String(v); }
  get textContent() { return this.text + this.children.map((c) => c.textContent).join(''); }
  setAttribute(k, v) { this.attributes[k] = v; if (k === 'value') this.value = v; }
  getAttribute(k) { return this.attributes[k]; }
  removeAttribute(k) { delete this.attributes[k]; }
  addEventListener(ev, fn) { (this.listeners[ev] = this.listeners[ev] || []).push(fn); }
  removeEventListener() {}
  querySelector() { return null; }
  closest() { return null; }
  scrollIntoView() {}
  focus() {}
  fire(ev) { (this.listeners[ev] || []).forEach((fn) => fn({ target: this, preventDefault() {}, stopPropagation() {} })); }
}

function find(node, pred) {
  if (pred(node)) return node;
  for (const c of node.children) { const hit = find(c, pred); if (hit) return hit; }
  return null;
}

const ids = {};
['app', 'topbar', 'banners', 'stepper', 'view', 'drawer', 'toasts'].forEach((id) => { ids[id] = new FakeNode('div'); });
globalThis.window = globalThis;
globalThis.document = {
  readyState: 'complete',
  title: '',
  activeElement: null,
  documentElement: new FakeNode('html'),
  body: new FakeNode('body'),
  createElement: (tag) => new FakeNode(tag),
  createTextNode: (t) => { const n = new FakeNode('#text'); n.text = t; return n; },
  getElementById: (id) => ids[id] || null,
  addEventListener() {},
  removeEventListener() {},
};
globalThis.location = { hash: '' };
globalThis.history = { replaceState() {} };
globalThis.addEventListener = () => {};
globalThis.scrollY = 0;
globalThis.scrollTo = () => {};
Object.defineProperty(globalThis, 'localStorage', { value: { getItem: () => null, setItem() {} }, configurable: true });

/* ------------------------------------------------------------------ stub server */

function payload() {
  return {
    tables: {
      'app.Customers': {
        kind: 'direct', sources: ['dbo.CUST'], confidence: 1, method: 'exact',
        columns: { Email: { expr: 's.[EMAIL_ADDR]', sourceColumns: ['dbo.CUST.EMAIL_ADDR'], confidence: 1, method: 'exact' } }
      }
    },
    drops: {},
    notes: []
  };
}

function context() {
  return {
    autoAccept: 0.85, candidate: 0.5, blockers: [], attention: [], uncovered: [],
    source: [{ key: 'dbo.CUST', rows: 10, columns: [{ name: 'EMAIL_ADDR', type: 'varchar(120)', nullable: true }] }],
    target: [{ key: 'app.Customers', columns: [
      { name: 'Email', type: 'nvarchar(120)', nullable: true, identity: false, computed: false, rowversion: false, hasDefault: false }] }]
  };
}

const server = { status: 'awaiting_review', version: 3, payloads: { 3: payload() }, feedback: [], posts: [], onEdit: null, agentOnline: true };
let sse = null;
let lastCtx = null;
const toasts = [];
const analysisRenders = [];   // a view without holdRender (stands in for analysis.js / setup.js / pending.js)

function stateDoc() {
  return {
    project: { name: 'p', paused: false, agentOnline: server.agentOnline },
    phases: [
      { name: 'setup', status: 'approved', currentVersion: null, approvedVersion: null },
      { name: 'analysis', status: 'approved', currentVersion: 1, approvedVersion: 1 },
      { name: 'mapping', status: server.status, currentVersion: server.version, approvedVersion: null },
    ],
    jobs: [], connections: { src: 'a', tgt: 'b' }, drift: { src: false, tgt: false }, transfer: null, next: {},
  };
}

function artifact(v) { return { version: v, author: 'agent', summary: 's', createdAt: null, payload: JSON.parse(JSON.stringify(server.payloads[v])) }; }

function get(url) {
  let body;
  if (url === '/api/state') body = stateDoc();
  else if (url === '/api/artifact/mapping') body = { current: artifact(server.version), versions: Object.keys(server.payloads).map((v) => ({ version: Number(v) })) };
  else if (/^\/api\/artifact\/mapping\/\d+$/.test(url)) body = artifact(Number(url.split('/').pop()));
  else if (url === '/api/feedback/mapping') body = server.feedback.slice();
  else if (url.indexOf('/api/mapping/context') === 0) body = context();
  else if (url === '/api/artifact/analysis') body = { current: { version: 1, payload: {} }, versions: [] };
  else if (url === '/api/feedback/analysis') body = [];
  else return Promise.reject(new Error('unexpected GET ' + url));
  return Promise.resolve(body);
}

function post(url, body) {
  server.posts.push({ url, body });
  if (url === '/api/edit/mapping') {
    if (body.baseVersion !== server.version) {
      return Promise.resolve({ ok: false, errors: ['baseVersion ' + body.baseVersion + ' does not match the current version ' + server.version] });
    }
    return server.onEdit(body);
  }
  return Promise.resolve({});
}

function publish(v, p) {
  server.payloads[v] = p;
  server.version = v;
}

/* ------------------------------------------------------------------ load the real UI */

const DBM = (globalThis.DBM = {});
vm.runInThisContext(fs.readFileSync(path.join(JS, 'lib', 'dom.js'), 'utf8'), { filename: 'dom.js' });
const stubComponents = {
  toast: (msg, kind) => { toasts.push({ msg, kind }); },
  errorText: (e) => String(e && e.message),
  reviewBar: (ctx) => { lastCtx = ctx; return DBM.h('div'); },
};
DBM.components = new Proxy(stubComponents, { get: (t, k) => (k in t ? t[k] : () => DBM.h('div')) });
DBM.review = { refresh() {}, openFeedback() {}, openHistory() {} };
DBM.phaseTitle = (n) => n;
DBM.anchorLabel = (a) => String(a);
DBM.api = { get, post, del: () => Promise.resolve({}), events: (onEvent) => { sse = onEvent; } };
DBM.views = {
  setup: { render() {} },
  pending: { render() {} },
  analysis: { title: 'Analysis', render(root, ctx) { analysisRenders.push({ ctx, args: arguments.length }); root.appendChild(DBM.h('div', { class: 'analysis' })); } },
};
vm.runInThisContext(fs.readFileSync(path.join(JS, 'views', 'mapping.js'), 'utf8'), { filename: 'mapping.js' });
vm.runInThisContext(fs.readFileSync(path.join(JS, 'app.js'), 'utf8'), { filename: 'app.js' });

/* ------------------------------------------------------------------ helpers */

const view = ids.view;
function settle(ms) { return new Promise((r) => setTimeout(r, ms || 20)); }
function byText(tag, text) { return find(view, (n) => n.tagName === tag && n.textContent === text); }
function barStatus() {
  const bar = find(view, (n) => /map-savebar/.test(n.className));
  return bar ? bar.children[0].textContent : null;
}
function header() { return find(view, (n) => /muted small/.test(n.className) && /^v/.test(n.textContent)).textContent; }

async function reset() {
  await settle();
  server.status = 'awaiting_review';
  server.payloads = { 3: payload() };
  server.version = 3;
  server.feedback = [];
  server.posts = [];
  server.onEdit = null;
  toasts.length = 0;
  // Leave whatever view the previous test left behind, then come back: every test starts from a first render.
  DBM.app.select('analysis');
  await settle();
  DBM.app.select('mapping');
  await settle();
  assert.equal(barStatus(), 'No unsaved changes', 'test starts from a clean first render');
}

async function editEmail(expr) {
  byText('button', '▸').fire('click');
  const ta = find(view, (n) => n.tagName === 'textarea' && n.attributes['aria-label'] === 'Expression for Email');
  ta.value = expr;
  ta.fire('input');
  assert.equal(barStatus(), '1 unsaved change');
}

async function save() {
  byText('button', 'Save as new version').fire('click');
  await settle();
  return server.posts.filter((p) => p.url === '/api/edit/mapping').pop();
}

const EDIT = { op: 'replace', path: '/tables/app.Customers/columns/Email/expr', value: 'LOWER(s.[EMAIL_ADDR])' };

/* ------------------------------------------------------------------ tests */

test('1. dirty view: posting a comment re-renders the view and the unsaved edits survive', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  const before = lastCtx;
  server.feedback.push({ id: 7, status: 'draft', anchor: null, text: 'hi' });
  await before.refresh();                       // exactly what review.js does after POST /api/feedback
  await settle();
  assert.notEqual(lastCtx, before, 'the view was re-rendered (new feedback row changed the view key)');
  assert.equal(lastCtx.feedback.length, 1, 'the re-render shows the new comment');
  assert.equal(barStatus(), '1 unsaved change');
  server.onEdit = () => Promise.resolve({ ok: true, version: 4, warnings: [] });
  const sent = await save();
  assert.ok(sent, 'Save posted the edits');
  assert.equal(sent.body.baseVersion, 3);
  assert.deepEqual(sent.body.ops.map((o) => o.op + ' ' + o.path + ' ' + (o.value === undefined ? '' : o.value)),
    ['replace /tables/app.Customers/columns/Email/expr LOWER(s.[EMAIL_ADDR])', 'replace /tables/app.Customers/columns/Email/method human']);
});

test('1b. dirty view: deleting a draft comment re-renders and the unsaved edits survive', async () => {
  await reset();
  server.feedback.push({ id: 8, status: 'draft', anchor: null, text: 'x' });
  await lastCtx.refresh();
  await settle();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  server.feedback = [];
  await lastCtx.refresh();                      // review.js:282 after DELETE /api/feedback/{id}
  await settle();
  assert.equal(lastCtx.feedback.length, 0);
  assert.equal(barStatus(), '1 unsaved change');
});

test('2. dirty view: artifact_created keeps the unsaved edits (app.js respects the view) and tells the user', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  const other = payload();
  other.notes = ['agent note'];
  publish(4, other);
  sse({ type: 'artifact_created', data: { phase: 'mapping', version: 4 } });
  await settle(300);                            // past app.js's 150 ms scheduleRefresh
  assert.equal(barStatus(), '1 unsaved change', 'edits survive the scheduled refresh');
  assert.equal(header().split(' · ')[0], 'v3', 'the view stays on the version the edits are based on');
  const warn = toasts.filter((t) => t.kind === 'warn');
  assert.equal(warn.length, 1, 'exactly one warning, not one per background refresh: ' + JSON.stringify(toasts));
  assert.match(warn[0].msg, /v4/);
  assert.doesNotMatch(warn[0].msg, /Save or discard/, 'must not suggest saving: the server rejects a stale baseVersion');

  // Further background refreshes (e.g. agent presence) keep holding, without repeating the warning.
  sse({ type: 'agent_presence', data: {} });
  await settle(300);
  assert.equal(barStatus(), '1 unsaved change');
  assert.equal(toasts.filter((t) => t.kind === 'warn').length, 1);

  // Discard releases the hold and loads the new version.
  byText('button', 'Discard').fire('click');
  await settle();
  assert.equal(header().split(' · ')[0], 'v4');
  assert.equal(barStatus(), 'No unsaved changes');
});

test('2b. a held view is released when the phase stops awaiting review (the edits could never be saved)', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  publish(4, payload());
  sse({ type: 'artifact_created', data: { phase: 'mapping', version: 4 } });
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v3', 'held');
  server.status = 'reworking';                 // e.g. Request changes was clicked while held
  sse({ type: 'state_changed', data: {} });
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v4', 'released to the new, read-only version');
  assert.equal(barStatus(), null);
  assert.ok(toasts.some((t) => /discarded/.test(t.msg)), 'the user is told the edits were dropped');
});

test('2c. the hold is not a latch: after Discard the next background refresh renders the new version', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  publish(4, payload());
  sse({ type: 'artifact_created', data: { phase: 'mapping', version: 4 } });
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v3', 'held while dirty');
  byText('button', 'Discard').fire('click');
  await settle(300);
  sse({ type: 'agent_presence', data: {} });    // an ordinary later refresh, independent of what Discard itself does
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v4', 'no longer dirty, so the refresh goes through');
  assert.equal(barStatus(), 'No unsaved changes');
});

test('7. a view without holdRender re-renders exactly as before on every view-key change', async () => {
  await reset();
  DBM.app.select('analysis');
  await settle();
  const n = analysisRenders.length;
  const first = analysisRenders[n - 1].ctx;
  server.agentOnline = false;                   // part of viewKey
  sse({ type: 'agent_presence', data: {} });
  await settle(300);
  server.agentOnline = true;
  assert.equal(analysisRenders.length, n + 1, 'the key change re-rendered the view');
  assert.notEqual(analysisRenders[n].ctx, first, 'with a fresh ctx');
  assert.equal(analysisRenders[n].ctx.state.project.agentOnline, false);
  assert.equal(view.children.length, 1, 'into a cleared root');
  sse({ type: 'agent_presence', data: {} });
  await settle(300);
  assert.equal(analysisRenders.length, n + 2, 'agentOnline flipped back, so one more render');
  sse({ type: 'agent_presence', data: {} });
  await settle(300);
  assert.equal(analysisRenders.length, n + 2, 'same key: no render');
  DBM.app.select('mapping');
  await settle();
});

test('3. clean view: artifact_created refreshes to the new version as before', async () => {
  await reset();
  const other = payload();
  other.notes = ['agent note'];
  publish(4, other);
  sse({ type: 'artifact_created', data: { phase: 'mapping', version: 4 } });
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v4');
  assert.equal(lastCtx.version, 4);
  assert.equal(barStatus(), 'No unsaved changes');
  assert.equal(toasts.filter((t) => t.kind === 'warn').length, 0);
});

test('4. first render builds work from the payload, even after leaving a dirty mapping view', async () => {
  await reset();
  assert.equal(header().split(' · ')[0], 'v3');
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  DBM.app.select('analysis');                  // navigate away with unsaved edits
  await settle();
  assert.ok(find(view, (n) => n.className === 'analysis'));
  DBM.app.select('mapping');                   // a first render of the mapping view again
  await settle();
  assert.equal(barStatus(), 'No unsaved changes', 'a first render does not inherit a previous view instance\'s work');
  const ta = (byText('button', '▸').fire('click'), find(view, (n) => n.tagName === 'textarea'));
  assert.equal(ta.textContent, 's.[EMAIL_ADDR]');
});

test('5. save then refresh: the new server payload wins and stale edits are not resurrected', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  server.onEdit = (body) => {
    const saved = payload();
    saved.tables['app.Customers'].columns.Email.expr = 'lower(s.[EMAIL_ADDR])';   // server-normalised, differs from `work`
    saved.tables['app.Customers'].columns.Email.method = 'human';
    publish(body.baseVersion + 1, saved);
    sse({ type: 'artifact_created', data: { phase: 'mapping', version: server.version } });   // SSE beats the HTTP response
    return Promise.resolve({ ok: true, version: server.version, warnings: [] });
  };
  await save();
  await settle(300);
  assert.equal(header().split(' · ')[0], 'v4', 'the refresh after save shows the new version');
  assert.equal(barStatus(), 'No unsaved changes', 'the old work is not re-applied over v4');
  assert.equal(toasts.filter((t) => t.kind === 'warn').length, 0, 'our own save is not reported as someone else\'s new version');
});

test('5b. save with a slow response: the refresh for our own artifact_created is held without a warning, then the save loads v4', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  server.onEdit = (body) => {
    const saved = payload();
    saved.tables['app.Customers'].columns.Email.expr = 'lower(s.[EMAIL_ADDR])';
    publish(body.baseVersion + 1, saved);
    sse({ type: 'artifact_created', data: { phase: 'mapping', version: server.version } });
    return new Promise((r) => setTimeout(() => r({ ok: true, version: server.version, warnings: [] }), 400));   // after the 150 ms refresh
  };
  byText('button', 'Save as new version').fire('click');
  await settle(250);
  assert.equal(header().split(' · ')[0], 'v3', 'mid-save the view is not replaced under the in-flight edits');
  assert.equal(toasts.filter((t) => t.kind === 'warn').length, 0, 'no "newer version" warning for our own save');
  await settle(400);
  assert.equal(header().split(' · ')[0], 'v4');
  assert.equal(barStatus(), 'No unsaved changes');
  assert.equal(toasts.filter((t) => t.kind === 'warn').length, 0);
});

test('6. a view that is no longer editable does not get the edits back', async () => {
  await reset();
  await editEmail('LOWER(s.[EMAIL_ADDR])');
  server.status = 'reworking';                  // e.g. Request changes was clicked with unsaved edits
  await lastCtx.refresh();
  await settle();
  assert.equal(barStatus(), null, 'read-only view has no save bar');
  assert.equal(find(view, (n) => n.tagName === 'textarea'), null);
  server.status = 'awaiting_review';            // editable again on the same version: the dropped edits stay dropped
  await lastCtx.refresh();
  await settle();
  assert.equal(barStatus(), 'No unsaved changes');
});
