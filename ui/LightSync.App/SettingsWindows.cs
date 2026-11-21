using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightSync.App;

/// <summary>The categories under the Settings menu; each opens its own small window.</summary>
public enum SettingsPage { General, Power, OpenRgb, Devices, Triggers }

/// <summary>Settings > General / Power / OpenRGB / Devices, keyboard shortcuts, and the Help menu's update check.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<SettingsPage, SettingsWindow> _settingsWindows = [];

    private static readonly (string Label, int Minutes)[] IdleChoices =
        [("Never", 0), ("After 5 minutes", 5), ("After 10 minutes", 10), ("After 15 minutes", 15), ("After 30 minutes", 30), ("After 1 hour", 60)];

    private void SettingsGeneral_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.General);

    private void SettingsPower_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.Power);

    private void SettingsOpenRgb_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.OpenRgb);

    private void SettingsDevices_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.Devices);

    private void OpenRgbCheck_Click(object sender, RoutedEventArgs e) => Safe("update check", () => CheckForUpdatesAsync(interactive: true));

    /// <summary>Open (or bring forward) the window for one settings category.</summary>
    private void OpenSettings(SettingsPage page)
    {
        try
        {
            if (_exiting)
            {
                return;
            }
            if (_settingsWindows.TryGetValue(page, out var open) && !open.IsClosed)
            {
                open.Activate();
                if (page == SettingsPage.Devices)
                {
                    open.Refresh();
                }
                return;
            }
            var window = page switch
            {
                SettingsPage.General => new SettingsWindow(this, "General", "\uE713", "Startup and the main window.", BuildGeneralAsync),
                SettingsPage.Power => new SettingsWindow(this, "Power", "\uE7E8", "When LightSync turns your lights off by itself.", BuildPowerAsync),
                SettingsPage.OpenRgb => new SettingsWindow(this, "OpenRGB", "\uE950", "The program LightSync uses to reach your devices.", BuildOpenRgbAsync, height: 760),
                _ => new SettingsWindow(this, "Devices", "\uE772", "Fans on addressable headers, groups and hidden devices.", BuildDevicesAsync, height: 700),
            };
            _settingsWindows[page] = window;
            window.Closed += (_, _) =>
            {
                if (_settingsWindows.TryGetValue(page, out var w) && w == window)
                {
                    _settingsWindows.Remove(page);
                }
            };
            window.Activate();
            window.Refresh();
        }
        catch (Exception e)
        {
            CrashLog.Report("opening settings", e);
        }
    }

    /// <summary>Rebuild a settings window that is open, after what it shows changed.</summary>
    private void RefreshSettingsWindow(SettingsPage page)
    {
        if (_settingsWindows.TryGetValue(page, out var window))
        {
            window.Refresh();
        }
    }

    private void CloseSettingsWindows()
    {
        foreach (var window in _settingsWindows.Values.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception e)
            {
                CrashLog.Write("closing settings", e);
            }
        }
    }

    /// <summary>
    /// Window-wide shortcuts. Registered here rather than on the menu items, so they work before a menu
    /// was ever opened; the menu items only show them.
    /// </summary>
    private void AddShortcuts()
    {
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += async (_, args) =>
            {
                args.Handled = true;
                try
                {
                    await action();
                }
                catch (Exception e)
                {
                    // E.g. a dialog is already open: the shortcut just does nothing.
                    CrashLog.Write($"shortcut {key}", e);
                }
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () => LoadDevicesAsync(rescan: true));
        Add(VirtualKey.Q, VirtualKeyModifiers.Control, () =>
        {
            ExitApp();
            return Task.CompletedTask;
        });
        Add(VirtualKey.S, VirtualKeyModifiers.Control, SaveProfileAsync);
        Add(VirtualKey.L, VirtualKeyModifiers.Control, TogglePowerAsync);
        Add(VirtualKey.G, VirtualKeyModifiers.Control, () => EditGroupAsync(null));
    }

    // ---------- Settings > General ----------

    private Task BuildGeneralAsync(SettingsWindow w)
    {
        var startup = SettingsUi.Toggle(StartupEntry.IsEnabled());
        startup.Toggled += (_, _) =>
        {
            if (!StartupEntry.Set(startup.IsOn))
            {
                startup.IsOn = StartupEntry.IsEnabled();
            }
        };
        var reset = new Button { Content = "Reset layout" };
        reset.Click += (_, _) =>
        {
            _preview.ResetLayout();
            HighlightPreview();
        };

        w.Add(SettingsUi.Section("Startup"));
        w.Add(SettingsUi.Card("Start with Windows",
            "LightSync starts in the tray when you sign in. With \"Start without a permission prompt\" on (Settings > OpenRGB), there's no administrator prompt either.", startup));
        w.Add(SettingsUi.Section("Preview"));
        w.Add(SettingsUi.Card("Preview layout", "Put every part, the case and the desk back in their default places. Use \"Arrange\" above the preview to move parts.", reset));
        return Task.CompletedTask;
    }

    // ---------- Settings > Power ----------

    private Task BuildPowerAsync(SettingsWindow w)
    {
        var sleep = SettingsUi.Toggle(_settings.LightsOffOnSleep);
        var lockOff = SettingsUi.Toggle(_settings.LightsOffOnLock);
        var idle = new ComboBox { MinWidth = 160, ItemsSource = IdleChoices.Select(c => c.Label).ToList() };
        idle.SelectedIndex = Math.Max(0, Array.FindIndex(IdleChoices, c => c.Minutes == _settings.IdleOffMinutes));
        sleep.Toggled += (_, _) => { _settings.LightsOffOnSleep = sleep.IsOn; _settings.Save(); };
        lockOff.Toggled += (_, _) => { _settings.LightsOffOnLock = lockOff.IsOn; _settings.Save(); };
        idle.SelectionChanged += (_, _) =>
        {
            _settings.IdleOffMinutes = IdleChoices[Math.Max(0, idle.SelectedIndex)].Minutes;
            _settings.Save();
        };

        w.Add(SettingsUi.Section("Turn the lights off"));
        w.Add(SettingsUi.Card("While the PC sleeps", "They come back on when it wakes up.", sleep));
        w.Add(SettingsUi.Card("While the PC is locked", "They come back on when you unlock it.", lockOff));
        w.Add(SettingsUi.Card("When you're away", "No keyboard or mouse input for a while. Any input turns them back on.", idle));
        w.Add(SettingsUi.Note("You can always turn every light off or on with the switch above the preview (Ctrl+L)."));
        return Task.CompletedTask;
    }

    // ---------- Settings > OpenRGB ----------

    /// <summary>OpenRGB status, start / stop / restart, versions and updates, no-prompt start, reset.</summary>
    private async Task BuildOpenRgbAsync(SettingsWindow w)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = SettingsUi.Style("BodyStrongTextBlockStyle") };
        var location = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = SettingsUi.Style("CaptionTextBlockStyle"), Foreground = Res("TextFillColorSecondaryBrush") };
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = SettingsUi.Style("CaptionTextBlockStyle") };
        var progress = new ProgressBar { Visibility = Visibility.Collapsed };
        var versions = new ComboBox { PlaceholderText = "Loading versions...", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var install = new Button { Content = "Install", IsEnabled = false, Style = SettingsUi.Style("AccentButtonStyle") };

        var start = new Button { Content = "Start" };
        var stop = new Button { Content = "Stop" };
        var restart = new Button { Content = "Restart" };
        var auto = SettingsUi.Toggle(_settings.AutoUpdateOpenRgb);
        var pre = SettingsUi.Toggle(_settings.IncludePrereleases);

        var check = new Button { Content = "Check now" };
        var noPrompt = SettingsUi.Toggle(await OpenRgbLauncher.NoPromptProgramAsync() is not null);
        var reset = new Button { Content = "Reset OpenRGB..." };
        var folder = new HyperlinkButton { Content = "Open the OpenRGB folder", Padding = new Thickness(0, 4, 0, 4) };
        var syncing = false;


        async Task RefreshStatusAsync()
        {
            var exe = OpenRgbLauncher.Find();
            var up = await OpenRgbLauncher.IsServerUpAsync();
            if (w.IsClosed)
            {
                return;
            }
            var version = exe is null ? null : OpenRgbLauncher.VersionOf(exe);
            status.Text = exe is null
                ? "OpenRGB is not installed. Pick a version below and install it."
                : $"{(up ? "Running" : OpenRgbLauncher.IsProcessRunning() ? "Starting (server not answering yet)" : "Stopped")} - version {OpenRgbVersion.Clean(version) ?? version ?? "unknown"}";
            location.Text = exe ?? "";
            start.IsEnabled = !up && exe is not null && !_openRgbBusy;
            stop.IsEnabled = OpenRgbLauncher.IsProcessRunning() && !_openRgbBusy;
            restart.IsEnabled = exe is not null && !_openRgbBusy;
            reset.IsEnabled = exe is not null && !_openRgbBusy;
            folder.IsEnabled = exe is not null;

        }

        // Install / restore progress. It can be reported from any thread and after this window closed
        // (an install keeps going): it is applied on this window's UI thread, and only while it is open.
        void Report(string text, double? value) => w.OnUi(() =>
        {
            progressText.Text = text;
            progress.Visibility = Visibility.Visible;
            if (value is { } v)
            {
                progress.IsIndeterminate = false;
                progress.Value = Math.Clamp(v * 100, 0, 100);
            }
            else
            {
                progress.IsIndeterminate = true;
            }
        });

        async Task LoadVersionsAsync()
        {
            versions.IsEnabled = false;
            install.IsEnabled = false;
            versions.ItemsSource = null;
            versions.PlaceholderText = "Loading versions...";
            try
            {
                var list = await OpenRgbUpdater.ListAsync(_settings.IncludePrereleases);
                if (w.IsClosed)
                {
                    return;
                }
                versions.ItemsSource = list;
                versions.SelectedIndex = list.Count > 0 ? 0 : -1;
                versions.PlaceholderText = list.Count > 0 ? "Pick a version" : "No Windows downloads found";
                versions.IsEnabled = install.IsEnabled = list.Count > 0;
            }
            catch (Exception e)
            {
                if (!w.IsClosed)
                {
                    versions.PlaceholderText = "Couldn't load versions";
                    progressText.Text = e.Message;
                }
            }
        }

        start.Click += (_, _) => w.Run("starting OpenRGB", async () =>
        {
            await OpenRgbOperationAsync("Starting OpenRGB...", async () => await AfterOpenRgbStartAsync(await OpenRgbLauncher.EnsureRunningAsync(TimeSpan.FromSeconds(30))));
            await RefreshStatusAsync();
        });
        stop.Click += (_, _) => w.Run("stopping OpenRGB", async () => { await StopOpenRgbAsync(); await RefreshStatusAsync(); });
        restart.Click += (_, _) => w.Run("restarting OpenRGB", async () => { await RestartOpenRgbAsync(); await RefreshStatusAsync(); });
        install.Click += (_, _) => w.Run("installing OpenRGB", async () =>
        {
            if (versions.SelectedItem is OpenRgbRelease r)
            {
                install.IsEnabled = false;
                try
                {
                    await InstallOpenRgbAsync(r, Report);
                }
                finally
                {
                    w.OnUi(() => install.IsEnabled = true);
                }
                await RefreshStatusAsync();
            }
        });
        auto.Toggled += (_, _) => { _settings.AutoUpdateOpenRgb = auto.IsOn; _settings.Save(); };
        pre.Toggled += (_, _) => w.Run("loading OpenRGB versions", async () =>
        {
            _settings.IncludePrereleases = pre.IsOn;
            _settings.Save();
            await LoadVersionsAsync();
        });
        check.Click += (_, _) => w.Run("update check", async () =>
        {
            check.IsEnabled = false;
            try
            {
                await CheckForUpdatesAsync(interactive: true);
            }
            finally
            {
                w.OnUi(() => check.IsEnabled = true);
            }
            await RefreshStatusAsync();
        });
        noPrompt.Toggled += (_, _) => w.Run("start without a prompt", async () =>
        {
            if (syncing)
            {
                return;
            }
            noPrompt.IsEnabled = false;
            var wanted = noPrompt.IsOn;
            var result = await OpenRgbLauncher.SetNoPromptAsync(wanted);
            var actual = await OpenRgbLauncher.NoPromptProgramAsync() is not null;
            if (result == ElevatedResult.Failed && wanted)
            {
                ShowNotice(InfoBarSeverity.Error, "Could not set that up", "Windows didn't create the OpenRGB tasks. Is OpenRGB installed?");
            }
            if (w.IsClosed)
            {
                return;
            }
            if (actual != noPrompt.IsOn)
            {
                syncing = true;
                noPrompt.IsOn = actual;
                syncing = false;
            }
            noPrompt.IsEnabled = true;
        });
        reset.Click += (_, _) => w.Run("resetting OpenRGB", async () =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = w.Root,
                Title = "Reset OpenRGB?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"OpenRGB is stopped, its settings folder ({OpenRgbLauncher.ConfigDir}) is deleted and it starts again, detecting every device from scratch. "
                        + "Windows asks for administrator rights. LightSync's own colors, groups and presets are kept.",
                },
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            Report("Resetting OpenRGB (Windows will ask for administrator rights)...", null);
            var done = await OpenRgbOperationAsync("Resetting OpenRGB...", async () =>
            {
                HideNotice();
                await AfterOpenRgbStartAsync(await OpenRgbLauncher.RestartAsync(TimeSpan.FromSeconds(30), fresh: true));
            });
            Report(done ? "Reset done." : "Another OpenRGB task is still running; try again in a moment.", 1);
            await RefreshStatusAsync();
        });
        folder.Click += (_, _) =>
        {
            if (OpenRgbLauncher.Find() is { } exe)
            {
                OpenFolder(System.IO.Path.GetDirectoryName(exe)!);
            }
        };

        var versionRow = new Grid { ColumnSpacing = 8 };
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versionRow.Children.Add(versions);
        Grid.SetColumn(install, 1);
        versionRow.Children.Add(install);

        w.Add(SettingsUi.Section("Status"));
        w.Add(SettingsUi.Panel(status, location, SettingsUi.Row(start, stop, restart), folder));
        w.Add(SettingsUi.Section("Version"));
        w.Add(SettingsUi.Panel(versionRow, progressText, progress));
        w.Add(SettingsUi.Card("Include pre-releases", "Also offer test versions of OpenRGB.", pre));
        w.Add(SettingsUi.Section("Updates"));
        w.Add(SettingsUi.Card("Update automatically", "Install new OpenRGB versions when LightSync starts (checked once a day).", auto));
        w.Add(SettingsUi.Card("Check for updates", "Look for a newer OpenRGB now.", check));
        w.Add(SettingsUi.Section("Permissions"));
        w.Add(SettingsUi.Card("Start without a permission prompt",
            "OpenRGB needs administrator rights. Turning this on asks once, then LightSync starts, stops and restarts it without asking.", noPrompt));
        w.Add(SettingsUi.Section("Troubleshooting"));
        w.Add(SettingsUi.Card("Reset OpenRGB's configuration",
            "Deletes OpenRGB's own settings (device sizes, profiles, plugins) and restarts it, so it detects every device from scratch.", reset));
        w.Add(SettingsUi.Note($"Installed versions go to {OpenRgbLauncher.ManagedDir}. OpenRGB runs as administrator, so starting, stopping and installing each ask Windows for permission. Your OpenRGB settings are kept."));

        await RefreshStatusAsync();
        w.Run("loading OpenRGB versions", LoadVersionsAsync);
        w.StartTimer(TimeSpan.FromSeconds(3), async () =>
        {
            if (!_openRgbBusy)
            {
                await RefreshStatusAsync();
            }
        });
    }

    // ---------- Settings > Devices ----------

    private Task BuildDevicesAsync(SettingsWindow w)
    {
        w.Add(SettingsUi.Section("Fan layouts"));
        w.Add(SettingsUi.Note("Fans chained on one addressable (ARGB) header share one long LED strip. Split a header into fans to light each fan on its own."));
        var headers = _devices.SelectMany(d => (d.Zones ?? []).Where(z => z.Resizable).Select(z => (Device: d, Zone: z))).ToList();
        if (headers.Count == 0)
        {
            w.Add(SettingsUi.Note(_devices.Count == 0
                ? "No devices yet. Start OpenRGB and rescan (F5)."
                : "None of your devices has an addressable header."));
        }
        foreach (var (device, zone) in headers)
        {
            var change = new Button { Content = zone.Fans.Count > 0 ? "Change fans..." : "Split into fans..." };
            change.Click += (_, _) => w.Run("fan layout", () => EditFansAsync(zone, selectAfter: false, w.Root));
            var detail = zone.Fans.Count > 0
                ? $"{device.Name} · {zone.Fans.Count} fans, {zone.Leds / (uint)zone.Fans.Count} LEDs each"
                : $"{device.Name} · one strip, {zone.Leds} LEDs";
            w.Add(SettingsUi.Card(zone.Name, detail, change));
        }

        w.Add(SettingsUi.Section("Groups"));
        w.Add(SettingsUi.Note("Groups are listed at the top of the Devices list; select one to light all its members at once. You can also Shift+click parts in the preview to change several at a time."));
        foreach (var group in _groups)
        {
            var edit = new Button { Content = "Edit..." };
            edit.Click += (_, _) => w.Run("editing a group", () => EditGroupAsync(group, w.Root));
            var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
            ToolTipService.SetToolTip(delete, "Delete group");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete {group.Name}");
            delete.Click += (_, _) => w.Run("deleting a group", () => DeleteGroupAsync(group, w.Root));
            var members = string.Join(", ", group.Members.Select(NameOf));
            w.Add(SettingsUi.Card(group.Name, $"{group.Members.Count} member{(group.Members.Count == 1 ? "" : "s")}: {members}", SettingsUi.Row(edit, delete)));
        }
        if (_groups.Count == 0)
        {
            w.Add(SettingsUi.Note("No groups yet."));
        }
        var create = new Button { Content = "New group...", Style = SettingsUi.Style("AccentButtonStyle"), Margin = new Thickness(0, 8, 0, 0) };
        create.Click += (_, _) => w.Run("new group", () => EditGroupAsync(null, w.Root));
        w.Add(create);

        AddHiddenDevicesSection(w);
        AddDiagnosticsSection(w);
        return Task.CompletedTask;
    }

    /// <summary>Settings > Devices: per device, how LightSync drives it and how its last write went (RPC "diagnostics"), on request.</summary>
    private void AddDiagnosticsSection(SettingsWindow w)
    {
        w.Add(SettingsUi.Section("Diagnostics"));
        var results = new StackPanel { Spacing = 4 };
        var load = new Button { Content = "Show diagnostics" };
        load.Click += (_, _) => w.Run("diagnostics", async () =>
        {
            load.IsEnabled = false;
            List<System.Text.Json.Nodes.JsonObject> rows;
            try
            {
                rows = await _client.DiagnosticsAsync();
            }
            catch (LightSyncException e) when (e.IsUnknownMethod)
            {
                if (!w.IsClosed)
                {
                    results.Children.Clear();
                    results.Children.Add(SettingsUi.Note("Diagnostics need a newer LightSync service (lightsyncd)."));
                    load.IsEnabled = true;
                }
                return;
            }
            catch (Exception e)
            {
                if (!w.IsClosed)
                {
                    results.Children.Clear();
                    results.Children.Add(SettingsUi.Note($"Couldn't get diagnostics: {Explain(e)}"));
                    load.IsEnabled = true;
                }
                return;
            }
            if (w.IsClosed)
            {
                return;
            }
            load.Content = "Refresh";
            load.IsEnabled = true;
            results.Children.Clear();
            if (rows.Count == 0)
            {
                results.Children.Add(SettingsUi.Note("No devices."));
            }
            foreach (var r in rows)
            {
                string S(string key) => r[key]?.ToString() ?? "";
                var drive = S("drive") switch
                {
                    "direct" => "Direct (every LED, live)",
                    "static" => "Static (one color at a time)",
                    "none" => "Not driven",
                    "hidden" => "Hidden (not controlled)",
                    "chroma" => "Razer Chroma",
                    var d => d,
                };
                var lines = new List<string> { $"{drive} · mode {S("mode")} · {S("leds")} LEDs (controller {S("controller_leds")}) · {S("provider")} #{S("index")}" };
                lines.Add(r["last_write_ms_ago"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var ms)
                    ? $"Last write {(ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.#} s")} ago" : "Not written yet");
                if (S("last_error") is { Length: > 0 } err)
                {
                    lines.Add($"Last error: {err}");
                }
                if (S("reason") is { Length: > 0 } reason)
                {
                    lines.Add(reason);
                }
                results.Children.Add(SettingsUi.Panel(
                    new TextBlock { Text = S("name") is { Length: > 0 } n ? n : S("id"), Style = SettingsUi.Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = string.Join("\n", lines), Style = SettingsUi.Style("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }));
            }
        });
        w.Add(SettingsUi.Card("Device diagnostics", "How LightSync drives each device and how its last update went. Useful when a device doesn't change color.", load));
        w.Add(results);
    }
}

/// <summary>
/// One settings category in its own small window, with the same layout for every category:
/// icon, title and summary on top, the settings as cards in the middle, Close at the bottom.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 4, Padding = new Thickness(0, 0, 16, 8) };
    private readonly Func<SettingsWindow, Task> _build;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _building;

    public SettingsWindow(Window owner, string title, string glyph, string summary, Func<SettingsWindow, Task> build, int width = 560, int height = 600)
    {
        _build = build;
        Title = $"{title} settings - LightSync";
        SystemBackdrop = new MicaBackdrop();

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, Style = SettingsUi.Style("SubtitleTextBlockStyle") });
        titles.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Style = SettingsUi.Style("CaptionTextBlockStyle"), Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var close = new Button { Content = "Close", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var scroller = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Grid { Padding = new Thickness(24, 20, 24, 20), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(escape);
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Content = root;

        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(ico))
        {
            AppWindow.SetIcon(ico);
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        // Same size on every screen: scale by the owner's DPI, and open centered over it.
        var scale = owner.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var size = new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        size.Width = Math.Min(size.Width, area.Width);
        size.Height = Math.Min(size.Height, area.Height);
        var x = owner.AppWindow.Position.X + (owner.AppWindow.Size.Width - size.Width) / 2;
        var y = owner.AppWindow.Position.Y + (owner.AppWindow.Size.Height - size.Height) / 2;
        x = Math.Clamp(x, area.X, area.X + area.Width - size.Width);
        y = Math.Clamp(y, area.Y, area.Y + area.Height - size.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, size.Width, size.Height));

        _queue = DispatcherQueue;
        Closed += (_, _) =>
        {
            IsClosed = true;
            _timer?.Stop();
        };
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    private bool _rebuildAgain;

    /// <summary>
    /// The window was closed. Its controls must not be touched any more (doing so can crash the whole app),
    /// so work that outlives it (an install, a download) checks this, or goes through <see cref="OnUi"/>.
    /// </summary>
    public bool IsClosed { get; private set; }

    /// <summary>For dialogs shown over this window.</summary>
    public XamlRoot Root => Content.XamlRoot;

    public void Add(UIElement element)
    {
        if (!IsClosed)
        {
            _body.Children.Add(element);
        }
    }

    /// <summary>Change this window's controls from any thread: on its UI thread, and only while it is open.</summary>
    public void OnUi(Action action)
    {
        void Do()
        {
            if (IsClosed)
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception e)
            {
                CrashLog.Write($"{Title}: updating the window", e);
            }
        }
        if (IsClosed)
        {
            return;
        }
        if (_queue.HasThreadAccess)
        {
            Do();
        }
        else
        {
            _queue.TryEnqueue(Do);
        }
    }

    /// <summary>Run a handler of this window (click, toggle, timer): skipped once closed, and nothing it throws ends the app.</summary>
    public async void Run(string where, Func<Task> work)
    {
        if (IsClosed)
        {
            return;
        }
        try
        {
            await work();
        }
        catch (Exception e)
        {
            if (IsClosed)
            {
                CrashLog.Write($"{where} (settings window closed)", e);
            }
            else
            {
                CrashLog.Report(where, e);
            }
        }
    }

    /// <summary>Run <paramref name="tick"/> every <paramref name="interval"/> while the window is open.</summary>
    public void StartTimer(TimeSpan interval, Func<Task> tick)
    {
        if (IsClosed)
        {
            return;
        }
        _timer?.Stop();
        _timer = _queue.CreateTimer();
        _timer.Interval = interval;
        _timer.Tick += (_, _) => Run($"{Title} timer", tick);
        _timer.Start();
    }

    /// <summary>Build (or rebuild) the settings shown, safely.</summary>
    public void Refresh() => Run($"{Title}: building", RefreshAsync);

    /// <summary>Build (or rebuild) the settings shown. A request while building rebuilds once more afterwards.</summary>
    public async Task RefreshAsync()
    {
        if (_building)
        {
            _rebuildAgain = true;
            return;
        }
        _building = true;
        try
        {
            do
            {
                _rebuildAgain = false;
                if (IsClosed)
                {
                    return;
                }
                _timer?.Stop();
                _body.Children.Clear();
                await _build(this);
            }
            while (_rebuildAgain);
        }
        finally
        {
            _building = false;
        }
    }
}

/// <summary>The building blocks every settings window uses, so they all look alike.</summary>
internal static class SettingsUi
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static ToggleSwitch Toggle(bool on) => new() { IsOn = on, OnContent = "On", OffContent = "Off", MinWidth = 0 };

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = Style("BodyStrongTextBlockStyle"),
        Margin = new Thickness(0, 14, 0, 4),
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = Brush("TextFillColorSecondaryBrush"),
        Margin = new Thickness(2, 2, 2, 6),
    };

    public static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items)
        {
            row.Children.Add(i);
        }
        return row;
    }

    private static Border Frame(UIElement child) => new()
    {
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 12, 16, 12),
        Child = child,
    };

    /// <summary>A card with free content stacked inside.</summary>
    public static Border Panel(params UIElement[] items)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var i in items)
        {
            stack.Children.Add(i);
        }
        return Frame(stack);
    }

    /// <summary>A setting: title and explanation on the left, its control on the right.</summary>
    public static Border Card(string title, string detail, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Style = Style("CaptionTextBlockStyle"),
            Foreground = Brush("TextFillColorSecondaryBrush"),
            Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible,
        });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control)))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
        }
        return Frame(grid);
    }

    /// <summary>Change the explanation of a card made by <see cref="Card"/>.</summary>
    public static void SetDetail(Border card, string detail)
    {
        if (card.Child is Grid grid && grid.Children.Count > 0 && grid.Children[0] is StackPanel stack &&
            stack.Children.Count > 1 && stack.Children[1] is TextBlock text)
        {
            text.Text = detail;
            text.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
