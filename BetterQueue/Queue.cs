using CeeFind.BetterQueue;

using NewC.Utils;


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NewC.BetterQueue
{
    internal class CeeFindQueue
    {
        private Dictionary<int, QueuedDirectory> done = new Dictionary<int, QueuedDirectory>();
        private string fileNameFilter;
        private Regex fileNameFilterRegex;
        private List<string> insideFileFilter;
        internal List<Regex> InsideFileFilterRegex { get; set; }
        private DirectoryInfo RootDirectory { get; init; }
        internal List<string> Root { get; set; }
        private Stuff stuff;
        private Dictionary<long, QueuedDirectory> preQueue;
        private PriorityQueue<QueuedDirectory, double> queue;

        private readonly long INDEX_LOOKUP_SCORE = 1000000;
        private readonly long BASE_SCORE = 100;

        public CeeFindQueue(Stuff stuff, DirectoryInfo rootDirectory, string fileNameFilter, List<string> fileFilterRegex)
        {
            this.fileNameFilter = fileNameFilter;
            this.fileNameFilterRegex = new Regex(fileNameFilter, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            this.insideFileFilter = fileFilterRegex;
            this.InsideFileFilterRegex = fileFilterRegex.Select(f => new Regex(f, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToList();
            this.RootDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
            List<string> rootPathList = rootDirectory.FullName.Split('\\').ToList();
            this.Root = rootPathList;
            this.stuff = stuff;
            this.queue = new PriorityQueue<QueuedDirectory, double>();
            this.preQueue = new Dictionary<long, QueuedDirectory>();
        }

        public void Initialize()
        {
            // Use indexes to allow for fast find
            UseIndexForFilenameSearch();
            MoveFromPreQueueToQueue();
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
                    vertex = new Vertex(subfolder.Name, subfolder);
                    stuff.Vertexes.Add(subfolder.Name, vertex);
                }
                else
                {
                    string fullname = subfolder.FullName;
                    if (!vertex.AbsolutePaths.Contains(fullname))
                    {
                        vertex.AbsolutePaths.Add(subfolder.FullName);
                    }
                }
                AddAdjacents(subfolder, parentHash);
                score = GenerateScore(vertex, vertex, score, 0);
                score = BoostScoreBasedOnWhenLastSeen(score, subfolder.LastWriteTimeUtc);
                QueueUpVertex(score, vertex, subfolder, parentHash);

            }
            MoveFromPreQueueToQueue();
        }



        internal void AddAdjacents(DirectoryInfo directory, int parentHash)
        {
            QueuedDirectory current;
            if (done.TryGetValue(parentHash, out QueuedDirectory qd))
            {
                current = qd;
            }
            else
            {
                return;
            }

            if (!stuff.Vertexes.TryGetValue(current.Directory.Name, out Vertex start))
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (stuff.Vertexes.TryGetValue(current.Directory.Name, out Vertex other))
                {
                    Vertex.UpdateAdjacents(i, other, start);
                    Vertex.UpdateAdjacents(-i, start, other);
                }

                if (done.TryGetValue(parentHash, out qd))
                {
                    current = qd;
                }
                else
                {
                    return;
                }
            }
        }

        public QueuedDirectory Consume()
        {
            QueuedDirectory qi;
            do
            {
                qi = queue.Dequeue();
            } while (qi.IsVisited || (done.ContainsKey(qi.Id) && done[qi.Id].IsVisited));

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
            foreach (Edge adjacent in vertex.Adjacents.Values)
            {
                if (stuff.Vertexes.TryGetValue(adjacent.VertexName, out Vertex subVertex)) {
                    score = AdjustScoreForFrequency(GenerateScore(origin, subVertex, BASE_SCORE, depth + 1), Math.Abs(adjacent.RelativePosition.Min()));
                }
            }

            score = vertex.LastFindCount.Count > 0 ? AdjustScoreForRarity(score, vertex.LastFindCount.Average()) : score;
            score = AdjustScoreForRarity(score, vertex.AbsolutePaths.Count);
            score = AdjustScoreForFrequency(score, vertex.LastFinds.Count);
            score = vertex.LastFinds.Count > 0 ? AdjustScoreForFrequency(score, vertex.Visits / vertex.LastFinds.Count) : 1.0 / vertex.Visits;
            score = vertex.LastFinds.Any() ? BoostScoreBasedOnWhenLastSeen(score, vertex.LastFinds.Last()) : score;
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
            if (stuff.RegexesToThings.ContainsKey(this.fileNameFilter))
            {
                // This exact regex has been used before
                QueueUpThing(stuff.RegexesToThings[this.fileNameFilter], INDEX_LOOKUP_SCORE);
            }

            // Something was found that matches this regex
            foreach (string search in stuff.Things.Keys)
            {
                if (fileNameFilterRegex.IsMatch(search))
                {
                    QueueUpThing(new List<string>() { search }, INDEX_LOOKUP_SCORE);
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
                    insideSearchFactor = BoostScoreBasedOnWhenLastSeen(insideSearchFactor, thing.Regexes[insideSearchStr]);
                }
            }

            foreach (string foundString in thing.FoundStrings.Keys)
            {
                foreach (Regex insideSearch in InsideFileFilterRegex)
                {
                    if (insideSearch.IsMatch(foundString))
                    {
                        insideSearchFactor = BoostScoreBasedOnWhenLastSeen(insideSearchFactor, thing.FoundStrings[foundString]);
                    }
                }
            }

            QueueUpVertex(thing.VertexNames, score * insideSearchFactor);
        }

        private static double BoostScoreBasedOnWhenLastSeen(double score, DateTime date)
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

        private void QueueUpVertex(string vertexName, double score)
        {
            Vertex vertex = stuff.Vertexes[vertexName];
            foreach (string path in vertex.AbsolutePaths)
            {
                if (path.Contains(this.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    score = BoostScoreBasedOnWhenLastSeen(score, vertex.LastFinds.Last());
                    score = AdjustScoreForFrequency(score, vertex.LastFinds.Count);
                    DirectoryInfo directory = new DirectoryInfo(path);
                    QueueUpVertex(score, vertex, directory, directory.Parent.FullName.GetHashCode());
                }
            }
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
    }
}
