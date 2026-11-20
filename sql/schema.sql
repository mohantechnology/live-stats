-- Taskbar Live Stats — SQLite schema (documentation)
--
-- Runtime still applies DDL from C# via CREATE TABLE IF NOT EXISTS:
--   src/TaskbarLiveStats/Metrics/UsageHistoryStore.cs
--   src/TaskbarLiveStats/Metrics/StartupSession.cs
--
-- Keep this file in sync when those constructors change.
--
-- Database file: %AppData%\TaskbarLiveStats\usage.db
-- Driver:        Microsoft.Data.Sqlite
--
-- Objects in use:
--   Tables:  usage_minute, uptime_minute, session_ledger
--   Indexes: PRIMARY KEY only (no extra CREATE INDEX)
--   Views / triggers / functions: none

------------------------------------------------------------------------
-- Connection PRAGMAs (set on open; not persisted as schema objects)
------------------------------------------------------------------------
-- UsageHistoryStore:
--   PRAGMA journal_mode=WAL;
--   PRAGMA busy_timeout=2000;
--   PRAGMA synchronous=NORMAL;
-- StartupSessionStore:
--   PRAGMA journal_mode=WAL;
--   PRAGMA busy_timeout=2000;
-- Read-only opens also set busy_timeout=2000.

------------------------------------------------------------------------
-- Tables
------------------------------------------------------------------------

-- Per-minute network + disk byte totals (UTC minute buckets).
-- minute_utc = Unix seconds aligned to the minute start (… / 60 * 60).
-- Writes use INSERT … ON CONFLICT(minute_utc) DO UPDATE (sum deltas).
-- Retention: rows older than 400 days are deleted on store open.
CREATE TABLE IF NOT EXISTS usage_minute (
    minute_utc INTEGER PRIMARY KEY,
    net_send   INTEGER NOT NULL,  -- bytes uploaded in that minute
    net_recv   INTEGER NOT NULL,  -- bytes downloaded
    disk_read  INTEGER NOT NULL,  -- disk read bytes
    disk_write INTEGER NOT NULL   -- disk write bytes
);

-- Per-minute machine "awake" time while the app is running.
-- awake_seconds is capped at 60 per minute on flush.
-- Same retention as usage_minute (400 days).
CREATE TABLE IF NOT EXISTS uptime_minute (
    minute_utc    INTEGER PRIMARY KEY,
    awake_seconds INTEGER NOT NULL
);

-- Singleton session ledger for "since last shutdown/restart" byte totals.
-- CHECK (id = 1) enforces a single row; UPSERT on id.
-- BYTE columns store ulong values clamped to signed INTEGER (long.MaxValue).
CREATE TABLE IF NOT EXISTS session_ledger (
    id                 INTEGER PRIMARY KEY CHECK (id = 1),
    boot_utc           INTEGER NOT NULL,  -- BootClock.Stamp()
    shutdown_filetime  INTEGER NOT NULL,  -- registry ShutdownTime FILETIME
    carry_send         INTEGER NOT NULL,  -- bytes carried across counter resets
    carry_recv         INTEGER NOT NULL,
    carry_read         INTEGER NOT NULL,
    carry_write        INTEGER NOT NULL,
    origin_send        INTEGER NOT NULL,  -- counter baselines at session start
    origin_recv        INTEGER NOT NULL,
    origin_read        INTEGER NOT NULL,
    origin_write       INTEGER NOT NULL,
    last_send          INTEGER NOT NULL,  -- last sampled OS counters
    last_recv          INTEGER NOT NULL,
    last_read          INTEGER NOT NULL,
    last_write         INTEGER NOT NULL,
    updated_utc        INTEGER NOT NULL   -- last save, Unix seconds
);

------------------------------------------------------------------------
-- Indexes
------------------------------------------------------------------------
-- No explicit CREATE INDEX. Access patterns use PRIMARY KEY lookups /
-- range filters on minute_utc and id=1, which use the PK B-tree indexes.

------------------------------------------------------------------------
-- Triggers / views / functions
------------------------------------------------------------------------
-- None. SQLite has no stored procedures; app logic lives in C#.
