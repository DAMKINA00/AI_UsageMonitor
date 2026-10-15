using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core;

public sealed class GeminiRemoteResult
{
    public bool Ok { get; init; }
    public bool LoginRequired { get; init; }
    /// <summary>
    /// agy 가 /usage 를 명령으로 처리하지 않고 프롬프트로 실행한 것으로 보이거나 출력 형식을 모르는 버전.
    /// 이때 agy 의 응답 본문은 Error·로그에 담지 않는다.
    /// </summary>
    public bool Unsupported { get; init; }
    /// <summary>
    /// 로그인 필요를 JSON 이 아닌 짧은 평문 안내로만 알았다. 평문이 실제로는 모델 응답일 수도 있으므로
    /// 자동으로 다시 실행하지 않고 수동 새로고침을 기다린다.
    /// </summary>
    public bool PlainTextLogin { get; init; }
    public string? Error { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Antigravity CLI 의 `agy -p /usage --output-format json` 을 잠깐 실행해 Gemini·Claude/GPT 쿼터를 읽는다.
/// 이 명령은 모델 턴과 쿼터를 쓰지 않는다. 인증은 agy 가 자기 keyring 로그인으로 처리하므로 이 앱은 토큰을 읽지 않는다.
/// </summary>
public static class AgyCli
{
    public const string Source = "Antigravity CLI";
    public const string MissingFractionWarning = "쿼터 값 확인 불가";
    public const string ExhaustedWarning = "한도 도달";
    /// <summary>remaining_fraction 이 생략됐지만 리셋 전인 버킷 (proto3 JSON 은 0 을 생략할 수 있다).</summary>
    public const string InferredExhaustedWarning = "한도 도달(추정)";
    public const string UnsupportedMessage = "agy 버전 미지원";
    public const string FormatChangedMessage = "쿼터 형식 변경";
    public const string LoginRequiredMessage = "agy 로그인이 필요합니다.";
    const int PlainLoginMaxChars = 300, PlainLoginMaxLines = 3;
    internal const int MaxOutputChars = 1024 * 1024;
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    // agy 가 끝난 뒤 출력 파이프가 닫히기를 이만큼만 기다린다
    static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(3);

    /// <summary>overridePath 가 지정되면 그 파일만 쓴다 (없으면 null — 다른 agy 로 넘어가지 않는다).</summary>
    public static string? FindExecutable(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return File.Exists(overridePath) ? overridePath : null;

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        if (File.Exists(installed)) return installed;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(dir.Trim(), "agy.exe");
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>실행 파일의 (경로, 크기, 수정 시각). agy 가 업데이트되면 바뀐다.</summary>
    public static string? Fingerprint(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var info = new FileInfo(exe);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static ProcessStartInfo StartInfo(string exe, string logFile)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // 앱 폴더를 작업 공간으로 삼지 않도록 임시 폴더에서 실행한다
            WorkingDirectory = Path.GetTempPath(),
        };
        // --log-file 이 없으면 실행마다 ~/.gemini/antigravity-cli/log 에 로그 파일이 하나씩 쌓인다
        foreach (var arg in new[] { "-p", "/usage", "--output-format", "json", "--log-file", logFile })
            psi.ArgumentList.Add(arg);
        return psi;
    }

    public static async Task<GeminiRemoteResult> QueryAsync(string? exeOverride, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var exe = FindExecutable(exeOverride);
        if (exe == null)
            return new GeminiRemoteResult
            {
                Error = string.IsNullOrWhiteSpace(exeOverride) ? "agy 실행 파일을 찾지 못했습니다." : "지정한 agy 경로를 찾지 못했습니다.",
            };

        var logFile = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-agy-{Guid.NewGuid():N}.log");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        Process? process = null;
        KillOnCloseJob? job = null;
        int pid = 0;
        try
        {
            if (ct.IsCancellationRequested) return new GeminiRemoteResult { Error = "조회 취소됨" };
            job = KillOnCloseJob.TryCreate();
            process = Process.Start(StartInfo(exe, logFile)) ?? throw new IOException("agy 를 시작하지 못했습니다.");
            pid = process.Id;
            ProcessUtil.RegisterOwnProcess(pid);
            // agy 가 띄운 자식도 Job 에 들어가 Job 을 닫을 때 함께 끝난다.
            // 시작과 할당 사이에 agy 가 띄운 자식은 Job 밖에 남을 수 있다 (CREATE_SUSPENDED 로 시작하지 않는 한계).
            // 실측(agy 1.3.5, 2026-10-15): /usage 실행 중 자식은 콘솔 호스트(conhost.exe)뿐이고 종료 10초 뒤 남은 프로세스가 없었다.
            // 다른 agy 와 공유하는 상주 데몬이 없으므로 Job 을 닫아 모두 끝내도 다른 agy 에 영향이 없다.
            if (job != null) job.TryAdd(process, "agy");
            else KillOnCloseJob.ReportUnavailable("agy");
            var running = process;
            // 시간 초과·취소·출력 초과 때 프로세스 트리를 끝내 파이프 읽기를 풀어 준다
            using var kill = limit.Token.Register(() => Kill(running));
            process.StandardInput.Close(); // 대화형 입력을 기다리지 않게 한다

            bool overflow = false;
            var stdout = ReadBoundedAsync(process.StandardOutput, MaxOutputChars, limit.Token, () => { overflow = true; limit.Cancel(); });
            var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, limit.Token, () => { });

            bool exited = false;
            try
            {
                await process.WaitForExitAsync(limit.Token);
                exited = true;
                limit.CancelAfter(PipeGrace);
                // 남은 자식이 출력 파이프를 붙잡고 있으면 끝내서 읽기를 마친다
                job?.Dispose();
            }
            catch (OperationCanceledException) { }

            var output = await stdout;
            var error = await stderr;
            if (overflow) return new GeminiRemoteResult { Error = "agy 출력이 너무 큽니다." };
            if (!exited)
                return new GeminiRemoteResult { Error = ct.IsCancellationRequested ? "조회 취소됨" : "agy 응답 시간 초과" };
            return ParseOutput(output, error, process.ExitCode);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GeminiRemoteResult { Error = Sanitize(ex.Message) };
        }
        finally
        {
            if (process != null)
            {
                Kill(process);
                ProcessUtil.UnregisterOwnProcess(pid);
                process.Dispose();
            }
            job?.Dispose();
            try { File.Delete(logFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, int max, CancellationToken ct, Action onOverflow)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (text.Length + n > max)
                {
                    onOverflow();
                    break;
                }
                text.Append(buffer, 0, n);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 종료된 프로세스의 파이프 — 지금까지 읽은 내용만 쓴다
        }
        return text.ToString();
    }

    /// <summary>
    /// agy 출력(JSON 한 개)을 결과로 바꾼다. /usage 명령 응답이 아닌 출력(대화가 만들어졌거나 턴·토큰을 썼거나,
    /// JSON 이 아닌 텍스트)은 Unsupported 로 분류하고 그 본문은 결과에 담지 않는다.
    /// </summary>
    public static GeminiRemoteResult ParseOutput(string stdout, string stderr, int exitCode, DateTimeOffset? now = null)
    {
        if (TryParseObject(stdout) is { } root)
        {
            if (RanAsPrompt(root)) return Unsupported();
            if (root.Str("status") == "SUCCESS")
            {
                if (root.Obj("command") is not { } command || command.Str("name") != "usage" || command.Obj("data") is not { } data)
                    return Unsupported();
                var usage = ParseUsage(data, now);
                if (usage.Windows.Count == 0) return new GeminiRemoteResult { Error = FormatChangedMessage };
                return new GeminiRemoteResult { Ok = true, Usage = usage };
            }
            var message = Sanitize(ErrorText(root) ?? AgyError(stderr) ?? $"agy /usage 실패 (status {root.Str("status") ?? "?"})");
            return new GeminiRemoteResult { LoginRequired = IsLoginFailure(root, message), Error = message };
        }

        // JSON 이 아닌 텍스트로 성공했다면 로그인 안내이거나, /usage 가 프롬프트로 처리됐거나 이 형식을 모르는 버전이다.
        // 짧은 로그인 안내(300자·3줄 이하)를 먼저 본다 (미지원으로 차단하면 로그인한 뒤에도 조회하지 않게 된다).
        // 그보다 긴 평문은 모델 응답으로 보고 미지원으로 둔다. 본문은 결과에 담지 않는다.
        var plain = stdout.Trim();
        if (exitCode == 0 && plain.Length > 0)
        {
            bool shortNotice = plain.Length <= PlainLoginMaxChars && plain.Split('\n').Length <= PlainLoginMaxLines;
            return shortNotice && IsLoginFailure(null, plain)
                ? new GeminiRemoteResult { LoginRequired = true, PlainTextLogin = true, Error = LoginRequiredMessage }
                : Unsupported();
        }
        var detail = Sanitize(AgyError(stderr) ?? FirstLine(stderr) ??
                              (exitCode != 0 ? $"agy 종료 코드 {exitCode}" : "agy 출력이 비어 있습니다."));
        return new GeminiRemoteResult { LoginRequired = IsLoginFailure(null, detail), Error = detail };
    }

    static GeminiRemoteResult Unsupported() => new() { Unsupported = true, Error = UnsupportedMessage };

    /// <summary>/usage 는 대화를 만들지 않고 턴·토큰을 쓰지 않는다. 하나라도 어긋나면 프롬프트로 실행된 것이다.</summary>
    static bool RanAsPrompt(JsonElement root) =>
        root.Str("conversation_id") is { Length: > 0 } ||
        root.Num("num_turns") is double turns && turns != 0 ||
        root.Obj("usage")?.Num("total_tokens") is double tokens && tokens != 0;

    /// <summary>
    /// `command.data` 의 groups[].buckets[] 를 사용량 창으로 바꾼다.
    /// 키는 &lt;그룹&gt;:w&lt;분&gt; (gemini:w300, gemini:w10080, claude_gpt:w300 …). 같은 키가 또 나오면 버킷 id 를 덧붙인다.
    /// remaining_fraction 이 없으면 리셋 전인 버킷은 소진(100%)으로, 리셋 시각도 없으면 제외하고 경고를 붙인다.
    /// 단, 응답의 어느 버킷에도 remaining_fraction 이 없으면 0 생략이 아니라 필드 이름이 바뀐 것으로 보고 추정하지 않는다
    /// (창이 하나도 남지 않으므로 ParseOutput 이 '쿼터 형식 변경' 실패로 처리한다).
    /// </summary>
    public static UsageSnapshot ParseUsage(JsonElement data, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var windows = new List<UsageWindow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        bool missing = false, exhausted = false, inferredExhausted = false;

        var enabled = new List<(string GroupName, JsonElement Bucket)>();
        if (data.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object ||
                    !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    if (bucket.Bool("disabled") == true || bucket.Bool("enabled") == false) continue;
                    enabled.Add((group.Str("name") ?? "", bucket));
                }
            }
        }
        bool anyFraction = enabled.Any(x => Fraction(x.Bucket) != null);

        foreach (var (groupName, bucket) in enabled)
        {
            var id = bucket.Str("id") ?? "";
            var groupKey = GroupKey(groupName, id);
            var reset = JsonUtil.Iso(bucket.Str("reset_time") ?? bucket.Str("resetTime"));
            bool inferred = false;
            double remaining;
            if (Fraction(bucket) is double fraction) remaining = Math.Clamp(fraction, 0, 1);
            else if (anyFraction && reset is { } r && r > at)
            {
                remaining = 0;
                inferred = true;
            }
            else
            {
                missing = true;
                continue;
            }
            if (remaining <= 0 && groupKey == "gemini")
            {
                if (inferred) inferredExhausted = true;
                else exhausted = true;
            }

            int minutes = WindowMinutes(bucket.Str("window"), id);
            var idSlug = Slug(id.Length > 0 ? id : bucket.Str("name") ?? "limit");
            var key = minutes > 0 ? $"{groupKey}:w{minutes}" : $"{groupKey}:{idSlug}";
            if (!keys.Add(key))
            {
                var baseKey = key + ":" + (idSlug.Length > 0 ? idSlug : "limit");
                key = baseKey;
                for (int i = 2; !keys.Add(key); i++) key = $"{baseKey}_{i}";
            }
            var windowLabel = minutes > 0 ? CodexLabels.Window(minutes) : bucket.Str("name") ?? id;
            var label = groupKey == "gemini" ? windowLabel : $"{GroupLabel(groupKey, groupName)} · {windowLabel}";
            windows.Add(new UsageWindow(key, label, (1 - remaining) * 100, reset));
        }

        var warnings = new List<string>();
        if (missing) warnings.Add(MissingFractionWarning);
        if (exhausted) warnings.Add(ExhaustedWarning);
        else if (inferredExhausted) warnings.Add(InferredExhaustedWarning);
        return new UsageSnapshot
        {
            Windows = windows
                .OrderBy(w => GroupRank(w.Key))
                .ThenBy(w => int.TryParse(w.Key[(w.Key.LastIndexOf(":w", StringComparison.Ordinal) + 2)..], out var m) ? m : int.MaxValue)
                .ToList(),
            AsOf = DateTimeOffset.Now,
            Source = Source,
            Warning = warnings.Count > 0 ? string.Join(" · ", warnings) : null,
        };
    }

    static double? Fraction(JsonElement bucket)
    {
        foreach (var name in new[] { "remaining_fraction", "remainingFraction" })
        {
            if (!bucket.TryGetProperty(name, out var value)) continue;
            double? result = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
                // 일부 버전은 oneof 형태로 감싼다: { "remainingFraction": 0.4 }
                JsonValueKind.Object => value.Num("remainingFraction") ?? value.Num("remaining_fraction") ?? value.Num("value"),
                _ => null,
            };
            if (result is double f && double.IsFinite(f)) return f;
        }
        if (bucket.Obj("remaining") is { } nested && (nested.Num("remainingFraction") ?? nested.Num("remaining_fraction")) is double n && double.IsFinite(n))
            return n;
        return null;
    }

    static string GroupKey(string groupName, string bucketId)
    {
        if (groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return "gemini";
        if (groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase) ||
            bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase))
            return "claude_gpt";
        return Slug(groupName) is { Length: > 0 } slug ? slug : "other";
    }

    static string GroupLabel(string groupKey, string groupName) => groupKey == "claude_gpt" ? "Claude·GPT" : groupName;

    static int GroupRank(string key) => key.StartsWith("gemini:", StringComparison.Ordinal) ? 0
        : key.StartsWith("claude_gpt:", StringComparison.Ordinal) ? 1 : 2;

    static int WindowMinutes(string? window, string id)
    {
        var w = (window ?? "").Trim().ToLowerInvariant();
        switch (w)
        {
            case "5h" or "5-hour" or "five_hour" or "fivehour" or "session": return 300;
            case "weekly" or "week" or "7d": return 10080;
            case "daily" or "day" or "1d": return 1440;
        }
        if (Regex.Match(w, @"^(\d+)\s*h$") is { Success: true } hours) return int.Parse(hours.Groups[1].Value) * 60;
        if (Regex.Match(w, @"^(\d+)\s*d$") is { Success: true } days) return int.Parse(days.Groups[1].Value) * 1440;
        if (id.EndsWith("-5h", StringComparison.OrdinalIgnoreCase)) return 300;
        if (id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase)) return 10080;
        return 0;
    }

    static string Slug(string text) =>
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    static JsonElement? TryParseObject(string text)
    {
        var trimmed = text.Trim();
        var candidates = new List<string>();
        if (trimmed.StartsWith('{')) candidates.Add(trimmed);
        candidates.AddRange(trimmed.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse());
        // 머리말 뒤에 여러 줄로 들여 쓴 JSON: 첫 '{' 부터 마지막 '}' 까지
        int first = trimmed.IndexOf('{'), last = trimmed.LastIndexOf('}');
        if (first >= 0 && last > first) candidates.Add(trimmed[first..(last + 1)]);

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        // 머리말에도 '{' 가 있으면 위가 실패한다 (우선순위는 위보다 뒤):
        // '{' 로 시작하는 각 줄에서 JSON 값 하나만 읽는다 (마지막 '}' 뒤에 꼬리말이 있어도 된다). 앞 줄부터 — 바깥 객체가 먼저.
        int offset = 0, tried = 0;
        foreach (var line in trimmed.Split('\n'))
        {
            int brace = offset + (line.Length - line.TrimStart().Length);
            offset += line.Length + 1;
            if (!line.TrimStart().StartsWith('{')) continue;
            if (tried++ >= 20) break; // 큰 출력에서 시도가 너무 많아지지 않게 앞쪽 몇 개만 본다
            if (TryParseLeadingObject(trimmed[brace..]) is { } found) return found;
        }
        return null;
    }

    /// <summary>text 맨 앞의 JSON 객체 하나만 읽는다 (뒤에 다른 글이 있어도 된다).</summary>
    static JsonElement? TryParseLeadingObject(string text)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    // response 는 모델 응답일 수 있으므로 오류 문구로 쓰지 않는다
    static string? ErrorText(JsonElement root) =>
        root.Str("error") ?? root.Obj("error")?.Str("message") ?? root.Str("short_error") ?? root.Str("message");

    /// <summary>stderr 의 `AGY_ERROR: {"short_error": "…"}` 줄.</summary>
    static string? AgyError(string stderr)
    {
        foreach (var line in stderr.Split('\n'))
        {
            int i = line.IndexOf("AGY_ERROR:", StringComparison.Ordinal);
            if (i < 0) continue;
            if (TryParseObject(line[(i + "AGY_ERROR:".Length)..]) is { } e && e.Str("short_error") is { Length: > 0 } s) return s;
        }
        return null;
    }

    static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    static readonly Regex NetworkFailure = new(
        @"timed? ?out|timeout|unreachable|network|connection (refused|reset|closed|failed)|dial tcp|\bdns\b|lookup|no such host|offline|catalog|temporar(il)?y unavailable|\b50[234]\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex LoginFailure = new(
        @"\bnot (logged|signed) in(to)?\b|\bplease (log|sign) ?in\b|\b(log|sign)[ -]?in (is )?required\b|\blogin required\b|" +
        @"\b(you are|you're) (logged|signed) out\b|\bauthentication required\b|\bunauthenticated\b|로그인(이)? 필요",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly HashSet<string> LoginCodes = new(StringComparer.OrdinalIgnoreCase)
        { "UNAUTHENTICATED", "LOGIN_REQUIRED", "NOT_LOGGED_IN", "AUTH_REQUIRED", "401" };

    /// <summary>
    /// 로그인 필요 여부. 순서: ① 구조화된 status·오류 코드(UNAUTHENTICATED, 401 등)가 있으면 그것을 믿는다.
    /// ② 문구에 네트워크 오류 표현이 섞이면 일시적 실패로 본다 (로그인 필요로 오판하면 정상 주기 조회가 멈춘다).
    /// ③ 남은 문구는 분명한 로그인 안내 표현만 인정한다.
    /// </summary>
    static bool IsLoginFailure(JsonElement? root, string message)
    {
        if (root is { } r)
        {
            var error = r.Obj("error");
            foreach (var code in new[] { r.Str("status"), r.Str("code"), r.Str("error_code"), error?.Str("code"), error?.Str("status") })
                if (code != null && LoginCodes.Contains(code)) return true;
            if ((r.Num("code") ?? error?.Num("code")) == 401) return true;
        }
        if (NetworkFailure.IsMatch(message)) return false;
        return LoginFailure.IsMatch(message);
    }

    /// <summary>오류 문구를 화면·로그에 남겨도 되도록 사용자 폴더·계정·토큰처럼 보이는 값을 지우고 한 줄로 줄인다.</summary>
    internal static string Sanitize(string message)
    {
        var text = message;
        var home = Paths.Home;
        if (home.Length > 3)
        {
            foreach (var variant in new[] { home, home.Replace('\\', '/') })
                text = Regex.Replace(text, Regex.Escape(variant), "~", RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "<계정>");
        text = Regex.Replace(text, @"(ya29\.|1//|eyJ)[A-Za-z0-9._\-]+", "***");
        text = Regex.Replace(text, @"[A-Za-z0-9_\-]{32,}", "***");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
