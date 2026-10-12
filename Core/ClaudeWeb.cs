using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMonitor.Core;

public enum ClaudeWebState { NotConfigured, Ok, LoginRequired, Error }

public sealed class ClaudeWebResult
{
    public ClaudeWebState State { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public string? OrgName { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 앱 전용 WebView2 프로필로 claude.ai 에 로그인해 두고, 설정 → 사용량 화면과 같은 데이터를
/// 페이지 안에서 fetch 해서 읽는다. 세션 쿠키는 WebView2 프로필 밖으로 꺼내지 않는다.
/// </summary>
public sealed class ClaudeWebClient : IDisposable
{
    const string Origin = "https://claude.ai";
    const string OriginPage = Origin + "/api/organizations";

    Window? _host;
    IntPtr _hwnd;
    CoreWebView2Controller? _controller;
    bool _originReady;
    Task? _init;

    public event Action? LoginWindowClosed;
    public bool LoginWindowOpen { get; private set; }

    const string FetchScript = """
        (async () => {
          const get = async (u) => {
            const r = await fetch(u, { credentials: 'include', headers: { accept: 'application/json' } });
            const t = await r.text(); let j = null; try { j = JSON.parse(t); } catch (e) {}
            return { status: r.status, json: j };
          };
          try {
            const o = await get('/api/organizations');
            if (o.status === 401 || (o.status === 403 && o.json)) return JSON.stringify({ state: 'login', status: o.status });
            if (o.status === 403) return JSON.stringify({ state: 'challenge' });
            if (!Array.isArray(o.json)) return JSON.stringify({ state: 'error', detail: 'organizations HTTP ' + o.status });
            const m = document.cookie.match(/(?:^|;\s*)lastActiveOrg=([^;]+)/);
            const active = m ? decodeURIComponent(m[1]) : null;
            const score = (x) => {
              const c = x.capabilities || []; let s = 0;
              if (x.uuid === active) s += 100;
              if (c.includes('claude_max')) s += 20;
              if (c.includes('claude_pro')) s += 10;
              if (c.includes('chat')) s += 5;
              return s;
            };
            const orgs = o.json.slice().sort((a, b) => score(b) - score(a));
            for (const org of orgs) {
              const u = await get('/api/organizations/' + org.uuid + '/usage');
              if (u.status === 200 && u.json && typeof u.json === 'object')
                return JSON.stringify({ state: 'ok', org: { name: org.name, capabilities: org.capabilities || [], tier: org.rate_limit_tier || null }, usage: u.json });
            }
            return JSON.stringify({ state: 'error', detail: 'usage endpoint unavailable (' + orgs.length + ' orgs)' });
          } catch (e) {
            return JSON.stringify({ state: 'error', detail: String(e) });
          }
        })()
        """;

    // 조회가 끝나면 잠시 뒤 WebView2(브라우저 프로세스들)를 닫아 메모리를 돌려준다
    readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    async Task EnsureInitAsync()
    {
        _releaseTimer.Stop();
        var init = _init ??= InitAsync();
        try
        {
            await init;
        }
        catch
        {
            // 같은 초기화를 기다리던 호출이 새로 시작한 초기화까지 지우지 않도록 한다.
            if (ReferenceEquals(_init, init))
            {
                try { _controller?.Close(); } catch { }
                _controller = null;
                _init = null;
                _originReady = false;
            }
            throw;
        }
    }

    void ReleaseSoon()
    {
        _releaseTimer.Stop();
        if (!LoginWindowOpen) _releaseTimer.Start();
    }

    void Release()
    {
        _releaseTimer.Stop();
        if (LoginWindowOpen) return;
        try { _controller?.Close(); } catch { }
        _controller = null;
        _init = null;
        _originReady = false;
    }

    async Task InitAsync()
    {
        if (_host == null) CreateHost();
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
        var controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
        _controller = controller;
        controller.IsVisible = LoginWindowOpen;
        controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
        controller.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // 새 창(구글 로그인 팝업 등)은 같은 뷰에서 연다
        controller.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            controller.CoreWebView2.Navigate(e.Uri);
        };
        controller.CoreWebView2.SourceChanged += async (_, _) =>
        {
            // 로그인 후 채팅 화면으로 넘어가면 창을 자동으로 닫는다
            if (!LoginWindowOpen || !IsSignedInPage(controller.CoreWebView2.Source)) return;
            await Task.Delay(1200);
            if (LoginWindowOpen && IsSignedInPage(controller.CoreWebView2.Source)) HideLogin();
        };
        UpdateBounds();
    }

    void CreateHost()
    {
        _releaseTimer.Tick += (_, _) => Release();
        _host = new Window
        {
            Title = "Claude 로그인 — AI 사용량 모니터 (로그인하면 자동으로 닫힙니다)",
            Width = 520,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };
        _hwnd = new WindowInteropHelper(_host).EnsureHandle();
        _host.SizeChanged += (_, _) => UpdateBounds();
        _host.DpiChanged += (_, _) => UpdateBounds();
        _host.Closing += (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            HideLogin();
        };
    }

    static bool IsSignedInPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == "claude.ai" &&
        (u.AbsolutePath.StartsWith("/new") || u.AbsolutePath.StartsWith("/chat") ||
         u.AbsolutePath.StartsWith("/recents") || u.AbsolutePath.StartsWith("/project"));

    public async void ShowLogin()
    {
        try
        {
            await EnsureInitAsync();
            _originReady = false;
            LoginWindowOpen = true;
            _host!.Show();
            _host.Activate();
            _controller!.IsVisible = true;
            UpdateBounds();
            _controller.CoreWebView2.Navigate(Origin + "/login");
        }
        catch (Exception ex)
        {
            Log.Write($"claude login window failed: {ex}");
            MessageBox.Show("WebView2를 시작하지 못했습니다.\n" + ex.Message, "AI 사용량 모니터");
        }
    }

    void HideLogin()
    {
        if (_controller != null) _controller.IsVisible = false;
        _host?.Hide();
        LoginWindowOpen = false;
        _originReady = false;
        LoginWindowClosed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        await EnsureInitAsync();
        _controller!.CoreWebView2.CookieManager.DeleteAllCookies();
        await _controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        _originReady = false;
        ReleaseSoon();
    }

    public async Task<ClaudeWebResult> FetchAsync()
    {
        try
        {
            await EnsureInitAsync();
            if (LoginWindowOpen)
                return new ClaudeWebResult { State = ClaudeWebState.Error, Error = "로그인 창이 열려 있음" };

            if (!_originReady)
            {
                await NavigateAsync(OriginPage, TimeSpan.FromSeconds(40));
                _originReady = true;
            }

            var raw = await EvaluateAsync(FetchScript, TimeSpan.FromSeconds(40));
            if (raw == null) return new ClaudeWebResult { State = ClaudeWebState.Error, Error = "응답 없음" };

            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            switch (r.Str("state"))
            {
                case "login":
                    _originReady = false;
                    return new ClaudeWebResult { State = ClaudeWebState.LoginRequired };
                case "ok":
                    var org = r.Obj("org");
                    var plan = PlanFromOrg(org);
                    return new ClaudeWebResult
                    {
                        State = ClaudeWebState.Ok,
                        OrgName = org?.Str("name"),
                        Usage = ParseUsage(r.GetProperty("usage"), plan),
                    };
                case "challenge":
                    _originReady = false;
                    return new ClaudeWebResult { State = ClaudeWebState.Error, Error = "claude.ai 보안 확인 필요 — 로그인 창을 한 번 열어 주세요" };
                default:
                    _originReady = false;
                    return new ClaudeWebResult { State = ClaudeWebState.Error, Error = r.Str("detail") ?? "알 수 없는 오류" };
            }
        }
        catch (Exception ex)
        {
            _originReady = false;
            Log.Write($"claude web fetch failed: {ex.Message}");
            return new ClaudeWebResult { State = ClaudeWebState.Error, Error = ex.Message };
        }
        finally
        {
            ReleaseSoon();
        }
    }

    static string? PlanFromOrg(JsonElement? org)
    {
        if (org is not { } o) return null;
        var tier = o.Str("tier") ?? "";
        if (tier.Contains("max_20x")) return "Max 20x";
        if (tier.Contains("max_5x")) return "Max 5x";
        var caps = o.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        if (caps.Contains("claude_max")) return "Max";
        if (caps.Contains("claude_pro")) return "Pro";
        return null;
    }

    static readonly string[] Order = ["five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus", "seven_day_oauth_apps"];

    internal static UsageSnapshot ParseUsage(JsonElement usage, string? plan)
    {
        var windows = new List<UsageWindow>();
        var extras = new List<string>();
        foreach (var p in usage.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (p.Name == "extra_usage")
            {
                if (p.Value.Bool("is_enabled") == true && p.Value.Num("utilization") is double eu)
                    extras.Add($"추가 사용량 {eu:0.#}% 사용");
                continue;
            }
            if (p.Value.Num("utilization") is not double util) continue;
            var reset = JsonUtil.Iso(p.Value.Str("resets_at"));
            bool primary = p.Name is "five_hour" or "seven_day";
            bool known = Array.IndexOf(Order, p.Name) >= 0;
            // 안 쓰는 세부 한도와, 의미를 모르는 내부 항목(사용량 0)은 숨긴다
            if (!primary && util <= 0 && (reset == null || !known)) continue;
            windows.Add(new UsageWindow(p.Name, LabelFor(p.Name), util, reset));
        }
        windows.Sort((a, b) => Rank(a.Key).CompareTo(Rank(b.Key)));
        return new UsageSnapshot { Windows = windows, AsOf = DateTimeOffset.Now, Source = "claude.ai", Plan = plan, Extras = extras };

        static int Rank(string k) => Array.IndexOf(Order, k) is var i and >= 0 ? i : 99;
    }

    static string LabelFor(string key) => key switch
    {
        "five_hour" => "5시간 한도",
        "seven_day" => "주간 한도",
        "seven_day_sonnet" => "주간 Sonnet",
        "seven_day_opus" => "주간 Opus",
        "seven_day_oauth_apps" => "주간 (연동 앱)",
        _ => "기타 한도",
    };

    async Task NavigateAsync(string url, TimeSpan timeout)
    {
        var wv = _controller!.CoreWebView2;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        wv.NavigationCompleted += Done;
        try
        {
            wv.Navigate(url);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (finished != tcs.Task) throw new TimeoutException("claude.ai 페이지 로드 시간 초과");
        }
        finally
        {
            wv.NavigationCompleted -= Done;
        }
    }

    async Task<string?> EvaluateAsync(string script, TimeSpan timeout)
    {
        var args = JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true });
        var call = _controller!.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
        var finished = await Task.WhenAny(call, Task.Delay(timeout));
        if (finished != call) throw new TimeoutException("claude.ai 응답 시간 초과");
        using var doc = JsonDocument.Parse(await call);
        var result = doc.RootElement.Obj("result");
        return result?.Str("value");
    }

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    void UpdateBounds()
    {
        if (_controller == null || _hwnd == IntPtr.Zero) return;
        if (GetClientRect(_hwnd, out var r))
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
    }

    bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _releaseTimer.Stop();
        try { _controller?.Close(); } catch { }
        try { _host?.Close(); } catch { }
    }
}
