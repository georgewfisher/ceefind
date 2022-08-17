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

namespace CeeFind
{
    internal class Program
    {
        private const char DIRECTORY_SEPARATOR = '\\';
        private static HashSet<string> flags;

        private static Stuff stuff;
        private static CeeFindQueue queue;

        private static HashSet<string> binaryFiles;
        private static DirectoryInfo rootDirectory;

        private static ILogger<Program> log;

        public Program()
        {
        }

        private static void Main(string[] args)
        {
            ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder
                        // add console as logging target
                        .AddConsole()
                        // set minimum level to log
                        .SetMinimumLevel(LogLevel.Debug));
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
            bool searchInFiles = false;
            List<string> inFileSearchStrings = new List<string>();
            Program.flags = new HashSet<string>();
            List<Regex> search = new List<Regex>();
            string codebase = Assembly.GetExecutingAssembly().GetName().CodeBase;
            string stateFile = Path.Combine(Path.GetDirectoryName(codebase), "state_v2.json");
            stateFile = stateFile.Replace("file:\\", string.Empty);

            if (File.Exists(stateFile))
            {
                stuff = (Stuff)JsonSerializer.Deserialize(File.ReadAllText(stateFile), typeof(Stuff));
            }
            else
            {
                stuff = new Stuff();
            }

            string[] strArrays = args;
            bool containsDivider = args.Any(a => a == "--");
            bool filenamePart = true;
            bool isNegated = false;
            for (int i = 0; i < (int)strArrays.Length; i++)
            {
                string arg = strArrays[i];
                if (arg == "history" && args.Length == 1)
                {
                    ShowHistory();
                    return;
                }
                if (arg == "--")
                {
                    filenamePart = false;
                }
                else if (arg == "not")
                {
                    isNegated = true;
                }
                else if (arg.StartsWith("-"))
                {
                    Program.flags.Add(arg.Substring(1).ToLower());
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
                        negativeFilenameFilterRegex.Add(CleanFilenameFilter(filter));
                    }
                    else if (arg != "*")
                    {
                        filenameFilterRegex.Add(CleanFilenameFilter(filter));
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
                        log.LogWarning(@$"Using ""*."" in file search string ""{arg}"" with regular expression ""\\..*"" to make searches easier to write.");
                        arg = Regex.Replace(arg, "(?<!\\.)\\*\\.", "\\..*");
                    }

                    inFileSearchStrings.Add(arg);
                    searchInFiles = true;
                }
            }

            rootDirectory = new DirectoryInfo(rootDirectoryString);

            if (filenameFilterRegex.All(f => f == "^.*$") && !negativeFilenameFilterRegex.Any())
            {
                flags.Add("all");
            }

            queue = new CeeFindQueue(stuff, rootDirectory, filenameFilterRegex, negativeFilenameFilterRegex, inFileSearchStrings);
            queue.Initialize();

            bool isVerbose = Program.flags.Contains("v") || Program.flags.Contains("verbose");
            bool includeBinary = Program.flags.Contains("b") || Program.flags.Contains("binary");

            List<SearchResult> results = new List<SearchResult>();
            if (isVerbose)
            {
                string searchDescription = String.Join(", ", search.Select(s => s.ToString()));
                Console.WriteLine($"Searching for {searchDescription}...");
                Console.WriteLine($"Including binary files: " + includeBinary);
            }
            SearchContext context = new SearchContext(isVerbose, includeBinary);
            Metrics metrics = new Metrics(searchInFiles, string.Join(' ', args));

            Console.CancelKeyPress += delegate
            {
                if (context.IsVerbose)
                {
                    Console.WriteLine("Terminated early");
                }
                Finish(false, stateFile, metrics);
                Environment.Exit(0);
            };

            if (!Program.flags.Contains("up"))
            {
                Program.Search(searchInFiles, results, context, metrics);
            }
            else
            {
                while (true)
                {
                    if ((results.Count != 0 ? true : rootDirectory == null))
                    {
                        break;
                    }
                    Program.Search(searchInFiles, results, context, metrics);
                    rootDirectory = rootDirectory.Parent;
                    queue = new CeeFindQueue(stuff, rootDirectory, filenameFilterRegex, negativeFilenameFilterRegex, inFileSearchStrings);
                    queue.Initialize();
                }
            }
            if (context.IsVerbose)
            {
                if (searchInFiles)
                {
                    TopExtensionsReport(metrics);
                    Console.WriteLine($"Found {metrics.FileCount} files over {metrics.DirectoryCount} directories, of which {metrics.FileMatchCount} were opened, which resulted in {metrics.FileMatchInsideCount} file matches and {metrics.MatchRowCount} lines matched. Scan time {metrics.Duration.TotalSeconds}s.");
                }
                else
                {
                    Console.WriteLine($"Found {metrics.FileCount} files over {metrics.DirectoryCount} directories, of which {metrics.FileMatchCount} were matches. Scan time {metrics.Duration.TotalSeconds}s.");
                }
            }
            if (Program.flags.Contains("replace"))
            {
                Program.Replace(results, inFileSearchStrings, context, metrics);
            }

            Finish(true, stateFile, metrics);
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
            if (filter.Contains(DIRECTORY_SEPARATOR)) {
                // Attempt to extract directories from prefix.
                // Slash could have other meanings so assume user knows what they are doing
                StringBuilder rootPath = new StringBuilder();
                for (int i = 0; i < filter.Length; i++)
                {
                    rootPath.Append(filter[i]);
                    if (filter[i] == DIRECTORY_SEPARATOR)
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

        private static void ShowHistory()
        {
            if (!stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
            {
                Console.WriteLine("No search history exists for this directory");
            }
            else
            {
                IEnumerable<string> searchHistory = stuff.SearchHistory[rootDirectory.FullName].OrderByDescending(x => x.SearchDate).Select(x => $"{x.Args} ({(int)DateTime.UtcNow.Subtract(x.SearchDate).TotalDays} days ago)").Distinct();
                foreach (String search in searchHistory)
                {
                    Console.WriteLine(search);
                }
            }
        }

        private static string CleanFilenameFilter(string arg)
        {
            string filter = arg;
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
                log.LogWarning(@$"Updating ""*"" in filename search string ""{arg}"" with regular expression ""{filter}"" to make searches easier to write.");
            }

            return filter;
        }

        private static void Finish(bool isComplete, string stateFile, Metrics metrics)
        {
            stuff.Clean();
            metrics.IsComplete = isComplete;
            metrics.Clean();
            if (!stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
            {
                stuff.SearchHistory.Add(rootDirectory.FullName, new List<Metrics>());
            }
            stuff.SearchHistory[rootDirectory.FullName].Add(metrics);

            if (metrics.IsFileNameSearchWithHumanReadableResults || metrics.IsFileSearchWithHumanReadableResults)
            {
                // either complete, or early termination
                if (isComplete || (!isComplete && DateTime.UtcNow.Subtract(metrics.SearchDate).TotalSeconds > 5))
                {
                    File.WriteAllText(stateFile, JsonSerializer.Serialize(stuff));
                }
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

        private static void Replace(List<SearchResult> results, List<string> inFileSearchString, SearchContext context, Metrics metrics)
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
                    Program.SearchFile(startPath, new List<Regex>(), replaceString, replaceResults, result, ref showDirName, context, metrics);
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

        private static Metrics Search(bool searchInFiles, List<SearchResult> allResults, SearchContext context, Metrics metrics)
        {
            bool isTopExtensionsReportShown = !context.IsVerbose;
            List<Vertex> verticesWhereObjFound = new List<Vertex>();
            FileInfo[] files;
            FileInfo file;
            Stopwatch sw = new Stopwatch();
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
                    if (context.IsVerbose)
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
                    if (context.IsVerbose)
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
                for (int i = 0; i < (int)fileInfoArray.Length; i++)
                {
                    metrics.FileCount++;
                    if (context.IsVerbose && sw.ElapsedTicks > backOffLoggingDuration)
                    {
                        Console.WriteLine(ProgressReport(metrics, sw));
                        backOffLoggingDuration *= 2;
                    }
                    file = fileInfoArray[i];

                    if (queue.IsFilenameMatch(file.Name))
                    { 
                        metrics.FileMatchCount++;
                        resultsInDirectory++;

                        // Remember directories where something was found (for index)
                        string fullname = directory.Directory.FullName;
                        if (!directory.Vertex.AbsolutePaths.Contains(fullname))
                        {
                            directory.Vertex.AbsolutePaths.Add(fullname);
                        }

                        if (!searchInFiles)
                        {
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

                            if (!Program.flags.Contains("dirs"))
                            {
                                Console.WriteLine(file.FullName);
                            }
                            else
                            {
                                Console.WriteLine(file.Directory.FullName);
                            }
                            if (flags.Contains("first"))
                            {
                                Environment.Exit(0);
                            }
                        }
                        else
                        {
                            if (SearchFile(directory, queue.InsideFileFilterRegex, string.Empty, allResults, file, ref shownDirName, context, metrics))
                            {
                                verticesWhereObjFound.Add(directory.Vertex);
                                metrics.FileMatchInsideCount++;
                            }
                        }
                    }
                }

                if (resultsInDirectory > 0)
                {
                    directory.Vertex.LastFindCount.Add(resultsInDirectory);
                }

                queue.EnqueueSubfolder(directory.Directory, directory.Directory.GetDirectories());

                // this part finds directories
                if ((searchInFiles ? false : !Program.flags.Contains("files")))
                {
                    if (queue.IsFilenameMatch(directory.Directory.Name))
                    {
                        Console.WriteLine(directory.Directory.FullName);
                        if (Program.flags.Contains("first"))
                        {
                            Environment.Exit(0);
                        }
                    }
                }
            }
            sw.Stop();
            metrics.Duration = sw.Elapsed;

            return metrics;
        }

        private static string ProgressReport(Metrics metrics, Stopwatch sw)
        {
            string mode = "Inspected";
            if (stuff.SearchHistory.ContainsKey(rootDirectory.FullName))
            {
                List<Metrics> metricsFromDir = stuff.SearchHistory[rootDirectory.FullName].Where(m => m.IsComplete).ToList();

                if (!metrics.SearchInFiles)
                {
                    List<Metrics> relevantMetrics = metricsFromDir.Where(m => !m.SearchInFiles).ToList();
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
                    List<Metrics> relevantMetrics = metricsFromDir.Where(m => m.SearchInFiles && !metrics.IsProbableDeepScan).ToList();
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
            
            return ((int)Math.Ceiling(seconds / 60)).ToString() + "m";
        }

        private static bool SearchFile(QueuedDirectory currentPath, List<Regex> search, string searchStr, List<SearchResult> allResults, FileInfo file, ref bool showDirName, SearchContext context, Metrics metrics)
        {
            if (!context.IncludeBinary)
            {
                if (file.Length > 1000000 ||
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
                    using (StreamReader sr = new StreamReader(file.FullName))
                    {
                        while (true)
                        {
                            string str = sr.ReadLine();
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
                if (context.IsVerbose)
                {
                    Console.WriteLine($"Skipping {file.FullName}: {e.Message}");
                }
                return false;
            }

            if (((IEnumerable<bool>)allFound).All<bool>((bool a) => a))
            {
                currentPath.Vertex.LastFinds.Add(DateTime.UtcNow);

                if (Program.flags.Contains("files"))
                {
                    Console.WriteLine(file.FullName);
                    if (Program.flags.Contains("first"))
                    {
                        Environment.Exit(0);
                    }
                }
                else if (!Program.flags.Contains("dirs"))
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
                                Console.Write(string.Concat(string.Format("{0} ({1},{2}):", file.Name, lineCount, result.Index).PadRight(35, ' '), firstPart));
                                Console.ForegroundColor = ConsoleColor.White;
                                Console.Write(capturedItem);
                                Console.ResetColor();
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
                else
                {
                    Console.WriteLine(file.Directory.FullName);
                    if (Program.flags.Contains("first"))
                    {
                        Environment.Exit(0);
                    }
                }
                return true;
            }
            return false;
        }
    }
}