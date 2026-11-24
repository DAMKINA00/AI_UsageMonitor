using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

/// <summary>
/// 캐릭터를 누르면 뜨는 질문 입력창. 보낼 채팅을 먼저 보여 주고, 열린 채팅이 없으면 안내만 하고 보내기를 막는다.
/// Enter 로 보내고(Shift+Enter 는 줄바꿈), Esc 나 다른 곳을 누르면 닫는다.
/// </summary>
public sealed class CharacterAskWindow : Window
{
    static readonly Brush Paper = Frozen(Color.FromRgb(0x24, 0x27, 0x2F));
    static readonly Brush Line = Frozen(Color.FromRgb(0x3B, 0x41, 0x4C));
    static readonly Brush Muted = Frozen(Color.FromRgb(0xA5, 0xAD, 0xBE));
    static readonly Brush Ink = Frozen(Color.FromRgb(0xE3, 0xE7, 0xF0));

    readonly TextBox _input;
    readonly Button _send;
    readonly TextBlock _target;
    bool _closing, _sent;

    public string Character { get; }
    /// <summary>호스트가 지금 알려 준 활성 채팅.</summary>
    public CharacterTarget Target { get; private set; }
    /// <summary>
    /// 이 초안이 갈 채팅. 처음 연 채팅으로 고정되고(열 때 채팅이 없었으면 처음 생긴 채팅), 활성 채팅이 바뀌어도 따라가지 않는다.
    /// </summary>
    public CharacterTarget DraftTarget { get; private set; }
    /// <summary>활성 채팅이 초안의 채팅과 달라 보낼 수 없는지 (초안은 그대로 둔다).</summary>
    public bool TargetChanged => DraftTarget.ChatId != null && Target.ChatId != DraftTarget.ChatId;

    /// <summary>보내기를 눌렀을 때: 초안의 채팅 id 와 질문 글.</summary>
    public event Action<long, string>? Submitted;

    /// <param name="draftTarget">다시 여는 초안의 채팅 (없으면 지금 채팅).</param>
    /// <param name="draft">다시 여는 초안 글.</param>
    public CharacterAskWindow(string character, string displayName, CharacterTarget target, CharacterTarget? draftTarget = null, string draft = "")
    {
        Character = character;
        Target = target;
        DraftTarget = draftTarget ?? target;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 300;
        Title = $"{displayName}에게 묻기";
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 12;

        var title = new TextBlock { Text = $"{displayName}에게 묻기", Foreground = Ink, FontWeight = FontWeights.SemiBold };
        _target = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
        _input = new TextBox
        {
            MaxLength = CharacterAsk.MaxQuestionLength, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Frozen(Color.FromRgb(0x1A, 0x1C, 0x22)), Foreground = Ink,
            BorderBrush = Line, CaretBrush = Ink, Padding = new Thickness(4),
        };
        AutomationName(_input, "질문");
        _send = new Button { Content = "보내기", Padding = new Thickness(12, 3, 12, 3), IsDefault = true };
        var cancel = new Button { Content = "닫기", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        _send.Click += (_, _) => Send();
        cancel.Click += (_, _) => Close();
        _input.TextChanged += (_, _) => UpdateSend();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var at = _input.CaretIndex;
                _input.Text = _input.Text.Insert(at, "\n");
                _input.CaretIndex = at + 1;
                e.Handled = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_send);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(_target);
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = new Border { Background = Paper, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = panel };

        Deactivated += (_, _) => { if (!_closing) Close(); };
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => { _input.Focus(); Keyboard.Focus(_input); };
        _input.Text = draft;
        SetTarget(target);
    }

    /// <summary>이 입력창에서 질문을 보냈는지 (보내지 않고 닫으면 초안을 남긴다).</summary>
    public bool WasSent => _sent;

    /// <summary>보낼 채팅이 바뀌면 바로 보여 준다 (열린 채팅이 없으면 보내기를 막는다).</summary>
    public void SetTarget(CharacterTarget target)
    {
        Target = target;
        // 열 때 채팅이 없었으면(초안도 쓸 수 없었으니) 처음 생긴 채팅을 초안의 채팅으로 삼는다. 그 뒤로는 바꾸지 않는다.
        if (DraftTarget.ChatId == null) DraftTarget = target;
        _target.Text = DraftTarget.ChatId == null ? "ChatterBox에서 채팅을 먼저 열어 주세요."
            : TargetChanged ? $"보던 채팅이 바뀌었어요. 이 질문은 '{DraftTarget.Title}'에만 보낼 수 있어요. 그 채팅으로 돌아가면 보낼 수 있고, 쓴 글은 남겨 둘게요."
            : $"보낼 채팅: {DraftTarget.Title}";
        _target.TextWrapping = TargetChanged ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _target.ToolTip = _target.Text;
        // 초안은 지우지 않는다: 채팅이 바뀌어도 글은 남기고 보내기만 막는다
        _input.IsEnabled = DraftTarget.ChatId != null;
        UpdateSend();
    }

    public bool CanSend => DraftTarget.ChatId != null && !TargetChanged && _input.Text.Trim().Length > 0;

    void UpdateSend() => _send.IsEnabled = CanSend;

    /// <summary>질문 글 (테스트용).</summary>
    public string Question { get => _input.Text; set => _input.Text = value; }

    public void Send()
    {
        // 한 입력창에서는 한 번만 보낸다 (Enter 연타·버튼 중복 눌림 방지)
        if (!CanSend || _sent) return;
        _sent = true;
        var text = _input.Text.Trim();
        _closing = true;
        Submitted?.Invoke(DraftTarget.ChatId!.Value, text);
        Close();
    }

    /// <summary>캐릭터 머리 위에 띄운다 (화면 밖으로 나가지 않게).</summary>
    public void PlaceAbove(Rect anchor, Rect workArea)
    {
        UpdateLayout();
        double height = ActualHeight > 0 ? ActualHeight : 150;
        Left = Math.Clamp(anchor.Left + anchor.Width / 2 - Width / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(anchor.Top - height - 4, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
    }

    static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
