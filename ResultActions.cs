using System;
using System.Diagnostics;
using System.IO;

namespace CeeFind
{
    /// <summary>
    /// The two actions that do something with a result rather than print it: change
    /// directory, and open a file.
    ///
    /// Changing the caller's directory is impossible for a child process - it can only
    /// change its own - so CeeFind cannot do it directly whatever flags are passed. What
    /// it can do is find the target and hand it back in a form the shell will act on,
    /// which is what --cd does: it prints one path and nothing else, so a one-line shell
    /// function can wrap it.
    ///
    /// Opening a file has no such restriction and is done here outright.
    /// </summary>
    internal enum ResultAction
    {
        Print,

        /// <summary>Print the containing directory alone, for a shell wrapper to cd into.</summary>
        ChangeDirectory,

        /// <summary>Open the first match with its associated program.</summary>
        Open,
    }

    internal static class ResultActions
    {
        /// <summary>
        /// Reads the action from the name the executable was invoked under.
        ///
        /// MSIX registers aliases, and an alias is just another name for the same binary,
        /// so 'c' and 'cx' can be made to work without shipping separate programs or
        /// asking the user to edit a profile. Running it as 'ceefind' or 'f' searches
        /// normally.
        /// </summary>
        internal static ResultAction FromInvocationName()
        {
            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            }
            catch (Exception)
            {
                return ResultAction.Print;
            }

            if (string.Equals(name, "c", StringComparison.OrdinalIgnoreCase))
            {
                return ResultAction.ChangeDirectory;
            }

            return string.Equals(name, "cx", StringComparison.OrdinalIgnoreCase)
                ? ResultAction.Open
                : ResultAction.Print;
        }

        /// <summary>
        /// Opens a file with whatever program is associated with it.
        /// </summary>
        internal static int Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ceefind: could not open {path} - {ex.Message}");
                return 2;
            }
        }
    }
}
