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

    public MonitorService(AppSettings settings) => _settings = settings;

    public ClaudeWebClient ClaudeWeb { get; } = new();

    public ClaudeLocalState? ClaudeLocal { get; private set; }
    public CodexLocalState? CodexLocal { get; private set; }

    /// <summary>마지막 조회 결과 (실패 포함).</summary>
    public ClaudeWebResult? ClaudeWebLast { get; private set; }
    /// <summary>마지막으로 성공한 조회 결과.</summary>
    public ClaudeWebResult? ClaudeWebGood { get; private set; }
    public CodexRemoteResult? CodexRemoteLast { get; private set; }
    public CodexRemoteResult? CodexRemoteGood { get; private set; }

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
        if (_settings.ClaudeWebEnabled && DateTime.UtcNow >= _nextClaude) _ = RefreshClaudeWebAsync();
    }

    public void RefreshAll()
    {
        _ = RefreshLocalAsync();
        _ = RefreshCodexRemoteAsync();
        if (_settings.ClaudeWebEnabled) _ = RefreshClaudeWebAsync();
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
        if (ClaudeWebBusy || ClaudeWeb.LoginWindowOpen) return;
        ClaudeWebBusy = true;
        _nextClaude = DateTime.UtcNow.AddSeconds(Math.Max(60, _settings.ClaudeWebPollSeconds));
        Changed?.Invoke();
        try
        {
            var result = await ClaudeWeb.FetchAsync(); // WebView2는 UI 스레드에서 다뤄야 한다
            ClaudeWebLast = result;
            if (result.State == ClaudeWebState.Ok) ClaudeWebGood = result;
            if (result.State == ClaudeWebState.LoginRequired) ClaudeWebGood = null;
        }
        finally
        {
            ClaudeWebBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task LogoutClaudeAsync()
    {
        _settings.ClaudeWebEnabled = false;
        _settings.Save();
        ClaudeWebGood = null;
        ClaudeWebLast = null;
        try { await ClaudeWeb.LogoutAsync(); } catch (Exception ex) { Log.Write($"logout failed: {ex.Message}"); }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Stop();
        ClaudeWeb.Dispose();
    }
}
