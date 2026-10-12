using System.IO;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class ClaudeLocalState
{
    public bool DesktopRunning { get; init; }
    public int CliProcessCount { get; init; }
    public List<SessionInfo> Sessions { get; init; } = [];
    /// <summary>실행 중인 세션의 sessionId·hostSessionId·bridgeSessionId. claude.ai 대화 목록과 겹치는 항목을 빼는 데 쓴다.</summary>
    public HashSet<string> KnownIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>터미널 CLI의 statusLine 헬퍼가 남긴 공식 rate_limits (있을 때만).</summary>
    public UsageSnapshot? StatusLineUsage { get; init; }
}

/// <summary>
/// ~/.claude/sessions/&lt;pid&gt;.json 에서 실행 중인 Claude Code 세션 상태를 읽는다.
/// </summary>
public sealed class ClaudeLocalReader
{
    public ClaudeLocalState Read()
    {
        var paths = ProcessUtil.ImagePathsByName("claude");
        bool IsCode(string p) => p.Contains(@"\claude-code\", StringComparison.OrdinalIgnoreCase);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new ClaudeLocalState
        {
            DesktopRunning = paths.Any(p => !IsCode(p)),
            CliProcessCount = paths.Count(IsCode),
            Sessions = ReadSessions(known),
            KnownIds = known,
            StatusLineUsage = ReadStatusLine(),
        };
    }

    static List<SessionInfo> ReadSessions(HashSet<string> known)
    {
        var list = new List<SessionInfo>();
        var dir = Path.Combine(Paths.ClaudeHome, "sessions");
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(file));
                var r = doc.RootElement;
                if (r.Num("pid") is not double pidD) continue;
                long? procStart = long.TryParse(r.Str("procStart"), out var ps) ? ps : null;
                if (!ProcessUtil.IsAlive((int)pidD, procStart)) continue;
                foreach (var key in new[] { "sessionId", "hostSessionId", "bridgeSessionId" })
                    if (r.Str(key) is { Length: > 0 } id) known.Add(id);

                var cwd = r.Str("cwd") ?? "";
                var folder = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var title = r.Str("name") is { Length: > 0 } n ? n : (folder.Length > 0 ? folder : "Claude Code");
                var status = r.Str("status") ?? "";
                var waitingFor = r.Str("waitingFor");
                var updated = JsonUtil.EpochSeconds((r.Num("statusUpdatedAt") ?? r.Num("updatedAt")) / 1000.0);

                var (state, text) = status switch
                {
                    "busy" => (SessionState.Working, "작업 중"),
                    "waiting" when waitingFor?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true
                        => (SessionState.NeedsInput, "승인 대기"),
                    "waiting" => (SessionState.NeedsInput, string.IsNullOrEmpty(waitingFor) ? "입력 대기" : $"대기: {waitingFor}"),
                    "idle" => (SessionState.Idle, "대기"),
                    "" => (SessionState.Idle, "알 수 없음"),
                    _ => (SessionState.Idle, status),
                };

                var entry = r.Str("entrypoint") ?? "";
                var where = entry switch
                {
                    _ when entry.Contains("desktop") => "데스크톱",
                    _ when entry.Contains("vscode") => "VS Code",
                    _ when entry.Contains("jetbrains") => "JetBrains",
                    "cli" => "터미널",
                    "" => "",
                    _ => entry,
                };

                // 휴대폰 등에서 원격 제어로 시작한 세션은 앱 사이드바에 "Remote Control session"으로 보인다
                if (r.Str("bridgeSessionId") is { Length: > 0 }) where = where.Length > 0 ? where + " (원격 제어)" : "원격 제어";
                var subtitle = string.Join(" · ", new[] { folder, where }.Where(s => s.Length > 0));
                // 데스크톱 앱 세션은 claude:// 링크로 바로 열 수 있다
                var open = r.Str("hostSessionId") is { Length: > 0 } host ? $"claude://claude.ai/epitaxy/{Uri.EscapeDataString(host)}" : null;
                list.Add(new SessionInfo(r.Str("sessionId") ?? file, title, subtitle, state, text, updated, ActivityIsStateChange: true, OpenUri: open));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 세션 파일이 쓰이는 도중일 수 있음 — 다음 주기에 다시 읽는다
            }
        }
        return list;
    }

    static UsageSnapshot? ReadStatusLine()
    {
        var file = Paths.ClaudeStatusLineFile;
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file));
            var r = doc.RootElement;
            if (r.Obj("rate_limits") is not { } rl) return null;
            var windows = new List<UsageWindow>();
            foreach (var (key, label) in new[] { ("five_hour", "5시간 한도"), ("seven_day", "주간 한도") })
            {
                if (rl.Obj(key) is { } w && w.Num("used_percentage") is double pct)
                    windows.Add(new UsageWindow(key, label, pct, JsonUtil.EpochSeconds(w.Num("resets_at"))));
            }
            if (windows.Count == 0) return null;
            return new UsageSnapshot
            {
                Windows = windows,
                AsOf = JsonUtil.EpochSeconds(r.Num("captured_at")) ?? DateTimeOffset.Now,
                Source = "Claude Code 상태줄",
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    internal static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
