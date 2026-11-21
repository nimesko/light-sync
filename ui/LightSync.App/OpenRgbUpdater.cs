using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LightSync.App;

/// <summary>One OpenRGB release with its Windows 64-bit download.</summary>
public sealed record OpenRgbRelease(string Tag, string Name, DateTimeOffset Published, bool Prerelease, string AssetName, string AssetUrl, long Size)
{
    public string Version => OpenRgbVersion.Clean(Tag) ?? OpenRgbVersion.Clean(Name) ?? Tag;

    public override string ToString() =>
        $"{Version}{(Prerelease ? " (pre-release)" : "")} - {Published.LocalDateTime:d MMM yyyy}";
}

/// <summary>Version numbers like "1.0rc2" or "1.1": compared as major.minor.patch, release candidates before the release.</summary>
public static partial class OpenRgbVersion
{
    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?(?:[\s_-]*(?:rc|release[\s_-]*candidate)[\s_-]*(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static (int, int, int, int)? Key(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var m = Pattern().Match(text);
        if (!m.Success)
        {
            return null;
        }
        int Num(int g, int fallback) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : fallback;
        var key = (Num(1, 0), Num(2, 0), Num(3, 0), Num(4, int.MaxValue));
        // Some OpenRGB builds report 0.0.0.0 in their file info: that means "unknown", not "very old".
        return key is (0, 0, 0, _) ? null : key;
    }

    /// <summary>Normalized version text, e.g. "1.0rc2" for "release_candidate_1.0rc2" or "1.0 Release Candidate 2".</summary>
    public static string? Clean(string? text) => Key(text) is { } k
        ? $"{k.Item1}.{k.Item2}{(k.Item3 > 0 ? $".{k.Item3}" : "")}{(k.Item4 != int.MaxValue ? $"rc{k.Item4}" : "")}"
        : null;

    /// <summary>True if <paramref name="candidate"/> is newer than <paramref name="installed"/> (false if either is unknown).</summary>
    public static bool IsNewer(string? candidate, string? installed) =>
        Key(candidate) is { } c && Key(installed) is { } i && c.CompareTo(i) > 0;
}

/// <summary>Lists OpenRGB releases (Codeberg, then GitLab as a fallback) and downloads one.</summary>
public static class OpenRgbUpdater
{
    private const string CodebergReleases = "https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases?limit=30";
    private const string GitLabReleases = "https://gitlab.com/api/v4/projects/CalcProgrammer1%2FOpenRGB/releases?per_page=30";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LightSync/1.0");
        return http;
    }

    /// <summary>Releases with a Windows 64-bit build, newest first.</summary>
    public static async Task<List<OpenRgbRelease>> ListAsync(bool includePrereleases)
    {
        var errors = new List<string>();
        foreach (var (url, parse) in new (string, Func<JsonNode, IEnumerable<OpenRgbRelease>>)[] { (CodebergReleases, ParseGitea), (GitLabReleases, ParseGitLab) })
        {
            try
            {
                var json = JsonNode.Parse(await Http.GetStringAsync(url));
                var list = json is null ? [] : parse(json).ToList();
                if (list.Count > 0)
                {
                    return list.Where(r => includePrereleases || !r.Prerelease).OrderByDescending(r => r.Published).ToList();
                }
                errors.Add($"{new Uri(url).Host}: no Windows 64-bit download found");
            }
            catch (Exception e)
            {
                errors.Add($"{new Uri(url).Host}: {e.Message}");
            }
        }
        throw new InvalidOperationException("Could not list OpenRGB releases. " + string.Join("; ", errors));
    }

    private static bool IsWindows64(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".zip") && n.Contains("windows") && n.Contains("64") && !n.Contains("arm") && !n.Contains("32");
    }

    private static IEnumerable<OpenRgbRelease> ParseGitea(JsonNode json)
    {
        foreach (var r in json.AsArray())
        {
            if (r is null || r["draft"]?.GetValue<bool>() == true)
            {
                continue;
            }
            var asset = r["assets"]?.AsArray().FirstOrDefault(a => IsWindows64(a?["name"]?.GetValue<string>() ?? ""));
            if (asset is null)
            {
                continue;
            }
            yield return new OpenRgbRelease(
                r["tag_name"]?.GetValue<string>() ?? "",
                r["name"]?.GetValue<string>() ?? "",
                DateTimeOffset.TryParse(r["published_at"]?.GetValue<string>(), out var d) ? d : DateTimeOffset.MinValue,
                r["prerelease"]?.GetValue<bool>() ?? false,
                asset["name"]!.GetValue<string>(),
                asset["browser_download_url"]!.GetValue<string>(),
                asset["size"]?.GetValue<long>() ?? 0);
        }
    }

    private static IEnumerable<OpenRgbRelease> ParseGitLab(JsonNode json)
    {
        foreach (var r in json.AsArray())
        {
            var link = r?["assets"]?["links"]?.AsArray().FirstOrDefault(a => IsWindows64(a?["name"]?.GetValue<string>() ?? "") || IsWindows64(a?["url"]?.GetValue<string>() ?? ""));
            if (r is null || link is null)
            {
                continue;
            }
            yield return new OpenRgbRelease(
                r["tag_name"]?.GetValue<string>() ?? "",
                r["name"]?.GetValue<string>() ?? "",
                DateTimeOffset.TryParse(r["released_at"]?.GetValue<string>(), out var d) ? d : DateTimeOffset.MinValue,
                r["upcoming_release"]?.GetValue<bool>() ?? false,
                link["name"]?.GetValue<string>() ?? "",
                (link["direct_asset_url"] ?? link["url"])!.GetValue<string>(),
                0);
        }
    }

    /// <summary>A downloaded release: the zip and the SHA-256 of the bytes that came over HTTPS.</summary>
    public sealed record Download(string Zip, string Sha256, string Version);

    /// <summary>
    /// Download a release's zip into %TEMP%\LightSync, hashing it on the way. It is unpacked later, by
    /// the elevated install, from a copy in an admin-only folder whose hash must match: so nothing
    /// another program changes in %TEMP% in the meantime can end up running as administrator.
    /// </summary>
    public static async Task<Download> DownloadAsync(OpenRgbRelease release, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(release.AssetUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The download link is not HTTPS.");
        }
        var root = Path.Combine(Path.GetTempPath(), "LightSync");
        Directory.CreateDirectory(root);
        var safe = string.Concat(release.Version.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
        var zip = Path.Combine(root, $"openrgb-{safe}.zip");
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0)
                {
                    progress.Report((double)done / total);
                }
            }
        }
        return new Download(zip, Convert.ToHexString(sha.GetHashAndReset()), release.Version);
    }

    /// <summary>
    /// Install a downloaded release over LightSync's OpenRGB copy and start it (one UAC prompt). Fails
    /// (exit code 3) if the zip changed since it was downloaded, or (4) if it holds no OpenRGB.exe; the
    /// installed copy is then left as it was.
    /// </summary>
    public static async Task<ElevatedResult> InstallAsync(Download download)
    {
        var result = await OpenRgbLauncher.RunElevatedAsync(InstallScript(download));
        try
        {
            File.Delete(download.Zip);
        }
        catch (Exception)
        {
            // Temp files get cleaned up eventually.
        }
        return result;
    }

    /// <summary>
    /// The elevated install. The zip is copied into a staging folder next to the managed copy (under
    /// Program Files: only administrators can write there) and its SHA-256 checked there, so the files
    /// that end up running as administrator are exactly the ones downloaded. The previous copy is kept
    /// only until the new one is in place, and put back if the swap fails.
    /// </summary>
    private static string InstallScript(Download d) => $$"""
        $ErrorActionPreference = 'Stop'
        $dst = {{OpenRgbLauncher.Quote(OpenRgbLauncher.ManagedDir)}}
        $prev = {{OpenRgbLauncher.Quote(OpenRgbLauncher.ManagedBackupDir)}}
        $parent = Split-Path -Parent $dst
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $stage = Join-Path $parent ('.staging-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        $zip = Join-Path $stage 'openrgb.zip'
        Copy-Item -LiteralPath {{OpenRgbLauncher.Quote(d.Zip)}} -Destination $zip
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne {{OpenRgbLauncher.Quote(d.Sha256)}}) { Remove-Item $stage -Recurse -Force; exit 3 }
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $stage 'files')
        $exe = Get-ChildItem -LiteralPath (Join-Path $stage 'files') -Recurse -Filter 'OpenRGB.exe' | Select-Object -First 1
        if (-not $exe) { Remove-Item $stage -Recurse -Force; exit 4 }
        Set-Content -LiteralPath (Join-Path $exe.DirectoryName {{OpenRgbLauncher.Quote(OpenRgbLauncher.VersionFile)}}) -Value {{OpenRgbLauncher.Quote(d.Version)}} -Encoding ASCII
        Stop-Process -Name OpenRGB -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        if (Test-Path $prev) { Remove-Item $prev -Recurse -Force }
        if (Test-Path $dst) { Move-Item $dst $prev }
        try {
            Move-Item -LiteralPath $exe.DirectoryName -Destination $dst
            if (Test-Path $prev) { Remove-Item $prev -Recurse -Force -ErrorAction SilentlyContinue }
        } catch {
            if ((Test-Path $prev) -and -not (Test-Path $dst)) { Move-Item $prev $dst }
            throw
        } finally {
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
        {{OpenRgbLauncher.StartScript(Path.Combine(OpenRgbLauncher.ManagedDir, "OpenRGB.exe"))}}
        """;
}

/// <summary>App settings (not lighting): %APPDATA%\LightSync\app-settings.json.</summary>
public sealed class AppSettings
{
    public bool AutoUpdateOpenRgb { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool LightsOffOnSleep { get; set; } = true;
    public bool LightsOffOnLock { get; set; }
    /// <summary>Turn the lights off after this many minutes without keyboard or mouse input (0 = never).</summary>
    public int IdleOffMinutes { get; set; }
    /// <summary>Window position and size (x, y, width, height) in pixels, restored at start.</summary>
    public int[]? WindowBounds { get; set; }
    /// <summary>
    /// Where each part of the 3D preview was moved to ("Arrange"), by stable device / zone / fan id, plus
    /// "@case" and "@desk" for the case and the desk: [x, y, z] in mm and rotation [x, y, z] in degrees,
    /// relative to the case (parts inside it), the desk (peripherals) or the room (case and desk).
    /// </summary>
    public Dictionary<string, double[]>? Preview3D { get; set; }
    /// <summary>The 3D preview's camera: position [x, y, z] and the point it looks at [x, y, z], in mm.</summary>
    public double[]? Preview3DCamera { get; set; }
    /// <summary>The case's size in the 3D preview: width, height, depth in mm (null = ATX mid-tower).</summary>
    public double[]? Preview3DCaseSize { get; set; }
    /// <summary>What each part of the preview is drawn as, when the user changed it: fan, strip, ram, board, keyboard, mouse, speaker or other.</summary>
    public Dictionary<string, string>? PreviewTypes { get; set; }
    /// <summary>
    /// Zones the user hid (by zone id, e.g. an ARGB header with nothing plugged in): left out of the device
    /// list and the preview. Their device stays; Settings > Devices shows them again.
    /// </summary>
    public List<string>? HiddenZones { get; set; }
    /// <summary>The preset loaded or saved last (status bar), null once the lighting was changed by hand.</summary>
    public string? CurrentPreset { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightSync", "app-settings.json");

    public static string Folder => Path.GetDirectoryName(FilePath)!;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // Write a temporary file, then swap it in: a crash mid-write never leaves a broken file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
