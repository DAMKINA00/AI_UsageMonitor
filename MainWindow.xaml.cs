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
    const double CharacterStageWidth = 144, CharacterStageHeight = 178;
    static readonly Rect CharacterFootprint = new(36, 70, 72, 108);
    readonly Point?[] _positions = new Point?[6];
    readonly Point?[] _destinations = new Point?[6];
    Size _fieldSize;
    bool _characterLayoutDirty = true;
    int _pressedCharacter = -1, _dropPriority = -1;
    Point _pressPoint, _pressOrigin;
    bool _draggingCharacter;
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
        if (!_askEnabled) return false;
        var anchor = CharacterAnchor(id);
        if (anchor.IsEmpty) return false;
        CharacterClicked?.Invoke(id, anchor);
        return true;
    }

    public Rect CharacterAnchor(string id)
    {
        int index = CharacterSelection.All.ToList().IndexOf(id);
        if (!CanSpeak || index < 0) return Rect.Empty;
        var stage = Stages[index];
        return stage.IsVisible && stage.HasArt ? ScreenRect(stage, stage.BodyBoundsIn(stage)) : Rect.Empty;
    }

    Rect ScreenRect(FrameworkElement element, Rect bounds)
    {
        if (bounds.IsEmpty || PresentationSource.FromVisual(element) is not { CompositionTarget: { } target }) return Rect.Empty;
        var topLeft = target.TransformFromDevice.Transform(element.PointToScreen(bounds.TopLeft));
        var bottomRight = target.TransformFromDevice.Transform(element.PointToScreen(bounds.BottomRight));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>이 창이 있는 화면의 작업 영역 (DIP).</summary>
    public Rect WorkArea => CurrentWorkArea();

    public bool ShowCharacterEvent(CharacterEvent evt)
    {
        if (!_seenEvents.Accept(evt.Id)) return false;
        return Speak(evt.Character, evt.Text);
    }

    /// <summary>모니터가 화면에 보이는지 (숨김·최소화가 아님). 보이지 않을 때 온 대사는 버린다.</summary>
    public bool CanSpeak => IsVisible && WindowState != WindowState.Minimized;

    /// <summary>
    /// 그 캐릭터가 한 번 말한다. 모니터가 숨었거나 최소화됐거나, 캐릭터가 꺼졌거나 그림이 없으면 말하지 않고 버린다
    /// (다시 보일 때 지난 대사가 나오지 않게).
    /// </summary>
    public bool Speak(string id, string text)
    {
        int index = CharacterSelection.All.ToList().IndexOf(id);
        if (index < 0 || !CanSpeak) return false;
        var stage = Stages[index];
        if (stage.Visibility != Visibility.Visible || !stage.HasArt) return false;
        stage.Say(text);
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
        foreach (var stage in Stages) stage.ExternalWalking = true;
        CharacterField.PreviewMouseLeftButtonDown += BeginCharacterDrag;
        CharacterField.PreviewMouseMove += MoveCharacter;
        CharacterField.PreviewMouseLeftButtonUp += EndCharacterDrag;
        CharacterField.LostMouseCapture += (_, _) => FinishCharacterDrag(false);
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

    Rect CharacterLimits => new(0, Math.Max(0, CharacterField.ActualHeight - CharacterStageHeight),
        Math.Max(0, CharacterField.ActualWidth - CharacterStageWidth), 0);

    public void AdvanceCharacters(double seconds)
    {
        var size = CharacterField.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        var stages = Stages;
        var visible = Enumerable.Range(0, stages.Length).Where(i => stages[i].Visibility == Visibility.Visible).ToArray();
        if (_fieldSize != size || _characterLayoutDirty)
        {
            _fieldSize = size;
            _characterLayoutDirty = false;
            var limits = CharacterLimits;
            var occupied = new List<Rect>();
            foreach (int i in visible.OrderBy(index => index == _dropPriority ? -1 : _destinations[index].HasValue ? 0 : 1))
            {
                var initial = new Point(limits.Width * (Array.IndexOf(visible, i) + 0.5) / visible.Length, limits.Bottom);
                var desired = _destinations[i] ?? initial;
                var destination = CharacterPlacement.Separate(desired, CharacterFootprint, limits, occupied);
                _destinations[i] = destination;
                _positions[i] = _positions[i] is Point current ? CharacterPlacement.Clamp(current, limits) : destination;
                occupied.Add(CharacterPlacement.At(CharacterFootprint, destination));
            }
            _dropPriority = -1;
        }
        var bodies = new Rect[stages.Length];
        Array.Fill(bodies, Rect.Empty);
        foreach (int i in visible)
        {
            var stage = stages[i];
            var old = _positions[i]!.Value;
            var destination = _destinations[i]!.Value;
            var delta = destination - old;
            double step = 310 * Math.Clamp(seconds, 0, 0.1);
            var next = !SystemParameters.ClientAreaAnimation || delta.Length <= step ? destination : old + delta * (step / delta.Length);
            _positions[i] = next;
            Canvas.SetLeft(stage, next.X);
            Canvas.SetTop(stage, next.Y);
            stage.IsWalking = next != old && delta.Length > 0.5 && i != _pressedCharacter;
            if (stage.IsWalking && delta.X != 0) stage.FacingLeft = delta.X < 0;
            stage.RepositionSpeech();
            bodies[i] = CharacterPlacement.At(CharacterFootprint, next);
        }
        var order = _layers.Update(bodies, stages.Select(stage => stage.IsWalking).ToArray());
        for (int z = 0; z < order.Count; z++) Panel.SetZIndex(stages[order[z]], z);
    }

    void BeginCharacterDrag(object sender, MouseButtonEventArgs e)
    {
        var stages = Stages;
        var pointer = e.GetPosition(CharacterField);
        var hit = Enumerable.Range(0, stages.Length).OrderByDescending(i => Panel.GetZIndex(stages[i])).FirstOrDefault(i =>
        {
            var stage = stages[i];
            return _positions[i] is Point origin && stage.IsVisible && stage.HasArt &&
                CharacterPlacement.At(stage.BodyBoundsIn(stage), origin).Contains(pointer);
        }, -1);
        if (hit < 0 || !CharacterField.CaptureMouse()) return;
        _pressedCharacter = hit;
        _pressPoint = pointer;
        _pressOrigin = _positions[hit]!.Value;
        e.Handled = true;
    }

    void MoveCharacter(object sender, MouseEventArgs e)
    {
        if (_pressedCharacter < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(CharacterField) - _pressPoint;
        if (!_draggingCharacter && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        _draggingCharacter = true;
        CharacterField.Cursor = Cursors.SizeWE;
        var origin = CharacterPlacement.Drag(_pressOrigin, delta, CharacterLimits);
        Stages[_pressedCharacter].SlideBy(origin.X - _positions[_pressedCharacter]!.Value.X);
        _positions[_pressedCharacter] = _destinations[_pressedCharacter] = origin;
        AdvanceCharacters(0);
        e.Handled = true;
    }

    void EndCharacterDrag(object sender, MouseButtonEventArgs e)
    {
        if (_pressedCharacter < 0) return;
        var delta = e.GetPosition(CharacterField) - _pressPoint;
        FinishCharacterDrag(Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance);
        CharacterField.ReleaseMouseCapture();
        e.Handled = true;
    }

    void FinishCharacterDrag(bool click)
    {
        int index = _pressedCharacter;
        if (index < 0) return;
        _pressedCharacter = -1;
        CharacterField.ClearValue(CursorProperty);
        if (_draggingCharacter)
        {
            _draggingCharacter = false;
            Stages[index].ReleaseSlide();
            _dropPriority = index;
            _characterLayoutDirty = true;
            AdvanceCharacters(0);
        }
        else if (click) ClickCharacter(Stages[index].Character);
    }

    void UpdateStages()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        foreach (var stage in Stages)
        {
            stage.SetActive(visible && stage.Visibility == Visibility.Visible);
            // 숨김·최소화: 말하던 대사와 줄 선 대사를 버린다 (다시 열 때 지난 대사가 남지 않게)
            if (!visible) stage.StopSpeech();
        }
        if (visible && CharactersShown) _walkTimer?.Start(); else _walkTimer?.Stop();
        if (visible) _dockTimer?.Start(); else _dockTimer?.Stop();
    }

    /// <summary>켜진 캐릭터만 세운다. 창 크기·위치와 다른 캐릭터의 자리는 그대로 둔다.</summary>
    public void ApplyCharacters(IEnumerable<string> characters)
    {
        _characters = CharacterSelection.Normalize(characters);
        _characterLayoutDirty = true;
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
