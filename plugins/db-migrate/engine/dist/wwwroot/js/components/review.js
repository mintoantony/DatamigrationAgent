/* Review loop UI: review bar, feedback drawer (anchored comments + agent responses), version history with diff. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var R = DBM.review = {};
  var REVIEWABLE = { analysis: true, mapping: true, sql: true };
  var drawerState = null;      // {kind: 'feedback'|'history', anchor, label, text, from, to}
  var payloadCache = {};       // "phase:version" → payload

  /** Human label for a feedback anchor (C5 anchor grammar). */
  DBM.anchorLabel = function (anchor, fallback) {
    if (!anchor) return fallback || 'General';
    var side = function (s) { return s === 'src' ? 'Source' : s === 'tgt' ? 'Target' : s; };
    var m;
    if ((m = /^finding:(.+)$/.exec(anchor))) return 'Finding ' + m[1];
    if ((m = /^table:(src|tgt):(.+)$/.exec(anchor))) return side(m[1]) + ' table ' + m[2];
    if ((m = /^column:(src|tgt):(.+)$/.exec(anchor))) return side(m[1]) + ' column ' + m[2];
    if (anchor === 'narrative') return 'Narrative';
    if ((m = /^tablemap:(.+)$/.exec(anchor))) return 'Mapping of ' + m[1];
    if ((m = /^colmap:(.+)$/.exec(anchor))) return 'Column mapping ' + m[1];
    if ((m = /^task:(.+)$/.exec(anchor))) return 'Task ' + m[1];
    if ((m = /^sql:([^:]+):(\d+)$/.exec(anchor))) return 'Task ' + m[1] + ', line ' + m[2];
    return fallback || anchor;
  };

  function transferPending(ctx) {
    var t = ctx.state && ctx.state.phases.filter(function (p) { return p.name === 'transfer'; })[0];
    return !t || t.status === 'pending';
  }

  /** Ruling 185: the server's own sentence for why the plan cannot change now, or null when it can - before the first run and after
   *  a completed, cancelled or failed one (a failed run is cancelled by the reopen). An older server without state.transfer falls back
   *  to the pre-185 rule. */
  function changesLocked(ctx) {
    var t = ctx.state && ctx.state.transfer;
    if (t && typeof t === 'object') return t.changesLocked || null;
    return transferPending(ctx) ? null : 'The transfer has started.';
  }
  R.changesLocked = changesLocked;

  function counts(ctx) {
    var c = { draft: 0, open: 0, all: ctx.feedback.length };
    ctx.feedback.forEach(function (f) { if (c[f.status] != null) c[f.status]++; });
    return c;
  }

  /* ------------------------------------------------------------------ review bar */

  /**
   * opts.blocked: a reason string. The view on screen is known to be stale (a newer version exists that it has not shown), so
   * Approve and Request changes — which act on the server's current version — are disabled and the reason is shown.
   */
  C.reviewBar = function (ctx, opts) {
    var row = ctx.phaseRow;
    var versions = ctx.versions || [];
    var shown = ctx.artifact;                 // {version, author, summary, createdAt, payload}
    var latest = ctx.latestVersion;
    if (window.DBM_EXPORT) return exportBar(row, shown);
    var viewingOld = !!shown && latest != null && shown.version !== latest;
    var n = counts(ctx);
    var canReview = row.status === 'awaiting_review' && !viewingOld;
    var blocked = (opts && opts.blocked) || null;

    var picker = h('select', {
      class: 'select',
      name: 'version',
      'aria-label': 'Version',
      on: { change: function (e) { ctx.setVersion(Number(e.target.value)); } },
    }, versions.slice().reverse().map(function (v) {
      var label = 'v' + v.version + ' · ' + v.author + (v.version === latest ? ' (current)' : '');
      return h('option', { value: String(v.version), selected: shown && v.version === shown.version }, label);
    }));

    var approveBtn = h('button', { type: 'button', class: 'btn btn-primary', disabled: !!blocked, title: blocked }, C.icon('check'), 'Approve');
    approveBtn.addEventListener('click', function () { approve(ctx, approveBtn); });
    var changesBtn = h('button', {
      type: 'button',
      class: 'btn',
      disabled: n.draft === 0 || !!blocked,
      title: blocked || (n.draft === 0 ? 'Add at least one comment first' : 'Send your comments to Claude'),
    }, 'Request changes' + (n.draft ? ' (' + n.draft + ')' : ''));
    changesBtn.addEventListener('click', function () { requestChanges(ctx, changesBtn, n.draft); });

    var reopenBtn = null;
    if (row.status === 'approved' && REVIEWABLE[ctx.phase] && !window.DBM_EXPORT) {
      var locked = changesLocked(ctx);
      if (!locked) {
        reopenBtn = h('button', { type: 'button', class: 'btn btn-sm' }, 'Reopen');
        reopenBtn.addEventListener('click', function () { reopen(ctx, reopenBtn); });
      } else {
        // A Reopen the server would refuse is shown disabled with its reason, never simply missing.
        reopenBtn = h('button', { type: 'button', class: 'btn btn-sm', disabled: true, title: locked }, 'Reopen');
      }
    }

    return h('div', { class: 'review-bar' },
      versions.length ? picker : null,
      C.badge(row.status),
      shown ? h('span', { class: 'tag', title: 'Author of this version' }, shown.author) : null,
      h('span', { class: 'review-summary ellipsis', title: (shown && shown.summary) || '' }, (shown && shown.summary) || ''),
      h('div', { class: 'review-actions' },
        h('button', { type: 'button', class: 'btn btn-ghost', on: { click: function () { ctx.openFeedback(null, null); } } },
          C.icon('comment'), 'Feedback' + (n.all ? ' (' + n.all + ')' : '')),
        h('button', { type: 'button', class: 'btn btn-ghost', on: { click: function () { ctx.openHistory(); } } }, C.icon('history'), 'History'),
        reopenBtn,
        canReview ? changesBtn : null,
        canReview ? approveBtn : null),
      blocked && canReview ? h('div', { class: 'review-blocked', style: { flexBasis: '100%' } }, C.notice('warn', blocked)) : null,
      viewingOld ? h('div', { style: { flexBasis: '100%' } }, C.notice('info', h('span', null,
        'You are viewing v' + shown.version + '. The current version is v' + latest + '. ',
        h('a', { href: '#', on: { click: function (e) { e.preventDefault(); ctx.setVersion(null); } } }, 'Show current')))) : null);
  };

  /** Export mode: facts only, no actions. */
  function exportBar(row, shown) {
    return h('div', { class: 'review-bar' },
      shown && shown.version != null ? h('span', { class: 'tag' }, 'v' + shown.version) : null,
      C.badge(row.status),
      shown && shown.author ? h('span', { class: 'tag' }, shown.author) : null,
      h('span', { class: 'review-summary ellipsis', title: (shown && shown.summary) || '' }, (shown && shown.summary) || ''),
      shown && shown.createdAt ? h('span', { class: 'small muted' }, DBM.fmt.ts(shown.createdAt)) : null);
  }

  function approve(ctx, btn) {
    C.busy(btn, function () {
      return ctx.api.post('/api/phase/' + ctx.phase + '/approve').then(function () {
        C.toast(DBM.phaseTitle(ctx.phase) + ' approved.', 'ok');
        ctx.refresh();
      }, function (err) {
        if (err.code === 'blocked' || err.code === 'guard') {
          return C.modal({
            title: 'Not ready to approve',
            body: h('div', { class: 'stack-sm' },
              h('p', { style: { margin: '0' } }, err.message),
              err.details && err.details.length ? h('ul', { style: { margin: '0', paddingLeft: '20px' } },
                err.details.map(function (d) { return h('li', null, d); })) : null),
            confirmText: 'OK',
            cancelText: null,
          });
        }
        C.toast(C.errorText(err), 'err');
      });
    });
  }

  function requestChanges(ctx, btn, drafts) {
    C.busy(btn, function () {
      return ctx.api.post('/api/phase/' + ctx.phase + '/request-changes').then(function () {
        C.toast('Sent ' + drafts + ' comment' + (drafts === 1 ? '' : 's') + ' to Claude.', 'ok');
        ctx.refresh();
      }, function (err) { C.toast(C.errorText(err), 'err'); });
    });
  }

  function reopen(ctx, btn) {
    var later = ctx.state.phases
      .filter(function (p) { return REVIEWABLE[p.name] && p.name !== ctx.phase && p.status !== 'pending'; })
      .filter(function (p) { return ctx.state.phases.indexOf(p) > ctx.state.phases.map(function (x) { return x.name; }).indexOf(ctx.phase); })
      .map(function (p) { return DBM.phaseTitle(p.name); });
    var t = (ctx.state && ctx.state.transfer) || {};
    var text = later.length
      ? 'You can edit and re-approve it. ' + later.join(' and ') + ' will be marked stale and must be approved again.'
      : 'You can add feedback and approve it again.';
    // Ruling 185: what happens to the run that is already there, said before the operator confirms.
    if (t.runStatus === 'failed') {
      text += ' Transfer run #' + t.runId + ' failed: reopening cancels it first, which runs the plan’s post-load SQL to re-enable '
        + 'what its pre-load SQL disabled. Rows it committed stay in the target.';
    } else if (t.runStatus === 'completed' || t.runStatus === 'cancelled') {
      text += ' Transfer run #' + t.runId + ' (' + t.runStatus + ') and its report stay as they are; once SQL is approved again, '
        + 'Execute starts a new run.';
    }
    C.modal({
      title: 'Reopen ' + DBM.phaseTitle(ctx.phase) + '?',
      body: text,
      confirmText: 'Reopen',
    }).then(function (ok) {
      if (!ok) return;
      C.busy(btn, function () {
        return ctx.api.post('/api/phase/' + ctx.phase + '/reopen').then(function () { ctx.refresh(); },
          function (err) { C.toast(C.errorText(err), 'err'); });
      });
    });
  }

  /* ------------------------------------------------------------------ drawer */

  R.openFeedback = function (ctx, anchor, label) {
    drawerState = { kind: 'feedback', anchor: anchor || null, label: label || null, text: drawerState && drawerState.text || '' };
    render(ctx, false);
  };

  R.openHistory = function (ctx) {
    drawerState = { kind: 'history', from: null, to: null, text: drawerState && drawerState.text || '' };
    render(ctx, false);
  };

  /** Called by app.js after every refresh so an open drawer shows fresh data. */
  R.refresh = function (ctx) {
    if (drawerState && C.drawer.isOpen()) render(ctx, true);
  };

  R.isOpen = function () { return !!drawerState && C.drawer.isOpen(); };

  function render(ctx, keepFocus) {
    var active = document.activeElement;
    var activeId = active && active.closest && active.closest('#drawer') ? active.id : null;
    var caret = activeId && typeof active.selectionStart === 'number' ? active.selectionStart : null;
    var tabs = h('div', { class: 'drawer-tabs', role: 'tablist' },
      tab(ctx, 'feedback', 'Feedback'), tab(ctx, 'history', 'History'));
    var body = drawerState.kind === 'history' ? historyPanel(ctx) : feedbackPanel(ctx);
    C.drawer.open(DBM.phaseTitle(ctx.phase) + ' review', body, {
      tabs: tabs,
      keepFocus: keepFocus,
      onClose: function () { drawerState = null; },
    });
    if (activeId) {
      var again = document.getElementById(activeId);
      if (again) {
        again.focus();
        if (caret != null && again.setSelectionRange) again.setSelectionRange(caret, caret);
      }
    }
  }

  function tab(ctx, kind, label) {
    return h('button', {
      type: 'button',
      role: 'tab',
      class: ['drawer-tab', drawerState.kind === kind && 'is-active'],
      'aria-selected': drawerState.kind === kind ? 'true' : 'false',
      on: { click: function () { drawerState.kind = kind; render(ctx, true); } },
    }, label);
  }

  function feedbackPanel(ctx) {
    var canComment = !ctx.readOnly && ctx.phaseRow.currentVersion != null && !window.DBM_EXPORT;
    var composer;
    if (canComment) {
      var text = h('textarea', {
        id: 'fb-text',
        class: 'textarea',
        rows: '4',
        placeholder: 'What should change? Be specific — Claude only sees your comments and the element they point at.',
        'aria-label': 'Comment',
      });
      text.value = drawerState.text || '';
      text.addEventListener('input', function () { drawerState.text = text.value; });
      var add = h('button', { type: 'submit', class: 'btn btn-primary btn-sm' }, 'Add comment');
      var form = h('form', { class: 'stack-sm fb-section' },
        drawerState.anchor
          ? h('div', { class: 'row' }, h('span', { class: 'chip', title: drawerState.anchor },
            DBM.anchorLabel(drawerState.anchor, drawerState.label),
            h('button', {
              type: 'button',
              'aria-label': 'Comment on the whole version instead',
              on: { click: function () { drawerState.anchor = null; drawerState.label = null; render(ctx, true); } },
            }, '×')))
          : h('div', { class: 'small muted' }, 'General comment on v' + ctx.phaseRow.currentVersion),
        text,
        h('div', { class: 'row' },
          h('span', { class: 'small muted spacer' }, 'Comments stay drafts until you click Request changes.'),
          add));
      form.addEventListener('submit', function (e) {
        e.preventDefault();
        var value = text.value.trim();
        if (!value) { text.focus(); return; }
        C.busy(add, function () {
          return ctx.api.post('/api/feedback/' + ctx.phase, { anchor: drawerState.anchor, text: value }).then(function () {
            drawerState.text = '';
            ctx.refresh();
          }, function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
      text.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) form.requestSubmit ? form.requestSubmit() : add.click();
      });
      composer = form;
    } else {
      composer = h('div', { class: 'fb-section' }, C.notice('info',
        ctx.phaseRow.status === 'reworking' ? 'Claude is working on the comments below.'
          : ctx.phaseRow.status === 'approved' ? 'This phase is approved. Reopen it to add comments.'
            : 'Comments can be added while the phase awaits your review.'));
    }

    var focus = drawerState.anchor;
    var sorted = ctx.feedback.slice().sort(function (a, b) {
      var fa = a.anchor === focus ? 0 : 1;
      var fb = b.anchor === focus ? 0 : 1;
      return fa - fb || b.id - a.id;
    });
    var groups = [
      { title: 'Drafts', hint: 'not sent yet', items: sorted.filter(function (f) { return f.status === 'draft'; }) },
      { title: 'Sent to Claude', hint: 'being worked on', items: sorted.filter(function (f) { return f.status === 'open'; }) },
      { title: 'Answered', hint: '', items: sorted.filter(function (f) { return f.status === 'addressed' || f.status === 'declined'; }) },
    ];

    return h('div', null, composer,
      ctx.feedback.length ? null : C.emptyState('No comments yet', 'Hover any element in the page and click the comment icon, or write a general comment above.'),
      groups.filter(function (g) { return g.items.length; }).map(function (g) {
        return h('section', { class: 'fb-section' },
          h('h3', { class: 'h3' }, g.title, h('span', { class: 'badge' }, String(g.items.length)), g.hint ? h('span', { class: 'small muted' }, g.hint) : null),
          g.items.map(function (f) { return feedbackItem(ctx, f); }));
      }));
  }

  function feedbackItem(ctx, f) {
    var del = null;
    if (f.status === 'draft' && !ctx.readOnly) {
      del = h('button', { type: 'button', class: 'btn btn-ghost btn-sm icon-btn', 'aria-label': 'Delete draft comment' }, C.icon('x'));
      del.addEventListener('click', function () {
        C.busy(del, function () {
          return ctx.api.del('/api/feedback/' + f.id).then(function () { ctx.refresh(); },
            function (err) { C.toast(C.errorText(err), 'err'); });
        });
      });
    }
    return h('article', { class: 'fb-item' },
      h('div', { class: 'fb-head' },
        C.badge(f.status),
        h('span', { class: 'tag', title: f.anchor || 'general' }, DBM.anchorLabel(f.anchor)),
        h('span', { class: 'spacer' }),
        h('span', { class: 'small muted', title: DBM.fmt.ts(f.createdAt) }, 'v' + f.version + ' · ' + DBM.fmt.rel(f.createdAt)),
        del),
      h('div', { class: 'fb-text prose', html: DBM.mdLite(f.text) }),
      f.response ? h('div', { class: ['fb-response', f.status === 'declined' && 'is-declined'] },
        h('div', { class: 'small muted' }, (f.status === 'declined' ? 'Declined' : 'Addressed') + ' by Claude in v' + f.respondedVersion),
        h('div', { class: 'prose', html: DBM.mdLite(f.response) })) : null);
  }

  function historyPanel(ctx) {
    var versions = ctx.versions || [];
    if (!versions.length) return C.emptyState('No versions yet', 'Versions appear once the first draft exists.');
    var last = versions[versions.length - 1].version;
    if (drawerState.to == null) drawerState.to = last;
    if (drawerState.from == null) drawerState.from = versions.length > 1 ? versions[versions.length - 2].version : last;

    var list = h('ul', { class: 'version-list' }, versions.slice().reverse().map(function (v) {
      return h('li', null,
        h('span', { class: 'tag' }, 'v' + v.version),
        h('div', { class: 'spacer', style: { minWidth: '0' } },
          h('div', { class: 'ellipsis', title: v.summary || '' }, v.summary || h('span', { class: 'muted' }, 'No summary')),
          h('div', { class: 'small muted' }, v.author + ' · ' + DBM.fmt.rel(v.createdAt))),
        h('button', {
          type: 'button',
          class: 'btn btn-ghost btn-sm',
          on: { click: function () { ctx.setVersion(v.version === last ? null : v.version); C.drawer.close(); } },
        }, 'View'));
    }));

    function versionSelect(key, label) {
      return h('label', { class: 'row small' }, label, h('select', {
        class: 'select',
        on: { change: function (e) { drawerState[key] = Number(e.target.value); render(ctx, true); } },
      }, versions.map(function (v) {
        return h('option', { value: String(v.version), selected: v.version === drawerState[key] }, 'v' + v.version);
      })));
    }

    var diffHost = h('div', { class: 'stack-sm' }, h('div', { class: 'row small muted' }, h('span', { class: 'spinner' }), 'Loading diff…'));
    loadDiff(ctx, drawerState.from, drawerState.to, diffHost);

    return h('div', { class: 'stack' },
      h('section', null, h('h3', { class: 'h3' }, 'Versions'), list),
      h('section', { class: 'stack-sm' },
        h('div', { class: 'row-wrap' }, h('h3', { class: 'h3', style: { margin: '0' } }, 'Compare'),
          h('span', { class: 'spacer' }), versionSelect('from', 'from'), versionSelect('to', 'to')),
        diffHost));
  }

  function payloadOf(ctx, version) {
    var key = ctx.phase + ':' + version;
    if (payloadCache[key]) return Promise.resolve(payloadCache[key]);
    return ctx.api.get('/api/artifact/' + ctx.phase + '/' + version).then(function (a) {
      payloadCache[key] = a.payload;
      return a.payload;
    });
  }

  function loadDiff(ctx, from, to, host) {
    Promise.all([payloadOf(ctx, from), payloadOf(ctx, to)]).then(function (pair) {
      var ops = DBM.diff.lines(JSON.stringify(pair[0], null, 2), JSON.stringify(pair[1], null, 2));
      var s = DBM.diff.stats(ops);
      var rows = DBM.diff.collapse(ops, 3).map(function (o) {
        if (o.op === 'skip') return h('div', { class: 'diff-skip' }, '… ' + o.count + ' unchanged line' + (o.count === 1 ? '' : 's'));
        return h('div', { class: 'diff-' + o.op }, o.text || ' ');
      });
      DBM.clear(host);
      host.appendChild(h('div', { class: 'row small' },
        h('span', { class: 'sev-low' }, 'v' + from + ' → v' + to),
        h('span', { class: 'spacer' }),
        h('span', { style: { color: 'var(--ok)' } }, '+' + s.add),
        h('span', { style: { color: 'var(--err)' } }, '−' + s.del)));
      host.appendChild(from === to || (!s.add && !s.del)
        ? C.notice('info', 'No differences.')
        : h('div', { class: 'diff', role: 'region', 'aria-label': 'Differences' }, rows));
    }, function (err) {
      DBM.clear(host);
      host.appendChild(C.notice('err', C.errorText(err)));
    });
  }
})(window.DBM = window.DBM || {});
