using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LightSync.App;

/// <summary>One effect on the trigger map: its look, its name, and the devices it lights.</summary>
/// <param name="Rest">The effect for the devices that have none of their own ("the other devices").</param>
public sealed record MapEffect(string Name, Brush Preview, Color Line, IReadOnlyList<MapDevice> Devices, bool Rest);

/// <summary>A device on the trigger map, and what part of it the trigger uses ("whole", "2 zones", "14 LEDs").</summary>
public sealed record MapDevice(string Name, string Part, string Glyph);

/// <summary>
/// The trigger at a glance, as a graph you build it with: what sets it off on the left, its effects in the
/// middle, and on the right the devices each effect lights, joined by lines in the effect's color. Every
/// box can be clicked: "When" to change what sets it off, an effect (or one of its devices) to change
/// that effect, and "Add an effect" for another one.
/// </summary>
public sealed class TriggerMap
{
    private const double Gap = 56;
    private readonly Grid _grid = new() { ColumnSpacing = 0, RowSpacing = 10 };
    private readonly Canvas _lines = new() { IsHitTestVisible = false };
    private readonly List<(FrameworkElement From, FrameworkElement To, Color Color, bool Dashed)> _edges = [];
    private bool _pending;

    /// <summary>"When" was clicked.</summary>
    public event Action? WhenClicked;

    /// <summary>An effect (or one of its devices) was clicked: its index.</summary>
    public event Action<int>? EffectClicked;

    /// <summary>"Add an effect" was clicked.</summary>
    public event Action? AddClicked;

    /// <summary>The map, to put on a page.</summary>
    public FrameworkElement Root { get; }

    public TriggerMap()
    {
        var host = new Grid();
        host.Children.Add(_lines);
        host.Children.Add(_grid);
        // The lines go under the boxes and are drawn once the boxes have their places.
        Canvas.SetZIndex(_lines, 0);
        Canvas.SetZIndex(_grid, 1);
        _grid.SizeChanged += (_, _) => DrawLines();
        _grid.LayoutUpdated += (_, _) =>
        {
            if (_pending)
            {
                _pending = false;
                DrawLines();
            }
        };
        Root = host;
    }

    /// <summary>
    /// Show the trigger: <paramref name="when"/> (with its icon) and its effects; <paramref name="selected"/>
    /// is the box being edited (-1: "When"). "Add an effect" is enabled with <paramref name="canAdd"/> and
    /// says <paramref name="addNote"/> under it.
    /// </summary>
    public void Show(string glyph, string when, IReadOnlyList<MapEffect> effects, int selected, bool canAdd, string addNote)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        _edges.Clear();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MaxWidth = 220 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star), MaxWidth = 260 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var source = Clickable(Node(glyph, "When", when, null, selected < 0, accentIcon: true), () => WhenClicked?.Invoke(), $"When: {when}. Click to change it.");
        source.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRowSpan(source, effects.Count + 1);
        _grid.Children.Add(source);

        for (var row = 0; row < effects.Count; row++)
        {
            var fx = effects[row];
            var index = row;
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var subtitle = fx.Devices.Count == 0
                ? "No devices yet"
                : fx.Rest ? (effects.Count > 1 ? "For the other devices" : "For every device") : fx.Devices.Count == 1 ? "For 1 device" : $"For {fx.Devices.Count} devices";
            var title = effects.Count > 1 ? $"{row + 1}. {fx.Name}" : fx.Name;
            var effectNode = Clickable(Node(null, title, subtitle, fx.Preview, selected == row), () => EffectClicked?.Invoke(index), $"Effect {row + 1}: {fx.Name}, {subtitle}. Click to change it.");
            effectNode.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(effectNode, row);
            Grid.SetColumn(effectNode, 2);
            _grid.Children.Add(effectNode);
            _edges.Add((source, effectNode, fx.Line, false));

            var devices = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var d in fx.Devices)
            {
                var deviceNode = Clickable(Node(d.Glyph, d.Name, d.Part, null, false, small: true), () => EffectClicked?.Invoke(index), $"{d.Name}, {d.Part}: effect {row + 1}");
                devices.Children.Add(deviceNode);
                _edges.Add((effectNode, deviceNode, fx.Line, false));
            }
            if (fx.Devices.Count == 0)
            {
                devices.Children.Add(Note(fx.Rest ? "No device left for it" : "Click the effect to pick its devices"));
            }
            Grid.SetRow(devices, row);
            Grid.SetColumn(devices, 4);
            _grid.Children.Add(devices);
        }

        // "Add an effect": joined to "When" by a dashed line.
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var add = Clickable(Node("", "Add an effect", addNote, null, false, accentIcon: true, dashed: true), () => AddClicked?.Invoke(), "Add an effect for other devices");
        add.IsEnabled = canAdd;
        add.Opacity = canAdd ? 1 : 0.6;
        Grid.SetRow(add, effects.Count);
        Grid.SetColumn(add, 2);
        _grid.Children.Add(add);
        _edges.Add((source, add, Color.FromArgb(255, 136, 136, 136), true));
        _pending = true;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A box that can be clicked (and reached with Tab): its border lights up under the pointer.</summary>
    private static Button Clickable(Border node, Action click, string name)
    {
        var b = new Button
        {
            Content = node,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var normal = node.BorderBrush;
        var hover = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
        b.PointerEntered += (_, _) =>
        {
            if (node.Tag is not true)
            {
                node.BorderBrush = hover;
            }
        };
        b.PointerExited += (_, _) => node.BorderBrush = normal;
        b.Click += (_, _) => click();
        ToolTipService.SetToolTip(b, "Click to change it");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>A box on the map: an icon or an effect preview, a title and a line under it; a thicker accent border while selected.</summary>
    private static Border Node(string? glyph, string title, string subtitle, Brush? preview, bool selected, bool small = false, bool accentIcon = false, bool dashed = false)
    {
        var stack = new StackPanel { Spacing = 2 };
        // An icon, then the title, which wraps to a second line where the box is narrow.
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (preview is not null)
        {
            stack.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = preview, Margin = new Thickness(0, 0, 0, 4) });
        }
        if (glyph is not null)
        {
            head.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = small ? 12 : 14,
                Foreground = (Brush)Application.Current.Resources[accentIcon ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var titleText = new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources[small ? "BodyTextBlockStyle" : "BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 1);
        head.Children.Add(titleText);
        stack.Children.Add(head);
        if (subtitle.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
            });
        }
        return new Border
        {
            Padding = small ? new Thickness(10, 6, 10, 6) : new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources[dashed ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"],
            Child = stack,
            Tag = selected,
        };
    }

    /// <summary>Join the boxes: a curve from the right edge of each to the left edge of the next.</summary>
    private void DrawLines()
    {
        _lines.Children.Clear();
        if (_grid.ActualWidth <= 0)
        {
            return;
        }
        foreach (var (from, to, color, dashed) in _edges)
        {
            if (from.ActualWidth <= 0 || to.ActualWidth <= 0)
            {
                continue;
            }
            Windows.Foundation.Point a, b;
            try
            {
                a = from.TransformToVisual(_lines).TransformPoint(new Windows.Foundation.Point(from.ActualWidth, from.ActualHeight / 2));
                b = to.TransformToVisual(_lines).TransformPoint(new Windows.Foundation.Point(0, to.ActualHeight / 2));
            }
            catch (ArgumentException)
            {
                continue; // not in the same tree (being rebuilt)
            }
            var mid = (a.X + b.X) / 2;
            var figure = new PathFigure { StartPoint = a, IsClosed = false };
            figure.Segments.Add(new BezierSegment { Point1 = new(mid, a.Y), Point2 = new(mid, b.Y), Point3 = b });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = dashed ? 1.5 : 2,
                Opacity = dashed ? 0.6 : 0.85,
            };
            if (dashed)
            {
                path.StrokeDashArray = new DoubleCollection { 4, 3 };
            }
            _lines.Children.Add(path);
            if (!dashed)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color) };
                Canvas.SetLeft(dot, b.X - 3.5);
                Canvas.SetTop(dot, b.Y - 3.5);
                _lines.Children.Add(dot);
            }
        }
    }
}
