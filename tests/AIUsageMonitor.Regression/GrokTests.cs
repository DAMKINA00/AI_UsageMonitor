using System.IO;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    const string FakeGrokScenario = "AIUSAGEMONITOR_FAKE_GROK";
    const string FakeGrokDirectory = "AIUSAGEMONITOR_FAKE_GROK_DIR";

    static void TestGrokBilling()
    {
        var weekly = ParseGrokBilling("""
            {"subscription_tier":"SuperGrok","config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-11-01T00:00:00Z"},"creditUsagePercent":12.5}}
            """);
        var weeklyWindow = weekly?.Windows.SingleOrDefault();
        Check("Grok weekly credit usage parses with its reset", weekly?.Plan == "SuperGrok" &&
            weeklyWindow is { Key: "weekly", UsedPercent: 12.5 } &&
            weeklyWindow.ResetsAt == DateTimeOffset.Parse("2026-11-01T00:00:00Z"), DescribeGrok(weekly));

        var monthly = ParseGrokBilling("""
            {"subscription_tier":"SuperGrok","config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","end":"2026-12-01T00:00:00Z"},"creditUsagePercent":37.25}}
            """);
        var monthlyWindow = monthly?.Windows.SingleOrDefault();
        Check("Grok monthly credit usage parses as a monthly window", monthlyWindow is { Key: "monthly", UsedPercent: 37.25 } &&
            monthlyWindow.ResetsAt == DateTimeOffset.Parse("2026-12-01T00:00:00Z"), DescribeGrok(monthly));

        var omittedZero = ParseGrokBilling("""
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-11-01T00:00:00Z"}}}
            """);
        Check("Grok omits a valid zero quota value as zero", omittedZero?.Windows.SingleOrDefault() is
            { Key: "weekly", UsedPercent: 0 } && omittedZero.Windows.Count == 1, DescribeGrok(omittedZero));

        var legacy = ParseGrokBilling("""
            {"subscription_tier":"Legacy SuperGrok","config":{"billingPeriodEnd":"2027-01-01T00:00:00Z","monthlyLimit":{"val":400},"used":{"val":100}}}
            """);
        var legacyWindow = legacy?.Windows.SingleOrDefault();
        Check("Grok legacy monthlyLimit.val and used.val become a monthly percent", legacy?.Plan == "Legacy SuperGrok" &&
            legacyWindow is { Key: "monthly", UsedPercent: 25 } &&
            legacyWindow.ResetsAt == DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DescribeGrok(legacy));

        var empty = ParseGrokBilling("{}");
        var missingConfig = ParseGrokBilling("""{"subscription_tier":"SuperGrok"}""");
        var invalidConfig = ParseGrokBilling("""{"config":null}""");
        var badType = ParseGrokBilling("""
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_DAILY","end":"2026-11-01T00:00:00Z"},"creditUsagePercent":10}}
            """);
        var badEnd = ParseGrokBilling("""
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"not-a-period"},"creditUsagePercent":10}}
            """);
        var malformedNumeric = ParseGrokBilling("""
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-11-01T00:00:00Z"},"creditUsagePercent":"not-a-number"}}
            """);
        Check("Grok rejects empty and missing config responses", empty == null && missingConfig == null && invalidConfig == null,
            $"empty={DescribeGrok(empty)}, missing={DescribeGrok(missingConfig)}, null={DescribeGrok(invalidConfig)}");
        Check("Grok rejects an unknown or malformed billing period", badType == null && badEnd == null,
            $"bad type={DescribeGrok(badType)}, bad end={DescribeGrok(badEnd)}");
        Check("Grok rejects malformed numeric usage", malformedNumeric == null, DescribeGrok(malformedNumeric));
        Check("Grok does not invent a five-hour quota window", weekly?.Windows.Count == 1 && monthly?.Windows.Count == 1 &&
            weekly.Windows.All(w => !w.Key.Contains("5h", StringComparison.OrdinalIgnoreCase)) &&
            monthly.Windows.All(w => !w.Key.Contains("5h", StringComparison.OrdinalIgnoreCase)),
            $"weekly={DescribeGrok(weekly)}, monthly={DescribeGrok(monthly)}");
        Check("Grok weekly and monthly allowances reach the visible ring", Presenter.DisplayUsage(weekly!.Windows) is [{ Label: "주간 한도", Value: 12.5 }] &&
            Presenter.DisplayUsage(monthly!.Windows) is [{ Label: "월간 한도", Value: 37.25 }], "visible display windows");
        var rows = Presenter.AccountUsageRows(monthly!.Windows);
        Check("Grok account panel keeps an unknown five-hour row above monthly", rows is [{ IsUnknown: true, IsUnlimited: false }, { Label: "월간 한도", Value: 37.25 }], "account rows");
        using var monitor = new MonitorService(new AppSettings(), _ => { });
        SetProperty(monitor, nameof(MonitorService.GrokRemoteGood), new GrokRemoteResult { Ok = true, Usage = weekly });
        SetProperty(monitor, nameof(MonitorService.GrokRemoteLast), new GrokRemoteResult { Error = "fixture offline" });
        var card = Presenter.Grok(monitor);
        Check("Grok failure preserves the prior quota visibly dimmed", card.Plan == "SuperGrok" && card.Status.IsDimmed && card.Usage is [{ Value: 12.5 }], "cached quota");
        var settings = new AppSettings { GeminiPlanOverride = "Google AI Pro" };
        Check("Gemini and Grok plan badges contain only the tier name", Presenter.Gemini(monitor, settings).Plan == "Google AI Pro" && card.Plan == "SuperGrok", "tier labels");
    }

    static async Task TestGrokAcpProcess()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Environment.ProcessPath!;
        var previousScenario = Environment.GetEnvironmentVariable(FakeGrokScenario);
        var previousDirectory = Environment.GetEnvironmentVariable(FakeGrokDirectory);
        Environment.SetEnvironmentVariable(FakeGrokDirectory, dir);

        void Scenario(string name)
        {
            Environment.SetEnvironmentVariable(FakeGrokScenario, name);
            foreach (var file in new[] { "args.txt", "pid.txt", "requests.jsonl" })
                File.Delete(Path.Combine(dir, file));
        }

        try
        {
            Scenario("success");
            var success = await GrokCli.QueryAsync(exe);
            var requests = ReadGrokRequests(Path.Combine(dir, "requests.jsonl"));
            var args = ReadOrEmpty(Path.Combine(dir, "args.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var methods = requests.Select(RequestMethod).ToArray();
            var initialize = requests.SingleOrDefault(x => RequestMethod(x) == "initialize");
            var auth = requests.SingleOrDefault(x => RequestMethod(x) == "authenticate");
            var billing = requests.SingleOrDefault(x => RequestMethod(x) == "_x.ai/billing");
            Check("Grok uses the exact ACP stdio invocation", args.SequenceEqual(["--permission-mode", "default", "--no-subagents", "agent", "stdio"]),
                string.Join(" ", args));
            Check("Grok uses the configured executable override", GrokCli.FindExecutable(exe) == exe, GrokCli.FindExecutable(exe) ?? "null");
            Check("Grok ACP usage query succeeds", success is { Ok: true, Installed: true, LoginRequired: false } &&
                success.Usage?.Plan == "SuperGrok" && success.Usage.Windows.Single() is { Key: "weekly", UsedPercent: 32.5 },
                $"ok={success.Ok}, error={success.Error}, usage={DescribeGrok(success.Usage)}");
            Check("Grok ACP sends only initialize, headless authenticate and billing", methods.SequenceEqual(["initialize", "authenticate", "_x.ai/billing"]) &&
                auth.TryGetProperty("params", out var authParams) && authParams.TryGetProperty("_meta", out var authMeta) &&
                authMeta.TryGetProperty("headless", out var headless) && headless.ValueKind == JsonValueKind.True,
                string.Join(",", methods));
            Check("Grok ACP initialize and billing parameters stay read-only", initialize.TryGetProperty("params", out var initParams) &&
                initParams.TryGetProperty("protocolVersion", out var protocol) && protocol.GetInt32() == 1 &&
                initParams.TryGetProperty("clientCapabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
                initParams.TryGetProperty("clientInfo", out var clientInfo) && clientInfo.GetProperty("name").GetString() == "AIUsageMonitor" &&
                clientInfo.GetProperty("version").GetString() == "1.0.0" && billing.TryGetProperty("params", out var billingParams) &&
                billingParams.ValueKind == JsonValueKind.Object && !billingParams.EnumerateObject().Any(),
                "initialize or billing parameters changed");
            Check("Grok ACP never invents a five-hour window", success.Usage?.Windows.Count == 1 &&
                success.Usage.Windows.All(w => !w.Key.Contains("5h", StringComparison.OrdinalIgnoreCase)), DescribeGrok(success.Usage));

            Scenario("authfailure");
            var authFailure = await GrokCli.QueryAsync(exe);
            Check("Grok ACP authentication failure asks for login", !authFailure.Ok && authFailure.LoginRequired &&
                !string.IsNullOrWhiteSpace(authFailure.Error), authFailure.Error ?? "");

            Scenario("methodnotfound");
            var unsupported = await GrokCli.QueryAsync(exe);
            Check("Grok ACP billing method-not-found is reported as unsupported", !unsupported.Ok && !unsupported.LoginRequired &&
                unsupported.Error?.Contains("미지원", StringComparison.Ordinal) == true, unsupported.Error ?? "");

            Scenario("cancel");
            int childPid = 0;
            using var cancellation = new CancellationTokenSource();
            var cancelledTask = GrokCli.QueryAsync(exe, cancellation.Token);
            for (var i = 0; i < 100 && childPid == 0; i++)
            {
                childPid = int.TryParse(ReadOrEmpty(Path.Combine(dir, "pid.txt")), out var parsed) ? parsed : 0;
                if (childPid == 0) await Task.Delay(20);
            }
            cancellation.Cancel();
            var cancelled = await cancelledTask;
            Check("cancelled Grok ACP query stops the process", cancelled.Error == "조회 취소됨" && childPid != 0 &&
                await ExitedSoon(childPid), $"error={cancelled.Error}, pid={childPid}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeGrokScenario, previousScenario);
            Environment.SetEnvironmentVariable(FakeGrokDirectory, previousDirectory);
            TryDelete(dir);
        }
    }

    static UsageSnapshot? ParseGrokBilling(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GrokCli.ParseBilling(document.RootElement);
    }

    static string DescribeGrok(UsageSnapshot? usage) => usage == null
        ? "null"
        : $"{usage.Plan ?? "(no plan)"}: {string.Join(", ", usage.Windows.Select(w => $"{w.Key}={w.UsedPercent:0.##}@{w.ResetsAt:o}"))}";

    static string RequestMethod(JsonElement request) => request.TryGetProperty("method", out var method) &&
        method.ValueKind == JsonValueKind.String ? method.GetString() ?? "" : "";

    static List<JsonElement> ReadGrokRequests(string path)
    {
        var requests = new List<JsonElement>();
        foreach (var line in ReadOrEmpty(path).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            requests.Add(document.RootElement.Clone());
        }
        return requests;
    }

    /// <summary>The test executable doubles as a tiny ACP stdio server for GrokCli.QueryAsync.</summary>
    static int FakeGrokAcp(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable(FakeGrokScenario) ?? "success";
        var directory = Environment.GetEnvironmentVariable(FakeGrokDirectory);
        var requestsPath = directory == null ? null : Path.Combine(directory, "requests.jsonl");
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "args.txt"), string.Join("\n", args));
            File.WriteAllText(Path.Combine(directory, "pid.txt"), Environment.ProcessId.ToString());
        }

        void Record(JsonElement request)
        {
            if (requestsPath != null) File.AppendAllText(requestsPath, request.GetRawText() + Environment.NewLine);
        }

        void Send(JsonElement id, object? result = null, object? error = null)
        {
            if (error == null)
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
            else
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error }));
            Console.Out.Flush();
        }

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            Record(request);
            if (!request.TryGetProperty("id", out var id)) continue;
            id = id.Clone();
            var method = RequestMethod(request);
            switch (method)
            {
                case "initialize":
                    if (scenario == "cancel")
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(30));
                        return 0;
                    }
                    Send(id, new { _meta = new { defaultAuthMethodId = "cached_token" } });
                    break;
                case "authenticate":
                    if (scenario == "authfailure")
                        Send(id, error: new { code = -32001, message = "authentication required" });
                    else Send(id, new { });
                    break;
                case "_x.ai/billing":
                    if (scenario == "methodnotfound")
                    {
                        Send(id, error: new { code = -32601, message = "method not found" });
                    }
                    else
                    {
                        Send(id, new
                        {
                            subscription_tier = "SuperGrok",
                            config = new
                            {
                                currentPeriod = new { type = "USAGE_PERIOD_TYPE_WEEKLY", end = "2026-11-01T00:00:00Z" },
                                creditUsagePercent = 32.5,
                            },
                        });
                    }
                    break;
                default:
                    Send(id, error: new { code = -32601, message = $"unexpected method {method}" });
                    break;
            }
        }
        return 0;
    }
}
