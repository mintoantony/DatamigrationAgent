/* DBM.graph — layered layout + FK graph SVG (no dependencies; layout also runs under Node for tests). */
(function (DBM) {
  'use strict';

  var DEFAULTS = { nodeW: 176, nodeH: 44, gapX: 28, gapY: 56, margin: 16, viewWidth: 1100, viewHeight: 520 };
  var uid = 0;

  function opt(opts, key) { return opts && opts[key] != null ? opts[key] : DEFAULTS[key]; }
  function cmp(a, b) { return a < b ? -1 : a > b ? 1 : 0; }
  function r1(v) { return Math.round(v * 10) / 10; }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function ellipsis(s, n) { s = String(s); return s.length > n ? s.slice(0, n - 1) + '…' : s; }

  /**
   * Pure layered layout. nodes: [{id}], edges: [{from, to}] where from = FK owner (child) and to = referenced table (parent).
   * Cycle edges are ignored for layering (DFS back edges), parents are placed above children (longest-path layering), then two
   * barycenter passes (down by parents, up by children) order each layer. Deterministic: ids are processed in sorted order.
   * Returns {pos: {id: {x, y, layer, order}}, width, height, layers, nodeW, nodeH}; x/y are the node box's top-left corner.
   */
  function layout(nodes, edges, opts) {
    var nodeW = opt(opts, 'nodeW'), nodeH = opt(opts, 'nodeH'), gapX = opt(opts, 'gapX'), gapY = opt(opts, 'gapY');
    var margin = opt(opts, 'margin');
    var ids = [], known = {};
    (nodes || []).forEach(function (n) {
      var id = String(n && n.id != null ? n.id : n);
      if (!known[id]) { known[id] = true; ids.push(id); }
    });
    ids.sort(cmp);
    if (!ids.length) return { pos: {}, width: 2 * margin, height: 2 * margin, layers: [], nodeW: nodeW, nodeH: nodeH };

    var parents = {}, dag = {}, children = {};
    ids.forEach(function (id) { parents[id] = []; dag[id] = []; children[id] = []; });
    var seen = {};
    (edges || []).forEach(function (e) {
      var a = String(e.from), b = String(e.to);
      var key = JSON.stringify([a, b]);
      if (a === b || !known[a] || !known[b] || seen[key]) return;
      seen[key] = true;
      parents[a].push(b);
    });
    ids.forEach(function (id) { parents[id].sort(cmp); });

    // 1. cycle removal: iterative DFS along child -> parent edges; an edge back to a node on the current path is dropped.
    var state = {};
    ids.forEach(function (root) {
      if (state[root]) return;
      var stack = [{ id: root, i: 0 }];
      state[root] = 1;
      while (stack.length) {
        var top = stack[stack.length - 1];
        var ps = parents[top.id];
        if (top.i < ps.length) {
          var p = ps[top.i++];
          if (state[p] === 1) continue;
          dag[top.id].push(p);
          if (!state[p]) { state[p] = 1; stack.push({ id: p, i: 0 }); }
        } else {
          state[top.id] = 2;
          stack.pop();
        }
      }
    });
    ids.forEach(function (id) { dag[id].forEach(function (p) { children[p].push(id); }); });

    // 2. longest-path layering (Kahn order over the acyclic edges): layer(child) = 1 + max(layer(parent)).
    var layer = {}, pending = {}, queue = [];
    ids.forEach(function (id) { layer[id] = 0; pending[id] = dag[id].length; if (!pending[id]) queue.push(id); });
    for (var qi = 0; qi < queue.length; qi++) {
      var n = queue[qi];
      children[n].forEach(function (c) {
        layer[c] = Math.max(layer[c], layer[n] + 1);
        if (--pending[c] === 0) queue.push(c);
      });
    }
    var maxLayer = 0;
    ids.forEach(function (id) { maxLayer = Math.max(maxLayer, layer[id]); });
    var layers = [];
    for (var l = 0; l <= maxLayer; l++) layers.push([]);
    ids.forEach(function (id) { layers[layer[id]].push(id); });

    // 3. ordering: centred index per layer, one barycenter pass down (by parents) and one up (by children).
    var pos = {};
    function place(row) { row.forEach(function (id, i) { pos[id] = i - (row.length - 1) / 2; }); }
    layers.forEach(place);
    function sweep(row, neighbours) {
      var bary = {};
      row.forEach(function (id) {
        var ns = neighbours[id];
        if (!ns.length) { bary[id] = pos[id]; return; }
        var s = 0;
        ns.forEach(function (x) { s += pos[x]; });
        bary[id] = s / ns.length;
      });
      row.sort(function (a, b) { return (bary[a] - bary[b]) || (pos[a] - pos[b]) || cmp(a, b); });
      place(row);
    }
    for (l = 1; l <= maxLayer; l++) sweep(layers[l], dag);
    for (l = maxLayer - 1; l >= 0; l--) sweep(layers[l], children);

    // 4. coordinates: every layer centred on the widest one.
    var widest = 0;
    layers.forEach(function (row) { widest = Math.max(widest, row.length); });
    var width = 2 * margin + widest * nodeW + (widest - 1) * gapX;
    var height = 2 * margin + layers.length * nodeH + (layers.length - 1) * gapY;
    var out = {};
    layers.forEach(function (row, li) {
      var rowW = row.length * nodeW + (row.length - 1) * gapX;
      var x0 = (width - rowW) / 2;
      row.forEach(function (id, i) {
        out[id] = { x: r1(x0 + i * (nodeW + gapX)), y: r1(margin + li * (nodeH + gapY)), layer: li, order: i };
      });
    });
    return { pos: out, width: r1(width), height: r1(height), layers: layers, nodeW: nodeW, nodeH: nodeH };
  }

  /**
   * FK graph as an SVG element. nodes: [{id, label?, sub?, title?, cls?}], edges: [{from, to}] (child -> parent).
   * opts: layout options + {label, onClick(id), viewWidth, viewHeight, document}. Drag pans (viewBox), hover/focus highlights a node's
   * edges and neighbours, Enter/Space or click calls onClick. The element gets svg.dbmFit(full) to toggle whole-graph / 1:1 view.
   */
  function fkSvg(nodes, edges, opts) {
    opts = opts || {};
    var doc = opts.document || document;
    var NS = 'http://www.w3.org/2000/svg';
    var L = layout(nodes, edges, opts);
    var id = 'fkg' + (++uid);
    var w = L.nodeW, h = L.nodeH;
    var fullW = Math.max(L.width, 1), fullH = Math.max(L.height, 1);
    var viewW = Math.min(fullW, opt(opts, 'viewWidth')), viewH = Math.min(fullH, opt(opts, 'viewHeight'));
    var vb = { x: 0, y: 0, w: viewW, h: viewH };
    var byId = {};
    (nodes || []).forEach(function (n) { byId[String(n.id)] = n; });

    function el(tag, attrs, parent) {
      var e = doc.createElementNS(NS, tag);
      Object.keys(attrs || {}).forEach(function (k) { if (attrs[k] != null) e.setAttribute(k, String(attrs[k])); });
      if (parent) parent.appendChild(e);
      return e;
    }
    function setViewBox() { svg.setAttribute('viewBox', [r1(vb.x), r1(vb.y), r1(vb.w), r1(vb.h)].join(' ')); }

    var svg = el('svg', { class: 'fkg', role: 'group', 'aria-label': opts.label || 'Foreign-key graph', preserveAspectRatio: 'xMidYMin meet' });
    svg.style.height = viewH + 'px';
    setViewBox();
    var defs = el('defs', {}, svg);
    var marker = el('marker', { id: id + '-arrow', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' }, defs);
    el('path', { d: 'M0,0 L10,5 L0,10 z', class: 'fkg-arrowhead' }, marker);
    var gEdges = el('g', { class: 'fkg-edges' }, svg);
    var gNodes = el('g', { class: 'fkg-nodes' }, svg);

    var edgeEls = [], nodeEls = {};
    (edges || []).forEach(function (e) {
      var from = String(e.from), to = String(e.to), a = L.pos[from], b = L.pos[to];
      if (!a || !b) return;
      var d, cycle = false;
      if (from === to) {
        var sx = a.x + w, sy = a.y + h * 0.3, ey = a.y + h * 0.7;
        d = 'M' + sx + ',' + sy + ' C' + (sx + 36) + ',' + (sy - 18) + ' ' + (sx + 36) + ',' + (ey + 18) + ' ' + sx + ',' + ey;
        cycle = true;
      } else if (a.layer > b.layer) {
        var x1 = a.x + w / 2, y1 = a.y, x2 = b.x + w / 2, y2 = b.y + h, my = (y1 + y2) / 2;
        d = 'M' + x1 + ',' + y1 + ' C' + x1 + ',' + my + ' ' + x2 + ',' + my + ' ' + x2 + ',' + y2;
      } else {
        var cx = a.x + w, cy = a.y + h / 2, tx = b.x + w, ty = b.y + h / 2, bulge = 40 + Math.abs(cy - ty) * 0.25;
        d = 'M' + cx + ',' + cy + ' C' + (cx + bulge) + ',' + cy + ' ' + (tx + bulge) + ',' + ty + ' ' + tx + ',' + ty;
        cycle = true;
      }
      var path = el('path', { d: d, class: 'fkg-edge' + (cycle ? ' fkg-edge-cycle' : ''), 'marker-end': 'url(#' + id + '-arrow)', 'data-from': from, 'data-to': to }, gEdges);
      edgeEls.push(path);
    });

    function highlight(nid, on) {
      svg.classList.toggle('fkg-dim', on);
      edgeEls.forEach(function (p) {
        var f = p.getAttribute('data-from'), t = p.getAttribute('data-to');
        var hit = on && (f === nid || t === nid);
        p.classList.toggle('fkg-hi', hit);
        if (hit) { nodeEls[f].classList.add('fkg-hi'); nodeEls[t].classList.add('fkg-hi'); }
      });
      if (!on) Object.keys(nodeEls).forEach(function (k) { nodeEls[k].classList.remove('fkg-hi'); });
      else nodeEls[nid].classList.add('fkg-hi');
    }

    var moved = false;
    Object.keys(L.pos).sort(cmp).forEach(function (nid) {
      var p = L.pos[nid], n = byId[nid] || {};
      var g = el('g', {
        class: 'fkg-node' + (n.cls ? ' ' + n.cls : ''), transform: 'translate(' + p.x + ',' + p.y + ')', tabindex: 0, role: 'button',
        'data-id': nid, 'aria-label': (n.label || nid) + (n.sub ? ', ' + n.sub : '')
      }, gNodes);
      el('rect', { width: w, height: h, rx: 8, ry: 8 }, g);
      el('text', { x: 12, y: n.sub ? 18 : h / 2 + 4, class: 'fkg-label' }, g).textContent = ellipsis(n.label || nid, 24);
      if (n.sub) el('text', { x: 12, y: 34, class: 'fkg-sub' }, g).textContent = ellipsis(n.sub, 28);
      el('title', {}, g).textContent = n.title || n.label || nid;
      nodeEls[nid] = g;
      g.addEventListener('mouseenter', function () { highlight(nid, true); });
      g.addEventListener('mouseleave', function () { highlight(nid, false); });
      g.addEventListener('focus', function () { highlight(nid, true); });
      g.addEventListener('blur', function () { highlight(nid, false); });
      g.addEventListener('click', function () { if (!moved && opts.onClick) opts.onClick(nid); });
      g.addEventListener('keydown', function (ev) {
        if ((ev.key === 'Enter' || ev.key === ' ') && opts.onClick) { ev.preventDefault(); opts.onClick(nid); }
      });
    });

    var drag = null;
    svg.addEventListener('pointerdown', function (ev) {
      if (ev.button !== 0) return;
      drag = { x: ev.clientX, y: ev.clientY, vx: vb.x, vy: vb.y, pointer: ev.pointerId };
      moved = false;
    });
    svg.addEventListener('pointermove', function (ev) {
      if (!drag) return;
      var rect = svg.getBoundingClientRect();
      var scale = Math.max(vb.w / Math.max(rect.width, 1), vb.h / Math.max(rect.height, 1));
      var dx = (ev.clientX - drag.x) * scale, dy = (ev.clientY - drag.y) * scale;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 4 * scale) return;
      if (!moved) { moved = true; svg.classList.add('is-panning'); if (svg.setPointerCapture) svg.setPointerCapture(drag.pointer); }
      vb.x = clamp(drag.vx - dx, Math.min(0, fullW - vb.w), Math.max(0, fullW - vb.w));
      vb.y = clamp(drag.vy - dy, Math.min(0, fullH - vb.h), Math.max(0, fullH - vb.h));
      setViewBox();
    });
    function endDrag() {
      if (drag && moved && svg.releasePointerCapture && svg.hasPointerCapture && svg.hasPointerCapture(drag.pointer)) svg.releasePointerCapture(drag.pointer);
      drag = null;
      svg.classList.remove('is-panning');
      setTimeout(function () { moved = false; }, 0);
    }
    svg.addEventListener('pointerup', endDrag);
    svg.addEventListener('pointercancel', endDrag);

    svg.dbmFit = function (full) {
      vb = full ? { x: 0, y: 0, w: fullW, h: fullH } : { x: 0, y: 0, w: viewW, h: viewH };
      svg.style.height = (full ? Math.min(fullH, opt(opts, 'viewHeight')) : viewH) + 'px';
      setViewBox();
    };
    svg.dbmLayout = L;
    return svg;
  }

  DBM.graph = { layout: layout, fkSvg: fkSvg };
})(globalThis.DBM = globalThis.DBM || {});
