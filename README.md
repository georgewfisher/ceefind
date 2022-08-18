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

1. Build .sln Release x64 in VS2022
2. Add release bin directory to path environment variable

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
