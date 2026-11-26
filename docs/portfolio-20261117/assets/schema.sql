CREATE TABLE inspection_job (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_name TEXT NOT NULL, model_version TEXT NOT NULL,
    source_name TEXT NOT NULL, status TEXT NOT NULL, threshold REAL NOT NULL,
    started_at TEXT NOT NULL, ended_at TEXT NULL);

CREATE TABLE inspection_result (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL REFERENCES inspection_job(id),
    image_path TEXT NOT NULL, verdict TEXT NOT NULL, score REAL NOT NULL,
    elapsed_ms INTEGER NOT NULL, error_message TEXT NULL, inspected_at TEXT NOT NULL, heatmap BLOB NULL);

CREATE TABLE defect (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    result_id INTEGER NOT NULL REFERENCES inspection_result(id) ON DELETE CASCADE,
    defect_type TEXT NOT NULL, confidence REAL NOT NULL,
    x INTEGER NOT NULL, y INTEGER NOT NULL, w INTEGER NOT NULL, h INTEGER NOT NULL);

CREATE TABLE result_review (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    result_id INTEGER NOT NULL REFERENCES inspection_result(id) ON DELETE CASCADE,
    reviewer TEXT NOT NULL,
    corrected_verdict TEXT NOT NULL CHECK (corrected_verdict IN ('OK','NG')),
    note TEXT NULL,
    reviewed_at TEXT NOT NULL);

CREATE TABLE user_account (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    login_id TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('Operator','Engineer','Admin')),
    active INTEGER NOT NULL DEFAULT 1,
    failed_count INTEGER NOT NULL DEFAULT 0,
    locked_until TEXT NULL,
    created_at TEXT NOT NULL);

CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at TEXT NOT NULL,
    user_id INTEGER NULL,
    login_id TEXT NOT NULL,
    action TEXT NOT NULL,
    detail TEXT NULL);