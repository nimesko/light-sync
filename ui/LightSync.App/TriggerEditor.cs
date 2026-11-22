using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LightSync.App;

/// <summary>
/// The trigger editor, in three steps: 1. When (what sets it off), 2. Devices (only those that can react
/// to it; for each, the whole device, some zones / fans, or single LEDs), 3. Lights (the effect).
/// Triggers always paint over a device's own lighting.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>One device in step 2: ticked or not, and which of its lights the trigger uses.</summary>
    private sealed class TriggerDeviceRow
    {
        public required DeviceInfo Device { get; init; }
        public required FrameworkElement Root { get; init; }
        public required CheckBox Box { get; init; }
        /// <summary>0: whole device, 1: some zones / fans, 2: single LEDs.</summary>
        public required ComboBox Mode { get; init; }
        public List<(string Id, CheckBox Box)> Zones { get; } = [];
        public HashSet<int> Leds { get; } = [];
        public bool LedsLoaded { get; set; }

        public IEnumerable<string> Targets() => !Box.IsChecked.GetValueOrDefault() ? [] : Mode.SelectedIndex switch
        {
            1 => Zones.Where(z => z.Box.IsChecked == true).Select(z => z.Id),
            2 => Leds.Order().Select(i => $"{Device.Id}@{i}"),
            _ => [Device.Id],
        };
    }

    /// <summary>A ComboBox whose options are listed under group titles (the titles can't be picked).</summary>
    private sealed class GroupedCombo
    {
        private List<ComboBoxItem> _items = [];

        public ComboBox Box { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 560 };

        /// <summary>The options, in order, each under its group; then select <paramref name="select"/> (or the first).</summary>
        public void Fill(IEnumerable<(string Group, string Id, string Label)> options, string? select)
        {
            var list = new List<ComboBoxItem>();
            string? group = null;
            foreach (var (g, id, label) in options)
            {
                if (g != group && g.Length > 0)
                {
                    list.Add(new ComboBoxItem
                    {
                        Content = new TextBlock
                        {
                            Text = g.ToUpperInvariant(),
                            FontSize = 11,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            CharacterSpacing = 60,
                            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                        },
                        IsEnabled = false,
                        Padding = new Thickness(12, list.Count == 0 ? 6 : 12, 12, 2),
                    });
                    group = g;
                }
                list.Add(new ComboBoxItem { Content = label, Tag = id, Padding = new Thickness(24, 6, 12, 6) });
            }
            _items = list;
            Box.ItemsSource = list;
            Select(select);
        }

        public string? Selected => (Box.SelectedItem as ComboBoxItem)?.Tag as string;

        public void Select(string? id) =>
            Box.SelectedItem = _items.FirstOrDefault(i => i.Tag is string s && s == id) ?? _items.FirstOrDefault(i => i.Tag is string);
    }

    /// <summary>One step of the trigger editor: a card with a numbered badge, a title and a short line, then its controls.</summary>
    private static Border StepCard(int number, string title, string subtitle, params UIElement[] content)
    {
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Res("AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(),
                Foreground = Res("TextOnAccentFillColorPrimaryBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        titles.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Res("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(badge);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(40, 4, 0, 0) };
        foreach (var c in content)
        {
            body.Children.Add(c);
        }
        return new Border
        {
            Padding = new Thickness(20, 16, 20, 18),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            Child = new StackPanel { Spacing = 10, Children = { head, body } },
        };
    }

    /// <summary>Which devices can react to a "When": keyboards to keys, mice to the mouse, anything to sound and apps.</summary>
    private static bool DeviceSupports(DeviceInfo d, string sourceKind) => sourceKind switch
    {
        "key_any" or "key" => d.Kind == "Keyboard",
        "mouse_click" or "mouse_move" => d.Kind == "Mouse",
        _ => true,
    };

    /// <summary>A short label for a key LED's button ("Number Pad 7" -> "Num 7").</summary>
    /// <summary>A short label that fits a key button ("Number Pad 7" -> "7", "Left Arrow" -> "←"); the full name shows on hover.</summary>
    private static string ShortKey(string name)
    {
        var n = name.Replace(" (ANSI)", "").Replace(" (ISO)", "").Trim();
        var exact = n switch
        {
            "Left Arrow" => "←",
            "Right Arrow" => "→",
            "Up Arrow" => "↑",
            "Down Arrow" => "↓",
            "Num Lock" => "NumLk",
            "Number Pad Enter" => "Ent",
            "Page Up" => "PgUp",
            "Page Down" => "PgDn",
            "Print Screen" => "PrtSc",
            "Scroll Lock" => "ScrLk",
            "Pause/Break" => "Pause",
            "Backspace" => "Bksp",
            "Caps Lock" => "Caps",
            "Escape" => "Esc",
            "Insert" => "Ins",
            "Delete" => "Del",
            "Right Fn" or "Fn" => "Fn",
            "Menu" or "Context" => "Menu",
            "Media Play/Pause" => "⏯",
            "Media Next" => "⏭",
            "Media Previous" => "⏮",
            "Media Stop" => "⏹",
            "Media Mute" => "Mute",
            "Media Volume +" => "Vol+",
            "Media Volume -" => "Vol−",
            _ => null,
        };
        if (exact is not null)
        {
            return exact;
        }
        // The number pad's keys say what they are ("7", "/", "+"); their tooltip names the pad.
        if (n.StartsWith("Number Pad ", StringComparison.Ordinal))
        {
            return n["Number Pad ".Length..];
        }
        return n.Replace("Left ", "L").Replace("Right ", "R").Replace("Control", "Ctrl").Replace("Windows", "Win").Replace("Shift", "Shft").Trim();
    }
    /// <summary>
    /// The editor for a new (<paramref name="isNew"/>, prefilled with its targets) or existing trigger, shown in
    /// place on the right: Save stores it and keeps it open, Cancel drops a new one / undoes changes.
    /// </summary>
    private FrameworkElement BuildTriggerEditor(TriggerInfo trigger, bool isNew)
    {
        var existing = isNew ? null : trigger;
        var t = TriggerInfo.FromJson(trigger.ToJson()) ?? new TriggerInfo { Targets = [.. trigger.Targets] };
        var small = SettingsUi.Style("CaptionTextBlockStyle");
        var secondary = Res("TextFillColorSecondaryBrush");
        TextBlock Caption(string text) => new() { Text = text, Style = small, Foreground = secondary, TextWrapping = TextWrapping.Wrap };

        // ---------- 1. When ----------
        var sourceCombo = new GroupedCombo();
        var startKind = t.SourceKind == "key" ? t.Source["keys"] is JsonArray ? "key" : "key_any" : t.SourceKind;
        sourceCombo.Fill(
        [
            ("Keyboard", "key_any", "A key is pressed"),
            ("Keyboard", "key", "One of some keys is pressed"),
            ("Mouse", "mouse_click", "The mouse is clicked"),
            ("Mouse", "mouse_move", "The mouse moves"),
            ("Music", "audio_beat", "The music beats"),
            ("Music", "audio_level", "The sound gets louder than..."),
            ("Apps", "app_running", "An app is running"),
        ], startKind);
        var source = sourceCombo.Box;
        string SourceKind() => sourceCombo.Selected ?? "key_any";

        var keys = t.Source["keys"] is JsonArray ka ? KeyCodes(ka).Distinct().ToList() : [];
        var keysText = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = SettingsUi.Style("BodyStrongTextBlockStyle") };
        var record = new ToggleButton { Content = "Record keys" };
        var clearKeys = new Button { Content = "Clear" };
        var keysPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                SettingsUi.Row(record, clearKeys),
                keysText,
                Caption("Press \"Record keys\", press the keys you want, then press \"Record keys\" again. Only these keys are recognized; LightSync never sees what else you type."),
            },
        };
        void ShowKeys() => keysText.Text = keys.Count == 0 ? "No keys yet" : string.Join(", ", keys.Select(KeyName));
        ShowKeys();
        clearKeys.Click += (_, _) =>
        {
            keys.Clear();
            ShowKeys();
        };
        record.Checked += (_, _) => record.Content = "Recording... (click to stop)";
        record.Unchecked += (_, _) => record.Content = "Record keys";

        var button = new ComboBox { Header = "Button", ItemsSource = ButtonChoices.Select(c => c.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.SelectedIndex = Math.Max(0, Array.FindIndex(ButtonChoices, c => c.Id == t.Source["button"]?.ToString()));
        var band = new ComboBox { Header = "Of", ItemsSource = BandChoices.Select(c => c.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        band.SelectedIndex = Math.Max(0, Array.FindIndex(BandChoices, c => c.Id == t.Source["band"]?.ToString()));
        var aboveValue = t.Source["above"] is JsonValue av && av.TryGetValue<double>(out var ad) ? Math.Clamp(ad, 0.01, 1) : 0.6;
        var above = new Slider { Header = "Louder than", Minimum = 1, Maximum = 100, StepFrequency = 1, Value = Math.Round(aboveValue * 100) };
        var aboveText = Caption("");
        void ShowAbove() => aboveText.Text = $"{(int)above.Value}% of full scale";
        above.ValueChanged += (_, _) => ShowAbove();
        ShowAbove();
        var levelPanel = new StackPanel { Spacing = 4, Children = { band, above, aboveText } };

        // The app: picked from the apps running now (icon, name, window), searchable; typing a program
        // name still works for apps that aren't open.
        var appExe = t.Source["app"]?.ToString() ?? "";
        // A browser: optionally only while its current page is on a site (just the host).
        var siteBox = new TextBox
        {
            Header = "Website (optional)",
            PlaceholderText = "For example youtube.com or music.youtube.com",
            Text = t.Source["site"]?.ToString() ?? "",
            MaxLength = 253,
        };
        var siteNote = Caption("");
        var sitePanel = new StackPanel { Spacing = 4, Children = { siteBox, siteNote } };
        void ShowSite()
        {
            sitePanel.Visibility = NowPlaying.IsBrowser(appExe) ? Visibility.Visible : Visibility.Collapsed;
            var host = NowPlaying.SiteHost(siteBox.Text);
            siteNote.Text = siteBox.Text.Trim().Length == 0
                ? "Empty: any page. With a site, only while a window of this browser shows a page on it (its current tab)."
                : host.Length == 0 ? "That isn't a web site's address: type its host, like youtube.com."
                : host.Count(c => c == '.') == 1
                    ? $"Only while a window of this browser shows a page on {host} or one of its subdomains (like m.{host})."
                    : $"Only while a window of this browser shows a page on {host} or one of its subdomains.";
        }
        siteBox.TextChanged += (_, _) => ShowSite();
        var appPicker = BuildAppPicker(appExe, exe =>
        {
            appExe = exe;
            ShowSite();
        });
        ShowSite();
        var appPanel = new StackPanel
        {
            Spacing = 6,
            Children = { appPicker, sitePanel, Caption("While the app runs, its lighting replaces what the devices show; when it closes, they go back.") },
        };
        string AppText() => appExe.Trim();
        string SiteText() => NowPlaying.IsBrowser(appExe) ? NowPlaying.SiteHost(siteBox.Text) : "";

        // ---------- 2. Devices ----------
        var picked = new HashSet<string>(t.Targets);
        var allDevices = new CheckBox { Content = "All devices", IsChecked = t.Targets.Count == 0 };
        var groupBoxes = _groupItems.Select(g => (g.Id, Box: new CheckBox { Content = $"Group: {g.Title}", IsChecked = picked.Contains(g.Id) })).ToList();
        var groupsPanel = new StackPanel { Spacing = 2 };
        groupBoxes.ForEach(g => groupsPanel.Children.Add(g.Box));
        var rows = new List<TriggerDeviceRow>();
        var rowsPanel = new StackPanel { Spacing = 6 };
        var devicesNote = Caption("");
        foreach (var d in _devices)
        {
            rows.Add(BuildDeviceRow(d, picked));
        }
        rows.ForEach(r => rowsPanel.Children.Add(r.Root));
        var devicesPanel = new StackPanel { Spacing = 6, Children = { allDevices, groupsPanel, rowsPanel, devicesNote } };

        void ShowDevices()
        {
            var s = SourceKind();
            var global = s is "audio_beat" or "audio_level" or "app_running";
            allDevices.Visibility = global ? Visibility.Visible : Visibility.Collapsed;
            var all = global && allDevices.IsChecked == true;
            groupsPanel.Visibility = global && !all && groupBoxes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var shown = 0;
            foreach (var r in rows)
            {
                // Devices that can react to this, plus any the trigger already uses.
                var used = r.Targets().Any() && existing is not null;
                var show = !all && (DeviceSupports(r.Device, s) || used);
                r.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                shown += show ? 1 : 0;
            }
            devicesNote.Text = all ? "Every device that isn't hidden."
                : shown == 0 ? (s is "key_any" or "key" ? "No keyboard found: key triggers light the keyboard they come from." : "No mouse found: mouse triggers light the mouse they come from.")
                : s is "key_any" or "key" ? "Keys light the keyboard (only keyboards are listed)."
                : s is "mouse_click" or "mouse_move" ? "Clicks and movement light the mouse (only mice are listed)."
                : "Tick one or more devices; for each, the whole device, some zones or single LEDs.";
        }
        allDevices.Checked += (_, _) => ShowDevices();
        allDevices.Unchecked += (_, _) => ShowDevices();

        // ---------- 3. Lights ----------
        var actionCombo = new GroupedCombo();
        var action = actionCombo.Box;
        void FillActions(string want)
        {
            // Every effect is available everywhere: an app shows one while it runs; any other trigger can
            // play one for a while, besides the quick flash / pulse / ripple / glow.
            actionCombo.Fill(SourceKind() == "app_running"
                ? [("Effect", "lighting", "Show an effect while it runs")]
                : [
                    ("Quick effects", "flash", "Flash a color"),
                    ("Quick effects", "pulse", "Pulse (brighten and back)"),
                    ("Quick effects", "ripple", "Ripple a color"),
                    ("Quick effects", "fade_in", "Fade in a color"),
                    ("Any effect", "lighting", "Play an effect (fire, plasma, music flow...)"),
                    ("Other", "preset", "Load a preset"),
                ], want);
        }
        string ActionKind() => actionCombo.Selected ?? "flash";

        var color = ParseHex(t.Action["color"]?.ToString() ?? "") ?? Color.FromArgb(255, 255, 255, 255);
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = Res("CardStrokeColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center };
        var hexText = new TextBlock { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
        void ShowPicked()
        {
            swatch.Background = new SolidColorBrush(color);
            hexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        ShowPicked();
        var swatches = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 34, ItemHeight = 34 };
        foreach (var (label, hex) in PresetColors)
        {
            var c = ParseHex(hex)!.Value;
            var b = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(c), Stroke = Res("CardStrokeColorDefaultBrush"), StrokeThickness = 1 },
            };
            ToolTipService.SetToolTip(b, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) =>
            {
                color = c;
                ShowPicked();
            };
            swatches.Children.Add(b);
        }
        var picker = new ColorPicker { ColorSpectrumShape = ColorSpectrumShape.Box, IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = true, Color = color };
        picker.ColorChanged += (_, e) =>
        {
            color = Color.FromArgb(255, e.NewColor.R, e.NewColor.G, e.NewColor.B);
            ShowPicked();
        };
        var custom = new Button { Content = "Other color...", Flyout = new Flyout { Content = picker } };
        var randomColor = new CheckBox { Content = "Random color each time", IsChecked = t.Action["random"] is JsonValue rnd && rnd.TryGetValue<bool>(out var rb) && rb };
        var colorPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Color" },
                swatches,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, hexText, custom } },
                randomColor,
            },
        };
        randomColor.Checked += (_, _) => swatches.Opacity = 0.4;
        randomColor.Unchecked += (_, _) => swatches.Opacity = 1;
        swatches.Opacity = randomColor.IsChecked == true ? 0.4 : 1;

        // Effects listed by family (calm, motion, colorful, alerts, sound reactive).
        var effectCombo = new GroupedCombo();
        effectCombo.Fill(EffectGroups.SelectMany(g => Effects.Where(e => e.Group == g).Select(e => (g, e.Id, e.Name))), t.Action["effect"]?.ToString());
        var effect = effectCombo.Box;
        effect.Header = "Effect";
        var lightBrightness = new Slider { Header = "Brightness", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = t.Action["brightness"] is JsonValue lb && lb.TryGetValue<int>(out var lbi) ? lbi : 100 };
        var lightSpeed = new Slider { Header = "Speed", Minimum = 1, Maximum = 100, StepFrequency = 1, Value = t.Action["speed"] is JsonValue ls && ls.TryGetValue<int>(out var lsi) ? lsi : 50 };
        // Sound effects: their colors can follow a palette (random per beat, cold to hot, ...).
        var palette = new ComboBox
        {
            Header = "Sound colors",
            ItemsSource = SoundPalettes.Select(p => p.Name).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, Array.FindIndex(SoundPalettes, p => p.Id == t.Action["palette"]?.ToString())),
        };
        var paletteTip = Caption("");
        string PaletteId() => SoundPalettes[Math.Max(0, palette.SelectedIndex)].Id;
        // Beat / Bass / Spectrum: what one light is, and how many colors random uses.
        var split = new ComboBox
        {
            Header = "Lights per",
            ItemsSource = Splits.Select(x => x.Name).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, Array.FindIndex(Splits, x => x.Id == (t.Action["split"]?.ToString() ?? "zone"))),
        };
        var colorCount = new ComboBox
        {
            Header = "Number of colors",
            ItemsSource = ColorCountNames,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = t.Action["colors"] is JsonValue cv && cv.TryGetValue<int>(out var cn) ? Math.Clamp(cn, 0, 8) : 3,
        };
        // What LightSync hears now, painted like these lights will be.
        var lightSound = AddVisualizer(70);
        var lightingPanel = new StackPanel { Spacing = 6, Children = { effect, split, palette, paletteTip, colorCount, lightBrightness, lightSpeed, lightSound.Root } };
        string EffectId() => effectCombo.Selected ?? "static";
        // The music triggers show what they listen to.
        var whenSound = AddVisualizer(70);

        var durationMs = t.Action["duration_ms"] is JsonValue dv && dv.TryGetValue<int>(out var dms) ? Math.Clamp(dms, 50, 10000) : 400;
        var duration = new Slider { Header = "Duration", Minimum = 50, Maximum = 5000, StepFrequency = 50, Value = durationMs };
        var durationText = Caption("");
        void ShowDuration() => durationText.Text = FormatMs((int)duration.Value);
        duration.ValueChanged += (_, _) => ShowDuration();
        ShowDuration();
        // An effect can stay on for good once fired: until the trigger is turned off, changed or deleted.
        var forever = new CheckBox
        {
            Content = "No end: keep it on until the trigger is turned off",
            IsChecked = !isNew && t.ActionKind == "lighting" && t.SourceKind != "app_running" && t.Action["duration_ms"] is null,
        };
        var durationPanel = new StackPanel { Spacing = 0, Children = { forever, duration, durationText } };
        var preset = new ComboBox
        {
            Header = "Preset",
            ItemsSource = _profiles.ToList(),
            PlaceholderText = _profiles.Count == 0 ? "No presets yet (File > Presets)" : "Pick a preset",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = _profiles.Count > 0,
        };
        if (t.Action["name"]?.ToString() is { } presetName && _profiles.Contains(presetName))
        {
            preset.SelectedItem = presetName;
        }

        // Start where it happened (the pressed key / the mouse wheel) and reach only so far, ring by ring.
        var radiusOn = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, IsOn = t.Action["radius"] is JsonValue };
        var radiusValue = t.Action["radius"] is JsonValue rv && rv.TryGetValue<double>(out var rd) ? Math.Clamp(rd, 0, 10) : 1;
        var radius = new Slider { Minimum = 0, Maximum = 10, StepFrequency = 1, Value = Math.Round(radiusValue), HorizontalAlignment = HorizontalAlignment.Stretch };
        var radiusTitle = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var radiusText = Caption("");
        var radiusRow = new Grid { ColumnSpacing = 12 };
        radiusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        radiusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        radiusRow.Children.Add(radiusTitle);
        Grid.SetColumn(radiusOn, 1);
        radiusRow.Children.Add(radiusOn);
        var delayMs = t.Action["layer_delay_ms"] is JsonValue ldv && ldv.TryGetValue<int>(out var ldi) ? Math.Clamp(ldi, 0, 500) : 0;
        var layerDelay = new Slider { Header = "Delay per ring", Minimum = 0, Maximum = 500, StepFrequency = 10, Value = delayMs };
        var layerDelayText = Caption("");
        var radiusPanel = new StackPanel { Spacing = 4, Children = { radiusRow, radius, radiusText, layerDelay, layerDelayText } };
        bool Local() => radiusPanel.Visibility == Visibility.Visible && radiusOn.IsOn;

        var name = new TextBox { PlaceholderText = "For example: Ripple on every key", Text = t.Name, MaxLength = 64 };
        var test = new Button { Content = "Test" };
        var testNote = Caption("");
        var error = new TextBlock { Foreground = Res("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        if (isNew)
        {
            radiusOn.IsOn = true; // keys and clicks start where they happen
        }
        FillActions(isNew ? (SourceKind() is "key_any" or "key" or "mouse_click" or "mouse_move" ? "ripple" : "flash") : t.ActionKind);

        void ShowParts()
        {
            var s = SourceKind();
            var a = ActionKind();
            var keyish = s is "key_any" or "key";
            var mouseish = s is "mouse_click" or "mouse_move";
            keysPanel.Visibility = s == "key" ? Visibility.Visible : Visibility.Collapsed;
            button.Visibility = s == "mouse_click" ? Visibility.Visible : Visibility.Collapsed;
            levelPanel.Visibility = s == "audio_level" ? Visibility.Visible : Visibility.Collapsed;
            appPanel.Visibility = s == "app_running" ? Visibility.Visible : Visibility.Collapsed;
            radiusPanel.Visibility = (keyish || mouseish) && a != "preset" ? Visibility.Visible : Visibility.Collapsed;
            radiusTitle.Text = keyish ? "Start at the pressed key and only reach this far" : "Start at the mouse wheel and only reach this far";
            radius.IsEnabled = radiusOn.IsOn;
            var r = (int)radius.Value;
            radiusText.Text = !radiusOn.IsOn ? "Off: all the chosen lights at once."
                : keyish ? (r == 0 ? "Radius 0: only the key you press." : $"Radius {r}: the key you press and {r} key{(r == 1 ? "" : "s")} around it.")
                : (r == 0 ? "Radius 0: only the wheel." : $"Radius {r}: the wheel and {r} LED{(r == 1 ? "" : "s")} on from it.");
            var rings = radiusOn.IsOn && radius.Value >= 1 && a != "lighting";
            layerDelay.Visibility = rings ? Visibility.Visible : Visibility.Collapsed;
            layerDelayText.Visibility = layerDelay.Visibility;
            var dl = (int)layerDelay.Value;
            layerDelayText.Text = dl == 0 ? "0 ms: every ring lights at once." : $"Each ring lights {dl} ms after the one inside it.";
            var lightingUsesColor = a == "lighting" && EffectById(EffectId()).UsesColor && !(EffectById(EffectId()).Audio && PaletteId() != "effect");
            colorPanel.Visibility = a is "flash" or "ripple" or "fade_in" || lightingUsesColor ? Visibility.Visible : Visibility.Collapsed;
            randomColor.Visibility = a is "flash" or "ripple" or "fade_in" ? Visibility.Visible : Visibility.Collapsed;
            durationPanel.Visibility = a == "preset" || a == "lighting" && s == "app_running" ? Visibility.Collapsed : Visibility.Visible;
            forever.Visibility = a == "lighting" ? Visibility.Visible : Visibility.Collapsed;
            var endless = a == "lighting" && forever.IsChecked == true;
            duration.Visibility = endless ? Visibility.Collapsed : Visibility.Visible;
            durationText.Text = endless ? "Once it plays, it stays on until you turn this trigger off, change it or delete it." : FormatMs((int)duration.Value);
            lightingPanel.Visibility = a == "lighting" ? Visibility.Visible : Visibility.Collapsed;
            var soundEffect = a == "lighting" && EffectById(EffectId()).Audio;
            palette.Visibility = soundEffect ? Visibility.Visible : Visibility.Collapsed;
            paletteTip.Visibility = palette.Visibility;
            var splitEffect = a == "lighting" && EffectId() is "sound_beat" or "sound_bass" or "sound_spectrum";
            split.Visibility = splitEffect ? Visibility.Visible : Visibility.Collapsed;
            colorCount.Visibility = splitEffect && PaletteId() == "random" ? Visibility.Visible : Visibility.Collapsed;
            paletteTip.Text = SoundPalettes[Math.Max(0, palette.SelectedIndex)].Tip + ".";
            lightSound.Root.Visibility = palette.Visibility;
            lightSound.Polling = soundEffect;

            lightSound.Recolor(color, PaletteId());
            lightSpeed.Header = soundEffect ? "Sensitivity" : "Speed";
            var musicSource = s is "audio_beat" or "audio_level";
            whenSound.Root.Visibility = musicSource ? Visibility.Visible : Visibility.Collapsed;
            whenSound.Polling = musicSource;
            UpdateSoundViews();
            preset.Visibility = a == "preset" ? Visibility.Visible : Visibility.Collapsed;
            var canTest = keyish || mouseish;
            test.IsEnabled = canTest;
            ToolTipService.SetToolTip(test, canTest
                ? "Pretend the key or mouse event happened: saved, enabled triggers that match it play now."
                : "Sound and app triggers play on their own (music playing, the app running); they can't be tested here.");
            if (s != "key" && record.IsChecked == true)
            {
                record.IsChecked = false;
            }
            ShowDevices();
        }
        source.SelectionChanged += (_, _) =>
        {
            FillActions(ActionKind());
            ShowParts();
        };
        action.SelectionChanged += (_, _) => ShowParts();
        effect.SelectionChanged += (_, _) => ShowParts();
        palette.SelectionChanged += (_, _) => ShowParts();
        split.SelectionChanged += (_, _) => ShowParts();
        radiusOn.Toggled += (_, _) => ShowParts();
        radius.ValueChanged += (_, _) => ShowParts();
        layerDelay.ValueChanged += (_, _) => ShowParts();
        forever.Checked += (_, _) => ShowParts();
        forever.Unchecked += (_, _) => ShowParts();
        duration.ValueChanged += (_, _) => ShowParts();
        ShowParts();

        var save = new Button { Content = "Save", Style = SettingsUi.Style("AccentButtonStyle") };
        var cancel = new Button { Content = existing is null ? "Cancel" : "Undo changes" };
        // Footer: test on the left, cancel / save on the right, messages above.
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        test.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "Test" } } };
        footer.Children.Add(test);
        save.MinWidth = 96;
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } };
        Grid.SetColumn(right, 2);
        footer.Children.Add(right);
        var footerCard = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("LayerOnMicaBaseAltFillColorDefaultBrush"),
            Child = new StackPanel { Spacing = 8, Children = { error, testNote, footer } },
        };

        var form = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                StepCard(1, "When", "What sets it off.", source, keysPanel, button, levelPanel, appPanel, whenSound.Root),
                StepCard(2, "Devices", "Which lights it uses: whole devices, some zones, or single LEDs.", devicesPanel),
                StepCard(3, "Lights", "What the lights do.", action, lightingPanel, radiusPanel, colorPanel, durationPanel, preset),
                StepCard(4, "Name", "Optional: shown in the trigger list.", name),
                footerCard,
            },
        };

        // Record keys: while "Record keys" is on, every key pressed in this editor is added (and does nothing else).
        form.PreviewKeyDown += (_, e) =>
        {
            if (record.IsChecked != true)
            {
                return;
            }
            e.Handled = true;
            var vk = (int)e.Key;
            if (vk is > 0 and < 255 && !keys.Contains(vk))
            {
                keys.Add(vk);
                ShowKeys();
            }
        };

        test.Click += (_, _) => Safe("testing a trigger", async () =>
        {
            var s = SourceKind();
            try
            {
                var fired = s switch
                {
                    "key_any" => await _client.InputEventAsync("key"),
                    "key" => await _client.InputEventAsync("key", keys.Count > 0 ? keys[0] : null),
                    "mouse_click" => await _client.InputEventAsync("mouse_click", null, ButtonChoices[Math.Max(0, button.SelectedIndex)].Id ?? "left"),
                    "mouse_move" => await _client.InputEventAsync("mouse_move"),
                    _ => 0,
                };
                var played = fired switch
                {
                    0 => "No saved, enabled trigger reacted (the lights must be on).",
                    1 => "1 trigger played.",
                    _ => $"{fired} triggers played.",
                };
                testNote.Text = existing is null || HasChanges()
                    ? $"Sent a pretend event. {played} Save first to test this trigger as edited."
                    : $"Sent a pretend event. {played}";
            }
            catch (LightSyncException e) when (e.IsUnknownMethod)
            {
                testNote.Text = "Testing needs a newer LightSync service.";
            }
            catch (Exception e) when (Errors.Expected(e))
            {
                testNote.Text = $"Couldn't test: {Explain(e)}";
            }
        });

        List<string> Targets()
        {
            var s = SourceKind();
            var global = s is "audio_beat" or "audio_level" or "app_running";
            if (global && allDevices.IsChecked == true)
            {
                return [];
            }
            var list = new List<string>();
            if (global)
            {
                list.AddRange(groupBoxes.Where(g => g.Box.IsChecked == true).Select(g => g.Id));
            }
            list.AddRange(rows.Where(r => r.Root.Visibility == Visibility.Visible).SelectMany(r => r.Targets()));
            // Keep targets that aren't listed right now (hidden or switched-off devices).
            var known = _devices.Select(d => d.Id).ToList();
            list.AddRange(picked.Where(p => !p.StartsWith("group:") && !known.Any(k => p == k || p.StartsWith(k + "/") || p.StartsWith(k + "@"))));
            return list.Distinct().ToList();
        }

        TriggerInfo Build()
        {
            var s = SourceKind();
            var src = new JsonObject { ["kind"] = s == "key_any" ? "key" : s };
            switch (s)
            {
                case "key_any":
                    src["keys"] = "any";
                    break;
                case "key":
                    src["keys"] = new JsonArray(keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
                    break;
                case "mouse_click" when ButtonChoices[Math.Max(0, button.SelectedIndex)].Id is { } b:
                    src["button"] = b;
                    break;
                case "audio_level":
                    src["band"] = BandChoices[Math.Max(0, band.SelectedIndex)].Id;
                    src["above"] = Math.Round(above.Value) / 100.0;
                    break;
                case "app_running":
                    src["app"] = AppText();
                    if (SiteText() is { Length: > 0 } site)
                    {
                        src["site"] = site;
                    }
                    break;
            }
            var a = ActionKind();
            var act = new JsonObject { ["kind"] = a };
            if (a is "flash" or "ripple" or "fade_in" or "lighting")
            {
                act["color"] = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            if (a is "flash" or "ripple" or "fade_in" && randomColor.IsChecked == true)
            {
                act["random"] = true;
            }
            if (a == "preset")
            {
                act["name"] = preset.SelectedItem as string ?? "";
            }
            else if (a == "lighting")
            {
                act["effect"] = EffectId();
                act["brightness"] = (int)lightBrightness.Value;
                act["speed"] = (int)lightSpeed.Value;
                act["palette"] = PaletteId();
                act["split"] = Splits[Math.Max(0, split.SelectedIndex)].Id;
                act["colors"] = Math.Max(0, colorCount.SelectedIndex);
                if (s != "app_running")
                {
                    // After an event: for this long (fading out at the end), or with no end; around the
                    // key / wheel if local.
                    if (forever.IsChecked != true)
                    {
                        act["duration_ms"] = (int)duration.Value;
                    }
                    if (Local())
                    {
                        act["radius"] = Math.Round(radius.Value);
                    }
                }
            }
            else
            {
                act["duration_ms"] = (int)duration.Value;
                if (Local())
                {
                    act["radius"] = Math.Round(radius.Value);
                    if (radius.Value >= 1 && layerDelay.Value > 0)
                    {
                        act["layer_delay_ms"] = (int)layerDelay.Value;
                    }
                }
            }
            var result = new TriggerInfo
            {
                Id = t.Id,
                Name = name.Text.Trim(),
                Enabled = t.Enabled,
                Source = src,
                Action = act,
                Targets = Targets(),
            };
            if (string.IsNullOrEmpty(result.Name))
            {
                result.Name = $"{DescribeSource(src)}: {DescribeAction(act).ToLowerInvariant()}";
            }
            if (result.Name.Length > 64)
            {
                result.Name = result.Name[..64].TrimEnd(); // the service takes 1..64 characters
            }
            return result;
        }

        bool HasChanges() => existing is null || Build().ToJson().ToJsonString() != existing.ToJson().ToJsonString();

        cancel.Click += (_, _) =>
        {
            if (existing is null)
            {
                _editingTrigger = null;
                ShowView(ShellView.Home);
            }
            else
            {
                BuildTriggerPane();
            }
        };
        save.Click += (_, _) => Safe("saving a trigger", async () =>
        {
            save.IsEnabled = false;
            var before = _triggers.Select(x => x.Id).OfType<string>().ToHashSet();
            try
            {
                var s = SourceKind();
                var global = s is "audio_beat" or "audio_level" or "app_running";
                string? problem = null;
                if (s == "key" && keys.Count == 0)
                {
                    problem = "Record at least one key, or pick \"Any key is pressed\".";
                }
                else if (s == "app_running" && NowPlaying.IsBrowser(appExe) && siteBox.Text.Trim().Length > 0 && SiteText().Length == 0)
                {
                    problem = "Type the web site's host, like youtube.com, or leave Website empty.";
                }
                else if (s == "app_running" && AppText().Length == 0)
                {
                    problem = "Pick the app, or type its program file (for example game.exe).";
                }
                else if (ActionKind() == "preset" && preset.SelectedItem is null)
                {
                    problem = _profiles.Count == 0 ? "Save a preset first (File > Presets), or pick another action." : "Pick the preset to load.";
                }
                else if (!(global && allDevices.IsChecked == true) && Targets().Count == 0)
                {
                    problem = rows.Any(r => r.Box.IsChecked == true)
                        ? "Pick at least one zone or LED for the devices set to \"Some zones\" or \"Single LEDs\"."
                        : "Tick at least one device in step 2.";
                }
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    return;
                }
                await _client.SaveTriggerAsync(Build());
            }
            catch (LightSyncException ex) when (ex.IsUnknownMethod)
            {
                error.Text = "Triggers need a newer LightSync service (lightsyncd).";
                error.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception ex) when (Errors.Expected(ex))
            {
                error.Text = Explain(ex);
                error.Visibility = Visibility.Visible;
                return;
            }
            finally
            {
                save.IsEnabled = true;
            }
            await AfterTriggerSavedAsync(t.Id, before);
        });
        return form;
    }

    /// <summary>
    /// One device in step 2: a tick box, then which of its lights: the whole device, some zones / fans, or
    /// single LEDs (a key grid for keyboards, loaded from the service the first time).
    /// </summary>
    private TriggerDeviceRow BuildDeviceRow(DeviceInfo d, HashSet<string> picked)
    {
        var zoneIds = (d.Zones ?? []).SelectMany(z => z.Fans.Count > 0 ? z.Fans.Select(f => (f.Id, Name: $"{z.Name} / {f.Name}")) : [(z.Id, z.Name)]).ToList();
        var usesZones = picked.Any(p => p.StartsWith(d.Id + "/"));
        var usesLeds = picked.Any(p => p.StartsWith(d.Id + "@"));
        var box = new CheckBox { Content = d.Name, IsChecked = picked.Contains(d.Id) || usesZones || usesLeds, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var mode = new ComboBox
        {
            ItemsSource = new List<string> { "Whole device", zoneIds.Count > 1 ? "Some zones / fans" : "Its zone", "Single LEDs" },
            SelectedIndex = usesLeds ? 2 : usesZones ? 1 : 0,
            MinWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(box);
        Grid.SetColumn(mode, 1);
        head.Children.Add(mode);

        var zonesPanel = new StackPanel { Spacing = 2, Margin = new Thickness(28, 0, 0, 0) };
        var ledsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };
        var root = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(10, 6, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = Res("SubtleFillColorSecondaryBrush"),
            Children = { head, zonesPanel, ledsPanel },
        };
        var row = new TriggerDeviceRow { Device = d, Root = root, Box = box, Mode = mode };
        foreach (var (id, zname) in zoneIds)
        {
            var zb = new CheckBox { Content = zname, IsChecked = picked.Contains(id) || picked.Any(p => p.StartsWith(id + "/")) };
            row.Zones.Add((id, zb));
            zonesPanel.Children.Add(zb);
        }
        foreach (var p in picked)
        {
            if (p.StartsWith(d.Id + "@") && int.TryParse(p[(d.Id.Length + 1)..], out var i))
            {
                row.Leds.Add(i);
            }
        }

        void Show()
        {
            var on = box.IsChecked == true;
            mode.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            zonesPanel.Visibility = on && mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            ledsPanel.Visibility = on && mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (on && mode.SelectedIndex == 2 && !row.LedsLoaded)
            {
                row.LedsLoaded = true;
                Safe("listing a device's LEDs", () => FillLedPickerAsync(row, ledsPanel));
            }
        }
        box.Checked += (_, _) => Show();
        box.Unchecked += (_, _) => Show();
        mode.SelectionChanged += (_, _) => Show();
        Show();
        return row;
    }

    /// <summary>Single LEDs: one toggle per LED, laid out like the keyboard where the grid is known.</summary>
    private async Task FillLedPickerAsync(TriggerDeviceRow row, StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "Loading the LEDs...", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        List<LedInfo> leds;
        try
        {
            leds = await _client.LedsAsync(row.Device.Id);
        }
        catch (Exception e) when (Errors.Recoverable(e))
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = e is LightSyncException { IsUnknownMethod: true } ? "Picking single LEDs needs a newer LightSync service." : $"Couldn't list the LEDs: {Explain(e)}",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
            row.LedsLoaded = false;
            return;
        }
        panel.Children.Clear();
        var count = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Res("TextFillColorSecondaryBrush") };
        void ShowCount() => count.Text = row.Leds.Count switch { 0 => "No LED picked yet: click the ones to light.", 1 => "1 LED picked.", var n => $"{n} LEDs picked." };
        var toggles = new List<(int Index, ToggleButton Button)>();
        ToggleButton Toggle(LedInfo l, double width)
        {
            var b = new ToggleButton
            {
                Content = new TextBlock { Text = ShortKey(l.Name), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = row.Leds.Contains(l.Index),
                Width = width,
                Height = 30,
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 0,
            };
            ToolTipService.SetToolTip(b, l.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, l.Name);
            b.Checked += (_, _) => { row.Leds.Add(l.Index); ShowCount(); };
            b.Unchecked += (_, _) => { row.Leds.Remove(l.Index); ShowCount(); };
            toggles.Add((l.Index, b));
            return b;
        }
        var all = new Button { Content = "All" };
        var none = new Button { Content = "None" };
        all.Click += (_, _) => toggles.ForEach(x => x.Button.IsChecked = true);
        none.Click += (_, _) => toggles.ForEach(x => x.Button.IsChecked = false);
        panel.Children.Add(SettingsUi.Row(all, none, count));

        var placed = leds.Where(l => l.Col is not null && l.Row is not null).ToList();
        if (placed.Count > 0)
        {
            // Keyboard: the key grid.
            var grid = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
            var cols = (int)placed.Max(l => l.Col!.Value) + 1;
            var rowsN = (int)placed.Max(l => l.Row!.Value) + 1;
            for (var c = 0; c < cols; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            }
            for (var r = 0; r < rowsN; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            foreach (var l in placed)
            {
                var b = Toggle(l, 36);
                Grid.SetColumn(b, (int)l.Col!.Value);
                Grid.SetRow(b, (int)l.Row!.Value);
                grid.Children.Add(b);
            }
            panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled });
        }
        var rest = leds.Where(l => l.Col is null || l.Row is null).ToList();
        if (rest.Count > 0)
        {
            // Everything else: in LED order, wrapped.
            var wrap = new WrapPanel { Spacing = 4 };
            rest.ForEach(l => wrap.Children.Add(Toggle(l, 96)));
            panel.Children.Add(wrap);
        }
        ShowCount();
    }
}
