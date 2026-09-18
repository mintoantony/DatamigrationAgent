'use strict';
/*
 * Open item 25: the node suite has ONE document stub, dom-stub.cjs. A render assertion is only as good as the stub under it - the
 * T5.6 stub's missing `disabled` reflection once made a test read an armed button where the browser shows a dead one - and a
 * second private stub is where that fidelity quietly drifts (mapping-edits.test.cjs carried one whose classList did nothing and
 * whose `disabled` never followed the attribute). Any test file that builds its own `document` or FakeNode fails here, by name.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const D = require('./dom-stub.cjs');

test('every node test that needs a document installs dom-stub.cjs instead of a private stub', () => {
  const own = fs.readdirSync(__dirname).filter((f) => f.endsWith('.test.cjs') && f !== path.basename(__filename))
    .filter((f) => /globalThis\.document\s*=|class\s+FakeNode\b/.test(fs.readFileSync(path.join(__dirname, f), 'utf8')));
  assert.deepEqual(own, [], 'these test files build a private DOM stub instead of installing dom-stub.cjs: ' + own.join(', '));
});

test('the shared stub reflects the boolean attributes DBM.h writes, as the browser does', () => {
  const button = new D.FakeNode('button');
  for (const name of ['disabled', 'hidden', 'checked', 'readonly', 'required']) {
    button.setAttribute(name, '');
    assert.equal(button[name], true, name + ' set as an attribute must read back true from the property');
    button.removeAttribute(name);
    assert.equal(button[name], false, name + ' removed must read back false');
  }
  button.classList.add('is-busy');
  assert.equal(button.classList.contains('is-busy'), true, 'classList must be a real class list, not a no-op');
});
