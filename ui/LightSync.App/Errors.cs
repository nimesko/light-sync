using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LightSync.App;

/// <summary>
/// Which exceptions LightSync handles, so no catch clause swallows everything.
/// <list type="bullet">
/// <item><see cref="Expected"/>: what best-effort code may run into and carries on after (the service
/// connection, files, other processes, Windows and WebView2, the network). Anything else is a bug and
/// reaches a top-level handler, which logs it.</item>
/// <item><see cref="Recoverable"/>: what a top-level handler (app start, an event handler, a window) logs
/// and survives: everything but the failures a process cannot recover from.</item>
/// </list>
/// </summary>
internal static class Errors
{
    public static bool Expected(Exception e) => e is
        // The LightSync service: its answers, and the named pipe to it.
        LightSyncException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException
        or System.Text.Json.JsonException
        // Files, the registry, other processes and their windows.
        or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception or InvalidDataException
        or System.Windows.Automation.ElementNotAvailableException
        // Windows, WinRT, WebView2 and COM.
        or COMException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException
        // The network (OpenRGB update checks and downloads) and checksums.
        or HttpRequestException or System.Security.Cryptography.CryptographicException;

    public static bool Recoverable(Exception e) => e is not
        (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException or SEHException
            or InvalidProgramException or BadImageFormatException);
}
