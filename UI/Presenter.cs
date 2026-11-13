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
    public static readonly SolidColorBrush Gemini = B("#B48CF2");
    public static readonly SolidColorBrush Grok = B("#D8BD72");
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
    /// <summary>리셋 시각. 남은 시간 글자를 카드 교체 없이 다시 계산할 때 쓴다.</summary>
    public DateTimeOffset? ResetsAt { get; init; }
    public bool IsResetPending { get; init; }
    public bool IsUnknown { get; init; }
    public bool IsUnlimited { get; init; }
    public double Remaining => IsUnknown || IsResetPending ? 0 : 100 - Math.Clamp(Value, 0, 100);
    public Visibility BarVisibility => IsResetPending || IsUnknown ? Visibility.Hidden : Visibility.Visible;
    public required Brush LevelBrush { get; init; }

    /// <summary>지금 시각 기준 "리셋까지 …" 문구. 리셋 시각이 지났으면 만들 때의 문구를 그대로 쓴다.</summary>
    public string ResetTextAt(DateTimeOffset now) =>
        !IsResetPending && ResetsAt is { } at && at > now ? $"리셋까지 {Fmt.Span(at - now)} · {Fmt.When(at)}" : ResetText;

    /// <summary>지금 시각 기준 "5D 12H 24M" 문구.</summary>
    public string ResetShortAt(DateTimeOffset now) =>
        !IsResetPending && ResetsAt is { } at && at > now ? Fmt.ShortSpan(at - now) : ResetShort;
}

/// <summary>
/// 세션 한 줄. 목록 구성(Id·제목·상태)이 같으면 카드를 바꾸지 않고, 활동 시각과 "N분 전" 글자만 갱신한다.
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string? OpenUri { get; init; }
    public ICommand? OpenCommand { get; init; }
    public string AppName { get; init; } = "";
    public required string Title { get; init; }
    /// <summary>폴더·종류 같은 고정 부제 (시간 글자 제외).</summary>
    public required string Subtitle { get; init; }
    public required string StateText { get; init; }
    public required Brush StateBrush { get; init; }
    public SessionState State { get; init; } = SessionState.Idle;
    public bool ActivityIsStateChange { get; init; }
    public DateTimeOffset? LastActivity { get; set; }

    /// <summary>"3분 전" 또는 작업·대기 중이면 "15분째".</summary>
    public string TimeText => ActivityIsStateChange && State is SessionState.Working or SessionState.NeedsInput
        ? Fmt.Since(LastActivity)
        : Fmt.Ago(LastActivity);

    public string DetailText => string.Join(" · ", new[] { Subtitle, TimeText }.Where(t => !string.IsNullOrEmpty(t)));

    public string ToolTipText => string.Join('\n', new[]
    {
        Title, DetailText, OpenCommand != null ? $"클릭하면 {AppName} 앱에서 열기" : null,
    }.Where(t => !string.IsNullOrEmpty(t)));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Tick()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
    }
}

/// <summary>카드 헤더의 상태 칩 종류. 앞쪽 다섯 개는 조회 결과에서, Busy·Fresh·Stale 은 시각에서 정한다.</summary>
public enum StatusKind { None, LoginRequired, NotInstalled, Disabled, Failed, Busy, Fresh, Stale }

/// <summary>
/// 데이터 기준 시각과 조회 상태. "N분 전"·오래됨 판정은 시간에 따라 바뀌므로 카드 서명에 넣지 않고,
/// 같은 카드를 유지한 채 <see cref="CopyFrom"/>·<see cref="Tick"/> 으로 화면만 갱신한다.
/// </summary>
public sealed class StatusChip : INotifyPropertyChanged
{
    public StatusChip() { }

    public StatusChip(StatusKind state, DateTimeOffset? asOf, TimeSpan staleAfter, bool busy, string? detail = null)
    {
        State = state;
        AsOf = asOf;
        StaleAfter = staleAfter;
        Busy = busy;
        Detail = detail;
    }

    /// <summary>조회 결과로 정해진 상태 (None·LoginRequired·NotInstalled·Disabled·Failed).</summary>
    public StatusKind State { get; private set; }
    public DateTimeOffset? AsOf { get; private set; }
    public TimeSpan StaleAfter { get; private set; } = TimeSpan.FromMinutes(5);
    public bool Busy { get; private set; }
    /// <summary>오류 문구 (툴팁용).</summary>
    public string? Detail { get; private set; }

    public bool IsStaleAt(DateTimeOffset now) => AsOf is { } at && now - at > StaleAfter;

    public StatusKind KindAt(DateTimeOffset now) => State switch
    {
        StatusKind.LoginRequired or StatusKind.NotInstalled or StatusKind.Disabled => State,
        _ when Busy => StatusKind.Busy,
        StatusKind.Failed => StatusKind.Failed,
        _ when AsOf == null => StatusKind.None,
        _ when IsStaleAt(now) => StatusKind.Stale,
        _ => StatusKind.Fresh,
    };

    public string TextAt(DateTimeOffset now) => KindAt(now) switch
    {
        StatusKind.LoginRequired => "로그인 필요",
        StatusKind.NotInstalled => "CLI 미설치",
        StatusKind.Disabled => "꺼짐",
        StatusKind.Failed => "⚠ 조회 실패",
        StatusKind.Busy => "갱신 중…",
        StatusKind.Stale => $"⚠ 오래됨 · {AgoAt(now)}",
        StatusKind.Fresh => AgoAt(now),
        _ => "데이터 없음",
    };

    /// <summary>사용량 원을 흐리게 할지: 조회 실패나 오래된 값 (갱신 중이어도 값 자체가 오래되면 흐리게).</summary>
    public bool IsDimmedAt(DateTimeOffset now) =>
        State == StatusKind.Failed || (State == StatusKind.None && IsStaleAt(now));

    public StatusKind Kind => KindAt(DateTimeOffset.Now);
    public string Text => TextAt(DateTimeOffset.Now);
    public bool IsDimmed => IsDimmedAt(DateTimeOffset.Now);

    public Brush Brush => Kind switch
    {
        StatusKind.Failed or StatusKind.LoginRequired => Palette.Bad,
        StatusKind.Stale => Palette.Warn,
        _ => Palette.Muted,
    };

    public string ToolTipText
    {
        get
        {
            var now = DateTimeOffset.Now;
            var kind = KindAt(now);
            // 로그인 필요는 서비스별 안내(Detail)가 일반 문구보다 정확하므로 그것만 쓴다
            if (kind == StatusKind.LoginRequired && !string.IsNullOrEmpty(Detail))
                return AsOf is { } since ? $"{Detail}\n기준 시각 {Fmt.When(since)} ({AgoAt(now)})" : Detail;
            var lines = new List<string>
            {
                kind switch
                {
                    StatusKind.LoginRequired => "로그인이 필요합니다.",
                    StatusKind.NotInstalled => "CLI를 찾지 못했습니다.",
                    StatusKind.Disabled => "설정에서 조회를 껐습니다.",
                    StatusKind.Failed => "마지막 조회가 실패했습니다. 이전 값을 흐리게 표시합니다.",
                    StatusKind.Busy => "사용량을 다시 확인하는 중입니다.",
                    StatusKind.Stale => $"값이 오래되었습니다 ({Fmt.Span(StaleAfter)}보다 오래됨).",
                    StatusKind.Fresh => "최신 사용량입니다.",
                    _ => "아직 사용량을 받지 못했습니다.",
                },
            };
            if (AsOf is { } at) lines.Add($"기준 시각 {Fmt.When(at)} ({AgoAt(now)})");
            if (!string.IsNullOrEmpty(Detail)) lines.Add(Detail);
            return string.Join('\n', lines);
        }
    }

    string AgoAt(DateTimeOffset now)
    {
        if (AsOf is not { } at) return "";
        var d = now - at;
        if (d.TotalSeconds < 45) return "방금";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}분 전";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}시간 전";
        return $"{(int)d.TotalDays}일 전";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(StatusChip other)
    {
        if (ReferenceEquals(this, other)) return;
        State = other.State;
        AsOf = other.AsOf;
        StaleAfter = other.StaleAfter;
        Busy = other.Busy;
        Detail = other.Detail;
        Tick();
    }

    /// <summary>시간이 지나 바뀌는 글자·색을 다시 읽게 한다.</summary>
    public void Tick()
    {
        foreach (var name in new[] { nameof(Kind), nameof(Text), nameof(Brush), nameof(IsDimmed), nameof(ToolTipText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
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
    public List<UsageRow> AccountUsage { get; init; } = [];
    public List<AccountUsageSlot> AccountSlots { get; set; } = [];
    public string? UsageEmpty { get; init; }
    public string? Extras { get; init; }
    /// <summary>사용량 자체의 경고 ("한도 도달", "한도 도달(추정)", "쿼터 값 확인 불가"). 원에 ⚠ 와 툴팁으로 보인다.</summary>
    public string? UsageWarning { get; init; }
    public string? Notice { get; init; }
    public Brush NoticeBrush { get; init; } = Palette.Warn;
    public string? ActionText { get; init; }
    public ICommand? ActionCommand { get; init; }
    /// <summary>데이터 출처 (시각은 <see cref="Status"/> 가 표시한다).</summary>
    public required string Footnote { get; init; }
    public required string SessionsHeader { get; init; }
    public List<SessionRow> Sessions { get; init; } = [];
    public string? SessionsEmpty { get; init; }
    /// <summary>헤더 상태 칩 (갱신 중 / N분 전 / 오래됨 / 조회 실패 / 로그인 필요 / CLI 미설치).</summary>
    public StatusChip Status { get; init; } = new();

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
    public Visibility NoticeVisibility => Vis(Notice);
    public Visibility ActionVisibility => Vis(ActionText);
    public Visibility NoticeRowVisibility => string.IsNullOrEmpty(Notice) && string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SessionsEmptyVisibility => Vis(SessionsEmpty);

    static Visibility Vis(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 화면 구성이 바뀌었는지 판단하는 서명. 시간이 지나면 저절로 바뀌는 글자("N분 전", "리셋까지 …")와
    /// 조회 진행 상태는 넣지 않는다 (같은 카드에서 <see cref="RefreshFrom"/>·<see cref="Tick"/> 으로 갱신).
    /// </summary>
    public string Signature()
    {
        var sb = new StringBuilder();
        sb.Append(Plan).Append('|').Append(AppStatus).Append('|').Append(Account).Append('|').Append(UsageEmpty).Append('|')
          .Append(Extras).Append('|').Append(UsageWarning).Append('|').Append(Notice).Append('|').Append(ActionText).Append('|').Append(Footnote).Append('|')
          .Append(SessionsHeader).Append('|').Append(SessionsEmpty).Append('|');
        foreach (var u in Usage)
            sb.Append(u.Label).Append(u.UsedText).Append(u.RemainText).Append(u.IsResetPending).Append(u.ResetsAt?.UtcTicks).Append(';');
        foreach (var u in AccountUsage)
            sb.Append(u.Label).Append(u.Value).Append(u.IsUnknown).Append(u.IsUnlimited).Append(u.IsResetPending).Append(u.ResetsAt?.UtcTicks).Append(';');
        foreach (var slot in AccountSlots) sb.Append(slot.Signature).Append(';');
        foreach (var s in Sessions) sb.Append(s.Id).Append('|').Append(s.OpenUri).Append('|')
            .Append(s.Title).Append(s.Subtitle).Append(s.StateText).Append(s.OpenCommand != null).Append(s.ActivityIsStateChange).Append(';');
        return sb.ToString();
    }

    /// <summary>서명이 같은 새 카드의 시간 관련 값(상태 칩, 세션 활동 시각)만 가져온다.</summary>
    public void RefreshFrom(ProviderCard newer)
    {
        Status.CopyFrom(newer.Status);
        // 계정 칸 서명에는 조회 시각이 없다: 숫자가 그대로인 계정도 새 기준 시각을 받아야 오래됨으로 흐려지지 않는다.
        for (int i = 0; i < AccountSlots.Count && i < newer.AccountSlots.Count; i++)
            if (AccountSlots[i].Id == newer.AccountSlots[i].Id) AccountSlots[i].RefreshFrom(newer.AccountSlots[i]);
        for (int i = 0; i < Sessions.Count && i < newer.Sessions.Count; i++)
            if (Sessions[i].Id == newer.Sessions[i].Id) Sessions[i].LastActivity = newer.Sessions[i].LastActivity;
        Tick();
    }

    public void Tick()
    {
        Status.Tick();
        foreach (var s in Sessions) s.Tick();
        foreach (var slot in AccountSlots) slot.Tick();
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
    ProviderCard? _claude, _codex, _grok;
    string _footer = "";
    string _claudeSig = "", _codexSig = "", _grokSig = "";

    public ProviderCard? Claude => _claude;
    public ProviderCard? Codex => _codex;
    public ProviderCard? Grok => _grok;
    public string Footer => _footer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ProviderCard claude, ProviderCard codex, string footer) => Update(claude, codex, null, footer);

    public void Update(ProviderCard claude, ProviderCard codex, ProviderCard? grok, string footer)
    {
        Apply(ref _claude, ref _claudeSig, claude, nameof(Claude));
        Apply(ref _codex, ref _codexSig, codex, nameof(Codex));
        if (grok != null) Apply(ref _grok, ref _grokSig, grok, nameof(Grok));
        if (footer != _footer) { _footer = footer; Raise(nameof(Footer)); }
    }

    void Apply(ref ProviderCard? current, ref string signature, ProviderCard next, string name)
    {
        var s = next.Signature();
        if (current == null || s != signature)
        {
            // The card is replaced, but its account slots that did not change (same position, account and
            // slot signature) carry over as the same objects, refreshed: the main window shows these objects
            // while its slot layout is unchanged, so it must keep seeing the card's current slots.
            if (current != null) next.AccountSlots = KeepUnchangedSlots(current.AccountSlots, next.AccountSlots);
            current = next;
            signature = s;
            Raise(name);
        }
        // 구성이 같으면 카드를 그대로 두어 목록·툴팁·포커스가 유지된다
        else current.RefreshFrom(next);
    }

    /// <summary>Newer slots, where each one unchanged from the previous card is that previous object refreshed.</summary>
    public static List<AccountUsageSlot> KeepUnchangedSlots(IReadOnlyList<AccountUsageSlot> previous, IReadOnlyList<AccountUsageSlot> newer) =>
        newer.Select((slot, i) =>
        {
            var old = i < previous.Count ? previous[i] : null;
            if (old == null || old.Id != slot.Id || old.Signature != slot.Signature) return slot;
            old.RefreshFrom(slot);
            return old;
        }).ToList();

    /// <summary>"N분 전"·남은 시간·오래됨 표시를 다시 계산한다 (App 의 시계 타이머가 부른다).</summary>
    public void Tick()
    {
        _claude?.Tick();
        _codex?.Tick();
        _grok?.Tick();
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
    // 기준 시각이 조회 주기의 이 배수보다 오래되면 "오래됨" (너무 자주 깜빡이지 않게 최소 5분)
    const int StaleFactor = 3;
    static readonly TimeSpan MinStaleAfter = TimeSpan.FromMinutes(5);

    static TimeSpan StaleAfter(int pollSeconds, int minPollSeconds) =>
        TimeSpan.FromSeconds(Math.Max(MinStaleAfter.TotalSeconds, StaleFactor * Math.Max(minPollSeconds, pollSeconds)));

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

    static bool IsFiveHour(UsageWindow w) => w.Key is "five_hour" || HasWindowPart(w.Key, "w300");
    static bool IsWeekly(UsageWindow w) => w.Key is "seven_day" or "weekly" || HasWindowPart(w.Key, "w10080");

    /// <summary>"codex:w300", "gemini:w300", "gemini:w300:&lt;id&gt;" 처럼 콜론으로 나눈 조각 중 그룹 이름 뒤에 창 길이가 있는지.</summary>
    static bool HasWindowPart(string key, string window) =>
        key.Split(':') is { Length: >= 2 } parts && parts.Skip(1).Contains(window, StringComparer.Ordinal);

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
        else if (windows.FirstOrDefault(w => w.Key == "monthly") is { } monthly) yield return monthly with { Label = "월간 한도" };
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
        return PickDisplayWindows(effective, now).Select(w => UsageRowFor(w, now)).ToList();
    }

    static UsageRow UsageRowFor(UsageWindow w, DateTimeOffset now) => new()
    {
        Label = w.Label, Value = w.UsedPercent,
        IsResetPending = IsExpired(w, now),
        ResetsAt = w.ResetsAt,
        UsedText = IsExpired(w, now) ? "확인 중" : Fmt.Pct(w.UsedPercent),
        RemainText = IsExpired(w, now) ? "" : $" 사용 · {Fmt.Pct(100 - w.UsedPercent)} 남음",
        ResetText = IsExpired(w, now)
            ? "리셋 시각 경과 · 새 사용량 확인 중"
            : w.ResetsAt is { } at ? $"리셋까지 {Fmt.Span(at - now)} · {Fmt.When(at)}" : "",
        ResetShort = IsExpired(w, now) ? "확인 중" : w.ResetsAt is { } resets ? Fmt.ShortSpan(resets - now) : "—",
        LevelBrush = IsExpired(w, now) ? Palette.Muted : Palette.Level(w.UsedPercent),
    };

    /// <summary>계정 메뉴는 원형 요약과 달리 5시간·주간 한도를 항상 같은 순서로 보여 준다.</summary>
    public static List<UsageRow> AccountUsageRows(IEnumerable<UsageWindow> windows, bool fiveHourUnlimited = false, DateTimeOffset? asOf = null)
    {
        var all = windows.Select(w => w with { UsedPercent = Math.Clamp(w.UsedPercent, 0, 100) }).ToList();
        var now = asOf ?? DateTimeOffset.Now;
        UsageRow Row(bool five)
        {
            var match = all.Where(w => five ? IsFiveHour(w) : IsWeekly(w) || w.Key == "monthly").OrderByDescending(w => w.UsedPercent).FirstOrDefault();
            var label = five ? "5시간 한도" : match?.Key == "monthly" ? "월간 한도" : "주간 한도";
            if (match != null) return UsageRowFor(match with { Label = label }, now);
            bool unlimited = five && fiveHourUnlimited;
            return new UsageRow
            {
                Label = label, Value = 0, UsedText = unlimited ? "무제한" : "확인 중", RemainText = "",
                ResetText = unlimited ? "5시간 제한 없음" : "한도 정보 없음", IsUnlimited = unlimited, IsUnknown = !unlimited,
                LevelBrush = unlimited ? Palette.Ok : Palette.Muted,
            };
        }
        return [Row(true), Row(false)];
    }

    public static ProviderCard Claude(MonitorService m, AppSettings s, Action login)
    {
        var local = m.ClaudeLocal;
        var good = m.ClaudeWebGood;
        var last = m.ClaudeWebLast;
        // Desktop과 CLI가 서로 다른 계정일 수 있으므로 Desktop 사용량에 CLI 값을 섞지 않는다.
        var usage = s.ClaudeDesktopEnabled ? good?.Usage : Merge(good?.Usage, local?.StatusLineUsage);

        string? notice = null, action = null, empty = null;
        var state = StatusKind.None;
        string? detail = null;
        if (s.ClaudeDesktopEnabled)
        {
            if (last?.State == ClaudeWebState.LoginRequired)
            {
                empty = "Claude 데스크톱에서 로그인하면 자동으로 연결됩니다.";
                action = "Claude 데스크톱 열기";
                state = StatusKind.LoginRequired;
            }
            else if (last?.State == ClaudeWebState.Error)
            {
                notice = $"데스크톱 연결 확인: {last.Error}";
                state = StatusKind.Failed;
                detail = last.Error;
            }
        }
        else if (!s.ClaudeWebEnabled)
        {
            if (usage == null) empty = "주간 사용량은 claude.ai 로그인 후 표시됩니다.";
            action = "claude.ai 로그인";
            if (usage == null) state = StatusKind.LoginRequired;
        }
        else if (last?.State == ClaudeWebState.LoginRequired)
        {
            notice = "claude.ai 로그인이 필요합니다.";
            action = "다시 로그인";
            state = StatusKind.LoginRequired;
        }
        else if (last?.State == ClaudeWebState.Error)
        {
            notice = $"사용량 조회 실패: {last.Error}";
            state = StatusKind.Failed;
            detail = last.Error;
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

        var status = new StatusChip(state, usage?.AsOf, StaleAfter(s.ClaudeWebPollSeconds, 60), m.ClaudeWebBusy, detail);
        var sessions = ClaudeSessions(m);
        return Build("Claude", Palette.Claude, usage, local is { DesktopRunning: true } or { CliProcessCount: > 0 }, app, appBrush, good?.OrgName, notice, action,
            action != null ? new RelayCommand(login) : null, empty,
            Footnote(usage), sessions,
            m.ClaudeEnabled ? "최근 대화·세션 없음" : "실행 중인 Claude Code 세션 없음 (앱 대화는 연결 후 표시)", status);
    }

    public static ProviderCard Codex(MonitorService m, AppSettings? s = null)
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
                FiveHourUnlimited = usage?.FiveHourUnlimited == true && !log.Windows.Any(IsFiveHour) &&
                    (log.Plan == null || log.Plan == usage.Plan),
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

        var status = new StatusChip(last is { Ok: false } ? StatusKind.Failed : StatusKind.None, usage?.AsOf,
            StaleAfter((s ?? DefaultSettings).CodexPollSeconds, 30), m.CodexRemoteBusy, last is { Ok: false } ? last.Error : null);
        var empty = usage == null ? (m.CodexRemoteBusy ? "사용량을 불러오는 중…" : "사용량 데이터 없음") : null;
        var threads = local?.Threads ?? [];
        return Build("Codex", Palette.Codex, usage, local is { DesktopRunning: true } or { CliProcessCount: > 0 }, app, appBrush, good?.Email, notice, null, null, empty,
            Footnote(usage), threads, "최근 활동한 스레드 없음", status);
    }

    public static ProviderCard Grok(MonitorService monitor, AppSettings? settings = null)
    {
        settings ??= DefaultSettings;
        var last = monitor.GrokRemoteLast;
        var usage = monitor.GrokRemoteGood?.Usage;
        var state = last is { Installed: false } ? StatusKind.NotInstalled : last is { LoginRequired: true } ? StatusKind.LoginRequired :
            last is { Ok: false } ? StatusKind.Failed : StatusKind.None;
        var status = new StatusChip(state, usage?.AsOf, StaleAfter(settings.GrokPollSeconds, 120), monitor.GrokRemoteBusy, last?.Error);
        bool running = monitor.GrokProcessCount > 0;
        return Build("Grok", Palette.Grok, usage, running, running ? "CLI 실행 중" : "CLI 실행 안 됨", running ? Palette.Ok : Palette.Muted, "GROK CLI", last?.Error,
            null, null, monitor.GrokRemoteBusy ? "사용량을 불러오는 중…" : "사용량 데이터 없음", Footnote(usage), [], "", status);
    }

    static readonly AppSettings DefaultSettings = new();

    static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"open {uri} failed: {ex.Message}"); }
    }

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

    // 시각은 상태 칩이 따로 갱신하므로 출처만 둔다 (시간 글자가 들어가면 1분마다 카드가 바뀐다)
    static string Footnote(UsageSnapshot? usage) => usage != null ? $"출처: {usage.Source}" : "";

    static ProviderCard Build(string name, Brush accent, UsageSnapshot? usage, bool appRunning, string app, Brush appBrush, string? account,
        string? notice, string? action, ICommand? cmd, string? empty, string footnote, List<SessionInfo> sessions, string sessionsEmpty,
        StatusChip status, string? planFallback = null)
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

        var header = new StringBuilder(name == "Claude" ? $"세션 {ordered.Count}" : name == "Codex" ? $"스레드 {ordered.Count}" : $"대화 {ordered.Count}");
        if (working > 0) header.Append($" · 작업 중 {working}");
        if (needs > 0) header.Append($" · 대기 {needs}");

        return new ProviderCard
        {
            Name = name,
            Accent = accent,
            Plan = usage?.Plan ?? planFallback,
            AppStatus = app,
            AppStatusBrush = appBrush,
            Account = Fmt.AccountId(account),
            Usage = rows,
            AccountUsage = AccountUsageRows(usage?.Windows ?? [], usage?.FiveHourUnlimited == true, now),
            UsageEmpty = rows.Count == 0 ? empty ?? "사용량 데이터 없음" : null,
            Extras = usage is { Extras.Count: > 0 } ? string.Join(" · ", usage.Extras) : null,
            UsageWarning = usage?.Warning,
            Notice = notice,
            NoticeBrush = notice?.Contains("도달") == true ? Palette.Bad : Palette.Warn,
            ActionText = action,
            ActionCommand = cmd,
            Footnote = footnote,
            SessionsHeader = header.ToString(),
            Status = status,
            Sessions = ordered.Take(MaxSessions).Select(x => new SessionRow
            {
                Id = x.Id,
                OpenUri = x.OpenUri,
                AppName = name,
                OpenCommand = x.OpenUri is { } uri ? new RelayCommand(() => OpenUri(uri)) : null,
                Title = x.Title,
                Subtitle = x.Subtitle,
                StateText = x.StateText,
                StateBrush = Palette.State(x.State),
                State = x.State,
                ActivityIsStateChange = x.ActivityIsStateChange,
                LastActivity = x.LastActivity,
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

    // ---------- hosted by ChatterBox: accounts and usage come only from ChatterBox's account store

    /// <summary>ChatterBox refreshes usage every few minutes; older readings are shown as stale (dimmed).</summary>
    public static readonly TimeSpan HostedStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The card of one service in hosted mode: activity (sessions, app state) from this PC as before, usage
    /// and accounts from ChatterBox. The first account in ChatterBox's order is the card's main account.
    /// </summary>
    public static ProviderCard Hosted(ProviderCard local, IReadOnlyList<HostedAccount> accounts, bool received, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var main = accounts.FirstOrDefault();
        var usage = main == null ? null : ChatterBoxAccounts.Usage(main);
        var effective = (usage?.Windows ?? []).Where(w => !IsFable(w) && !IsExpired(w, at))
            .Select(w => w with { UsedPercent = Math.Clamp(w.UsedPercent, 0, 100) }).ToList();
        bool resetPending = (usage?.Windows ?? []).Any(w => IsExpired(w, at));
        var rows = HostedDisplayUsage(usage?.Windows ?? [], at);
        string? empty = !received ? "ChatterBox에서 계정 정보를 받는 중…"
            : main == null ? "ChatterBox에 등록된 계정이 없습니다."
            : main.Error != null ? "사용량 조회 실패" : rows.Count == 0 ? "사용량 데이터 없음" : null;
        var card = new ProviderCard
        {
            Name = local.Name, Accent = local.Accent, AppStatus = local.AppStatus, AppStatusBrush = local.AppStatusBrush,
            Account = main?.Label,
            Usage = rows,
            AccountUsage = AccountUsageRows(usage?.Windows ?? [], false, at),
            UsageEmpty = rows.Count == 0 ? empty ?? "사용량 데이터 없음" : null,
            Notice = main?.Error is { } error ? $"사용량 조회 실패: {error}" : main?.AuthStatus == "required" ? "ChatterBox에서 로그인이 필요합니다." : null,
            Footnote = "계정·사용량: ChatterBox",
            SessionsHeader = local.SessionsHeader, Sessions = local.Sessions, SessionsEmpty = local.SessionsEmpty,
            Status = main == null ? new StatusChip() : HostedStatus(main),
            MaxUsed = effective.Count > 0 ? effective.Max(w => w.UsedPercent) : null,
            RawWindows = effective,
            WorkingCount = local.WorkingCount, NeedsInputCount = local.NeedsInputCount, FreshNeedsInputCount = local.FreshNeedsInputCount,
            AppRunning = local.AppRunning, LastActivity = local.LastActivity,
            UsageResetPending = resetPending,
            SoonestReset = effective.OrderByDescending(w => w.UsedPercent).FirstOrDefault()?.ResetsAt,
        };
        card.AccountSlots = accounts.Select(a => HostedSlot(card, a, at)).ToList();
        return card;
    }

    /// <summary>ChatterBox's FIVE_HOUR_SWITCH_AT: the 5-hour window is shown once 10% or less of it is left.</summary>
    const double HostedFiveHourSwitchUsed = 90;

    /// <summary>
    /// The window ChatterBox shows (shared/usage.ts pickUsage), so both show the same value: the 5-hour window
    /// when nearly used up, else weekly, else monthly, else the 5-hour window when it is the only one.
    /// </summary>
    public static List<UsageRow> HostedDisplayUsage(IEnumerable<UsageWindow> windows, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.Now;
        var all = windows.Select(w => w with { UsedPercent = Math.Clamp(w.UsedPercent, 0, 100) }).ToList();
        var five = all.FirstOrDefault(IsFiveHour);
        var weekly = all.FirstOrDefault(IsWeekly);
        var monthly = all.FirstOrDefault(w => w.Key == "monthly");
        UsageWindow? pick = five is { UsedPercent: >= HostedFiveHourSwitchUsed } ? five with { Label = "5시간 한도" }
            : weekly != null ? weekly with { Label = "주간 한도" }
            : monthly != null ? monthly with { Label = "월간 한도" }
            : five != null ? five with { Label = "5시간 한도" } : null;
        return pick == null ? [] : [UsageRowFor(pick, now)];
    }

    public sealed record HostedAlertTarget(string StateKey, string Display, List<UsageWindow> Windows);

    /// <summary>
    /// Usage alerts in hosted mode: accounts in assignment whose reading is fresh (no older than
    /// <see cref="HostedStaleAfter"/>), windows not yet reset. The state key is ChatterBox's account id.
    /// </summary>
    public static List<HostedAlertTarget> HostedAlertTargets(HostedAccountsSnapshot? snapshot, DateTimeOffset now) =>
        (snapshot?.Accounts ?? [])
            .Where(a => a.Enabled && a.CheckedAt is { } at && at <= now.AddMinutes(1) && now - at <= HostedStaleAfter)
            .Select(a => (a, usage: ChatterBoxAccounts.Usage(a)))
            .Where(x => x.usage != null)
            .Select(x => new HostedAlertTarget($"chatterbox:{x.a.Id}", $"{x.a.Provider} · {x.a.Label}",
                x.usage!.Windows.Where(w => w.ResetsAt is not { } r || r > now).ToList()))
            .ToList();

    public static StatusChip HostedStatus(HostedAccount a) => new(
        a.Error != null ? StatusKind.Failed : a.AuthStatus == "required" ? StatusKind.LoginRequired : StatusKind.None,
        a.CheckedAt, HostedStaleAfter, busy: false, a.Error ?? (a.AuthStatus == "required" ? "ChatterBox의 CLI 계정 관리에서 로그인하세요." : null));

    static AccountUsageSlot HostedSlot(ProviderCard card, HostedAccount a, DateTimeOffset now)
    {
        var usage = ChatterBoxAccounts.Usage(a);
        return new AccountUsageSlot
        {
            Role = a.IsDefault ? "기본 계정" : "계정", Id = a.Id, Provider = card.Name, Account = a.Label, Draggable = false,
            Usage = HostedDisplayUsage(usage?.Windows ?? [], now).FirstOrDefault(),
            Freshness = HostedStatus(a), Disabled = !a.Enabled, HasRefreshError = a.Error != null,
            Status = string.Join('\n', new[]
            {
                a.Enabled ? null : "ChatterBox에서 배정 제외된 계정 (사용량을 새로 조회하지 않음)",
                a.RunningWorkers > 0 ? $"작업자 {a.RunningWorkers}명 실행 중" : null,
                a.Error,
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
    }

    /// <summary>Accounts panel in hosted mode: one card per ChatterBox account, in ChatterBox's order, plus the DeepSeek balance.</summary>
    public static List<AccountUsageCard> HostedAccountCards(HostedAccountsSnapshot? snapshot, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        if (snapshot == null) return [];
        var cards = snapshot.Accounts.Select(a =>
        {
            var usage = ChatterBoxAccounts.Usage(a);
            var chip = HostedStatus(a);
            var status = new[]
            {
                a.Enabled ? null : "배정 제외",
                a.AuthStatus == "required" ? "로그인 필요" : null,
                a.Error is { } e ? $"조회 실패: {e}" : null,
                a.CheckedAt is { } c ? chip.TextAt(at) + (chip.IsStaleAt(at) ? "" : " 조회") : "조회 기록 없음",
            }.Where(x => x != null);
            return new AccountUsageCard
            {
                Id = a.Id, ProviderName = $"{a.Provider} · {a.Id}", DisplayName = a.Label, Status = string.Join(" · ", status),
                Usage = AccountUsageRows(usage?.Windows ?? [], false, at), Dimmed = !a.Enabled || chip.IsDimmedAt(at),
            };
        }).ToList();
        if (snapshot.DeepSeek is { } d && d.State != "unconfigured")
            cards.Add(new AccountUsageCard
            {
                Id = "deepseek", ProviderName = "DeepSeek · API 잔고", DisplayName = d.Error != null ? "조회 실패" : d.Balance.Length > 0 ? d.Balance : "정보 없음",
                Status = d.Error ?? (d.State == "loading" ? "조회 중" : ""),
            });
        return cards;
    }
}
