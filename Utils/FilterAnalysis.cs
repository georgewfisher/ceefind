using System;
using System.Collections.Generic;
using System.Text;
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
        /// Makes separator dots literal.
        ///
        /// "*.cs" became "^.*.cs$", where the second dot still meant "any character" - so
        /// the filter also matched "Foocs", and no glob could be derived from it safely.
        /// A dot introducing a quantifier is left alone, because "." "+" and ".{2}" are
        /// regular expression intent the user wrote deliberately.
        /// </summary>
        internal static string EscapeLiteralDots(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return pattern;
            }

            StringBuilder builder = new StringBuilder(pattern.Length + 4);

            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];

                if (c == '\\' && i + 1 < pattern.Length)
                {
                    // Already escaped - copy the pair through untouched.
                    builder.Append(c).Append(pattern[i + 1]);
                    i++;
                    continue;
                }

                if (c == '.' && !IsQuantifier(i + 1 < pattern.Length ? pattern[i + 1] : '\0'))
                {
                    builder.Append("\\.");
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static bool IsQuantifier(char c)
        {
            return c == '*' || c == '+' || c == '?' || c == '{';
        }

        /// <summary>
        /// The narrowest wildcard pattern the operating system can be asked for that is
        /// still guaranteed to include everything the regular expression could match.
        ///
        /// Enumerating a directory and testing every name in managed code is the dominant
        /// cost of a cold search; the filesystem can do most of that filtering during the
        /// directory read. Correctness rests on the pattern being a superset - the real
        /// expression is still applied to whatever comes back - so anything that cannot be
        /// bounded safely returns null and falls back to reading everything.
        /// </summary>
        internal static string TryGetEnumerationGlob(string pattern)
        {
            string extension = TryGetTargetExtension(pattern);
            if (extension == null)
            {
                return null;
            }

            // The extension has to be the genuine end of the expression. Anything after it
            // - a quantifier, an alternation - could allow names that the glob would miss.
            int end = pattern.Length - 1;
            if (end >= 0 && pattern[end] == '$')
            {
                end--;
            }

            if (end < 0 || !char.IsLetterOrDigit(pattern[end]))
            {
                return null;
            }

            return "*." + extension;
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
