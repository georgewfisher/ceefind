using Microsoft.Data.Sqlite;

using System;

namespace CeeFind.Storage
{
    /// <summary>
    /// Creates and opens the on-disk index.
    ///
    /// Shape is driven by one hard requirement: resolving a directory name during a walk
    /// must be a single indexed point read. Everything the scorer needs for that decision
    /// (visits, find counts, adjacency, how many known paths exist) therefore lives on the
    /// vertex row itself. Only the absolute path list - which is enumerated on the much
    /// rarer "jump straight to a known location" path - is a child table loaded on demand.
    /// </summary>
    internal static class IndexSchema
    {
        internal const int SchemaVersion = 3;

        internal static SqliteConnection Open(string databasePath)
        {
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };

            SqliteConnection connection = new SqliteConnection(builder.ToString());
            connection.Open();

            // WAL lets concurrent `f` invocations read while one writes, which the previous
            // whole-file rewrite could not do safely. busy_timeout absorbs the remaining
            // writer contention instead of surfacing it as an error to the user.
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA temp_store=MEMORY;");

            // Re-running the DDL on every invocation cost real milliseconds on the startup
            // path. user_version lives in the database header, so this check is free.
            if (ReadUserVersion(connection) != SchemaVersion)
            {
                Create(connection);
                Execute(connection, $"PRAGMA user_version={SchemaVersion};");
            }

            return connection;
        }

        private static int ReadUserVersion(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            object value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static void Create(SqliteConnection connection)
        {
            Execute(connection, @"
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS vertex (
    name            TEXT PRIMARY KEY COLLATE NOCASE,
    visits          INTEGER NOT NULL DEFAULT 0,
    find_count      INTEGER NOT NULL DEFAULT 0,
    last_find_utc   INTEGER NULL,
    histogram_json  TEXT NULL,
    adjacents_json  TEXT NULL,
    path_count      INTEGER NOT NULL DEFAULT 0
);

-- Retention is evidence-based, so these partial indexes split the two tiers pruning
-- cares about: vertexes that have produced a find, and pure traversal residue.
CREATE INDEX IF NOT EXISTS idx_vertex_evidence
    ON vertex(find_count) WHERE find_count > 0;

CREATE INDEX IF NOT EXISTS idx_vertex_residue
    ON vertex(visits) WHERE find_count = 0;

CREATE TABLE IF NOT EXISTS vertex_path (
    vertex_name   TEXT NOT NULL COLLATE NOCASE,
    path          TEXT NOT NULL COLLATE NOCASE,
    finds         INTEGER NOT NULL DEFAULT 1,
    last_find_utc INTEGER NOT NULL,
    PRIMARY KEY (vertex_name, path)
);

CREATE TABLE IF NOT EXISTS thing (
    filename           TEXT PRIMARY KEY COLLATE NOCASE,
    extension          TEXT NULL COLLATE NOCASE,
    last_seen_utc      INTEGER NOT NULL,
    vertexes_json      TEXT NULL,
    regexes_json       TEXT NULL,
    found_strings_json TEXT NULL
);

-- Turns the overwhelmingly common '*.ext' filter into an index seek instead of the
-- full key scan the in-memory version did at the start of every search.
CREATE INDEX IF NOT EXISTS idx_thing_extension ON thing(extension);

-- Used only when a filter is not extension-shaped: lets the fallback take a bounded,
-- already-ordered slice instead of sorting the whole table. Prioritisation, not retention.
CREATE INDEX IF NOT EXISTS idx_thing_last_seen ON thing(last_seen_utc DESC);

CREATE TABLE IF NOT EXISTS regex_thing (
    regex    TEXT NOT NULL,
    filename TEXT NOT NULL COLLATE NOCASE,
    used_utc INTEGER NOT NULL,
    PRIMARY KEY (regex, filename)
);

CREATE TABLE IF NOT EXISTS search_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    root            TEXT NOT NULL COLLATE NOCASE,
    search_date_utc INTEGER NOT NULL,
    metrics_json    TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_history_root
    ON search_history(root, search_date_utc DESC);
");

            Execute(
                connection,
                "INSERT INTO meta (key, value) VALUES ('schema_version', $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                ("$v", SchemaVersion.ToString()));
        }

        private static void Execute(SqliteConnection connection, string sql, params (string, string)[] parameters)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, string value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }
    }
}
