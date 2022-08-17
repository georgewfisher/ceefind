using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeeFind.Utils
{
    internal class DirectoryUtils
    {
        internal static bool isSubpath(List<string> root, List<string> path)
        {
            for (int i = 0; i < root.Count; i++)
            {
                if (!root[i].Equals(path[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        internal static long GetHashCodeFromRelativePath(List<string> path)
        {
            return string.Join('/', path).GetHashCode();
        }

        internal static string GetPath(List<string> root, List<string> path)
        {
            return string.Concat(string.Join('/', root), "/", string.Join('/', path));
        }

        internal static string GetRelativePath(DirectoryInfo rootDirectory, string path)
        {
            return path.Replace(rootDirectory.FullName, string.Empty);
        }

        internal static string GetRelativePath(DirectoryInfo rootDirectory, DirectoryInfo path)
        {
            return GetRelativePath(rootDirectory, path.FullName);
        }
    }
}
