using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestClaudeProjectThreads()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-12);
        const string project = "AI 개발";

        var json = JsonSerializer.Serialize(new object[]
        {
            ProjectThread("working", "진행 중", project, "session_project/1", now.AddDays(-1), "working"),
            ProjectThread("needs", "입력 필요", project, "session_needs", now.AddDays(-1), "needs_input"),
            ProjectThread("resolved", "완료", project, "session_resolved", now.AddMinutes(-2), "resolved"),
            ProjectThread("failed", "실패", project, "session_failed", now.AddMinutes(-2), "failed"),
            ProjectThread("idle", "대기", project, "session_idle", now.AddMinutes(-2), "idle"),
            ProjectThread("archived", "보관", project, "session_archived", now, "archived"),
            ProjectThread("unknown-recent", "알 수 없음", project, "session_unknown", now.AddMinutes(-2), "future_status"),
            ProjectThread("missing-recent", "상태 없음", project, "session_missing", now.AddMinutes(-2), null, includeStatus: false),
            ProjectThread("unknown-old", "오래된 알 수 없음", project, "session_unknown_old", now.AddDays(-2), "future_status"),
            ProjectThread("missing-old", "오래된 상태 없음", project, "session_missing_old", now.AddDays(-2), null, includeStatus: false),
            ProjectThread("numeric-resolved", "숫자 완료", project, "session_numeric_resolved", now, 1),
            ProjectThread("numeric-failed", "숫자 실패", project, "session_numeric_failed", now, 2),
            ProjectThread("numeric-working", "숫자 진행", project, "session_numeric_working", now, 3),
            ProjectThread("numeric-needs", "숫자 입력", project, "session_numeric_needs", now, 4),
            ProjectThread("numeric-archived", "숫자 보관", project, "session_numeric_archived", now, 5),
            ProjectThread("numeric-idle", "숫자 대기", project, "session_numeric_idle", now, 6),
            new
            {
                uuid = "regular-chat",
                name = "일반 대화",
                updated_at = ProjectIso(now),
            },
        });

        using var doc = JsonDocument.Parse(json);
        var chats = ClaudeWebClient.ParseChats(doc.RootElement, cutoff);
        SessionInfo? ChatOf(string id) => chats.FirstOrDefault(c => c.Id == "chat:" + id);

        Check("project thread uses chat identity and project subtitle", ChatOf("working") is
            { Id: "chat:working", Title: "진행 중", Subtitle: "AI 개발 · 프로젝트 작업", AltId: "session_project/1" },
            Describe(ChatOf("working")));
        Check("project thread opens the Cowork session with escaped id",
            ChatOf("working")?.OpenUri == "claude://claude.ai/cowork/session_project%2F1",
            $"uri={ChatOf("working")?.OpenUri}");

        Check("working project thread maps to working", ChatOf("working") is
            { State: SessionState.Working, StateText: "작업 중" }, Describe(ChatOf("working")));
        Check("needs-input project thread maps to waiting", ChatOf("needs") is
            { State: SessionState.NeedsInput, StateText: "입력 대기" }, Describe(ChatOf("needs")));
        Check("resolved project thread maps to completed idle", ChatOf("resolved") is
            { State: SessionState.Idle, StateText: "완료" }, Describe(ChatOf("resolved")));
        Check("failed project thread maps to failed idle", ChatOf("failed") is
            { State: SessionState.Idle, StateText: "실패" }, Describe(ChatOf("failed")));
        Check("idle project thread maps to idle waiting", ChatOf("idle") is
            { State: SessionState.Idle, StateText: "대기" }, Describe(ChatOf("idle")));
        Check("archived recent project thread is removed", ChatOf("archived") == null,
            Describe(ChatOf("archived")));
        Check("unknown recent project thread is retained for review", ChatOf("unknown-recent") is
            { State: SessionState.Stale, StateText: "상태 확인 필요" }, Describe(ChatOf("unknown-recent")));
        Check("missing recent project status is retained for review", ChatOf("missing-recent") is
            { State: SessionState.Stale, StateText: "상태 확인 필요" }, Describe(ChatOf("missing-recent")));
        Check("unknown old project thread is excluded", ChatOf("unknown-old") == null,
            Describe(ChatOf("unknown-old")));
        Check("missing old project status is excluded", ChatOf("missing-old") == null,
            Describe(ChatOf("missing-old")));

        Check("numeric project statuses map to their string states",
            ChatOf("numeric-resolved") is { State: SessionState.Idle, StateText: "완료" } &&
            ChatOf("numeric-failed") is { State: SessionState.Idle, StateText: "실패" } &&
            ChatOf("numeric-working") is { State: SessionState.Working, StateText: "작업 중" } &&
            ChatOf("numeric-needs") is { State: SessionState.NeedsInput, StateText: "입력 대기" } &&
            ChatOf("numeric-archived") == null &&
            ChatOf("numeric-idle") is { State: SessionState.Idle, StateText: "대기" },
            string.Join(";", chats.Select(Describe)));
        Check("old working and needs-input project threads stay visible",
            ChatOf("working") != null && ChatOf("needs") != null,
            string.Join(";", chats.Select(Describe)));
        Check("regular chat remains supported beside project threads", ChatOf("regular-chat") is
            { Id: "chat:regular-chat", Title: "일반 대화", State: SessionState.Idle },
            Describe(ChatOf("regular-chat")));

        using var monitor = new MonitorService(new AppSettings());
        SetProperty(monitor, nameof(MonitorService.ClaudeLocal), new ClaudeLocalState { DesktopRunning = true });
        var settings = new AppSettings { ClaudeDesktopEnabled = true };
        var mood = new MoodTracker("claude");

        static List<SessionInfo> ParseSingle(DateTimeOffset timestamp, string status) =>
            ParseProject(JsonSerializer.Serialize(new[]
            {
                ProjectThread("mood", "기분 상태", "AI 개발", "session_mood", timestamp, status),
            }), timestamp.AddHours(-12));

        var workingMood = UpdateMood(ParseSingle(now, "working"));
        Check("project working state drives working mood and count", workingMood.mood.Mood == Mood.Working &&
            workingMood.card.WorkingCount == 1 && workingMood.card.NeedsInputCount == 0,
            $"mood={workingMood.mood.Mood}, counts={workingMood.card.WorkingCount}/{workingMood.card.NeedsInputCount}");

        var waitingMood = UpdateMood(ParseSingle(now.AddHours(-2), "needs_input"));
        Check("project needs-input state drives waiting mood and count", waitingMood.mood.Mood == Mood.Waiting &&
            waitingMood.card.WorkingCount == 0 && waitingMood.card.NeedsInputCount == 1,
            $"mood={waitingMood.mood.Mood}, counts={waitingMood.card.WorkingCount}/{waitingMood.card.NeedsInputCount}");

        var idleMood = UpdateMood(ParseSingle(now, "idle"));
        Check("project idle state is neither working nor waiting", idleMood.mood.Mood is not (Mood.Working or Mood.Waiting) &&
            idleMood.card.WorkingCount == 0 && idleMood.card.NeedsInputCount == 0,
            $"mood={idleMood.mood.Mood}, counts={idleMood.card.WorkingCount}/{idleMood.card.NeedsInputCount}");

        var workingAgainMood = UpdateMood(ParseSingle(now, "working"));
        Check("project working state restores working mood", workingAgainMood.mood.Mood == Mood.Working &&
            workingAgainMood.card.WorkingCount == 1,
            $"mood={workingAgainMood.mood.Mood}, count={workingAgainMood.card.WorkingCount}");

        SetProperty(monitor, nameof(MonitorService.ClaudeChatsAt), DateTimeOffset.Now.AddMinutes(-11));
        var stale = Presenter.ClaudeSessions(monitor).Single();
        Check("stale project chat downgrades active state", stale.State == SessionState.Stale &&
            stale.StateText == "갱신 안 됨", Describe(stale));

        var setChats = typeof(MonitorService).GetMethod("SetChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setChats.Invoke(monitor, [new ClaudeWebResult { State = ClaudeWebState.Ok, Chats = ParseSingle(now, "working") }]);
        var goodAt = monitor.ClaudeChatsAt;
        setChats.Invoke(monitor, [new ClaudeWebResult { State = ClaudeWebState.Ok, ChatsError = "프로젝트 조회 실패" }]);
        var failedCard = Presenter.Claude(monitor, settings, () => { });
        Check("project query failure retains snapshot but invalidates active status",
            monitor.ClaudeChats.Count == 1 && monitor.ClaudeChatsAt == goodAt &&
            Presenter.ClaudeSessions(monitor).Single().State == SessionState.Stale &&
            failedCard.Notice?.Contains("프로젝트 조회 실패") == true && failedCard.WorkingCount == 0,
            $"count={monitor.ClaudeChats.Count}, notice={failedCard.Notice}");
        setChats.Invoke(monitor, [new ClaudeWebResult { State = ClaudeWebState.Error, Error = "조회 실패" }]);
        Check("fast project query failure updates error", monitor.ClaudeChatsError == "조회 실패", monitor.ClaudeChatsError ?? "missing");
        setChats.Invoke(monitor, [new ClaudeWebResult { State = ClaudeWebState.Ok, Chats = ParseSingle(now, "working") }]);
        Check("project query recovery restores active status", monitor.ClaudeChatsError == null &&
            Presenter.Claude(monitor, settings, () => { }).WorkingCount == 1, monitor.ClaudeChatsError ?? "cleared");
        setChats.Invoke(monitor, [new ClaudeWebResult { State = ClaudeWebState.LoginRequired }]);
        Check("login loss clears project status and error", monitor.ClaudeChats.Count == 0 &&
            monitor.ClaudeChatsAt == null && monitor.ClaudeChatsError == null, $"count={monitor.ClaudeChats.Count}");

        (MoodTracker.Result mood, ProviderCard card) UpdateMood(List<SessionInfo> sessions)
        {
            SetProperty(monitor, nameof(MonitorService.ClaudeChats), sessions);
            SetProperty(monitor, nameof(MonitorService.ClaudeChatsAt), DateTimeOffset.Now);
            var card = Presenter.Claude(monitor, settings, () => { });
            return (mood.Update(card), card);
        }

        static List<SessionInfo> ParseProject(string source, DateTimeOffset cutoff)
        {
            using var document = JsonDocument.Parse(source);
            return ClaudeWebClient.ParseChats(document.RootElement, cutoff);
        }

        static string Describe(SessionInfo? session) => session == null
            ? "missing"
            : $"{session.Id}:{session.State}:{session.StateText}:{session.Title}";

        static object ProjectThread(string uuid, string name, string project, string workspaceSessionId,
            DateTimeOffset updatedAt, object? status, bool includeStatus = true) =>
            includeStatus
                ? new Dictionary<string, object?>
                {
                    ["source_kind"] = "project_thread",
                    ["uuid"] = uuid,
                    ["name"] = name,
                    ["project"] = project,
                    ["project_uuid"] = "chan_AI_development",
                    ["workspace_session_id"] = workspaceSessionId,
                    ["updated_at"] = ProjectIso(updatedAt),
                    ["thread_status"] = status,
                }
                : new Dictionary<string, object?>
                {
                    ["source_kind"] = "project_thread",
                    ["uuid"] = uuid,
                    ["name"] = name,
                    ["project"] = project,
                    ["project_uuid"] = "chan_AI_development",
                    ["workspace_session_id"] = workspaceSessionId,
                    ["updated_at"] = ProjectIso(updatedAt),
                };

        static string ProjectIso(DateTimeOffset value) => value.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture);
    }
}
