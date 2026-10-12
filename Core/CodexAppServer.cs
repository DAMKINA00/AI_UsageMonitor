using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class CodexRemoteResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Email { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// `codex app-server` (Codex 공식 JSON-RPC 연동 프로토콜)를 잠깐 띄워 계정과 rate limit 을 조회한다.
/// 인증은 Codex 자체가 처리하므로 이 앱은 토큰을 다루지 않는다.
/// </summary>
public static class CodexAppServer
{
    public static string? FindExecutable(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        // Store판 Codex 앱이 쓰는 CLI (%LOCALAPPDATA%\OpenAI\Codex\bin\<버전>\codex.exe) — 앱과 버전이 맞는다
        var storeBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(storeBin))
        {
            var newest = new DirectoryInfo(storeBin).EnumerateDirectories()
                .Select(d => Path.Combine(d.FullName, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest != null) return newest;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }

        var releases = Path.Combine(Paths.CodexHome, "packages", "standalone", "releases");
        if (Directory.Exists(releases))
        {
            var best = new DirectoryInfo(releases).EnumerateDirectories()
                .Select(d => Path.Combine(d.FullName, "bin", "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (best != null) return best;
        }

        // 실행 중인 Codex 데스크톱 앱에 포함된 CLI
        return ProcessUtil.ImagePathsByName("codex")
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p) ?? "").Equals("resources", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<CodexRemoteResult> QueryAsync(string? exeOverride, CancellationToken ct = default)
    {
        var exe = FindExecutable(exeOverride);
        if (exe == null) return new CodexRemoteResult { Error = "codex.exe를 찾지 못함" };

        var psi = new ProcessStartInfo(exe, "app-server")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Process? p = null;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("프로세스 시작 실패");
            _ = p.StandardError.ReadToEndAsync(timeout.Token);

            async Task Send(object msg)
            {
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg));
                await p.StandardInput.FlushAsync();
            }

            var responses = new Dictionary<int, JsonElement>();

            async Task WaitFor(params int[] ids)
            {
                while (!ids.All(responses.ContainsKey))
                {
                    var line = await p.StandardOutput.ReadLineAsync(timeout.Token)
                               ?? throw new IOException("codex app-server가 응답 없이 종료됨");
                    if (line.Length == 0 || line[0] != '{') continue;
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        responses[idEl.GetInt32()] = doc.RootElement.Clone();
                }
            }

            await Send(new { method = "initialize", id = 0, @params = new { clientInfo = new { name = "ai_usage_monitor", title = "AI Usage Monitor", version = "1.0.0" } } });
            await WaitFor(0);
            await Send(new { method = "initialized" });
            await Send(new { method = "account/read", id = 1, @params = new { } });
            await Send(new { method = "account/rateLimits/read", id = 2 });
            await WaitFor(1, 2);

            if (responses[2].Obj("error") is { } err)
                return new CodexRemoteResult { Error = err.Str("message") ?? "rate limit 조회 실패" };

            var account = responses[1].Obj("result")?.Obj("account");
            var result = responses[2].Obj("result");
            return new CodexRemoteResult
            {
                Ok = result != null,
                Email = account?.Str("email"),
                Usage = result is { } r ? ParseRateLimits(r, account?.Str("planType")) : null,
                Error = result == null ? "rate limit 응답 없음" : null,
            };
        }
        catch (OperationCanceledException)
        {
            return new CodexRemoteResult { Error = "codex app-server 응답 시간 초과" };
        }
        catch (Exception ex)
        {
            Log.Write($"codex app-server failed: {ex.Message}");
            return new CodexRemoteResult { Error = ex.Message };
        }
        finally
        {
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
    }

    static UsageSnapshot ParseRateLimits(JsonElement result, string? accountPlan)
    {
        var windows = new List<UsageWindow>();
        var extras = new List<string>();
        string? warning = null;
        string? plan = CodexLabels.Plan(accountPlan);

        // rateLimitsByLimitId 에 여러 한도가 올 수 있다. 기본 한도("codex")는 접두어 없이 표시.
        var limits = new List<JsonElement>();
        if (result.Obj("rateLimitsByLimitId") is { } byId)
            limits.AddRange(byId.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => x.Value));
        else if (result.Obj("rateLimits") is { } single)
            limits.Add(single);

        foreach (var l in limits)
        {
            var id = l.Str("limitId") ?? "codex";
            var prefix = id == "codex" ? "" : (l.Str("limitName") ?? id) + " · ";
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (l.Obj(key) is not { } w || w.Num("usedPercent") is not double pct) continue;
                var mins = (int)(w.Num("windowDurationMins") ?? 0);
                windows.Add(new UsageWindow($"{id}:w{mins}", prefix + CodexLabels.Window(mins), pct, JsonUtil.EpochSeconds(w.Num("resetsAt"))));
            }
            if (id == "codex" && l.Obj("credits") is { } cr) CodexLabels.AddCredits(extras, cr.Bool("unlimited"), cr.Str("balance"));
            if (l.Str("rateLimitReachedType") is { Length: > 0 }) warning = "한도 도달";
            if (l.Bool("spendControlReached") == true) warning = "지출 한도 도달";
            plan ??= CodexLabels.Plan(l.Str("planType"));
        }

        if (result.Obj("rateLimitResetCredits") is { } rc && rc.Num("availableCount") is double cnt && cnt > 0)
        {
            DateTimeOffset? soonest = null;
            if (rc.TryGetProperty("credits", out var arr) && arr.ValueKind == JsonValueKind.Array)
                soonest = arr.EnumerateArray()
                    .Where(c => c.Str("status") == "available")
                    .Select(c => JsonUtil.EpochSeconds(c.Num("expiresAt")))
                    .Where(d => d != null)
                    .Min();
            extras.Add(soonest is { } s
                ? $"무료 리셋권 {cnt:0}개 (가장 빠른 만료 {s.ToLocalTime():M/d})"
                : $"무료 리셋권 {cnt:0}개");
        }

        if (result.Bool("ordinaryUsageAllowed") == false) warning ??= "사용 불가 상태";

        return new UsageSnapshot
        {
            Windows = windows
                .OrderBy(w => w.Key.StartsWith("codex:") ? 0 : 1)
                .ThenBy(w => int.TryParse(w.Key[(w.Key.LastIndexOf(":w", StringComparison.Ordinal) + 2)..], out var m) ? m : 0)
                .ToList(),
            AsOf = DateTimeOffset.Now,
            Source = "Codex 서버",
            Plan = plan,
            Extras = extras,
            Warning = warning,
        };
    }
}
