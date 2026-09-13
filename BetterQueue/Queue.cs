using CeeFind.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CeeFind.BetterQueue
{
    internal class CeeFindQueue
    {
        private Dictionary<int, QueuedDirectory> done = new Dictionary<int, QueuedDirectory>();
        internal string[] FileNameFilters { get; }
        private Regex[] FileNameFilterRegex { get; }
        private Regex[] NegativeFileNameFilterRegex { get; }
        private List<string> insideFileFilter;
        internal List<Regex> InsideFileFilterRegex { get; set; }
        private DirectoryInfo RootDirectory { get; }
        internal List<string> Root { get; }
        private int RootHash { get; }
        private Stuff stuff;
        private Dictionary<long, QueuedDirectory> preQueue;
        private char separator;
        private PriorityQueue<QueuedDirectory, double> queue;

        /// <summary>
        /// Memoises directory probes made while rebasing remembered paths onto this root.
        /// Remembered locations share suffixes heavily - every repo with a 'src' proposes the
        /// same candidate - so without this the same handful of probes is repeated hundreds
        /// of times per search.
        /// </summary>
        private readonly Dictionary<string, bool> directoryExists =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly long INDEX_LOOKUP_SCORE = 1000000;
        private readonly long BASE_SCORE = 100;

        /// <summary>
        /// Upper bound on index candidates considered per filter. The index is a heuristic
        /// accelerator - anything beyond this is still reached by the ordinary walk - so
        /// capping it keeps startup bounded no matter how large the index grows.
        /// </summary>
        private const int IndexCandidateLimit = 2000;

        public CeeFindQueue(
            char separator,
            Stuff stuff,
            DirectoryInfo rootDirectory,
            List<string> fileNameFilter,
            List<string> negativeFilenameFilter,
            List<string> fileFilterRegex,
            SearchSettings searchSettings)
        {
            this.FileNameFilters = fileNameFilter.ToArray();
            RegexOptions caseSensitivity = searchSettings.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            this.FileNameFilterRegex = fileNameFilter.Select(f => new Regex(f, caseSensitivity | RegexOptions.Compiled)).ToArray();
            this.NegativeFileNameFilterRegex = negativeFilenameFilter.Select(f => new Regex(f, caseSensitivity | RegexOptions.Compiled)).ToArray();
            this.insideFileFilter = fileFilterRegex;
            this.InsideFileFilterRegex = fileFilterRegex.Select(f => new Regex(f, caseSensitivity | RegexOptions.Compiled)).ToList();
            this.RootDirectory = rootDirectory;
            List<string> rootPathList = rootDirectory.FullName.Split(separator).ToList();
            this.Root = rootPathList;
            this.RootHash = rootDirectory.FullName.GetHashCode();
            this.stuff = stuff;
            this.queue = new PriorityQueue<QueuedDirectory, double>();
            this.preQueue = new Dictionary<long, QueuedDirectory>();
            this.separator = separator;
        }

        public override string ToString()
        {
            string filesPositive = String.Join(" && ", FileNameFilterRegex.Select(r => r.ToString()));
            string filesNegative = String.Join(" || ", NegativeFileNameFilterRegex.Select(r => r.ToString()));

            StringBuilder sb = new StringBuilder();
            sb.Append("files: ");
            if (FileNameFilterRegex.Any() && NegativeFileNameFilterRegex.Any())
            {
                sb.Append($"({filesPositive}) && !({filesNegative})");
            }
            else if (FileNameFilterRegex.Any())
            {
                sb.Append($"{filesPositive}");
            }
            else if (NegativeFileNameFilterRegex.Any())
            {
                sb.Append($"!({filesNegative})");
            }
            else
            {
                sb.Append("*");
            }

            if (InsideFileFilterRegex.Any())
            {
                string inFiles = String.Join(" && ", InsideFileFilterRegex.Select(r => r.ToString()));
                sb.Append(", contents: ");
                sb.Append(inFiles);
            }
            return sb.ToString();
        }

        public void Initialize()
        {
            // Use indexes to allow for fast find
            UseIndexForFilenameSearch();
            MoveFromPreQueueToQueue();

            QueuedDirectory startPath = QueuedDirectory.InitializeRoot(this.RootDirectory, stuff);
            startPath.IsRoot = true;
            queue.Enqueue(startPath, int.MinValue);
        }

        public void EnqueueSubfolder(DirectoryInfo parent, DirectoryInfo[] subfolders)
        {
            int parentHash = parent.FullName.GetHashCode();
            foreach (DirectoryInfo subfolder in subfolders)
            {
                // Skip symlinks and junctions to prevent endless recursion
                if ((subfolder.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    continue;
                }

                if (done.TryGetValue(subfolder.FullName.GetHashCode(), out QueuedDirectory qd)
                    && qd.IsVisited)
                {
                    continue;
                }

                stuff.TryGetVertex(subfolder.Name, out Vertex vertex);
                double score = BASE_SCORE;
                if (vertex == null)
                {
                    vertex = new Vertex(subfolder.Name);
                    stuff.AddVertex(vertex);
                }

                score = GenerateScore(vertex, vertex, score, 0);
                score = BoostScoreBasedOnDate(score, subfolder.LastWriteTimeUtc);
                QueueUpVertex(score, vertex, subfolder, parentHash);

            }
            MoveFromPreQueueToQueue();
        }

        public void AddAdjacents(DirectoryInfo start, Vertex startVertex, int firstParentHash)
        {
            int currentParentHash = firstParentHash;
            QueuedDirectory currentQueuedDirectory;
            int depth = 1;
            while (currentParentHash != RootHash)
            {
                if (!done.TryGetValue(currentParentHash, out currentQueuedDirectory))
                {
                    break;
                }
                UpdateAdjacents(depth, currentQueuedDirectory, startVertex);
                currentParentHash = currentQueuedDirectory.Parent;
                depth++;
            }
        }

        private void UpdateAdjacents(int distance, QueuedDirectory current, Vertex start)
        {
            if (stuff.TryGetVertex(current.Directory.Name, out Vertex other))
            {
                Vertex.UpdateAdjacents(distance, other, start);
                Vertex.UpdateAdjacents(-distance, start, other);
            }
        }

        public QueuedDirectory Consume()
        {
            QueuedDirectory qi;
            do
            {
                if (queue.Count == 0)
                {
                    return null;
                }
                qi = queue.Dequeue();
            }
            // directories can be queued by both spidering and indexes, deduping has to happen after dequeuing 
            while (qi.IsVisited || (done.ContainsKey(qi.Id) && done[qi.Id].IsVisited));

            qi.Vertex.Visits++;
            qi.Vertex.IsDirty = true;
            qi.IsVisited = true;
            done.Add(qi.Id, qi);
            return qi;
        }

        private double GenerateScore(Vertex origin, Vertex vertex, double score, int depth)
        {
            if (depth > 2 || (depth > 0 && vertex.Name.Equals(origin.Name)))
            {
                return score;
            }

            // if the subfolder is known it can be scored, otherwise it can be ignored
            // adjust based on context of adjacent paths
            if (vertex.Adjacents != null)
            {
                foreach (Edge adjacent in vertex.Adjacents.Values)
                {
                    if (stuff.TryGetVertex(adjacent.VertexName, out Vertex subVertex))
                    {
                        score = AdjustScoreForFrequency(GenerateScore(origin, subVertex, BASE_SCORE, depth + 1), Math.Abs(adjacent.RelativePosition.Min()));
                    }
                }
            }

            if (vertex.LastFindCount != null)
            {
                score = vertex.LastFindCount.Count > 0 ? AdjustScoreForRarity(score, vertex.LastFindCount.Average()) : score;
            }
            if (vertex.PathCount > 0)
            {
                score = AdjustScoreForRarity(score, vertex.PathCount);
            }
            if (vertex.FindCount > 0)
            {
                score = AdjustScoreForFrequency(score, vertex.FindCount);
                score = AdjustScoreForFrequency(score, (double)vertex.Visits / vertex.FindCount);
                if (vertex.LastFindUtc.HasValue)
                {
                    score = BoostScoreBasedOnDate(score, vertex.LastFindUtc.Value);
                }
            }
            return score;
        }

        private void MoveFromPreQueueToQueue()
        {
            foreach (QueuedDirectory q in this.preQueue.Values)
            {
                this.queue.Enqueue(q, q.Score * -1);
            }
            this.preQueue.Clear();
        }

        /// <summary>
        /// Seeds the queue from the index.
        ///
        /// Candidate files are resolved to the set of directories worth visiting, and each
        /// of those directories is expanded exactly once at its best score. Expanding per
        /// matching file instead - as this originally did - repeats identical path
        /// rebasing work thousands of times over on a large index.
        /// </summary>
        private void UseIndexForFilenameSearch()
        {
            Dictionary<string, double> bestScoreByVertex =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            bool needInsideDetail = insideFileFilter.Count > 0;

            for (int i = 0; i < FileNameFilters.Length; i++)
            {
                string filenameFilter = FileNameFilters[i];

                if (!needInsideDetail &&
                    stuff.TryGetCandidateVertexesForExtension(
                        filenameFilter, out List<string> vertexNames, out long matchingThings))
                {
                    // Directories known to hold this extension at all.
                    Seed(bestScoreByVertex, vertexNames, AdjustScoreForRarity(INDEX_LOOKUP_SCORE, matchingThings));

                    // Directories where this exact filter has previously succeeded. A
                    // smaller, more specific set, so rarity adjustment scores it higher.
                    if (stuff.TryGetCandidateVertexesForRegex(
                            filenameFilter, out List<string> exactVertexes, out long exactCount))
                    {
                        Seed(bestScoreByVertex, exactVertexes, AdjustScoreForRarity(INDEX_LOOKUP_SCORE, exactCount));
                    }

                    continue;
                }

                Accumulate(
                    bestScoreByVertex,
                    stuff.GetThingsForRegex(filenameFilter, IndexCandidateLimit, needInsideDetail));
                Accumulate(
                    bestScoreByVertex,
                    stuff.FindThingsMatching(
                        filenameFilter, FileNameFilterRegex[i], IndexCandidateLimit, needInsideDetail));
            }

            foreach (KeyValuePair<string, double> candidate in bestScoreByVertex)
            {
                QueueUpVertex(candidate.Key, candidate.Value);
            }
        }

        private static void Seed(Dictionary<string, double> bestScoreByVertex, List<string> vertexNames, double score)
        {
            foreach (string vertexName in vertexNames)
            {
                if (!bestScoreByVertex.TryGetValue(vertexName, out double existing) || score > existing)
                {
                    bestScoreByVertex[vertexName] = score;
                }
            }
        }

        private void Accumulate(Dictionary<string, double> bestScoreByVertex, List<Thing> things)
        {
            if (things.Count == 0)
            {
                return;
            }

            double adjustedScore = AdjustScoreForRarity(INDEX_LOOKUP_SCORE, things.Count);

            foreach (Thing thing in things)
            {
                double score = adjustedScore * InsideSearchFactor(thing);

                foreach (string vertexName in thing.VertexNames)
                {
                    if (!bestScoreByVertex.TryGetValue(vertexName, out double existing) || score > existing)
                    {
                        bestScoreByVertex[vertexName] = score;
                    }
                }
            }
        }

        private double InsideSearchFactor(Thing thing)
        {
            if (insideFileFilter.Count == 0)
            {
                return 1;
            }

            double insideSearchFactor = 1;

            foreach (string insideSearchStr in insideFileFilter)
            {
                if (thing.Regexes.TryGetValue(insideSearchStr, out DateTime found))
                {
                    insideSearchFactor = BoostScoreBasedOnDate(insideSearchFactor, found);
                }
            }

            foreach (KeyValuePair<string, DateTime> foundString in thing.FoundStrings)
            {
                foreach (Regex insideSearch in InsideFileFilterRegex)
                {
                    if (insideSearch.IsMatch(foundString.Key))
                    {
                        insideSearchFactor = BoostScoreBasedOnDate(insideSearchFactor, foundString.Value);
                    }
                }
            }

            return insideSearchFactor;
        }

        private void QueueUpVertex(List<string> list, double score)
        {
            double adjustedScore = AdjustScoreForRarity(score, list.Count());

            foreach (string vertex in list)
            {
                QueueUpVertex(vertex, adjustedScore);
            }
        }

        private static double AdjustScoreForRarity(double score, double count)
        {
            // Math.Log(1) is 0, so a count of zero would divide the score to Infinity and a
            // subsequent multiply by zero would yield NaN - which silently corrupts ordering
            // in the priority queue. Clamp instead.
            double divisor = Math.Log(count + 1);
            return divisor <= double.Epsilon ? score : score / divisor;
        }

        private static double BoostScoreBasedOnDate(double score, DateTime date)
        {
            double daysSinceLastSeen = DateTime.UtcNow.Subtract(date).TotalDays;
            score = AdjustScoreForRarity(score, daysSinceLastSeen);
            return score;
        }

        private static double AdjustScoreForFrequency(double score, double frequency)
        {
            score *= Math.Log(frequency + 1.1);
            return score;
        }

        /// <summary>
        /// Queue up vertices (static directories)
        /// </summary>
        /// <param name="vertexName"></param>
        /// <param name="score"></param>
        private void QueueUpVertex(string vertexName, double score)
        {
            // Defensive: a thing can outlive the vertex it referenced if pruning retired it.
            if (!stuff.TryGetVertex(vertexName, out Vertex vertex))
            {
                return;
            }

            stuff.EnsurePathsLoaded(vertex);
            if (vertex.AbsolutePaths == null)
            {
                return;
            }

            // Locations that no longer exist are provably worthless at any age. We are
            // already resolving each path here, so reclaiming them costs no extra I/O and
            // needs no scheduled sweep.
            List<string> deadPaths = null;

            foreach (string path in vertex.AbsolutePaths.Keys.ToList())
            {
                // Simple case: the path is a subdirectory of the root
                if (path.Contains(this.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!DirectoryExists(path))
                    {
                        (deadPaths ??= new List<string>()).Add(path);
                        continue;
                    }

                    score = QueueUpVertex(score, vertex, path);
                }
                // Complex case: the path isn't a subdirectory of the root
                else
                {
                    string[] pathParts = path.Split(separator);

                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        StringBuilder testPath = new StringBuilder();
                        testPath.Append(RootDirectory.FullName);
                        for (int j = i; j < pathParts.Length; j++)
                        {
                            testPath.Append(separator);
                            testPath.Append(pathParts[j]);
                        }

                        string proposedPath = testPath.ToString();
                        if (DirectoryExists(proposedPath))
                        {
                            score = QueueUpVertex(score, vertex, proposedPath);
                            break;
                        }
                    }
                }
            }

            if (deadPaths != null)
            {
                foreach (string dead in deadPaths)
                {
                    stuff.ForgetLocation(vertex, dead);
                }
            }
        }

        private bool DirectoryExists(string path)
        {
            if (directoryExists.TryGetValue(path, out bool exists))
            {
                return exists;
            }

            exists = Directory.Exists(path);
            directoryExists[path] = exists;
            return exists;
        }

        /// <summary>
        /// Used by indexing path
        /// </summary>
        /// <param name="score"></param>
        /// <param name="vertex"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        private double QueueUpVertex(double score, Vertex vertex, string path)
        {
            if (vertex.FindCount > 0)
            {
                if (vertex.LastFindUtc.HasValue)
                {
                    score = BoostScoreBasedOnDate(score, vertex.LastFindUtc.Value);
                }
                score = AdjustScoreForFrequency(score, vertex.FindCount);
            }
            DirectoryInfo directory = new DirectoryInfo(path);
            QueueUpVertex(score, vertex, directory, directory.Parent.FullName.GetHashCode());
            return score;
        }

        private void QueueUpVertex(double score, Vertex vertex, DirectoryInfo directory, int parent)
        {
            int pathHash = directory.FullName.GetHashCode();
            if (!done.ContainsKey(directory.FullName.GetHashCode()))
            {
                if (preQueue.ContainsKey(pathHash))
                {
                    // combine the scores if already present in the prequeue
                    preQueue[pathHash].Score += score;
                }
                else
                {
                    preQueue.Add(pathHash, new QueuedDirectory(pathHash, directory, directory.Parent.FullName.GetHashCode(), vertex, score));
                }

            }
        }

        internal bool IsMore()
        {
            return this.queue.Count > 0;
        }

        internal bool IsFilenameMatch(string name)
        {
            if (FileNameFilterRegex.Length > 0)
            {
                // positive matches are logical OR operations i.e. one of *.py OR *.jsx
                bool anyMatch = false;
                for (int i = 0; i < this.FileNameFilterRegex.Length; i++)
                {
                    if (this.FileNameFilterRegex[i].IsMatch(name))
                    {
                        anyMatch |= true;
                    }
                }

                if (!anyMatch)
                {
                    return false;
                }
            }

            // negative matches are logical AND: !*.js AND !*.exe
            for (int i = 0; i < this.NegativeFileNameFilterRegex.Length; i++)
            {
                if (this.NegativeFileNameFilterRegex[i].IsMatch(name))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
