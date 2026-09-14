CREATE TABLE project (id INTEGER PRIMARY KEY CHECK (id = 1), name TEXT NOT NULL, created_at TEXT NOT NULL,
  paused INTEGER NOT NULL DEFAULT 0, agent_seen_at TEXT, settings_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE connection (side TEXT PRIMARY KEY CHECK (side IN ('src','tgt')), encrypted TEXT NOT NULL,
  server_meta_json TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE phase (name TEXT PRIMARY KEY, ordinal INTEGER NOT NULL, status TEXT NOT NULL, current_version INTEGER,
  approved_version INTEGER, approved_fingerprint TEXT, updated_at TEXT NOT NULL);
CREATE TABLE artifact (id INTEGER PRIMARY KEY AUTOINCREMENT, phase TEXT NOT NULL, version INTEGER NOT NULL,
  payload_json TEXT NOT NULL, author TEXT NOT NULL, summary TEXT, created_at TEXT NOT NULL, UNIQUE (phase, version));
CREATE TABLE feedback (id INTEGER PRIMARY KEY AUTOINCREMENT, phase TEXT NOT NULL, version INTEGER NOT NULL, anchor TEXT,
  text TEXT NOT NULL, status TEXT NOT NULL, response TEXT, responded_version INTEGER, created_at TEXT NOT NULL);
CREATE TABLE event (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, type TEXT NOT NULL, payload_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE job (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, phase TEXT, status TEXT NOT NULL, error TEXT,
  created_at TEXT NOT NULL, started_at TEXT, ended_at TEXT);
CREATE TABLE catalog (side TEXT PRIMARY KEY, snapshot_json TEXT NOT NULL, fingerprint TEXT NOT NULL, extracted_at TEXT NOT NULL);
CREATE TABLE vector (side TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, text TEXT NOT NULL, vec_json TEXT NOT NULL,
  PRIMARY KEY (side, kind, key));
CREATE TABLE transfer_run (id INTEGER PRIMARY KEY AUTOINCREMENT, sql_version INTEGER NOT NULL, status TEXT NOT NULL,
  options_json TEXT NOT NULL, started_at TEXT, ended_at TEXT, summary_json TEXT);
CREATE TABLE transfer_task (run_id INTEGER NOT NULL, task_id TEXT NOT NULL, target TEXT NOT NULL, ordinal INTEGER NOT NULL,
  status TEXT NOT NULL, rows_source INTEGER, rows_before INTEGER, rows_done INTEGER NOT NULL DEFAULT 0,
  rows_error INTEGER NOT NULL DEFAULT 0, last_key_json TEXT, heartbeat_at TEXT, started_at TEXT, ended_at TEXT,
  error TEXT, validation_json TEXT, PRIMARY KEY (run_id, task_id));
CREATE TABLE error_row (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, task_id TEXT NOT NULL,
  key_json TEXT, row_json TEXT, error TEXT NOT NULL, ts TEXT NOT NULL);
