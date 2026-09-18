/* Fallback view: phases without their own view, or waiting on a job / on Claude. Shows job status, live log, retry,
   and — when an artifact exists but no dedicated view is loaded yet — the raw artifact with the review bar. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var logHost = null;
  var logEmpty = null;

  var MESSAGES = {
    pending: ['Waiting for earlier phases', 'This phase starts automatically once the phases before it are approved.'],
    stale: ['Needs to run again', 'An earlier phase changed. This phase re-runs when that phase is approved again.'],
    running: ['Working…', 'A background job is preparing this phase. You can close the browser; it keeps running.'],
    drafting: ['Claude is preparing the first version', 'The script draft is ready; Claude is turning it into a reviewable version.'],
    reworking: ['Claude is working on your feedback', 'A new version will appear here with a response to each comment.'],
    approved: ['Approved', 'Nothing to do here.'],
    awaiting_review: ['Ready for your review', 'Review the version below, then approve it or request changes.'],
  };

  function latestJob(ctx) {
    return (ctx.state.jobs || []).filter(function (j) { return j.phase === ctx.phase; })[0] || null;
  }

  function logLine(entry) {
    return h('span', { class: ['line', entry.level === 'warn' && 'log-warn', entry.level === 'error' && 'log-error'] },
      h('span', { class: 'muted' }, DBM.fmt.ts(entry.ts).slice(11) + '  '), entry.message);
  }

  function jobCard(ctx, job) {
    if (!job) return null;
    var failed = job.status === 'failed';
    var retry = null;
    if (failed && ctx.phaseRow.status === 'running') {
      retry = h('button', { type: 'button', class: 'btn btn-primary btn-sm' }, C.icon('refresh'), 'Retry');
      retry.addEventListener('click', function () {
        C.busy(retry, function () {
          return ctx.api.post('/api/phase/' + ctx.phase + '/retry').then(function () { ctx.refresh(); },
            function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
    }
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('span', { class: 'spacer' }, 'Job ', h('span', { class: 'mono' }, job.kind)), C.badge(job.status)),
      h('div', { class: 'card-b stack-sm' },
        job.status === 'running' || job.status === 'queued' ? C.progress(0, 1, { indeterminate: true }) : null,
        h('dl', { class: 'meta' },
          h('dt', null, 'Started'), h('dd', null, job.startedAt ? DBM.fmt.rel(job.startedAt) : 'queued'),
          job.endedAt ? h('dt', null, 'Ended') : null, job.endedAt ? h('dd', null, DBM.fmt.ts(job.endedAt)) : null),
        failed ? C.notice('err', job.error || 'The job failed.') : null,
        retry ? h('div', { class: 'row' }, retry) : null));
  }

  function logCard(ctx) {
    var lines = (ctx.logs || []).slice(-200);
    logEmpty = h('p', { class: 'muted small', hidden: lines.length > 0 }, 'Log messages from background jobs appear here.');
    logHost = h('pre', { class: 'code pending-log', 'aria-label': 'Live log', hidden: lines.length === 0 }, lines.map(logLine));
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, 'Live log'),
      h('div', { class: 'card-b' }, logEmpty, logHost));
  }

  function artifactCard(ctx) {
    var current = ctx.artifact;
    if (!current) return null;
    var pre = h('pre', { class: 'code plain' }, JSON.stringify(current.payload, null, 2));
    var card = h('section', { class: 'card' },
      h('div', { class: 'card-h' }, 'Artifact v' + current.version, h('span', { class: 'spacer' }),
        h('span', { class: 'small muted' }, 'Raw view — the dedicated screen for this phase arrives in a later release.')),
      h('div', { class: 'card-b' }, pre));
    ctx.commentable(card.querySelector('.card-b'), null, 'this version');
    return card;
  }

  DBM.views = DBM.views || {};
  DBM.views.pending = {
    title: 'Status',
    render: function (root, ctx) {
      var status = ctx.phaseRow.status;
      var job = latestJob(ctx);
      var msg = MESSAGES[status] || [DBM.statusLabel(status), ''];
      if (status === 'running' && job && job.status === 'failed') msg = ['The job failed', 'Fix the cause (see the error below) and retry.'];
      var agentHint = (status === 'drafting' || status === 'reworking') && !ctx.state.project.agentOnline
        ? C.notice('warn', h('span', null, 'Claude is not connected. In Claude Code run ', h('code', null, '/db-migrate resume'), ' to continue.'))
        : null;
      // Ruling 195: a phase Claude is stuck on (a patch rejected twice) can be taken over; on a reworking phase the review bar
      // below carries the button, so here it is added only when there is no review bar.
      var hasArtifact = !!ctx.artifact && (status === 'awaiting_review' || status === 'approved' || status === 'reworking');
      var takeOver = !hasArtifact && DBM.review && DBM.review.takeOverButton ? DBM.review.takeOverButton(ctx) : null;
      var takeOverRow = takeOver ? h('div', { class: 'row-wrap' }, takeOver,
        h('span', { class: 'small muted' }, 'Stuck? Take over discards Claude’s pending work so you can review and edit v'
          + ctx.phaseRow.currentVersion + ' yourself.')) : null;
      var jobFailed = !!job && job.status === 'failed';
      var busy = !hasArtifact && !jobFailed && (status === 'running' || status === 'drafting' || status === 'reworking');

      root.appendChild(h('div', { class: 'page' },
        h('div', { class: 'page-h' },
          h('div', null, h('h1', { class: 'h1' }, DBM.phaseTitle(ctx.phase)), h('p', { class: 'muted' }, msg[1])),
          C.badge(status)),
        hasArtifact ? C.reviewBar(ctx) : null,
        h('div', { class: 'stack' },
          agentHint,
          takeOverRow,
          busy ? h('div', { class: 'row muted' }, h('span', { class: 'spinner' }), msg[0]) : null,
          hasArtifact ? artifactCard(ctx) : null,
          jobCard(ctx, job),
          status === 'running' || (job && job.status !== 'done') ? logCard(ctx) : null)));
    },
    onEvent: function (evt, ctx) {
      if (evt.type !== 'log' || !logHost || !logHost.isConnected) return;
      logHost.hidden = false;
      if (logEmpty) logEmpty.hidden = true;
      logHost.appendChild(logLine({ ts: new Date().toISOString(), level: evt.data.level, message: evt.data.message }));
      while (logHost.childNodes.length > 200) logHost.removeChild(logHost.firstChild);
      logHost.scrollTop = logHost.scrollHeight;
    },
  };
})(window.DBM = window.DBM || {});
