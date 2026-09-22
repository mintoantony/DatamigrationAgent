'use strict';
/*
 * Open item 37, Ruling 195: a review phase Claude is drafting or reworking (its patch rejected twice) offers "Take over". The
 * confirm dialog says what is discarded; confirming posts /api/phase/{phase}/take-over. While Claude is connected the button is
 * disabled with the reason. Other statuses show no button.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
const env = D.install();
['lib/dom.js', 'components/core.js', 'components/review.js', 'views/pending.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});
const DBM = globalThis.DBM;
const C = DBM.components;
DBM.fmt = DBM.fmt || { ts: (t) => String(t), rel: (t) => String(t) };

function ctxFor(phase, status, { agentOnline = false, feedback = [] } = {}) {
  const phases = ['setup', 'discovery', 'analysis', 'mapping', 'sql', 'ready', 'transfer', 'complete'].map((name) => ({
    name, status: name === phase ? status : 'approved', currentVersion: name === phase ? 3 : 1,
  }));
  const posts = [];
  const ctx = {
    phase,
    phaseRow: phases.find((p) => p.name === phase),
    state: { phases, transfer: {}, project: { agentOnline, paused: false }, jobs: [] },
    feedback,
    logs: [],
    versions: [{ version: 3, author: 'agent', summary: 'v3', createdAt: '2026-09-18T09:00:00+00:00' }],
    latestVersion: 3,
    artifact: { version: 3, author: 'agent', summary: 'v3', payload: {} },
    readOnly: true,
    api: { post: (url, body) => { posts.push({ url, body }); return Promise.resolve({ ok: true }); } },
    posts,
    refreshed: 0,
    commentable: (el) => el,
    openFeedback() {}, openHistory() {}, setVersion() {},
    refresh() { ctx.refreshed++; return Promise.resolve(); },
  };
  return ctx;
}

const isTakeOver = (n) => String(n.tagName).toLowerCase() === 'button' && n.textContent === 'Take over';

function capture(answer) {
  const seen = { toasts: [], modals: [] };
  const saved = { toast: C.toast, modal: C.modal };
  C.toast = (message, kind) => seen.toasts.push({ message: String(message), kind });
  C.modal = (o) => { seen.modals.push(o); return Promise.resolve(answer); };
  seen.restore = () => Object.assign(C, saved);
  return seen;
}

for (const phase of ['analysis', 'mapping', 'sql']) {
  test(phase + ' reworking: Take over is offered, says what it discards and posts take-over once confirmed', async () => {
    const ctx = ctxFor(phase, 'reworking', { feedback: [{ id: 1, status: 'open' }, { id: 2, status: 'open' }] });
    const btn = D.find(C.reviewBar(ctx), isTakeOver);
    assert.ok(btn, 'a reworking ' + phase + ' must offer Take over - without it the phase is stuck when the patch is rejected twice');
    assert.equal(!!btn.disabled, false, 'Take over is disabled although no agent is connected');

    const seen = capture(true);
    try {
      btn.fire('click');
      await D.settle();
    } finally { seen.restore(); }

    assert.equal(seen.modals.length, 1, 'Take over must ask for confirmation first');
    const body = seen.modals[0].body;
    assert.match(body, /discards the pending agent work/, 'the dialog must say the pending agent work is discarded: ' + body);
    assert.match(body, /rework of your 2 open comments/);
    assert.match(body, /Awaiting review on v3/);
    assert.match(body, /open comments stay open/);
    assert.deepEqual(ctx.posts.map((p) => p.url), ['/api/phase/' + phase + '/take-over']);
    assert.equal(ctx.refreshed, 1);
  });
}

/* Ruling 198 (fix round 1, F2): Analysis has no editor, and a drafting Analysis has no narrative, which blocks its approval. The
   dialog and the status-screen hint promise hand editing only where it exists. */
async function dialogBody(phase, status) {
  const ctx = ctxFor(phase, status, { feedback: status === 'reworking' ? [{ id: 1, status: 'open' }] : [] });
  let btn;
  let root = null;
  if (status === 'drafting') {
    root = new D.FakeNode('div');
    DBM.views.pending.render(root, ctx);
    btn = D.find(root, isTakeOver);
  } else {
    btn = D.find(C.reviewBar(ctx), isTakeOver);
  }
  const seen = capture(false);
  try {
    btn.fire('click');
    await D.settle();
  } finally { seen.restore(); }
  return { body: seen.modals[0].body, hint: root ? D.text(root) : '' };
}

for (const status of ['drafting', 'reworking']) {
  test('analysis ' + status + ': Take over never promises hand editing; the way on is Request changes', async () => {
    const { body, hint } = await dialogBody('analysis', status);
    const said = body + ' ' + hint;
    assert.ok(!/edit it directly|edit v\d+ yourself|edit it yourself/.test(said),
      'Analysis has no editor, yet Take over promises hand editing: ' + said);
    assert.match(body, /Analysis cannot be edited by hand/);
    assert.match(body, /Request changes/);
    if (status === 'drafting') {
      assert.match(body, /cannot be approved until it has a narrative/,
        'a drafting Analysis has no narrative, so approval is blocked - the dialog must say so: ' + body);
    } else {
      assert.ok(!/until it has a narrative/.test(body), 'a reworked Analysis already has its narrative: ' + body);
    }
  });
}

for (const phase of ['mapping', 'sql']) {
  test(phase + ' drafting: Take over offers hand editing', async () => {
    const { body, hint } = await dialogBody(phase, 'drafting');
    assert.match(body, /edit it directly, approve it or request changes/);
    assert.match(hint, /edit v3 yourself/);
  });
}

test('cancelling the dialog takes nothing over', async () => {
  const ctx = ctxFor('sql', 'reworking');
  const seen = capture(false);
  try {
    D.find(C.reviewBar(ctx), isTakeOver).fire('click');
    await D.settle();
  } finally { seen.restore(); }
  assert.deepEqual(ctx.posts, []);
});

test('while Claude is connected Take over is disabled with the reason', () => {
  const btn = D.find(C.reviewBar(ctxFor('mapping', 'reworking', { agentOnline: true })), isTakeOver);
  assert.ok(btn, 'the button is shown disabled, not hidden');
  assert.equal(!!btn.disabled, true, 'Take over must be refused while an agent may be applying a patch');
  assert.match(btn.getAttribute('title'), /may be applying a patch/);
});

for (const status of ['awaiting_review', 'approved']) {
  test('no Take over on a phase that is ' + status, () => {
    assert.equal(D.find(C.reviewBar(ctxFor('mapping', status)), isTakeOver), null);
  });
}

test('drafting (the pending screen): Take over is offered and the dialog names the first version as discarded', async () => {
  const ctx = ctxFor('analysis', 'drafting');
  const root = new D.FakeNode('div');
  DBM.views.pending.render(root, ctx);
  const btn = D.find(root, isTakeOver);
  assert.ok(btn, 'a drafting phase must offer Take over on its status screen');

  const seen = capture(true);
  try {
    btn.fire('click');
    await D.settle();
  } finally { seen.restore(); }
  assert.match(seen.modals[0].body, /first version of Analysis/);
  assert.deepEqual(ctx.posts.map((p) => p.url), ['/api/phase/analysis/take-over']);
  env.flushFrames();
});
