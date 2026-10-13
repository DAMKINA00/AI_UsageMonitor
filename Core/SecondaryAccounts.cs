using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace AIUsageMonitor.Core;

public enum AccountProvider { Claude, Codex }

public sealed class SecondaryAccountSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountProvider Provider { get; set; }
}

public sealed class SecondaryAccountState(SecondaryAccountSettings settings)
{
    public SecondaryAccountSettings Settings { get; } = settings;
    public string? AccountName { get; internal set; }
    public UsageSnapshot? Usage { get; internal set; }
    public string Status { get; internal set; } = "사용량 확인 중…";
    public bool HasRefreshError { get; internal set; }
    public bool IsLoggingIn { get; internal set; }
    internal bool Removed;
    internal DateTime NextRefresh;
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal CancellationTokenSource Lifetime = new();
    internal ClaudeWebClient? Claude;
}

/// <summary>추가 계정은 앱 전용 디렉터리에서 인증과 사용량을 관리한다. Desktop/CLI 계정은 변경하지 않는다.</summary>
public sealed class SecondaryAccountService : IDisposable
{
    readonly AppSettings _settings;
    readonly List<SecondaryAccountState> _accounts = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    bool _refreshing, _disposed;
    public IReadOnlyList<SecondaryAccountState> Accounts => _accounts;
    public bool LoginBusy { get; private set; }
    public event Action? Changed;

    public SecondaryAccountService(AppSettings settings)
    {
        _settings = settings;
        // Only generated IDs may select a local authentication directory.
        _settings.SecondaryAccounts = (settings.SecondaryAccounts ?? []).Where(x => x != null &&
            Guid.TryParseExact(x.Id, "N", out _) && Enum.IsDefined(x.Provider))
            .Select(x => { x.Id = Guid.Parse(x.Id).ToString("N"); return x; }).DistinctBy(x => x.Id).ToList();
        _accounts.AddRange(_settings.SecondaryAccounts.Select(x => new SecondaryAccountState(x)));
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public static string AccountDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var value)) throw new ArgumentException("잘못된 계정 ID입니다.", nameof(id));
        return Path.Combine(Paths.AppData, "accounts", value.ToString("N"));
    }

    static string CodexDirectory(SecondaryAccountState account) => Path.Combine(AccountDirectory(account.Settings.Id), "codex");
    static ClaudeWebClient Claude(SecondaryAccountState account) => account.Claude ??=
        new ClaudeWebClient(profileDirectory: Path.Combine(AccountDirectory(account.Settings.Id), "webview"));

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task AddAsync(AccountProvider provider)
    {
        if (LoginBusy || _disposed) return;
        var account = new SecondaryAccountState(new SecondaryAccountSettings { Provider = provider });
        _accounts.Add(account);
        _settings.SecondaryAccounts.Add(account.Settings);
        _settings.Save();
        await LoginAsync(account.Settings.Id);
    }

    public async Task LoginAsync(string id)
    {
        var account = _accounts.FirstOrDefault(x => x.Settings.Id == id);
        if (account == null || account.Removed || LoginBusy || _disposed) return;
        LoginBusy = true;
        account.IsLoggingIn = true;
        account.HasRefreshError = false;
        account.Status = "브라우저에서 추가할 계정으로 로그인해 주세요.";
        Changed?.Invoke();
        bool entered = false;
        try
        {
            await account.Gate.WaitAsync(account.Lifetime.Token);
            entered = true;
            account.Usage = null;
            account.AccountName = null;
            Changed?.Invoke();
            if (account.Settings.Provider == AccountProvider.Claude)
            {
                await Claude(account).LogoutAsync();
                account.Lifetime.Token.ThrowIfCancellationRequested();
                await Claude(account).LoginAsync(account.Lifetime.Token);
            }
            else
            {
                Directory.CreateDirectory(CodexDirectory(account));
                await CodexAppServer.LoginAsync(_settings.CodexExePath, CodexDirectory(account), url =>
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), account.Lifetime.Token);
            }
            if (!account.Removed) await ReadAsync(account);
        }
        catch (OperationCanceledException) { account.Status = "로그인이 취소되었거나 시간이 초과되었습니다."; }
        catch (Exception ex) { account.Status = "로그인 실패: " + ex.Message; }
        finally
        {
            if (entered) account.Gate.Release();
            account.IsLoggingIn = false;
            LoginBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            foreach (var account in _accounts.ToArray())
            {
                if (account.Removed || account.IsLoggingIn || (!force && DateTime.UtcNow < account.NextRefresh)) continue;
                if (!await account.Gate.WaitAsync(0)) continue;
                try { await ReadAsync(account); }
                catch (Exception ex)
                {
                    if (!account.Removed) ApplyResult(account, false, false, null, null, ex.Message);
                }
                finally { account.Gate.Release(); }
                if (_disposed) break;
            }
        }
        finally { _refreshing = false; Changed?.Invoke(); }
    }

    async Task ReadAsync(SecondaryAccountState account)
    {
        account.NextRefresh = DateTime.UtcNow.AddSeconds(account.Settings.Provider == AccountProvider.Claude
            ? Math.Max(60, _settings.ClaudeWebPollSeconds) : Math.Max(30, _settings.CodexPollSeconds));
        if (account.Settings.Provider == AccountProvider.Claude)
        {
            var result = await Claude(account).FetchAsync(includeChats: false);
            if (account.Removed || _disposed) return;
            ApplyResult(account, result.State == ClaudeWebState.Ok, result.State == ClaudeWebState.LoginRequired,
                result.OrgName, result.Usage, result.Error);
        }
        else
        {
            Directory.CreateDirectory(CodexDirectory(account));
            var result = await CodexAppServer.QueryAsync(_settings.CodexExePath, account.Lifetime.Token, CodexDirectory(account));
            if (account.Removed || _disposed) return;
            ApplyResult(account, result.Ok, result.LoginRequired, result.Email, result.Usage, result.Error);
        }
        Changed?.Invoke();
    }

    internal static void ApplyResult(SecondaryAccountState account, bool ok, bool loginRequired,
        string? name, UsageSnapshot? usage, string? error)
    {
        account.HasRefreshError = !ok && !loginRequired;
        if (loginRequired)
        {
            account.Usage = null;
            account.AccountName = null;
            account.Status = "브라우저 로그인이 필요합니다.";
        }
        else if (ok)
        {
            account.Usage = usage;
            account.AccountName = name;
            account.Status = usage == null ? "사용량 데이터 없음" : $"연결됨 · {usage.AsOf.ToLocalTime():HH:mm} 기준";
        }
        else account.Status = (account.Usage == null ? "조회 실패: " :
            $"이전 사용량 표시 중 · 마지막 확인 {account.Usage.AsOf.ToLocalTime():M/d HH:mm}\n조회 실패: ") + error;
    }

    /// <summary>같은 서비스의 추가 계정 순서를 교환한다. 성공하면 호출자가 설정을 저장한다.</summary>
    public bool SwapAccounts(string firstId, string secondId)
    {
        if (_disposed || firstId == secondId) return false;
        int first = _accounts.FindIndex(x => x.Settings.Id == firstId && !x.Removed);
        int second = _accounts.FindIndex(x => x.Settings.Id == secondId && !x.Removed);
        if (first < 0 || second < 0 || _accounts[first].Settings.Provider != _accounts[second].Settings.Provider)
            return false;
        (_accounts[first], _accounts[second]) = (_accounts[second], _accounts[first]);
        _settings.SecondaryAccounts = _accounts.Select(x => x.Settings).ToList();
        Changed?.Invoke();
        return true;
    }

    public async Task RemoveAsync(string id)
    {
        var account = _accounts.FirstOrDefault(x => x.Settings.Id == id);
        if (account == null || account.Removed) return;
        account.Removed = true;
        account.Lifetime.Cancel();
        account.Status = "연결 삭제 중…";
        Changed?.Invoke();
        await account.Gate.WaitAsync();
        try
        {
            if (account.Settings.Provider == AccountProvider.Claude &&
                (account.Claude != null || Directory.Exists(Path.Combine(AccountDirectory(id), "webview"))))
                await Claude(account).LogoutAsync();
            else if (account.Settings.Provider == AccountProvider.Codex && Directory.Exists(CodexDirectory(account)))
                await CodexAppServer.LogoutAsync(_settings.CodexExePath, CodexDirectory(account));
            account.Claude?.Dispose();
            account.Claude = null;
            // The account-specific path is validated before recursive cleanup.
            var directory = AccountDirectory(id);
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { /* WebView may still hold its cleared cache. */ }
                catch (UnauthorizedAccessException) { /* Authentication was already cleared above. */ }
            }
            _accounts.Remove(account);
            _settings.SecondaryAccounts.Remove(account.Settings);
            _settings.Save();
        }
        catch (Exception ex)
        {
            // Keep the row when logout fails, so deletion can be retried instead of silently retaining credentials.
            account.Removed = false;
            account.Lifetime.Dispose();
            account.Lifetime = new();
            account.Status = "연결 삭제 실패: " + ex.Message;
        }
        finally { account.Gate.Release(); Changed?.Invoke(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        foreach (var account in _accounts)
        {
            account.Lifetime.Cancel();
            account.Claude?.Dispose();
        }
    }
}
