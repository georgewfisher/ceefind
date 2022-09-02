using CeeFind.BetterQueue;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CeeFind.Utils;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using CeeFind.Files;
using System.IO.Compression;
using System.Threading.Tasks;
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
        private static readonly object terminationLock = new object();

        public Program()
        {
        }

        private static void Main(string[] args)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                directorySeparator = DIRECTORY_SEPARATOR_WINDOWS;
            }
            else
            {
                directorySeparator = DIRECTORY_SEPARATOR_OTHER;
            }

            string codebase = Assembly.GetExecutingAssembly().GetName().CodeBase;
            string stateFile = Path.Combine(Path.GetDirectoryName(codebase), "state_v2.json.gz");
            stateFile = stateFile.Replace("file:\\", string.Empty);
            Task<Stuff> task;
            if (File.Exists(stateFile))
            {
                task = LoadHistory(stateFile);
            }
            else
            {
                task = new Task<Stuff>(
                    () =>
                        new Stuff());
                task.Start();
            }

            ILoggerFactory loggerFactory = LoggerFactory.Create(
                builder => builder
                            .AddConsole()
                            .SetMinimumLevel(LogLevel.Information));
            log = loggerFactory.CreateLogger<Program>();
            string rootDirectoryString = Directory.GetCurrentDirectory();
            binaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string binaryPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (string extension in File.ReadAllLines(Path.Combine(binaryPath, "binary_files.txt")))
            {
                binaryFiles.Add(extension);
            }
            List<string> filenameFilterRegex = new List<string>();
            List<string> negativeFilenameFilterRegex = new List<string>();
            List<string> inFileSearchStrings = new List<string>();
            List<Regex> search = new List<Regex>();
            SearchSettings settings = new SearchSettings();

            string[] strArrays = args;
            bool containsDivider = args.Any(a => a == "--");
            bool filenamePart = true;
            bool isNegated = false;
            List<string> warnings = new List<string>();
            for (int i = 0; i < (int)strArrays.Length; i++)
            {
                string arg = strArrays[i];
                if (arg == "--")
                {
                    filenamePart = false;
                }
                else if (arg.StartsWith("-"))
                {
                    string argValue = arg.Substring(1).ToLower();
                    switch (argValue)
                    {
                        case "silent":
                            settings.IsSilent = true;
                            break;
                        case "binary":
                        case "b":
                            settings.IncludeBinary = true;
                            break;
                        case "verbose":
                        case "v":
                            settings.IsVerbose = true;
                            break;
                        case "history":
                        case "h":
                            settings.ShowHistory = true;
                            break;
                        case "previous":
                        case "p":
                            settings.ShowPreviousResults = true;
                            break;
                        case "dirs":
                        case "dir":
                        case "d":
                            settings.OutputDirectoriesOnly = true;
                            break;
                        case "files":
                        case "file":
                            settings.SearchFilesOnly = true;
                            break;
                        case "up":
                        case "u":
                            settings.Up = true;
                            break;
                        case "first":
                        case "f":
                            settings.First = true;
                            break;
                        case "sensitive":
                        case "s":
                            settings.CaseSensitive = true;
                            break;
                        case "json":
                        case "j":
                            settings.WriteStateAsJson = true;
                            break;
                        case "regex":
                        case "r":
                            settings.NoRegexAssist = true;
                            break;
                        case "ignorenewlines":
                        case "n":
                            settings.IgnoreNewLines = true;
                            break;
                    }
                }
                else if (filenamePart && arg == "not")
                {
                    isNegated = true;
                }
                else if (filenamePart)
                {
                    string filter;
                    if (DiscoverRootPath(arg, out string replacementRoot, out string filterWithoutRoot))
                    {
                        filter = filterWithoutRoot;
                        rootDirectoryString = replacementRoot;
                        log.LogInformation($"Search path updated to {replacementRoot}");
                    }
                    else
                    {
                        filter = arg;
                    }

                    if (isNegated)
                    {
                        negativeFilenameFilterRegex.Add(CleanFilenameFilter(filter, warnings, settings));
                    }
                    else if (arg != "*")
                    {
                        filenameFilterRegex.Add(CleanFilenameFilter(filter, warnings, settings));
                    }

                    if (!containsDivider)
                    {
                        filenamePart = false;
                    }
                }
                else if (!filenamePart)
                {
                    if (Regex.IsMatch(arg, $"(?<!\\.)\\*\\."))
                    {
                        warnings.Add(@$"Using ""*."" in file search string ""{arg}"" with regular expression ""\\..*"" to make searches easier to write.");
                        arg = Regex.Replace(arg, "(?<!\\.)\\*\\.", "\\..*");
                    }

                    inFileSearchStrings.Add(arg);
                    settings.SearchInFiles = true;
                }
            }

            /*if (settings.ShowPreviousResults)
            {
                (string, Metrics) mostRecent = stuff.SearchHistory.SelectMany(sh => sh.Value.Select(v => (sh.Key, v))).OrderByDescending(v => v.Item2.SearchDate).FirstOrDefault();
                if (mostRecent != null)
                {
                    rootDirectoryString = mostRecent.Item1;
                    settings = mostRecent.Item2.Settings;
                }
            }*/

            DirectoryInfo rootDirectory = new DirectoryInfo(rootDirectoryString);
            if (filenameFilterRegex.All(f => f == "^.*$") && !negativeFilenameFilterRegex.Any())
            {
                settings.ScanAllFiles = true;
            }

            Metrics metrics = new Metrics(settings, string.Join(' ', args));

            // Setup complete, proceed with search
            Stuff stuff = task.Result;

            Console.CancelKeyPress += delegate
            {
                if (settings.IsVerbose)
                {
                    Console.WriteLine("Terminated early");
                }
                Finish(stuff, rootDirectory, true, false, false, stateFile, metrics);
                Environment.Exit(0);
            };

            if (settings.ShowHistory)
            {
                ShowHistory(stuff, rootDirectory);
                return;
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
            if (settings.IsVerbose)
            {
                if (settings.SearchInFiles)
                {
                    TopExtensionsReport(metrics);
                    Console.WriteLine($"Found {metrics.FileCount} files over {metrics.DirectoryCount} directories, of which {metrics.FileMatchCount} were opened, which resulted in {metrics.FileMatchInsideCount} file matches and {metrics.MatchRowCount} lines matched. Scan time {metrics.Duration.TotalSeconds}s. Efficiency {metrics.OverallEfficiency}%.");
                }
                else
                {
                    Console.WriteLine($"Found {metrics.FileCount} files over {metrics.DirectoryCount} directories, of which {metrics.FileMatchCount} were matches. Scan time {metrics.Duration.TotalSeconds}s. Efficiency {metrics.OverallEfficiency}%.");
                }
            }

            Finish(stuff, rootDirectory, false, !queue.IsMore(), true, stateFile, metrics);
        }

        private static async Task<Stuff> LoadHistory(string stateFile)
        {
            Stuff stuff;
            using (FileStream stream = File.Open(stateFile, FileMode.Open))
            {
                using (GZipStream compressedStream = new GZipStream(stream, CompressionMode.Decompress))
                {
                    ValueTask<Stuff> task = JsonSerializer.DeserializeAsync<Stuff>(compressedStream);
                    await task;
                    stuff = task.Result;
                }
            }
            return stuff;
        }

        /// <summary>
        /// Allows for a root path to be provided as part of a path filter, allowing for things like C:\myfiles\*.txt
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="rootDirectoryString"></param>
        /// <param name="replacementFilter"></param>
        /// <returns></returns>
        private static bool DiscoverRootPath(string filter, out string rootDirectoryString, out string replacementFilter)
        {
            rootDirectoryString = string.Empty;
            replacementFilter = string.Empty;
            if (filter.Contains(directorySeparator)) {
                // Attempt to extract directories from prefix.
                // Slash could have other meanings so assume user knows what they are doing
                StringBuilder rootPath = new StringBuilder();
                for (int i = 0; i < filter.Length; i++)
                {
                    rootPath.Append(filter[i]);
                    if (filter[i] == directorySeparator)
                    {
                        string currentRoot = rootPath.ToString();
                        if (Directory.Exists(currentRoot))
                        {
                            rootDirectoryString = currentRoot;
                        }
                    }
                }
            }
            replacementFilter = filter.Substring(rootDirectoryString.Length, filter.Length - rootDirectoryString.Length);
            return rootDirectoryString.Length > 0;
        }

        private static void ShowHistory(Stuff stuff, DirectoryInfo rootDirectory)
        {
            if (!stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
            {
                Console.WriteLine("No search history exists for this directory");
            }
            else
            {
                IEnumerable<string> searchHistory = stuff.SearchHistory[rootDirectory.FullName].OrderByDescending(x => x.SearchDate).Select(x => $"{x.Args} ({HumanTime(DateTime.UtcNow.Subtract(x.SearchDate).TotalSeconds)} ago)").Distinct();
                foreach (String search in searchHistory)
                {
                    Console.WriteLine(search);
                }
            }
        }

        private static string CleanFilenameFilter(string arg, List<string> warnings, SearchSettings settings)
        {
            string filter = arg;

            if (!settings.NoRegexAssist)
            {
                if (!filter.StartsWith("^"))
                {
                    filter = string.Concat("^", filter);
                }
                if (!filter.EndsWith("$"))
                {
                    filter = string.Concat(filter, "$");
                }

                if (Regex.IsMatch(filter, "(?<!\\.)\\*"))
                {
                    filter = Regex.Replace(filter, "(?<!\\.)\\*", ".*");
                    warnings.Add(@$"Updating ""*"" in filename search string ""{arg}"" with regular expression ""{filter}"" to make searches easier to write.");
                }
            }

            return filter;
        }

        private static void Finish(Stuff stuff, DirectoryInfo rootDirectory, bool isEarlyTerminated, bool isCompleteScan, bool isFinished, string stateFile, Metrics metrics)
        {
            lock (terminationLock)
            {
                stuff.Clean();
                metrics.IsComplete = isCompleteScan;
                metrics.Clean();

                // Store the search results for the root directory
                if (!stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
                {
                    stuff.SearchHistory.Add(rootDirectory.FullName, new List<Metrics>());
                }
                stuff.SearchHistory[rootDirectory.FullName].Add(metrics);

                if (metrics.Settings.WriteStateAsJson)
                {
                    File.WriteAllText("state.json", JsonSerializer.Serialize(stuff, new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull
                    }));
                }

                if (metrics.IsFileNameSearchWithHumanReadableResults || metrics.IsFileSearchWithHumanReadableResults)
                {
                    // either complete, or early termination
                    if (isFinished || (isEarlyTerminated && DateTime.UtcNow.Subtract(metrics.SearchDate).TotalSeconds > 5))
                    {
                        using (FileStream stream = File.Open(stateFile, FileMode.Create))
                        {
                            using (GZipStream compressedStream = new GZipStream(stream, CompressionMode.Compress))
                            {
                                Task task = JsonSerializer.SerializeAsync(compressedStream, stuff, new JsonSerializerOptions
                                {
                                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull
                                });
                                task.Wait();
                            }
                        }
                    }
                }

                Environment.Exit(0);
            }
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

                bool shownDirName = false;

                try
                {
                    files = directory.Directory.GetFiles();
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
                            if (directory.Vertex.LastFinds == null)
                            {
                                directory.Vertex.LastFinds = new List<DateTime>();
                            }
                            directory.Vertex.LastFinds.Add(DateTime.UtcNow);
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

                            if (!metrics.Settings.OutputDirectoriesOnly)
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
                if (resultCount > 0)
                {
                    lastItemFound = sw.ElapsedTicks;
                    queue.AddAdjacents(directory.Directory, directory.Vertex, directory.Parent.GetHashCode());

                    // Remember when something was found
                    if (directory.Vertex.LastFindCount == null)
                    {
                        directory.Vertex.LastFindCount = new Histogram();
                    }
                    directory.Vertex.LastFindCount.Add(resultCount);
                    
                    // Remember directories where something was found (for index)
                    string fullname = directory.Directory.FullName;
                    if (directory.Vertex.AbsolutePaths == null)
                    {
                        directory.Vertex.AbsolutePaths = new HashSet<string>();
                    }
                    if (!directory.Vertex.AbsolutePaths.Contains(fullname))
                    {
                        directory.Vertex.AbsolutePaths.Add(fullname);
                    }
                }

                queue.EnqueueSubfolder(directory.Directory, directory.Directory.GetDirectories());

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

        private static void EndSearchStatistics(Metrics metrics, Stopwatch sw, long lastItemFound)
        {
            sw.Stop();
            metrics.Duration = sw.Elapsed;
            metrics.OverallEfficiency = lastItemFound == -1 ? 0 : 100 - Math.Round(((double)lastItemFound / sw.ElapsedTicks) * 100.0, 2);
        }

        private static string ProgressReport(Stuff stuff, Metrics metrics, DirectoryInfo rootDirectory, Stopwatch sw)
        {
            string mode = "Inspected";
            if (stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
            {
                List<Metrics> metricsFromDir = stuff.SearchHistory[rootDirectory.FullName].Where(m => m.IsComplete).ToList();

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
                if (currentPath.Vertex.LastFinds == null)
                {
                    currentPath.Vertex.LastFinds = new List<DateTime>();
                }

                currentPath.Vertex.LastFinds.Add(DateTime.UtcNow);

                if (metrics.Settings.SearchFilesOnly)
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
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine(formattedPath.Substring(1));
                                    Console.ResetColor();
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
                                firstPart = firstPart.TrimStart(new char[0]);
                                lastPart = lastPart.TrimEnd(new char[0]);
                                if (firstPart.Length > 100)
                                {
                                    firstPart = string.Concat("...", firstPart.Substring(firstPart.Length - 30));
                                }
                                if (lastPart.Length > 100)
                                {
                                    lastPart = string.Concat(lastPart.Substring(0, 30), "...");
                                }
                                if (metrics.Settings.IgnoreNewLines)
                                {
                                    Console.Write(string.Concat(string.Format("{0} ({1}-{2}):", file.Name, result.Index, result.Index + capturedItem.Length).PadRight(35, ' '), firstPart));
                                }
                                else
                                {
                                    Console.Write(string.Concat(string.Format("{0} ({1},{2}):", file.Name, lineCount, result.Index).PadRight(35, ' '), firstPart));
                                }
                                Console.ForegroundColor = ConsoleColor.White;
                                Console.Write(capturedItem);
                                Console.ResetColor();
                                Console.WriteLine(lastPart.Trim());
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