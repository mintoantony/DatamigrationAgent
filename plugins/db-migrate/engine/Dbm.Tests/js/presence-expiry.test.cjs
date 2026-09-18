'use strict';
/*
 * Ruling 197 (fix round 1, F1): "Claude is connected" also means "a dbm next/await ran within the last 120 s", and nothing is
 * published when that window lapses. /api/state therefore carries project.agentSeenUntil while online, and the shell schedules one
 * refresh at that instant - no polling - so a Take over button disabled for presence enables by itself, with no event.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
const env = D.install();

const until = new Date(Date.now() + 60000).toISOString();
let online = true;
function state() {
  const phases = ['setup', 'discovery', 'analysis', 'mapping', 'sql', 'ready', 'transfer', 'complete'].map((name, i) => ({
    name, status: i < 3 ? 'approved' : name === 'mapping' ? 'reworking' : 'pending', currentVersion: i <= 3 ? 2 : null, approvedVersion: null,
  }));
  return {
    project: Object.assign({ name: 'p', paused: false, agentOnline: online, sampleValues: true }, online ? { agentSeenUntil: until } : {}),
    phases, jobs: [], connections: { src: { saved: true }, tgt: { saved: true } }, drift: { src: false, tgt: false }, transfer: {},
    next: { action: 'agent', phase: 'mapping' },
  };
}
const artifact = { version: 2, author: 'agent', summary: 'v2', createdAt: '2026-09-18T09:00:00+00:00', payload: {} };
globalThis.DBM = {
  api: {
    get(p) {
      if (p === '/api/state') return Promise.resolve(state());
      if (p === '/api/artifact/mapping') return Promise.resolve({ versions: [artifact], current: artifact });
      if (p === '/api/feedback/mapping') return Promise.resolve([{ id: 1, status: 'open', anchor: null, text: 'x', version: 2 }]);
      return Promise.reject(new Error('unexpected GET ' + p));
    },
    post() { return Promise.resolve({ ok: true }); },
    events() { return { close() {} }; },
  },
};

// Long timers are captured instead of run: the one this test is about must be scheduled, not polled.
const realSetTimeout = globalThis.setTimeout;
const timers = [];
globalThis.setTimeout = (fn, ms) => {
  if (ms >= 1000) { timers.push({ fn, ms }); return timers.length; }
  return realSetTimeout(fn, ms);
};

['lib/dom.js', 'components/core.js', 'components/review.js', 'views/pending.js', 'app.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});

const takeOver = () => D.find(env.ids.view, (n) => String(n.tagName).toLowerCase() === 'button' && n.textContent === 'Take over');

test('Take over enables by itself when the agent-seen window lapses, with no event', async () => {
  await D.settle(20);
  let btn = takeOver();
  assert.ok(btn, 'the reworking Mapping screen offers Take over');
  assert.equal(!!btn.disabled, true, 'disabled while Claude counts as connected');

  const expiry = timers.find((t) => Math.abs(t.ms - 60000) < 5000);
  assert.ok(expiry, 'the shell must schedule one refresh at state.project.agentSeenUntil (~60 s away); scheduled: '
    + JSON.stringify(timers.map((t) => t.ms)));

  online = false;             // the clock passes agentSeenUntil: the server now says offline
  expiry.fn();
  await D.settle(20);

  btn = takeOver();
  assert.ok(btn && !btn.disabled, 'after agentSeenUntil passed, Take over is still disabled - the page waits for an event that never comes');
  globalThis.setTimeout = realSetTimeout;
});
