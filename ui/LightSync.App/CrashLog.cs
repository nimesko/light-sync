namespace LightSync.App;

/// <summary>
/// Errors LightSync recovered from (and the ones it couldn't) go to %LOCALAPPDATA%\LightSync\crash.log in
/// the user's profile (next to LightSync.exe only if that can't be written). Only exceptions are written:
/// never what was typed or clicked.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Shows a notice about a recovered error; set by the main window. May be called on any thread.</summary>
    public static Action<string, Exception>? Notify { get; set; }

    private static string[] Paths =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSync", "crash.log"),
        Path.Combine(AppContext.BaseDirectory, "crash.log"),
    ];

    /// <summary>Append an error to the log. Never throws.</summary>
    public static void Write(string where, object? error)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {error}\n\n";
        lock (Gate)
        {
            foreach (var path in Paths)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Keep the log small: start over once it passes 1 MB.
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        File.Delete(path);
                    }
                    File.AppendAllText(path, text);
                    return;
                }
                catch (Exception failure) when (Errors.Recoverable(failure))
                {
                    // Try the next place.
                }
            }
        }
    }

    /// <summary>Log an error that was caught, and show a notice about it. Never throws.</summary>
    public static void Report(string where, Exception e)
    {
        Write(where, e);
        try
        {
            Notify?.Invoke(where, e);
        }
        catch (Exception failure) when (Errors.Recoverable(failure))
        {
            // The notice is a courtesy.
        }
    }

    /// <summary>Run an async handler (button click, timer tick) so that nothing it throws can end the app.</summary>
    public static async void Run(string where, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            Report(where, e);
        }
    }
}
