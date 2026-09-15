using CeeFind.Utils;

using System.IO;

namespace CeeFind.BetterQueue
{
    internal class QueuedDirectory
    {
        public QueuedDirectory(int pathHash, DirectoryInfo directory, int parent, Vertex vertex, double score)
        {
            this.Id = pathHash;
            this.Directory = directory;
            this.Vertex = vertex;
            this.Score = score;
            this.Parent = parent;
        }

        public QueuedDirectory(DirectoryInfo directory, int parent, Vertex vertex, double score) :
            this(PathHash.Of(directory.FullName), directory, parent, vertex, score)
        {
        }

        public static QueuedDirectory InitializeRoot(DirectoryInfo rootDirectory, Stuff stuff)
        {
            Vertex v = stuff.GetOrAddVertex(rootDirectory.Name);
            QueuedDirectory qd = new QueuedDirectory(rootDirectory, 0, v, 0);
            qd.IsRoot = true;
            return qd;
        }

        public double Score { get; set; }
        public int Parent { get; }
        public int Id { get; }

        /// <summary>
        /// Distance from the search root. Generated and vendored trees run deep; work you
        /// are actually looking for usually does not.
        /// </summary>
        public int Depth { get; set; }
        public DirectoryInfo Directory { get; set; }
        public Vertex Vertex { get; set; }
        public bool IsVisited { get; set; }
        public bool IsRoot { get; set; }

        /// <summary>
        /// Set once this directory has been pushed to the back of the queue by the trigram
        /// check, so it is not deferred a second time.
        /// </summary>
        public bool IsDeferred { get; set; }

        public override string ToString()
        {
            return this.Directory.Name;
        }
    }
}
