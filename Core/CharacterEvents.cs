using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

/// <summary>
/// One thing a character says, pushed by ChatterBox (hosted "character-event" command):
/// <c>{"id":"…","character":"orch","kind":"assign","text":"…","chatId":1,"consultId":2}</c>.
/// The text is already a single cleaned line; the monitor shows it once in that character's bubble.
/// </summary>
public sealed record CharacterEvent(string Id, string Character, string Kind, bool? Ok, string Text, long ChatId, long? ConsultId)
{
    public const int MaxIdLength = 64, MaxTextLength = 120;

    /// <summary>assign: Orch names who got the work · result: that character reports (ok) · approval/error/done: Orch.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["assign", "result", "approval", "error", "done"];

    /// <summary>Needs the user's attention even when no bubble can be shown (OS / host notification).</summary>
    public bool NeedsNotification => Kind is "approval" or "error";

    public static bool TryParse(JsonElement value, out CharacterEvent evt, out string error)
    {
        evt = null!;
        error = "";
        if (value.ValueKind != JsonValueKind.Object) return Fail("character-event requires an object value", out error);
        string? id = null, character = null, kind = null, text = null;
        bool? ok = null;
        long? chatId = null, consultId = null;
        foreach (var p in value.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id" when p.Value.ValueKind == JsonValueKind.String: id = p.Value.GetString(); break;
                case "character" when p.Value.ValueKind == JsonValueKind.String: character = p.Value.GetString(); break;
                case "kind" when p.Value.ValueKind == JsonValueKind.String: kind = p.Value.GetString(); break;
                case "text" when p.Value.ValueKind == JsonValueKind.String: text = p.Value.GetString(); break;
                case "ok" when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False: ok = p.Value.GetBoolean(); break;
                case "chatId" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var c): chatId = c; break;
                case "consultId" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var k): consultId = k; break;
                case "consultId" when p.Value.ValueKind == JsonValueKind.Null: break;
                default: return Fail($"invalid character-event field: {p.Name}", out error);
            }
        }
        if (id is not { Length: > 0 and <= MaxIdLength }) return Fail("character-event id must be 1-64 characters", out error);
        if (!CharacterSelection.IsKnown(character)) return Fail($"unknown character: {character}", out error);
        if (kind == null || !Kinds.Contains(kind)) return Fail($"unknown character-event kind: {kind}", out error);
        if (ok != null && kind != "result") return Fail("ok is only for result events", out error);
        if (chatId == null) return Fail("character-event requires chatId", out error);
        var clean = Clean(text);
        if (clean.Length == 0 || clean.Length > MaxTextLength) return Fail("character-event text must be 1-120 characters", out error);
        evt = new CharacterEvent(id, character!, kind, ok, clean, chatId.Value, consultId);
        return true;
    }

    /// <summary>One line without control characters (the host already cleans it; this only guards the screen).</summary>
    static string Clean(string? text)
    {
        if (text == null) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

/// <summary>Remembers the last event ids so a repeated event is shown only once.</summary>
public sealed class CharacterEventDeduper(int capacity = 256)
{
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    /// <summary>true the first time an id is seen.</summary>
    public bool Accept(string id)
    {
        if (!_seen.Add(id)) return false;
        _order.Enqueue(id);
        while (_order.Count > capacity) _seen.Remove(_order.Dequeue());
        return true;
    }
}
