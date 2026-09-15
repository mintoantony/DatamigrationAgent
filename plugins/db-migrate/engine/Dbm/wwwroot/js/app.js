/* App shell: routing over the phase stepper, live refresh from SSE, banners, theme, export (read-only) mode. */
(function (DBM) {
  'use strict';

  var h = DBM.h;
  var C = DBM.components;
  var REVIEWABLE = { analysis: true, mapping: true, sql: true };
  var REFRESH_EVENTS = {
    state_changed: 1, artifact_created: 1, feedback_changed: 1, job_started: 1, job_done: 1, job_failed: 1,
    paused: 1, resumed: 1, agent_presence: 1, drift_detected: 1, transfer_run_changed: 1,
  };
  var THEMES = ['system', 'light', 'dark'];

  var S = {
    state: null,
    current: null,        // phase shown
    picked: false,        // user chose a phase explicitly
    pickedDefault: null,  // the default phase at the moment the user picked
    artifact: null,       // GET /api/artifact/{phase}
    viewed: null,         // {version, author, summary, payload} being shown (null = current)
    viewedVersion: null,  // explicit older version, or null for current
    feedback: [],
    logs: [],
    live: true,
    viewKey: null,
    view: null,
    ctx: null,
    refreshTimer: null,
    loading: null,
  };

  /* ------------------------------------------------------------------ theme */

  function theme() {
    try { var t = localStorage.getItem('dbm.theme'); return THEMES.indexOf(t) >= 0 ? t : 'system'; } catch (e) { return 'system'; }
  }

  function setTheme(t) {
    try { localStorage.setItem('dbm.theme', t); } catch (e) { /* storage blocked: session only */ }
    if (t === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', t);
  }

  function cycleTheme() {
    setTheme(THEMES[(THEMES.indexOf(theme()) + 1) % THEMES.length]);
    renderChrome();
  }

  /* ------------------------------------------------------------------ routing */

  function defaultPhase(state) {
    var open = state.phases.filter(function (p) { return p.status !== 'approved'; })[0];
    return open ? open.name : 'complete';
  }

  function phaseRow(name) {
    return S.state.phases.filter(function (p) { return p.name === name; })[0] || S.state.phases[0];
  }

  /** Which view renders a phase (C9): dedicated view when it has something to show, else the pending view. */
  function pickView(row) {
    if (row.name === 'setup') return DBM.views.setup;
    var custom = DBM.views[row.name];
    if (REVIEWABLE[row.name]) {
      var reviewable = (row.status === 'awaiting_review' || row.status === 'reworking' || row.status === 'approved') && row.currentVersion != null;
      return custom && reviewable ? custom : DBM.views.pending;
    }
    return custom && row.status !== 'pending' && row.status !== 'stale' ? custom : DBM.views.pending;
  }

  function select(name) {
    if (name === S.current) return;
    S.current = name;
    S.picked = name !== defaultPhase(S.state);
    S.pickedDefault = defaultPhase(S.state);
    S.viewedVersion = null;
    if (window.location.hash !== '#/' + name) window.history.replaceState(null, '', '#/' + name);
    refresh(true);
  }

  /* ------------------------------------------------------------------ data */

  function loadPhaseData(row) {
    if (row.currentVersion == null || row.name === 'setup') {
      S.artifact = null;
      S.viewed = null;
      S.feedback = [];
      return Promise.resolve();
    }
    var base = '/api/artifact/' + row.name;
    return Promise.all([
      DBM.api.get(base),
      DBM.api.get('/api/feedback/' + row.name),
      S.viewedVersion != null ? DBM.api.get(base + '/' + S.viewedVersion) : Promise.resolve(null),
    ]).then(function (r) {
      S.artifact = r[0];
      S.feedback = r[1] || [];
      S.viewed = r[2] || (r[0] && r[0].current) || null;
    });
  }

  function refresh(force) {
    if (S.loading) { S.loading.again = S.loading.again || force || true; return S.loading.promise; }
    var job = { again: false };
    job.promise = DBM.api.get('/api/state').then(function (state) {
      S.state = state;
      var def = defaultPhase(state);
      if (!S.current || !S.picked || S.pickedDefault !== def) {
        if (S.current !== def) S.viewedVersion = null;
        S.current = def;
        S.picked = false;
      }
      var row = phaseRow(S.current);
      return loadPhaseData(row).then(function () {
        renderChrome();
        var key = viewKey(row);
        if (force === true || key !== S.viewKey) {
          var view = pickView(row);
          var ctx = makeCtx(row);
          if (view === S.view && view.holdRender && view.holdRender(ctx)) {
            // The view holds unsaved state this render would destroy and declined it. Keep the view on screen; give the
            // shell (and the review drawer) the fresh ctx; leave S.viewKey stale so every later refresh asks again.
            S.ctx = ctx;
          } else {
            S.viewKey = key;
            renderView(row, view, ctx);
          }
        }
        DBM.review.refresh(S.ctx);
      });
    }).catch(function (err) {
      renderChrome();
      if (err && err.status === 401) {
        renderFatal('This page needs its access link.', 'Open the UI again from Claude Code with /db-migrate ui (or run "dbm ui").');
      } else {
        C.toast(C.errorText(err), 'err');
      }
    }).then(function () {
      S.loading = null;
      if (job.again) return refresh(job.again === true ? false : job.again);
    });
    S.loading = job;
    return job.promise;
  }

  function scheduleRefresh() {
    clearTimeout(S.refreshTimer);
    S.refreshTimer = setTimeout(function () { refresh(false); }, 150);
  }

  /** Re-render the view only when something it shows changed (keeps inputs and focus stable). */
  function viewKey(row) {
    var st = S.state;
    return JSON.stringify([
      row.name, row.status, row.currentVersion, row.approvedVersion, S.viewedVersion,
      S.feedback.map(function (f) { return [f.id, f.status]; }),
      st.project.paused, st.project.agentOnline,
      st.connections && [st.connections.src, st.connections.tgt],
      (st.jobs || []).filter(function (j) { return j.phase === row.name; }).map(function (j) { return [j.id, j.status]; }),
      st.phases.map(function (p) { return p.status; }),
      st.drift, st.transfer,
    ]);
  }

  /* ------------------------------------------------------------------ rendering */

  function makeCtx(row) {
    var art = S.artifact;
    var ctx = {
      state: S.state,
      phase: row.name,
      phaseRow: row,
      artifact: S.viewed || null,                     // {version, author, summary, createdAt, payload} being shown
      versions: art ? art.versions || [] : [],        // [ArtifactMeta], ascending
      latestVersion: art && art.current ? art.current.version : null,
      version: S.viewed ? S.viewed.version : null,
      export: null,
      feedback: S.feedback,
      logs: S.logs,
      readOnly: row.status !== 'awaiting_review' || S.viewedVersion != null,
      api: DBM.api,
      refresh: function () { return refresh(true); },
      toast: C.toast,
      commentable: function (el, anchor, label) { return commentable(ctx, el, anchor, label); },
      setVersion: function (v) {
        var latest = art && art.current ? art.current.version : null;
        S.viewedVersion = v == null || v === latest ? null : v;
        refresh(true);
      },
      openFeedback: function (anchor, label) { DBM.review.openFeedback(ctx, anchor, label); },
      openHistory: function () { DBM.review.openHistory(ctx); },
    };
    return ctx;
  }

  function commentable(ctx, el, anchor, label) {
    if (!el || window.DBM_EXPORT) return el;
    var pending = ctx.feedback.some(function (f) { return (f.anchor || null) === (anchor || null) && (f.status === 'draft' || f.status === 'open'); });
    if (pending && anchor) el.classList.add('has-feedback');
    if (ctx.readOnly) return el;
    el.classList.add('commentable');
    el.appendChild(h('button', {
      type: 'button',
      class: 'comment-btn',
      title: 'Comment on ' + (label || DBM.anchorLabel(anchor)),
      'aria-label': 'Comment on ' + (label || DBM.anchorLabel(anchor)),
      on: { click: function (e) { e.preventDefault(); e.stopPropagation(); ctx.openFeedback(anchor, label); } },
    }, C.icon('comment')));
    return el;
  }

  function renderChrome() {
    var top = document.getElementById('topbar');
    DBM.clear(top).appendChild(C.topbar(S.state, {
      live: S.live,
      theme: theme(),
      onTheme: cycleTheme,
      onPause: function () { DBM.api.post('/api/pause').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); }); },
      onResume: function () { DBM.api.post('/api/resume').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); }); },
    }));
    if (!S.state) return;
    var stepper = document.getElementById('stepper');
    DBM.clear(stepper).appendChild(C.stepper(S.state, { current: S.current, onSelect: select }));
    var active = stepper.querySelector('.step.is-active');
    if (active && stepper.scrollWidth > stepper.clientWidth) active.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    renderBanners();
    document.title = S.state.project.name + ' · ' + DBM.phaseTitle(S.current) + ' — db-migrate';
  }

  function renderBanners() {
    var host = DBM.clear(document.getElementById('banners'));
    var st = S.state;
    var next = st.next || {};
    if (st.project.paused) {
      host.appendChild(C.banner('info', 'Paused — Claude will not start new work until you resume.',
        h('button', { type: 'button', class: 'btn btn-primary btn-sm', on: { click: function () { DBM.api.post('/api/resume').then(function () { refresh(false); }); } } }, 'Resume')));
    }
    if (next.action === 'agent' && !st.project.agentOnline) {
      host.appendChild(C.banner('warn', h('span', null, 'Agent offline — run ', h('code', null, '/db-migrate resume'), ' in Claude Code to let Claude continue ', DBM.phaseTitle(next.phase), '.')));
    }
    if (next.action === 'stop' && next.reason === 'job_failed') {
      var retry = h('button', { type: 'button', class: 'btn btn-sm' }, C.icon('refresh'), 'Retry');
      retry.addEventListener('click', function () {
        C.busy(retry, function () {
          return DBM.api.post('/api/phase/' + next.phase + '/retry').then(function () { refresh(false); }, function (e) { C.toast(C.errorText(e), 'err'); });
        });
      });
      host.appendChild(C.banner('err', h('span', null, 'A background job failed in ', h('strong', null, DBM.phaseTitle(next.phase)), ': ', next.summary || 'unknown error'), retry));
    }
  }

  /**
   * Views may implement holdRender(nextCtx) → true to decline a re-render that would destroy unsaved state (the shell then
   * keeps them on screen), and receive render(root, ctx, {rerender}) where rerender says the same view is being redrawn
   * over itself (as opposed to a first render after navigation), so it can carry in-flight state across.
   */
  function renderView(row, view, ctx) {
    var root = DBM.clear(document.getElementById('view'));
    var rerender = view === S.view;
    S.view = view;
    S.ctx = ctx;
    try {
      view.render(root, S.ctx, { rerender: rerender });
    } catch (err) {
      root.appendChild(h('div', { class: 'page' }, C.notice('err', 'This screen failed to render: ' + (err && err.message))));
      if (window.console) console.error(err);
    }
  }

  function renderFatal(title, text) {
    DBM.clear(document.getElementById('view')).appendChild(h('div', { class: 'page' }, C.emptyState(title, text)));
  }

  /* ------------------------------------------------------------------ events */

  function onEvent(evt) {
    if (evt.type === 'log') {
      S.logs.push({ ts: new Date().toISOString(), level: evt.data.level || 'info', message: evt.data.message || '' });
      if (S.logs.length > 300) S.logs.splice(0, S.logs.length - 300);
    }
    if (S.view && S.view.onEvent) {
      try { S.view.onEvent(evt, S.ctx); } catch (err) { if (window.console) console.error(err); }
    }
    if (REFRESH_EVENTS[evt.type]) scheduleRefresh();
  }

  function onStatus(connected) {
    if (S.live === connected) return;
    S.live = connected;
    renderChrome();
    if (connected) scheduleRefresh();
  }

  /* ------------------------------------------------------------------ export mode */

  /** A standalone export may not contain index.html's skeleton: build whatever container is missing. */
  function ensureShell() {
    if (!document.getElementById('app')) {
      document.body.appendChild(h('div', { id: 'app', class: 'app' },
        h('header', { id: 'topbar', class: 'topbar' }),
        h('div', { id: 'banners', class: 'banners' }),
        h('nav', { id: 'stepper', class: 'stepper', 'aria-label': 'Migration phases' }),
        h('main', { id: 'view', class: 'main', tabindex: '-1' })));
    }
    if (!document.getElementById('drawer')) document.body.appendChild(h('aside', { id: 'drawer', class: 'drawer', 'aria-hidden': 'true' }));
    if (!document.getElementById('toasts')) document.body.appendChild(h('div', { id: 'toasts', class: 'toasts', role: 'status' }));
  }

  /**
   * DBM_EXPORT = {view, title, payload: {artifact: {version, author, summary, createdAt, payload}, state?, …}}.
   * Read-only, no API (api.js is not part of an export), no SSE, no review actions.
   */
  function renderExport(x) {
    ensureShell();
    document.body.classList.add('is-export');
    DBM.clear(document.getElementById('topbar')).appendChild(C.topbar(null, { title: x.title, theme: theme(), onTheme: function () { cycleTheme(); renderExport(x); } }));
    var view = DBM.views[x.view] || DBM.views.pending;
    var root = DBM.clear(document.getElementById('view'));
    var data = x.payload || {};
    var a = data.artifact || {};
    var artifact = {
      version: a.version == null ? null : a.version,
      author: a.author || null,
      summary: a.summary || null,
      createdAt: a.createdAt || null,
      payload: a.payload === undefined ? null : a.payload,
    };
    var row = { name: x.view, status: 'approved', currentVersion: artifact.version, approvedVersion: artifact.version };
    var ctx = {
      state: data.state || {
        project: { name: x.title, paused: false, agentOnline: false }, phases: [row], jobs: [], connections: {},
        drift: { src: false, tgt: false }, transfer: null, next: {},
      },
      phase: x.view,
      phaseRow: row,
      artifact: artifact,
      versions: [],
      latestVersion: artifact.version,
      version: artifact.version,
      export: data,
      feedback: [],
      logs: [],
      readOnly: true,
      api: null,
      refresh: function () { return Promise.resolve(); },
      toast: C.toast,
      commentable: function (el) { return el; },
      setVersion: function () {},
      openFeedback: function () {},
      openHistory: function () {},
    };
    document.title = x.title;
    view.render(root, ctx);
  }

  /* ------------------------------------------------------------------ start */

  function start() {
    if (window.DBM_EXPORT) {
      renderExport(window.DBM_EXPORT);
      return;
    }
    var fromHash = /^#\/([a-z_]+)$/.exec(window.location.hash || '');
    if (fromHash) { S.current = fromHash[1]; S.picked = true; }
    window.addEventListener('hashchange', function () {
      var m = /^#\/([a-z_]+)$/.exec(window.location.hash || '');
      if (m && S.state) select(m[1]);
    });
    renderChrome();
    refresh(true).then(function () {
      if (S.picked) S.pickedDefault = defaultPhase(S.state);
    });
    DBM.api.events(onEvent, onStatus);
  }

  DBM.app = { refresh: refresh, select: select, state: function () { return S.state; } };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})(window.DBM = window.DBM || {});
