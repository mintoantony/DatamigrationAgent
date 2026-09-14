'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'graph.js'), 'utf8'));
const G = globalThis.DBM.graph;

const shopV2 = {
  nodes: ['app.Customers', 'app.Addresses', 'app.Products', 'app.Orders', 'app.OrderLines', 'app.AuditEvents'].map((id) => ({ id })),
  edges: [
    { from: 'app.Customers', to: 'app.Addresses' },
    { from: 'app.Addresses', to: 'app.Customers' },
    { from: 'app.Orders', to: 'app.Customers' },
    { from: 'app.Orders', to: 'app.Addresses' },
    { from: 'app.OrderLines', to: 'app.Orders' },
    { from: 'app.OrderLines', to: 'app.Products' },
  ],
};

function finite(result) {
  for (const [id, p] of Object.entries(result.pos)) {
    for (const k of ['x', 'y', 'layer', 'order']) assert.ok(Number.isFinite(p[k]), `${id}.${k} = ${p[k]}`);
  }
  assert.ok(Number.isFinite(result.width) && Number.isFinite(result.height));
}

test('parents are layered above their children', () => {
  const r = G.layout(
    [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }],
    [{ from: 'b', to: 'a' }, { from: 'c', to: 'b' }, { from: 'd', to: 'a' }, { from: 'c', to: 'd' }]);
  assert.equal(r.pos.a.layer, 0);
  assert.equal(r.pos.b.layer, 1);
  assert.equal(r.pos.d.layer, 1);
  assert.equal(r.pos.c.layer, 2);
  assert.ok(r.pos.a.y < r.pos.b.y && r.pos.b.y < r.pos.c.y);
  finite(r);
});

test('the ShopV2 graph with its Customers <-> Addresses cycle lays out in 4 layers', () => {
  const r = G.layout(shopV2.nodes, shopV2.edges);
  assert.equal(r.layers.length, 4);
  assert.ok(r.pos['app.Orders'].layer > r.pos['app.Customers'].layer);
  assert.ok(r.pos['app.Orders'].layer > r.pos['app.Addresses'].layer);
  assert.ok(r.pos['app.OrderLines'].layer > r.pos['app.Orders'].layer);
  assert.ok(r.pos['app.OrderLines'].layer > r.pos['app.Products'].layer);
  assert.equal(Object.keys(r.pos).length, 6);
  finite(r);
});

test('self loops, duplicate and unknown edges are ignored', () => {
  const r = G.layout([{ id: 'x' }, { id: 'y' }, { id: 'x' }],
    [{ from: 'x', to: 'x' }, { from: 'y', to: 'x' }, { from: 'y', to: 'x' }, { from: 'y', to: 'nope' }]);
  assert.deepEqual(Object.keys(r.pos).sort(), ['x', 'y']);
  assert.equal(r.pos.y.layer, 1);
  finite(r);
});

test('layout is deterministic regardless of input order', () => {
  const a = G.layout(shopV2.nodes, shopV2.edges);
  const b = G.layout([...shopV2.nodes].reverse(), [...shopV2.edges].reverse());
  assert.equal(JSON.stringify(a), JSON.stringify(G.layout(shopV2.nodes, shopV2.edges)));
  assert.equal(JSON.stringify(a.pos), JSON.stringify(b.pos));
});

test('nodes of one layer do not overlap and fit inside the width', () => {
  const r = G.layout(shopV2.nodes, shopV2.edges, { nodeW: 100, gapX: 10 });
  for (const row of r.layers) {
    const xs = row.map((id) => r.pos[id].x).sort((p, q) => p - q);
    for (let i = 1; i < xs.length; i++) assert.ok(xs[i] - xs[i - 1] >= 110 - 0.01);
    for (const id of row) assert.ok(r.pos[id].x >= 0 && r.pos[id].x + 100 <= r.width + 0.01);
  }
});

test('an empty graph has no positions', () => {
  const r = G.layout([], []);
  assert.deepEqual(r.pos, {});
  assert.equal(r.layers.length, 0);
  finite(r);
});

test('a large random graph stays finite', () => {
  const nodes = Array.from({ length: 300 }, (_, i) => ({ id: 'T' + i }));
  const edges = [];
  let seed = 7;
  const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
  for (let i = 0; i < 600; i++) edges.push({ from: 'T' + Math.floor(rnd() * 300), to: 'T' + Math.floor(rnd() * 300) });
  finite(G.layout(nodes, edges));
});
