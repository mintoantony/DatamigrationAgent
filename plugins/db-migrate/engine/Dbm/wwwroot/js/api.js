/* HTTP + SSE client. The project token comes from ?t= once and is kept in sessionStorage for this tab. */
(function (DBM) {
  'use strict';

  var KEY = 'dbm.token';

  function readToken() {
    var fromQuery = null;
    try { fromQuery = new URLSearchParams(window.location.search).get('t'); } catch (e) { fromQuery = null; }
    if (fromQuery) {
      try { sessionStorage.setItem(KEY, fromQuery); } catch (e) { /* storage blocked */ }
      return fromQuery;
    }
    try { return sessionStorage.getItem(KEY) || ''; } catch (e) { return ''; }
  }

  var TOKEN = window.DBM_EXPORT ? '' : readToken();

  function ApiError(status, code, message, details) {
    var err = new Error(message || code || 'Request failed');
    err.name = 'ApiError';
    err.status = status;
    err.code = code;
    err.details = details || [];
    Object.setPrototypeOf(err, ApiError.prototype);
    return err;
  }
  ApiError.prototype = Object.create(Error.prototype);
  ApiError.prototype.constructor = ApiError;

  function request(method, path, body) {
    var headers = { 'X-Dbm-Token': TOKEN, 'Accept': 'application/json' };
    var init = { method: method, headers: headers, cache: 'no-store' };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    return fetch(path, init).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        if (text) {
          try { data = JSON.parse(text); } catch (e) { data = { message: text }; }
        }
        if (!res.ok) {
          throw new ApiError(res.status, (data && data.error) || 'http_' + res.status,
            (data && data.message) || res.statusText, data && data.details);
        }
        return data;
      });
    }, function (networkError) {
      throw new ApiError(0, 'network', 'The db-migrate server is not reachable. Run "dbm ui" to restart it.', [String(networkError)]);
    });
  }

  /** Event types the UI listens for. Later milestones may push more before app.js starts. */
  var EVENT_TYPES = [
    'state_changed', 'artifact_created', 'feedback_changed', 'job_started', 'job_done', 'job_failed',
    'paused', 'resumed', 'agent_presence', 'drift_detected', 'log',
    'transfer_run_changed', 'transfer_task_changed', 'transfer_progress',
  ];

  /**
   * events(onEvent, onStatus) — onEvent({id, type, data}); onStatus(connected). EventSource reconnects by itself
   * (retry: 2000, Last-Event-ID); if the browser gives up (e.g. server restarted) we reconnect every 3 s.
   */
  function events(onEvent, onStatus) {
    var source = null;
    var closed = false;
    var timer = null;
    var lastId = null;

    function connect() {
      var url = '/api/events?t=' + encodeURIComponent(TOKEN) + (lastId ? '&lastEventId=' + encodeURIComponent(lastId) : '');
      source = new EventSource(url);
      source.onopen = function () { if (onStatus) onStatus(true); };
      source.onerror = function () {
        if (onStatus) onStatus(false);
        if (source.readyState === EventSource.CLOSED) {
          source.close();
          if (!closed) timer = setTimeout(connect, 3000);
        }
      };
      EVENT_TYPES.forEach(function (type) {
        source.addEventListener(type, function (e) {
          if (e.lastEventId) lastId = e.lastEventId;
          var data = {};
          try { data = e.data ? JSON.parse(e.data) : {}; } catch (err) { data = { raw: e.data }; }
          onEvent({ id: e.lastEventId ? Number(e.lastEventId) : null, type: type, data: data });
        });
      });
    }

    connect();
    return {
      close: function () {
        closed = true;
        clearTimeout(timer);
        if (source) source.close();
      },
    };
  }

  DBM.ApiError = ApiError;
  DBM.api = {
    token: function () { return TOKEN; },
    /** Same-origin URL with the token, for GET links such as downloads. */
    url: function (path) { return path + (path.indexOf('?') >= 0 ? '&' : '?') + 't=' + encodeURIComponent(TOKEN); },
    get: function (path) { return request('GET', path); },
    post: function (path, body) { return request('POST', path, body === undefined ? {} : body); },
    put: function (path, body) { return request('PUT', path, body === undefined ? {} : body); },
    del: function (path) { return request('DELETE', path); },
    events: events,
    EVENT_TYPES: EVENT_TYPES,
  };
})(window.DBM = window.DBM || {});
