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

/* Review N2: the one deliberate exception, named so it cannot grow quietly. sql-view.test.cjs (T4.5) never builds a `document`: it
   swaps DBM.h for a plain-object tree builder and drives handlers through `attrs.on`, a different layer that 30+ of its render tests
   assert against. Folding it into dom-stub.cjs would rewrite those assertions, which open item 25 forbids. */
const EXCEPTIONS = { 'sql-view.test.cjs': 'replaces DBM.h with a plain-object tree (no document); see the comment above' };

test('every node test that needs a document installs dom-stub.cjs instead of a private stub', () => {
  const stub = /globalThis\.document\s*=|class\s+FakeNode\b|DBM\.h\s*=/;
  const files = fs.readdirSync(__dirname).filter((f) => f.endsWith('.test.cjs') && f !== path.basename(__filename));
  const own = files.filter((f) => stub.test(fs.readFileSync(path.join(__dirname, f), 'utf8')));
  const unnamed = own.filter((f) => !Object.prototype.hasOwnProperty.call(EXCEPTIONS, f));
  assert.deepEqual(unnamed, [], 'these test files build a private DOM stub instead of installing dom-stub.cjs: ' + unnamed.join(', '));
  for (const f of Object.keys(EXCEPTIONS)) {
    assert.ok(own.includes(f), f + ' is named as a DOM-stub exception but no longer builds one: remove it from EXCEPTIONS');
  }
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
