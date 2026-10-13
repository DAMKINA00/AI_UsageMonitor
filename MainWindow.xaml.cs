using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;

    public event Action? RefreshRequested;
    public event Action? AccountsRequested;
    public event Action<bool>? TopmostToggled;

    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = vm;
        ApplyTopmost(settings.Topmost);
        ApplyCharacters(settings.ShowCharacters);
        SourceInitialized += (_, _) =>
        {
            RoundCorners();
            FitToContent();
        };
        ContentRendered += (_, _) => RestorePosition();
        IsVisibleChanged += (_, _) => UpdateStages();
        StateChanged += (_, _) => UpdateStages();
    }

    public CharacterStage Claude => ClaudeStage;
    public CharacterStage Codex => CodexStage;

    public void ApplyCharacters(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        ClaudeStage.Visibility = v;
        CodexStage.Visibility = v;
        if (IsInitialized && PresentationSource.FromVisual(this) != null) FitToContent();
    }

    /// <summary>
    /// 창 크기를 내용에 딱 맞춘다. SizeToContent 를 WindowChrome 과 같이 쓰면 원래 창 테두리 두께만큼
    /// 오른쪽·아래에 빈 공간이 남아서 직접 잰다. (카드·캐릭터 크기가 고정이라 데이터와 무관하게 정해진다)
    /// </summary>
    void FitToContent()
    {
        if (Content is not FrameworkElement root) return;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Ceiling(root.DesiredSize.Width);
        Height = Math.Ceiling(root.DesiredSize.Height);
    }

    void UpdateStages()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        ClaudeStage.SetActive(active);
        CodexStage.SetActive(active);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void RoundCorners()
    {
        // Windows 11: 둥근 모서리 (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)
        int pref = 2;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref pref, sizeof(int)); } catch { }
    }

    bool _positioned;

    public void ApplyTopmost(bool on)
    {
        Topmost = on;
        PinButton.Content = on ? "" : "";
        PinButton.ToolTip = on ? "항상 위에 표시 해제" : "항상 위에 표시";
    }

    void RestorePosition()
    {
        if (_positioned) return;
        _positioned = true;
        var area = SystemParameters.WorkArea;
        if (_settings.WindowLeft is double l && _settings.WindowTop is double t &&
            l >= SystemParameters.VirtualScreenLeft && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
            t >= SystemParameters.VirtualScreenTop && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = area.Right - ActualWidth - 12;
            Top = Math.Max(area.Top + 12, area.Bottom - ActualHeight - 12);
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void SavePosition()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        SavePosition();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    void Accounts_Click(object sender, RoutedEventArgs e) => AccountsRequested?.Invoke();

    void Pin_Click(object sender, RoutedEventArgs e) => TopmostToggled?.Invoke(!Topmost);

    void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
