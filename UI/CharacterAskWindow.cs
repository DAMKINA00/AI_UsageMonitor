using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

/// <summary>A small private question and reply panel anchored to the selected character.</summary>
public sealed class CharacterAskWindow : Window
{
    static readonly Brush Paper = BrushOf(0xFF26242D);
    static readonly Brush Well = BrushOf(0xFF1D1B23);
    static readonly Brush Ink = BrushOf(0xFFFFF5F5);
    static readonly Brush Muted = BrushOf(0xFFC3B9CD);
    static readonly Brush Outline = BrushOf(0xFF575064);
    const double ReplyWidth = 264;

    readonly TextBox _input;
    readonly Button _send;
    readonly TextBlock _placeholder, _reply;
    readonly Border _replyBox;
    readonly Canvas _tailRow;
    readonly Polygon _tail;
    readonly Grid _layout;
    Rect _anchor, _workArea;
    bool _positioned, _waiting, _closing;

    public string Character { get; }
    public event Action<string>? Submitted;
    public string Question { get => _input.Text; set => _input.Text = value; }
    public bool CanSend => !_waiting && !string.IsNullOrWhiteSpace(Question);

    public CharacterAskWindow(string character, string displayName, string draft = "")
    {
        Character = character;
        var accent = new SolidColorBrush(CharacterTheme.For(character).Sparkles[0]);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 310;
        Title = $"{displayName}에게 묻기";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 13;
        UseLayoutRounding = true;

        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        var close = MakeButton("×", Muted, Brushes.Transparent, 28, 28);
        AutomationProperties.SetName(close, "닫기");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var portrait = new Image { Width = 29, Height = 29, Source = new BitmapImage(CharacterTheme.FaceUri(character, "neutral")) };
        RenderOptions.SetBitmapScalingMode(portrait, BitmapScalingMode.HighQuality);
        var badge = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(15), Background = Well,
            BorderBrush = accent, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0), Child = portrait };
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = displayName, Foreground = Ink, FontSize = 14, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = character == "orch" ? "모델 의견을 모아 교차검증해요" : "둘만의 짧은 대화", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) });
        header.Children.Add(heading);
        panel.Children.Add(header);

        _reply = new TextBlock { Foreground = Ink, FontSize = 12, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        AutomationProperties.SetLiveSetting(_reply, AutomationLiveSetting.Polite);
        _replyBox = new Border { Background = Well, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 6, 9, 7),
            Margin = new Thickness(0, 0, 0, 8), Child = _reply, Visibility = Visibility.Collapsed };
        panel.Children.Add(_replyBox);

        var field = new Grid();
        _input = new TextBox { MaxLength = CharacterAsk.MaxQuestionLength, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 42, MaxHeight = 58, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Background = Brushes.Transparent,
            Foreground = Ink, BorderThickness = new Thickness(0), CaretBrush = Ink, Padding = new Thickness(3, 4, 3, 2) };
        AutomationProperties.SetName(_input, "질문");
        _placeholder = new TextBlock { Text = "무엇이 궁금해요?", Foreground = Muted, IsHitTestVisible = false, Margin = new Thickness(6, 6, 0, 0) };
        field.Children.Add(_input);
        field.Children.Add(_placeholder);
        var inputBox = new Border { Background = Well, BorderBrush = Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5, 0, 5, 0), Child = field };
        _input.IsKeyboardFocusWithinChanged += (_, _) => inputBox.BorderBrush = _input.IsKeyboardFocusWithin ? accent : Outline;
        panel.Children.Add(inputBox);

        _send = MakeButton("보내기", Well, accent, 72, 30);
        _send.HorizontalAlignment = HorizontalAlignment.Right;
        _send.Margin = new Thickness(0, 8, 0, 0);
        _send.Click += (_, _) => Send();
        panel.Children.Add(_send);
        var card = new Border { Background = Paper, BorderBrush = Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(17),
            Padding = new Thickness(12), Child = panel };
        _layout = new Grid();
        for (int i = 0; i < 3; i++) _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card, 1);
        _layout.Children.Add(card);
        _tailRow = new Canvas { Height = 9, IsHitTestVisible = false };
        _tail = new Polygon { Fill = Paper, Stroke = Outline, StrokeThickness = 1 };
        _tailRow.Children.Add(_tail);
        _layout.Children.Add(_tailRow);
        Content = _layout;

        _input.TextChanged += (_, _) => UpdateSend();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            Send();
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => { if (!_closing) Close(); };
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => { if (!_waiting) _input.Focus(); };
        SizeChanged += (_, _) => { if (_positioned) Reposition(); };
        Question = draft;
        UpdateSend();
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
        SetWaiting();
        Question = "";
        Submitted?.Invoke(text);
    }

    public void SetWaiting()
    {
        ShowReply(Character == "orch" ? "여러 모델의 답을 비교하고 있어요…" : "생각하고 있어요…", waiting: true);
    }

    public void ShowReply(string text, bool waiting = false)
    {
        bool restoreFocus = _waiting && !waiting && IsActive;
        _waiting = waiting;
        _input.IsEnabled = !waiting;
        UpdateSend();
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _reply.Text = SpeechText.Preview(text, ReplyWidth, typeface, _reply.FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _replyBox.Visibility = Visibility.Visible;
        if (restoreFocus) _input.Focus();
    }

    public void PlaceBy(Rect anchor, Rect workArea)
    {
        _anchor = anchor;
        _workArea = workArea;
        _positioned = !anchor.IsEmpty && !workArea.IsEmpty;
        if (_positioned) Reposition();
    }

    void Reposition()
    {
        double x = _anchor.Left + _anchor.Width / 2;
        double height = ActualHeight > 0 ? ActualHeight : 164;
        bool below = _anchor.Top - height - 4 < _workArea.Top && _anchor.Bottom + height + 4 <= _workArea.Bottom;
        Left = Math.Clamp(x - Width / 2, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - Width));
        Top = Math.Clamp(below ? _anchor.Bottom + 4 : _anchor.Top - height - 4, _workArea.Top, Math.Max(_workArea.Top, _workArea.Bottom - height));
        Grid.SetRow(_tailRow, below ? 0 : 2);
        Canvas.SetLeft(_tail, Math.Clamp(x - Left - 8, 17, Width - 33));
        _tail.Points = below ? [new Point(0, 9), new Point(8, 0), new Point(16, 9)]
            : [new Point(0, 0), new Point(8, 9), new Point(16, 0)];
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
