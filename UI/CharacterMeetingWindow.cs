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
    static readonly Brush Paper = ColorBrush(0x242735), Side = ColorBrush(0x1B1E2A), Well = ColorBrush(0x1F222F),
        Outline = ColorBrush(0x3B4054), Ink = ColorBrush(0xF4F1FC), Muted = ColorBrush(0xBCC1D6),
        Mint = ColorBrush(0xA0EDCD), Coral = ColorBrush(0xF5AB99), Active = ColorBrush(0x273C3C);
    readonly StackPanel _messages = new() { Margin = new Thickness(22, 16, 22, 12) };
    readonly Dictionary<string, Row> _rows = new();
    readonly Dictionary<string, (Avatar Avatar, TextBlock Note)> _participants = new();
    readonly Dictionary<string, (ImageSource? Neutral, ImageSource? Speaking)> _portraits = new();
    readonly TextBlock _status, _empty, _placeholder;
    readonly TextBox _input;
    readonly ScrollViewer _scroll;
    readonly Button _send, _stop, _folderButton, _clearFolder;
    bool _busy, _pickingFolder, _closing;

    public event Action<string, string?>? Submitted;
    public event Action? StopRequested;
    public string Draft => _input.Text;
    public string? Folder { get; private set; }
    public void RestoreDraft(string text) => _input.Text = text;

    public CharacterMeetingWindow(IReadOnlyDictionary<string, ImageSource?> stagePortraits, string draft, string? folder)
    {
        Title = "Boardroom · 캐릭터 회의실";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 14;
        Width = 1020;
        Height = 750;
        MinWidth = 595;
        MinHeight = 420;
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
        shell.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = Side, Margin = new Thickness(18, 12, 14, 12) };
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var close = ButtonFor("×", Ink, Side);
        System.Windows.Automation.AutomationProperties.SetName(close, "회의실 닫기");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var shortcut = Label("Alt + Space", Muted, 12);
        shortcut.VerticalAlignment = VerticalAlignment.Center;
        shortcut.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(shortcut, Dock.Right);
        header.Children.Add(shortcut);
        var mark = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(12), Background = Mint,
            Margin = new Thickness(0, 0, 11, 0), Child = new TextBlock { Text = "✦", Foreground = Side, FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(mark, Dock.Left);
        header.Children.Add(mark);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Label("Boardroom", Ink, 17, true));
        title.Children.Add(Label("캐릭터 회의실", Muted, 12));
        header.Children.Add(title);
        shell.Children.Add(new Border { Background = Side, BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(204) });
        Grid.SetRow(body, 1);
        shell.Children.Add(body);
        var channel = new Grid();
        channel.RowDefinitions.Add(new RowDefinition());
        channel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _empty = Label("함께 검토할 안건을 적어 보세요.\n마스터가 한 명을 지명하면 담당자가 발표하고 참가자들이 의견을 나눠요.", Muted, 14);
        _empty.LineHeight = 24;
        _empty.Margin = new Thickness(48, 32, 48, 8);
        _messages.Children.Add(_empty);
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        channel.Children.Add(_scroll);
        body.Children.Add(channel);

        var rail = new StackPanel { Margin = new Thickness(16, 18, 13, 12) };
        rail.Children.Add(Label("참가자", Ink, 14, true));
        foreach (var id in CharacterSelection.All)
        {
            var avatar = MakeAvatar(id, 34);
            var titleText = Label(NameFor(id), Ink, 13, true);
            var note = Label("연결 확인 중", Muted, 11);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
            info.Children.Add(titleText);
            info.Children.Add(note);
            var item = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            item.Children.Add(avatar.Surface);
            item.Children.Add(info);
            rail.Children.Add(item);
            _participants[id] = (avatar, note);
        }
        var side = new Border { Background = Side, BorderBrush = Outline, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rail } };
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 690;
            body.ColumnDefinitions[1].Width = new GridLength(wide ? 204 : 0);
            side.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        };

        var footer = new StackPanel { Margin = new Thickness(20, 0, 20, 16) };
        _status = Label("", Mint, 12);
        _status.Margin = new Thickness(5, 0, 5, 6);
        footer.Children.Add(_status);
        var composer = new StackPanel();
        var field = new Grid();
        _input = new TextBox { MaxLength = CharacterAsk.MaxQuestionLength, AcceptsReturn = true, MinHeight = 56, MaxHeight = 145,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2, 5, 2, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Ink, CaretBrush = Mint, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
        _send = ButtonFor("보내기", Side, Mint);
        _send.ToolTip = "Enter로 보내기 · Shift+Enter로 줄바꿈";
        _send.Click += (_, _) => Send();
        DockPanel.SetDock(_send, Dock.Right);
        controls.Children.Add(_send);
        _stop = ButtonFor("중단", Coral, Well);
        _stop.Click += (_, _) => StopRequested?.Invoke();
        DockPanel.SetDock(_stop, Dock.Right);
        controls.Children.Add(_stop);
        composer.Children.Add(controls);
        var box = new Border { Child = composer, Padding = new Thickness(11, 7, 11, 8), CornerRadius = new CornerRadius(13),
            Background = Well, BorderBrush = Outline, BorderThickness = new Thickness(1) };
        _input.IsKeyboardFocusWithinChanged += (_, _) => box.BorderBrush = _input.IsKeyboardFocusWithin ? Mint : Outline;
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
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() => { if (!IsActive && !_pickingFolder && !_closing) Close(); });
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => { _input.Focus(); _scroll.ScrollToEnd(); };
        SetFolder(folder);
        _input.Text = draft;
        SetStatus("", false);
    }

    public void PlaceIn(Rect area)
    {
        if (area.IsEmpty) return;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width - 12);
        Height = Math.Min(Height, area.Height - 12);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

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
        foreach (var (id, entry) in _participants)
        {
            bool writing = speaking.Contains(id);
            bool available = connected?.Contains(id) == true;
            entry.Note.Text = writing ? "작성 중…" : connected == null ? "연결 확인 중" : available ? "참여 가능" : "연결 필요";
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
        _empty.Visibility = Visibility.Visible;
    }

    public void Upsert(MeetingMessage message, bool draft)
    {
        bool scroll = message.Sender == "user" || _scroll.ScrollableHeight - _scroll.VerticalOffset < 36;
        if (!_rows.TryGetValue(message.Id, out var row))
        {
            var avatar = MakeAvatar(message.Sender, 40);
            var top = new WrapPanel();
            top.Children.Add(Label(NameFor(message.Sender), SenderColor(message.Sender), 14, true));
            var time = Label(DateTimeOffset.FromUnixTimeMilliseconds(message.At).ToLocalTime().ToString("HH:mm"), Muted, 11);
            time.Margin = new Thickness(8, 3, 0, 0);
            top.Children.Add(time);
            if (message.Kind is { } kind)
            {
                var role = Label(kind switch { "assignment" => "지명", "report" => "발표", "review" => "의견", _ => "회의 결과" }, Mint, 11);
                role.Margin = new Thickness(9, 3, 0, 0);
                top.Children.Add(role);
            }
            var content = new ContentControl { Margin = new Thickness(0, 3, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var text = new StackPanel { Margin = new Thickness(12, 0, 1, 0) };
            text.Children.Add(top);
            text.Children.Add(content);
            var layout = new DockPanel();
            layout.Children.Add(avatar.Surface);
            layout.Children.Add(text);
            var border = new Border { Padding = new Thickness(10, 8, 10, 9), Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(10), Child = layout };
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
            row.Content.Content = ChatRichContent.Create(message.Text, Ink, Mint, Outline);
        row.Surface.Background = draft || message.Kind == "summary" ? Active : message.Kind == "assignment" ? Well : Brushes.Transparent;
        Animate(row.Avatar, message.Sender, draft);
        if (scroll) _scroll.ScrollToEnd();
    }

    public void Remove(string id)
    {
        if (_rows.Remove(id, out var row)) _messages.Children.Remove(row.Surface);
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var surface = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(12), Background = Well,
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
