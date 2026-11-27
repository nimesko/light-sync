using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LightSync.App;

/// <summary>
/// The trigger editor: its name, then the map that builds it: 1. When (what sets it off), 2. each effect,
/// 3. the devices that show it (each the whole device, or single LEDs). Any effect mixes with any other.
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
        /// <summary>Which lights: one of <see cref="Modes"/>.</summary>
        public required ComboBox Mode { get; init; }
        /// <summary>What each choice of <see cref="Mode"/> is: 0 whole device, 1 some zones / fans, 2 single LEDs.</summary>
        public required int[] Modes { get; init; }
        public int ModeOf => Modes[Math.Clamp(Mode.SelectedIndex, 0, Modes.Length - 1)];
        public List<(string Id, CheckBox Box)> Zones { get; } = [];
        public HashSet<int> Leds { get; } = [];
        public bool LedsLoaded { get; set; }
        /// <summary>Called when what it lights changes (ticked, its mode, a zone or an LED).</summary>
        public Action? Changed { get; set; }
        /// <summary>Its own "lights per" and sound colors (sound effects): first choice "as the effect".</summary>
        public required FrameworkElement SoundPanel { get; init; }
        public required ComboBox Split { get; init; }
        public required ComboBox Palette { get; init; }
        /// <summary>Its own "lights per", or null to use the effect's.</summary>
        public string? SplitId => Split.SelectedIndex > 0 ? Splits[Split.SelectedIndex - 1].Id : null;
        /// <summary>Its own sound colors, or null to use the effect's.</summary>
        public string? PaletteId => Palette.SelectedIndex > 0 ? SoundPalettes[Palette.SelectedIndex - 1].Id : null;

        public IEnumerable<string> Targets() => !Box.IsChecked.GetValueOrDefault() ? [] : ModeOf switch
        {
            1 => Zones.Where(z => z.Box.IsChecked == true).Select(z => z.Id),
            2 => Leds.Order().Select(i => $"{Device.Id}@{i}"),
            _ => [Device.Id],
        };
    }

    /// <summary>
    /// One effect of a trigger: the effect (a quick effect, any effect, or a preset) with its settings, and
    /// the devices that show it, or "all (the other) devices".
    /// </summary>
    private sealed class TriggerBlock
    {
        /// <summary>The effect and its settings.</summary>
        public required FrameworkElement View { get; init; }
        /// <summary>Its devices: a card each, and "Add a device".</summary>
        public required FrameworkElement DevicesView { get; init; }
        public required GroupedCombo Effect { get; init; }
        public Color Color { get; set; }
        public required CheckBox Random { get; init; }
        public required Slider Brightness { get; init; }
        public required Slider Speed { get; init; }
        public required ComboBox Palette { get; init; }
        public required ComboBox Split { get; init; }
        public required ComboBox Colors { get; init; }
        public required ComboBox Preset { get; init; }
        /// <summary>"All devices" / "All the other devices".</summary>
        public required CheckBox Rest { get; init; }
        public required List<(string Id, CheckBox Box)> Groups { get; init; }
        public required List<TriggerDeviceRow> Rows { get; init; }
        /// <summary>Saved targets on devices not listed now (hidden or switched off): kept as they are.</summary>
        public required List<string> Kept { get; init; }
        /// <summary>Shows the parts its effect and the trigger's "When" use.</summary>
        public required Action Refresh { get; init; }
        /// <summary>The effect picked when it was made (until its list is filled).</summary>
        public required string Initial { get; init; }
        /// <summary>Set once its list of effects is filled.</summary>
        public string Options { get; set; } = "";
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
    private static Border StepCard(int number, string title, string subtitle, params UIElement[] content) =>
        StepCard(number, new TextBlock { Text = title }, new TextBlock { Text = subtitle }, content);

    /// <summary>A step card whose title and subtitle the caller can change later.</summary>
    private static Border StepCard(int number, TextBlock title, TextBlock subtitle, params UIElement[] content)
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
        title.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        subtitle.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        subtitle.Foreground = Res("TextFillColorSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(title);
        titles.Children.Add(subtitle);
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

    /// <summary>The devices a new trigger starts on for a "When": keyboards for keys, mice for the mouse (any device can be added).</summary>
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
    /// place on the right: its name on top, then the map, which is how it is built: click "When" to pick what
    /// sets it off, click an effect to pick it and its devices, add more effects for other devices. Save
    /// stores it and keeps it open, Cancel drops a new one / undoes changes.
    /// </summary>
    private FrameworkElement BuildTriggerEditor(TriggerInfo trigger, bool isNew, Button save)
    {
        var existing = isNew ? null : trigger;
        var t = TriggerInfo.FromJson(trigger.ToJson()) ?? new TriggerInfo { Targets = [.. trigger.Targets] };
        var small = SettingsUi.Style("CaptionTextBlockStyle");
        var secondary = Res("TextFillColorSecondaryBrush");
        TextBlock Caption(string text) => new() { Text = text, Style = small, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
        var white = Color.FromArgb(255, 255, 255, 255);
        // Redraws everything that depends on the choices (set once everything exists).
        Action? refresh = null;
        void Changed() => refresh?.Invoke();

        // ---------- the name: required, on top ----------
        var name = new TextBox { Header = "Name", PlaceholderText = "For example: Ripple on every key", Text = t.Name, MaxLength = 64 };
        var nameCard = Card(new StackPanel { Spacing = 4, Children = { name, Caption("Required: the trigger list shows it.") } });

        // ---------- 1. When ----------
        var sourceCombo = new GroupedCombo();
        var startKind = t.SourceKind == "key" ? t.Source["keys"] is JsonArray ? "key" : "key_any" : t.SourceKind;
        var sources = new List<(string Group, string Id, string Label)>
        {
            ("Keyboard", "key_any", "A key is pressed"),
            ("Keyboard", "key", "One of some keys is pressed"),
            ("Mouse", "mouse_click", "The mouse is clicked"),
            ("Mouse", "mouse_move", "The mouse moves"),
            ("Apps", "app_running", "An app is running"),
        };
        // The music sources aren't offered any more; a trigger that already has one keeps it.
        if (startKind == "audio_beat")
        {
            sources.Add(("Music", "audio_beat", "The music beats"));
        }
        else if (startKind == "audio_level")
        {
            sources.Add(("Music", "audio_level", "The sound gets louder than..."));
        }
        sourceCombo.Fill(sources, startKind);
        var source = sourceCombo.Box;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(source, "When");
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
        void ShowKeys()
        {
            keysText.Text = keys.Count == 0 ? "No keys yet" : string.Join(", ", keys.Select(KeyName));
            Changed();
        }
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
        // Each row: an app (and, for a browser, optionally a site its current page is on). The trigger
        // holds while any one of them does.
        var appRows = new List<(Func<string> Exe, TextBox Site, FrameworkElement View)>();
        var appRowsPanel = new StackPanel { Spacing = 10 };
        var addApp = new Button { Content = "Add another app or website" };
        ToolTipService.SetToolTip(addApp, "The trigger lights while any one of its apps (or websites) is open");
        void ShowAppRows()
        {
            appRowsPanel.Children.Clear();
            foreach (var row in appRows)
            {
                appRowsPanel.Children.Add(row.View);
            }
            addApp.IsEnabled = appRows.Count < 16;
            Changed();
        }
        void AddAppRow(string initialExe, string initialSite)
        {
            var exe = initialExe;
            // A browser: optionally only while its current page is on a site (just the host).
            var siteBox = new TextBox
            {
                Header = "Website (optional)",
                PlaceholderText = "For example youtube.com or music.youtube.com",
                Text = initialSite,
                MaxLength = 253,
            };
            var siteNote = Caption("");
            var sitePanel = new StackPanel { Spacing = 4, Children = { siteBox, siteNote } };
            void ShowSite()
            {
                sitePanel.Visibility = NowPlaying.IsBrowser(exe) ? Visibility.Visible : Visibility.Collapsed;
                var host = NowPlaying.SiteHost(siteBox.Text);
                siteNote.Text = siteBox.Text.Trim().Length == 0
                    ? "Empty: any page. With a site, only while a window of this browser shows a page on it (its current tab)."
                    : host.Length == 0 ? "That isn't a web site's address: type its host, like youtube.com."
                    : host.Count(c => c == '.') == 1
                        ? $"Only while a window of this browser shows a page on {host} or one of its subdomains (like m.{host})."
                        : $"Only while a window of this browser shows a page on {host} or one of its subdomains.";
            }
            siteBox.TextChanged += (_, _) =>
            {
                ShowSite();
                Changed();
            };
            var picker = BuildAppPicker(exe, chosen =>
            {
                exe = chosen;
                ShowSite();
                Changed();
            });
            ShowSite();
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Top };
            ToolTipService.SetToolTip(remove, "Remove this app");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove this app");
            var body = new StackPanel { Spacing = 6, Children = { picker, sitePanel } };
            var view = new Grid { ColumnSpacing = 8 };
            view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            view.Children.Add(body);
            Grid.SetColumn(remove, 1);
            view.Children.Add(remove);
            var row = (Exe: (Func<string>)(() => exe.Trim()), Site: siteBox, View: (FrameworkElement)view);
            remove.Click += (_, _) =>
            {
                appRows.Remove(row);
                if (appRows.Count == 0)
                {
                    AddAppRow("", "");
                    return;
                }
                ShowAppRows();
            };
            appRows.Add(row);
            ShowAppRows();
        }
        AddAppRow(t.Source["app"]?.ToString() ?? "", t.Source["site"]?.ToString() ?? "");
        if (t.Source["apps"] is JsonArray moreApps)
        {
            foreach (var w in moreApps.OfType<JsonObject>())
            {
                AddAppRow(w["app"]?.ToString() ?? "", w["site"]?.ToString() ?? "");
            }
        }
        addApp.Click += (_, _) => AddAppRow("", "");
        var appPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                appRowsPanel,
                addApp,
                Caption("While any of these apps runs (or a browser shows one of the sites), its lighting replaces what the devices show; when none does, they go back."),
            },
        };
        // The rows with an app, as (program, site or ""): a site only for a browser.
        List<(string App, string Site)> Watches() => appRows
            .Where(r => r.Exe().Length > 0)
            .Select(r => (r.Exe(), NowPlaying.IsBrowser(r.Exe()) ? NowPlaying.SiteHost(r.Site.Text) : ""))
            .ToList();
        // A browser row whose site isn't a host.
        bool BadSite() => appRows.Any(r => NowPlaying.IsBrowser(r.Exe()) && r.Site.Text.Trim().Length > 0 && NowPlaying.SiteHost(r.Site.Text).Length == 0);
        // A trigger that still listens to the music shows what it hears.
        var whenSound = AddVisualizer(70);

        // ---------- timing, for all the effects ----------
        var durationMs = t.Action["duration_ms"] is JsonValue dv && dv.TryGetValue<int>(out var dms) ? Math.Clamp(dms, 50, 10000) : 400;
        var duration = new Slider { Header = "Duration", Minimum = 50, Maximum = 5000, StepFrequency = 50, Value = durationMs };
        var durationText = Caption("");
        // An effect can stay on for good once fired: until the trigger is turned off, changed or deleted.
        var forever = new CheckBox
        {
            Content = "No end: keep it on until the trigger is turned off",
            IsChecked = !isNew && t.ActionKind == "lighting" && t.SourceKind != "app_running" && t.Action["duration_ms"] is null,
        };
        var durationPanel = new StackPanel { Spacing = 0, Children = { forever, duration, durationText } };

        // Start where it happened (the pressed key / the mouse wheel) and reach only so far, ring by ring.
        var radiusOn = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, IsOn = isNew || t.Action["radius"] is JsonValue };
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

        // ---------- 2. Effects: each one with its devices ----------
        var blocks = new List<TriggerBlock>();
        var selected = -1; // -1: "When" is being edited; else the effect
        var filling = false;
        string KindOf(TriggerBlock b) => b.Effect.Selected is { } s && s.StartsWith("kind:", StringComparison.Ordinal) ? s[5..] : "lighting";
        string EffectOf(TriggerBlock b) => b.Effect.Selected is { } s && !s.StartsWith("kind:", StringComparison.Ordinal) ? s : "static";
        string PaletteOf(TriggerBlock b) => SoundPalettes[Math.Max(0, b.Palette.SelectedIndex)].Id;
        bool IsRest(TriggerBlock b) => b.Rest.IsChecked == true;
        // Every effect can mix with any other, on any device, for any trigger.
        bool CanAdd() => blocks.Count < 17;
        bool AnyKind(Func<string, bool> test) => blocks.Any(b => test(KindOf(b)));

        // What a block can be: a quick effect, any effect, or a preset.
        IEnumerable<(string Group, string Id, string Label)> EffectOptions()
        {
            yield return ("Quick effects", "kind:flash", "Flash a color");
            yield return ("Quick effects", "kind:pulse", "Pulse (brighten and back)");
            yield return ("Quick effects", "kind:ripple", "Ripple a color");
            yield return ("Quick effects", "kind:fade_in", "Fade in a color");
            foreach (var g in EffectGroups)
            {
                foreach (var e in Effects.Where(e => e.Group == g))
                {
                    yield return (g, e.Id, e.Name);
                }
            }
            yield return ("Other", "kind:preset", "Load a preset");
        }
        void FillEffects(TriggerBlock b)
        {
            if (b.Options.Length > 0)
            {
                return;
            }
            b.Options = "all";
            filling = true;
            b.Effect.Fill(EffectOptions(), b.Effect.Selected ?? b.Initial);
            filling = false;
        }

        // The block's devices (and groups) as targets; none for "all (the other) devices".
        List<string> TargetsOf(TriggerBlock b)
        {
            if (IsRest(b))
            {
                return [];
            }
            var list = new List<string>();
            list.AddRange(b.Groups.Where(g => g.Box.IsChecked == true).Select(g => g.Id));
            list.AddRange(b.Rows.SelectMany(r => r.Targets()));
            list.AddRange(b.Kept);
            return list.Distinct().ToList();
        }

        // A lighting block's state: as the service keeps it in the action and in each "per_target" entry.
        JsonObject StateOf(TriggerBlock b) => new()
        {
            ["effect"] = EffectOf(b),
            ["color"] = $"{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
            ["brightness"] = (int)b.Brightness.Value,
            ["speed"] = (int)b.Speed.Value,
            ["palette"] = PaletteOf(b),
            ["split"] = Splits[Math.Max(0, b.Split.SelectedIndex)].Id,
            ["colors"] = Math.Max(0, b.Colors.SelectedIndex),
        };

        // A block: <paramref name="state"/> holds its settings (the action, or a "per_target" entry).
        TriggerBlock NewBlock(JsonNode state, string want, IEnumerable<string> targets, bool rest, Dictionary<string, (string? Split, string? Palette)>? own = null)
        {
            var picked = new HashSet<string>(targets);
            var combo = new GroupedCombo();
            combo.Box.Header = "Effect";
            var blockColor = ParseHex(state["color"]?.ToString() ?? "") ?? white;
            TriggerBlock? self = null;

            // Its color: preset swatches, any other from the picker.
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = Res("CardStrokeColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center };
            var hexText = new TextBlock { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
            void SetColor(Color c)
            {
                blockColor = c;
                if (self is not null)
                {
                    self.Color = c;
                }
                swatch.Background = new SolidColorBrush(c);
                hexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                Changed();
            }
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
                b.Click += (_, _) => SetColor(c);
                swatches.Children.Add(b);
            }
            var colorPicker = new ColorPicker { ColorSpectrumShape = ColorSpectrumShape.Box, IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = true, Color = blockColor };
            colorPicker.ColorChanged += (_, e) => SetColor(Color.FromArgb(255, e.NewColor.R, e.NewColor.G, e.NewColor.B));
            var custom = new Button { Content = "Other color...", Flyout = new Flyout { Content = colorPicker } };
            var random = new CheckBox { Content = "Random color each time", IsChecked = state["random"] is JsonValue rnd && rnd.TryGetValue<bool>(out var rb) && rb };
            random.Checked += (_, _) => { swatches.Opacity = 0.4; Changed(); };
            random.Unchecked += (_, _) => { swatches.Opacity = 1; Changed(); };
            swatches.Opacity = random.IsChecked == true ? 0.4 : 1;
            var colorPanel = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Color" },
                    swatches,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, hexText, custom } },
                    random,
                },
            };

            var brightness = new Slider { Header = "Brightness", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = state["brightness"] is JsonValue lb && lb.TryGetValue<int>(out var lbi) ? lbi : 100 };
            var speed = new Slider { Header = "Speed", Minimum = 1, Maximum = 100, StepFrequency = 1, Value = state["speed"] is JsonValue ls && ls.TryGetValue<int>(out var lsi) ? lsi : 50 };
            // Sound effects: their colors can follow a palette (random per beat, cold to hot, ...).
            var palette = new ComboBox
            {
                Header = "Sound colors",
                ItemsSource = SoundPalettes.Select(p => p.Name).ToList(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Math.Max(0, Array.FindIndex(SoundPalettes, p => p.Id == state["palette"]?.ToString())),
            };
            var paletteTip = Caption("");
            // What one light is, and how many colors random uses.
            var split = new ComboBox
            {
                Header = "Lights per",
                ItemsSource = Splits.Select(x => x.Name).ToList(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Math.Max(0, Array.FindIndex(Splits, x => x.Id == (state["split"]?.ToString() ?? "device"))),
            };
            var colors = new ComboBox
            {
                Header = "Number of colors",
                ItemsSource = ColorCountNames,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = state["colors"] is JsonValue cv && cv.TryGetValue<int>(out var cn) ? Math.Clamp(cn, 0, 8) : 3,
            };
            var lightingPanel = new StackPanel { Spacing = 6, Children = { split, palette, paletteTip, colors, brightness, speed } };
            var preset = new ComboBox
            {
                Header = "Preset",
                ItemsSource = _profiles.ToList(),
                PlaceholderText = _profiles.Count == 0 ? "No presets yet (File > Presets)" : "Pick a preset",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = _profiles.Count > 0,
            };
            if (state["name"]?.ToString() is { } presetName && _profiles.Contains(presetName))
            {
                preset.SelectedItem = presetName;
            }

            // Its devices: one card each (the whole device, or single LEDs), groups, or "all (the other)
            // devices"; "Add a device" picks more.
            var restBox = new CheckBox { IsChecked = rest };
            var groups = _groupItems.Select(g => (g.Id, Box: new CheckBox { Content = g.Title, IsChecked = picked.Contains(g.Id) })).ToList();
            var rows = _devices.Select(d => own is not null && own.TryGetValue(d.Id, out var o) ? BuildDeviceRow(d, picked, o.Split, o.Palette) : BuildDeviceRow(d, picked)).ToList();
            var known = _devices.Select(d => d.Id).ToList();
            var kept = picked.Where(p => !p.StartsWith("group:", StringComparison.Ordinal)
                && !known.Any(k => p == k || p.StartsWith(k + "/", StringComparison.Ordinal) || p.StartsWith(k + "@", StringComparison.Ordinal))).ToList();
            // A card for "all (the other) devices" or a group: its icon, its name and a remove button.
            (Border Card, TextBlock Text) SimpleCard(string glyph, string text, CheckBox state)
            {
                var label = new TextBlock { Text = text, Style = SettingsUi.Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(remove, "Remove");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {text}");
                remove.Click += (_, _) => state.IsChecked = false;
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
                var card = new Border
                {
                    Padding = new Thickness(12, 8, 8, 8),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Res("CardStrokeColorDefaultBrush"),
                    Background = Res("LayerOnMicaBaseAltFillColorDefaultBrush"),
                    Child = grid,
                };
                return (card, label);
            }
            var (restCard, restText) = SimpleCard("", "All devices", restBox);
            var groupCards = groups.Select(g => (g.Box, SimpleCard("", $"Group: {g.Box.Content}", g.Box).Card)).ToList();
            var cardsPanel = new StackPanel { Spacing = 8 };
            cardsPanel.Children.Add(restCard);
            groupCards.ForEach(g => cardsPanel.Children.Add(g.Card));
            rows.ForEach(r => cardsPanel.Children.Add(r.Root));
            var devicesNote = Caption("");
            var addMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            var addDevice = new DropDownButton
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "Add a device" } } },
                Flyout = addMenu,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addDevice, "Add a device");
            // The menu lists what isn't on a card yet.
            addMenu.Opening += (_, _) =>
            {
                addMenu.Items.Clear();
                var anyRows = false;
                foreach (var r in rows.Where(r => r.Box.IsChecked != true))
                {
                    var row = r;
                    var item = new MenuFlyoutItem { Text = row.Device.Name, Icon = new FontIcon { Glyph = row.Device.Glyph } };
                    item.Click += (_, _) =>
                    {
                        restBox.IsChecked = false;
                        row.Box.IsChecked = true;
                    };
                    addMenu.Items.Add(item);
                    anyRows = true;
                }
                var freeGroups = groups.Where(g => g.Box.IsChecked != true).ToList();
                if (freeGroups.Count > 0 && anyRows)
                {
                    addMenu.Items.Add(new MenuFlyoutSeparator());
                }
                foreach (var (_, box) in freeGroups)
                {
                    var gb = box;
                    var item = new MenuFlyoutItem { Text = $"Group: {gb.Content}", Icon = new FontIcon { Glyph = "" } };
                    item.Click += (_, _) =>
                    {
                        restBox.IsChecked = false;
                        gb.IsChecked = true;
                    };
                    addMenu.Items.Add(item);
                }
                if (restBox.IsChecked != true)
                {
                    if (addMenu.Items.Count > 0)
                    {
                        addMenu.Items.Add(new MenuFlyoutSeparator());
                    }
                    var all = new MenuFlyoutItem { Text = blocks.Count > 1 ? "All the other devices" : "All devices", Icon = new FontIcon { Glyph = "" } };
                    all.Click += (_, _) => restBox.IsChecked = true;
                    addMenu.Items.Add(all);
                }
            };
            var devicesView = new StackPanel { Spacing = 10, Children = { cardsPanel, devicesNote, addDevice } };
            var view = new StackPanel
            {
                Spacing = 10,
                Children = { combo.Box, colorPanel, preset, lightingPanel },
            };

            void Refresh()
            {
                var b = self!;
                var kind = KindOf(b);
                var fx = EffectById(EffectOf(b));
                var audio = kind == "lighting" && fx.Audio;
                var pal = PaletteOf(b);
                var quickColor = kind is "flash" or "ripple" or "fade_in";
                colorPanel.Visibility = quickColor || kind == "lighting" && fx.UsesColor && !(audio && pal != "effect") ? Visibility.Visible : Visibility.Collapsed;
                random.Visibility = quickColor ? Visibility.Visible : Visibility.Collapsed;
                preset.Visibility = kind == "preset" ? Visibility.Visible : Visibility.Collapsed;
                lightingPanel.Visibility = kind == "lighting" ? Visibility.Visible : Visibility.Collapsed;
                split.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
                palette.Visibility = split.Visibility;
                paletteTip.Visibility = split.Visibility;
                paletteTip.Text = SoundPalettes[Math.Max(0, palette.SelectedIndex)].Tip + ".";
                colors.Visibility = audio && pal == "random" ? Visibility.Visible : Visibility.Collapsed;
                speed.Header = audio ? "Sensitivity" : "Speed";

                // The cards: "all (the other) devices" alone, or the devices and groups added.
                var all = restBox.IsChecked == true;
                restText.Text = blocks.Count > 1 ? "All the other devices (the ones no other effect lights)" : "All devices";
                restCard.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
                foreach (var (box, card) in groupCards)
                {
                    card.Visibility = !all && box.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                }
                foreach (var r in rows)
                {
                    r.Root.Visibility = !all && r.Box.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                    // A sound effect: each device can have its own "lights per" and colors.
                    r.SoundPanel.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
                }
                var none = !all && !rows.Any(r => r.Box.IsChecked == true) && !groups.Any(g => g.Box.IsChecked == true) && kept.Count == 0;
                devicesNote.Text = none ? "No device yet: add the devices that show this effect."
                    : all ? "Any device this trigger's other effects don't light shows this one."
                    : audio ? "For each device: the whole device or single LEDs, and its own \"lights per\" and colors (else the effect's)."
                    : "For each device: the whole device, or pick single LEDs.";
            }

            var block = new TriggerBlock
            {
                View = view,
                DevicesView = devicesView,
                Effect = combo,
                Color = blockColor,
                Random = random,
                Brightness = brightness,
                Speed = speed,
                Palette = palette,
                Split = split,
                Colors = colors,
                Preset = preset,
                Rest = restBox,
                Groups = groups,
                Rows = rows,
                Kept = kept,
                Refresh = Refresh,
                Initial = want,
            };
            self = block;
            SetColor(blockColor);
            combo.Box.SelectionChanged += (_, _) =>
            {
                if (!filling)
                {
                    Changed();
                }
            };
            palette.SelectionChanged += (_, _) => Changed();
            split.SelectionChanged += (_, _) => Changed();
            brightness.ValueChanged += (_, _) => Changed();
            preset.SelectionChanged += (_, _) => Changed();
            restBox.Checked += (_, _) =>
            {
                // One effect at most is for "the rest".
                foreach (var o in blocks.Where(o => o != block))
                {
                    o.Rest.IsChecked = false;
                }
                Changed();
            };
            restBox.Unchecked += (_, _) => Changed();
            foreach (var g in groups)
            {
                g.Box.Checked += (_, _) => Changed();
                g.Box.Unchecked += (_, _) => Changed();
            }
            rows.ForEach(r => r.Changed = Changed);
            return block;
        }

        // Load: the trigger's own effect (on its targets, or "the rest" with none; a lighting with
        // effects per target becomes one block per distinct effect), then each effect in "also"; an
        // effect saved "with_previous" is devices of the block before with their own sound settings.
        static string WantOf(JsonObject action) => action["kind"]?.ToString() is "lighting" or null
            ? action["effect"]?.ToString() ?? "static"
            : "kind:" + action["kind"];
        var specs = new List<(JsonObject State, string Want, List<string> Targets, bool ForRest, Dictionary<string, (string? Split, string? Palette)> Own)>();
        if (!isNew && t.ActionKind == "lighting")
        {
            var per = (t.Action["per_target"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var perTargets = per.Select(p => p["target"]?.ToString() ?? "").ToHashSet();
            var mainTargets = t.Targets.Where(x => !perTargets.Contains(x)).ToList();
            var restBlock = t.Targets.Count == 0;
            if (restBlock || mainTargets.Count > 0 || per.Count == 0)
            {
                specs.Add((t.Action, WantOf(t.Action), restBlock ? [] : mainTargets, restBlock, []));
            }
            static string Same(JsonObject p) => string.Join("|", new[] { "effect", "color", "brightness", "speed", "palette", "split", "colors" }.Select(k => p[k]?.ToJsonString() ?? ""));
            foreach (var g in per.GroupBy(Same))
            {
                specs.Add((g.First(), g.First()["effect"]?.ToString() ?? "static", [.. g.Select(p => p["target"]?.ToString() ?? "")], false, []));
            }
        }
        else
        {
            var want = isNew
                ? (startKind is "key_any" or "key" or "mouse_click" or "mouse_move" ? "kind:ripple" : "static")
                : WantOf(t.Action);
            // A new key / mouse trigger starts on the keyboards / mice.
            var startTargets = isNew && t.Targets.Count == 0 && startKind is "key_any" or "key" or "mouse_click" or "mouse_move"
                ? _devices.Where(d => DeviceSupports(d, startKind)).Select(d => d.Id).ToList()
                : t.Targets;
            specs.Add((t.Action, want, startTargets, startTargets.Count == 0, []));
        }
        foreach (var part in t.Also.OfType<JsonObject>())
        {
            var action = part["action"] as JsonObject ?? [];
            var targets = (part["targets"] as JsonArray)?.Select(x => x?.ToString()).OfType<string>().ToList() ?? [];
            if (part["with_previous"] is JsonValue wp && wp.TryGetValue<bool>(out var with) && with && specs.Count > 0 && !specs[^1].ForRest)
            {
                // Its devices join the block before, each with these settings where they differ.
                var prev = specs[^1];
                prev.Targets.AddRange(targets);
                var split = action["split"]?.ToString();
                var palette = action["palette"]?.ToString();
                foreach (var key in targets.Select(DeviceKey).Distinct())
                {
                    prev.Own[key] = (split == (prev.State["split"]?.ToString() ?? "device") ? null : split,
                        palette == (prev.State["palette"]?.ToString() ?? "effect") ? null : palette);
                }
                continue;
            }
            specs.Add((action, WantOf(action), targets, false, []));
        }
        foreach (var (state, want, targets, rest, own) in specs)
        {
            blocks.Add(NewBlock(state, want, targets, rest, own));
        }

        // Shown at the top when Save (in the title) finds something missing.
        var error = new TextBlock { Foreground = Res("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0) };

        // ---------- the cards: "When", or the selected effect and its devices ----------
        var whenCard = StepCard(1, "When", "What sets it off.", source, keysPanel, button, levelPanel, appPanel, whenSound.Root);

        var blockTitle = new TextBlock { Style = SettingsUi.Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        var addBlock = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "Add an effect" } } } };
        ToolTipService.SetToolTip(addBlock, "Another effect, for other devices");
        var removeBlock = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(removeBlock, "Remove this effect");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(removeBlock, "Remove this effect");
        var blockHead = new Grid { ColumnSpacing = 8 };
        blockHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        blockHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        blockHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        blockHead.Children.Add(blockTitle);
        Grid.SetColumn(addBlock, 1);
        blockHead.Children.Add(addBlock);
        Grid.SetColumn(removeBlock, 2);
        blockHead.Children.Add(removeBlock);
        var blockHost = new Border();
        // What LightSync hears now, painted like the selected sound effect's lights.
        var blockSound = AddVisualizer(70);
        var sharedTitle = new TextBlock { Text = "For all its effects", Style = SettingsUi.Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 8, 0, 0) };
        // Its title and subtitle describe the selected effect (name; family, colors, timing, devices).
        var effectTitle = new TextBlock { Text = "Effect" };
        var effectSubtitle = new TextBlock { Text = "What the lights do." };
        var blockCard = StepCard(2, effectTitle, effectSubtitle, blockHead, blockHost, blockSound.Root, sharedTitle, radiusPanel, durationPanel);
        var devicesHost = new Border();
        var devicesCard = StepCard(3, "Devices", "The devices that show this effect: the whole device, or single LEDs.", devicesHost);

        void AddBlock()
        {
            if (!CanAdd())
            {
                return;
            }
            var next = ParseHex(PresetColors[blocks.Count % PresetColors.Length].Hex) ?? white;
            var state = new JsonObject { ["color"] = $"{next.R:X2}{next.G:X2}{next.B:X2}", ["brightness"] = 100, ["speed"] = 50 };
            blocks.Add(NewBlock(state, "static", [], rest: false));
            selected = blocks.Count - 1;
            Changed();
        }
        addBlock.Click += (_, _) => AddBlock();
        removeBlock.Click += (_, _) =>
        {
            if (blocks.Count <= 1 || selected < 0)
            {
                return;
            }
            blocks.RemoveAt(selected);
            selected = Math.Min(selected, blocks.Count - 1);
            Changed();
        };


        // ---------- the map: when -> effects -> devices; click a box to change it ----------
        var map = new TriggerMap();
        map.WhenClicked += () =>
        {
            selected = -1;
            Changed();
        };
        map.EffectClicked += i =>
        {
            selected = Math.Clamp(i, 0, blocks.Count - 1);
            Changed();
        };
        map.AddClicked += AddBlock;
        var mapCard = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                CardHeader("At a glance"),
                Caption("Build the trigger here: click \"When\" to pick what sets it off, click an effect to pick it and its devices, or add an effect to light other devices differently."),
                map.Root,
            },
        });
        // The device (or group) a target belongs to: a zone, fan or LED goes with its device.
        string DeviceKey(string target)
        {
            if (target.StartsWith("group:", StringComparison.Ordinal))
            {
                return target;
            }
            var d = _devices.FirstOrDefault(d => target == d.Id || target.StartsWith(d.Id + "/", StringComparison.Ordinal) || target.StartsWith(d.Id + "@", StringComparison.Ordinal));
            return d?.Id ?? target;
        }
        string DeviceName(string key) => key.StartsWith("group:", StringComparison.Ordinal)
            ? "Group: " + (_groupItems.FirstOrDefault(x => x.Id == key)?.Title ?? key)
            : _devices.FirstOrDefault(d => d.Id == key)?.Name ?? key;
        // A device on the map: its name, the part the trigger uses and its icon.
        MapDevice MapDeviceOf(string key, List<string> targets)
        {
            var dev = _devices.FirstOrDefault(d => d.Id == key);
            var leds = targets.Count(x => x.Contains('@'));
            var zones = targets.Count(x => x.Contains('/'));
            var part = key.StartsWith("group:", StringComparison.Ordinal) ? "Group"
                : leds > 0 ? (leds == 1 ? "1 LED" : $"{leds} LEDs")
                : zones > 0 ? (zones == 1 ? "1 zone" : $"{zones} zones")
                : "Whole device";
            return new MapDevice(DeviceName(key), part, dev?.Glyph ?? "");
        }
        // A line color for an effect: its own color, or the middle of its picture; never too dark to see.
        static Color LineColor(Brush b)
        {
            var c = b switch
            {
                SolidColorBrush s => s.Color,
                LinearGradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[g.GradientStops.Count / 2].Color,
                _ => Color.FromArgb(255, 136, 136, 136),
            };
            return Math.Max(c.R, Math.Max(c.G, c.B)) < 90 ? Color.FromArgb(255, 136, 136, 136) : c;
        }
        // A block's name and look on the map: "Static #FF0000", "Ripple (random color)", "Preset: Night"...
        (string Name, Brush Look) Look(TriggerBlock b)
        {
            var hex = $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            var kind = KindOf(b);
            if (kind == "lighting")
            {
                var fx = EffectById(EffectOf(b));
                return (fx.UsesColor && !(fx.Audio && PaletteOf(b) != "effect") ? $"{fx.Name} {hex}" : fx.Name, EffectBrush(fx.Id, b.Color));
            }
            var label = kind switch { "flash" => "Flash", "ripple" => "Ripple", "fade_in" => "Fade in", "pulse" => "Pulse", _ => "Preset" };
            return kind switch
            {
                "pulse" => (label, Res("AccentFillColorDefaultBrush")),
                "preset" => (b.Preset.SelectedItem is string p ? $"Preset: {p}" : "Preset", Res("AccentFillColorDefaultBrush")),
                _ when b.Random.IsChecked == true => ($"{label} (random color)", EffectBrush("rainbow_wave", b.Color)),
                _ => ($"{label} {hex}", new SolidColorBrush(b.Color)),
            };
        }
        void UpdateMap()
        {
            var built = Build();
            var taken = blocks.Where(b => !IsRest(b)).SelectMany(TargetsOf).Select(DeviceKey).ToHashSet();
            var effects = new List<MapEffect>();
            foreach (var b in blocks)
            {
                List<MapDevice> devices = IsRest(b)
                    ? [.. _devices.Where(d => !taken.Contains(d.Id)).Select(d => MapDeviceOf(d.Id, [d.Id]))]
                    : [.. TargetsOf(b).GroupBy(DeviceKey).Select(g => MapDeviceOf(g.Key, g.ToList()))];
                var (title, look) = Look(b);
                effects.Add(new MapEffect(title, look, LineColor(look), devices, IsRest(b)));
            }
            map.Show(SourceGlyph(built.SourceKind), DescribeSource(built.Source), effects, selected, CanAdd(),
                CanAdd() ? "For other devices" : "17 at most");
        }

        void ShowParts()
        {
            var s = SourceKind();
            var keyish = s is "key_any" or "key";
            var mouseish = s is "mouse_click" or "mouse_move";
            keysPanel.Visibility = s == "key" ? Visibility.Visible : Visibility.Collapsed;
            button.Visibility = s == "mouse_click" ? Visibility.Visible : Visibility.Collapsed;
            levelPanel.Visibility = s == "audio_level" ? Visibility.Visible : Visibility.Collapsed;
            appPanel.Visibility = s == "app_running" ? Visibility.Visible : Visibility.Collapsed;
            radiusPanel.Visibility = (keyish || mouseish) && AnyKind(k => k != "preset") ? Visibility.Visible : Visibility.Collapsed;
            radiusTitle.Text = keyish ? "Start at the pressed key and only reach this far" : "Start at the mouse wheel and only reach this far";
            radius.IsEnabled = radiusOn.IsOn;
            var r = (int)radius.Value;
            radiusText.Text = !radiusOn.IsOn ? "Off: all the chosen lights at once."
                : keyish ? (r == 0 ? "Radius 0: only the key you press." : $"Radius {r}: the key you press and {r} key{(r == 1 ? "" : "s")} around it.")
                : (r == 0 ? "Radius 0: only the wheel." : $"Radius {r}: the wheel and {r} LED{(r == 1 ? "" : "s")} on from it.");
            var rings = radiusOn.IsOn && radius.Value >= 1 && AnyKind(k => k is not "lighting" and not "preset");
            layerDelay.Visibility = rings ? Visibility.Visible : Visibility.Collapsed;
            layerDelayText.Visibility = layerDelay.Visibility;
            var dl = (int)layerDelay.Value;
            layerDelayText.Text = dl == 0 ? "0 ms: every ring lights at once." : $"Each ring lights {dl} ms after the one inside it.";
            // How long: the quick effects play for the duration; an effect too, or with no end (an app's
            // shows while it runs).
            var quick = AnyKind(k => k is "flash" or "pulse" or "ripple" or "fade_in");
            var timedEffect = s != "app_running" && AnyKind(k => k == "lighting");
            durationPanel.Visibility = quick || timedEffect ? Visibility.Visible : Visibility.Collapsed;
            forever.Visibility = timedEffect ? Visibility.Visible : Visibility.Collapsed;
            var endless = timedEffect && forever.IsChecked == true;
            duration.Visibility = endless && !quick ? Visibility.Collapsed : Visibility.Visible;
            durationText.Text = endless
                ? (quick ? $"{FormatMs((int)duration.Value)} for the quick effects; the others stay on until you turn this trigger off, change it or delete it." : "Once it plays, it stays on until you turn this trigger off, change it or delete it.")
                : FormatMs((int)duration.Value);
            sharedTitle.Visibility = radiusPanel.Visibility == Visibility.Visible || durationPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            sharedTitle.Text = blocks.Count > 1 ? "For all its effects" : "How it plays";

            foreach (var b in blocks)
            {
                FillEffects(b);
                b.Refresh();
            }

            // The cards being edited: "When", or the selected effect and its devices.
            selected = Math.Min(selected, blocks.Count - 1);
            whenCard.Visibility = selected < 0 ? Visibility.Visible : Visibility.Collapsed;
            blockCard.Visibility = selected < 0 ? Visibility.Collapsed : Visibility.Visible;
            devicesCard.Visibility = blockCard.Visibility;
            var current = selected >= 0 ? blocks[selected] : null;
            if (current is not null && blockHost.Child != current.View)
            {
                blockHost.Child = current.View;
                devicesHost.Child = current.DevicesView;
            }
            blockTitle.Text = blocks.Count > 1 ? $"Effect {selected + 1} of {blocks.Count}" : "Effect";
            if (current is not null)
            {
                (effectTitle.Text, effectSubtitle.Text) = Details(current, s);
            }
            addBlock.IsEnabled = CanAdd();
            removeBlock.Visibility = blocks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            var soundEffect = current is not null && KindOf(current) == "lighting" && EffectById(EffectOf(current)).Audio;
            blockSound.Root.Visibility = soundEffect ? Visibility.Visible : Visibility.Collapsed;
            blockSound.Polling = soundEffect;
            if (current is not null)
            {
                blockSound.Recolor(current.Color, PaletteOf(current));
            }
            var musicSource = s is "audio_beat" or "audio_level";
            whenSound.Root.Visibility = musicSource ? Visibility.Visible : Visibility.Collapsed;
            whenSound.Polling = musicSource && selected < 0;
            UpdateSoundViews();

            if (s != "key" && record.IsChecked == true)
            {
                record.IsChecked = false;
            }
            UpdateMap();
        }
        source.SelectionChanged += (_, _) => Changed();
        button.SelectionChanged += (_, _) => Changed();
        radiusOn.Toggled += (_, _) => Changed();
        radius.ValueChanged += (_, _) => Changed();
        layerDelay.ValueChanged += (_, _) => Changed();
        forever.Checked += (_, _) => Changed();
        forever.Unchecked += (_, _) => Changed();
        duration.ValueChanged += (_, _) => Changed();
        refresh = ShowParts;
        ShowParts();

        var form = new StackPanel
        {
            Spacing = 12,
            Children = { error, nameCard, mapCard, whenCard, blockCard, devicesCard },
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

        // A block as effects for the service: one, or (a sound effect whose devices have their own "lights
        // per" or colors) one for the devices that use the block's settings, then one per other setting.
        List<(JsonObject Action, List<string> Targets)> UnitsOf(TriggerBlock b, string s)
        {
            var all = TargetsOf(b);
            if (KindOf(b) != "lighting" || !EffectById(EffectOf(b)).Audio || IsRest(b))
            {
                return [(ActionOf(b, s), all)];
            }
            var baseSplit = Splits[Math.Max(0, b.Split.SelectedIndex)].Id;
            var basePalette = PaletteOf(b);
            var groups = b.Rows.Where(r => r.Box.IsChecked == true)
                .Select(r => (Row: r, Split: r.SplitId ?? baseSplit, Palette: r.PaletteId ?? basePalette))
                .Where(x => x.Split != baseSplit || x.Palette != basePalette)
                .GroupBy(x => (x.Split, x.Palette))
                .ToList();
            if (groups.Count == 0)
            {
                return [(ActionOf(b, s), all)];
            }
            var ownTargets = groups.SelectMany(g => g.SelectMany(x => x.Row.Targets())).ToHashSet();
            var units = new List<(JsonObject Action, List<string> Targets)>();
            var shared = all.Where(x => !ownTargets.Contains(x)).ToList();
            if (shared.Count > 0)
            {
                units.Add((ActionOf(b, s), shared));
            }
            foreach (var g in groups)
            {
                var action = ActionOf(b, s);
                action["split"] = g.Key.Split;
                action["palette"] = g.Key.Palette;
                var mine = g.SelectMany(x => x.Row.Targets()).ToList();
                if (mine.Count > 0)
                {
                    units.Add((action, mine));
                }
            }
            return units.Count > 0 ? units : [(ActionOf(b, s), all)];
        }

        // The effect card's title and subtitle for a block: its name, then what it is and does.
        (string Title, string Subtitle) Details(TriggerBlock b, string s)
        {
            var kind = KindOf(b);
            var hex = $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            var parts = new List<string>();
            var app = s == "app_running";
            string title;
            if (kind == "lighting")
            {
                var fx = EffectById(EffectOf(b));
                title = fx.Name;
                parts.Add(fx.Audio ? "Sound reactive effect" : $"{fx.Group} effect");
                if (fx.Audio)
                {
                    parts.Add("reacts to the sound LightSync hears");
                    parts.Add($"colors: {SoundPalettes[Math.Max(0, b.Palette.SelectedIndex)].Name.ToLowerInvariant()}");
                    parts.Add($"lights per {Splits[Math.Max(0, b.Split.SelectedIndex)].Name.ToLowerInvariant()}");
                }
                else
                {
                    parts.Add(fx.UsesColor ? $"color {hex}" : "its own colors");
                }
                parts.Add($"brightness {(int)b.Brightness.Value}%");
                parts.Add(app ? "shows while the app runs" : forever.IsChecked == true ? "stays on once it plays" : $"plays for {FormatMs((int)duration.Value)}");
            }
            else if (kind == "preset")
            {
                title = b.Preset.SelectedItem is string name ? $"Preset: {name}" : "Load a preset";
                parts.Add("Loads a saved preset on every device");
            }
            else
            {
                title = kind switch { "flash" => "Flash", "pulse" => "Pulse", "ripple" => "Ripple", _ => "Fade in" };
                parts.Add("Quick effect");
                parts.Add(kind switch
                {
                    "flash" => "the color, fading out",
                    "pulse" => "brightens the lights and back",
                    "ripple" => "a wave of the color from the middle out",
                    _ => "a glow that builds while it repeats",
                });
                if (kind != "pulse")
                {
                    parts.Add(b.Random.IsChecked == true ? "a random color each time" : $"color {hex}");
                }
                parts.Add($"plays for {FormatMs((int)duration.Value)}");
                if (app)
                {
                    parts.Add("when the app starts");
                }
            }
            if (kind != "preset")
            {
                var devices = TargetsOf(b).Select(DeviceKey).Distinct().Count();
                parts.Add(IsRest(b) ? (blocks.Count > 1 ? "on all the other devices" : "on every device")
                    : devices switch { 0 => "no devices yet", 1 => "on 1 device", _ => $"on {devices} devices" });
            }
            return (title, string.Join(" · ", parts) + ".");
        }

        // One block as the service keeps an effect: its kind and settings, with the trigger's timing.
        JsonObject ActionOf(TriggerBlock b, string s)
        {
            var a = KindOf(b);
            var act = new JsonObject { ["kind"] = a };
            var app = s == "app_running";
            switch (a)
            {
                case "lighting":
                    foreach (var (k, v) in StateOf(b))
                    {
                        act[k] = v?.DeepClone();
                    }
                    if (!app)
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
                    break;
                case "preset":
                    act["name"] = b.Preset.SelectedItem as string ?? "";
                    break;
                default:
                    if (a is "flash" or "ripple" or "fade_in")
                    {
                        act["color"] = $"{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
                        if (b.Random.IsChecked == true)
                        {
                            act["random"] = true;
                        }
                    }
                    act["duration_ms"] = (int)duration.Value;
                    if (!app && Local())
                    {
                        act["radius"] = Math.Round(radius.Value);
                        if (radius.Value >= 1 && layerDelay.Value > 0)
                        {
                            act["layer_delay_ms"] = (int)layerDelay.Value;
                        }
                    }
                    break;
            }
            return act;
        }

        // Save: the trigger's source, its own effect and the others in "also".
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
                    // The first app (and site) in "app" / "site", the others in "apps".
                    var watches = Watches();
                    src["app"] = watches.Count > 0 ? watches[0].App : "";
                    if (watches.Count > 0 && watches[0].Site.Length > 0)
                    {
                        src["site"] = watches[0].Site;
                    }
                    if (watches.Count > 1)
                    {
                        src["apps"] = new JsonArray([.. watches.Skip(1).Select(w => (JsonNode)(w.Site.Length > 0
                            ? new JsonObject { ["app"] = w.App, ["site"] = w.Site }
                            : new JsonObject { ["app"] = w.App }))]);
                    }
                    break;
            }
            // The effect for "the rest" (on no targets: every device) is the trigger's own, under the
            // others; else the first one is. The others go in "also", each on its own devices.
            // A block whose devices have their own sound settings is saved as one effect per setting,
            // the later ones "with_previous".
            var main = blocks.FirstOrDefault(IsRest) ?? blocks[0];
            var mainUnits = UnitsOf(main, s);
            var act = mainUnits[0].Action;
            var targets = mainUnits[0].Targets;
            var also = new JsonArray();
            void AddUnit((JsonObject Action, List<string> Targets) u, bool withPrevious)
            {
                var part = new JsonObject
                {
                    ["action"] = u.Action,
                    ["targets"] = new JsonArray([.. u.Targets.Select(x => (JsonNode)JsonValue.Create(x))]),
                };
                if (withPrevious)
                {
                    part["with_previous"] = true;
                }
                also.Add(part);
            }
            foreach (var u in mainUnits.Skip(1))
            {
                AddUnit(u, true);
            }
            foreach (var b in blocks.Where(b => b != main))
            {
                var units = UnitsOf(b, s);
                for (var i = 0; i < units.Count; i++)
                {
                    AddUnit(units[i], i > 0);
                }
            }
            var result = new TriggerInfo
            {
                Id = t.Id,
                Name = name.Text.Trim(),
                Enabled = t.Enabled,
                Source = src,
                Action = act,
                Targets = targets,
                Also = also,
            };
            if (result.Name.Length > 64)
            {
                result.Name = result.Name[..64].TrimEnd(); // the service takes 1..64 characters
            }
            return result;
        }


        // Two effects on the same light: the first such pair, as "Keyboard is in effect 1 and effect 2".
        string? Overlap()
        {
            static bool Covers(string a, string b) => a == b || b.StartsWith(a + "/", StringComparison.Ordinal) || b.StartsWith(a + "@", StringComparison.Ordinal);
            for (var i = 0; i < blocks.Count; i++)
            {
                for (var j = i + 1; j < blocks.Count; j++)
                {
                    foreach (var x in TargetsOf(blocks[i]))
                    {
                        if (TargetsOf(blocks[j]).FirstOrDefault(y => Covers(x, y) || Covers(y, x)) is { } y)
                        {
                            return $"{DeviceName(DeviceKey(x.Length <= y.Length ? x : y))} is in effect {i + 1} and effect {j + 1}: give each device (or zone, or LED) one effect.";
                        }
                    }
                }
            }
            return null;
        }

        save.Click += (_, _) => Safe("saving a trigger", async () =>
        {
            save.IsEnabled = false;
            var before = _triggers.Select(x => x.Id).OfType<string>().ToHashSet();
            try
            {
                var s = SourceKind();
                string? problem = null;
                var go = selected; // the card to show with the problem
                var empty = blocks.FindIndex(b => !IsRest(b) && TargetsOf(b).Count == 0);
                if (name.Text.Trim().Length == 0)
                {
                    problem = "Give the trigger a name (at the top).";
                }
                else if (s == "key" && keys.Count == 0)
                {
                    problem = "Record at least one key, or pick \"A key is pressed\".";
                    go = -1;
                }
                else if (s == "app_running" && BadSite())
                {
                    problem = "Type the web site's host, like youtube.com, or leave Website empty.";
                    go = -1;
                }
                else if (s == "app_running" && Watches().Count == 0)
                {
                    problem = "Pick the app, or type its program file (for example game.exe).";
                    go = -1;
                }
                else if (blocks.FindIndex(b => KindOf(b) == "preset" && b.Preset.SelectedItem is null) is var noPreset and >= 0)
                {
                    problem = _profiles.Count == 0 ? "Save a preset first (File > Presets), or pick another effect." : "Pick the preset to load.";
                    go = noPreset;
                }
                else if (empty >= 0)
                {
                    var what = blocks.Count > 1 ? $"effect {empty + 1}" : "the effect";
                    problem = blocks[empty].Rows.Any(r => r.Box.IsChecked == true)
                        ? $"Pick at least one LED for the devices of {what} set to \"Single LEDs\"."
                        : $"Add at least one device for {what}.";
                    go = empty;
                }
                else if (Overlap() is { } both)
                {
                    problem = both;
                }
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    if (go != selected)
                    {
                        selected = go;
                        Changed();
                    }
                    if (name.Text.Trim().Length == 0)
                    {
                        name.Focus(FocusState.Programmatic);
                    }
                    return;
                }
                error.Visibility = Visibility.Collapsed;
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
    /// One device of an effect, as a card: its icon and name, which of its lights (the whole device, or
    /// single LEDs: a key grid for keyboards, loaded from the service the first time) and a remove
    /// button. <see cref="TriggerDeviceRow.Box"/> holds whether it is added (the card shows while it is).
    /// A device a trigger already lights by zones also offers "Some zones / fans".
    /// </summary>
    private TriggerDeviceRow BuildDeviceRow(DeviceInfo d, HashSet<string> picked, string? ownSplit = null, string? ownPalette = null)
    {
        var zoneIds = (d.Zones ?? []).SelectMany(z => z.Fans.Count > 0 ? z.Fans.Select(f => (f.Id, Name: $"{z.Name} / {f.Name}")) : [(z.Id, z.Name)]).ToList();
        var usesZones = picked.Any(p => p.StartsWith(d.Id + "/"));
        var usesLeds = picked.Any(p => p.StartsWith(d.Id + "@"));
        var box = new CheckBox { IsChecked = picked.Contains(d.Id) || usesZones || usesLeds };
        int[] modes = usesZones ? [0, 1, 2] : [0, 2];
        var mode = new ComboBox
        {
            ItemsSource = modes.Select(m => m switch { 0 => "Whole device", 1 => "Some zones / fans", _ => "Per LED" }).ToList(),
            SelectedIndex = Array.IndexOf(modes, usesLeds ? 2 : usesZones ? 1 : 0),
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, $"{d.Name}: which lights");
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(remove, "Remove this device");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {d.Name}");
        remove.Click += (_, _) => box.IsChecked = false;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = d.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(new TextBlock { Text = d.KindLabel, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Res("TextFillColorSecondaryBrush") });
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new FontIcon { Glyph = d.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        Grid.SetColumn(mode, 2);
        head.Children.Add(mode);
        Grid.SetColumn(remove, 3);
        head.Children.Add(remove);

        // Sound effects: its own "lights per" and colors, else the effect's.
        var split = new ComboBox
        {
            Header = "Lights per",
            ItemsSource = new List<string> { "As the effect" }.Concat(Splits.Select(x => x.Name)).ToList(),
            SelectedIndex = ownSplit is null ? 0 : Array.FindIndex(Splits, x => x.Id == ownSplit) + 1,
            MinWidth = 180,
        };
        var palette = new ComboBox
        {
            Header = "Sound colors",
            ItemsSource = new List<string> { "As the effect" }.Concat(SoundPalettes.Select(x => x.Name)).ToList(),
            SelectedIndex = ownPalette is null ? 0 : Array.FindIndex(SoundPalettes, x => x.Id == ownPalette) + 1,
            MinWidth = 180,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(split, $"{d.Name}: lights per");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(palette, $"{d.Name}: sound colors");
        var soundPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(28, 0, 0, 0), Visibility = Visibility.Collapsed, Children = { split, palette } };
        var zonesPanel = new StackPanel { Spacing = 2, Margin = new Thickness(28, 0, 0, 0) };
        var ledsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };
        var root = new Border
        {
            Padding = new Thickness(12, 8, 8, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("LayerOnMicaBaseAltFillColorDefaultBrush"),
            Child = new StackPanel { Spacing = 8, Children = { head, soundPanel, zonesPanel, ledsPanel } },
        };
        var row = new TriggerDeviceRow { Device = d, Root = root, Box = box, Mode = mode, Modes = modes, SoundPanel = soundPanel, Split = split, Palette = palette };
        split.SelectionChanged += (_, _) => row.Changed?.Invoke();
        palette.SelectionChanged += (_, _) => row.Changed?.Invoke();
        foreach (var (id, zname) in zoneIds)
        {
            var zb = new CheckBox { Content = zname, IsChecked = picked.Contains(id) || picked.Any(p => p.StartsWith(id + "/")) };
            zb.Checked += (_, _) => row.Changed?.Invoke();
            zb.Unchecked += (_, _) => row.Changed?.Invoke();
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
            zonesPanel.Visibility = on && row.ModeOf == 1 ? Visibility.Visible : Visibility.Collapsed;
            ledsPanel.Visibility = on && row.ModeOf == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (on && row.ModeOf == 2 && !row.LedsLoaded)
            {
                row.LedsLoaded = true;
                Safe("listing a device's LEDs", () => FillLedPickerAsync(row, ledsPanel));
            }
        }
        void ShowAndTell()
        {
            Show();
            row.Changed?.Invoke();
        }
        box.Checked += (_, _) => ShowAndTell();
        box.Unchecked += (_, _) => ShowAndTell();
        mode.SelectionChanged += (_, _) => ShowAndTell();
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
            b.Checked += (_, _) => { row.Leds.Add(l.Index); ShowCount(); row.Changed?.Invoke(); };
            b.Unchecked += (_, _) => { row.Leds.Remove(l.Index); ShowCount(); row.Changed?.Invoke(); };
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
