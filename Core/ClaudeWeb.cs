using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>claude.ai 앱 대화 중 진행 중이거나 최근 활동한 것. 대화 목록을 못 읽었으면 null.</summary>
    public List<SessionInfo>? Chats { get; init; }
    public string? ChatsError { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Claude Desktop의 기존 로그인 또는 선택한 웹 로그인으로 사용량을 조회한다.
/// Desktop 인증은 InPrivate WebView에서만 사용하고 별도 로그인 파일에 저장하지 않는다.
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
    readonly bool _desktopSession;
    readonly string _webViewData;
    string? _desktopIdentity;

    public ClaudeWebClient(bool desktopSession = false, string? profileDirectory = null)
    {
        _desktopSession = desktopSession;
        _webViewData = profileDirectory ?? Paths.WebViewData;
    }
    public event Action? AccountChanged;

    public event Action? LoginWindowClosed;
    public bool LoginWindowOpen { get; private set; }

    // 서버 응답에서 제목·상태만 골라 넘긴다. 프로젝트 타임라인의 본문은 사용하거나 저장하지 않는다.
    const string Prelude = """
          const get = async (u, headers = {}, signal = AbortSignal.timeout(12000)) => {
            const r = await fetch(u, { credentials: 'include', headers: { accept: 'application/json', ...headers }, signal });
            const t = await r.text(); let j = null; try { j = JSON.parse(t); } catch (e) {}
            return { status: r.status, json: j };
          };
          const projectThreadsOf = async (orgId, signal) => {
            const headers = { 'x-organization-uuid': orgId, 'anthropic-version': '2023-06-01',
              'anthropic-beta': 'ccr-byoc-2025-07-29', 'anthropic-client-feature': 'ccr' };
            const pages = async (url) => {
              const rows = []; const seen = new Set(); let cursor = null;
              for (let page = 0; page < 20; page++) {
                const r = await get(url + (cursor ? '&cursor=' + encodeURIComponent(cursor) : ''), headers, signal);
                if (r.status !== 200 || !Array.isArray(r.json?.data))
                  throw new Error('프로젝트 작업 조회 실패 (HTTP ' + r.status + ')');
                rows.push(...r.json.data);
                cursor = r.json.next_cursor;
                if (!cursor) return rows;
                if (typeof cursor !== 'string' || seen.has(cursor)) break;
                seen.add(cursor);
              }
              throw new Error('프로젝트 작업 목록을 끝까지 읽지 못했습니다.');
            };
            const projects = (await pages('/v1/code/channels?scope=joined&limit=200')).filter(p => !p.archived_at);
            const result = []; let next = 0;
            await Promise.all(Array.from({ length: Math.min(4, projects.length) }, async () => {
              while (next < projects.length) {
                const project = projects[next++];
                if (typeof project.id !== 'string' || !project.id) throw new Error('프로젝트 ID가 없습니다.');
                const rows = await pages('/v1/code/channels/' + encodeURIComponent(project.id) +
                  '/timeline?limit=200&bound_only=true&thread_roots_only=true');
                for (const row of rows) {
                  const session = row.bound_sessions?.find(s => s.title) || row.bound_sessions?.[0];
                  if (!session || !row.id) continue;
                  result.push({ source_kind: 'project_thread', uuid: row.id, name: session.title || '프로젝트 작업',
                    project: project.name || '', project_uuid: project.id, thread_status: row.thread_status ?? null,
                    updated_at: row.last_reply_at || row.created_at || null,
                    workspace_session_id: typeof session.id === 'string' ? session.id.replace(/^cse_/, 'session_') : null });
                }
              }
            }));
            return result;
          };
          const chatsOf = async (orgId) => {
            const signal = AbortSignal.timeout(25000);
            const [c, projects] = await Promise.all([
              get('/api/organizations/' + orgId + '/chat_conversations_v2?limit=20&offset=0&archived=false&consistency=strong', {}, signal),
              projectThreadsOf(orgId, signal)
            ]);
            const list = c.json && Array.isArray(c.json.data) ? c.json.data : (Array.isArray(c.json) ? c.json : null);
            if (!list) return { status: c.status, chats: null };
            return { status: c.status, chats: [...list.map(x => ({
              uuid: x.uuid, name: x.name || '', updated_at: x.updated_at || null,
              live_status: x.live_status || null, live_status_started_at: x.live_status_started_at || null,
              needs_input: x.needs_input ?? null, needs_input_kind: x.needs_input_kind || null,
              project: (x.project && x.project.name) || null, workspace_session_id: x.workspace_session_id || null,
            })), ...projects] };
          };
        """;

    const string FetchScript = "(async () => {\n" + Prelude + "\n" + """
          try {
            const o = await get('/api/organizations');
            if (o.status === 401 || (o.status === 403 && o.json)) return JSON.stringify({ state: 'login', status: o.status });
            if (o.status === 403) return JSON.stringify({ state: 'challenge' });
            if (!Array.isArray(o.json)) return JSON.stringify({ state: 'error', detail: 'organizations HTTP ' + o.status });
            const m = document.cookie.match(/(?:^|;\s*)lastActiveOrg=([^;]+)/);
            const active = m ? decodeURIComponent(m[1]) : null;
            if (__DESKTOP__ && (active ? !o.json.some(x => x.uuid === active) : o.json.length !== 1))
              return JSON.stringify({ state: 'error', detail: 'Claude 데스크톱에서 사용할 계정을 선택해 주세요.' });
            const score = (x) => {
              const c = x.capabilities || []; let s = 0;
              if (x.uuid === active) s += 100;
              if (c.includes('claude_max')) s += 20;
              if (c.includes('claude_pro')) s += 10;
              if (c.includes('chat')) s += 5;
              return s;
            };
            const orgs = (__DESKTOP__ && active ? o.json.filter(x => x.uuid === active) : o.json.slice()).sort((a, b) => score(b) - score(a));
            for (const org of orgs) {
              const u = await get('/api/organizations/' + org.uuid + '/usage');
              if (u.status === 200 && u.json && typeof u.json === 'object') {
                let chats = null, chats_error = null;
                if (__INCLUDE_CHATS__) try {
                  const c = await chatsOf(org.uuid); chats = c.chats;
                  if (!chats) chats_error = '대화 목록 조회 실패 (HTTP ' + c.status + ')';
                } catch (e) { chats_error = String(e); }
                return JSON.stringify({ state: 'ok', org: { uuid: org.uuid, name: org.name, capabilities: org.capabilities || [], tier: org.rate_limit_tier || null }, usage: u.json, chats, chats_error });
              }
            }
            return JSON.stringify({ state: 'error', detail: 'usage endpoint unavailable (' + orgs.length + ' orgs)' });
          } catch (e) {
            return JSON.stringify({ state: 'error', detail: String(e) });
          }
        })()
        """;

    /// <summary>대화 목록만 다시 읽는다. __ORG__ 자리에 조직 uuid(JSON 문자열)가 들어간다.</summary>
    const string ChatsScript = "(async () => {\n" + Prelude + "\n" + """
          try {
            const c = await chatsOf(__ORG__);
            if (!c.chats) return JSON.stringify({ state: 'error', status: c.status, detail: 'chat_conversations HTTP ' + c.status });
            return JSON.stringify({ state: 'ok', chats: c.chats });
          } catch (e) {
            return JSON.stringify({ state: 'error', detail: String(e) });
          }
        })()
        """;

    // 조회가 끝나면 잠시 뒤 WebView2(브라우저 프로세스들)를 닫아 메모리를 돌려준다
    readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    // 사용량 조회와 대화 목록 조회가 같은 WebView를 동시에 쓰지 않도록 한다
    readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>마지막으로 사용량을 읽은 조직. 대화 목록만 따로 읽을 때 쓴다.</summary>
    string? _orgUuid;
    bool _keepAlive;

    /// <summary>
    /// true면 조회가 끝나도 WebView2를 닫지 않는다. 대화 목록을 자주 확인하는 동안(30초 간격)
    /// 매번 브라우저 프로세스를 새로 띄우지 않으려고 쓴다.
    /// </summary>
    public bool KeepAlive
    {
        get => _keepAlive;
        set
        {
            if (_keepAlive == value) return;
            _keepAlive = value;
            if (!value && _controller != null) ReleaseSoon();
        }
    }

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
        if (!LoginWindowOpen && !KeepAlive) _releaseTimer.Start();
    }

    void Release()
    {
        _releaseTimer.Stop();
        if (LoginWindowOpen) return;
        // 조회 중이면 닫지 않고 조회가 끝난 뒤로 미룬다 (조회의 finally 에서 다시 예약된다)
        if (_gate.CurrentCount == 0) return;
        try { _controller?.Close(); } catch { }
        _controller = null;
        _init = null;
        _originReady = false;
    }

    async Task InitAsync()
    {
        if (_host == null) CreateHost();
        var env = await CoreWebView2Environment.CreateAsync(null, _webViewData);
        CoreWebView2Controller controller;
        if (_desktopSession)
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "ClaudeDesktop";
            options.IsInPrivateModeEnabled = true;
            controller = await env.CreateCoreWebView2ControllerAsync(_hwnd, options);
        }
        else controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
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
         u.AbsolutePath.StartsWith("/recents") || u.AbsolutePath.StartsWith("/project") ||
         u.AbsolutePath.StartsWith("/cowork"));

    public async Task LoginAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed() => closed.TrySetResult();
        LoginWindowClosed += OnClosed;
        try
        {
            await OpenLoginAsync();
            await closed.Task.WaitAsync(ct);
        }
        finally
        {
            LoginWindowClosed -= OnClosed;
            if (LoginWindowOpen) HideLogin();
            _gate.Release();
            ReleaseSoon();
        }
    }

    async Task OpenLoginAsync()
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

    public async void ShowLogin()
    {
        try
        {
            await OpenLoginAsync();
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
        _orgUuid = null;
        ReleaseSoon();
    }

    /// <summary>사용량과 앱 대화 목록을 함께 읽는다. chatCutoff 이전에 끝난 대화는 뺀다.</summary>
    public async Task<ClaudeWebResult> FetchAsync(DateTimeOffset? chatCutoff = null, bool includeChats = true)
    {
        await _gate.WaitAsync();
        try
        {
            var r = await RunAsync(FetchScript, includeChats);
            if (r.Error is { } error) return error;
            var root = r.Root;
            switch (root.Str("state"))
            {
                case "login":
                    _originReady = false;
                    _orgUuid = null;
                    return new ClaudeWebResult { State = ClaudeWebState.LoginRequired };
                case "ok":
                    var org = root.Obj("org");
                    var plan = PlanFromOrg(org);
                    _orgUuid = org?.Str("uuid");
                    return new ClaudeWebResult
                    {
                        State = ClaudeWebState.Ok,
                        OrgName = org?.Str("name"),
                        Usage = ParseUsage(root.GetProperty("usage"), plan, _desktopSession ? "Claude 데스크톱" : "claude.ai"),
                        Chats = root.TryGetProperty("chats", out var chats) && chats.ValueKind == JsonValueKind.Array
                            ? ParseChats(chats, chatCutoff ?? DefaultCutoff())
                            : null,
                        ChatsError = root.Str("chats_error"),
                    };
                case "challenge":
                    _originReady = false;
                    return new ClaudeWebResult { State = ClaudeWebState.Error, Error = _desktopSession
                        ? "Claude 데스크톱에서 사용량 화면을 확인한 뒤 새로고침해 주세요."
                        : "claude.ai 보안 확인 필요 — 로그인 창을 한 번 열어 주세요" };
                default:
                    _originReady = false;
                    return new ClaudeWebResult { State = ClaudeWebState.Error, Error = root.Str("detail") ?? "알 수 없는 오류" };
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
            _gate.Release();
            ReleaseSoon();
        }
    }

    /// <summary>
    /// 앱 대화 목록만 읽는다 (사용량보다 자주 확인). 아직 조직을 모르면 사용량까지 한 번에 읽는다.
    /// 로그인 만료 같은 판단은 사용량 조회에 맡기고, 여기서 실패하면 Error 만 돌려준다.
    /// </summary>
    public async Task<ClaudeWebResult> FetchChatsAsync(DateTimeOffset? chatCutoff = null)
    {
        if (_orgUuid is not { } orgUuid) return await FetchAsync(chatCutoff);

        await _gate.WaitAsync();
        try
        {
            var r = await RunAsync(ChatsScript.Replace("__ORG__", JsonSerializer.Serialize(orgUuid)));
            if (r.Error is { } error) return error;
            var root = r.Root;
            if (root.Str("state") == "ok" && root.TryGetProperty("chats", out var chats))
                return new ClaudeWebResult { State = ClaudeWebState.Ok, Chats = ParseChats(chats, chatCutoff ?? DefaultCutoff()) };

            // 401/403 이면 다음 조회에서 조직과 로그인 상태부터 다시 확인한다
            if (root.Num("status") is 401.0 or 403.0) { _orgUuid = null; _originReady = false; }
            return new ClaudeWebResult { State = ClaudeWebState.Error, Error = root.Str("detail") ?? "대화 목록 조회 실패" };
        }
        catch (Exception ex)
        {
            _originReady = false;
            Log.Write($"claude chats fetch failed: {ex.Message}");
            return new ClaudeWebResult { State = ClaudeWebState.Error, Error = ex.Message };
        }
        finally
        {
            _gate.Release();
            ReleaseSoon();
        }
    }

    sealed record ScriptRun(JsonElement Root, ClaudeWebResult? Error);

    /// <summary>claude.ai 페이지를 준비하고 스크립트를 실행한다. 결과 JSON은 호출자가 쓰는 동안 살아 있어야 해서 Clone 한다.</summary>
    async Task<ScriptRun> RunAsync(string script, bool includeChats = true)
    {
        if (_desktopSession)
        {
            var cookies = await Task.Run(() => ClaudeDesktopSession.Read());
            var session = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
            var org = cookies.FirstOrDefault(c => c.Name == "lastActiveOrg")?.Value;
            var identity = session == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session + "\n" + org)));
            bool changed = identity != _desktopIdentity;
            if (changed)
            {
                _desktopIdentity = identity;
                _controller?.CoreWebView2.CookieManager.DeleteAllCookies();
                _originReady = false;
                _orgUuid = null;
                AccountChanged?.Invoke();
            }
            if (session == null)
                return new ScriptRun(default, new ClaudeWebResult { State = ClaudeWebState.LoginRequired });

            await EnsureInitAsync();
            var manager = _controller!.CoreWebView2.CookieManager;
            foreach (var value in cookies)
            {
                var cookie = manager.CreateCookie(value.Name, value.Value, value.Domain, value.Path);
                cookie.IsHttpOnly = value.HttpOnly;
                cookie.IsSecure = value.Secure;
                manager.AddOrUpdateCookie(cookie);
            }
            cookies.Clear();
            if (changed && script != FetchScript)
                return new ScriptRun(default, new ClaudeWebResult { State = ClaudeWebState.Error, Error = "데스크톱 계정이 변경되어 사용량을 다시 확인합니다." });
        }
        await EnsureInitAsync();
        if (LoginWindowOpen)
            return new ScriptRun(default, new ClaudeWebResult { State = ClaudeWebState.Error, Error = "로그인 창이 열려 있음" });

        if (!_originReady)
        {
            await NavigateAsync(OriginPage, TimeSpan.FromSeconds(40));
            _originReady = true;
        }

        var raw = await EvaluateAsync(script.Replace("__DESKTOP__", _desktopSession ? "true" : "false")
            .Replace("__INCLUDE_CHATS__", includeChats ? "true" : "false"), TimeSpan.FromSeconds(40));
        if (raw == null) return new ScriptRun(default, new ClaudeWebResult { State = ClaudeWebState.Error, Error = "응답 없음" });
        using var doc = JsonDocument.Parse(raw);
        return new ScriptRun(doc.RootElement.Clone(), null);
    }

    static DateTimeOffset DefaultCutoff() => DateTimeOffset.UtcNow.AddHours(-12);

    /// <summary>
    /// claude.ai 대화 목록(chat_conversations_v2)을 카드용 세션으로 바꾼다.
    /// 진행 중(live_status = running)이거나 입력을 기다리는 대화는 항상, 나머지는 cutoff 이후에 활동한 것만 남긴다.
    /// </summary>
    public static List<SessionInfo> ParseChats(JsonElement chats, DateTimeOffset cutoff)
    {
        var list = new List<SessionInfo>();
        if (chats.ValueKind != JsonValueKind.Array) return list;

        foreach (var c in chats.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || c.Str("uuid") is not { Length: > 0 } uuid) continue;
            if (c.Str("source_kind") == "project_thread")
            {
                var status = c.Str("thread_status")?.ToLowerInvariant().Replace("thread_status_", "");
                if (c.Num("thread_status") is { } number)
                    status = number switch { 1 => "resolved", 2 => "failed", 3 => "working", 4 => "needs_input", 5 => "archived", 6 => "idle", _ => null };
                if (status == "archived") continue;
                var activity = JsonUtil.Iso(c.Str("updated_at"));
                var (taskState, taskText) = status switch
                {
                    "working" => (SessionState.Working, "작업 중"),
                    "needs_input" => (SessionState.NeedsInput, "입력 대기"),
                    "resolved" => (SessionState.Idle, "완료"),
                    "failed" => (SessionState.Idle, "실패"),
                    "idle" => (SessionState.Idle, "대기"),
                    _ => (SessionState.Stale, "상태 확인 필요"),
                };
                if (taskState is not (SessionState.Working or SessionState.NeedsInput) &&
                    (activity is not { } taskAt || taskAt < cutoff)) continue;
                var projectName = c.Str("project");
                var sessionId = c.Str("workspace_session_id");
                list.Add(new SessionInfo($"chat:{uuid}", c.Str("name") is { Length: > 0 } projectTitle ? projectTitle : "프로젝트 작업",
                    string.IsNullOrEmpty(projectName) ? "프로젝트 작업" : $"{projectName} · 프로젝트 작업",
                    taskState, taskText, activity, false,
                    OpenUri: sessionId is { Length: > 0 } ? $"claude://claude.ai/cowork/{Uri.EscapeDataString(sessionId)}" : null,
                    AltId: sessionId));
                continue;
            }

            var live = c.Str("live_status");
            bool needs = IsSet(c, "needs_input");
            var updated = JsonUtil.Iso(c.Str("updated_at"));
            var started = JsonUtil.Iso(c.Str("live_status_started_at"));
            bool running = string.Equals(live, "running", StringComparison.OrdinalIgnoreCase);
            if (!needs && !running && (updated is not { } u || u < cutoff)) continue;

            var kind = c.Str("needs_input_kind") ?? "";
            var (state, text, at, sinceChange) = (needs, running) switch
            {
                (true, _) when kind.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                               kind.Contains("approv", StringComparison.OrdinalIgnoreCase)
                    => (SessionState.NeedsInput, "승인 대기", updated, true),
                (true, _) => (SessionState.NeedsInput, "입력 대기", updated, true),
                (_, true) => (SessionState.Working, "작업 중", started ?? updated, true),
                // 모르는 상태값은 그대로 보여 준다
                _ when !string.IsNullOrEmpty(live) => (SessionState.Idle, live, updated, false),
                _ => (SessionState.Idle, "완료", updated, false),
            };

            var title = c.Str("name") is { Length: > 0 } n ? n : "새 대화";
            var subtitle = c.Str("project") is { Length: > 0 } p ? $"{p} · 앱 대화" : "앱 대화";
            list.Add(new SessionInfo($"chat:{uuid}", title, subtitle, state, text, at, sinceChange,
                OpenUri: $"claude://claude.ai/chat/{Uri.EscapeDataString(uuid)}",
                AltId: c.Str("workspace_session_id")));
        }
        return list;

        static bool IsSet(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => v.GetString() is { Length: > 0 },
                JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
                JsonValueKind.Object => v.EnumerateObject().Any(),
                JsonValueKind.Array => v.GetArrayLength() > 0,
                _ => false,
            };
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

    internal static UsageSnapshot ParseUsage(JsonElement usage, string? plan, string source = "claude.ai")
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
            if (p.Value.Num("utilization") is not double util || !double.IsFinite(util) || util < 0 || util > 100) continue;
            var reset = JsonUtil.Iso(p.Value.Str("resets_at"));
            bool primary = p.Name is "five_hour" or "seven_day";
            bool known = Array.IndexOf(Order, p.Name) >= 0;
            // 안 쓰는 세부 한도와, 의미를 모르는 내부 항목(사용량 0)은 숨긴다
            if (!primary && util <= 0 && (reset == null || !known)) continue;
            windows.Add(new UsageWindow(p.Name, LabelFor(p.Name), util, reset));
        }
        // 새 사용량 화면의 모델별 한도(Fable 등)는 limits 배열에만 들어온다.
        if (usage.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.Num("percent") is not double percent || !double.IsFinite(percent) || percent < 0 || percent > 100) continue;
                var kind = limit.Str("kind");
                var scope = limit.Obj("scope");
                var label = scope?.Obj("model")?.Str("display_name") ?? scope?.Obj("surface")?.Str("display_name");
                string? key = kind switch
                {
                    "session" => "five_hour",
                    "weekly_all" => "seven_day",
                    "weekly_scoped" when !string.IsNullOrWhiteSpace(label) => "seven_day_" + label.Trim().ToLowerInvariant(),
                    _ => null,
                };
                if (key == null) continue;
                windows.RemoveAll(w => w.Key == key);
                windows.Add(new UsageWindow(key, kind == "weekly_scoped" ? "주간 " + label!.Trim() : LabelFor(key), percent, JsonUtil.Iso(limit.Str("resets_at"))));
            }
        }
        windows.Sort((a, b) => Rank(a.Key).CompareTo(Rank(b.Key)));
        return new UsageSnapshot { Windows = windows, AsOf = DateTimeOffset.Now, Source = source, Plan = plan, Extras = extras };

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
