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
        internal const int SchemaVersion = 11;

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

            // Must be set before anything writes a page - including journal_mode - because
            // SQLite can only choose this when the database is first created. Lets pruning
            // return space to the filesystem instead of only freeing pages for reuse.
            Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL;");

            // WAL lets concurrent `f` invocations read while one writes, which the previous
            // whole-file rewrite could not do safely. busy_timeout absorbs the remaining
            // writer contention instead of surfacing it as an error to the user.
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA temp_store=MEMORY;");

            // The index is a cache: everything in it is rediscoverable by walking. On a
            // schema change it is cheaper and safer to rebuild than to migrate.
            if (ReadUserVersion(connection) != SchemaVersion)
            {
                Reset(connection);
                Create(connection);
                Execute(connection, $"PRAGMA user_version={SchemaVersion};");
            }

            return connection;
        }

        private static void Reset(SqliteConnection connection)
        {
            Execute(connection, @"
DROP TABLE IF EXISTS search_history;
DROP TABLE IF EXISTS regex_thing;
DROP TABLE IF EXISTS thing_content;
DROP TABLE IF EXISTS thing;
DROP TABLE IF EXISTS vertex_path;
DROP TABLE IF EXISTS vertex;
DROP TABLE IF EXISTS meta;
");
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
    subtree_visits  INTEGER NOT NULL DEFAULT 0,
    subtree_finds   INTEGER NOT NULL DEFAULT 0,
    subtree_finds_by_type TEXT NULL,
    last_find_utc   INTEGER NULL,
    histogram_json  TEXT NULL,
    adjacents_json  TEXT NULL,
    path_count      INTEGER NOT NULL DEFAULT 0,
    extensions_json TEXT NULL,
    extensions_truncated INTEGER NOT NULL DEFAULT 0,
    name_filter     BLOB NULL,
    filter_files    INTEGER NOT NULL DEFAULT 0,
    mtime_files     INTEGER NOT NULL DEFAULT 0,
    mtime_distinct  INTEGER NOT NULL DEFAULT 0,
    markers         INTEGER NOT NULL DEFAULT 0,
    child_dir_total INTEGER NOT NULL DEFAULT 0,
    child_dir_samples INTEGER NOT NULL DEFAULT 0
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
    hits               INTEGER NOT NULL DEFAULT 1,
    has_content        INTEGER NOT NULL DEFAULT 0,
    vertexes_json      TEXT NULL,
    regexes_json       TEXT NULL,
    found_strings_json TEXT NULL
);

-- Narrows in-file searches to candidate files. Only content evidence and specific
-- filename hits are stored now, so this is a small, high-value table.
CREATE INDEX IF NOT EXISTS idx_thing_extension ON thing(extension);

-- Retention order: content evidence is expensive to rediscover (it means re-reading
-- every candidate file), so it outranks filename-only records. Within a tier the
-- measure is how often the entry has actually been useful, never how old it is.
CREATE INDEX IF NOT EXISTS idx_thing_tier ON thing(has_content, hits);

CREATE TABLE IF NOT EXISTS regex_thing (
    regex    TEXT NOT NULL,
    filename TEXT NOT NULL COLLATE NOCASE,
    used_utc INTEGER NOT NULL,
    PRIMARY KEY (regex, filename)
);

-- What was found *inside* files, keyed by the matched text. This is the expensive
-- knowledge - rediscovering it means re-reading every candidate file - so it gets a
-- direct seek rather than being filtered out of a wider scan in memory.
-- kind 0 = a search expression previously used, 1 = a string previously captured.
CREATE TABLE IF NOT EXISTS thing_content (
    value     TEXT NOT NULL,
    filename  TEXT NOT NULL COLLATE NOCASE,
    kind      INTEGER NOT NULL,
    found_utc INTEGER NOT NULL,
    PRIMARY KEY (value, filename, kind)
);

CREATE INDEX IF NOT EXISTS idx_content_filename ON thing_content(filename);
CREATE INDEX IF NOT EXISTS idx_content_kind ON thing_content(kind);

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
