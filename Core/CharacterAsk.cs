using System.Text.Json;

namespace AIUsageMonitor.Core;

/// <summary>The host's answer to a character question (hosted "character-reply").</summary>
public sealed record CharacterReply(string RequestId, string Character, string Status, string Text)
{
    public const int MaxTextLength = 12_000;
    public static IReadOnlyList<string> Statuses { get; } = ["answer", "busy", "error"];

    /// <summary>What the character says: the answer, or the host's note, or a default note for the status.</summary>
    public string Spoken => Text.Length > 0 ? Text : Status switch
    {
        "answer" => "답을 받지 못했어요.",
        "busy" => "지금은 다른 일을 하고 있어요. 조금 뒤에 물어봐 주세요.",
        _ => "질문을 전하지 못했어요.",
    };

    public static bool TryParse(JsonElement value, out CharacterReply reply, out string error)
    {
        reply = null!;
        error = "";
        if (value.ValueKind != JsonValueKind.Object) return Fail("character-reply requires an object value", out error);
        string? requestId = null, character = null, status = null, text = null;
        foreach (var p in value.EnumerateObject())
        {
            switch (p.Name)
            {
                case "requestId" when p.Value.ValueKind == JsonValueKind.String: requestId = p.Value.GetString(); break;
                case "character" when p.Value.ValueKind == JsonValueKind.String: character = p.Value.GetString(); break;
                case "status" when p.Value.ValueKind == JsonValueKind.String: status = p.Value.GetString(); break;
                case "text" when p.Value.ValueKind == JsonValueKind.String: text = p.Value.GetString(); break;
                default: return Fail($"invalid character-reply field: {p.Name}", out error);
            }
        }
        if (!CharacterAsk.IsRequestId(requestId)) return Fail("character-reply requestId must be 32 hex characters", out error);
        if (!CharacterSelection.IsKnown(character)) return Fail($"unknown character: {character}", out error);
        if (status == null || !Statuses.Contains(status)) return Fail($"unknown character-reply status: {status}", out error);
        var clean = (text ?? "").Trim();
        if (clean.Length > MaxTextLength) return Fail("character-reply text must be at most 12000 characters", out error);
        if (status == "answer" && clean.Length == 0) return Fail("an answer needs text", out error);
        reply = new CharacterReply(requestId!, character!, status, clean);
        return true;
    }

    static bool Fail(string message, out string error) { error = message; return false; }
}

/// <summary>
/// Questions asked by clicking a character: at most one waiting question per character, matched by request id,
/// given up after <see cref="Timeout"/> (a late reply is then ignored).
/// </summary>
public sealed class CharacterAsk
{
    public const int MaxQuestionLength = 4_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    readonly Dictionary<string, (string RequestId, DateTimeOffset At)> _pending = new(StringComparer.Ordinal);
    readonly CharacterEventDeduper _answered = new();

    public static bool IsRequestId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public bool IsWaiting(string character) => _pending.ContainsKey(character);

    /// <summary>Starts a question; null when this character is already waiting or the question is empty/too long.</summary>
    public string? Start(string character, string question, DateTimeOffset now)
    {
        if (!CharacterSelection.IsKnown(character) || _pending.ContainsKey(character)) return null;
        var text = question.Trim();
        if (text.Length == 0 || text.Length > MaxQuestionLength) return null;
        var id = Guid.NewGuid().ToString("N");
        _pending[character] = (id, now);
        return id;
    }

    /// <summary>true when the reply answers the question this character is waiting for: same request id and character (only once).</summary>
    public bool Complete(CharacterReply reply)
    {
        if (!_pending.TryGetValue(reply.Character, out var waiting) || waiting.RequestId != reply.RequestId) return false;
        if (!_answered.Accept(reply.RequestId)) return false;
        _pending.Remove(reply.Character);
        return true;
    }

    /// <summary>Characters whose question waited longer than <see cref="Timeout"/> (they stop waiting).</summary>
    public IReadOnlyList<string> Expire(DateTimeOffset now)
    {
        var expired = _pending.Where(p => now - p.Value.At >= Timeout).Select(p => p.Key).ToList();
        foreach (var character in expired) _pending.Remove(character);
        return expired;
    }
}
