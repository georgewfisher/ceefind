using CeeFind.BetterQueue;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CeeFind.Utils;
using System.Diagnostics;
using System.Text;
using CeeFind.Files;
using System.Runtime.InteropServices;

namespace CeeFind
{
    internal class Program
    {
        private const char DIRECTORY_SEPARATOR_WINDOWS = '\\';
        private const char DIRECTORY_SEPARATOR_OTHER = '/';
        private static char directorySeparator;
        private static CeeFindQueue queue;
        private static HashSet<string> binaryFiles;
        private static ILogger<Program> log;
        private const long LARGE_FILE_SIZE = 1024 * 1024;
        private const string INDEX_FILE_NAME = "index.db";

        /// <summary>
        /// Set by the Ctrl+C handler, which runs on its own thread pool thread while the
        /// walk keeps running on the main thread. The walk polls this flag and stops itself
        /// rather than the handler reaching into `stuff` directly - `Stuff` and its
        /// `SqliteConnection` are not safe for concurrent use, so only ever one thread may
        /// touch them.
        /// </summary>
        private static volatile bool cancelRequested;

        public Program()
        {
        }

        /// <summary>
        /// Exit codes follow the convention of grep and find, so that CeeFind can be used
        /// in a script. Previously every path returned zero, including an unhandled
        /// exception, so a caller could not tell success from a crash.
        /// </summary>
        private const int ExitFound = 0;
        private const int ExitNothingFound = 1;
        private const int ExitUsageError = 2;

        /// <summary>
        /// Width of the filename column in in-file results.
        ///
        /// Chosen so the content lines up and the eye can run straight down it. Measured
        /// against real source filenames it holds about 83% of them: 35 fits 82.7%, where
        /// 30 would fit only 61% and 45 would buy 96% at the cost of a ninth of the
        /// content width on a 120 column terminal.
        /// </summary>
        private const int MatchColumnWidth = 35;

        /// <summary>
        /// Set when --open finds its target, so the file is launched after the search has
        /// finished and its index has been written rather than from inside the walk.
        /// </summary>
        private static string openTarget;

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (RegexParseException ex)
            {
                // A malformed pattern is the user's typo, not a fault. Reporting it as a
                // stack trace told them nothing and still exited zero.
                Console.Error.WriteLine($"ceefind: the search pattern could not be understood - {ex.Message}");
                Console.Error.WriteLine("Try -r to use the pattern as a pure regular expression, or see -help.");
                return ExitUsageError;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ceefind: {ex.Message}");
                return ExitUsageError;
            }
        }

        private static int Run(string[] args)
        {
            directorySeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? DIRECTORY_SEPARATOR_WINDOWS
                : DIRECTORY_SEPARATOR_OTHER;

            // Reading the command line comes first and touches nothing. Opening the index
            // before this meant that asking for --help, asking for shell integration, or
            // mistyping an option all created a database on disk.
            ParsedCommand command = CommandLine.Parse(args, ExitFound, ExitUsageError);
            if (command.ExitCode.HasValue)
            {
                return command.ExitCode.Value;
            }

            SearchSettings settings = command.Settings;
            List<string> filenameFilterRegex = command.FilenameFilters;
            List<string> negativeFilenameFilterRegex = command.NegativeFilenameFilters;
            List<string> inFileSearchStrings = command.InFileFilters;
            List<string> warnings = command.Warnings;

            string stateFile = Path.Combine(GetStateDirectory(), INDEX_FILE_NAME);
            Stuff stuff = new Stuff(stateFile);

            ILoggerFactory loggerFactory = LoggerFactory.Create(
                builder => builder
                            .AddConsole()
                            .SetMinimumLevel(LogLevel.Information));
            log = loggerFactory.CreateLogger<Program>();

            binaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string extension in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "binary_files.txt")))
            {
                binaryFiles.Add(extension);
            }

            string rootDirectoryString = command.RootOverride ?? Directory.GetCurrentDirectory();
            if (command.RootOverride != null && settings.IsVerbose)
            {
                log.LogInformation($"Search path updated to {command.RootOverride}");
            }
            DirectoryInfo rootDirectory = new DirectoryInfo(rootDirectoryString);
            if (filenameFilterRegex.All(f => f == "^.*$") && !negativeFilenameFilterRegex.Any())
            {
                settings.ScanAllFiles = true;
            }

            Metrics metrics = new Metrics(settings, string.Join(' ', args));

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                // Prevent the process from terminating immediately. The walk itself notices
                // this flag and winds down; finishing and flushing happens back on the main
                // thread, never here.
                e.Cancel = true;

                if (!cancelRequested)
                {
                    if (settings.IsVerbose)
                    {
                        Console.WriteLine("Gracefully terminating, please wait...");
                    }

                    cancelRequested = true;
                }
            };

            if (settings.ShowHistory)
            {
                ShowHistory(stuff, rootDirectory);
                return ExitFound;
            }

            queue = new CeeFindQueue(directorySeparator, stuff, rootDirectory, filenameFilterRegex, negativeFilenameFilterRegex, inFileSearchStrings, settings);
            queue.Initialize();

            if (settings.IsVerbose)
            {
                log.LogInformation($"Searching for {queue}...");
                log.LogInformation("Settings: " + Environment.NewLine + settings.ToString());

                foreach (string warning in warnings)
                {
                    log.LogWarning(warning);
                }
            }

            // Indeterminate until something is found, then a bar that fills in with the
            // match count - not a real completion percentage, since the walk has no total
            // until it is done, but enough to tell "still looking" from "found some" from
            // "found plenty" at a glance. Cleared on every exit path below.
            TaskbarProgress.MarkBusy();

            try
            {
                if (!settings.Up)
                {
                    Search(stuff, rootDirectory, metrics);
                }
                else
                {
                    List<SearchResult> results = new List<SearchResult>();
                    while (true)
                    {
                        results = Search(stuff, rootDirectory, metrics);

                        if (cancelRequested)
                        {
                            break;
                        }

                        rootDirectory = rootDirectory.Parent;

                        if (rootDirectory == null || results.Count != 0)
                        {
                            break;
                        }
                        if (metrics.Settings.IsVerbose)
                        {
                            log.LogInformation($"Moving up to parent folder {rootDirectory}");
                        }
                        queue = new CeeFindQueue(directorySeparator, stuff, rootDirectory, filenameFilterRegex, negativeFilenameFilterRegex, inFileSearchStrings, settings);
                        queue.Initialize();
                    }
                }

                if (cancelRequested)
                {
                    // Same treatment as the old Ctrl+C handler: report neither stats nor a
                    // found/not-found verdict for a search that did not run to completion. The
                    // 5-second guard inside Finish still decides whether a near-instant cancel
                    // is worth a flush at all.
                    Finish(stuff, rootDirectory, true, false, false, stateFile, metrics);
                    return ExitFound;
                }

                if (settings.IsVerbose)
                {
                    // Thousands separators and a sensible number of decimal places: these are
                    // read by a person, and '0.0636436s' or '227243' take a moment to parse.
                    string scanned = $"Found {metrics.FileCount:N0} files over {metrics.DirectoryCount:N0} directories";
                    string timing = $"Scan time {FormatDuration(metrics.Duration)}. Efficiency {metrics.OverallEfficiency:N0}%.";

                    if (settings.SearchInFiles)
                    {
                        TopExtensionsReport(metrics);
                        Console.WriteLine($"{scanned}, of which {metrics.FileMatchCount:N0} were opened, which resulted in {metrics.FileMatchInsideCount:N0} file matches and {metrics.MatchRowCount:N0} lines matched. {timing}");
                    }
                    else
                    {
                        Console.WriteLine($"{scanned}, of which {metrics.FileMatchCount:N0} were matches. {timing}");
                    }
                }

                Finish(stuff, rootDirectory, false, !queue.IsMore(), true, stateFile, metrics);

                // What the caller actually wants to know: was anything found?
                bool found = settings.SearchInFiles
                    ? metrics.FileMatchInsideCount > 0
                    : metrics.FileMatchCount > 0;

                if (!found)
                {
                    // Say so, unless the output is being consumed by a shell wrapper that
                    // expects a path and nothing else.
                    if (settings.Action != ResultAction.ChangeDirectory)
                    {
                        Console.Error.WriteLine($"ceefind: no match for {string.Join(' ', args)}");
                    }

                    return ExitNothingFound;
                }

                // Launching is left until the search has finished and the index has been
                // written, so the record of the find survives whatever the opened program does.
                if (settings.Action == ResultAction.Open && openTarget != null)
                {
                    return ResultActions.Open(openTarget);
                }

                return ExitFound;
            }
            finally
            {
                TaskbarProgress.Clear();
            }
        }

        /// <summary>
        /// A duration at a precision worth reading. Sub-second timings were printed to
        /// seven decimal places.
        /// </summary>
        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 1)
            {
                return $"{duration.TotalMilliseconds:N0}ms";
            }

            return duration.TotalSeconds < 60
                ? $"{duration.TotalSeconds:N1}s"
                : $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        /// <summary>
        /// Returns the per-user directory used to persist the index. Writing next to the
        /// executable fails when CeeFind is installed to a read-only location such as
        /// Program Files or an MSIX package root.
        ///
        /// CEEFIND_INDEX overrides it. That exists so that testing cannot destroy a real
        /// index: the accumulated knowledge is the whole value of the tool, and a harness
        /// that resets state between runs would otherwise wipe it on every execution.
        /// </summary>
        private static string GetStateDirectory()
        {
            string overridden = Environment.GetEnvironmentVariable("CEEFIND_INDEX");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                try
                {
                    Directory.CreateDirectory(overridden);
                    return overridden;
                }
                catch (Exception)
                {
                    // Fall through to the default rather than failing the search.
                }
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (string.IsNullOrEmpty(root))
            {
                return AppContext.BaseDirectory;
            }

            string stateDirectory = Path.Combine(root, "CeeFind");
            try
            {
                Directory.CreateDirectory(stateDirectory);
                return stateDirectory;
            }
            catch (Exception)
            {
                return AppContext.BaseDirectory;
            }
        }

        private static void ShowHistory(Stuff stuff, DirectoryInfo rootDirectory)
        {
            List<Metrics> history = stuff.GetHistory(rootDirectory.FullName);
            if (history.Count == 0)
            {
                Console.WriteLine("No search history exists for this directory");
            }
            else
            {
                IEnumerable<string> searchHistory = history.OrderByDescending(x => x.SearchDate).Select(x => $"{x.Args} ({HumanTime(DateTime.UtcNow.Subtract(x.SearchDate).TotalSeconds)} ago)").Distinct();
                foreach (String search in searchHistory)
                {
                    Console.WriteLine(search);
                }
            }
        }

        private static void Finish(Stuff stuff, DirectoryInfo rootDirectory, bool isEarlyTerminated, bool isCompleteScan, bool isFinished, string stateFile, Metrics metrics)
        {
            metrics.IsComplete = isCompleteScan;
            metrics.Clean();

            if (metrics.Settings.WriteStateAsJson)
            {
                try
                {
                    stuff.ExportJson("state.json");
                }
                catch (Exception ex)
                {
                    if (metrics.Settings.IsVerbose)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error writing state.json: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }

            if (metrics.IsFileNameSearchWithHumanReadableResults || metrics.IsFileSearchWithHumanReadableResults)
            {
                // either complete, or early termination
                if (isFinished || (isEarlyTerminated && DateTime.UtcNow.Subtract(metrics.SearchDate).TotalSeconds > 5))
                {
                    stuff.AddHistory(rootDirectory.FullName, metrics);

                    try
                    {
                        // Only what this search actually touched is written, in one
                        // transaction - not the whole graph as the old format required.
                        stuff.Flush();
                    }
                    catch (Exception ex)
                    {
                        if (metrics.Settings.IsVerbose)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Error saving index: {ex.Message}");
                            Console.ResetColor();
                        }
                    }

                    try
                    {
                        // Guarded deliberately: enforcing retention budgets must never be
                        // able to crash a search or block the index from being persisted.
                        stuff.Prune();
                    }
                    catch (Exception ex)
                    {
                        if (metrics.Settings.IsVerbose)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Error pruning index: {ex.Message}");
                            Console.ResetColor();
                        }
                    }
                }
            }

            stuff.Dispose();
        }

        private static void TopExtensionsReport(Metrics metrics)
        {
            GenerateExtensionReport(
                "Top file types skipped (binary and suspected binary):", 
                metrics.Top5(metrics.ExcludedBinaries));
            GenerateExtensionReport(
                "Top extensions read:",
                metrics.Top5(metrics.ScanSizeByExtension));
        }

        private static void GenerateExtensionReport(
            string description,
            IEnumerable<KeyValuePair<string, long>> extensions)
        {
            if (extensions.Any())
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(description);
                List<string> topExtensions = extensions.Select(ext => $"{ext.Key} -> {Math.Round((double)ext.Value / 1024 / 1024, 1)}mb").ToList();
                foreach (string topExt in topExtensions)
                {
                    Console.WriteLine("\t" + topExt);
                }
                Console.ResetColor();
            }
        }

        private static void Replace(Stuff stuff, DirectoryInfo rootDirectory, List<SearchResult> results, List<string> inFileSearchString, SearchSettings context, Metrics metrics)
        {
            if (results.Count != 0)
            {
                Console.WriteLine();
                Console.WriteLine("--------------------------------------------------------------");
                Console.WriteLine();
                Console.Write("Please enter the replacement string: ");
                string replaceString = Console.ReadLine();
                List<SearchResult> replaceResults = new List<SearchResult>();
                Console.WriteLine("Searching to determine if ambiguous replacement warning is needed ...");
                Console.ResetColor();
                IEnumerable<FileInfo> distinctResultFiles = (
                    from r in results
                    select r.File).Distinct<FileInfo>();
                foreach (FileInfo result in distinctResultFiles)
                {
                    bool showDirName = true;
                    QueuedDirectory startPath = QueuedDirectory.InitializeRoot(rootDirectory, stuff);
                    Program.SearchFile(stuff, rootDirectory, startPath, new List<Regex>(), replaceString, replaceResults, result, ref showDirName, metrics);
                }
                if (replaceResults.Count <= 0)
                {
                    Console.WriteLine("No ambiguities found!");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Possible ambiguities found! (the string \"{0}\" already exists in these files)", replaceString);
                    Console.ResetColor();
                }
                string[] strArrays = new string[] { string.Concat("\"", inFileSearchString, "\"") };
                Console.Write("Are you sure you want to replace {0} with \"{1}\" in these {2} locations? (y/n): ", string.Join(",", strArrays), replaceString, results.Count);
                if (Console.ReadLine().ToLower().Trim().StartsWith("y"))
                {
                    foreach (FileInfo fileInfo in distinctResultFiles)
                    {
                        Console.WriteLine(string.Concat("Updating ", fileInfo.FullName));
                        string[] contents = File.ReadAllLines(fileInfo.FullName);
                        for (int i = 0; i < (int)contents.Length; i++)
                        {
                            foreach (string pattern in inFileSearchString)
                            {
                                contents[i] = Regex.Replace(contents[i], pattern, replaceString, RegexOptions.IgnoreCase);
                            }
                        }
                        try
                        {
                            File.WriteAllLines(fileInfo.FullName, contents);
                        }
                        catch (Exception exception)
                        {
                            Exception e = exception;
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(e.Message);
                            Console.ResetColor();
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("No results found. Cannot perform replace.");
            }
        }

        private static List<SearchResult> Search(Stuff stuff, DirectoryInfo rootDirectory, Metrics metrics)
        {
            List<SearchResult> results = new List<SearchResult>();
            bool isTopExtensionsReportShown = !metrics.Settings.IsVerbose;
            List<Vertex> verticesWhereObjFound = new List<Vertex>();
            FileInfo[] files;
            FileInfo file;
            Stopwatch sw = new Stopwatch();
            long lastItemFound = -1;
            sw.Start();
            long backOffLoggingDuration = TimeSpan.FromSeconds(5).Ticks;
            while (true)
            {
                if (cancelRequested)
                {
                    break;
                }

                if (!isTopExtensionsReportShown && sw.ElapsedTicks > TimeSpan.TicksPerSecond * 15)
                {
                    TopExtensionsReport(metrics);
                    isTopExtensionsReportShown = true;
                }

                QueuedDirectory directory = queue.Consume();
                if (directory == null)
                {
                    break;
                }

                // First pass: a cheap look at what this subtree is known to contain. If
                // nothing here resembles the search, it goes to the back of the queue -
                // still scanned, just after the promising places.
                if (queue.ShouldDefer(directory))
                {
                    continue;
                }

                bool shownDirName = false;
                bool readFully = queue.ShouldReadFully(directory.Vertex);
                DirectoryInfo[] subdirectories;

                try
                {
                    // One enumeration for both files and subdirectories. Asking the
                    // filesystem to filter meant reading every directory twice, once per
                    // call, and a second full read costs far more than testing the names
                    // here - measured at 55% of the walk on a real tree.
                    (files, subdirectories) = ReadDirectory(directory.Directory, queue, readFully);
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    if (metrics.Settings.IsVerbose)
                    {
                        string relativePath = DirectoryUtils.GetRelativePath(rootDirectory, directory.Directory.FullName);
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Unauthorized: {relativePath}");
                        Console.ResetColor();
                    }
                    continue;
                }
                catch (IOException e)
                {
                    if (metrics.Settings.IsVerbose)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        string relativePath = DirectoryUtils.GetRelativePath(rootDirectory, directory.Directory.FullName);
                        Console.WriteLine($"{e.Message}: {relativePath}");
                        Console.ResetColor();
                    }
                    continue;
                }
                finally
                {
                    metrics.DirectoryCount++;
                }

                FileInfo[] fileInfoArray = files;
                int resultsInDirectory = 0;
                int resultsInFiles = 0;
                for (int i = 0; i < (int)fileInfoArray.Length; i++)
                {
                    metrics.FileCount++;
                    if (metrics.Settings.IsVerbose && sw.ElapsedTicks > backOffLoggingDuration)
                    {
                        Console.WriteLine(ProgressReport(stuff, metrics, rootDirectory, sw));
                        backOffLoggingDuration *= 2;
                    }
                    file = fileInfoArray[i];

                    if (queue.IsFilenameMatch(file.Name))
                    {
                        metrics.FileMatchCount++;
                        resultsInDirectory++;

                        if (!metrics.Settings.SearchInFiles)
                        {
                            directory.Vertex.RecordFind(DateTime.UtcNow);
                            if (queue.FileNameFilters.Length != 0)
                            {
                                verticesWhereObjFound.Add(directory.Vertex);
                                stuff.AddThing(
                                    file.Name,
                                    queue.FileNameFilters,
                                    new List<string>(),
                                    new List<string>(),
                                    directory.Vertex);
                            }

                            if (metrics.Settings.Action == ResultAction.Open)
                            {
                                openTarget = file.FullName;
                            }
                            else if (!metrics.Settings.OutputDirectoriesOnly)
                            {
                                Console.WriteLine(file.FullName);
                            }
                            else
                            {
                                Console.WriteLine(file.Directory.FullName);
                            }

                            results.Add(new SearchResult(new SimpleMatchCollection(), null, file));

                            if (metrics.Settings.First)
                            {
                                EndSearchStatistics(metrics, sw, lastItemFound);
                                return results;
                            }
                        }
                        else
                        {
                            if (SearchFile(stuff, rootDirectory, directory, queue.InsideFileFilterRegex, string.Empty, results, file, ref shownDirName, metrics))
                            {
                                verticesWhereObjFound.Add(directory.Vertex);
                                resultsInFiles++;
                                metrics.FileMatchInsideCount++;

                                if (metrics.Settings.First)
                                {
                                    EndSearchStatistics(metrics, sw, lastItemFound);
                                    return results;
                                }
                            }
                        }
                    }
                }

                int resultCount = metrics.Settings.SearchInFiles ? resultsInFiles : resultsInDirectory;

                // Observing what is here costs nothing - the files are already enumerated -
                // but only a full listing tells the truth about what the directory holds.
                if (readFully)
                {
                    queue.RecordDirectoryContents(
                        directory,
                        fileInfoArray.Select(f => f.Extension),
                        fileInfoArray.Select(f => f.Name),
                        fileInfoArray.Select(f => f.LastWriteTimeUtc));
                }

                if (resultCount > 0)
                {
                    lastItemFound = sw.ElapsedTicks;
                    queue.AddAdjacents(directory.Directory, directory.Vertex, directory.Parent);

                    // Remember when something was found
                    if (directory.Vertex.LastFindCount == null)
                    {
                        directory.Vertex.LastFindCount = new Histogram();
                    }
                    directory.Vertex.LastFindCount.Add(resultCount);

                    // Remember directories where something was found (for index)
                    stuff.RecordFindLocation(directory.Vertex, directory.Directory.FullName, DateTime.UtcNow);
                    queue.RecordFindAncestry(directory, DateTime.UtcNow);

                    TaskbarProgress.ReportFound(
                        metrics.Settings.SearchInFiles ? metrics.FileMatchInsideCount : metrics.FileMatchCount);
                }

                queue.EnqueueSubfolder(directory.Directory, subdirectories);

                // this part finds directories
                if (metrics.Settings.SearchInFiles ? false : !metrics.Settings.SearchFilesOnly)
                {
                    if (queue.IsFilenameMatch(directory.Directory.Name))
                    {
                        Console.WriteLine(directory.Directory.FullName);
                        if (metrics.Settings.First)
                        {
                            EndSearchStatistics(metrics, sw, lastItemFound);
                            return results;
                        }
                    }
                }
            }
            EndSearchStatistics(metrics, sw, lastItemFound);
            return results;
        }

        /// <summary>
        /// Reads a directory once, returning its files and its subdirectories.
        ///
        /// Both were previously fetched with separate calls, which enumerated the
        /// directory twice. A filtered listing still cannot teach the index what a
        /// directory holds, so the caller decides whether to keep every file or only those
        /// that could match.
        /// </summary>
        private static (FileInfo[] Files, DirectoryInfo[] Directories) ReadDirectory(
            DirectoryInfo directory, CeeFindQueue queue, bool readFully)
        {
            List<FileInfo> files = new List<FileInfo>();
            List<DirectoryInfo> directories = new List<DirectoryInfo>();

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo subdirectory)
                {
                    directories.Add(subdirectory);
                }
                else if (entry is FileInfo file && (readFully || queue.PassesEnumerationFilter(file.Name)))
                {
                    files.Add(file);
                }
            }

            return (files.ToArray(), directories.ToArray());
        }

        /// <summary>
        /// Mirrors the protection already applied to GetFiles. Enumerating subdirectories
        /// fails on exactly the same conditions - a protected folder such as System Volume
        /// Information, or a directory removed mid-walk - and left unguarded a single
        /// unreadable directory aborted the whole search instead of being skipped.
        /// </summary>
        private static DirectoryInfo[] GetSubdirectories(
            DirectoryInfo directory, DirectoryInfo rootDirectory, Metrics metrics)
        {
            try
            {
                return directory.GetDirectories();
            }
            catch (DirectoryNotFoundException)
            {
                return Array.Empty<DirectoryInfo>();
            }
            catch (UnauthorizedAccessException)
            {
                if (metrics.Settings.IsVerbose)
                {
                    string relativePath = DirectoryUtils.GetRelativePath(rootDirectory, directory.FullName);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Unauthorized: {relativePath}");
                    Console.ResetColor();
                }

                return Array.Empty<DirectoryInfo>();
            }
            catch (IOException e)
            {
                if (metrics.Settings.IsVerbose)
                {
                    string relativePath = DirectoryUtils.GetRelativePath(rootDirectory, directory.FullName);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{e.Message}: {relativePath}");
                    Console.ResetColor();
                }

                return Array.Empty<DirectoryInfo>();
            }
        }

        private static void EndSearchStatistics(Metrics metrics, Stopwatch sw, long lastItemFound)
        {
            sw.Stop();
            metrics.Duration = sw.Elapsed;
            metrics.OverallEfficiency = lastItemFound == -1 ? 0 : 100 - Math.Round(((double)lastItemFound / sw.ElapsedTicks) * 100.0, 2);
        }

        private static string ProgressReport(Stuff stuff, Metrics metrics, DirectoryInfo rootDirectory, Stopwatch sw)
        {
            string mode = "Inspected";
            List<Metrics> history = stuff.GetHistory(rootDirectory.FullName);
            if (history.Count > 0)
            {
                List<Metrics> metricsFromDir = history.Where(m => m.IsComplete).ToList();

                if (!metrics.Settings.SearchInFiles)
                {
                    List<Metrics> relevantMetrics = metricsFromDir.Where(m => !m.Settings.SearchInFiles).ToList();
                    if (relevantMetrics.Any())
                    {
                        return GenerateEtaReport(mode, metrics, sw, relevantMetrics, false);
                    }
                }
                else if (metrics.IsProbableDeepScan)
                {
                    mode = "Deep Scanned";
                    // search in files, scan many files
                    List<Metrics> relevantMetrics = metricsFromDir.Where(m => m.IsProbableDeepScan).ToList();
                    if (relevantMetrics.Any())
                    {
                        return GenerateEtaReport(mode, metrics, sw, relevantMetrics, false);
                    }
                }
                else
                {
                    mode = "Shallow Scanned";
                    // search in files, scan few files
                    List<Metrics> relevantMetrics = metricsFromDir.Where(m => m.Settings.SearchInFiles && !metrics.IsProbableDeepScan).ToList();
                    if (relevantMetrics.Any())
                    {
                        return GenerateEtaReport(mode, metrics, sw, relevantMetrics, false);
                    }
                }

                // if no pattern to scan type, just approximate
                if (metricsFromDir.Any())
                {
                    return GenerateEtaReport("Scanned", metrics, sw, metricsFromDir, true);
                }
            }
            
            
            return $"{mode} {metrics.FileCount} files.";
        }

        private static string GenerateEtaReport(
            string action, Metrics metrics, Stopwatch sw, List<Metrics> relevantMetrics,
            bool useFileCountOnly
            )
        {
            if (relevantMetrics.Any())
            {
                // it's possible to scale the duration the completed scale if multiple are run, but this logic is convoluted
                List<int> fileCounts = relevantMetrics.Select(m => m.FileCount).OrderBy(m => m).ToList();

                double timeRemainingViaFileCount = (fileCounts[fileCounts.Count / 2] / metrics.FileCount) * sw.Elapsed.TotalSeconds;
                double percentCompleteViaFileCount = (double)metrics.FileCount / fileCounts[fileCounts.Count / 2] * 100;
                double timeRemaining;
                int percentComplete;
                double medianDuration = 0.0;
                if (useFileCountOnly)
                {
                    timeRemaining = timeRemainingViaFileCount;
                    percentComplete = (int)percentCompleteViaFileCount;
                }
                else
                {
                    List<double> durations = relevantMetrics.Select(m => m.Duration.TotalSeconds).OrderBy(m => m).ToList();
                    medianDuration = durations[durations.Count / 2];
                    double timeRemainingViaTimeEstimate = medianDuration - sw.Elapsed.TotalSeconds;
                    double percentCompleteViaTimeEstimate = sw.Elapsed.TotalSeconds / medianDuration * 100;
                    timeRemaining = (timeRemainingViaTimeEstimate + timeRemainingViaFileCount) / 2;
                    percentComplete = (int)((percentCompleteViaTimeEstimate + percentCompleteViaFileCount) / 2);
                }

                if (timeRemaining > 0 && percentComplete < 100)
                {
                    return $"{action} {metrics.FileCount} files. Est. {percentComplete}% with time remaining: <{HumanTime(timeRemaining)}";
                }
                else if (!useFileCountOnly)
                {
                    return $"{action} {metrics.FileCount} files. Taking longer than normal. Median scan time for this directory is <{HumanTime(medianDuration)}, however, based on progress this is more likely to be ~{HumanTime(timeRemainingViaFileCount)}";
                }
                else
                {
                    return $"{action} {metrics.FileCount} files. Est. time remaining ~{HumanTime(timeRemainingViaFileCount)}";
                }
            }
            return String.Empty;
        }

        private static string HumanTime(double seconds)
        {
            if (seconds < 20)
            {
                return ((int)Math.Ceiling(seconds / 5) * 5).ToString() + "s";
            }
            else if (seconds < 60)
            {
                return ((int)Math.Ceiling(seconds / 10) * 10).ToString() + "s";
            }
            else if (seconds < 60 * 60)
            {
                return ((int)Math.Ceiling(seconds / 60)).ToString() + "m";
            }
            else if (seconds < 60 * 60 * 24)
            {
                return ((int)Math.Ceiling(seconds / 60 / 60)).ToString() + "h";
            }
            return ((int)Math.Ceiling(seconds / 60 / 60 / 24)).ToString() + "d";
        }

        private static bool SearchFile(
            Stuff stuff,
            DirectoryInfo rootDirectory, 
            QueuedDirectory currentPath,
            List<Regex> search,
            string searchStr,
            List<SearchResult> allResults,
            FileInfo file,
            ref bool showDirName,
            Metrics metrics)
        {
            if (!metrics.Settings.IncludeBinary)
            {
                if (file.Length > LARGE_FILE_SIZE ||
                    (file.Extension.Length > 1
                    && Program.binaryFiles.Contains(file.Extension.Substring(1))))
                {
                    metrics.ExcludedBinaries[file.Extension] = file.Length + metrics.ExcludedBinaries.GetValueOrDefault(file.Extension, 0);
                    return false;
                }
            }

            metrics.ScanSizeByExtension[file.Extension] = file.Length + metrics.ScanSizeByExtension.GetValueOrDefault(file.Extension, 0);
            metrics.TotalBytesScanned += file.Length;

            int itemsNeeded = (search.Count == 0 ? 1 : search.Count);
            bool[] allFound = new bool[itemsNeeded];
            string line;
            SimpleMatchCollection smc;
            string lastPart;
            int lineCount = 1;
            List<SearchResult> matchList = new List<SearchResult>();
            try
            {
                if ((search.Count != 0 ? false : searchStr.Length == 0))
                {
                    throw new Exception("Cannot search file with nothing to search");
                }
                for (int j = 0; j < itemsNeeded; j++)
                {
                    allFound[j] = false;
                }
                using (FileStream fs = new FileStream(file.FullName,
                                          FileMode.Open,
                                          FileAccess.Read,
                                          FileShare.ReadWrite))
                {
                    string str = string.Empty;
                    bool isRead = false;
                    using (StreamReader sr = new StreamReader(file.FullName))
                    {
                        while (true)
                        {
                            if (!metrics.Settings.IgnoreNewLines)
                            {
                                str = sr.ReadLine();
                            }
                            else
                            {
                                if (!isRead)
                                {
                                    str = sr.ReadToEnd();
                                    isRead = true;
                                }
                                else
                                {
                                    break;
                                }
                            }
                            line = str;
                            if (str == null)
                            {
                                break;
                            }
                            if (search.Count != 0)
                            {
                                for (int i = 0; i < search.Count; i++)
                                {
                                    MatchCollection results = search[i].Matches(line);
                                    smc = new SimpleMatchCollection();
                                    foreach (Match r in results)
                                    {
                                        smc.Matches.Add(new SimpleMatch(r.Index, r.Length, search[i].ToString()));
                                        allFound[i] = true;
                                    }
                                    matchList.Add(new SearchResult(smc, line, file));
                                }
                            }
                            else
                            {
                                int index = line.IndexOf(searchStr);
                                if (index >= 0)
                                {
                                    smc = new SimpleMatchCollection();
                                    SimpleMatch sm = new SimpleMatch(index, searchStr.Length, searchStr);
                                    smc.Matches.Add(sm);
                                    matchList.Add(new SearchResult(smc, line, file));
                                    allFound[0] = true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (metrics.Settings.IsVerbose)
                {
                    Console.WriteLine($"Skipping {file.FullName}: {e.Message}");
                }
                return false;
            }

            if (((IEnumerable<bool>)allFound).All<bool>((bool a) => a))
            {
                currentPath.Vertex.RecordFind(DateTime.UtcNow);

                if (metrics.Settings.Action == ResultAction.Open)
                {
                    openTarget = file.FullName;
                    if (metrics.Settings.First)
                    {
                        return true;
                    }
                }
                else if (metrics.Settings.SearchFilesOnly)
                {
                    Console.WriteLine(file.FullName);
                    if (metrics.Settings.First)
                    {
                        return true;
                    }
                }
                else if (metrics.Settings.OutputDirectoriesOnly)
                {
                    Console.WriteLine(file.Directory.FullName);
                    if (metrics.Settings.First)
                    {
                        return true;
                    }
                }
                else
                {
                    foreach (SearchResult matchInfo in matchList)
                    {
                        SimpleMatchCollection results = matchInfo.Collection;
                        line = matchInfo.Line;
                        if (results.Matches.Count > 0)
                        {
                            allResults.Add(matchInfo);
                            if (!showDirName)
                            {
                                string formattedPath = file.Directory.FullName.Replace(rootDirectory.FullName, string.Empty);
                                if (formattedPath.Length > 1)
                                {
                                    // A trailing separator marks this as a directory
                                    // heading rather than another match. Printed bare it
                                    // read as a stray word between results.
                                    ConsoleColours.WriteLine(
                                        formattedPath.Substring(1) + Path.DirectorySeparatorChar,
                                        ConsoleColor.DarkGray);
                                    showDirName = true;
                                }
                            }

                            List<string> capturedItems = new List<string>();
                            foreach (SimpleMatch result in results.Matches)
                            {
                                metrics.MatchRowCount++;
                                string firstPart = line.Substring(0, result.Index);
                                string capturedItem = line.Substring(result.Index, result.Length);

                                lastPart = (result.Index + result.Length <= line.Length ? line.Substring(result.Index + result.Length) : string.Empty);

                                // Only the outer edges are trimmed. Trimming the inner
                                // edges - the characters adjacent to the match - removed
                                // the space either side of it, so 'public Histogram
                                // LastFindCount' was printed as 'public HistogramLast-
                                // FindCount'. The tool was misreporting file contents.
                                firstPart = firstPart.TrimStart();
                                lastPart = lastPart.TrimEnd();

                                string prefix = metrics.Settings.IgnoreNewLines
                                    ? string.Format("{0} ({1}-{2}):", file.Name, result.Index, result.Index + capturedItem.Length)
                                    : string.Format("{0} ({1},{2}):", file.Name, lineCount, result.Index);

                                // Pad to a common width so the eye can run down the
                                // content column. A name that overruns it keeps at least
                                // one space, or the colon ran straight into the content:
                                // 'Thing.cs (1,6):class Needle'.
                                prefix = prefix.Length >= MatchColumnWidth
                                    ? prefix + " "
                                    : prefix.PadRight(MatchColumnWidth, ' ');

                                // Fit the line to the terminal, so the aligned filename
                                // column survives. A long match or a minified line could
                                // previously emit several hundred characters and wrap.
                                (firstPart, capturedItem, lastPart) =
                                    ConsoleLayout.Fit(firstPart, capturedItem, lastPart, prefix.Length);

                                Console.Write(string.Concat(prefix, firstPart));
                                ConsoleColours.WriteMatch(capturedItem);
                                Console.WriteLine(lastPart);
                            }

                            stuff.AddThing(
                                file.Name,
                                queue.FileNameFilters,
                                results.Matches.Select(m => m.RegexMethod).ToList(),
                                capturedItems,
                                currentPath.Vertex);
                        }
                        lineCount++;
                    }

                }
                return true;
            }
            return false;
        }
    }
}
