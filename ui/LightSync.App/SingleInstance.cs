namespace LightSync.App;

/// <summary>
/// One LightSync per user session: a second start (Start menu, startup entry, double click) shows the
/// window of the one already running and exits.
/// </summary>
public static class SingleInstance
{
    private const string Name = @"Local\LightSync.App";
    private static Mutex? _mutex;

    /// <summary>True for the first instance. Otherwise asks the first one to show its window; the caller should exit.</summary>
    public static bool Claim()
    {
        _mutex = new Mutex(initiallyOwned: true, Name + ".Instance", out var created);
        if (created)
        {
            return true;
        }
        try
        {
            using var show = EventWaitHandle.OpenExisting(Name + ".Show");
            show.Set();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // The first instance is still starting up; it shows its window anyway.
        }
        return false;
    }

    /// <summary>Run <paramref name="show"/> (on a background thread) whenever another start asks for the window.</summary>
    public static void OnShowRequested(Action show)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show");
        new Thread(() =>
        {
            while (signal.WaitOne())
            {
                show();
            }
        })
        { IsBackground = true, Name = "LightSync single instance" }.Start();
    }
}
