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

        /// <summary>
        /// File types this search is after, recovered from the filters however they were
        /// written. Empty means no opinion, which must never be read as "matches nothing".
        /// </summary>
        private readonly HashSet<string> targetExtensions;

        private readonly long INDEX_LOOKUP_SCORE = 1000000;
        private readonly long BASE_SCORE = 100;

        /// <summary>
        /// How many of the most productive directory names to rebase onto the current root.
        /// These are whole directories, not matched files, so the set is inherently small
        /// and each is expanded once.
        /// </summary>
        private const int ShapeSeedLimit = 50;

        /// <summary>
        /// Per-vertex bound on speculative cross-codebase rebasing. Storage is deliberately
        /// generous - every known location is kept - but probing all of them on every search
        /// is what makes a large index feel slow, so the work is capped, not the data.
        /// </summary>
        private const int RebaseAttemptsPerVertex = 32;

        /// <summary>
        /// Bound on neighbours examined per vertex. Scoring recurses two levels, so an
        /// unbounded fan-out here would be quadratic on a well-connected graph.
        /// </summary>
        private const int MaxAdjacentsConsidered = 16;

        /// <summary>
        /// How much fruitless exploration of a subtree is required before it is treated as
        /// noise. High enough that a directory merely passed through once is not condemned.
        /// </summary>
        private const long BarrenSubtreeEvidence = 25;

        /// <summary>
        /// Depth at which the structural penalty begins, and how sharply it grows.
        /// </summary>
        private const int ShallowDepth = 4;
        private const double DepthPenalty = 0.15;

        /// <summary>
        /// Safety bound on the ancestor walk, which should be short but is driven by
        /// on-disk structure.
        /// </summary>
        private const int MaxAncestorWalk = 64;

        /// <summary>
        /// How thoroughly a subtree must have been observed before its *absence* of a file
        /// type is believed. Below this the check abstains.
        /// </summary>
        private const long ExtensionEvidenceRequired = 25;

        /// <summary>
        /// Demotion applied to a directory that demonstrably holds none of the file types
        /// being searched for. Firm, but still a demotion rather than a prune - the walk
        /// reaches it if nothing better exists.
        /// </summary>
        private const double ImplausibleTypePenalty = 25.0;

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
            this.targetExtensions = FilterAnalysis.TargetExtensions(this.FileNameFilters);
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

            done.TryGetValue(parentHash, out QueuedDirectory parentDirectory);
            Vertex parentVertex = parentDirectory?.Vertex;
            int childDepth = (parentDirectory?.Depth ?? 0) + 1;

            // A subtree walked substantially without ever yielding a result. Vendored and
            // generated trees defeat per-name learning because every child name is unique,
            // so the evidence has to be held against the subtree that contains them.
            bool parentIsBarren = parentVertex != null && parentVertex.IsProvenBarren(BarrenSubtreeEvidence, targetExtensions);

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
                score = ApplyModifiedTimePrior(score, subfolder.LastWriteTimeUtc);
                score = ApplyStructuralPriors(score, childDepth, subfolders.Length);

                // A directory thoroughly observed to contain none of the file types being
                // searched for cannot satisfy this search, whatever its history says. This
                // is a fact about the directory rather than a verdict on past queries, so
                // unlike the visit/find counters it cannot be poisoned by a search for an
                // unrelated file type.
                if (vertex.SubtreeVisits >= ExtensionEvidenceRequired && !vertex.CouldContainAny(targetExtensions))
                {
                    score /= ImplausibleTypePenalty;
                }

                if (parentIsBarren)
                {
                    // Demotion grows with how much fruitless exploration has been done, and
                    // stops entirely the moment anything is found anywhere in the subtree.
                    score /= Math.Log(parentVertex.SubtreeVisits + Math.E);
                }

                QueueUpVertex(score, vertex, subfolder, parentHash, childDepth);
            }
            MoveFromPreQueueToQueue();
        }

        /// <summary>
        /// Priors that need no history: how deep a directory sits, and how diluted it is
        /// among its siblings. One of six hundred siblings is individually less likely to be
        /// what you want than one of three, and generated trees are both deep and wide.
        /// </summary>
        private static double ApplyStructuralPriors(double score, int depth, int siblingCount)
        {
            score = AdjustScoreForRarity(score, siblingCount);

            if (depth > ShallowDepth)
            {
                score /= 1.0 + ((depth - ShallowDepth) * DepthPenalty);
            }

            return score;
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

                // Credit the whole containing chain, so a subtree that does produce results
                // is never mistaken for noise.
                currentQueuedDirectory.Vertex.RecordSubtreeFind(targetExtensions);

                currentParentHash = currentQueuedDirectory.Parent;
                depth++;
            }
        }

        /// <summary>
        /// Charges a visit against every directory containing this one. Individually these
        /// children are unknowable - vendored trees name every child differently - but the
        /// containing directory accumulates a very clear picture of whether looking inside
        /// it has ever been worth the effort.
        /// </summary>
        private void RecordSubtreeVisit(int parentHash)
        {
            int currentParentHash = parentHash;
            int guard = 0;

            while (currentParentHash != RootHash && guard++ < MaxAncestorWalk)
            {
                if (!done.TryGetValue(currentParentHash, out QueuedDirectory ancestor))
                {
                    break;
                }

                ancestor.Vertex.RecordSubtreeVisit();
                currentParentHash = ancestor.Parent;
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

        /// <summary>
        /// Records the file types present in a directory, against that directory and every
        /// one containing it. Costs nothing extra: the walk has already enumerated these
        /// files, and the ancestor chain is already walked to charge the visit.
        /// </summary>
        public void RecordDirectoryContents(QueuedDirectory directory, IEnumerable<string> extensions)
        {
            HashSet<string> distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in extensions)
            {
                string normalised = FilterAnalysis.NormaliseExtension(raw);
                if (normalised != null)
                {
                    distinct.Add(normalised);
                }
            }

            if (distinct.Count == 0)
            {
                return;
            }

            foreach (string extension in distinct)
            {
                directory.Vertex.RecordExtension(extension);
            }

            int currentParentHash = directory.Parent;
            int guard = 0;
            while (currentParentHash != RootHash && guard++ < MaxAncestorWalk)
            {
                if (!done.TryGetValue(currentParentHash, out QueuedDirectory ancestor))
                {
                    break;
                }

                foreach (string extension in distinct)
                {
                    ancestor.Vertex.RecordExtension(extension);
                }

                currentParentHash = ancestor.Parent;
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
            RecordSubtreeVisit(qi.Parent);
            return qi;
        }

        private double GenerateScore(Vertex origin, Vertex vertex, double score, int depth)
        {
            if (depth > 2 || (depth > 0 && vertex.Name.Equals(origin.Name)))
            {
                return score;
            }

            // Directories that have historically sat near this one. A neighbourhood that
            // tends to be productive makes this directory worth looking at sooner.
            //
            // Each neighbour's contribution is averaged into the running score rather than
            // replacing it: assigning here meant only the last neighbour enumerated had any
            // effect, and the incoming score was discarded entirely.
            if (vertex.Adjacents != null && vertex.Adjacents.Count > 0)
            {
                double neighbourTotal = 0;
                int considered = 0;

                foreach (Edge adjacent in vertex.Adjacents.Values)
                {
                    if (considered >= MaxAdjacentsConsidered)
                    {
                        break;
                    }

                    if (!stuff.TryGetVertex(adjacent.VertexName, out Vertex subVertex))
                    {
                        continue;
                    }

                    // Closest observed offset. Abs(Min(..)) is not the nearest neighbour -
                    // for offsets {-3, 1} it yields 3 rather than 1.
                    int distance = adjacent.RelativePosition.Min(p => Math.Abs(p));

                    // Nearer neighbours say more about this directory than distant ones.
                    neighbourTotal += GenerateScore(origin, subVertex, BASE_SCORE, depth + 1) / (distance + 1.0);
                    considered++;
                }

                if (considered > 0)
                {
                    score += neighbourTotal / considered;
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

            // Hit rate: how often visiting this directory actually pays off. Applied to
            // every visited directory, not just productive ones, so that somewhere walked
            // repeatedly without ever yielding a result is demoted - which is the whole
            // point of preferring likely locations. Laplace smoothing keeps a directory
            // that simply has not been tried yet from being treated as proven barren.
            if (vertex.Visits > 0)
            {
                double hitRate = (vertex.FindCount + 1.0) / (vertex.Visits + 2.0);
                score = AdjustScoreForFrequency(score, hitRate);
            }

            // Subtree productivity: a directory whose contents have been searched many times
            // without ever yielding anything is a poor place to look, however it is named.
            // This is where vendored and generated trees are caught, since their children
            // have unique names that per-directory learning can never accumulate.
            if (vertex.SubtreeVisits > 0)
            {
                double subtreeHit = (vertex.SubtreeFinds + 1.0) / (vertex.SubtreeVisits + 2.0);
                score = AdjustScoreForFrequency(score, subtreeHit);
            }

            if (vertex.FindCount > 0)
            {
                score = AdjustScoreForFrequency(score, vertex.FindCount);
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
        /// The two kinds of search want completely different things from it. Finding files
        /// by name is cheap to do by walking, so a filename search is seeded from the shape
        /// graph - the directories that most often produce results, rebased onto this root.
        /// An in-file search is the expensive case, because the fallback is re-reading every
        /// candidate file, so it additionally seeds from recorded content evidence.
        ///
        /// Either way each directory is expanded exactly once at its best score; expanding
        /// per matching file repeats identical path rebasing work thousands of times over.
        /// </summary>
        private void UseIndexForFilenameSearch()
        {
            Dictionary<string, double> bestScoreByVertex =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            bool needInsideDetail = insideFileFilter.Count > 0;

            Seed(bestScoreByVertex, stuff.GetShapeSeedVertexes(ShapeSeedLimit), BASE_SCORE * 10);

            foreach (string filenameFilter in FileNameFilters)
            {
                // Directories where this exact filter has previously succeeded.
                Accumulate(bestScoreByVertex, stuff.GetThingsForRegex(filenameFilter, needInsideDetail));
            }

            // Content evidence: where these in-file searches have matched before. A direct
            // seek, and the reason the index exists - the alternative is re-reading every
            // candidate file. Resolved once for the search, not once per filename filter.
            if (needInsideDetail)
            {
                foreach (KeyValuePair<string, DateTime> hit in
                         stuff.GetContentSeedVertexes(insideFileFilter, InsideFileFilterRegex))
                {
                    double contentScore = BoostScoreBasedOnDate(INDEX_LOOKUP_SCORE, hit.Value);
                    if (!bestScoreByVertex.TryGetValue(hit.Key, out double existing) || contentScore > existing)
                    {
                        bestScoreByVertex[hit.Key] = contentScore;
                    }
                }
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

        /// <summary>
        /// A directory's own modification time, as a deliberately weak prior.
        ///
        /// This is filesystem noise as much as signal: a git checkout rewrites timestamps
        /// across an entire tree, and a package install touches every vendored directory.
        /// Feeding it through the same curve as learned evidence let build output outrank
        /// long-stable source by an order of magnitude in both directions - fresh noise
        /// boosted, old source penalised. It should nudge ordering, never decide it.
        /// </summary>
        private static double ApplyModifiedTimePrior(double score, DateTime modifiedUtc)
        {
            double days = Math.Max(DateTime.UtcNow.Subtract(modifiedUtc).TotalDays, 0);
            double ageFraction = Math.Min(days / 365.0, 1.0);
            return score * (1.25 - (0.5 * ageFraction));
        }

        private static double BoostScoreBasedOnDate(double score, DateTime date)
        {
            // Learned recency - when this directory last actually produced a result, or when
            // matching content was last seen. Unlike a raw file timestamp this is earned,
            // so it is allowed real weight. Clamped to a day because sub-day precision
            // carries no signal and an unclamped divisor approaches zero.
            double daysSinceLastSeen = Math.Max(DateTime.UtcNow.Subtract(date).TotalDays, 1.0);
            return AdjustScoreForRarity(score, daysSinceLastSeen);
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

            // Seeding reads only the locations it can actually use: those already under this
            // root, then the strongest evidence. Loading every remembered location for every
            // seeded vertex cost tens of thousands of rows per search for candidates that
            // were never going to be probed.
            List<(string Path, int Finds)> seedPaths =
                stuff.GetSeedPaths(vertex.Name, this.RootDirectory.FullName, RebaseAttemptsPerVertex);

            if (seedPaths.Count == 0)
            {
                return;
            }

            // Locations that no longer exist are provably worthless at any age. We are
            // already resolving each path here, so reclaiming them costs no extra I/O and
            // needs no scheduled sweep.
            List<string> deadPaths = null;

            foreach ((string path, int _) in seedPaths)
            {
                // Simple case: the path is already under this root - one probe, exact.
                if (path.Contains(this.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!DirectoryExists(path))
                    {
                        (deadPaths ??= new List<string>()).Add(path);
                        continue;
                    }

                    score = QueueUpVertex(score, vertex, path);
                    continue;
                }

                // Complex case: rebase a location remembered from another codebase onto this
                // root. This is the cross-codebase shape match, and the expensive part, so
                // the candidate set reaching it is already bounded.
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
            if (deadPaths != null)
            {
                foreach (string dead in deadPaths)
                {
                    stuff.ForgetLocationDirect(vertex, dead);
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
            QueueUpVertex(score, vertex, directory, directory.Parent.FullName.GetHashCode(), DepthFromRoot(directory));
            return score;
        }

        /// <summary>
        /// Depth of a directory reached by index lookup rather than by walking, where no
        /// parent is on hand to count from.
        /// </summary>
        private int DepthFromRoot(DirectoryInfo directory)
        {
            if (!directory.FullName.StartsWith(RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            string relative = directory.FullName.Substring(RootDirectory.FullName.Length);
            return relative.Count(c => c == separator);
        }

        private void QueueUpVertex(double score, Vertex vertex, DirectoryInfo directory, int parent, int depth)
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
                    preQueue.Add(
                        pathHash,
                        new QueuedDirectory(pathHash, directory, directory.Parent.FullName.GetHashCode(), vertex, score)
                        {
                            Depth = depth,
                        });
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
