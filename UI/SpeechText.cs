using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AIUsageMonitor.UI;

internal static class SpeechText
{
    public static string Preview(string text, double width, Typeface typeface, double fontSize, double pixelsPerDip) =>
        Wrap(text, width, value => new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace, 3);

    public static string Wrap(string text, double width, Func<string, double> measure, int maxLines = int.MaxValue)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = line.Length == 0 ? word : line + " " + word;
                if (measure(next) <= width) { line = next; continue; }
                if (line.Length != 0) lines.Add(line);
                if (lines.Count >= maxLines) return Ellipsize(lines, width, measure);
                line = "";
                var elements = StringInfo.GetTextElementEnumerator(word);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement();
                    if (line.Length != 0 && measure(line + element) > width)
                    {
                        lines.Add(line);
                        if (lines.Count >= maxLines) return Ellipsize(lines, width, measure);
                        line = "";
                    }
                    line += element;
                }
            }
            lines.Add(line);
            if (lines.Count > maxLines) { lines.RemoveAt(maxLines); return Ellipsize(lines, width, measure); }
        }
        return string.Join('\n', lines);
    }

    static string Ellipsize(List<string> lines, double width, Func<string, double> measure)
    {
        var last = lines[^1].TrimEnd().TrimEnd('.', '…');
        while (last.Length > 0 && measure(last + "...") > width)
            last = last[..StringInfo.ParseCombiningCharacters(last)[^1]].TrimEnd();
        lines[^1] = last + "...";
        return string.Join('\n', lines);
    }
}
