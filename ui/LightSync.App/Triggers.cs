using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace LightSync.App;

/// <summary>
/// Triggers: a light effect (flash, pulse, ripple, fade in, a preset) that plays when a key is pressed, the
/// mouse clicks or moves, the music beats or gets loud. The service keeps and plays them (RPC triggers,
/// save_trigger, delete_trigger); this app edits them in Settings > Triggers and, only while an enabled
/// keyboard or mouse trigger needs it, tells the service about key presses and mouse activity (input_event).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _inputTimer;
    private readonly InputListener _input;
    /// <summary>Input events go over their own connection, so they never wait behind the window's requests.</summary>
    private readonly LightSyncClient _inputClient = new();
    private List<TriggerInfo> _triggers = [];
    /// <summary>False once the service said it doesn't know the trigger methods (an older lightsyncd).</summary>
    private bool _triggersSupported = true;
    /// <summary>What to listen for; read on the listener thread.</summary>
    private volatile InputPlan _inputPlan = InputPlan.None;
    private int _inputInFlight;

    /// <summary>
    /// What the input listener passes on. Only the key codes in <see cref="Codes"/> (the ones a trigger
    /// listens for) are sent with their code; any other key is sent as "a key was pressed", and only if
    /// a trigger wants any key.
    /// </summary>
    private sealed record InputPlan(bool AnyKey, HashSet<int> Codes, bool Clicks, bool Moves, bool AllCodes = false)
    {
        public static readonly InputPlan None = new(false, [], false, false);
        public bool Keys => AnyKey || AllCodes || Codes.Count > 0;
    }

    private static readonly (string Id, string Label)[] BandChoices = [("level", "Overall level"), ("bass", "Bass"), ("mid", "Mid"), ("treble", "Treble")];

    private static readonly (string? Id, string Label)[] ButtonChoices = [(null, "Any button"), ("left", "Left button"), ("right", "Right button"), ("middle", "Middle button")];

    // ---------- loading, and what to listen for ----------

    private bool _ledTargetsMigrated;

    /// <summary>
    /// Single-LED targets used to be "device#index", which clashes with the ids of devices that share a
    /// name ("openrgb:ENE DRAM#2"). They are now "device@index": rewrite old ones once, when both the
    /// devices and the triggers are known (only targets that aren't themselves a device id).
    /// </summary>
    private async Task MigrateLedTargetsAsync()
    {
        if (_ledTargetsMigrated || _allDevices.Count == 0 || _triggers.Count == 0)
        {
            return;
        }
        _ledTargetsMigrated = true;
        var ids = _allDevices.Select(d => d.Id).ToHashSet();
        var changed = false;
        foreach (var t in _triggers.ToList())
        {
            var targets = t.Targets.Select(x =>
            {
                var k = x.LastIndexOf('#');
                return k > 0 && !ids.Contains(x) && ids.Contains(x[..k]) && int.TryParse(x[(k + 1)..], out _) ? $"{x[..k]}@{x[(k + 1)..]}" : x;
            }).ToList();
            if (targets.SequenceEqual(t.Targets))
            {
                continue;
            }
            t.Targets = targets;
            try
            {
                await _client.SaveTriggerAsync(t);
                changed = true;
            }
            catch (Exception e) when (Errors.Recoverable(e))
            {
                CrashLog.Write("updating a trigger's LED targets", e);
            }
        }
        if (changed)
        {
            await LoadTriggersAsync();
        }
    }

    /// <summary>Load the triggers (to know which keys they use), then listen for what they need.</summary>
    private async Task LoadTriggersAsync()
    {
        try
        {
            _triggers = await _client.TriggersAsync();
            _triggersSupported = true;
        }
        catch (LightSyncException e) when (e.IsUnknownMethod)
        {
            _triggersSupported = false;
            _triggers = [];
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            // Not reachable right now: keep what we had; the next check tries again.
        }
        ShowTriggers();
        await RefreshInputWantedAsync();
        await MigrateLedTargetsAsync();
    }

    /// <summary>
    /// Ask the service what keyboard and mouse input enabled triggers need, and listen for exactly that
    /// (nothing at all if none, or if the service can't be reached). Runs every few seconds and after
    /// every trigger change.
    /// </summary>
    private async Task RefreshInputWantedAsync()
    {
        if (_exiting)
        {
            return;
        }
        InputPlan plan;
        if (!_triggersSupported)
        {
            plan = InputPlan.None;
        }
        else
        {
            try
            {
                var wanted = await _client.InputWantedAsync();
                plan = PlanFor(wanted);
                try
                {
                    await ReportAppsAsync(wanted.Apps);
                }
                catch (Exception failure) when (Errors.Expected(failure))
                {
                    // An older service, or busy: app triggers wait for the next check.
                }
            }
            catch (LightSyncException e) when (e.IsUnknownMethod)
            {
                plan = InputPlan.None;
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                plan = InputPlan.None; // Nobody to tell: don't listen.
            }
        }
        if (_exiting)
        {
            return;
        }
        var before = _inputPlan;
        _inputPlan = plan;
        _input.Configure(plan.Keys, plan.Clicks, plan.Moves);
        if (before.Keys != plan.Keys || before.Clicks != plan.Clicks || before.Moves != plan.Moves)
        {
            ShowTriggers(); // its note says what LightSync listens to
        }
    }

    /// <summary>
    /// "An app is running" triggers: tell the service which of the programs they watch run now (checked
    /// with the input check, every few seconds). No other program names leave this app.
    /// </summary>
    private async Task ReportAppsAsync(List<string> watched)
    {
        if (watched.Count == 0)
        {
            return;
        }
        var running = await Task.Run(() =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    names.Add(p.ProcessName + ".exe");
                }
                catch (Exception failure) when (Errors.Expected(failure))
                {
                    // Gone already.
                }
                finally
                {
                    p.Dispose();
                }
            }
            // "chrome.exe" holds while it runs; "chrome.exe/youtube.com" while one of its windows shows a page
            // on that site (read once per browser, only when a trigger watches a site).
            var hosts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var holds = new List<string>();
            foreach (var w in watched)
            {
                var slash = w.IndexOf('/');
                var app = slash < 0 ? w : w[..slash];
                if (!names.Contains(app))
                {
                    continue;
                }
                if (slash < 0)
                {
                    holds.Add(w);
                    continue;
                }
                var site = w[(slash + 1)..];
                if (!hosts.TryGetValue(app, out var open))
                {
                    open = hosts[app] = NowPlaying.CurrentHosts(app);
                }
                if (open.Any(h => NowPlaying.OnSite(h, site)))
                {
                    holds.Add(w);
                }
            }
            return holds;
        });
        await _client.SetAppsAsync(running);
    }


    /// <summary>
    /// The service decides whether input is needed; the triggers tell which keys. With "keys: true" from
    /// the service and only specific-key triggers here, only those keys are passed on with their code.
    /// </summary>
    private InputPlan PlanFor(InputWanted wanted)
    {
        var enabledKeys = _triggers.Where(t => t.Enabled && t.SourceKind == "key").ToList();
        var localAny = enabledKeys.Any(t => t.Source["keys"] is not JsonArray);
        var localCodes = enabledKeys.SelectMany(t => t.Source["keys"] is JsonArray a ? KeyCodes(a) : []).ToHashSet();
        var codes = new HashSet<int>(wanted.KeyCodes);
        var anyKey = false;
        if (wanted.AnyKey)
        {
            codes.UnionWith(localCodes);
            // "Some key" from the service: any key, unless the triggers here say it's only specific ones.
            anyKey = localAny || localCodes.Count == 0;
        }
        // A trigger that lights the pressed key needs every key's code (to find the key; never kept).
        return new InputPlan(anyKey, codes, wanted.MouseClick, wanted.MouseMove, wanted.AllKeyCodes);
    }

    private static IEnumerable<int> KeyCodes(JsonArray a)
    {
        foreach (var x in a)
        {
            if (x is JsonValue v && v.TryGetValue<int>(out var code) && code is > 0 and < 255)
            {
                yield return code;
            }
        }
    }

    /// <summary>
    /// From the listener thread: pass a key press, click or movement on to the service. Nothing is kept
    /// or logged; a key not used by any trigger is sent without its code. Sends are dropped (not queued)
    /// while several are still on their way.
    /// </summary>
    private void OnInput(string kind, int vk, string? button)
    {
        var plan = _inputPlan;
        int? code = null;
        switch (kind)
        {
            case "key":
                if (plan.AllCodes || plan.Codes.Contains(vk))
                {
                    code = vk;
                }
                else if (!plan.AnyKey)
                {
                    return;
                }
                break;
            case "mouse_click" when !plan.Clicks:
            case "mouse_move" when !plan.Moves:
                return;
        }
        if (_exiting || Interlocked.Increment(ref _inputInFlight) > 6)
        {
            Interlocked.Decrement(ref _inputInFlight);
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                await _inputClient.InputEventAsync(kind, code, button);
            }
            catch (Exception failure) when (Errors.Expected(failure))
            {
                // The service is busy or gone; this one press just doesn't light anything.
            }
            finally
            {
                Interlocked.Decrement(ref _inputInFlight);
            }
        });
    }

    /// <summary>After a trigger was deleted: reload, listen for what is needed now, show the list (and Home).</summary>
    private async Task AfterTriggersChangedAsync()
    {
        _editingTrigger = null;
        await LoadTriggersAsync();
        ShowTriggers(force: true);
    }

    // ---------- the Triggers section (left) and a trigger's configuration (right) ----------

    /// <summary>The trigger open on the right (null: none). A new one has no id yet.</summary>
    private TriggerInfo? _editingTrigger;
    private bool _editingNew;

    /// <summary>What a new trigger is aimed at: the selected device (when one is open), else all devices.</summary>
    private List<string> DefaultTriggerTargets() =>
        _view != ShellView.Device ? [] : _selection.Count > 1 ? [.. _selection] : _target.Kind == TargetKind.All ? [] : [_target.Id];

    /// <summary>
    /// Show the triggers: the count and list on the left, and Home's summary. With <paramref name="force"/>
    /// the open trigger's configuration is built again (after a save or a switch); otherwise an edit in
    /// progress is left alone.
    /// </summary>
    private void ShowTriggers(bool force = false)
    {
        if (_exiting)
        {
            return;
        }
        RefreshTriggerList();
        if (_view == ShellView.Trigger)
        {
            if (_editingTrigger is null)
            {
                ShowView(ShellView.Home);
            }
            else if (force || TriggerPane.Children.Count == 0)
            {
                BuildTriggerPane();
            }
        }
        RefreshHome();
    }

    /// <summary>The left Triggers section: how many there are (and how many are on), one row each.</summary>
    private void RefreshTriggerList()
    {
        var on = _triggers.Count(t => t.Enabled);
        TriggerCount.Text = _triggers.Count == 0 ? "0" : on == _triggers.Count ? $"{_triggers.Count}" : $"{on} / {_triggers.Count} on";
        ToolTipService.SetToolTip(TriggerCount, $"{_triggers.Count} trigger{(_triggers.Count == 1 ? "" : "s")}, {on} on");
        TriggerEmpty.Visibility = _triggers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TriggerList.Visibility = _triggers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var items = new List<FrameworkElement>();
        FrameworkElement? selected = null;
        foreach (var t in _triggers)
        {
            var row = new Grid { ColumnSpacing = 10, Tag = t.Id };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Its priority: 1 is on top where triggers light the same LEDs.
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rank = new TextBlock
            {
                Text = $"#{_triggers.IndexOf(t) + 1}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Res("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(rank, "Priority: where triggers light the same LEDs, the higher one (lower number) wins");
            Grid.SetColumn(rank, 2);
            row.Children.Add(rank);
            row.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Res("SubtleFillColorSecondaryBrush"),
                Child = new FontIcon { Glyph = SourceGlyph(t.SourceKind), FontSize = 11, Foreground = Res(t.Enabled ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush") },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = t.Enabled ? 1 : 0.6 };
            // Just the name (the full description shows on hover); turned-off triggers are dimmed.
            text.Children.Add(new TextBlock { Text = TriggerName(t), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            ToolTipService.SetToolTip(row, Describe(t));
            items.Add(row);
            if (_view == ShellView.Trigger && !_editingNew && _editingTrigger?.Id == t.Id)
            {
                selected = row;
            }
        }
        _loading = true;
        TriggerList.ItemsSource = items;
        TriggerList.SelectedItem = selected;
        _loading = false;
    }

    private static string SourceGlyph(string kind) => kind switch
    {
        "key" => "",
        "mouse_click" or "mouse_move" => "",
        "audio_beat" or "audio_level" => "",
        "app_running" => "",
        _ => "",
    };

    private void TriggerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || e.AddedItems.Count == 0 || e.AddedItems[0] is not FrameworkElement { Tag: string id } ||
            _triggers.FirstOrDefault(t => t.Id == id) is not { } trigger)
        {
            return;
        }
        OpenTrigger(trigger, isNew: false);
    }

    private void AddTrigger_Click(object sender, RoutedEventArgs e) =>
        OpenTrigger(new TriggerInfo { Targets = DefaultTriggerTargets() }, isNew: true);

    /// <summary>Show a trigger's configuration on the right (a new one: an empty form).</summary>
    private void OpenTrigger(TriggerInfo trigger, bool isNew)
    {
        _editingTrigger = trigger;
        _editingNew = isNew;
        ShowView(ShellView.Trigger);
        BuildTriggerPane();
        RefreshTriggerList();
    }

    /// <summary>The right side for the open trigger: its header (on / off, delete), the privacy note and the form.</summary>
    private void BuildTriggerPane()
    {
        TriggerPane.Children.Clear();
        if (_editingTrigger is not { } t)
        {
            return;
        }
        var secondary = Res("TextFillColorSecondaryBrush");
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        // Header: an icon for what sets it off, its name and summary with status chips, and its controls.
        // Icon and text on the first row; the controls on their own row below (the middle column can be narrow).
        var header = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(10),
            Background = Res(_editingNew || t.Enabled ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDisabledBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = _editingNew ? "\uE710" : SourceGlyph(t.SourceKind), FontSize = 22, Foreground = Res("TextOnAccentFillColorPrimaryBrush") },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = _editingNew ? "New trigger" : TriggerName(t), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(new TextBlock { Text = _editingNew ? "Pick what sets it off, which lights it uses, and what they do." : Describe(t), Style = caption, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        if (!_editingNew)
        {
            Border Chip(string text, string bg, string fg) => new()
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 4),
                MinHeight = 24,
                Background = Res(bg),
                Child = new TextBlock { Text = text, Style = caption, Foreground = Res(fg), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap },
            };
            var chips = new WrapPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            chips.Children.Add(t.Enabled
                ? Chip("On", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush")
                : Chip("Off", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"));
            chips.Children.Add(Chip($"Priority #{_triggers.FindIndex(x => x.Id == t.Id) + 1}", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"));
            chips.Children.Add(Chip(t.Targets.Count == 0 ? "All devices" : t.Targets.Count == 1 ? "1 target" : $"{t.Targets.Count} targets", "SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"));
            titles.Children.Add(chips);
        }
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        if (!_editingNew)
        {
            var on = new ToggleSwitch { IsOn = t.Enabled, OnContent = "On", OffContent = "Off", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, $"{TriggerName(t)} on or off");
            var syncing = false;
            on.Toggled += (_, _) =>
            {
                if (syncing || on.IsOn == t.Enabled)
                {
                    return;
                }
                Safe("turning a trigger on or off", async () =>
                {
                    t.Enabled = on.IsOn;
                    try
                    {
                        await _client.SaveTriggerAsync(t);
                    }
                    catch (Exception e) when (Errors.Expected(e))
                    {
                        t.Enabled = !on.IsOn;
                        syncing = true;
                        on.IsOn = t.Enabled;
                        syncing = false;
                        ShowNotice(InfoBarSeverity.Error, "Could not change the trigger", Explain(e));
                        return;
                    }
                    await LoadTriggersAsync(); // listen (or stop listening) right away
                });
            };
            // Delete asks in a small flyout next to the button, not a dialog.
            var confirm = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 260,
                    Children = { new TextBlock { Text = $"Delete \"{TriggerName(t)}\"? Your lighting doesn't change.", TextWrapping = TextWrapping.Wrap }, confirm },
                },
            };
            var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = flyout, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(delete, "Delete trigger");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete {TriggerName(t)}");
            confirm.Click += (_, _) =>
            {
                flyout.Hide();
                Safe("deleting a trigger", () => DeleteTriggerAsync(t));
            };
            // Priority: move it up or down the list (the top one wins where triggers overlap).
            var index = _triggers.FindIndex(x => x.Id == t.Id);
            var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, IsEnabled = index > 0, VerticalAlignment = VerticalAlignment.Center };
            var down = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, IsEnabled = index >= 0 && index < _triggers.Count - 1, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(up, "Higher priority: shown on top of the triggers below it");
            ToolTipService.SetToolTip(down, "Lower priority");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, "Move up (higher priority)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, "Move down (lower priority)");
            up.Click += (_, _) => Safe("moving a trigger", () => MoveTriggerAsync(t, index - 1));
            down.Click += (_, _) => Safe("moving a trigger", () => MoveTriggerAsync(t, index + 1));
            var priority = new TextBlock
            {
                Text = $"Priority {index + 1} of {_triggers.Count}",
                Style = caption,
                Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(priority, "Where triggers light the same LEDs at the same time, the one higher in the list wins.");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { priority, up, down, on, delete } };
            Grid.SetRow(actions, 1);
            Grid.SetColumnSpan(actions, 2);
            header.Children.Add(actions);
        }
        TriggerPane.Children.Add(Card(header));

        // Keyboard and mouse triggers notice input everywhere: say so, and whether that is happening now.
        var listening = new List<string>();
        if (_input.ListeningToKeys)
        {
            listening.Add(_inputPlan.AllCodes ? "which key you press (to light that key)" : _inputPlan.AnyKey ? "key presses" : "the keys your triggers use");
        }
        if (_input.ListeningToMouse)
        {
            listening.Add(_inputPlan is { Clicks: true, Moves: true } ? "mouse clicks and movement" : _inputPlan.Clicks ? "mouse clicks" : "mouse movement");
        }
        TriggerPane.Children.Add(BuildTriggerEditor(t, _editingNew));
        // Privacy, in a quiet note with a shield.
        var privacy = new Grid { ColumnSpacing = 10, Margin = new Thickness(4, 0, 4, 0) };
        privacy.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        privacy.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        privacy.Children.Add(new FontIcon { Glyph = "\uEA18", FontSize = 14, Foreground = secondary, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var note = new TextBlock
        {
            Style = caption,
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = "Key and mouse triggers only notice that something was pressed, never what you type; sound triggers use the sound LightSync "
                + "already hears for the sound effects. Nothing is saved. "
                + (listening.Count > 0 ? $"Right now LightSync notices {string.Join(" and ", listening)}." : "Right now LightSync isn't listening to the keyboard or mouse."),
        };
        Grid.SetColumn(note, 1);
        privacy.Children.Add(note);
        TriggerPane.Children.Add(privacy);
    }

    /// <summary>Move a trigger in the list (its priority) and keep it open.</summary>
    private async Task MoveTriggerAsync(TriggerInfo t, int to)
    {
        if (string.IsNullOrEmpty(t.Id) || to < 0 || to >= _triggers.Count)
        {
            return;
        }
        try
        {
            await _client.MoveTriggerAsync(t.Id, to);
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not change the priority", Explain(e));
            return;
        }
        await LoadTriggersAsync();
        if (_triggers.FirstOrDefault(x => x.Id == t.Id) is { } moved)
        {
            OpenTrigger(moved, isNew: false);
        }
    }

    /// <summary>After Save: reload, and keep the saved trigger open (a new one is found by its new id).</summary>
    private async Task AfterTriggerSavedAsync(string? id, HashSet<string> before)
    {
        await LoadTriggersAsync();
        var saved = id is { Length: > 0 }
            ? _triggers.FirstOrDefault(t => t.Id == id)
            : _triggers.FirstOrDefault(t => t.Id is { } tid && !before.Contains(tid));
        if (saved is null)
        {
            _editingTrigger = null;
            ShowTriggers(force: true);
            return;
        }
        OpenTrigger(saved, isNew: false);
        RefreshHome();
    }

    private string TriggerName(TriggerInfo t) => string.IsNullOrWhiteSpace(t.Name) ? DescribeSource(t.Source) : t.Name;

    private async Task DeleteTriggerAsync(TriggerInfo t)
    {
        if (string.IsNullOrEmpty(t.Id))
        {
            return;
        }
        try
        {
            await _client.DeleteTriggerAsync(t.Id);
        }
        catch (Exception e) when (Errors.Expected(e))
        {
            ShowNotice(InfoBarSeverity.Error, "Could not delete the trigger", Explain(e));
            return;
        }
        await AfterTriggersChangedAsync();
    }

    // ---------- describing triggers ----------

    private string Describe(TriggerInfo t)
    {
        var targets = t.Targets.Count == 0
            ? "all devices"
            : t.Targets.Count <= 2 ? string.Join(" and ", t.Targets.Select(NameOf)) : $"{t.Targets.Count} devices and zones";
        return $"When {DescribeSource(t.Source).ToLowerInvariant()}: {DescribeAction(t.Action).ToLowerInvariant()} on {targets}."
            + (t.Enabled ? "" : " (off)");
    }

    private static string DescribeSource(JsonObject s)
    {
        var kind = s["kind"]?.ToString();
        switch (kind)
        {
            case "key":
                if (s["keys"] is JsonArray a)
                {
                    var names = KeyCodes(a).Select(KeyName).ToList();
                    return names.Count == 1 ? $"{names[0]} is pressed" : $"{string.Join(", ", names)} are pressed";
                }
                return "Any key is pressed";
            case "mouse_click":
                return s["button"]?.ToString() is { Length: > 0 } b ? $"The {b} mouse button is clicked" : "The mouse is clicked";
            case "mouse_move":
                return "The mouse moves";
            case "audio_beat":
                return "The music beats";
            case "audio_level":
                var band = BandChoices.FirstOrDefault(c => c.Id == s["band"]?.ToString()).Label ?? "Overall level";
                var above = s["above"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.5;
                return $"{band} is above {Math.Round(above * 100)}%";
            case "app_running":
                return s["site"]?.ToString() is { Length: > 0 } site ? $"{s["app"]} is on {site}" : $"{s["app"]} is running";
            default:
                return $"\"{kind}\" happens";
        }
    }

    private static string DescribeAction(JsonObject a)
    {
        var ms = a["duration_ms"] is JsonValue v && v.TryGetValue<int>(out var d) ? $" for {FormatMs(d)}" : "";
        var color = a["color"]?.ToString() is { Length: > 0 } c ? $" #{c.TrimStart('#').ToUpperInvariant()}" : "";
        // A local effect: where the key / click happened, out to the radius.
        ms += a["random"] is JsonValue rnd && rnd.TryGetValue<bool>(out var rb) && rb ? ", random color" : "";
        ms += a["layer_delay_ms"] is JsonValue ld && ld.TryGetValue<int>(out var ldi) && ldi > 0 ? $", {ldi} ms per ring" : "";
        ms += a["radius"] is JsonValue rv && rv.TryGetValue<double>(out var r) ? (r < 0.5 ? ", only where it happened" : $", radius {r:0}") : "";
        return a["kind"]?.ToString() switch
        {
            "flash" => $"Flash{color}{ms}",
            "pulse" => $"Pulse{ms}",
            "ripple" => $"Ripple{color}{ms}",
            "fade_in" => $"Fade in{color}{ms}",
            "preset" => $"Load the preset \"{a["name"]}\"",
            "lighting" => a["duration_ms"] is null ? $"Show {Effects.FirstOrDefault(e => e.Id == a["effect"]?.ToString())?.Name ?? "lighting"}{color}" : $"Play {Effects.FirstOrDefault(e => e.Id == a["effect"]?.ToString())?.Name ?? "an effect"}{color}{ms}",
            var k => $"\"{k}\"",
        };
    }

    private static string FormatMs(int ms) => ms >= 1000 ? $"{ms / 1000.0:0.##} s" : $"{ms} ms";

    /// <summary>A readable name for a virtual-key code (the codes are the same as Windows.System.VirtualKey).</summary>
    private static string KeyName(int vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B or 0x5C => "Windows",
            0x5D => "Menu",
            >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            0xA0 or 0xA1 => "Shift",
            0xA2 or 0xA3 => "Ctrl",
            0xA4 or 0xA5 => "Alt",
            0xAD => "Mute",
            0xAE => "Volume Down",
            0xAF => "Volume Up",
            0xB0 => "Next Track",
            0xB1 => "Previous Track",
            0xB3 => "Play/Pause",
            _ => ((VirtualKey)vk).ToString() is { } s && !int.TryParse(s, out _) ? s : $"Key {vk}",
        };
    }


}
