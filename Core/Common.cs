using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core;

public static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");

    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".codex");

    public static string AppData
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string LogFile => Path.Combine(AppData, "monitor.log");
    public static string WebViewData => Path.Combine(AppData, "WebView2");

    /// <summary>helpers\claude_statusline.py 가 쓰는 파일.</summary>
    public static string ClaudeStatusLineFile => Path.Combine(AppData, "claude", "rate_limits.json");
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로그 실패는 무시
        }
    }
}

public sealed class AppSettings
{
    public bool Topmost { get; set; }
    public bool StartWithWindows { get; set; }
    public bool UsageAlerts { get; set; } = true;
    public bool ActivityAlerts { get; set; }
    public int[] AlertThresholds { get; set; } = [80, 95];
    public int CodexPollSeconds { get; set; } = 60;
    public int ClaudeWebPollSeconds { get; set; } = 60;
    public int CodexActiveHours { get; set; } = 12;
    /// <summary>Claude 앱이 켜져 있거나 진행 중인 대화가 있을 때 claude.ai 대화 목록을 확인하는 간격.</summary>
    public int ClaudeChatPollSeconds { get; set; } = 15;
    /// <summary>진행 중이 아닌 claude.ai 대화를 카드에 남겨 두는 시간.</summary>
    public int ClaudeChatActiveHours { get; set; } = 12;
    public bool ClaudeWebEnabled { get; set; }
    public bool ClaudeDesktopEnabled { get; set; } = true;
    public string? CodexExePath { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool TrayHintShown { get; set; }
    public bool ShowCharacters { get; set; } = true;
    public Dictionary<string, int> AlertState { get; set; } = new();
    public List<SecondaryAccountSettings> SecondaryAccounts { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write($"settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}

public static class ProcessUtil
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public static string? GetImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// pid가 살아 있는지 확인한다. expectedStartFileTime이 있으면 PID 재사용을 막기 위해 시작 시각도 비교한다.
    /// </summary>
    public static bool IsAlive(int pid, long? expectedStartFileTime)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetExitCodeProcess(h, out var code) || code != StillActive) return false;
            if (expectedStartFileTime is long expected && GetProcessTimes(h, out var creation, out _, out _, out _))
                return Math.Abs(creation - expected) < 20_000_000; // 2초 허용
            return true;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static List<string> ImagePathsByName(string name)
    {
        var result = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (GetImagePath(p.Id) is { } path) result.Add(path);
            }
        }
        return result;
    }
}

public enum SessionState { Working, NeedsInput, Idle, Stale }

public sealed record UsageWindow(string Key, string Label, double UsedPercent, DateTimeOffset? ResetsAt);

public sealed class UsageSnapshot
{
    public List<UsageWindow> Windows { get; init; } = [];
    public DateTimeOffset AsOf { get; init; }
    public string Source { get; init; } = "";
    public string? Plan { get; init; }
    public List<string> Extras { get; init; } = [];
    public string? Warning { get; init; }
}

public sealed record SessionInfo(
    string Id,
    string Title,
    string Subtitle,
    SessionState State,
    string StateText,
    DateTimeOffset? LastActivity,
    bool ActivityIsStateChange = false,
    string? OpenUri = null,
    /// <summary>같은 세션을 가리키는 다른 ID (claude.ai 대화의 workspace_session_id 등). 중복 제거용.</summary>
    string? AltId = null);

public static class JsonUtil
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Num(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static DateTimeOffset? EpochSeconds(double? s) =>
        s is double v && v > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(v * 1000)) : null;

    public static DateTimeOffset? Iso(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
