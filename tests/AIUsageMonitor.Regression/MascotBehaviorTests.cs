using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestMascotBehavior()
    {
        var start = DateTimeOffset.UtcNow;

        // MaxUsed is deliberately different: sleep follows the first visible row's remaining value.
        var visibleThreshold = MascotCard(start, used: 75, maxUsed: 95);
        Check("mascot threshold is the visible row's 25% remaining value",
            visibleThreshold.Usage.Count > 0 && visibleThreshold.Usage[0].Remaining == 25 &&
            visibleThreshold.Usage[0].RemainText.Contains("25%", StringComparison.Ordinal),
            DescribeMascotCard(visibleThreshold));

        var aboveThreshold = VirtualMoods(
            at => MascotCard(at, used: 74), start, TimeSpan.FromMinutes(40));
        Check("more than 25% remaining stays idle for 40 virtual minutes",
            aboveThreshold.All(x => x.Mood == Mood.Idle), DescribeMoods(aboveThreshold));

        var noApp = VirtualMoods(
            at => MascotCard(at, used: 50, appRunning: false), start, TimeSpan.FromMinutes(40));
        var oldActivity = VirtualMoods(
            at => MascotCard(at, used: 50, lastActivity: at.AddMinutes(-40)), start, TimeSpan.FromMinutes(40));
        var ordinaryIdle = new MoodTracker("claude").Update(MascotCard(start, used: 50), start);
        Check("no work defaults to idle, including stopped apps and old activity",
            ordinaryIdle.Mood == Mood.Idle && noApp.All(x => x.Mood == Mood.Idle) &&
            oldActivity.All(x => x.Mood == Mood.Idle),
            $"ordinary={ordinaryIdle.Mood}; noApp={DescribeMoods(noApp)}; old={DescribeMoods(oldActivity)}");

        var thresholdMoods = VirtualMoods(
            at => MascotCard(at, used: 75, maxUsed: 95), start, TimeSpan.FromMinutes(40));
        var thresholdSleep = SleepSpans(thresholdMoods);
        Check("exactly 25% remaining sleeps after a fresh 8-15 minute wait",
            thresholdSleep.Count > 0 &&
            WaitFromStart(thresholdSleep[0], start) is >= 8 * 60 and <= 15 * 60 + 1 &&
            thresholdSleep.All(x => (x.End - x.Start).TotalSeconds is >= 30 and <= 45) &&
            thresholdSleep.Count <= 4 &&
            thresholdSleep.Zip(thresholdSleep.Skip(1))
                .All(pair => (pair.Second.Start - pair.First.End).TotalSeconds is >= 8 * 60 and <= 15 * 60 + 2),
            DescribeSleep(thresholdSleep, start));

        var almostExhausted = VirtualMoods(
            at => MascotCard(at, used: 99), start, TimeSpan.FromMinutes(40));
        var fullyUsed = VirtualMoods(
            at => MascotCard(at, used: 100), start, TimeSpan.FromMinutes(40));
        Check("only 100% used is excluded as exhausted",
            almostExhausted.Any(x => x.Mood == Mood.Sleeping) && almostExhausted.All(x => x.Mood != Mood.Panic) &&
            fullyUsed.All(x => x.Mood == Mood.Panic),
            $"99={DescribeMoods(almostExhausted)}; 100={DescribeMoods(fullyUsed)}");

        var unknown = VirtualMoods(
            at => MascotCard(at, used: 75, unknown: true), start, TimeSpan.FromMinutes(40));
        var stale = VirtualMoods(
            at => MascotCard(at, used: 75, statusAsOf: at.AddMinutes(-6)), start, TimeSpan.FromMinutes(40));
        var rowPending = VirtualMoods(
            at => MascotCard(at, used: 75, resetPending: true), start, TimeSpan.FromMinutes(40));
        var cardPending = VirtualMoods(
            at => MascotCard(at, used: 75, usageResetPending: true), start, TimeSpan.FromMinutes(40));
        Check("unknown, stale, and reset-pending data never sleeps",
            unknown.All(x => x.Mood == Mood.Idle) && stale.All(x => x.Mood == Mood.Idle) &&
            rowPending.All(x => x.Mood == Mood.Idle) && cardPending.All(x => x.Mood == Mood.Idle),
            $"unknown={DescribeMoods(unknown)}; stale={DescribeMoods(stale)}; row={DescribeMoods(rowPending)}; card={DescribeMoods(cardPending)}");

        var sleepingTracker = new MoodTracker("claude");
        DateTimeOffset? sleepingAt = null;
        for (var second = 0; second <= 15 * 60; second++)
        {
            var at = start.AddSeconds(second);
            if (sleepingTracker.Update(MascotCard(at, used: 75), at).Mood == Mood.Sleeping)
            {
                sleepingAt = at;
                break;
            }
        }
        Check("a sleeping mascot wakes immediately for work or approval",
            sleepingAt is { } asleep &&
            sleepingTracker.Update(MascotCard(asleep.AddSeconds(1), used: 50, workingCount: 1), asleep.AddSeconds(1)).Mood == Mood.Working &&
            sleepingTracker.Update(MascotCard(asleep.AddSeconds(2), used: 75, freshNeedsInputCount: 1), asleep.AddSeconds(2)).Mood == Mood.Waiting,
            sleepingAt?.ToString("O") ?? "sleep was not reached");

        var hard = new MoodTracker("claude").Update(
            MascotCard(start, used: 95, workingCount: 1), start);
        Check("95% used while working is WorkingHard, not Panic",
            hard.Mood == Mood.WorkingHard && hard.Mood != Mood.Panic, hard.Mood.ToString());

        TestMascotDialogueTiming();
    }

    static void TestMascotDialogueTiming()
    {
        var stage = new CharacterStage();
        var show = PrivateMethod<CharacterStage>("ShowBubble");
        var hide = PrivateMethod<CharacterStage>("HideBubble");
        var next = PrivateField<CharacterStage>("_nextBubbleAt");
        var time = PrivateField<CharacterStage>("_t");
        var shown = PrivateField<CharacterStage>("_bubbleShown");
        var frame = PrivateMethod<CharacterStage>("Frame");

        stage.SetMood(Mood.Sleeping, ["졸려"], ["sleep"]);
        show.Invoke(stage, ["졸려"]);
        hide.Invoke(stage, null);
        var sleepingGap = (double)next.GetValue(stage)! - (double)time.GetValue(stage)!;

        stage.SetMood(Mood.Idle, ["대기"], ["idle"]);
        show.Invoke(stage, ["대기"]);
        hide.Invoke(stage, null);
        var ordinaryGap = (double)next.GetValue(stage)! - (double)time.GetValue(stage)!;
        Check("sleep dialogue rests 25-45 seconds and ordinary dialogue 3-12",
            sleepingGap is >= 25 and <= 45 && ordinaryGap is >= 3 and <= 12,
            $"sleep={sleepingGap:0.###}; ordinary={ordinaryGap:0.###}");

        stage.SetMood(Mood.Sleeping, ["졸려"], ["sleep"]);
        show.Invoke(stage, ["졸려"]);
        hide.Invoke(stage, null);
        var deadline = (double)next.GetValue(stage)!;
        for (var i = 0; i < 30; i++) frame.Invoke(stage, null);
        stage.SetMood(Mood.Sleeping, ["졸려"], ["sleep"]);
        Check("same sleeping mood update does not bypass the dialogue deadline",
            !(bool)shown.GetValue(stage)! && Math.Abs((double)next.GetValue(stage)! - deadline) < 0.000001,
            $"shown={shown.GetValue(stage)}; next={next.GetValue(stage)}; deadline={deadline}");
    }

    static ProviderCard MascotCard(DateTimeOffset at, double used, double? maxUsed = null,
        bool appRunning = true, DateTimeOffset? lastActivity = null, StatusKind status = StatusKind.None,
        DateTimeOffset? statusAsOf = null, bool unknown = false, bool resetPending = false,
        bool usageResetPending = false, int workingCount = 0, int freshNeedsInputCount = 0,
        bool limitReached = false)
    {
        var resetAt = resetPending ? at.AddMinutes(-1) : at.AddHours(1);
        const string label = "주간 한도";
        var window = new UsageWindow("seven_day", label, used, resetAt);
        var row = unknown
            ? new UsageRow
            {
                Label = label, Value = used, UsedText = "확인 중", RemainText = "",
                ResetText = "확인 중", ResetShort = "확인 중", IsUnknown = true,
                LevelBrush = Palette.Muted,
            }
            : Presenter.DisplayUsage([window], at).Single();
        return new ProviderCard
        {
            Name = "Claude", Accent = Palette.Claude, AppStatus = "fixture", AppStatusBrush = Palette.Muted,
            Footnote = "fixture", SessionsHeader = "fixture", Usage = [row], AccountUsage = [row],
            MaxUsed = maxUsed ?? used, RawWindows = [window], WorkingCount = workingCount,
            FreshNeedsInputCount = freshNeedsInputCount, AppRunning = appRunning, LastActivity = lastActivity,
            LimitReached = limitReached, UsageResetPending = usageResetPending,
            SoonestReset = resetAt,
            Status = new StatusChip(status, statusAsOf ?? at, TimeSpan.FromMinutes(5), false),
        };
    }

    static List<(DateTimeOffset At, Mood Mood)> VirtualMoods(
        Func<DateTimeOffset, ProviderCard> card, DateTimeOffset start, TimeSpan duration)
    {
        var tracker = new MoodTracker("claude");
        var samples = new List<(DateTimeOffset At, Mood Mood)>();
        for (var second = 0; second <= (int)duration.TotalSeconds; second++)
        {
            var at = start.AddSeconds(second);
            samples.Add((at, tracker.Update(card(at), at).Mood));
        }
        return samples;
    }

    static List<(DateTimeOffset Start, DateTimeOffset End)> SleepSpans(
        IReadOnlyList<(DateTimeOffset At, Mood Mood)> samples)
    {
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        DateTimeOffset? started = null;
        foreach (var sample in samples)
        {
            if (sample.Mood == Mood.Sleeping)
            {
                started ??= sample.At;
            }
            else if (started is { } begin)
            {
                spans.Add((begin, sample.At));
                started = null;
            }
        }
        if (started is { } tail && samples.Count > 0)
            spans.Add((tail, samples[^1].At.AddSeconds(1)));
        return spans;
    }

    static double WaitFromStart((DateTimeOffset Start, DateTimeOffset End) span, DateTimeOffset start) =>
        (span.Start - start).TotalSeconds;

    static string DescribeMoods(IEnumerable<(DateTimeOffset At, Mood Mood)> moods) =>
        string.Join(", ", moods.GroupBy(x => x.Mood).Select(g => $"{g.Key}={g.Count()}"));

    static string DescribeSleep(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans, DateTimeOffset start) =>
        string.Join(", ", spans.Select(x => $"{(x.Start - start).TotalSeconds:0}s/{(x.End - x.Start).TotalSeconds:0}s"));

    static string DescribeMascotCard(ProviderCard card) =>
        $"used={card.MaxUsed}; row={card.Usage.FirstOrDefault()?.Value}; remaining={card.Usage.FirstOrDefault()?.Remaining}; remainText={card.Usage.FirstOrDefault()?.RemainText}";
}
