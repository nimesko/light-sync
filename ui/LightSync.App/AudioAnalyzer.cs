using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LightSync.App;

/// <summary>
/// A live view of what the service hears from the Windows sound output: 24 bars with peak marks (bass
/// on the left), the level / bass / mid / treble the lights follow, and a beat light with the tempo.
/// The bars are painted like the lights: the effect's colors, or the sound palette picked for it.
/// Every shape is made once; a frame only changes transforms, opacity and brush colors.
/// </summary>
public sealed class SoundVisualizer
{
    public const int Bands = 24;

    private readonly double _height;
    private readonly ScaleTransform[] _barScales = new ScaleTransform[Bands];
    private readonly Rectangle[] _bars = new Rectangle[Bands];
    private readonly SolidColorBrush[] _barBrushes = new SolidColorBrush[Bands];
    private readonly Rectangle[] _peaks = new Rectangle[Bands];
    private readonly TranslateTransform[] _peakMoves = new TranslateTransform[Bands];
    private readonly ScaleTransform[] _meterScales = new ScaleTransform[4];
    private readonly Ellipse _beatDot;
    private readonly TextBlock _tempo;
    private readonly TextBlock _status;
    /// <summary>What is playing: the app, the media and the site (NowPlaying).</summary>
    private readonly TextBlock _source;
    private double _beatGlow;
    private bool _wasBeat;
    private int _beats;
    private string _palette = "effect";

    /// <summary>The whole view, to put on a page.</summary>
    public Border Root { get; }

    /// <summary>Whether the window polls the service for this view (it also has to be on screen).</summary>
    public bool Polling { get; set; }

    /// <summary><paramref name="height"/>: of the bars; <paramref name="meters"/>: show the level / bass / mid / treble meters beside them.</summary>
    public SoundVisualizer(double height, bool meters = true)
    {
        _height = height;
        var canvas = new Canvas { Height = height, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(canvas, "Sound spectrum, 24 bands from bass to treble");
        var peakBrush = Res("TextFillColorPrimaryBrush");
        for (var i = 0; i < Bands; i++)
        {
            _barBrushes[i] = new SolidColorBrush(Hue(0.78 * i / (Bands - 1)));
            _barScales[i] = new ScaleTransform { ScaleY = 0.01 };
            _bars[i] = new Rectangle
            {
                Height = height,
                Fill = _barBrushes[i],
                RadiusX = 2,
                RadiusY = 2,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
                RenderTransform = _barScales[i],
            };
            _peakMoves[i] = new TranslateTransform { Y = height - 2 };
            _peaks[i] = new Rectangle { Height = 2, Fill = peakBrush, Opacity = 0.8, RenderTransform = _peakMoves[i] };
            canvas.Children.Add(_bars[i]);
            canvas.Children.Add(_peaks[i]);
        }
        canvas.SizeChanged += (_, e) => LayoutBars(e.NewSize.Width);

        var side = new StackPanel { Spacing = 8, Width = 170, VerticalAlignment = VerticalAlignment.Center };
        var names = new[] { "Level", "Bass", "Mid", "Treble" };
        for (var i = 0; i < names.Length; i++)
        {
            _meterScales[i] = new ScaleTransform { ScaleX = 0 };
            var fill = new Rectangle
            {
                Fill = Res("AccentFillColorDefaultBrush"),
                RadiusX = 3,
                RadiusY = 3,
                RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5),
                RenderTransform = _meterScales[i],
            };
            var track = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = Res("ControlStrongFillColorDisabledBrush"),
                Child = fill,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = names[i], Style = Style("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(track, $"{names[i]} meter");
            ToolTipService.SetToolTip(track, $"{names[i]}: the level with its hits (what the lights follow)");
            side.Children.Add(row);
        }
        _beatDot = new Ellipse { Width = 14, Height = 14, Fill = Res("AccentFillColorDefaultBrush"), Opacity = 0.15, VerticalAlignment = VerticalAlignment.Center };
        _tempo = new TextBlock { Text = "Beat", Style = Style("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        var beat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        beat.Children.Add(_beatDot);
        beat.Children.Add(_tempo);
        side.Children.Add(beat);
        if (!meters)
        {
            // Compact: only the beat light and tempo beside the bars.
            for (var i = 0; i < names.Length; i++)
            {
                side.Children[i].Visibility = Visibility.Collapsed;
            }
            side.Width = 110;
        }

        _status = new TextBlock
        {
            Style = Style("CaptionTextBlockStyle"),
            Foreground = Res("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Text = "Waiting for sound...",
        };

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(canvas);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        Grid.SetRow(_status, 1);
        _source = new TextBlock
        {
            Style = Style("CaptionTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_source, 2);
        Grid.SetColumnSpan(_source, 2);
        grid.Children.Add(_source);
        Grid.SetColumnSpan(_status, 2);
        grid.Children.Add(_status);
        Root = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = Res("SubtleFillColorSecondaryBrush"),
            Child = grid,
        };
    }

    /// <summary>Whether the view can be seen on the page (shown, and part of the window).</summary>
    public bool IsShown => Polling && Root.IsLoaded && Root.Visibility == Visibility.Visible;

    /// <summary>Paint the bars in the chosen color and sound palette (as the lights get them).</summary>
    public void Recolor(Color color, string palette)
    {
        _palette = palette;
        if (palette == "effect")
        {
            for (var i = 0; i < Bands; i++)
            {
                _barBrushes[i].Color = color;
            }
        }
    }

    /// <summary>The "what is playing" line (hidden when empty).</summary>
    public void SetSource(string? text)
    {
        if (_source.Text != (text ?? ""))
        {
            _source.Text = text ?? "";
        }
        _source.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetStatus(string text)
    {
        if (_status.Text != text)
        {
            _status.Text = text;
        }
    }

    public void Draw(AudioSpectrum s)
    {
        if (s.Beat && !_wasBeat)
        {
            _beats++; // a new beat: random and neon palettes change
        }
        _wasBeat = s.Beat;
        for (var i = 0; i < Bands; i++)
        {
            var v = i < s.Bands.Length ? s.Bands[i] : 0;
            var peak = i < s.Peaks.Length ? Math.Max(s.Peaks[i], v) : v;
            _barScales[i].ScaleY = Math.Max(0.01, v);
            _peakMoves[i].Y = (1 - peak) * (_height - 2);
            if (_palette != "effect")
            {
                var c = PaletteColor(_palette, v, (double)i / (Bands - 1), _beats);
                if (_barBrushes[i].Color != c)
                {
                    _barBrushes[i].Color = c;
                }
            }
        }
        double[] levels = [s.Level, s.Bass, s.Mid, s.Treble];
        for (var i = 0; i < levels.Length; i++)
        {
            // What the sound effects follow: the level mixed with its hits (PUNCH_MIX in effects.rs).
            var drive = i < s.Punch.Length ? 0.35 * levels[i] + 0.65 * s.Punch[i] : levels[i];
            _meterScales[i].ScaleX = Math.Clamp(drive, 0, 1);
        }
        _beatGlow = s.Beat ? Math.Max(_beatGlow * 0.8, s.BeatStrength > 0 ? s.BeatStrength : 1) : _beatGlow * 0.8;
        _beatDot.Opacity = 0.15 + 0.85 * _beatGlow;
        var tempo = s.TempoBpm >= 40 ? $"{s.TempoBpm:0} BPM" : "Beat";
        if (_tempo.Text != tempo)
        {
            _tempo.Text = tempo;
        }
    }

    /// <summary>Back to rest (nothing heard).</summary>
    public void Clear() => Draw(new AudioSpectrum());

    private void LayoutBars(double width)
    {
        if (width <= 0)
        {
            return;
        }
        const double gap = 3;
        var barWidth = Math.Max(1, (width - gap * (Bands - 1)) / Bands);
        for (var i = 0; i < Bands; i++)
        {
            var left = i * (barWidth + gap);
            _bars[i].Width = barWidth;
            _peaks[i].Width = barWidth;
            Canvas.SetLeft(_bars[i], left);
            Canvas.SetLeft(_peaks[i], left);
        }
    }

    /// <summary>
    /// A band's color with a sound palette (recolor in effects.rs): the rainbow along the bands, by how
    /// loud it is (cold to hot, fire), a new color per beat (random), or pink and cyan halves swapping on
    /// the beat (neon).
    /// </summary>
    public static Color PaletteColor(string palette, double heat, double x, int beat)
    {
        heat = Math.Clamp(heat, 0, 1);
        return palette switch
        {
            "rainbow" => Hue(x + 0.05 * beat),
            "random" => Hue(beat * 0.618034 + 0.03 * (x - 0.5)),
            "cold_hot" => Hue(0.66 * (1 - heat)),
            "low_high" => Hue(0.78 * x),
            "fire" => Hsv(0.14 * heat, 1 - 0.55 * heat * heat, 1),
            "neon" => Hue(((x < 0.5 ? 0 : 1) + beat) % 2 == 0 ? 0.92 : 0.5),
            _ => Hue(0.78 * x),
        };
    }

    public static Color Hue(double h) => Hsv(h, 1, 1);

    public static Color Hsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)h;
        var f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        return Color.FromArgb(255, B(r), B(g), B(b));
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}

/// <summary>
/// The window's side of the sound views: one poll of "audio_spectrum" about 30 times a second feeds every
/// visualizer on screen (the device's, Home's, a trigger's); nothing runs while none can be seen.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Sound palettes for the sound effects: id, name, what it does (effects.rs Palette).</summary>
    internal static readonly (string Id, string Name, string Tip)[] SoundPalettes =
    [
        ("effect", "One color", "The color you pick, for every light"),
        ("rainbow", "Rainbow", "Every color of the rainbow along each device, drifting with the music"),
        ("random", "Random", "A new random color on every beat"),
        ("cold_hot", "Cold to hot", "By how loud it is: blue when quiet, through green and yellow, to red at the loudest"),
        ("low_high", "Low to high", "By pitch: red for bass, through orange, green and blue, to violet for treble"),
        ("fire", "Fire", "By how loud it is: deep red when quiet to yellow and white-hot at the loudest"),
        ("neon", "Neon", "Neon pink and cyan along each device, swapping on every beat"),
    ];

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _analyzerTimer;
    private readonly List<SoundVisualizer> _visualizers = [];
    /// <summary>The device view's visualizer (under the sound effects).</summary>
    private SoundVisualizer? _deviceSound;
    /// <summary>Home's visualizer: its card shows while the service is listening.</summary>
    private SoundVisualizer? _homeSound;
    private Border? _homeSoundCard;
    private bool _analyzerBusy;
    /// <summary>0: audio_spectrum; 1: older service, only audio_status (level); 2: neither.</summary>
    private int _analyzerMode;
    private int _analyzerFrame;
    private bool _lastHeardActive;

    /// <summary>Make the device view's and Home's visualizers (once; Home can be built before the rest of the window).</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_deviceSound), nameof(_homeSound))]
    private void BuildAnalyzer()
    {
        if (_deviceSound is not null && _homeSound is not null)
        {
            return;
        }
        _deviceSound = new SoundVisualizer(110);
        _homeSound = new SoundVisualizer(70, meters: false);
        _visualizers.Add(_deviceSound);
        _visualizers.Add(_homeSound);
        AnalyzerHost.Child = _deviceSound.Root;
        AnalyzerHost.Padding = new Thickness(0);
    }

    /// <summary>A visualizer for a page that comes and goes (the trigger editor): polled while it is loaded and shown.</summary>
    private SoundVisualizer AddVisualizer(double height)
    {
        var v = new SoundVisualizer(height) { Polling = true };
        v.Root.Unloaded += (_, _) => _visualizers.Remove(v);
        _visualizers.Add(v);
        UpdateSoundViews();
        return v;
    }

    /// <summary>Paint the device view's bars in the colors the lights get for the selected sound effect.</summary>
    private void RecolorAnalyzer() => _deviceSound?.Recolor(_color, _palette);

    /// <summary>Which views poll: the device view while a sound effect is selected, Home while it is shown.</summary>
    private void UpdateSoundViews()
    {
        // Not ready yet while the window is being made (the timer comes later).
        if (_deviceSound is null || _homeSound is null || _analyzerTimer is null)
        {
            return;
        }
        var deviceAudio = EffectById(_effect).Audio;
        AnalyzerHost.Visibility = deviceAudio ? Visibility.Visible : Visibility.Collapsed;
        _deviceSound.Polling = _view == ShellView.Device && deviceAudio;
        _homeSound.Polling = _view == ShellView.Home;
        RecolorAnalyzer();
        if (!_exiting && IsOnScreen && _visualizers.Any(v => v.Polling))
        {
            _analyzerTimer.Start();
        }
        else
        {
            _analyzerTimer.Stop();
            foreach (var v in _visualizers)
            {
                v.Clear();
            }
        }
    }

    /// <summary>Home's card with the live spectrum (shown only while the service listens: a sound effect or trigger is on).</summary>
    private Border HomeSoundCard()
    {
        BuildAnalyzer();
        if (_homeSoundCard is null)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(CardHeader("Sound", Text("What the sound effects hear", "CaptionTextBlockStyle", secondary: true)));
            panel.Children.Add(_homeSound.Root);
            _homeSoundCard = Card(panel);
        }
        _homeSoundCard.Visibility = _lastHeardActive ? Visibility.Visible : Visibility.Collapsed;
        return _homeSoundCard;
    }

    /// <summary>One frame for every visualizer on screen. Skipped while the window is hidden or minimized, or the last request still runs.</summary>
    private async Task RefreshAnalyzerAsync()
    {
        if (_analyzerBusy || !_ready || _exiting || _analyzerMode == 2 || !IsOnScreen)
        {
            return;
        }
        var shown = _visualizers.Where(v => v.Polling && v.Root.IsLoaded).ToList();
        if (shown.Count == 0)
        {
            return;
        }
        // Only Home is open and nothing is being heard: check now and then whether that changes.
        var onlyHome = shown.All(v => v == _homeSound);
        if ((onlyHome && !_lastHeardActive || _analyzerMode == 1) && ++_analyzerFrame % 10 != 0)
        {
            return;
        }
        _analyzerBusy = true;
        try
        {
            AudioSpectrum s;
            try
            {
                s = _analyzerMode == 0 ? await _client.AudioSpectrumAsync() : await _client.AudioStatusAsync();
            }
            catch (LightSyncException e) when (e.IsUnknownMethod)
            {
                _analyzerMode++;
                var note = _analyzerMode == 1
                    ? "This LightSync service only reports the overall level. Update lightsyncd for the full analyzer."
                    : "This LightSync service can't show what it hears. Update lightsyncd for the analyzer.";
                foreach (var v in shown)
                {
                    v.SetStatus(note);
                    v.Clear();
                }
                return;
            }
            if (_exiting)
            {
                return;
            }
            if (s.Active != _lastHeardActive)
            {
                _lastHeardActive = s.Active;
                if (_homeSoundCard is not null)
                {
                    _homeSoundCard.Visibility = s.Active ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            // Sound is captured from the Windows default output device (WASAPI loopback), whatever plays there.
            var source = s.Device is { Length: > 0 } dev ? $"\"{dev}\" (Windows sound output)" : "the Windows sound output";
            var status = !s.Active
                ? "Not listening right now: LightSync listens while a sound effect or an enabled sound trigger is on."
                : s.Device is null && s.Level < 0.005 ? "Connecting to the Windows sound output..."
                : s.Level < 0.005 ? $"Listening to {source}. Nothing is playing right now."
                : $"Listening to {source}: the lights follow this spectrum.{Flow(s)}";
            foreach (var v in shown)
            {
                v.Draw(s);
                if (_analyzerMode == 0)
                {
                    v.SetStatus(status);
                }
            }
            RefreshSource();
        }
        catch (Exception failure) when (Errors.Recoverable(failure))
        {
            foreach (var v in shown)
            {
                v.SetStatus("Not connected to the LightSync service.");
            }
        }
        finally
        {
            _analyzerBusy = false;
        }
    }

    private bool _sourceBusy;
    private DateTime _sourceAt;

    /// <summary>
    /// Every 2 seconds while a sound view is on screen: what is playing (the app, the media, the site), read
    /// off the UI thread (NowPlaying) and shown under each view.
    /// </summary>
    private void RefreshSource()
    {
        if (_sourceBusy || DateTime.UtcNow - _sourceAt < TimeSpan.FromSeconds(2))
        {
            return;
        }
        _sourceBusy = true;
        _sourceAt = DateTime.UtcNow;
        Task.Run(NowPlaying.GetAsync).ContinueWith(t => OnUi(() =>
        {
            _sourceBusy = false;
            var text = t.IsCompletedSuccessfully ? t.Result.Describe() : null;
            foreach (var v in _visualizers.Where(v => v.Polling))
            {
                v.SetSource(text);
            }
        }), TaskScheduler.Default);
    }

    /// <summary>" Bright and energetic." (the tempo shows beside the beat light).</summary>
    private static string Flow(AudioSpectrum s)
    {
        var tone = s.Mood < 0.35 ? "deep, bass-heavy" : s.Mood > 0.65 ? "bright" : "balanced";
        var pace = s.Energy < 0.3 ? "calm" : s.Energy > 0.65 ? "energetic" : "steady";
        return $" Sound: {tone}, {pace}.";
    }
}
