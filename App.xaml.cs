using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;
using Microsoft.Win32;

namespace AIUsageMonitor;

public partial class App : Application
{
    const string MutexName = @"Local\AIUsageMonitor.SingleInstance";
    const string ShowEventName = @"Local\AIUsageMonitor.Show";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "AIUsageMonitor";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    AppSettings _settings = null!;
    MonitorService _monitor = null!;
    MainViewModel _vm = null!;
    MainWindow _window = null!;
    Tray _tray = null!;
    Alerts _alerts = null!;
    SecondaryAccountService _secondaryAccounts = null!;
    string _accountsSignature = "";
    readonly MoodTracker _claudeMood = new("claude");
    readonly MoodTracker _codexMood = new("codex");
    readonly MoodTracker _geminiMood = new("gemini");
    readonly MoodTracker _grokMood = new("grok");
    // "N분 전"·남은 시간·오래됨 표시만 다시 그리는 시계 (카드를 교체하지 않는다)
    DispatcherTimer? _clock;
    bool _firstUpdate = true;
    bool _demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 이미 실행 중이면 기존 창을 띄우고 종료
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"unhandled: {ex.Exception}");
            ex.Handled = true;
        };

        _settings = AppSettings.Load();
        // 캐릭터 기본 표시 마이그레이션을 한 번만 적용한다
        if (_settings.Migrated) _settings.Save();
        _vm = new MainViewModel();
        _monitor = new MonitorService(_settings);
        _window = new MainWindow(_vm, _settings);
        _tray = new Tray(_settings);
        // 설정을 읽지 못했으면 시작할 때 한 번만 알린다
        if (Tray.SettingsLoadNotice(_settings) is { } notice) _tray.Balloon("AI 사용량 모니터", notice, true);
        _alerts = new Alerts(_settings, _tray);
        _secondaryAccounts = new SecondaryAccountService(_settings);

        WireUp();
        SyncStartupRegistration();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _window.ShowAndActivate());
        }) { IsBackground = true }.Start();

        _monitor.Start();
        _secondaryAccounts.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => _vm.Tick();
        _clock.Start();
        if (!e.Args.Contains("--minimized")) _window.ShowAndActivate();

        // 진단용: 캐릭터 상태를 하나씩 띄워 PNG로 저장하고 종료
        var moodsIdx = Array.IndexOf(e.Args, "--render-moods");
        if (moodsIdx >= 0 && moodsIdx + 1 < e.Args.Length) _ = RenderMoodsAsync(e.Args[moodsIdx + 1]);

        // 진단용: 데이터를 잠시 모은 뒤 창을 PNG로 저장하고 종료
        var renderIdx = Array.IndexOf(e.Args, "--render-png");
        if (renderIdx >= 0 && renderIdx + 1 < e.Args.Length)
        {
            var path = e.Args[renderIdx + 1];
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SaveWindowPng(path);
                ExitApp();
            };
            timer.Start();
        }
    }

    async Task RenderMoodsAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(6000);
        _demo = true;
        foreach (var mood in Enum.GetValues<Mood>())
        {
            _window.Claude.SetMood(mood, [$"[{mood}] 말풍선 테스트"]);
            _window.Codex.SetMood(mood, [$"[{mood}] 말풍선"]);
            _window.Gemini.SetMood(mood, [$"[{mood}] 말풍선"]);
            _window.Grok.SetMood(mood, [$"[{mood}] 말풍선"]);
            await Task.Delay(1500);
            if (mood == Mood.Working)
            {
                _window.Claude.PopText("+3%", System.Windows.Media.Color.FromRgb(0x9B, 0xF0, 0xC0));
                _window.Codex.PopText("+1%", System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x6B));
                _window.Gemini.PopText("+2%", System.Windows.Media.Color.FromRgb(0x9B, 0xF0, 0xC0));
                await Task.Delay(500);
            }
            if (mood == Mood.Happy) { _window.Claude.Celebrate(); _window.Codex.Celebrate(); _window.Gemini.Celebrate(); _window.Grok.Celebrate(); await Task.Delay(300); }
            SaveWindowPng(System.IO.Path.Combine(dir, $"mood_{(int)mood}_{mood}.png"));
        }
        ExitApp();
    }

    void SaveWindowPng(string path)
    {
        FrameworkElement root = _window;
        root.UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    void WireUp()
    {
        _monitor.Changed += Render;
        _secondaryAccounts.Changed += Render;

        _window.RefreshRequested += RefreshAllAccounts;
        _window.AccountsRequested += ShowAccounts;
        _window.Accounts.AddClaudeRequested += async () => await _secondaryAccounts.AddAsync(AccountProvider.Claude);
        _window.Accounts.AddCodexRequested += async () => await _secondaryAccounts.AddAsync(AccountProvider.Codex);
        _window.Accounts.RefreshRequested += RefreshAllAccounts;
        _window.SecondaryAccountSwapRequested += (first, second) =>
        {
            if (_secondaryAccounts.SwapAccounts(first, second)) _settings.Save();
        };
        _window.TopmostToggled += SetTopmost;
        _window.IsVisibleChanged += (_, _) =>
        {
            if (!_window.IsVisible && !_settings.TrayHintShown && !_window.AllowClose)
            {
                _settings.TrayHintShown = true;
                _settings.Save();
                _tray.Balloon("AI 사용량 모니터", "트레이에서 계속 실행됩니다. 아이콘을 클릭하면 다시 열립니다.", false);
            }
        };

        _tray.OpenRequested += () => _window.ShowAndActivate();
        _tray.RefreshRequested += RefreshAllAccounts;
        _tray.AccountsRequested += ShowAccounts;
        _tray.ExitRequested += ExitApp;
        _tray.TopmostChanged += SetTopmost;
        _tray.GeminiPlanChanged += plan =>
        {
            _settings.GeminiPlanOverride = plan;
            _settings.Save();
            _tray.SetGeminiPlan(plan);
            Render();
        };
        _tray.CharactersChanged += v =>
        {
            _settings.ShowCharacters = v;
            _settings.Save();
            _window.ApplyCharacters(v);
        };
        _tray.StartupChanged += v =>
        {
            _settings.StartWithWindows = v;
            _settings.Save();
            SyncStartupRegistration();
        };
        _tray.UsageAlertsChanged += v => { _settings.UsageAlerts = v; _settings.Save(); };
        _tray.ActivityAlertsChanged += v => { _settings.ActivityAlerts = v; _settings.Save(); };
        _tray.LoginRequested += () => _monitor.ClaudeWeb.ShowLogin();
        _tray.ClaudeDesktopChanged += value => _monitor.SetClaudeDesktopEnabled(value);
        _tray.LogoutRequested += async () =>
        {
            await _monitor.LogoutClaudeAsync();
            _tray.SetLoggedIn(false);
        };
        _monitor.ClaudeWeb.LoginWindowClosed += () => _tray.SetLoggedIn(true);
        _tray.GeminiUnblockRequested += async () =>
        {
            try
            {
                await GeminiBlockAction.RunAsync(_monitor, text => GeminiBlockAction.Confirm(_window, text));
            }
            catch (Exception ex) { Log.Write($"gemini unblock failed: {ex.Message}"); }
        };
    }

    void SetTopmost(bool on)
    {
        _settings.Topmost = on;
        _settings.Save();
        _window.ApplyTopmost(on);
        _tray.SetTopmost(on);
    }

    void RefreshAllAccounts()
    {
        _monitor.RefreshAll();
        _ = _secondaryAccounts.RefreshAsync(force: true);
    }

    void ShowAccounts()
    {
        _window.ShowAndActivate();
        _window.SetAccountsOpen(true);
        Render();
    }

    void Render()
    {
        var claude = Presenter.Claude(_monitor, _settings, () =>
        {
            if (_settings.ClaudeDesktopEnabled)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("claude://") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write($"Claude Desktop open failed: {ex.Message}"); }
            }
            else _monitor.ClaudeWeb.ShowLogin();
        });
        var codex = Presenter.Codex(_monitor, _settings);
        var gemini = Presenter.Gemini(_monitor, _settings);
        var grok = Presenter.Grok(_monitor, _settings);
        claude.AccountSlots = AccountUsageSlot.Create(claude, _secondaryAccounts.Accounts);
        codex.AccountSlots = AccountUsageSlot.Create(codex, _secondaryAccounts.Accounts);
        // Gemini 는 서브 계정을 지원하지 않는다
        gemini.AccountSlots = AccountUsageSlot.Create(gemini, _secondaryAccounts.Accounts, secondarySlots: 0);
        grok.AccountSlots = AccountUsageSlot.Create(grok, _secondaryAccounts.Accounts, secondarySlots: 0);
        _vm.Update(claude, codex, gemini, grok, "");
        if (_window.IsAccountsOpen)
        {
            var accounts = AccountUsageCard.Create(claude, codex, _secondaryAccounts, gemini, grok);
            var signature = string.Join('\n', accounts.Select(x => x.Signature));
            if (_accountsSignature != signature)
            {
                _accountsSignature = signature;
                _window.Accounts.Update(accounts, _secondaryAccounts.LoginBusy);
            }
        }

        if (!_demo)
        {
            Animate(_window.Claude, _claudeMood.Update(claude));
            Animate(_window.Codex, _codexMood.Update(codex));
            Animate(_window.Gemini, _geminiMood.Update(gemini));
            Animate(_window.Grok, _grokMood.Update(grok));
        }

        _tray.UpdateIcon(claude.MaxUsed, codex.MaxUsed, gemini.MaxUsed, grok.MaxUsed);
        _tray.SetTooltip(Tray.Tooltip([claude, codex, gemini, grok]));
        _tray.SetGeminiBlocked(_monitor.GeminiUnsupported);

        _alerts.CheckUsage("Claude", claude.RawWindows);
        _alerts.CheckUsage("Codex", codex.RawWindows);
        // Gemini 카드의 RawWindows 는 gemini 그룹 창만 담는다 (Claude·GPT 그룹은 툴팁 전용)
        _alerts.CheckUsage("Gemini", gemini.RawWindows);
        _alerts.CheckUsage("Grok", grok.RawWindows);
        if (_monitor.ClaudeLocal != null) _alerts.CheckActivity("Claude", Presenter.ClaudeSessions(_monitor));
        if (_monitor.CodexLocal is { } xl) _alerts.CheckActivity("Codex", xl.Threads);
        if (_monitor.GeminiLocal is { } gl) _alerts.CheckActivity("Gemini", gl.Sessions);
        if (_firstUpdate && _monitor.ClaudeLocal != null && _monitor.CodexLocal != null)
        {
            _firstUpdate = false;
            _alerts.MarkBaseline();
        }
    }

    static void Animate(CharacterStage stage, MoodTracker.Result r)
    {
        stage.SetMood(r.Mood, r.Lines, r.Keys);
        if (r.Reset) stage.Celebrate();
        else if (r.UsageDelta is double d) stage.PopText($"+{Fmt.Pct(d)}", r.DeltaColor);
    }

    void SyncStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (_settings.StartWithWindows && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\" --minimized");
            else if (key.GetValue(RunValue) != null)
                key.DeleteValue(RunValue, false);
        }
        catch (Exception ex)
        {
            Log.Write($"startup registration failed: {ex.Message}");
        }
    }

    void ExitApp()
    {
        _window.AllowClose = true;
        _settings.Save();
        _clock?.Stop();
        _monitor.Changed -= Render;
        _secondaryAccounts.Changed -= Render;
        _secondaryAccounts.Dispose();
        _monitor.Dispose();
        _tray.Dispose();
        _window.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
