/* Final report (phase Complete; also the standalone export view "report"). Read-only rendering of the FinalReport payload.
 *
 * The report is where the run's absences are settled, so nothing here is allowed to round one off: a count nobody compared is "not
 * compared" with the engine's note beside it and never a blank or a cross; a checksum headline carries the columns nobody could cover;
 * the run's notes are printed word for word, in order. A run with no stored report gets the reason it has none, never an empty page.
 */
(function (DBM) {
  'use strict';
  DBM.views = DBM.views || {};
  const h = DBM.h;
  const X = DBM.xfer;

  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); }
  function fmtTs(s) { return !s ? '—' : (DBM.fmt && DBM.fmt.ts ? DBM.fmt.ts(s) : new Date(s).toLocaleString()); }

  /** Live view: ctx.artifact = {version, …, payload: report}. Standalone export: window.DBM_EXPORT.payload is the envelope. */
  function reportOf(ctx) {
    const a = ctx && ctx.artifact;
    if (a && typeof a === 'object') {
      if (a.payload && typeof a.payload === 'object' && 'runId' in a.payload) return a.payload;
      if ('runId' in a) return a;
    }
    const e = typeof window !== 'undefined' && window.DBM_EXPORT;
    if (e && e.payload && typeof e.payload === 'object' && 'runId' in e.payload) return e.payload;
    return null;
  }

  function kpi(label, value, sub, tone) {
    return h('div', { class: 'kpi' + (tone ? ' rep-kpi-' + tone : '') },
      h('div', { class: 'kpi-l' }, label), h('div', { class: 'kpi-v num' }, value), h('div', { class: 'kpi-s' }, sub || ''));
  }

  /** A row-count verdict in words. countMatch null is "not compared", with the engine's note - never a blank and never a cross. */
  function countMark(t) {
    const info = X.countMatchInfo(t);
    const cls = info.tone === 'ok' ? 'rep-match' : info.tone === 'err' ? 'rep-mismatch' : 'rep-unknown';
    return h('span', { class: cls, title: info.note || '' }, info.text);
  }

  /**
   * One column's verdict. The third branch is a **contract assertion against a trigger unreachable from here** (rulings 104, 112,
   * 121): `ChecksumResult.Match` is a plain `bool`, so the engine cannot send a column with no verdict today. It is written and kept
   * so that making `Match` nullable - the obvious fix for a column the validator could not read - cannot silently draw an uncompared
   * column as a match or a mismatch. `X.reportChecksums` counts `differ` rather than subtracting for the same reason.
   */
  function matchMark(v) {
    if (v === true) return h('span', { class: 'rep-match' }, '✓ match');
    if (v === false) return h('span', { class: 'rep-mismatch' }, '✕ mismatch');
    return h('span', { class: 'rep-unknown' }, 'not compared');
  }

  function checksumCell(t) {
    const list = t.checksums || [];
    const text = X.checksumDetail(t);
    const bad = list.some(function (c) { return c.match === false; });
    const missed = t.checksumColumnsNotCompared || 0;
    const cls = list.length ? (bad ? 'rep-mismatch' : (missed ? 'rep-unknown' : 'rep-match')) : 'rep-unknown';
    return h('span', { class: cls, title: t.checksumsSkipped || t.checksumColumnsNote || '' }, text);
  }

  function num(n) { return X.numOrUnknown(n); }

  function render(root, ctx) {
    clear(root);
    const r = reportOf(ctx);
    if (!r) { renderWithoutReport(root, ctx); return; }

    const tasks = r.tasks || [];
    const counts = X.reportCounts(tasks);
    const sums = X.reportChecksums(tasks);
    const missing = r.tasksWithoutSource || 0;

    const exportBtn = ctx.export ? null
      : h('button', { class: 'btn', type: 'button', on: { click: function (e) { exportReport(e.currentTarget, ctx); } } }, 'Export HTML');
    // Ruling 185: every run that completed has its own report (one Complete version each); after a reopen and a new run the earlier
    // ones stay readable here. The export is always the latest.
    const versions = (ctx && ctx.versions) || [];
    const picker = ctx.export || versions.length < 2 || !ctx.setVersion ? null
      : h('select', { class: 'select', 'aria-label': 'Report of which run', on: { change: function (e) { ctx.setVersion(Number(e.target.value)); } } },
        versions.slice().reverse().map(function (v) {
          const shown = ctx.artifact && ctx.artifact.version === v.version;
          return h('option', { value: String(v.version), selected: shown, title: v.summary || '' },
            'Report ' + v.version + ' · ' + fmtTs(v.createdAt) + (v.version === versions[versions.length - 1].version ? ' (latest)' : ''));
        }));
    const page = h('div', { class: 'page rep-page' },
      h('div', { class: 'page-h row' },
        h('div', { class: 'stack' }, h('div', { class: 'h1' }, 'Final report'),
          h('div', { class: 'muted small' }, 'Run #' + r.runId + ' · ' + fmtTs(r.startedAt) + ' → ' + fmtTs(r.endedAt))),
        h('div', { class: 'spacer' }), picker, DBM.components.badge(r.status), exportBtn),
      h('div', { class: 'grid-kpi' },
        kpi('Rows loaded', X.num(r.rowsLoaded), X.totalText(r.rowsSource, missing) + ' source rows'),
        kpi('Rejected', X.num(r.rowsError),
          r.rowsError ? (r.rowsSource > 0 ? X.pctText(r.rowsError, r.rowsSource) + ' of source · logged' : 'logged') : 'none',
          r.rowsError ? 'warn' : 'ok'),
        // A rate priced from a duration nobody established is no rate, not 0 rows/s.
        kpi('Duration', r.durationSec === null || r.durationSec === undefined ? 'unknown' : X.fmtEta(r.durationSec),
          r.rowsPerSec === null || r.rowsPerSec === undefined ? 'throughput unknown' : X.fmtRate(r.rowsPerSec)),
        kpi('Row counts', X.countHeadline(counts),
          counts.notCompared ? counts.notCompared + ' of ' + counts.total + ' tasks were not compared'
            : (counts.mismatched ? counts.mismatched + ' mismatched' : 'every task matches'),
          counts.mismatched ? 'err' : (counts.notCompared ? 'warn' : 'ok')),
        // "differ" is the counted number, never total - matched: a column nobody compared is a gap, not a difference.
        kpi('Checksums', X.checksumHeadline(sums),
          sums.total ? (sums.differ ? sums.differ + ' differ' : 'every column compared matches') : 'not computed',
          sums.differ ? 'err' : (sums.notCompared ? 'warn' : (sums.total ? 'ok' : '')))),
      // "of at least": while a task has no source count the total above it is a floor, not a total.
      missing ? DBM.components.notice('warn', missing + (missing === 1 ? ' task has' : ' tasks have')
        + ' no source row count, so the source total above is a floor and the percentages are priced from it.') : null,
      // Ruling 186: over a target that already held rows the counts compare rows added, not the table.
      tasks.some(function (t) { return t.rowsBefore > 0; }) ? DBM.components.notice('warn', 'Target tables were not empty before this run; '
        + 'the row counts compare the rows it added, not the tables\u2019 contents. A table with no key can hold its rows twice.') : null,
      tasksCard(tasks),
      sums.total ? h('div', { class: 'muted small rep-caveat' },
        'Column checksums are sums of per-row BINARY_CHECKSUM values; equal sums do not prove identical rows, because two changed '
        + 'rows can cancel out. The row counts are the other half of the check.') : null);
    const withSamples = tasks.filter(function (t) { return (t.errorSamples || []).length || t.errorSamplesNote; });
    if (withSamples.length) page.appendChild(samplesCard(withSamples));
    page.appendChild(h('div', { class: 'grid-2' }, notesCard(r.notes || []), optionsCard(r.options || {})));
    root.appendChild(page);
  }

  /**
   * Ruling 117: HasReport is true only when a Complete artifact was stored. A run that failed, was cancelled or is still paused has
   * none, and this screen says which of those it is, with the run's own error and notes - an empty report would read as a clean one.
   */
  function renderWithoutReport(root, ctx) {
    const page = h('div', { class: 'page rep-page' });
    root.appendChild(page);
    if (ctx.export || !DBM.api) {
      page.appendChild(DBM.components.emptyState('No final report in this file',
        'This export was built without a completed run’s report.'));
      return;
    }
    page.appendChild(h('div', { class: 'empty' }, 'Loading the run…'));
    DBM.api.get('/api/transfer').then(function (v) {
      clear(page);
      const run = v.run;
      if (!run) {
        page.appendChild(DBM.components.emptyState('No final report yet',
          v.planNote || 'The report is created when a transfer run completes. Follow the run on the Execute screen.'));
        return;
      }
      const why = run.status === 'completed'
        ? 'Run #' + run.id + ' completed, but no final report was stored for it, so there is nothing here to show. '
          + 'The report is written when the run finishes validation; this run’s was not.'
        : 'Run #' + run.id + ' ' + DBM.statusLabel(run.status).toLowerCase() + ', so no final report was written for it. '
          + 'A report describes a run that finished; this one did not.';
      page.appendChild(h('div', { class: 'page-h row' },
        h('div', { class: 'stack' }, h('div', { class: 'h1' }, 'Final report'),
          h('div', { class: 'muted small' }, 'Run #' + run.id + ' · ' + fmtTs(run.startedAt) + ' → ' + fmtTs(run.endedAt))),
        h('div', { class: 'spacer' }), DBM.components.badge(run.status)));
      page.appendChild(DBM.components.notice(run.status === 'failed' ? 'err' : 'warn', h('div', { class: 'stack-sm' },
        h('div', {}, why),
        run.error ? h('div', { class: 'wrap-anywhere' }, run.error) : null)));
      if (run.notes && run.notes.length) page.appendChild(notesCard(run.notes));
    }).catch(function (e) {
      clear(page);
      page.appendChild(DBM.components.notice('err', 'The run behind this report could not be read: ' + X.refusalText(e)));
    });
  }

  function tasksCard(tasks) {
    const tbody = h('tbody', {});
    tasks.forEach(function (t) {
      const detail = h('tr', { class: 'rep-detail' }, h('td', { colspan: '10' }, taskDetail(t)));
      detail.hidden = true;
      const row = h('tr', { class: 'tr-click', tabindex: '0', 'aria-expanded': 'false' },
        h('td', { class: 'mono' }, t.taskId),
        h('td', { class: 'mono ellipsis', title: t.target }, t.target),
        h('td', {}, DBM.components.badge(t.status),
          t.statusNote ? h('div', { class: 'exe-task-note' }, t.statusNote) : null),
        h('td', { class: 'num', title: t.rowsSourceNote || '' },
          num(t.rowsSource), t.rowsSourceNote ? h('span', { class: 'rep-flag', 'aria-hidden': 'true' }, '*') : null),
        h('td', { class: 'num' }, X.num(t.rowsLoaded)),
        h('td', { class: 'num' + (t.rowsError ? ' rep-mismatch' : '') }, X.num(t.rowsError)),
        h('td', {}, countMark(t)),
        h('td', {}, checksumCell(t)),
        h('td', { class: 'num' }, t.durationSec === null || t.durationSec === undefined ? 'unknown' : X.fmtEta(t.durationSec)),
        h('td', { class: 'num' }, t.durationSec > 0 ? X.fmtRate(t.rowsLoaded / t.durationSec) : '—'));
      const toggle = function () {
        detail.hidden = !detail.hidden;
        row.setAttribute('aria-expanded', String(!detail.hidden));
      };
      row.addEventListener('click', toggle);
      row.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
      tbody.appendChild(row);
      tbody.appendChild(detail);
    });
    const heads = ['Task', 'Target', 'Status', 'Source', 'Loaded', 'Rejected', 'Count', 'Checksums', 'Duration', 'Rows/s'];
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Tasks'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, 'Select a row for checksum details and error samples')),
      h('div', { class: 'card-b rep-table-wrap' },
        h('table', { class: 'tbl tbl-compact rep-tasks' },
          h('thead', {}, h('tr', {}, ...heads.map(function (c) { return h('th', { scope: 'col' }, c); }))), tbody)));
  }

  function taskDetail(t) {
    const box = h('div', { class: 'stack rep-detail-body' });
    if (t.error) box.appendChild(h('div', { class: 'sev-high wrap-anywhere' }, t.error));
    // Every note the engine attached to this task, next to the number it qualifies.
    const info = X.countMatchInfo(t);
    if (info.note) box.appendChild(h('div', { class: 'small' }, 'Row counts: ' + info.note));
    if (t.rowsSourceNote) box.appendChild(h('div', { class: 'small' }, 'Source row count: ' + t.rowsSourceNote));
    if (t.statusNote) box.appendChild(h('div', { class: 'small' }, t.statusNote));
    if ((t.checksums || []).length) {
      box.appendChild(h('table', { class: 'tbl tbl-compact rep-checksums' },
        h('thead', {}, h('tr', {}, h('th', {}, 'Column'), h('th', {}, 'Source checksum'), h('th', {}, 'Target checksum'), h('th', {}, 'Result'))),
        h('tbody', {}, ...t.checksums.map(function (c) {
          return h('tr', {}, h('td', { class: 'mono' }, c.column),
            h('td', { class: 'num mono' }, c.source === null || c.source === undefined ? 'null (empty)' : String(c.source)),
            h('td', { class: 'num mono' }, c.target === null || c.target === undefined ? 'null (empty)' : String(c.target)),
            h('td', {}, matchMark(c.match)));
        }))));
    } else {
      box.appendChild(h('div', { class: 'muted small' }, 'Checksums: ' + (t.checksumsSkipped ? 'skipped — ' + t.checksumsSkipped : 'not computed')));
    }
    if (t.checksumColumnsNote) box.appendChild(h('div', { class: 'small' }, t.checksumColumnsNote));
    if ((t.errorSamples || []).length || t.errorSamplesNote) box.appendChild(samplesBlock(t));
    return box;
  }

  function samplesList(samples) {
    return h('ul', { class: 'rep-samples' }, ...samples.map(function (s) {
      // The separator is a text node, not the grid gap: read aloud these two run together into "Id=88213Cannot insert…".
      return h('li', {}, h('span', { class: 'mono small' }, X.keyText(s.key)), ' ', h('span', { class: 'small' }, s.error));
    }));
  }

  /**
   * The rejected rows themselves are customer data and stay on the server (GET /api/transfer/errors, behind the token, for the
   * Execute screen's drawer). What a report carries is the count and the keyed samples the engine chose.
   */
  function samplesBlock(t) {
    const samples = t.errorSamples || [];
    return h('div', { class: 'stack-sm' },
      h('div', { class: 'row' }, h('span', { class: 'mono' }, t.target), ' ', h('span', { class: 'muted small' },
        X.num(t.rowsError) + (t.rowsError === 1 ? ' rejected row; ' : ' rejected rows; ')
        + (samples.length ? samples.length + ' sample' + (samples.length === 1 ? '' : 's') + ' below' : 'no samples were recorded'))),
      samples.length ? samplesList(samples) : null,
      t.errorSamplesNote ? h('div', { class: 'small' }, t.errorSamplesNote) : null);
  }

  function samplesCard(tasks) {
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Rejected row samples'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, 'Keys and messages only — the rejected rows stay in the workspace')),
      h('div', { class: 'card-b stack' }, ...tasks.map(samplesBlock)));
  }

  function notesCard(notes) {
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Notes')),
      h('div', { class: 'card-b' }, notes.length
        ? h('ul', { class: 'rep-notes' }, ...notes.map(function (n) { return h('li', {}, n); }))
        : h('div', { class: 'muted' }, 'Nothing to note.')));
  }

  function optionsCard(o) {
    const chip = function (text) { return h('span', { class: 'chip' }, text); };
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Run options')),
      h('div', { class: 'card-b row-wrap' },
        chip('Chunk ' + X.num(o.chunkSize)),
        chip(o.parallelism + ' parallel'),
        chip(o.errorMode === 'skip' ? 'Skip and log bad rows' : 'Stop on first bad row'),
        chip(o.truncateTarget ? 'Target truncated first' : 'Appended to target'),
        chip(o.validateChecksums ? 'Checksums on' : 'Checksums off'),
        o.tableLock ? chip('Table lock') : null,
        o.fireTriggers ? chip('Triggers fired') : null,
        o.keepControlTable ? chip('Checkpoint table kept') : null));
  }

  function exportReport(btn, ctx) {
    btn.disabled = true;
    fetch(DBM.api.url('/api/export/report'), { cache: 'no-store' })
      .then(function (res) {
        if (res.ok) return res.blob();
        // 404 export_unavailable: the builder found no stored Complete artifact. Its sentence says so; ours would not.
        return res.json().then(
          function (e) { throw new Error(e.message || 'Export failed (HTTP ' + res.status + ')'); },
          function () { throw new Error('Export failed (HTTP ' + res.status + ')'); });
      })
      .then(function (blob) {
        const url = URL.createObjectURL(blob);
        const a = h('a', { href: url, download: 'final-report.html' });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        ctx.toast('Final report exported', 'ok');
      })
      .catch(function (e) { ctx.toast(e.message || String(e), 'err'); })
      .then(function () { btn.disabled = false; });
  }

  const view = { title: 'Final report', render: render };
  DBM.views.complete = view;
  DBM.views.report = view;
})(window.DBM = window.DBM || {});
