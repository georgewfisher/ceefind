using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CeeFind.Utils
{
    /// <summary>
    /// Drives the progress Windows 11 draws for a console: a line on the taskbar button,
    /// and - under Windows Terminal - a ring in the tab header too.
    ///
    /// Two independent mechanisms are used, because neither alone covers every console
    /// host:
    ///
    ///   - The OSC 9;4 escape sequence (ConEmu's, since adopted by Windows Terminal) is
    ///     what actually reaches the visible window when a process is hosted through
    ///     ConPTY, which is how Windows Terminal runs every console app. GetConsoleWindow()
    ///     in that setup returns a hidden pseudo-console window, not the one pinned to the
    ///     taskbar, so the COM call below silently does nothing there even though it
    ///     succeeds.
    ///   - ITaskbarList3 is what reaches the taskbar for the classic conhost.exe window -
    ///     a plain cmd.exe or PowerShell console, or Windows Terminal set to use the legacy
    ///     console - which does not understand the escape sequence at all.
    ///
    /// Both are best-effort. An unsupported host either ignores the escape sequence or
    /// fails the COM activation, and either is swallowed rather than surfaced: this is a
    /// visual nicety and must never be able to affect a search. Neither is ever emitted
    /// when output is redirected - the escape bytes would land in whatever is reading
    /// stdout instead of on a screen, and CeeFind's own shell integration (`--cd`) is a
    /// concrete example of something that would break.
    /// </summary>
    internal static class TaskbarProgress
    {
        // Not a literal completion percentage - there is no total to measure against until
        // the walk is done. A growing bar reads better than an all-or-nothing dot, so a
        // modest match count already fills it: the point is "found 1" and "found plenty"
        // looking visibly different, not an accurate count.
        private const int MatchesForFullBar = 20;

        private static bool isActive;

        private static bool CanWrite => OperatingSystem.IsWindows() && !Console.IsOutputRedirected;

        internal static void MarkBusy()
        {
            if (!CanWrite)
            {
                return;
            }

            WriteOsc(3, 0);
            if (OperatingSystem.IsWindows())
            {
                Com.SetIndeterminate();
            }

            isActive = true;
        }

        internal static void ReportFound(int matchCount)
        {
            if (!CanWrite)
            {
                return;
            }

            int percent = Math.Min(100, matchCount * 100 / MatchesForFullBar);
            WriteOsc(1, percent);
            if (OperatingSystem.IsWindows())
            {
                Com.SetValue(percent);
            }

            isActive = true;
        }

        internal static void Clear()
        {
            if (!isActive)
            {
                return;
            }

            WriteOsc(0, 0);
            if (OperatingSystem.IsWindows())
            {
                Com.Clear();
            }

            isActive = false;
        }

        private static void WriteOsc(int state, int progress)
        {
            if (!CanWrite)
            {
                return;
            }

            try
            {
                Console.Write($"\x1b]9;4;{state};{progress}\x07");
            }
            catch (Exception)
            {
                // Best effort - an unsupported or half-closed console must never break a
                // search over a cosmetic write.
            }
        }

        /// <summary>
        /// The classic conhost.exe path, via the same Win32 taskbar API Explorer itself
        /// uses. Kept separate from the OSC path above so a failure here (or there) never
        /// affects the other.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static class Com
        {
            private static ITaskbarList3 taskbar;
            private static IntPtr consoleWindow;
            private static bool initialized;
            private static bool available;

            internal static void SetIndeterminate()
            {
                if (!EnsureInitialized())
                {
                    return;
                }

                Try(() => taskbar.SetProgressState(consoleWindow, TbpFlag.Indeterminate));
            }

            internal static void SetValue(int percent)
            {
                if (!EnsureInitialized())
                {
                    return;
                }

                Try(() =>
                {
                    taskbar.SetProgressState(consoleWindow, TbpFlag.Normal);
                    taskbar.SetProgressValue(consoleWindow, (ulong)percent, 100);
                });
            }

            internal static void Clear()
            {
                if (!available)
                {
                    return;
                }

                Try(() => taskbar.SetProgressState(consoleWindow, TbpFlag.NoProgress));
            }

            private static bool EnsureInitialized()
            {
                if (initialized)
                {
                    return available;
                }

                initialized = true;

                if (!OperatingSystem.IsWindows())
                {
                    return false;
                }

                Try(() =>
                {
                    IntPtr hwnd = GetConsoleWindow();
                    if (hwnd == IntPtr.Zero)
                    {
                        return;
                    }

                    ITaskbarList3 instance = (ITaskbarList3)new TaskbarInstance();
                    instance.HrInit();

                    consoleWindow = hwnd;
                    taskbar = instance;
                    available = true;
                });

                return available;
            }

            private static void Try(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    available = false;
                }
            }

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetConsoleWindow();

            [ComImport]
            [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
            private class TaskbarInstance
            {
            }

            /// <summary>
            /// Only the vtable slots up to and including SetProgressValue/SetProgressState
            /// are declared. COM interop binds by position, so everything before the
            /// methods actually called must be present in order - but nothing after them
            /// needs to be, since it is never invoked.
            /// </summary>
            [ComImport]
            [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface ITaskbarList3
            {
                // ITaskbarList
                void HrInit();
                void AddTab(IntPtr hwnd);
                void DeleteTab(IntPtr hwnd);
                void ActivateTab(IntPtr hwnd);
                void SetActiveAlt(IntPtr hwnd);

                // ITaskbarList2
                void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

                // ITaskbarList3
                void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
                void SetProgressState(IntPtr hwnd, TbpFlag tbpFlags);
            }

            private enum TbpFlag
            {
                NoProgress = 0,
                Normal = 0x2,
                Indeterminate = 0x1,
            }
        }
    }
}
