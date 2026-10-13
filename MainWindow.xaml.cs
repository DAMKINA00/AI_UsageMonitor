using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;

    public event Action? RefreshRequested;
    public event Action? AccountsRequested;
    public event Action<string, string>? SecondaryAccountSwapRequested;
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
        IsVisibleChanged += (_, _) => { UpdateStages(); if (!IsVisible) SetAccountsOpen(false); };
        StateChanged += (_, _) => { UpdateStages(); if (WindowState == WindowState.Minimized) SetAccountsOpen(false); };
        LocationChanged += (_, _) => { if (IsAccountsOpen) _accountsFlyout!.PositionBeside(this, CurrentWorkArea()); };
        Accounts.CloseRequested += () => SetAccountsOpen(false);
    }

    public CharacterStage Claude => ClaudeStage;
    public CharacterStage Codex => CodexStage;
    public AccountsPanel Accounts { get; } = new();
    public bool IsAccountsOpen { get; private set; }
    AccountsFlyout? _accountsFlyout;

    public void SetAccountsOpen(bool open)
    {
        if (IsAccountsOpen == open) return;
        if (!_positioned) RestorePosition();
        IsAccountsOpen = open;
        if (open)
        {
            if (_accountsFlyout == null)
            {
                _accountsFlyout = new AccountsFlyout(Accounts) { Owner = this };
                _accountsFlyout.CloseRequested += () => SetAccountsOpen(false);
            }
            _accountsFlyout.Topmost = Topmost;
            _accountsFlyout.Opacity = Opacity;
            _accountsFlyout.PositionBeside(this, CurrentWorkArea());
            _accountsFlyout.Show();
        }
        else _accountsFlyout?.Hide();
        AccountsButton.Foreground = open ? Palette.Text : Palette.Muted;
        if (!open) AccountsButton.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsAccountsOpen)
        {
            SetAccountsOpen(false);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    public void ApplyCharacters(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        ClaudeStage.Visibility = v;
        CodexStage.Visibility = v;
    }

    /// <summary>
    /// 창 크기를 내용에 딱 맞춘다. SizeToContent 를 WindowChrome 과 같이 쓰면 원래 창 테두리 두께만큼
    /// 오른쪽·아래에 빈 공간이 남아서 직접 잰다. (카드·캐릭터 크기가 고정이라 데이터와 무관하게 정해진다)
    /// </summary>
    void FitToContent()
    {
        if (Content is not FrameworkElement root) return;
        const double baseWidth = 410, preferredScale = 1.5;
        var area = CurrentWorkArea();
        root.LayoutTransform = Transform.Identity;
        root.Width = baseWidth;
        Dashboard.Width = baseWidth;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentHeight = root.DesiredSize.Height;
        double scale = Math.Min(preferredScale, Math.Max(0.1, (area.Height - 24) / contentHeight));
        // 세로가 짧은 화면에서는 비율을 유지해 맞추고, 가로 여유는 세션 목록에 배분한다.
        root.Width = Math.Max(1, Math.Min(baseWidth * preferredScale, area.Width - 24)) / scale;
        Dashboard.Width = root.Width;
        root.LayoutTransform = new ScaleTransform(scale, scale);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Ceiling(root.DesiredSize.Width);
        Height = Math.Ceiling(contentHeight * scale);
    }

    Rect CurrentWorkArea()
    {
        var bounds = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var fromPixels = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return Rect.Transform(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), fromPixels);
    }

    void KeepOnScreen()
    {
        var area = CurrentWorkArea();
        Left = Math.Clamp(Left, area.Left + 12, Math.Max(area.Left + 12, area.Right - Width - 12));
        Top = Math.Clamp(Top, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - Height - 12));
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
        if (_accountsFlyout != null) _accountsFlyout.Topmost = on;
        PinButton.Content = on ? "" : "";
        PinButton.ToolTip = on ? "항상 위에 표시 해제" : "항상 위에 표시";
    }

    void RestorePosition()
    {
        if (_positioned) return;
        _positioned = true;
        var area = CurrentWorkArea();
        if (_settings.WindowLeft is double l && _settings.WindowTop is double t &&
            l >= SystemParameters.VirtualScreenLeft && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
            t >= SystemParameters.VirtualScreenTop && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = area.Right - Width - 12;
            Top = Math.Max(area.Top + 12, area.Bottom - Height - 12);
        }
        FitToContent();
        KeepOnScreen();
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

    void Accounts_Click(object sender, RoutedEventArgs e)
    {
        if (IsAccountsOpen) SetAccountsOpen(false);
        else AccountsRequested?.Invoke();
    }

    const string AccountDragFormat = "AIUsageMonitor.SecondaryAccount";
    Point _accountDragStart;
    AccountUsageSlot? _dragAccount;
    Border? _dropTarget;

    void Account_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragAccount = (sender as FrameworkElement)?.DataContext as AccountUsageSlot;
        _accountDragStart = e.GetPosition(this);
    }

    void Account_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragAccount = null; return; }
        if (_dragAccount is not { CanDrag: true } account || sender is not FrameworkElement element ||
            element.DataContext is not AccountUsageSlot current || current.Id != account.Id) return;
        var delta = e.GetPosition(this) - _accountDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragAccount = null;
        var data = new DataObject();
        data.SetData(AccountDragFormat, account, false);
        try { DragDrop.DoDragDrop(element, data, DragDropEffects.Move); }
        finally { ClearDropTarget(); }
    }

    static AccountUsageSlot? DragSource(DragEventArgs e) =>
        e.Data.GetDataPresent(AccountDragFormat, false) ? e.Data.GetData(AccountDragFormat, false) as AccountUsageSlot : null;

    void ClearDropTarget()
    {
        if (_dropTarget != null) _dropTarget.Background = Brushes.Transparent;
        _dropTarget = null;
    }

    void Account_DragOver(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        bool valid = sender is Border { DataContext: AccountUsageSlot target } && target.CanSwapWith(DragSource(e));
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid && sender is Border border)
        {
            _dropTarget = border;
            border.Background = new SolidColorBrush(Color.FromArgb(28, 91, 185, 140));
        }
        e.Handled = true;
    }

    void Account_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    void Account_Drop(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        var source = DragSource(e);
        if (sender is FrameworkElement { DataContext: AccountUsageSlot target } && target.CanSwapWith(source))
        {
            SecondaryAccountSwapRequested?.Invoke(source!.Id, target.Id);
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

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
