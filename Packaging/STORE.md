# Store submission

What is ready, what only you can do, and where review is likely to push back.

## Before anything else

Three values in `Package.appxmanifest` are marked `REPLACE` and the package will
be rejected until they are filled in. All three come from Partner Center, under
**Product Identity**:

| Manifest | Partner Center |
|---|---|
| `Identity/@Name` | Package/Identity/Name |
| `Identity/@Publisher` | Package/Identity/Publisher, a full DN such as `CN=ABCD1234-…` |
| `Properties/PublisherDisplayName` | the display name shown to customers |

Reserving the name **CeeFind** in Partner Center is what issues them.

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
c *.csproj        changes directory
cx readme.md      opens the file
```

`c` is the one to watch. It works because the executable reads the name it was
invoked under, so if the alias is registered but `c` does nothing, the alias
name and the expected name have diverged.

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
- The submission has not been tested end to end, because that needs the Partner
  Center identity values.
