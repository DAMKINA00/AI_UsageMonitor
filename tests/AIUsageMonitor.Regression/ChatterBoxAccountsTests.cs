using System.IO;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void RunChatterBoxAccountTests()
    {
        Run("ChatterBox account snapshot command and parsing", TestChatterBoxSnapshotParsing);
        Run("Antigravity hosted account, quota, freshness and display", TestHostedAntigravity);
        Run("hosted cards follow ChatterBox accounts only", TestHostedCards);
        Run("hosted window choice matches ChatterBox", TestHostedWindowChoice);
        Run("hosted alerts: account id keys, fresh readings only", TestHostedAlerts);
        Run("hosted slots take a newer as-of time when the numbers are unchanged", TestHostedSlotFreshness);
    }

    static void TestHostedAntigravity()
    {
        var now = DateTimeOffset.Now;
        var line = JsonSerializer.SerializeToElement(new { version = 1, sentAt = now.ToUnixTimeMilliseconds(), groups = new[] {
            new { kind = "gemini", name = "안티그래비티", accounts = new[] { new { accountId = "gemini-1", label = "기본 계정", enabled = true, isDefault = true,
                authStatus = "authenticated", checkedAt = now.ToUnixTimeMilliseconds(), windows = new { weekly = new { usedPercent = 53, resetsAt = now.AddDays(6).ToUnixTimeSeconds() } } } } }
        }});
        HostedCheck("Antigravity snapshot parses", ChatterBoxAccounts.TryParse(line, out var snapshot, out _));
        HostedCheck("Antigravity keeps its native CLI account id", snapshot.For("Antigravity").Single().Id == "gemini-1");
        var card = Presenter.Hosted(Presenter.Antigravity(), snapshot.For("Antigravity"), true, now);
        HostedCheck("Antigravity weekly reading is 47 percent remaining", card.AccountSlots.Single().RemainingText == "47%");
        HostedCheck("Antigravity uses the Gemini accent", Equals(card.AccountSlots.Single().Accent, Palette.Gemini));
        var vm = new MainViewModel();
        vm.Update(Presenter.Antigravity(), Presenter.Antigravity(), null, "", card);
        HostedCheck("Antigravity card reaches the view model", vm.Gemini?.Name == "Antigravity");
        HostedCheck("Antigravity appears in the hosted accounts panel", Presenter.HostedAccountCards(snapshot).Single().ProviderName.Contains("Antigravity"));
        var lowerFive = snapshot.For("Antigravity").Single() with { FiveHour = new HostedWindow(70, now.AddHours(2)), Weekly = new HostedWindow(20, now.AddDays(6)) };
        var fiveCard = Presenter.Hosted(Presenter.Antigravity(), [lowerFive], true, now);
        HostedCheck("Antigravity shows lower 5-hour remaining even above 10 percent", fiveCard.AccountSlots.Single().RemainingText == "30%" && fiveCard.Usage.Single().Label == "5시간 한도");
        HostedCheck("Antigravity account detail shows just the lower window", fiveCard.AccountUsage.Count == 1);
        var lowerWeek = lowerFive with { FiveHour = new HostedWindow(20, now.AddHours(2)), Weekly = new HostedWindow(80, now.AddDays(6)) };
        HostedCheck("Antigravity shows lower weekly remaining", Presenter.Hosted(Presenter.Antigravity(), [lowerWeek], true, now).AccountSlots.Single().RemainingText == "20%");
    }

    /// <summary>
    /// Regression: a Codex account whose numbers did not change kept the first reading's as-of time on screen
    /// (the card and its slots were kept by signature) and went dim after 15 minutes although ChatterBox kept
    /// sending fresh readings.
    /// </summary>
    static void TestHostedSlotFreshness()
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddDays(3);
        var local = new ProviderCard { Name = "Codex", Accent = Palette.Codex, AppStatus = "CLI 실행 중", AppStatusBrush = Palette.Ok, Footnote = "", SessionsHeader = "", AppRunning = true };
        HostedAccount[] Snapshot(DateTimeOffset checkedAt) =>
            [Account("gpt-1", "PRIVATE", checkedAt, weekly: new HostedWindow(57, reset)), Account("gpt-3", "PUBLIC", checkedAt, weekly: new HostedWindow(16, reset))];

        var first = Presenter.Hosted(local, Snapshot(now - TimeSpan.FromMinutes(40)), received: true, now);
        var vm = new MainViewModel();
        var claude = new ProviderCard { Name = "Claude", Accent = Palette.Claude, AppStatus = "", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "" };
        vm.Update(claude, first, null, "");
        HostedCheck("the old reading starts out stale", vm.Codex!.AccountSlots.All(s => s.IsDimmed));

        int replaced = 0;
        vm.PropertyChanged += (_, e) => replaced += e.PropertyName == nameof(MainViewModel.Codex) ? 1 : 0;
        var second = Presenter.Hosted(local, Snapshot(now), received: true, now);
        HostedCheck("same numbers keep the same card (no replacement, tooltips stay open)", second.Signature() == first.Signature());
        vm.Update(vm.Claude!, second, null, "");
        HostedCheck("the card was kept", replaced == 0 && ReferenceEquals(vm.Codex, first));
        HostedCheck("kept slots take the newer as-of time and are not stale",
            vm.Codex!.AccountSlots.All(s => !s.IsDimmed && s.Freshness?.AsOf is { } at && Math.Abs((at - now).TotalSeconds) < 1));
        HostedCheck("the slot tooltip no longer says the value is old", vm.Codex.AccountSlots.All(s => !s.ToolTipText.Contains("오래되었습니다", StringComparison.Ordinal)));

        // The main window keeps the slots it shows when the layout is unchanged: they must refresh too.
        var shown = Presenter.Hosted(local, Snapshot(now - TimeSpan.FromMinutes(40)), received: true, now).AccountSlots.ToArray();
        var newer = Presenter.Hosted(local, Snapshot(now), received: true, now).AccountSlots.ToArray();
        HostedCheck("shown slots start stale", shown.All(s => s.IsDimmed));
        MainWindow.RefreshShownSlots(shown, newer);
        HostedCheck("shown slots take the newer as-of time", shown.All(s => !s.IsDimmed));
        // A slot for a different account at that position is left alone.
        var other = Presenter.Hosted(local, [Account("gpt-2", "OTHER", now - TimeSpan.FromMinutes(40), weekly: new HostedWindow(57, reset))], received: true, now).AccountSlots.ToArray();
        MainWindow.RefreshShownSlots(other, newer);
        HostedCheck("a different account is not overwritten", other[0].IsDimmed);

        TestHostedSlotFreshnessAfterReplacement(now, reset);
    }

    /// <summary>
    /// Review 873: card A shown → card B replaces it (only the app status changed, same slots) → card B kept
    /// on the next reading. The slots on screen must stay the card's current slots with the newest as-of time.
    /// </summary>
    static void TestHostedSlotFreshnessAfterReplacement(DateTimeOffset now, DateTimeOffset reset)
    {
        HostedAccount[] Snapshot(DateTimeOffset checkedAt) =>
            [Account("gpt-1", "PRIVATE", checkedAt, weekly: new HostedWindow(57, reset)), Account("gpt-3", "PUBLIC", checkedAt, weekly: new HostedWindow(16, reset))];
        ProviderCard Local(string status) => new() { Name = "Codex", Accent = Palette.Codex, AppStatus = status, AppStatusBrush = Palette.Ok, Footnote = "", SessionsHeader = "", AppRunning = true };
        var claude = new ProviderCard { Name = "Claude", Accent = Palette.Claude, AppStatus = "", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "" };
        var vm = new MainViewModel();
        AccountUsageSlot[] shown = [];
        string shownSignature = "";
        // The main window's rule (MainWindow.RefreshPlatforms): new objects only when the slot layout changes.
        void Show()
        {
            var slots = vm.Codex!.AccountSlots.ToArray();
            var signature = string.Join('\n', slots.Select(s => s.Signature));
            if (signature != shownSignature) { shown = slots; shownSignature = signature; }
            else MainWindow.RefreshShownSlots(shown, slots);
        }

        var a = Presenter.Hosted(Local("앱 실행 중"), Snapshot(now - TimeSpan.FromMinutes(40)), received: true, now);
        vm.Update(claude, a, null, ""); Show();
        var b = Presenter.Hosted(Local("앱 실행 안 됨"), Snapshot(now - TimeSpan.FromMinutes(20)), received: true, now);
        vm.Update(claude, b, null, ""); Show();
        HostedCheck("a status-only change replaces the card", ReferenceEquals(vm.Codex, b));
        HostedCheck("the replaced card keeps the unchanged slot objects that are on screen", shown.Zip(vm.Codex!.AccountSlots).All(p => ReferenceEquals(p.First, p.Second)));
        var c = Presenter.Hosted(Local("앱 실행 안 됨"), Snapshot(now), received: true, now);
        vm.Update(claude, c, null, ""); Show();
        HostedCheck("the next reading keeps the card", ReferenceEquals(vm.Codex, b));
        HostedCheck("screen slots and card slots are the same objects with the newest as-of time",
            shown.Zip(vm.Codex!.AccountSlots).All(p => ReferenceEquals(p.First, p.Second) && p.First.Freshness?.AsOf is { } at && Math.Abs((at - now).TotalSeconds) < 1));
        HostedCheck("no slot on screen is stale after the replace-then-keep sequence", shown.All(s => !s.IsDimmed));
        // A changed slot (new usage number) is a new object, not an old one carried over.
        var d = Presenter.Hosted(Local("앱 실행 중"), [Account("gpt-1", "PRIVATE", now, weekly: new HostedWindow(58, reset)), Account("gpt-3", "PUBLIC", now, weekly: new HostedWindow(16, reset))], received: true, now);
        var keptSecond = vm.Codex!.AccountSlots[1];
        vm.Update(claude, d, null, "");
        HostedCheck("a changed slot is new and an unchanged one is carried over", !ReferenceEquals(vm.Codex!.AccountSlots[0], shown[0]) && ReferenceEquals(vm.Codex.AccountSlots[1], keptSecond));
    }

    static HostedAccount Account(string id, string label, DateTimeOffset? checkedAt, HostedWindow? five = null, HostedWindow? weekly = null, bool enabled = true) =>
        new(id, id.StartsWith("gpt", StringComparison.Ordinal) ? "Codex" : id.StartsWith("grok", StringComparison.Ordinal) ? "Grok" : "Claude",
            label, enabled, id.EndsWith("-1", StringComparison.Ordinal), "authenticated", 0, checkedAt, null, five, weekly, null);

    static void TestHostedWindowChoice()
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddHours(2);
        // ChatterBox (shared/usage.ts pickUsage): 5-hour when <= 10% left, else weekly, else monthly, else 5-hour.
        var fiveOnly = Presenter.HostedDisplayUsage([new UsageWindow("five_hour", "", 30, reset)], now);
        HostedCheck("an account with only a 5-hour window shows it (70% left)", fiveOnly.Count == 1 && fiveOnly[0].Label == "5시간 한도" && Math.Abs(fiveOnly[0].Value - 30) < 0.01);
        var nearlyUsed = Presenter.HostedDisplayUsage([new UsageWindow("five_hour", "", 92, reset), new UsageWindow("seven_day", "", 40, reset)], now);
        HostedCheck("a nearly used 5-hour window wins over weekly", nearlyUsed.Single().Label == "5시간 한도");
        var weekly = Presenter.HostedDisplayUsage([new UsageWindow("five_hour", "", 89, reset), new UsageWindow("seven_day", "", 40, reset)], now);
        HostedCheck("otherwise weekly is shown", weekly.Single().Label == "주간 한도" && Math.Abs(weekly[0].Value - 40) < 0.01);
        HostedCheck("no window, no row", Presenter.HostedDisplayUsage([], now).Count == 0);

        var local = new ProviderCard { Name = "Claude", Accent = Palette.Claude, AppStatus = "", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "" };
        var card = Presenter.Hosted(local, [Account("claude-2", "5시간만", now, five: new HostedWindow(30, reset))], received: true, now);
        HostedCheck("the slot of a 5-hour-only account shows 70% left", Math.Abs(card.AccountSlots.Single().Remaining - 70) < 0.01 && card.UsageEmpty == null);
    }

    static void TestHostedAlerts()
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddDays(2);
        var hot = new HostedWindow(96, reset);
        var snapshot = new HostedAccountsSnapshot(now, [
            Account("gpt-2", "공용", now.AddMinutes(-1), weekly: hot),
            Account("gpt-3", "공용", now.AddMinutes(-1), weekly: hot),
            Account("gpt-4", "오래됨", now.AddMinutes(-20), weekly: hot),
            Account("gpt-5", "제외", now.AddMinutes(-1), weekly: hot, enabled: false),
            Account("gpt-6", "미래 시각", now.AddHours(1), weekly: hot),
        ], null);
        var targets = Presenter.HostedAlertTargets(snapshot, now);
        HostedCheck("alert targets are fresh, assigned accounts keyed by id", targets.Select(t => t.StateKey).SequenceEqual(["chatterbox:gpt-2", "chatterbox:gpt-3"]));
        HostedCheck("the label is only for display", targets.All(t => t.Display == "Codex · 공용"));

        var previous = Environment.GetEnvironmentVariable(Paths.DataDirVariable);
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Alerts-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(Paths.DataDirVariable, dir);
        try
        {
            var settings = new AppSettings { UsageAlerts = true };
            var shown = new List<string>();
            using var tray = new Tray(settings, visible: false, notification: (title, _, _) => shown.Add(title));
            var alerts = new Alerts(settings, tray);
            foreach (var t in targets) alerts.CheckUsage(t.Display, t.Windows, t.StateKey);
            HostedCheck("two accounts with the same label both alert", shown.Count == 2);
            var renamed = new HostedAccountsSnapshot(now, [Account("gpt-2", "새 이름", now.AddMinutes(-1), weekly: hot)], null);
            foreach (var t in Presenter.HostedAlertTargets(renamed, now)) alerts.CheckUsage(t.Display, t.Windows, t.StateKey);
            HostedCheck("renaming an account does not alert again", shown.Count == 2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Paths.DataDirVariable, previous);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
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
