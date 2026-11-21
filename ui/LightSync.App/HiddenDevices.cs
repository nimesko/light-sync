using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace LightSync.App;

/// <summary>
/// Hidden devices: they leave the device list and the preview, and the service stops controlling them
/// (RPC set_hidden / hidden, and "hidden" on each device). Settings > Devices lists them to show again.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The empty list's button opens the hidden devices (every device is hidden) instead of starting OpenRGB.</summary>
    private bool _emptyShowsHidden;
    /// <summary>Settings > Devices lists the hidden devices.</summary>
    private bool _showHiddenInSettings;
    /// <summary>False once the service said it doesn't know set_hidden / hidden (an older lightsyncd).</summary>
    private bool _hidingSupported = true;
    /// <summary>Ids of hidden devices, also ones the service leaves out of its device list.</summary>
    private HashSet<string> _hiddenIds = [];
    /// <summary>Names of devices hidden in this session, for hidden devices the service no longer lists.</summary>
    private readonly Dictionary<string, string> _hiddenNames = [];

    /// <summary>
    /// Mark hidden devices. The device list says so itself; "hidden" is asked as well, for a service that
    /// keeps the list but doesn't send the flag.
    /// </summary>
    private async Task<List<DeviceInfo>> WithHiddenAsync(List<DeviceInfo> devices)
    {
        _hiddenIds = devices.Where(d => d.Hidden).Select(d => d.Id).ToHashSet();
        if (!_hidingSupported)
        {
            return devices;
        }
        try
        {
            var hidden = (await _client.HiddenAsync()).ToHashSet();
            hidden.UnionWith(_hiddenIds);
            _hiddenIds = hidden;
            return hidden.Count == 0 ? devices : devices.Select(d => d with { Hidden = hidden.Contains(d.Id) }).ToList();
        }
        catch (LightSyncException e) when (e.IsUnknownMethod)
        {
            _hidingSupported = false;
            return devices;
        }
        catch (Exception)
        {
            return devices;
        }
    }

    /// <summary>Hide a device (or show it again), then show the lists without it (or with it).</summary>
    private async Task SetHiddenAsync(string deviceId, bool hidden)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }
        var name = _allDevices.FirstOrDefault(d => d.Id == deviceId)?.Name
            ?? (_hiddenNames.TryGetValue(deviceId, out var known) ? known : deviceId);
        List<string> ids;
        try
        {
            if (_pushTimer.IsRunning)
            {
                await PushAsync();
            }
            ids = await _client.SetHiddenAsync(deviceId, hidden);
        }
        catch (LightSyncException e) when (e.IsUnknownMethod)
        {
            _hidingSupported = false;
            ShowNotice(InfoBarSeverity.Warning, "Hiding devices needs a newer LightSync service",
                "Update lightsyncd (the service next to LightSync.exe), then try again.");
            RefreshSettingsWindow(SettingsPage.Devices);
            return;
        }
        catch (Exception e)
        {
            ShowNotice(InfoBarSeverity.Error, hidden ? $"Could not hide {name}" : $"Could not show {name}", Explain(e));
            return;
        }
        if (_exiting)
        {
            return;
        }
        _hidingSupported = true;
        var set = ids.ToHashSet();
        // An answer without the list (older shape): trust the request.
        if (ids.Count == 0 && hidden)
        {
            set.Add(deviceId);
        }
        _hiddenIds = set;
        if (hidden)
        {
            _hiddenNames[deviceId] = name;
        }
        if (!hidden && _allDevices.All(d => d.Id != deviceId))
        {
            // The service didn't list it while hidden: fetch the devices again to get it back.
            await LoadDevicesAsync(rescan: false);
            return;
        }
        var devices = _allDevices.Select(d => d with { Hidden = set.Contains(d.Id) }).ToList();
        ShowDevices(devices);
        if (_devices.Count == 0 && devices.Count > 0)
        {
            ShowEmpty(devices.Count == 1 ? "Your only device is hidden." : $"All {devices.Count} of your devices are hidden.", hiddenOnly: true);
        }
        else if (_devices.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            TargetList.Visibility = Visibility.Visible;
            SetConnected();
        }
        if (hidden)
        {
            ShowNotice(InfoBarSeverity.Informational, $"{name} is hidden",
                "LightSync no longer changes its lighting. Show it again from Settings > Devices.", "Undo",
                () => SetHiddenAsync(deviceId, false));
        }
        else if (Notice.IsOpen && Notice.Title == $"{name} is hidden")
        {
            HideNotice();
        }
    }

    /// <summary>The small eye button on a device row (hides the device) or a zone row (hides only that zone).</summary>
    private void RowHide_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case TargetItem { Kind: TargetKind.Zone } zone:
                SetZoneHidden(zone.Id, true);
                break;
            case TargetItem { DeviceId: { Length: > 0 } id }:
                Safe("hiding a device", () => SetHiddenAsync(id, true));
                break;
        }
    }

    /// <summary>
    /// Hide one zone (an ARGB header with nothing on it, say) or show it again. It is only left out of the
    /// list and the preview (kept in the app's settings); the device and its other zones stay.
    /// </summary>
    private void SetZoneHidden(string zoneId, bool hidden)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return;
        }
        var list = _settings.HiddenZones ?? [];
        var changed = hidden ? !list.Contains(zoneId) : list.Remove(zoneId);
        if (hidden && changed)
        {
            list.Add(zoneId);
        }
        if (!changed)
        {
            return;
        }
        _settings.HiddenZones = list.Count > 0 ? list : null;
        _settings.Save();
        if (hidden && (_target.Id == zoneId || _target.Zone?.Id == zoneId))
        {
            _target = TargetItem.All(0); // Reselect falls back to "All devices"
        }
        _selection.RemoveAll(id => id == zoneId || _targets.Any(t => t.Id == id && t.Zone?.Id == zoneId));
        ShowDevices(_allDevices);
        var name = ZoneName(zoneId);
        if (hidden)
        {
            ShowNotice(InfoBarSeverity.Informational, $"{name} is hidden",
                "It no longer shows in the list or the preview. Show it again from Settings > Devices.", "Undo",
                () => { SetZoneHidden(zoneId, false); return Task.CompletedTask; });
        }
        else if (Notice.IsOpen && Notice.Title == $"{name} is hidden")
        {
            HideNotice();
        }
    }

    /// <summary>"Board / ARGB_V2_1" for a zone id, from the full device list (hidden zones included).</summary>
    private string ZoneName(string zoneId)
    {
        foreach (var d in _allDevices)
        {
            if (d.Zones?.FirstOrDefault(z => z.Id == zoneId) is { } z)
            {
                return $"{d.Name} / {z.Name}";
            }
        }
        return zoneId.Split(':').Last();
    }

    /// <summary>Right-click (or the menu key) on a device, zone or fan row: hide its device.</summary>
    private void TargetList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        try
        {
            var item = (args.OriginalSource as FrameworkElement)?.DataContext as TargetItem
                ?? ((args.OriginalSource as ListViewItem)?.Content as TargetItem);
            if (item is not { DeviceId: { Length: > 0 } id })
            {
                return;
            }
            var deviceName = item.Kind == TargetKind.Device ? item.Title : item.DeviceName;
            var menu = new MenuFlyout();
            if (item is { Kind: TargetKind.Zone or TargetKind.Fan, Zone: { } zone })
            {
                var hideZone = new MenuFlyoutItem { Text = $"Hide {zone.Name}", Icon = new FontIcon { Glyph = "" } };
                ToolTipService.SetToolTip(hideZone, "Only this zone leaves the list and the preview. Settings > Devices shows it again.");
                hideZone.Click += (_, _) => SetZoneHidden(zone.Id, true);
                menu.Items.Add(hideZone);
            }
            var hide = new MenuFlyoutItem { Text = $"Hide {deviceName}", Icon = new FontIcon { Glyph = "\uED1A" } };
            ToolTipService.SetToolTip(hide, "It leaves the list and the preview, and LightSync stops controlling it. Settings > Devices shows it again.");
            hide.Click += (_, _) => Safe("hiding a device", () => SetHiddenAsync(id, true));
            menu.Items.Add(hide);
            var hidden = new MenuFlyoutItem { Text = "Hidden devices...", Icon = new FontIcon { Glyph = "\uE890" } };
            hidden.Click += (_, _) =>
            {
                _showHiddenInSettings = true;
                OpenSettings(SettingsPage.Devices);
            };
            menu.Items.Add(hidden);
            if (args.TryGetPosition(TargetList, out var at))
            {
                menu.ShowAt(TargetList, new FlyoutShowOptions { Position = at });
            }
            else if (args.OriginalSource is FrameworkElement fe)
            {
                menu.ShowAt(fe);
            }
            args.Handled = true;
        }
        catch (Exception e)
        {
            CrashLog.Report("device menu", e);
        }
    }

    /// <summary>Settings > Devices: "Show hidden devices", and each hidden device with a Show button.</summary>
    private void AddHiddenDevicesSection(SettingsWindow w)
    {
        // Hidden zones first: they are kept by the app itself, so they work with any service.
        var zones = _settings.HiddenZones ?? [];
        if (zones.Count > 0)
        {
            w.Add(SettingsUi.Section("Hidden zones"));
            foreach (var zoneId in zones.ToList())
            {
                var show = new Button { Content = "Show" };
                var name = ZoneName(zoneId);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, $"Show {name}");
                show.Click += (_, _) =>
                {
                    SetZoneHidden(zoneId, false);
                    DispatcherQueue.TryEnqueue(() => RefreshSettingsWindow(SettingsPage.Devices));
                };
                w.Add(SettingsUi.Card(name, "Left out of the list and the preview", show));
            }
        }
        w.Add(SettingsUi.Section("Hidden devices"));
        if (!_hidingSupported)
        {
            w.Add(SettingsUi.Note("Hiding devices needs a newer LightSync service (lightsyncd). Update it to use this."));
            return;
        }
        // Hidden devices: the listed ones with their details, plus any the service leaves out of its list.
        var hidden = _allDevices.Where(d => d.Hidden).Select(d => (d.Id, d.Name, d.Details))
            .Concat(_hiddenIds.Where(id => _allDevices.All(d => d.Id != id))
                .Select(id => (Id: id, Name: _hiddenNames.TryGetValue(id, out var n) ? n : id.Split(':').Last(), Details: "Not connected right now, or not listed by the service")))
            .ToList();
        var toggle = SettingsUi.Toggle(_showHiddenInSettings);
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != _showHiddenInSettings)
            {
                _showHiddenInSettings = toggle.IsOn;
                // Rebuild after this event, not while the toggle is still handling it.
                DispatcherQueue.TryEnqueue(() => RefreshSettingsWindow(SettingsPage.Devices));
            }
        };
        w.Add(SettingsUi.Card("Show hidden devices",
            hidden.Count switch
            {
                0 => "No device is hidden. Right-click a device in the list (or use its eye button) to hide it.",
                1 => "1 device is hidden: LightSync doesn't show or change it.",
                _ => $"{hidden.Count} devices are hidden: LightSync doesn't show or change them.",
            }, toggle));
        if (!_showHiddenInSettings)
        {
            return;
        }
        foreach (var device in hidden)
        {
            var show = new Button { Content = "Show" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, $"Show {device.Name}");
            var id = device.Id;
            show.Click += (_, _) => w.Run("showing a device", () => SetHiddenAsync(id, false));
            w.Add(SettingsUi.Card(device.Name, device.Details, show));
        }
    }
}
