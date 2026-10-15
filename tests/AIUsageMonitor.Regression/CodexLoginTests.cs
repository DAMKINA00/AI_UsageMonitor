using System.Text.Json;
using System.Reflection;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    // The test executable also acts as a synthetic stdio server, using only its temporary working directory.
    static int FakeCodexServer()
    {
        string mode = System.IO.File.ReadAllText("scenario.txt");
        string? line;
        void Send(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
        while ((line = Console.ReadLine()) != null)
        {
            using var doc = JsonDocument.Parse(line);
            var request = doc.RootElement;
            if (request.Num("id") is not { } id) continue;
            switch (request.Str("method"))
            {
                case "initialize":
                    Send(new { id, result = new { } }); break;
                case "account/read":
                    Send(new { id, result = new { account = mode == "empty" ? null : new { type = "chatgpt", email = "sub@example.test", planType = "pro" } } }); break;
                case "account/rateLimits/read":
                    Send(new { id, result = new { rateLimits = new { primary = new { usedPercent = 37, windowDurationMins = 10080 } } } }); break;
                case "account/login/start":
                    // Completion may precede the request response; another login's notification must be ignored.
                    Send(new { method = "account/login/completed", @params = new { loginId = "unrelated", success = true } });
                    if (mode is "success" or "failure")
                        Send(new { method = "account/login/completed", @params = new { loginId = "test-login", success = mode == "success" } });
                    Send(new { id, result = new { loginId = "test-login", authUrl = mode == "bad-url" ? "https://example.test/login" : "https://auth.openai.com/authorize?state=test" } });
                    break;
                case "account/login/cancel":
                    System.IO.File.WriteAllText("cancelled.txt", "cancelled");
                    Send(new { id, result = new { } }); break;
                case "account/logout":
                    System.IO.File.WriteAllText("logout.txt", "logout");
                    Send(new { id, result = new { } }); break;
            }
        }
        return 0;
    }

    static async Task TestCodexLoginProtocol()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageMonitor-rpc-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var exe = Environment.ProcessPath!;
        void Mode(string mode) => System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "scenario.txt"), mode);
        try
        {
            Mode("empty");
            var empty = await CodexAppServer.QueryAsync(exe, codexHome: directory);
            Check("isolated unsigned Codex account requires browser login", empty.LoginRequired && !empty.Ok, empty.Error ?? "");
            Mode("success");
            int opens = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CodexAppServer.LoginAsync(exe, directory, _ => opens++, timeout.Token);
            Check("Codex accepts matching early login completion and opens browser once", opens == 1, $"opens={opens}");
            var usage = await CodexAppServer.QueryAsync(exe, codexHome: directory);
            Check("isolated Codex usage is read after login", usage.Ok && usage.Email == "sub@example.test" &&
                usage.Usage?.Windows.Single().UsedPercent == 37, usage.Error ?? "");
            await CodexAppServer.LogoutAsync(exe, directory);
            Check("logout uses isolated stdio server", System.IO.File.Exists(System.IO.Path.Combine(directory, "logout.txt")), directory);

            Mode("failure");
            bool failed = false;
            try { await CodexAppServer.LoginAsync(exe, directory, _ => { }, timeout.Token); }
            catch (System.IO.IOException) { failed = true; }
            Check("Codex login failure is not success", failed, "failure response accepted");

            Mode("bad-url");
            opens = 0;
            failed = false;
            try { await CodexAppServer.LoginAsync(exe, directory, _ => opens++, timeout.Token); }
            catch (System.IO.IOException) { failed = true; }
            Check("unexpected auth host never opens browser", failed && opens == 0, $"failed={failed}, opens={opens}");

            Mode("pending");
            System.IO.File.Delete(System.IO.Path.Combine(directory, "cancelled.txt"));
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try { await CodexAppServer.LoginAsync(exe, directory, _ => cancel.Cancel(), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("cancelled login sends cancellation and terminates", cancelled &&
                System.IO.File.Exists(System.IO.Path.Combine(directory, "cancelled.txt")), $"cancelled={cancelled}");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    static void TestCodexRateLimitInference()
    {
        UsageSnapshot Parse(string json, string plan = "pro")
        {
            using var document = JsonDocument.Parse(json);
            var method = typeof(CodexAppServer).GetMethod("ParseRateLimits", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(CodexAppServer), "ParseRateLimits");
            return (UsageSnapshot)method.Invoke(null, [document.RootElement, plan])!;
        }

        var primaryNull = Parse("""
            {"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":10080},"secondary":null}}
            """);
        var secondaryNull = Parse("""
            {"rateLimits":{"primary":null,"secondary":{"usedPercent":7,"windowDurationMins":10080}}}
            """);
        Check("Codex Pro with an explicit missing primary window is unlimited", primaryNull.FiveHourUnlimited &&
            primaryNull.Windows is [{ Key: "codex:w10080" }], DescribeWindows(primaryNull.Windows));
        Check("Codex Pro with an explicit missing secondary window is unlimited", secondaryNull.FiveHourUnlimited &&
            secondaryNull.Windows is [{ Key: "codex:w10080" }], DescribeWindows(secondaryNull.Windows));

        var noLimits = Parse("{}");
        var emptyLimits = Parse("{\"rateLimits\":{}}");
        var missingWindow = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":5}}}");
        Check("Codex Pro without a rate-limit object is not inferred unlimited", !noLimits.FiveHourUnlimited && noLimits.Windows.Count == 0,
            DescribeWindows(noLimits.Windows));
        Check("Codex Pro with empty rate limits is not inferred unlimited", !emptyLimits.FiveHourUnlimited && emptyLimits.Windows.Count == 0,
            DescribeWindows(emptyLimits.Windows));
        Check("Codex Pro without a window duration is not inferred unlimited", !missingWindow.FiveHourUnlimited && missingWindow.Windows.Count == 1,
            DescribeWindows(missingWindow.Windows));

        var measuredFiveHour = Parse("""
            {"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":300},"secondary":{"usedPercent":8,"windowDurationMins":10080}}}
            """);
        Check("an explicit five-hour window suppresses unlimited inference", !measuredFiveHour.FiveHourUnlimited &&
            measuredFiveHour.Windows.Any(w => w.Key == "codex:w300"), DescribeWindows(measuredFiveHour.Windows));

        var now = DateTimeOffset.UtcNow;
        var server = new UsageSnapshot
        {
            Windows = [new UsageWindow("codex:w10080", "주간 한도", 20, now.AddDays(2))],
            AsOf = now,
            Source = "Codex 서버",
            Plan = "Pro 20x",
            FiveHourUnlimited = true,
        };
        var logWithoutFive = new UsageSnapshot
        {
            Windows = [new UsageWindow("codex:w10080", "주간 한도", 25, now.AddDays(2))],
            AsOf = now.AddMinutes(1),
            Source = "Codex 로그",
        };
        using var monitor = new MonitorService(new AppSettings());
        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), new CodexRemoteResult { Ok = true, Usage = server });
        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState { LogUsage = logWithoutFive });
        var kept = Presenter.Codex(monitor);
        Check("newer log without a five-hour window keeps unlimited state", kept.AccountUsage[0].IsUnlimited && kept.AccountUsage[0].UsedText == "무제한",
            DescribeRows(kept.AccountUsage));

        var logWithFive = new UsageSnapshot
        {
            Windows = [new UsageWindow("codex:w300", "5시간 한도", 9, now.AddHours(1)), ..logWithoutFive.Windows],
            AsOf = logWithoutFive.AsOf,
            Source = logWithoutFive.Source,
        };
        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState { LogUsage = logWithFive });
        var measured = Presenter.Codex(monitor);
        Check("newer log with a five-hour window clears unlimited state", !measured.AccountUsage[0].IsUnlimited &&
            measured.AccountUsage[0].Value == 9, DescribeRows(measured.AccountUsage));
    }
}
