using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    internal class NextPath
    {
            public NextPath(long hash, DirectoryInfo directory, List<string> relativePath, Vertex vertex, double score)
            {
                this.Hash = hash;
                this.Directory = directory;
                this.Vertex = vertex;
                this.Score = score;
            this.RelativePath = relativePath;
            }

            public double Score { get; set; }
        public List<string> RelativePath { get; }
        public long Hash { get; set; }

            public DirectoryInfo Directory { get; set; }

            public Vertex Vertex { get; set; }
     
    }
}
