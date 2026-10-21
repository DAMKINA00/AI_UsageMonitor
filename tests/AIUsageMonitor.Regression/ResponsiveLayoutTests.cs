using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
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
        TestDockBounds();
    }

    static void TestCharacterDefaultMigration()
    {
        Check("characters are on by default", new AppSettings().ShowCharacters, "default off");

        var legacy = AppSettings.Parse("""{"ShowCharacters": true, "Topmost": true}""");
        Check("legacy settings turn characters on once and keep other values",
            legacy.ShowCharacters && legacy.Migrated && legacy.SettingsVersion == AppSettings.CurrentSettingsVersion && legacy.Topmost,
            $"show={legacy.ShowCharacters}, migrated={legacy.Migrated}, version={legacy.SettingsVersion}");

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

    /// <summary>이 테스트 소스 파일의 위치에서 위로 올라가 저장소 루트(AIUsageMonitor.csproj가 있는 폴더)를 찾는다.</summary>
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
            files.Count > 30 && bad.Count == 0 && correct > 0,
            $"files={files.Count}, U+CA29 nicknames={correct}, bad={string.Join(", ", bad.Take(8))}");
    }

    /// <summary>네 서비스 모두 값·안내가 있는 합성 모델. Window/Platform 테스트가 공유한다.</summary>
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
            Notice = "데스크톱 연결 확인: 합성 오류 문구", ActionText = "Claude 데스크톱 열기", ActionCommand = new RelayCommand(() => { }),
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

    static void UpdateViewModel(MainViewModel vm, ProviderCard claude, ProviderCard codex, ProviderCard? gemini,
        ProviderCard? grok, string footer)
        => vm.Update(claude, codex, gemini, grok, footer);

    sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var item in items) item.Dispose(); }
    }

    static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    static T FindNamed<T>(FrameworkElement owner, string name) where T : class
    {
        var found = owner.FindName(name);
        if (found == null && owner is Window { Content: FrameworkElement content }) found = content.FindName(name);
        return found as T ?? throw new InvalidOperationException($"Named element not found: {name}");
    }

    static void TestDockBounds()
    {
        var area = new Rect(100, 200, 1200, 800);
        var four = MainWindow.DockBounds(area, 4);
        Check("four-account docking uses the default stage size", four.Width == MainWindow.DefaultWidth &&
            four.Height == MainWindow.DefaultHeight && four.Right == area.Right - 6 && four.Bottom == area.Bottom,
            $"bounds={four}");

        var many = MainWindow.DockBounds(area, 8);
        Check("many-account docking grows left while staying on the right edge", many.Width == Math.Min(area.Width - 12, 8 * MainWindow.PlatformWidth) &&
            many.Right == area.Right - 6 && many.Left < four.Left,
            $"four={four}, many={many}");

        var inset = MainWindow.DockBounds(area, 4, 23);
        Check("taskbar inset moves only the bottom edge", inset.Width == four.Width && inset.Left == four.Left && inset.Bottom == area.Bottom - 23,
            $"inset={inset}");

        var negative = MainWindow.DockBounds(area, -20, -10);
        Check("invalid docking inputs stay finite and inside the work area", negative.Width > 0 && negative.Height > 0 &&
            negative.Left >= area.Left && negative.Top >= area.Top && negative.Right <= area.Right && negative.Bottom <= area.Bottom,
            $"negative={negative}");
    }

    static void RenderFixture(MainWindow window, string name)
    {
        if (window.Content is not FrameworkElement root) return;
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
}
