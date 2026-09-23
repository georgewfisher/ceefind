using System;

namespace CeeFind
{
    /// <summary>
    /// Usage text.
    ///
    /// Worth its own file because it is the first thing a new user sees and the only
    /// documentation most will read. Previously there was none: --help and -? were treated
    /// as search filters and silently found nothing.
    /// </summary>
    internal static class Help
    {
        internal static void Show()
        {
            Console.WriteLine(@"CeeFind - find files, and things inside them, by looking in likely places first.

USAGE
  f <file filter> [more filters...] [not <filter>...] [-- <text to find>...] [flags]
  c <file filter>          go to the directory containing the first match
  cx <file filter>         run the first match

FINDING FILES
  f *.java                 files ending .java
  f .*Aggregation.cs       regular expression against the whole name
  f *.java *.cs --         either type
  f *.java not *test* --   excluding a pattern

FINDING TEXT INSIDE FILES
  f *.java override        .java files containing 'override'
  f *.java override sql    containing both
  f * \r\n -n -files       files with CRLF line endings

  File filters are regular expressions, except that a bare '*' is read as '.*' so
  that '*.cpp' works as expected. Use -r to disable that assistance. Text filters
  are always regular expressions.

FLAGS
  -v  -verbose             progress, estimates and a summary
  -h  -history             previous searches from this directory
  -f  -first               stop at the first result
  -d  -dir  -dirs          print directories only
      -file -files         print file names only
  -b  -binary              include binary and files over 1MB
  -s  -sensitive           case sensitive
  -r  -regex               no regular expression assistance
  -n  -ignorenewlines      match across line endings
  -u  -up                  search parent directories if nothing is found
  -j  -json                write the index out as state.json
      -silent              suppress non-result output

EXIT CODES
  0  something was found
  1  nothing matched
  2  the command could not be understood

SHELL INTEGRATION
  c changes the directory of the shell you are in, which only the shell can do,
  so it has to be a function rather than a program. Run 'ceefind init' to see how
  to set that up for your shell.

CeeFind remembers where it found things and looks there first next time. The index
lives in %LOCALAPPDATA%\CeeFind and can be deleted at any time.");
        }
    }
}
