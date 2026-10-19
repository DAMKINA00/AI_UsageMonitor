using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor;

/// <summary>창 폭에 따른 레이아웃 구간.</summary>
public enum LayoutMode
{
    /// <summary>계정 원과 캐릭터는 유지하고 앱 상태 글자만 숨긴다.</summary>
    Narrow,
    /// <summary>계정 원 | 캐릭터(켰을 때).</summary>
    Medium,
    /// <summary>계정 원 | 캐릭터(켰을 때).</summary>
    Wide,
}

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly Action<AppSettings> _saveSettings;
    bool _showCharacters;

    public event Action? RefreshRequested;
    public event Action? AccountsRequested;
    public event Action<string, string>? SecondaryAccountSwapRequested;
    public event Action<bool>? TopmostToggled;

    public bool AllowClose { get; set; }

    /// <summary>처음 실행할 때의 창 크기 (DIP). 사용자가 크기를 바꾸면 그 값을 저장해 다음에 쓴다.</summary>
    public const double DefaultWidth = 640, DefaultHeight = 640;
    /// <summary>네 행이 헤더·계정 원·안내 한 줄을 모두 보일 수 있는 최소 크기.</summary>
    public const double MinWindowWidth = 340, MinWindowHeight = 590;
    /// <summary>넓은 창 레이아웃 구간.</summary>
    public const double WideFrom = 620;
    /// <summary>이 폭 미만이면 헤더의 앱 상태 글자를 숨긴다.</summary>
    public const double MediumFrom = 440;
    /// <summary>캐릭터 그림 높이 상한 (행이 높아져도 이보다 크게 그리지 않는다).</summary>
    const double MaxFigureHeight = 150;

    /// <param name="saveSettings">설정 저장 방법 (기본: settings.json). 테스트는 사용자 설정 파일을 건드리지 않도록 바꾼다.</param>
    public MainWindow(MainViewModel vm, AppSettings settings, Action<AppSettings>? saveSettings = null)
    {
        InitializeComponent();
        _settings = settings;
        _saveSettings = saveSettings ?? (s => s.Save());
        DataContext = vm;
        MinWidth = MinWindowWidth;
        MinHeight = MinWindowHeight;
        Width = DefaultWidth;
        Height = DefaultHeight;
        ApplyTopmost(settings.Topmost);
        _showCharacters = settings.ShowCharacters;
        ApplyLayout(Width);
        SourceInitialized += (_, _) =>
        {
            RoundCorners();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            RestoreSavedBounds();
        };
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        IsVisibleChanged += (_, _) => { UpdateStages(); if (!IsVisible) SetAccountsOpen(false); };
        StateChanged += (_, _) => { UpdateStages(); if (WindowState == WindowState.Minimized) SetAccountsOpen(false); };
        LocationChanged += (_, _) =>
        {
            if (_positioned) UpdateMaxSize();
            if (IsAccountsOpen) _accountsFlyout!.PositionBeside(this, CurrentWorkArea());
        };
        foreach (var stage in Stages) stage.SizeChanged += (_, _) => FitFigure(stage);
        Accounts.CloseRequested += () => SetAccountsOpen(false);
    }

    public CharacterStage Claude => ClaudeStage;
    public CharacterStage Codex => CodexStage;
    public CharacterStage Gemini => GeminiStage;
    public CharacterStage Grok => GrokStage;
    IEnumerable<CharacterStage> Stages => [ClaudeStage, CodexStage, GeminiStage, GrokStage];
    public AccountsPanel Accounts { get; } = new();
    public bool IsAccountsOpen { get; private set; }
    AccountsFlyout? _accountsFlyout;

    /// <summary>지금 폭의 레이아웃 구간.</summary>
    public LayoutMode Mode { get; private set; } = LayoutMode.Wide;
    /// <summary>창 폭과 관계없이 캐릭터 표시 설정을 따른다.</summary>
    public bool CharactersShown => _showCharacters;

    public void SetAccountsOpen(bool open)
    {
        if (IsAccountsOpen == open) return;
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

    /// <summary>
    /// 캐릭터 켜기/끄기. 창 크기와 사용량 위치는 바꾸지 않는다.
    /// </summary>
    public void ApplyCharacters(bool on)
    {
        _showCharacters = on;
        ApplyLayout(ActualWidth > 0 ? ActualWidth : Width);
    }

    public static LayoutMode ModeFor(double width) =>
        width >= WideFrom ? LayoutMode.Wide : width >= MediumFrom ? LayoutMode.Medium : LayoutMode.Narrow;

    /// <summary>
    /// 사용량과 캐릭터 배치는 유지하고 헤더와 캐릭터의 표시 여부만 바꾼다.
    /// </summary>
    void ApplyLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0) return;
        Mode = ModeFor(width);
        bool characters = CharactersShown;
        // 크기를 조정하는 동안 SizeChanged 가 계속 오므로 구간이 바뀔 때만 리소스를 바꾼다.
        if (_appliedLayout == (Mode, characters)) return;
        _appliedLayout = (Mode, characters);
        Resources["AppStatusVisibility"] = Mode == LayoutMode.Narrow ? Visibility.Collapsed : Visibility.Visible;
        foreach (var stage in Stages) stage.Visibility = characters ? Visibility.Visible : Visibility.Collapsed;
        UpdateStages();
    }

    (LayoutMode Mode, bool Characters)? _appliedLayout;

    /// <summary>행 높이에 맞춰 캐릭터 그림 크기를 정한다 (말풍선 글자 폭은 그림이 가장 커도 120 DIP 이상 남는다).</summary>
    static void FitFigure(CharacterStage stage)
    {
        if (stage.ActualHeight <= 0) return;
        stage.FigureHeight = Math.Min(MaxFigureHeight, Math.Max(40, stage.ActualHeight - 2));
    }

    void AccountBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0 || sender is not FrameworkElement { DataContext: ProviderCard card } accountBody) return;
        var stage = card.Name switch
        {
            "Claude" => Claude,
            "Codex" => Codex,
            "Gemini" => Gemini,
            _ => Grok,
        };
        stage.Height = Math.Min(MaxFigureHeight, e.NewSize.Height - 2);
        // 오류 안내 줄 높이까지 반영해 사용량과 캐릭터의 세로 중심을 맞춘다.
        stage.Margin = new Thickness(0, accountBody.TranslatePoint(new Point(), (UIElement)stage.Parent).Y, 6, 6);
    }

    Rect CurrentWorkArea()
    {
        var bounds = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var fromPixels = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return Rect.Transform(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), fromPixels);
    }

    /// <summary>창이 작업 영역 밖에 있으면 안으로 끌어온다 (크기는 작업 영역보다 클 때만 줄인다).</summary>
    void KeepOnScreen()
    {
        var area = CurrentWorkArea();
        if (Width > area.Width) Width = Math.Max(MinWidth, area.Width);
        if (Height > area.Height) Height = Math.Max(MinHeight, area.Height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    /// <summary>
    /// 실행 중 작업 영역·해상도·DPI 가 바뀌었을 때: 창이 작업 영역보다 크면 줄이고, 거의 보이지 않을 만큼 화면 밖이면 끌어온다.
    /// 그 밖에는 사용자가 둔 위치·크기를 그대로 둔다 (앱이 스스로 크기를 바꾸지 않는다).
    /// </summary>
    void PullInsideIfLost()
    {
        var area = CurrentWorkArea();
        if (Width > area.Width) Width = Math.Max(MinWidth, area.Width);
        if (Height > area.Height) Height = Math.Max(MinHeight, area.Height);
        var visible = Rect.Intersect(new Rect(Left, Top, Width, Height), area);
        if (visible.IsEmpty || visible.Width < MinVisibleWidth || visible.Height < MinVisibleHeight)
        {
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        }
    }

    // 이만큼 보이면 사용자가 일부러 화면 가장자리에 걸쳐 둔 것으로 본다
    const double MinVisibleWidth = 120, MinVisibleHeight = 40;
    bool _displayCheckQueued;

    /// <summary>디스플레이 변경 메시지가 몰려 와도 Windows 가 적용을 마친 뒤 한 번만 확인한다.</summary>
    void QueueDisplayCheck()
    {
        if (!_positioned || _displayCheckQueued) return;
        _displayCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _displayCheckQueued = false;
            if (!IsLoaded && !IsVisible) return;
            _maxArea = default;
            UpdateMaxSize();
            PullInsideIfLost();
        }));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueDisplayCheck();
    }

    Rect _maxArea;

    /// <summary>최대 크기는 지금 모니터의 작업 영역.</summary>
    void UpdateMaxSize()
    {
        var area = CurrentWorkArea();
        if (area == _maxArea) return;
        _maxArea = area;
        MaxWidth = Math.Max(MinWidth, area.Width);
        MaxHeight = Math.Max(MinHeight, area.Height);
    }

    void UpdateStages()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        foreach (var stage in Stages) stage.SetActive(active && stage.Visibility == Visibility.Visible);
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

    /// <summary>
    /// 저장된 크기·위치를 되살린다. 저장값이 없으면 기본 크기로 작업 영역 오른쪽 아래에 둔다.
    /// 모니터나 작업 영역이 바뀌어 화면 밖이면 안으로 끌어온다. 이후에는 앱이 스스로 크기를 바꾸지 않는다.
    /// </summary>
    void RestoreSavedBounds()
    {
        if (_positioned) return;
        _positioned = true;
        Width = Math.Max(MinWidth, _settings.WindowWidth is double w and > 0 ? w : DefaultWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight is double h and > 0 ? h : DefaultHeight);
        if (_settings.WindowLeft is double l && _settings.WindowTop is double t)
        {
            Left = l;
            Top = t;
        }
        else
        {
            var area = CurrentWorkArea();
            Left = area.Right - Width - 12;
            Top = Math.Max(area.Top + 12, area.Bottom - Height - 12);
        }
        KeepOnScreen();
        UpdateMaxSize();
        ApplyLayout(Width);
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>사용자가 옮기거나 크기를 바꾼 뒤의 위치·크기를 저장한다.</summary>
    void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _saveSettings(_settings);
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_EXITSIZEMOVE: 테두리를 끌어 크기를 바꾸거나 옮기기(제목 줄 끌기 포함)를 마쳤을 때 한 번
        const int wmExitSizeMove = 0x0232, wmDisplayChange = 0x007E, wmSettingChange = 0x001A, spiSetWorkArea = 0x002F;
        if (message == wmExitSizeMove) SaveBounds();
        else if (message == wmDisplayChange || (message == wmSettingChange && wParam.ToInt64() == spiSetWorkArea)) QueueDisplayCheck();
        return IntPtr.Zero;
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // 저장은 끌기가 끝날 때 오는 WM_EXITSIZEMOVE 에서 한 번만 한다
        DragMove();
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
