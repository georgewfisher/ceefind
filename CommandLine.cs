using System;
using System.Collections.Generic;
using System.Linq;

namespace CeeFind
{
    /// <summary>
    /// The result of reading a command line: either something to do, or a reason not to.
    /// </summary>
    internal sealed class ParsedCommand
    {
        internal SearchSettings Settings { get; init; }

        internal List<string> FilenameFilters { get; init; } = new List<string>();

        internal List<string> NegativeFilenameFilters { get; init; } = new List<string>();

        internal List<string> InFileFilters { get; init; } = new List<string>();

        internal List<string> Warnings { get; init; } = new List<string>();

        /// <summary>
        /// Set when a path was given as part of a filter, as in C:\code\*.txt.
        /// </summary>
        internal string RootOverride { get; init; }

        /// <summary>
        /// Set when the command line asks for something other than a search - help, shell
        /// integration, or a malformed option. Nothing else should happen when this has a
        /// value, and in particular the index should not be opened.
        /// </summary>
        internal int? ExitCode { get; init; }
    }

    /// <summary>
    /// Reads a command line.
    ///
    /// Kept apart from running a search for one reason above tidiness: the index used to
    /// be opened before the arguments were read, so asking for --help or shell
    /// integration, or mistyping an option, created a database on disk. Deciding what was
    /// asked for has no business touching anything.
    /// </summary>
    internal static class CommandLine
    {
        internal static ParsedCommand Parse(string[] args, int exitFound, int exitUsageError)
        {
            if (args.Length == 0 || args.Any(IsHelpRequest))
            {
                Help.Show();
                return new ParsedCommand { ExitCode = args.Length == 0 ? exitUsageError : exitFound };
            }

            if (string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    ShellInit.ShowInstructions();
                    return new ParsedCommand { ExitCode = exitFound };
                }

                return new ParsedCommand { ExitCode = ShellInit.Emit(args[1]) };
            }

            SearchSettings settings = new SearchSettings();

            // MSIX registers aliases, so the same binary may be launched as c or cx.
            // Taking the action from the invocation name means those work without a shell
            // profile; --cd and --open can still override it.
            ApplyAction(settings, ResultActions.FromInvocationName());

            List<string> filters = new List<string>();
            List<string> negativeFilters = new List<string>();
            List<string> inFileFilters = new List<string>();
            List<string> warnings = new List<string>();
            string rootOverride = null;

            bool containsDivider = args.Any(a => a == "--");
            bool filenamePart = true;
            bool isNegated = false;

            foreach (string arg in args)
            {
                if (arg == "--")
                {
                    filenamePart = false;
                    continue;
                }

                if (arg.StartsWith("-"))
                {
                    if (!TryReadOption(arg, out string option) || !Apply(settings, option))
                    {
                        Console.Error.WriteLine($"ceefind: unknown option '{arg}'");
                        Console.Error.WriteLine("Run 'f --help' to see the available options.");
                        return new ParsedCommand { ExitCode = exitUsageError };
                    }

                    continue;
                }

                if (filenamePart && arg == "not")
                {
                    isNegated = true;
                    continue;
                }

                if (filenamePart)
                {
                    string filter = arg;
                    if (DiscoverRootPath(arg, out string replacementRoot, out string filterWithoutRoot))
                    {
                        filter = filterWithoutRoot;
                        rootOverride = replacementRoot;
                    }

                    if (isNegated)
                    {
                        negativeFilters.Add(CleanFilenameFilter(filter, warnings, settings));
                    }
                    else if (arg != "*")
                    {
                        filters.Add(CleanFilenameFilter(filter, warnings, settings));
                    }

                    if (!containsDivider)
                    {
                        filenamePart = false;
                    }

                    continue;
                }

                inFileFilters.Add(CleanInFileFilter(arg, warnings));
            }

            settings.SearchInFiles = inFileFilters.Count > 0;

            return new ParsedCommand
            {
                Settings = settings,
                FilenameFilters = filters,
                NegativeFilenameFilters = negativeFilters,
                InFileFilters = inFileFilters,
                Warnings = warnings,
                RootOverride = rootOverride,
            };
        }

        private static void ApplyAction(SearchSettings settings, ResultAction action)
        {
            settings.Action = action;

            if (action == ResultAction.ChangeDirectory)
            {
                // Both implied: a wrapper can only cd to one directory.
                settings.First = true;
                settings.OutputDirectoriesOnly = true;
            }
            else if (action == ResultAction.Open)
            {
                settings.First = true;
            }
        }

        /// <summary>
        /// Applies one option, or reports that it is not recognised.
        /// </summary>
        private static bool Apply(SearchSettings settings, string option)
        {
            switch (option)
            {
                case "silent":
                case "q": settings.IsSilent = true; return true;

                case "binary":
                case "b": settings.IncludeBinary = true; return true;

                case "verbose":
                case "v": settings.IsVerbose = true; return true;

                case "history":
                case "h": settings.ShowHistory = true; return true;

                case "previous":
                case "p": settings.ShowPreviousResults = true; return true;

                case "dirs":
                case "dir":
                case "d": settings.OutputDirectoriesOnly = true; return true;

                case "files":
                case "file":
                case "l": settings.SearchFilesOnly = true; return true;

                case "up":
                case "u": settings.Up = true; return true;

                case "first":
                case "f": settings.First = true; return true;

                case "cd": ApplyAction(settings, ResultAction.ChangeDirectory); return true;
                case "open": ApplyAction(settings, ResultAction.Open); return true;

                case "sensitive":
                case "s": settings.CaseSensitive = true; return true;

                case "json":
                case "j": settings.WriteStateAsJson = true; return true;

                case "regex":
                case "r": settings.NoRegexAssist = true; return true;

                case "ignorenewlines":
                case "newlines":
                case "n": settings.IgnoreNewLines = true; return true;

                default: return false;
            }
        }

        /// <summary>
        /// Reads an option, enforcing the usual shape: a single dash introduces one
        /// letter, two dashes introduce a name.
        ///
        /// A long name after one dash is also allowed, because CeeFind has always
        /// accepted -verbose and breaking that would be gratuitous. What is not allowed is
        /// anything malformed - three dashes, or a run of letters after a single dash -
        /// which was previously trimmed until it matched something, so '---v' quietly
        /// behaved as '-v'.
        /// </summary>
        private static bool TryReadOption(string arg, out string name)
        {
            name = null;

            if (arg.StartsWith("--"))
            {
                if (arg.Length < 4 || arg[2] == '-')
                {
                    return false;
                }

                name = arg.Substring(2).ToLowerInvariant();
                return true;
            }

            if (arg.Length < 2 || arg[1] == '-')
            {
                return false;
            }

            name = arg.Substring(1).ToLowerInvariant();
            return true;
        }

        private static bool IsHelpRequest(string arg)
        {
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                    // Deliberately absent: -h has always meant history here.
                    return false;
                case "-help":
                case "--help":
                case "-?":
                case "/?":
                case "help":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Allows a root path to be given as part of a filter, as in C:\code\*.txt.
        /// </summary>
        private static bool DiscoverRootPath(string filter, out string rootDirectoryString, out string replacementFilter)
        {
            rootDirectoryString = string.Empty;
            replacementFilter = filter;

            int lastSeparator = filter.LastIndexOfAny(new[] { '\\', '/' });
            if (lastSeparator < 0)
            {
                return false;
            }

            rootDirectoryString = filter.Substring(0, lastSeparator);
            replacementFilter = filter.Substring(lastSeparator + 1);
            return rootDirectoryString.Length > 0;
        }

        private static string CleanFilenameFilter(string arg, List<string> warnings, SearchSettings settings)
        {
            string filter = arg;

            if (settings.NoRegexAssist)
            {
                return filter;
            }

            if (!filter.StartsWith("^"))
            {
                filter = string.Concat("^", filter);
            }

            if (!filter.EndsWith("$"))
            {
                filter = string.Concat(filter, "$");
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(filter, "(?<!\\.)\\*"))
            {
                filter = System.Text.RegularExpressions.Regex.Replace(filter, "(?<!\\.)\\*", ".*");
                warnings.Add($@"Updating ""*"" in filename search string ""{arg}"" with regular expression ""{filter}"" to make searches easier to write.");
            }

            return Utils.FilterAnalysis.EscapeLiteralDots(filter);
        }

        private static string CleanInFileFilter(string arg, List<string> warnings)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(arg, "(?<!\\.)\\*\\."))
            {
                return arg;
            }

            warnings.Add($@"Using ""*."" in file search string ""{arg}"" with regular expression ""\..*"" to make searches easier to write.");
            return System.Text.RegularExpressions.Regex.Replace(arg, "(?<!\\.)\\*\\.", "\\..*");
        }
    }
}
