using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LightSync.App;

/// <summary>
/// The sound analyzer under the effects, shown while a sound effect is selected: 24 bars with peak marks,
/// level / bass / mid / treble meters and a beat light. It polls "audio_spectrum" about 30 times a second,
/// only while it can be seen. Every shape is made once; a frame only changes transforms and opacity.
/// </summary>
public sealed partial class MainWindow
{
    private const int AnalyzerBands = 24;
    private const double AnalyzerHeight = 110;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _analyzerTimer;
    private readonly ScaleTransform[] _barScales = new ScaleTransform[AnalyzerBands];
    private readonly Rectangle[] _bars = new Rectangle[AnalyzerBands];
    private readonly Rectangle[] _peaks = new Rectangle[AnalyzerBands];
    private readonly TranslateTransform[] _peakMoves = new TranslateTransform[AnalyzerBands];
    private readonly ScaleTransform[] _meterScales = new ScaleTransform[4];
    private Ellipse? _beatDot;
    private TextBlock? _analyzerStatus;
    private double _beatGlow;
    private bool _analyzerBusy;
    /// <summary>0: audio_spectrum; 1: older service, only audio_status (level); 2: neither.</summary>
    private int _analyzerMode;
    private int _analyzerFrame;

    private void BuildAnalyzer()
    {
        var canvas = new Canvas { Height = AnalyzerHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(canvas, "Sound spectrum, 24 bands from bass to treble");
        var peakBrush = Res("TextFillColorPrimaryBrush");
        for (var i = 0; i < AnalyzerBands; i++)
        {
            // Bass on the left in red, through yellow and green, to treble in blue and violet.
            var brush = new SolidColorBrush(FromHue(0.78 * i / (AnalyzerBands - 1)));
            _barScales[i] = new ScaleTransform { ScaleY = 0.01 };
            _bars[i] = new Rectangle
            {
                Height = AnalyzerHeight,
                Fill = brush,
                RadiusX = 2,
                RadiusY = 2,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
                RenderTransform = _barScales[i],
            };
            _peakMoves[i] = new TranslateTransform { Y = AnalyzerHeight - 2 };
            _peaks[i] = new Rectangle { Height = 2, Fill = peakBrush, Opacity = 0.8, RenderTransform = _peakMoves[i] };
            canvas.Children.Add(_bars[i]);
            canvas.Children.Add(_peaks[i]);
        }
        canvas.SizeChanged += (_, e) => LayoutBars(e.NewSize.Width);

        var meters = new StackPanel { Spacing = 8, Width = 170, VerticalAlignment = VerticalAlignment.Center };
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
            row.Children.Add(new TextBlock { Text = names[i], Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(track, $"{names[i]} meter");
            meters.Children.Add(row);
        }
        _beatDot = new Ellipse { Width = 14, Height = 14, Fill = Res("AccentFillColorDefaultBrush"), Opacity = 0.15, VerticalAlignment = VerticalAlignment.Center };
        var beat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        beat.Children.Add(_beatDot);
        beat.Children.Add(new TextBlock { Text = "Beat", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        meters.Children.Add(beat);

        _analyzerStatus = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
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
        grid.Children.Add(canvas);
        Grid.SetColumn(meters, 1);
        grid.Children.Add(meters);
        Grid.SetRow(_analyzerStatus, 1);
        Grid.SetColumnSpan(_analyzerStatus, 2);
        grid.Children.Add(_analyzerStatus);
        AnalyzerHost.Child = grid;
    }

    /// <summary>Spread the bars over the canvas width (only when its size changes).</summary>
    private void LayoutBars(double width)
    {
        if (width <= 0)
        {
            return;
        }
        const double gap = 3;
        var barWidth = Math.Max(1, (width - gap * (AnalyzerBands - 1)) / AnalyzerBands);
        for (var i = 0; i < AnalyzerBands; i++)
        {
            var left = i * (barWidth + gap);
            _bars[i].Width = barWidth;
            _peaks[i].Width = barWidth;
            Canvas.SetLeft(_bars[i], left);
            Canvas.SetLeft(_peaks[i], left);
        }
    }

    /// <summary>
    /// Paint the bars in the colors the lights get for the selected sound effect, so the analyzer and the
    /// LEDs match: the spectrum effects use the service's band hues (red bass to violet treble, see
    /// band_hue in effects.rs), the level meter green / yellow / red, the others the chosen color.
    /// </summary>
    private void RecolorAnalyzer()
    {
        for (var i = 0; i < AnalyzerBands; i++)
        {
            var x = (double)i / (AnalyzerBands - 1);
            var c = _effect switch
            {
                "spectrum_analyzer" or "spectrum_bars" or "music_flow" => FromHue(0.78 * x),
                // Bass warm, treble cool, like the effect.
                "bass_treble" => x < 0.5 ? Windows.UI.Color.FromArgb(255, 255, 40, 70) : Windows.UI.Color.FromArgb(255, 150, 215, 255),
                "vu_meter" => FromHue(x < 0.55 ? 1.0 / 3 : x < 0.8 ? 1.0 / 3 - (x - 0.55) / 0.25 / 6 : Math.Max(0, 1.0 / 6 - (x - 0.8) / 0.12 / 6)),
                _ => _color,
            };
            if (_bars[i] is { } bar && (bar.Fill as SolidColorBrush)?.Color != c)
            {
                bar.Fill = new SolidColorBrush(c);
            }
        }
    }

    /// <summary>Show the analyzer (and poll) while a sound effect is selected; hide it (and stop) otherwise.</summary>
    private void ShowAnalyzer(bool on)
    {
        RecolorAnalyzer();
        AnalyzerHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && !_exiting)
        {
            _analyzerTimer.Start();
        }
        else
        {
            _analyzerTimer.Stop();
            DrawSpectrum(new AudioSpectrum());
        }
    }

    /// <summary>One analyzer frame. Skipped while the window is hidden or minimized, or the last request still runs.</summary>
    private async Task RefreshAnalyzerAsync()
    {
        if (_analyzerBusy || !_ready || _exiting || _analyzerMode == 2 || AnalyzerHost.Visibility != Visibility.Visible || !IsOnScreen)
        {
            return;
        }
        // The older fallback only has a level: 10 times a second is plenty.
        if (_analyzerMode == 1 && ++_analyzerFrame % 3 != 0)
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
                SetAnalyzerStatus(_analyzerMode == 1
                    ? "This LightSync service only reports the overall level. Update lightsyncd for the full analyzer."
                    : "This LightSync service can't show what it hears. Update lightsyncd for the analyzer.");
                DrawSpectrum(new AudioSpectrum());
                return;
            }
            if (_exiting || AnalyzerHost.Visibility != Visibility.Visible)
            {
                return;
            }
            DrawSpectrum(s);
            if (_analyzerMode != 0)
            {
                return; // the older service's note stays
            }
            // Sound is captured from the Windows default output device (WASAPI loopback), whatever plays there.
            var source = s.Device is { Length: > 0 } dev ? $"\"{dev}\" (Windows sound output)" : "the Windows sound output";
            SetAnalyzerStatus(!s.Active
                ? "Not listening yet: the sound effect starts listening within a moment of being shown."
                : s.Device is null && s.Level < 0.005 ? "Connecting to the Windows sound output..."
                : s.Level < 0.005 ? $"Listening to {source}. Nothing is playing right now."
                : $"Listening to {source}: the lights follow this spectrum.{Flow(s)}");
        }
        catch (Exception)
        {
            SetAnalyzerStatus("Not connected to the LightSync service.");
        }
        finally
        {
            _analyzerBusy = false;
        }
    }

    /// <summary>" About 124 BPM, bright and energetic." once a tempo is heard (what Music flow follows).</summary>
    private static string Flow(AudioSpectrum s)
    {
        var tone = s.Mood < 0.35 ? "deep, bass-heavy" : s.Mood > 0.65 ? "bright" : "balanced";
        var pace = s.Energy < 0.3 ? "calm" : s.Energy > 0.65 ? "energetic" : "steady";
        return s.TempoBpm >= 40 ? $" About {s.TempoBpm:0} BPM, {tone} and {pace}." : $" Sound: {tone}, {pace}.";
    }

    private void SetAnalyzerStatus(string text)
    {
        if (_analyzerStatus is { } t && t.Text != text)
        {
            t.Text = text;
        }
    }

    private void DrawSpectrum(AudioSpectrum s)
    {
        for (var i = 0; i < AnalyzerBands; i++)
        {
            var v = i < s.Bands.Length ? s.Bands[i] : 0;
            var peak = i < s.Peaks.Length ? Math.Max(s.Peaks[i], v) : v;
            if (_barScales[i] is { } scale)
            {
                scale.ScaleY = Math.Max(0.01, v);
            }
            if (_peakMoves[i] is { } move)
            {
                move.Y = (1 - peak) * (AnalyzerHeight - 2);
            }
        }
        double[] meters = [s.Level, s.Bass, s.Mid, s.Treble];
        for (var i = 0; i < meters.Length; i++)
        {
            if (_meterScales[i] is { } scale)
            {
                scale.ScaleX = meters[i];
            }
        }
        _beatGlow = s.Beat ? 1 : _beatGlow * 0.8;
        if (_beatDot is { } dot)
        {
            dot.Opacity = 0.15 + 0.85 * _beatGlow;
        }
    }
}
