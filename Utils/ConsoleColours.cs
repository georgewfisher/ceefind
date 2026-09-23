using System;

namespace CeeFind.Utils
{
    /// <summary>
    /// Decides whether output may be coloured, and applies it.
    ///
    /// Colour is suppressed when output is redirected - escape sequences in a pipeline
    /// corrupt whatever reads them - and when the environment asks for it to be off.
    /// NO_COLOR is honoured because it is the convention other command line tools follow,
    /// and a person who sets it has usually done so for a reason, such as a screen reader
    /// or a terminal that renders colour badly.
    /// </summary>
    internal static class ConsoleColours
    {
        private static readonly Lazy<bool> Enabled = new Lazy<bool>(Detect);

        private static bool Detect()
        {
            // https://no-color.org - any value at all means disable.
            if (Environment.GetEnvironmentVariable("NO_COLOR") != null)
            {
                return false;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !Console.IsOutputRedirected;
        }

        internal static bool IsEnabled => Enabled.Value;

        /// <summary>
        /// Writes the text that matched.
        ///
        /// Previously written in white, which is invisible on a light terminal and
        /// indistinguishable from ordinary output on many dark ones - so the one part of
        /// the line worth spotting was the hardest to see. Yellow reads clearly against
        /// both, and the original colour is restored rather than assumed.
        /// </summary>
        internal static void WriteMatch(string text)
        {
            if (!Enabled.Value)
            {
                Console.Write(text);
                return;
            }

            ConsoleColor previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }

        internal static void WriteLine(string text, ConsoleColor colour)
        {
            if (!Enabled.Value)
            {
                Console.WriteLine(text);
                return;
            }

            ConsoleColor previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = colour;
                Console.WriteLine(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }
}
