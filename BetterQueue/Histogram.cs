using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CeeFind.BetterQueue
{
    /// <summary>
    /// How many results a directory tends to produce when it produces any.
    ///
    /// Deliberately not a Dictionary any more. Inheriting one put a Count property in scope
    /// that means "distinct keys", and the average was computed against it - so a directory
    /// observed yielding one result five times and three results twice averaged 5.5 instead
    /// of 1.57, out by three and a half times, and the error grew the more varied the
    /// directory's history became. The name Count was the trap; there is no Count here.
    /// </summary>
    internal class Histogram
    {
        /// <summary>
        /// Result count seen, against the number of times it was seen.
        /// </summary>
        public Dictionary<long, long> Observations { get; set; } = new Dictionary<long, long>();

        [JsonIgnore]
        internal long TotalObservations => Observations.Values.Sum();

        [JsonIgnore]
        internal bool IsEmpty => Observations.Count == 0;

        internal void Add(int resultsInDirectory)
        {
            Observations[resultsInDirectory] = Observations.GetValueOrDefault(resultsInDirectory) + 1;
        }

        /// <summary>
        /// Mean results per occasion, weighted by how often each count was seen.
        /// </summary>
        internal double Average()
        {
            long occasions = TotalObservations;
            if (occasions == 0)
            {
                return 0;
            }

            long total = Observations.Sum(o => o.Key * o.Value);
            return (double)total / occasions;
        }
    }
}
