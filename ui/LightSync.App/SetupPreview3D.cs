using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace LightSync.App;

/// <summary>What one device (and each of its zones) shows right now, from the service's "preview".</summary>
public sealed record PreviewZone(string Id, List<string> Colors);

public sealed record PreviewDevice(string Id, List<string> Colors, List<PreviewZone> Zones);

/// <summary>
/// A 3D model of the user's setup: the case (with the motherboard, memory, fans and strips inside) and the
/// desk with the keyboard, mouse, speakers and other peripherals, every LED in its live colour. It is a
/// local three.js page (Preview3D\) in a WebView2 that may load nothing but that folder.
/// Clicking a part selects that device, zone or fan (Shift+click adds it). In "Arrange" mode parts, the
/// case and the desk can be moved and turned in 3D, lined up and snapped; right-clicking a part sets
/// what it is. Positions are kept in <see cref="AppSettings.Preview3D"/> by stable id.
/// </summary>
internal sealed class SetupPreview3D
{
    /// <summary>The kinds of part the user can choose from, in menu order.</summary>
    public static readonly string[] Types = ["fan", "strip", "ram", "board", "keyboard", "mouse", "speaker", "other"];

    public static string TypeLabel(string type) => type switch
    {
        "fan" => "Fan",
        "strip" => "LED strip",
        "ram" => "Memory (RAM)",
        "board" => "Motherboard",
        "keyboard" => "Keyboard",
        "mouse" => "Mouse",
        "speaker" => "Speaker",
        _ => "Other",
    };

    private const string HostName = "lightsync.preview";
    private const string StartPage = "https://" + HostName + "/index.html";
    /// <summary>Ids of the two movable groups (not device ids).</summary>
    private const string CaseId = "@case", DeskId = "@desk";
    private const double MaxPosition = 20000;

    private static readonly string[] PeripheralWords =
        ["headset", "headphone", "mousepad", "mouse pad", "mousemat", "mouse mat", "keypad", "dock", "stand", "monitor", "lightbar", "chroma",
         "leviathan", "soundbar", "speaker"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Grid _host;
    private readonly AppSettings _settings;
    private readonly Action<string, bool> _select;
    /// <summary>Hide a device (by device id), from the right-click menu.</summary>
    private readonly Action<string> _hide;
    /// <summary>Hide one zone (by zone id): a header with nothing on it, from the right-click menu.</summary>
    private readonly Action<string> _hideZone;
    private WebView2? _web;
    /// <summary>LightSync is exiting: nothing is sent to the page any more.</summary>
    private bool _closed;
    private bool _starting;
    private bool _failed;
    /// <summary>The page has loaded and said "ready"; until then nothing is sent.</summary>
    private bool _ready;
    private List<Part> _parts = [];
    private HashSet<string> _ids = [];
    private List<DeviceInfo> _devices = [];
    private HashSet<string> _highlight = ["all"];
    private bool _editMode;
    private string? _lastColors;
    private bool _sentInit;

    public SetupPreview3D(Grid host, AppSettings settings, Action<string, bool> select, Action<string> hide, Action<string> hideZone)
    {
        _host = host;
        _settings = settings;
        _select = select;
        _hide = hide;
        _hideZone = hideZone;
        _host.ActualThemeChanged += (_, _) => Post(new { type = "theme", dark = IsDark });
    }

    private bool _active = true;

    /// <summary>
    /// Let the 3D page sleep while it can't be seen (another view, the window hidden in the tray or
    /// minimized): the browser gives back most of its memory and does no work. It wakes when shown.
    /// </summary>
    public async void SetActive(bool active)
    {
        if (_closed || active == _active || _web?.CoreWebView2 is not { } core)
        {
            return;
        }
        _active = active;
        try
        {
            if (active)
            {
                if (core.IsSuspended)
                {
                    core.Resume();
                }
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                _lastColors = null; // repaint with the current colors
                // The window may have been resized while the page slept: make the browser take its
                // current size again (otherwise it can keep the old, smaller one).
                if (_web is { } web)
                {
                    web.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            web.Width = Math.Max(1, web.ActualWidth - 1);
                            web.UpdateLayout();
                            web.Width = double.NaN;
                        }
                        catch (Exception failure) when (Errors.Expected(failure))
                        {
                            // Closing.
                        }
                    });
                }
            }
            else
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                // Only possible while the control is hidden; if not, the low memory target still applies.
                await core.TrySuspendAsync();
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Not supported by this WebView2 runtime, or it is reloading: nothing to do.
        }
    }

    /// <summary>Stop sending to the page (LightSync is exiting).</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
    }

    private sealed class Part
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        /// <summary>The device or zone whose colors the LEDs show, and the range of them this part covers.</summary>
        public required string Source { get; init; }
        /// <summary>The device the part belongs to (what "Hide" hides).</summary>
        public required string DeviceId { get; init; }
        /// <summary>The zone the part shows (what "Hide this zone" hides), if it is one zone or part of one.</summary>
        public string? ZoneId { get; init; }
        /// <summary>What a click selects: the part itself, or its zone for a piece of a zone that isn't split into fans.</summary>
        public string? Target { get; init; }
        /// <summary>Where it goes / what it is in the case: logo, rear, bottom, column, top or cpu (null = by type).</summary>
        public string? Role { get; init; }
        /// <summary>A specific look for a known device: "soundbar" (null = the type's generic model).</summary>
        public string? Model { get; init; }
        public double Start { get; init; }
        public double Span { get; init; } = 1;
        public int Leds { get; init; }
        public required string DefaultType { get; init; }
        /// <summary>Found outside the case (peripheral), so an "other" part sits on the desk.</summary>
        public bool Peripheral { get; init; }
        /// <summary>A fan on a fan hub or cooler (not a motherboard header): placed in the side fan column first.</summary>
        public bool Hub { get; init; }
        public string Type { get; set; } = "other";
        public bool OnDesk { get; set; }
    }

    /// <summary>Nothing to show (no devices), or the page can't show colours yet.</summary>
    public bool IsEmpty => _parts.Count == 0 || !_ready || _closed || _web is null;

    private bool IsDark => _host.ActualTheme == ElementTheme.Dark;

    /// <summary>Arrange mode: parts can be moved, turned and lined up, and their type set with a right-click.</summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            Post(new { type = "arrange", on = value });
        }
    }

    // ---------- WebView2 ----------

    /// <summary>Create the WebView2 and load the page, the first time the preview is shown.</summary>
    public async void EnsureStarted()
    {
        if (_web is not null || _starting || _failed || _closed)
        {
            return;
        }
        _starting = true;
        try
        {
            await StartAsync();
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            CrashLog.Write("starting the 3D preview", e);
            try
            {
                _web = null;
                Fail("The 3D preview couldn't start. Everything else works without it; restarting LightSync tries again.");
            }
            catch (Exception failure) when (Errors.Recoverable(failure))
            {
                // Logged already.
            }
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Preview3D");
        if (!File.Exists(Path.Combine(folder, "index.html")))
        {
            Fail("The 3D preview's files are missing from LightSync's folder. Reinstalling LightSync puts them back.");
            return;
        }
        try
        {
            if (string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                FailNoRuntime();
                return;
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            FailNoRuntime();
            return;
        }

        // Keep the browser's data with LightSync's (the program folder may not be writable), and paint the
        // first frame in the preview's own background instead of white.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSync", "WebView2"));
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", IsDark ? "FF1F2126" : "FFECEEF2");

        var web = new WebView2();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(web, "3D preview of your setup");
        _host.Children.Clear();
        _host.Children.Add(web);
        _web = web;
        try
        {
            await web.EnsureCoreWebView2Async();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            _host.Children.Clear();
            _web = null;
            FailNoRuntime();
            return;
        }

        var core = web.CoreWebView2;
        var s = core.Settings;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        s.AreHostObjectsAllowed = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;
        core.Profile.PreferredColorScheme = IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

        // Only the bundled page: no other site, no new windows, downloads, permissions or requests elsewhere.
        core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, e) =>
        {
            if (!IsLocal(e.Uri))
            {
                e.Cancel = true;
            }
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = !IsLocal(e.Uri);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!IsLocal(e.Request.Uri))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            }
        };
        core.WebMessageReceived += OnMessage;
        var reloads = 0;
        core.ProcessFailed += (_, _) =>
        {
            // The page's process went away (e.g. GPU reset): load it again (a few times) and resend everything.
            _ready = false;
            _sentInit = false;
            if (++reloads > 3)
            {
                return;
            }
            try
            {
                core.Navigate(StartPage);
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // Nothing more to do; the preview stays blank.
            }
        };
        core.Navigate(StartPage);
    }

    private static bool IsLocal(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps &&
        string.Equals(u.Host, HostName, StringComparison.OrdinalIgnoreCase) &&
        u.IsDefaultPort &&
        string.IsNullOrEmpty(u.UserInfo);

    private void FailNoRuntime() => Fail(
        "The 3D preview needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC. " +
        "Install it from Microsoft, then restart LightSync. Everything else works without it.",
        "https://developer.microsoft.com/microsoft-edge/webview2/");

    private void Fail(string text, string? link = null)
    {
        _failed = true;
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        if (link is not null)
        {
            panel.Children.Add(new HyperlinkButton { Content = "Get the WebView2 Runtime", NavigateUri = new Uri(link), HorizontalAlignment = HorizontalAlignment.Center });
        }
        _host.Children.Clear();
        _host.Children.Add(panel);
    }

    private void Post(object message)
    {
        if (!_ready || _closed || _web is null)
        {
            return;
        }
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // The page is reloading (or the window is closing); it asks for everything again when ready.
        }
    }

    // ---------- messages from the page ----------

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!_closed)
            {
                HandleMessage(e);
            }
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            CrashLog.Report("3D preview", ex);
        }
    }

    private void HandleMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsLocal(e.Source))
        {
            return;
        }
        JsonObject? msg;
        try
        {
            var json = e.WebMessageAsJson;
            if (json is null || json.Length > 256 * 1024)
            {
                return;
            }
            msg = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return;
        }
        if (msg is null || Text(msg, "type") is not { } type)
        {
            return;
        }
        switch (type)
        {
            case "ready":
                _ready = true;
                _lastColors = null;
                SendInit(keepCamera: false);
                break;
            case "select":
                if (Text(msg, "id") is { } id && (id == "all" || _ids.Contains(id)))
                {
                    _select(_parts.FirstOrDefault(p => p.Id == id)?.Target ?? id, Flag(msg, "add"));
                }
                break;
            case "context":
                // Arrange mode: type, place and hide. Otherwise only parts (to hide their device).
                if (Text(msg, "id") is { } target && (_ids.Contains(target) || _editMode && target is CaseId or DeskId) &&
                    Number(msg, "x") is { } x && Number(msg, "y") is { } y)
                {
                    ShowMenu(target, new Point(Math.Max(0, x), Math.Max(0, y)));
                }
                break;
            case "transforms":
                if (msg["items"] is JsonObject items && items.Count <= 4096)
                {
                    SaveTransforms(items);
                }
                break;
            case "camera":
                if (Numbers(msg["value"], 6) is { } cam && cam.All(v => Math.Abs(v) <= 4 * MaxPosition))
                {
                    _settings.Preview3DCamera = cam.Select(v => Math.Round(v, 1)).ToArray();
                    _settings.Save();
                }
                break;
            case "caseSize":
                if (Numbers(msg["value"], 3) is { } size && size.All(v => v is >= 150 and <= 1200))
                {
                    _settings.Preview3DCaseSize = size.Select(v => Math.Round(v)).ToArray();
                    _settings.Save();
                }
                break;
        }
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 512 ? s : null;

    private static bool Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static double? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    private static double[]? Numbers(JsonNode? node, int count)
    {
        if (node is not JsonArray a || a.Count != count)
        {
            return null;
        }
        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (a[i] is not JsonValue v || !v.TryGetValue<double>(out var d) || !double.IsFinite(d))
            {
                return null;
            }
            result[i] = d;
        }
        return result;
    }

    /// <summary>Keep where parts were moved to: [x, y, z] in mm and rotation [x, y, z] in degrees; null = back to default.</summary>
    private void SaveTransforms(JsonObject items)
    {
        var changed = false;
        foreach (var (id, value) in items)
        {
            if (!_ids.Contains(id) && id is not (CaseId or DeskId))
            {
                continue;
            }
            if (value is null)
            {
                changed |= _settings.Preview3D?.Remove(id) == true;
                continue;
            }
            if (Numbers(value, 6) is not { } t)
            {
                continue;
            }
            for (var i = 0; i < 3; i++)
            {
                t[i] = Math.Round(Math.Clamp(t[i], -MaxPosition, MaxPosition), 1);
                t[i + 3] = Math.Round(Math.IEEERemainder(t[i + 3], 360), 1);
            }
            _settings.Preview3D ??= [];
            _settings.Preview3D[id] = t;
            changed = true;
        }
        if (changed)
        {
            _settings.Save();
        }
    }

    // ---------- what to show ----------

    private static bool IsPeripheral(DeviceInfo d) =>
        d.Kind is "Keyboard" or "Mouse" or "Speaker" ||
        d.Kind == "Other" && PeripheralWords.Any(w => d.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static bool IsDeskType(string type) => type is "keyboard" or "mouse" or "speaker";

    /// <summary>
    /// The motherboard header whose chain is known on this PC (ARGB_V2_2), in chain order: the case's lit
    /// logo, the rear fan, the two bottom fans, the three side fans and the CPU cooler's LEDs. Weight is
    /// the piece's share of the LEDs when the header isn't split into fans (a fan counts 1).
    /// </summary>
    private static readonly (string Role, string Name, double Weight)[] Chain =
    [
        ("logo", "Logo", 0.5),
        ("rear", "Rear fan", 1), ("bottom", "Bottom fan 1", 1), ("bottom", "Bottom fan 2", 1),
        ("column", "Side fan 1", 1), ("column", "Side fan 2", 1), ("column", "Side fan 3", 1),
        ("cpu", "CPU cooler", 0.5),
    ];

    private static string Compact(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static bool IsChainHeader(ZoneInfo z) => Compact(z.Name).Contains("ARGBV22");

    /// <summary>A known device's own look in the preview (by name; no brand is drawn).</summary>
    private static string? ModelFor(DeviceInfo d)
    {
        var n = Compact(d.Name);
        if (n.Contains("LEVIATHAN") || d.Kind == "Speaker" && n.Contains("SOUNDBAR"))
        {
            return "soundbar";
        }
        return null;
    }

    private static bool OnDeskFor(Part p, string type) => IsDeskType(type) || type == "other" && p.Peripheral;

    public void Build(List<DeviceInfo> devices)
    {
        _devices = devices;
        _parts = CollectParts(devices);
        var types = _settings.PreviewTypes ?? [];
        foreach (var p in _parts)
        {
            p.Type = types.TryGetValue(p.Id, out var t) && Types.Contains(t) ? t : p.DefaultType;
            p.OnDesk = OnDeskFor(p, p.Type);
        }
        _ids = [.. _parts.Select(p => p.Id)];
        _lastColors = null;
        if (_ready)
        {
            SendInit(keepCamera: _sentInit);
        }
    }

    private void SendInit(bool keepCamera)
    {
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var a) && a is Windows.UI.Color c
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : "#4CC2FF";
        var layout = (_settings.Preview3D ?? [])
            .Where(kv => (_ids.Contains(kv.Key) || kv.Key is CaseId or DeskId) && kv.Value is { Length: 6 } && kv.Value.All(double.IsFinite))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Post(new
        {
            type = "init",
            dark = IsDark,
            accent,
            arrange = _editMode,
            keepCamera,
            caseSize = _settings.Preview3DCaseSize is { Length: 3 } cs && cs.All(v => v is >= 150 and <= 1200) ? cs : null,
            camera = _settings.Preview3DCamera is { Length: 6 } cam && cam.All(double.IsFinite) ? cam : null,
            layout,
            highlight = _highlight,
            parts = _parts.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                type = p.Type,
                typeLabel = TypeLabel(p.Type),
                desk = p.OnDesk,
                source = p.Source,
                start = p.Start,
                span = p.Span,
                leds = p.Leds,
                hub = p.Hub,
                target = p.Target ?? p.Id,
                role = p.Role,
                model = p.Model,
            }),
        });
        _sentInit = true;
    }

    /// <summary>Every part to draw: one per device, board zone, header or fan.</summary>
    private static List<Part> CollectParts(List<DeviceInfo> devices)
    {
        var parts = new List<Part>();
        var seen = new HashSet<string>();
        void Add(Part p)
        {
            if (seen.Add(p.Id))
            {
                parts.Add(p);
            }
        }

        void AddHeader(DeviceInfo d, ZoneInfo z)
        {
            var hub = d.Kind != "Motherboard";
            if (!hub && IsChainHeader(z))
            {
                AddChain(d, z);
                return;
            }
            if (z.Fans.Count > 0)
            {
                for (var k = 0; k < z.Fans.Count; k++)
                {
                    Add(new Part
                    {
                        Id = z.Fans[k].Id, Name = $"{d.Name} / {z.Name} / {z.Fans[k].Name}", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id,
                        Start = (double)k / z.Fans.Count, Span = 1.0 / z.Fans.Count, Leds = (int)(z.Leds / (uint)z.Fans.Count), DefaultType = "fan", Hub = hub,
                    });
                }
            }
            else
            {
                Add(new Part { Id = z.Id, Name = $"{d.Name} / {z.Name}", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id, Leds = (int)z.Leds, DefaultType = "fan", Hub = hub });
            }
        }

        // The chained header: one part per piece of the chain, in its place in the case. Split into fans,
        // each fan is one piece (in chain order); otherwise the zone's LEDs are shared out by the pieces'
        // usual sizes, and clicking any piece selects the zone.
        void AddChain(DeviceInfo d, ZoneInfo z)
        {
            if (z.Fans.Count > 0)
            {
                for (var k = 0; k < z.Fans.Count; k++)
                {
                    var link = k < Chain.Length ? Chain[k] : (Role: "column", Name: "Fan", Weight: 1.0);
                    Add(new Part
                    {
                        Id = z.Fans[k].Id, Name = $"{d.Name} / {z.Name} / {z.Fans[k].Name} ({link.Name})", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id,
                        Start = (double)k / z.Fans.Count, Span = 1.0 / z.Fans.Count, Leds = (int)(z.Leds / (uint)z.Fans.Count),
                        DefaultType = link.Role is "logo" or "cpu" ? "other" : "fan", Role = link.Role,
                    });
                }
                return;
            }
            var total = Chain.Sum(c => c.Weight);
            var start = 0.0;
            for (var k = 0; k < Chain.Length; k++)
            {
                var (role, name, weight) = Chain[k];
                Add(new Part
                {
                    Id = $"{z.Id}#{k}", Name = $"{d.Name} / {z.Name} / {name}", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id, Target = z.Id,
                    Start = start / total, Span = weight / total, Leds = Math.Max(1, (int)Math.Round(z.Leds * weight / total)),
                    DefaultType = role is "logo" or "cpu" ? "other" : "fan", Role = role,
                });
                start += weight;
            }
        }

        foreach (var d in devices)
        {
            if (IsPeripheral(d))
            {
                var type = d.Kind switch
                {
                    "Keyboard" => "keyboard",
                    "Mouse" => "mouse",
                    "Speaker" => "speaker",
                    _ => ModelFor(d) == "soundbar" ? "speaker" : "other",
                };
                Add(new Part { Id = d.Id, Name = d.Name, Source = d.Id, DeviceId = d.Id, Leds = d.LedCount, DefaultType = type, Peripheral = true, Model = ModelFor(d) });
                continue;
            }
            switch (d.Kind)
            {
                case "Motherboard":
                    Add(new Part { Id = d.Id, Name = d.Name, Source = d.Id, DeviceId = d.Id, Leds = d.LedCount, DefaultType = "board" });
                    foreach (var z in d.Zones.Where(z => z.Leds > 0 && !z.Resizable))
                    {
                        Add(new Part { Id = z.Id, Name = $"{d.Name} / {z.Name}", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id, Leds = (int)z.Leds, DefaultType = "strip" });
                    }
                    foreach (var z in d.Zones.Where(z => z.Resizable))
                    {
                        AddHeader(d, z);
                    }
                    break;
                case "Ram":
                    Add(new Part { Id = d.Id, Name = d.Name, Source = d.Id, DeviceId = d.Id, Leds = d.LedCount, DefaultType = "ram" });
                    break;
                default:
                    var headers = d.Zones.Where(z => z.Resizable || z.Fans.Count > 0).ToList();
                    if (d.Kind == "Fan" || headers.Count > 0)
                    {
                        var zones = headers.Count > 0 ? headers : d.Zones.Where(z => z.Leds > 0).ToList();
                        if (zones.Count == 0)
                        {
                            Add(new Part { Id = d.Id, Name = d.Name, Source = d.Id, DeviceId = d.Id, Leds = d.LedCount, DefaultType = "fan", Hub = true });
                        }
                        foreach (var z in zones)
                        {
                            AddHeader(d, z);
                        }
                        foreach (var z in d.Zones.Where(z => z.Leds > 0 && !z.Resizable && z.Fans.Count == 0 && headers.Count > 0))
                        {
                            Add(new Part { Id = z.Id, Name = $"{d.Name} / {z.Name}", Source = z.Id, DeviceId = d.Id, ZoneId = z.Id, Leds = (int)z.Leds, DefaultType = "strip" });
                        }
                    }
                    else
                    {
                        Add(new Part { Id = d.Id, Name = d.Name, Source = d.Id, DeviceId = d.Id, Leds = d.LedCount, DefaultType = d.Kind == "LedStrip" ? "strip" : "other" });
                    }
                    break;
            }
        }
        return parts;
    }

    // ---------- type menu ----------

    private void ShowMenu(string id, Point at)
    {
        if (_web is null)
        {
            return;
        }
        var menu = new MenuFlyout();
        var part = _parts.FirstOrDefault(p => p.Id == id);
        menu.Items.Add(new MenuFlyoutItem { Text = part?.Name ?? (id == CaseId ? "Case" : "Desk"), IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (part is not null)
        {
            // Hiding takes the whole device out of the preview and the list; LightSync stops controlling it.
            var deviceName = _devices.FirstOrDefault(d => d.Id == part.DeviceId)?.Name ?? part.Name;
            var hide = new MenuFlyoutItem { Text = $"Hide {deviceName}", Icon = new FontIcon { Glyph = "\uED1A" } };
            ToolTipService.SetToolTip(hide, "It leaves the preview and the list, and LightSync stops controlling it. Settings > Devices shows it again.");
            hide.Click += (_, _) => _hide(part.DeviceId);
            // A zone (a header with nothing on it, say) can be hidden on its own; the device stays.
            if (part.ZoneId is { } zoneId && _devices.FirstOrDefault(d => d.Id == part.DeviceId)?.Zones.FirstOrDefault(z => z.Id == zoneId) is { } zone)
            {
                var hideZone = new MenuFlyoutItem { Text = $"Hide {zone.Name}", Icon = new FontIcon { Glyph = "" } };
                ToolTipService.SetToolTip(hideZone, "Only this zone leaves the preview and the list. Settings > Devices shows it again.");
                hideZone.Click += (_, _) => _hideZone(zoneId);
                menu.Items.Add(hideZone);
            }
            menu.Items.Add(hide);
            if (!_editMode)
            {
                menu.ShowAt(_web, new FlyoutShowOptions { Position = at });
                return;
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (part is not null)
        {
            foreach (var type in Types)
            {
                var item = new RadioMenuFlyoutItem { Text = TypeLabel(type), GroupName = "type", IsChecked = type == part.Type };
                item.Click += (_, _) => SetType(part, type);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var auto = new MenuFlyoutItem { Text = $"Detected type ({TypeLabel(part.DefaultType)})", IsEnabled = part.Type != part.DefaultType };
            auto.Click += (_, _) => SetType(part, part.DefaultType);
            menu.Items.Add(auto);
        }
        var reset = new MenuFlyoutItem { Text = "Back to its default place", IsEnabled = _settings.Preview3D?.ContainsKey(id) == true };
        reset.Click += (_, _) =>
        {
            _settings.Preview3D?.Remove(id);
            _settings.Save();
            SendInit(keepCamera: true);
        };
        menu.Items.Add(reset);
        menu.ShowAt(_web, new FlyoutShowOptions { Position = at });
    }

    private void SetType(Part p, string type)
    {
        _settings.PreviewTypes ??= [];
        if (type == p.DefaultType)
        {
            _settings.PreviewTypes.Remove(p.Id);
        }
        else
        {
            _settings.PreviewTypes[p.Id] = type;
        }
        // A part that moves between the case and the desk starts at a default place there.
        if (OnDeskFor(p, type) != p.OnDesk)
        {
            _settings.Preview3D?.Remove(p.Id);
        }
        _settings.Save();
        Build(_devices);
    }

    /// <summary>Put every part, the case and the desk back where they start (types, case size and camera stay).</summary>
    public void ResetLayout()
    {
        _settings.Preview3D = null;
        _settings.Save();
        if (_ready)
        {
            SendInit(keepCamera: true);
        }
    }

    // ---------- selection and colours ----------

    /// <summary>Outline the parts for <paramref name="targets"/> (and nothing else).</summary>
    public void Highlight(IEnumerable<string> targets)
    {
        _highlight = [.. targets];
        Post(new { type = "highlight", ids = _highlight });
    }

    /// <summary>Send the latest colours of every device and zone a part shows.</summary>
    /// <remarks>Called from a timer: it never throws (missing fields in the data, a page that is reloading, a closing window).</remarks>
    public void Update(List<PreviewDevice>? data)
    {
        if (!_ready || _closed || _web is null || data is null)
        {
            return;
        }
        try
        {
            var wanted = _parts.Select(p => p.Source).ToHashSet();
            var sources = new Dictionary<string, List<string>>();
            foreach (var d in data)
            {
                if (d is null)
                {
                    continue;
                }
                if (d.Id is not null && d.Colors is not null && wanted.Contains(d.Id))
                {
                    sources[d.Id] = d.Colors;
                }
                foreach (var z in d.Zones ?? [])
                {
                    if (z?.Id is not null && z.Colors is not null && wanted.Contains(z.Id))
                    {
                        sources[z.Id] = z.Colors;
                    }
                }
            }
            var json = JsonSerializer.Serialize(new { type = "colors", sources }, Json);
            if (json == _lastColors)
            {
                return;
            }
            _lastColors = json;
            _web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Reloading; colours come again in 100 ms.
        }
    }
}
