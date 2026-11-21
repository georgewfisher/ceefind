# Store submission

What is ready, what only you can do, and where review is likely to push back.

## Identity

The three values Partner Center issues under **Product Identity** are filled in
in `Package.appxmanifest`:

| Manifest | Partner Center |
|---|---|
| `Identity/@Name` | `36541BlackWhiteL1.Ceefind` |
| `Identity/@Publisher` | `CN=88B73C94-12D4-4F05-BEDE-2F7F384506F4` |
| `Properties/PublisherDisplayName` | `Black & White L1` |

If the app is ever re-created under a new reservation, these come from the
Product Identity page again and need updating to match.

## Building the package

```powershell
# One publish per architecture; both go in the same bundle.
.\build.ps1 -Publish -Runtime win-x64   -OutputDir publish\x64
.\build.ps1 -Publish -Runtime win-arm64 -OutputDir publish\arm64
```

Then package each with `makeappx.exe` from the Windows SDK, pointing at
`Packaging\Package.appxmanifest` with `Assets\` alongside the binaries, and
combine them into a `.msixbundle`.

Store submissions are signed by Microsoft, so no certificate of your own is
needed. Sideloading for testing does need one.

## Testing before submitting

The thing worth proving is the aliases, because they are the reason for
packaging this rather than shipping a zip. After installing the package, in a
**new** terminal:

```
f --help          shows usage
f *.csproj        finds files
cx readme.md      opens the file
```

`c` is not registered as an alias — only `f` and `cx` are, since both do their
whole job as a bare child process. `c` cannot: changing the caller's directory
is impossible for anything launched as a separate process, so there is nothing
useful a bare `c` alias could do beyond printing a path, which looks like it
did nothing. Testing `c` therefore means testing the shell function instead:
run `ceefind init powershell | Out-String | Invoke-Expression` (or the
`cmd`/`bash` equivalent) in that new terminal, then confirm `c *.csproj` moves
you there.

## Where review is likely to push back

**`broadFileSystemAccess` needs justifying.** It is a restricted capability and
submissions using it are reviewed by hand. The justification is straightforward:
CeeFind is a file search tool, the folders it searches belong to the user rather
than to the package, and a packaged application cannot otherwise read them. Say
so plainly in the submission notes and link to `PRIVACY.md`.

**No visible window.** `AppListEntry="none"` hides CeeFind from the Start menu,
which is right for a console tool but occasionally queried. It is a command line
application, reached by typing its name.

**Privacy policy URL is required** because of the capability above. `PRIVACY.md`
is written; it needs a public URL, which the GitHub copy provides.

## Listing

- **Category**: Developer tools
- **Description**: the README opening is a reasonable starting point
- **Screenshots**: at least one 1366x768. A terminal showing `f *.cs override`
  with results, and `-v` output showing directories scanned, demonstrates the
  point of the tool better than a description does.

## Still open

- Screenshots have not been taken.
- No package has been built yet — `publish\` is empty and no `.msix`/`.msixbundle`
  exists.
- The submission has not been tested end to end.
