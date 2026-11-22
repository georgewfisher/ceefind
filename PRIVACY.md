# Privacy policy

**CeeFind** — last updated 21 November 2026

## The short version

CeeFind sends nothing anywhere. It has no network code, no telemetry, no
analytics and no crash reporting. Everything it records stays on your machine, in
a file you can delete at any time. Nothing you search is ever seen by anyone but
you - not the developer, not Microsoft, not a third party. CeeFind is open
source, so none of this is a claim you have to take on faith: the code that
reads and writes the index is at
https://github.com/georgewfisher/ceefind, and it is exactly what builds the
Store package.

## What CeeFind stores

To find things faster next time, CeeFind keeps an index of what it has learned
while searching. It lives in:

```
%LOCALAPPDATA%\CeeFind\index.db
```

It contains:

- **Directory names and paths** where searches have produced results
- **File names** that matched a search
- **Search terms** you have used, both file name patterns and text searched for
  inside files, and the text that matched them
- **Counts and timestamps** recording how often somewhere has been useful and
  when it last was
- **A record of your recent searches**, which `f --history` shows you

Being explicit about the third item: when you search inside files, the text you
searched for and the text that matched are written to the index, so that the same
search is faster next time. If you search your own source code for a password,
that string will be in the index.

## Why the Store listing says CeeFind "uses personal information"

The Store asks a yes-or-no question: does the product access, collect, or
transmit personal information? That is broader than asking whether it is sent
anywhere - it also covers data a product simply stores, even if only on the
same machine. Because of the index above, the honest answer is yes, and that
is what the listing says.

What that answer does not mean: it does not mean CeeFind phones home, or that a
server somewhere has your search history. It means exactly what this page
describes - a local cache, under your control, deletable at any time - and
nothing more (see below for exactly how isolated "local" is). Answering
truthfully here is what makes the rest of this page worth trusting.

## What CeeFind does not do

- It does not transmit anything. There is no network code in the application.
- It does not copy the contents of your files. It records what matched, not the
  files themselves.
- It does not run in the background. It does nothing except while you are running
  a search.
- It contains no advertising and no third-party analytics.

## Deleting what it has stored

Delete the folder:

```
%LOCALAPPDATA%\CeeFind
```

CeeFind will start again from nothing the next time it runs. Nothing else is left
behind anywhere.

You can also point the index somewhere else, or at a temporary location, with the
`CEEFIND_INDEX` environment variable.

## Is the index isolated from other applications?

Installed from the Store, partly: Windows runs CeeFind with its own package
identity, which redirects the folder above to a package-specific location
(`%LOCALAPPDATA%\Packages\<package id>\LocalCache\Local\CeeFind\index.db`)
rather than the plain shared one, and removes it entirely when the package is
uninstalled. Built from source and run unpackaged, it is the plain shared
folder above, the same as any other application's.

Either way, this is not the same guarantee as encryption. CeeFind runs with
your ordinary Windows account rather than inside a sandbox, so another program
you run under that same account could still open the file if it went looking
for it - the same as it could read any other file of yours. What packaging
buys is a location nothing else collides with or stumbles into, and a clean
uninstall. It does not buy a lock that only CeeFind holds the key to.

## Why CeeFind asks for broad file system access

The Store will tell you CeeFind requests access to your file system. It does,
because searching the folders you point it at is the entire purpose of the tool,
and those folders are yours rather than the application's. It reads only what it
is searching, and only while a search is running.

## Contact

Raise an issue at https://github.com/georgewfisher/ceefind.
