using System.IO;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed record HostedCommand(string Name, bool? BoolValue = null, string? GeminiPlan = null);

public readonly record struct HostedInputLine(string? Text, string? Error)
{
    public static HostedInputLine FromText(string text) => new(text, null);
    public static HostedInputLine FromError(string error) => new(null, error);
}

public static class HostedProtocol
{
    public const int MaxLineBytes = 8 * 1024;

    static readonly HashSet<string> NoValueCommands = new(StringComparer.Ordinal)
    {
        "show", "hide", "refresh", "accounts", "claude-login", "claude-logout",
        "gemini-unblock", "data-folder", "quit",
    };

    static readonly HashSet<string> BoolCommands = new(StringComparer.Ordinal)
    {
        "topmost", "characters", "taskbar", "startup", "usage-alerts",
        "activity-alerts", "claude-desktop",
    };

    public static bool TryParseCommand(string line, out HostedCommand command, out string error)
    {
        command = null!;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("command must be a JSON object", out error);

            string? name = null;
            JsonElement value = default;
            bool hasValue = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "command" or "type")
                {
                    if (name != null) return Fail("command must contain one command/type field", out error);
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Fail("command must be a string", out error);
                    name = property.Value.GetString();
                }
                else if (property.Name == "value")
                {
                    if (hasValue) return Fail("command must contain one value field", out error);
                    value = property.Value;
                    hasValue = true;
                }
                else return Fail($"unknown command field: {property.Name}", out error);
            }

            if (string.IsNullOrEmpty(name)) return Fail("command is required", out error);
            if (NoValueCommands.Contains(name))
            {
                if (hasValue) return Fail($"command '{name}' does not accept value", out error);
                command = new HostedCommand(name);
                return true;
            }

            if (BoolCommands.Contains(name))
            {
                if (!hasValue || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail($"command '{name}' requires a boolean value", out error);
                command = new HostedCommand(name, value.GetBoolean());
                return true;
            }

            if (name == "gemini-plan")
            {
                if (!hasValue || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    return Fail("command 'gemini-plan' requires null or a supported plan string", out error);
                var plan = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                if (plan is not (null or "Free" or "Google AI Pro" or "Google AI Ultra"))
                    return Fail("command 'gemini-plan' has an unsupported plan", out error);
                command = new HostedCommand(name, GeminiPlan: plan);
                return true;
            }

            return Fail($"unknown command: {name}", out error);
        }
        catch (JsonException)
        {
            return Fail("command is not valid JSON", out error);
        }
    }

    public static async Task ReadLinesAsync(Stream input, Action<HostedInputLine> onLine,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        var line = new List<byte>(256);
        bool tooLong = false;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\n')
                {
                    onLine(Decode(line, tooLong));
                    line.Clear();
                    tooLong = false;
                }
                else if (!tooLong)
                {
                    if (line.Count == MaxLineBytes) tooLong = true;
                    else line.Add(b);
                }
            }
        }

        if (tooLong || line.Count > 0) onLine(Decode(line, tooLong));
    }

    static HostedInputLine Decode(List<byte> bytes, bool tooLong)
    {
        if (tooLong) return HostedInputLine.FromError($"command line exceeds {MaxLineBytes} bytes");
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
        try { return HostedInputLine.FromText(new UTF8Encoding(false, true).GetString(bytes.ToArray())); }
        catch (DecoderFallbackException) { return HostedInputLine.FromError("command line is not valid UTF-8"); }
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

public sealed class HostedProtocolWriter(TextWriter writer)
{
    readonly object _gate = new();

    public void State(object state) => Write(new { type = "state", state });

    public void Notification(string title, string message, bool error) =>
        Write(new { type = "notification", title, message, error });

    public void Error(string message) => Write(new { type = "error", message });

    void Write(object value)
    {
        try
        {
            lock (_gate)
            {
                writer.WriteLine(JsonSerializer.Serialize(value));
                writer.Flush();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
