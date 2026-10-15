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

    /// <summary>Antigravity CLI(agy)가 대화 요약·로그를 두는 폴더.</summary>
    public static string AgyHome => Path.Combine(Home, ".gemini", "antigravity-cli");

    /// <summary>이 변수가 있으면 설정·로그·WebView·추가 계정 폴더를 그 아래에 둔다 (회귀 테스트가 사용자 데이터를 건드리지 않게).</summary>
    public const string DataDirVariable = "AIUSAGEMONITOR_DATA_DIR";

    public static string AppData
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor");
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
    /// <summary>Gemini(Antigravity CLI) 사용량 조회. null 이면 agy 실행 파일을 찾았을 때만 켠다.</summary>
    public bool? GeminiEnabled { get; set; }
    /// <summary>`agy -p /usage` 를 실행하는 간격 (최소 120초).</summary>
    public int GeminiPollSeconds { get; set; } = 300;
    /// <summary>진행 중이 아닌 Antigravity 대화를 세션 목록에 남겨 두는 시간.</summary>
    public int GeminiActiveHours { get; set; } = 12;
    public string? AgyExePath { get; set; }
    /// <summary>agy가 구독 등급을 제공하지 않을 때 사용자가 선택한 표시값.</summary>
    public string? GeminiPlanOverride { get; set; }
    /// <summary>/usage 를 명령으로 처리하지 않은 agy 실행 파일의 지문 (경로|크기|수정 시각). 같은 파일로는 조회하지 않는다.</summary>
    public string? AgyBlockedFingerprint { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    /// <summary>사용자가 끌어서 정한 창 크기 (DIP). null 이면 기본 크기.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool TrayHintShown { get; set; }
    /// <summary>캐릭터 표시. 기본은 끔 (트레이 메뉴 "캐릭터 표시"로 켠다).</summary>
    public bool ShowCharacters { get; set; }
    public bool ShowSessions { get; set; } = true;
    public Dictionary<string, int> AlertState { get; set; } = new();
    public List<SecondaryAccountSettings> SecondaryAccounts { get; set; } = [];

    /// <summary>
    /// 설정 파일 형식 버전. 키가 없는 예전 파일은 0 으로 읽혀 <see cref="Migrate"/> 가 한 번 적용된다.
    /// 2: 캐릭터 기본값을 끔으로 바꿈 (예전 파일에는 ShowCharacters=true 가 항상 저장돼 있었다).
    /// </summary>
    public int SettingsVersion { get; set; }
    public const int CurrentSettingsVersion = 2;

    /// <summary>이번 실행에서 마이그레이션을 적용했는지 (저장하지 않는다). 읽기에 실패한 경우는 true 가 되지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Migrated { get; private set; }

    public enum LoadResult { Loaded, Missing, Failed }

    /// <summary>설정 파일을 어떻게 읽었는지: 읽음 / 파일 없음 / 읽기·해석 실패.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LoadResult LoadedFrom { get; private set; } = LoadResult.Missing;

    /// <summary>읽기에 실패했을 때 원본을 옮겨 둔 백업 파일 (없으면 null).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BackupFile { get; private set; }

    /// <summary>원본을 읽지도 백업하지도 못해 이번 실행에서는 저장하지 않는지.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SaveBlocked => _saveBlocked;

    // 저장할 파일 (null = Paths.SettingsFile). 원본을 읽지도 백업하지도 못했으면 저장하지 않아 원본을 지킨다.
    string? _file;
    bool _saveBlocked;
    bool _saveSkipLogged;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly TimeSpan[] ReadRetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];
    // 회귀 테스트용: 읽기가 실패해 다시 읽기 직전에 불린다 (인자 = 실패한 시도 번호)
    static Action<int>? _readRetrying = null;

    /// <summary>이보다 큰 설정 파일은 읽지 않고 실패로 본다 (백업만 남긴다).</summary>
    public const long MaxSettingsBytes = 1024 * 1024;
    /// <summary>settings.json.bak-* 는 최근 것만 이만큼 남긴다.</summary>
    public const int MaxBackups = 5;

    public static AppSettings Load() => Load(Paths.SettingsFile);

    /// <summary>
    /// 설정을 읽는다. 파일이 없거나 비어 있으면(공백·BOM 만 있어도) 기본값(버전 2, Migrated)이고,
    /// 읽기·해석에 실패하면(잘못된 UTF-8 바이트, 1MB 초과 포함) 기본값으로 시작하되 Migrated 로 표시하지 않고,
    /// 덮어쓰기 전에 원본을 settings.json.bak-&lt;시각&gt; 으로 남긴다 (같은 내용의 백업이 있으면 다시 만들지 않는다).
    /// 다른 프로그램이 잠깐 잠근 경우를 위해 짧게 다시 읽어 본다.
    /// </summary>
    public static AppSettings Load(string file)
    {
        if (!File.Exists(file)) return Fresh(file);

        byte[]? raw = null;
        Exception? error = null;
        bool tooLarge = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (new FileInfo(file).Length > MaxSettingsBytes)
                {
                    tooLarge = true;
                    error = new InvalidDataException($"settings file is larger than {MaxSettingsBytes} bytes");
                    break;
                }
                raw = File.ReadAllBytes(file);
                break;
            }
            // 확인한 뒤 읽기 전에 파일(또는 폴더)이 사라졌으면 없는 파일과 같다
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Fresh(file);
            }
            catch (IOException ex) when (attempt < ReadRetryDelays.Length)
            {
                error = ex;
                _readRetrying?.Invoke(attempt);
                Thread.Sleep(ReadRetryDelays[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                break;
            }
        }

        if (raw != null)
        {
            try
            {
                var text = Decode(raw);
                // 빈 파일·BOM 만 있는 파일·공백뿐인 내용은 없는 파일과 같다 (경고·백업 없이 기본값)
                if (string.IsNullOrWhiteSpace(text)) return Fresh(file);
                var settings = Parse(text);
                settings._file = file;
                settings.LoadedFrom = LoadResult.Loaded;
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                // ArgumentException 에는 잘못된 바이트의 DecoderFallbackException 이 포함된다
                error = ex;
            }
        }

        Log.Write($"settings load failed: {error?.Message}");
        var fallback = new AppSettings { _file = file, LoadedFrom = LoadResult.Failed };
        // 기본값이 버전 2 형식이므로 버전만 맞추고 Migrated 로 표시하지 않는다 (시작하자마자 저장하지 않게)
        fallback.SettingsVersion = CurrentSettingsVersion;
        if (raw != null || tooLarge) fallback.BackupFile = Backup(file, raw);
        // 원본을 읽지도 백업하지도 못했으면 이번 실행에서는 저장하지 않는다 (원본을 덮어쓰지 않게)
        fallback._saveBlocked = fallback.BackupFile == null;
        return fallback;
    }

    static AppSettings Fresh(string file)
    {
        var fresh = new AppSettings { _file = file, LoadedFrom = LoadResult.Missing };
        fresh.Migrate();
        return fresh;
    }

    /// <summary>
    /// 메모장·PowerShell 로 편집하면 BOM(UTF-8, UTF-16 LE·BE)이 붙을 수 있다. BOM 으로 인코딩을 고르고 BOM 은 떼어 낸다
    /// (BOM 이 남으면 JSON 해석이 거부한다). BOM 이 없으면 UTF-8 이다.
    /// 어느 경우든 잘못된 바이트(예: ANSI·cp949 로 저장한 한글)는 U+FFFD 로 바꾸지 않고 예외를 던져 실패로 처리한다
    /// (깨진 값으로 읽고 다음 저장에서 원본을 덮어쓰지 않게).
    /// </summary>
    static string Decode(byte[] raw)
    {
        ReadOnlySpan<byte> bytes = raw;
        return bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes[3..]),
            [0xFF, 0xFE, ..] => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes[2..]),
            [0xFE, 0xFF, ..] => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes[2..]),
            _ => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes),
        };
    }

    /// <summary>
    /// 읽지 못한 원본을 settings.json.bak-yyyyMMdd-HHmmss 로 남긴다. 같은 내용의 백업이 이미 있으면 그 파일을 쓴다.
    /// raw 가 null 이면(너무 커서 읽지 않은 파일) 파일을 그대로 복사한다. 백업은 최근 <see cref="MaxBackups"/> 개만 남긴다.
    /// </summary>
    static string? Backup(string file, byte[]? raw)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var prefix = Path.GetFileName(file) + ".bak-";
            long length = raw?.LongLength ?? new FileInfo(file).Length;
            foreach (var existing in Directory.GetFiles(dir, prefix + "*"))
            {
                try
                {
                    if (new FileInfo(existing).Length != length) continue;
                    if (raw != null ? File.ReadAllBytes(existing).AsSpan().SequenceEqual(raw) : SameContent(existing, file)) return existing;
                }
                catch (IOException) { }
            }
            var target = Path.Combine(dir, prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}-{n}");
            if (raw != null) File.WriteAllBytes(target, raw);
            else File.Copy(file, target);
            Log.Write($"settings backup written: {target}");
            PruneBackups(dir, prefix, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings backup failed: {ex.Message}");
            return null;
        }
    }

    static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[81920];
        var bb = new byte[81920];
        while (true)
        {
            int na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    /// <summary>방금 만든 백업을 포함해 최근 <see cref="MaxBackups"/> 개만 남긴다 (이름의 시각 순, 오래된 것부터 지운다).</summary>
    static void PruneBackups(string dir, string prefix, string keep)
    {
        var old = Directory.GetFiles(dir, prefix + "*")
            .Where(f => !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(MaxBackups - 1);
        foreach (var f in old)
        {
            try { File.Delete(f); Log.Write($"settings backup removed: {f}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>설정 JSON 을 읽고 예전 형식이면 마이그레이션한다.</summary>
    public static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        settings.Migrate();
        return settings;
    }

    /// <summary>
    /// 예전 버전 설정을 현재 형식으로 바꾼다. 버전 2 로 올리면서 캐릭터를 한 번 끈다.
    /// 다음 저장부터 버전 2 가 기록되므로 그 뒤에 사용자가 켠 값은 유지된다.
    /// </summary>
    public void Migrate()
    {
        if (SettingsVersion >= CurrentSettingsVersion) return;
        if (SettingsVersion < 2) ShowCharacters = false;
        SettingsVersion = CurrentSettingsVersion;
        Migrated = true;
    }

    public void Save()
    {
        if (_saveBlocked)
        {
            // 창 이동·토글마다 저장을 부르므로 로그는 처음 한 번만 남긴다
            if (!_saveSkipLogged) Log.Write("settings save skipped: the original file could not be read or backed up");
            _saveSkipLogged = true;
            return;
        }
        try
        {
            var file = _file ?? Paths.SettingsFile;
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, file, true);
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

    // 이 앱이 조회용으로 잠깐 띄운 프로세스 (codex app-server, agy /usage). 실행 판정에서 뺀다.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> OwnPids = new();

    public static void RegisterOwnProcess(int pid) => OwnPids[pid] = 0;
    public static void UnregisterOwnProcess(int pid) => OwnPids.TryRemove(pid, out _);
    public static bool IsOwnProcess(int pid) => OwnPids.ContainsKey(pid);

    /// <summary>이름이 같은 프로세스의 PID와 실행 파일 경로. 이 앱이 띄운 조회용 프로세스는 뺀다.</summary>
    public static List<(int Pid, string Path)> ProcessesByName(string name)
    {
        var result = new List<(int, string)>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (!IsOwnProcess(p.Id) && GetImagePath(p.Id) is { } path) result.Add((p.Id, path));
            }
        }
        return result;
    }

    public static List<string> ImagePathsByName(string name) => ProcessesByName(name).Select(x => x.Path).ToList();
}

public enum SessionState { Working, NeedsInput, Idle, Stale }

public sealed record UsageWindow(string Key, string Label, double UsedPercent, DateTimeOffset? ResetsAt);

public sealed class UsageSnapshot
{
    public List<UsageWindow> Windows { get; init; } = [];
    public DateTimeOffset AsOf { get; init; }
    public string Source { get; init; } = "";
    public string? Plan { get; init; }
    /// <summary>서버가 Pro 계정의 기본 한도를 반환했고 5시간 창이 명시적으로 없을 때만 true.</summary>
    public bool FiveHourUnlimited { get; init; }
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
