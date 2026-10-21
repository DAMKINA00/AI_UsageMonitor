using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AIUsageMonitor.Core;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.UI;

public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _topmost, _characters, _taskbarUsage, _startup, _usageAlerts, _activityAlerts, _login, _logout, _desktop;
    bool _webLoggedIn;
    IntPtr _hIcon;
    string _iconSig = "";

    public event Action? OpenRequested, HideRequested, RefreshRequested, ExitRequested, LoginRequested, LogoutRequested;
    public event Action? AccountsRequested;
    public event Action<bool>? TopmostChanged, CharactersChanged, StartupChanged, UsageAlertsChanged, ActivityAlertsChanged;
    public event Action<bool>? ClaudeDesktopChanged;
    public event Action<bool>? TaskbarUsageChanged;
    public event Action<string?>? GeminiPlanChanged;
    readonly List<WinForms.ToolStripMenuItem> _geminiPlans = [];
    public event Action? GeminiUnblockRequested;
    readonly WinForms.ToolStripMenuItem _geminiUnblock;

    /// <summary>'Gemini 조회 차단 해제' 메뉴를 차단됐을 때만 보인다.</summary>
    public void SetGeminiBlocked(bool blocked)
    {
        if (_geminiUnblock.Visible != blocked) _geminiUnblock.Visible = blocked;
    }

    public Tray(AppSettings s)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("모니터 열기", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("모니터 숨기기", null, (_, _) => HideRequested?.Invoke());
        menu.Items.Add("지금 새로고침", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("계정 추가 및 사용량…", null, (_, _) => AccountsRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _topmost = Check("항상 위에 표시", s.Topmost, v => TopmostChanged?.Invoke(v));
        _characters = Check("캐릭터 표시", s.ShowCharacters, v => CharactersChanged?.Invoke(v));
        _taskbarUsage = Check("작업표시줄 사용량 표시", s.ShowTaskbarUsage, v => TaskbarUsageChanged?.Invoke(v));
        _startup = Check("Windows 시작 시 실행", s.StartWithWindows, v => StartupChanged?.Invoke(v));
        _usageAlerts = Check("사용량 경고 알림 (" + string.Join("/", s.AlertThresholds) + "%)", s.UsageAlerts, v => UsageAlertsChanged?.Invoke(v));
        _activityAlerts = Check("작업 완료·승인 대기 알림", s.ActivityAlerts, v => ActivityAlertsChanged?.Invoke(v));
        menu.Items.AddRange([_topmost, _characters, _taskbarUsage, _startup, _usageAlerts, _activityAlerts, new WinForms.ToolStripSeparator()]);
        _desktop = Check("Claude 데스크톱 로그인 자동 연결", s.ClaudeDesktopEnabled, value =>
        {
            ClaudeDesktopChanged?.Invoke(value);
            SetLoggedIn(_webLoggedIn);
        });
        menu.Items.Add(_desktop);
        _login = new WinForms.ToolStripMenuItem("Claude 웹 로그인 (보조 연결)…", null, (_, _) => LoginRequested?.Invoke());
        _logout = new WinForms.ToolStripMenuItem("Claude 로그아웃", null, (_, _) => LogoutRequested?.Invoke());
        menu.Items.AddRange([_login, _logout]);
        // agy 가 /usage 미지원으로 판정돼 Gemini 조회가 막혔을 때만 보인다
        _geminiUnblock = new WinForms.ToolStripMenuItem(GeminiBlockAction.MenuText + "…", null, (_, _) => GeminiUnblockRequested?.Invoke())
        {
            Visible = false,
        };
        menu.Items.Add(_geminiUnblock);
        var geminiPlan = new WinForms.ToolStripMenuItem("Gemini 계정 등급");
        foreach (var plan in new[] { "자동 확인", "Free", "Google AI Pro", "Google AI Ultra" })
        {
            var value = plan == "자동 확인" ? null : plan;
            var item = new WinForms.ToolStripMenuItem(plan) { Tag = value };
            item.Click += (_, _) => GeminiPlanChanged?.Invoke(value);
            _geminiPlans.Add(item);
            geminiPlan.DropDownItems.Add(item);
        }
        SetGeminiPlan(s.GeminiPlanOverride);
        menu.Items.Add(geminiPlan);
        menu.Items.Add("데이터 폴더 열기", null, (_, _) => OpenFolder(Paths.AppData));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon { ContextMenuStrip = menu, Text = "AI 사용량 모니터", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
        SetLoggedIn(s.ClaudeWebEnabled);
        UpdateIcon(null, null, null);
    }

    WinForms.ToolStripMenuItem Check(string text, bool value, Action<bool> changed)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = value, CheckOnClick = true };
        item.CheckedChanged += (_, _) => changed(item.Checked);
        return item;
    }

    public void SetTopmost(bool v) => _topmost.Checked = v;
    public void SetTaskbarUsage(bool v) => _taskbarUsage.Checked = v;
    public void SetGeminiPlan(string? plan)
    {
        foreach (var item in _geminiPlans) item.Checked = Equals(item.Tag, plan);
    }

    public void SetLoggedIn(bool v)
    {
        _webLoggedIn = v;
        _login.Text = v ? "Claude 웹 다시 로그인 (보조 연결)…" : "Claude 웹 로그인 (보조 연결)…";
        _login.Enabled = !_desktop.Checked;
        _logout.Text = "Claude 웹 연결 로그아웃";
        _logout.Enabled = !_desktop.Checked && v;
    }

    /// <summary>NotifyIcon 툴팁은 127자까지만 받는다.</summary>
    public const int MaxTooltipLength = 127;

    public void SetTooltip(string text) => _icon.Text = text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;

    /// <summary>
    /// 서비스마다 한 줄: "Claude 주간 62% · 작업 2 · 대기 1". 화면 원과 같은 한도를 쓴다.
    /// 세 줄을 합쳐 127자를 넘지 않도록 줄마다 길이를 나눠 자른다.
    /// </summary>
    public static string Tooltip(IReadOnlyList<ProviderCard> cards)
    {
        int perLine = (MaxTooltipLength - Math.Max(0, cards.Count - 1)) / Math.Max(1, cards.Count);
        static string Line(ProviderCard c)
        {
            string usage = c.Usage.FirstOrDefault() is { } row
                ? $"{row.Label.Replace(" 한도", "")} {(row.IsResetPending ? "확인 중" : Fmt.Pct(row.Value))}"
                : c.Status.State switch
                {
                    StatusKind.NotInstalled => c.Name == "Grok" ? "CLI 미설치" : "agy 미설치",
                    StatusKind.Unsupported => AgyCli.UnsupportedMessage,
                    StatusKind.LoginRequired => "로그인 필요",
                    StatusKind.Disabled => "꺼짐",
                    _ => "사용량 미확인",
                };
            var work = c.WorkingCount > 0 ? $" · 작업 {c.WorkingCount}" : "";
            var wait = c.NeedsInputCount > 0 ? $" · 대기 {c.NeedsInputCount}" : "";
            var stale = c.Status.IsDimmed || c.UsageWarning != null ? " ⚠" : "";
            return $"{c.Name} {usage}{stale}{work}{wait}";
        }
        return string.Join('\n', cards.Select(c =>
        {
            var line = Line(c);
            return line.Length > perLine ? line[..perLine] : line;
        }));
    }

    /// <summary>
    /// 설정을 읽지 못해 기본값으로 시작했을 때 시작 풍선에 띄울 문구 (정상으로 읽었거나 파일이 없었으면 null).
    /// 풍선은 255자까지만 보이므로 짧게 쓴다.
    /// </summary>
    public static string? SettingsLoadNotice(AppSettings s) => s.LoadedFrom != AppSettings.LoadResult.Failed ? null
        : s.SaveBlocked || s.BackupFile == null ? "설정 파일을 읽을 수 없어 기본값으로 시작했습니다. 이번 실행의 변경은 저장되지 않습니다."
        : $"설정을 읽지 못해 기본값으로 시작했습니다. 원본 백업: 데이터 폴더의 {System.IO.Path.GetFileName(s.BackupFile)}. " +
          "다음 저장 때 settings.json 이 기본값으로 바뀌고 추가 계정 연결 목록도 비게 됩니다.";

    public void Balloon(string title, string text, bool warning) =>
        _icon.ShowBalloonTip(6000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    /// <summary>
    /// 왼쪽부터 Claude, Codex, Gemini, Grok 계정 사용률.
    /// </summary>
    public void UpdateIcon(double? claude, double? codex, double? gemini, double? grok = null)
    {
        var size = WinForms.SystemInformation.SmallIconSize.Width;
        var sig = $"{size}:{claude:0}:{codex:0}:{gemini:0}:{grok:0}";
        if (sig == _iconSig) return;
        _iconSig = sig;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = size * 0.04f, gap = size * 0.07f;
            float barW = (size - pad * 2 - gap * 3) / 4f, barH = size - pad * 2;
            DrawBar(g, pad, pad, barW, barH, claude, Color.FromArgb(0xD9, 0x77, 0x57));
            DrawBar(g, pad + barW + gap, pad, barW, barH, codex, Color.FromArgb(0x7A, 0xA2, 0xF7));
            DrawBar(g, pad + (barW + gap) * 2, pad, barW, barH, gemini, Color.FromArgb(0xB4, 0x8C, 0xF2));
            DrawBar(g, pad + (barW + gap) * 3, pad, barW, barH, grok, Color.FromArgb(0xD8, 0xBD, 0x72));
        }

        var h = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = h;
    }

    static void DrawBar(Graphics g, float x, float y, float w, float h, double? used, Color accent)
    {
        float r = Math.Max(1.5f, w * 0.28f);
        using var track = Rounded(x, y, w, h, r);
        using (var bg = new SolidBrush(Color.FromArgb(150, 40, 42, 48))) g.FillPath(bg, track);
        if (used is double u)
        {
            float fill = (float)(Math.Clamp(u, 0, 100) / 100.0 * h);
            if (fill > 0.5f)
            {
                var c = u >= 90 ? Color.FromArgb(0xE5, 0x53, 0x4B) : u >= 70 ? Color.FromArgb(0xE5, 0xA5, 0x4B) : Color.FromArgb(0x5B, 0xB9, 0x8C);
                var state = g.Save();
                g.SetClip(track);
                using var b = new SolidBrush(c);
                g.FillRectangle(b, x, y + h - fill, w, fill);
                g.Restore(state);
            }
        }
        using var pen = new Pen(accent, Math.Max(1f, w * 0.16f));
        g.DrawPath(pen, track);
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }
}

/// <summary>사용량 임계치 알림과 작업 상태 변경 알림.</summary>
public sealed class Alerts(AppSettings settings, Tray tray)
{
    readonly Dictionary<string, SessionState> _prev = new();
    bool _baseline;

    public void CheckUsage(string provider, List<UsageWindow> windows)
    {
        if (!settings.UsageAlerts) return;
        var thresholds = settings.AlertThresholds.Where(t => t is > 0 and <= 100).OrderByDescending(t => t).ToArray();
        bool changed = false;
        foreach (var w in windows)
        {
            if (w.ResetsAt is not { } reset) continue;
            var hit = thresholds.FirstOrDefault(t => w.UsedPercent >= t);
            if (hit == 0) continue;
            // 같은 리셋 주기 안에서는 단계별로 한 번만 알린다 (리셋 시각의 초 단위 오차는 시간 단위로 흡수)
            var period = reset.ToUniversalTime().AddMinutes(30);
            var key = $"{provider}|{w.Key}|{period:yyyyMMddHH}";
            if (settings.AlertState.TryGetValue(key, out var done) && done >= hit) continue;
            settings.AlertState[key] = hit;
            changed = true;
            tray.Balloon($"{provider} {w.Label} {Fmt.Pct(w.UsedPercent)} 사용",
                $"{Fmt.Pct(100 - w.UsedPercent)} 남음 · 리셋까지 {Fmt.Span(reset - DateTimeOffset.Now)} ({Fmt.When(reset)})",
                hit >= 90);
        }
        if (changed)
        {
            Prune();
            settings.Save();
        }
    }

    public void CheckActivity(string provider, List<SessionInfo> sessions)
    {
        foreach (var s in sessions)
        {
            var key = provider + "|" + s.Id;
            _prev.TryGetValue(key, out var before);
            bool known = _prev.ContainsKey(key);
            _prev[key] = s.State;
            if (!_baseline || !known || !settings.ActivityAlerts || before == s.State) continue;

            if (s.State == SessionState.NeedsInput)
                tray.Balloon($"{provider}: {s.StateText}", s.Title, true);
            else if (before == SessionState.Working && s.State == SessionState.Idle)
                tray.Balloon($"{provider}: 작업 끝남 ({s.StateText})", s.Title, false);
        }
    }

    /// <summary>첫 갱신 이후부터 상태 변경 알림을 낸다.</summary>
    public void MarkBaseline() => _baseline = true;

    void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-8).ToString("yyyyMMddHH");
        foreach (var k in settings.AlertState.Keys.Where(k => string.CompareOrdinal(k[(k.LastIndexOf('|') + 1)..], cutoff) < 0).ToList())
            settings.AlertState.Remove(k);
    }
}
