using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

namespace LightSync.App;

public enum OpenRgbStartResult
{
    AlreadyRunning,
    Started,
    NotFound,
    Declined,
    TimedOut,
}

public enum ElevatedResult
{
    Ok,
    Declined,
    Failed,
}

/// <summary>
/// Finds OpenRGB and starts, stops or restarts it elevated (one UAC prompt each) as a headless SDK
/// server on 127.0.0.1:6742. Search order: LIGHTSYNC_OPENRGB env var, the copy LightSync installs and
/// updates (Program Files\LightSync\OpenRGB), an "openrgb" folder next to LightSync.exe, the repo's
/// vendor\openrgb folder (walking up from the exe), then Program Files\OpenRGB.
/// </summary>
public static class OpenRgbLauncher
{
    public const int Port = 6742;

    /// <summary>
    /// OpenRGB's settings folder for the copy LightSync starts. OpenRGB's normal one (%APPDATA%\OpenRGB) can
    /// hold profiles that load on start or resume, plugins, and SDK client connections; any of those
    /// changes device modes and colors behind LightSync's back. A separate folder keeps OpenRGB a plain
    /// device server, and leaves the user's own OpenRGB settings untouched.
    /// It sits under Program Files (only administrators can write there) because OpenRGB runs as
    /// administrator and loads plugin DLLs from its settings folder: a folder the user's programs can
    /// write to would let any of them run code as administrator. Elevated scripts create it.
    /// </summary>
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LightSync", "OpenRGB-config");

    /// <summary>
    /// Command line for a headless SDK server using <see cref="ConfigDir"/>. The server only listens on
    /// this PC (127.0.0.1): OpenRGB's SDK has no password, so on all interfaces anyone on the network
    /// could change the lights, and Windows Firewall asks about it.
    /// </summary>
    public static string Arguments =>
        $"--server --server-host 127.0.0.1 --noautoconnect --startminimized --config \"{ConfigDir}\"";

    /// <summary>PowerShell that creates <see cref="ConfigDir"/> (run elevated).</summary>
    private static string MakeConfigDir => $"New-Item -ItemType Directory -Path {Quote(ConfigDir)} -Force | Out-Null";

    /// <summary>PowerShell that starts <paramref name="exe"/> as the SDK server (run elevated).</summary>
    public static string StartScript(string exe) => $"""
        {MakeConfigDir}
        Start-Process -FilePath {Quote(exe)} -ArgumentList {Quote(Arguments)} -WorkingDirectory {Quote(Path.GetDirectoryName(exe)!)} -WindowStyle Hidden
        """;

    public static async Task<bool> IsServerUpAsync(int timeoutMs = 400)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync("127.0.0.1", Port, cts.Token);
            return true;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return false;
        }
    }

    public static async Task<OpenRgbStartResult> EnsureRunningAsync(TimeSpan wait)
    {
        if (await IsServerUpAsync())
        {
            return OpenRgbStartResult.AlreadyRunning;
        }
        var exe = Find();
        if (exe is null)
        {
            return OpenRgbStartResult.NotFound;
        }
        if (await NoPromptReadyAsync(exe) && await RunTaskAsync(StartTask))
        {
            return await WaitForServerAsync(wait);
        }
        // One prompt, which also sets up (or brings up to date) the no-prompt tasks: the next starts,
        // after a reboot too, need none.
        var script = await TasksIfWantedAsync(exe) + StartScript(exe);
        return await RunElevatedAsync(script) switch
        {
            ElevatedResult.Declined => OpenRgbStartResult.Declined,
            _ => await WaitForServerAsync(wait),
        };
    }

    /// <summary>Where LightSync installs OpenRGB versions it downloads (admin-only folder, so an elevated start can trust it).</summary>
    public static string ManagedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LightSync", "OpenRGB");

    public static string ManagedBackupDir => ManagedDir + ".previous";

    public const string VersionFile = "lightsync-version.txt";

    public static bool IsProcessRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    /// <summary>Version of an OpenRGB install: the tag LightSync recorded when installing it, else the exe's version info.</summary>
    public static string? VersionOf(string exe)
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(exe)!, VersionFile);
            if (File.Exists(file))
            {
                return File.ReadAllText(file).Trim();
            }
            var info = FileVersionInfo.GetVersionInfo(exe);
            var v = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return null;
        }
    }

    /// <summary>Stop OpenRGB (it runs as administrator, so this needs one UAC prompt).</summary>
    public static async Task<ElevatedResult> StopAsync()
    {
        if (!IsProcessRunning())
        {
            return ElevatedResult.Ok;
        }
        var exe = Find();
        var result = exe is not null && await NoPromptReadyAsync(exe) && await RunTaskAsync(StopTask)
            ? ElevatedResult.Ok
            : await RunElevatedAsync("Stop-Process -Name OpenRGB -Force -ErrorAction SilentlyContinue; exit 0");
        if (result != ElevatedResult.Ok)
        {
            return result;
        }
        for (var i = 0; i < 20 && IsProcessRunning(); i++)
        {
            await Task.Delay(250);
        }
        return IsProcessRunning() ? ElevatedResult.Failed : ElevatedResult.Ok;
    }

    /// <summary>
    /// Stop OpenRGB if it runs and start it again, with one UAC prompt (none with the no-prompt tasks).
    /// Waits for its SDK server. <paramref name="fresh"/> also deletes its settings folder
    /// (<see cref="ConfigDir"/>) in between, so it detects every device from scratch; that always
    /// takes a prompt, since only administrators can change that folder.
    /// </summary>
    public static async Task<OpenRgbStartResult> RestartAsync(TimeSpan wait, bool fresh = false)
    {
        var exe = Find();
        if (exe is null)
        {
            return OpenRgbStartResult.NotFound;
        }
        if (!fresh && await NoPromptReadyAsync(exe) && await RunTaskAsync(StopTask))
        {
            for (var i = 0; i < 20 && IsProcessRunning(); i++)
            {
                await Task.Delay(250);
            }
            await Task.Delay(500);
            if (await RunTaskAsync(StartTask))
            {
                return await WaitForServerAsync(wait);
            }
        }
        var tasks = await TasksIfWantedAsync(exe);
        var script = $"""
            Stop-Process -Name OpenRGB -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
            {(fresh ? $"Remove-Item -LiteralPath {Quote(ConfigDir)} -Recurse -Force -ErrorAction SilentlyContinue" : "")}
            {tasks}
            {StartScript(exe)}
            """;
        return await RunElevatedAsync(script) switch
        {
            ElevatedResult.Declined => OpenRgbStartResult.Declined,
            _ => await WaitForServerAsync(wait),
        };
    }

    public static async Task<OpenRgbStartResult> WaitForServerAsync(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsServerUpAsync())
            {
                return OpenRgbStartResult.Started;
            }
            await Task.Delay(500);
        }
        return OpenRgbStartResult.TimedOut;
    }

    // ---------- "start OpenRGB without a permission prompt": two scheduled tasks ----------
    // Windows' supported way to start one specific program as administrator without a UAC prompt each
    // time: tasks that run with highest privileges for this user. Setting them up (or removing them)
    // takes one prompt; after that LightSync starts and stops OpenRGB by running the tasks.

    private const string TaskFolder = @"\LightSync\";
    private const string StartTask = @"\LightSync\Start OpenRGB";
    private const string StopTask = @"\LightSync\Stop OpenRGB";

    /// <summary>
    /// Whether the next permission prompt also sets up the no-prompt tasks (the default; off when the
    /// user turned "Start OpenRGB without a prompt" off). Set from the app settings at start.
    /// </summary>
    public static bool NoPromptWanted { get; set; } = true;

    /// <summary>PowerShell for an elevated script: the no-prompt tasks for <paramref name="exe"/> when wanted (or already there), else nothing.</summary>
    public static async Task<string> TasksIfWantedAsync(string exe) =>
        CanRunWithoutPrompt(exe) && (NoPromptWanted || await NoPromptProgramAsync() is not null) ? TasksScript(exe) + "\n" : "";

    /// <summary>
    /// Whether <paramref name="exe"/> may be run as administrator without a prompt: only from a folder
    /// that only administrators can change (Program Files). A copy anywhere the user's programs can
    /// write (next to LightSync, a download folder) could be swapped for another program, which the
    /// task would then run as administrator without asking.
    /// </summary>
    public static bool CanRunWithoutPrompt(string exe)
    {
        var full = Path.GetFullPath(exe);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(d => d.Length > 0)
            .Any(d => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The program the start task runs, or null if the task doesn't exist.</summary>
    public static async Task<string?> NoPromptProgramAsync() => (await StartTaskActionAsync())?.Program;

    /// <summary>The start task's program and arguments, or null if the task doesn't exist.</summary>
    private static async Task<(string Program, string Arguments)?> StartTaskActionAsync()
    {
        var (code, xml) = await SchtasksAsync($"/Query /TN \"{StartTask}\" /XML");
        if (code != 0)
        {
            return null;
        }
        string? Tag(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(xml, $"<{name}>(.*?)</{name}>", System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        }
        return Tag("Command") is { } program ? (program.Trim('"'), Tag("Arguments") ?? "") : null;
    }

    /// <summary>True if the tasks exist, start this copy of OpenRGB with today's arguments, and its settings folder exists.</summary>
    private static async Task<bool> NoPromptReadyAsync(string exe) =>
        CanRunWithoutPrompt(exe) &&
        await StartTaskActionAsync() is { } action &&
        string.Equals(Path.GetFullPath(action.Program), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) &&
        action.Arguments == Arguments &&
        Directory.Exists(ConfigDir);

    /// <summary>Create (for the OpenRGB that <see cref="Find"/> picks) or remove the two tasks. One UAC prompt.</summary>
    public static Task<ElevatedResult> SetNoPromptAsync(bool enabled)
    {
        if (!enabled)
        {
            return RunElevatedAsync($"""
                Unregister-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName 'Start OpenRGB' -Confirm:$false -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName 'Stop OpenRGB' -Confirm:$false -ErrorAction SilentlyContinue
                exit 0
                """);
        }
        var exe = Find();
        if (exe is null || !CanRunWithoutPrompt(exe))
        {
            return Task.FromResult(ElevatedResult.Failed);
        }
        return RunElevatedAsync("$ErrorActionPreference = 'Stop'\n" + TasksScript(exe));
    }

    /// <summary>PowerShell that (re)creates both tasks for <paramref name="exe"/> and the settings folder (run elevated).</summary>
    private static string TasksScript(string exe)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        return $"""
            {MakeConfigDir}
            $p = New-ScheduledTaskPrincipal -UserId {Quote(user)} -LogonType Interactive -RunLevel Highest
            $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
            $start = New-ScheduledTaskAction -Execute {Quote(exe)} -Argument {Quote(Arguments)} -WorkingDirectory {Quote(Path.GetDirectoryName(exe)!)}
            $stop = New-ScheduledTaskAction -Execute {Quote(taskkill)} -Argument '/IM OpenRGB.exe /F'
            Register-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName 'Start OpenRGB' -Action $start -Principal $p -Settings $s -Description 'Started by LightSync: OpenRGB as administrator, without a prompt.' -Force | Out-Null
            Register-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName 'Stop OpenRGB' -Action $stop -Principal $p -Settings $s -Description 'Run by LightSync to stop OpenRGB.' -Force | Out-Null
            """;
    }

    private static async Task<bool> RunTaskAsync(string name) => (await SchtasksAsync($"/Run /TN \"{name}\"")).Code == 0;

    private static async Task<(int Code, string Output)> SchtasksAsync(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
            {
                return (-1, "");
            }
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, output);
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return (-1, "");
        }
    }

    /// <summary>PowerShell single-quoted string literal.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Run a PowerShell script as administrator (one UAC prompt) and wait for it. The script is passed
    /// inline (-EncodedCommand), never through a file another process could change.
    /// </summary>
    public static async Task<ElevatedResult> RunElevatedAsync(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            // Full path: a bare "powershell.exe" could be resolved from a folder another program controls.
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            using var p = Process.Start(new ProcessStartInfo(powershell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null)
            {
                return ElevatedResult.Failed;
            }
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? ElevatedResult.Ok : ElevatedResult.Failed;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return ElevatedResult.Declined;
        }
        catch (Exception failure) when (Errors.Expected(failure))
        {
            return ElevatedResult.Failed;
        }
    }

    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("LIGHTSYNC_OPENRGB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }
        var managed = Path.Combine(ManagedDir, "OpenRGB.exe");
        if (File.Exists(managed))
        {
            return managed;
        }
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir.FullName, "openrgb", "OpenRGB.exe"),
            Path.Combine(baseDir.FullName, "OpenRGB.exe"),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        for (var d = baseDir; d is not null; d = d.Parent)
        {
            var vendor = Path.Combine(d.FullName, "vendor", "openrgb");
            if (Directory.Exists(vendor))
            {
                var hit = Directory.EnumerateFiles(vendor, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null)
                {
                    return hit;
                }
            }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = Path.Combine(root, "OpenRGB", "OpenRGB.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
