using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

public sealed record CharacterMessage(bool FromUser, string Text);

/// <summary>A dedicated conversation window for a monitor character.</summary>
public sealed class CharacterAskWindow : Window
{
    static readonly Brush Paper = BrushOf(0xFF211E28);
    static readonly Brush Well = BrushOf(0xFF19171F);
    static readonly Brush Ink = BrushOf(0xFFFFF5F5);
    static readonly Brush Muted = BrushOf(0xFFC3B9CD);
    static readonly Brush Outline = BrushOf(0xFF575064);
    readonly Brush _accent;
    readonly TextBox _input;
    readonly Button _send;
    readonly TextBlock _placeholder, _status;
    readonly StackPanel _messages = new() { Margin = new Thickness(24, 19, 24, 5) };
    readonly ScrollViewer _scroll;
    bool _waiting, _closing;

    public string Character { get; }
    public event Action<string>? Submitted;
    public string Question { get => _input.Text; set => _input.Text = value; }
    public bool CanSend => !_waiting && !string.IsNullOrWhiteSpace(Question);

    public CharacterAskWindow(string character, string displayName, string draft = "", ImageSource? stagePortrait = null)
    {
        Character = character;
        _accent = new SolidColorBrush(CharacterTheme.For(character).Sparkles[0]);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Paper;
        ShowInTaskbar = true;
        Width = 740;
        Height = 720;
        MinWidth = 360;
        MinHeight = 350;
        Title = $"{displayName} · 캐릭터 대화";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 14;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12) });

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Background = Paper, Margin = new Thickness(17, 12, 13, 10) };
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        var close = MakeButton("×", Ink, Paper, 31, 31);
        System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var portrait = new Image { Width = 35, Height = 35, Source = CharacterTheme.ConversationPortrait(character, stage: stagePortrait) };
        RenderOptions.SetBitmapScalingMode(portrait, BitmapScalingMode.HighQuality);
        var badge = new Border { Width = 45, Height = 45, CornerRadius = new CornerRadius(16), Background = Well,
            BorderBrush = _accent, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 11, 0), Child = portrait };
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = displayName, Foreground = Ink, FontSize = 17, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = character == "orch" ? "캐릭터 대화 · Win + Space" : "캐릭터 대화", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(heading);
        layout.Children.Add(new Border { BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });

        _status = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 10, 24, 16) };
        var conversation = new StackPanel();
        conversation.Children.Add(_messages);
        conversation.Children.Add(_status);
        _scroll = new ScrollViewer { Content = conversation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1);
        layout.Children.Add(_scroll);

        var field = new Grid();
        _input = new TextBox { MaxLength = CharacterAsk.MaxQuestionLength, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 66, MaxHeight = 165, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent,
            Foreground = Ink, BorderThickness = new Thickness(0), CaretBrush = Ink, Padding = new Thickness(5, 6, 5, 4) };
        System.Windows.Automation.AutomationProperties.SetName(_input, "메시지");
        _placeholder = new TextBlock { Text = $"{displayName}에게 메시지 보내기", Foreground = Muted, IsHitTestVisible = false, Margin = new Thickness(7, 8, 0, 0) };
        field.Children.Add(_input);
        field.Children.Add(_placeholder);
        var composer = new StackPanel();
        composer.Children.Add(field);
        _send = MakeButton("보내기", Well, _accent, 77, 33);
        _send.ToolTip = "Enter로 보내기 · Shift+Enter로 줄바꿈";
        _send.HorizontalAlignment = HorizontalAlignment.Right;
        _send.Click += (_, _) => Send();
        composer.Children.Add(_send);
        var inputBox = new Border { Background = Well, BorderBrush = Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15),
            Padding = new Thickness(11, 5, 11, 8), Margin = new Thickness(19, 10, 19, 17), Child = composer };
        _input.IsKeyboardFocusWithinChanged += (_, _) => inputBox.BorderBrush = _input.IsKeyboardFocusWithin ? _accent : Outline;
        Grid.SetRow(inputBox, 2);
        layout.Children.Add(inputBox);
        Content = new Border { Background = Paper, BorderBrush = Outline, BorderThickness = new Thickness(1), Child = layout };

        _input.TextChanged += (_, _) => UpdateSend();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            Send();
            e.Handled = true;
        };
        Loaded += (_, _) => { if (!_waiting) _input.Focus(); _scroll.ScrollToEnd(); };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) =>
        {
            if (!_closing) Dispatcher.BeginInvoke(() => { if (!_closing) Close(); });
        };
        Question = draft;
        SetWaiting(false);
    }

    void UpdateSend()
    {
        _placeholder.Visibility = Question.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = CanSend;
    }

    public void Send()
    {
        if (!CanSend) return;
        var text = Question.Trim();
        SetWaiting(true);
        Question = "";
        Submitted?.Invoke(text);
    }

    public void SetWaiting(bool waiting)
    {
        bool restoreFocus = _waiting && !waiting && IsActive;
        _waiting = waiting;
        _input.IsEnabled = !waiting;
        _status.Text = waiting ? Character == "orch" ? "여러 모델의 답을 비교하고 있어요…" : "답변을 작성하고 있어요…"
            : _messages.Children.Count == 0 ? "무엇이 궁금해요?" : "";
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSend();
        if (restoreFocus) _input.Focus();
        if (waiting) _scroll.ScrollToEnd();
    }

    public void AddMessage(CharacterMessage message)
    {
        bool scroll = message.FromUser || _scroll.ScrollableHeight - _scroll.VerticalOffset < 32;
        var body = ChatRichContent.Create(message.Text, Ink, _accent, BrushOf(0xFF3D3749));
        var bubble = new Border { Background = message.FromUser ? BrushOf(0xFF3C3549) : Well, CornerRadius = new CornerRadius(14),
            HorizontalAlignment = message.FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left, Padding = new Thickness(15, 9, 15, 10),
            Margin = new Thickness(message.FromUser ? 48 : 0, 0, message.FromUser ? 0 : 48, 12), Child = body };
        _messages.Children.Add(bubble);
        if (_messages.Children.Count > 60) _messages.Children.RemoveAt(0);
        if (!_waiting) SetWaiting(false);
        if (scroll) _scroll.ScrollToEnd();
    }

    public void PlaceBy(Rect anchor, Rect workArea)
    {
        if (workArea.IsEmpty) return;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        if (!anchor.IsEmpty)
        {
            double roomAbove = Math.Min(workArea.Height, anchor.Top - workArea.Top - 10);
            if (roomAbove >= MinHeight) Height = Math.Min(Height, roomAbove);
        }
        double x = anchor.IsEmpty ? workArea.Left + workArea.Width / 2 : anchor.Left + anchor.Width / 2;
        double y = anchor.IsEmpty ? workArea.Top + (workArea.Height - Height) / 2 : anchor.Top - Height - 10;
        Left = Math.Clamp(x - Width / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    public void ActivateConversation()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!Activate())
        {
            Topmost = true;
            Activate();
            Topmost = false;
        }
        if (!_waiting) _input.Focus();
    }

    static Button MakeButton(string text, Brush ink, Brush background, double width, double height)
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        var hovered = new Trigger { Property = IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(OpacityProperty, 0.8));
        template.Triggers.Add(hovered);
        return new Button { Content = text, Template = template, Width = width, Height = height, Foreground = ink, Background = background,
            FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
    }

    static SolidColorBrush BrushOf(uint rgba)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba));
        brush.Freeze();
        return brush;
    }
}

public static partial class ChatLinks
{
    [GeneratedRegex(@"\[(?<label>[^\]\r\n]+)\]\((?<markdown>https?://(?:[^\s<>()]+|\([^()\s]*\))+)\)|(?<raw>https?://[^\s<>""'\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    public static IEnumerable<Inline> Inlines(string text, Brush accent)
    {
        int position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            var markdown = match.Groups["markdown"];
            var raw = match.Groups["raw"].Value;
            string target = markdown.Success ? markdown.Value : raw.TrimEnd('.', ',', ';', ':', '!', '。');
            if (!markdown.Success)
                while (target.EndsWith(')') && target.Count(c => c == ')') > target.Count(c => c == '(')) target = target[..^1];
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.Host.Length == 0 || uri.UserInfo.Length != 0) continue;
            if (match.Index > position) yield return new Run(text[position..match.Index]);
            var link = new Hyperlink(new Run(markdown.Success ? match.Groups["label"].Value : target)) { NavigateUri = uri,
                ToolTip = uri.AbsoluteUri, Foreground = accent };
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                try { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { MessageBox.Show("기본 브라우저에서 링크를 열지 못했어요.", "캐릭터 대화"); }
            };
            yield return link;
            if (!markdown.Success && target.Length < raw.Length) yield return new Run(raw[target.Length..]);
            position = match.Index + match.Length;
        }
        if (position < text.Length) yield return new Run(text[position..]);
    }
}
