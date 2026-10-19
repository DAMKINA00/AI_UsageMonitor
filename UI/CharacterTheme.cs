using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows.Media;

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

    /// <summary>모르는 이름은 클쨩 테마로 대체한다 (기존 동작과 같다).</summary>
    public static CharacterTheme For(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Claude;

    public static string AssetPath(string key, string pose) => $"Assets/Characters/{key}_{pose}.png";

    public static Uri AssetUri(string key, string pose) =>
        new($"pack://application:,,,/AIUsageMonitor;component/{AssetPath(key, pose)}");

    /// <summary>포즈 이미지가 앱 리소스에 들어 있는지. 없는 리소스를 열어 예외가 나지 않게 미리 확인한다.</summary>
    public static bool HasAsset(string key, string pose) => ResourceNames.Value.Contains(AssetPath(key, pose).ToLowerInvariant());

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
