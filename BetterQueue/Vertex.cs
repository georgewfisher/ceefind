using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Finds beneath this directory, broken down by the file type that was being
        /// searched for. A single total cannot distinguish "nothing has ever been found
        /// here" from "nothing of the kind you asked for last time was found here", which
        /// is what let a run of .csproj searches condemn a TypeScript source tree.
        /// The "*" key holds searches with no identifiable target type.
        /// </summary>
        public Dictionary<string, long> SubtreeFindsByType { get; set; }

        /// <summary>
        /// File types observed anywhere beneath this directory.
        ///
        /// This answers a different question from the visit/find counters, and a better
        /// posed one. Those counters record whether past searches happened to succeed here,
        /// which says nothing about a search for a different file type - a directory full
        /// of TypeScript accrues nothing but failures while you are looking for .csproj.
        /// What is observed here is a property of the directory rather than of the query.
        /// </summary>
        public HashSet<string> Extensions { get; set; }

        /// <summary>
        /// Set once the observed set has outgrown its cap. Absence can no longer be trusted
        /// after that, so plausibility checks must abstain rather than guess.
        /// </summary>
        public bool ExtensionsTruncated { get; set; }

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

        /// <summary>
        /// Trigram sketch over filenames seen beneath this directory, carried only for
        /// subtrees big enough that scanning them is expensive. Used to push unlikely
        /// places down the queue, never to remove them from it.
        /// </summary>
        [JsonIgnore]
        public byte[] NameFilter { get; set; }

        /// <summary>
        /// How many files have contributed to the sketch. Confidence, and the test for
        /// whether a subtree is big enough to deserve one.
        /// </summary>
        public long FilterFileCount { get; set; }

        /// <summary>
        /// How varied the modification times of files beneath this directory are.
        ///
        /// Learned without searching for anything: the walk already holds every file's
        /// timestamp. Content written as a block - extracted archives, build output,
        /// installed packages - shares timestamps, while a directory somebody works in
        /// accumulates varied ones as individual files change.
        ///
        /// Spread rather than recency, because recency is not trustworthy here: a git
        /// checkout rewrites the timestamps of an entire tree at once, which would make
        /// every file in a freshly cloned repository look equally interesting.
        /// </summary>
        public long MtimeFiles { get; set; }

        public long MtimeDistinct { get; set; }

        /// <summary>
        /// What directories of this name have been seen to be - a Node package, a build
        /// output directory, a repository root. Unioned across every directory sharing the
        /// name, so it answers "what does this name usually mean here" rather than
        /// describing one place.
        /// </summary>
        public int Markers { get; set; }

        /// <summary>
        /// Child directory counts, kept as a running total and a sample count so a mean can
        /// be taken. Breadth is a structural signal in its own right: generated trees fan
        /// out far wider than authored ones.
        /// </summary>
        public long ChildDirTotal { get; set; }

        public long ChildDirSamples { get; set; }

        [JsonIgnore]
        public bool IsFilterDirty { get; set; }

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
        /// result *for the kind of thing now being looked for*. Judging that against a
        /// single undifferentiated total is what poisoned directories that were simply the
        /// wrong file type for an earlier search.
        /// </summary>
        internal bool IsProvenBarren(long minimumEvidence, HashSet<string> wantedTypes)
        {
            if (SubtreeVisits < minimumEvidence)
            {
                return false;
            }

            if (wantedTypes == null || wantedTypes.Count == 0)
            {
                return SubtreeFinds == 0;
            }

            if (SubtreeFindsByType == null)
            {
                return true;
            }

            foreach (string type in wantedTypes)
            {
                if (SubtreeFindsByType.TryGetValue(type, out long finds) && finds > 0)
                {
                    return false;
                }
            }

            // A wildcard search that succeeded here is evidence for anything.
            return !(SubtreeFindsByType.TryGetValue("*", out long any) && any > 0);
        }

        internal void RecordSubtreeVisit()
        {
            SubtreeVisits++;

            // Evidence is capped rather than accumulated without limit. These counters are
            // totals across every search ever run, so on a large tree they reach millions -
            // WinSxS recorded 1,348,304 visits - and once the denominator is that large no
            // amount of later success can move the ratio. Halving both sides keeps their
            // meaning while letting recent behaviour still count for something.
            if (SubtreeVisits > MaxSubtreeEvidence)
            {
                SubtreeVisits /= 2;
                SubtreeFinds /= 2;

                if (SubtreeFindsByType != null)
                {
                    foreach (string key in SubtreeFindsByType.Keys.ToList())
                    {
                        SubtreeFindsByType[key] /= 2;
                    }
                }
            }

            IsDirty = true;
        }

        private const long MaxSubtreeEvidence = 100_000;

        internal void RecordSubtreeFind(HashSet<string> types)
        {
            SubtreeFinds++;
            SubtreeFindsByType ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (types == null || types.Count == 0)
            {
                SubtreeFindsByType["*"] = SubtreeFindsByType.GetValueOrDefault("*") + 1;
            }
            else
            {
                foreach (string type in types)
                {
                    SubtreeFindsByType[type] = SubtreeFindsByType.GetValueOrDefault(type) + 1;
                }
            }

            IsDirty = true;
        }

        /// <summary>
        /// Maximum distinct file types tracked per directory. A directory holding more than
        /// this is a grab-bag whose contents predict nothing anyway.
        /// </summary>
        private const int MaxExtensions = 64;

        internal bool RecordExtension(string extension)
        {
            if (extension == null)
            {
                return false;
            }

            Extensions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Extensions.Contains(extension))
            {
                return false;
            }

            if (Extensions.Count >= MaxExtensions)
            {
                if (!ExtensionsTruncated)
                {
                    ExtensionsTruncated = true;
                    IsDirty = true;
                }

                return false;
            }

            Extensions.Add(extension);
            IsDirty = true;
            return true;
        }

        /// <summary>
        /// Whether this directory could plausibly satisfy a search for the given file types.
        ///
        /// Abstains - returns true - whenever the evidence is not good enough to rule it
        /// out: nothing observed yet, the observed set was truncated, or the caller has no
        /// particular type in mind. Only a confident, complete observation that none of the
        /// wanted types has ever appeared here is allowed to say no.
        /// </summary>
        internal void RecordMarkers(int markers)
        {
            if (markers == 0 || (Markers & markers) == markers)
            {
                return;
            }

            Markers |= markers;
            IsDirty = true;
        }

        internal void RecordChildDirectories(int count)
        {
            ChildDirTotal += count;
            ChildDirSamples++;
            IsDirty = true;
        }

        /// <summary>
        /// Mean number of child directories, or null when nothing has been observed.
        /// </summary>
        internal double? MeanChildDirectories()
        {
            return ChildDirSamples == 0 ? null : (double)ChildDirTotal / ChildDirSamples;
        }

        internal void RecordMtimeSpread(int fileCount, int distinctTimestamps)
        {
            if (fileCount <= 0)
            {
                return;
            }

            MtimeFiles += fileCount;
            MtimeDistinct += distinctTimestamps;
            IsDirty = true;
        }

        /// <summary>
        /// Proportion of distinct modification times among the files seen beneath this
        /// directory. Near zero means everything was written at once. Null when too little
        /// has been observed to say.
        /// </summary>
        internal double? MtimeSpread()
        {
            return MtimeFiles < MinimumMtimeEvidence ? null : (double)MtimeDistinct / MtimeFiles;
        }

        private const long MinimumMtimeEvidence = 40;

        internal bool CouldContainAny(HashSet<string> wanted)
        {
            if (wanted == null || wanted.Count == 0)
            {
                return true;
            }

            if (ExtensionsTruncated || Extensions == null || Extensions.Count == 0)
            {
                return true;
            }

            foreach (string extension in wanted)
            {
                if (Extensions.Contains(extension))
                {
                    return true;
                }
            }

            return false;
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
