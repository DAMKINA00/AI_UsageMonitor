using System.IO;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void HostedCheck(string name, bool condition) => Check(name, condition, "unexpected result");

    static int RunHostedProtocolTests()
    {
        Run("hosted command validation", TestHostedCommandValidation);
        Run("hosted input line limits", () => TestHostedInputLines().GetAwaiter().GetResult());
        Run("hosted protocol output", TestHostedOutput);
        Console.WriteLine($"Totals: {s_assertions} assertions, {s_failures} failures.");
        return s_failures == 0 ? 0 : 1;
    }

    static void TestHostedCommandValidation()
    {
        HostedCheck("show command parses", HostedProtocol.TryParseCommand("{\"command\":\"show\"}", out var show, out _) && show.Name == "show");
        HostedCheck("type alias parses", HostedProtocol.TryParseCommand("{\"type\":\"hide\"}", out var hide, out _) && hide.Name == "hide");
        HostedCheck("boolean command parses", HostedProtocol.TryParseCommand("{\"command\":\"topmost\",\"value\":true}", out var topmost, out _) && topmost.BoolValue == true);
        HostedCheck("gemini plan null parses", HostedProtocol.TryParseCommand("{\"command\":\"gemini-plan\",\"value\":null}", out var automatic, out _) && automatic.GeminiPlan == null);
        HostedCheck("supported gemini plan parses", HostedProtocol.TryParseCommand("{\"command\":\"gemini-plan\",\"value\":\"Google AI Pro\"}", out var pro, out _) && pro.GeminiPlan == "Google AI Pro");
        HostedCheck("wrong boolean type is rejected", !HostedProtocol.TryParseCommand("{\"command\":\"topmost\",\"value\":\"true\"}", out _, out var boolError) && boolError.Contains("boolean", StringComparison.Ordinal));
        HostedCheck("unknown command is rejected", !HostedProtocol.TryParseCommand("{\"command\":\"restart\"}", out _, out var unknownError) && unknownError.Contains("unknown", StringComparison.Ordinal));
        HostedCheck("unexpected field is rejected", !HostedProtocol.TryParseCommand("{\"command\":\"show\",\"extra\":1}", out _, out var fieldError) && fieldError.Contains("field", StringComparison.Ordinal));
        HostedCheck("unsupported plan is rejected", !HostedProtocol.TryParseCommand("{\"command\":\"gemini-plan\",\"value\":\"Ultra\"}", out _, out var planError) && planError.Contains("unsupported", StringComparison.Ordinal));
    }

    static async Task TestHostedInputLines()
    {
        var lines = new List<HostedInputLine>();
        await HostedProtocol.ReadLinesAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"command\":\"show\"}\r\n{\"command\":\"quit\"}")), lines.Add);
        HostedCheck("CRLF and final line are read", lines.Count == 2 && lines[0].Text?.EndsWith("show\"}") == true && lines[1].Text?.EndsWith("quit\"}") == true);

        lines.Clear();
        await HostedProtocol.ReadLinesAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', HostedProtocol.MaxLineBytes + 1) + "\n")), lines.Add);
        HostedCheck("oversized line is rejected", lines.Count == 1 && lines[0].Error?.Contains(HostedProtocol.MaxLineBytes.ToString(), StringComparison.Ordinal) == true);
    }

    static void TestHostedOutput()
    {
        using var output = new StringWriter();
        var writer = new HostedProtocolWriter(output);
        writer.State(new { visible = false });
        writer.Notification("title", "message", true);
        writer.Error("bad command");
        var rows = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => JsonDocument.Parse(value)).Select(x => x.RootElement.Clone()).ToArray();
        HostedCheck("output has three JSON protocol rows", rows.Length == 3);
        HostedCheck("output types are stable", rows.Select(x => x.GetProperty("type").GetString()).SequenceEqual(["state", "notification", "error"]));
        HostedCheck("notification uses error flag", rows[1].GetProperty("error").GetBoolean());
    }
}
