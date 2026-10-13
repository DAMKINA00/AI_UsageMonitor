using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static int s_assertions;
    static int s_failures;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("app-server")) return FakeCodexServer();
        Run("Presenter Codex usage merge and failure notice", TestPresenterCodex);
        Run("Claude WebView initialization recovery", TestClaudeInitializationRecovery);
        Run("Codex local activity timestamps and incremental reads", TestCodexLocalReader);
        Run("Session identity and current click target", TestSessionIdentityAndViewModel);
        Run("Claude app conversations from claude.ai", TestClaudeChats);
        Run("Claude Desktop authentication and usage isolation", TestClaudeDesktop);
        Run("Claude project thread status and mood transitions", TestClaudeProjectThreads);
        Run("Isolated account state and display names", TestMultiAccounts);
        Run("Compact usage and account slots", TestCompactUsage);
        Run("Character overlay activity and speech anchor", TestCharacterOverlay);
        Run("Account swap and Codex subagent filtering", TestAccountSwapAndCodexSubagents);
        Run("Codex stale file timestamps", TestCodexStaleFileTimes);
        Run("Codex browser login protocol and cancellation", () => TestCodexLoginProtocol().GetAwaiter().GetResult());

        Console.WriteLine($"Totals: {s_assertions} assertions, {s_failures} failures.");
        return s_failures == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            s_failures++;
            Console.WriteLine($"FAIL {name}: {Unwrap(ex).Message}");
        }
    }

    static void Check(string name, bool condition, string detail)
    {
        s_assertions++;
        if (condition)
        {
            Console.WriteLine($"PASS {name}");
            return;
        }

        s_failures++;
        Console.WriteLine($"FAIL {name}: {detail}");
    }

    static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner }
        ? Unwrap(inner)
        : ex;

    static UsageSnapshot Snapshot(string source, DateTimeOffset asOf, params UsageWindow[] windows) => new()
    {
        Windows = windows.ToList(),
        AsOf = asOf,
        Source = source,
    };

    static UsageWindow Window(string key, double used, DateTimeOffset reset) =>
        new(key, key, used, reset);

    static MonitorService Monitor(CodexRemoteResult? good, CodexRemoteResult? last, CodexLocalState? local)
    {
        var monitor = new MonitorService(new AppSettings());
        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), good);
        SetProperty(monitor, nameof(MonitorService.CodexRemoteLast), last);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), local);
        return monitor;
    }

    static void TestPresenterCodex()
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddHours(1);
        const string baseKey = "codex:w300";
        const string serverOnlyKey = "codex:special:w10080";
        const string logOnlyKey = "codex:w60";

        var server = Snapshot("Codex 서버", now.AddMinutes(-2),
            Window(baseKey, 20, reset),
            Window(serverOnlyKey, 100, reset));
        var newerLog = Snapshot("Codex 로그", now.AddMinutes(-1),
            Window(baseKey, 25, reset),
            Window(logOnlyKey, 40, reset));

        using var monitor = Monitor(
            new CodexRemoteResult { Ok = true, Usage = server },
            null,
            new CodexLocalState { LogUsage = newerLog });

        var merged = Presenter.Codex(monitor);
        var baseWindow = merged.RawWindows.FirstOrDefault(x => x.Key == baseKey);
        var serverOnlyWindow = merged.RawWindows.FirstOrDefault(x => x.Key == serverOnlyKey);
        var logOnlyWindow = merged.RawWindows.FirstOrDefault(x => x.Key == logOnlyKey);
        var duplicateKeys = merged.RawWindows.GroupBy(x => x.Key).Where(g => g.Count() > 1).ToList();

        Check("newer log replaces matching key", baseWindow?.UsedPercent == 25,
            $"matching value={baseWindow?.UsedPercent}");
        Check("newer log retains server-only limit", serverOnlyWindow?.UsedPercent == 100,
            $"server-only value={serverOnlyWindow?.UsedPercent}");
        Check("newer log adds log-only window", logOnlyWindow?.UsedPercent == 40,
            $"log-only value={logOnlyWindow?.UsedPercent}");
        Check("merged windows have no duplicate keys", duplicateKeys.Count == 0,
            $"duplicate keys={string.Join(",", duplicateKeys.Select(x => x.Key))}");
        Check("server special bucket remains max used", merged.MaxUsed == 100,
            $"max used={merged.MaxUsed}");
        Check("mixed source identifies selected data", merged.Footnote.Contains("Codex 로그 + Codex 서버 (이전 조회)", StringComparison.Ordinal),
            $"footnote={merged.Footnote}");

        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState
        {
            LogUsage = Snapshot("Codex 로그", server.AsOf.AddMinutes(-1), Window(baseKey, 90, reset), Window(logOnlyKey, 40, reset)),
        });
        var older = Presenter.Codex(monitor);
        Check("older log does not replace server", older.RawWindows.Count == 2 &&
            older.RawWindows.Any(x => x.Key == baseKey && x.UsedPercent == 20) &&
            older.RawWindows.Any(x => x.Key == serverOnlyKey && x.UsedPercent == 100),
            $"windows={DescribeWindows(older.RawWindows)}");
        Check("older log keeps server source", older.Footnote.Contains(server.Source, StringComparison.Ordinal) &&
            !older.Footnote.Contains("Codex 로그 +", StringComparison.Ordinal), $"footnote={older.Footnote}");

        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState
        {
            LogUsage = Snapshot("Codex 로그", server.AsOf, Window(baseKey, 90, reset), Window(logOnlyKey, 40, reset)),
        });
        var equal = Presenter.Codex(monitor);
        Check("equal timestamp log does not replace server", equal.RawWindows.Count == 2 &&
            equal.RawWindows.Any(x => x.Key == baseKey && x.UsedPercent == 20),
            $"windows={DescribeWindows(equal.RawWindows)}");

        SetProperty(monitor, nameof(MonitorService.CodexLocal), null);
        var serverOnly = Presenter.Codex(monitor);
        Check("server-only snapshot is safe", serverOnly.MaxUsed == 100 && serverOnly.RawWindows.Count == 2,
            $"max={serverOnly.MaxUsed}, windows={serverOnly.RawWindows.Count}");

        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), null);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState { LogUsage = newerLog });
        var logOnly = Presenter.Codex(monitor);
        Check("log-only snapshot is safe", logOnly.MaxUsed == 40 && logOnly.RawWindows.Count == 2 &&
            logOnly.Footnote.Contains(newerLog.Source, StringComparison.Ordinal),
            $"max={logOnly.MaxUsed}, windows={logOnly.RawWindows.Count}, footnote={logOnly.Footnote}");

        SetProperty(monitor, nameof(MonitorService.CodexLocal), null);
        var noData = Presenter.Codex(monitor);
        Check("no server and no log is safe", noData.MaxUsed == null && noData.RawWindows.Count == 0,
            $"max={noData.MaxUsed}, windows={noData.RawWindows.Count}");

        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), new CodexRemoteResult { Ok = true, Usage = server });
        SetProperty(monitor, nameof(MonitorService.CodexLocal), null);
        var before = Presenter.Codex(monitor);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState { LogUsage = newerLog });
        var after = Presenter.Codex(monitor);
        var mood = new MoodTracker("codex");
        mood.Update(before);
        var moodChange = mood.Update(after);
        Check("partial newer log does not cause false mood reset", !moodChange.Reset && after.MaxUsed == 100,
            $"reset={moodChange.Reset}, after max={after.MaxUsed}");

        var failure = new CodexRemoteResult { Ok = false, Error = "fixture offline" };
        SetProperty(monitor, nameof(MonitorService.CodexRemoteLast), failure);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), null);
        var serverFailure = Presenter.Codex(monitor);
        Check("server fallback notice names selected source", serverFailure.Notice?.Contains(server.Source, StringComparison.Ordinal) == true,
            $"notice={serverFailure.Notice}");
        Check("server fallback notice does not claim log source", serverFailure.Notice?.Contains("로그 값 표시 중", StringComparison.Ordinal) != true,
            $"notice={serverFailure.Notice}");

        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), null);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), new CodexLocalState { LogUsage = newerLog });
        var logFailure = Presenter.Codex(monitor);
        Check("log fallback notice names selected source", logFailure.Notice?.Contains(newerLog.Source, StringComparison.Ordinal) == true,
            $"notice={logFailure.Notice}");

        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), new CodexRemoteResult { Ok = true, Usage = server });
        var combinedFailure = Presenter.Codex(monitor);
        Check("combined fallback notice names selected source", combinedFailure.Notice?.Contains("Codex 로그 + Codex 서버", StringComparison.Ordinal) == true,
            $"notice={combinedFailure.Notice}");

        SetProperty(monitor, nameof(MonitorService.CodexRemoteGood), null);
        SetProperty(monitor, nameof(MonitorService.CodexLocal), null);
        var noDataFailure = Presenter.Codex(monitor);
        Check("no-data failure notice is handled", noDataFailure.Notice is { } noDataNotice &&
            noDataNotice.Contains("사용량 조회 실패", StringComparison.Ordinal) &&
            noDataNotice.Contains("fixture offline", StringComparison.Ordinal), $"notice={noDataFailure.Notice}");
    }

    static string DescribeWindows(IEnumerable<UsageWindow> windows) =>
        string.Join(", ", windows.Select(x => $"{x.Key}={x.UsedPercent:0.#}"));

    static void TestClaudeInitializationRecovery()
    {
        using var web = new ClaudeWebClient();
        var initField = PrivateField<ClaudeWebClient>("_init");
        var ensure = PrivateMethod<ClaudeWebClient>("EnsureInitAsync");
        var release = PrivateMethod<ClaudeWebClient>("Release");

        var failed = Task.FromException(new InvalidOperationException("fixture init failure"));
        initField.SetValue(web, failed);
        var observed = Observe((Task)ensure.Invoke(web, null)!);
        Check("failed cached init is observed as a fault", observed is InvalidOperationException,
            $"exception={observed?.GetType().Name ?? "none"}");
        Check("failed cached init is cleared after await", initField.GetValue(web) == null,
            $"_init={initField.GetValue(web)?.GetType().Name ?? "null"}");

        // The field is deliberately injected before each call so this check never starts WebView2.
        initField.SetValue(web, Task.CompletedTask);
        var success = Observe((Task)ensure.Invoke(web, null)!);
        Check("completed injected init succeeds", success == null,
            $"exception={success?.Message ?? "none"}");

        initField.SetValue(web, Task.FromException(new InvalidOperationException("fixture release failure")));
        release.Invoke(web, null);
        Check("Release clears failed init without a controller", initField.GetValue(web) == null,
            $"_init={initField.GetValue(web)?.GetType().Name ?? "null"}");
    }

    static Exception? Observe(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return Unwrap(ex);
        }
    }

    static void TestCodexLocalReader()
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
            Check("fixture timestamps use UTC Z format", Stamp(now).EndsWith("Z", StringComparison.Ordinal) &&
                !Stamp(now).Contains("+00:00", StringComparison.Ordinal), Stamp(now));

            var oldId = Guid.NewGuid().ToString();
            WriteRollout(sessions, oldId,
                Meta(now.AddDays(-3), fixtureHome) +
                Event("task_complete", now.AddDays(-3)) +
                Event("thread_settings_applied", now));
            var oldState = new CodexLocalReader().Read(12);
            Check("old task plus recent settings is excluded", Find(oldState, oldId) == null,
                DescribeThreads(oldState, oldId));

            var recentId = Guid.NewGuid().ToString();
            var recentAt = now.AddMinutes(-2);
            WriteRollout(sessions, recentId,
                Meta(recentAt.AddSeconds(-1), fixtureHome) + Event("task_complete", recentAt));
            var recentState = new CodexLocalReader().Read(12);
            var recent = Find(recentState, recentId);
            Check("recent real activity remains visible", recent is { StateText: "완료" } &&
                recent.LastActivity?.UtcDateTime == recentAt,
                DescribeThreads(recentState, recentId));

            var tailId = Guid.NewGuid().ToString();
            var settingsLine = Event("thread_settings_applied", now);
            var tailPath = WriteRollout(sessions, tailId,
                Meta(now.AddDays(-3), fixtureHome) +
                Event("task_complete", now.AddDays(-3)) +
                string.Concat(Enumerable.Repeat(settingsLine, 7000)));
            Check("settings-only fixture exceeds reader tail", new FileInfo(tailPath).Length > 512 * 1024,
                $"bytes={new FileInfo(tailPath).Length}");
            var tailState = new CodexLocalReader().Read(12);
            Check("settings-only tail does not revive old thread", Find(tailState, tailId) == null,
                DescribeThreads(tailState, tailId));

            var incrementalId = Guid.NewGuid().ToString();
            var incrementalAt = now.AddMinutes(-1);
            var incrementalPath = WriteRollout(sessions, incrementalId,
                Meta(incrementalAt.AddSeconds(-1), fixtureHome) + Event("task_complete", incrementalAt));
            var incrementalReader = new CodexLocalReader();
            var first = Find(incrementalReader.Read(12), incrementalId);
            var abortedLine = Event("turn_aborted", incrementalAt.AddSeconds(1));
            File.AppendAllText(incrementalPath, abortedLine[..^1]);
            var partial = Find(incrementalReader.Read(12), incrementalId);
            File.AppendAllText(incrementalPath, "\n");
            var completed = Find(incrementalReader.Read(12), incrementalId);
            Check("partial JSONL waits for newline", first?.StateText == "완료" && partial?.StateText == "완료" &&
                completed?.StateText == "중단됨",
                $"states={first?.StateText}/{partial?.StateText}/{completed?.StateText}");

            var settingsAppendId = Guid.NewGuid().ToString();
            var settingsActivity = now.AddMinutes(-3);
            var settingsAppendPath = WriteRollout(sessions, settingsAppendId,
                Meta(settingsActivity.AddSeconds(-1), fixtureHome) + Event("task_complete", settingsActivity));
            var settingsReader = new CodexLocalReader();
            var settingsBefore = Find(settingsReader.Read(12), settingsAppendId);
            File.AppendAllText(settingsAppendPath, Event("thread_settings_applied", now));
            var settingsAfter = Find(settingsReader.Read(12), settingsAppendId);
            Check("settings-only append does not advance activity", settingsBefore?.LastActivity == settingsAfter?.LastActivity &&
                settingsAfter?.LastActivity?.UtcDateTime == settingsActivity,
                $"activity={settingsBefore?.LastActivity}/{settingsAfter?.LastActivity}");

            var truncatedId = Guid.NewGuid().ToString();
            var truncatedActivity = now.AddMinutes(-4);
            var truncatedPath = WriteRollout(sessions, truncatedId,
                Meta(truncatedActivity.AddSeconds(-1), fixtureHome) +
                Event("task_complete", truncatedActivity) +
                Event("thread_settings_applied", truncatedActivity.AddSeconds(1)));
            var truncationReader = new CodexLocalReader();
            var beforeTruncate = Find(truncationReader.Read(12), truncatedId);
            File.WriteAllText(truncatedPath,
                Meta(now.AddDays(-3), fixtureHome) + Event("thread_settings_applied", now));
            var afterTruncate = Find(truncationReader.Read(12), truncatedId);
            Check("truncated settings-only file cannot retain old activity", beforeTruncate != null && afterTruncate == null,
                $"before={beforeTruncate?.LastActivity}, after={afterTruncate?.LastActivity}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            TryDelete(fixtureHome);
        }
    }

    static string DescribeThreads(CodexLocalState state, string id) =>
        state.Threads.FirstOrDefault(x => x.Id == id) is { } thread
            ? $"{thread.Id}:{thread.StateText}:{thread.LastActivity:o}"
            : "not present";

    static SessionInfo? Find(CodexLocalState state, string id) =>
        state.Threads.FirstOrDefault(x => x.Id == id);

    static string WriteRollout(string sessions, string id, string content)
    {
        var path = Path.Combine(sessions, $"rollout-2026-10-12T00-00-00-{id}.jsonl");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    static string Meta(DateTime timestamp, string cwd) => JsonSerializer.Serialize(new
    {
        timestamp = Stamp(timestamp),
        type = "session_meta",
        payload = new { cwd },
    }) + "\n";

    static string Event(string type, DateTime timestamp) => JsonSerializer.Serialize(new
    {
        timestamp = Stamp(timestamp),
        type = "event_msg",
        payload = new { type },
    }) + "\n";

    static string Stamp(DateTime timestamp) => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToString("o");

    static void TestClaudeChats()
    {
        var now = DateTimeOffset.UtcNow;
        const string runningId = "11111111-1111-1111-1111-111111111111";
        const string recentId = "22222222-2222-2222-2222-222222222222";
        const string oldId = "33333333-3333-3333-3333-333333333333";
        const string waitingId = "44444444-4444-4444-4444-444444444444";
        const string duplicateId = "55555555-5555-5555-5555-555555555555";

        // chat_conversations_v2 에서 스크립트가 골라 넘기는 필드와 같은 모양
        var json = JsonSerializer.Serialize(new object[]
        {
            Chat(runningId, "실행 중 대화", now.AddSeconds(-5), "running", now.AddMinutes(-2), null, null, null, "cse_running"),
            Chat(recentId, "", now.AddHours(-1), null, null, null, null, "게임", null),
            Chat(oldId, "오래된 대화", now.AddDays(-2), null, null, null, null, null, null),
            Chat(waitingId, "승인 필요", now.AddDays(-2), null, null, true, "tool_permission", null, null),
            Chat(duplicateId, "로컬과 같은 세션", now.AddMinutes(-1), "running", now.AddMinutes(-1), null, null, null, "cse_local"),
            new { name = "uuid 없음", updated_at = Iso(now) },
        });
        using var doc = JsonDocument.Parse(json);
        var chats = ClaudeWebClient.ParseChats(doc.RootElement, now.AddHours(-12));

        SessionInfo? ChatOf(string id) => chats.FirstOrDefault(c => c.Id == "chat:" + id);
        var running = ChatOf(runningId);
        Check("running chat is working since live_status_started_at",
            running is { State: SessionState.Working, StateText: "작업 중", ActivityIsStateChange: true } &&
            running.LastActivity == JsonUtil.Iso(Iso(now.AddMinutes(-2))),
            $"{running?.State}/{running?.StateText}/{running?.LastActivity:o}");
        Check("chat opens in the Claude app", running?.OpenUri == $"claude://claude.ai/chat/{runningId}" && running.AltId == "cse_running",
            $"uri={running?.OpenUri}, alt={running?.AltId}");

        var recent = ChatOf(recentId);
        Check("untitled recent chat gets fallback title and project", recent is { Title: "새 대화", Subtitle: "게임 · 앱 대화", State: SessionState.Idle, StateText: "완료" },
            $"{recent?.Title}/{recent?.Subtitle}/{recent?.StateText}");
        Check("old finished chat is excluded", ChatOf(oldId) == null, "old chat present");
        var waiting = ChatOf(waitingId);
        Check("old chat waiting for approval stays visible", waiting is { State: SessionState.NeedsInput, StateText: "승인 대기" },
            $"{waiting?.State}/{waiting?.StateText}");
        Check("chat without uuid is skipped", chats.Count == 4, $"count={chats.Count}");

        var local = new ClaudeLocalState
        {
            Sessions = [new SessionInfo("local-1", "터미널 세션", "repo · 터미널", SessionState.Working, "작업 중", now, true)],
            KnownIds = new(StringComparer.OrdinalIgnoreCase) { "local-1", "cse_local" },
        };
        var monitor = new MonitorService(new AppSettings());
        SetProperty(monitor, nameof(MonitorService.ClaudeLocal), local);
        SetProperty(monitor, nameof(MonitorService.ClaudeChats), chats);
        SetProperty(monitor, nameof(MonitorService.ClaudeChatsAt), (DateTimeOffset?)DateTimeOffset.Now);

        var merged = Presenter.ClaudeSessions(monitor);
        Check("local session and app chats are merged without duplicates",
            merged.Count == 4 && merged.Any(s => s.Id == "local-1") && merged.All(s => s.Id != "chat:" + duplicateId),
            string.Join(",", merged.Select(s => s.Id)));

        var card = Presenter.Claude(monitor, new AppSettings { ClaudeWebEnabled = true }, () => { });
        Check("card counts working and waiting app chats",
            card.SessionsHeader == "세션 4 · 작업 중 2 · 대기 1" && card.Sessions.FirstOrDefault()?.Title == "승인 필요",
            $"header={card.SessionsHeader}, first={card.Sessions.FirstOrDefault()?.Title}");

        SetProperty(monitor, nameof(MonitorService.ClaudeChatsAt), (DateTimeOffset?)DateTimeOffset.Now.AddMinutes(-30));
        var stale = Presenter.ClaudeSessions(monitor);
        var staleRunning = stale.FirstOrDefault(s => s.Id == "chat:" + runningId);
        Check("old chat data does not claim a chat is still running",
            staleRunning is { State: SessionState.Stale, StateText: "갱신 안 됨" } &&
            stale.First(s => s.Id == "local-1").State == SessionState.Working,
            $"{staleRunning?.State}/{staleRunning?.StateText}");

        static object Chat(string uuid, string name, DateTimeOffset updated, string? live, DateTimeOffset? started,
            bool? needsInput, string? needsKind, string? project, string? workspace) => new
        {
            uuid,
            name,
            updated_at = Iso(updated),
            live_status = live,
            live_status_started_at = started is { } s ? Iso(s) : null,
            needs_input = needsInput,
            needs_input_kind = needsKind,
            project,
            workspace_session_id = workspace,
        };

        static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    static void TestSessionIdentityAndViewModel()
    {
        var defaultRow = new SessionRow
        {
            Title = "default",
            Subtitle = "fixture",
            StateText = "완료",
            StateBrush = Palette.Ok,
        };
        Check("SessionRow identity fields have safe defaults", defaultRow.Id == "" && defaultRow.OpenUri == null,
            $"defaults={defaultRow.Id}/{defaultRow.OpenUri}");

        var session = new SessionInfo(
            "build-thread-id",
            "same display",
            "fixture folder",
            SessionState.Idle,
            "완료",
            DateTimeOffset.Now.AddMinutes(-1),
            OpenUri: "codex://threads/build-thread-id");
        using (var monitor = Monitor(null, null, new CodexLocalState { Threads = [session] }))
        {
            var built = Presenter.Codex(monitor);
            var row = built.Sessions.Single();
            Check("Presenter.Build copies session Id and OpenUri", row.Id == session.Id && row.OpenUri == session.OpenUri,
                $"row={row.Id}/{row.OpenUri}");
        }

        var clicked = "none";
        var blank = Card();
        var old = Card(Row("same title", "same subtitle", "old-id", "codex://old", () => clicked = "old"));
        var idChanged = Card(Row("same title", "same subtitle", "new-id", "codex://old", () => clicked = "id-new"));
        var uriChanged = Card(Row("same title", "same subtitle", "new-id", "codex://new", () => clicked = "uri-new"));
        var unchanged = Card(Row("same title", "same subtitle", "new-id", "codex://new", () => clicked = "replacement"));
        var vm = new MainViewModel();

        vm.Update(blank, old, "fixture");
        vm.Update(blank, idChanged, "fixture");
        Check("different session Id replaces same display row", ReferenceEquals(vm.Codex, idChanged),
            "view-model retained old card");
        vm.Codex!.Sessions.Single().OpenCommand!.Execute(null);
        Check("Id replacement exposes its click target", clicked == "id-new", $"clicked={clicked}");

        vm.Update(blank, uriChanged, "fixture");
        Check("different OpenUri replaces same display row", ReferenceEquals(vm.Codex, uriChanged),
            "view-model retained old URI card");
        vm.Codex!.Sessions.Single().OpenCommand!.Execute(null);
        Check("OpenUri replacement exposes its click target", clicked == "uri-new", $"clicked={clicked}");

        vm.Update(blank, unchanged, "fixture");
        Check("unchanged session identity keeps existing card", ReferenceEquals(vm.Codex, uriChanged),
            "view-model replaced an unchanged card");
        vm.Codex!.Sessions.Single().OpenCommand!.Execute(null);
        Check("unchanged identity keeps current command callback", clicked == "uri-new", $"clicked={clicked}");
    }

    static ProviderCard Card(params SessionRow[] sessions) => new()
    {
        Name = "Codex",
        Accent = Palette.Codex,
        AppStatus = "fixture",
        AppStatusBrush = Palette.Muted,
        Footnote = "fixture",
        SessionsHeader = "스레드 1",
        Sessions = sessions.ToList(),
    };

    static SessionRow Row(string title, string subtitle, string id, string uri, Action callback) => new()
    {
        Id = id,
        OpenUri = uri,
        OpenCommand = new RelayCommand(callback),
        Title = title,
        Subtitle = subtitle,
        StateText = "완료",
        StateBrush = Palette.Ok,
    };

    static FieldInfo PrivateField<T>(string name) => typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(T).FullName, name);

    static MethodInfo PrivateMethod<T>(string name) => typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(T).FullName, name);

    static void SetProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        property.SetValue(target, value);
    }

    static void TryDelete(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("AIUsageMonitor-Regression-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup path is outside the test directory.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // The fixture is temporary; a locked file does not affect production data.
        }
    }
}
