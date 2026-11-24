using System.Text.Json;

namespace AIUsageMonitor.Core;

/// <summary>
/// Which of the six characters (Orch = the master, Claude, GPT, Gemini, Grok, DeepSeek) stand on the monitor.
/// Each one is switched on or off on its own; lists are always in <see cref="All"/> order without duplicates.
/// </summary>
public static class CharacterSelection
{
    public const string Orch = "orch", Claude = "claude", Gpt = "gpt", Gemini = "gemini", Grok = "grok", DeepSeek = "deepseek";

    public static IReadOnlyList<string> All { get; } = [Orch, Claude, Gpt, Gemini, Grok, DeepSeek];

    /// <summary>What the single "캐릭터 표시" switch showed before: the four provider characters (no Orch).</summary>
    public static IReadOnlyList<string> Legacy { get; } = [Claude, Gpt, Gemini, Grok];

    public static bool IsKnown(string? id) => id != null && All.Contains(id, StringComparer.Ordinal);

    /// <summary>Known ids only, canonical order, no duplicates.</summary>
    public static List<string> Normalize(IEnumerable<string?>? ids)
    {
        var picked = new HashSet<string>(ids?.Where(IsKnown).Select(id => id!) ?? [], StringComparer.Ordinal);
        return All.Where(picked.Contains).ToList();
    }

    /// <summary>Turns one character on or off; the others stay as they are.</summary>
    public static List<string> Set(IEnumerable<string> current, string id, bool on)
    {
        if (!IsKnown(id)) throw new ArgumentException($"unknown character: {id}", nameof(id));
        var list = Normalize(current);
        if (on) list.Add(id);
        else list.Remove(id);
        return Normalize(list);
    }

    /// <summary>Value of the hosted "character" command: <c>{"id":"gpt","on":false}</c>.</summary>
    public static bool TryParseCommand(JsonElement value, out string id, out bool on, out string error)
    {
        id = "";
        on = false;
        error = "";
        if (value.ValueKind != JsonValueKind.Object) return Fail("character command requires an object value", out error);
        bool hasId = false, hasOn = false;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id" when property.Value.ValueKind == JsonValueKind.String && !hasId:
                    id = property.Value.GetString()!;
                    hasId = true;
                    break;
                case "on" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False && !hasOn:
                    on = property.Value.GetBoolean();
                    hasOn = true;
                    break;
                default:
                    return Fail($"invalid character field: {property.Name}", out error);
            }
        }
        if (!hasId || !hasOn) return Fail("character command requires id and on", out error);
        if (!IsKnown(id)) return Fail($"unknown character: {id}", out error);
        return true;
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
