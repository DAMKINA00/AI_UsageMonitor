using System.Windows.Threading;

namespace AIUsageMonitor.Core;

/// <summary>
/// 각 데이터 소스를 주기적으로 갱신한다. 모든 상태 변경과 Changed 이벤트는 UI 스레드에서 일어난다.
/// </summary>
public sealed class MonitorService : IDisposable
{
    readonly AppSettings _settings;
    readonly ClaudeLocalReader _claudeLocal = new();
    readonly CodexLocalReader _codexLocal = new();
    DispatcherTimer? _timer;
    bool _localBusy;
    DateTime _nextCodex = DateTime.MinValue;
    DateTime _nextClaude = DateTime.MinValue;
    DateTime _nextClaudeChats = DateTime.MinValue;
    bool _chatsBusy;
    int _claudeGeneration;

    public MonitorService(AppSettings settings)
    {
        _settings = settings;
        ClaudeDesktop.AccountChanged += () =>
        {
            if (!_settings.ClaudeDesktopEnabled) return;
            ClearClaudeState();
            if (!ClaudeWebBusy) _nextClaude = DateTime.MinValue;
            Changed?.Invoke();
        };
    }

    public ClaudeWebClient ClaudeWeb { get; } = new();
    public ClaudeWebClient ClaudeDesktop { get; } = new(desktopSession: true);
    ClaudeWebClient ClaudeClient => _settings.ClaudeDesktopEnabled ? ClaudeDesktop : ClaudeWeb;
    public bool ClaudeEnabled => _settings.ClaudeDesktopEnabled || _settings.ClaudeWebEnabled;

    public void SetClaudeDesktopEnabled(bool enabled)
    {
        if (_settings.ClaudeDesktopEnabled == enabled) return;
        _claudeGeneration++;
        _settings.ClaudeDesktopEnabled = enabled;
        _settings.Save();
        ClaudeWeb.KeepAlive = ClaudeDesktop.KeepAlive = false;
        ClearClaudeState();
        _nextClaude = DateTime.MinValue;
        Changed?.Invoke();
        if (ClaudeEnabled) _ = RefreshClaudeWebAsync();
    }

    void ClearClaudeState()
    {
        ClaudeWebGood = null;
        ClaudeWebLast = null;
        ClaudeChats = [];
        ClaudeChatsAt = null;
        ClaudeChatsError = null;
    }

    public ClaudeLocalState? ClaudeLocal { get; private set; }
    public CodexLocalState? CodexLocal { get; private set; }

    /// <summary>마지막 조회 결과 (실패 포함).</summary>
    public ClaudeWebResult? ClaudeWebLast { get; private set; }
    /// <summary>마지막으로 성공한 조회 결과.</summary>
    public ClaudeWebResult? ClaudeWebGood { get; private set; }
    public CodexRemoteResult? CodexRemoteLast { get; private set; }
    public CodexRemoteResult? CodexRemoteGood { get; private set; }

    /// <summary>claude.ai 앱 대화 중 진행 중이거나 최근 활동한 것 (사용량 조회 때 함께, 앱이 켜져 있으면 더 자주 갱신).</summary>
    public List<SessionInfo> ClaudeChats { get; private set; } = [];
    /// <summary>ClaudeChats 를 마지막으로 읽은 시각. 오래되면 진행 중 표시를 믿지 않는다.</summary>
    public DateTimeOffset? ClaudeChatsAt { get; private set; }
    public string? ClaudeChatsError { get; private set; }

    public bool ClaudeWebBusy { get; private set; }
    public bool CodexRemoteBusy { get; private set; }
    public DateTimeOffset? LastLocalRefresh { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        ClaudeWeb.LoginWindowClosed += () =>
        {
            _settings.ClaudeWebEnabled = true;
            _settings.Save();
            _ = RefreshClaudeWebAsync();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void Tick()
    {
        _ = RefreshLocalAsync();
        if (DateTime.UtcNow >= _nextCodex) _ = RefreshCodexRemoteAsync();

        bool fastChats = ClaudeEnabled && FastChatPolling;
        ClaudeClient.KeepAlive = fastChats;
        if (ClaudeEnabled && DateTime.UtcNow >= _nextClaude) _ = RefreshClaudeWebAsync();
        else if (fastChats && DateTime.UtcNow >= _nextClaudeChats) _ = RefreshClaudeChatsAsync();
    }

    /// <summary>Claude 앱이 켜져 있거나 진행 중인 대화가 있으면 대화 목록을 사용량보다 자주 확인한다.</summary>
    bool FastChatPolling =>
        ClaudeLocal is { DesktopRunning: true } ||
        ClaudeChats.Any(c => c.State is SessionState.Working or SessionState.NeedsInput);

    DateTimeOffset ChatCutoff => DateTimeOffset.UtcNow.AddHours(-Math.Max(1, _settings.ClaudeChatActiveHours));

    void SetChats(ClaudeWebResult result)
    {
        if (result.State == ClaudeWebState.LoginRequired)
        {
            ClearClaudeState();
            ClaudeWebLast = result;
        }
        else if (result.Chats is { } chats)
        {
            ClaudeChats = chats;
            ClaudeChatsAt = result.At;
            ClaudeChatsError = null;
            _nextClaudeChats = DateTime.UtcNow.AddSeconds(Math.Max(15, _settings.ClaudeChatPollSeconds));
        }
        else
            ClaudeChatsError = result.ChatsError ?? result.Error ?? "작업 목록을 읽지 못했습니다.";
    }

    public async Task RefreshClaudeChatsAsync()
    {
        if (_chatsBusy || ClaudeWebBusy || ClaudeClient.LoginWindowOpen) return;
        _chatsBusy = true;
        int generation = _claudeGeneration;
        // 실패해도 다음 확인까지는 간격을 둔다
        _nextClaudeChats = DateTime.UtcNow.AddSeconds(Math.Max(15, _settings.ClaudeChatPollSeconds));
        try
        {
            var result = await ClaudeClient.FetchChatsAsync(ChatCutoff);
            if (generation != _claudeGeneration) return;
            SetChats(result);
            if (result.State == ClaudeWebState.Error) Log.Write($"claude chats: {result.Error}");
        }
        finally
        {
            _chatsBusy = false;
            Changed?.Invoke();
        }
    }

    public void RefreshAll()
    {
        _ = RefreshLocalAsync();
        _ = RefreshCodexRemoteAsync();
        if (ClaudeEnabled) _ = RefreshClaudeWebAsync(); // 대화 목록도 함께 읽는다
    }

    async Task RefreshLocalAsync()
    {
        if (_localBusy) return;
        _localBusy = true;
        try
        {
            int hours = Math.Max(1, _settings.CodexActiveHours);
            var (claude, codex) = await Task.Run(() => (_claudeLocal.Read(), _codexLocal.Read(hours)));
            ClaudeLocal = claude;
            CodexLocal = codex;
            LastLocalRefresh = DateTimeOffset.Now;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"local refresh failed: {ex}");
        }
        finally
        {
            _localBusy = false;
        }
    }

    public async Task RefreshCodexRemoteAsync()
    {
        if (CodexRemoteBusy) return;
        CodexRemoteBusy = true;
        _nextCodex = DateTime.UtcNow.AddSeconds(Math.Max(30, _settings.CodexPollSeconds));
        Changed?.Invoke();
        try
        {
            var result = await Task.Run(() => CodexAppServer.QueryAsync(_settings.CodexExePath));
            CodexRemoteLast = result;
            if (result.Ok) CodexRemoteGood = result;
        }
        finally
        {
            CodexRemoteBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task RefreshClaudeWebAsync()
    {
        if (ClaudeWebBusy || ClaudeClient.LoginWindowOpen) return;
        ClaudeWebBusy = true;
        int generation = _claudeGeneration;
        _nextClaude = DateTime.UtcNow.AddSeconds(Math.Max(60, _settings.ClaudeWebPollSeconds));
        Changed?.Invoke();
        try
        {
            var result = await ClaudeClient.FetchAsync(ChatCutoff); // WebView2는 UI 스레드에서 다뤄야 한다
            if (generation != _claudeGeneration) return;
            ClaudeWebLast = result;
            if (result.State == ClaudeWebState.Ok) ClaudeWebGood = result;
            if (result.State == ClaudeWebState.LoginRequired) ClaudeWebGood = null;
            SetChats(result);
        }
        finally
        {
            ClaudeWebBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task LogoutClaudeAsync()
    {
        _claudeGeneration++;
        _settings.ClaudeWebEnabled = false;
        _settings.Save();
        ClearClaudeState();
        ClaudeWeb.KeepAlive = false;
        try { await ClaudeWeb.LogoutAsync(); } catch (Exception ex) { Log.Write($"logout failed: {ex.Message}"); }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Stop();
        ClaudeWeb.Dispose();
        ClaudeDesktop.Dispose();
    }
}
