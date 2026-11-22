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
        catch (Exception)
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
                    catch (Exception)
                    {
                        // Another user's or an elevated process: keep its process name.
                    }
                    if (!found.TryGetValue(p.ProcessName, out var had) || had.Peak < peak)
                    {
                        found[p.ProcessName] = new SoundApp(name, p.ProcessName, peak);
                    }
                }
                catch (Exception)
                {
                    // The process just ended.
                }
            }
        }
        catch (Exception)
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
        catch (Exception)
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
