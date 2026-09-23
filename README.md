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

3. Set up the shell integration (see below).

## Shell integration

`c` changes the directory of the shell you are in. No program can do that — a
process can only change its own directory — so `c` has to be a function inside
the shell itself rather than an executable.

Run `ceefind init` for the instructions, or add the line for your shell directly:

**PowerShell** — add to `$PROFILE`:

```powershell
ceefind init powershell | Out-String | Invoke-Expression
```

**Bash or Zsh** — add to `~/.bashrc` or `~/.zshrc`:

```bash
eval "$(ceefind init bash)"
```

**Cmd** — save the macros and load them from AutoRun:

```bat
ceefind init cmd > "%USERPROFILE%\ceefind.cmd"
reg add "HKCU\Software\Microsoft\Command Processor" /v AutoRun ^
    /t REG_EXPAND_SZ /d "%USERPROFILE%\ceefind.cmd" /f
```

This works however CeeFind was installed, because the generated function calls
`ceefind` by name. The `c.cmd`, `c.ps1`, `f.cmd` and `cx.cmd` files in the build
output do the same job for a plain folder install, but cannot be used by a
packaged install, which can only register executables.

## Stored data

CeeFind keeps its index and search history in a SQLite database in a per-user directory:

* Windows: `%LOCALAPPDATA%\CeeFind\index.db`
* Other platforms: `$XDG_DATA_HOME`/`~/.local/share` equivalent resolved by .NET

Nothing is written next to the executable, so CeeFind works when installed to a
read-only location such as `Program Files`.

The index loads progressively: directory knowledge is read by name as the search
meets it, and only what a search actually changed is written back, in a single
transaction. Startup cost is therefore flat as the index grows, and concurrent
`f` invocations in different terminals no longer overwrite each other.

To reset the index, delete the database.

### What CeeFind remembers, and what it forgets

Index space is spent in proportion to how hard something is to rediscover:

1. **Directory shape** — which directories, in which recurring layouts, tend to hold
   what you want. This is the primary asset, and the last thing discarded.
2. **Content evidence** — what was found *inside* files. The most expensive knowledge
   to rebuild, because the alternative is re-reading every candidate file.
3. **Specific filenames** — worth remembering, but cheap to find again by walking.
4. **Traversal residue** — directory names that never produced a result. Rebuilt for
   free by the next walk, so evicted first.

Files matched purely by suffix (`*.cs`) are deliberately **not** indexed. One walk
finds them with no file reads, so storing them would consume the budget that the
first two tiers need.

The index is capped at 256MB. Within every tier the measure is how often an entry has
actually been useful — never how old it is. A location you found something in two
years ago is precisely what you are least likely to remember unaided, so age ranks
results but never decides what to discard. Entries are also retired when provably
invalid, noticed for free during a search that was already resolving them.

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
|-help<br />--help<br />-?|Show usage|

### Exit codes

|Code|Meaning|
|-|-|
|0|Something was found|
|1|Nothing matched|
|2|The command could not be understood|

These follow the convention used by `grep` and `find`, so CeeFind can be used in a
script:

```
f *.config connectionString && echo "found one"
```
