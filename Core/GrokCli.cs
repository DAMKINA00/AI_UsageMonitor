using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class GrokRemoteResult
{
    public bool Ok { get; init; }
    public bool Installed { get; init; } = true;
    public bool LoginRequired { get; init; }
    public string? Error { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>Official Grok ACP account billing. Never creates a session or sends a prompt.</summary>
public static class GrokCli
{
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var official = Path.Combine(Paths.Home, ".grok", "bin", "grok.exe");
        if (File.Exists(official)) return official;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var file = Path.Combine(dir.Trim(' ', '"'), "grok.exe");
                if (File.Exists(file)) return file;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static UsageSnapshot? ParseBilling(JsonElement response)
    {
        if (!Object(response, "config", out var config)) return null;
        bool hasPeriod = Object(config, "currentPeriod", out var period);
        if (config.TryGetProperty("currentPeriod", out _) && !hasPeriod) return null;
        string? type = Text(period, "type");
        string? key = type switch
        {
            "USAGE_PERIOD_TYPE_WEEKLY" => "weekly",
            "USAGE_PERIOD_TYPE_MONTHLY" => "monthly",
            _ when !hasPeriod && Object(config, "monthlyLimit", out _) => "monthly",
            _ => null,
        };
        if (key == null) return null;
        var endText = hasPeriod ? Text(period, "end") : Text(config, "billingPeriodEnd");
        DateTimeOffset? end = DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        if (endText != null && end == null) return null;
        double used;
        if (config.TryGetProperty("creditUsagePercent", out var percent))
        {
            if (!percent.TryGetDoubleSafe(out used)) return null;
        }
        else if (Object(config, "monthlyLimit", out var limit) && Number(limit, "val", out var cap) && cap > 0 &&
            (!config.TryGetProperty("used", out var spent) || ObjectValue(spent) && (!spent.TryGetProperty("val", out _) || Number(spent, "val", out _))))
        {
            double amount = 0;
            if (Object(config, "used", out var spentObject)) Number(spentObject, "val", out amount);
            if (amount < 0) return null;
            used = Math.Min(100, amount / cap * 100);
        }
        else if (hasPeriod && end != null && !config.TryGetProperty("monthlyLimit", out _) && !config.TryGetProperty("used", out _))
        {
            // Grok's protobuf JSON omits numeric zero. Only a valid quota period permits this default.
            used = 0;
        }
        else return null;
        if (!double.IsFinite(used) || used < 0 || used > 100) return null;
        return new UsageSnapshot
        {
            Windows = [new(key, key == "weekly" ? "주간 한도" : "월간 한도", used, end)],
            AsOf = DateTimeOffset.Now, Source = "Grok CLI · 계정 한도", Plan = Text(response, "subscription_tier"),
        };
    }

    static bool ObjectValue(JsonElement value) => value.ValueKind == JsonValueKind.Object;
    static bool Object(JsonElement value, string key, out JsonElement result)
    {
        result = default;
        return ObjectValue(value) && value.TryGetProperty(key, out result) && ObjectValue(result);
    }
    static string? Text(JsonElement value, string key) => ObjectValue(value) && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    static bool Number(JsonElement value, string key, out double number)
    {
        number = 0;
        return ObjectValue(value) && value.TryGetProperty(key, out var item) && item.TryGetDoubleSafe(out number) && double.IsFinite(number);
    }
    static bool TryGetDoubleSafe(this JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }

    public static async Task<GrokRemoteResult> QueryAsync(string? configured = null, CancellationToken ct = default)
    {
        var exe = FindExecutable(configured);
        if (exe == null) return new() { Installed = false, Error = "Grok CLI 미설치 · grok.exe 경로를 확인하세요." };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var child = new Process { StartInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Paths.Home,
        } };
        foreach (var arg in new[] { "--permission-mode", "default", "--no-subagents", "agent", "stdio" }) child.StartInfo.ArgumentList.Add(arg);
        using var job = KillOnCloseJob.TryCreate();
        Task? drain = null;
        int? ownPid = null;
        try
        {
            child.Start();
            ownPid = child.Id;
            ProcessUtil.RegisterOwnProcess(child.Id);
            job?.TryAdd(child, "grok usage");
            drain = DrainAsync(child.StandardError, timeout.Token);
            int nextId = 0;
            async Task<JsonElement> Request(string method, object parameters)
            {
                int id = ++nextId;
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), timeout.Token);
                await child.StandardInput.FlushAsync(timeout.Token);
                while (true)
                {
                    var line = await child.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Grok 연결 종료");
                    if (line.Length > 1_048_576) throw new IOException("Grok 응답 크기 초과");
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                    using (doc)
                    {
                        var message = doc.RootElement;
                        if (!ObjectValue(message)) continue;
                        if (message.TryGetProperty("method", out _))
                        {
                            if (message.TryGetProperty("id", out var incomingId))
                                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = incomingId, error = new { code = -32601, message = "Read-only quota client" } }).AsMemory(), timeout.Token);
                            continue;
                        }
                        if (!message.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || !replyId.TryGetInt32(out int reply) || reply != id) continue;
                        if (Object(message, "error", out var error)) throw new IOException(Text(error, "message") ?? "Grok RPC 실패");
                        return message.TryGetProperty("result", out var result) ? result.Clone() : default;
                    }
                }
            }
            var init = await Request("initialize", new { protocolVersion = 1, clientCapabilities = new { }, clientInfo = new { name = "AIUsageMonitor", version = "1.0.0" } });
            string? auth = Object(init, "_meta", out var meta) ? Text(meta, "defaultAuthMethodId") : null;
            if (auth != "cached_token" && auth != "xai.api_key")
            {
                if (init.TryGetProperty("authMethods", out var methods) && methods.ValueKind == JsonValueKind.Array && methods.EnumerateArray().Any(x => Text(x, "id") == "cached_token")) auth = "cached_token";
                else return new() { LoginRequired = true, Error = "터미널에서 grok login 후 새로고침하세요." };
            }
            await Request("authenticate", new { methodId = auth, _meta = new { headless = true } });
            var billing = await Request("_x.ai/billing", new { });
            var usage = ParseBilling(billing);
            return usage == null ? new() { Error = "Grok CLI가 계정 한도 정보를 제공하지 않았습니다. CLI 버전을 확인하세요." } : new() { Ok = true, Usage = usage };
        }
        catch (OperationCanceledException) { return new() { Error = ct.IsCancellationRequested ? "조회 취소됨" : "Grok 사용량 응답 시간 초과" }; }
        catch (Exception ex)
        {
            bool auth = new[] { "auth", "login", "unauthorized", "401", "sign in" }.Any(x => ex.Message.Contains(x, StringComparison.OrdinalIgnoreCase));
            return new() { LoginRequired = auth, Error = auth ? "터미널에서 grok login 후 새로고침하세요." :
                ex.Message.Contains("method not found", StringComparison.OrdinalIgnoreCase) ? "Grok CLI 사용량 조회 미지원 · CLI를 업데이트하세요." : "Grok 사용량 조회 실패 · 연결 상태를 확인하세요." };
        }
        finally
        {
            try
            {
                child.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await child.WaitForExitAsync(grace.Token);
            }
            catch { try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { } }
            timeout.Cancel();
            if (drain != null) { try { await drain; } catch { } }
            if (ownPid is int pid) ProcessUtil.UnregisterOwnProcess(pid);
        }
    }

    static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
}
