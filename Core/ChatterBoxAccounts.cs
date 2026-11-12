using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core;

/// <summary>One usage window as ChatterBox sends it (used percent, reset time).</summary>
public sealed record HostedWindow(double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// One ChatterBox CLI account: id, label, enabled (assignment) and sign-in state, and the reading stored with
/// it. Nothing else comes from ChatterBox (no email, folder, token or key).
/// </summary>
public sealed record HostedAccount(
    string Id, string Provider, string Label, bool Enabled, bool IsDefault, string AuthStatus, int RunningWorkers,
    DateTimeOffset? CheckedAt, string? Error, HostedWindow? FiveHour, HostedWindow? Weekly, HostedWindow? Monthly);

public sealed record HostedDeepSeek(string State, string Balance, string? Error);

/// <summary>ChatterBox's account snapshot. In hosted mode this is the monitor's only account list and usage source.</summary>
public sealed record HostedAccountsSnapshot(DateTimeOffset SentAt, IReadOnlyList<HostedAccount> Accounts, HostedDeepSeek? DeepSeek)
{
    public IReadOnlyList<HostedAccount> For(string provider) => Accounts.Where(a => a.Provider == provider).ToList();
}

/// <summary>Reads the <c>chatterbox-accounts</c> hosted command (ChatterBox src/main/usage-monitor-feed.ts).</summary>
public static partial class ChatterBoxAccounts
{
    /// <summary>Major version this monitor reads; announced in the hosted state as <c>accountsVersion</c>.</summary>
    public const int Version = 1;
    public const int MaxAccounts = 60;

    static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["claude"] = "Claude", ["gpt"] = "Codex", ["grok"] = "Grok",
    };

    [GeneratedRegex(@"^(claude|gpt|grok)-([1-9]|10)$")]
    private static partial Regex AccountIdPattern();

    public static bool TryParse(JsonElement value, out HostedAccountsSnapshot snapshot, out string error)
    {
        snapshot = null!;
        error = "";
        try
        {
            if (value.ValueKind != JsonValueKind.Object) return Fail("account snapshot must be an object", out error);
            if (!value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != Version)
                return Fail($"unsupported account snapshot version (this monitor reads {Version})", out error);
            var sentAt = Time(value, "sentAt") ?? DateTimeOffset.Now;
            if (!value.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return Fail("account snapshot needs groups", out error);

            var accounts = new List<HostedAccount>();
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("accounts", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    return Fail("account group needs accounts", out error);
                foreach (var row in rows.EnumerateArray())
                {
                    if (accounts.Count == MaxAccounts) return Fail($"more than {MaxAccounts} accounts", out error);
                    if (!TryAccount(row, out var account, out error)) return false;
                    if (accounts.Any(a => a.Id == account.Id)) return Fail($"duplicate account {account.Id}", out error);
                    accounts.Add(account);
                }
            }

            HostedDeepSeek? deepseek = null;
            if (value.TryGetProperty("deepseek", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                var balances = d.TryGetProperty("balances", out var b) && b.ValueKind == JsonValueKind.Array
                    ? string.Join(" · ", b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                        .Select(x => $"{Text(x, "total", 32)} {Text(x, "currency", 8)}".Trim()).Where(x => x.Length > 0))
                    : "";
                deepseek = new HostedDeepSeek(Text(d, "state", 20) ?? "unknown", balances, Text(d, "error", 300));
            }

            snapshot = new HostedAccountsSnapshot(sentAt, accounts, deepseek);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Fail("account snapshot has a value of the wrong type", out error);
        }
    }

    static bool TryAccount(JsonElement row, out HostedAccount account, out string error)
    {
        account = null!;
        if (row.ValueKind != JsonValueKind.Object) return Fail("account must be an object", out error);
        var id = Text(row, "accountId", 20);
        if (id == null || !AccountIdPattern().IsMatch(id)) return Fail("account has no valid accountId", out error);
        var provider = Providers[id[..id.IndexOf('-')]];
        var label = Text(row, "label", 80);
        if (string.IsNullOrWhiteSpace(label)) label = id;
        HostedWindow? fiveHour = null, weekly = null, monthly = null;
        if (row.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            fiveHour = Window(windows, "fiveHour");
            weekly = Window(windows, "weekly");
            monthly = Window(windows, "monthly");
        }
        account = new HostedAccount(id, provider, label, Bool(row, "enabled", true), Bool(row, "isDefault", false),
            Text(row, "authStatus", 20) ?? "unknown",
            row.TryGetProperty("runningWorkers", out var w) && w.ValueKind == JsonValueKind.Number ? Math.Clamp(w.GetInt32(), 0, 99) : 0,
            Time(row, "checkedAt"), Text(row, "error", 300), fiveHour, weekly, monthly);
        error = "";
        return true;
    }

    static HostedWindow? Window(JsonElement windows, string name)
    {
        if (!windows.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        var value = used.GetDouble();
        if (!double.IsFinite(value)) return null;
        return new HostedWindow(Math.Clamp(value, 0, 100), Time(w, "resetsAt"));
    }

    /// <summary>Seconds or milliseconds since the epoch (ChatterBox passes provider values through).</summary>
    static DateTimeOffset? Time(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Number) return null;
        var v = t.GetDouble();
        if (!double.IsFinite(v) || v <= 0) return null;
        var ms = v > 1e12 ? v : v * 1000;
        return ms > 4e12 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime();
    }

    static string? Text(JsonElement o, string name, int max) =>
        o.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() is { } s ? s.Length > max ? s[..max] : s : null : null;

    static bool Bool(JsonElement o, string name, bool fallback) =>
        o.TryGetProperty(name, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : fallback;

    /// <summary>The account's reading as the monitor's usage model (window keys the presenters already know).</summary>
    public static UsageSnapshot? Usage(HostedAccount account)
    {
        if (account.Error != null || account.CheckedAt is not { } asOf) return null;
        var windows = new List<UsageWindow>();
        if (account.FiveHour is { } five) windows.Add(new("five_hour", "5시간 한도", five.UsedPercent, five.ResetsAt));
        if (account.Weekly is { } week) windows.Add(new("seven_day", "주간 한도", week.UsedPercent, week.ResetsAt));
        if (account.Monthly is { } month) windows.Add(new("monthly", "월간 한도", month.UsedPercent, month.ResetsAt));
        return windows.Count == 0 ? null : new UsageSnapshot { Windows = windows, AsOf = asOf, Source = "ChatterBox" };
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
