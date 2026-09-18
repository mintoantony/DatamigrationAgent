/* Line diff (LCS) for version comparison. Classic script; runs under Node for tests. */
(function (DBM) {
  'use strict';

  var MAX_CELLS = 4000000;   // beyond this the middle section is shown as a full replace

  function split(text) {
    if (text == null || text === '') return [];
    return String(text).replace(/\r\n?/g, '\n').split('\n');
  }

  /** lines(a, b) → [{op: 'eq'|'add'|'del', text}] turning text a into text b. */
  function lines(a, b) {
    var A = split(a);
    var B = split(b);
    var start = 0;
    while (start < A.length && start < B.length && A[start] === B[start]) start++;
    var endA = A.length;
    var endB = B.length;
    while (endA > start && endB > start && A[endA - 1] === B[endB - 1]) { endA--; endB--; }

    var ops = [];
    var i;
    for (i = 0; i < start; i++) ops.push({ op: 'eq', text: A[i] });

    var a = A.slice(start, endA);
    var b = B.slice(start, endB);
    var n = a.length;
    var m = b.length;
    if ((n + 1) * (m + 1) > MAX_CELLS) {
      a.forEach(function (t) { ops.push({ op: 'del', text: t }); });
      b.forEach(function (t) { ops.push({ op: 'add', text: t }); });
    } else {
      // dp[x][y] = LCS length of a[x..] and b[y..], stored row-major in one typed array.
      var w = m + 1;
      var dp = new Uint32Array((n + 1) * w);
      for (var x = n - 1; x >= 0; x--) {
        for (var y = m - 1; y >= 0; y--) {
          dp[x * w + y] = a[x] === b[y] ? dp[(x + 1) * w + y + 1] + 1 : Math.max(dp[(x + 1) * w + y], dp[x * w + y + 1]);
        }
      }
      var p = 0;
      var q = 0;
      while (p < n && q < m) {
        if (a[p] === b[q]) { ops.push({ op: 'eq', text: a[p] }); p++; q++; }
        else if (dp[(p + 1) * w + q] >= dp[p * w + q + 1]) { ops.push({ op: 'del', text: a[p] }); p++; }
        else { ops.push({ op: 'add', text: b[q] }); q++; }
      }
      while (p < n) ops.push({ op: 'del', text: a[p++] });
      while (q < m) ops.push({ op: 'add', text: b[q++] });
    }

    for (i = endA; i < A.length; i++) ops.push({ op: 'eq', text: A[i] });
    return ops;
  }

  /** Keeps `context` unchanged lines around each change; longer unchanged runs become {op: 'skip', count}. */
  function collapse(ops, context) {
    var ctx = context == null ? 3 : context;
    var keep = ops.map(function () { return false; });
    ops.forEach(function (o, i) {
      if (o.op === 'eq') return;
      for (var k = Math.max(0, i - ctx); k <= Math.min(ops.length - 1, i + ctx); k++) keep[k] = true;
    });
    var out = [];
    var skipped = 0;
    ops.forEach(function (o, i) {
      if (keep[i]) {
        if (skipped) { out.push({ op: 'skip', count: skipped }); skipped = 0; }
        out.push(o);
      } else {
        skipped++;
      }
    });
    if (skipped) out.push({ op: 'skip', count: skipped });
    return out;
  }

  function stats(ops) {
    var s = { add: 0, del: 0 };
    ops.forEach(function (o) { if (o.op === 'add') s.add++; else if (o.op === 'del') s.del++; });
    return s;
  }

  DBM.diff = { lines: lines, collapse: collapse, stats: stats };
})(globalThis.DBM = globalThis.DBM || {});
