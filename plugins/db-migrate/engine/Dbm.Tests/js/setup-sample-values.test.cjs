'use strict';
/*
 * Open item 33, Ruling 196: the Setup screen has the sample-values switch, shown as state.project.sampleValues says, with a
 * one-line privacy explanation; changing it posts {sampleValues} to /api/settings.
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const D = require('./dom-stub.cjs');

const JS = path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js');
D.install();
['lib/dom.js', 'components/core.js', 'views/setup.js'].forEach((f) => {
  vm.runInThisContext(fs.readFileSync(path.join(JS, f.replace('/', path.sep)), 'utf8'));
});
const DBM = globalThis.DBM;
const C = DBM.components;

function render(sampleValues) {
  const posts = [];
  const ctx = {
    state: {
      project: { name: 'p', sampleValues }, phases: [{ name: 'setup', status: 'awaiting_review' }],
      connections: { src: { saved: false }, tgt: { saved: false } }, transfer: {},
    },
    api: { post: (url, body) => { posts.push({ url, body }); return Promise.resolve({ sampleValues: body.sampleValues, note: 'n' }); } },
    refresh() { return Promise.resolve(); },
  };
  const root = new D.FakeNode('div');
  DBM.views.setup.render(root, ctx);
  const box = D.find(root, (n) => n.attributes && n.attributes.id === 'sample-values');
  return { root, box, posts };
}

for (const on of [true, false]) {
  test('the switch shows the project setting (' + (on ? 'on' : 'off') + ') and explains what it sends', () => {
    const { root, box } = render(on);
    assert.ok(box, 'the Setup screen has no sample-values switch');
    assert.equal(box.checked, on);
    const text = D.text(root);
    assert.match(text, /Send sample values to Claude/);
    assert.match(text, /real values per text column/);
  });
}

test('switching it off posts sampleValues false to /api/settings', async () => {
  const { box, posts } = render(true);
  const saved = C.toast;
  C.toast = () => {};
  try {
    box.checked = false;
    box.fire('change');
    await D.settle();
  } finally { C.toast = saved; }
  assert.deepEqual(posts, [{ url: '/api/settings', body: { sampleValues: false } }]);
});
