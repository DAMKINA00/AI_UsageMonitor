using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class CodexRemoteResult
{
    public bool Ok { get; init; }
    public bool LoginRequired { get; init; }
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

    public static async Task<CodexRemoteResult> QueryAsync(string? exeOverride, CancellationToken ct = default, string? codexHome = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var connection = new Connection(exeOverride, codexHome);
            await connection.InitializeAsync(timeout.Token);
            var account = (await connection.RequestAsync("account/read", new { }, timeout.Token)).Obj("account");
            if (account == null)
                return new CodexRemoteResult { LoginRequired = true, Error = "브라우저 로그인이 필요합니다." };
            var result = await connection.RequestAsync("account/rateLimits/read", new { }, timeout.Token);
            return new CodexRemoteResult
            {
                Ok = true,
                Email = account?.Str("email"),
                Usage = ParseRateLimits(result, account?.Str("planType")),
            };
        }
        catch (OperationCanceledException)
        {
            return new CodexRemoteResult { Error = ct.IsCancellationRequested ? "조회 취소됨" : "codex app-server 응답 시간 초과" };
        }
        catch (Exception ex)
        {
            return new CodexRemoteResult { Error = ex.Message };
        }
    }

    public static async Task LoginAsync(string? exeOverride, string codexHome, Action<string> openBrowser, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var connection = new Connection(exeOverride, codexHome);
        string? loginId = null;
        bool loggedIn = false;
        try
        {
            await connection.InitializeAsync(timeout.Token);
            var result = await connection.RequestAsync("account/login/start", new { type = "chatgpt" }, timeout.Token);
            loginId = result.Str("loginId") ?? throw new IOException("로그인 ID가 없습니다.");
            var url = result.Str("authUrl");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host is not ("auth.openai.com" or "auth0.openai.com" or "chatgpt.com" or "auth.chatgpt.com"))
                throw new IOException("로그인 주소를 확인할 수 없습니다.");
            openBrowser(uri.AbsoluteUri);
            while (true)
            {
                var completed = await connection.WaitForLoginAsync(timeout.Token);
                if (completed.Str("loginId") != loginId) continue;
                if (completed.Bool("success") != true) throw new IOException("로그인이 완료되지 않았습니다. 다시 시도해 주세요.");
                loggedIn = true;
                return;
            }
        }
        finally
        {
            if (loginId != null && !loggedIn)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await connection.RequestAsync("account/login/cancel", new { loginId }, cancel.Token); } catch { }
            }
        }
    }

    public static async Task LogoutAsync(string? exeOverride, string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = new Connection(exeOverride, codexHome);
        await connection.InitializeAsync(timeout.Token);
        await connection.RequestAsync("account/logout", new { }, timeout.Token);
    }

    internal static ProcessStartInfo StartInfo(string exe, string? codexHome)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("app-server");
        // Account queries do not need plugin refreshes, which spawn Git HTTPS helpers at startup.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("features.plugins=false");
        if (codexHome != null)
        {
            psi.WorkingDirectory = codexHome;
            psi.Environment["CODEX_HOME"] = codexHome;
            foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL" })
                psi.Environment.Remove(key);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        }
        return psi;
    }

    sealed class Connection : IDisposable
    {
        readonly Process _process;
        readonly Queue<JsonElement> _logins = new();
        int _id;

        public Connection(string? exeOverride, string? codexHome)
        {
            var exe = FindExecutable(exeOverride) ?? throw new IOException("codex.exe를 찾지 못함");
            _process = Process.Start(StartInfo(exe, codexHome)) ?? throw new IOException("프로세스 시작 실패");
            // stderr may contain authentication diagnostics; drain it without persisting it.
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            await RequestAsync("initialize", new { clientInfo = new { name = "ai_usage_monitor", title = "AI Usage Monitor", version = "1.0.0" } }, ct);
            await SendAsync(new { method = "initialized" }, ct);
        }

        async Task SendAsync(object message, CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            int id = ++_id;
            await SendAsync(new { method, id, @params = parameters }, ct);
            while (true)
            {
                var message = await ReadAsync(ct);
                if (message.Num("id") != id) continue;
                if (message.Obj("error") is { } error)
                    throw new IOException(error.Str("message") ?? "Codex 요청 실패");
                return message.Obj("result") ?? throw new IOException("Codex 응답 형식 오류");
            }
        }

        async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("codex app-server가 응답 없이 종료됨");
                if (line.Length == 0 || line[0] != '{') continue;
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement.Clone();
                if (message.Str("method") == "account/login/completed" && message.Obj("params") is { } login)
                    _logins.Enqueue(login);
                return message;
            }
        }

        public async Task<JsonElement> WaitForLoginAsync(CancellationToken ct)
        {
            while (_logins.Count == 0) await ReadAsync(ct);
            return _logins.Dequeue();
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(3000); } } catch { }
            _process.Dispose();
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
