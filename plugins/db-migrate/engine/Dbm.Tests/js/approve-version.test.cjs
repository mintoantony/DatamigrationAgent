'use strict';
/*
 * Open item 1, Ruling 194: Approve names the version on the reviewer's screen - POST /api/phase/{phase}/approve {"version": n} - and
 * a 409 stale_version (a newer version arrived) is said in so many words and reloads the screen, instead of signing off a version
 * the reviewer never displayed.
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
DBM.phaseTitle = DBM.phaseTitle || ((n) => n);
const C = DBM.components;

function reviewCtx(phase, version, api) {
  const phases = ['setup', 'discovery', 'analysis', 'mapping', 'sql', 'ready', 'transfer', 'complete'].map((name) => ({
    name, status: name === phase ? 'awaiting_review' : 'pending', currentVersion: name === phase ? version : null,
  }));
  const ctx = {
    phase,
    phaseRow: phases.find((p) => p.name === phase),
    state: { phases, transfer: {}, project: { agentOnline: false } },
    feedback: [],
    versions: [{ version, author: 'agent', summary: phase + ' v' + version, createdAt: '2026-09-18T09:00:00+00:00' }],
    latestVersion: version,
    artifact: { version, author: 'agent', summary: phase + ' v' + version },
    readOnly: false,
    api,
    refreshed: 0,
    openFeedback() {}, openHistory() {}, setVersion() {},
    refresh() { ctx.refreshed++; return Promise.resolve(); },
  };
  return ctx;
}

function approveButton(bar) {
  return D.find(bar, (n) => String(n.tagName).toLowerCase() === 'button' && n.textContent === 'Approve');
}

/** Captures toasts and modals instead of drawing them (their timers would hold the test process open). */
function capture() {
  const seen = { toasts: [], modals: [] };
  const saved = { toast: C.toast, modal: C.modal };
  C.toast = (message, kind) => seen.toasts.push({ message: String(message), kind });
  C.modal = (o) => { seen.modals.push(o); return Promise.resolve(true); };
  seen.restore = () => Object.assign(C, saved);
  return seen;
}

for (const phase of ['analysis', 'mapping', 'sql']) {
  test(phase + ': Approve sends the version on screen', async () => {
    const posts = [];
    const api = { post: (url, body) => { posts.push({ url, body }); return Promise.resolve({ ok: true }); } };
    const ctx = reviewCtx(phase, 4, api);
    const seen = capture();
    try {
      approveButton(C.reviewBar(ctx)).fire('click');
      await D.settle();
    } finally { seen.restore(); }

    assert.equal(posts.length, 1);
    assert.equal(posts[0].url, '/api/phase/' + phase + '/approve');
    assert.deepEqual(posts[0].body, { version: 4 },
      'the approve request must name the displayed version (v4), else a stale tab signs off a version it never showed; sent ' + JSON.stringify(posts[0].body));
  });
}

test('a 409 stale_version says a newer version arrived and reloads the screen', async () => {
  const err = Object.assign(new Error('You approved v4 of mapping, but its current version is v5. A newer version arrived - review it first.'),
    { status: 409, code: 'stale_version', details: [] });
  const api = { post: () => Promise.reject(err) };
  const ctx = reviewCtx('mapping', 4, api);
  const seen = capture();
  try {
    approveButton(C.reviewBar(ctx)).fire('click');
    await D.settle();
  } finally { seen.restore(); }

  const said = seen.toasts.map((t) => t.message).concat(seen.modals.map((m) => m.title + ' ' + (typeof m.body === 'string' ? m.body : D.text(m.body))));
  assert.ok(said.some((m) => /newer version arrived/i.test(m) && /review it first/i.test(m)),
    'a stale approve must tell the reviewer a newer version arrived; said: ' + JSON.stringify(said));
  assert.ok(!seen.modals.some((m) => m.title === 'Not ready to approve'), 'a stale version is not an approval blocker');
  assert.equal(ctx.refreshed, 1, 'the screen must reload so the newer version is shown');
});
