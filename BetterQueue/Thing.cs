using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    /// <summary>
    /// Equivalent to a file
    /// </summary>
    internal class Thing
    {
        public string Filename { get; set; }
        // regexes found inside this thing
        public Dictionary<string, DateTime> Regexes { get; set; }
        // strings found inside this thing
        public Dictionary<string, DateTime> FoundStrings { get; set; }
        public List<string> VertexNames { get; set; }

        public Thing()
        {

        }

        public Thing(List<string> insideRegex, List<string> insideCapture, String filename, String vertexName)
        {
            this.Filename = filename;
            Regexes = new Dictionary<string, DateTime>();
            foreach (string icrx in insideRegex) {
                Regexes[icrx] = DateTime.UtcNow;
            }
            FoundStrings = new Dictionary<string, DateTime>();

            foreach (string icc in insideCapture)
            {
                FoundStrings[icc] = DateTime.UtcNow;
            }
            VertexNames = new List<string>() { vertexName };
        }
    }
}
