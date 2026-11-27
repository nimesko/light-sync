using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Windows.Media.Control;

namespace LightSync.App;

/// <summary>An app making sound on the Windows output right now.</summary>
public sealed record SoundApp(string Name, string Process, double Peak);

/// <summary>
/// What is playing on the Windows sound output: the apps making sound now (loudest first), the media
/// Windows knows about (title, artist), and, when a browser plays it, the tab and the site it is on.
/// </summary>
public sealed record NowPlayingInfo(IReadOnlyList<SoundApp> Apps, string? Title, string? Artist, string? MediaApp, string? Site, string? Domain)
{
    public static readonly NowPlayingInfo Nothing = new([], null, null, null, null, null);

    /// <summary>One line for the analyzer: "Playing "Veridis Quo" by Daft Punk on YouTube Music (music.youtube.com) in Google Chrome. Also: Steam."</summary>
    public string Describe()
    {
        if (Apps.Count == 0)
        {
            return Title is null ? "Nothing is making sound right now." : $"Nothing is making sound right now (\"{Title}\" is paused).";
        }
        var main = Apps[0];
        var what = Title is { Length: > 0 } t ? $"\"{t}\"" + (Artist is { Length: > 0 } a ? $" by {a}" : "") : null;
        var where = Site is not null ? $" on {Site}" + (Domain is not null ? $" ({Domain})" : "") : "";
        var line = what is not null ? $"Playing {what}{where} in {main.Name}." : $"Sound from {main.Name}{where}.";
        var others = Apps.Skip(1).Select(x => x.Name).Distinct().ToList();
        return others.Count == 0 ? line : $"{line} Also making sound: {string.Join(", ", others)}.";
    }
}

/// <summary>
/// Reads what is playing, only while a sound view is on screen and only on this PC: the audio sessions of
/// the default output (which apps, how loud), the media session (System Media Transport Controls) and, for
/// browsers, the name of the tab marked as playing audio (UI Automation). Nothing is stored or sent.
/// </summary>
public static class NowPlaying
{
    private static readonly string[] Browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];

    /// <summary>Sites recognised in a tab's name, most specific first.</summary>
    private static readonly (string Name, string Domain)[] Sites =
    [
        ("YouTube Music", "music.youtube.com"), ("YouTube", "youtube.com"), ("Spotify", "open.spotify.com"),
        ("SoundCloud", "soundcloud.com"), ("Deezer", "deezer.com"), ("TIDAL", "tidal.com"), ("Apple Music", "music.apple.com"),
        ("Amazon Music", "music.amazon.com"), ("Bandcamp", "bandcamp.com"), ("Mixcloud", "mixcloud.com"), ("Twitch", "twitch.tv"),
        ("Netflix", "netflix.com"), ("Prime Video", "primevideo.com"), ("Disney+", "disneyplus.com"), ("Vimeo", "vimeo.com"),
        ("Kick", "kick.com"),
    ];

    /// <summary>Gather it (COM and UI Automation calls: run it off the UI thread).</summary>
    public static async Task<NowPlayingInfo> GetAsync()
    {
        var apps = SoundingApps();
        string? title = null, artist = null, mediaApp = null;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            // The session that plays, else the one Windows shows as current.
            var sessions = manager.GetSessions();
            var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? manager.GetCurrentSession();
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                title = Clean(props?.Title);
                artist = Clean(props?.Artist);
                mediaApp = session.SourceAppUserModelId;
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // No media sessions (or the service is unavailable): the apps are still shown.
        }
        string? site = null, domain = null;
        if (apps.FirstOrDefault(a => Browsers.Contains(a.Process, StringComparer.OrdinalIgnoreCase)) is { } browser)
        {
            (site, domain) = PlayingSite(browser.Process, title);
        }
        return new NowPlayingInfo(apps, title, artist, mediaApp, site, domain);
    }

    /// <summary>Whether a program ("chrome.exe") is a web browser LightSync can read the current page of.</summary>
    public static bool IsBrowser(string exe) =>
        Browsers.Contains(Path.GetFileNameWithoutExtension(exe.Trim()), StringComparer.OrdinalIgnoreCase);

    /// <summary>Each browser window's address bar, found once (searching a window is slow) and read again on every check.</summary>
    private static readonly Dictionary<int, AutomationElement> AddressBars = [];

    /// <summary>
    /// The sites (hosts) of the pages a browser shows now: each window's current tab, from its address bar
    /// (UI Automation). Used only for "app is running" triggers that watch a site; nothing is kept but
    /// where each window's address bar is.
    /// </summary>
    /// <remarks>
    /// Runs every couple of seconds, so it stays cheap: the top-level windows are listed once with their
    /// process and title in one call (a browser has dozens of processes, most without a window), only
    /// titled windows of the browser are looked at, and each window's address bar is searched for once.
    /// </remarks>
    public static HashSet<string> CurrentHosts(string exe)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe.Trim())))
        {
            using (p)
            {
                ids.Add(p.Id);
            }
        }
        if (ids.Count == 0)
        {
            return hosts;
        }
        lock (AddressBars)
        {
            var seen = new HashSet<int>();
            try
            {
                var request = new CacheRequest();
                request.Add(AutomationElement.ProcessIdProperty);
                request.Add(AutomationElement.NameProperty);
                request.Add(AutomationElement.NativeWindowHandleProperty);
                AutomationElementCollection windows;
                using (request.Activate())
                {
                    windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                }
                foreach (AutomationElement window in windows)
                {
                    if (!ids.Contains(window.Cached.ProcessId) || string.IsNullOrEmpty(window.Cached.Name))
                    {
                        continue;
                    }
                    var hwnd = window.Cached.NativeWindowHandle;
                    seen.Add(hwnd);
                    if (AddressHost(hwnd, window) is { Length: > 0 } host)
                    {
                        hosts.Add(host);
                    }
                }
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // The browser is busy: the next check reads it again.
            }
            // Windows that closed.
            foreach (var gone in AddressBars.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                AddressBars.Remove(gone);
            }
        }
        return hosts;
    }

    /// <summary>The host of the page a browser window shows ("" if none), from its address bar.</summary>
    private static string AddressHost(int hwnd, AutomationElement window)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!AddressBars.TryGetValue(hwnd, out var edit))
                {
                    // The address bar is the first text box in the window (the toolbar comes before the page).
                    edit = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (edit is null)
                    {
                        return "";
                    }
                    AddressBars[hwnd] = edit;
                }
                return edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? SiteHost(((ValuePattern)pattern).Current.Value) : "";
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // The address bar went away (the window changed): find it again once.
                AddressBars.Remove(hwnd);
            }
        }
        return "";
    }

    /// <summary>
    /// A site's host from an address or what the user typed: lowercase, without the scheme, "www.", port,
    /// path or query ("https://www.YouTube.com/watch?v=1" -> "youtube.com"); "" if it isn't one. The same rules
    /// as site_host in the service (triggers.rs).
    /// </summary>
    public static string SiteHost(string? text)
    {
        var s = (text ?? "").Trim().ToLowerInvariant();
        if (s.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0)
        {
            s = s[(scheme + 3)..];
        }
        var host = s.Split('/', '?', '#')[0];
        host = host[(host.LastIndexOf('@') + 1)..].Split(':')[0];
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        host = host.TrimEnd('.');
        var ok = host.Length is >= 3 and <= 253 && host.Contains('.')
            && host.Split('.').All(l => l.Length > 0 && !l.StartsWith('-') && !l.EndsWith('-'))
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
        return ok ? host : "";
    }

    /// <summary>Whether <paramref name="host"/> is on <paramref name="site"/> (the site itself or one of its subdomains).</summary>
    public static bool OnSite(string host, string site) =>
        host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 120)];

    /// <summary>The apps with sound on the default output now, loudest first (LightSync itself left out).</summary>
    private static List<SoundApp> SoundingApps()
    {
        var found = new Dictionary<string, SoundApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0)
            {
                return [];
            }
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0 || o is not IAudioSessionManager2 manager)
            {
                return [];
            }
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var s) != 0)
                {
                    continue;
                }
                s.GetProcessId(out var pid);
                ((IAudioMeterInformation)s).GetPeakValue(out var peak);
                if (pid == 0 || peak < 0.002)
                {
                    continue; // system sounds, or silent
                }
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    if (p.ProcessName is "LightSync" or "lightsyncd")
                    {
                        continue;
                    }
                    var name = p.ProcessName;
                    try
                    {
                        name = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name;
                    }
                    catch (Exception failure) when (Errors.Expected(failure))
                    {
                        // Another user's or an elevated process: keep its process name.
                    }
                    if (!found.TryGetValue(p.ProcessName, out var had) || had.Peak < peak)
                    {
                        found[p.ProcessName] = new SoundApp(name, p.ProcessName, peak);
                    }
                }
                catch (Exception failure) when (Errors.Expected(failure))
                {
                    // The process just ended.
                }
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return [];
        }
        return found.Values.OrderByDescending(a => a.Peak).ToList();
    }

    /// <summary>
    /// The site a browser plays from: the tab whose name has the media's title (else the one marked as
    /// playing audio), matched against known sites. Null when it can't be told.
    /// </summary>
    private static (string?, string?) PlayingSite(string process, string? title)
    {
        try
        {
            var names = new List<string>();
            foreach (var p in Process.GetProcessesByName(process))
            {
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }
                    var root = AutomationElement.FromHandle(p.MainWindowHandle);
                    var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                    foreach (AutomationElement tab in tabs)
                    {
                        names.Add(tab.Current.Name ?? "");
                    }
                }
            }
            // Tabs that play show it in their name ("Audio playing", "Reprodução de áudio"...).
            var tabName = (title is not null ? names.FirstOrDefault(n => n.Contains(title, StringComparison.OrdinalIgnoreCase)) : null)
                ?? names.FirstOrDefault(n => n.Contains("audio", StringComparison.OrdinalIgnoreCase) || n.Contains("áudio", StringComparison.OrdinalIgnoreCase));
            if (tabName is null)
            {
                return (null, null);
            }
            foreach (var (name, domain) in Sites)
            {
                if (tabName.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    return (name, domain);
                }
            }
            return ("a web page", null);
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return (null, null);
        }
    }

    // ---------- Core Audio (the audio sessions of an output device) ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr context);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr context);
        int GetGroupingParam(out Guid group);
        int SetGroupingParam(IntPtr group, IntPtr context);
        int RegisterAudioSessionNotification(IntPtr events);
        int UnregisterAudioSessionNotification(IntPtr events);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }
}
