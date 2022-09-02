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

        private readonly long INDEX_LOOKUP_SCORE = 1000000;
        private readonly long BASE_SCORE = 100;

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
                if (done.TryGetValue(subfolder.FullName.GetHashCode(), out QueuedDirectory qd)
                    && qd.IsVisited)
                {
                    continue;
                }

                stuff.Vertexes.TryGetValue(subfolder.Name, out Vertex vertex);
                double score = BASE_SCORE;
                if (vertex == null)
                {
                    vertex = new Vertex(subfolder.Name);
                    stuff.Vertexes.Add(subfolder.Name, vertex);
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
            if (stuff.Vertexes.TryGetValue(current.Directory.Name, out Vertex other))
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
                    if (stuff.Vertexes.TryGetValue(adjacent.VertexName, out Vertex subVertex))
                    {
                        score = AdjustScoreForFrequency(GenerateScore(origin, subVertex, BASE_SCORE, depth + 1), Math.Abs(adjacent.RelativePosition.Min()));
                    }
                }
            }

            if (vertex.LastFindCount != null)
            {
                score = vertex.LastFindCount.Count > 0 ? AdjustScoreForRarity(score, vertex.LastFindCount.Average()) : score;
            }
            if (vertex.AbsolutePaths != null)
            {
                score = AdjustScoreForRarity(score, vertex.AbsolutePaths.Count);
            }
            if (vertex.LastFinds != null)
            {
                score = AdjustScoreForFrequency(score, vertex.LastFinds.Count);
                score = vertex.LastFinds.Count > 0 ? AdjustScoreForFrequency(score, vertex.Visits / vertex.LastFinds.Count) : 1.0 / vertex.Visits;
                score = vertex.LastFinds.Any() ? BoostScoreBasedOnDate(score, vertex.LastFinds.Last()) : score;
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

        private void UseIndexForFilenameSearch()
        {
            for (int i = 0; i < FileNameFilters.Length; i++)
            {
                string filenameFilter = FileNameFilters[i];
                if (stuff.RegexesToThings.ContainsKey(filenameFilter))
                {
                    // This exact regex has been used before
                    QueueUpThing(stuff.RegexesToThings[filenameFilter], INDEX_LOOKUP_SCORE);
                }

                // Something was found that matches this regex
                foreach (string search in stuff.Things.Keys)
                {
                    if (FileNameFilterRegex[i].IsMatch(search))
                    {
                        QueueUpThing(new List<string>() { search }, INDEX_LOOKUP_SCORE);
                    }
                }
            }
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
            return score / Math.Log(count + 1);
        }

        private void QueueUpThing(List<string> things, double score)
        {
            double adjustedScore = AdjustScoreForRarity(score, things.Count);
            foreach (string thing in things)
            {
                QueueUpThing(thing, adjustedScore);
            }
        }

        private void QueueUpThing(string thingName, double score)
        {
            Thing thing = stuff.Things[thingName];

            // Adjust score for any find inside file results
            double insideSearchFactor = 1;
            foreach (string insideSearchStr in insideFileFilter)
            {
                if (thing.Regexes.ContainsKey(insideSearchStr))
                {
                    insideSearchFactor = BoostScoreBasedOnDate(insideSearchFactor, thing.Regexes[insideSearchStr]);
                }
            }

            foreach (string foundString in thing.FoundStrings.Keys)
            {
                foreach (Regex insideSearch in InsideFileFilterRegex)
                {
                    if (insideSearch.IsMatch(foundString))
                    {
                        insideSearchFactor = BoostScoreBasedOnDate(insideSearchFactor, thing.FoundStrings[foundString]);
                    }
                }
            }

            QueueUpVertex(thing.VertexNames, score * insideSearchFactor);
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
            Vertex vertex = stuff.Vertexes[vertexName];
            if (vertex.AbsolutePaths != null)
            {
                foreach (string path in vertex.AbsolutePaths)
                {
                    // Simple case: the path is a subdirectory of the root
                    if (path.Contains(this.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                    {
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
                            if (Directory.Exists(proposedPath))
                            {
                                score = QueueUpVertex(score, vertex, proposedPath);
                                break;
                            }
                        }
                    }
                }
            }
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
            if (vertex.LastFinds != null)
            {
                score = BoostScoreBasedOnDate(score, vertex.LastFinds.Last());
                score = AdjustScoreForFrequency(score, vertex.LastFinds.Count);
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
