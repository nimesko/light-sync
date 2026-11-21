using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightSync.App;

/// <summary>What the right side of the window shows below the preview.</summary>
public enum ShellView
{
    /// <summary>An overview of what is set up: at start, and when nothing is selected.</summary>
    Home,
    /// <summary>The selected device / zone / fan / group's lighting (effect, color, brightness).</summary>
    Device,
    /// <summary>One trigger's configuration, picked in the Triggers section.</summary>
    Trigger,
}

/// <summary>
/// The app shell: Home, a device's lighting, or a trigger's configuration on the right; the left column
/// holds Home, the devices and the triggers. Home summarises the setup in a few cards.
/// </summary>
public sealed partial class MainWindow
{
    private ShellView _view = ShellView.Home;

    private void Home_Click(object sender, RoutedEventArgs e) => ShowView(ShellView.Home);

    /// <summary>Switch the right side to <paramref name="view"/>, clearing the other lists' selection.</summary>
    private void ShowView(ShellView view)
    {
        var changed = _view != view;
        _view = view;
        HomePane.Visibility = view == ShellView.Home ? Visibility.Visible : Visibility.Collapsed;
        // The 3D preview belongs to Home; Arrange mode ends when leaving it.
        PreviewCard.Visibility = view == ShellView.Home ? Visibility.Visible : Visibility.Collapsed;
        if (view != ShellView.Home && ArrangeToggle.IsChecked == true)
        {
            ArrangeToggle.IsChecked = false;
            _preview.EditMode = false;
            ResetLayoutButton.Visibility = Visibility.Collapsed;
        }
        DevicePane.Visibility = view == ShellView.Device ? Visibility.Visible : Visibility.Collapsed;
        TriggerPane.Visibility = view == ShellView.Trigger ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.Style = view == ShellView.Home ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
        _loading = true;
        if (view != ShellView.Device)
        {
            TargetList.SelectedItem = null;
        }
        if (view != ShellView.Trigger)
        {
            TriggerList.SelectedItem = null;
        }
        _loading = false;
        if (view != ShellView.Trigger)
        {
            _editingTrigger = null;
            TriggerPane.Children.Clear();
        }
        if (view == ShellView.Device && changed)
        {
            // Show the current target as selected in the list again.
            _loading = true;
            TargetList.SelectedItem = _selection.Count > 1 ? null : Rows.FirstOrDefault(t => t.Id == _target.Id);
            _loading = false;
        }
        if (view == ShellView.Home)
        {
            BuildHome();
        }
        HighlightPreview();
        UpdatePreviewActivity();
    }

    /// <summary>The 3D preview only runs while it can be seen: on Home, with the window on screen.</summary>
    private void UpdatePreviewActivity() => _preview.SetActive(_view == ShellView.Home && IsOnScreen);

    /// <summary>Outline in the preview what the right side is about: the selected parts, the trigger's targets, or nothing on Home.</summary>
    private void HighlightPreview()
    {
        IEnumerable<string> ids = _view switch
        {
            ShellView.Device => TargetIds,
            ShellView.Trigger when _editingTrigger is { } t => t.Targets.Count == 0 ? ["all"] : t.Targets,
            _ => [],
        };
        _preview.Highlight(ids);
    }

    /// <summary>Rebuild Home when it is shown (after devices, triggers, the preset or the lights changed).</summary>
    private void RefreshHome()
    {
        if (_view == ShellView.Home && !_exiting)
        {
            BuildHome();
        }
    }

    private static Border Card(UIElement content) => new()
    {
        Background = Res("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Res("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(20, 16, 20, 16),
        Child = content,
    };

    private static TextBlock Text(string text, string style = "BodyTextBlockStyle", bool secondary = false)
    {
        var t = new TextBlock { Text = text, Style = (Style)Application.Current.Resources[style], TextWrapping = TextWrapping.Wrap };
        if (secondary)
        {
            t.Foreground = Res("TextFillColorSecondaryBrush"); // otherwise keep the theme's text colour
        }
        return t;
    }

    /// <summary>A small clickable chip: an icon and a name.</summary>
    private static Button Chip(string glyph, string label, Action click, Brush? iconBrush = null)
    {
        var b = new Button
        {
            Padding = new Thickness(10, 5, 12, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 13, Foreground = iconBrush ?? Res("AccentTextFillColorPrimaryBrush") },
                    new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static Grid CardHeader(string title, UIElement? right = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.Children.Add(Text(title, "BodyStrongTextBlockStyle"));
        if (right is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Right;
            fe.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(fe);
        }
        return g;
    }

    private void OpenDevice(string id)
    {
        if (Rows.FirstOrDefault(t => t.Id == id) is { } item)
        {
            ShowView(ShellView.Device);
            Safe("selecting", () => SelectAsync(item));
        }
    }

    /// <summary>Home: the setup, the lighting, the triggers, each in a few lines with a way in.</summary>
    private void BuildHome()
    {
        HomePane.Children.Clear();
        // Setup: connection, devices (each opens its lighting), hidden ones.
        var setup = new StackPanel { Spacing = 8 };
        setup.Children.Add(CardHeader("Your setup", Text(StatusText.Text, "CaptionTextBlockStyle", secondary: true)));
        if (_devices.Count == 0)
        {
            setup.Children.Add(Text("No devices yet. LightSync finds them through OpenRGB; start it from the status bar below.", secondary: true));
        }
        else
        {
            setup.Children.Add(Text(_devices.Count == 1 ? "1 device, click it to change its lighting:" : $"{_devices.Count} devices, click one to change its lighting:", "CaptionTextBlockStyle", secondary: true));
            var flow = new ItemsRepeaterLikePanel();
            foreach (var d in _devices)
            {
                var id = d.Id;
                flow.Add(Chip(d.Glyph, d.Name, () => OpenDevice(id)));
            }
            setup.Children.Add(flow.Element);
            var hiddenDevices = _allDevices.Count(d => d.Hidden);
            var hiddenZones = _settings.HiddenZones?.Count ?? 0;
            if (hiddenDevices + hiddenZones > 0)
            {
                var parts = new List<string>();
                if (hiddenDevices > 0)
                {
                    parts.Add(hiddenDevices == 1 ? "1 hidden device" : $"{hiddenDevices} hidden devices");
                }
                if (hiddenZones > 0)
                {
                    parts.Add(hiddenZones == 1 ? "1 hidden zone" : $"{hiddenZones} hidden zones");
                }
                setup.Children.Add(Text($"{string.Join(" and ", parts)} (Settings > Devices shows them again).", "CaptionTextBlockStyle", secondary: true));
            }
        }
        HomePane.Children.Add(Card(setup));

        // Lighting: what "All devices" shows, the preset, on or off.
        var lighting = new StackPanel { Spacing = 10 };
        var change = new Button { Content = "Change lighting" };
        change.Click += (_, _) => OpenDevice("all");
        lighting.Children.Add(CardHeader("Lighting", change));
        var all = _targets.FirstOrDefault(t => t.Kind == TargetKind.All);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (all?.StateBrush is { } brush)
        {
            row.Children.Add(new Border { Width = 56, Height = 20, CornerRadius = new CornerRadius(10), Background = brush, BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Text($"All devices: {all.StateText}", "BodyTextBlockStyle"));
        }
        else
        {
            row.Children.Add(Text("All devices", "BodyTextBlockStyle"));
        }
        lighting.Children.Add(row);
        lighting.Children.Add(Text(
            $"{(_on ? "Lights on" : "Lights off")} · {(_currentPreset is null ? "No preset loaded (custom lighting)" : $"Preset: {_currentPreset}")}"
            + (_profiles.Count > 0 ? $" · {_profiles.Count} saved preset{(_profiles.Count == 1 ? "" : "s")} in File > Presets" : ""),
            "CaptionTextBlockStyle", secondary: true));
        HomePane.Children.Add(Card(lighting));

        // Triggers: how many, which are on; each opens its configuration.
        var triggers = new StackPanel { Spacing = 8 };
        var add = new Button { Content = "Add trigger" };
        add.Click += (_, _) => OpenTrigger(new TriggerInfo { Targets = [] }, isNew: true);
        triggers.Children.Add(CardHeader("Triggers", add));
        if (!_triggersSupported)
        {
            triggers.Children.Add(Text("Triggers need a newer LightSync service (lightsyncd).", secondary: true));
        }
        else if (_triggers.Count == 0)
        {
            triggers.Children.Add(Text("None yet. A trigger plays a light effect when you press a key, click, or the music beats.", secondary: true));
        }
        else
        {
            var on = _triggers.Count(t => t.Enabled);
            triggers.Children.Add(Text($"{_triggers.Count} trigger{(_triggers.Count == 1 ? "" : "s")}, {on} on:", "CaptionTextBlockStyle", secondary: true));
            var flow = new ItemsRepeaterLikePanel();
            foreach (var t in _triggers)
            {
                var trigger = t;
                flow.Add(Chip(SourceGlyph(t.SourceKind), (t.Enabled ? "" : "(off) ") + TriggerName(t), () => OpenTrigger(trigger, isNew: false),
                    t.Enabled ? null : Res("TextFillColorTertiaryBrush")));
            }
            triggers.Children.Add(flow.Element);
        }
        HomePane.Children.Add(Card(triggers));
    }

    /// <summary>Chips that wrap onto new lines.</summary>
    private sealed class ItemsRepeaterLikePanel
    {
        private readonly WrapPanel _panel = new() { Spacing = 8 };

        public void Add(UIElement e) => _panel.Children.Add(e);

        public UIElement Element => _panel;
    }
}

/// <summary>Lays its children out left to right, starting a new line when one doesn't fit.</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var c in Children)
        {
            // Natural size: a chip is never squeezed (it moves to the next line instead).
            c.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > available.Width)
            {
                y += line + Spacing;
                x = 0;
                line = 0;
            }
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
            width = Math.Max(width, x - Spacing);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? width : available.Width, y + line);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width)
            {
                y += line + Spacing;
                x = 0;
                line = 0;
            }
            c.Arrange(new Windows.Foundation.Rect(x, y, s.Width, s.Height));
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
        }
        return final;
    }
}