using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CeeFind.BetterQueue
{
    /// <summary>
    /// A single known location for a vertex. Finds drives retention, LastFind drives ranking -
    /// deliberately separate, because an old find is evidence the user has forgotten where
    /// something is, not evidence the entry is worthless.
    /// </summary>
    internal class PathStat
    {
        public int Finds { get; set; }

        public DateTime LastFind { get; set; }

        public PathStat()
        {
        }

        public PathStat(int finds, DateTime lastFind)
        {
            Finds = finds;
            LastFind = lastFind;
        }
    }

    internal class Vertex
    {
        public string Name { get; set; }

        public int Visits { get; set; }

        /// <summary>
        /// Number of times this directory name has produced a result. Replaces the old
        /// unbounded List&lt;DateTime&gt;, whose every consumer only ever read Count, Last and Any.
        /// </summary>
        public int FindCount { get; set; }

        /// <summary>
        /// Directories visited anywhere beneath this one, and how many of those produced a
        /// result. Barrenness is structural: vendored and generated trees have thousands of
        /// uniquely-named children, so nothing is ever learned about them individually -
        /// but a great deal can be learned about the subtree that contains them.
        /// </summary>
        public long SubtreeVisits { get; set; }

        public long SubtreeFinds { get; set; }

        public DateTime? LastFindUtc { get; set; }

        public Histogram LastFindCount { get; set; }

        public Dictionary<string, Edge> Adjacents { get; set; }

        /// <summary>
        /// Denormalised count of known absolute paths, so scoring never has to pay for
        /// loading the path list during a walk.
        /// </summary>
        public int PathCount { get; set; }

        /// <summary>
        /// Known locations, loaded on demand. Null until the store populates it.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, PathStat> AbsolutePaths { get; set; }

        [JsonIgnore]
        public bool ArePathsLoaded { get; set; }

        [JsonIgnore]
        public bool ArePathsDirty { get; set; }

        [JsonIgnore]
        public bool IsDirty { get; set; }

        [JsonIgnore]
        public int AdjacentsHitCount { get; private set; }

        public Vertex()
        {
        }

        public Vertex(string name)
        {
            Name = name;
            Visits = 1;
            FindCount = 0;
            LastFindUtc = null;
            LastFindCount = null;
            Adjacents = null;
            PathCount = 0;
            AbsolutePaths = null;
            ArePathsLoaded = true; // brand new vertex has nothing on disk to load
            IsDirty = true;
        }

        /// <summary>
        /// Retention score. Never a function of age on its own: recency is already applied
        /// when ranking search candidates, and applying it again here would evict exactly the
        /// long-forgotten locations this index exists to remember.
        /// </summary>
        internal double Rank()
        {
            if (FindCount <= 0)
            {
                // No evidence this name ever produced anything - traversal residue, and
                // rebuilt for free by the next walk.
                return 0.0;
            }

            return FindCount * Math.Log(Visits + 1) * Math.Log(PathCount + 2);
        }

        internal void RecordFind(DateTime utcNow)
        {
            FindCount++;
            LastFindUtc = utcNow;
            IsDirty = true;
        }

        /// <summary>
        /// True when this subtree has been explored substantially and has never produced a
        /// result. Zero finds is the discriminator rather than a ratio: one result anywhere
        /// beneath a directory is enough to stop treating it as noise.
        /// </summary>
        internal bool IsProvenBarren(long minimumEvidence)
        {
            return SubtreeFinds == 0 && SubtreeVisits >= minimumEvidence;
        }

        internal void RecordSubtreeVisit()
        {
            SubtreeVisits++;
            IsDirty = true;
        }

        internal void RecordSubtreeFind()
        {
            SubtreeFinds++;
            IsDirty = true;
        }

        internal void RecordFindLocation(string absolutePath, DateTime utcNow)
        {
            if (AbsolutePaths == null)
            {
                AbsolutePaths = new Dictionary<string, PathStat>(StringComparer.OrdinalIgnoreCase);
            }

            if (AbsolutePaths.TryGetValue(absolutePath, out PathStat existing))
            {
                existing.Finds++;
                existing.LastFind = utcNow;
            }
            else
            {
                AbsolutePaths.Add(absolutePath, new PathStat(1, utcNow));
            }

            PathCount = AbsolutePaths.Count;
            ArePathsDirty = true;
            IsDirty = true;
        }

        /// <summary>
        /// Drops a location that no longer exists on disk. Called from the walk itself, so
        /// dead entries are reclaimed without a scheduled scan.
        /// </summary>
        internal void ForgetLocation(string absolutePath)
        {
            if (AbsolutePaths != null && AbsolutePaths.Remove(absolutePath))
            {
                PathCount = AbsolutePaths.Count;
                ArePathsDirty = true;
                IsDirty = true;
            }
        }

        internal static void UpdateAdjacents(int distance, Vertex v, Vertex v2)
        {
            if (v2.Adjacents == null)
            {
                v2.Adjacents = new Dictionary<string, Edge>();
            }

            v2.AdjacentsHitCount++;

            if (v2.Adjacents.TryGetValue(v.Name, out Edge edge))
            {
                if (!edge.RelativePosition.Contains(distance))
                {
                    edge.RelativePosition.Add(distance);
                    v2.IsDirty = true;
                }
            }
            else
            {
                v2.Adjacents.Add(v.Name, new Edge(distance, v.Name));
                v2.IsDirty = true;
            }
        }

        public override string ToString()
        {
            return this.Name;
        }
    }
}
