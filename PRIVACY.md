# Privacy policy

**CeeFind** — last updated 25 September 2026

## The short version

CeeFind sends nothing anywhere. It has no network code, no telemetry, no
analytics and no crash reporting. Everything it records stays on your machine, in
a file you can delete at any time.

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

## Why CeeFind asks for broad file system access

The Store will tell you CeeFind requests access to your file system. It does,
because searching the folders you point it at is the entire purpose of the tool,
and those folders are yours rather than the application's. It reads only what it
is searching, and only while a search is running.

## Contact

Raise an issue at https://github.com/georgewfisher/ceefind.
