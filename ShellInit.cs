using System;

namespace CeeFind
{
    /// <summary>
    /// Emits the shell integration for c, f and cx.
    ///
    /// c has to change the directory of the shell that invoked it, and no child process
    /// can do that - it can only change its own. So c cannot be an executable, however it
    /// is installed; it has to be a function inside the shell itself.
    ///
    /// Printing that function rather than shipping script files is what lets CeeFind be
    /// installed by any means. Script files have to sit on PATH to be found, which rules
    /// out packaged installs: an MSIX execution alias can only register a real .exe. A
    /// function the user adds to their own profile works the same way whether CeeFind
    /// arrived from the Store, from a package manager, or from a folder.
    ///
    /// The generated code calls ceefind by name rather than by path, so an upgrade that
    /// moves the executable does not leave a profile pointing at the old one.
    /// </summary>
    internal static class ShellInit
    {
        internal static int Emit(string shell)
        {
            switch ((shell ?? string.Empty).ToLowerInvariant())
            {
                case "powershell":
                case "pwsh":
                    Console.WriteLine(PowerShell);
                    return 0;

                case "cmd":
                    Console.WriteLine(Cmd);
                    return 0;

                case "bash":
                case "zsh":
                    Console.WriteLine(Posix);
                    return 0;

                default:
                    Console.Error.WriteLine($"ceefind: unknown shell '{shell}'");
                    Console.Error.WriteLine("Supported: powershell, cmd, bash, zsh");
                    return 2;
            }
        }

        internal static void ShowInstructions()
        {
            Console.WriteLine(@"CeeFind shell integration

  c changes the directory of the shell you are in. Only the shell itself can do
  that, so c has to be a shell function rather than a program.

  Add the line for your shell to your profile, then open a new terminal.

POWERSHELL
  Add to $PROFILE:

      ceefind init powershell | Out-String | Invoke-Expression

  To open that file:  notepad $PROFILE

CMD
  Save the output somewhere and run it from your AutoRun:

      ceefind init cmd > ""%USERPROFILE%\ceefind.cmd""
      reg add ""HKCU\Software\Microsoft\Command Processor"" /v AutoRun ^
          /t REG_EXPAND_SZ /d ""%USERPROFILE%\ceefind.cmd"" /f

BASH OR ZSH
  Add to ~/.bashrc or ~/.zshrc:

      eval ""$(ceefind init bash)""

  Afterwards:
      f <pattern>     find files
      c <pattern>     go to the directory of the first match
      cx <pattern>    run the first match");
        }

        private const string PowerShell = @"function f { ceefind @args }

function c {
    $target = ceefind -first -dirs @args
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($target)) {
        Write-Host ""Could not find $args"" -ForegroundColor Yellow
        return
    }
    Set-Location -LiteralPath $target
}

function cx {
    $target = ceefind -first @args
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($target)) {
        Write-Host ""Could not find $args"" -ForegroundColor Yellow
        return
    }
    & $target
}";

        private const string Cmd = @"@echo off
rem Doskey macros expand at the interactive prompt, where a for variable takes a
rem single percent sign. This file is read as a batch script first, which consumes
rem one of each pair, so they are written doubled here to arrive singled.
doskey f=ceefind $*
doskey c=for /f ""delims="" %%i in ('ceefind -first -dirs $*') do @cd /d ""%%i""
doskey cx=for /f ""delims="" %%i in ('ceefind -first $*') do @""%%i""";

        private const string Posix = @"f() { ceefind ""$@""; }

c() {
    local target
    target=$(ceefind -first -dirs ""$@"") || { echo ""Could not find $*"" >&2; return 1; }
    [ -n ""$target"" ] || { echo ""Could not find $*"" >&2; return 1; }
    cd ""$target""
}

cx() {
    local target
    target=$(ceefind -first ""$@"") || { echo ""Could not find $*"" >&2; return 1; }
    [ -n ""$target"" ] || { echo ""Could not find $*"" >&2; return 1; }
    ""$target""
}";
    }
}
