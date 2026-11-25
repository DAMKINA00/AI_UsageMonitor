using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed record MeetingMessage(string Id, long At, string Sender, string Text, string? Kind = null)
{
    public bool IsValid(bool draft) => Id is { Length: > 0 and <= 100 } && At is > 0 and < 253402300799999L
        && Text is { Length: <= CharacterReply.MaxTextLength } && (draft || Text.Length > 0)
        && (Sender is "user" or "system" || CharacterSelection.All.Contains(Sender));
}

public sealed record MeetingReset(long Seq, string? RequestId, string Status, string[] Connected);
public sealed record MeetingRestore(MeetingMessage Message, bool Draft);

public sealed record MeetingEvent(string RequestId, long Seq, string Type, string? Text, MeetingMessage? Message, string[]? Connected, string? Id)
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static MeetingEvent? Read(JsonElement value)
    {
        var item = Decode<MeetingEvent>(value);
        if (item == null || !CharacterAsk.IsRequestId(item.RequestId)) return null;
        return item.Type switch
        {
            "roster" when IsRoster(item.Connected) => item,
            "status" or "end" when item.Text is { Length: <= 500 } => item,
            "message" or "draft" when item.Message?.IsValid(item.Type == "draft") == true => item,
            "withdraw" when item.Id is { Length: > 0 and <= 100 } => item,
            _ => null,
        };
    }

    public static bool IsRoster(string[]? ids) => ids is { Length: <= 6 } && ids.All(id => CharacterSelection.All.Contains(id));

    public static T? Decode<T>(JsonElement value) where T : class
    {
        try
        {
            return value.Deserialize<T>(JsonOptions);
        }
        catch (JsonException) { return null; }
    }
}
