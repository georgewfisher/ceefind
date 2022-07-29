using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    internal class Stuff
    {
        public Dictionary<string, Thing> Things { get; set; }

        public Dictionary<string, Vertex> Vertexes { get; set; }

        public Dictionary<string, List<string>> RegexesToThings { get; set; }

        public Dictionary<string, List<Metrics>> SearchHistory { get; set; }

        public Stuff()
        {
            this.Things = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
            this.Vertexes = new Dictionary<string, Vertex>(StringComparer.OrdinalIgnoreCase);
            this.RegexesToThings = new Dictionary<string, List<string>>();
            this.SearchHistory = new Dictionary<string, List<Metrics>>();
        }

        public void Clean()
        {
            int MAX_VERTEX = 100000;
            if (this.Vertexes.Count < MAX_VERTEX)
            {
                return;
            }

            HashSet<string> vToKeep = Vertexes.OrderByDescending(v => v.Value.Rank()).Select(v => v.Key).Take(MAX_VERTEX).ToHashSet();
            foreach (string s in vToKeep)
            {
                if (!vToKeep.Contains(s))
                {
                    Vertexes.Remove(s);
                }
            }
        }

        internal void AddThing(string filename, string[] filenameRegexes, List<string> insideRegex, List<string> insideCapture, Vertex v)
        {
            if (Things.ContainsKey(filename))
            {
                foreach (string ir in insideRegex)
                {
                    Things[filename].Regexes[ir] = DateTime.UtcNow;
                }

                foreach (string ic in insideCapture)
                {
                    Things[filename].FoundStrings[ic] = DateTime.UtcNow;
                }
            }
            else
            {
                Things.Add(filename, new Thing(insideRegex, insideCapture, filename, v.Name));
            }

            foreach (string filenameRegex in filenameRegexes)
            {
                if (RegexesToThings.ContainsKey(filenameRegex))
                {
                    if (!RegexesToThings[filenameRegex].Contains(filename))
                    {
                        RegexesToThings[filenameRegex].Add(filename);
                    }
                }
                else
                {
                    RegexesToThings[filenameRegex] = new List<string>() { filename };
                }
            }
        }
    }
}
