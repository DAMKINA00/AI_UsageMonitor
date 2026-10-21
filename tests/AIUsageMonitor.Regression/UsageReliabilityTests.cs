using System.Windows;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestUsageReliability()
    {
        var now = DateTimeOffset.UtcNow;
        var future = now.AddDays(2);
        using var service = new SecondaryAccountService(new AppSettings
        {
            SecondaryAccounts = [new() { Provider = AccountProvider.Claude }, new() { Provider = AccountProvider.Claude }],
        });
        var account = service.Accounts[0];
        var saved = Snapshot("fixture", now, Window("seven_day", 35, future));
        ApplySecondary(account, true, false, "sub@example.test", saved, null);
        ApplySecondary(service.Accounts[1], true, false, "other@example.test",
            Snapshot("fixture", now, Window("seven_day", 12, future)), null);
        var main = CompactProviderCard("Claude", "MAIN");
        var fresh = AccountUsageSlot.Create(main, service.Accounts)[1];
        Check("fresh account has no warning", !account.HasRefreshError && fresh.UsageOpacity == 1 &&
            fresh.WarningVisibility == Visibility.Collapsed && fresh.RemainingText == "65%", fresh.ToolTipText);

        ApplySecondary(account, false, false, null, null, "fixture offline");
        var stale = AccountUsageSlot.Create(main, service.Accounts)[1];
        Check("failed refresh marks retained usage visibly", account.HasRefreshError && ReferenceEquals(account.Usage, saved) &&
            stale.HasRefreshError && stale.WarningVisibility == Visibility.Visible && stale.UsageOpacity == .45 &&
            stale.RemainingText == "65%" && stale.ToolTipText.Contains("마지막 확인"), stale.ToolTipText);
        Check("failed refresh changes the rendered signature", fresh.Signature != stale.Signature, stale.Signature);
        Check("failed refresh leaves the other account fresh", !service.Accounts[1].HasRefreshError &&
            AccountUsageSlot.Create(main, service.Accounts)[2].RemainingText == "88%", "other account altered");

        ApplySecondary(account, true, false, "sub@example.test",
            Snapshot("fixture", now, Window("seven_day", 36, future)), null);
        var recovered = AccountUsageSlot.Create(main, service.Accounts)[1];
        Check("recovery clears warning and restores opacity", !account.HasRefreshError && recovered.UsageOpacity == 1 &&
            recovered.WarningVisibility == Visibility.Collapsed && recovered.RemainingText == "64%", recovered.ToolTipText);
        ApplySecondary(account, false, true, null, null, null);
        Check("login required remains blank and clears refresh error", !account.HasRefreshError &&
            AccountUsageSlot.Create(main, service.Accounts)[1].IsEmpty, account.Status);

        var expired = Presenter.DisplayUsage([Window("seven_day", 95, now.AddSeconds(-1))], now).Single();
        var pendingSlot = new AccountUsageSlot { Role = "메인", Usage = expired };
        Check("expired usage shows pending instead of a fabricated full balance", expired.IsResetPending && expired.Value == 95 &&
            expired.UsedText == "확인 중" && expired.RemainText == "" && expired.ResetShort == "확인 중" &&
            expired.LevelBrush == Palette.Muted && expired.BarVisibility == Visibility.Hidden &&
            pendingSlot.Remaining == 0 && pendingSlot.RemainingText == "—", expired.ResetText);
        var confirmed = Presenter.DisplayUsage([Window("seven_day", 3, future)], now).Single();
        Check("confirmed new reset restores percentage", !confirmed.IsResetPending && confirmed.BarVisibility == Visibility.Visible &&
            new AccountUsageSlot { Role = "메인", Usage = confirmed }.RemainingText == "97%", confirmed.ResetText);

        using var monitor = new MonitorService(new AppSettings());
        void SetClaude(params UsageWindow[] windows) => SetProperty(monitor, nameof(MonitorService.ClaudeWebGood),
            new ClaudeWebResult { State = ClaudeWebState.Ok, Usage = Snapshot("fixture", now, windows) });
        SetClaude(Window("five_hour", 10, future), Window("seven_day", 20, future), Window("seven_day_FABLE", 100, now.AddHours(1)));
        var card = Presenter.Claude(monitor, new AppSettings(), () => { });
        Check("hidden Fable cannot drive warning percentage or reset time", card.MaxUsed == 20 &&
            card.RawWindows.Count == 2 && card.SoonestReset == future && !card.UsageResetPending,
            $"max={card.MaxUsed}, windows={DescribeWindows(card.RawWindows)}");
        Check("hidden Fable cannot trigger panic", new MoodTracker("claude").Update(card).Mood != Mood.Panic, "Fable panic");
        SetClaude(Window("seven_day_fable", 100, now.AddSeconds(-1)), Window("seven_day", 20, future));
        var expiredFable = Presenter.Claude(monitor, new AppSettings(), () => { });
        Check("expired hidden Fable does not block reset confirmation", !expiredFable.UsageResetPending &&
            expiredFable.MaxUsed == 20, "Fable pending leaked");

        var mood = new MoodTracker("claude");
        SetClaude(Window("five_hour", 95, future), Window("seven_day", 20, future));
        mood.Update(Presenter.Claude(monitor, new AppSettings(), () => { }));
        SetClaude(Window("five_hour", 95, now.AddSeconds(-1)), Window("seven_day", 20, future));
        var pendingCard = Presenter.Claude(monitor, new AppSettings(), () => { });
        var pendingMood = mood.Update(pendingCard);
        Check("expired five-hour falls back to weekly without a reset celebration", pendingCard.UsageResetPending &&
            pendingCard.Usage.Single().Label == "주간 한도" && pendingCard.MaxUsed == 20 && pendingCard.RawWindows.Count == 1 &&
            !pendingMood.Reset && pendingMood.Mood != Mood.Happy, $"reset={pendingMood.Reset}, mood={pendingMood.Mood}");
        SetClaude(Window("five_hour", 0, future), Window("seven_day", 20, future));
        var resetMood = mood.Update(Presenter.Claude(monitor, new AppSettings(), () => { }));
        Check("nonzero displayed recovery does not trigger a reset celebration",
            !resetMood.Reset && resetMood.ResetAnnouncement == null && resetMood.Mood != Mood.Happy,
            $"reset={resetMood.Reset}, mood={resetMood.Mood}");

        SetClaude(Window("seven_day", 95, now.AddSeconds(-1)));
        var allPending = Presenter.Claude(monitor, new AppSettings(), () => { });
        var management = AccountUsageCard.Create(allPending, CompactProviderCard("Codex", "MAIN"), service);
        Check("pending main quota stays visible in account management", allPending.MaxUsed == null && allPending.RawWindows.Count == 0 &&
            management[0].Usage.Count == 2 && management[0].Usage.Any(x => x.Label == "주간 한도" && x.IsResetPending),
            DescribeRows(management[0].Usage));
    }
}
