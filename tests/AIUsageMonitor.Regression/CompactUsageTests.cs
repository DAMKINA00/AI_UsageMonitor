using System.Reflection;
using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCompactUsage()
    {
        TestDisplayUsage();
        TestAccountUsageRows();
        TestAccountSlots();
        TestPaletteLevels();
        TestShortResetAndBubble();
    }

    static void TestDisplayUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(1);
        CheckUsageBoundaries("Claude", "five_hour", "seven_day", now, reset);
        CheckUsageBoundaries("Codex", "codex:w300", "codex:w10080", now, reset);

        foreach (var (name, fiveKey, weeklyKey) in new[]
        {
            ("Claude", "five_hour", "seven_day"),
            ("Codex", "codex:w300", "codex:w10080"),
        })
        {
            var rows = Presenter.DisplayUsage([
                new UsageWindow(fiveKey, "5시간", 100, now.AddMinutes(-1)),
                new UsageWindow(weeklyKey, "주간", 37, reset),
            ], now);
            Check($"{name} expired five-hour limit does not hide weekly limit",
                rows is [{ Label: "주간 한도", Value: 37 }], DescribeRows(rows));
        }

        var fableOnly = new[]
        {
            new UsageWindow("seven_day_fable", "주간 Fable", 42, reset),
        };
        Check("Fable-only usage has no quota row", Presenter.DisplayUsage(fableOnly, now).Count == 0,
            DescribeRows(Presenter.DisplayUsage(fableOnly, now)));

        var original = new[]
        {
            new UsageWindow("five_hour", "5시간", 100, now.AddMinutes(-1)),
            new UsageWindow("seven_day", "주간", 42, reset),
        };
        var before = original.ToArray();
        _ = Presenter.DisplayUsage(original, now);
        Check("DisplayUsage leaves source windows unchanged", original.SequenceEqual(before),
            DescribeWindows(original));
    }

    static void CheckUsageBoundaries(string provider, string fiveKey, string weeklyKey,
        DateTimeOffset now, DateTimeOffset reset)
    {
        foreach (var used in new[] { 89.9, 90d, 100d })
        {
            var rows = Presenter.DisplayUsage([
                new UsageWindow(fiveKey, "5시간", used, reset),
                new UsageWindow(weeklyKey, "주간", 37, reset),
            ], now);
            var expected = used >= 90 ? "5시간 한도" : "주간 한도";
            var expectedValue = used >= 90 ? used : 37;
            var row = rows.SingleOrDefault();
            Check($"{provider} {used:0.0}% boundary selects the right limit",
                row is not null && row.Label == expected && row.Value == expectedValue,
                DescribeRows(rows));
        }
    }

    static void TestAccountUsageRows()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = Presenter.AccountUsageRows([
            new UsageWindow("seven_day", "주간", 100, now.AddDays(2)),
            new UsageWindow("five_hour", "5시간", 0, now.AddHours(1)),
        ], asOf: now);
        Check("account usage rows always order five-hour before weekly and expose 0/100 remaining",
            rows.Select(x => x.Label).SequenceEqual(["5시간 한도", "주간 한도"]) &&
            rows[0] is { Value: 0, Remaining: 100, IsUnknown: false, IsResetPending: false, BarVisibility: Visibility.Visible } &&
            rows[1] is { Value: 100, Remaining: 0, IsUnknown: false, IsResetPending: false, BarVisibility: Visibility.Visible },
            DescribeRows(rows));

        var unknown = Presenter.AccountUsageRows([], asOf: now);
        Check("missing account windows are explicit unknown placeholders",
            unknown is [{ Label: "5시간 한도", IsUnknown: true, IsUnlimited: false, UsedText: "확인 중", BarVisibility: Visibility.Hidden },
                { Label: "주간 한도", IsUnknown: true, IsUnlimited: false, UsedText: "확인 중", BarVisibility: Visibility.Hidden }],
            DescribeRows(unknown));

        var unlimited = Presenter.AccountUsageRows([], fiveHourUnlimited: true, asOf: now);
        Check("only an explicit unlimited flag creates an unlimited five-hour row",
            unlimited[0] is { Label: "5시간 한도", IsUnlimited: true, IsUnknown: false, UsedText: "무제한", Remaining: 100 } &&
            unlimited[1].IsUnknown,
            DescribeRows(unlimited));

        var pending = Presenter.AccountUsageRows([
            new UsageWindow("codex:w300", "5시간", 42, now.AddSeconds(-1)),
            new UsageWindow("codex:w10080", "주간", 12, now.AddHours(2)),
        ], asOf: now);
        Check("expired account usage is pending while weekly remains usable",
            pending[0] is { Label: "5시간 한도", Value: 42, IsResetPending: true, Remaining: 0, BarVisibility: Visibility.Hidden } &&
            pending[1] is { Label: "주간 한도", Value: 12, IsResetPending: false, Remaining: 88, BarVisibility: Visibility.Visible },
            DescribeRows(pending));
    }

    static void TestAccountSlots()
    {
        var secondarySettings = new List<SecondaryAccountSettings>();
        for (int index = 0; index < 4; index++)
            secondarySettings.Add(new() { Id = Guid.NewGuid().ToString("N"), Provider = AccountProvider.Claude });
        for (int index = 0; index < 2; index++)
            secondarySettings.Add(new() { Id = Guid.NewGuid().ToString("N"), Provider = AccountProvider.Codex });

        using var service = new SecondaryAccountService(new AppSettings { SecondaryAccounts = secondarySettings });
        var claudeAccounts = service.Accounts.Where(x => x.Settings.Provider == AccountProvider.Claude).ToList();
        var codexAccounts = service.Accounts.Where(x => x.Settings.Provider == AccountProvider.Codex).ToList();
        var reset = DateTimeOffset.UtcNow.AddHours(1);

        ApplySecondary(claudeAccounts[1], true, false, "claude2@example.test",
            Snapshot("fixture", DateTimeOffset.UtcNow, Window("seven_day", 22, reset)), null);
        ApplySecondary(claudeAccounts[2], true, false, "claude3@example.test",
            Snapshot("fixture", DateTimeOffset.UtcNow, Window("seven_day", 33, reset)), null);
        ApplySecondary(claudeAccounts[3], true, false, "claude4@example.test",
            Snapshot("fixture", DateTimeOffset.UtcNow, Window("seven_day", 44, reset)), null);

        var claudeMain = CompactProviderCard("Claude", "mainclaude");
        var codexMain = CompactProviderCard("Codex", "maincodex");
        var claudeSlots = AccountUsageSlot.Create(claudeMain, service.Accounts);
        var codexSlots = AccountUsageSlot.Create(codexMain, service.Accounts);
        Check("each provider always has main, secondary 1, and secondary 2 slots",
            claudeSlots.Count == 3 && codexSlots.Count == 3 &&
            claudeSlots.Select(x => x.Role).SequenceEqual(["메인", "서브1", "서브2"]) &&
            codexSlots.Select(x => x.Role).SequenceEqual(["메인", "서브1", "서브2"]),
            $"Claude={claudeSlots.Count}, Codex={codexSlots.Count}");
        Check("unauthenticated secondary is blank without shifting secondary 2",
            claudeSlots[1].IsEmpty && claudeSlots[1].Account == null &&
            claudeSlots[1].ContentVisibility == Visibility.Hidden &&
            claudeSlots[2].Account == "CLAUDE2" && claudeSlots[2].Usage?.Value == 22 &&
            claudeSlots.All(x => x.Account is not "CLAUDE3" and not "CLAUDE4"),
            string.Join(",", claudeSlots.Select(x => $"{x.Role}:{x.Account}:{x.Usage?.Value}")));
        Check("populated slot exposes remaining text, label, visibility, and signature",
            claudeSlots[2].Remaining == 78 && claudeSlots[2].RemainingText == "78%" &&
            claudeSlots[2].Label == "주간 한도" &&
            claudeSlots[2].ContentVisibility == Visibility.Visible &&
            claudeSlots[2].Signature.Contains("22", StringComparison.Ordinal), claudeSlots[2].Signature);
        Check("unconnected Codex secondary slots remain blank",
            codexSlots[1].IsEmpty && codexSlots[2].IsEmpty &&
            codexSlots[1].RemainingText == "—" && codexSlots[1].Label == "확인 중",
            string.Join(",", codexSlots.Select(x => $"{x.Role}:{x.IsEmpty}:{x.RemainingText}")));

        ApplySecondary(claudeAccounts[0], true, false, "claude1@example.test",
            Snapshot("fixture", DateTimeOffset.UtcNow, Window("seven_day", 11, reset)), null);
        var independent = AccountUsageSlot.Create(claudeMain, service.Accounts);
        Check("secondary account usage stays independent",
            independent[1].Account == "CLAUDE1" && independent[1].Usage?.Value == 11 &&
            independent[2].Account == "CLAUDE2" && independent[2].Usage?.Value == 22,
            string.Join(",", independent.Select(x => $"{x.Role}:{x.Account}:{x.Usage?.Value}")));

        ApplySecondary(claudeAccounts[0], false, true, null, null, null);
        var afterLoginLoss = AccountUsageSlot.Create(claudeMain, service.Accounts);
        Check("login loss clears only the affected secondary slot",
            afterLoginLoss[1].IsEmpty && afterLoginLoss[2].Account == "CLAUDE2" && afterLoginLoss[2].Usage?.Value == 22,
            string.Join(",", afterLoginLoss.Select(x => $"{x.Role}:{x.Account}:{x.Usage?.Value}")));

        var vm = new MainViewModel();
        var beforeCard = CompactProviderCard("Claude", "mainclaude",
            AccountUsageSlot.Create(claudeMain, service.Accounts));
        vm.Update(beforeCard, codexMain, "fixture");
        var previous = vm.Claude;
        var notifications = 0;
        vm.PropertyChanged += (_, e) => notifications += e.PropertyName == nameof(MainViewModel.Claude) ? 1 : 0;

        ApplySecondary(claudeAccounts[1], true, false, "claude2-updated@example.test",
            Snapshot("fixture", DateTimeOffset.UtcNow, Window("seven_day", 23, reset)), null);
        var afterCard = CompactProviderCard("Claude", "mainclaude",
            AccountUsageSlot.Create(claudeMain, service.Accounts));
        vm.Update(afterCard, codexMain, "fixture");
        Check("secondary change refreshes MainViewModel card",
            notifications == 1 && previous is not null && !ReferenceEquals(previous, vm.Claude) &&
            vm.Claude?.AccountSlots[2].Usage?.Value == 23,
            $"notifications={notifications}, value={vm.Claude?.AccountSlots.ElementAtOrDefault(2)?.Usage?.Value}");
    }

    static ProviderCard CompactProviderCard(string name, string account,
        List<AccountUsageSlot>? slots = null, List<UsageRow>? usage = null) => new()
    {
        Name = name,
        Accent = name == "Claude" ? Palette.Claude : Palette.Codex,
        Account = account,
        AppStatus = "fixture",
        AppStatusBrush = Palette.Muted,
        Footnote = "fixture",
        SessionsHeader = "fixture",
        Usage = usage ?? [],
        AccountSlots = slots ?? [],
    };

    static void ApplySecondary(SecondaryAccountState account, bool ok, bool loginRequired,
        string? name, UsageSnapshot? usage, string? error)
    {
        var apply = typeof(SecondaryAccountService).GetMethod("ApplyResult",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SecondaryAccountService), "ApplyResult");
        apply.Invoke(null, [account, ok, loginRequired, name, usage, error]);
    }

    static void TestPaletteLevels()
    {
        Check("palette remaining 100 ends at Ok", Palette.Level(0).Color == Palette.Ok.Color,
            Palette.Level(0).Color.ToString());
        Check("palette remaining 20 ends at Warn", Palette.Level(80).Color == Palette.Warn.Color,
            Palette.Level(80).Color.ToString());
        Check("palette remaining 0 ends at Bad", Palette.Level(100).Color == Palette.Bad.Color,
            Palette.Level(100).Color.ToString());
        Check("palette Ok-Warn midpoint is linear", Palette.Level(40).Color == Midpoint(Palette.Warn.Color, Palette.Ok.Color),
            Palette.Level(40).Color.ToString());
        Check("palette Warn-Bad midpoint is linear", Palette.Level(90).Color == Midpoint(Palette.Bad.Color, Palette.Warn.Color),
            Palette.Level(90).Color.ToString());
        Check("palette is continuous around the 90 percent boundary",
            ColorDistance(Palette.Level(89.9).Color, Palette.Level(90).Color) <= 3 &&
            ColorDistance(Palette.Level(90).Color, Palette.Level(90.1).Color) <= 3,
            $"{Palette.Level(89.9).Color}/{Palette.Level(90).Color}/{Palette.Level(90.1).Color}");
    }

    static Color Midpoint(Color a, Color b) => Color.FromRgb(
        (byte)Math.Round((a.R + b.R) / 2d),
        (byte)Math.Round((a.G + b.G) / 2d),
        (byte)Math.Round((a.B + b.B) / 2d));

    static int ColorDistance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    static string DescribeRows(IEnumerable<UsageRow> rows) =>
        string.Join(", ", rows.Select(x => $"{x.Label}={x.Value:0.##}"));

    static void TestShortResetAndBubble()
    {
        Check("ShortSpan covers day, hour, minute, sub-minute, and non-positive boundaries",
            new[]
            {
                Fmt.ShortSpan(TimeSpan.FromDays(5) + TimeSpan.FromHours(12) + TimeSpan.FromMinutes(24)),
                Fmt.ShortSpan(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)),
                Fmt.ShortSpan(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59)),
                Fmt.ShortSpan(TimeSpan.FromSeconds(1)), Fmt.ShortSpan(TimeSpan.Zero),
                Fmt.ShortSpan(TimeSpan.FromSeconds(-1)),
            }.SequenceEqual(["5D 12H 24M", "23H 59M", "59M", "<1M", "0M", "0M"]),
            "ShortSpan boundary output mismatch");

        var now = DateTimeOffset.UtcNow;
        var future = Presenter.DisplayUsage([Window("seven_day", 10, now.AddHours(1).AddMinutes(5))], now).Single();
        var expired = Presenter.DisplayUsage([Window("seven_day", 10, now.AddSeconds(-1))], now).Single();
        Check("UsageRow carries a short future reset timer", future.ResetShort == "1H 5M", future.ResetShort);
        Check("expired UsageRow awaits confirmation without fabricating usage", expired.Value == 10 && expired.IsResetPending && expired.ResetShort == "확인 중",
            $"value={expired.Value}, reset={expired.ResetShort}");

        var fiveMinute = Presenter.DisplayUsage([Window("seven_day", 10, now.AddMinutes(5))], now).Single();
        var sixMinute = Presenter.DisplayUsage([Window("seven_day", 10, now.AddMinutes(6))], now).Single();
        var firstSlot = AccountUsageSlot.Create(CompactProviderCard("Claude", "main", usage: [fiveMinute]), [])[0];
        var secondSlot = AccountUsageSlot.Create(CompactProviderCard("Claude", "main", usage: [sixMinute]), [])[0];
        var firstCard = CompactProviderCard("Claude", "main", [firstSlot]);
        var secondCard = CompactProviderCard("Claude", "main", [secondSlot]);
        Check("AccountUsageSlot timer changes are visible in ResetTimer and Signature",
            firstSlot.ResetTimer == "5M" && secondSlot.ResetTimer == "6M" &&
            firstSlot.Signature != secondSlot.Signature && firstCard.Signature() != secondCard.Signature(),
            $"{firstSlot.ResetTimer}/{secondSlot.ResetTimer}");

        var stage = new CharacterStage();
        var show = PrivateMethod<CharacterStage>("ShowBubble");
        var hide = PrivateMethod<CharacterStage>("HideBubble");
        var showFor = PrivateField<CharacterStage>("_bubbleShowFor");
        var next = PrivateField<CharacterStage>("_nextBubbleAt");
        var time = PrivateField<CharacterStage>("_t");
        var durations = new List<double>();
        var gaps = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            show.Invoke(stage, ["같은 대사"]);
            durations.Add((double)showFor.GetValue(stage)!);
            hide.Invoke(stage, null);
            gaps.Add((double)next.GetValue(stage)! - (double)time.GetValue(stage)!);
        }
        Check("bubble timing stays bounded and varies for repeated dialogue",
            durations.All(x => x >= 4 && x <= 10) && gaps.All(x => x >= 3 && x <= 12) &&
            durations.Select(x => Math.Round(x, 6)).Distinct().Count() > 1,
            $"duration={durations.Min():0.###}-{durations.Max():0.###}, gap={gaps.Min():0.###}-{gaps.Max():0.###}");

        stage.Measure(new Size(140, 280));
        stage.Arrange(new Rect(0, 0, 140, 280));
        var bubbleText = (System.Windows.Controls.TextBlock)PrivateField<CharacterStage>("_bubbleText").GetValue(stage)!;
        double MaxLineWidth(string[] lines) => lines.Max(line => new FormattedText(line,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(bubbleText.FontFamily, bubbleText.FontStyle, bubbleText.FontWeight, bubbleText.FontStretch),
            bubbleText.FontSize, bubbleText.Foreground, 1).WidthIncludingTrailingWhitespace);
        foreach (var dialogue in new[] { "인간… 어이가 없군요. 지금 열심히 작업 중이에요.", "그 정도는 금방 해. 결과만 기다려." })
        {
            show.Invoke(stage, [dialogue]);
            var lines = bubbleText.Text.Split('\n');
            double maxWidth = MaxLineWidth(lines);
            Check("dialogue wraps whole words within the bubble width",
                string.Join(' ', lines) == dialogue && maxWidth <= 120.01 && bubbleText.FontSize == 13 &&
                bubbleText.TextWrapping == TextWrapping.NoWrap && !lines.Contains("히") && !lines.Contains("해."),
                $"width={maxWidth:0.##}, text={bubbleText.Text}");
        }

        // 바뀐 요구사항: 최종 글자는 10 DIP 이상. 예전에는 공백 없는 긴 대사를 5px 대까지 줄여 한 줄에 넣었으나,
        // 이제는 10 DIP 까지만 줄이고 남는 부분은 글자 단위로 나눈다 (글자는 빠지거나 바뀌지 않는다).
        const string noSpaces = "공백이없는아주긴대사도잘리지않게표시합니다";
        show.Invoke(stage, [noSpaces]);
        var split = bubbleText.Text.Split('\n');
        Check("unbreakable dialogue keeps a 10 DIP minimum and breaks by character",
            bubbleText.FontSize >= 10 && split.Length > 1 && string.Concat(split) == noSpaces && MaxLineWidth(split) <= 120.01,
            $"font={bubbleText.FontSize:0.##}, width={MaxLineWidth(split):0.##}, text={bubbleText.Text}");
    }
}
