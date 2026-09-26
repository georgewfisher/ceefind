using System;

namespace CeeFind.Utils
{
    /// <summary>
    /// Fits a line of matched file content into the terminal.
    ///
    /// Only ever applied to content matches. Paths are printed whole: they are parsed by
    /// the c and cx shims and piped into other tools, so truncating one would break it.
    ///
    /// Only applied when a terminal is actually attached. Redirected output belongs to
    /// grep or awk rather than to a reader, and silently shortening it there would corrupt
    /// data for no benefit.
    /// </summary>
    internal static class ConsoleLayout
    {
        private const string Ellipsis = "...";

        /// <summary>
        /// Width assumed when none can be discovered. Console.WindowWidth throws when
        /// output is redirected, and returns nothing useful in some hosts.
        /// </summary>
        private const int AssumedWidth = 120;

        private const int MinimumUsableWidth = 40;

        /// <summary>
        /// Smallest amount of surrounding text worth keeping. Below this the context says
        /// nothing, so the space is better given to the match.
        /// </summary>
        private const int MinimumContext = 8;

        private static readonly Lazy<int?> DetectedWidth = new Lazy<int?>(Detect);

        /// <summary>
        /// Width to lay out against, or null to leave text alone. Settable so the layout
        /// can be exercised without attaching a terminal, which is otherwise impossible to
        /// do from a test.
        /// </summary>
        private static int? overriddenWidth;
        private static bool widthOverridden;

        internal static void OverrideWidthForTesting(int? width)
        {
            overriddenWidth = width;
            widthOverridden = true;
        }

        private static int? Width => widthOverridden ? overriddenWidth : DetectedWidth.Value;

        private static int? Detect()
        {
            if (Console.IsOutputRedirected)
            {
                return null;
            }

            try
            {
                int width = Console.WindowWidth;
                return width >= MinimumUsableWidth ? width : (int?)AssumedWidth;
            }
            catch (Exception)
            {
                // No console attached - a service, a detached process, some terminals.
                return null;
            }
        }

        internal static bool IsTerminal => Width.HasValue;

        /// <summary>
        /// Trims the text either side of a match so the whole line fits on one row,
        /// keeping the match itself visible and centred in what is left.
        ///
        /// The match takes priority over its context: seeing what was found matters more
        /// than seeing what surrounds it. Where even the match cannot fit, it is cut too,
        /// because wrapping destroys the aligned column that makes results scannable.
        /// </summary>
        internal static (string Before, string Match, string After) Fit(
            string before, string match, string after, int prefixWidth)
        {
            int? terminal = Width;
            if (terminal == null)
            {
                return (before, match, after);
            }

            // One column is left spare: writing to the last cell of a Windows console
            // wraps to the next row on its own.
            int budget = terminal.Value - prefixWidth - 1;
            if (budget <= 0)
            {
                // The prefix alone is wider than the terminal - a very long filename.
                // Nothing that can be trimmed here will make the line fit, so the text is
                // dropped rather than pretending otherwise.
                return (string.Empty, string.Empty, string.Empty);
            }

            if (before.Length + match.Length + after.Length <= budget)
            {
                return (before, match, after);
            }

            // The match may have as much as half the line; beyond that the context
            // disappears entirely and the result becomes unreadable. The floor is capped
            // by the budget, since a minimum that exceeds the space available would let
            // the match overrun the line it is supposed to fit into.
            int matchBudget = Math.Min(Math.Max(budget / 2, MinimumContext), budget);
            string fittedMatch = match.Length > matchBudget ? Truncate(match, matchBudget) : match;

            int remaining = budget - fittedMatch.Length;
            if (remaining <= 0)
            {
                return (string.Empty, fittedMatch, string.Empty);
            }

            // Split what is left between the two sides, giving back whatever one side
            // does not need rather than wasting it.
            int half = remaining / 2;
            int beforeBudget = Math.Min(before.Length, half);
            int afterBudget = Math.Min(after.Length, remaining - beforeBudget);
            beforeBudget = Math.Min(before.Length, remaining - afterBudget);

            string fittedBefore = beforeBudget >= before.Length
                ? before
                : TruncateStart(before, beforeBudget);

            string fittedAfter = afterBudget >= after.Length
                ? after
                : Truncate(after, afterBudget);

            return (fittedBefore, fittedMatch, fittedAfter);
        }

        /// <summary>Keeps the start, marking what was removed from the end.</summary>
        private static string Truncate(string value, int budget)
        {
            if (budget <= 0)
            {
                return string.Empty;
            }

            if (value.Length <= budget)
            {
                return value;
            }

            return budget <= Ellipsis.Length
                ? value.Substring(0, budget)
                : value.Substring(0, budget - Ellipsis.Length) + Ellipsis;
        }

        /// <summary>
        /// Keeps the end, marking what was removed from the start. Used for the text
        /// leading up to a match, where the characters nearest the match are the ones
        /// worth showing.
        /// </summary>
        private static string TruncateStart(string value, int budget)
        {
            if (budget <= 0)
            {
                return string.Empty;
            }

            if (value.Length <= budget)
            {
                return value;
            }

            return budget <= Ellipsis.Length
                ? value.Substring(value.Length - budget)
                : Ellipsis + value.Substring(value.Length - (budget - Ellipsis.Length));
        }
    }
}
