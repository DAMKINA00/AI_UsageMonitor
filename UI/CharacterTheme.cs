using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIUsageMonitor.UI;

/// <summary>
/// 캐릭터별 이름과 연출 색. 포즈 이미지는 Assets/Characters/{Key}_{pose}.png 규칙을 따른다.
/// 클쨩(Claude)은 연갈색, 짚쨩(Codex)은 형광 시안, 젬쨩(Gemini)은 코랄·핑크·청보라·틸 계열.
/// </summary>
public sealed class CharacterTheme
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>"+N%" 글자와 반짝이의 외곽선.</summary>
    public required Brush FxStroke { get; init; }
    /// <summary>리셋 반짝이 색 (번갈아 쓴다).</summary>
    public required Color[] Sparkles { get; init; }

    public static readonly CharacterTheme Claude = new()
    {
        Key = "claude", DisplayName = "클쨩",
        FxStroke = Frozen(Color.FromRgb(0x6B, 0x47, 0x2E)),
        Sparkles = [Color.FromRgb(0xEE, 0xCF, 0xA6), Colors.White, Color.FromRgb(0xFF, 0xD9, 0x7A), Color.FromRgb(0xF6, 0xB8, 0x9E)],
    };

    public static readonly CharacterTheme Codex = new()
    {
        Key = "codex", DisplayName = "짚쨩",
        FxStroke = Frozen(Color.FromRgb(0x08, 0x2A, 0x36)),
        Sparkles = [Color.FromRgb(0x2B, 0xF6, 0xFF), Colors.White, Color.FromRgb(0x9A, 0xFF, 0xF8), Color.FromRgb(0x4D, 0xA8, 0xFF)],
    };

    // 젬쨩 팔레트: 머리끝 코랄 #F4A783 · 핑크 #F4677A · 청보라 #554FB5, 리본 틸 #1C99A0, 스커트 남색 #243671
    public static readonly CharacterTheme Gemini = new()
    {
        Key = "gemini", DisplayName = "젬쨩",
        FxStroke = Frozen(Color.FromRgb(0x24, 0x36, 0x71)),
        Sparkles = [Color.FromRgb(0xF4, 0xA7, 0x83), Color.FromRgb(0xF4, 0x67, 0x7A), Colors.White, Color.FromRgb(0x55, 0x4F, 0xB5), Color.FromRgb(0x1C, 0x99, 0xA0)],
    };

    public static readonly CharacterTheme Grok = new()
    {
        Key = "grok", DisplayName = "그쨩",
        FxStroke = Frozen(Color.FromRgb(0x38, 0x32, 0x2D)),
        Sparkles = [Color.FromRgb(0xF2, 0xD0, 0x83), Colors.White, Color.FromRgb(0xA0, 0xAF, 0xC0)],
    };

    public static IReadOnlyList<CharacterTheme> All { get; } = [Claude, Codex, Gemini, Grok];

    // 표정 원화(ChatterBox와 같은 5캐릭터)에만 있는 마스터와 GPT. 연출 색은 ChatterBox 원화의 색을 따른다.
    public static readonly CharacterTheme Orch = new()
    {
        Key = "orch", DisplayName = "마스터",
        FxStroke = Frozen(Color.FromRgb(0x5A, 0x34, 0x24)),
        Sparkles = [Color.FromRgb(0xF6, 0xB0, 0x94), Colors.White, Color.FromRgb(0xF2, 0xE6, 0xC8), Color.FromRgb(0x9C, 0xB0, 0x7C)],
    };

    public static readonly CharacterTheme Gpt = new()
    {
        Key = "gpt", DisplayName = "지피티",
        FxStroke = Frozen(Color.FromRgb(0x08, 0x2A, 0x36)),
        Sparkles = [Color.FromRgb(0x2B, 0xC8, 0xF0), Colors.White, Color.FromRgb(0xC8, 0xC8, 0xE8), Color.FromRgb(0x4D, 0xA8, 0xFF)],
    };

    public static readonly CharacterTheme DeepSeek = new()
    {
        Key = "deepseek", DisplayName = "딥시크",
        FxStroke = Frozen(Color.FromRgb(0x1E, 0x2A, 0x5A)),
        Sparkles = [Color.FromRgb(0x4D, 0x6B, 0xFE), Colors.White, Color.FromRgb(0xA8, 0xB8, 0xFF)],
    };

    /// <summary>모니터에 세우는 여섯 캐릭터의 테마 (CharacterSelection.All 순서).</summary>
    public static IReadOnlyList<CharacterTheme> Faces { get; } = [Orch, Claude, Gpt, Gemini, Grok, DeepSeek];

    /// <summary>모르는 이름은 클쨩 테마로 대체한다 (기존 동작과 같다).</summary>
    public static CharacterTheme For(string? key) =>
        All.Concat([Orch, Gpt, DeepSeek]).FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Claude;

    /// <summary>파츠 원화의 레이어 이름. 모두 256×256, 0,0 정렬이고 body → 눈 → 눈썹 → 입 순서로 겹친다.</summary>
    public static IReadOnlyList<string> Parts { get; } =
        ["body", "eyes-open", "eyes-half", "eyes-closed", "brows-neutral", "brows-thinking", "mouth-closed", "mouth-open", "mouth-small"];

    /// <summary>합성용 파츠: Assets/CharacterActions/{key}/{part}.png.</summary>
    public static string PartPath(string key, string part) => $"Assets/CharacterActions/{key}/{part}.png";

    public static Uri PartUri(string key, string part) => PackUri(PartPath(key, part));

    /// <summary>이 캐릭터의 파츠 9장이 모두 리소스에 있는지 (하나라도 없으면 표정 원화로 대신한다).</summary>
    public static bool HasParts(string key) => Parts.All(part => HasPart(key, part));

    public static bool HasPart(string key, string part) => HasResource(PartPath(key, part));

    public static string AssetPath(string key, string pose) => $"Assets/Characters/{key}_{pose}.png";

    public static Uri AssetUri(string key, string pose) => PackUri(AssetPath(key, pose));

    /// <summary>포즈 이미지가 앱 리소스에 들어 있는지. 없는 리소스를 열어 예외가 나지 않게 미리 확인한다.</summary>
    public static bool HasAsset(string key, string pose) => HasResource(AssetPath(key, pose));

    /// <summary>ChatterBox에서 가져온 표정 원화: Assets/Faces/{key}/{expression}.png (art/import_chatterbox_faces.ps1).</summary>
    public static string FacePath(string key, string expression) => $"Assets/Faces/{key}/{expression}.png";

    public static Uri FaceUri(string key, string expression) => PackUri(FacePath(key, expression));

    public static bool HasFace(string key, string expression) => HasResource(FacePath(key, expression));

    public static ImageSource? ConversationPortrait(string key, bool speaking = false, ImageSource? stage = null)
    {
        string expression = speaking ? "speaking" : "neutral";
        if (HasFace(key, expression)) return new BitmapImage(FaceUri(key, expression));
        if (!speaking && stage != null) return stage;
        if (!HasParts(key)) return stage;
        string[] parts = ["body", "eyes-open", "brows-neutral", speaking ? "mouth-open" : "mouth-closed"];
        return Composite(parts.Select(part => new BitmapImage(PartUri(key, part))).ToArray());
    }

    internal static BitmapSource Composite(IReadOnlyList<BitmapSource> layers)
    {
        var body = layers[0];
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            foreach (var layer in layers)
                dc.DrawImage(layer, new Rect(0, 0, body.PixelWidth, body.PixelHeight));
        var result = new RenderTargetBitmap(body.PixelWidth, body.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    static Uri PackUri(string path) => new($"pack://application:,,,/AIUsageMonitor;component/{path}");

    static bool HasResource(string path) => ResourceNames.Value.Contains(path.ToLowerInvariant());

    static readonly Lazy<HashSet<string>> ResourceNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var assembly = typeof(CharacterTheme).Assembly;
            using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources");
            if (stream == null) return names;
            using var reader = new ResourceReader(stream);
            foreach (DictionaryEntry entry in reader)
                if (entry.Key is string name) names.Add(name.ToLower(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or BadImageFormatException)
        {
            // 목록을 못 읽으면 이미지가 없는 것으로 보고 캐릭터를 비워 둔다
        }
        return names;
    });

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
