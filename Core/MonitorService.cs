using System.Windows.Threading;

namespace AIUsageMonitor.Core;

/// <summary>
/// 각 데이터 소스를 주기적으로 갱신한다. 모든 상태 변경과 Changed 이벤트는 UI 스레드에서 일어난다.
/// </summary>
public sealed class MonitorService : IDisposable
{
    readonly AppSettings _settings;
    readonly Action<AppSettings> _saveSettings;
    readonly ClaudeLocalReader _claudeLocal = new();
    readonly CodexLocalReader _codexLocal = new();
    readonly GeminiLocalReader _geminiLocal = new();
    readonly Dictionary<string, string> _localErrors = new();
    // 앱이 끝나면 진행 중인 codex app-server·agy 조회를 취소한다
    readonly CancellationTokenSource _lifetime = new();
    // 다른 곳에서 띄운 agy 가 끝나 앞당긴 조회 시각 (시간당 횟수 제한용)
    readonly Queue<DateTime> _externalGeminiTriggers = new();
    DispatcherTimer? _timer;
    bool _localBusy;
    DateTime _nextCodex = DateTime.MinValue;
    DateTime _nextGrok = DateTime.MinValue;
    DateTime _nextGemini = DateTime.MinValue;
    DateTime _lastGeminiQuery = DateTime.MinValue;
    int _geminiFailures;
    int _geminiAccountGeneration;
    DateTime _nextClaude = DateTime.MinValue;
    DateTime _nextClaudeChats = DateTime.MinValue;
    bool _chatsBusy;
    int _claudeGeneration;

    /// <param name="saveSettings">설정 저장 방법 (기본: settings.json). 테스트는 사용자 설정 파일을 건드리지 않도록 바꾼다.</param>
    public MonitorService(AppSettings settings, Action<AppSettings>? saveSettings = null)
    {
        _settings = settings;
        _saveSettings = saveSettings ?? (s => s.Save());
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
        _saveSettings(_settings);
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
    public GrokRemoteResult? GrokRemoteLast { get; private set; }
    public GrokRemoteResult? GrokRemoteGood { get; private set; }
    public bool GrokRemoteBusy { get; private set; }
    public int GrokProcessCount { get; private set; }

    public GeminiLocalState? GeminiLocal { get; private set; }
    /// <summary>마지막 `agy /usage` 결과 (실패 포함).</summary>
    public GeminiRemoteResult? GeminiRemoteLast { get; private set; }
    /// <summary>마지막으로 성공한 `agy /usage` 결과. 로그인이 필요해지면 지운다.</summary>
    public GeminiRemoteResult? GeminiRemoteGood { get; private set; }
    public bool GeminiRemoteBusy { get; private set; }
    /// <summary>
    /// 로그인이 필요해 정상 주기 조회를 멈춘 상태. 60분마다, 그리고 다른 곳에서 띄운 agy 가 끝나면 다시 확인하며,
    /// 수동 새로고침(RefreshAll)으로 바로 다시 시작한다.
    /// </summary>
    public bool GeminiPollingPaused { get; private set; }
    /// <summary>
    /// 로그인 필요를 평문 안내로만 알아 자동 재확인을 하지 않는 상태 (평문이 모델 응답일 수도 있어 프롬프트 반복을 막는다).
    /// 수동 새로고침(RefreshAll)이나 ClearGeminiBlock 으로만 다시 조회한다.
    /// </summary>
    public bool GeminiWaitingForManualRefresh { get; private set; }
    /// <summary>설정이 null(자동)이면 agy 실행 파일을 찾았을 때만 켠다.</summary>
    public bool GeminiEnabled => _settings.GeminiEnabled ?? GeminiLocal?.Installed == true;
    /// <summary>
    /// 지금 agy 가 /usage 를 명령으로 처리하지 않는 버전으로 판정돼 조회를 막은 상태 (설정에 저장돼 재시작 후에도 유지).
    /// 수동 새로고침으로도 풀리지 않으며, agy 실행 파일이 바뀌면(업데이트) 다시 시도하고 정상 응답일 때만 차단을 푼다.
    /// 실행 파일 지문을 얻지 못한 채 차단됐으면("unknown") 지문과 상관없이 막고, ClearGeminiBlock 으로만 푼다.
    /// 지금 지문을 알 수 없을 때도 막는다.
    /// </summary>
    public bool GeminiUnsupported => _settings.AgyBlockedFingerprint is { } blocked &&
        (blocked == UnknownFingerprint || CurrentAgyFingerprint() is not { } current || blocked == current);
    /// <summary>GeminiUnsupported 일 때 표시할 사유.</summary>
    public string? GeminiUnsupportedReason => GeminiUnsupported ? AgyCli.UnsupportedMessage : null;

    string? CurrentAgyFingerprint() => GeminiLocal is { } local
        ? local.ExecutableFingerprint
        : AgyCli.Fingerprint(AgyCli.FindExecutable(_settings.AgyExePath));

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
            _saveSettings(_settings);
            _ = RefreshClaudeWebAsync();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void Tick()
    {
        _ = RefreshLocalAsync();
        if (DateTime.UtcNow >= _nextCodex) _ = RefreshCodexRemoteAsync();
        if (DateTime.UtcNow >= _nextGrok) _ = RefreshGrokRemoteAsync();
        if (GeminiEnabled && DateTime.UtcNow >= _nextGemini) _ = RefreshGeminiRemoteAsync();

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
        _ = RefreshGrokRemoteAsync();
        if (GeminiEnabled) _ = RefreshGeminiRemoteAsync(manual: true);
        if (ClaudeEnabled) _ = RefreshClaudeWebAsync(); // 대화 목록도 함께 읽는다
    }

    async Task RefreshLocalAsync()
    {
        if (_localBusy) return;
        _localBusy = true;
        try
        {
            int hours = Math.Max(1, _settings.CodexActiveHours);
            int geminiHours = Math.Max(1, _settings.GeminiActiveHours);
            var agyPath = _settings.AgyExePath;
            // 소스마다 따로 읽어 한 소스의 실패가 나머지 갱신을 막지 않게 한다
            var (claude, codex, gemini, grokCount, errors) = await Task.Run(() =>
            {
                var failures = new Dictionary<string, Exception>();
                T? Safe<T>(string name, Func<T> read) where T : class
                {
                    try { return read(); }
                    catch (Exception ex) { failures[name] = ex; return null; }
                }
                int grokCount = GrokProcessCount;
                try { grokCount = ProcessUtil.ProcessesByName("grok").Count; }
                catch (Exception ex) { failures["grok"] = ex; }
                return (Safe("claude", _claudeLocal.Read), Safe("codex", () => _codexLocal.Read(hours)),
                    Safe("gemini", () => _geminiLocal.Read(geminiHours, agyPath)), grokCount, failures);
            });
            ReportLocalErrors(errors);
            if (claude != null) ClaudeLocal = claude;
            if (codex != null) CodexLocal = codex;
            GrokProcessCount = grokCount;
            if (gemini != null)
            {
                OnGeminiLocalChanged(GeminiLocal, gemini);
                GeminiLocal = gemini;
            }
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

    const int ExternalGeminiTriggersPerHour = 6;

    /// <summary>
    /// 다른 곳에서 띄운 agy 가 끝나면 쿼터가 바뀌었을 수 있으니 곧 다시 확인한다
    /// (마지막 조회에서 최소 2분 뒤, 시간당 최대 6회. 실패 백오프 중에는 앞당기지 않는다).
    /// </summary>
    void OnGeminiLocalChanged(GeminiLocalState? previous, GeminiLocalState current)
    {
        if (previous != null && !string.Equals(previous.Account, current.Account, StringComparison.OrdinalIgnoreCase))
        {
            _geminiAccountGeneration++;
            GeminiRemoteGood = GeminiRemoteLast = null;
            GeminiPollingPaused = GeminiWaitingForManualRefresh = false;
            _geminiFailures = 0;
            _nextGemini = DateTime.MinValue;
        }
        if (previous is { CliProcessCount: > 0 } && current.CliProcessCount == 0) OnExternalAgyFinished();
        // 차단된 agy 가 업데이트(지문 변경)되면 24시간 대기를 앞당겨 한 번 다시 확인한다 ("업데이트하면 다시 확인합니다")
        if (previous != null && _settings.AgyBlockedFingerprint is { } blocked && blocked != UnknownFingerprint &&
            current.ExecutableFingerprint is { } updated && updated != previous.ExecutableFingerprint && updated != blocked)
        {
            var soon = DateTime.UtcNow.AddSeconds(10);
            if (soon < _nextGemini) _nextGemini = soon;
            Log.Write("gemini usage: agy changed after an unsupported block; checking again");
        }
    }

    void OnExternalAgyFinished()
    {
        if (_geminiFailures > 0 || GeminiWaitingForManualRefresh) return;
        var now = DateTime.UtcNow;
        while (_externalGeminiTriggers.Count > 0 && now - _externalGeminiTriggers.Peek() > TimeSpan.FromHours(1))
            _externalGeminiTriggers.Dequeue();
        if (_externalGeminiTriggers.Count >= ExternalGeminiTriggersPerHour) return;
        var soon = now.AddSeconds(10);
        var floor = _lastGeminiQuery.AddSeconds(GeminiMinPollSeconds);
        var candidate = soon > floor ? soon : floor;
        if (candidate >= _nextGemini) return;
        _nextGemini = candidate;
        _externalGeminiTriggers.Enqueue(now);
    }

    /// <summary>같은 오류가 1초마다 로그에 쌓이지 않도록 소스별로 바뀐 오류만 남긴다.</summary>
    void ReportLocalErrors(Dictionary<string, Exception> errors)
    {
        foreach (var name in new[] { "claude", "codex", "gemini", "grok" })
        {
            if (errors.TryGetValue(name, out var ex))
            {
                var message = ex.GetType().Name + ": " + ex.Message;
                if (_localErrors.TryGetValue(name, out var previous) && previous == message) continue;
                _localErrors[name] = message;
                Log.Write($"{name} local refresh failed: {ex}");
            }
            else _localErrors.Remove(name);
        }
    }

    public async Task RefreshCodexRemoteAsync()
    {
        if (CodexRemoteBusy || _lifetime.IsCancellationRequested) return;
        CodexRemoteBusy = true;
        try
        {
            _nextCodex = DateTime.UtcNow.AddSeconds(Math.Max(30, _settings.CodexPollSeconds));
            Changed?.Invoke();
            var token = _lifetime.Token;
            var result = await Task.Run(() => CodexAppServer.QueryAsync(_settings.CodexExePath, token));
            CodexRemoteLast = result;
            if (result.Ok) CodexRemoteGood = result;
        }
        finally
        {
            CodexRemoteBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task RefreshGrokRemoteAsync()
    {
        if (GrokRemoteBusy || _lifetime.IsCancellationRequested) return;
        GrokRemoteBusy = true;
        _nextGrok = DateTime.UtcNow.AddSeconds(Math.Max(120, _settings.GrokPollSeconds));
        try
        {
            Changed?.Invoke();
            var token = _lifetime.Token;
            var result = await Task.Run(() => GrokCli.QueryAsync(_settings.GrokExePath, token));
            if (_lifetime.IsCancellationRequested) return;
            GrokRemoteLast = result;
            if (result.Ok) GrokRemoteGood = result;
            else if (result.LoginRequired) GrokRemoteGood = null;
        }
        finally
        {
            GrokRemoteBusy = false;
            if (!_lifetime.IsCancellationRequested) Changed?.Invoke();
        }
    }

    const int GeminiMinPollSeconds = 120;
    int GeminiPollSeconds => Math.Max(GeminiMinPollSeconds, _settings.GeminiPollSeconds);

    const int GeminiLoginRetrySeconds = 3600;
    const string UnknownFingerprint = "unknown";

    /// <summary>
    /// 사용자가 'agy 버전 미지원' 차단을 푼다: 저장된 지문을 지우고 저장한 뒤 바로 한 번 조회한다.
    /// 그 조회가 다시 미지원이면 새로 차단된다 (agy 가 /usage 를 프롬프트로 한 번 더 실행할 수 있으므로 UI 에서 확인을 받는다).
    /// </summary>
    public Task ClearGeminiBlock()
    {
        if (_settings.AgyBlockedFingerprint == null) return Task.CompletedTask;
        _settings.AgyBlockedFingerprint = null;
        _saveSettings(_settings);
        _nextGemini = DateTime.UtcNow;
        Log.Write("gemini usage: block cleared by the user");
        Changed?.Invoke();
        return RefreshGeminiRemoteAsync(manual: true);
    }

    /// <summary>
    /// `agy -p /usage` 로 Gemini 쿼터를 읽는다. 실패하면 600초, 이어서 1800초 뒤에 다시 시도한다.
    /// 로그인이 필요하면 60분마다만 확인하고(manual 이면 바로), /usage 를 명령으로 처리하지 않는 agy 로 판정되면
    /// 그 실행 파일이 바뀔 때까지 manual 이어도 실행하지 않는다.
    /// </summary>
    public async Task RefreshGeminiRemoteAsync(bool manual = false)
    {
        if (GeminiRemoteBusy || !GeminiEnabled || _lifetime.IsCancellationRequested) return;
        if (GeminiUnsupported) return;
        if (!manual && GeminiWaitingForManualRefresh) return;
        if (!manual && GeminiPollingPaused && DateTime.UtcNow < _nextGemini) return;
        GeminiRemoteBusy = true;
        var accountGeneration = _geminiAccountGeneration;
        try
        {
            if (manual)
            {
                _geminiFailures = 0;
                GeminiWaitingForManualRefresh = false;
            }
            _lastGeminiQuery = DateTime.UtcNow;
            _nextGemini = DateTime.UtcNow.AddSeconds(GeminiPollSeconds);
            Changed?.Invoke();
            var exe = _settings.AgyExePath is { Length: > 0 } configured ? configured : GeminiLocal?.ExecutablePath;
            var fingerprint = AgyCli.Fingerprint(AgyCli.FindExecutable(exe));
            var token = _lifetime.Token;
            var result = await Task.Run(() => AgyCli.QueryAsync(exe, token));
            if (accountGeneration != _geminiAccountGeneration) { _nextGemini = DateTime.MinValue; return; }
            GeminiRemoteLast = result;
            if (result.Ok && _settings.AgyBlockedFingerprint != null)
            {
                // 업데이트된 agy 가 다시 정상 응답했다 (시간 초과·네트워크 오류로는 풀지 않는다)
                _settings.AgyBlockedFingerprint = null;
                _saveSettings(_settings);
            }
            if (result.Ok)
            {
                GeminiRemoteGood = result;
                GeminiPollingPaused = false;
                GeminiWaitingForManualRefresh = false;
                _geminiFailures = 0;
            }
            else if (result.Unsupported)
            {
                // 응답 본문은 남기지 않는다. 같은 실행 파일로는 다시 실행하지 않는다.
                GeminiRemoteGood = null;
                _settings.AgyBlockedFingerprint = fingerprint ?? UnknownFingerprint;
                _saveSettings(_settings);
                // 차단 판정과 별개로 하루는 자동 조회를 하지 않는다 (지문 비교가 어긋나도 120초마다 다시 실행되지 않게)
                _nextGemini = DateTime.UtcNow.AddHours(24);
                Log.Write("gemini usage: agy did not handle /usage as a command; blocked until agy changes");
            }
            else if (result.LoginRequired)
            {
                GeminiRemoteGood = null;
                GeminiPollingPaused = true;
                // 평문 안내로만 안 경우는 자동 재확인을 하지 않는다 (그 평문이 모델 응답이었다면 프롬프트 턴이 반복된다)
                GeminiWaitingForManualRefresh = result.PlainTextLogin;
                _nextGemini = DateTime.UtcNow.AddSeconds(Math.Max(GeminiLoginRetrySeconds, GeminiPollSeconds));
                Log.Write($"gemini usage: login required ({result.Error}){(result.PlainTextLogin ? "; waiting for a manual refresh" : "")}");
            }
            else
            {
                _geminiFailures++;
                int backoff = _geminiFailures == 1 ? 600 : 1800;
                _nextGemini = DateTime.UtcNow.AddSeconds(Math.Max(backoff, GeminiPollSeconds));
                Log.Write($"gemini usage failed ({_geminiFailures}): {result.Error}");
            }
        }
        finally
        {
            GeminiRemoteBusy = false;
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
        _saveSettings(_settings);
        ClearClaudeState();
        ClaudeWeb.KeepAlive = false;
        try { await ClaudeWeb.LogoutAsync(); } catch (Exception ex) { Log.Write($"logout failed: {ex.Message}"); }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Stop();
        // 진행 중인 조회의 프로세스를 끝낸다 (QueryAsync 가 토큰 취소 때 프로세스 트리와 Job 을 닫는다)
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        ClaudeWeb.Dispose();
        ClaudeDesktop.Dispose();
    }
}
