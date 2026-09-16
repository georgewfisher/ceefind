using CeeFind.Storage;
using CeeFind.Utils;

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
        // Retention budget. A size rather than a row count: the index loads lazily, so its
        // footprint no longer drives memory, and what actually matters is disk. Generous by
        // design - this index exists to remember locations the user no longer can - and in
        // practice usage sits well below it now that suffix-only records are not stored.
        private const long MaxIndexBytes = 256L * 1024 * 1024;

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
        /// Bound on the distinct-captured-value scan used for pattern matching in-file
        /// searches. Distinct values are far fewer than the files containing them.
        /// </summary>
        private const int DistinctContentScanLimit = 50_000;

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

        private readonly List<(string VertexName, string Path)> pendingPathDeletes =
            new List<(string, string)>();

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
                "SELECT name, visits, find_count, last_find_utc, histogram_json, adjacents_json, path_count, " +
                "subtree_visits, subtree_finds, extensions_json, extensions_truncated, subtree_finds_by_type, " +
                "name_filter, filter_files, mtime_files, mtime_distinct " +
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
                SubtreeVisits = reader.GetInt64(7),
                SubtreeFinds = reader.GetInt64(8),
                Extensions = reader.IsDBNull(9)
                    ? null
                    : new HashSet<string>(
                        JsonSerializer.Deserialize<List<string>>(reader.GetString(9), JsonOptions),
                        StringComparer.OrdinalIgnoreCase),
                ExtensionsTruncated = reader.GetInt32(10) != 0,
                SubtreeFindsByType = reader.IsDBNull(11)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(11), JsonOptions),
                NameFilter = reader.IsDBNull(12) ? null : (byte[])reader["name_filter"],
                FilterFileCount = reader.GetInt64(13),
                MtimeFiles = reader.GetInt64(14),
                MtimeDistinct = reader.GetInt64(15),
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
        /// The directories that have most often produced results, for rebasing onto the
        /// current root.
        ///
        /// This is the index's primary asset. Finding files by suffix is trivial - one walk,
        /// no file reads - so it is not worth indexing. What is genuinely hard to rediscover
        /// is which directories, in which recurring shapes, tend to hold what you want. That
        /// is what this returns, ranked by how often each has actually delivered.
        /// </summary>
        internal List<string> GetShapeSeedVertexes(int limit)
        {
            List<string> names = new List<string>();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM vertex WHERE find_count > 0 " +
                // name breaks ties so the seed set is the same from one run to the next;
                // without it SQLite may return equally-ranked rows in any order.
                "ORDER BY find_count DESC, path_count DESC, name ASC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        /// <summary>
        /// Directories where the given in-file searches have previously matched, with how
        /// recently, resolved by direct seek on the matched text.
        ///
        /// This is the index's highest-value query: without it an in-file search has to
        /// re-read every candidate file. Values are matched exactly where possible; captured
        /// strings additionally support pattern matching, done over the distinct captured
        /// values, which is a far smaller set than the files containing them.
        /// </summary>
        internal Dictionary<string, DateTime> GetContentSeedVertexes(
            List<string> insideFilters, List<Regex> insideRegexes)
        {
            Dictionary<string, DateTime> byVertex =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> values = new HashSet<string>(insideFilters, StringComparer.Ordinal);

            // Captured strings are matched by pattern, so resolve which distinct captured
            // values qualify before seeking the files that contain them.
            if (insideRegexes.Count > 0)
            {
                using SqliteCommand distinct = connection.CreateCommand();
                distinct.CommandText =
                    "SELECT DISTINCT value FROM thing_content WHERE kind = 1 LIMIT $limit;";
                distinct.Parameters.AddWithValue("$limit", DistinctContentScanLimit);

                using SqliteDataReader reader = distinct.ExecuteReader();
                while (reader.Read())
                {
                    string value = reader.GetString(0);
                    foreach (Regex pattern in insideRegexes)
                    {
                        if (pattern.IsMatch(value))
                        {
                            values.Add(value);
                            break;
                        }
                    }
                }
            }

            if (values.Count == 0)
            {
                return byVertex;
            }

            try
            {
                using SqliteCommand command = connection.CreateCommand();
                List<string> parameterNames = new List<string>();
                int index = 0;
                foreach (string value in values)
                {
                    string name = "$v" + index++;
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, value);
                }

                command.CommandText =
                    "SELECT je.value, MAX(tc.found_utc) " +
                    "FROM thing_content tc " +
                    "JOIN thing t ON t.filename = tc.filename, json_each(t.vertexes_json) je " +
                    $"WHERE tc.value IN ({string.Join(",", parameterNames)}) " +
                    "GROUP BY je.value;";

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        byVertex[reader.GetString(0)] = FromUnix(reader.GetInt64(1));
                    }
                }
            }
            catch (SqliteException)
            {
                // json_each unavailable - the walk still finds everything, just unaided.
            }

            return byVertex;
        }

        /// <summary>
        /// Reads just enough locations to seed a search, without claiming the vertex's full
        /// path set is loaded.
        ///
        /// Paths already under this root come first because they are exact hits; the rest are
        /// ordered by how often they have delivered, since only a bounded number will be
        /// speculatively rebased. Loading every location for every seeded vertex was costing
        /// tens of thousands of rows per search for candidates that were never used.
        /// </summary>
        internal List<(string Path, int Finds)> GetSeedPaths(string vertexName, string rootPrefix, int limit)
        {
            List<(string, int)> paths = new List<(string, int)>();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT path, finds FROM vertex_path WHERE vertex_name = $name " +
                "ORDER BY (CASE WHEN path LIKE $prefix THEN 0 ELSE 1 END), finds DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$name", vertexName);
            command.Parameters.AddWithValue("$prefix", Escape(rootPrefix) + "%");
            command.Parameters.AddWithValue("$limit", limit);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                paths.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            return paths;
        }

        /// <summary>
        /// Retires a location that no longer exists without paying to load the vertex's
        /// whole path set first.
        /// </summary>
        internal void ForgetLocationDirect(Vertex vertex, string absolutePath)
        {
            pendingPathDeletes.Add((vertex.Name, absolutePath));

            if (vertex.ArePathsLoaded && vertex.AbsolutePaths != null)
            {
                vertex.ForgetLocation(absolutePath);
            }
            else if (vertex.PathCount > 0)
            {
                vertex.PathCount--;
                vertex.IsDirty = true;
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("%", "\\%").Replace("_", "\\_");
        }

        internal List<Thing> GetThingsForRegex(string regex, bool needInsideDetail)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT t.filename, t.vertexes_json, t.regexes_json, t.found_strings_json " +
                "FROM regex_thing r JOIN thing t ON t.filename = r.filename " +
                "WHERE r.regex = $regex;";
            command.Parameters.AddWithValue("$regex", regex);

            return ReadThings(command, null, needInsideDetail);
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
            bool hasContent = insideRegex.Count > 0 || insideCapture.Count > 0;

            // A filename matched purely by its suffix is the cheapest thing there is to
            // rediscover - one walk, no file reads - so it earns no index space. The find is
            // still recorded against the vertex, which is where the durable value lives:
            // which directories, in which shapes, tend to hold what you are looking for.
            string[] worthRecording = hasContent
                ? filenameRegexes
                : filenameRegexes.Where(f => !FilterAnalysis.IsPureSuffix(f)).ToArray();

            if (!hasContent && worthRecording.Length == 0)
            {
                return;
            }

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

            foreach (string filenameRegex in worthRecording)
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
                WriteThingContent(transaction, thing);
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

            foreach ((string vertexName, string path) in pendingPathDeletes)
            {
                using SqliteCommand delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM vertex_path WHERE vertex_name = $name AND path = $path;";
                delete.Parameters.AddWithValue("$name", vertexName);
                delete.Parameters.AddWithValue("$path", path);
                delete.ExecuteNonQuery();
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
            pendingPathDeletes.Clear();
        }

        private void WriteVertex(SqliteTransaction transaction, Vertex vertex)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO vertex (name, visits, find_count, last_find_utc, histogram_json, adjacents_json, path_count, subtree_visits, subtree_finds, extensions_json, extensions_truncated, subtree_finds_by_type, name_filter, filter_files, mtime_files, mtime_distinct)
VALUES ($name, $visits, $findCount, $lastFind, $histogram, $adjacents, $pathCount, $subtreeVisits, $subtreeFinds, $extensions, $truncated, $findsByType, $nameFilter, $filterFiles, $mtimeFiles, $mtimeDistinct)
ON CONFLICT(name) DO UPDATE SET
    visits         = excluded.visits,
    find_count     = excluded.find_count,
    last_find_utc  = excluded.last_find_utc,
    histogram_json = excluded.histogram_json,
    adjacents_json = excluded.adjacents_json,
    path_count     = excluded.path_count,
    subtree_visits = excluded.subtree_visits,
    subtree_finds  = excluded.subtree_finds,
    extensions_json = excluded.extensions_json,
    extensions_truncated = excluded.extensions_truncated,
    subtree_finds_by_type = excluded.subtree_finds_by_type,
    name_filter    = COALESCE(excluded.name_filter, vertex.name_filter),
    filter_files   = excluded.filter_files,
    mtime_files    = excluded.mtime_files,
    mtime_distinct = excluded.mtime_distinct;";

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
            command.Parameters.AddWithValue("$subtreeVisits", vertex.SubtreeVisits);
            command.Parameters.AddWithValue("$subtreeFinds", vertex.SubtreeFinds);
            command.Parameters.AddWithValue(
                "$extensions",
                vertex.Extensions == null || vertex.Extensions.Count == 0
                    ? (object)DBNull.Value
                    : JsonSerializer.Serialize(
                        vertex.Extensions.OrderBy(e => e, StringComparer.Ordinal).ToList(), JsonOptions));
            command.Parameters.AddWithValue("$truncated", vertex.ExtensionsTruncated ? 1 : 0);
            command.Parameters.AddWithValue(
                "$findsByType",
                vertex.SubtreeFindsByType == null || vertex.SubtreeFindsByType.Count == 0
                    ? (object)DBNull.Value
                    : JsonSerializer.Serialize(vertex.SubtreeFindsByType, JsonOptions));
            command.Parameters.AddWithValue(
                "$nameFilter",
                vertex.IsFilterDirty && vertex.NameFilter != null
                    ? (object)vertex.NameFilter
                    : DBNull.Value);
            command.Parameters.AddWithValue("$filterFiles", vertex.FilterFileCount);
            command.Parameters.AddWithValue("$mtimeFiles", vertex.MtimeFiles);
            command.Parameters.AddWithValue("$mtimeDistinct", vertex.MtimeDistinct);

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

            foreach (KeyValuePair<string, PathStat> entry in vertex.AbsolutePaths)
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
INSERT INTO thing (filename, extension, last_seen_utc, hits, has_content, vertexes_json, regexes_json, found_strings_json)
VALUES ($filename, $extension, $lastSeen, 1, $hasContent, $vertexes, $regexes, $foundStrings)
ON CONFLICT(filename) DO UPDATE SET
    extension          = excluded.extension,
    last_seen_utc      = excluded.last_seen_utc,
    hits               = thing.hits + 1,
    has_content        = MAX(thing.has_content, excluded.has_content),
    vertexes_json      = excluded.vertexes_json,
    regexes_json       = excluded.regexes_json,
    found_strings_json = excluded.found_strings_json;";

            bool hasContent = thing.Regexes.Count > 0 || thing.FoundStrings.Count > 0;

            command.Parameters.AddWithValue("$filename", thing.Filename);
            command.Parameters.AddWithValue("$extension", (object)GetExtension(thing.Filename) ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSeen", ToUnix(DateTime.UtcNow));
            command.Parameters.AddWithValue("$hasContent", hasContent ? 1 : 0);
            command.Parameters.AddWithValue("$vertexes", JsonSerializer.Serialize(thing.VertexNames, JsonOptions));
            command.Parameters.AddWithValue("$regexes", JsonSerializer.Serialize(thing.Regexes, JsonOptions));
            command.Parameters.AddWithValue("$foundStrings", JsonSerializer.Serialize(thing.FoundStrings, JsonOptions));

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Mirrors a thing's in-file findings into the seekable content table.
        /// </summary>
        private void WriteThingContent(SqliteTransaction transaction, Thing thing)
        {
            if (thing.Regexes.Count == 0 && thing.FoundStrings.Count == 0)
            {
                return;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO thing_content (value, filename, kind, found_utc) " +
                "VALUES ($value, $filename, $kind, $found) " +
                "ON CONFLICT(value, filename, kind) DO UPDATE SET found_utc = excluded.found_utc;";

            SqliteParameter value = command.Parameters.Add("$value", SqliteType.Text);
            SqliteParameter filename = command.Parameters.Add("$filename", SqliteType.Text);
            SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Integer);
            SqliteParameter found = command.Parameters.Add("$found", SqliteType.Integer);

            filename.Value = thing.Filename;

            kind.Value = 0;
            foreach (KeyValuePair<string, DateTime> entry in thing.Regexes)
            {
                value.Value = entry.Key;
                found.Value = ToUnix(entry.Value);
                command.ExecuteNonQuery();
            }

            kind.Value = 1;
            foreach (KeyValuePair<string, DateTime> entry in thing.FoundStrings)
            {
                value.Value = entry.Key;
                found.Value = ToUnix(entry.Value);
                command.ExecuteNonQuery();
            }
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
        /// Enforces the retention budget.
        ///
        /// The budget is a size, not a row count, because what matters is the footprint on
        /// disk - and since the index loads lazily, that footprint no longer drives memory.
        ///
        /// Eviction runs in reverse order of how hard something is to rediscover:
        ///   1. traversal residue      - directory names that never produced a find, rebuilt
        ///                               for free by the next walk
        ///   2. filename-only records  - a specific name worth remembering, but cheap to
        ///                               find again by walking
        ///   3. content evidence       - expensive: rediscovering it means re-reading every
        ///                               candidate file
        ///   4. the shape graph        - the primary asset, and the last thing to go
        ///
        /// Within every tier the measure is how often an entry has actually been useful.
        /// Age is never a factor: an old find is what the user is least able to remember
        /// unaided, so recency ranks results but never decides what to discard.
        ///
        /// Gated, because these are full-table operations and running them after every
        /// search dominated the cost of searching against a large index.
        /// </summary>
        internal void Prune()
        {
            if (!ShouldPruneNow())
            {
                return;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            // Referential cleanup first: it is pure waste and may be enough on its own.
            Execute(transaction, "DELETE FROM vertex_path WHERE vertex_name NOT IN (SELECT name FROM vertex);");
            Execute(transaction, "DELETE FROM regex_thing WHERE filename NOT IN (SELECT filename FROM thing);");
            Execute(transaction, "DELETE FROM thing_content WHERE filename NOT IN (SELECT filename FROM thing);");

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

            // Per-vertex location cap: keep the locations that have delivered most often.
            Execute(
                transaction,
                "DELETE FROM vertex_path WHERE rowid NOT IN (" +
                "  SELECT rowid FROM vertex_path vp WHERE vp.rowid IN (" +
                "    SELECT rowid FROM vertex_path WHERE vertex_name = vp.vertex_name " +
                "    ORDER BY finds DESC LIMIT $limit));",
                ("$limit", MaxPathsPerVertex));

            long target = (long)(MaxIndexBytes * LowWaterMark);
            if (EstimateSizeBytes(transaction) > MaxIndexBytes)
            {
                PruneTier(transaction, target,
                    "DELETE FROM vertex WHERE name IN (" +
                    "  SELECT name FROM vertex WHERE find_count = 0 ORDER BY visits ASC LIMIT $limit);");

                PruneTier(transaction, target,
                    "DELETE FROM thing WHERE filename IN (" +
                    "  SELECT filename FROM thing WHERE has_content = 0 ORDER BY hits ASC LIMIT $limit);");

                PruneTier(transaction, target,
                    "DELETE FROM thing WHERE filename IN (" +
                    "  SELECT filename FROM thing WHERE has_content = 1 ORDER BY hits ASC LIMIT $limit);");

                PruneTier(transaction, target,
                    "DELETE FROM vertex WHERE name IN (" +
                    "  SELECT name FROM vertex WHERE find_count > 0 " +
                    "  ORDER BY find_count ASC, path_count ASC LIMIT $limit);");

                // Re-run referential cleanup for anything the tiers orphaned.
                Execute(transaction, "DELETE FROM vertex_path WHERE vertex_name NOT IN (SELECT name FROM vertex);");
                Execute(transaction, "DELETE FROM regex_thing WHERE filename NOT IN (SELECT filename FROM thing);");
                Execute(transaction, "DELETE FROM thing_content WHERE filename NOT IN (SELECT filename FROM thing);");
            }

            transaction.Commit();

            // Return freed pages to the filesystem. Outside the transaction, because
            // incremental_vacuum cannot run inside one.
            using SqliteCommand vacuum = connection.CreateCommand();
            vacuum.CommandText = "PRAGMA incremental_vacuum;";
            vacuum.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes from one tier in bounded batches until the index is back under target or
        /// the tier is exhausted, so a single sweep never has to sort an entire table.
        /// </summary>
        private void PruneTier(SqliteTransaction transaction, long targetBytes, string deleteSql)
        {
            const int BatchSize = 10_000;

            while (EstimateSizeBytes(transaction) > targetBytes)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = deleteSql;
                command.Parameters.AddWithValue("$limit", BatchSize);

                if (command.ExecuteNonQuery() == 0)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Current on-disk footprint. Freelist pages are excluded because they are already
        /// available for reuse.
        /// </summary>
        private long EstimateSizeBytes(SqliteTransaction transaction)
        {
            long pageSize = ScalarCount(transaction, "PRAGMA page_size;");
            long pageCount = ScalarCount(transaction, "PRAGMA page_count;");
            long freePages = ScalarCount(transaction, "PRAGMA freelist_count;");
            return Math.Max(pageCount - freePages, 0) * pageSize;
        }
        /// <summary>
        /// Cheap point read against meta, so the expensive budget work only happens
        /// periodically rather than on the critical path of every search.
        /// </summary>
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
                    "SELECT name, visits, find_count, last_find_utc, path_count FROM vertex " +
                    "ORDER BY find_count DESC;";
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
                command.CommandText =
                    "SELECT filename, hits, has_content, vertexes_json FROM thing ORDER BY hits DESC;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    writer.WriteStartObject();
                    writer.WriteString("Filename", reader.GetString(0));
                    writer.WriteNumber("Hits", reader.GetInt32(1));
                    writer.WriteBoolean("HasContent", reader.GetInt32(2) != 0);
                    if (!reader.IsDBNull(3))
                    {
                        writer.WriteString("Vertexes", reader.GetString(3));
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

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
