using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LightSync.App;

public sealed record FanInfo(string Id, string Name);

/// <summary>One LED of a device: its place in the device's frame, name ("A", "Logo 1"), zone, and grid place if known.</summary>
public sealed record LedInfo(int Index, string Name, string Zone, double? Col, double? Row);

public sealed record ZoneInfo(string Id, uint Index, string Name, uint Leds, bool Resizable, uint MaxLeds, List<FanInfo> Fans)
{
    public override string ToString() => $"{Name} ({Leds} LEDs)";
}

public sealed record DeviceInfo(string Id, string Name, string Kind, string Provider, int LedCount,
    [property: JsonPropertyName("openrgb_index")] uint? OpenRgbIndex, List<ZoneInfo> Zones, bool Hidden = false)
{
    public string Details => $"{KindLabel} · {LedCount} LEDs · {Provider}";

    public string KindLabel => Kind switch
    {
        "Ram" => "Memory",
        "Fan" => "Fans",
        "Other" => "Lighting",
        _ => Kind,
    };

    /// <summary>Segoe Fluent Icons glyph for the device type.</summary>
    public string Glyph => Kind switch
    {
        "Keyboard" => "\uE765",
        "Mouse" => "\uE962",
        "Speaker" => "\uE7F5",
        "Motherboard" or "Ram" => "\uE950",
        _ => "\uE82F",
    };
}

public sealed record LightStatus(string Effect, string Color, int Brightness, int Speed, bool Connected, string? Error, bool? On);

/// <summary>What one target (device, zone, fan, group or "all") currently shows.</summary>
/// <remarks><see cref="Palette"/>: the colors of a sound effect ("effect", "random", "cold_hot", "fire", "neon").</remarks>
public sealed record TargetState(string Effect, string Color, int Brightness, int Speed, string Target, bool Own, string Palette = "effect", string Split = "device", int Colors = 3);

/// <summary>What a row of the device list shows: its lighting, whether it is set on that row itself, and for groups whether members differ.</summary>
public sealed record RowState(string Effect, string Color, int Brightness, int Speed, bool Own, bool Mixed, bool On, string Palette = "effect", string Split = "device", int Colors = 3);

public sealed record GroupInfo(string Id, string Name, List<string> Members);

/// <summary>An error the service answered with (or a broken answer). <see cref="Code"/> is the JSON-RPC error code, 0 if none.</summary>
public sealed class LightSyncException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>The service is older than this app and doesn't know the method.</summary>
    public bool IsUnknownMethod => Code == -32601 || Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The sound the service hears right now, from "audio_spectrum": levels 0..1, 24 bands and their peaks.</summary>
public sealed class AudioSpectrum
{
    public bool Active { get; init; }
    public double Level { get; init; }
    public double Bass { get; init; }
    public double Mid { get; init; }
    public double Treble { get; init; }
    public bool Beat { get; init; }
    /// <summary>The Windows output device being captured (loopback), null until capture runs.</summary>
    public string? Device { get; init; }
    public double[] Bands { get; init; } = [];
    public double[] Peaks { get; init; } = [];
    /// <summary>The tempo once steady (0 until then).</summary>
    public double TempoBpm { get; init; }
    /// <summary>The music's character over the last seconds: 0 dark / bass-heavy .. 1 bright / airy.</summary>
    public double Mood { get; init; } = 0.5;
    /// <summary>Hits of level, bass, mid and treble (0..1, 0.5 while steady): what the sound effects follow.</summary>
    public double[] Punch { get; init; } = [];
    /// <summary>How strong the current beat is (0 between beats).</summary>
    public double BeatStrength { get; init; }
    /// <summary>How energetic the music is over the last seconds, 0..1.</summary>
    public double Energy { get; init; }
}

/// <summary>What the service wants the app to listen for, from "input_wanted".</summary>
public sealed class InputWanted
{
    /// <summary>Key presses are wanted, without saying which ("keys": true or "any").</summary>
    public bool AnyKey { get; init; }
    /// <summary>Every key press is wanted with its code: a trigger lights the pressed key itself ("key_codes").</summary>
    public bool AllKeyCodes { get; init; }
    /// <summary>Programs "app is running" triggers watch ("game.exe"): only these are reported back.</summary>
    public List<string> Apps { get; init; } = [];
    /// <summary>Virtual-key codes some trigger listens for ("keys": [vk...]).</summary>
    public HashSet<int> KeyCodes { get; init; } = [];
    public bool MouseClick { get; init; }
    public bool MouseMove { get; init; }
    public bool Keys => AnyKey || KeyCodes.Count > 0;

    public static readonly InputWanted None = new();
}

/// <summary>
/// One trigger: when <see cref="Source"/> happens, <see cref="Action"/> plays on <see cref="Targets"/> (empty = all devices).
/// Source and action are kept as JSON, so kinds this app doesn't know survive an edit.
/// </summary>
public sealed class TriggerInfo
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public JsonObject Source { get; set; } = new() { ["kind"] = "key", ["keys"] = "any" };
    public JsonObject Action { get; set; } = new() { ["kind"] = "flash", ["color"] = "FFFFFF", ["duration_ms"] = 300 };
    public List<string> Targets { get; set; } = [];
    /// <summary>More effects it plays at once, each {action, targets}, over the one above.</summary>
    public JsonArray Also { get; set; } = [];

    public string SourceKind => Source["kind"]?.GetValue<string>() ?? "";
    public string ActionKind => Action["kind"]?.GetValue<string>() ?? "";

    public static TriggerInfo? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }
        return new TriggerInfo
        {
            Id = o["id"]?.ToString(),
            Name = o["name"]?.ToString() ?? "",
            Enabled = o["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) ? on : true,
            Source = o["source"] is JsonObject s ? (JsonObject)s.DeepClone() : new JsonObject { ["kind"] = "key", ["keys"] = "any" },
            Action = o["action"] is JsonObject a ? (JsonObject)a.DeepClone() : new JsonObject { ["kind"] = "flash", ["color"] = "FFFFFF", ["duration_ms"] = 300 },
            Targets = o["targets"] is JsonArray t ? t.Select(x => x?.ToString()).OfType<string>().ToList() : [],
            Also = o["also"] is JsonArray more ? (JsonArray)more.DeepClone() : [],
        };
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (!string.IsNullOrEmpty(Id))
        {
            o["id"] = Id;
        }
        o["name"] = Name;
        o["enabled"] = Enabled;
        o["source"] = Source.DeepClone();
        o["action"] = Action.DeepClone();
        o["targets"] = new JsonArray(Targets.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        if (Also.Count > 0)
        {
            o["also"] = Also.DeepClone();
        }
        return o;
    }
}

/// <summary>JSON-RPC client for lightsyncd over the named pipe \\.\pipe\lightsync (one JSON object per line).</summary>
public sealed class LightSyncClient
{
    private const string PipeName = "lightsync";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<JsonNode?> CallAsync(string method, object? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = ++_nextId,
                ["method"] = method,
            };
            if (parameters is not null)
            {
                request["params"] = JsonSerializer.SerializeToNode(parameters, Json);
            }
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync() ?? throw new LightSyncException("lightsyncd closed the connection");
            var response = JsonNode.Parse(line) ?? throw new LightSyncException("empty response from lightsyncd");
            if (response["error"] is JsonNode err)
            {
                var code = err["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
                throw new LightSyncException(err["message"]?.ToString() ?? "unknown error", code);
            }
            return response["result"];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<DeviceInfo>> DevicesAsync(bool rescan = false)
    {
        var result = await CallAsync(rescan ? "rescan" : "devices");
        return result?.Deserialize<List<DeviceInfo>>(Json) ?? [];
    }

    public async Task<LightStatus> StatusAsync() =>
        (await CallAsync("status"))?.Deserialize<LightStatus>(Json) ?? throw new LightSyncException("empty status");

    public async Task<Dictionary<string, RowState>> GetTargetsAsync(IEnumerable<string> targets) =>
        (await CallAsync("get_targets", new { targets = targets.Distinct().ToArray() }))?.Deserialize<Dictionary<string, RowState>>(Json) ?? [];

    public async Task<TargetState> GetTargetAsync(string target) =>
        (await CallAsync("get_target", new { target }))?.Deserialize<TargetState>(Json) ?? throw new LightSyncException("empty state");

    /// <summary>
    /// Set the lighting of one target: "all", a device / zone / fan id, or "group:" plus the group id.
    /// The service renders it continuously.
    /// </summary>
    public Task SetStateAsync(string target, string effect, string hex, int brightness, int speed, string palette, string split, int colors) =>
        CallAsync("set_state", new { target, effect, color = hex, brightness, speed, palette, split, colors });

    public async Task<List<GroupInfo>> GroupsAsync() =>
        (await CallAsync("groups"))?.Deserialize<List<GroupInfo>>(Json) ?? [];

    /// <summary>Create (id null) or update a group.</summary>
    public async Task<GroupInfo> SaveGroupAsync(string? id, string name, IEnumerable<string> members) =>
        (await CallAsync("save_group", new { id, name, members = members.ToList() }))?.Deserialize<GroupInfo>(Json)
        ?? throw new LightSyncException("empty group");

    public Task DeleteGroupAsync(string id) => CallAsync("delete_group", new { id });

    /// <summary>Split an addressable header into fans (fans 0 = one strip again). Returns the new device list.</summary>
    public async Task<List<DeviceInfo>> SetLayoutAsync(string zone, int fans, int ledsPerFan) =>
        (await CallAsync("set_layout", new { zone, fans, leds_per_fan = ledsPerFan }))?.Deserialize<List<DeviceInfo>>(Json) ?? [];

    /// <summary>All lights off (black) or back on. Changing any lighting also turns them on.</summary>
    public Task SetPowerAsync(bool on) => CallAsync("set_power", new { on });

    public async Task<List<string>> ProfilesAsync() =>
        (await CallAsync("profiles"))?.Deserialize<List<string>>(Json) ?? [];

    /// <summary>save_profile, load_profile or delete_profile. Returns the profile names.</summary>
    public async Task<List<string>> ProfileAsync(string action, string name) =>
        (await CallAsync($"{action}_profile", new { name }))?.Deserialize<List<string>>(Json) ?? [];

    /// <summary>Back to default lighting with no per-device settings, groups or fan layouts.</summary>
    public Task ResetAsync(bool profiles) => CallAsync("reset", new { profiles });

    /// <summary>What every device and zone shows right now (at most <paramref name="max"/> colors each).</summary>
    public async Task<List<PreviewDevice>> PreviewAsync(int max) =>
        (await CallAsync("preview", new { max }))?.Deserialize<List<PreviewDevice>>(Json) ?? [];

    public Task SetColorAsync(string hex) => CallAsync("set_color", new { color = hex });

    // ---------- hidden devices ----------

    /// <summary>Hide a device (LightSync stops controlling it) or show it again. Returns the ids of all hidden devices.</summary>
    public async Task<List<string>> SetHiddenAsync(string id, bool hidden)
    {
        var result = await CallAsync("set_hidden", new { id, hidden });
        return Ids(result?["hidden"] ?? result);
    }

    public async Task<List<string>> HiddenAsync() => Ids(await CallAsync("hidden"));

    private static List<string> Ids(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.ToString()).OfType<string>().ToList() : [];

    // ---------- sound ----------

    /// <summary>The analyzer's data: levels, beat, 24 bands and their peaks (0..1).</summary>
    public async Task<AudioSpectrum> AudioSpectrumAsync()
    {
        var r = await CallAsync("audio_spectrum") as JsonObject ?? throw new LightSyncException("empty spectrum");
        return new AudioSpectrum
        {
            Active = Bool(r["active"]),
            Level = Num(r["level"]),
            Bass = Num(r["bass"]),
            Mid = Num(r["mid"]),
            Treble = Num(r["treble"]),
            Beat = Bool(r["beat"]),
            Device = r["device"] is JsonValue dv && dv.TryGetValue<string>(out var dn) && dn.Length <= 256 ? dn : null,
            Bands = r["bands"] is JsonArray b ? b.Select(Num).ToArray() : [],
            Peaks = r["peaks"] is JsonArray p ? p.Select(Num).ToArray() : [],
            TempoBpm = r["tempo_bpm"] is JsonValue tb && tb.TryGetValue<double>(out var bpm) && double.IsFinite(bpm) ? Math.Clamp(bpm, 0, 400) : 0,
            Mood = r["mood"] is null ? 0.5 : Num(r["mood"]),
            Energy = Num(r["energy"]),
            Punch = r["punch"] is JsonObject pu ? [Num(pu["level"]), Num(pu["bass"]), Num(pu["mid"]), Num(pu["treble"])] : [],
            BeatStrength = Num(r["beat_strength"]),
        };
    }

    /// <summary>Older services: only whether sound is captured and its level.</summary>
    public async Task<AudioSpectrum> AudioStatusAsync()
    {
        var r = await CallAsync("audio_status") as JsonObject ?? throw new LightSyncException("empty audio status");
        return new AudioSpectrum
        {
            Active = Bool(r["active"]),
            Level = Num(r["level"]),
            Device = r["device"] is JsonValue dv && dv.TryGetValue<string>(out var dn) && dn.Length <= 256 ? dn : null,
        };
    }

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static double Num(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;

    // ---------- triggers ----------

    public async Task<List<TriggerInfo>> TriggersAsync() =>
        (await CallAsync("triggers")) is JsonArray a ? a.Select(TriggerInfo.FromJson).OfType<TriggerInfo>().ToList() : [];

    /// <summary>Create (no id) or update a trigger.</summary>
    public Task SaveTriggerAsync(TriggerInfo trigger) =>
        CallAsync("save_trigger", new JsonObject { ["trigger"] = trigger.ToJson() });

    public Task DeleteTriggerAsync(string id) => CallAsync("delete_trigger", new { id });

    /// <summary>Ask the service to save and exit (LightSync closing entirely). An older service ignores it.</summary>
    public Task ShutdownAsync() => CallAsync("shutdown");

    /// <summary>A device's LEDs (frame order), for picking single LEDs as trigger targets ("device#index").</summary>
    public async Task<List<LedInfo>> LedsAsync(string device) =>
        (await CallAsync("leds", new { device })) is JsonArray a
            ? a.OfType<JsonObject>().Select(o => new LedInfo(
                o["index"] is JsonValue i && i.TryGetValue<int>(out var ix) ? ix : -1,
                o["name"]?.ToString() ?? "",
                o["zone"]?.ToString() ?? "",
                o["col"] is JsonValue c && c.TryGetValue<double>(out var cx) ? cx : null,
                o["row"] is JsonValue r && r.TryGetValue<double>(out var rx) ? rx : null)).Where(l => l.Index >= 0).ToList()
            : [];

    /// <summary>Move a trigger to position <paramref name="to"/> (the list order is the priority).</summary>
    public Task MoveTriggerAsync(string id, int to) => CallAsync("move_trigger", new { id, to });

    /// <summary>Which of the watched programs run now.</summary>
    public Task SetAppsAsync(IEnumerable<string> apps) => CallAsync("set_apps", new { apps = apps.ToArray() });

    /// <summary>
    /// Tell the service a key was pressed, the mouse clicked or moved. <paramref name="vk"/> is only
    /// given for keys a trigger listens for; otherwise it is just "a key was pressed". <paramref name="sc"/>
    /// is where the key is (its scan code), sent with the code, to light the pressed key itself.
    /// </summary>
    /// <returns>How many triggers fired ("fired"), 0 if the answer doesn't say.</returns>
    public async Task<int> InputEventAsync(string kind, int? vk = null, string? button = null, int? sc = null)
    {
        var p = new JsonObject { ["kind"] = kind };
        if (vk is { } k)
        {
            p["vk"] = k;
        }
        if (sc is { } place)
        {
            p["sc"] = place;
        }
        if (button is not null)
        {
            p["button"] = button;
        }
        var r = await CallAsync("input_event", p);
        return r?["fired"] is JsonValue f && f.TryGetValue<int>(out var n) ? n : 0;
    }

    /// <summary>Per device: how LightSync drives it and how the last write went ("diagnostics").</summary>
    public async Task<List<JsonObject>> DiagnosticsAsync() =>
        (await CallAsync("diagnostics")) is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    /// <summary>What keyboard and mouse input enabled triggers need right now.</summary>
    public async Task<InputWanted> InputWantedAsync()
    {
        if (await CallAsync("input_wanted") is not JsonObject r)
        {
            return InputWanted.None;
        }
        var codes = new HashSet<int>();
        var anyKey = false;
        switch (r["keys"])
        {
            case JsonArray list:
                foreach (var x in list)
                {
                    if (x is JsonValue v && v.TryGetValue<int>(out var code) && code is > 0 and < 256)
                    {
                        codes.Add(code);
                    }
                }
                break;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                anyKey = b;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                anyKey = s == "any";
                break;
        }
        return new InputWanted { AnyKey = anyKey, KeyCodes = codes, AllKeyCodes = Bool(r["key_codes"]), Apps = r["apps"] is JsonArray apps ? apps.Select(a => a?.ToString() ?? "").Where(a => a.Length is > 0 and <= 128).ToList() : [], MouseClick = Bool(r["mouse_click"]), MouseMove = Bool(r["mouse_move"]) };
    }

    /// <summary>
    /// Start lightsyncd if its pipe is not there. Looks next to this exe, then in the repo's
    /// target\release and target\debug folders (walking up from the exe), or LIGHTSYNCD_PATH.
    /// </summary>
    public static bool EnsureDaemonRunning()
    {
        if (PipeAvailable(200))
        {
            return true;
        }
        var exe = FindDaemon();
        if (exe is null)
        {
            return false;
        }
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        for (var i = 0; i < 30 && !PipeAvailable(100); i++)
        {
            Thread.Sleep(100);
        }
        return true;
    }

    private static bool PipeAvailable(int timeoutMs)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            probe.Connect(timeoutMs);
            return true;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return false;
        }
    }

    private static string? FindDaemon()
    {
        var env = Environment.GetEnvironmentVariable("LIGHTSYNCD_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var next = Path.Combine(dir.FullName, "lightsyncd.exe");
        if (File.Exists(next))
        {
            return next;
        }
        for (var d = dir; d is not null; d = d.Parent)
        {
            foreach (var profile in new[] { "release", "debug" })
            {
                var candidate = Path.Combine(d.FullName, "target", profile, "lightsyncd.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
