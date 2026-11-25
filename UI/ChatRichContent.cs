using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIUsageMonitor.UI;

public static partial class ChatRichContent
{
    const int MaxImageBytes = 8 * 1024 * 1024;
    static readonly HttpClient Images = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };

    [GeneratedRegex(@"!\[(?<alt>[^\]\r\n]*)\]\((?<url>https?://(?:[^\s<>()]+|\([^()\s]*\))+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSeparator();

    public static FrameworkElement Create(string text, Brush ink, Brush accent, Brush outline)
    {
        var panel = new StackPanel();
        var prose = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int imageCount = 0;
        bool codeBlock = false;
        void Flush()
        {
            if (prose.Any(line => !string.IsNullOrWhiteSpace(line))) Add(panel, Body(string.Join('\n', prose), ink, accent));
            prose.Clear();
        }

        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                codeBlock = !codeBlock;
                prose.Add(line);
                continue;
            }
            var headings = codeBlock ? [] : Cells(line);
            var delimiters = !codeBlock && index + 1 < lines.Length ? Cells(lines[index + 1]) : [];
            if (headings.Length > 0 && headings.Length == delimiters.Length && delimiters.All(cell => TableDelimiter().IsMatch(cell)))
            {
                Flush();
                var rows = new List<string[]> { headings };
                index += 2;
                for (; index < lines.Length && rows.Count < 51; index++)
                {
                    var row = Cells(lines[index]);
                    if (row.Length != headings.Length) break;
                    rows.Add(row);
                }
                index--;
                Add(panel, Table(rows, delimiters, ink, accent, outline));
                continue;
            }
            if (codeBlock || imageCount >= 8) { prose.Add(line); continue; }
            int position = 0;
            foreach (Match match in MarkdownImage().Matches(line))
            {
                if (imageCount >= 8) break;
                if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0) continue;
                if (match.Index > position) prose.Add(line[position..match.Index]);
                Flush();
                Add(panel, ImagePreview(uri, match.Groups["alt"].Value, ink, accent, outline));
                imageCount++;
                position = match.Index + match.Length;
            }
            if (position == 0 || position < line.Length) prose.Add(line[position..]);
        }
        Flush();
        if (panel.Children.Count != 1) return panel;
        var single = (FrameworkElement)panel.Children[0];
        panel.Children.Clear();
        return single;
    }

    static void Add(Panel panel, FrameworkElement block)
    {
        if (panel.Children.Count > 0) block.Margin = new Thickness(0, 7, 0, 0);
        panel.Children.Add(block);
    }

    static string[] Cells(string line)
    {
        var trimmed = line.Trim();
        if (!CellSeparator().IsMatch(trimmed)) return [];
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith(@"\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        var parts = CellSeparator().Split(trimmed);
        if (parts.Length > 12) return [];
        return parts.Select(cell => cell.Trim().Replace(@"\|", "|")).ToArray();
    }

    static TextBlock Body(string text, Brush ink, Brush accent)
    {
        var body = new TextBlock { Foreground = ink, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        foreach (Inline inline in ChatLinks.Inlines(text, accent)) body.Inlines.Add(inline);
        return body;
    }

    static Border Table(List<string[]> rows, string[] delimiters, Brush ink, Brush accent, Brush outline)
    {
        var grid = new Grid();
        foreach (var _ in delimiters) grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 64 });
        for (int row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int column = 0; column < delimiters.Length; column++)
            {
                var body = Body(rows[row][column], ink, accent);
                body.TextAlignment = delimiters[column].EndsWith(':')
                    ? delimiters[column].StartsWith(':') ? TextAlignment.Center : TextAlignment.Right : TextAlignment.Left;
                if (row == 0) body.FontWeight = FontWeights.SemiBold;
                var cell = new Border { Padding = new Thickness(8, 5, 8, 5), BorderBrush = outline,
                    Background = row == 0 ? outline : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 1, 1), Child = body };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid };
        return new Border { BorderThickness = new Thickness(1, 1, 0, 0), BorderBrush = outline, Child = scroll };
    }

    static Border ImagePreview(Uri uri, string description, Brush ink, Brush accent, Brush outline)
    {
        var alt = string.IsNullOrWhiteSpace(description) ? "이미지" : description;
        var panel = new StackPanel();
        var load = new Button { Content = "이미지 보기", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4), Foreground = ink, Background = outline, BorderBrush = outline, Cursor = System.Windows.Input.Cursors.Hand };
        var link = Body($"[{alt}]({uri.AbsoluteUri})", ink, accent);
        var preview = new Image { MaxHeight = 350, MaxWidth = 640, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(preview, alt);
        panel.Children.Add(preview);
        panel.Children.Add(load);
        panel.Children.Add(link);
        load.Click += async (_, _) =>
        {
            load.IsEnabled = false;
            load.Content = "이미지 불러오는 중…";
            try
            {
                preview.Source = await Download(uri);
                preview.Visibility = Visibility.Visible;
                load.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                load.Content = "불러오지 못함 · 다시 시도";
                load.IsEnabled = true;
            }
        };
        return new Border { BorderBrush = outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7), Child = panel, HorizontalAlignment = HorizontalAlignment.Left };
    }

    static async Task<BitmapSource> Download(Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var response = await Images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes) throw new IOException("Image exceeds the byte limit");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16_384];
        int count;
        while ((count = await input.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > MaxImageBytes) throw new IOException("Image exceeds the byte limit");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 900;
        bitmap.StreamSource = buffer;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
