using System.IO;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class CodexLocalState
{
    public bool DesktopRunning { get; init; }
    public int CliProcessCount { get; init; }
    public List<SessionInfo> Threads { get; init; } = [];
    /// <summary>가장 최근 token_count 이벤트에 기록된 rate_limits.</summary>
    public UsageSnapshot? LogUsage { get; init; }
}

/// <summary>
/// ~/.codex/sessions/**/rollout-*.jsonl 의 끝부분을 읽어 스레드별 작업 상태와 최신 rate_limits 를 얻는다.
/// </summary>
public sealed class CodexLocalReader
{
    const int TailBytes = 512 * 1024;
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    sealed class FileInfoCache
    {
        public long Length;
        public DateTime Mtime;
        public string? Cwd;
        public bool HeadRead;
        public long Position;
        public string? LastTaskEvent;
        /// <summary>마지막 실제 활동 시각 (앱이 덧붙이는 thread_settings_applied 는 제외).</summary>
        public DateTimeOffset? LastActivityAt;
        public UsageSnapshot? RateLimits;
    }

    readonly Dictionary<string, FileInfoCache> _cache = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _names = new();
    DateTime _namesMtime;

    public CodexLocalState Read(int activeHours)
    {
        var cliPaths = ProcessUtil.ImagePathsByName("codex");
        // Store판 Codex 앱은 메인 창이 ChatGPT.exe (WindowsApps\OpenAI.Codex_…), 직접 설치판은 Codex.exe
        bool desktop = cliPaths.Concat(ProcessUtil.ImagePathsByName("ChatGPT")).Any(IsDesktopApp);
        int cli = cliPaths.Count(p => p.Contains(@"\packages\standalone\", StringComparison.OrdinalIgnoreCase)
                                      || p.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase));
        bool anyCodexProcess = desktop || cliPaths.Count > 0;

        var dir = Path.Combine(Paths.CodexHome, "sessions");
        if (!Directory.Exists(dir))
            return new CodexLocalState { DesktopRunning = desktop, CliProcessCount = cli };

        var files = new DirectoryInfo(dir)
            .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var cutoff = DateTime.UtcNow.AddHours(-activeHours);
        var recent = files.Where(f => f.LastWriteTimeUtc >= cutoff).ToList();
        if (recent.Count == 0 && files.Count > 0) recent.Add(files[0]); // rate_limits 용으로 최신 파일 하나는 읽는다

        LoadNames();
        var threads = new List<SessionInfo>();
        UsageSnapshot? newest = null;

        foreach (var f in recent)
        {
            var c = Refresh(f);
            if (c.RateLimits is { } rl && (newest == null || rl.AsOf > newest.AsOf)) newest = rl;
            // 파일 갱신 시각이 아니라 실제 활동 시각으로 판단한다 (앱을 켜면 모든 스레드에 설정 이벤트가 덧붙는다)
            if (c.LastActivityAt is not { } last || last.UtcDateTime < cutoff) continue;

            var id = ThreadIdFromName(f.Name);
            var folder = string.IsNullOrEmpty(c.Cwd) ? "" : Path.GetFileName(c.Cwd.TrimEnd('\\', '/'));
            var title = id != null && _names.TryGetValue(id, out var n) && n.Length > 0 ? n : (folder.Length > 0 ? folder : "Codex 스레드");
            var idle = DateTimeOffset.UtcNow - last;

            var (state, text) = c.LastTaskEvent switch
            {
                "task_started" when !anyCodexProcess => (SessionState.Stale, "중단됨"),
                "task_started" when idle > StaleAfter => (SessionState.Stale, "응답 없음"),
                "task_started" => (SessionState.Working, "작업 중"),
                "turn_aborted" => (SessionState.Idle, "중단됨"),
                "task_complete" => (SessionState.Idle, "완료"),
                // 한 턴이 tail 범위보다 길면 이벤트를 못 찾는다 — 실제 활동 시각으로 추정
                _ when anyCodexProcess && idle < TimeSpan.FromSeconds(90) => (SessionState.Working, "작업 중"),
                _ => (SessionState.Idle, "대기"),
            };

            var open = id != null ? $"codex://threads/{id}" : null;
            threads.Add(new SessionInfo(id ?? f.FullName, title, folder, state, text, last, OpenUri: open));
        }

        // 사라진 파일 캐시 정리
        if (_cache.Count > 200)
        {
            var keep = recent.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var k in _cache.Keys.Where(k => !keep.Contains(k)).ToList()) _cache.Remove(k);
        }

        return new CodexLocalState
        {
            DesktopRunning = desktop,
            CliProcessCount = cli,
            Threads = threads,
            LogUsage = newest,
        };
    }

    static bool IsDesktopApp(string path)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        bool codexPackage = path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)
                            || path.Contains(@"\CODEX-Desktop\", StringComparison.OrdinalIgnoreCase);
        // 메인 창 실행 파일만 센다 (resources\codex.exe, bin\…\codex.exe 같은 내부 서버는 제외)
        return codexPackage
               && (name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) && !parent.Equals("resources", StringComparison.OrdinalIgnoreCase));
    }

    static string? ThreadIdFromName(string fileName)
    {
        // rollout-2026-10-11T15-03-21-<uuid>.jsonl
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length >= 36 ? stem[^36..] : null;
    }

    void LoadNames()
    {
        var file = Path.Combine(Paths.CodexHome, "session_index.jsonl");
        try
        {
            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime == _namesMtime) return;
            var names = new Dictionary<string, string>();
            foreach (var line in ClaudeLocalReader.ReadShared(file).Split('\n'))
            {
                if (line.Length < 2) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.Str("id") is { } id && doc.RootElement.Str("thread_name") is { } name)
                        names[id] = name;
                }
                catch (JsonException) { }
            }
            _names = names;
            _namesMtime = mtime;
        }
        catch (IOException) { }
    }

    FileInfoCache Refresh(FileInfo f)
    {
        if (!_cache.TryGetValue(f.FullName, out var c))
            _cache[f.FullName] = c = new FileInfoCache();
        if (c.Length == f.Length && c.Mtime == f.LastWriteTimeUtc) return c;

        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length < c.Length) // 파일이 새로 쓰였으면 이전 파일의 상태를 버린다.
                _cache[f.FullName] = c = new FileInfoCache();

            if (!c.HeadRead)
            {
                using (var sr = new StreamReader(fs, Encoding.UTF8, false, 64 * 1024, leaveOpen: true))
                {
                    var first = sr.ReadLine();
                    if (first != null) c.Cwd = ParseCwd(first);
                }
                c.HeadRead = true;
            }

            // 로그는 append-only 이므로 지난번 이후 추가된 부분만 읽는다 (처음에는 끝 TailBytes 만)
            long start = c.Position;
            bool partialFirst = false;
            if (length - start > TailBytes)
            {
                start = length - TailBytes;
                partialFirst = start > 0;
            }

            var buf = new byte[length - start];
            fs.Seek(start, SeekOrigin.Begin);
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n == 0) break;
                read += n;
            }

            // 마지막 줄바꿈까지만 처리하고, 쓰이는 중인 마지막 줄은 다음에 읽는다
            int lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (lastNl >= 0)
            {
                var lines = Encoding.UTF8.GetString(buf, 0, lastNl).Split('\n');
                int from = partialFirst ? 1 : 0;

                string? taskEvent = null;
                UsageSnapshot? rl = null;
                DateTimeOffset? activity = null;
                for (int i = lines.Length - 1; i >= from && (taskEvent == null || rl == null || activity == null); i--)
                {
                    var line = lines[i];
                    if (line.Length < 10) continue;
                    if (activity == null && !line.Contains("\"thread_settings_applied\"")) activity = LineTimestamp(line);
                    if (!line.Contains("\"event_msg\"")) continue;
                    if (taskEvent == null && (line.Contains("\"task_started\"") || line.Contains("\"task_complete\"") || line.Contains("\"turn_aborted\"")))
                    {
                        var t = EventType(line);
                        if (t is "task_started" or "task_complete" or "turn_aborted") taskEvent = t;
                    }
                    if (rl == null && line.Contains("\"token_count\"") && line.Contains("\"rate_limits\""))
                        rl = ParseRateLimits(line);
                }

                if (taskEvent != null) c.LastTaskEvent = taskEvent;
                if (rl != null) c.RateLimits = rl;
                if (activity != null) c.LastActivityAt = activity;
                c.Position = start + lastNl + 1;
            }
            else if (partialFirst)
            {
                c.Position = length; // 거대한 한 줄 — 건너뛴다
            }
            c.Length = f.Length;
            c.Mtime = f.LastWriteTimeUtc;
        }
        catch (IOException) { }
        return c;
    }

    /// <summary>줄 맨 앞의 "timestamp" 만 읽는다 (큰 줄을 통째로 파싱하지 않으려고).</summary>
    static DateTimeOffset? LineTimestamp(string line)
    {
        const string key = "\"timestamp\":\"";
        int i = line.IndexOf(key, 0, Math.Min(line.Length, 120), StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + key.Length, end = line.IndexOf('"', start);
        return end > start ? JsonUtil.Iso(line[start..end]) : null;
    }

    static string? ParseCwd(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Obj("payload")?.Str("cwd");
        }
        catch (JsonException) { return null; }
    }

    static string? EventType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Str("type") == "event_msg" ? doc.RootElement.Obj("payload")?.Str("type") : null;
        }
        catch (JsonException) { return null; }
    }

    static UsageSnapshot? ParseRateLimits(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var payload = root.Obj("payload");
            if (payload?.Str("type") != "token_count" || payload.Value.Obj("rate_limits") is not { } rl) return null;

            var windows = new List<UsageWindow>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (rl.Obj(key) is not { } w || w.Num("used_percent") is not double pct) continue;
                var mins = (int)(w.Num("window_minutes") ?? 0);
                windows.Add(new UsageWindow($"codex:w{mins}", CodexLabels.Window(mins), pct, JsonUtil.EpochSeconds(w.Num("resets_at"))));
            }
            if (windows.Count == 0) return null;

            var extras = new List<string>();
            if (rl.Obj("credits") is { } cr) CodexLabels.AddCredits(extras, cr.Bool("unlimited"), cr.Str("balance"));

            return new UsageSnapshot
            {
                Windows = windows,
                AsOf = JsonUtil.Iso(root.Str("timestamp")) ?? DateTimeOffset.Now,
                Source = "Codex 로그",
                Plan = CodexLabels.Plan(rl.Str("plan_type")),
                Extras = extras,
                Warning = rl.Str("rate_limit_reached_type") is { Length: > 0 } ? "한도 도달" : null,
            };
        }
        catch (JsonException) { return null; }
    }
}

public static class CodexLabels
{
    public static string Window(int minutes) => minutes switch
    {
        300 => "5시간 한도",
        1440 => "일간 한도",
        10080 => "주간 한도",
        <= 0 => "한도",
        < 1440 => $"{minutes / 60.0:0.#}시간 한도",
        _ => $"{minutes / 1440.0:0.#}일 한도",
    };

    public static string? Plan(string? planType) => planType switch
    {
        null or "" => null,
        // Codex 앱 기준 등급: prolite = Pro 100 (5x), pro = Pro 200 (20x), promax = Pro 500
        "prolite" => "Pro 5x",
        "pro" => "Pro 20x",
        "promax" => "Pro Max",
        "plus" => "Plus",
        "go" => "Go",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "free" => "Free",
        _ => planType,
    };

    public static void AddCredits(List<string> extras, bool? unlimited, string? balance)
    {
        if (unlimited == true) extras.Add("크레딧 무제한");
        else if (double.TryParse(balance, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b))
            extras.Add($"크레딧 잔액 {b:N0}");
    }
}
