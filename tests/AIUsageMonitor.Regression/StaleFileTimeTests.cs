using System.IO;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCodexStaleFileTimes()
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
            var oldMtime = now.AddDays(-2);
            var coldStartId = Guid.NewGuid().ToString();
            var metadataOnlyId = Guid.NewGuid().ToString();
            var oldActivityId = Guid.NewGuid().ToString();
            var coldStartAt = now.AddMinutes(-2);

            var coldStartPath = WriteRollout(sessions, coldStartId,
                Meta(coldStartAt.AddSeconds(-1), fixtureHome) + Event("task_complete", coldStartAt));
            File.SetLastWriteTimeUtc(coldStartPath, oldMtime);

            // These files have current mtime but their only real activity is old.
            WriteRollout(sessions, metadataOnlyId,
                Meta(now.AddDays(-2), fixtureHome) + Event("thread_settings_applied", now));
            WriteRollout(sessions, oldActivityId,
                Meta(now.AddDays(-2), fixtureHome) + Event("task_complete", now.AddDays(-2)));

            var coldStart = new CodexLocalReader().Read(12);
            Check("cold-start reads recent activity from an old-mtime file",
                Find(coldStart, coldStartId) is { StateText: "완료" } &&
                Find(coldStart, coldStartId)?.LastActivity?.UtcDateTime == coldStartAt,
                DescribeThreads(coldStart, coldStartId));
            Check("old metadata and old activity remain excluded by content time",
                Find(coldStart, metadataOnlyId) == null && Find(coldStart, oldActivityId) == null,
                $"metadata={DescribeThreads(coldStart, metadataOnlyId)}, activity={DescribeThreads(coldStart, oldActivityId)}");

            var appendId = Guid.NewGuid().ToString();
            var initialAt = now.AddMinutes(-4);
            var appendPath = WriteRollout(sessions, appendId,
                Meta(initialAt.AddSeconds(-1), fixtureHome) + Event("task_complete", initialAt));
            File.SetLastWriteTimeUtc(appendPath, oldMtime);
            var appendReader = new CodexLocalReader();
            var beforeAppend = Find(appendReader.Read(12), appendId);
            File.AppendAllText(appendPath, Event("turn_aborted", now.AddMinutes(-1)));
            File.SetLastWriteTimeUtc(appendPath, oldMtime);
            var afterAppend = Find(appendReader.Read(12), appendId);
            Check("same reader observes appended activity despite restored old mtime",
                beforeAppend is { StateText: "완료" } && afterAppend is { StateText: "중단됨" } &&
                afterAppend.LastActivity > beforeAppend.LastActivity,
                $"before={beforeAppend?.StateText}/{beforeAppend?.LastActivity:o}, after={afterAppend?.StateText}/{afterAppend?.LastActivity:o}");

            var subagentId = Guid.NewGuid().ToString();
            var subagentAt = now.AddMinutes(-1);
            var subagentPath = WriteRollout(sessions, subagentId,
                CodexMeta(subagentAt.AddSeconds(-1), fixtureHome, "subagent") +
                SubagentEvent("task_complete", subagentAt) + CodexTokenCount(subagentAt, 93));
            File.SetLastWriteTimeUtc(subagentPath, oldMtime);
            var subagentState = new CodexLocalReader().Read(12);
            var subagentRate = subagentState.LogUsage?.Windows.FirstOrDefault(x => x.Key == "codex:w300");
            Check("old-mtime subagent stays hidden while its rate limit is retained",
                Find(subagentState, subagentId) == null && subagentRate?.UsedPercent == 93,
                $"thread={DescribeThreads(subagentState, subagentId)}, rate={subagentRate?.UsedPercent}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            TryDelete(fixtureHome);
        }
    }
}
