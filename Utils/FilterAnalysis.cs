using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CeeFind.Utils
{
    /// <summary>
    /// Reads intent out of a filename filter after it has been turned into a regular
    /// expression by <c>CleanFilenameFilter</c> (anchored, with <c>*</c> rewritten to
    /// <c>.*</c>, and dots left unescaped).
    ///
    /// Two questions are asked of a filter and they are deliberately not the same one:
    ///
    ///   IsPureSuffix        - is this filter nothing but "*.ext"? Such a search is the
    ///                         cheapest thing there is to repeat, so its results are not
    ///                         worth index space.
    ///   TryGetTargetExtension - what file type is this search looking for, whatever else
    ///                         the filter says? Used to judge whether a directory could
    ///                         plausibly satisfy the search at all.
    ///
    /// A specific search like ".*Aggregation.cs" answers no to the first and "cs" to the
    /// second: worth remembering, and known to be after C# files.
    /// </summary>
    internal static class FilterAnalysis
    {
        private const int MaxExtensionLength = 12;

        /// <summary>
        /// Matches a filter that is exactly "anything, dot, extension" and nothing more.
        /// </summary>
        private static readonly Regex PureSuffixFilter = new Regex(
            @"^\^\.\*\\?\.([A-Za-z0-9_]+)\$$",
            RegexOptions.Compiled);

        internal static bool IsPureSuffix(string pattern)
        {
            return !string.IsNullOrEmpty(pattern) && PureSuffixFilter.IsMatch(pattern);
        }

        /// <summary>
        /// Recovers the file extension a filter is ultimately looking for, by walking back
        /// from the end of the pattern through literal characters to a dot. This reaches
        /// the cases a whole-pattern match cannot: "^.*Aggregation.cs$" and
        /// "^[a-z]+[0-9].cs$" are both after ".cs" even though neither is a plain suffix
        /// filter.
        ///
        /// Returns null when no extension can be established - a wildcard such as
        /// "^.*test.*$" genuinely targets no file type, and guessing would be worse than
        /// saying nothing.
        /// </summary>
        internal static string TryGetTargetExtension(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            int end = pattern.Length - 1;
            if (end >= 0 && pattern[end] == '$')
            {
                end--;
            }

            int cursor = end;
            while (cursor >= 0 && IsExtensionCharacter(pattern[cursor]))
            {
                cursor--;
            }

            int length = end - cursor;
            if (length <= 0 || length > MaxExtensionLength)
            {
                return null;
            }

            // The run of literal characters has to be introduced by a dot, escaped or not,
            // otherwise it is just the tail of a name rather than an extension.
            if (cursor < 0 || pattern[cursor] != '.')
            {
                return null;
            }

            // A dot that is itself preceded by an unescaped quantifier or class is part of
            // the pattern rather than a separator - "^.*$" must not yield an extension.
            if (cursor > 0 && pattern[cursor - 1] == '\\' && cursor - 1 > 0 && pattern[cursor - 2] == '\\')
            {
                return null;
            }

            return pattern.Substring(cursor + 1, length).ToLowerInvariant();
        }

        /// <summary>
        /// The distinct file types a set of filters is after. Empty when nothing can be
        /// established, which callers must treat as "no opinion" rather than "matches
        /// nothing".
        /// </summary>
        internal static HashSet<string> TargetExtensions(IEnumerable<string> patterns)
        {
            HashSet<string> extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string pattern in patterns)
            {
                string extension = TryGetTargetExtension(pattern);
                if (extension != null)
                {
                    extensions.Add(extension);
                }
            }

            return extensions;
        }

        /// <summary>
        /// Normalises an observed file extension to the same form the extractor produces.
        /// </summary>
        internal static string NormaliseExtension(string fileExtension)
        {
            if (string.IsNullOrEmpty(fileExtension))
            {
                return null;
            }

            string trimmed = fileExtension.TrimStart('.');
            return trimmed.Length == 0 || trimmed.Length > MaxExtensionLength
                ? null
                : trimmed.ToLowerInvariant();
        }

        private static bool IsExtensionCharacter(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
