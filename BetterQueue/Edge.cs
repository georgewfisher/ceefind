using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    internal class Edge
    {
        public List<int> RelativePosition { get; set; }

        public string VertexName { get; set;}

        public Edge() { }

        public Edge(int relativePosition, string vertexName)
        {
            RelativePosition = new List<int>() { relativePosition };
            VertexName = vertexName;
        }
    }
}
