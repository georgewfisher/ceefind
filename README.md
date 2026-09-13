# CeeFind - smart, all in one find tool with simple index

Principles:
1. Look in familiar places. *If you lost something before, it's likely to be where you found it last time*
2. Look in similar places. *If you lost something in another house, it's likely to be in a similar place in that house*
3. If a place looks similar, maybe it is. *If you lost something in a building that looks like a house, act as if it were a house*
4. Don't remember everything.

Usable:
* Simple shortcuts `f` and `c`:
	* f is search
	* c is GO TO
* Actually useful verbose mode so you know what's going on:
	* Time estimates
	* Progress information / summary statistics
* Works in Cmdshell and Powershell

Smart:
* Searches nearby previous results before searching everywhere
* Matches similar directory patterns across multiple root paths
* Minimally indexes in a single file for quick scans

## Installation

1. Build Release using the .NET SDK (https://dotnet.microsoft.com/en-us/download):

	`dotnet build CeeFind.sln -c Release -p:Platform=x64`

	Or produce a standalone executable with no .NET runtime dependency:

	`dotnet publish CeeFind.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish`

	Replace `win-x64` with `win-arm64` for Arm64 devices.

2. Add the output directory to your `PATH` environment variable.

## Stored data

CeeFind keeps its index and search history in a per-user directory:

* Windows: `%LOCALAPPDATA%\CeeFind\`
* Other platforms: `$XDG_DATA_HOME`/`~/.local/share` equivalent resolved by .NET

Nothing is written next to the executable, so CeeFind works when installed to a
read-only location such as `Program Files`. An index created by an older build that
stored state alongside the executable is migrated automatically on first run.

To reset the index, delete that directory.

## Usage

### Examples

#### Find files:

**f [file filter...]**

`f *.java`

`f .*Aggregation.cs`

`f [a-z]+[0-9].cs`

*Note:* the file filter is a regular expression, but `*.` when not in the form to `.*.` is converted to `.*` to allow for filters like `*.cpp`. Then, anchors are added at each end to make the regular expression `^.*\.cpp$`. To disable this assistance use the flag `-r` or `-regex`.

#### Find in files:

**f [file filter] [inside file filter] ...**

`f *.java override`

`f *.java override sql color`

Find filenames with crlf line endings:
`f * \r\n -ignorenewlines -files`

Find in file filter is always a regular expression.

#### Find in multiple types of file filters

**f [file filter]... not [negative file filter]... -- [inside file filter]...**

`f *.java *.cs --`

`f *.java *.cs -- override`

`f *.java *.cs not *test* -- override`

#### Go to file:

**c [args]**

`c *.csproj`

`c *.csproj nuget`

### Verbose Flag

It's highly recommended you use the -verbose or -v flag as it provides detailed output of search progress which is invaluable if you have very large count of files.

This includes:
* File read count
* Progress %, ETA (when statistics are available)
* Summary of requested search
* Top file types skipped (binary and suspected binary)
* Top file extensions scanned

### Flags

|Flag|Description|
|-|-|
|-b<br />-binary|Include binary files, include large files (files over 1mb)|
|-v<br />-verbose|Show progress and other diagnostic information|
|-h<br />-history|Show previous searches executed from the current directory|
|-dir<br />-dir</br>-dirs|Show only directories containing results, no file names or file lines|
|-f<br />-first|Output only the first result. The command `c` uses `-first -dir`|
|-file<br />-files|Show only filenames, not directories or file lines|
|-json|Dump the current index out as `state.json`|
|-r<br />-regex|Use pure regular expressions, no conversion|
|-n<br />-ignorenewlines|Read entire files, including newlines|
