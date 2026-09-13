using CeeFind.BetterQueue;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CeeFind.Storage
{
    /// <summary>
    /// One-time import of the pre-SQLite gzipped-JSON index, so upgrading does not throw
    /// away everything the tool had learned. The source file is retired after a successful
    /// import so this never runs twice.
    /// </summary>
    internal static class LegacyStateImporter
    {
        private const string LegacyStateFileName = "state_v2.json.gz";

        internal static void ImportIfPresent(Stuff stuff, string stateDirectory)
        {
            try
            {
                string legacyFile = FindLegacyFile(stateDirectory);
                if (legacyFile == null)
                {
                    return;
                }

                LegacyState state = Read(legacyFile);
                if (state == null)
                {
                    Retire(legacyFile);
                    return;
                }

                Import(stuff, state);
                stuff.Flush();
                stuff.Checkpoint();
                Retire(legacyFile);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("Imported previous CeeFind index into the new format.");
                Console.ResetColor();
            }
            catch (Exception)
            {
                // Importing is best effort. A failure here must never stop a search - the
                // index simply starts empty and rebuilds as the user works.
            }
        }

        private static string FindLegacyFile(string stateDirectory)
        {
            string inStateDirectory = Path.Combine(stateDirectory, LegacyStateFileName);
            if (File.Exists(inStateDirectory))
            {
                return inStateDirectory;
            }

            // Builds before the state directory move kept the index beside the executable.
            string besideExecutable = Path.Combine(AppContext.BaseDirectory, LegacyStateFileName);
            return File.Exists(besideExecutable) ? besideExecutable : null;
        }

        private static LegacyState Read(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using GZipStream decompressed = new GZipStream(stream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<LegacyState>(decompressed);
        }

        private static void Import(Stuff stuff, LegacyState state)
        {
            DateTime now = DateTime.UtcNow;

            if (state.Vertexes != null)
            {
                foreach (KeyValuePair<string, LegacyVertex> entry in state.Vertexes)
                {
                    LegacyVertex source = entry.Value;
                    if (source == null)
                    {
                        continue;
                    }

                    Vertex vertex = stuff.GetOrAddVertex(entry.Key);
                    vertex.Visits = Math.Max(source.Visits, 1);
                    vertex.LastFindCount = source.LastFindCount;
                    vertex.Adjacents = source.Adjacents;

                    // The old format kept one timestamp per find. Collapse to the two values
                    // every consumer actually read: how many, and how recent.
                    if (source.LastFinds != null && source.LastFinds.Count > 0)
                    {
                        vertex.FindCount = source.LastFinds.Count;
                        vertex.LastFindUtc = source.LastFinds.Max();
                    }

                    stuff.EnsurePathsLoaded(vertex);
                    if (source.AbsolutePaths != null)
                    {
                        DateTime lastFind = vertex.LastFindUtc ?? now;
                        foreach (string path in source.AbsolutePaths)
                        {
                            vertex.RecordFindLocation(path, lastFind);
                        }
                    }

                    vertex.IsDirty = true;
                }
            }

            if (state.Things != null)
            {
                Dictionary<string, List<string>> regexesByFilename = InvertRegexIndex(state.RegexesToThings);

                foreach (KeyValuePair<string, LegacyThing> entry in state.Things)
                {
                    LegacyThing source = entry.Value;
                    if (source == null)
                    {
                        continue;
                    }

                    Thing thing = new Thing
                    {
                        Filename = entry.Key,
                        Regexes = source.Regexes ?? new Dictionary<string, DateTime>(),
                        FoundStrings = source.FoundStrings ?? new Dictionary<string, DateTime>(),
                        VertexNames = source.VertexNames ?? new List<string>(),
                    };

                    regexesByFilename.TryGetValue(entry.Key, out List<string> regexes);
                    stuff.ImportThing(thing, regexes ?? Enumerable.Empty<string>());
                }
            }

            if (state.SearchHistory != null)
            {
                foreach (KeyValuePair<string, List<Metrics>> entry in state.SearchHistory)
                {
                    if (entry.Value == null)
                    {
                        continue;
                    }

                    foreach (Metrics metrics in entry.Value.Where(m => m != null))
                    {
                        stuff.AddHistory(entry.Key, metrics);
                    }
                }
            }
        }

        private static Dictionary<string, List<string>> InvertRegexIndex(
            Dictionary<string, List<string>> regexesToThings)
        {
            Dictionary<string, List<string>> inverted =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (regexesToThings == null)
            {
                return inverted;
            }

            foreach (KeyValuePair<string, List<string>> entry in regexesToThings)
            {
                if (entry.Value == null)
                {
                    continue;
                }

                foreach (string filename in entry.Value)
                {
                    if (!inverted.TryGetValue(filename, out List<string> regexes))
                    {
                        regexes = new List<string>();
                        inverted[filename] = regexes;
                    }

                    regexes.Add(entry.Key);
                }
            }

            return inverted;
        }

        private static void Retire(string path)
        {
            try
            {
                string retired = path + ".imported";
                if (File.Exists(retired))
                {
                    File.Delete(retired);
                }

                File.Move(path, retired);
            }
            catch (Exception)
            {
                // If it cannot be renamed the import is still complete; worst case it is
                // re-imported once more on a later run.
            }
        }

        private sealed class LegacyState
        {
            [JsonPropertyName("Things")]
            public Dictionary<string, LegacyThing> Things { get; set; }

            [JsonPropertyName("Vertexes")]
            public Dictionary<string, LegacyVertex> Vertexes { get; set; }

            [JsonPropertyName("RegexesToThings")]
            public Dictionary<string, List<string>> RegexesToThings { get; set; }

            [JsonPropertyName("SearchHistory")]
            public Dictionary<string, List<Metrics>> SearchHistory { get; set; }
        }

        private sealed class LegacyThing
        {
            public string Filename { get; set; }

            public Dictionary<string, DateTime> Regexes { get; set; }

            public Dictionary<string, DateTime> FoundStrings { get; set; }

            public List<string> VertexNames { get; set; }
        }

        private sealed class LegacyVertex
        {
            public string Name { get; set; }

            public List<string> AbsolutePaths { get; set; }

            public int Visits { get; set; }

            public List<DateTime> LastFinds { get; set; }

            public Histogram LastFindCount { get; set; }

            public Dictionary<string, Edge> Adjacents { get; set; }
        }
    }
}
