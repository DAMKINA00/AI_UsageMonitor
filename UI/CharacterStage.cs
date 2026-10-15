using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AIUsageMonitor.UI;

public enum Mood { Idle, Playing, Sleeping, Working, WorkingHard, Waiting, Tired, Exhausted, Panic, Happy }

/// <summary>말풍선 위치: 머리 위(Above) 또는 캐릭터 왼쪽에 꼬리를 오른쪽으로 둔 채(Left).</summary>
public enum BubblePlacement { Above, Left }

/// <summary>
/// 캐릭터가 상태(Mood)에 따라 움직이는 작은 무대.
/// 뮤직비디오 컷아웃 느낌을 내려고 일부러 15fps로 끊어서 움직인다.
/// </summary>
public sealed class CharacterStage : Border
{
    const double Fps = 15;
    const double BubbleFontSize = 13, MinBubbleFontSize = 10;
    static readonly Brush Ink = Frozen(Color.FromRgb(0x5A, 0x3A, 0x28));
    static readonly FontFamily PopFont = new("Malgun Gothic");
    // 캐릭터 그림 영역의 원래 비율 (_figure 150×318 을 Viewbox 로 줄여 쓴다)
    const double FigureAspect = 150.0 / 318.0;

    readonly Grid _root = new();
    readonly TranslateTransform _wander = new();
    readonly Viewbox _figureBox;
    readonly Border _box;
    readonly Path _tail;
    readonly Grid _figure = new() { Width = 150, Height = 318 };
    readonly Image _img = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
    readonly ScaleTransform _scale = new(1, 1);
    readonly RotateTransform _rot = new(0);
    readonly TranslateTransform _move = new(0, 0);
    readonly Ellipse _shadow;
    readonly Canvas _fx = new() { IsHitTestVisible = false, ClipToBounds = false };
    readonly Grid _bubble = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0) };
    readonly TextBlock _bubbleText = new()
    {
        Foreground = Ink, FontFamily = PopFont, FontWeight = FontWeights.Bold, FontSize = 13,
        TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center,
    };
    readonly ScaleTransform _bubbleScale = new(1, 1);
    readonly TranslateTransform _bubblePosition = new();
    readonly DispatcherTimer _timer;
    readonly List<Particle> _particles = [];
    readonly Random _rng = new();
    readonly Dictionary<string, BitmapImage?> _poseCache = new();
    readonly Dictionary<string, Rect> _visibleBounds = new();
    Rect _bodyBounds = new(0, 0, 1, 1);

    string _character = "claude";
    Mood _mood = Mood.Idle;
    string _pose = "";
    double _t, _moodT, _popT = 99, _bubbleT = 99, _nextSpawn;
    bool _alt;

    public CharacterStage()
    {
        // 배경 없이 창 위에 캐릭터만 선다 (말풍선·효과는 위로 삐져나가도 되게 클립하지 않음)
        Background = Brushes.Transparent;
        Child = _root;
        _root.RenderTransform = _wander;
        _figureBox = new Viewbox
        {
            Width = 75, Height = 159, Child = _figure,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        _root.Children.Add(_figureBox);
        _nextBubbleAt = _rng.NextDouble() * 4;

        // 발밑의 옅은 바닥 그림자
        _shadow = new Ellipse
        {
            Width = 92, Height = 10, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10),
        };
        _figure.Children.Add(_shadow);

        _img.Margin = new Thickness(0, 0, 0, 12);
        _img.RenderTransformOrigin = new Point(0.5, 1);
        _img.RenderTransform = new TransformGroup { Children = { _scale, _rot, _move } };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _figure.Children.Add(_img);
        _figure.Children.Add(_fx);

        // 말풍선: 흰 바탕 + 갈색 테두리 + 아래 꼬리
        _box = new Border
        {
            Background = Brushes.White, BorderBrush = Ink, BorderThickness = new Thickness(1.6),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 4, 6, 5), Child = _bubbleText,
        };
        _tail = new Path
        {
            Data = TailDown, Fill = Brushes.White, Stroke = Ink, StrokeThickness = 1.6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -8),
        };
        _bubble.Children.Add(_box);
        _bubble.Children.Add(_tail);
        Panel.SetZIndex(_tail, 1);
        _bubble.RenderTransformOrigin = new Point(0.5, 1);
        _bubble.RenderTransform = new TransformGroup { Children = { _bubbleScale, _bubblePosition } };
        _bubble.Visibility = Visibility.Collapsed;
        _root.Children.Add(_bubble);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / Fps) };
        _timer.Tick += (_, _) => Frame();
        _figure.SizeChanged += (_, _) => ApplyImageSize();
        _img.SizeChanged += (_, _) => PositionBubble();
        _bubble.SizeChanged += (_, _) => PositionBubble();
        SizeChanged += (_, _) => LayoutBubble();
        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => UpdateRunning();
        Unloaded += (_, _) => _timer.Stop();
        HasArt = CharacterTheme.HasAsset(_character, "idle");
    }

    public string Character
    {
        get => _character;
        set
        {
            _character = value;
            _pose = "";
            // 리셋 반짝이와 "+N%" 표시 색은 캐릭터 테마가 정한다
            var theme = CharacterTheme.For(value);
            _fxStroke = theme.FxStroke;
            _sparkles = theme.Sparkles;
            AutomationProperties.SetName(this, theme.DisplayName);
            // 포즈 이미지가 아직 없는 캐릭터는 자리만 비워 둔다 (말풍선·효과도 숨김)
            HasArt = CharacterTheme.HasAsset(value, "idle");
            _root.Visibility = HasArt ? Visibility.Visible : Visibility.Hidden;
            ApplyPose();
            UpdateRunning();
        }
    }

    /// <summary>이 캐릭터의 기본 포즈 이미지가 리소스에 있는지.</summary>
    public bool HasArt { get; private set; }

    Brush _fxStroke = Ink;
    Color[] _sparkles = [Colors.White];

    BubblePlacement _placement = BubblePlacement.Above;

    /// <summary>Left 는 캐릭터를 무대 오른쪽 아래에 두고 말풍선을 그 왼쪽에 띄운다 (행 옆에 캐릭터를 둘 때).</summary>
    public BubblePlacement BubblePlacement
    {
        get => _placement;
        set
        {
            _placement = value;
            bool left = value == BubblePlacement.Left;
            _figureBox.HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            _bubble.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            _bubble.VerticalAlignment = left ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _bubble.Margin = left ? new Thickness(0) : new Thickness(2, 0, 2, 0);
            _bubble.RenderTransformOrigin = left ? new Point(1, 0.5) : new Point(0.5, 1);
            _box.Margin = left ? new Thickness(0, 0, 8, 0) : new Thickness(0);
            _tail.Data = left ? TailRight : TailDown;
            _tail.HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            _tail.VerticalAlignment = left ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            _tail.Margin = left ? new Thickness(0) : new Thickness(0, 0, 0, -8);
            if (left) _wander.X = 0;
            LayoutBubble();
        }
    }

    /// <summary>캐릭터 그림 영역의 높이 (기본 159). 폭은 원래 비율로 정한다.</summary>
    public double FigureHeight
    {
        get => _figureBox.Height;
        set
        {
            if (_figureBox.Height == value) return;
            _figureBox.Height = value;
            _figureBox.Width = value * FigureAspect;
            // 그림 폭이 바뀌면 왼쪽 말풍선의 글자 폭도 바뀌므로 지금 대사를 다시 줄바꿈한다
            if (_bubbleContent.Length > 0) LayoutBubble();
        }
    }

    static readonly Geometry TailDown = Freeze(Geometry.Parse("M0,0 L7,9 L13,0"));
    static readonly Geometry TailRight = Freeze(Geometry.Parse("M0,0 L9,6 L0,12"));

    public Mood Mood => _mood;

    /// <summary>창이 숨겨지면 애니메이션을 멈춘다.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        UpdateRunning();
    }

    bool _active = true;

    void UpdateRunning()
    {
        if (_active && HasArt && IsVisible && IsLoaded) _timer.Start();
        else _timer.Stop();
    }

    // 말풍선: 몇 초 보였다가 사라지고, 잠깐 쉬었다가 다른 대사가 나온다
    string[] _lines = [];
    // 대사마다 같은 대사인지 가리는 키 (원문 템플릿). "리셋까지 5분"·"리셋까지 4분" 처럼 채운 값만 다른 대사를 같은 대사로 본다.
    string[] _keys = [];
    string? _lastKey;
    bool _bubbleShown;
    double _bubbleShowFor, _bubbleHideT = 99, _nextBubbleAt;

    /// <param name="keys">각 대사의 원문 템플릿 (lines 와 같은 순서). 없으면 대사 자체를 키로 쓴다.</param>
    public void SetMood(Mood mood, IReadOnlyList<string> lines, IReadOnlyList<string>? keys = null)
    {
        var pairs = lines.Select((line, i) => (Line: line, Key: keys != null && i < keys.Count ? keys[i] : line))
            .Where(p => !string.IsNullOrEmpty(p.Line)).ToArray();
        _lines = pairs.Select(p => p.Line).ToArray();
        _keys = pairs.Select(p => p.Key).ToArray();
        if (mood != _mood)
        {
            _mood = mood;
            if (mood != Mood.Idle) _wander.X = 0;
            _moodT = 0;
            _nextSpawn = 0.3;
            _popT = 0;
            ApplyPose();
            // 상태가 바뀌면 바로 새 상태의 대사로
            if (_lines.Length > 0) ShowBubble(PickLine());
            else HideBubble();
        }
        else if (!_bubbleShown && _bubbleHideT > 1 && _lines.Length > 0 && _t >= _nextBubbleAt)
        {
            ShowBubble(PickLine());
        }
    }

    /// <summary>직전에 보인 대사와 원문 템플릿이 다른 대사를 고른다 (고를 수 있는 대사가 하나뿐이면 그것).</summary>
    string PickLine()
    {
        var indices = Enumerable.Range(0, _lines.Length).ToArray();
        var pool = indices.Length > 1 && indices.Any(i => _keys[i] != _lastKey) ? indices.Where(i => _keys[i] != _lastKey).ToArray() : indices;
        int pick = pool[_rng.Next(pool.Length)];
        _lastKey = _keys[pick];
        return _lines[pick];
    }

    void ShowBubble(string text)
    {
        _bubbleContent = text;
        LayoutBubble();
        _bubble.Visibility = Visibility.Visible;
        _bubble.Opacity = 1;
        _bubbleShown = true;
        _bubbleT = 0;
        _bubbleHideT = 99;
        _bubbleShowFor = 4 + Math.Min(3, 0.08 * text.Length) + _rng.NextDouble() * 3;
    }

    void HideBubble()
    {
        if (!_bubbleShown) return;
        _bubbleShown = false;
        _bubbleHideT = 0;
        _nextBubbleAt = _t + 3 + _rng.NextDouble() * 9;
    }

    void UpdateBubble(double dt)
    {
        if (_bubbleShown)
        {
            _bubbleScale.ScaleX = _bubbleScale.ScaleY = _bubbleT < 0.25 ? 0.7 + 1.2 * _bubbleT : 1;
            if (_bubbleT >= _bubbleShowFor) HideBubble();
        }
        else if (_bubbleHideT < 0.2)
        {
            _bubbleHideT += dt;
            var k = Math.Min(1, _bubbleHideT / 0.2);
            _bubbleScale.ScaleX = _bubbleScale.ScaleY = 1 - 0.35 * k;
            _bubble.Opacity = 1 - k;
            if (k >= 1) _bubble.Visibility = Visibility.Collapsed;
        }
        else if (_lines.Length > 0 && _t >= _nextBubbleAt)
        {
            ShowBubble(PickLine());
        }
    }

    string _bubbleContent = "";

    /// <summary>말풍선 글자 줄의 최대 폭. 위쪽 말풍선은 무대 폭, 왼쪽 말풍선은 캐릭터 그림 영역을 뺀 폭을 쓴다.</summary>
    double BubbleTextWidth()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width is > 0 and var w ? w : 140;
        return _placement == BubblePlacement.Left
            ? Math.Max(48, width - _figureBox.Width - 30)  // 꼬리 8 + 상자 여백·테두리 15 + 그림과의 간격
            : Math.Max(1, width - 20);
    }

    void LayoutBubble()
    {
        // 한글 기본 줄바꿈은 어절의 마지막 글자까지 쪼갠다. 실제 폭을 재서 공백에서만 나눈다.
        double available = BubbleTextWidth();
        var typeface = new Typeface(PopFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Measure(string value, double size) => new FormattedText(value,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Ink, pixelsPerDip).WidthIncludingTrailingWhitespace;
        var words = _bubbleContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        double longest = words.Length == 0 ? 0 : words.Max(word => Measure(word, BubbleFontSize));
        // 긴 어절은 글자를 줄여 맞추되 10 DIP 아래로는 줄이지 않는다 (그래도 넘치면 글자 단위로 나눈다)
        double fontSize = longest > available ? Math.Max(MinBubbleFontSize, BubbleFontSize * available / longest) : BubbleFontSize;
        _bubbleText.FontSize = fontSize;
        var lines = new List<string>();
        foreach (var paragraph in _bubbleContent.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Measure(word, fontSize) > available)
                {
                    // 10 DIP 로도 한 줄에 안 들어가는 어절: 앞 줄을 끝내고 글자 단위로 나눈다
                    if (line.Length > 0) lines.Add(line);
                    var pieces = SplitToWidth(word, available, w => Measure(w, fontSize));
                    lines.AddRange(pieces.Take(pieces.Count - 1));
                    line = pieces[^1];
                    continue;
                }
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(next, fontSize) > available)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = next;
            }
            lines.Add(line);
        }
        _bubbleText.Text = string.Join('\n', lines);
    }

    /// <summary>한 줄에 들어가지 않는 어절을 글자(문자 요소) 단위로 나눈다.</summary>
    static List<string> SplitToWidth(string word, double available, Func<string, double> measure)
    {
        var pieces = new List<string>();
        var piece = new System.Text.StringBuilder();
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (piece.Length > 0 && measure(piece + element) > available)
            {
                pieces.Add(piece.ToString());
                piece.Clear();
            }
            piece.Append(element);
        }
        pieces.Add(piece.ToString());
        return pieces;
    }

    void PositionBubble()
    {
        if (_img.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
        var body = _img.TransformToAncestor(_root).TransformBounds(new Rect(
            _bodyBounds.X * _img.ActualWidth, _bodyBounds.Y * _img.ActualHeight,
            _bodyBounds.Width * _img.ActualWidth, _bodyBounds.Height * _img.ActualHeight));
        if (_placement == BubblePlacement.Left)
        {
            // 꼬리 끝이 몸 왼쪽에 붙고, 말풍선 가운데가 머리 높이에 오도록 둔다. 무대 밖으로는 나가지 않는다.
            double headY = body.Top + body.Height * 0.18;
            _bubblePosition.X = Math.Max(0, body.Left - 2 - _bubble.ActualWidth);
            _bubblePosition.Y = Math.Clamp(headY - _bubble.ActualHeight / 2, 0, Math.Max(0, _root.ActualHeight - _bubble.ActualHeight));
            return;
        }
        // 투명 여백이 많은 졸음/휴식 포즈에서도 말풍선 꼬리가 몸 바로 위에 붙는다.
        _bubblePosition.Y = body.Top - _root.ActualHeight - 12;
        double room = Math.Max(0, (_root.ActualWidth - _bubble.ActualWidth) / 2 - 2);
        _bubblePosition.X = Math.Clamp(body.Left + body.Width / 2 - _root.ActualWidth / 2, -room, room);
    }

    /// <summary>사용률이 올랐을 때 머리 위로 "+3%" 같은 큰 글자를 띄운다.</summary>
    public void PopText(string text, Color color)
    {
        var head = HeadPoint();
        var el = Glyph(text, 24, new SolidColorBrush(color), 1.1, _fxStroke);
        Spawn(el, head.X - 24 + _rng.NextDouble() * 20, head.Y - 30, 0, -26, 1.8, 0.6, 1.15, 0);
    }

    /// <summary>리셋 등 좋은 일이 생겼을 때 반짝이를 터뜨린다.</summary>
    public void Celebrate()
    {
        var head = HeadPoint();
        var colors = _sparkles;
        for (int i = 0; i < 14; i++)
        {
            double a = _rng.NextDouble() * Math.PI * 2, v = 50 + _rng.NextDouble() * 60;
            var el = Glyph("✦", 12 + _rng.Next(8), new SolidColorBrush(colors[i % colors.Length]), 1.2, _fxStroke);
            Spawn(el, head.X, head.Y + 10, Math.Cos(a) * v, Math.Sin(a) * v - 30, 1.3, 1, 0.6, (_rng.NextDouble() - 0.5) * 300, gravity: 60);
        }
    }

    // 작업 중: 짚쨩은 홀로그램 타이핑, 클쨩은 수첩 메모. 1→2→1→3 순서로 손이 움직이고 가끔 눈을 깜빡인다
    static readonly string[] WorkFrames = ["work1", "work2", "work1", "work3"];

    static string[] FramesFor(Mood m) => m switch
    {
        Mood.Working or Mood.WorkingHard => WorkFrames,
        Mood.Playing => ["playing"],
        Mood.Sleeping => ["sleeping"],
        Mood.Waiting => ["waiting"],
        Mood.Tired => ["tired"],
        Mood.Exhausted => ["collapsed"],
        Mood.Panic => ["panic"],
        Mood.Happy => ["happy"],
        _ => ["idle"],
    };

    int _frame = -1;
    double _frameT = 99;

    void ApplyPose()
    {
        _frame = -1;
        ShowFrame(0);
        // 기진맥진하면 살짝 흐리게
        _img.Opacity = _mood == Mood.Exhausted ? 0.85 : 1;
    }

    void ShowFrame(int index)
    {
        var frames = FramesFor(_mood);
        index %= frames.Length;
        if (index == _frame && _img.Source != null) return;
        _frame = index;
        _frameT = 0;
        var pose = frames[index];
        _pose = pose;
        var key = $"{_character}_{pose}";
        if (!_poseCache.TryGetValue(key, out var bmp))
        {
            bmp = null;
            // 아직 그리지 않은 포즈는 열지 않는다 (없는 리소스를 열면 예외가 난다)
            if (CharacterTheme.HasAsset(_character, pose))
            {
                try
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = CharacterTheme.AssetUri(_character, pose);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or InvalidOperationException or System.IO.FileFormatException)
                {
                    bmp = null;
                }
            }
            _poseCache[key] = bmp;
        }
        _img.Source = bmp ?? _img.Source;
        if (bmp != null)
        {
            if (!_visibleBounds.TryGetValue(key, out _bodyBounds))
                _visibleBounds[key] = _bodyBounds = VisibleBounds(bmp);
        }
        ApplyImageSize();
    }

    static Rect VisibleBounds(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[y * stride + x * 4 + 3] > 12)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        return right < 0 ? new Rect(0, 0, 1, 1) : new Rect((double)left / width, (double)top / height,
            (double)(right - left + 1) / width, (double)(bottom - top + 1) / height);
    }

    // 에셋은 두 캐릭터 모두 "기본 자세의 키가 같은 픽셀 수"로 맞춰져 있다 (art/build_assets.py).
    // 그래서 무대에 맞춰 늘리지 않고 픽셀 크기에 같은 배율을 곱하면 두 캐릭터·모든 동작의 체형이 같게 보인다.
    const double RefCanvasHeight = 620;

    void ApplyImageSize()
    {
        if (_img.Source is not BitmapSource b || _figure.ActualHeight <= 0) return;
        double k = Math.Max(0.2, (_figure.ActualHeight - 58) / RefCanvasHeight);
        _img.Width = b.PixelWidth * k;
        _img.Height = b.PixelHeight * k;
    }

    void Frame()
    {
        const double dt = 1 / Fps;
        _t += dt;
        _moodT += dt;
        _popT += dt;
        _frameT += dt;
        _bubbleT += dt;

        double sx = 1, sy = 1, rot = 0, x = 0, y = 0;
        double t = _t;
        switch (_mood)
        {
            case Mood.Idle:
                sy = 1 + 0.015 * Math.Sin(t * 2 * Math.PI / 2.4);
                sx = 1 - 0.008 * Math.Sin(t * 2 * Math.PI / 2.4);
                y = -8 * Math.Pow(Math.Sin(_moodT * Math.PI / 1.2), 2);
                break;
            case Mood.Playing:
                sy = 1 + 0.012 * Math.Sin(t * 2 * Math.PI / 0.8);
                break;
            case Mood.Sleeping:
                sy = 1 + 0.025 * Math.Sin(t * 2 * Math.PI / 3.2);
                sx = 1 + 0.01 * Math.Sin(t * 2 * Math.PI / 3.2);
                break;
            case Mood.Working:
            case Mood.WorkingHard:
                // 서서 타이핑·메모: 프레임만 바꾸고 몸은 거의 움직이지 않는다 (바쁘면 손이 빨라짐)
                bool hard = _mood == Mood.WorkingHard;
                ShowFrame((int)(_moodT / (hard ? 0.28 : 0.45)));
                sy = 1 + 0.008 * Math.Sin(t * 2 * Math.PI / 2.4);
                break;
            case Mood.Waiting:
                rot = 6 * Math.Sin(t * 2 * Math.PI / 1.4);
                y = -2 * Math.Abs(Math.Sin(t * 2 * Math.PI / 1.4));
                break;
            case Mood.Tired:
                y = 2 + 1.5 * Math.Sin(t * 2 * Math.PI / 3);
                sy = 0.985 + 0.01 * Math.Sin(t * 2 * Math.PI / 3);
                rot = 1.5 * Math.Sin(t * 2 * Math.PI / 5);
                break;
            case Mood.Exhausted:
                y = 3;
                sy = 0.975 + 0.008 * Math.Sin(t * 2 * Math.PI / 4);
                rot = -2 + Math.Sin(t * 2 * Math.PI / 6);
                break;
            case Mood.Panic:
                x = (Math.Sin(t * 2 * Math.PI * 7) > 0 ? 1 : -1) * 2.5;
                rot = 3 * Math.Sin(t * 2 * Math.PI * 3.5);
                y = -1.5 * Math.Abs(Math.Sin(t * 2 * Math.PI * 3.5));
                break;
            case Mood.Happy:
                var p = (t % 0.9) / 0.9;
                y = -18 * Math.Sin(Math.PI * p);
                if (p < 0.12) { sy = 0.92; sx = 1.06; }
                break;
        }

        // 포즈가 바뀐 직후 "뿅" 하고 튀어나오는 연출
        if (_popT < 0.3)
        {
            var k = _popT / 0.3;
            var s = k < 0.5 ? 0.86 + 0.44 * k : 1.08 - 0.16 * (k - 0.5);
            sx *= s;
            sy *= s;
        }

        _scale.ScaleX = sx;
        _scale.ScaleY = sy;
        _rot.Angle = rot;
        _move.X = x;
        _move.Y = y;
        // Idle 포즈에서만 산책한다. 게임/독서/티타임, 작업, 졸음 등은 오른쪽 아래에 고정한다.
        // 왼쪽 말풍선 배치에서는 말풍선 자리를 침범하지 않도록 산책하지 않는다.
        _wander.X = _mood == Mood.Idle && _placement == BubblePlacement.Above ? -18 * (1 - Math.Cos(_moodT * 2 * Math.PI / 12)) : 0;
        _shadow.Width = 92 * (1 + y / 60);

        UpdateBubble(dt);
        PositionBubble();

        if (_moodT >= _nextSpawn) SpawnForMood();
        UpdateParticles(dt);
    }

    Point HeadPoint()
    {
        if (_img.ActualWidth <= 0) return new Point(_figure.ActualWidth / 2, _figure.ActualHeight * 0.45);
        var r = _img.TransformToAncestor(_figure).TransformBounds(new Rect(0, 0, _img.ActualWidth, _img.ActualHeight));
        return new Point(r.Left + r.Width / 2, r.Top + r.Height * 0.17);
    }

    Rect ImageRect() => _img.ActualWidth <= 0
        ? new Rect(_figure.ActualWidth / 2 - 50, _figure.ActualHeight - 180, 100, 160)
        : _img.TransformToAncestor(_figure).TransformBounds(new Rect(0, 0, _img.ActualWidth, _img.ActualHeight));

    void SpawnForMood()
    {
        var r = ImageRect();
        var head = new Point(r.Left + r.Width / 2, r.Top + r.Height * 0.17);
        _alt = !_alt;
        // 효과음 글자는 쓰지 않는다 (사용자 요청). 땀방울·눈물 같은 그림 효과만 남긴다.
        switch (_mood)
        {
            case Mood.WorkingHard:
                var side = _alt ? -1 : 1;
                Spawn(SweatDrop(), head.X + side * r.Width * 0.3, head.Y - 6, side * (30 + _rng.Next(25)), -40, 0.9, 1, 1, side * 120, gravity: 160);
                _nextSpawn = _moodT + 0.55;
                break;
            case Mood.Tired:
                Spawn(SweatDrop(), head.X + r.Width * 0.22, head.Y - 4, 0, 10, 1.8, 0.9, 1.05, 0);
                _nextSpawn = _moodT + 2.4;
                break;
            case Mood.Panic:
                for (int i = 0; i < 2; i++)
                {
                    var s = i == 0 ? -1 : 1;
                    Spawn(SweatDrop(), head.X + s * 14, head.Y + 10, s * (45 + _rng.Next(30)), -50, 0.9, 1, 0.9, s * 200, gravity: 220);
                }
                _nextSpawn = _moodT + 0.35;
                break;
            default:
                _nextSpawn = _moodT + 5;
                break;
        }
    }

    sealed class Particle
    {
        public required FrameworkElement El;
        public double X, Y, VX, VY, Life, Age, S0, S1, Rot, VRot, Gravity;
    }

    void Spawn(FrameworkElement el, double x, double y, double vx, double vy, double life, double s0, double s1, double vrot, double gravity = 0)
    {
        if (_particles.Count > 40) return;
        // 무대 밖으로 잘리지 않게 시작 위치를 안쪽으로
        if (_figure.ActualWidth > 0)
        {
            double half = (double.IsNaN(el.Width) ? 10 : el.Width / 2) + 4;
            x = Math.Clamp(x, half, Math.Max(half, _figure.ActualWidth - half));
        }
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new TransformGroup { Children = { new ScaleTransform(s0, s0), new RotateTransform(0) } };
        _fx.Children.Add(el);
        var p = new Particle { El = el, X = x, Y = y, VX = vx, VY = vy, Life = life, S0 = s0, S1 = s1, VRot = vrot, Gravity = gravity };
        _particles.Add(p);
        Place(p);
    }

    void UpdateParticles(double dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _fx.Children.Remove(p.El);
                _particles.RemoveAt(i);
                continue;
            }
            p.VY += p.Gravity * dt;
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.Rot += p.VRot * dt;
            Place(p);
        }
    }

    static void Place(Particle p)
    {
        double k = p.Age / p.Life;
        var g = (TransformGroup)p.El.RenderTransform;
        var s = (ScaleTransform)g.Children[0];
        s.ScaleX = s.ScaleY = p.S0 + (p.S1 - p.S0) * Math.Min(1, k * 1.6);
        ((RotateTransform)g.Children[1]).Angle = p.Rot;
        p.El.Opacity = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
        Canvas.SetLeft(p.El, p.X - p.El.Width / 2);
        Canvas.SetTop(p.El, p.Y - p.El.Height / 2);
    }

    /// <summary>뮤직비디오의 팝 텍스트처럼 갈색 외곽선이 있는 글자.</summary>
    static FrameworkElement Glyph(string text, double size, Brush fill, double stroke, Brush? strokeBrush = null)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(PopFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, Brushes.Black, 1.0);
        var geo = ft.BuildGeometry(new Point(stroke, stroke));
        geo.Freeze();
        return new Path
        {
            Data = geo, Fill = fill, Stroke = strokeBrush ?? Ink, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round,
            Width = ft.WidthIncludingTrailingWhitespace + stroke * 2, Height = ft.Height + stroke * 2,
        };
    }

    static readonly Geometry DropGeo = Freeze(Geometry.Parse("M5,0 C5,0 10,7 10,10.5 A5,5 0 1 1 0,10.5 C0,7 5,0 5,0 Z"));
    static readonly Brush DropFill = Frozen(Color.FromRgb(0xA8, 0xDD, 0xFF));
    static readonly Brush DropStroke = Frozen(Color.FromRgb(0x3E, 0x78, 0x9E));

    static FrameworkElement SweatDrop() => new Path
    {
        Data = DropGeo, Fill = DropFill, Stroke = DropStroke, StrokeThickness = 1.2, Width = 11, Height = 16, Stretch = Stretch.None,
    };

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}
