using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static readonly string[] DialogueCharacters = ["claude", "codex", "gemini", "grok"];

    /// <summary>Mood.cs 의 분기 그대로: Panic 은 한도 도달(limit)과 작업 중 90% 이상 두 묶음.</summary>
    static readonly (Mood Mood, bool Limit, string Name)[] DialogueBranches =
    [
        (Mood.Working, false, "Working"), (Mood.WorkingHard, false, "WorkingHard"), (Mood.Waiting, false, "Waiting"),
        (Mood.Playing, false, "Playing"), (Mood.Idle, false, "Idle"), (Mood.Sleeping, false, "Sleeping"),
        (Mood.Tired, false, "Tired"), (Mood.Exhausted, false, "Exhausted"), (Mood.Panic, true, "Panic(limit)"),
        (Mood.Panic, false, "Panic(90%)"), (Mood.Happy, false, "Happy"),
    ];

    // 애칭 끝 글자: U+CA29 (ㅉ+ㅑ+ㅇ). U+CAA1 (ㅉ+ㅖ+ㄱ) 은 이전에 잘못 들어간 글자, U+C9F1 은 예전 표기.
    const char NicknameEnd = '\uCA29', WrongNicknameEnd = '\uCAA1', OldNicknameEnd = '\uC9F1';
    const double DialogueBubbleWidth = 120;

    static string[] DialogueTemplates(string character, Mood mood, bool limit) =>
        (string[])typeof(MoodTracker).GetMethod("Templates", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [character, mood, limit])!;

    static string[] DialogueFill(string[] templates, string? w, string? r) =>
        (string[])typeof(MoodTracker).GetMethod("Fill", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [templates, w, r])!;

    static void TestDialogue()
    {
        var all = DialogueCharacters.SelectMany(c => DialogueBranches.Select(b => (c, b.Name, Lines: DialogueTemplates(c, b.Mood, b.Limit)))).ToList();

        var empty = all.Where(x => x.Lines.Length == 0).Select(x => $"{x.c}/{x.Name}").ToList();
        Check("every character has dialogue for every mood branch", empty.Count == 0, string.Join(", ", empty));

        var placeholder = new Regex(@"\{[^}]*\}");
        var badPlaceholders = all.SelectMany(x => x.Lines.SelectMany(l => placeholder.Matches(l).Select(m => $"{x.c}/{x.Name}: {m.Value}")))
            .Where(s => !s.EndsWith("{w}") && !s.EndsWith("{r}")).ToList();
        Check("dialogue placeholders are only {w} and {r}", badPlaceholders.Count == 0, string.Join(", ", badPlaceholders));

        var fewPlain = all.Where(x => x.Lines.Count(l => !placeholder.IsMatch(l)) < 2).Select(x => $"{x.c}/{x.Name}").ToList();
        Check("every branch has at least two lines without placeholders", fewPlain.Count == 0, string.Join(", ", fewPlain));

        // 값을 모르면 그 자리표시자가 든 대사는 빠지고, 남은 대사에 '?' 나 자리표시자가 새지 않는다
        var leaks = new List<string>();
        foreach (var x in all)
        {
            foreach (var (w, r) in new[] { ((string?)null, (string?)null), ("73%", null), (null, "2시간 15분") })
            {
                var filled = DialogueFill(x.Lines, w, r);
                var expected = x.Lines.Count(l => (w != null || !l.Contains("{w}")) && (r != null || !l.Contains("{r}")));
                if (filled.Length != expected || filled.Length < 2 || filled.Any(l => l.Contains('{') || l.Contains("?…") || l.Contains("까지 ?")))
                    leaks.Add($"{x.c}/{x.Name} w={w ?? "-"} r={r ?? "-"}: {string.Join(" | ", filled)}");
            }
            var known = DialogueFill(x.Lines, "73%", "2시간 15분");
            if (known.Length != x.Lines.Length || known.Any(l => l.Contains('{'))) leaks.Add($"{x.c}/{x.Name}: known values not filled");
        }
        Check("unknown usage or reset time never prints '?' or a raw placeholder", leaks.Count == 0, string.Join("\n", leaks.Take(5)));

        // 실제 MoodTracker 경로: 한도 도달인데 사용률·리셋 시각을 모르는 카드
        var unknownCard = new ProviderCard
        {
            Name = "fixture", Accent = Palette.Claude, AppStatus = "x", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "",
            LimitReached = true,
        };
        var trackerLeaks = DialogueCharacters.Select(c => (c, r: new MoodTracker(c).Update(unknownCard)))
            .Where(x => x.r.Mood != Mood.Panic || x.r.Lines.Length < 2 || x.r.Lines.Any(l => l.Contains('?') || l.Contains('{')))
            .Select(x => $"{x.c}: {x.r.Mood} {string.Join(" | ", x.r.Lines)}").ToList();
        Check("limit panic with unknown values shows only complete sentences", trackerLeaks.Count == 0, string.Join("\n", trackerLeaks));

        // 애칭: 클·짚·젬 + U+CA29. 잘못된 글자(U+CAA1)와 예전 표기(U+C9F1)는 없다
        var text = string.Join("\n", all.SelectMany(x => x.Lines));
        var nicknames = Regex.Matches(text, "[클짚젬](.)").Select(m => m.Groups[1].Value[0])
            .Where(c => c is NicknameEnd or WrongNicknameEnd or OldNicknameEnd).ToList();
        Check("dialogue nicknames use U+CA29 and never U+CAA1",
            !text.Contains(WrongNicknameEnd) && !text.Contains(OldNicknameEnd) && nicknames.Count > 0 && nicknames.All(c => c == NicknameEnd),
            $"U+CAA1={text.Count(c => c == WrongNicknameEnd)}, U+C9F1={text.Count(c => c == OldNicknameEnd)}, nicknames={nicknames.Count}");
        Check("Gemini dialogue mentions the other two nicknames",
            DialogueBranches.SelectMany(b => DialogueTemplates("gemini", b.Mood, b.Limit))
                .Any(l => l.Contains($"클{NicknameEnd}") && l.Contains($"짚{NicknameEnd}")), "no 클·짚 nickname in Gemini lines");

        // 말풍선: 글자 폭 120 DIP, 13px 굵은 글꼴에서 3줄 이하이고 글자를 줄이지 않는다 (CharacterStage 의 실제 줄바꿈 사용)
        var stage = new CharacterStage { Width = DialogueBubbleWidth + 20 };
        stage.Measure(new Size(DialogueBubbleWidth + 20, 280));
        stage.Arrange(new Rect(0, 0, DialogueBubbleWidth + 20, 280));
        var show = PrivateMethod<CharacterStage>("ShowBubble");
        var bubbleText = (TextBlock)PrivateField<CharacterStage>("_bubbleText").GetValue(stage)!;
        var typeface = new Typeface(bubbleText.FontFamily, bubbleText.FontStyle, bubbleText.FontWeight, bubbleText.FontStretch);
        var tooLong = new List<string>();
        int measured = 0;
        foreach (var x in all)
        {
            foreach (var line in DialogueFill(x.Lines, "100%", "23시간 59분"))
            {
                show.Invoke(stage, [line]);
                var rows = bubbleText.Text.Split('\n');
                double width = rows.Max(row => new FormattedText(row, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, bubbleText.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace);
                measured++;
                if (rows.Length > 3 || bubbleText.FontSize != 13 || width > DialogueBubbleWidth + 0.01)
                    tooLong.Add($"{x.c}/{x.Name} [{rows.Length} lines, {bubbleText.FontSize}px, {width:0.#}] {line.Replace("\n", "⏎")}");
            }
        }
        Check("every line fits a 120 DIP bubble in three lines at 13px", tooLong.Count == 0 && measured > 200,
            $"measured={measured}\n" + string.Join("\n", tooLong));
    }
}
