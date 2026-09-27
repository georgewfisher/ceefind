<#
.SYNOPSIS
    Builds or publishes CeeFind.

.DESCRIPTION
    Wraps the two build paths documented in README.md:
      - a plain build, for local development
      - a self-contained, single-file publish, for distribution

.PARAMETER Publish
    Produce a self-contained, single-file executable instead of a plain build.

.PARAMETER Runtime
    Runtime identifier to publish for. Only used with -Publish. Defaults to win-x64.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER OutputDir
    Output directory for -Publish. Defaults to ".\publish".

.EXAMPLE
    .\build.ps1
    Release build for x64.

.EXAMPLE
    .\build.ps1 -Publish
    Self-contained, single-file win-x64 executable in .\publish.

.EXAMPLE
    .\build.ps1 -Publish -Runtime win-arm64
    Self-contained, single-file win-arm64 executable in .\publish.
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "publish"
)

$ErrorActionPreference = "Stop"

# Run from the repo root regardless of the caller's working directory.
Push-Location $PSScriptRoot
try {
    if ($Publish) {
        dotnet publish CeeFind.csproj `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishSingleFile=true `
            -o $OutputDir
    }
    else {
        dotnet build CeeFind.sln -c $Configuration -p:Platform=x64
    }

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
