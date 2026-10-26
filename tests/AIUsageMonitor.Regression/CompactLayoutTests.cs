using System.IO;
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
        TestStatusChipStates();
        TestLiveRefreshWithoutReplacement();
        TestTrayTooltip();
        TestCharacterThemes();
        TestCompactWindow();
    }

    static ProviderCard StatusCard(StatusChip status) => new()
    {
        Name = "Grok", Accent = Palette.Grok, AppStatus = "fixture", AppStatusBrush = Palette.Muted,
        Footnote = "fixture", SessionsHeader = "fixture", Status = status,
        Usage = Presenter.DisplayUsage([new UsageWindow("grok:w10080", "주간 한도", 40, DateTimeOffset.Now.AddDays(2))]),
    };

    static void TestStatusChipStates()
    {
        var now = DateTimeOffset.Now;
        var fresh = new StatusChip(StatusKind.None, now, TimeSpan.FromMinutes(15), false);
        Check("fresh data shows the time chip without dimming", fresh.KindAt(now) == StatusKind.Fresh &&
            fresh.TextAt(now) == "방금" && fresh.Brush == Palette.Muted && !fresh.IsDimmedAt(now), fresh.TextAt(now));
        Check("chip time text advances without a new card", fresh.TextAt(now.AddMinutes(7)) == "7분 전", fresh.TextAt(now.AddMinutes(7)));

        var stale = new StatusChip(StatusKind.None, now.AddMinutes(-20), TimeSpan.FromMinutes(15), false);
        var staleCard = StatusCard(stale);
        var staleSlot = AccountUsageSlot.Create(staleCard, [], 0)[0];
        Check("old data shows the stale chip and dims the main ring", stale.KindAt(now) == StatusKind.Stale &&
            stale.TextAt(now) == "⚠ 오래됨 · 20분 전" && stale.Brush == Palette.Warn && stale.IsDimmedAt(now) &&
            staleSlot is { UsageOpacity: .45, WarningVisibility: Visibility.Visible },
            $"{stale.TextAt(now)}/{staleSlot.UsageOpacity}");
        Check("stale threshold follows the provider interval", fresh.KindAt(now.AddMinutes(14)) == StatusKind.Fresh &&
            fresh.KindAt(now.AddMinutes(16)) == StatusKind.Stale, fresh.StaleAfter.ToString());

        var busy = new StatusChip(StatusKind.None, now, TimeSpan.FromMinutes(15), true);
        Check("query in progress shows the busy chip", busy.KindAt(now) == StatusKind.Busy && busy.TextAt(now) == "갱신 중…", busy.TextAt(now));

        var failed = new StatusChip(StatusKind.Failed, now, TimeSpan.FromMinutes(15), false, "fixture offline");
        Check("failed query keeps the last value dimmed and explains", failed.KindAt(now) == StatusKind.Failed &&
            failed.TextAt(now) == "⚠ 조회 실패" && failed.Brush == Palette.Bad && failed.IsDimmedAt(now) &&
            failed.ToolTipText.Contains("fixture offline"), failed.ToolTipText);

        var login = new StatusChip(StatusKind.LoginRequired, null, TimeSpan.FromMinutes(15), false, "login fixture");
        var missing = new StatusChip(StatusKind.NotInstalled, null, TimeSpan.FromMinutes(15), false);
        var disabled = new StatusChip(StatusKind.Disabled, null, TimeSpan.FromMinutes(15), false);
        Check("non-query states remain explicit", login.TextAt(now) == "로그인 필요" && missing.TextAt(now) == "CLI 미설치" &&
            disabled.TextAt(now) == "꺼짐" && login.ToolTipText.Contains("login fixture"),
            $"{login.TextAt(now)}/{missing.TextAt(now)}/{disabled.TextAt(now)}");

        // Claude·Codex 카드에도 같은 칩을 쓴다.
        using var codexMonitor = Monitor(new CodexRemoteResult
        {
            Ok = true,
            Usage = Snapshot("Codex 서버", now.AddMinutes(-30), Window("codex:w10080", 40, now.AddDays(2))),
        }, new CodexRemoteResult { Ok = false, Error = "fixture offline" }, null);
        var codex = Presenter.Codex(codexMonitor);
        Check("Codex failure uses the shared failed chip", codex.Status.Kind == StatusKind.Failed &&
            codex.Status.ToolTipText.Contains("fixture offline"), codex.Status.Text);
        using var claudeMonitor = new MonitorService(new AppSettings());
        SetProperty(claudeMonitor, nameof(MonitorService.ClaudeWebLast), new ClaudeWebResult { State = ClaudeWebState.LoginRequired });
        var claude = Presenter.Claude(claudeMonitor, new AppSettings(), () => { });
        Check("Claude desktop login loss uses the login chip", claude.Status.Kind == StatusKind.LoginRequired && claude.Status.Text == "로그인 필요",
            claude.Status.Text);
    }

    static void TestLiveRefreshWithoutReplacement()
    {
        var now = DateTimeOffset.Now;
        ProviderCard Build(DateTimeOffset asOf, bool busy, DateTimeOffset activity, double weekly = 35)
        {
            var card = new ProviderCard
            {
                Name = "Grok", Accent = Palette.Grok, Account = "GROK", AppStatus = "fixture", AppStatusBrush = Palette.Muted,
                Footnote = "fixture", SessionsHeader = "세션 1 · 작업 중 1",
                Usage = Presenter.DisplayUsage([new UsageWindow("grok:w10080", "주간 한도", weekly, now.AddDays(2))], asOf),
                Status = new StatusChip(StatusKind.None, asOf, TimeSpan.FromMinutes(15), busy),
                Sessions = [new SessionRow
                {
                    Id = "grok:run", Title = "작업", Subtitle = "fixture", StateText = "작업 중", StateBrush = Palette.Ok,
                    State = SessionState.Working, ActivityIsStateChange = false, LastActivity = activity,
                }],
            };
            card.AccountSlots = AccountUsageSlot.Create(card, [], 0);
            return card;
        }

        var vm = new MainViewModel();
        var claude = CompactProviderCard("Claude", "C");
        var codex = CompactProviderCard("Codex", "X");
        var first = Build(now.AddMinutes(-2), false, now.AddMinutes(-5));
        vm.Update(claude, codex, first, "");
        int replaced = 0;
        vm.PropertyChanged += (_, e) => replaced += e.PropertyName == nameof(MainViewModel.Grok) ? 1 : 0;
        int chipChanges = 0;
        first.Status.PropertyChanged += (_, e) => chipChanges += e.PropertyName == nameof(StatusChip.Text) ? 1 : 0;

        var newerTime = Build(now, true, now.AddMinutes(-1));
        Check("new query time and activity keep the same card", first.Signature() == newerTime.Signature(), "signature changed");
        vm.Update(claude, codex, newerTime, "");
        Check("same-structure refresh updates the chip and activity in place", replaced == 0 && ReferenceEquals(vm.Grok, first) &&
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
        Check("usage change still replaces the card", replaced == 1 && ReferenceEquals(vm.Grok, changed), $"replaced={replaced}");

        var row = new SessionRow { Title = "t", Subtitle = "folder", StateText = "완료", StateBrush = Palette.Ok, LastActivity = now.AddMinutes(-3) };
        Check("session detail joins the fixed subtitle and live time", row.DetailText == "folder · 3분 전" && !row.Subtitle.Contains("전"), row.DetailText);
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
        var text = Tray.Tooltip([Busy("Claude"), Busy("Codex"), Busy("Grok")]);
        Check("tray tooltip has three provider lines within the limit", text.Length <= Tray.MaxTooltipLength &&
            text.Split('\n') is [var a, var b, var c] && a.StartsWith("Claude 5시간") && b.StartsWith("Codex") && c.StartsWith("Grok"),
            $"{text.Length}: {text}");

        var normal = Tray.Tooltip([CompactProviderCard("Claude", "C", usage: Presenter.DisplayUsage([Window("seven_day", 62, now.AddDays(2))], now)),
            CompactProviderCard("Codex", "X"), CompactProviderCard("Grok", "G")]);
        Check("tray tooltip keeps only the active providers", normal == "Claude 주간 62%\nCodex 사용량 미확인\nGrok 사용량 미확인", normal);
    }

    static void TestCharacterThemes()
    {
        var themes = CharacterTheme.All;
        Check("four character themes with new nicknames", themes.Select(t => t.Key).SequenceEqual(["claude", "codex", "gemini", "grok"]) &&
            themes.Take(3).Select(t => t.DisplayName).SequenceEqual(["클쨩", "짚쨩", "젬쨩"]) && themes[3].DisplayName == "그쨩" &&
            CharacterTheme.For("unknown") == CharacterTheme.Claude && CharacterTheme.For("GEMINI") == CharacterTheme.Gemini &&
            CharacterTheme.For("GROK") == CharacterTheme.Grok, string.Join(",", themes.Select(t => t.DisplayName)));
        Check("Gemini effects use the character palette", CharacterTheme.Gemini.Sparkles.Contains(Color.FromRgb(0x1C, 0x99, 0xA0)) &&
            !CharacterTheme.Gemini.Sparkles.SequenceEqual(CharacterTheme.Claude.Sparkles), "palette");
        Check("existing character art is found by resource name", CharacterTheme.HasAsset("claude", "idle") && CharacterTheme.HasAsset("codex", "work3") &&
            !CharacterTheme.HasAsset("missing-art", "idle"), "resource lookup");

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
        var codex = CompactProviderCard("Codex", "TEST");
        var grok = CompactProviderCard("Grok", "TEST", usage: Presenter.DisplayUsage([Window("grok:w10080", 35, now.AddDays(2))], now));
        vm.Update(claude, codex, grok, "");

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
                window.Background is SolidColorBrush background && background.Color == Colors.Transparent && root.Background == null &&
                field.Background == null && scroll.Background == null && window.FindName("Toolbar") == null,
                $"transparent={window.AllowsTransparency}, background={window.Background}, resize={window.ResizeMode}");

            var slots = items.Items.OfType<AccountUsageSlot>().ToList();
            Check("three active providers render without Gemini usage placeholders", slots.Count == 3 && slots.All(slot => !slot.IsEmpty) &&
                slots.Select(slot => slot.Provider).SequenceEqual(["Claude", "Codex", "Grok"]),
                string.Join(",", slots.Select(slot => $"{slot.Provider}:{slot.IsEmpty}")));
            Check("platform scroll surface is horizontal only", scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, $"scroll={scroll.HorizontalScrollBarVisibility}/{scroll.VerticalScrollBarVisibility}");

            var stages = new[] { window.Claude, window.Codex, window.Gemini, window.Grok };
            Check("four characters use the fixed independent stage size", window.CharactersShown && field.ActualHeight >= 178 &&
                stages.All(stage => stage.Width == 144 && stage.Height == 178 && stage.FigureHeight == 106 && stage.IsVisible),
                string.Join(",", stages.Select(stage => $"{stage.Character}:{stage.Width}x{stage.Height}/{stage.FigureHeight}")));
            Check("Gemini stage follows the art in the resources", window.Gemini.HasArt == CharacterTheme.HasAsset("gemini", "idle"), window.Gemini.HasArt.ToString());
            Check("Grok stage follows the art in the resources", window.Grok.HasArt == CharacterTheme.HasAsset("grok", "idle"), window.Grok.HasArt.ToString());

            var size = new Size(window.Width, window.Height);
            window.ApplyCharacters(false);
            PumpDisplay();
            Check("hiding characters keeps the window size", window.Width == size.Width && window.Height == size.Height &&
                !window.Claude.IsVisible && !window.Grok.IsVisible, $"{window.Width}x{window.Height}");
            window.ApplyCharacters(true);
            PumpDisplay();
            Check("showing characters restores all four stages without resizing", window.CharactersShown && stages.All(stage => stage.IsVisible) &&
                window.Width == size.Width && window.Height == size.Height, $"size={window.Width}x{window.Height}");
            RenderFixture(window, "compact-stage");
        }
        finally { window.Close(); }
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("compact window test does not write user settings", settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter),
            "settings changed");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

}
