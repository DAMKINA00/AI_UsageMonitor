using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCompactLayout()
    {
        TestGeminiCard();
        TestStatusChipStates();
        TestLiveRefreshWithoutReplacement();
        TestTrayTooltip();
        TestCharacterThemes();
        TestCompactWindow();
    }

    static readonly DateTimeOffset GeminiResetBase = DateTimeOffset.Now;

    // 리셋 시각은 조회 시각과 무관하게 고정한다 (같은 쿼터를 다시 읽은 상황을 흉내 낸다)
    static UsageSnapshot GeminiUsage(DateTimeOffset asOf, double five = 20, double weekly = 35, string? warning = null) => new()
    {
        Windows =
        [
            new("gemini:w300", "5시간 한도", five, GeminiResetBase.AddHours(2)),
            new("gemini:w10080", "주간 한도", weekly, GeminiResetBase.AddDays(3)),
            new("claude_gpt:w300", "Claude·GPT · 5시간 한도", 10, GeminiResetBase.AddHours(4)),
            new("claude_gpt:w10080", "Claude·GPT · 주간 한도", 50, GeminiResetBase.AddDays(5)),
        ],
        AsOf = asOf, Source = AgyCli.Source, Warning = warning,
    };

    static MonitorService GeminiMonitor(AppSettings settings, GeminiLocalState? local, GeminiRemoteResult? good, GeminiRemoteResult? last,
        bool busy = false, bool paused = false)
    {
        var monitor = new MonitorService(settings);
        SetProperty(monitor, nameof(MonitorService.GeminiLocal), local);
        SetProperty(monitor, nameof(MonitorService.GeminiRemoteGood), good);
        SetProperty(monitor, nameof(MonitorService.GeminiRemoteLast), last);
        SetProperty(monitor, nameof(MonitorService.GeminiRemoteBusy), busy);
        SetProperty(monitor, nameof(MonitorService.GeminiPollingPaused), paused);
        return monitor;
    }

    static GeminiLocalState GeminiLocalFixture(DateTimeOffset now, bool installed = true) => new()
    {
        Installed = installed, ExecutablePath = installed ? @"C:\fake\agy.exe" : null, CliProcessCount = installed ? 1 : 0,
        Sessions = installed
            ? [new SessionInfo("agy:run", "리팩터링", "alpha · Antigravity CLI", SessionState.Working, "작업 중", now.AddMinutes(-3))]
            : [],
    };

    static void TestGeminiCard()
    {
        var now = DateTimeOffset.Now;
        var ok = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now), At = now };
        using var monitor = GeminiMonitor(new AppSettings { GeminiEnabled = true }, GeminiLocalFixture(now), ok, ok);
        var card = Presenter.Gemini(monitor);
        Check("Gemini does not infer a Pro tier from usage models", card.Plan == "등급 확인 불가" && card.Plan.Contains("Pro", StringComparison.OrdinalIgnoreCase) == false,
            card.Plan ?? "null");
        var explicitSettings = new AppSettings
        {
            GeminiEnabled = true, GeminiPlanOverride = "Google AI Pro", SettingsVersion = AppSettings.CurrentSettingsVersion,
        };
        var explicitCard = Presenter.Gemini(monitor, explicitSettings);
        var roundtrip = AppSettings.Parse(JsonSerializer.Serialize(explicitSettings));
        Check("Gemini tier override persists and appears in the card header", explicitCard.Plan == "Google AI Pro" &&
            roundtrip.GeminiPlanOverride == "Google AI Pro" && !roundtrip.Migrated, explicitCard.Plan ?? "null");
        Check("Gemini ring uses only the gemini group and shows the weekly limit",
            card.Usage is [{ Label: "주간 한도", Value: 35 }] && card.RawWindows.All(w => w.Key.StartsWith("gemini:")) &&
            card.RawWindows.Count == 2 && card.MaxUsed == 35, $"rows={DescribeRows(card.Usage)}, raw={DescribeWindows(card.RawWindows)}");
        Check("Claude·GPT group quota is tooltip-only", card.Extras == "Claude·GPT · 5시간 한도 10% 사용 · Claude·GPT · 주간 한도 50% 사용" &&
            AccountUsageSlot.Create(card, [], secondarySlots: 0).Single().ToolTipText.Contains("Claude·GPT · 주간 한도 50% 사용"),
            card.Extras ?? "");
        Check("Gemini card shows CLI activity and conversations", card is { Name: "Gemini", AppStatus: "CLI 실행 중", AppRunning: true, WorkingCount: 1 } &&
            card.SessionsHeader == "대화 1 · 작업 중 1" && card.Sessions.Single().Subtitle == "alpha · Antigravity CLI",
            $"{card.AppStatus}/{card.SessionsHeader}");
        Check("Gemini working conversation drives the working mood", new MoodTracker("gemini").Update(card).Mood == Mood.Working, "mood");

        var high = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now, five: 95), At = now };
        SetProperty(monitor, nameof(MonitorService.GeminiRemoteGood), high);
        Check("Gemini five-hour limit replaces weekly at 90 percent", Presenter.Gemini(monitor).Usage is [{ Label: "5시간 한도", Value: 95 }],
            DescribeRows(Presenter.Gemini(monitor).Usage));

        var exhausted = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now, weekly: 100, warning: AgyCli.ExhaustedWarning), At = now };
        SetProperty(monitor, nameof(MonitorService.GeminiRemoteGood), exhausted);
        var limitCard = Presenter.Gemini(monitor);
        Check("Gemini limit warning reaches notice, card and mood", limitCard.LimitReached && limitCard.Notice == AgyCli.ExhaustedWarning &&
            limitCard.NoticeBrush == Palette.Bad && new MoodTracker("gemini").Update(limitCard).Mood == Mood.Panic, limitCard.Notice ?? "");

        var slots = AccountUsageSlot.Create(card, [], secondarySlots: 0);
        Check("Gemini row has only the main account slot", slots is [{ Role: "메인", Provider: "Gemini" }], slots.Count.ToString());
        using var service = new SecondaryAccountService(new AppSettings());
        var panel = AccountUsageCard.Create(CompactProviderCard("Claude", "C"), CompactProviderCard("Codex", "X"), service, card);
        Check("account panel adds the Gemini main account only", panel.Count == 3 && panel[2] is { ProviderName: "Gemini · 주 계정", IsSecondary: false },
            string.Join(",", panel.Select(p => p.ProviderName)));
        var explicitPanel = AccountUsageCard.Create(CompactProviderCard("Claude", "C"), CompactProviderCard("Codex", "X"), service, explicitCard);
        Check("account card carries the explicit Gemini tier", explicitPanel[2].Plan == "Google AI Pro", explicitPanel[2].Plan ?? "null");
    }

    static void TestStatusChipStates()
    {
        var now = DateTimeOffset.Now;
        var settings = new AppSettings { GeminiEnabled = true };
        ProviderCard Card(GeminiLocalState? local, GeminiRemoteResult? good, GeminiRemoteResult? last, bool busy = false, bool paused = false,
            AppSettings? s = null)
        {
            using var monitor = GeminiMonitor(s ?? settings, local, good, last, busy, paused);
            return Presenter.Gemini(monitor, s ?? settings);
        }
        var fresh = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now), At = now };
        var old = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now.AddMinutes(-20)), At = now.AddMinutes(-20) };
        var failed = new GeminiRemoteResult { Error = "quota service unavailable" };
        var login = new GeminiRemoteResult { LoginRequired = true, Error = "You are not logged into Antigravity." };

        var freshCard = Card(GeminiLocalFixture(now), fresh, fresh);
        Check("fresh data shows the time chip without dimming", freshCard.Status.KindAt(now) == StatusKind.Fresh &&
            freshCard.Status.TextAt(now) == "방금" && freshCard.Status.Brush == Palette.Muted &&
            AccountUsageSlot.Create(freshCard, [], 0)[0] is { UsageOpacity: 1, WarningVisibility: Visibility.Collapsed },
            freshCard.Status.TextAt(now));
        Check("chip time text advances without a new card", freshCard.Status.TextAt(now.AddMinutes(7)) == "7분 전", freshCard.Status.TextAt(now.AddMinutes(7)));

        var staleCard = Card(GeminiLocalFixture(now), old, old);
        var staleSlot = AccountUsageSlot.Create(staleCard, [], 0)[0];
        Check("old data shows the stale chip and dims the main ring", staleCard.Status.Kind == StatusKind.Stale &&
            staleCard.Status.Text == "⚠ 오래됨 · 20분 전" && staleCard.Status.Brush == Palette.Warn &&
            staleSlot is { UsageOpacity: .45, WarningVisibility: Visibility.Visible }, $"{staleCard.Status.Text}/{staleSlot.UsageOpacity}");
        Check("stale threshold follows the Gemini poll interval", staleCard.Status.StaleAfter == TimeSpan.FromMinutes(15) &&
            freshCard.Status.KindAt(now.AddMinutes(14)) == StatusKind.Fresh && freshCard.Status.KindAt(now.AddMinutes(16)) == StatusKind.Stale,
            staleCard.Status.StaleAfter.ToString());

        var busyCard = Card(GeminiLocalFixture(now), fresh, fresh, busy: true);
        Check("query in progress shows the busy chip", busyCard.Status.Kind == StatusKind.Busy && busyCard.Status.Text == "갱신 중…", busyCard.Status.Text);

        var failedCard = Card(GeminiLocalFixture(now), fresh, failed);
        var failedSlot = AccountUsageSlot.Create(failedCard, [], 0)[0];
        Check("failed query keeps the last value, dims it and explains", failedCard.Status.Kind == StatusKind.Failed &&
            failedCard.Status.Text == "⚠ 조회 실패" && failedCard.Status.Brush == Palette.Bad &&
            failedCard.Status.ToolTipText.Contains("quota service unavailable") && failedCard.Usage.Count == 1 &&
            failedCard.Notice == "조회 실패 (이전 값 표시 중): quota service unavailable" && failedSlot.UsageOpacity == .45,
            $"{failedCard.Status.Text}/{failedCard.Notice}");

        var loginCard = Card(GeminiLocalFixture(now), null, login, paused: true);
        Check("login requirement shows the login chip and instructions", loginCard.Status.Kind == StatusKind.LoginRequired &&
            loginCard.Status.Text == "로그인 필요" && loginCard.Usage.Count == 0 && loginCard.Notice?.Contains("agy 로그인") == true,
            $"{loginCard.Status.Text}/{loginCard.Notice}");

        var missing = Card(GeminiLocalFixture(now, installed: false), null, null);
        Check("missing agy shows the not-installed chip quietly", missing.Status.Kind == StatusKind.NotInstalled &&
            missing.Status.Text == "agy 미설치" && missing.AppStatus == "agy 미설치" && missing.Notice == null &&
            missing.UsageEmpty?.Contains("설치되어 있지 않습니다") == true && missing.Sessions.Count == 0 && missing.MaxUsed == null,
            $"{missing.Status.Text}/{missing.AppStatus}/{missing.UsageEmpty}");

        var off = new AppSettings { GeminiEnabled = false };
        var disabled = Card(GeminiLocalFixture(now), null, null, s: off);
        Check("explicitly disabled Gemini shows the off chip", disabled.Status.Kind == StatusKind.Disabled && disabled.Status.Text == "꺼짐",
            disabled.Status.Text);

        var pending = Card(null, null, null);
        Check("before the first local read the chip waits for data", pending.Status.Kind == StatusKind.None &&
            pending.Status.Text == "데이터 없음" && pending.AppStatus == "확인 중…", pending.Status.Text);

        // Claude·Codex 카드에도 같은 칩을 쓴다
        using var codexMonitor = Monitor(new CodexRemoteResult { Ok = true, Usage = Snapshot("Codex 서버", now.AddMinutes(-30), Window("codex:w10080", 40, now.AddDays(2))) },
            new CodexRemoteResult { Ok = false, Error = "fixture offline" }, null);
        var codex = Presenter.Codex(codexMonitor);
        Check("Codex failure uses the shared failed chip", codex.Status.Kind == StatusKind.Failed && codex.Status.ToolTipText.Contains("fixture offline"),
            codex.Status.Text);
        using var claudeMonitor = new MonitorService(new AppSettings());
        SetProperty(claudeMonitor, nameof(MonitorService.ClaudeWebLast), new ClaudeWebResult { State = ClaudeWebState.LoginRequired });
        var claude = Presenter.Claude(claudeMonitor, new AppSettings(), () => { });
        Check("Claude desktop login loss uses the login chip", claude.Status.Kind == StatusKind.LoginRequired && claude.Status.Text == "로그인 필요",
            claude.Status.Text);
    }

    static void TestLiveRefreshWithoutReplacement()
    {
        var now = DateTimeOffset.Now;
        var settings = new AppSettings { GeminiEnabled = true };
        ProviderCard Build(DateTimeOffset asOf, bool busy, DateTimeOffset activity, double weekly = 35)
        {
            var good = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(asOf, weekly: weekly), At = asOf };
            var fixture = GeminiLocalFixture(now);
            var local = new GeminiLocalState
            {
                Installed = true, CliProcessCount = 1,
                Sessions = [fixture.Sessions[0] with { LastActivity = activity }],
            };
            using var monitor = GeminiMonitor(settings, local, good, good, busy);
            var card = Presenter.Gemini(monitor, settings);
            card.AccountSlots = AccountUsageSlot.Create(card, [], 0);
            return card;
        }

        var vm = new MainViewModel();
        var claude = CompactProviderCard("Claude", "C");
        var codex = CompactProviderCard("Codex", "X");
        var first = Build(now.AddMinutes(-2), false, now.AddMinutes(-5));
        vm.Update(claude, codex, first, "");
        int replaced = 0;
        vm.PropertyChanged += (_, e) => replaced += e.PropertyName == nameof(MainViewModel.Gemini) ? 1 : 0;
        int chipChanges = 0;
        first.Status.PropertyChanged += (_, e) => chipChanges += e.PropertyName == nameof(StatusChip.Text) ? 1 : 0;

        var newerTime = Build(now, true, now.AddMinutes(-1));
        Check("new query time and activity keep the same card", first.Signature() == newerTime.Signature(), "signature changed");
        vm.Update(claude, codex, newerTime, "");
        Check("same-structure refresh updates the chip and activity in place", replaced == 0 && ReferenceEquals(vm.Gemini, first) &&
            first.Status.Busy && first.Status.AsOf == newerTime.Status.AsOf && chipChanges > 0 &&
            first.Sessions[0].LastActivity == newerTime.Sessions[0].LastActivity,
            $"replaced={replaced}, busy={first.Status.Busy}, chip={chipChanges}");

        int rowChanges = 0;
        first.Sessions[0].PropertyChanged += (_, e) => rowChanges += e.PropertyName == nameof(SessionRow.TimeText) ? 1 : 0;
        int slotChanges = 0;
        first.AccountSlots[0].PropertyChanged += (_, e) => slotChanges += e.PropertyName == nameof(AccountUsageSlot.ResetTimer) ? 1 : 0;
        vm.Tick();
        Check("clock tick refreshes time text without replacing the card", replaced == 0 && rowChanges == 1 && slotChanges == 1 &&
            first.Sessions[0].TimeText == "1분 전", $"rows={rowChanges}, slots={slotChanges}, time={first.Sessions[0].TimeText}");

        var changed = Build(now, false, now.AddMinutes(-1), weekly: 41);
        vm.Update(claude, codex, changed, "");
        Check("usage change still replaces the card", replaced == 1 && ReferenceEquals(vm.Gemini, changed), $"replaced={replaced}");

        var row = new SessionRow { Title = "t", Subtitle = "folder", StateText = "완료", StateBrush = Palette.Ok, LastActivity = now.AddMinutes(-3) };
        Check("session detail joins the fixed subtitle and live time", row.DetailText == "folder · 3분 전" && !row.Subtitle.Contains("전"),
            row.DetailText);
    }

    static void TestTrayTooltip()
    {
        var now = DateTimeOffset.Now;
        ProviderCard Busy(string name) => new()
        {
            Name = name, Accent = Palette.Claude, AppStatus = "x", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "",
            Usage = Presenter.DisplayUsage([new UsageWindow("five_hour", "5시간", 99.5, now.AddHours(1)), new UsageWindow("seven_day", "주간", 50, now.AddDays(1))], now),
            WorkingCount = 123456, NeedsInputCount = 654321,
            Status = new StatusChip(StatusKind.Failed, now, TimeSpan.FromMinutes(5), false, "offline"),
        };
        var text = Tray.Tooltip([Busy("Claude"), Busy("Codex"), Busy("Gemini"), Busy("Grok")]);
        Check("tray tooltip has four lines within the 127 character limit", text.Length <= Tray.MaxTooltipLength &&
            text.Split('\n') is [var a, var b, var c, var d] && a.StartsWith("Claude 5시간") && b.StartsWith("Codex") &&
            c.StartsWith("Gemini") && d.StartsWith("Grok"),
            $"{text.Length}: {text}");

        using var monitor = GeminiMonitor(new AppSettings(), GeminiLocalFixture(now, installed: false), null, null);
        var missing = Presenter.Gemini(monitor);
        var normal = Tray.Tooltip([CompactProviderCard("Claude", "C", usage: Presenter.DisplayUsage([Window("seven_day", 62, now.AddDays(2))], now)),
            CompactProviderCard("Codex", "X"), missing, CompactProviderCard("Grok", "G")]);
        Check("tray tooltip names the Gemini state", normal == "Claude 주간 62%\nCodex 사용량 미확인\nGemini agy 미설치\nGrok 사용량 미확인", normal);
    }

    static void TestCharacterThemes()
    {
        var themes = CharacterTheme.All;
        Check("four character themes with new nicknames", themes.Select(t => t.Key).SequenceEqual(["claude", "codex", "gemini", "grok"]) &&
            themes.Take(3).Select(t => t.DisplayName).SequenceEqual(["클쨩", "짚쨩", "젬쨩"]) && themes[3].DisplayName == "그쨩" &&
            CharacterTheme.For("unknown") == CharacterTheme.Claude && CharacterTheme.For("GEMINI") == CharacterTheme.Gemini &&
            CharacterTheme.For("GROK") == CharacterTheme.Grok,
            string.Join(",", themes.Select(t => t.DisplayName)));
        Check("Gemini effects use the character palette", CharacterTheme.Gemini.Sparkles.Contains(Color.FromRgb(0x1C, 0x99, 0xA0)) &&
            !CharacterTheme.Gemini.Sparkles.SequenceEqual(CharacterTheme.Claude.Sparkles), "palette");
        Check("existing character art is found by resource name", CharacterTheme.HasAsset("claude", "idle") && CharacterTheme.HasAsset("codex", "work3") &&
            !CharacterTheme.HasAsset("missing-art", "idle"), "resource lookup");

        // 그림이 없는 캐릭터: 예외 없이 자리만 비운다 (젬쨩 그림이 들어오기 전 상태와 같다)
        Exception? failure = null;
        var stage = new CharacterStage();
        try
        {
            stage.Character = "missing-art";
            stage.SetMood(Mood.Working, ["작업 중"]);
            stage.PopText("+1%", Colors.White);
            stage.Celebrate();
            var frame = PrivateMethod<CharacterStage>("Frame");
            for (int i = 0; i < 20; i++) frame.Invoke(stage, null);
        }
        catch (Exception ex) { failure = Unwrap(ex); }
        var root = (Grid)PrivateField<CharacterStage>("_root").GetValue(stage)!;
        Check("character without art stays empty without exceptions", failure == null && !stage.HasArt && root.Visibility == Visibility.Hidden,
            failure?.Message ?? root.Visibility.ToString());
    }

    static void TestCompactWindow()
    {
        var now = DateTimeOffset.Now;
        var settingsBefore = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        var vm = new MainViewModel();
        var claude = CompactProviderCard("Claude", "TEST", usage: Presenter.DisplayUsage([Window("seven_day", 40, now.AddDays(2))], now));
        claude.AccountSlots = AccountUsageSlot.Create(claude, []);
        var ok = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now.AddMinutes(-20)), At = now };
        using var monitor = GeminiMonitor(new AppSettings { GeminiEnabled = true }, GeminiLocalFixture(now), ok, ok);
        var gemini = Presenter.Gemini(monitor);
        gemini.AccountSlots = AccountUsageSlot.Create(gemini, [], 0);
        var codex = CompactProviderCard("Codex", "TEST");
        var grok = CompactProviderCard("Grok", "TEST", usage: Presenter.DisplayUsage([Window("grok:w10080", 35, now.AddDays(2))], now));
        UpdateViewModel(vm, claude, codex, gemini, grok, "");

        var window = new MainWindow(vm, new AppSettings { ShowCharacters = true, Topmost = false }, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            var root = FindNamed<Grid>(window, "Root");
            var field = FindNamed<Canvas>(window, "CharacterField");
            var items = FindNamed<ItemsControl>(window, "PlatformItems");
            var scroll = FindNamed<ScrollViewer>(window, "PlatformScroll");
            Check("stage window is transparent and uses the named surfaces", window.AllowsTransparency &&
                window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.NoResize && !window.ShowInTaskbar &&
                window.Background is SolidColorBrush background && background.Color == Colors.Transparent &&
                root.Background == null && field.Background == null && scroll.Background == null && window.FindName("Toolbar") == null,
                $"transparent={window.AllowsTransparency}, background={window.Background}, resize={window.ResizeMode}");

            var slots = items.Items.OfType<AccountUsageSlot>().ToList();
            Check("four registered accounts render as four platform blocks without placeholders", slots.Count == 4 &&
                slots.All(slot => !slot.IsEmpty) && slots.Select(slot => slot.Provider).SequenceEqual(["Claude", "Codex", "Gemini", "Grok"]),
                string.Join(",", slots.Select(slot => $"{slot.Provider}:{slot.IsEmpty}")));
            Check("platform scroll surface is horizontal only", scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto,
                $"scroll={scroll.HorizontalScrollBarVisibility}/{scroll.VerticalScrollBarVisibility}");

            var stages = new[] { window.Claude, window.Codex, window.Gemini, window.Grok };
            Check("four characters use the fixed independent stage size", window.CharactersShown && field.ActualHeight >= 178 &&
                stages.All(stage => stage.Width == 144 && stage.Height == 178 && stage.FigureHeight == 106 && stage.IsVisible),
                string.Join(",", stages.Select(stage => $"{stage.Character}:{stage.Width}x{stage.Height}/{stage.FigureHeight}")));

            Check("Gemini stage follows the art in the resources", window.Gemini.HasArt == CharacterTheme.HasAsset("gemini", "idle"),
                window.Gemini.HasArt.ToString());
            Check("Grok stage follows the art in the resources", window.Grok.HasArt == CharacterTheme.HasAsset("grok", "idle"),
                window.Grok.HasArt.ToString());

            var size = new Size(window.Width, window.Height);
            window.ApplyCharacters(false);
            PumpDisplay();
            Check("hiding characters keeps the window size",
                window.Width == size.Width && window.Height == size.Height && !window.Claude.IsVisible && !window.Grok.IsVisible,
                $"{window.Width}x{window.Height}");
            window.ApplyCharacters(true);
            PumpDisplay();
            Check("showing characters restores all four stages without resizing", window.CharactersShown &&
                stages.All(stage => stage.IsVisible) && window.Width == size.Width && window.Height == size.Height,
                $"size={window.Width}x{window.Height}");
            RenderFixture(window, "compact-stage");
        }
        finally { window.Close(); }
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("compact window test does not write user settings",
            settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter), "settings changed");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
