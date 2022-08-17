using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace CeeFind.BetterQueue
{
    internal class Vertex
    {
        public string Name { get; set; }

        public HashSet<string> AbsolutePaths { get; set; }

        public int Visits { get; set; }

        public List<DateTime> LastFinds { get; set; }

        public Histogram LastFindCount { get; set; }

        public Dictionary<string, Edge> Adjacents { get; set; }

        [JsonIgnore]
        public int AdjacentsHitCount { get; private set; }

        public Vertex()
        {
        }

        internal double Rank()
        {
            if (LastFinds == null || LastFindCount == null)
            {
                return Visits;
            }
            return LastFindCount.Count * (1 / DateTime.UtcNow.Subtract(LastFinds.Last()).Days) * Visits;
        }

        public Vertex(string name)
        {
            Name = name;
            this.AbsolutePaths = null;
            Visits = 1;
            LastFinds = null;
            LastFindCount = null;
            Adjacents = null;
        }


        internal static void UpdateAdjacents(int distance, Vertex v, Vertex v2)
        {
            if (v2.Adjacents == null)
            {
                v2.Adjacents = new Dictionary<string, Edge>();
            }

            v2.AdjacentsHitCount++;

            if (v2.Adjacents.ContainsKey(v.Name))
            {
                if (!v2.Adjacents[v.Name].RelativePosition.Contains(distance))
                {
                    v2.Adjacents[v.Name].RelativePosition.Add(distance);
                }
            }
            else
            {
                v2.Adjacents.Add(v.Name, new Edge(distance, v.Name));
            }
        }

        public override string ToString()
        {
            return this.Name;
        }
    }
}
