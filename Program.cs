using CeeFind.BetterQueue;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CeeFind
{
    internal class Program
    {
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

            rootDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
            binaryFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string binaryPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (string extension in File.ReadAllLines(Path.Combine(binaryPath, "binary_files.txt")))
            {
                binaryFiles.Add(extension);
            }
            string filenameFilterRegex = string.Empty;
            bool searchInFiles = false;
            List<string> inFileSearchStrings = new List<string>();
            int argIndex = 0;
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
            for (int i = 0; i < (int)strArrays.Length; i++)
            {
                string arg = strArrays[i];
                if (arg.StartsWith("-"))
                {
                    Program.flags.Add(arg.Substring(1).ToLower());
                }
                else if (argIndex == 0)
                {
                    if (!filenameFilterRegex.StartsWith("^"))
                    {
                        filenameFilterRegex = string.Concat("^", arg);
                    }
                    if (!filenameFilterRegex.EndsWith("$"))
                    {
                        filenameFilterRegex = string.Concat(filenameFilterRegex, "$");
                    }

                    if (Regex.IsMatch(filenameFilterRegex, "(?<!\\.)\\*"))
                    {
                        log.LogWarning(@$"Replacing ""*"" in filename search string ""{arg}"" with regular expression "".*"" to make searches easier to write.");
                        filenameFilterRegex = Regex.Replace(filenameFilterRegex, "(?<!\\.)\\*", ".*");
                    }

                    if (filenameFilterRegex == "^.*$")
                    {
                        flags.Add("all");
                    }

                    argIndex++;
                }
                else if (argIndex > 0)
                {
                    if (Regex.IsMatch(arg, $"(?<!\\.)\\*\\."))
                    {
                        log.LogWarning(@$"Replacing ""*."" in file search string ""{arg}"" with regular expression ""\\..*"" to make searches easier to write.");
                        arg = Regex.Replace(arg, "(?<!\\.)\\*\\.", "\\..*");
                    }

                    inFileSearchStrings.Add(arg);
                    searchInFiles = true;
                }
            }


            queue = new CeeFindQueue(stuff, rootDirectory, filenameFilterRegex, inFileSearchStrings);
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
            if (!Program.flags.Contains("up"))
            {
                Program.Search(new List<string>(), filenameFilterRegex, searchInFiles, search, results, 0, context);
            }
            else
            {
                while (true)
                {
                    if ((results.Count != 0 ? true : rootDirectory == null))
                    {
                        break;
                    }
                    Program.Search(new List<string>(), filenameFilterRegex, searchInFiles, search, results, 0, context);
                    rootDirectory = rootDirectory.Parent;
                    queue = new CeeFindQueue(stuff, rootDirectory, filenameFilterRegex, inFileSearchStrings);
                    queue.Initialize();
                }
            }
            if (context.IsVerbose)
            {
                Console.WriteLine($"Inspected {context.Count} files");
            }
            if (Program.flags.Contains("replace"))
            {
                Program.Replace(results, inFileSearchStrings, context);
            }

            stuff.Clean();

            File.WriteAllText(stateFile, JsonSerializer.Serialize(stuff));
        }

        private static void Replace(List<SearchResult> results, List<string> inFileSearchString, SearchContext context)
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
                    Program.SearchFile(startPath, string.Empty, new List<Regex>(), replaceString, replaceResults, result, ref showDirName, context);
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

        private static void Search(List<string> path, string searchString, bool searchInFiles, List<Regex> search, List<SearchResult> allResults, int depth, SearchContext context)
        {
            bool all = flags.Contains("all");
            List<Vertex> verticesWhereObjFound = new List<Vertex>();
            long resultCount = 0;
            FileInfo[] files;
            FileInfo file;
            long directoryCount = 0;
            while (true)
            {
                QueuedDirectory directory = queue.Consume();
                if (directory == null)
                {
                    return;
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
                    continue;
                }
                FileInfo[] fileInfoArray = files;
                for (int i = 0; i < (int)fileInfoArray.Length; i++)
                {
                    context.Count++;
                    if (context.IsVerbose && context.Count % 10000 == 0)
                    {
                        Console.WriteLine($"Inspected {context.Count} files");
                    }
                    file = fileInfoArray[i];

                    if (all || Regex.IsMatch(file.Name, searchString, RegexOptions.IgnoreCase))
                    {
                        if (!searchInFiles)
                        {
                            directory.Vertex.LastFinds.Add(DateTime.UtcNow);
                            if (!all)
                            {
                                verticesWhereObjFound.Add(directory.Vertex);
                                resultCount++;

                                stuff.AddThing(
                                    file.Name,
                                    searchString,
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
                        if (searchInFiles)
                        {
                            if (SearchFile(directory, searchString, queue.InsideFileFilterRegex, string.Empty, allResults, file, ref shownDirName, context))
                            {
                                verticesWhereObjFound.Add(directory.Vertex);
                                resultCount++;
                            }
                        }
                    }
                }

                queue.EnqueueSubfolder(directory.Directory, directory.Directory.GetDirectories());

                if ((searchInFiles ? false : !Program.flags.Contains("files")))
                {
                    if (Regex.IsMatch(directory.Directory.Name, searchString, RegexOptions.IgnoreCase))
                    {
                        Console.WriteLine(directory.Directory.FullName);
                        if (Program.flags.Contains("first"))
                        {
                            Environment.Exit(0);
                        }
                    }
                }
                directoryCount++;
            }
            foreach (Vertex v in verticesWhereObjFound)
            {
                v.LastFindCount.Add(resultCount);
            }

            return;
        }

        private static bool SearchFile(QueuedDirectory currentPath, string filenameSearchRegex, List<Regex> search, string searchStr, List<SearchResult> allResults, FileInfo file, ref bool showDirName, SearchContext context)
        {
            if (!context.IncludeBinary)
            {
                if (file.Length > 1000000 ||
                    (file.Extension.Length > 1
                    && Program.binaryFiles.Contains(file.Extension.Substring(1))))
                {
                    if (context.IsVerbose)
                    {
                        Console.WriteLine("Excluding " + file.Extension + " of size " + file.Length);
                    }
                    return false;
                }
            }

            int itemsNeeded = (search.Count == 0 ? 1 : search.Count);
            bool[] allFound = new bool[itemsNeeded];
            string line;
            SimpleMatchCollection smc;
            string lastPart;
            int lineCount = 0;
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
                                filenameSearchRegex,
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