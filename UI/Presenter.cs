using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

public static class Palette
{
    static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Claude = B("#D97757");
    public static readonly SolidColorBrush Codex = B("#7AA2F7");
    public static readonly SolidColorBrush Ok = B("#5BB98C");
    public static readonly SolidColorBrush Warn = B("#E5A54B");
    public static readonly SolidColorBrush Bad = B("#E5534B");
    public static readonly SolidColorBrush Muted = B("#8B8E98");
    public static readonly SolidColorBrush Text = B("#E8E8EA");

    public static SolidColorBrush Level(double used)
    {
        double remaining = 100 - Math.Clamp(used, 0, 100);
        var from = remaining <= 20 ? Bad.Color : Warn.Color;
        var to = remaining <= 20 ? Warn.Color : Ok.Color;
        double fraction = remaining <= 20 ? remaining / 20 : (remaining - 20) / 80;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * fraction);
        var brush = new SolidColorBrush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush State(SessionState s) => s switch
    {
        SessionState.Working => Ok,
        SessionState.NeedsInput => Warn,
        SessionState.Stale => Bad,
        _ => Muted,
    };
}

public sealed class UsageRow
{
    public required string Label { get; init; }
    public required double Value { get; init; }
    public required string UsedText { get; init; }
    public required string RemainText { get; init; }
    public required string ResetText { get; init; }
    public string ResetShort { get; init; } = "";
    public bool IsResetPending { get; init; }
    public Visibility BarVisibility => IsResetPending ? Visibility.Hidden : Visibility.Visible;
    public required Brush LevelBrush { get; init; }
}

public sealed class SessionRow
{
    public string Id { get; init; } = "";
    public string? OpenUri { get; init; }
    public ICommand? OpenCommand { get; init; }
    public string AppName { get; init; } = "";
    public string ToolTipText => OpenCommand != null ? $"{Title}\n클릭하면 {AppName} 앱에서 열기" : Title;
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string StateText { get; init; }
    public required Brush StateBrush { get; init; }
}

public sealed class ProviderCard
{
    public required string Name { get; init; }
    public required Brush Accent { get; init; }
    public string? Plan { get; init; }
    public required string AppStatus { get; init; }
    public required Brush AppStatusBrush { get; init; }
    public string? Account { get; init; }
    public List<UsageRow> Usage { get; init; } = [];
    public List<AccountUsageSlot> AccountSlots { get; set; } = [];
    public string? UsageEmpty { get; init; }
    public string? Extras { get; init; }
    public string? Notice { get; init; }
    public Brush NoticeBrush { get; init; } = Palette.Warn;
    public string? ActionText { get; init; }
    public ICommand? ActionCommand { get; init; }
    public required string Footnote { get; init; }
    public required string SessionsHeader { get; init; }
    public List<SessionRow> Sessions { get; init; } = [];
    public string? SessionsEmpty { get; init; }

    /// <summary>트레이 아이콘과 알림용: 가장 많이 쓴 한도의 사용률.</summary>
    public double? MaxUsed { get; init; }
    public List<UsageWindow> RawWindows { get; init; } = [];
    public int WorkingCount { get; init; }
    public int NeedsInputCount { get; init; }
    /// <summary>최근 30분 안에 생긴 대기만 (캐릭터 연출용).</summary>
    public int FreshNeedsInputCount { get; init; }
    public bool AppRunning { get; init; }
    public bool LimitReached { get; init; }
    public bool UsageResetPending { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public DateTimeOffset? SoonestReset { get; init; }

    public Visibility PlanVisibility => Vis(Plan);
    public Visibility AccountVisibility => Vis(Account);
    public Visibility UsageEmptyVisibility => Vis(UsageEmpty);
    public Visibility ExtrasVisibility => Vis(Extras);
    public Visibility NoticeVisibility => Vis(Notice);
    public Visibility ActionVisibility => Vis(ActionText);
    public Visibility SessionsEmptyVisibility => Vis(SessionsEmpty);

    static Visibility Vis(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    public string Signature()
    {
        var sb = new StringBuilder();
        sb.Append(Plan).Append('|').Append(AppStatus).Append('|').Append(Account).Append('|').Append(UsageEmpty).Append('|')
          .Append(Extras).Append('|').Append(Notice).Append('|').Append(ActionText).Append('|').Append(Footnote).Append('|')
          .Append(SessionsHeader).Append('|').Append(SessionsEmpty).Append('|');
        foreach (var u in Usage) sb.Append(u.Label).Append(u.UsedText).Append(u.RemainText).Append(u.ResetText).Append(';');
        foreach (var slot in AccountSlots) sb.Append(slot.Signature).Append(';');
        foreach (var s in Sessions) sb.Append(s.Id).Append('|').Append(s.OpenUri).Append('|')
            .Append(s.Title).Append(s.Subtitle).Append(s.StateText).Append(s.OpenCommand != null).Append(';');
        return sb.ToString();
    }
}

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    ProviderCard? _claude, _codex;
    string _footer = "";
    string _claudeSig = "", _codexSig = "";

    public ProviderCard? Claude => _claude;
    public ProviderCard? Codex => _codex;
    public string Footer => _footer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ProviderCard claude, ProviderCard codex, string footer)
    {
        var cs = claude.Signature();
        if (cs != _claudeSig) { _claude = claude; _claudeSig = cs; Raise(nameof(Claude)); }
        var xs = codex.Signature();
        if (xs != _codexSig) { _codex = codex; _codexSig = xs; Raise(nameof(Codex)); }
        if (footer != _footer) { _footer = footer; Raise(nameof(Footer)); }
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Fmt
{
    public static string? AccountId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int at = name.IndexOf('@');
        return (at >= 0 ? name[..at] : name).Trim().ToUpperInvariant();
    }

    static readonly string[] Days = ["일", "월", "화", "수", "목", "금", "토"];

    public static string Ago(DateTimeOffset? t)
    {
        if (t is not { } v) return "";
        var d = DateTimeOffset.Now - v;
        if (d.TotalSeconds < 45) return "방금";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}분 전";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}시간 전";
        return $"{(int)d.TotalDays}일 전";
    }

    /// <summary>"15분째" 처럼 어떤 상태가 이어진 시간.</summary>
    public static string Since(DateTimeOffset? t)
    {
        if (t is not { } v) return "";
        var d = DateTimeOffset.Now - v;
        if (d.TotalMinutes < 1) return "방금 시작";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}분째";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}시간째";
        return $"{(int)d.TotalDays}일째";
    }

    public static string Span(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "1분 미만";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}분";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}시간 {d.Minutes}분";
        return $"{(int)d.TotalDays}일 {d.Hours}시간";
    }

    public static string ShortSpan(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0M";
        if (duration.TotalMinutes < 1) return "<1M";
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}D {duration.Hours}H {duration.Minutes}M";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}H {duration.Minutes}M";
        return $"{duration.Minutes}M";
    }

    public static string When(DateTimeOffset t)
    {
        var l = t.ToLocalTime();
        return $"{l:M/d}({Days[(int)l.DayOfWeek]}) {l:HH:mm}";
    }

    public static string Pct(double v) => v > 0 && v < 1 ? $"{v:0.#}%" : $"{v:0}%";
}

/// <summary>MonitorService 상태를 화면용 카드로 바꾼다.</summary>
public static class Presenter
{
    // 카드 크기는 고정이고 목록은 스크롤되므로 넉넉하게
    const int MaxSessions = 30;
    // 이보다 오래 대화 목록을 못 읽었으면 "작업 중"·"대기" 표시를 믿지 않는다
    static readonly TimeSpan ChatStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 이 PC에서 실행 중인 Claude Code 세션과 claude.ai 앱 대화(클라우드에서 실행되는 대화 포함)를 합친 목록.
    /// 같은 세션이 양쪽에 있으면 로컬 세션만 남긴다.
    /// </summary>
    public static List<SessionInfo> ClaudeSessions(MonitorService m)
    {
        var local = m.ClaudeLocal?.Sessions ?? [];
        var known = m.ClaudeLocal?.KnownIds ?? [];
        bool stale = m.ClaudeChatsError != null || m.ClaudeChatsAt is not { } at || DateTimeOffset.Now - at > ChatStaleAfter;

        var chats = m.ClaudeChats
            .Where(c => !known.Contains(c.Id.StartsWith("chat:", StringComparison.Ordinal) ? c.Id[5..] : c.Id) &&
                        (c.AltId == null || !known.Contains(c.AltId)))
            .Select(c => stale && c.State is SessionState.Working or SessionState.NeedsInput
                ? c with { State = SessionState.Stale, StateText = "갱신 안 됨" }
                : c);
        return local.Concat(chats).ToList();
    }

    static bool IsFiveHour(UsageWindow w) => w.Key is "five_hour" || w.Key.EndsWith(":w300", StringComparison.Ordinal);
    static bool IsWeekly(UsageWindow w) => w.Key is "seven_day" || w.Key.EndsWith(":w10080", StringComparison.Ordinal);

    static bool IsExpired(UsageWindow window, DateTimeOffset now) => window.ResetsAt is { } reset && reset <= now;
    static bool IsFable(UsageWindow window) => window.Key.Equals("seven_day_fable", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<UsageWindow> PickDisplayWindows(List<UsageWindow> windows, DateTimeOffset now)
    {
        if (windows.Count == 0) yield break;
        var five = windows.Where(w => IsFiveHour(w) && !IsExpired(w, now)).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        if (five is { UsedPercent: >= 90 })
        {
            yield return five with { Label = "5시간 한도" };
            yield break;
        }
        var weekly = windows.FirstOrDefault(IsWeekly);
        if (weekly != null) yield return weekly with { Label = "주간 한도" };
        else if (windows.FirstOrDefault(w => IsFiveHour(w) && IsExpired(w, now) && w.UsedPercent >= 90) is { } pending)
            yield return pending with { Label = "5시간 한도" };
    }

    public static List<UsageRow> DisplayUsage(IEnumerable<UsageWindow> windows, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.Now;
        var effective = windows.Select(w => w with
        {
            UsedPercent = Math.Clamp(w.UsedPercent, 0, 100),
        }).ToList();
        return PickDisplayWindows(effective, now).Select(w => new UsageRow
        {
            Label = w.Label, Value = w.UsedPercent,
            IsResetPending = IsExpired(w, now),
            UsedText = IsExpired(w, now) ? "확인 중" : Fmt.Pct(w.UsedPercent),
            RemainText = IsExpired(w, now) ? "" : $" 사용 · {Fmt.Pct(100 - w.UsedPercent)} 남음",
            ResetText = IsExpired(w, now)
                ? "리셋 시각 경과 · 새 사용량 확인 중"
                : w.ResetsAt is { } at ? $"리셋까지 {Fmt.Span(at - now)} · {Fmt.When(at)}" : "",
            ResetShort = IsExpired(w, now) ? "확인 중" : w.ResetsAt is { } resets ? Fmt.ShortSpan(resets - now) : "—",
            LevelBrush = IsExpired(w, now) ? Palette.Muted : Palette.Level(w.UsedPercent),
        }).ToList();
    }

    public static ProviderCard Claude(MonitorService m, AppSettings s, Action login)
    {
        var local = m.ClaudeLocal;
        var good = m.ClaudeWebGood;
        var last = m.ClaudeWebLast;
        // Desktop과 CLI가 서로 다른 계정일 수 있으므로 Desktop 사용량에 CLI 값을 섞지 않는다.
        var usage = s.ClaudeDesktopEnabled ? good?.Usage : Merge(good?.Usage, local?.StatusLineUsage);

        string? notice = null, action = null, empty = null;
        if (s.ClaudeDesktopEnabled)
        {
            if (last?.State == ClaudeWebState.LoginRequired)
            {
                empty = "Claude 데스크톱에서 로그인하면 자동으로 연결됩니다.";
                action = "Claude 데스크톱 열기";
            }
            else if (last?.State == ClaudeWebState.Error)
                notice = $"데스크톱 연결 확인: {last.Error}";
        }
        else if (!s.ClaudeWebEnabled)
        {
            if (usage == null) empty = "주간 사용량은 claude.ai 로그인 후 표시됩니다.";
            action = "claude.ai 로그인";
        }
        else if (last?.State == ClaudeWebState.LoginRequired)
        {
            notice = "claude.ai 로그인이 필요합니다.";
            action = "다시 로그인";
        }
        else if (last?.State == ClaudeWebState.Error)
        {
            notice = $"사용량 조회 실패: {last.Error}";
        }
        if (m.ClaudeChatsError is { } chatsError)
            notice ??= $"작업 상태 갱신 실패: {chatsError}";
        if (usage == null && empty == null)
            empty = m.ClaudeWebBusy ? "사용량을 불러오는 중…" : "사용량 데이터 없음";

        var (app, appBrush) = local switch
        {
            { DesktopRunning: true } => ("앱 실행 중", (Brush)Palette.Ok),
            { CliProcessCount: > 0 } => ("CLI 실행 중", Palette.Ok),
            null => ("확인 중…", Palette.Muted),
            _ => ("실행 안 됨", Palette.Muted),
        };

        var sessions = ClaudeSessions(m);
        return Build("Claude", Palette.Claude, usage, local is { DesktopRunning: true } or { CliProcessCount: > 0 }, app, appBrush, good?.OrgName, notice, action,
            action != null ? new RelayCommand(login) : null, empty,
            Footnote(usage, m.ClaudeWebBusy), sessions,
            m.ClaudeEnabled ? "최근 대화·세션 없음" : "실행 중인 Claude Code 세션 없음 (앱 대화는 연결 후 표시)");
    }

    public static ProviderCard Codex(MonitorService m)
    {
        var local = m.CodexLocal;
        var good = m.CodexRemoteGood;
        var last = m.CodexRemoteLast;

        UsageSnapshot? usage = good?.Usage;
        if (local?.LogUsage is { } log && (usage == null || log.AsOf > usage.AsOf))
        {
            // 로그에 있는 한도만 갱신하고 서버 전용 한도와 리셋권은 유지한다.
            var extras = log.Extras.Concat(usage?.Extras.Where(e => !e.StartsWith("크레딧")) ?? []).ToList();
            usage = new UsageSnapshot
            {
                Windows = log.Windows.Concat(usage?.Windows ?? []).DistinctBy(w => w.Key).ToList(),
                AsOf = log.AsOf,
                Source = usage != null ? $"{log.Source} + {usage.Source} (이전 조회)" : log.Source,
                Plan = log.Plan ?? usage?.Plan, Extras = extras, Warning = log.Warning,
            };
        }

        string? notice = usage?.Warning;
        if (last is { Ok: false })
            notice ??= usage == null
                ? $"사용량 조회 실패: {last.Error}"
                : $"서버 조회 실패 ({usage.Source} 값 표시 중): {last.Error}";

        var (app, appBrush) = local switch
        {
            { DesktopRunning: true } => ("앱 실행 중", (Brush)Palette.Ok),
            { CliProcessCount: > 0 } => ("CLI 실행 중", Palette.Ok),
            null => ("확인 중…", Palette.Muted),
            _ => ("실행 안 됨", Palette.Muted),
        };

        var empty = usage == null ? (m.CodexRemoteBusy ? "사용량을 불러오는 중…" : "사용량 데이터 없음") : null;
        var threads = local?.Threads ?? [];
        return Build("Codex", Palette.Codex, usage, local is { DesktopRunning: true } or { CliProcessCount: > 0 }, app, appBrush, good?.Email, notice, null, null, empty,
            Footnote(usage, m.CodexRemoteBusy), threads, "최근 활동한 스레드 없음");
    }

    static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"open {uri} failed: {ex.Message}"); }
    }

    static string TimeText(SessionInfo s) =>
        s.ActivityIsStateChange && s.State is SessionState.Working or SessionState.NeedsInput
            ? Fmt.Since(s.LastActivity)
            : Fmt.Ago(s.LastActivity);

    static UsageSnapshot? Merge(UsageSnapshot? web, UsageSnapshot? statusLine)
    {
        if (web == null) return statusLine;
        if (statusLine == null || statusLine.AsOf <= web.AsOf) return web;
        // 상태줄 값이 더 최신이면 5시간/주간 값만 덮어쓴다 (Opus/Sonnet 세부 한도는 웹에만 있음)
        var byKey = statusLine.Windows.ToDictionary(w => w.Key);
        return new UsageSnapshot
        {
            Windows = web.Windows.Select(w => byKey.TryGetValue(w.Key, out var n) ? n with { Label = w.Label } : w).ToList(),
            AsOf = statusLine.AsOf,
            Source = statusLine.Source,
            Plan = web.Plan,
            Extras = web.Extras,
        };
    }

    static string Footnote(UsageSnapshot? usage, bool busy)
    {
        var parts = new List<string>();
        if (usage != null) parts.Add($"출처: {usage.Source} · {Fmt.Ago(usage.AsOf)} 기준");
        if (busy) parts.Add("갱신 중…");
        return string.Join("  ", parts);
    }

    static ProviderCard Build(string name, Brush accent, UsageSnapshot? usage, bool appRunning, string app, Brush appBrush, string? account,
        string? notice, string? action, ICommand? cmd, string? empty, string footnote, List<SessionInfo> sessions, string sessionsEmpty)
    {
        var now = DateTimeOffset.Now;
        var rows = DisplayUsage(usage?.Windows ?? [], now);
        var effective = new List<UsageWindow>();
        var relevant = (usage?.Windows ?? []).Where(w => !IsFable(w)).ToList();
        bool resetPending = relevant.Any(w => IsExpired(w, now));
        foreach (var w in relevant.Where(w => !IsExpired(w, now)))
        {
            effective.Add(w with { UsedPercent = Math.Clamp(w.UsedPercent, 0, 100) });
        }

        var ordered = sessions
            .OrderBy(x => x.State switch { SessionState.NeedsInput => 0, SessionState.Working => 1, SessionState.Stale => 2, _ => 3 })
            .ThenByDescending(x => x.LastActivity)
            .ToList();
        int working = ordered.Count(x => x.State == SessionState.Working);
        int needs = ordered.Count(x => x.State == SessionState.NeedsInput);

        var header = new StringBuilder(name == "Codex" ? $"스레드 {ordered.Count}" : $"세션 {ordered.Count}");
        if (working > 0) header.Append($" · 작업 중 {working}");
        if (needs > 0) header.Append($" · 대기 {needs}");

        return new ProviderCard
        {
            Name = name,
            Accent = accent,
            Plan = usage?.Plan,
            AppStatus = app,
            AppStatusBrush = appBrush,
            Account = Fmt.AccountId(account),
            Usage = rows,
            UsageEmpty = rows.Count == 0 ? empty ?? "사용량 데이터 없음" : null,
            Extras = usage is { Extras.Count: > 0 } ? string.Join(" · ", usage.Extras) : null,
            Notice = notice,
            NoticeBrush = notice?.Contains("도달") == true ? Palette.Bad : Palette.Warn,
            ActionText = action,
            ActionCommand = cmd,
            Footnote = footnote,
            SessionsHeader = header.ToString(),
            Sessions = ordered.Take(MaxSessions).Select(x => new SessionRow
            {
                Id = x.Id,
                OpenUri = x.OpenUri,
                AppName = name,
                OpenCommand = x.OpenUri is { } uri ? new RelayCommand(() => OpenUri(uri)) : null,
                Title = x.Title,
                Subtitle = string.Join(" · ", new[] { x.Subtitle, TimeText(x) }.Where(t => !string.IsNullOrEmpty(t))),
                StateText = x.StateText,
                StateBrush = Palette.State(x.State),
            }).ToList(),
            SessionsEmpty = ordered.Count == 0 ? sessionsEmpty : null,
            MaxUsed = effective.Count > 0 ? effective.Max(w => w.UsedPercent) : null,
            RawWindows = effective,
            WorkingCount = working,
            NeedsInputCount = needs,
            FreshNeedsInputCount = ordered.Count(x => x.State == SessionState.NeedsInput &&
                (x.Id.StartsWith("chat:", StringComparison.Ordinal) ||
                 x.LastActivity is not { } la || now - la < TimeSpan.FromMinutes(30))),
            AppRunning = appRunning,
            LimitReached = !resetPending && usage?.Warning?.Contains("도달") == true,
            UsageResetPending = resetPending,
            LastActivity = ordered.Count > 0 ? ordered.Max(x => x.LastActivity) : null,
            SoonestReset = effective.OrderByDescending(w => w.UsedPercent).FirstOrDefault()?.ResetsAt,
        };
    }
}
