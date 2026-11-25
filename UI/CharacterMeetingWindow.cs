using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using AIUsageMonitor.Core;
using Microsoft.Win32;

namespace AIUsageMonitor.UI;

public sealed class CharacterMeetingWindow : Window
{
    const double DefaultWidth = 1020, DefaultHeight = 750, MinimumWidth = 490, MinimumHeight = 380;
    static readonly Brush Paper = ColorBrush(0x22262E), Side = ColorBrush(0x1B1E26), Well = ColorBrush(0x2A303C),
        Outline = ColorBrush(0x363E4D), Ink = ColorBrush(0xF2F4FA), Muted = ColorBrush(0xB4BDCE),
        Accent = ColorBrush(0xAFC2FF), Mint = ColorBrush(0x96E2CB), Coral = ColorBrush(0xFFB1A7),
        Active = ColorBrush(0x27353D), Conclusion = ColorBrush(0x2D2D43);
    readonly StackPanel _messages = new() { Margin = new Thickness(16, 10, 16, 10), MaxWidth = 920 };
    readonly List<string> _results = [];
    readonly Dictionary<string, Row> _rows = new();
    readonly Dictionary<string, (Avatar Avatar, TextBlock Note)> _participants = new();
    readonly Dictionary<string, (ImageSource? Neutral, ImageSource? Speaking)> _portraits = new();
    readonly TextBlock _status, _empty, _placeholder, _rosterCount;
    readonly TextBox _input;
    readonly ScrollViewer _scroll;
    readonly Button _send, _stop, _folderButton, _clearFolder, _latestResult, _latestMessage;
    Size _preferredSize, _appliedSize;
    bool _busy, _pickingFolder;

    public event Action<string, string?>? Submitted;
    public event Action? StopRequested;
    public event Action<Size>? UserSizeChanged;
    public string Draft => _input.Text;
    public string? Folder { get; private set; }
    public void RestoreDraft(string text) => _input.Text = text;

    public CharacterMeetingWindow(IReadOnlyDictionary<string, ImageSource?> stagePortraits, string draft, string? folder,
        double? savedWidth = null, double? savedHeight = null)
    {
        Title = "Boardroom · 캐릭터 회의실";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 14;
        _preferredSize = _appliedSize = new Size(SavedDimension(savedWidth, DefaultWidth), SavedDimension(savedHeight, DefaultHeight));
        Width = _appliedSize.Width;
        Height = _appliedSize.Height;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        Background = Paper;
        SnapsToDevicePixels = UseLayoutRounding = true;
        WindowStyle = WindowStyle.None;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIUsageMonitor;component/UI/BoardroomTheme.xaml", UriKind.Relative) });
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14) });
        foreach (var id in CharacterSelection.All)
        {
            stagePortraits.TryGetValue(id, out var portrait);
            _portraits[id] = (CharacterTheme.ConversationPortrait(id, stage: portrait), CharacterTheme.ConversationPortrait(id, true, portrait));
        }

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = Side, Margin = new Thickness(14, 6, 9, 6) };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else if (WindowState == WindowState.Normal) DragMove();
        };
        var close = ButtonFor("×", Ink, Side);
        System.Windows.Automation.AutomationProperties.SetName(close, "회의실 숨기기");
        close.ToolTip = "숨기기 · Win+Space";
        close.Click += (_, _) => HideIfOpen();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var maximize = ButtonFor("□", Muted, Side);
        var maximizeLabel = "최대화";
        maximize.ToolTip = maximizeLabel;
        System.Windows.Automation.AutomationProperties.SetName(maximize, maximizeLabel);
        maximize.Click += (_, _) => ToggleMaximize();
        StateChanged += (_, _) =>
        {
            bool full = WindowState == WindowState.Maximized;
            maximize.Content = full ? "❐" : "□";
            maximize.ToolTip = full ? "창 크기 복원" : maximizeLabel;
            System.Windows.Automation.AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
        };
        DockPanel.SetDock(maximize, Dock.Right);
        header.Children.Add(maximize);
        var shortcut = Label("Win + Space", Muted, 11);
        shortcut.VerticalAlignment = VerticalAlignment.Center;
        shortcut.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(shortcut, Dock.Right);
        header.Children.Add(shortcut);
        var mark = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Accent,
            Margin = new Thickness(0, 0, 9, 0), Child = new TextBlock { Text = "✦", Foreground = Side, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(mark, Dock.Left);
        header.Children.Add(mark);
        var title = Label("Boardroom", Ink, 13, true);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        shell.Children.Add(new Border { Background = Side, BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 2);
        shell.Children.Add(body);
        var channelHeader = new DockPanel { Margin = new Thickness(14, 7, 11, 7), LastChildFill = true };
        var members = ButtonFor("참가자", Muted, Side);
        DockPanel.SetDock(members, Dock.Right);
        channelHeader.Children.Add(members);
        _latestResult = ButtonFor("최근 결론", Accent, Side);
        _latestResult.IsEnabled = false;
        _latestResult.Click += (_, _) => JumpToResult();
        DockPanel.SetDock(_latestResult, Dock.Right);
        channelHeader.Children.Add(_latestResult);
        var channelName = Label("#  회의실", Ink, 15, true);
        channelName.VerticalAlignment = VerticalAlignment.Center;
        channelHeader.Children.Add(channelName);
        var channelBar = new Border { Background = Paper, BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1), Child = channelHeader };
        Grid.SetRow(channelBar, 1);
        shell.Children.Add(channelBar);

        var channel = new Grid();
        channel.RowDefinitions.Add(new RowDefinition());
        channel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _empty = Label("안건을 보내면 마스터가 첫 질문을 던지고 참가자들이 차례로 의견을 이어갑니다.", Muted, 14);
        _empty.LineHeight = 24;
        _empty.Margin = new Thickness(18, 26, 20, 6);
        _messages.Children.Add(_empty);
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        channel.Children.Add(_scroll);
        body.Children.Add(channel);

        var rail = new StackPanel { Margin = new Thickness(13, 16, 11, 12) };
        _rosterCount = Label("참가자", Ink, 13, true);
        _rosterCount.Margin = new Thickness(5, 0, 0, 3);
        rail.Children.Add(_rosterCount);
        foreach (var id in CharacterSelection.All)
        {
            var avatar = MakeAvatar(id, 32);
            var titleText = Label(NameFor(id), Ink, 13, true);
            var note = Label("연결 확인 중", Muted, 11);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
            info.Children.Add(titleText);
            info.Children.Add(note);
            var item = new DockPanel { Margin = new Thickness(5, 9, 2, 1) };
            item.Children.Add(avatar.Surface);
            item.Children.Add(info);
            rail.Children.Add(item);
            _participants[id] = (avatar, note);
        }
        var side = new Border { Background = Side, BorderBrush = Outline, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rail } };
        body.Children.Add(side);
        bool? rosterPreference = null;
        void LayoutRoster(double width)
        {
            bool wide = width >= 720;
            bool show = rosterPreference ?? wide;
            body.ColumnDefinitions[1].Width = new GridLength(show && wide ? 206 : 0);
            Grid.SetColumn(side, wide ? 1 : 0);
            side.Width = wide ? double.NaN : 194;
            side.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            side.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            members.Background = show ? Well : Side;
            members.Foreground = show ? Accent : Muted;
            members.ToolTip = show ? "참가자 목록 숨기기" : "참가자 목록 보기";
        }
        members.Click += (_, _) =>
        {
            rosterPreference = side.Visibility != Visibility.Visible;
            LayoutRoster(ActualWidth);
        };

        var footer = new StackPanel { Margin = new Thickness(18, 0, 18, 12), MaxWidth = 916 };
        var information = new DockPanel { Margin = new Thickness(3, 0, 3, 5) };
        _latestMessage = ButtonFor("↓ 최신 메시지", Accent, Well);
        _latestMessage.Visibility = Visibility.Collapsed;
        _latestMessage.Click += (_, _) => _scroll.ScrollToEnd();
        DockPanel.SetDock(_latestMessage, Dock.Right);
        information.Children.Add(_latestMessage);
        _scroll.ScrollChanged += (_, _) => _latestMessage.Visibility = AtEnd() ? Visibility.Collapsed : Visibility.Visible;
        _status = Label("", Mint, 12);
        _status.VerticalAlignment = VerticalAlignment.Center;
        information.Children.Add(_status);
        footer.Children.Add(information);
        var composer = new StackPanel();
        var field = new Grid();
        _input = new TextBox { MaxLength = CharacterAsk.MaxQuestionLength, AcceptsReturn = true, MinHeight = 39, MaxHeight = 145,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2, 5, 2, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Ink, CaretBrush = Accent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(_input, "회의 안건");
        field.Children.Add(_input);
        _placeholder = Label("회의실에 안건 보내기", Muted, 14);
        _placeholder.Margin = new Thickness(2, 5, 2, 0);
        _placeholder.IsHitTestVisible = false;
        field.Children.Add(_placeholder);
        composer.Children.Add(field);
        var controls = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        _folderButton = ButtonFor("조사 폴더", Muted, Well);
        _folderButton.ToolTip = "선택한 폴더에서 찾은 관련 텍스트 일부가 모델에 전달되며, 회의 기록은 원격 Boardroom과 공유됩니다.";
        _folderButton.Click += (_, _) => PickFolder();
        controls.Children.Add(_folderButton);
        _clearFolder = ButtonFor("×", Muted, Well);
        _clearFolder.ToolTip = "조사 폴더 해제";
        _clearFolder.Click += (_, _) => SetFolder(null);
        controls.Children.Add(_clearFolder);
        _send = ButtonFor("보내기", Side, Accent);
        _send.ToolTip = "Enter로 보내기 · Shift+Enter로 줄바꿈";
        _send.Click += (_, _) => Send();
        DockPanel.SetDock(_send, Dock.Right);
        controls.Children.Add(_send);
        _stop = ButtonFor("중단", Coral, Well);
        _stop.Click += (_, _) => StopRequested?.Invoke();
        DockPanel.SetDock(_stop, Dock.Right);
        controls.Children.Add(_stop);
        composer.Children.Add(controls);
        var box = new Border { Child = composer, Padding = new Thickness(9, 5, 9, 7), CornerRadius = new CornerRadius(9),
            Background = Well, BorderBrush = Outline, BorderThickness = new Thickness(1) };
        _input.IsKeyboardFocusWithinChanged += (_, _) => box.BorderBrush = _input.IsKeyboardFocusWithin ? Accent : Outline;
        footer.Children.Add(box);
        Grid.SetRow(footer, 1);
        channel.Children.Add(footer);
        Content = new Border { Background = Paper, BorderBrush = Outline, BorderThickness = new Thickness(1), Child = shell };

        _input.TextChanged += (_, _) => RefreshComposer();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            e.Handled = true;
            Send();
        };
        SizeChanged += (_, e) =>
        {
            LayoutRoster(e.NewSize.Width);
            RememberSize(e.NewSize);
        };
        Loaded += (_, _) => { LayoutRoster(ActualWidth); _scroll.ScrollToEnd(); };
        SetFolder(folder);
        _input.Text = draft;
        SetStatus("", false);
    }

    public void PlaceIn(Rect area)
    {
        if (area.IsEmpty) return;
        double availableWidth = Math.Max(1, area.Width - 12);
        double availableHeight = Math.Max(1, area.Height - 12);
        MinWidth = Math.Min(MinimumWidth, availableWidth);
        MinHeight = Math.Min(MinimumHeight, availableHeight);
        _appliedSize = new Size(Math.Clamp(_preferredSize.Width, MinWidth, availableWidth), Math.Clamp(_preferredSize.Height, MinHeight, availableHeight));
        Width = _appliedSize.Width;
        Height = _appliedSize.Height;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    public bool HideIfOpen()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return false;
        if (!_pickingFolder) Hide();
        return true;
    }

    public void Present()
    {
        if (_pickingFolder) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!Activate())
        {
            Topmost = true;
            Activate();
            Topmost = false;
        }
        _input.Focus();
    }

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void RememberSize(Size size)
    {
        if (!IsLoaded || !IsVisible || WindowState != WindowState.Normal) return;
        bool widthChanged = Math.Abs(size.Width - _appliedSize.Width) > 1;
        bool heightChanged = Math.Abs(size.Height - _appliedSize.Height) > 1;
        if (!widthChanged && !heightChanged) return;
        _appliedSize = size;
        _preferredSize = new Size(widthChanged ? size.Width : _preferredSize.Width, heightChanged ? size.Height : _preferredSize.Height);
        UserSizeChanged?.Invoke(_preferredSize);
    }

    static double SavedDimension(double? saved, double fallback) =>
        saved is { } value && double.IsFinite(value) && value > 0 ? value : fallback;

    public void SetStatus(string text, bool busy)
    {
        _busy = busy;
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshComposer();
    }

    public void SetRoster(IReadOnlyCollection<string>? connected, IReadOnlyCollection<string> speaking)
    {
        _rosterCount.Text = connected == null ? "참가자" : $"참가자  ·  {connected.Count}/{_participants.Count} 연결";
        foreach (var (id, entry) in _participants)
        {
            bool writing = speaking.Contains(id);
            bool available = connected?.Contains(id) == true;
            entry.Note.Text = writing ? "●  작성 중" : connected == null ? "연결 확인 중" : available ? "참여 가능" : "연결 필요";
            entry.Note.Foreground = writing ? Mint : Muted;
            entry.Avatar.Surface.Opacity = available || writing || connected == null ? 1 : 0.50;
            Animate(entry.Avatar, id, writing);
        }
    }

    public void ResetMessages()
    {
        _messages.Children.Clear();
        _messages.Children.Add(_empty);
        _rows.Clear();
        _results.Clear();
        _latestResult.IsEnabled = false;
        _empty.Visibility = Visibility.Visible;
    }

    public void Upsert(MeetingMessage message, bool draft)
    {
        bool scroll = message.Sender == "user" || AtEnd();
        if (!_rows.TryGetValue(message.Id, out var row))
        {
            var avatar = MakeAvatar(message.Sender, 34);
            var top = new WrapPanel();
            top.Children.Add(Label(NameFor(message.Sender), SenderColor(message.Sender), 14, true));
            var time = Label(DateTimeOffset.FromUnixTimeMilliseconds(message.At).ToLocalTime().ToString("HH:mm"), Muted, 11);
            time.Margin = new Thickness(8, 3, 0, 0);
            top.Children.Add(time);
            if (message.Kind is { } kind)
            {
                var role = Label(kind switch { "assignment" => "질문", "report" => "발표", "review" => "의견", "discussion" => "발언", _ => "결론" }, Accent, 11);
                top.Children.Add(new Border { Background = Well, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 1, 0, 0),
                    Padding = new Thickness(5, 1, 5, 1), Child = role });
            }
            var content = new ContentControl { Margin = new Thickness(0, 3, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var text = new StackPanel { Margin = new Thickness(12, 0, 1, 0) };
            text.Children.Add(top);
            text.Children.Add(content);
            var layout = new DockPanel();
            layout.Children.Add(avatar.Surface);
            layout.Children.Add(text);
            var border = new Border { Padding = new Thickness(9, 6, 9, 7), Margin = new Thickness(0, 0, 0, 3),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2, 0, 0, 0), Child = layout };
            row = new Row(border, content, avatar, Label("", Ink, 14));
            row.Live.LineHeight = 21;
            _rows.Add(message.Id, row);
            _messages.Children.Add(border);
            _empty.Visibility = Visibility.Collapsed;
        }
        if (draft)
        {
            row.Live.Text = message.Text.Length == 0 ? "…" : message.Text + " ▏";
            row.Content.Content = row.Live;
        }
        else
        {
            row.Content.Content = ChatRichContent.Create(message.Text, Ink, Accent, Outline);
            if (message.Kind == "summary" && !_results.Contains(message.Id))
            {
                _results.Add(message.Id);
                _latestResult.IsEnabled = true;
            }
        }
        row.Surface.Background = message.Kind == "summary" ? Conclusion : draft ? Active : message.Kind == "assignment" ? Well : Brushes.Transparent;
        row.Surface.BorderBrush = message.Kind == "summary" ? Accent : draft ? Mint : Brushes.Transparent;
        Animate(row.Avatar, message.Sender, draft);
        if (scroll) _scroll.ScrollToEnd();
    }

    public void Remove(string id)
    {
        if (_rows.Remove(id, out var row)) _messages.Children.Remove(row.Surface);
        if (_results.Remove(id)) _latestResult.IsEnabled = _results.Count > 0;
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    bool AtEnd() => _scroll.ScrollableHeight - _scroll.VerticalOffset < 24;

    void JumpToResult()
    {
        if (_results.Count == 0 || !_rows.TryGetValue(_results[^1], out var row)) return;
        double offset = _scroll.VerticalOffset + row.Surface.TranslatePoint(new Point(0, 0), _scroll).Y;
        _scroll.ScrollToVerticalOffset(offset);
    }

    void RefreshComposer()
    {
        _send.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(Draft);
        _placeholder.Visibility = Draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Send()
    {
        if (!_send.IsEnabled) return;
        var text = Draft.Trim();
        _input.Clear();
        Submitted?.Invoke(text, Folder);
    }

    void PickFolder()
    {
        _pickingFolder = true;
        try
        {
            var dialog = new OpenFolderDialog { Title = "회의에서 읽을 텍스트가 있는 폴더 선택", Multiselect = false };
            if (Folder != null) dialog.InitialDirectory = Folder;
            if (dialog.ShowDialog(this) == true) SetFolder(dialog.FolderName);
        }
        finally { _pickingFolder = false; }
    }

    void SetFolder(string? folder)
    {
        Folder = folder;
        var name = folder == null ? "" : new System.IO.DirectoryInfo(folder).Name;
        _folderButton.Content = folder == null ? "＋ 조사 폴더" : "▣ " + (name.Length > 26 ? name[..26] + "…" : name);
        _clearFolder.Visibility = folder == null ? Visibility.Collapsed : Visibility.Visible;
    }

    sealed record Row(Border Surface, ContentControl Content, Avatar Avatar, TextBlock Live);
    sealed class Avatar(Border surface, Image image)
    {
        public Border Surface { get; } = surface;
        public Image Image { get; } = image;
        public bool Speaking { get; set; }
    }

    Avatar MakeAvatar(string id, double size)
    {
        var image = new Image { Width = size - 6, Height = size - 6, RenderTransform = new TranslateTransform() };
        if (_portraits.TryGetValue(id, out var portrait)) image.Source = portrait.Neutral;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var surface = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Side,
            BorderBrush = Outline, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Child = image };
        if (id is "user" or "system")
        {
            var glyph = Label(id == "user" ? "나" : "✦", SenderColor(id), 15, true);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            surface.Child = glyph;
        }
        return new Avatar(surface, image);
    }

    void Animate(Avatar avatar, string id, bool speaking)
    {
        if (avatar.Speaking == speaking) return;
        avatar.Speaking = speaking;
        avatar.Surface.BorderBrush = speaking ? Mint : Outline;
        if (_portraits.TryGetValue(id, out var portrait)) avatar.Image.Source = speaking ? portrait.Speaking ?? portrait.Neutral : portrait.Neutral;
        var transform = (TranslateTransform)avatar.Image.RenderTransform;
        transform.BeginAnimation(TranslateTransform.YProperty, speaking && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, -3, TimeSpan.FromMilliseconds(240)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    static string NameFor(string id) => id == "user" ? "나" : id == "system" ? "회의 안내" : id == "orch" ? "마스터" : Tray.CharacterLabels[id];
    static Brush SenderColor(string id) => id is "user" or "system" ? Muted : id == "orch" ? Mint : new SolidColorBrush(CharacterTheme.For(id).Sparkles[0]);
    static TextBlock Label(string text, Brush color, double size, bool bold = false) => new()
        { Text = text, Foreground = color, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    static Button ButtonFor(string text, Brush ink, Brush background) => new()
        { Content = text, Foreground = ink, Background = background, BorderBrush = Outline, Padding = new Thickness(10, 4, 10, 4),
          Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold };
    static Brush ColorBrush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
