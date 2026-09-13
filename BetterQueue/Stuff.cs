using CeeFind.Storage;

using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CeeFind.BetterQueue
{
    /// <summary>
    /// The index, backed by SQLite.
    ///
    /// Startup does no loading at all: vertexes are faulted in by name as the walk meets
    /// them, and every entity touched during a run is written back in one transaction at
    /// the end. This replaces deserialising - and then re-serialising - the entire graph
    /// on every single search.
    /// </summary>
    internal sealed class Stuff : IDisposable
    {
        // Retention budgets. Generous by design: this index exists to remember locations
        // the user no longer can, so pressure is aimed at traversal residue rather than
        // at earned evidence.
        private const int MaxVertexTotal = 100_000;
        private const int MaxVertexWithEvidence = 50_000;
        private const int MaxThings = 200_000;
        private const int MaxRegexes = 5_000;
        private const int MaxPathsPerVertex = 500;
        private const int MaxHistoryRoots = 500;
        private const int MaxHistoryPerRoot = 25;

        // Prune to 80% of budget so an index sitting at capacity does not re-prune on
        // every invocation.
        private const double LowWaterMark = 0.8;

        /// <summary>
        /// How many searches may pass between retention sweeps.
        /// </summary>
        private const int SearchesBetweenPrunes = 50;

        /// <summary>
        /// Matches the regex CeeFind builds for a plain '*.ext' filter, with the dot either
        /// escaped or not (Program's own conversion leaves it unescaped).
        /// </summary>
        private static readonly Regex ExtensionShapedFilter = new Regex(
            @"^\^\.\*\\?\.([A-Za-z0-9_]+)\$$",
            RegexOptions.Compiled);

        private readonly SqliteConnection connection;

        /// <summary>
        /// Identity map. Guarantees one live object per directory name so in-place mutation
        /// by the queue and the scanner stays coherent, and doubles as the write-back set.
        /// </summary>
        private readonly Dictionary<string, Vertex> vertexCache =
            new Dictionary<string, Vertex>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Thing> thingCache =
            new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Thing> dirtyThings =
            new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, HashSet<string>> dirtyRegexLinks =
            new Dictionary<string, HashSet<string>>();

        private readonly List<(string Root, Metrics Metrics)> pendingHistory =
            new List<(string, Metrics)>();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions();

        internal Stuff(string databasePath)
        {
            this.connection = IndexSchema.Open(databasePath);
        }

        // ---------------------------------------------------------------- vertexes

        /// <summary>
        /// Single indexed point read. This is the hot path - called once per directory
        /// encountered during a walk - so it must not touch child tables.
        /// </summary>
        internal bool TryGetVertex(string name, out Vertex vertex)
        {
            if (vertexCache.TryGetValue(name, out vertex))
            {
                return true;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT name, visits, find_count, last_find_utc, histogram_json, adjacents_json, path_count " +
                "FROM vertex WHERE name = $name LIMIT 1;";
            command.Parameters.AddWithValue("$name", name);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                vertex = null;
                return false;
            }

            vertex = new Vertex
            {
                Name = reader.GetString(0),
                Visits = reader.GetInt32(1),
                FindCount = reader.GetInt32(2),
                LastFindUtc = reader.IsDBNull(3) ? null : FromUnix(reader.GetInt64(3)),
                LastFindCount = reader.IsDBNull(4)
                    ? null
                    : JsonSerializer.Deserialize<Histogram>(reader.GetString(4), JsonOptions),
                Adjacents = reader.IsDBNull(5)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, Edge>>(reader.GetString(5), JsonOptions),
                PathCount = reader.GetInt32(6),
                ArePathsLoaded = false,
                IsDirty = false,
            };

            vertexCache[vertex.Name] = vertex;
            return true;
        }

        internal Vertex GetOrAddVertex(string name)
        {
            if (TryGetVertex(name, out Vertex existing))
            {
                return existing;
            }

            Vertex created = new Vertex(name);
            vertexCache[name] = created;
            return created;
        }

        internal void AddVertex(Vertex vertex)
        {
            vertexCache[vertex.Name] = vertex;
            vertex.IsDirty = true;
        }

        /// <summary>
        /// Faults in the known locations for a vertex. Deliberately separate from the point
        /// read: only the "jump straight to a remembered location" path needs them.
        /// </summary>
        internal void EnsurePathsLoaded(Vertex vertex)
        {
            if (vertex.ArePathsLoaded)
            {
                return;
            }

            Dictionary<string, PathStat> paths =
                new Dictionary<string, PathStat>(StringComparer.OrdinalIgnoreCase);

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT path, finds, last_find_utc FROM vertex_path WHERE vertex_name = $name;";
                command.Parameters.AddWithValue("$name", vertex.Name);

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    paths[reader.GetString(0)] = new PathStat(reader.GetInt32(1), FromUnix(reader.GetInt64(2)));
                }
            }

            vertex.AbsolutePaths = paths;
            vertex.PathCount = paths.Count;
            vertex.ArePathsLoaded = true;
        }

        internal void RecordFindLocation(Vertex vertex, string absolutePath, DateTime utcNow)
        {
            EnsurePathsLoaded(vertex);
            vertex.RecordFindLocation(absolutePath, utcNow);
        }

        /// <summary>
        /// Validity-based eviction, driven by the walk itself rather than a scheduled sweep.
        /// A location that no longer exists is provably worthless at any age.
        /// </summary>
        internal void ForgetLocation(Vertex vertex, string absolutePath)
        {
            EnsurePathsLoaded(vertex);
            vertex.ForgetLocation(absolutePath);
        }

        // ------------------------------------------------------------------ things

        internal bool TryGetThing(string filename, out Thing thing)
        {
            if (thingCache.TryGetValue(filename, out thing))
            {
                return true;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT filename, vertexes_json, regexes_json, found_strings_json " +
                "FROM thing WHERE filename = $filename LIMIT 1;";
            command.Parameters.AddWithValue("$filename", filename);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                thing = null;
                return false;
            }

            thing = new Thing
            {
                Filename = reader.GetString(0),
                VertexNames = reader.IsDBNull(1)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(reader.GetString(1), JsonOptions),
                Regexes = reader.IsDBNull(2)
                    ? new Dictionary<string, DateTime>()
                    : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(reader.GetString(2), JsonOptions),
                FoundStrings = reader.IsDBNull(3)
                    ? new Dictionary<string, DateTime>()
                    : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(reader.GetString(3), JsonOptions),
            };

            thingCache[thing.Filename] = thing;
            return true;
        }

        /// <summary>
        /// Exact previous use of this filter - the highest-confidence index hit available.
        /// Returned as whole things in one query: the per-filename point lookups this
        /// replaced turned a large index into thousands of round trips.
        /// </summary>
        /// <summary>
        /// Fast path for a plain '*.ext' filter with no in-file filters, which is the most
        /// common search there is.
        ///
        /// All the queue ultimately needs from the index is the set of directory names worth
        /// visiting, so this resolves that inside SQL and returns a handful of rows, rather
        /// than materialising and JSON-parsing thousands of file records to derive the same
        /// few names in memory. Scoring is unchanged: with no in-file filters every matching
        /// file contributed an identical score, so one rarity-adjusted value covers them all.
        /// </summary>
        internal bool TryGetCandidateVertexesForExtension(
            string filterPattern,
            out List<string> vertexNames,
            out long matchingThings)
        {
            vertexNames = null;
            matchingThings = 0;

            Match extensionMatch = ExtensionShapedFilter.Match(filterPattern);
            if (!extensionMatch.Success)
            {
                return false;
            }

            string extension = extensionMatch.Groups[1].Value;

            try
            {
                List<string> names = new List<string>();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT DISTINCT je.value FROM thing t, json_each(t.vertexes_json) je " +
                        "WHERE t.extension = $ext;";
                    command.Parameters.AddWithValue("$ext", extension);

                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            names.Add(reader.GetString(0));
                        }
                    }
                }

                using (SqliteCommand count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*) FROM thing WHERE extension = $ext;";
                    count.Parameters.AddWithValue("$ext", extension);
                    object value = count.ExecuteScalar();
                    matchingThings = value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
                }

                vertexNames = names;
                return true;
            }
            catch (SqliteException)
            {
                // json_each is unavailable in this SQLite build - fall back to the
                // general path rather than failing the search.
                return false;
            }
        }

        /// <summary>
        /// Directories where this exact filter previously found something. Kept distinct
        /// from the extension lookup because a confirmed prior hit is stronger evidence
        /// than "a file with this extension lives here".
        /// </summary>
        internal bool TryGetCandidateVertexesForRegex(
            string regex,
            out List<string> vertexNames,
            out long matchingThings)
        {
            vertexNames = null;
            matchingThings = 0;

            try
            {
                List<string> names = new List<string>();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT DISTINCT je.value " +
                        "FROM regex_thing r JOIN thing t ON t.filename = r.filename, " +
                        "     json_each(t.vertexes_json) je " +
                        "WHERE r.regex = $regex;";
                    command.Parameters.AddWithValue("$regex", regex);

                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            names.Add(reader.GetString(0));
                        }
                    }
                }

                using (SqliteCommand count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*) FROM regex_thing WHERE regex = $regex;";
                    count.Parameters.AddWithValue("$regex", regex);
                    object value = count.ExecuteScalar();
                    matchingThings = value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
                }

                vertexNames = names;
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        internal List<Thing> GetThingsForRegex(string regex, int limit, bool needInsideDetail)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT t.filename, t.vertexes_json, t.regexes_json, t.found_strings_json " +
                "FROM regex_thing r JOIN thing t ON t.filename = r.filename " +
                "WHERE r.regex = $regex LIMIT $limit;";
            command.Parameters.AddWithValue("$regex", regex);
            command.Parameters.AddWithValue("$limit", limit);

            return ReadThings(command, null, needInsideDetail);
        }

        /// <summary>
        /// Previously-seen files matching a filter.
        ///
        /// The in-memory version walked every key in the index here, which after this port
        /// would have meant a full table scan before the first result - the one thing that
        /// would have undermined moving to SQLite at all. An extension-shaped filter (the
        /// common case) becomes an index seek; anything else takes a bounded slice. Results
        /// are capped because this is a heuristic accelerator: whatever it misses is still
        /// reached by the ordinary walk.
        /// </summary>
        internal List<Thing> FindThingsMatching(string filterPattern, Regex compiled, int limit, bool needInsideDetail)
        {
            Match extensionMatch = ExtensionShapedFilter.Match(filterPattern);

            using SqliteCommand command = connection.CreateCommand();
            if (extensionMatch.Success)
            {
                command.CommandText =
                    "SELECT filename, vertexes_json, regexes_json, found_strings_json " +
                    "FROM thing WHERE extension = $ext LIMIT $limit;";
                command.Parameters.AddWithValue("$ext", extensionMatch.Groups[1].Value);
            }
            else
            {
                command.CommandText =
                    "SELECT filename, vertexes_json, regexes_json, found_strings_json " +
                    "FROM thing ORDER BY last_seen_utc DESC LIMIT $limit;";
            }

            command.Parameters.AddWithValue("$limit", limit);
            return ReadThings(command, compiled, needInsideDetail);
        }

        /// <summary>
        /// The inside-file columns are only consulted when the search actually has in-file
        /// filters. Skipping them otherwise avoids thousands of pointless JSON parses on the
        /// startup path of a plain filename search.
        /// </summary>
        private List<Thing> ReadThings(SqliteCommand command, Regex mustMatch, bool needInsideDetail)
        {
            List<Thing> things = new List<Thing>();

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string filename = reader.GetString(0);
                if (mustMatch != null && !mustMatch.IsMatch(filename))
                {
                    continue;
                }

                if (thingCache.TryGetValue(filename, out Thing cached))
                {
                    things.Add(cached);
                    continue;
                }

                Thing thing = new Thing
                {
                    Filename = filename,
                    VertexNames = reader.IsDBNull(1)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(reader.GetString(1), JsonOptions),
                };

                if (needInsideDetail)
                {
                    thing.Regexes = reader.IsDBNull(2)
                        ? new Dictionary<string, DateTime>()
                        : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(reader.GetString(2), JsonOptions);
                    thing.FoundStrings = reader.IsDBNull(3)
                        ? new Dictionary<string, DateTime>()
                        : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(reader.GetString(3), JsonOptions);

                    // Only a fully-populated thing is safe to reuse from cache.
                    thingCache[filename] = thing;
                }

                things.Add(thing);
            }

            return things;
        }

        internal void AddThing(
            string filename,
            string[] filenameRegexes,
            List<string> insideRegex,
            List<string> insideCapture,
            Vertex vertex)
        {
            DateTime now = DateTime.UtcNow;

            if (!TryGetThing(filename, out Thing thing))
            {
                thing = new Thing(insideRegex, insideCapture, filename, vertex.Name);
                thingCache[filename] = thing;
            }
            else
            {
                foreach (string regex in insideRegex)
                {
                    thing.Regexes[regex] = now;
                }

                foreach (string capture in insideCapture)
                {
                    thing.FoundStrings[capture] = now;
                }

                if (!thing.VertexNames.Contains(vertex.Name, StringComparer.OrdinalIgnoreCase))
                {
                    thing.VertexNames.Add(vertex.Name);
                }
            }

            dirtyThings[filename] = thing;

            foreach (string filenameRegex in filenameRegexes)
            {
                if (!dirtyRegexLinks.TryGetValue(filenameRegex, out HashSet<string> links))
                {
                    links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dirtyRegexLinks[filenameRegex] = links;
                }

                links.Add(filename);
            }
        }

        // ----------------------------------------------------------------- history

        internal List<Metrics> GetHistory(string root)
        {
            List<Metrics> history = new List<Metrics>();

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT metrics_json FROM search_history WHERE root = $root " +
                    "ORDER BY search_date_utc DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$root", root);
                command.Parameters.AddWithValue("$limit", MaxHistoryPerRoot);

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    try
                    {
                        Metrics metrics = JsonSerializer.Deserialize<Metrics>(reader.GetString(0), JsonOptions);
                        if (metrics != null)
                        {
                            history.Add(metrics);
                        }
                    }
                    catch (JsonException)
                    {
                        // A single unreadable history row must never break a search.
                    }
                }
            }

            history.AddRange(pendingHistory.Where(h =>
                string.Equals(h.Root, root, StringComparison.OrdinalIgnoreCase)).Select(h => h.Metrics));

            return history;
        }

        internal void AddHistory(string root, Metrics metrics)
        {
            pendingHistory.Add((root, metrics));
        }

        // ------------------------------------------------------------------- flush

        /// <summary>
        /// Writes back everything touched during this run in a single transaction. Only
        /// dirty entities are written, so cost scales with what the search actually changed
        /// rather than with the size of the index.
        /// </summary>
        internal void Flush()
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            foreach (Vertex vertex in vertexCache.Values)
            {
                if (!vertex.IsDirty)
                {
                    continue;
                }

                WriteVertex(transaction, vertex);

                if (vertex.ArePathsDirty && vertex.ArePathsLoaded)
                {
                    WriteVertexPaths(transaction, vertex);
                }
            }

            foreach (Thing thing in dirtyThings.Values)
            {
                WriteThing(transaction, thing);
            }

            foreach (KeyValuePair<string, HashSet<string>> link in dirtyRegexLinks)
            {
                foreach (string filename in link.Value)
                {
                    WriteRegexLink(transaction, link.Key, filename);
                }
            }

            foreach ((string root, Metrics metrics) in pendingHistory)
            {
                WriteHistory(transaction, root, metrics);
            }

            transaction.Commit();

            foreach (Vertex vertex in vertexCache.Values)
            {
                vertex.IsDirty = false;
                vertex.ArePathsDirty = false;
            }

            dirtyThings.Clear();
            dirtyRegexLinks.Clear();
            pendingHistory.Clear();
        }

        private void WriteVertex(SqliteTransaction transaction, Vertex vertex)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO vertex (name, visits, find_count, last_find_utc, histogram_json, adjacents_json, path_count)
VALUES ($name, $visits, $findCount, $lastFind, $histogram, $adjacents, $pathCount)
ON CONFLICT(name) DO UPDATE SET
    visits         = excluded.visits,
    find_count     = excluded.find_count,
    last_find_utc  = excluded.last_find_utc,
    histogram_json = excluded.histogram_json,
    adjacents_json = excluded.adjacents_json,
    path_count     = excluded.path_count;";

            command.Parameters.AddWithValue("$name", vertex.Name);
            command.Parameters.AddWithValue("$visits", vertex.Visits);
            command.Parameters.AddWithValue("$findCount", vertex.FindCount);
            command.Parameters.AddWithValue(
                "$lastFind",
                vertex.LastFindUtc.HasValue ? ToUnix(vertex.LastFindUtc.Value) : (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$histogram",
                vertex.LastFindCount == null
                    ? (object)DBNull.Value
                    : JsonSerializer.Serialize(vertex.LastFindCount, JsonOptions));
            command.Parameters.AddWithValue(
                "$adjacents",
                vertex.Adjacents == null
                    ? (object)DBNull.Value
                    : JsonSerializer.Serialize(vertex.Adjacents, JsonOptions));
            command.Parameters.AddWithValue("$pathCount", vertex.PathCount);

            command.ExecuteNonQuery();
        }

        private void WriteVertexPaths(SqliteTransaction transaction, Vertex vertex)
        {
            // Safe to replace wholesale because ArePathsLoaded guarantees the in-memory
            // dictionary is the complete set for this vertex.
            using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM vertex_path WHERE vertex_name = $name;";
                delete.Parameters.AddWithValue("$name", vertex.Name);
                delete.ExecuteNonQuery();
            }

            if (vertex.AbsolutePaths == null || vertex.AbsolutePaths.Count == 0)
            {
                return;
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO vertex_path (vertex_name, path, finds, last_find_utc) " +
                "VALUES ($name, $path, $finds, $lastFind);";

            SqliteParameter name = insert.Parameters.Add("$name", SqliteType.Text);
            SqliteParameter path = insert.Parameters.Add("$path", SqliteType.Text);
            SqliteParameter finds = insert.Parameters.Add("$finds", SqliteType.Integer);
            SqliteParameter lastFind = insert.Parameters.Add("$lastFind", SqliteType.Integer);

            name.Value = vertex.Name;

            // Keep the strongest evidence if a single name has accumulated more known
            // locations than the per-vertex budget allows.
            IEnumerable<KeyValuePair<string, PathStat>> retained = vertex.AbsolutePaths.Count > MaxPathsPerVertex
                ? vertex.AbsolutePaths.OrderByDescending(p => p.Value.Finds).Take(MaxPathsPerVertex)
                : vertex.AbsolutePaths;

            foreach (KeyValuePair<string, PathStat> entry in retained)
            {
                path.Value = entry.Key;
                finds.Value = entry.Value.Finds;
                lastFind.Value = ToUnix(entry.Value.LastFind);
                insert.ExecuteNonQuery();
            }
        }

        private void WriteThing(SqliteTransaction transaction, Thing thing)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO thing (filename, extension, last_seen_utc, vertexes_json, regexes_json, found_strings_json)
VALUES ($filename, $extension, $lastSeen, $vertexes, $regexes, $foundStrings)
ON CONFLICT(filename) DO UPDATE SET
    extension          = excluded.extension,
    last_seen_utc      = excluded.last_seen_utc,
    vertexes_json      = excluded.vertexes_json,
    regexes_json       = excluded.regexes_json,
    found_strings_json = excluded.found_strings_json;";

            command.Parameters.AddWithValue("$filename", thing.Filename);
            command.Parameters.AddWithValue("$extension", (object)GetExtension(thing.Filename) ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSeen", ToUnix(DateTime.UtcNow));
            command.Parameters.AddWithValue("$vertexes", JsonSerializer.Serialize(thing.VertexNames, JsonOptions));
            command.Parameters.AddWithValue("$regexes", JsonSerializer.Serialize(thing.Regexes, JsonOptions));
            command.Parameters.AddWithValue("$foundStrings", JsonSerializer.Serialize(thing.FoundStrings, JsonOptions));

            command.ExecuteNonQuery();
        }

        private void WriteRegexLink(SqliteTransaction transaction, string regex, string filename)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO regex_thing (regex, filename, used_utc) VALUES ($regex, $filename, $used) " +
                "ON CONFLICT(regex, filename) DO UPDATE SET used_utc = excluded.used_utc;";
            command.Parameters.AddWithValue("$regex", regex);
            command.Parameters.AddWithValue("$filename", filename);
            command.Parameters.AddWithValue("$used", ToUnix(DateTime.UtcNow));
            command.ExecuteNonQuery();
        }

        private void WriteHistory(SqliteTransaction transaction, string root, Metrics metrics)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO search_history (root, search_date_utc, metrics_json) " +
                "VALUES ($root, $date, $json);";
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$date", ToUnix(metrics.SearchDate));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(metrics, JsonOptions));
            command.ExecuteNonQuery();
        }

        // ------------------------------------------------------------------- prune

        /// <summary>
        /// Enforces the retention budgets.
        ///
        /// Gated deliberately. The budget checks and referential sweeps are full-table
        /// operations, and running them after every single search dominated the cost of a
        /// search against a large index. Growth is slow relative to the budgets, so
        /// amortising this over many searches costs nothing in practice.
        ///
        /// Order matters: residue before evidence, and never by age. A find from two years
        /// ago in a directory that still exists is exactly what the user cannot remember on
        /// their own, so it outranks a recent find for retention purposes.
        /// </summary>
        internal void Prune()
        {
            if (!ShouldPruneNow())
            {
                return;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            long totalVertexes = ScalarCount(transaction, "SELECT COUNT(*) FROM vertex;");
            if (totalVertexes > MaxVertexTotal)
            {
                long target = (long)(MaxVertexTotal * LowWaterMark);

                // Tier 1: traversal residue. Never produced a find, rebuilt for free by the
                // next walk, so it absorbs the pressure first.
                long excess = totalVertexes - target;
                Execute(
                    transaction,
                    "DELETE FROM vertex WHERE name IN (" +
                    "  SELECT name FROM vertex WHERE find_count = 0 ORDER BY visits ASC LIMIT $limit);",
                    ("$limit", excess));

                // Tier 2: only if earned evidence alone still exceeds its own budget.
                long withEvidence = ScalarCount(
                    transaction, "SELECT COUNT(*) FROM vertex WHERE find_count > 0;");
                if (withEvidence > MaxVertexWithEvidence)
                {
                    long evidenceExcess = withEvidence - (long)(MaxVertexWithEvidence * LowWaterMark);
                    Execute(
                        transaction,
                        "DELETE FROM vertex WHERE name IN (" +
                        "  SELECT name FROM vertex WHERE find_count > 0 " +
                        "  ORDER BY find_count ASC, path_count ASC LIMIT $limit);",
                        ("$limit", evidenceExcess));
                }
            }

            long things = ScalarCount(transaction, "SELECT COUNT(*) FROM thing;");
            if (things > MaxThings)
            {
                long excess = things - (long)(MaxThings * LowWaterMark);
                Execute(
                    transaction,
                    "DELETE FROM thing WHERE filename IN (" +
                    "  SELECT t.filename FROM thing t " +
                    "  ORDER BY (SELECT COUNT(*) FROM regex_thing r WHERE r.filename = t.filename) ASC " +
                    "  LIMIT $limit);",
                    ("$limit", excess));
            }

            long regexes = ScalarCount(transaction, "SELECT COUNT(DISTINCT regex) FROM regex_thing;");
            if (regexes > MaxRegexes)
            {
                long excess = regexes - (long)(MaxRegexes * LowWaterMark);
                Execute(
                    transaction,
                    "DELETE FROM regex_thing WHERE regex IN (" +
                    "  SELECT regex FROM regex_thing GROUP BY regex " +
                    "  ORDER BY COUNT(*) ASC LIMIT $limit);",
                    ("$limit", excess));
            }

            // Referential cleanup. The two lookups that dereference these names were made
            // defensive as well, but leaving dangling rows would silently waste index space.
            Execute(transaction, "DELETE FROM vertex_path WHERE vertex_name NOT IN (SELECT name FROM vertex);");
            Execute(transaction, "DELETE FROM regex_thing WHERE filename NOT IN (SELECT filename FROM thing);");

            // History is the one place age is the correct key: -h is a recency feature.
            Execute(
                transaction,
                "DELETE FROM search_history WHERE root NOT IN (" +
                "  SELECT root FROM search_history GROUP BY root " +
                "  ORDER BY MAX(search_date_utc) DESC LIMIT $limit);",
                ("$limit", MaxHistoryRoots));

            Execute(
                transaction,
                "DELETE FROM search_history WHERE id NOT IN (" +
                "  SELECT id FROM search_history sh WHERE sh.id IN (" +
                "    SELECT id FROM search_history WHERE root = sh.root " +
                "    ORDER BY search_date_utc DESC LIMIT $limit));",
                ("$limit", MaxHistoryPerRoot));

            transaction.Commit();
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Supports the -json flag. Streams the index out rather than holding it in memory.
        /// </summary>
        internal void ExportJson(string path)
        {
            using FileStream stream = File.Create(path);
            using Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();

            writer.WriteStartArray("Vertexes");
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name, visits, find_count, last_find_utc, path_count FROM vertex;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    writer.WriteStartObject();
                    writer.WriteString("Name", reader.GetString(0));
                    writer.WriteNumber("Visits", reader.GetInt32(1));
                    writer.WriteNumber("FindCount", reader.GetInt32(2));
                    if (!reader.IsDBNull(3))
                    {
                        writer.WriteString("LastFindUtc", FromUnix(reader.GetInt64(3)));
                    }
                    writer.WriteNumber("PathCount", reader.GetInt32(4));
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            writer.WriteStartArray("Things");
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT filename, vertexes_json FROM thing;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    writer.WriteStartObject();
                    writer.WriteString("Filename", reader.GetString(0));
                    if (!reader.IsDBNull(1))
                    {
                        writer.WriteString("Vertexes", reader.GetString(1));
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Forces the write-ahead log back into the main database file. Worth doing after a
        /// bulk import: leaving a multi-megabyte WAL behind makes the next few searches pay
        /// to read through it, which showed up as a dramatic one-off slowdown after upgrade.
        /// </summary>
        /// <summary>
        /// Cheap point read against meta, so the expensive budget work only happens
        /// periodically rather than on the critical path of every search.
        /// </summary>
        private bool ShouldPruneNow()
        {
            const string CounterKey = "searches_since_prune";
            long counter;

            using (SqliteCommand read = connection.CreateCommand())
            {
                read.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
                read.Parameters.AddWithValue("$key", CounterKey);
                object value = read.ExecuteScalar();
                counter = value == null || value == DBNull.Value
                    ? 0
                    : (long.TryParse(Convert.ToString(value), out long parsed) ? parsed : 0);
            }

            counter++;
            bool due = counter >= SearchesBetweenPrunes;

            using (SqliteCommand write = connection.CreateCommand())
            {
                write.CommandText =
                    "INSERT INTO meta (key, value) VALUES ($key, $value) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                write.Parameters.AddWithValue("$key", CounterKey);
                write.Parameters.AddWithValue("$value", (due ? 0 : counter).ToString());
                write.ExecuteNonQuery();
            }

            return due;
        }

        private long ScalarCount(SqliteTransaction transaction, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            object result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
        }

        private void Execute(SqliteTransaction transaction, string sql, params (string Name, long Value)[] parameters)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach ((string name, long value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }

        private static string GetExtension(string filename)
        {
            string extension = Path.GetExtension(filename);
            return string.IsNullOrEmpty(extension) ? null : extension.TrimStart('.');
        }

        private static long ToUnix(DateTime value)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }

        private static DateTime FromUnix(long value)
        {
            return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
        }

        public void Dispose()
        {
            connection?.Dispose();
        }
    }
}
