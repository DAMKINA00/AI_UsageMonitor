using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestResetAnnouncement()
    {
        var now = DateTimeOffset.UtcNow;
        var firstReset = now.AddHours(1);
        var secondReset = now.AddHours(2);
        var thirdReset = now.AddHours(3);

        var first = new MoodTracker("claude");
        Check("first full observation is not a reset", first.Update(ResetCard("MAIN", "seven_day", 0, now, firstReset)).ResetAnnouncement == null,
            "first zero sample announced");

        var consumed = first.Update(ResetCard("MAIN", "seven_day", 25, now.AddMinutes(1), firstReset));
        Check("usage increase still reports a delta", consumed.UsageDelta == 25 && !consumed.Reset,
            $"delta={consumed.UsageDelta}, reset={consumed.Reset}");

        var recovered = first.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(2), secondReset));
        Check("fresh visible zero after consumption announces once", recovered.Reset && recovered.Mood == Mood.Happy &&
            recovered.ResetAnnouncement == "주간 한도 100% 회복!\n다시 일할 준비 됐어요.", recovered.ResetAnnouncement ?? "missing");
        var duplicate = first.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(3), secondReset));
        Check("duplicate zero does not reannounce", !duplicate.Reset && duplicate.ResetAnnouncement == null && duplicate.Mood != Mood.Happy,
            $"reset={duplicate.Reset}, mood={duplicate.Mood}");

        var nextConsumed = first.Update(ResetCard("MAIN", "seven_day", 10, now.AddMinutes(4), secondReset));
        var nextRecovered = first.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(5), thirdReset));
        Check("a later reset cycle can announce", nextConsumed.UsageDelta == 10 && nextRecovered.Reset,
            $"delta={nextConsumed.UsageDelta}, reset={nextRecovered.Reset}");

        var partial = new MoodTracker("claude");
        partial.Update(ResetCard("MAIN", "seven_day", 90, now, firstReset));
        var partialDrop = partial.Update(ResetCard("MAIN", "seven_day", 20, now.AddMinutes(1), secondReset));
        Check("partial drop is not a reset", !partialDrop.Reset && partialDrop.ResetAnnouncement == null,
            $"reset={partialDrop.Reset}");

        var invalid = new MoodTracker("claude");
        invalid.Update(ResetCard("MAIN", "seven_day", 40, now, firstReset));
        Check("failed sample is not a reset", !invalid.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(1), secondReset,
            state: StatusKind.Failed)).Reset,
            "failed sample announced");
        Check("stale sample is not a reset", !invalid.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(2), secondReset,
            statusAsOf: now.AddMinutes(-30))).Reset,
            "stale sample announced");
        Check("pending sample is not a reset", !invalid.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(3), now.AddMinutes(-1),
            pending: true)).Reset,
            "pending sample announced");

        var changedAccount = new MoodTracker("claude");
        changedAccount.Update(ResetCard("MAIN", "seven_day", 35, now, firstReset));
        Check("account switch does not fabricate a reset", !changedAccount.Update(ResetCard("OTHER", "seven_day", 0,
            now.AddMinutes(1), secondReset)).Reset, "account switch announced");

        var changedQuota = new MoodTracker("claude");
        changedQuota.Update(ResetCard("MAIN", "seven_day", 35, now, firstReset));
        Check("displayed quota switch does not fabricate a reset", !changedQuota.Update(ResetCard("MAIN", "monthly", 0,
            now.AddMinutes(1), secondReset, label: "월간 한도")).Reset, "quota switch announced");

        var outOfOrder = new MoodTracker("claude");
        outOfOrder.Update(ResetCard("MAIN", "seven_day", 35, now.AddMinutes(2), firstReset));
        Check("out of order zero does not announce", !outOfOrder.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(1), secondReset)).Reset,
            "older sample announced");

        var working = new MoodTracker("claude");
        working.Update(ResetCard("MAIN", "seven_day", 35, now, firstReset));
        var workingRecovery = working.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(1), secondReset, workingCount: 1));
        Check("working state keeps precedence during announcement", workingRecovery.Reset && workingRecovery.Mood == Mood.Working,
            $"reset={workingRecovery.Reset}, mood={workingRecovery.Mood}");

        var waiting = new MoodTracker("claude");
        waiting.Update(ResetCard("MAIN", "seven_day", 35, now, firstReset));
        var waitingRecovery = waiting.Update(ResetCard("MAIN", "seven_day", 0, now.AddMinutes(1), secondReset, freshNeedsInputCount: 1));
        Check("waiting state keeps precedence during announcement", waitingRecovery.Reset && waitingRecovery.Mood == Mood.Waiting,
            $"reset={waitingRecovery.Reset}, mood={waitingRecovery.Mood}");
    }

    static ProviderCard ResetCard(string account, string key, double used, DateTimeOffset asOf, DateTimeOffset resetAt,
        StatusKind state = StatusKind.None, DateTimeOffset? statusAsOf = null, bool pending = false,
        string? label = null, int workingCount = 0, int freshNeedsInputCount = 0)
    {
        label ??= key == "seven_day" ? "주간 한도" : key == "monthly" ? "월간 한도" : key;
        var window = new UsageWindow(key, label, used, resetAt);
        var row = Presenter.DisplayUsage([window], asOf).Single();
        return new ProviderCard
        {
            Name = "Claude", Account = account, Accent = Palette.Claude,
            AppStatus = "fixture", AppStatusBrush = Palette.Muted,
            Footnote = "fixture", SessionsHeader = "fixture", Usage = [row], AccountUsage = [row],
            RawWindows = [window], MaxUsed = used, UsageResetPending = pending,
            WorkingCount = workingCount, FreshNeedsInputCount = freshNeedsInputCount,
            Status = new StatusChip(state, statusAsOf ?? asOf, TimeSpan.FromMinutes(5), false),
        };
    }
}
