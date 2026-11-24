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

public partial class MainWindow : Window
{
    public const double DefaultWidth = 584, DefaultHeight = 280;
    public const double MinWindowWidth = 292, MinWindowHeight = 280;
    public const double PlatformWidth = 146;
    readonly MainViewModel _vm;
    readonly DispatcherTimer _walkTimer;
    readonly DispatcherTimer _dockTimer;
    // 예전 네 캐릭터(claude..grok)는 예전과 같은 산책 씨앗(31~34)을 쓴다.
    readonly PlatformWalker[] _walkers = Enumerable.Range(0, 6).Select(i => new PlatformWalker(i + 30)).ToArray();
    readonly CharacterLayers _layers = new(6);
    readonly CharacterEventDeduper _seenEvents = new();
    IReadOnlyList<string> _characters = [];
    bool _positioned, _dockQueued;
    int _platformCount = 4;
    double _taskbarInset;
    string _slotSignature = "";
    AccountsFlyout? _accountsFlyout;

    public event Action<string, string>? SecondaryAccountSwapRequested;
    public bool AllowClose { get; set; }
    public bool IsAccountsOpen { get; private set; }
    /// <summary>하나라도 켜져 있는지 (예전 단일 스위치 값).</summary>
    public bool CharactersShown => _characters.Count > 0;
    /// <summary>켜져 있는 캐릭터 (CharacterSelection.All 순서).</summary>
    public IReadOnlyList<string> ShownCharacters => _characters;
    public AccountsPanel Accounts { get; } = new();
    public CharacterStage Orch => OrchStage;
    public CharacterStage Claude => ClaudeStage;
    public CharacterStage Gpt => GptStage;
    public CharacterStage Gemini => GeminiStage;
    public CharacterStage Grok => GrokStage;
    public CharacterStage DeepSeek => DeepSeekStage;
    /// <summary>CharacterSelection.All 순서의 여섯 무대.</summary>
    public CharacterStage[] Stages => [OrchStage, ClaudeStage, GptStage, GeminiStage, GrokStage, DeepSeekStage];

    /// <summary>
    /// 호스트가 보낸 실제 이벤트를 그 캐릭터의 말풍선에 한 번 띄운다. 같은 id 는 다시 말하지 않고, 꺼져 있거나 그림이 없는
    /// 캐릭터에게 온 이벤트는 버린다. 반환: 말풍선에 띄웠거나 줄 세웠는지.
    /// </summary>
    /// <summary>캐릭터를 눌렀을 때 (질문 가능할 때만): 캐릭터 id 와 화면 위치(DIP).</summary>
    public event Action<string, Rect>? CharacterClicked;

    bool _askEnabled;

    /// <summary>호스트가 질문을 받을 수 있으면 캐릭터를 눌러 질문한다 (손가락 커서).</summary>
    public bool AskEnabled
    {
        get => _askEnabled;
        set
        {
            _askEnabled = value;
            foreach (var stage in Stages) stage.Cursor = value ? Cursors.Hand : null;
        }
    }

    /// <summary>캐릭터를 누른 것으로 처리한다 (마우스 이벤트와 테스트가 함께 쓴다).</summary>
    public bool ClickCharacter(string id)
    {
        int index = CharacterSelection.All.ToList().IndexOf(id);
        if (!_askEnabled || index < 0) return false;
        var stage = Stages[index];
        if (stage.Visibility != Visibility.Visible || !stage.HasArt) return false;
        CharacterClicked?.Invoke(id, ScreenRect(stage));
        return true;
    }

    Rect ScreenRect(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is not { CompositionTarget: { } target }) return Rect.Empty;
        var topLeft = target.TransformFromDevice.Transform(element.PointToScreen(new Point(0, 0)));
        var bottomRight = target.TransformFromDevice.Transform(element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>이 창이 있는 화면의 작업 영역 (DIP).</summary>
    public Rect WorkArea => CurrentWorkArea();

    public bool ShowCharacterEvent(CharacterEvent evt)
    {
        if (!_seenEvents.Accept(evt.Id)) return false;
        int index = CharacterSelection.All.ToList().IndexOf(evt.Character);
        if (index < 0) return false;
        var stage = Stages[index];
        if (stage.Visibility != Visibility.Visible || !stage.HasArt) return false;
        stage.Say(evt.Text);
        return true;
    }

    public MainWindow(MainViewModel vm, AppSettings settings, Action<AppSettings>? saveSettings = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = MinWindowWidth;
        MinHeight = MinWindowHeight;
        ApplyTopmost(settings.Topmost);
        ApplyCharacters(settings.EnabledCharacters);
        foreach (var stage in Stages)
        {
            stage.ExternalWalking = true;
            var id = stage.Character;
            stage.MouseLeftButtonUp += (_, e) => { if (ClickCharacter(id)) e.Handled = true; };
        }
        _walkTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
        _walkTimer.Tick += (_, _) => AdvanceCharacters(0.066);
        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _dockTimer.Tick += (_, _) => DockToTaskbar();
        _vm.PropertyChanged += ViewModelChanged;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            // Use the previously selected monitor, but replace portrait bounds with the new docked layout.
            if (settings.WindowLeft is double left && settings.WindowTop is double top && double.IsFinite(left) && double.IsFinite(top))
            { Left = left; Top = top; }
            _positioned = true;
            DockToTaskbar();
        };
        IsVisibleChanged += (_, _) => { UpdateStages(); if (!IsVisible) SetAccountsOpen(false); else DockToTaskbar(); };
        StateChanged += (_, _) => { UpdateStages(); if (WindowState == WindowState.Minimized) SetAccountsOpen(false); };
        SizeChanged += (_, _) => AdvanceCharacters(0);
        LocationChanged += (_, _) => QueueDock();
        Accounts.CloseRequested += () => SetAccountsOpen(false);
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= ViewModelChanged;
            _walkTimer.Stop(); _dockTimer.Stop();
            foreach (var stage in Stages) stage.SetActive(false);
            _accountsFlyout?.Close();
        };
        RefreshPlatforms();
    }

    void ViewModelChanged(object? sender, PropertyChangedEventArgs e) => RefreshPlatforms();

    public void RefreshPlatforms()
    {
        var cards = new[] { _vm.Claude, _vm.Codex, _vm.Gemini, _vm.Grok }.OfType<ProviderCard>();
        var slots = cards.SelectMany(card => card.AccountSlots.Count > 0 ? card.AccountSlots : AccountUsageSlot.Create(card, [], 0))
            .Where(slot => !slot.IsEmpty || slot.Id.Length > 0).ToArray();
        var signature = string.Join('\n', slots.Select(slot => slot.Signature));
        if (signature != _slotSignature || PlatformItems.ItemsSource == null)
        {
            _slotSignature = signature;
            PlatformItems.ItemsSource = slots;
            _platformCount = Math.Max(4, slots.Length);
            DockToTaskbar();
        }
        // 같은 구성이면 보이는 칸을 유지하되, 교체된 카드의 새 조회 상태(기준 시각)는 옮긴다.
        else if (PlatformItems.ItemsSource is AccountUsageSlot[] shown) RefreshShownSlots(shown, slots);
    }

    /// <summary>Same layout: carries each newer slot's freshness into the slot already on screen (matched by position and id).</summary>
    public static void RefreshShownSlots(IReadOnlyList<AccountUsageSlot> shown, IReadOnlyList<AccountUsageSlot> newer)
    {
        for (int i = 0; i < shown.Count && i < newer.Count; i++)
            if (shown[i].Id == newer[i].Id) shown[i].RefreshFrom(newer[i]);
    }

    public static Rect DockBounds(Rect area, int platformCount, double inset = 0)
    {
        var width = Math.Min(Math.Max(1, area.Width - 12), Math.Max(4, platformCount) * PlatformWidth);
        bool overflow = platformCount * PlatformWidth > width;
        var height = Math.Min(area.Height, DefaultHeight + (overflow ? SystemParameters.HorizontalScrollBarHeight : 0));
        return new Rect(Math.Max(area.Left, area.Right - width - 6), Math.Max(area.Top, area.Bottom - height - Math.Max(0, inset)), width, height);
    }

    public void SetTaskbarInset(double inset) { _taskbarInset = inset; DockToTaskbar(); }

    void QueueDock()
    {
        if (!_positioned || _dockQueued) return;
        _dockQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _dockQueued = false;
            if (IsLoaded) DockToTaskbar();
        }));
    }

    Rect CurrentWorkArea()
    {
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return Rect.Transform(new Rect(area.X, area.Y, area.Width, area.Height), transform);
    }

    void DockToTaskbar()
    {
        if (!_positioned || WindowState != WindowState.Normal) return;
        var bounds = DockBounds(CurrentWorkArea(), _platformCount, _taskbarInset);
        MinWidth = Math.Min(MinWindowWidth, bounds.Width);
        MinHeight = Math.Min(MinWindowHeight, bounds.Height);
        if (Math.Abs(Width - bounds.Width) > 0.1) Width = bounds.Width;
        if (Math.Abs(Height - bounds.Height) > 0.1) Height = bounds.Height;
        if (Math.Abs(Left - bounds.Left) > 0.1) Left = bounds.Left;
        if (Math.Abs(Top - bounds.Top) > 0.1) Top = bounds.Top;
        if (IsAccountsOpen) _accountsFlyout!.PositionBeside(this, CurrentWorkArea());
        AdvanceCharacters(0);
    }

    public void AdvanceCharacters(double seconds)
    {
        double extent = Math.Max(0, CharacterField.ActualWidth - 144);
        var stages = Stages;
        var bodies = new Rect[stages.Length];
        // 켜진 캐릭터끼리만 자리를 나눈다 (예전 네 캐릭터만 켜면 예전과 같은 자리).
        var shown = Enumerable.Range(0, stages.Length).Where(i => stages[i].Visibility == Visibility.Visible).ToList();
        for (int i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];
            int slot = shown.IndexOf(i);
            if (slot < 0)
            {
                bodies[i] = Rect.Empty;
                stage.IsWalking = false;
                continue;
            }
            bool walking = SystemParameters.ClientAreaAnimation && stage.Mood is Mood.Idle or Mood.Playing or Mood.Happy;
            var walker = _walkers[i];
            walker.Advance(seconds, extent, walking, (slot + 0.5) / shown.Count);
            Canvas.SetLeft(stage, walker.Position);
            Canvas.SetTop(stage, Math.Max(0, CharacterField.ActualHeight - stage.Height));
            stage.IsWalking = walker.IsMoving;
            stage.FacingLeft = walker.FacingLeft;
            bodies[i] = stage.BodyBoundsIn(stage);
            if (!bodies[i].IsEmpty) bodies[i].Offset(walker.Position, Canvas.GetTop(stage));
        }
        var order = _layers.Update(bodies, stages.Select(stage => stage.IsWalking).ToArray());
        for (int z = 0; z < order.Count; z++) Panel.SetZIndex(stages[order[z]], z);
    }

    void UpdateStages()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        foreach (var stage in Stages) stage.SetActive(visible && stage.Visibility == Visibility.Visible);
        if (visible && CharactersShown) _walkTimer?.Start(); else _walkTimer?.Stop();
        if (visible) _dockTimer?.Start(); else _dockTimer?.Stop();
    }

    /// <summary>켜진 캐릭터만 세운다. 창 크기·위치와 다른 캐릭터의 자리는 그대로 둔다.</summary>
    public void ApplyCharacters(IEnumerable<string> characters)
    {
        _characters = CharacterSelection.Normalize(characters);
        var stages = Stages;
        for (int i = 0; i < stages.Length; i++)
        {
            bool on = _characters.Contains(CharacterSelection.All[i]);
            stages[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // 숨긴 캐릭터는 그림·합성 결과·줄 선 대사를 놓아 주고, 다시 켜면 읽어 들인다.
            if (on) stages[i].ReloadArt(); else stages[i].ReleaseArt();
        }
        UpdateStages();
        AdvanceCharacters(0);
    }

    public void ApplyTopmost(bool on)
    {
        Topmost = on;
        if (_accountsFlyout != null) _accountsFlyout.Topmost = on;
    }

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
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        DockToTaskbar();
        Activate();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsAccountsOpen) { SetAccountsOpen(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(DockToTaskbar);
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A) Dispatcher.BeginInvoke(DockToTaskbar);
        return IntPtr.Zero;
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
