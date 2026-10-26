using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
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
    TaskbarWidget _taskbar = null!;
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
    bool _hosted;
    HostedProtocolWriter? _hostedProtocol;
    CancellationTokenSource? _hostedInputLifetime;
    string? _hostedState;
    int _exitStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _hosted = e.Args.Any(x => string.Equals(x, "--hosted", StringComparison.OrdinalIgnoreCase));
        if (_hosted) _hostedProtocol = new HostedProtocolWriter(Console.Out);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (_hosted)
            {
                _hostedProtocol!.Error("AI 사용량 모니터가 이미 실행 중입니다. 기존 단독 실행을 종료한 뒤 호스트 모드로 시작하세요.");
                Environment.ExitCode = 2;
            }
            else
            {
                // 이미 실행 중이면 기존 창을 띄우고 종료
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            }
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
        Action<string, string, bool>? notification = _hosted ? _hostedProtocol!.Notification : null;
        _tray = new Tray(_settings, visible: !_hosted, notification: notification);
        _taskbar = new TaskbarWidget();
        // 설정을 읽지 못했으면 시작할 때 한 번만 알린다
        if (Tray.SettingsLoadNotice(_settings) is { } notice) _tray.Balloon("AI 사용량 모니터", notice, true);
        _alerts = new Alerts(_settings, _tray);
        _secondaryAccounts = new SecondaryAccountService(_settings);

        WireUp();
        _taskbar.SetEnabled(_settings.ShowTaskbarUsage);
        if (!_hosted) SyncStartupRegistration();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _window.ShowAndActivate());
        }) { IsBackground = true }.Start();

        _monitor.Start();
        _secondaryAccounts.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => { _vm.Tick(); _taskbar.Tick(); };
        _clock.Start();
        if (!_hosted && !e.Args.Contains("--minimized")) _window.ShowAndActivate();

        if (_hosted)
        {
            EmitHostedState(force: true);
            StartHostedInput();
        }

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
                var taskbarPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "",
                    System.IO.Path.GetFileNameWithoutExtension(path) + "-taskbar.png");
                if (_taskbar.IsVisible && _taskbar.Opacity > 0) SaveWindowPng(taskbarPath, _taskbar);
                System.IO.File.WriteAllText(path + ".placement.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    Main = new { _window.Left, _window.Top, _window.Width, _window.Height, _window.IsVisible },
                    Taskbar = new { _taskbar.Left, _taskbar.Top, _taskbar.Width, _taskbar.Height, _taskbar.IsVisible,
                        _taskbar.Opacity, _taskbar.DesktopInset },
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
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

    void SaveWindowPng(string path, FrameworkElement? target = null)
    {
        FrameworkElement root = target ?? _window;
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

        _window.Accounts.AddClaudeRequested += async () => await _secondaryAccounts.AddAsync(AccountProvider.Claude);
        _window.Accounts.AddCodexRequested += async () => await _secondaryAccounts.AddAsync(AccountProvider.Codex);
        _window.Accounts.RefreshRequested += RefreshAllAccounts;
        _window.SecondaryAccountSwapRequested += (first, second) =>
        {
            if (_secondaryAccounts.SwapAccounts(first, second)) _settings.Save();
        };
        _window.IsVisibleChanged += (_, _) =>
        {
            if (!_window.IsVisible && !_settings.TrayHintShown && !_window.AllowClose)
            {
                _settings.TrayHintShown = true;
                _settings.Save();
                _tray.Balloon("AI 사용량 모니터", "트레이에서 계속 실행됩니다. 아이콘을 클릭하면 다시 열립니다.", false);
            }
            EmitHostedState();
        };

        _tray.OpenRequested += ShowMonitor;
        _tray.HideRequested += HideMonitor;
        _taskbar.OpenRequested += ShowMonitor;
        _taskbar.RefreshRequested += RefreshAllAccounts;
        _taskbar.PlacementChanged += () => _window.SetTaskbarInset(_taskbar.DesktopInset);
        _taskbar.HideRequested += () => SetTaskbarUsage(false);
        _tray.TaskbarUsageChanged += SetTaskbarUsage;
        _tray.RefreshRequested += RefreshAllAccounts;
        _tray.AccountsRequested += ShowAccounts;
        _tray.ExitRequested += ExitApp;
        _tray.TopmostChanged += SetTopmost;
        _tray.CharactersChanged += SetCharacters;
        _tray.StartupChanged += SetStartup;
        _tray.UsageAlertsChanged += SetUsageAlerts;
        _tray.ActivityAlertsChanged += SetActivityAlerts;
        _tray.LoginRequested += RequestClaudeLogin;
        _tray.ClaudeDesktopChanged += SetClaudeDesktop;
        _tray.LogoutRequested += () => _ = LogoutClaudeAsync();
        _monitor.ClaudeWeb.LoginWindowClosed += () =>
        {
            _tray.SetLoggedIn(true);
            EmitHostedState();
        };
    }

    void ShowMonitor() => _window.ShowAndActivate();

    void HideMonitor() => _window.Hide();

    void SetCharacters(bool on)
    {
        _settings.ShowCharacters = on;
        _settings.Save();
        _window.ApplyCharacters(on);
        EmitHostedState();
    }

    void SetStartup(bool on)
    {
        _settings.StartWithWindows = on;
        _settings.Save();
        if (!_hosted) SyncStartupRegistration();
        EmitHostedState();
    }

    void SetUsageAlerts(bool on)
    {
        _settings.UsageAlerts = on;
        _settings.Save();
        EmitHostedState();
    }

    void SetActivityAlerts(bool on)
    {
        _settings.ActivityAlerts = on;
        _settings.Save();
        EmitHostedState();
    }

    void SetClaudeDesktop(bool on)
    {
        _monitor.SetClaudeDesktopEnabled(on);
        EmitHostedState();
    }

    void RequestClaudeLogin() => _monitor.ClaudeWeb.ShowLogin();

    async Task LogoutClaudeAsync()
    {
        await _monitor.LogoutClaudeAsync();
        _tray.SetLoggedIn(false);
        EmitHostedState();
    }

    void SetTopmost(bool on)
    {
        _settings.Topmost = on;
        _settings.Save();
        _window.ApplyTopmost(on);
        _tray.SetTopmost(on);
        EmitHostedState();
    }

    void SetTaskbarUsage(bool on)
    {
        _settings.ShowTaskbarUsage = on;
        _settings.Save();
        _tray.SetTaskbarUsage(on);
        _taskbar.SetEnabled(on);
        EmitHostedState();
    }

    void RefreshAllAccounts()
    {
        _monitor.RefreshAll();
        _ = _secondaryAccounts.RefreshAsync(force: true);
        EmitHostedState();
    }

    void ShowAccounts()
    {
        _window.ShowAndActivate();
        _window.SetAccountsOpen(true);
        Render();
    }

    void StartHostedInput()
    {
        _hostedInputLifetime = new CancellationTokenSource();
        var input = Console.OpenStandardInput();
        _ = Task.Run(async () =>
        {
            try
            {
                await HostedProtocol.ReadLinesAsync(input, HandleHostedInput,
                    _hostedInputLifetime.Token);
                DispatchHostedExit();
            }
            catch (OperationCanceledException) when (_hostedInputLifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Write($"hosted input failed: {ex.Message}");
                _hostedProtocol?.Error("호스트 입력을 읽지 못했습니다.");
                DispatchHostedExit();
            }
        }, _hostedInputLifetime.Token);
    }

    void HandleHostedInput(HostedInputLine line)
    {
        if (line.Error is { } inputError)
        {
            _hostedProtocol?.Error(inputError);
            return;
        }

        if (!HostedProtocol.TryParseCommand(line.Text!, out var command, out var error))
        {
            _hostedProtocol?.Error(error);
            return;
        }

        try { Dispatcher.BeginInvoke(new Action(() => _ = HandleHostedCommandAsync(command))); }
        catch (InvalidOperationException) { }
    }

    void DispatchHostedExit()
    {
        if (Volatile.Read(ref _exitStarted) != 0) return;
        try { Dispatcher.BeginInvoke(new Action(ExitApp)); }
        catch (InvalidOperationException) { }
    }

    async Task HandleHostedCommandAsync(HostedCommand command)
    {
        if (Volatile.Read(ref _exitStarted) != 0) return;
        try
        {
            switch (command.Name)
            {
                case "show": ShowMonitor(); break;
                case "hide": HideMonitor(); break;
                case "refresh": RefreshAllAccounts(); break;
                case "accounts": ShowAccounts(); break;
                case "topmost": SetTopmost(command.BoolValue!.Value); break;
                case "characters": SetCharacters(command.BoolValue!.Value); break;
                case "taskbar": SetTaskbarUsage(command.BoolValue!.Value); break;
                case "startup": SetStartup(command.BoolValue!.Value); break;
                case "usage-alerts": SetUsageAlerts(command.BoolValue!.Value); break;
                case "activity-alerts": SetActivityAlerts(command.BoolValue!.Value); break;
                case "claude-desktop": SetClaudeDesktop(command.BoolValue!.Value); break;
                case "claude-login": RequestClaudeLogin(); break;
                case "claude-logout": await LogoutClaudeAsync(); break;
                case "data-folder": _tray.OpenDataFolder(); break;
                case "quit": ExitApp(); return;
            }
            EmitHostedState();
        }
        catch (Exception ex)
        {
            Log.Write($"hosted command '{command.Name}' failed: {ex.Message}");
            _hostedProtocol?.Error($"명령 실행에 실패했습니다: {ex.Message}");
        }
    }

    void EmitHostedState(bool force = false)
    {
        if (!_hosted || Volatile.Read(ref _exitStarted) != 0) return;
        var state = new
        {
            visible = _window.IsVisible && _window.WindowState != WindowState.Minimized,
            topmost = _window.Topmost,
            characters = _window.CharactersShown,
            taskbar = _settings.ShowTaskbarUsage,
            startup = _settings.StartWithWindows,
            usageAlerts = _settings.UsageAlerts,
            activityAlerts = _settings.ActivityAlerts,
            claudeDesktop = _settings.ClaudeDesktopEnabled,
            claudeLoggedIn = _tray.ClaudeLoggedIn,
        };
        var signature = JsonSerializer.Serialize(state);
        if (!force && signature == _hostedState) return;
        _hostedState = signature;
        _hostedProtocol!.State(state);
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
        var grok = Presenter.Grok(_monitor, _settings);
        claude.AccountSlots = AccountUsageSlot.Create(claude, _secondaryAccounts.Accounts,
            _secondaryAccounts.Accounts.Count(a => a.Settings.Provider == AccountProvider.Claude));
        codex.AccountSlots = AccountUsageSlot.Create(codex, _secondaryAccounts.Accounts,
            _secondaryAccounts.Accounts.Count(a => a.Settings.Provider == AccountProvider.Codex));
        grok.AccountSlots = AccountUsageSlot.Create(grok, _secondaryAccounts.Accounts, secondarySlots: 0);
        _vm.Update(claude, codex, grok, "");
        _taskbar.Update([claude, codex, grok]);
        if (_window.IsAccountsOpen)
        {
            var accounts = AccountUsageCard.Create(claude, codex, _secondaryAccounts, grok);
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
            Animate(_window.Grok, _grokMood.Update(grok));
        }

        _tray.UpdateIcon(claude.MaxUsed, codex.MaxUsed, grok.MaxUsed);
        _tray.SetTooltip(Tray.Tooltip([claude, codex, grok]));

        _alerts.CheckUsage("Claude", claude.RawWindows);
        _alerts.CheckUsage("Codex", codex.RawWindows);
        _alerts.CheckUsage("Grok", grok.RawWindows);
        if (_monitor.ClaudeLocal != null) _alerts.CheckActivity("Claude", Presenter.ClaudeSessions(_monitor));
        if (_monitor.CodexLocal is { } xl) _alerts.CheckActivity("Codex", xl.Threads);
        if (_firstUpdate && _monitor.ClaudeLocal != null && _monitor.CodexLocal != null)
        {
            _firstUpdate = false;
            _alerts.MarkBaseline();
        }
        EmitHostedState();
    }

    static void Animate(CharacterStage stage, MoodTracker.Result r)
    {
        // Recovery speech belongs to the event, never to the repeating mood dialogue pool.
        stage.SetMood(r.Mood, r.Reset ? [] : r.Lines, r.Reset ? [] : r.Keys);
        if (r.Reset)
        {
            stage.Celebrate();
            if (r.ResetAnnouncement is { } announcement) stage.ShowAnnouncement(announcement);
        }
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
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        try { _hostedInputLifetime?.Cancel(); } catch (ObjectDisposedException) { }
        _window.AllowClose = true;
        _settings.Save();
        _clock?.Stop();
        _monitor.Changed -= Render;
        _secondaryAccounts.Changed -= Render;
        _secondaryAccounts.Dispose();
        _monitor.Dispose();
        _tray.Dispose();
        _taskbar.Dispose();
        _window.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
