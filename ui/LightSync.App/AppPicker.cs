using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace LightSync.App;

/// <summary>
/// "An app is running" triggers: pick the app from the ones running now (icon, name, window title),
/// with search; a typed program name ("game.exe") still works for an app that isn't open.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>A running app with a window: its program file ("game.exe"), friendly name, window title and path.</summary>
    private sealed record RunningApp(string Exe, string Name, string Title, string? Path);

    /// <summary>Icons by program path, kept while LightSync runs (a few KB each).</summary>
    private static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' own background hosts that own invisible or system windows: not apps anyone picks.</summary>
    private static readonly HashSet<string> WindowsHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe", "textinputhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "searchhost.exe", "lockapp.exe", "searchapp.exe", "nvcontainer.exe", "nvidia overlay.exe",
    };

    /// <summary>Apps with a window in this session, one per program, by name.</summary>
    private static List<RunningApp> RunningAppList()
    {
        var me = Environment.ProcessId;
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == me || p.SessionId != session || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle))
                {
                    continue;
                }
                var exe = p.ProcessName.ToLowerInvariant() + ".exe";
                if (apps.ContainsKey(exe) || WindowsHosts.Contains(exe))
                {
                    continue;
                }
                string? path = null;
                string name = p.ProcessName;
                try
                {
                    // Elevated apps don't let us read this; they still show by program name.
                    path = p.MainModule?.FileName;
                    if (path is not null)
                    {
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        name = info.FileDescription is { Length: > 0 } d ? d : info.ProductName is { Length: > 0 } pn ? pn : name;
                    }
                }
                catch (Exception failure) when (Errors.Expected(failure))
                {
                    // No access: keep the program name.
                }
                apps[exe] = new RunningApp(exe, name.Trim(), p.MainWindowTitle.Trim(), path);
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // Gone already, or no access.
            }
            finally
            {
                p.Dispose();
            }
        }
        return [.. apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The app's icon (from its program file), loaded once.</summary>
    private static async Task<ImageSource?> AppIconAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (AppIcons.TryGetValue(path, out var cached))
        {
            return cached;
        }
        ImageSource? icon = null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (thumb is not null)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                icon = bmp;
            }
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // No icon; a generic one shows.
        }
        AppIcons[path] = icon;
        return icon;
    }

    /// <summary>A 32 px app icon slot: the app's own icon once loaded, a generic app glyph until then.</summary>
    private static Grid IconSlot(string? path, double size = 32)
    {
        var slot = new Grid { Width = size, Height = size };
        slot.Children.Add(new FontIcon { Glyph = "", FontSize = size * 0.6, Foreground = Res("TextFillColorSecondaryBrush") });
        _ = LoadIconInto(slot, path, size);
        return slot;
    }

    private static async Task LoadIconInto(Grid slot, string? path, double size)
    {
        if (await AppIconAsync(path) is { } src)
        {
            slot.Children.Clear();
            slot.Children.Add(new Image { Source = src, Width = size, Height = size });
        }
    }

    /// <summary>
    /// The app picker: a card with the chosen app (icon, name, program file, whether it runs now) and a
    /// "Choose app" button that opens a searchable list of the running apps.
    /// </summary>
    private FrameworkElement BuildAppPicker(string initialExe, Action<string> picked)
    {
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = Res("TextFillColorSecondaryBrush");
        var exe = initialExe;

        // The chosen app.
        var iconHost = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = Res("SubtleFillColorSecondaryBrush"), Padding = new Thickness(4) };
        var title = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Style = caption, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
        var running = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var choose = new Button { Content = "Choose app..." };
        var card = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("LayerOnMicaBaseAltFillColorDefaultBrush"),
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Children.Add(iconHost);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { title, sub, running } };
        Grid.SetColumn(texts, 1);
        card.Children.Add(texts);
        choose.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(choose, 2);
        card.Children.Add(choose);

        void ShowChosen(RunningApp? app)
        {
            running.Children.Clear();
            if (exe.Length == 0)
            {
                iconHost.Child = new FontIcon { Glyph = "", FontSize = 20, Foreground = secondary };
                title.Text = "No app chosen yet";
                sub.Text = "Choose one of the apps running now, or type its program name.";
                return;
            }
            iconHost.Child = IconSlot(app?.Path);
            title.Text = app?.Name ?? exe;
            sub.Text = app is null ? exe : $"{exe} · {app.Title}";
            var on = app is not null;
            running.Children.Add(new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = Res(on ? "SystemFillColorSuccessBrush" : "ControlStrongFillColorDisabledBrush") });
            running.Children.Add(new TextBlock { Text = on ? "Running now" : "Not running now", Style = caption, Foreground = secondary });
        }

        // The list: search, then one row per running app; a typed name that matches nothing can be used as is.
        var search = new TextBox { PlaceholderText = "Search running apps, or type a program name (game.exe)" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 380 };
        var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(refresh, "Refresh the list");
        var count = new TextBlock { Style = caption, Foreground = secondary };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(search);
        Grid.SetColumn(refresh, 1);
        top.Children.Add(refresh);
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel { Width = 460, Spacing = 8, Children = { top, list, count } },
        };
        choose.Flyout = flyout;
        var apps = new List<RunningApp>();

        static string Typed(string text)
        {
            var s = text.Trim().Trim('"');
            var file = s.Split('\\', '/').Last().Trim().ToLowerInvariant();
            return file.Length == 0 ? "" : file.EndsWith(".exe", StringComparison.Ordinal) ? file : file + ".exe";
        }

        FrameworkElement Row(string icon, string? path, string name, string detail)
        {
            var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(path is null ? new FontIcon { Glyph = icon, FontSize = 20, Width = 32, Foreground = secondary } : IconSlot(path));
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            t.Children.Add(new TextBlock { Text = detail, Style = caption, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        void Fill()
        {
            var q = search.Text.Trim();
            var shown = apps.Where(a => q.Length == 0
                || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || a.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            var items = new List<object>();
            var typed = Typed(q);
            if (typed.Length > 4 && shown.All(a => !a.Exe.Equals(typed, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new ListViewItem { Content = Row("", null, $"Use \"{typed}\"", "For an app that isn't open right now"), Tag = typed });
            }
            foreach (var a in shown)
            {
                items.Add(new ListViewItem { Content = Row("", a.Path ?? "", a.Name, $"{a.Exe} · {a.Title}"), Tag = a.Exe });
            }
            list.ItemsSource = items;
            count.Text = apps.Count == 0 ? "Looking for running apps..." : shown.Count == 0 ? "No running app matches." : $"{shown.Count} of {apps.Count} running apps";
        }

        async Task LoadAsync()
        {
            apps = await Task.Run(RunningAppList);
            Fill();
            ShowChosen(apps.FirstOrDefault(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            // Enter takes the first row (the typed name, or the best match).
            if (e.Key == Windows.System.VirtualKey.Enter && list.Items.Count > 0 && list.Items[0] is ListViewItem { Tag: string first })
            {
                Pick(first);
                e.Handled = true;
            }
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem { Tag: string chosen })
            {
                Pick(chosen);
            }
        };
        refresh.Click += (_, _) => Safe("listing running apps", LoadAsync);
        flyout.Opening += (_, _) =>
        {
            search.Text = "";
            Safe("listing running apps", LoadAsync);
        };

        void Pick(string chosen)
        {
            exe = chosen;
            picked(chosen);
            ShowChosen(apps.FirstOrDefault(a => a.Exe.Equals(chosen, StringComparison.OrdinalIgnoreCase)));
            flyout.Hide();
        }

        ShowChosen(null);
        Safe("listing running apps", LoadAsync);
        return card;
    }
}
