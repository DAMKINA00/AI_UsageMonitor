using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void RunChatterBoxAccountTests()
    {
        Run("ChatterBox account snapshot command and parsing", TestChatterBoxSnapshotParsing);
        Run("hosted cards follow ChatterBox accounts only", TestHostedCards);
    }

    /// <summary>The message ChatterBox's usage-monitor-feed.ts sends (labels, flags and numbers only).</summary>
    static string ChatterBoxAccountsLine(long checkedAtMs, bool gpt2Enabled = false)
    {
        var resetWeek = DateTimeOffset.Now.AddDays(3).ToUnixTimeSeconds();
        var resetFive = DateTimeOffset.Now.AddHours(2).ToUnixTimeSeconds();
        object Row(string id, string label, bool enabled, double weekly) => new
        {
            accountId = id, kind = id[..id.IndexOf('-')], label, enabled, isDefault = id.EndsWith("-1"), authStatus = "authenticated",
            runningWorkers = 0, display = (object?)null, checkedAt = checkedAtMs,
            windows = new { fiveHour = new { usedPercent = 10, resetsAt = resetFive }, weekly = new { usedPercent = weekly, resetsAt = resetWeek } },
        };
        var value = new
        {
            version = 1, sentAt = checkedAtMs,
            groups = new object[]
            {
                new { kind = "claude", name = "클로드", accounts = new[] { Row("claude-1", "기본 클로드", true, 29) } },
                new { kind = "gpt", name = "지피티", accounts = new[] { Row("gpt-1", "PREVATE", true, 57), Row("gpt-2", "PUBLIC 1", gpt2Enabled, 51), Row("gpt-3", "PUBLIC 2", true, 15) } },
                new { kind = "grok", name = "그록", accounts = new[] { Row("grok-1", "기본 그록", true, 1) } },
            },
            deepseek = new { state = "ready", balances = new[] { new { currency = "USD", total = "5.20" } } },
        };
        return JsonSerializer.Serialize(new { command = "chatterbox-accounts", value });
    }

    static HostedAccountsSnapshot ParseSnapshot(string line)
    {
        if (!HostedProtocol.TryParseCommand(line, out var command, out var error)) throw new InvalidOperationException(error);
        if (!ChatterBoxAccounts.TryParse(command.Data!.Value, out var snapshot, out error)) throw new InvalidOperationException(error);
        return snapshot;
    }

    static void TestChatterBoxSnapshotParsing()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var line = ChatterBoxAccountsLine(now);
        HostedCheck("account snapshot fits the hosted line limit", System.Text.Encoding.UTF8.GetByteCount(line) < HostedProtocol.MaxLineBytes);
        HostedCheck("accounts command parses with an object value", HostedProtocol.TryParseCommand(line, out var command, out _) && command.Name == "chatterbox-accounts" && command.Data is { ValueKind: JsonValueKind.Object });
        HostedCheck("accounts command needs an object", !HostedProtocol.TryParseCommand("{\"command\":\"chatterbox-accounts\",\"value\":true}", out _, out var objectError) && objectError.Contains("object", StringComparison.Ordinal));

        var snapshot = ParseSnapshot(line);
        HostedCheck("accounts keep ChatterBox's order and labels", snapshot.Accounts.Select(a => $"{a.Provider}:{a.Id}:{a.Label}").SequenceEqual(
            ["Claude:claude-1:기본 클로드", "Codex:gpt-1:PREVATE", "Codex:gpt-2:PUBLIC 1", "Codex:gpt-3:PUBLIC 2", "Grok:grok-1:기본 그록"]));
        HostedCheck("disabled flag and check time are kept", !snapshot.Accounts[2].Enabled && snapshot.Accounts[0].CheckedAt?.ToUnixTimeMilliseconds() == now);
        var usage = ChatterBoxAccounts.Usage(snapshot.Accounts[3])!;
        HostedCheck("windows become the presenters' usage keys", usage.Windows.Select(w => $"{w.Key}:{w.UsedPercent}").SequenceEqual(["five_hour:10", "seven_day:15"]) && usage.Source == "ChatterBox");
        HostedCheck("DeepSeek balance is read", snapshot.DeepSeek is { State: "ready", Balance: "5.20 USD" });

        var wrongVersion = line.Replace("\"version\":1", "\"version\":2");
        HostedCheck("another major version is refused", HostedProtocol.TryParseCommand(wrongVersion, out var v2, out _) &&
            !ChatterBoxAccounts.TryParse(v2.Data!.Value, out _, out var versionError) && versionError.Contains("version", StringComparison.Ordinal));
        var badId = line.Replace("\"accountId\":\"gpt-3\"", "\"accountId\":\"../gpt\"");
        HostedCheck("an invalid account id is refused", HostedProtocol.TryParseCommand(badId, out var bad, out _) && !ChatterBoxAccounts.TryParse(bad.Data!.Value, out _, out _));
        var withError = line.Replace("\"display\":null,", "\"error\":\"failed\",\"display\":null,");
        HostedCheck("a failed reading has no usage", ChatterBoxAccounts.Usage(ParseSnapshot(withError).Accounts[0]) == null);
    }

    static void TestHostedCards()
    {
        var now = DateTimeOffset.Now;
        var snapshot = ParseSnapshot(ChatterBoxAccountsLine(now.ToUnixTimeMilliseconds()));
        var local = new ProviderCard { Name = "Codex", Accent = Palette.Codex, AppStatus = "CLI 실행 중", AppStatusBrush = Palette.Ok, Footnote = "", SessionsHeader = "스레드 2", AppRunning = true, WorkingCount = 1 };
        var codex = Presenter.Hosted(local, snapshot.For("Codex"), received: true, now);
        HostedCheck("one slot per ChatterBox account, in its order, labelled", codex.AccountSlots.Select(s => s.Account).SequenceEqual(["PREVATE", "PUBLIC 1", "PUBLIC 2"]));
        HostedCheck("the first account is the card's main account", codex.Account == "PREVATE" && codex.MaxUsed is > 56 and < 58);
        HostedCheck("an account excluded in ChatterBox is dimmed and cannot be dragged", codex.AccountSlots[1] is { IsDimmed: true, CanDrag: false } && !codex.AccountSlots[0].IsDimmed);
        HostedCheck("local activity is kept", codex.AppStatus == "CLI 실행 중" && codex.WorkingCount == 1 && codex.SessionsHeader == "스레드 2");
        HostedCheck("remaining shows the ChatterBox reading", Math.Abs(codex.AccountSlots[2].Remaining - 85) < 0.01 || Math.Abs(codex.AccountSlots[2].Remaining - 90) < 0.01);

        var old = ParseSnapshot(ChatterBoxAccountsLine((now - TimeSpan.FromHours(2)).ToUnixTimeMilliseconds(), gpt2Enabled: true));
        var stale = Presenter.Hosted(local, old.For("Codex"), received: true, now);
        HostedCheck("an old reading is marked stale and dimmed", stale.Status.KindAt(now) == StatusKind.Stale && stale.AccountSlots.All(s => s.IsDimmed));

        var waiting = Presenter.Hosted(local, [], received: false, now);
        HostedCheck("before ChatterBox sends anything nothing is guessed", waiting.AccountSlots.Count == 0 && waiting.UsageEmpty?.Contains("ChatterBox", StringComparison.Ordinal) == true && waiting.MaxUsed == null);

        var cards = Presenter.HostedAccountCards(snapshot, now);
        HostedCheck("accounts panel lists ChatterBox accounts then the DeepSeek balance", cards.Select(c => c.DisplayName).SequenceEqual(["기본 클로드", "PREVATE", "PUBLIC 1", "PUBLIC 2", "기본 그록", "5.20 USD"]));
        HostedCheck("panel has no login or remove actions in hosted mode", cards.All(c => !c.IsSecondary && c.LoginCommand == null && c.RemoveCommand == null));
        HostedCheck("panel dims the excluded account", cards[2].Dimmed && cards[2].Status.Contains("배정 제외", StringComparison.Ordinal) && !cards[1].Dimmed);
    }
}
