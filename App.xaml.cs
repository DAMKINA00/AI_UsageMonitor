using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;
using Microsoft.Win32;

namespace AIUsageMonitor;

public partial class App : Application
{
    // A separate data folder (AIUSAGEMONITOR_DATA_DIR, tests and trial runs) is a separate instance.
    static readonly string MutexName = @"Local\AIUsageMonitor.SingleInstance" + InstanceSuffix();
    static readonly string ShowEventName = @"Local\AIUsageMonitor.Show" + InstanceSuffix();
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
    /// <summary>Hosted by ChatterBox: its account snapshot is the only account list and usage source.</summary>
    HostedAccountsSnapshot? _hostedAccounts;
    /// <summary>캐릭터를 눌러 묻는 질문: 캐릭터마다 하나만 기다린다.</summary>
    readonly CharacterAsk _asks = new();
    readonly Dictionary<string, CharacterAskWindow> _askWindows = new(StringComparer.Ordinal);
    BoardroomShortcut? _boardroomShortcut;
    DispatcherTimer? _boardroomSizeSave;
    CharacterMeetingWindow? _meetingWindow;
    readonly List<(MeetingMessage Message, bool Draft)> _meetingPosts = [];
    string? _meetingActive, _meetingPending, _meetingFolder;
    string _meetingDraft = "", _meetingStatus = "", _submittedAgenda = "";
    string[]? _meetingMembers, _meetingResting;
    long _meetingSequence;
    bool _meetingSnapshotLoaded;
    int _exitStarted;

    static string InstanceSuffix()
    {
        if (Environment.GetEnvironmentVariable(Paths.DataDirVariable) is not { Length: > 0 } dir) return "";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(dir).ToUpperInvariant()));
        return "." + Convert.ToHexString(hash)[..16];
    }

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
        // Hosted: no usage or account queries of its own; ChatterBox pushes its accounts and usage.
        _monitor = new MonitorService(_settings) { UsageQueriesEnabled = !_hosted };
        _window = new MainWindow(_vm, _settings);
        Action<string, string, bool>? notification = _hosted ? _hostedProtocol!.Notification : null;
        _tray = new Tray(_settings, visible: !_hosted, notification: notification);
        _taskbar = new TaskbarWidget();
        // 설정을 읽지 못했으면 시작할 때 한 번만 알린다
        if (Tray.SettingsLoadNotice(_settings) is { } notice) _tray.Balloon("AI 사용량 모니터", notice, true);
        _alerts = new Alerts(_settings, _tray);
        _secondaryAccounts = new SecondaryAccountService(_settings);
        if (_hosted)
        {
            _window.Accounts.SetManagedByChatterBox();
            _window.AskEnabled = true;
            _window.CharacterClicked += OpenAsk;
            _boardroomShortcut = new BoardroomShortcut(Dispatcher, ToggleMeeting);
            if (!_boardroomShortcut.IsActive) Log.Write("Boardroom shortcut could not be registered.");
        }

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
        if (!_hosted) _secondaryAccounts.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => { _vm.Tick(); _taskbar.Tick(); ExpireAsks(); };
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
            _window.Orch.SetMood(mood, [$"[{mood}] 말풍선"]);
            _window.Claude.SetMood(mood, [$"[{mood}] 말풍선 테스트"]);
            _window.Gpt.SetMood(mood, [$"[{mood}] 말풍선"]);
            _window.Gemini.SetMood(mood, [$"[{mood}] 말풍선"]);
            _window.Grok.SetMood(mood, [$"[{mood}] 말풍선"]);
            await Task.Delay(1500);
            if (mood == Mood.Working)
            {
                _window.Claude.PopText("+3%", System.Windows.Media.Color.FromRgb(0x9B, 0xF0, 0xC0));
                _window.Gpt.PopText("+1%", System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x6B));
                _window.Gemini.PopText("+2%", System.Windows.Media.Color.FromRgb(0x9B, 0xF0, 0xC0));
                await Task.Delay(500);
            }
            if (mood == Mood.Happy) { _window.Orch.Celebrate(); _window.Claude.Celebrate(); _window.Gpt.Celebrate(); _window.Gemini.Celebrate(); _window.Grok.Celebrate(); await Task.Delay(300); }
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
        _tray.CharactersChanged += SetAllCharacters;
        _tray.CharacterChanged += SetCharacter;
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

    /// <summary>예전 단일 스위치: 끄면 모두 숨기고, 켜면 마지막 선택(없으면 다섯 모두)을 되살린다.</summary>
    void SetAllCharacters(bool on)
    {
        _settings.SetAllCharacters(on);
        ApplyCharacterSelection();
    }

    /// <summary>캐릭터 하나만 켜거나 끈다. 다른 캐릭터와 창 위치는 그대로다.</summary>
    void SetCharacter(string id, bool on)
    {
        _settings.SetCharacters(CharacterSelection.Set(_settings.EnabledCharacters, id, on));
        ApplyCharacterSelection();
    }

    void ApplyCharacterSelection()
    {
        _settings.Save();
        _window.ApplyCharacters(_settings.EnabledCharacters);
        _tray.SetCharacters(_settings.EnabledCharacters);
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
        if (_hosted)
        {
            // ChatterBox refreshes its accounts and pushes the result back.
            _monitor.RefreshAll();
            _hostedProtocol!.Refresh();
            return;
        }
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
            if (command.Name is "claude-login" or "claude-logout" or "claude-desktop")
            {
                _hostedProtocol?.Error("계정은 ChatterBox에서 관리합니다. ChatterBox의 CLI 계정 관리를 사용하세요.");
                return;
            }
            switch (command.Name)
            {
                case "chatterbox-accounts": ApplyHostedAccounts(command.Data!.Value); break;
                case "show": ShowMonitor(); break;
                case "hide": HideMonitor(); break;
                case "refresh": RefreshAllAccounts(); break;
                case "accounts": ShowAccounts(); break;
                case "topmost": SetTopmost(command.BoolValue!.Value); break;
                case "characters": SetAllCharacters(command.BoolValue!.Value); break;
                case "meeting-open": OpenMeeting(); break;
                case "meeting-event":
                    if (MeetingEvent.Read(command.Data!.Value) is { } meeting) ApplyMeeting(meeting);
                    break;
                case "meeting-reset":
                    if (MeetingEvent.Decode<MeetingReset>(command.Data!.Value) is { } room && room.Seq >= _meetingSequence
                        && room.Status is { Length: <= 500 } && MeetingEvent.IsRoster(room.Connected))
                    {
                        _meetingSnapshotLoaded = true;
                        _meetingSequence = room.Seq;
                        _meetingActive = room.RequestId;
                        _meetingMembers = room.Connected;
                        _meetingResting = MeetingEvent.IsRoster(room.Resting) ? room.Resting : null;
                        _meetingStatus = room.Status;
                        _meetingPosts.Clear();
                        _meetingWindow?.ResetMessages();
                        RefreshMeeting();
                    }
                    break;
                case "meeting-restore":
                    if (MeetingEvent.Decode<MeetingRestore>(command.Data!.Value) is { } restore && restore.Message?.IsValid(restore.Draft) == true)
                    {
                        UpdateMeetingPost(restore.Message, restore.Draft);
                        RefreshMeeting();
                    }
                    break;
                case "character-event":
                    if (!CharacterEvent.TryParse(command.Data!.Value, out var characterEvent, out var eventError))
                    {
                        _hostedProtocol?.Error(eventError);
                        return;
                    }
                    // 실제 이벤트만 그 캐릭터 말풍선에 한 번 (꺼진 캐릭터면 버린다; 알림은 ChatterBox 가 이미 띄운다)
                    _window.ShowCharacterEvent(characterEvent);
                    break;
                case "character-reply":
                    if (!CharacterReply.TryParse(command.Data!.Value, out var reply, out var replyError))
                    {
                        _hostedProtocol?.Error(replyError);
                        return;
                    }
                    ApplyCharacterReply(reply);
                    break;
                case "character":
                    if (!CharacterSelection.TryParseCommand(command.Data!.Value, out var id, out var on, out var characterError))
                    {
                        _hostedProtocol?.Error(characterError);
                        return;
                    }
                    SetCharacter(id, on);
                    break;
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
            characterSet = _window.ShownCharacters.ToArray(),
            // 표시할 수 있는 캐릭터와 지원하는 캐릭터 기능 (호스트는 이 값이 있을 때만 이벤트·질문을 쓴다)
            characterIds = CharacterSelection.All.ToArray(),
            characterEvents = 1,
            characterAsk = 2,
            characterMeeting = 1,
            taskbar = _settings.ShowTaskbarUsage,
            startup = _settings.StartWithWindows,
            usageAlerts = _settings.UsageAlerts,
            activityAlerts = _settings.ActivityAlerts,
            claudeDesktop = _settings.ClaudeDesktopEnabled,
            claudeLoggedIn = _tray.ClaudeLoggedIn,
            accountsVersion = ChatterBoxAccounts.Version,
        };
        var signature = JsonSerializer.Serialize(state);
        if (!force && signature == _hostedState) return;
        _hostedState = signature;
        _hostedProtocol!.State(state);
    }

    void ApplyHostedAccounts(JsonElement value)
    {
        if (!ChatterBoxAccounts.TryParse(value, out var snapshot, out var error))
        {
            _hostedProtocol?.Error($"ChatterBox 계정 정보를 읽지 못했습니다: {error}");
            return;
        }
        _hostedAccounts = snapshot;
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
        var grok = Presenter.Grok(_monitor, _settings);
        ProviderCard? gemini = null;
        if (_hosted)
        {
            // Activity from this PC, accounts and usage from ChatterBox only.
            bool received = _hostedAccounts != null;
            claude = Presenter.Hosted(claude, _hostedAccounts?.For("Claude") ?? [], received);
            codex = Presenter.Hosted(codex, _hostedAccounts?.For("Codex") ?? [], received);
            gemini = Presenter.Hosted(Presenter.Antigravity(), _hostedAccounts?.For("Antigravity") ?? [], received);
            grok = Presenter.Hosted(grok, _hostedAccounts?.For("Grok") ?? [], received);
        }
        else
        {
            claude.AccountSlots = AccountUsageSlot.Create(claude, _secondaryAccounts.Accounts,
                _secondaryAccounts.Accounts.Count(a => a.Settings.Provider == AccountProvider.Claude));
            codex.AccountSlots = AccountUsageSlot.Create(codex, _secondaryAccounts.Accounts,
                _secondaryAccounts.Accounts.Count(a => a.Settings.Provider == AccountProvider.Codex));
            grok.AccountSlots = AccountUsageSlot.Create(grok, _secondaryAccounts.Accounts, secondarySlots: 0);
        }
        _vm.Update(claude, codex, grok, "", gemini);
        _taskbar.Update(new[] { claude, codex, gemini, grok }.OfType<ProviderCard>().ToArray());
        if (_window.IsAccountsOpen)
        {
            var accounts = _hosted ? Presenter.HostedAccountCards(_hostedAccounts) : AccountUsageCard.Create(claude, codex, _secondaryAccounts, grok);
            var signature = string.Join('\n', accounts.Select(x => x.Signature));
            if (_accountsSignature != signature)
            {
                _accountsSignature = signature;
                _window.Accounts.Update(accounts, !_hosted && _secondaryAccounts.LoginBusy);
            }
        }

        if (!_demo)
        {
            var moods = new List<MoodTracker.Result> { _claudeMood.Update(claude), _codexMood.Update(codex), _grokMood.Update(grok) };
            Animate(_window.Claude, moods[0]);
            Animate(_window.Gpt, moods[1]);
            Animate(_window.Grok, moods[2]);
            if (gemini != null)
            {
                moods.Add(_geminiMood.Update(gemini));
                Animate(_window.Gemini, moods[^1]);
            }
            // 마스터는 팀 전체를 지켜본다: 가장 급한 캐릭터의 기분을 따르고, 대사는 각 캐릭터에게 맡긴다.
            _window.Orch.SetMood(MoodTracker.Overall(moods.Select(m => m.Mood)), []);
        }

        _tray.UpdateIcon(claude.MaxUsed, codex.MaxUsed, grok.MaxUsed);
        _tray.SetTooltip(Tray.Tooltip(new[] { claude, codex, gemini, grok }.OfType<ProviderCard>().ToArray()));

        if (_hosted)
        {
            // Every ChatterBox account in assignment with a fresh reading, with the monitor's own thresholds
            // (80/95% by default); keyed by the account id, the label is only shown.
            foreach (var target in Presenter.HostedAlertTargets(_hostedAccounts, DateTimeOffset.Now))
                _alerts.CheckUsage(target.Display, target.Windows, target.StateKey);
        }
        else
        {
            _alerts.CheckUsage("Claude", claude.RawWindows);
            _alerts.CheckUsage("Codex", codex.RawWindows);
            _alerts.CheckUsage("Grok", grok.RawWindows);
        }
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

    readonly Dictionary<string, string> _askDrafts = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<CharacterMessage>> _conversations = new(StringComparer.Ordinal);

    void OpenAsk(string character, Rect anchor)
    {
        if (!_hosted || _exitStarted != 0) return;
        if (_askWindows.TryGetValue(character, out var existing))
        {
            existing.PlaceBy(anchor, _window.WorkArea);
            existing.ActivateConversation();
            return;
        }
        var name = character == CharacterSelection.Orch ? "마스터" : Tray.CharacterLabels[character];
        _askDrafts.TryGetValue(character, out var draft);
        var portrait = _window.Stages.FirstOrDefault(stage => stage.Character == character)?.Portrait;
        var window = new CharacterAskWindow(character, name, draft ?? "", portrait);
        _askDrafts.Remove(character);
        _askWindows.Add(character, window);
        window.Submitted += text => SendAsk(character, text);
        window.Closed += (_, _) =>
        {
            _askWindows.Remove(character);
            if (!string.IsNullOrWhiteSpace(window.Question)) _askDrafts[character] = window.Question;
        };
        if (_conversations.TryGetValue(character, out var messages))
            foreach (var message in messages) window.AddMessage(message);
        window.SetWaiting(_asks.IsWaiting(character));
        window.PlaceBy(anchor, _window.WorkArea);
        window.Show();
        window.ActivateConversation();
    }

    void ToggleMeeting()
    {
        if (_meetingWindow?.HideIfOpen() != true) OpenMeeting();
    }

    void OpenMeeting()
    {
        if (!_hosted || _exitStarted != 0) return;
        if (_meetingWindow is { } existing) { existing.Present(); return; }
        var portraits = _window.Stages.ToDictionary(stage => stage.Character, stage => stage.Portrait);
        var window = _meetingWindow = new CharacterMeetingWindow(portraits, _meetingDraft, _meetingFolder,
            _settings.BoardroomWidth, _settings.BoardroomHeight);
        window.UserSizeChanged += RememberBoardroomSize;
        window.Submitted += (text, folder) =>
        {
            if (_meetingPending != null || _meetingActive != null) return;
            _meetingPending = Guid.NewGuid().ToString("N");
            _submittedAgenda = text;
            _meetingStatus = "회의에 안건을 보내는 중…";
            RefreshMeeting();
            _hostedProtocol!.MeetingStart(_meetingPending, text, folder);
        };
        window.StopRequested += () =>
        {
            if ((_meetingActive ?? _meetingPending) is { } id) _hostedProtocol!.MeetingCancel(id);
        };
        window.Closed += (_, _) =>
        {
            _meetingDraft = window.Draft;
            _meetingFolder = window.Folder;
            _meetingWindow = null;
        };
        foreach (var (message, draft) in _meetingPosts) window.Upsert(message, draft);
        RefreshMeeting();
        window.PlaceIn(_window.WorkArea);
        window.Present();
        if (!_meetingSnapshotLoaded) _hostedProtocol!.MeetingSync();
    }

    void RememberBoardroomSize(Size size)
    {
        _settings.BoardroomWidth = size.Width;
        _settings.BoardroomHeight = size.Height;
        if (_boardroomSizeSave == null)
        {
            _boardroomSizeSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _boardroomSizeSave.Tick += (_, _) =>
            {
                _boardroomSizeSave.Stop();
                _settings.Save();
            };
        }
        _boardroomSizeSave.Stop();
        _boardroomSizeSave.Start();
    }

    void ApplyMeeting(MeetingEvent item)
    {
        if (item.Seq == 0 && item.Type == "end" && item.RequestId == _meetingPending)
        {
            _meetingPending = null;
            _meetingStatus = item.Text!;
            if (_meetingWindow is { Draft.Length: 0 } window) window.RestoreDraft(_submittedAgenda);
            else if (_meetingWindow == null && _meetingDraft.Length == 0) _meetingDraft = _submittedAgenda;
            RefreshMeeting();
            return;
        }
        if (item.Seq <= _meetingSequence) return;
        _meetingSequence = item.Seq;
        _meetingActive = item.Type == "end" ? null : item.RequestId;
        if (_meetingPending == item.RequestId) _meetingPending = null;
        switch (item.Type)
        {
            case "roster":
                _meetingMembers = item.Connected;
                _meetingResting = item.Resting;
                break;
            case "status": _meetingStatus = item.Text!; break;
            case "message": UpdateMeetingPost(item.Message!, false); break;
            case "draft": UpdateMeetingPost(item.Message!, true); break;
            case "withdraw":
                _meetingPosts.RemoveAll(post => post.Message.Id == item.Id);
                _meetingWindow?.Remove(item.Id!);
                break;
            case "end": _meetingStatus = item.Text!; break;
        }
        RefreshMeeting();
    }

    void UpdateMeetingPost(MeetingMessage message, bool draft)
    {
        int index = _meetingPosts.FindIndex(post => post.Message.Id == message.Id);
        if (index < 0) _meetingPosts.Add((message, draft));
        else _meetingPosts[index] = (message, draft);
        _meetingWindow?.Upsert(message, draft);
        if (_meetingPosts.Count > 51 && _meetingPosts.FindIndex(post => !post.Draft) is >= 0 and var oldest)
        {
            _meetingWindow?.Remove(_meetingPosts[oldest].Message.Id);
            _meetingPosts.RemoveAt(oldest);
        }
    }

    void RefreshMeeting()
    {
        _meetingWindow?.SetRoster(_meetingMembers, _meetingPosts.Where(post => post.Draft).Select(post => post.Message.Sender).ToArray(), _meetingResting);
        _meetingWindow?.SetStatus(_meetingStatus, _meetingPending != null || _meetingActive != null);
    }

    void SendAsk(string character, string text)
    {
        if (_asks.Start(character, text, DateTimeOffset.Now) is not { } requestId) return;
        RecordMessage(character, new CharacterMessage(true, text));
        _window.Stages[CharacterSelection.All.ToList().IndexOf(character)].ExternalThinking = true;
        _hostedProtocol!.CharacterAsk(requestId, character, text);
    }

    void RecordMessage(string character, CharacterMessage message)
    {
        if (!_conversations.TryGetValue(character, out var messages))
            _conversations[character] = messages = [];
        messages.Add(message);
        if (messages.Count > 60) messages.RemoveAt(0);
        if (_askWindows.TryGetValue(character, out var window)) window.AddMessage(message);
    }

    void PresentReply(string character, string text)
    {
        RecordMessage(character, new CharacterMessage(false, text));
        if (_askWindows.TryGetValue(character, out var window)) window.SetWaiting(false);
        if (window is null || window.WindowState == WindowState.Minimized) _window.Speak(character, text);
    }

    void ApplyCharacterReply(CharacterReply reply)
    {
        if (!_asks.Complete(reply)) return;
        _window.Stages[CharacterSelection.All.ToList().IndexOf(reply.Character)].ExternalThinking = false;
        PresentReply(reply.Character, reply.Spoken);
    }

    void ExpireAsks()
    {
        foreach (var character in _asks.Expire(DateTimeOffset.Now))
        {
            _window.Stages[CharacterSelection.All.ToList().IndexOf(character)].ExternalThinking = false;
            PresentReply(character, "답을 받지 못했어요. 다시 물어봐 주세요.");
        }
    }
    void ExitApp()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        try { _hostedInputLifetime?.Cancel(); } catch (ObjectDisposedException) { }
        _boardroomShortcut?.Dispose();
        _boardroomSizeSave?.Stop();
        _meetingWindow?.Close();
        foreach (var chat in _askWindows.Values.ToArray()) chat.Close();
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
        _boardroomShortcut?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
