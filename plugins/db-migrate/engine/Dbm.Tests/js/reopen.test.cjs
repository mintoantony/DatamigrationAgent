'use strict';
/*
 * Ruling 185 (open item 36): the Reopen button on an approved review phase follows the server's state.transfer.changesLocked - offered
 * before the first run and after a completed, cancelled or failed one, shown disabled with the server's sentence under a running or
 * paused run. Before 185 it followed "is the Transfer phase pending", which hid it for good after the first Execute.
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

function ctx(transfer, transferPhase) {
  const phases = ['setup', 'discovery', 'analysis', 'mapping', 'sql', 'ready', 'transfer', 'complete'].map((name) => ({
    name, status: name === 'transfer' ? transferPhase : 'approved', currentVersion: 1, approvedVersion: 1,
  }));
  return {
    phase: 'mapping',
    phaseRow: phases[3],
    state: { phases, transfer },
    feedback: [],
    versions: [{ version: 1, author: 'agent', summary: 'mapping v1', createdAt: '2026-09-17T09:00:00+00:00' }],
    latestVersion: 1,
    artifact: { version: 1, author: 'agent', summary: 'mapping v1' },
    readOnly: true,
    openFeedback() {}, openHistory() {}, setVersion() {}, refresh() {},
  };
}

function reopenButton(bar) {
  return D.find(bar, (n) => String(n.tagName).toLowerCase() === 'button' && n.textContent === 'Reopen');
}

const cases = [
  ['before the first run', {}, 'pending', true],
  ['after a completed run', { runId: 1, runStatus: 'completed' }, 'approved', true],
  ['after a cancelled run', { runId: 1, runStatus: 'cancelled' }, 'running', true],
  ['after a failed run (the server cancels it first)', { runId: 1, runStatus: 'failed' }, 'running', true],
  ['under a paused run', { runId: 1, runStatus: 'paused', changesLocked: 'transfer run 1 is paused. Cancel it first.' }, 'running', false],
  ['under a running run', { runId: 1, runStatus: 'running', changesLocked: 'transfer run 1 is running. Pause it and cancel it first.' }, 'running', false],
];

for (const [when, transfer, transferPhase, enabled] of cases) {
  test('Reopen ' + (enabled ? 'is offered ' : 'is shown disabled with the reason ') + when, () => {
    const bar = DBM.components.reviewBar(ctx(transfer, transferPhase));
    const btn = reopenButton(bar);
    assert.ok(btn, 'no Reopen button at all ' + when + ' - the review loop ends here for the operator');
    assert.equal(!!btn.disabled, !enabled, 'Reopen ' + (enabled ? 'disabled' : 'enabled') + ' ' + when);
    if (!enabled) assert.equal(btn.getAttribute('title'), transfer.changesLocked);
  });
}
