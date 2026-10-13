using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AIUsageMonitor.Core;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.UI;

public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _topmost, _characters, _startup, _usageAlerts, _activityAlerts, _login, _logout, _desktop;
    bool _webLoggedIn;
    IntPtr _hIcon;
    string _iconSig = "";

    public event Action? OpenRequested, RefreshRequested, ExitRequested, LoginRequested, LogoutRequested;
    public event Action? AccountsRequested;
    public event Action<bool>? TopmostChanged, CharactersChanged, StartupChanged, UsageAlertsChanged, ActivityAlertsChanged;
    public event Action<bool>? ClaudeDesktopChanged;

    public Tray(AppSettings s)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("지금 새로고침", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("계정 추가 및 사용량…", null, (_, _) => AccountsRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _topmost = Check("항상 위에 표시", s.Topmost, v => TopmostChanged?.Invoke(v));
        _characters = Check("캐릭터 표시", s.ShowCharacters, v => CharactersChanged?.Invoke(v));
        _startup = Check("Windows 시작 시 실행", s.StartWithWindows, v => StartupChanged?.Invoke(v));
        _usageAlerts = Check("사용량 경고 알림 (" + string.Join("/", s.AlertThresholds) + "%)", s.UsageAlerts, v => UsageAlertsChanged?.Invoke(v));
        _activityAlerts = Check("작업 완료·승인 대기 알림", s.ActivityAlerts, v => ActivityAlertsChanged?.Invoke(v));
        menu.Items.AddRange([_topmost, _characters, _startup, _usageAlerts, _activityAlerts, new WinForms.ToolStripSeparator()]);
        _desktop = Check("Claude 데스크톱 로그인 자동 연결", s.ClaudeDesktopEnabled, value =>
        {
            ClaudeDesktopChanged?.Invoke(value);
            SetLoggedIn(_webLoggedIn);
        });
        menu.Items.Add(_desktop);
        _login = new WinForms.ToolStripMenuItem("Claude 웹 로그인 (보조 연결)…", null, (_, _) => LoginRequested?.Invoke());
        _logout = new WinForms.ToolStripMenuItem("Claude 로그아웃", null, (_, _) => LogoutRequested?.Invoke());
        menu.Items.AddRange([_login, _logout]);
        menu.Items.Add("데이터 폴더 열기", null, (_, _) => OpenFolder(Paths.AppData));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon { ContextMenuStrip = menu, Text = "AI 사용량 모니터", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
        SetLoggedIn(s.ClaudeWebEnabled);
        UpdateIcon(null, null);
    }

    WinForms.ToolStripMenuItem Check(string text, bool value, Action<bool> changed)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = value, CheckOnClick = true };
        item.CheckedChanged += (_, _) => changed(item.Checked);
        return item;
    }

    public void SetTopmost(bool v) => _topmost.Checked = v;

    public void SetLoggedIn(bool v)
    {
        _webLoggedIn = v;
        _login.Text = v ? "Claude 웹 다시 로그인 (보조 연결)…" : "Claude 웹 로그인 (보조 연결)…";
        _login.Enabled = !_desktop.Checked;
        _logout.Text = "Claude 웹 연결 로그아웃";
        _logout.Enabled = !_desktop.Checked && v;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void Balloon(string title, string text, bool warning) =>
        _icon.ShowBalloonTip(6000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    /// <summary>왼쪽 막대 = Claude, 오른쪽 막대 = Codex. 채워진 높이 = 가장 많이 쓴 한도의 사용률.</summary>
    public void UpdateIcon(double? claude, double? codex)
    {
        var size = WinForms.SystemInformation.SmallIconSize.Width;
        var sig = $"{size}:{claude:0}:{codex:0}";
        if (sig == _iconSig) return;
        _iconSig = sig;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = size * 0.06f, gap = size * 0.12f;
            float barW = (size - pad * 2 - gap) / 2f, barH = size - pad * 2;
            DrawBar(g, pad, pad, barW, barH, claude, Color.FromArgb(0xD9, 0x77, 0x57));
            DrawBar(g, pad + barW + gap, pad, barW, barH, codex, Color.FromArgb(0x7A, 0xA2, 0xF7));
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
