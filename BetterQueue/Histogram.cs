using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.BetterQueue
{
    internal class Histogram : Dictionary<long, long>
    {
        internal void Add(int resultsInDirectory)
        {
            if (!this.ContainsKey(resultsInDirectory))
            {
                this.Add(resultsInDirectory, 1);
            }
            else
            {
                this[resultsInDirectory]++;
            }
        }

        internal double Average()
        {
            return (double)this.Select(kvp => kvp.Key * kvp.Value).Sum() / this.Count;
        }
    }
}
