using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestResponsiveLayout()
    {
        TestCharacterDefaultMigration();
        TestGeminiUnsupportedAndWarnings();
        TestNicknameCodepoints();
        var settingsBefore = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        TestResponsiveSizes();
        TestBoundsPersistence();
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("responsive window tests do not write user settings",
            settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter), "settings changed");
    }

    static void TestCharacterDefaultMigration()
    {
        Check("characters are on by default", new AppSettings().ShowCharacters, "default off");

        var legacy = AppSettings.Parse("""{"ShowCharacters": true, "Topmost": true}""");
        Check("legacy settings turn characters on once and keep other values",
            legacy.ShowCharacters && legacy.Migrated && legacy.SettingsVersion == AppSettings.CurrentSettingsVersion && legacy.Topmost,
            $"show={legacy.ShowCharacters}, migrated={legacy.Migrated}, version={legacy.SettingsVersion}");

        // 마이그레이션 뒤 사용자가 끄면 다음 실행에도 꺼진 채로 남는다
        legacy.ShowCharacters = false;
        var reloaded = AppSettings.Parse(JsonSerializer.Serialize(legacy));
        Check("characters turned off after migration stay off", !reloaded.ShowCharacters && !reloaded.Migrated, $"show={reloaded.ShowCharacters}");
        var off = AppSettings.Parse(JsonSerializer.Serialize(new AppSettings { ShowCharacters = false, SettingsVersion = AppSettings.CurrentSettingsVersion }));
        Check("current settings keep characters off", !off.ShowCharacters && !off.Migrated, $"show={off.ShowCharacters}");
        var empty = AppSettings.Parse("{}");
        Check("settings without a version are migrated", empty.ShowCharacters && empty.Migrated, $"migrated={empty.Migrated}");
        Check("migration flag is not written to the file", !JsonSerializer.Serialize(legacy).Contains(nameof(AppSettings.Migrated)), "flag serialized");
    }

    static void TestGeminiUnsupportedAndWarnings()
    {
        var now = DateTimeOffset.Now;
        var local = new GeminiLocalState { Installed = true, ExecutablePath = @"C:\fake\agy.exe", ExecutableFingerprint = "fixture-fp", CliProcessCount = 0 };
        using var blocked = GeminiMonitor(new AppSettings { GeminiEnabled = true, AgyBlockedFingerprint = "fixture-fp" }, local, null,
            new GeminiRemoteResult { Unsupported = true, Error = AgyCli.UnsupportedMessage });
        var card = Presenter.Gemini(blocked);
        Check("unsupported agy shows the version chip", blocked.GeminiUnsupported && card.Status.Kind == StatusKind.Unsupported &&
            card.Status.Text == "agy 버전 미지원" && card.Status.Brush == Palette.Warn && card.Notice?.Contains(GeminiBlockAction.MenuText) == true &&
            card.Status.ToolTipText.StartsWith("agy 버전 미지원: 설치된 agy 가 /usage 조회를 지원하지 않아"),
            $"{card.Status.Text}/{card.Notice}/{card.Status.ToolTipText}");
        Check("unsupported agy is named in the tray tooltip", Tray.Tooltip([card]) == "Gemini agy 버전 미지원", Tray.Tooltip([card]));

        // agy 가 바뀐 경우: 예전 실행 파일의 차단 지문은 설정에 남아 있고 지금 지문과 다르다
        var changedSettings = new AppSettings { GeminiEnabled = true, AgyBlockedFingerprint = "old-fp" };
        using var resultOnly = GeminiMonitor(changedSettings,
            new GeminiLocalState { Installed = true, ExecutablePath = @"C:\fake\agy.exe", ExecutableFingerprint = "other-fp" }, null,
            new GeminiRemoteResult { Unsupported = true, Error = AgyCli.UnsupportedMessage });
        var changed = Presenter.Gemini(resultOnly, changedSettings);
        Check("after agy changes the last unsupported result asks for a refresh", !resultOnly.GeminiUnsupported &&
            changed.Status.Kind == StatusKind.Unsupported && changed.Notice?.Contains("새로고침") == true &&
            changed.Notice?.Contains(GeminiBlockAction.MenuText) == false, $"{changed.Status.Text}/{changed.Notice}");

        var inferred = new GeminiRemoteResult { Ok = true, At = now, Usage = GeminiUsage(now, weekly: 100, warning: AgyCli.InferredExhaustedWarning) };
        using var limit = GeminiMonitor(new AppSettings { GeminiEnabled = true }, GeminiLocalFixture(now), inferred, inferred);
        var limitCard = Presenter.Gemini(limit);
        var slot = AccountUsageSlot.Create(limitCard, [], 0)[0];
        Check("inferred limit warning marks the ring and its tooltip", limitCard.UsageWarning == AgyCli.InferredExhaustedWarning &&
            limitCard.LimitReached && limitCard.NoticeBrush == Palette.Bad &&
            slot.WarningVisibility == Visibility.Visible && slot.UsageOpacity == 1 && slot.ToolTipText.Contains("⚠ 한도 도달(추정)"),
            slot.ToolTipText);
    }

    /// <summary>이 테스트 소스 파일의 위치에서 위로 올라가 저장소 루트(AIUsageMonitor.csproj 가 있는 폴더)를 찾는다.
    /// 빌드 출력 위치(--artifacts-path 등)와 무관하게 동작한다.</summary>
    static DirectoryInfo? RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var dir = new FileInfo(sourceFile).Directory;
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AIUsageMonitor.csproj"))) dir = dir.Parent;
        return dir;
    }

    static void TestNicknameCodepoints()
    {
        const char right = '\uCA29', wrong = '\uCAA1', earlier = '\uCA69', old = '\uC9F1';
        Check("character nicknames end with U+CA29", CharacterTheme.All.All(t => t.DisplayName.Length == 2 && t.DisplayName[1] == right),
            string.Join(",", CharacterTheme.All.Select(t => $"U+{(int)t.DisplayName[^1]:X4}")));

        var root = RepositoryRoot();
        if (root == null)
        {
            Check("repository sources found for the nickname scan", false, "AIUsageMonitor.csproj not found above the test source");
            return;
        }
        // 저장소의 모든 텍스트 소스(빌드 산출물·배포 폴더 제외)에서 잘못 들어갔던 글자를 찾는다.
        // U+C9F1 은 다른 낱말에도 쓰이므로 애칭 자리(클·짚·젬 바로 뒤)에서만 본다.
        string[] skip = ["bin", "obj", "publish", ".git", ".vs", "node_modules"];
        var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".cs" or ".xaml" or ".md" or ".py" or ".json")
            .Where(f => !Path.GetRelativePath(root.FullName, f.FullName).Split(Path.DirectorySeparatorChar).Any(skip.Contains))
            .ToList();
        int correct = 0;
        var bad = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file.FullName);
            var name = Path.GetRelativePath(root.FullName, file.FullName);
            if (text.Contains(wrong)) bad.Add($"{name}: U+CAA1");
            if (text.Contains(earlier)) bad.Add($"{name}: U+CA69");
            for (int i = 1; i < text.Length; i++)
            {
                if (text[i - 1] is not ('\uD074' or '\uC9DA' or '\uC82C')) continue;
                if (text[i] == old) bad.Add($"{name}: U+C9F1 nickname");
                if (text[i] == right) correct++;
            }
        }
        Check("no text source uses U+CAA1, U+CA69 or the old U+C9F1 nickname",
            files.Count > 30 && bad.Count == 0 && correct > 0, $"files={files.Count}, U+CA29 nicknames={correct}, bad={string.Join(", ", bad.Take(8))}");
    }

    /// <summary>네 서비스 모두 값·안내가 있는 화면 (잘림·겹침이 생기기 쉬운 상태).</summary>
    static MainViewModel BusyViewModel(out IDisposable cleanup)
    {
        var now = DateTimeOffset.Now;
        var service = new SecondaryAccountService(new AppSettings
        {
            SecondaryAccounts = [new() { Provider = AccountProvider.Claude }, new() { Provider = AccountProvider.Claude }],
        });
        ApplySecondary(service.Accounts[0], true, false, "longaccountname@example.test", Snapshot("t", now, Window("seven_day", 27, now.AddDays(4).AddHours(23))), null);
        ApplySecondary(service.Accounts[1], true, false, "side@example.test", Snapshot("t", now, Window("seven_day", 73, now.AddDays(2).AddHours(23))), null);
        ApplySecondary(service.Accounts[1], false, false, null, null, "fixture offline");

        SessionRow Row(string title, string state, SessionState s) => new()
        {
            Id = title, Title = title, Subtitle = "folder", StateText = state, StateBrush = Palette.State(s), State = s, LastActivity = now.AddMinutes(-3),
        };
        var claude = new ProviderCard
        {
            Name = "Claude", Accent = Palette.Claude, Plan = "Max 20x", AppStatus = "앱 실행 중", AppStatusBrush = Palette.Ok, Account = "DAMKINA",
            Usage = Presenter.DisplayUsage([Window("seven_day", 42, now.AddDays(4).AddHours(23))], now),
            Notice = "데스크톱 연결 확인: 아주 긴 오류 문구가 들어와도 한 줄로 줄여서 보여 주고 툴팁에서 전체를 확인합니다",
            ActionText = "Claude 데스크톱 열기", ActionCommand = new RelayCommand(() => { }),
            Footnote = "출처: fixture", SessionsHeader = "세션 6 · 작업 중 2 · 대기 1",
            Status = new StatusChip(StatusKind.Failed, now.AddMinutes(-90), TimeSpan.FromMinutes(5), false, "offline"),
            Sessions = [Row("아주 긴 세션 제목이 잘리지 않고 말줄임으로 표시되는지 확인", "승인 대기", SessionState.NeedsInput),
                Row("UI 압축 레이아웃 구현", "작업 중", SessionState.Working), Row("Gemini 수집기 리뷰", "작업 중", SessionState.Working),
                Row("README 정리", "완료", SessionState.Idle), Row("캐릭터 대사 검수", "완료", SessionState.Idle), Row("빌드", "완료", SessionState.Idle)],
        };
        claude.AccountSlots = AccountUsageSlot.Create(claude, service.Accounts);
        var codex = new ProviderCard
        {
            Name = "Codex", Accent = Palette.Codex, Plan = "Pro", AppStatus = "CLI 실행 중", AppStatusBrush = Palette.Ok, Account = "SUB",
            Usage = Presenter.DisplayUsage([Window("codex:w300", 92, now.AddMinutes(48)), Window("codex:w10080", 61, now.AddDays(2))], now),
            Footnote = "출처: fixture", SessionsHeader = "스레드 2 · 작업 중 1",
            Status = new StatusChip(StatusKind.None, now, TimeSpan.FromMinutes(5), true),
            Sessions = [Row("빌드 스크립트 정리", "작업 중", SessionState.Working), Row("테스트 실패 분석", "완료", SessionState.Idle)],
        };
        codex.AccountSlots = AccountUsageSlot.Create(codex, service.Accounts);
        var ok = new GeminiRemoteResult { Ok = true, Usage = GeminiUsage(now.AddMinutes(-20), warning: AgyCli.InferredExhaustedWarning), At = now };
        var monitor = GeminiMonitor(new AppSettings { GeminiEnabled = true }, GeminiLocalFixture(now), ok, ok);
        var gemini = Presenter.Gemini(monitor);
        gemini.AccountSlots = AccountUsageSlot.Create(gemini, [], 0);
        var grok = new ProviderCard
        {
            Name = "Grok", Accent = Palette.Codex, Plan = "SuperGrok", AppStatus = "CLI 실행 중", AppStatusBrush = Palette.Ok,
            Account = "GROK", Usage = Presenter.DisplayUsage([Window("grok:w10080", 54, now.AddDays(2))], now),
            Footnote = "출처: fixture", SessionsHeader = "", Status = new StatusChip(StatusKind.None, now, TimeSpan.FromMinutes(5), false),
        };
        grok.AccountSlots = AccountUsageSlot.Create(grok, [], 0);
        var vm = new MainViewModel();
        UpdateViewModel(vm, claude, codex, gemini, grok, "");
        cleanup = new Disposables(service, monitor);
        return vm;
    }

    /// <summary>네 제공자 카드를 뷰 모델에 넣는다.</summary>
    static void UpdateViewModel(MainViewModel vm, ProviderCard claude, ProviderCard codex, ProviderCard? gemini,
        ProviderCard? grok, string footer)
        => vm.Update(claude, codex, gemini, grok, footer);

    sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var item in items) item.Dispose(); }
    }

    static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    static bool Overlaps(Rect a, Rect b) => a.IntersectsWith(b) && Rect.Intersect(a, b) is { Width: > 0.5, Height: > 0.5 };

    /// <summary>글자 크기·잘림·겹침 문제를 찾는다.</summary>
    static List<string> LayoutProblems(MainWindow window)
    {
        var problems = new List<string>();
        var root = (FrameworkElement)window.Content;
        foreach (var text in Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible && t.ActualWidth > 0))
        {
            if (text.FontSize < 10) problems.Add($"font {text.FontSize}: {text.Text}");
            // 말줄임·줄바꿈이 없는 글자는 받은 폭 안에 다 들어가야 한다
            if (text.TextTrimming == TextTrimming.None && text.TextWrapping == TextWrapping.NoWrap && !string.IsNullOrEmpty(text.Text))
            {
                // 창과 같은 글자 그리기 방식(Display)과 DPI 로 잰다
                double need = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                    new NumberSubstitution(), TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip)
                    .WidthIncludingTrailingWhitespace;
                if (need > text.ActualWidth + 0.5) problems.Add($"clipped {need:0.#}>{text.ActualWidth:0.#}: {text.Text}");
            }
        }

        foreach (var content in Descendants(root).OfType<ContentControl>().Where(c => c.Content is ProviderCard))
        {
            var name = ((ProviderCard)content.Content).Name;
            var card = Descendants(content).OfType<Border>().First();
            var cardRect = BoundsIn(card, window);
            foreach (var text in Descendants(card).OfType<TextBlock>().Where(t => t.IsVisible && t.ActualWidth > 0 &&
                         t.Text != null))
            {
                var r = BoundsIn(text, window);
                if (r.Left < cardRect.Left - 0.5 || r.Right > cardRect.Right + 0.5 || r.Top < cardRect.Top - 0.5 || r.Bottom > cardRect.Bottom + 0.5)
                    problems.Add($"{name}: outside card: {text.Text} {r} / {cardRect}");
            }
            var header = Descendants(card).OfType<DockPanel>().First();
            var parts = header.Children.OfType<FrameworkElement>().Where(e => e.IsVisible && e.ActualWidth > 0).Select(e => (e, r: BoundsIn(e, window))).ToList();
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                    if (Overlaps(parts[i].r, parts[j].r)) problems.Add($"{name}: header overlap {parts[i].e.GetType().Name}/{parts[j].e.GetType().Name}");

            var rings = Descendants(card).OfType<ItemsControl>().FirstOrDefault(i => i.ItemsSource is List<AccountUsageSlot>);
            if (rings == null) problems.Add($"{name}: account slots missing");
        }
        return problems;
    }

    static void TestResponsiveSizes()
    {
        var vm = BusyViewModel(out var cleanup);
        int saves = 0;
        var settings = new AppSettings { ShowCharacters = true, Topmost = false };
        var window = new MainWindow(vm, settings, _ => saves++) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true };
        try
        {
            window.Show();
            PumpDisplay();
            var sizes = new (string Name, double W, double H, LayoutMode Mode)[]
            {
                ("minimum", MainWindow.MinWindowWidth, MainWindow.MinWindowHeight, LayoutMode.Narrow),
                ("default", MainWindow.DefaultWidth, MainWindow.DefaultHeight, LayoutMode.Wide),
                ("medium", 520, MainWindow.MinWindowHeight, LayoutMode.Medium),
                ("narrow and tall", 360, 900, LayoutMode.Narrow),
                ("wide", 1100, 760, LayoutMode.Wide),
            };
            var rowHeights = new Dictionary<string, double>();
            foreach (var (name, w, h, mode) in sizes)
            {
                window.Width = w;
                window.Height = h;
                PumpDisplay(250);
                var root = (FrameworkElement)window.Content;
                var rows = Descendants(root).OfType<ContentControl>().Where(c => c.Content is ProviderCard)
                    .Select(c => (Name: ((ProviderCard)c.Content).Name, Rect: BoundsIn(c, window))).ToList();
                Check($"{name}: rows are Claude, Codex, Gemini, Grok from top to bottom and share the height",
                    rows.Select(r => r.Name).SequenceEqual(["Claude", "Codex", "Gemini", "Grok"]) &&
                    rows[0].Rect.Top < rows[1].Rect.Top && rows[1].Rect.Top < rows[2].Rect.Top &&
                    rows[2].Rect.Top < rows[3].Rect.Top &&
                    rows.Max(r => r.Rect.Height) - rows.Min(r => r.Rect.Height) <= 1 && rows[3].Rect.Bottom <= window.ActualHeight,
                    string.Join(", ", rows.Select(r => $"{r.Name}:{r.Rect.Top:0}+{r.Rect.Height:0}")));
                rowHeights[name] = rows[0].Rect.Height;
                Check($"{name}: layout mode follows the width", window.Mode == mode && window.ActualWidth == w && window.ActualHeight == h,
                    $"mode={window.Mode}, size={window.ActualWidth}x{window.ActualHeight}");
                var problems = LayoutProblems(window);
                Check($"{name}: text is at least 10 DIP without clipping or overlap", problems.Count == 0, string.Join("\n", problems.Take(6)));

                var lists = Descendants(root).OfType<ScrollViewer>().ToList();
                Check($"{name}: usage and all four characters remain visible without session lists",
                    lists.Count == 0 && window.CharactersShown && window.Claude.IsVisible && window.Codex.IsVisible &&
                    window.Gemini.IsVisible && window.Grok.IsVisible,
                    $"lists={lists.Count}, characters={window.CharactersShown}");

                foreach (var stage in new[] { window.Claude, window.Codex, window.Gemini, window.Grok })
                {
                    var stageBounds = BoundsIn(stage, window);
                    var row = Descendants(root).OfType<ContentControl>().First(c => c.Content is ProviderCard p && p.Name.Equals(stage.Character, StringComparison.OrdinalIgnoreCase));
                    var rowBounds = BoundsIn(row, window);
                    var accountScale = Descendants(row).OfType<Viewbox>().First(v => v.Name == "AccountScale");
                    var accountBounds = BoundsIn(accountScale, window);
                    Check($"{name}: usage stays left and {stage.Character} stays at the right edge",
                        stageBounds.Height <= 150.5 && Math.Abs(rowBounds.Right - stageBounds.Right - 6) <= 1 &&
                        accountBounds.Width is > 40 and <= 168.5 &&
                        Math.Abs(accountBounds.Left - rowBounds.Left - 14) <= 1 &&
                        stageBounds.Top >= rowBounds.Top - 0.5 &&
                        stageBounds.Bottom <= rowBounds.Bottom + 0.5 &&
                        accountBounds.Left >= rowBounds.Left && accountBounds.Right <= rowBounds.Right,
                        $"stage={stageBounds}, account={accountBounds}, row={rowBounds}");
                    var image = (Image)PrivateField<CharacterStage>("_img").GetValue(stage)!;
                    var body = (Rect)PrivateField<CharacterStage>("_bodyBounds").GetValue(stage)!;
                    var imageBounds = image.TransformToAncestor(window).TransformBounds(new Rect(
                        body.X * image.ActualWidth, body.Y * image.ActualHeight,
                        body.Width * image.ActualWidth, body.Height * image.ActualHeight));
                    Check($"{name}: {stage.Character} rendered image stays inside its row",
                        image.IsVisible && image.ActualHeight > 0 && imageBounds.Top >= rowBounds.Top - 1 &&
                        imageBounds.Bottom <= rowBounds.Bottom + 1 && imageBounds.Right <= rowBounds.Right + 1,
                        $"image={imageBounds}, row={rowBounds}");
                }

                if (mode == LayoutMode.Wide)
                {
                    foreach (var content in Descendants(root).OfType<ContentControl>().Where(c => c.Content is ProviderCard))
                    {
                        var provider = ((ProviderCard)content.Content).Name;
                        var usageRings = Descendants(content).OfType<UsageRing>().Where(r => r.IsVisible)
                            .Select(r => BoundsIn(r, window)).ToList();
                        var centers = usageRings.Select(r => r.Top + r.Height / 2).ToList();
                        bool distinctCenters = centers.Count < 2 || centers.Zip(centers.Skip(1)).All(p => Math.Abs(p.First - p.Second) > 1);
                        bool sameSize = usageRings.Count < 2 || usageRings.Max(r => r.Width) - usageRings.Min(r => r.Width) <= 1.0 &&
                            usageRings.Max(r => r.Height) - usageRings.Min(r => r.Height) <= 1.0;
                        Check($"{name}: {provider} account ring centers are vertically distinct and equal-sized",
                            distinctCenters && sameSize,
                            $"rings={string.Join(",", usageRings.Select(r => $"{r.Width:0.#}x{r.Height:0.#}@{r.Top + r.Height / 2:0.#}"))}");
                    }
                }

                {
                    var widths = new[] { window.Claude, window.Codex, window.Gemini, window.Grok }
                        .Select(s => (double)typeof(CharacterStage).GetMethod("BubbleTextWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s, null)!).ToList();
                    window.Gemini.SetMood(Mood.Waiting, ["사주세요… 아니,\n승인해 주세요!"]);
                    PumpDisplay(400);
                    var bubble = (Grid)PrivateField<CharacterStage>("_bubble").GetValue(window.Gemini)!;
                    var bubbleRect = BoundsIn(bubble, window.Gemini);
                    Check($"{name}: character bubbles keep at least 120 DIP of text width inside the row",
                        widths.All(x => x >= 120) && window.Gemini.HasArt && bubble.IsVisible &&
                        bubbleRect.Left >= -0.5 && bubbleRect.Top >= -0.5 && bubbleRect.Bottom <= window.Gemini.ActualHeight + 0.5,
                        $"widths={string.Join(",", widths.Select(x => x.ToString("0.#")))}, bubble={bubbleRect}");
                }
                if (name == "minimum") RenderFixture(window, "minimum");
            }
            Check("taller windows give each provider row more room", rowHeights["wide"] > rowHeights["default"] && rowHeights["narrow and tall"] > rowHeights["default"],
                string.Join(", ", rowHeights.Select(v => $"{v.Key}={v.Value:0}")));

            // 이 상태는 네 캐릭터와 계정 슬롯이 보이는 넓은 합성 화면이다.
            RenderFixture(window, "wide-large");

            // 폭만 줄여도 원·글자는 같은 크기이고 사용량은 왼쪽 위치를 유지한다.
            foreach (var provider in new[] { "Claude", "Codex", "Gemini", "Grok" })
            {
                window.Width = 1100;
                PumpDisplay();
                var wideUsage = MeasureUsageVisual(window, provider);
                window.Width = MainWindow.MinWindowWidth;
                PumpDisplay();
                var narrowUsage = MeasureUsageVisual(window, provider);
                Check($"{provider}: width changes preserve rendered usage ring and text sizes",
                    Math.Abs(wideUsage.Ring.Width - narrowUsage.Ring.Width) <= 0.5 &&
                    Math.Abs(wideUsage.Ring.Height - narrowUsage.Ring.Height) <= 0.5 &&
                    Math.Abs(wideUsage.Text.Width - narrowUsage.Text.Width) <= 0.5 &&
                    Math.Abs(wideUsage.Text.Height - narrowUsage.Text.Height) <= 0.5 &&
                    Math.Abs(wideUsage.Ring.Left - narrowUsage.Ring.Left) <= 1,
                    $"ring={wideUsage.Ring}->{narrowUsage.Ring}, text={wideUsage.Text}->{narrowUsage.Text}");
            }

            var before = new Size(window.Width, window.Height);
            var usageBefore = MeasureUsageVisual(window, "Claude");
            window.ApplyCharacters(false);
            PumpDisplay();
            var usageAfter = MeasureUsageVisual(window, "Claude");
            Check("turning characters off keeps the size and account geometry",
                window.Width == before.Width && window.Height == before.Height && !window.Claude.IsVisible && !window.Grok.IsVisible &&
                usageBefore == usageAfter && LayoutProblems(window).Count == 0,
                $"size={window.Width}x{window.Height}, usage={usageBefore}->{usageAfter}");
            window.ApplyCharacters(true);
            PumpDisplay();
            Check("layout changes never save settings by themselves", saves == 0, $"saves={saves}");
            window.ApplyCharacters(true);
            PumpDisplay();
            window.Width = 520;
            window.Height = 560;
            PumpDisplay();
            RenderFixture(window, "compact");
        }
        finally
        {
            window.Close();
            cleanup.Dispose();
        }
    }

    static (Rect Ring, Rect Text) MeasureUsageVisual(MainWindow window, string provider)
    {
        var root = (FrameworkElement)window.Content;
        var card = Descendants(root).OfType<ContentControl>()
            .First(c => c.Content is ProviderCard p && p.Name == provider);
        var ring = Descendants(card).OfType<UsageRing>().FirstOrDefault(r => r.IsVisible);
        var text = Descendants(card).OfType<TextBlock>()
            .FirstOrDefault(t => t.IsVisible && t.ActualWidth > 0 && t.Text.EndsWith('%'));
        return (ring == null ? default : BoundsIn(ring, window), text == null ? default : BoundsIn(text, window));
    }

    static void RenderFixture(MainWindow window, string name)
    {
        var root = (FrameworkElement)window.Content;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var path = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{name}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Check($"{name} synthetic fixture renders to PNG", File.Exists(path) && new FileInfo(path).Length > 0,
            $"path={path}, size={width}x{height}");
        Console.WriteLine($"INFO rendered {name} fixture: {path}");
    }

    static void TestBoundsPersistence()
    {
        var vm = BusyViewModel(out var cleanup);
        try
        {
            MainWindow Open(AppSettings settings, Action<AppSettings> save)
            {
                var w = new MainWindow(vm, settings, save) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true };
                w.Show();
                PumpDisplay();
                return w;
            }
            Rect Area(MainWindow w) => (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null)!;

            // 기본 자리를 먼저 열어 작업 영역을 알아낸다
            int saves = 0;
            var probe = Open(new AppSettings(), _ => saves++);
            var area = Area(probe);
            probe.Close();

            var saved = new AppSettings { WindowLeft = area.Left + 50, WindowTop = area.Top + 40, WindowWidth = 700, WindowHeight = 620 };
            var restored = Open(saved, _ => saves++);
            Check("saved size and position are restored", restored.Left == area.Left + 50 && restored.Top == area.Top + 40 &&
                restored.Width == 700 && restored.Height == 620, $"{restored.Left}/{restored.Top}/{restored.Width}/{restored.Height}");

            // 사용자가 끌어 크기를 바꾸고 놓으면(WM_EXITSIZEMOVE) 저장한다
            restored.Width = 760;
            restored.Height = 640;
            PumpDisplay();
            Check("resizing alone does not save before the drag ends", saves == 0, $"saves={saves}");
            var hwnd = new WindowInteropHelper(restored).Handle;
            SendMessage(hwnd, 0x0232, IntPtr.Zero, IntPtr.Zero);
            Check("finishing a resize saves the new bounds", saves == 1 && saved.WindowWidth == 760 && saved.WindowHeight == 640 &&
                saved.WindowLeft == restored.Left && saved.WindowTop == restored.Top, $"saves={saves}, {saved.WindowWidth}x{saved.WindowHeight}");
            restored.Close();

            var reopened = Open(saved, _ => saves++);
            Check("the saved size is used on the next start", reopened.Width == 760 && reopened.Height == 640, $"{reopened.Width}x{reopened.Height}");
            reopened.Close();

            var lost = new AppSettings
            {
                WindowLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2000,
                WindowTop = SystemParameters.VirtualScreenTop - 3000, WindowWidth = 9000, WindowHeight = 7000,
            };
            var pulled = Open(lost, _ => saves++);
            var pulledArea = Area(pulled);
            Check("bounds from a missing monitor are pulled inside the work area",
                pulled.Left >= pulledArea.Left - 0.5 && pulled.Top >= pulledArea.Top - 0.5 &&
                pulled.Left + pulled.Width <= pulledArea.Right + 0.5 && pulled.Top + pulled.Height <= pulledArea.Bottom + 0.5 &&
                pulled.Width <= pulledArea.Width + 0.5 && pulled.Height <= pulledArea.Height + 0.5,
                $"window={pulled.Left}/{pulled.Top}/{pulled.Width}/{pulled.Height}, area={pulledArea}");
            pulled.Close();

            var tiny = Open(new AppSettings { WindowWidth = 100, WindowHeight = 80 }, _ => saves++);
            Check("saved sizes below the minimum open at the minimum",
                tiny.Width == MainWindow.MinWindowWidth && tiny.Height == MainWindow.MinWindowHeight, $"{tiny.Width}x{tiny.Height}");
            tiny.Close();
            Check("opening and closing windows does not save bounds", saves == 1, $"saves={saves}");
        }
        finally { cleanup.Dispose(); }
    }
}
