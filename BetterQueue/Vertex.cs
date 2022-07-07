using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    internal class Vertex
    {
        public string Name { get; set; }

        public HashSet<string> AbsolutePaths { get; set; }

        public int Visits { get; set; }

        public List<DateTime> LastFinds { get; set; }

        public List<long> LastFindCount { get; set; }

        public Dictionary<string, Edge> Adjacents { get; set; }

        public Vertex()
        {
        }

        internal double Rank()
        {
            return LastFindCount.Count * (1 / DateTime.UtcNow.Subtract(LastFinds.Last()).Days) * Visits;
        }

        public Vertex(string name, DirectoryInfo path)
        {
            Name = name;
            this.AbsolutePaths = new HashSet<string>() { path.FullName };
            Visits = 1;
            LastFinds = new List<DateTime>();
            LastFindCount = new List<long>();
            Adjacents = new Dictionary<string, Edge>();
        }

        internal void AddAdjacents(Stuff stuff, List<string> path)
        {
            for (int i = path.Count - 2; i >= 0; i--)
            {
                int offset = i - path.Count + 1;
                string current = path[i];
                if (stuff.Vertexes.TryGetValue(current, out Vertex other))
                {
                    UpdateAdjacents(offset, other, this);
                    UpdateAdjacents(-offset, this, other);
                }
            }
        }


        private static void UpdateAdjacents(int i, Vertex v, Vertex v2)
        {
            if (v2.Adjacents.ContainsKey(v.Name))
            {
                if (!v2.Adjacents[v.Name].RelativePosition.Contains(i))
                {
                    v2.Adjacents[v.Name].RelativePosition.Add(i);
                }
            }
            else
            {
                v2.Adjacents.Add(v.Name, new Edge(i, v.Name));
            }
        }
    }
}
