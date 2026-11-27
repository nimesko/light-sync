using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace LightSync.App;

public sealed partial class MainWindow : Window
{
    private enum Health { Good, Busy, Bad }

    /// <summary>
    /// An effect tile. <paramref name="Audio"/> effects react to sound: they sit under "Sound" and show the analyzer.
    /// <paramref name="Group"/> is its family in lists (calm, motion, colorful, alerts, sound).
    /// </summary>
    private sealed record EffectOption(string Id, string Name, bool UsesColor, bool Audio = false, string Group = "Calm & solid");

    private const string Calm = "Calm & solid", Motion = "Motion", Colorful = "Colorful & nature", Alerts = "Alerts", Sound = "Sound reactive";

    /// <summary>Effect families, in the order lists show them.</summary>
    private static readonly string[] EffectGroups = [Calm, Motion, Colorful, Alerts, Sound];

    private static readonly EffectOption[] Effects =
    [
        new("static", "Static", true, Group: Calm),
        new("gradient", "Gradient", true, Group: Calm),
        new("breathing", "Breathing", true, Group: Calm),
        new("candle", "Candle", true, Group: Calm),
        new("lava_lamp", "Lava lamp", true, Group: Calm),
        new("color_wave", "Color wave", true, Group: Motion),
        new("comet", "Comet", true, Group: Motion),
        new("chase", "Chase", true, Group: Motion),
        new("scanner", "Scanner", true, Group: Motion),
        new("twinkle", "Twinkle", true, Group: Motion),
        new("rain", "Rain", true, Group: Motion),
        new("meteor_shower", "Meteor shower", true, Group: Motion),
        new("digital_rain", "Digital rain", true, Group: Motion),
        new("bounce", "Bounce", true, Group: Motion),
        new("spectrum", "Spectrum", false, Group: Colorful),
        new("rainbow_wave", "Rainbow wave", false, Group: Colorful),
        new("plasma", "Plasma", false, Group: Colorful),
        new("neon_pulse", "Neon pulse", true, Group: Colorful),
        new("fire", "Fire", false, Group: Colorful),
        new("aurora", "Aurora", false, Group: Colorful),
        new("ocean", "Ocean", false, Group: Colorful),
        new("lightning_storm", "Lightning storm", false, Group: Colorful),
        new("fireworks", "Fireworks", false, Group: Colorful),
        new("confetti", "Confetti", false, Group: Colorful),
        new("sunset", "Sunset", false, Group: Colorful),
        new("heartbeat", "Heartbeat", true, Group: Alerts),
        new("strobe", "Strobe", true, Group: Alerts),
        new("police", "Police", false, Group: Alerts),
        new("beacon", "Beacon", true, Group: Alerts),
        // The sound effects: beat, bass or spectrum, with lights per device / zone / LED and their colors.
        new("sound_beat", "Beat", true, Audio: true, Group: Sound),
        new("sound_bass", "Bass", true, Audio: true, Group: Sound),
        new("sound_spectrum", "Spectrum", true, Audio: true, Group: Sound),
        new("sound_level", "Level meter", true, Audio: true, Group: Sound),
        new("sound_ripple", "Beat ripple", true, Audio: true, Group: Sound),
        new("sound_sparkle", "Sparkle", true, Audio: true, Group: Sound),
    ];
    private static EffectOption EffectById(string id) => Effects.FirstOrDefault(e => e.Id == id) ?? Effects[0];

    private static readonly (string Name, string Hex)[] PresetColors =
    [
        ("Red", "FF1A1A"), ("Orange", "FF6A00"), ("Amber", "FFB300"), ("Lime", "7CFF00"), ("Green", "00E05A"), ("Teal", "00D1B2"),
        ("Cyan", "00C8FF"), ("Blue", "1A5CFF"), ("Indigo", "5B3DFF"), ("Purple", "A020F0"), ("Pink", "FF2D95"), ("White", "FFFFFF"),
    ];

    private readonly LightSyncClient _client = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pushTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statusTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idleTimer;
    /// <summary>The lights were turned off because nobody used the PC for a while.</summary>
    private bool _idleOff;
    private readonly string _assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
    private readonly Dictionary<string, Button> _effectButtons = [];
    private readonly Dictionary<string, Border> _effectPreviews = [];
    private readonly List<(string Hex, Ellipse Ring)> _presetRings = [];
    private readonly ColorPicker _customPicker;
    private TaskbarIcon? _trayIcon;
    private Func<Task>? _noticeAction;
    private string _effect = "static";
    /// <summary>The sound palette of the selected target (sound effects only): "effect", "random", "cold_hot", "fire" or "neon".</summary>
    private string _palette = "effect";
    /// <summary>Beat / Bass / Spectrum: what one light is ("device" or "led") and how many colors random uses (0: random).</summary>
    private string _split = "device";
    private int _colors = 3;
    private Color _color = Color.FromArgb(255, 255, 255, 255);
    private int _deviceCount;
    private bool _exiting;
    private bool _ready;
    private bool _loading;
    private bool _syncingPicker;
    /// <summary>The devices shown and controlled (not hidden).</summary>
    private List<DeviceInfo> _devices = [];
    /// <summary>Every device the service knows, hidden ones included.</summary>
    private List<DeviceInfo> _allDevices = [];
    private List<GroupInfo> _groups = [];
    private List<TargetItem> _targets = [];
    /// <summary>Group rows, shown above the devices in the same list.</summary>
    private List<TargetItem> _groupItems = [];
    private TargetItem _target = TargetItem.All(0);
    /// <summary>Targets picked with Shift+click in the preview; when it holds two or more, changes apply to each.</summary>
    private List<string> _selection = [];
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _openRgbBusy;
    private bool _restartingService;
    private bool _on = true;
    /// <summary>Set while the code moves the lights switch, so it doesn't count as a click.</summary>
    private bool _showingPower;
    private readonly SetupPreview3D _preview;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewTimer;
    private bool _previewBusy;
    /// <summary>Lights were turned off by a lock or sleep, so unlock / resume turns them back on.</summary>
    private bool _autoOff;
    private List<string> _profiles = [];
    private MenuFlyout? _trayMenu;

    public MainWindow()
    {
        InitializeComponent();
        OpenRgbLauncher.NoPromptWanted = _settings.OpenRgbWithoutPrompt;
        // Errors that were caught (and logged) show up as a notice instead of closing LightSync.
        CrashLog.Notify = (where, e) => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!_exiting)
                {
                    ShowNotice(InfoBarSeverity.Warning, "Something went wrong",
                        $"LightSync recovered from an error ({where}): {e.Message} Details are in crash.log.");
                }
            }
            catch (Exception failure) when (Errors.Recoverable(failure))
            {
                // Logged already.
            }
        });

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var ico = System.IO.Path.Combine(_assets, "AppIcon.ico");
        if (File.Exists(ico))
        {
            AppWindow.SetIcon(ico);
        }
        TitleIcon.Source = new BitmapImage(new Uri(System.IO.Path.Combine(_assets, "AppIcon.png")));
        RestoreBounds();

        // Changes are pushed to the service shortly after the last adjustment (smooth while dragging).
        _pushTimer = DispatcherQueue.CreateTimer();
        _pushTimer.Interval = TimeSpan.FromMilliseconds(60);
        _pushTimer.IsRepeating = false;
        _pushTimer.Tick += (_, _) => Safe("applying a change", PushAsync);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(3);
        _statusTimer.Tick += (_, _) => Safe("status check", RefreshStatusAsync);

        // Closing the window hides it to the tray; "Exit" in the tray menu quits. Lights keep running.
        AppWindow.Closing += (_, args) =>
        {
            if (!_exiting)
            {
                args.Cancel = true;
                SaveBounds();
                AppWindow.Hide();
            }
        };

        _customPicker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Box,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsColorPreviewVisible = true,
            IsColorSliderVisible = true,
        };
        _customPicker.ColorChanged += (_, args) =>
        {
            try
            {
                if (_ready && !_syncingPicker)
                {
                    SetColor(args.NewColor, fromPicker: true);
                }
            }
            catch (Exception e) when (Errors.Recoverable(e))
            {
                CrashLog.Report("color picker", e);
            }
        };

        _preview = new SetupPreview3D(PreviewHost, _settings,
            (id, add) => Safe("selecting in the preview", () => SelectFromPreviewAsync(id, add)),
            id => Safe("hiding a device", () => SetHiddenAsync(id, true)),
            id => SetZoneHidden(id, true));
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(100);
        _previewTimer.Tick += (_, _) => Safe("preview", RefreshPreviewAsync);
        StartPreview();
        ShowView(ShellView.Home); // the app opens on Home
        // Hidden to the tray or minimized: the preview sleeps; shown again: it wakes.
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange)
            {
                OnUi(UpdatePreviewActivity);
            }
        };
        _currentPreset = _settings.CurrentPreset;
        FooterPreset.Text = _currentPreset is null ? "Preset: Custom" : $"Preset: {_currentPreset}";

        _analyzerTimer = DispatcherQueue.CreateTimer();
        _analyzerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _analyzerTimer.Tick += (_, _) => Safe("sound analyzer", RefreshAnalyzerAsync);
        _inputTimer = DispatcherQueue.CreateTimer();
        _inputTimer.Interval = TimeSpan.FromSeconds(2); // also how quickly an app trigger notices its app
        _inputTimer.Tick += (_, _) => Safe("trigger input check", RefreshInputWantedAsync);
        _input = new InputListener(OnInput);

        BuildEffectTiles();
        BuildAnalyzer();
        BuildPaletteTiles();
        BuildSoundOptions();
        BuildPresets();
        ShowColor();
        ShowEffect();
        CreateTrayIcon(ico);
        AddShortcuts();
        _idleTimer = DispatcherQueue.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(2);
        _idleTimer.Tick += (_, _) => Safe("idle check", CheckIdleAsync);
        _idleTimer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Safe("startup", StartAsync);
    }

    /// <summary>
    /// Run an async handler (click, timer tick, callback) so that nothing it throws can end the app:
    /// errors are logged to crash.log and shown as a notice.
    /// </summary>
    private void Safe(string where, Func<Task> work) => CrashLog.Run(where, async () =>
    {
        if (!_exiting)
        {
            await work();
        }
    });

    /// <summary>Run <paramref name="action"/> on this window's UI thread (now if already there), unless LightSync is exiting.</summary>
    private void OnUi(Action action)
    {
        void Do()
        {
            if (_exiting)
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception e) when (Errors.Recoverable(e))
            {
                CrashLog.Report("updating the window", e);
            }
        }
        if (DispatcherQueue.HasThreadAccess)
        {
            Do();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Do);
        }
    }

    private string Hex => $"{_color.R:X2}{_color.G:X2}{_color.B:X2}";

    /// <summary>What a change applies to: the multi-selection if there is one, else the selected target.</summary>
    private IReadOnlyList<string> TargetIds => _selection.Count > 1 ? _selection : [_target.Id];

    // ---------- startup: service, OpenRGB, devices ----------

    private async Task StartAsync()
    {
        SetHealth(Health.Busy, "Starting service...");
        var found = await Task.Run(LightSyncClient.EnsureDaemonRunning);
        if (!found)
        {
            SetHealth(Health.Bad, "Service missing");
            ShowNotice(InfoBarSeverity.Error, "lightsyncd was not found",
                "Build it with `cargo build -p lightsyncd`, put lightsyncd.exe next to LightSync.exe, or set LIGHTSYNCD_PATH.");
            return;
        }

        // Show what the lights are doing now, so opening the app never changes them.
        ShowTarget();
        await LoadTargetStateAsync();
        await LoadGroupsAsync();
        await LoadProfilesAsync();
        try
        {
            ShowPower((await _client.StatusAsync()).On ?? true);
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Not fatal: the status timer tries again.
        }
        _ready = true;

        // Keyboard and mouse triggers: listen only while an enabled trigger needs it.
        await LoadTriggersAsync();
        _inputTimer.Start();

        await StartOpenRgbAndLoadAsync();
        _statusTimer.Start();
        await AutoUpdateCheckAsync();
        WarnAboutOtherLightingApps();
        WarnIfOpenRgbIsOnTheNetwork();
    }

    /// <summary>
    /// OpenRGB started by an older LightSync (or by hand) listens on every network interface, and its SDK
    /// has no password. Offer a restart, which brings it back listening on this PC only.
    /// </summary>
    private void WarnIfOpenRgbIsOnTheNetwork()
    {
        try
        {
            var exposed = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(e => e.Port == OpenRgbLauncher.Port && !System.Net.IPAddress.IsLoopback(e.Address));
            if (exposed)
            {
                ShowNotice(InfoBarSeverity.Warning, "OpenRGB can be reached from your network",
                    "It was started with older settings. Restart it so only this PC can change your lights (Windows asks for administrator rights once).",
                    "Restart OpenRGB", RestartOpenRgbAsync);
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Listing listeners is best effort.
        }
    }

    /// <summary>Vendor apps that drive the same controllers: while they run, colors can blink or not stick.</summary>
    private static readonly (string Process, string App, string Devices)[] OtherLightingApps =
    [
        ("RGBFusion", "Gigabyte RGB Fusion", "the motherboard"),
        ("iCUE", "Corsair iCUE", "Corsair devices"),
        ("SignalRgb", "SignalRGB", "your devices"),
        ("ArmouryCrate", "Armoury Crate", "ASUS devices"),
        ("MSI.CentralServer", "MSI Center", "MSI devices"),
        ("LightingService", "ASUS Aura", "ASUS devices"),
    ];

    private void WarnAboutOtherLightingApps()
    {
        var running = OtherLightingApps
            .Where(a => System.Diagnostics.Process.GetProcessesByName(a.Process).Length > 0)
            .ToList();
        if (running.Count == 0 || Notice.IsOpen && Notice.Severity == InfoBarSeverity.Error)
        {
            return;
        }
        var names = string.Join(" and ", running.Select(a => a.App).Distinct());
        var devices = string.Join(" and ", running.Select(a => a.Devices).Distinct());
        ShowNotice(InfoBarSeverity.Warning, $"{names} {(running.Count == 1 ? "is" : "are")} also controlling {devices}",
            "Two apps setting the same lights makes colors blink or not stick. Quit it from the tray (right-click its icon) or turn off its lighting control, then press F5.");
    }

    private async Task StartOpenRgbAndLoadAsync()
    {
        HideNotice();
        SetHealth(Health.Busy, "Starting OpenRGB...");
        var result = await OpenRgbLauncher.EnsureRunningAsync(TimeSpan.FromSeconds(30));
        switch (result)
        {
            case OpenRgbStartResult.NotFound:
                SetHealth(Health.Bad, "OpenRGB not found");
                ShowEmpty("OpenRGB was not found.");
                ShowNotice(InfoBarSeverity.Error, "OpenRGB was not found",
                    "Put OpenRGB in the repo's vendor\\openrgb folder or in an \"openrgb\" folder next to LightSync.exe, or set LIGHTSYNC_OPENRGB to OpenRGB.exe.");
                return;
            case OpenRgbStartResult.Declined:
                SetHealth(Health.Bad, "OpenRGB not running");
                ShowEmpty("OpenRGB isn't running, so no devices can be controlled.");
                ShowNotice(InfoBarSeverity.Warning, "OpenRGB needs administrator rights",
                    "LightSync starts OpenRGB as administrator so it can reach motherboard and memory lighting.",
                    "Try again", StartOpenRgbAndLoadAsync);
                return;
            case OpenRgbStartResult.TimedOut:
                SetHealth(Health.Bad, "OpenRGB not responding");
                ShowEmpty("OpenRGB started but its server didn't answer.");
                ShowNotice(InfoBarSeverity.Warning, "OpenRGB is taking a while",
                    "OpenRGB was started but its SDK server on port 6742 didn't answer within 30 seconds.",
                    "Retry", StartOpenRgbAndLoadAsync);
                return;
            case OpenRgbStartResult.Started:
                SetHealth(Health.Busy, "Detecting devices...");
                await Task.Delay(2500);
                await LoadDevicesAsync(rescan: true);
                // OpenRGB keeps detecting for a few seconds after its server opens; pick up late devices.
                await Task.Delay(6000);
                await LoadDevicesAsync(rescan: true);
                return;
            default:
                await LoadDevicesAsync(rescan: false);
                return;
        }
    }

    private async Task LoadDevicesAsync(bool rescan)
    {
        RescanButton.IsEnabled = false;
        try
        {
            var devices = await WithHiddenAsync(await _client.DevicesAsync(rescan));
            if (_exiting)
            {
                return;
            }
            ShowDevices(devices);
            if (devices.Count == 0)
            {
                ShowEmpty("OpenRGB is running but reported no devices.");
                SetHealth(Health.Bad, "No devices");
            }
            else if (_devices.Count == 0)
            {
                ShowEmpty(devices.Count == 1 ? "Your only device is hidden." : $"All {devices.Count} of your devices are hidden.", hiddenOnly: true);
                SetHealth(Health.Good, "All devices hidden");
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                TargetList.Visibility = Visibility.Visible;
                SetConnected();
                if (Notice.Severity == InfoBarSeverity.Error)
                {
                    HideNotice();
                }
            }
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            SetHealth(Health.Bad, "Not connected");
            ShowEmpty("LightSync couldn't reach your devices.");
            ShowNotice(InfoBarSeverity.Error, "Could not list devices", Explain(e), "Start OpenRGB", StartOpenRgbAndLoadAsync);
        }
        finally
        {
            RescanButton.IsEnabled = true;
        }
    }

    private async Task RefreshStatusAsync()
    {
        await ShowOpenRgbStateAsync();
        if (_deviceCount == 0)
        {
            return;
        }
        try
        {
            var s = await _client.StatusAsync();
            if (s.On is { } on && on != _on)
            {
                ShowPower(on);
            }
            if (s.Error is { } err)
            {
                SetHealth(Health.Bad, "Connection lost");
                ShowNotice(InfoBarSeverity.Error, "Lights are not being updated", Explain(new LightSyncException(err)), "Start OpenRGB", StartOpenRgbAndLoadAsync);
            }
            else if (s.Connected)
            {
                SetConnected();
                if (Notice.IsOpen && Notice.Title == "Lights are not being updated")
                {
                    HideNotice();
                }
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // The service stopped (crash, update, someone ended it): start it again and reload.
            SetHealth(Health.Busy, "Restarting the lighting service...");
            if (!_restartingService)
            {
                _restartingService = true;
                try
                {
                    if (await Task.Run(LightSyncClient.EnsureDaemonRunning))
                    {
                        await LoadDevicesAsync(rescan: false);
                    }
                    else
                    {
                        SetHealth(Health.Bad, "Service not responding");
                    }
                }
                finally
                {
                    _restartingService = false;
                }
            }
        }
    }

    // ---------- applying ----------

    private void QueuePush()
    {
        if (!_ready || _loading)
        {
            return;
        }
        _pushTimer.Stop();
        _pushTimer.Start();
    }

    private async Task PushAsync()
    {
        _pushTimer.Stop();
        try
        {
            foreach (var id in TargetIds)
            {
                await _client.SetStateAsync(id, _effect, Hex, (int)BrightnessSlider.Value, (int)SpeedSlider.Value, _palette, _split, _colors);
            }
            // A change by hand: the lighting is no longer exactly the preset.
            if (_currentPreset is not null)
            {
                ShowPreset(null);
            }
            await RefreshRowsAsync();
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not apply", Explain(e), "Start OpenRGB", StartOpenRgbAndLoadAsync);
        }
    }

    private void SetColor(Color color, bool fromPicker = false)
    {
        _color = Color.FromArgb(255, color.R, color.G, color.B);
        if (!fromPicker)
        {
            SyncPicker();
        }
        // A color only makes sense with an effect that uses it.
        if (!EffectById(_effect).UsesColor)
        {
            _effect = "static";
            ShowEffect();
        }
        else if (_palette != "effect")
        {
            _palette = "effect"; // picking a color means: use it, not the sound palette
            ShowEffect();
        }
        ShowColor();
        QueuePush();
    }

    // ---------- beat / bass / spectrum options ----------

    private readonly Dictionary<string, ToggleButton> _splitButtons = [];

    /// <summary>Lights per device / zone / LED, as shown to the user.</summary>
    internal static readonly (string Id, string Name, string Tip)[] Splits =
    [
        ("device", "Device", "Each device acts as one LED: one color for the whole device"),
        ("led", "LED", "Every LED of every device on its own"),
    ];

    /// <summary>The "Lights per" buttons and the "Number of colors" choices (Beat, Bass, Spectrum).</summary>
    private void BuildSoundOptions()
    {
        foreach (var (id, name, tip) in Splits)
        {
            var b = new ToggleButton { Content = name, MinWidth = 80 };
            ToolTipService.SetToolTip(b, tip);
            var pick = id;
            b.Click += (_, _) =>
            {
                _split = pick;
                ShowEffect();
                QueuePush();
            };
            _splitButtons[id] = b;
            SplitButtons.Children.Add(b);
        }
        ColorCountBox.ItemsSource = ColorCountNames;
    }

    /// <summary>"Random" (a new number on every beat), then 1 to 8 colors.</summary>
    internal static readonly List<string> ColorCountNames = ["Random number", .. Enumerable.Range(1, 8).Select(n => n == 1 ? "1 color" : $"{n} colors")];

    private void ColorCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingColorCount || ColorCountBox.SelectedIndex < 0)
        {
            return;
        }
        _colors = ColorCountBox.SelectedIndex; // index 0 = random, n = n colors
        QueuePush();
    }

    private bool _syncingColorCount;

    /// <summary>Sound effects that light per device or per LED ("Lights per"); the others run along each device's LEDs.</summary>
    private static bool UsesSplit(string effect) => effect is "sound_beat" or "sound_bass" or "sound_spectrum";

    private void ShowSoundOptions(EffectOption effect)
    {
        var split = effect.Audio && UsesSplit(effect.Id);
        SoundOptions.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, b) in _splitButtons)
        {
            b.IsChecked = id == _split;
        }
        SplitHint.Text = (effect.Id, _split) switch
        {
            ("sound_spectrum", "device") => "The spectrum is spread over your devices: bass on the first, treble on the last.",
            ("sound_spectrum", _) => "The spectrum runs along each device's LEDs: bass at the start, treble at the end.",
            (_, "device") => "Each device acts as one LED: the whole device shows one color.",
            _ => "Every LED of every device is handled on its own: its own color and brightness.",
        };
        ColorCountPanel.Visibility = effect.Audio && _palette == "random" ? Visibility.Visible : Visibility.Collapsed;
        _syncingColorCount = true;
        ColorCountBox.SelectedIndex = Math.Clamp(_colors, 0, 8);
        _syncingColorCount = false;
    }

    // ---------- sound palettes ----------

    private readonly Dictionary<string, Button> _paletteButtons = [];

    /// <summary>The sound palette tiles under the colors (shown for sound effects).</summary>
    private void BuildPaletteTiles()
    {
        foreach (var (id, name, tip) in SoundPalettes)
        {
            var swatch = new Border { Height = 18, CornerRadius = new CornerRadius(9), Background = PaletteBrush(id), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
            var button = new Button
            {
                Width = 110,
                Height = 58,
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children = { swatch, new TextBlock { Text = name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center } },
                },
            };
            ToolTipService.SetToolTip(button, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Sound colors: {name}");
            var pick = id;
            button.Click += (_, _) =>
            {
                _palette = pick;
                ShowEffect();
                QueuePush();
            };
            _paletteButtons[id] = button;
            PaletteTiles.Children.Add(button);
        }
    }

    private void ShowPalette()
    {
        foreach (var (id, button) in _paletteButtons)
        {
            var selected = id == _palette;
            button.BorderBrush = selected ? Res("AccentFillColorDefaultBrush") : Res("CardStrokeColorDefaultBrush");
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }
        if (_paletteButtons.TryGetValue("effect", out var own) && own.Content is StackPanel { Children: [Border swatch, ..] })
        {
            swatch.Background = EffectBrush(_effect, _color); // the effect's own colors
        }
    }

    /// <summary>A small picture of a sound palette.</summary>
    private static Brush PaletteBrush(string id) => id switch
    {
        "rainbow" => HueBrush(1),
        "random" => Gradient([.. Enumerable.Range(0, 6).Select(k => SoundVisualizer.Hue(k * 0.618034))]),
        "cold_hot" => Gradient([.. Enumerable.Range(0, 5).Select(k => SoundVisualizer.PaletteColor("cold_hot", k / 4.0, 0, 0))]),
        "low_high" => Gradient([.. Enumerable.Range(0, 5).Select(k => SoundVisualizer.Hue(0.78 * k / 4.0))]),
        "fire" => Gradient([Color.FromArgb(255, 90, 0, 0), .. Enumerable.Range(0, 4).Select(k => SoundVisualizer.PaletteColor("fire", k / 3.0, 0, 0))]),
        "neon" => Gradient([SoundVisualizer.Hue(0.92), SoundVisualizer.Hue(0.92), SoundVisualizer.Hue(0.5), SoundVisualizer.Hue(0.5)]),
        _ => new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
    };

    private void SyncPicker()
    {
        _syncingPicker = true;
        _customPicker.Color = _color;
        _syncingPicker = false;
    }

    private void SetEffect(string id)
    {
        _effect = id;
        ShowEffect();
        QueuePush();
    }

    // ---------- targets: all devices, a device, a zone, a fan or a group ----------

    /// <summary>Show the devices that aren't hidden in the list and the preview; hidden ones are only listed in Settings > Devices.</summary>
    private void ShowDevices(List<DeviceInfo> devices)
    {
        _allDevices = devices;
        // Hidden zones (Settings > Devices shows them again) leave the list and the preview; their device stays.
        var hiddenZones = (_settings.HiddenZones ?? []).ToHashSet();
        _devices = devices.Where(d => !d.Hidden)
            .Select(d => hiddenZones.Count == 0 || d.Zones is null || d.Zones.All(z => !hiddenZones.Contains(z.Id))
                ? d
                : d with { Zones = d.Zones.Where(z => !hiddenZones.Contains(z.Id)).ToList() })
            .ToList();
        _deviceCount = _devices.Count;
        ShowDeviceCount();
        _targets = TargetItem.FromDevices(_devices);
        _selection.RemoveAll(id => _targets.All(t => t.Id != id));
        ShowRows();
        _preview.Build(_devices);
        Reselect();
        Safe("updating triggers", MigrateLedTargetsAsync);
        Safe("device states", RefreshRowsAsync);
        RefreshSettingsWindow(SettingsPage.Devices);
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            _groups = await _client.GroupsAsync();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            _groups = [];
        }
        _groupItems = _groups.Select(g => TargetItem.ForGroup(g)).ToList();
        ShowRows();
        Reselect();
        Safe("device states", RefreshRowsAsync);
        RefreshSettingsWindow(SettingsPage.Devices);
    }

    /// <summary>Every selectable row: groups first, then all devices and each device with its zones and fans.</summary>
    private IEnumerable<TargetItem> Rows => _groupItems.Concat(_targets);

    /// <summary>Fill the device list: with "Groups" and "Devices" headers once there are groups, else just the devices.</summary>
    private void ShowRows()
    {
        _loading = true;
        if (_groupItems.Count > 0)
        {
            TargetList.ItemsSource = new Microsoft.UI.Xaml.Data.CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = new List<TargetSection> { new("Groups", _groupItems), new("Devices", _targets) },
            }.View;
        }
        else
        {
            TargetList.ItemsSource = _targets;
        }
        _loading = false;
    }

    /// <summary>After the list is rebuilt, select the current target again (or "All devices" if it is gone).</summary>
    private void Reselect()
    {
        var found = Rows.FirstOrDefault(t => t.Id == _target.Id);
        _loading = true;
        TargetList.SelectedItem = _selection.Count > 1 || _view != ShellView.Device ? null : found;
        _loading = false;
        if (found is null && _targets.Count > 0)
        {
            var first = _targets[0];
            Safe("selecting", () => SelectAsync(first));
            return;
        }
        if (found is not null)
        {
            _target = found;
            ShowTarget();
            HighlightPreview();
        }
    }

    private async Task SelectAsync(TargetItem item, bool keepSelection = false)
    {
        // Finish a pending change on the previous target first.
        if (_pushTimer.IsRunning)
        {
            await PushAsync();
        }
        if (!keepSelection)
        {
            _selection.Clear();
        }
        _target = item;
        _loading = true;
        TargetList.SelectedItem = _selection.Count > 1 || _view != ShellView.Device ? null : Rows.FirstOrDefault(t => t.Id == item.Id);
        _loading = false;
        ShowTarget();
        HighlightPreview();
        await LoadTargetStateAsync();
    }

    /// <summary>
    /// A part of the preview was clicked: select it, or with Shift add it to (or take it out of) the
    /// multi-selection. Changes then go to every selected part.
    /// </summary>
    private async Task SelectFromPreviewAsync(string id, bool add)
    {
        if (_targets.FirstOrDefault(t => t.Id == id) is not { } item)
        {
            return;
        }
        // Clicking a part opens its lighting, from Home or a trigger too.
        ShowView(ShellView.Device);
        if (!add || id == "all")
        {
            await SelectAsync(item);
            return;
        }
        if (_pushTimer.IsRunning)
        {
            await PushAsync();
        }
        if (_selection.Count == 0 && _target.Kind is not (TargetKind.All or TargetKind.Group))
        {
            _selection.Add(_target.Id);
        }
        if (!_selection.Remove(id))
        {
            _selection.Add(id);
        }
        var first = _selection.Count > 0 ? _targets.FirstOrDefault(t => t.Id == _selection[0]) : null;
        await SelectAsync(first ?? item, keepSelection: _selection.Count > 1);
    }

    // ---------- preview ----------

    /// <summary>Arrange mode: move, turn and line up parts of the preview in 3D, right-click one to set its type.</summary>
    private void ArrangeToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = ArrangeToggle.IsChecked == true;
        _preview.EditMode = on;
        ResetLayoutButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        ResetLayoutFlyout.Hide();
        _preview.ResetLayout();
        HighlightPreview();
    }

    /// <summary>The preview is always shown (it can't be folded away); start it and its colour updates.</summary>
    private void StartPreview()
    {
        PreviewScroller.Visibility = Visibility.Visible;
        ResetLayoutButton.Visibility = ArrangeToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _preview.EnsureStarted();
        _previewTimer.Start();
    }

    /// <summary>The window is on screen (not hidden to the tray, minimized or closed).</summary>
    private bool IsOnScreen
    {
        get
        {
            if (_exiting)
            {
                return false;
            }
            try
            {
                return AppWindow is { IsVisible: true } w &&
                    w.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                return false; // The window is going away.
            }
        }
    }

    /// <summary>
    /// Paint the preview with what the lights show now (only while it can be seen). Runs from a timer, so
    /// it never throws: no preview, a page that isn't ready, a closed window or a lost service just skip a frame.
    /// </summary>
    private async Task RefreshPreviewAsync()
    {
        if (_previewBusy || !_ready || _exiting || _deviceCount == 0 || _view != ShellView.Home || _preview is not { IsEmpty: false } preview || !IsOnScreen)
        {
            return;
        }
        _previewBusy = true;
        try
        {
            var data = await _client.PreviewAsync(48);
            if (!_exiting && data is not null)
            {
                preview.Update(data);
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Not connected right now: the status pill says so; keep the last picture.
        }
        finally
        {
            _previewBusy = false;
        }
    }

    /// <summary>Show the selected target's current lighting in the controls without changing anything.</summary>
    private async Task LoadTargetStateAsync()
    {
        try
        {
            var s = await _client.GetTargetAsync(_target.Id);
            _loading = true;
            _effect = Effects.Any(e => e.Id == s.Effect) ? s.Effect : "static";
            _color = ParseHex(s.Color) ?? _color;
            BrightnessSlider.Value = s.Brightness;
            SpeedSlider.Value = s.Speed;
            _palette = SoundPalettes.Any(p => p.Id == s.Palette) ? s.Palette : "effect";
            _split = s.Split == "led" ? "led" : "device";
            _colors = Math.Clamp(s.Colors, 0, 8);
            SyncPicker();
            ShowColor();
            ShowEffect();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Older service or not ready yet: keep what is shown.
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowTarget()
    {
        if (_selection.Count > 1)
        {
            TargetTitle.Text = $"{_selection.Count} selected";
            TargetHint.Text = string.Join(", ", _selection.Select(NameOf));
            ToolTipService.SetToolTip(TargetHint, "Changes apply to each selected part. Shift+click a part in the preview to add or remove it; click without Shift to select one again.");
            FansButton.Visibility = Visibility.Collapsed;
            EditGroupButton.Visibility = Visibility.Collapsed;
            DeleteGroupButton.Visibility = Visibility.Collapsed;
            ShowTriggers();
            return;
        }
        TargetTitle.Text = _target.Title;
        TargetHint.Text = _target.Kind switch
        {
            TargetKind.All => _deviceCount == 1 ? "1 device" : $"{_deviceCount} devices",
            TargetKind.Device => _target.Subtitle,
            TargetKind.Zone or TargetKind.Fan => $"{_target.DeviceName} · {_target.Subtitle}",
            TargetKind.Group => _target.Group is { } grp ? string.Join(", ", grp.Members.Select(NameOf)) : "",
            _ => "",
        };
        // The longer explanation stays one hover away.
        ToolTipService.SetToolTip(TargetHint, _target.Kind switch
        {
            TargetKind.All => "Changes apply to every device and replace individual settings.",
            TargetKind.Device => "Changes apply to the whole device, including its zones.",
            TargetKind.Zone when _target.Zone is { Fans.Count: > 0 } z => $"{_target.DeviceName}: {z.Fans.Count} fans with {z.Leds / z.Fans.Count} LEDs each. Pick a fan to set it alone.",
            TargetKind.Zone when _target.Zone is { Resizable: true } => $"Addressable header on {_target.DeviceName}. If fans are chained on it, split it into fans to light each one on its own.",
            TargetKind.Zone => $"One lighting zone of {_target.DeviceName}.",
            TargetKind.Fan => $"{_target.Subtitle} of {_target.DeviceName}.",
            TargetKind.Group => _target.Group is { } g ? $"{g.Members.Count} member{(g.Members.Count == 1 ? "" : "s")}: {string.Join(", ", g.Members.Select(NameOf))}" : "",
            _ => "",
        });
        FansButton.Visibility = _target.Kind == TargetKind.Zone && _target.Zone is { Resizable: true } ? Visibility.Visible : Visibility.Collapsed;
        FansButton.Content = _target.Zone is { Fans.Count: > 0 } ? "Change fans" : "Split into fans";
        var isGroup = _target.Kind == TargetKind.Group ? Visibility.Visible : Visibility.Collapsed;
        EditGroupButton.Visibility = isGroup;
        DeleteGroupButton.Visibility = isGroup;
        ShowTriggers();
    }

    /// <summary>Readable name of a device / zone / fan id, for group summaries.</summary>
    private string NameOf(string id) => Rows.FirstOrDefault(t => t.Id == id)?.LongName ?? id.Split(':').Last();

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && e.AddedItems.Count > 0 && e.AddedItems[0] is TargetItem item)
        {
            ShowView(ShellView.Device);
            Safe("selecting", () => SelectAsync(item));
        }
    }

    // ---------- groups ----------

    private void NewGroup_Click(object sender, RoutedEventArgs e) => Safe("new group", () => EditGroupAsync(null));

    private void EditGroup_Click(object sender, RoutedEventArgs e) => Safe("editing a group", () => EditGroupAsync(_target.Group));

    /// <summary>Create (null) or edit a group, in a dialog over <paramref name="root"/> (default: this window).</summary>
    private async Task EditGroupAsync(GroupInfo? group, XamlRoot? root = null)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "For example: Front fans", Text = group?.Name ?? "" };
        var error = new TextBlock { Foreground = Res("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        // A new group starts with the parts picked in the preview (Shift+click), if any.
        var members = new HashSet<string>(group?.Members ?? (_selection.Count > 1 ? _selection : []));
        var boxes = new List<(string Id, CheckBox Box)>();
        var list = new StackPanel();
        foreach (var t in _targets.Where(t => t.Kind != TargetKind.All))
        {
            var box = new CheckBox
            {
                Content = t.Kind == TargetKind.Device ? t.Title : $"{t.Title}  ({t.Subtitle})",
                IsChecked = members.Contains(t.Id),
                Margin = new Thickness(t.Level * 24.0, 0, 0, 0),
                FontWeight = t.Kind == TargetKind.Device ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            boxes.Add((t.Id, box));
            list.Children.Add(box);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root ?? Content.XamlRoot,
            Title = group is null ? "New group" : "Edit group",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 380,
                Children =
                {
                    name,
                    new TextBlock { Text = "Members", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    new ScrollViewer { MaxHeight = 360, Content = list },
                    error,
                },
            },
        };
        // Set by the dialog's Save handler (a box: the handler runs while the dialog is open).
        var saved = new System.Runtime.CompilerServices.StrongBox<GroupInfo?>();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                // Keep members that are not connected right now (e.g. a device that is switched off).
                var shown = boxes.Select(b => b.Id).ToHashSet();
                var picked = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id)
                    .Concat(members.Where(m => !shown.Contains(m))).ToList();
                if (string.IsNullOrWhiteSpace(name.Text) || picked.Count == 0)
                {
                    error.Text = string.IsNullOrWhiteSpace(name.Text) ? "Give the group a name." : "Pick at least one device, zone or fan.";
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
                saved.Value = await _client.SaveGroupAsync(group?.Id, name.Text.Trim(), picked);
            }
            catch (Exception ex) when (Errors.Expected(ex))
            {
                error.Text = Explain(ex);
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && saved.Value is { } savedGroup)
        {
            await LoadGroupsAsync();
            await SelectAsync(TargetItem.ForGroup(savedGroup));
        }
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_target.Group is { } group)
        {
            Safe("deleting a group", () => DeleteGroupAsync(group));
        }
    }

    private async Task DeleteGroupAsync(GroupInfo group, XamlRoot? root = null)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = root ?? Content.XamlRoot,
            Title = $"Delete \"{group.Name}\"?",
            Content = "The devices keep their current lighting. Only the group is removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            await _client.DeleteGroupAsync(group.Id);
            var wasSelected = _target.Id == $"group:{group.Id}";
            if (wasSelected)
            {
                _target = TargetItem.All(_deviceCount);
            }
            await LoadGroupsAsync();
            if (wasSelected)
            {
                await SelectAsync(_targets.FirstOrDefault() ?? _target);
            }
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not delete the group", Explain(ex));
        }
    }

    // ---------- fans on an addressable header ----------

    private void Fans_Click(object sender, RoutedEventArgs e)
    {
        if (_target.Zone is { } zone)
        {
            Safe("fan layout", () => EditFansAsync(zone, selectAfter: true));
        }
    }

    /// <summary>Split an addressable header into fans (or back into one strip), in a dialog over <paramref name="root"/>.</summary>
    private async Task EditFansAsync(ZoneInfo zone, bool selectAfter, XamlRoot? root = null)
    {
        var currentPer = zone.Fans.Count > 0 ? (int)(zone.Leds / (uint)zone.Fans.Count) : 0;
        var fans = new NumberBox
        {
            Header = "Fans on this header",
            Minimum = 0,
            Maximum = 32,
            Value = zone.Fans.Count,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var per = new NumberBox
        {
            Header = "LEDs in each fan",
            Minimum = 1,
            Maximum = Math.Max(1, zone.MaxLeds),
            Value = currentPer > 0 ? currentPer : double.NaN,
            PlaceholderText = "From the fan's specs",
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var error = new TextBlock { Foreground = Res("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = root ?? Content.XamlRoot,
            Title = $"Fans on {zone.Name}",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Fans chained on one header share one long LED strip. Tell LightSync how many fans are on it and how many LEDs each has, and each fan gets its own entry. Set fans to 0 to go back to one strip.",
                    },
                    fans,
                    per,
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = Res("TextFillColorSecondaryBrush"),
                        Text = "Tip: after saving, give Fan 1 a color of its own. If the color spills into the next fan, the LED count per fan is too high; if Fan 1 is only partly lit, it is too low.",
                    },
                    error,
                },
            },
        };
        // Set by the dialog's Apply handler (a box: the handler runs while the dialog is open).
        var devices = new System.Runtime.CompilerServices.StrongBox<List<DeviceInfo>?>();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var f = double.IsNaN(fans.Value) ? 0 : (int)fans.Value;
                var p = double.IsNaN(per.Value) ? 0 : (int)per.Value;
                if (f > 0 && p <= 0)
                {
                    error.Text = "Enter how many LEDs each fan has.";
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
                if (f * p > zone.MaxLeds)
                {
                    error.Text = $"This header supports at most {zone.MaxLeds} LEDs.";
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
                devices.Value = await _client.SetLayoutAsync(zone.Id, f, f == 0 ? 0 : p);
            }
            catch (Exception ex) when (Errors.Expected(ex))
            {
                error.Text = Explain(ex);
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && devices.Value is { } newDevices)
        {
            ShowDevices(newDevices);
            if (selectAfter && _targets.FirstOrDefault(t => t.Id == zone.Id) is { } z)
            {
                await SelectAsync(z);
            }
        }
    }

    // ---------- power, profiles, sleep / lock, start with Windows ----------

    private void ShowPower(bool on)
    {
        _on = on;
        _showingPower = true;
        LightsButton.IsChecked = on;
        LightsText.Text = on ? "Stop lights" : "Start lights";
        LightsIcon.Glyph = on ? "" : ""; // pause / play
        ToolTipService.SetToolTip(LightsButton, on ? "Lights are running. Click to turn all lights off (Ctrl+L)." : "Lights are off. Click to turn them back on (Ctrl+L).");
        _showingPower = false;
        Safe("device states", RefreshRowsAsync);
    }

    private async Task SetPowerAsync(bool on)
    {
        try
        {
            await _client.SetPowerAsync(on);
            ShowPower(on);
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowPower(_on);
            ShowNotice(InfoBarSeverity.Error, on ? "Could not turn the lights on" : "Could not turn the lights off", Explain(e));
        }
    }

    /// <summary>Ctrl+L: all lights off, or back on.</summary>
    private async Task TogglePowerAsync()
    {
        _autoOff = false;
        await SetPowerAsync(!_on);
    }

    /// <summary>The status bar's Start / Stop lights button.</summary>
    private void LightsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_showingPower)
        {
            return;
        }
        _autoOff = false;
        var on = !_on;
        // Show the current state until the service confirms the change.
        LightsButton.IsChecked = _on;
        Safe("lights on or off", () => SetPowerAsync(on));
    }

    /// <summary>The title bar's OpenRGB button: stop it if it runs, else start it.</summary>
    private void OpenRgbToggle_Click(object sender, RoutedEventArgs e) => Safe("OpenRGB on or off", async () =>
    {
        if (await OpenRgbLauncher.IsServerUpAsync() || OpenRgbLauncher.IsProcessRunning())
        {
            await StopOpenRgbAsync();
        }
        else
        {
            await StartOpenRgbAndLoadAsync();
        }
        await ShowOpenRgbStateAsync();
    });

    private async Task ShowOpenRgbStateAsync()
    {
        var up = await OpenRgbLauncher.IsServerUpAsync();
        if (_exiting)
        {
            return;
        }
        var starting = !up && OpenRgbLauncher.IsProcessRunning();
        OpenRgbButton.IsChecked = up;
        OpenRgbText.Text = up ? "OpenRGB on" : starting ? "OpenRGB starting" : "OpenRGB off";
        OpenRgbDot.Fill = Res(up ? "SystemFillColorSuccessBrush" : starting ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
        ToolTipService.SetToolTip(OpenRgbButton, up
            ? "OpenRGB is running. Click to stop it (your lights keep their last colors)."
            : "OpenRGB is not running, so LightSync can't reach your devices. Click to start it.");
    }

    /// <summary>Turn the lights off for a lock or sleep (only if they are on), remembering to turn them back on.</summary>
    private void AutoPower(bool on)
    {
        if (!on && _on)
        {
            _autoOff = true;
            // Called on the system-events thread: block briefly so the lights are dark before Windows sleeps.
            try
            {
                _client.SetPowerAsync(false).Wait(TimeSpan.FromSeconds(2));
                Thread.Sleep(150);
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // Service gone: nothing to turn off.
            }
            OnUi(() => ShowPower(false));
        }
        else if (on && _autoOff)
        {
            _autoOff = false;
            OnUi(() => Safe("lights back on", () => SetPowerAsync(true)));
        }
    }

    // Both run on the system-events thread: an exception there would end the app, so nothing escapes.
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        try
        {
            if (!_settings.LightsOffOnLock || _exiting)
            {
                return;
            }
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            {
                AutoPower(false);
            }
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
            {
                AutoPower(true);
            }
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            CrashLog.Write("lock / unlock", ex);
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        try
        {
            if (!_settings.LightsOffOnSleep || _exiting)
            {
                return;
            }
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                AutoPower(false);
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                // OpenRGB and the controllers need a moment after waking up.
                OnUi(() => Safe("waking up", async () =>
                {
                    await Task.Delay(3000);
                    AutoPower(true);
                }));
            }
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            CrashLog.Write("sleep / resume", ex);
        }
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            _profiles = await _client.ProfilesAsync();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            _profiles = [];
        }
        BuildProfileMenus();
    }

    /// <summary>
    /// File > Presets (and the tray's Presets): save the current lighting, load a preset, delete one.
    /// Presets are the service's "profiles"; only the wording in the app differs.
    /// </summary>
    private void BuildProfileMenus()
    {
        ProfilesMenu.Items.Clear();
        // Ctrl+S itself is registered on the window (AddShortcuts); the item only shows it.
        var save = new MenuFlyoutItem { Text = "Save current lighting as preset...", Icon = new FontIcon { Glyph = "\uE74E" }, KeyboardAcceleratorTextOverride = "Ctrl+S" };
        save.Click += (_, _) => Safe("saving a preset", SaveProfileAsync);
        ProfilesMenu.Items.Add(save);
        ProfilesMenu.Items.Add(new MenuFlyoutSeparator());
        if (_profiles.Count > 0)
        {
            foreach (var name in _profiles)
            {
                var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = "\uE790" } };
                ToolTipService.SetToolTip(item, $"Load \"{name}\"");
                item.Click += (_, _) => Safe("loading a preset", () => LoadProfileAsync(name));
                ProfilesMenu.Items.Add(item);
            }
            ProfilesMenu.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutSubItem { Text = "Delete preset", Icon = new FontIcon { Glyph = "\uE74D" } };
            foreach (var name in _profiles)
            {
                var item = new MenuFlyoutItem { Text = name };
                item.Click += (_, _) => Safe("deleting a preset", () => DeleteProfileAsync(name));
                delete.Items.Add(item);
            }
            ProfilesMenu.Items.Add(delete);
        }
        else
        {
            ProfilesMenu.Items.Add(new MenuFlyoutItem { Text = "No presets yet", IsEnabled = false });
        }
        ProfilesMenu.Items.Add(new MenuFlyoutSeparator());
        var import = new MenuFlyoutItem { Text = "Import presets...", Icon = new FontIcon { Glyph = "\uE8B5" } };
        import.Click += (_, _) => Safe("importing presets", ImportProfilesAsync);
        ProfilesMenu.Items.Add(import);
        var export = new MenuFlyoutItem { Text = "Export presets...", Icon = new FontIcon { Glyph = "\uEDE1" }, IsEnabled = _profiles.Count > 0 };
        export.Click += (_, _) => Safe("exporting presets", ExportProfilesAsync);
        ProfilesMenu.Items.Add(export);

        BuildTrayMenu();
    }

    /// <summary>
    /// The tray menu, three items: Open LightSync, Presets (the saved presets, the loaded one checked) and Exit.
    /// It is a native Windows menu (H.NotifyIcon's PopupMenu mode), so it fits its items with no scrolling and
    /// the Presets submenu opens beside it. Items run through Command (what that mode invokes).
    /// </summary>
    private void BuildTrayMenu()
    {
        if (_trayMenu is not { } menu)
        {
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open LightSync",
            Command = new RelayCommand(() => OnUi(ShowWindow)),
        });
        var presets = new MenuFlyoutSubItem { Text = "Presets" };
        if (_profiles.Count == 0)
        {
            presets.Items.Add(new MenuFlyoutItem { Text = "No presets yet (File > Presets > Save)", IsEnabled = false });
        }
        foreach (var name in _profiles)
        {
            var preset = name;
            presets.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = name,
                IsChecked = name == _currentPreset,
                Command = new RelayCommand(() => OnUi(() => Safe("loading a preset", () => LoadProfileAsync(preset)))),
            });
        }
        menu.Items.Add(presets);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Exit LightSync",
            Command = new RelayCommand(() => OnUi(ExitApp)),
        });
    }

    private async Task SaveProfileAsync()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "For example: Gaming, Night, Work", MaxLength = 64 };
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Res("TextFillColorSecondaryBrush"),
            Text = "Saves the colors and effects of every device, zone and fan. Using the name of an existing preset replaces it.",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save preset",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { name, note } },
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            if (_pushTimer.IsRunning)
            {
                await PushAsync();
            }
            _profiles = await _client.ProfileAsync("save", name.Text.Trim());
            BuildProfileMenus();
            ShowPreset(name.Text.Trim());
            ShowNotice(InfoBarSeverity.Success, $"Saved \"{name.Text.Trim()}\"", "Pick it any time from File > Presets or the tray icon.");
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not save the preset", Explain(e));
        }
    }

    /// <summary>The biggest presets file read (the service takes requests up to 1 MB).</summary>
    private const long MaxPresetsFile = 900 * 1024;

    /// <summary>
    /// Save presets to a file (all, or the ones ticked): their colors and effects, and the ids of the
    /// devices, zones and fans they set (which name the hardware, like "openrgb:Razer ..."); nothing else.
    /// </summary>
    private async Task ExportProfilesAsync()
    {
        if (_profiles.Count == 0)
        {
            return;
        }
        var boxes = _profiles.Select(n => new CheckBox { Content = n, IsChecked = true }).ToList();
        var list = new StackPanel { Spacing = 2 };
        boxes.ForEach(b => list.Children.Add(b));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Export presets",
            PrimaryButtonText = "Export...",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 360,
                Children =
                {
                    new ScrollViewer { Content = list, MaxHeight = 300 },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = Res("TextFillColorSecondaryBrush"),
                        Text = "The file holds these presets' colors and effects, and the names of the devices they set (like your keyboard's model). Nothing else is in it.",
                    },
                },
            },
        };
        foreach (var b in boxes)
        {
            b.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            b.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = boxes.Any(x => x.IsChecked == true);
        }
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var names = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = names.Count == 1 ? $"LightSync preset - {names[0]}" : "LightSync presets",
        };
        picker.FileTypeChoices.Add("LightSync presets", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            var data = await _client.ExportProfilesAsync(names);
            var text = data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, text);
            ShowNotice(InfoBarSeverity.Success, names.Count == 1 ? $"Exported \"{names[0]}\"" : $"Exported {names.Count} presets", file.Name);
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not export the presets", Explain(e));
        }
    }

    /// <summary>Add the presets of a file exported from LightSync (checked by the service before any is kept).</summary>
    private async Task ImportProfilesAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        System.Text.Json.Nodes.JsonObject presets;
        try
        {
            var props = await file.GetBasicPropertiesAsync();
            if (props.Size > MaxPresetsFile)
            {
                ShowNotice(InfoBarSeverity.Error, "Could not import the presets", "That file is too big for a presets file.");
                return;
            }
            var root = System.Text.Json.Nodes.JsonNode.Parse(await Windows.Storage.FileIO.ReadTextAsync(file)) as System.Text.Json.Nodes.JsonObject;
            if (root?["presets"] is not System.Text.Json.Nodes.JsonObject p || p.Count == 0)
            {
                ShowNotice(InfoBarSeverity.Error, "Could not import the presets", "That isn't a LightSync presets file (File > Presets > Export presets makes one).");
                return;
            }
            presets = p;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException || Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not import the presets", "That file couldn't be read as a LightSync presets file.");
            return;
        }
        // Names already in use: keep both (the imported one gets a number) or replace.
        var taken = presets.Select(kv => kv.Key.Trim()).Where(_profiles.Contains).ToList();
        var replace = false;
        if (taken.Count > 0)
        {
            var ask = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Some presets have the same name",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"You already have {string.Join(", ", taken.Select(n => $"\"{n}\""))}. Replace them with the imported ones, or keep both (the imported ones get a number)?",
                },
                PrimaryButtonText = "Keep both",
                SecondaryButtonText = "Replace",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var answer = await ask.ShowAsync();
            if (answer == ContentDialogResult.None)
            {
                return;
            }
            replace = answer == ContentDialogResult.Secondary;
        }
        try
        {
            var imported = await _client.ImportProfilesAsync(presets, replace);
            await LoadProfilesAsync();
            ShowNotice(InfoBarSeverity.Success, imported.Count == 1 ? $"Imported \"{imported[0]}\"" : $"Imported {imported.Count} presets", "Pick them from File > Presets or the tray icon.");
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not import the presets", Explain(e));
        }
    }

    private async Task LoadProfileAsync(string name)
    {
        try
        {
            _pushTimer.Stop();
            _profiles = await _client.ProfileAsync("load", name);
            BuildProfileMenus();
            ShowPower(true);
            await LoadTargetStateAsync();
            ShowPreset(name);
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, $"Could not load \"{name}\"", Explain(e));
        }
    }

    private async Task DeleteProfileAsync(string name)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete preset \"{name}\"?",
            Content = "Your current lighting doesn't change.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            _profiles = await _client.ProfileAsync("delete", name);
            BuildProfileMenus();
            if (_currentPreset == name)
            {
                ShowPreset(null);
            }
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not delete the preset", Explain(e));
        }
    }

    // ---------- OpenRGB: start, stop, restart, updates ----------

    private async Task<bool> OpenRgbOperationAsync(string busyText, Func<Task> work)
    {
        if (_openRgbBusy)
        {
            return false;
        }
        _openRgbBusy = true;
        SetHealth(Health.Busy, busyText);
        try
        {
            await work();
            return true;
        }
        finally
        {
            _openRgbBusy = false;
        }
    }

    private async Task StopOpenRgbAsync()
    {
        await OpenRgbOperationAsync("Stopping OpenRGB...", async () =>
        {
            switch (await OpenRgbLauncher.StopAsync())
            {
                case ElevatedResult.Ok:
                    SetHealth(Health.Bad, "OpenRGB stopped");
                    ShowNotice(InfoBarSeverity.Informational, "OpenRGB is stopped",
                        "Your lights keep their last colors but LightSync can't change them until OpenRGB runs again.",
                        "Start OpenRGB", StartOpenRgbAndLoadAsync);
                    break;
                case ElevatedResult.Declined:
                    SetConnected();
                    ShowNotice(InfoBarSeverity.Warning, "OpenRGB is still running", "Stopping it needs administrator rights.");
                    break;
                default:
                    SetConnected();
                    ShowNotice(InfoBarSeverity.Error, "Could not stop OpenRGB", "It may have been started by another user or program.");
                    break;
            }
        });
    }

    private void StartFresh_Click(object sender, RoutedEventArgs e) => Safe("start fresh", StartFreshAsync);

    /// <summary>Forget all lighting settings and detect every device again from scratch.</summary>
    private async Task StartFreshAsync()
    {
        var openRgb = new CheckBox { Content = "Also reset OpenRGB's device settings and restart it", IsChecked = true };
        var profiles = new CheckBox { Content = "Also delete my saved presets", IsChecked = false };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Start fresh?",
            PrimaryButtonText = "Start fresh",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 440,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Every device goes back to white, and the colors you set per device, zone or fan, your groups and your fan splits are removed. "
                            + "Then LightSync looks for devices again.",
                    },
                    openRgb,
                    profiles,
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = Res("TextFillColorSecondaryBrush"),
                        Text = "Resetting OpenRGB asks for administrator rights: only administrators can change its settings folder.",
                    },
                },
            },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            _pushTimer.Stop();
            await _client.ResetAsync(profiles.IsChecked == true);
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not reset", Explain(ex));
            return;
        }
        _target = TargetItem.All(_deviceCount);
        await LoadGroupsAsync();
        await LoadProfilesAsync();
        await AfterTriggersChangedAsync(); // reset clears triggers too: stop listening, update the window
        ShowPower(true);
        if (openRgb.IsChecked == true)
        {
            await OpenRgbOperationAsync("Resetting OpenRGB...", async () =>
            {
                HideNotice();
                await AfterOpenRgbStartAsync(await OpenRgbLauncher.RestartAsync(TimeSpan.FromSeconds(30), fresh: true));
            });
        }
        else
        {
            await LoadDevicesAsync(rescan: true);
        }
        await LoadTargetStateAsync();
        if (!Notice.IsOpen)
        {
            ShowNotice(InfoBarSeverity.Success, "Fresh start done", "All devices are white again. Pick a device to give it its own color.");
        }
    }

    private async Task RestartOpenRgbAsync()
    {
        await OpenRgbOperationAsync("Restarting OpenRGB...", async () =>
        {
            HideNotice();
            await AfterOpenRgbStartAsync(await OpenRgbLauncher.RestartAsync(TimeSpan.FromSeconds(30)));
        });
    }

    /// <summary>Report how an OpenRGB (re)start went and pick up its devices.</summary>
    private async Task AfterOpenRgbStartAsync(OpenRgbStartResult result)
    {
        switch (result)
        {
            case OpenRgbStartResult.NotFound:
                SetHealth(Health.Bad, "OpenRGB not found");
                ShowNotice(InfoBarSeverity.Error, "OpenRGB was not found", "Install it from Settings > OpenRGB.", "Open OpenRGB settings", () =>
                {
                    OpenSettings(SettingsPage.OpenRgb);
                    return Task.CompletedTask;
                });
                break;
            case OpenRgbStartResult.Declined:
                SetHealth(Health.Bad, "OpenRGB not restarted");
                ShowNotice(InfoBarSeverity.Warning, "OpenRGB needs administrator rights", "Restarting OpenRGB was cancelled.", "Try again", RestartOpenRgbAsync);
                break;
            case OpenRgbStartResult.TimedOut:
                SetHealth(Health.Bad, "OpenRGB not responding");
                ShowNotice(InfoBarSeverity.Warning, "OpenRGB is taking a while", "OpenRGB started but its SDK server on port 6742 didn't answer within 30 seconds.", "Retry", RestartOpenRgbAsync);
                break;
            default:
                SetHealth(Health.Busy, "Detecting devices...");
                await Task.Delay(2500);
                await LoadDevicesAsync(rescan: true);
                await Task.Delay(6000);
                await LoadDevicesAsync(rescan: true);
                break;
        }
    }

    /// <summary>Once a day (if enabled), look for a newer OpenRGB and install it.</summary>
    private async Task AutoUpdateCheckAsync()
    {
        if (!_settings.AutoUpdateOpenRgb || _settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromDays(1))
        {
            return;
        }
        await CheckForUpdatesAsync(interactive: false);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        var exe = OpenRgbLauncher.Find();
        var installed = exe is null ? null : OpenRgbLauncher.VersionOf(exe);
        List<OpenRgbRelease> releases;
        try
        {
            releases = await OpenRgbUpdater.ListAsync(_settings.IncludePrereleases);
            _settings.LastUpdateCheck = DateTimeOffset.Now;
            _settings.Save();
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            if (interactive)
            {
                ShowNotice(InfoBarSeverity.Error, "Could not check for OpenRGB updates", e.Message);
            }
            return;
        }
        var latest = releases.FirstOrDefault();
        if (latest is null)
        {
            if (interactive)
            {
                ShowNotice(InfoBarSeverity.Warning, "No OpenRGB downloads found", "No release with a Windows 64-bit build was found.");
            }
            return;
        }
        var newer = OpenRgbVersion.IsNewer(latest.Version, installed);
        if (newer && !interactive && _settings.AutoUpdateOpenRgb)
        {
            ShowNotice(InfoBarSeverity.Informational, $"Updating OpenRGB to {latest.Version}",
                "Windows will ask for administrator rights to replace OpenRGB.");
            await InstallOpenRgbAsync(latest, null);
            return;
        }
        if (newer)
        {
            ShowNotice(InfoBarSeverity.Informational, $"OpenRGB {latest.Version} is available",
                $"You have {installed}.", "Update", () => InstallOpenRgbAsync(latest, null));
        }
        else if (interactive)
        {
            var known = OpenRgbVersion.Clean(installed) ?? installed ?? "an unknown version";
            ShowNotice(InfoBarSeverity.Success, "OpenRGB is up to date",
                OpenRgbVersion.Key(installed) is null
                    ? $"Couldn't tell which version is installed. The newest is {latest.Version}; install it from Settings > OpenRGB."
                    : $"You have {known}; the newest {(_settings.IncludePrereleases ? "" : "stable ")}version is {latest.Version}.");
        }
    }

    /// <summary>Download, install (one UAC prompt) and start a release, then reload devices.</summary>
    private async Task InstallOpenRgbAsync(OpenRgbRelease release, Action<string, double?>? report)
    {
        await OpenRgbOperationAsync($"Installing OpenRGB {release.Version}...", async () =>
        {
            // Progress can arrive late (Progress<T> posts it) or after a settings window closed: every report
            // goes through the UI thread, and one that fails is logged instead of ending the app.
            var target = report ?? ((text, _) => SetHealth(Health.Busy, text));
            report = (text, value) => OnUi(() =>
            {
                try
                {
                    target(text, value);
                }
                catch (Exception e) when (Errors.Recoverable(e))
                {
                    CrashLog.Write("install progress", e);
                }
            });
            try
            {
                report($"Downloading OpenRGB {release.Version}...", 0);
                var lastPercent = -1;
                var progress = new Progress<double>(p =>
                {
                    // One update per percent, not one per 80 KB read.
                    var percent = (int)Math.Clamp(p * 100, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        report($"Downloading OpenRGB {release.Version}... {percent}%", percent / 100.0);
                    }
                });
                var installed = OpenRgbLauncher.Find() is { } current ? OpenRgbLauncher.VersionOf(current) : null;
                var download = await OpenRgbUpdater.DownloadAsync(release, progress);
                report("Installing (Windows will ask for administrator rights)...", null);
                switch (await OpenRgbUpdater.InstallAsync(download))
                {
                    case ElevatedResult.Ok:
                        _settings.Save();
                        report($"OpenRGB {release.Version} installed. Starting it...", null);
                        ShowNotice(InfoBarSeverity.Success, $"OpenRGB {release.Version} installed",
                            "Your OpenRGB settings were kept.");
                        await AfterOpenRgbStartAsync(await OpenRgbLauncher.WaitForServerAsync(TimeSpan.FromSeconds(30)));
                        break;
                    case ElevatedResult.Declined:
                        report("Install cancelled.", null);
                        SetConnected();
                        break;
                    default:
                        report("Install failed.", null);
                        ShowNotice(InfoBarSeverity.Error, "Could not install OpenRGB", "Replacing the OpenRGB files failed, or the download changed before it could be installed. The previous version is unchanged or can be restored.");
                        await AfterOpenRgbStartAsync(await OpenRgbLauncher.EnsureRunningAsync(TimeSpan.FromSeconds(30)));
                        break;
                }
            }
            catch (Exception e) when (Errors.Expected(e))
            {
                report($"Failed: {e.Message}", null);
                ShowNotice(InfoBarSeverity.Error, "Could not update OpenRGB", e.Message);
                SetConnected();
            }
        });
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{path}\"") { UseShellExecute = true });
        }
    }

    /// <summary>Lights off after the chosen idle time; any keyboard or mouse input turns them back on.</summary>
    private async Task CheckIdleAsync()
    {
        var limit = _settings.IdleOffMinutes;
        var idle = IdleTime();
        if (!_idleOff && limit > 0 && _on && idle >= TimeSpan.FromMinutes(limit))
        {
            _idleOff = true;
            _autoOff = true;
            await SetPowerAsync(false);
        }
        else if (_idleOff && idle < TimeSpan.FromSeconds(5))
        {
            _idleOff = false;
            if (_autoOff)
            {
                _autoOff = false;
                await SetPowerAsync(true);
            }
        }
    }

    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        return NativeMethods.GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    private void OpenRgbFolder_Click(object sender, RoutedEventArgs e)
    {
        if (OpenRgbLauncher.Find() is { } exe)
        {
            OpenFolder(System.IO.Path.GetDirectoryName(exe)!);
        }
    }

    private void SettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        OpenFolder(AppSettings.Folder);
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    /// <summary>Help > About: what LightSync is, who made it, its parts and versions, your setup, where things are kept.</summary>
    private void About_Click(object sender, RoutedEventArgs e) => Safe("about", async () =>
    {
        var asm = typeof(MainWindow).Assembly;
        var appVersion = asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";
        var author = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyCompanyAttribute), false)
            .OfType<System.Reflection.AssemblyCompanyAttribute>().FirstOrDefault()?.Company is { Length: > 0 } c && c != "LightSync.App" ? c : "Rogerio Tristao Junior";
        var openRgb = OpenRgbLauncher.Find() is { } exe ? OpenRgbVersion.Clean(OpenRgbLauncher.VersionOf(exe)) ?? "installed" : "not installed";
        string webView;
        try
        {
            webView = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not installed";
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            webView = "not installed";
        }
        var service = await Task.Run(() =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessesByName("lightsyncd").FirstOrDefault();
                return p?.MainModule?.FileName is { } path ? $"running, built {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}" : "not running";
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                return "running";
            }
        });
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = Res("TextFillColorSecondaryBrush");
        TextBlock Line(string text, bool dim = false)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = caption, IsTextSelectionEnabled = true };
            if (dim)
            {
                tb.Foreground = secondary; // (a null Foreground would make the text invisible)
            }
            return tb;
        }
        StackPanel Section(string title, params UIElement[] items)
        {
            var s = new StackPanel { Spacing = 2, Margin = new Thickness(0, 12, 0, 0) };
            s.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 2) });
            foreach (var i in items)
            {
                s.Children.Add(i);
            }
            return s;
        }
        Grid Row(string label, string value)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Line(label, dim: true));
            var val = Line(value);
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
            return g;
        }
        HyperlinkButton Folder(string text, string path)
        {
            var b = new HyperlinkButton { Content = text, Padding = new Thickness(0, 2, 0, 2) };
            ToolTipService.SetToolTip(b, path);
            b.Click += (_, _) =>
            {
                Directory.CreateDirectory(path);
                OpenFolder(path);
            };
            return b;
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new Image { Source = TitleIcon.Source, Width = 48, Height = 48 });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "LightSync", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        names.Children.Add(Line($"Version {appVersion} · by {author}", dim: true));
        header.Children.Add(names);

        var on = _triggers.Count(t => t.Enabled);
        var content = new StackPanel
        {
            MinWidth = 440,
            Children =
            {
                header,
                Line("One place for all your PC lighting: every device in one list and a 3D view of your setup, 30+ effects including ones that follow the music, presets, and triggers that react to keys, clicks, the beat or the apps you run."),
                Section("Your setup",
                    Row("Devices", _devices.Count == 1 ? "1 device" : $"{_devices.Count} devices"),
                    Row("Triggers", $"{_triggers.Count} ({on} on)"),
                    Row("Presets", _profiles.Count == 0 ? "none yet" : string.Join(", ", _profiles)),
                    Row("Current preset", _currentPreset ?? "custom lighting")),
                Section("Parts and versions",
                    Row("LightSync app", $"{appVersion} (.NET {Environment.Version}, Windows App SDK)"),
                    Row("Lighting service", $"lightsyncd, {service}"),
                    Row("OpenRGB", $"{openRgb} (talks to the devices; local only, 127.0.0.1)"),
                    Row("3D preview", $"WebView2 {webView}"),
                    Row("Windows", Environment.OSVersion.Version.ToString())),
                Section("Your data",
                    Line("Everything stays on this PC, in your Windows profile. The only thing LightSync looks up online is new OpenRGB versions (Codeberg / GitLab).", dim: true),
                    Folder("Settings, presets and triggers", AppSettings.Folder),
                    Folder("Logs (crash.log) and preview data", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSync"))),
                Section("Built with",
                    Line("OpenRGB (GPL-2.0, run as a separate program), three.js (MIT) for the 3D preview, cpal and RustFFT (Apache-2.0 / MIT) for sound, tokio and serde (MIT / Apache-2.0), WinUI 3 and H.NotifyIcon.", dim: true)),
                Section("License",
                    Line($"Open source under the MIT License. © 2026 {author}. You may use, copy, change and share it, as long as the copyright and license notice stay with it. It comes with no warranty.", dim: true)),
            },
        };
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "About LightSync",
            Content = new ScrollViewer { Content = content, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    });

    // ---------- building and refreshing the controls ----------

    private void BuildEffectTiles()
    {
        // Non-sound effects in their families, each under a small title.
        var groupPanels = new Dictionary<string, Panel>();
        foreach (var group in EffectGroups.Where(g => g != Sound))
        {
            EffectTiles.Children.Add(new TextBlock
            {
                Text = group,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Res("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, EffectTiles.Children.Count == 0 ? 0 : 6, 0, 0),
            });
            var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 116, ItemHeight = 80 };
            EffectTiles.Children.Add(grid);
            groupPanels[group] = grid;
        }
        foreach (var effect in Effects)
        {
            var preview = new Border { Height = 26, CornerRadius = new CornerRadius(5) };
            _effectPreviews[effect.Id] = preview;
            var button = new Button
            {
                Width = 108,
                Height = 72,
                Padding = new Thickness(7),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        preview,
                        new TextBlock { Text = effect.Name, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) => SetEffect(effect.Id);
            _effectButtons[effect.Id] = button;
            Panel tiles = effect.Group == Sound ? SoundTiles : groupPanels.GetValueOrDefault(effect.Group) ?? groupPanels[Calm];
            tiles.Children.Add(button);
        }
    }

    private void BuildPresets()
    {
        foreach (var (name, hex) in PresetColors)
        {
            var color = ParseHex(hex)!.Value;
            var ring = new Ellipse { Width = 38, Height = 38, StrokeThickness = 2, Stroke = Res("AccentFillColorDefaultBrush"), Visibility = Visibility.Collapsed };
            var button = SwatchButton(new Grid
            {
                Children =
                {
                    ring,
                    new Ellipse { Width = 28, Height = 28, Fill = new SolidColorBrush(color), Stroke = Res("CardStrokeColorDefaultBrush"), StrokeThickness = 1 },
                },
            });
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => SetColor(color);
            _presetRings.Add((hex, ring));
            Presets.Children.Add(button);
        }

        // Last swatch: any color, from a square color picker.
        var custom = SwatchButton(new Grid
        {
            Children =
            {
                new Ellipse { Width = 28, Height = 28, Fill = HueBrush(1), Stroke = Res("CardStrokeColorDefaultBrush"), StrokeThickness = 1 },
                new FontIcon { Glyph = "\uE710", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            },
        });
        ToolTipService.SetToolTip(custom, "Custom color");
        custom.Flyout = new Flyout { Content = _customPicker, Placement = FlyoutPlacementMode.Bottom };
        Presets.Children.Add(custom);
    }

    private static Button SwatchButton(UIElement content) => new()
    {
        Width = 44,
        Height = 44,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(22),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = content,
    };

    private void ShowColor()
    {
        Swatch.Background = new SolidColorBrush(_color);
        HexText.Text = "#" + Hex;
        foreach (var (hex, ring) in _presetRings)
        {
            ring.Visibility = string.Equals(hex, Hex, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var effect in Effects)
        {
            _effectPreviews[effect.Id].Background = EffectBrush(effect.Id, _color);
        }
        RecolorAnalyzer();
    }

    /// <summary>A small picture of an effect in a color: the effect tiles and the device list use it.</summary>
    private static Brush EffectBrush(string effect, Color c) => effect switch
    {
        "breathing" => Gradient(Stops(Dim(c, 0.1), c, Dim(c, 0.1))),
        "color_wave" => Gradient(Stops(c, Dim(c, 0.15), c, Dim(c, 0.15), c)),
        "spectrum" => HueBrush(1),
        "rainbow_wave" => HueBrush(2),
        "comet" => Gradient(Stops(Dim(c, 0.05), Dim(c, 0.05), Dim(c, 0.25), c, Dim(c, 0.05))),
        "twinkle" => Gradient(Stops(Dim(c, 0.1), c, Dim(c, 0.1), Dim(c, 0.1), c, Dim(c, 0.1), c, Dim(c, 0.1))),
        "fire" => Gradient(Stops(Color.FromArgb(255, 120, 0, 0), Color.FromArgb(255, 255, 60, 0), Color.FromArgb(255, 255, 170, 20), Color.FromArgb(255, 255, 60, 0), Color.FromArgb(255, 160, 10, 0))),
        "aurora" => Gradient(Stops(Color.FromArgb(255, 0, 200, 120), Color.FromArgb(255, 0, 170, 200), Color.FromArgb(255, 120, 60, 220), Color.FromArgb(255, 0, 210, 140))),
        "gradient" => Gradient(Stops(ShiftHue(c, -0.08), c, ShiftHue(c, 0.08))),
        "heartbeat" => Gradient(Stops(Dim(c, 0.05), c, Dim(c, 0.1), Dim(c, 0.7), Dim(c, 0.05), Dim(c, 0.05))),
        "strobe" => Gradient(Stops(c, Color.FromArgb(255, 0, 0, 0), c, Color.FromArgb(255, 0, 0, 0), c)),
        "chase" => Gradient(Stops(c, Dim(c, 0.05), Dim(c, 0.05), c, Dim(c, 0.05), Dim(c, 0.05), c)),
        "rain" => Gradient(Stops(Dim(c, 0.05), c, Dim(c, 0.2), Dim(c, 0.05), Dim(c, 0.05), c, Dim(c, 0.1))),
        "candle" => Gradient(Stops(Dim(c, 0.6), c, Dim(c, 0.8), Dim(c, 0.5), c)),
        "ocean" => Gradient(Stops(Color.FromArgb(255, 0, 90, 170), Color.FromArgb(255, 0, 170, 200), Color.FromArgb(255, 0, 60, 140), Color.FromArgb(255, 40, 190, 210))),
        "police" => Gradient(Stops(Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 60, 0, 0), Color.FromArgb(255, 0, 10, 70), Color.FromArgb(255, 0, 40, 255))),
        "sound_beat" => Gradient(Stops(c, Dim(c, 0.35), Dim(c, 0.08), Dim(c, 0.04), c, Dim(c, 0.25), Dim(c, 0.05))),
        "sound_bass" => Gradient(Stops(Dim(c, 0.2), c, Dim(c, 0.5), Dim(c, 0.2), Dim(c, 0.7), c, Dim(c, 0.25))),
        "sound_spectrum" => HueBrush(1),
        "plasma" => Gradient(Stops(Color.FromArgb(255, 255, 0, 170), Color.FromArgb(255, 120, 0, 255), Color.FromArgb(255, 0, 200, 255), Color.FromArgb(255, 0, 255, 120), Color.FromArgb(255, 255, 220, 0), Color.FromArgb(255, 255, 0, 170))),
        "meteor_shower" => Gradient(Stops(Dim(c, 0.03), Dim(c, 0.1), Dim(c, 0.4), c, Color.FromArgb(255, 255, 255, 255), Dim(c, 0.03), Dim(c, 0.2), c, Dim(c, 0.03))),
        "lava_lamp" => Gradient(Stops(Dim(ShiftHue(c, -0.03), 0.15), ShiftHue(c, 0.03), Dim(c, 0.3), Dim(ShiftHue(c, -0.03), 0.12), ShiftHue(c, 0.03), Dim(c, 0.2))),
        "digital_rain" => Gradient(Stops(Dim(c, 0.03), Dim(c, 0.3), Dim(c, 0.7), Color.FromArgb(255, 220, 255, 220), Dim(c, 0.03), Dim(c, 0.4), c, Dim(c, 0.03))),
        "fireworks" => Gradient(Stops(Color.FromArgb(255, 10, 10, 20), Color.FromArgb(255, 255, 60, 60), Color.FromArgb(255, 10, 10, 20), Color.FromArgb(255, 255, 220, 60), Color.FromArgb(255, 10, 10, 20), Color.FromArgb(255, 80, 160, 255), Color.FromArgb(255, 10, 10, 20))),
        "scanner" => Gradient(Stops(Dim(c, 0.03), Dim(c, 0.03), Dim(c, 0.3), c, Dim(c, 0.3), Dim(c, 0.03), Dim(c, 0.03))),
        "lightning_storm" => Gradient(Stops(Color.FromArgb(255, 5, 10, 35), Color.FromArgb(255, 10, 15, 45), Color.FromArgb(255, 220, 230, 255), Color.FromArgb(255, 60, 70, 120), Color.FromArgb(255, 5, 10, 35))),
        "neon_pulse" => Gradient(Stops(c, Dim(c, 0.2), ShiftHue(c, 0.5), Dim(ShiftHue(c, 0.5), 0.2), c)),
        "bounce" => Gradient(Stops(Dim(c, 0.03), Dim(c, 0.03), c, Dim(c, 0.03), Dim(c, 0.03), Dim(c, 0.03))),
        "confetti" => Gradient(Stops(Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 255, 40, 120), Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 40, 220, 255), Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 255, 210, 30), Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 120, 255, 60))),
        "sunset" => Gradient(Stops(Color.FromArgb(255, 255, 190, 40), Color.FromArgb(255, 255, 110, 20), Color.FromArgb(255, 255, 40, 90), Color.FromArgb(255, 150, 30, 160), Color.FromArgb(255, 255, 110, 20))),
        "beacon" => Gradient(Stops(Dim(c, 0.04), Dim(c, 0.3), c, Dim(c, 0.3), Dim(c, 0.04), Dim(c, 0.04))),
        "sound_level" => Gradient(Stops(c, c, c, Dim(c, 0.6), Dim(c, 0.04), Dim(c, 0.04), Dim(c, 0.8), Dim(c, 0.04))),
        "sound_ripple" => Gradient(Stops(Dim(c, 0.05), c, Dim(c, 0.1), Dim(c, 0.6), Dim(c, 0.1), c, Dim(c, 0.05))),
        "sound_sparkle" => Gradient(Stops(Dim(c, 0.04), c, Dim(c, 0.04), Dim(c, 0.04), c, Dim(c, 0.04), c, Dim(c, 0.04))),
        _ => new SolidColorBrush(c),
    };

    /// <summary>The same color with its hue turned by <paramref name="turn"/> (a fraction of the color wheel).</summary>
    private static Color ShiftHue(Color c, double turn)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 0.05)
        {
            return Dim(c, turn < 0 ? 1 : 0.25); // grey: fade instead, like the service does
        }
        var h = max == r ? ((g - b) / d % 6 + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        var hue = FromHue(h / 6 + turn);
        var s = d / max;
        byte Mix(byte v) => (byte)Math.Round(max * 255 * (1 - s + s * v / 255.0));
        return Color.FromArgb(255, Mix(hue.R), Mix(hue.G), Mix(hue.B));
    }

    /// <summary>Show each row's current effect and color in the device and group lists.</summary>
    private async Task RefreshRowsAsync()
    {
        var rows = Rows.ToList();
        if (rows.Count == 0)
        {
            return;
        }
        Dictionary<string, RowState> states;
        try
        {
            states = await _client.GetTargetsAsync(rows.Select(r => r.Id));
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return; // The status pill already reports a lost connection.
        }
        foreach (var row in rows)
        {
            if (!states.TryGetValue(row.Id, out var s))
            {
                continue;
            }
            var name = Effects.FirstOrDefault(e => e.Id == s.Effect)?.Name ?? s.Effect;
            var color = ParseHex(s.Color) ?? Color.FromArgb(255, 255, 255, 255);
            if (!s.On)
            {
                row.SetState(new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)), "Off", "All lights are off");
            }
            else if (s.Mixed)
            {
                row.SetState(Gradient(Stops(color, Color.FromArgb(255, 90, 90, 90))), "Mixed", "The members of this group show different lighting");
            }
            else
            {
                var text = s.Brightness < 100 ? $"{name} · {s.Brightness}%" : name;
                var tip = $"{name}, #{s.Color.ToUpperInvariant()}, brightness {s.Brightness}%" +
                    (row.Kind is TargetKind.All or TargetKind.Group || s.Own ? "" : " (same as the device or zone above)");
                row.SetState(EffectBrush(s.Effect, color), text, tip);
            }
        }
        RefreshHome(); // Home shows what "All devices" is set to
    }

    private void ShowEffect()
    {
        foreach (var (id, button) in _effectButtons)
        {
            var selected = id == _effect;
            button.BorderBrush = selected ? Res("AccentFillColorDefaultBrush") : Res("CardStrokeColorDefaultBrush");
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }
        var effect = EffectById(_effect);
        var palette = effect.Audio && _palette != "effect";
        ColorHint.Text = palette ? "The sound colors set the colors now. Pick a color to use it instead." : "This effect uses its own colors.";
        ColorHint.Visibility = effect.UsesColor && !palette ? Visibility.Collapsed : Visibility.Visible;
        Presets.Opacity = effect.UsesColor && !palette ? 1 : 0.5;
        PalettePanel.Visibility = effect.Audio ? Visibility.Visible : Visibility.Collapsed;
        ShowPalette();
        ShowSoundOptions(effect);
        SpeedPanel.Visibility = _effect == "static" ? Visibility.Collapsed : Visibility.Visible;
        // For sound effects the slider sets how strongly they react.
        SpeedLabel.Text = effect.Audio ? "Sensitivity" : "Speed";
        ToolTipService.SetToolTip(SpeedSlider, effect.Audio ? "How strongly the lights react to quiet sound" : null);
        UpdateSoundViews();
    }

    private static Color[] Stops(params Color[] c) => c;

    private static Color Dim(Color c, double f) => Color.FromArgb(255, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static LinearGradientBrush Gradient(Color[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i < colors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop { Color = colors[i], Offset = colors.Length == 1 ? 0 : (double)i / (colors.Length - 1) });
        }
        return brush;
    }

    private static LinearGradientBrush HueBrush(int cycles)
    {
        var stops = new List<Color>();
        for (var i = 0; i <= 6 * cycles; i++)
        {
            stops.Add(FromHue(i / 6.0));
        }
        return Gradient([.. stops]);
    }

    private static Color FromHue(double h)
    {
        h = (h % 1 + 1) % 1 * 6;
        var x = (byte)(255 * (1 - Math.Abs(h % 2 - 1)));
        return (int)h switch
        {
            0 => Color.FromArgb(255, 255, x, 0),
            1 => Color.FromArgb(255, x, 255, 0),
            2 => Color.FromArgb(255, 0, 255, x),
            3 => Color.FromArgb(255, 0, x, 255),
            4 => Color.FromArgb(255, x, 0, 255),
            _ => Color.FromArgb(255, 255, 0, x),
        };
    }

    private void CreateTrayIcon(string icoPath)
    {
        _trayMenu = new MenuFlyout();
        var menu = _trayMenu;
        BuildTrayMenu();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "LightSync (click to open)",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowWindow),
        };
        if (File.Exists(icoPath))
        {
            _trayIcon.Icon = new System.Drawing.Icon(icoPath);
        }
        else
        {
            _trayIcon.IconSource = new GeneratedIconSource { Text = "L", Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        }
        _trayIcon.ForceCreate();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p)
        {
            p.Restore();
        }
        Activate();
    }

    /// <summary>Called when LightSync is started again while it already runs: show this window instead.</summary>
    public void BringToFront() => ShowWindow();

    /// <summary>Put the window where it was last time, if that place is still on a screen; else a default size.</summary>
    private void RestoreBounds()
    {
        if (_settings.WindowBounds is [var x, var y, var w, var h] && w >= 640 && h >= 480)
        {
            var rect = new Windows.Graphics.RectInt32(x, y, w, h);
            if (Microsoft.UI.Windowing.DisplayArea.GetFromRect(rect, Microsoft.UI.Windowing.DisplayAreaFallback.None) is not null)
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
    }

    private void SaveBounds()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored } && AppWindow.IsVisible)
        {
            _settings.WindowBounds = [AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height];
            _settings.Save();
        }
    }

    private void ExitApp()
    {
        SaveBounds();
        _exiting = true;
        foreach (var timer in new[] { _statusTimer, _idleTimer, _previewTimer, _pushTimer, _analyzerTimer, _inputTimer })
        {
            timer.Stop();
        }
        // Stop listening to the keyboard and mouse before anything else goes away.
        _input.Dispose();
        _preview.Shutdown();
        // Close LightSync entirely: the lighting service saves and exits too (the lights keep their
        // last colors; OpenRGB keeps running, it needs administrator rights to stop).
        try
        {
            Task.Run(() => _client.ShutdownAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Not running, or an older service: nothing to stop.
        }
        CrashLog.Notify = null;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            _trayIcon?.Dispose();
            CloseSettingsWindows();
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            CrashLog.Write("exit", e);
        }
        Close();
        Application.Current.Exit();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void SetConnected() => SetHealth(Health.Good, "Connected");

    /// <summary>The preset loaded (or saved) last, shown in the status bar; null once something is changed by hand.</summary>
    private string? _currentPreset;

    private void ShowPreset(string? name)
    {
        _currentPreset = name;
        _settings.CurrentPreset = name;
        _settings.Save();
        FooterPreset.Text = name is null ? "Preset: Custom" : $"Preset: {name}";
        BuildTrayMenu(); // the tray checks the current preset
        RefreshHome();
    }

    private void ShowDeviceCount() =>
        FooterDevices.Text = _deviceCount switch { 0 => "No devices", 1 => "1 device", _ => $"{_deviceCount} devices" };

    private void SetHealth(Health health, string text)
    {
        ShowDeviceCount();
        StatusDot.Fill = Res(health switch
        {
            Health.Good => "SystemFillColorSuccessBrush",
            Health.Busy => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush",
        });
        StatusText.Text = text;
    }

    private void ShowEmpty(string text, bool hiddenOnly = false)
    {
        _deviceCount = 0;
        _emptyShowsHidden = hiddenOnly;
        EmptyButton.Content = hiddenOnly ? "Show hidden devices" : "Start OpenRGB";
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
        TargetList.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message, string? actionText = null, Func<Task>? action = null)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message;
        _noticeAction = action;
        NoticeAction.Content = actionText;
        NoticeAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Notice.IsOpen = true;
    }

    private void HideNotice() => Notice.IsOpen = false;

    private static string Explain(Exception e) => e switch
    {
        TimeoutException => "The LightSync service is not responding.",
        LightSyncException l when l.Message.Contains("not reachable") || l.Message.Contains("timed out") || l.Message.Contains("os error") =>
            "OpenRGB isn't running or its SDK server is off.",
        _ => e.Message,
    };

    private static Color? ParseHex(string text)
    {
        var h = text.Trim().TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return null;
        }
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ---------- event handlers ----------

    private void Brightness_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (BrightnessValue is not null)
        {
            BrightnessValue.Text = $"{(int)e.NewValue}%";
        }
        QueuePush();
    }

    private void Speed_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SpeedValue is not null)
        {
            SpeedValue.Text = $"{(int)e.NewValue}%";
        }
        QueuePush();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Safe("rescan", () => LoadDevicesAsync(rescan: true));

    private void StartOpenRgb_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyShowsHidden)
        {
            _showHiddenInSettings = true;
            OpenSettings(SettingsPage.Devices);
            return;
        }
        Safe("starting OpenRGB", StartOpenRgbAndLoadAsync);
    }

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (_noticeAction is { } action)
        {
            Safe(Notice.Title ?? "notice", action);
        }
    }
}

// Minimal ICommand for the tray icon's left click.
internal sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        try
        {
            action();
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            CrashLog.Report("tray icon", e);
        }
    }
}

public enum TargetKind { All, Device, Zone, Fan, Group }

/// <summary>One row of the device / group lists: something the controls can be pointed at.</summary>
public sealed class TargetItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The row's current lighting: a small swatch and the effect name (empty until known).</summary>
    public Brush? StateBrush { get; private set; }
    public string StateText { get; private set; } = "";
    public string StateTip { get; private set; } = "";
    public Visibility StateVisibility => StateBrush is null ? Visibility.Collapsed : Visibility.Visible;

    public void SetState(Brush brush, string text, string tip)
    {
        StateBrush = brush;
        StateText = text;
        StateTip = tip;
        foreach (var p in new[] { nameof(StateBrush), nameof(StateText), nameof(StateTip), nameof(StateVisibility) })
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
        }
    }

    public required string Id { get; init; }
    public required TargetKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";

    /// <summary>What screen readers say for the row (otherwise the type name).</summary>
    public override string ToString() => Title;
    public string Glyph { get; init; } = "\uE82F";
    public int Level { get; init; }
    public string DeviceName { get; init; } = "";
    public ZoneInfo? Zone { get; init; }
    public GroupInfo? Group { get; init; }

    public Thickness Indent => new(Level * 18.0, 0, 0, 0);
    public double IconSize => Level == 0 ? 28 : 22;
    public double GlyphSize => Level == 0 ? 14 : 11;

    /// <summary>Name with its device, e.g. "Gigabyte X870 / ARGB_V2_1 / Fan 2".</summary>
    public string LongName { get; init; } = "";

    /// <summary>The device this row belongs to (itself for a device row); empty for "All devices" and groups.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Device rows (hide the device) and zone rows (hide just that zone) get the small "hide" button.</summary>
    public Visibility HideVisibility => Kind is TargetKind.Device or TargetKind.Zone ? Visibility.Visible : Visibility.Collapsed;

    public static TargetItem All(int devices) => new()
    {
        Id = "all",
        Kind = TargetKind.All,
        Title = "All devices",
        Subtitle = devices == 1 ? "1 device" : $"{devices} devices",
        Glyph = "\uE71D",
        LongName = "All devices",
    };

    public static TargetItem ForGroup(GroupInfo g) => new()
    {
        Id = $"group:{g.Id}",
        Kind = TargetKind.Group,
        Title = g.Name,
        Subtitle = g.Members.Count == 1 ? "1 member" : $"{g.Members.Count} members",
        Glyph = "\uE902",
        Group = g,
        LongName = g.Name,
    };

    /// <summary>All devices, then each device with its zones and fans indented below it.</summary>
    public static List<TargetItem> FromDevices(List<DeviceInfo> devices)
    {
        var list = new List<TargetItem> { All(devices.Count) };
        foreach (var d in devices)
        {
            // No LED counts in the list (Settings > Devices still shows them).
            list.Add(new TargetItem { Id = d.Id, Kind = TargetKind.Device, Title = d.Name, Subtitle = $"{d.KindLabel} · {d.Provider}", Glyph = d.Glyph, LongName = d.Name, DeviceId = d.Id });
            var zones = (d.Zones ?? []).Where(z => z.Leds > 0 || z.Resizable).ToList();
            // A device with one plain zone has nothing more to pick.
            if (zones.Count == 1 && zones[0] is { Resizable: false, Fans.Count: 0 })
            {
                continue;
            }
            foreach (var z in zones)
            {
                var sub = z.Fans.Count > 0 ? (z.Fans.Count == 1 ? "1 fan" : $"{z.Fans.Count} fans") : z.Resizable ? "Addressable header" : "Zone";
                list.Add(new TargetItem
                {
                    Id = z.Id, Kind = TargetKind.Zone, Title = z.Name, Subtitle = sub, Glyph = "\uE81E", Level = 1,
                    DeviceName = d.Name, Zone = z, LongName = $"{d.Name} / {z.Name}", DeviceId = d.Id,
                });
                foreach (var f in z.Fans)
                {
                    list.Add(new TargetItem
                    {
                        Id = f.Id, Kind = TargetKind.Fan, Title = f.Name, Subtitle = $"Fan on {z.Name}", Glyph = "\uE706", Level = 2,
                        DeviceName = d.Name, Zone = z, LongName = $"{d.Name} / {z.Name} / {f.Name}", DeviceId = d.Id,
                    });
                }
            }
        }
        return list;
    }
}

/// <summary>A titled part of the device list ("Groups", "Devices"), for its grouped view.</summary>
public sealed class TargetSection(string title, IEnumerable<TargetItem> items) : List<TargetItem>(items)
{
    public string Title { get; } = title;
}

/// <summary>"Start with Windows": a per-user Run entry that starts LightSync in the tray.</summary>
public static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "LightSync";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(Name, Command);
            }
            else if (key.GetValue(Name) is not null)
            {
                key.DeleteValue(Name);
            }
            return true;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return false;
        }
    }
}
