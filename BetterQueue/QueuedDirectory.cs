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
        }

        public QueuedDirectory(DirectoryInfo directory, int parent, Vertex vertex, double score) :
            this(directory.FullName.GetHashCode(), directory, parent, vertex, score)
        {
        }

        public static QueuedDirectory InitializeRoot(DirectoryInfo rootDirectory, Stuff stuff)
        {
            Vertex v;
            if (stuff.Vertexes.ContainsKey(rootDirectory.Name))
            {
                v = stuff.Vertexes[rootDirectory.Name];
            }
            else
            {
                v = new Vertex(rootDirectory.Name, rootDirectory);
                stuff.Vertexes.Add(v.Name, v);
            }
            QueuedDirectory qd = new QueuedDirectory(rootDirectory, 0, v, 0);
            qd.IsRoot = true;
            return qd;
        }

        public double Score { get; set; }
        public int Id { get; }
        public DirectoryInfo Directory { get; set; }
        public Vertex Vertex { get; set; }
        public bool IsVisited { get; set; }
        public bool IsRoot { get; set; }
    }
}
