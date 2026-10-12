using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestClaudeDesktop()
    {
        var type = typeof(ClaudeWebClient).Assembly.GetType("AIUsageMonitor.Core.ClaudeDesktopSession", true)!;
        var decode = type.GetMethod("DecodeCookie", BindingFlags.NonPublic | BindingFlags.Static)!;
        var key = RandomNumberGenerator.GetBytes(32);
        const string domain = ".claude.ai", value = "synthetic-session-for-offline-test";
        byte[] Encrypt(string host, string text, string prefix = "v10", bool bindHost = true)
        {
            var plain = (bindHost ? SHA256.HashData(Encoding.UTF8.GetBytes(host)) : [])
                .Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            var result = new byte[31 + plain.Length];
            Encoding.ASCII.GetBytes(prefix).CopyTo(result, 0);
            RandomNumberGenerator.Fill(result.AsSpan(3, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(3, 12), plain, result.AsSpan(15, plain.Length), result.AsSpan(result.Length - 16));
            return result;
        }
        string Decode(byte[] input, string host = domain, int version = 24) =>
            (string)decode.Invoke(null, [input, key, host, version])!;
        bool Rejected(byte[] input, string host = domain)
        {
            try { Decode(input, host); return false; }
            catch (TargetInvocationException e) when (e.InnerException is CryptographicException) { return true; }
        }
        var encoded = Encrypt(domain, value);
        Check("Desktop cookie decrypts with matching user key and host", Decode(encoded) == value, "fixture decryption failed");
        Check("Desktop v11 cookie decrypts", Decode(Encrypt(domain, value, "v11")) == value, "v11 failed");
        Check("legacy Desktop cookie schema remains readable", Decode(Encrypt(domain, value, bindHost: false), version: 23) == value, "legacy failed");
        Check("Desktop cookie rejects a different host", Rejected(encoded, "other.example"), "host binding was ignored");
        var corrupt = encoded.ToArray(); corrupt[^1] ^= 1;
        Check("Desktop cookie rejects a corrupt authentication tag", Rejected(corrupt), "corrupt data accepted");
        Check("Desktop cookie rejects unsupported encryption", Rejected(Encrypt(domain, value, "v20")), "unsupported format accepted");
        Check("Desktop cookie rejects truncated data", Rejected([1, 2, 3]), "truncated data accepted");
        CryptographicOperations.ZeroMemory(key);

        var read = type.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
        var absent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageMonitor-Regression-" + Guid.NewGuid().ToString("N"));
        var empty = (System.Collections.ICollection)read.Invoke(null, [absent])!;
        Check("missing Desktop profile needs no separate login dialog", empty.Count == 0, "unexpected cookie data");

        var reset = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = 2, resets_at = reset },
            seven_day = new { utilization = 18, resets_at = reset },
            internal_unused = new { utilization = 0, resets_at = reset },
            limits = new object[]
            {
                new { kind = "session", percent = 3, resets_at = reset },
                new { kind = "weekly_all", percent = 18, resets_at = reset },
                new { kind = "weekly_scoped", percent = 0, resets_at = reset, scope = new { model = new { display_name = "Fable" } } },
                new { kind = "weekly_scoped", percent = -1, scope = new { model = new { display_name = "Invalid" } } },
                new { kind = "weekly_scoped", percent = 101, scope = new { model = new { display_name = "Invalid high" } } },
            },
        }));
        var parse = typeof(ClaudeWebClient).GetMethod("ParseUsage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var desktop = (UsageSnapshot)parse.Invoke(null, [doc.RootElement, "Max 20x", "Claude 데스크톱"])!;
        Check("Desktop usage keeps zero-valued named model limit", desktop.Windows.Any(w => w.Label == "주간 Fable" && w.UsedPercent == 0), "Fable missing");
        Check("Desktop usage merges modern fields without duplicate primary limits", desktop.Windows.Count == 3 && desktop.Windows.Single(w => w.Key == "five_hour").UsedPercent == 3, DescribeWindows(desktop.Windows));
        Check("Desktop usage retains reset time and source", desktop.Windows.All(w => w.ResetsAt.HasValue) && desktop.Source == "Claude 데스크톱", "reset/source missing");

        var settings = new AppSettings();
        Check("Desktop connection is enabled without monitor login", settings.ClaudeDesktopEnabled && !settings.ClaudeWebEnabled, "unexpected defaults");
        using var monitor = new MonitorService(settings);
        SetProperty(monitor, nameof(MonitorService.ClaudeWebGood), new ClaudeWebResult { State = ClaudeWebState.Ok, Usage = desktop });
        SetProperty(monitor, nameof(MonitorService.ClaudeLocal), new ClaudeLocalState
        {
            DesktopRunning = true,
            StatusLineUsage = Snapshot("different CLI account", desktop.AsOf.AddMinutes(1), Window("seven_day", 95, DateTimeOffset.Now.AddHours(1))),
            Sessions = [new SessionInfo("local", "local task", "fixture", SessionState.Working, "작업 중", DateTimeOffset.Now)],
        });
        var card = Presenter.Claude(monitor, settings, () => { });
        Check("Desktop account usage is not overwritten by a different CLI login", card.RawWindows.Single(w => w.Key == "seven_day").UsedPercent == 18, DescribeWindows(card.RawWindows));
        Check("Desktop connected card does not ask for monitor login", card.ActionText == null && card.Footnote.Contains("Claude 데스크톱"), card.ActionText ?? card.Footnote);
        Check("Desktop connection retains local work status", card.Sessions.Any(s => s.Id == "local"), "local work missing");

        SetProperty(monitor, nameof(MonitorService.ClaudeChats), new List<SessionInfo> { new("chat:old", "old account", "fixture", SessionState.Working, "작업 중", DateTimeOffset.Now) });
        SetProperty(monitor, nameof(MonitorService.ClaudeChatsAt), DateTimeOffset.Now);
        var accountChanged = (Action?)typeof(ClaudeWebClient).GetField("AccountChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor.ClaudeDesktop);
        accountChanged!.Invoke();
        Check("Desktop account switch clears previous account usage and chats", monitor.ClaudeWebGood == null && monitor.ClaudeChats.Count == 0 && monitor.ClaudeChatsAt == null, "previous account state retained");
        SetProperty(monitor, nameof(MonitorService.ClaudeWebLast), new ClaudeWebResult { State = ClaudeWebState.LoginRequired });
        card = Presenter.Claude(monitor, settings, () => { });
        Check("Desktop logout directs user to the Desktop app", card.ActionText == "Claude 데스크톱 열기" && card.Usage.Count == 0 && card.Sessions.Any(s => s.Id == "local"), "wrong login prompt or lost work state");
    }
}
