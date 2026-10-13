using System.Reflection;
using System.Text;
using System.IO;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestAccountSwapAndCodexSubagents()
    {
        TestSecondaryAccountSwap();
        TestAccountUsageSlotRestrictions();
        TestCodexSubagentFiltering();
    }

    static void TestSecondaryAccountSwap()
    {
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var codexId = Guid.NewGuid().ToString("N");
        var settings = new AppSettings
        {
            SecondaryAccounts =
            [
                new() { Id = firstId, Provider = AccountProvider.Claude },
                new() { Id = secondId, Provider = AccountProvider.Claude },
                new() { Id = codexId, Provider = AccountProvider.Codex },
            ],
        };

        using var service = new SecondaryAccountService(settings);
        var apply = typeof(SecondaryAccountService).GetMethod("ApplyResult", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SecondaryAccountService), "ApplyResult");
        var reset = DateTimeOffset.UtcNow.AddDays(2);
        var firstUsage = Snapshot("first", DateTimeOffset.UtcNow.AddMinutes(-2), Window("seven_day", 17, reset));
        var secondUsage = Snapshot("second", DateTimeOffset.UtcNow.AddMinutes(-1), Window("seven_day", 83, reset));
        apply.Invoke(null, [service.Accounts[0], true, false, "first@example.test", firstUsage, null]);
        apply.Invoke(null, [service.Accounts[1], true, false, "second@example.test", secondUsage, null]);

        var firstState = service.Accounts[0];
        var secondState = service.Accounts[1];
        var changed = 0;
        service.Changed += () => changed++;
        var swapped = service.SwapAccounts(firstId, secondId);
        Check("same-provider account swap preserves object identity and usage",
            swapped && ReferenceEquals(service.Accounts[0], secondState) && ReferenceEquals(service.Accounts[1], firstState) &&
            service.Accounts[0].AccountName == "second@example.test" && service.Accounts[0].Usage?.Windows[0].UsedPercent == 83 &&
            service.Accounts[1].AccountName == "first@example.test" && service.Accounts[1].Usage?.Windows[0].UsedPercent == 17 &&
            changed == 1,
            $"swapped={swapped}, order={string.Join(',', service.Accounts.Select(x => x.Settings.Id))}, changed={changed}");

        var serialized = JsonSerializer.Serialize(settings.SecondaryAccounts);
        var roundTripSettings = new AppSettings
        {
            SecondaryAccounts = JsonSerializer.Deserialize<List<SecondaryAccountSettings>>(serialized) ?? [],
        };
        using var roundTrip = new SecondaryAccountService(roundTripSettings);
        Check("serialized settings restore swapped order in a new service",
            roundTrip.Accounts.Select(x => x.Settings.Id).SequenceEqual([secondId, firstId, codexId]) &&
            roundTrip.Accounts[0].Settings.Provider == AccountProvider.Claude &&
            roundTrip.Accounts[2].Settings.Provider == AccountProvider.Codex,
            string.Join(',', roundTrip.Accounts.Select(x => $"{x.Settings.Id}:{x.Settings.Provider}")));

        var orderBeforeRejects = service.Accounts.Select(x => x.Settings.Id).ToArray();
        var rejects = !service.SwapAccounts(firstId, codexId) &&
            !service.SwapAccounts(firstId, Guid.NewGuid().ToString("N")) &&
            !service.SwapAccounts(firstId, firstId);
        Check("swap rejects cross-provider, missing, and self targets",
            rejects && orderBeforeRejects.SequenceEqual(service.Accounts.Select(x => x.Settings.Id)) && changed == 1,
            $"rejects={rejects}, order={string.Join(',', service.Accounts.Select(x => x.Settings.Id))}, changed={changed}");

        var disposed = new SecondaryAccountService(new AppSettings
        {
            SecondaryAccounts =
            [
                new() { Id = Guid.NewGuid().ToString("N"), Provider = AccountProvider.Claude },
                new() { Id = Guid.NewGuid().ToString("N"), Provider = AccountProvider.Claude },
            ],
        });
        var disposedFirst = disposed.Accounts[0].Settings.Id;
        var disposedSecond = disposed.Accounts[1].Settings.Id;
        disposed.Dispose();
        Check("disposed account service rejects swaps", !disposed.SwapAccounts(disposedFirst, disposedSecond),
            "disposed service accepted a swap");
    }

    static void TestAccountUsageSlotRestrictions()
    {
        var primary = new AccountUsageSlot { Role = "메인", Provider = "Claude", Account = "main" };
        var first = new AccountUsageSlot { Role = "서브1", Id = "claude-a", Provider = "Claude", Account = "a" };
        var second = new AccountUsageSlot { Role = "서브2", Id = "claude-b", Provider = "Claude", Account = "b" };
        var codex = new AccountUsageSlot { Role = "서브1", Id = "codex-a", Provider = "Codex", Account = "c" };
        var blank = new AccountUsageSlot { Role = "서브2", Id = "blank", Provider = "Claude", IsEmpty = true };

        Check("account slots restrict dragging and swaps to populated same-provider secondaries",
            !primary.CanDrag && first.CanDrag && !blank.CanDrag && first.CanSwapWith(second) &&
            !first.CanSwapWith(codex) && !first.CanSwapWith(blank) && !first.CanSwapWith(first),
            $"drag={primary.CanDrag}/{first.CanDrag}/{blank.CanDrag}, swap={first.CanSwapWith(second)}");
    }

    static void TestCodexSubagentFiltering()
    {
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var fixtureHome = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        var sessions = Path.Combine(fixtureHome, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(fixtureHome, "session_index.jsonl"), "");
        Environment.SetEnvironmentVariable("CODEX_HOME", fixtureHome);

        try
        {
            var now = DateTime.UtcNow;
            var childId = Guid.NewGuid().ToString();
            var guardianId = Guid.NewGuid().ToString();
            var spawnedId = Guid.NewGuid().ToString();
            var forkId = Guid.NewGuid().ToString();
            var childUsageAt = now.AddMinutes(-3);
            var guardianUsageAt = now.AddMinutes(-1);
            var childPath = WriteRollout(sessions, childId,
                CodexMeta(now.AddMinutes(-4), fixtureHome, "subagent") +
                string.Concat(Enumerable.Repeat(SubagentEvent("thread_spawn", now.AddMinutes(-3).AddSeconds(-1)), 5)) +
                CodexTokenCount(childUsageAt, 31));
            WriteRollout(sessions, guardianId,
                CodexMeta(now.AddMinutes(-2), fixtureHome,
                    new Dictionary<string, object?>
                    {
                        ["subagent"] = new Dictionary<string, object?> { ["other"] = "guardian" },
                    }) +
                SubagentEvent("task_complete", guardianUsageAt.AddSeconds(-1)) +
                CodexTokenCount(guardianUsageAt, 88));
            WriteRollout(sessions, spawnedId,
                CodexMeta(now.AddMinutes(-2), fixtureHome, new
                {
                    subagent = new { thread_spawn = new { parent_thread_id = forkId, depth = 1, agent_path = "/root/test" } },
                }) + SubagentEvent("task_started", now.AddSeconds(-40)));
            WriteRollout(sessions, forkId,
                CodexMeta(now.AddMinutes(-2), fixtureHome, "vscode", childId) +
                SubagentEvent("task_complete", now.AddSeconds(-80)) +
                CodexTokenCount(now.AddSeconds(-70), 12));

            var reader = new CodexLocalReader();
            var first = reader.Read(12);
            var newest = first.LogUsage?.Windows.FirstOrDefault(x => x.Key == "codex:w300");
            Check("Codex filters spawned, string and guardian subagents but keeps newest usage and user fork",
                Find(first, childId) == null && Find(first, guardianId) == null && Find(first, spawnedId) == null &&
                Find(first, forkId) is { StateText: "완료" } && newest?.UsedPercent == 88 &&
                first.LogUsage?.AsOf == new DateTimeOffset(guardianUsageAt),
                $"threads={string.Join(',', first.Threads.Select(x => x.Id))}, newest={newest?.UsedPercent}/{first.LogUsage?.AsOf:o}");

            File.AppendAllText(childPath, SubagentEvent("task_complete", now.AddSeconds(-30)));
            var incremental = reader.Read(12);
            Check("incremental reads keep a subagent filtered",
                Find(incremental, childId) == null && incremental.LogUsage?.Windows.FirstOrDefault(x => x.Key == "codex:w300")?.UsedPercent == 88,
                DescribeThreads(incremental, childId));

            File.WriteAllText(childPath,
                CodexMeta(now.AddSeconds(-20), fixtureHome, "cli", childId) +
                SubagentEvent("task_complete", now.AddSeconds(-10)), Encoding.UTF8);
            var replaced = reader.Read(12);
            Check("truncated child replacement clears stale subagent filtering",
                Find(replaced, childId) is { StateText: "완료" }, DescribeThreads(replaced, childId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            TryDelete(fixtureHome);
        }
    }

    static string CodexMeta(DateTime timestamp, string cwd, object? source, string? forkedFrom = null)
    {
        var payload = new Dictionary<string, object?> { ["cwd"] = cwd };
        if (source != null) payload["source"] = source;
        if (forkedFrom != null) payload["forked_from"] = forkedFrom;
        return JsonSerializer.Serialize(new
        {
            timestamp = Stamp(timestamp),
            type = "session_meta",
            payload,
        }) + "\n";
    }

    static string SubagentEvent(string type, DateTime timestamp) => JsonSerializer.Serialize(new
    {
        timestamp = Stamp(timestamp),
        type = "event_msg",
        payload = new { type },
    }) + "\n";

    static string CodexTokenCount(DateTime timestamp, double used) => JsonSerializer.Serialize(new
    {
        timestamp = Stamp(timestamp),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            rate_limits = new
            {
                primary = new { used_percent = used, window_minutes = 300, resets_at = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() },
                secondary = new { used_percent = used, window_minutes = 10080, resets_at = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds() },
            },
        },
    }) + "\n";
}
