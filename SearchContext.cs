using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind
{
    public class SearchContext
    {
        public SearchContext(bool isVerbose, bool includeBinary)
        {
            this.IsVerbose = isVerbose;
            this.IncludeBinary = includeBinary;
        }

        public bool IsVerbose { get; }
        public bool IncludeBinary { get; }
        public int Count { get; set; }
    }
}
