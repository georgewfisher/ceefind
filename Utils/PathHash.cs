using System;
using System.IO;

namespace CeeFind.Utils
{
    /// <summary>
    /// Stable hashing for filesystem paths.
    ///
    /// String.GetHashCode is randomised per process, so keying directories by it made a
    /// search non-reproducible: the identity of every queued directory changed between
    /// runs, which altered dictionary layout, which altered the order equally-scored
    /// directories came off the queue. The same search was measured at 998 directories and
    /// at 2,570. A fixed hash makes a search behave the same way twice.
    /// </summary>
    internal static class PathHash
    {
        internal static int Of(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            // FNV-1a, case-insensitive to match how paths are compared elsewhere.
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;

                uint hash = offset;
                foreach (char c in path)
                {
                    hash ^= char.ToLowerInvariant(c);
                    hash *= prime;
                }

                return (int)hash;
            }
        }

        internal static int Of(DirectoryInfo directory)
        {
            return Of(directory?.FullName);
        }
    }
}
