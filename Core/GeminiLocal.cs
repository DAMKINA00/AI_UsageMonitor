using System.IO;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public sealed class GeminiLocalState
{
    /// <summary>agy 실행 파일을 찾았는지. false 면 "agy 미설치" 상태.</summary>
    public bool Installed { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>agy 실행 파일의 (경로|크기|수정 시각). 업데이트를 알아채는 데 쓴다.</summary>
    public string? ExecutableFingerprint { get; init; }
    public string? Account { get; init; }
    /// <summary>실행 중인 agy 프로세스 수 (이 앱이 사용량 조회로 띄운 것은 뺀다).</summary>
    public int CliProcessCount { get; init; }
    public List<SessionInfo> Sessions { get; init; } = [];
    /// <summary>대화 목록을 읽지 못했을 때의 이유. 이때 Sessions 는 마지막으로 읽은 목록이다.</summary>
    public string? SessionsError { get; init; }
}

/// <summary>
/// Antigravity CLI(agy)의 실행 여부와 ~/.gemini/antigravity-cli/conversation_summaries.db 의 대화 상태를 읽는다.
/// DB 는 읽기 전용으로 열고, 상태 표시에 필요한 열만 읽는다 (미리보기·요약 본문·대화 단계는 읽지 않는다).
/// </summary>
public sealed class GeminiLocalReader
{
    public const string SummaryQuery =
        "SELECT conversation_id, title, workspace_uris, status, not_fully_idle, killed, last_modified_time, parent_conversation_id " +
        "FROM conversation_summaries";

    static readonly TimeSpan ExecutableRecheck = TimeSpan.FromSeconds(60);
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    // 파일 시각이 늦게 갱신되는 경우를 대비해 바뀌지 않아 보여도 이 간격으로 다시 읽는다
    static readonly TimeSpan ForcedReload = TimeSpan.FromSeconds(30);

    sealed record Row(string Id, string? Title, string? Workspaces, string? Status, bool NotFullyIdle, bool Killed,
        DateTimeOffset? Modified, string? Parent);

    readonly string _home;
    readonly Func<string?, string?> _findExecutable;
    readonly Func<int> _processCount;
    readonly Func<string?> _readAccount;
    string? _account;
    string? _exe, _exeOverride, _exeFingerprint;
    DateTime _exeCheckedAt = DateTime.MinValue;
    (long, DateTime, long, DateTime)? _stamp;
    DateTime _loadedAt;
    List<Row> _rows = [];
    string? _rowsError;

    public GeminiLocalReader(string? home = null, Func<string?, string?>? findExecutable = null, Func<int>? processCount = null,
        Func<string?>? readAccount = null)
    {
        _home = home ?? Paths.AgyHome;
        _findExecutable = findExecutable ?? AgyCli.FindExecutable;
        _processCount = processCount ?? CountAgyProcesses;
        // A custom home is an isolated profile/fixture, not the current Windows credential store.
        _readAccount = readAccount ?? (home == null ? AgyAccount.ReadName : () => null);
    }

    public GeminiLocalState Read(int activeHours, string? exeOverride = null)
    {
        var now = DateTime.UtcNow;
        if (exeOverride != _exeOverride || now - _exeCheckedAt > ExecutableRecheck)
        {
            _exe = _findExecutable(exeOverride);
            _exeFingerprint = AgyCli.Fingerprint(_exe);
            _exeOverride = exeOverride;
            _account = _exe == null ? null : _readAccount();
            _exeCheckedAt = now;
        }
        int processes = _processCount();
        LoadRows(now);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, activeHours));
        var sessions = new List<SessionInfo>();
        foreach (var row in _rows)
        {
            // 하위 에이전트 대화는 부모 대화의 상태로 충분하다
            if (row.Id.Length == 0 || !string.IsNullOrEmpty(row.Parent)) continue;
            var (state, text) = StateOf(row, processes);
            bool active = state is SessionState.Working or SessionState.NeedsInput;
            if (!active && (row.Modified is not { } modified || modified < cutoff)) continue;

            var folder = FolderOf(row.Workspaces);
            var title = row.Title is { Length: > 0 } t ? t : folder.Length > 0 ? folder : "Antigravity 대화";
            var subtitle = folder.Length > 0 ? $"{folder} · Antigravity CLI" : "Antigravity CLI";
            sessions.Add(new SessionInfo("agy:" + row.Id, title, subtitle, state, text, row.Modified));
        }

        return new GeminiLocalState
        {
            Installed = _exe != null,
            ExecutablePath = _exe,
            ExecutableFingerprint = _exeFingerprint,
            Account = _account,
            CliProcessCount = processes,
            Sessions = sessions,
            SessionsError = _rowsError,
        };
    }

    static (SessionState, string) StateOf(Row row, int processes)
    {
        var status = row.Status ?? "";
        // 진행 중 기록이 이만큼 바뀌지 않았으면 비정상 종료로 남은 것이다 (Codex 의 응답 없음 판정과 같은 기준)
        bool stale = row.Modified is not { } modified || DateTimeOffset.UtcNow - modified > StaleAfter;
        if (row.Killed) return (SessionState.Idle, "중단됨");
        if (status.EndsWith("_CANCELING", StringComparison.Ordinal))
            return processes > 0 && !stale ? (SessionState.Working, "중단 중") : (SessionState.Idle, "중단됨");
        if (row.NotFullyIdle || status.EndsWith("_RUNNING", StringComparison.Ordinal) || status.EndsWith("_BUSY", StringComparison.Ordinal))
            // agy 가 하나도 없거나 오래 바뀌지 않았으면 기록만 남은 것이다
            return processes > 0 && !stale ? (SessionState.Working, "작업 중") : (SessionState.Stale, "중단됨");
        if (status.EndsWith("_IDLE", StringComparison.Ordinal)) return (SessionState.Idle, "대기");
        return (SessionState.Idle, "알 수 없음");
    }

    void LoadRows(DateTime now)
    {
        var db = new FileInfo(Path.Combine(_home, "conversation_summaries.db"));
        if (!db.Exists)
        {
            _rows = [];
            _rowsError = null;
            _stamp = null;
            return;
        }
        var wal = new FileInfo(db.FullName + "-wal");
        var stamp = (db.Length, db.LastWriteTimeUtc, wal.Exists ? wal.Length : -1, wal.Exists ? wal.LastWriteTimeUtc : default);
        if (stamp == _stamp && now - _loadedAt < ForcedReload) return;

        try
        {
            _rows = SqliteReadOnly.Query(db.FullName, SummaryQuery, r => new Row(
                SqliteReadOnly.Text(r, 0) ?? "",
                SqliteReadOnly.Text(r, 1),
                SqliteReadOnly.Text(r, 2),
                SqliteReadOnly.Text(r, 3),
                SqliteReadOnly.Int(r, 4) != 0,
                SqliteReadOnly.Int(r, 5) != 0,
                ParseTime(SqliteReadOnly.Text(r, 6)),
                SqliteReadOnly.Text(r, 7)));
            _rowsError = null;
            _stamp = stamp;
            _loadedAt = now;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            // agy 가 쓰는 중이면 잠길 수 있다 — 이전 목록을 유지하고 다음 주기에 다시 읽는다
            _rowsError = "Antigravity 대화 목록을 읽지 못했습니다: " + ex.Message;
        }
    }

    internal static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year >= 2000 ? d : null;

    /// <summary>workspace_uris (["file:///C:/Apps", …]) 의 첫 폴더 이름.</summary>
    internal static string FolderOf(string? workspaces)
    {
        if (string.IsNullOrWhiteSpace(workspaces)) return "";
        string? first = null;
        try
        {
            using var doc = JsonDocument.Parse(workspaces);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                first = doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }
        catch (JsonException)
        {
            first = workspaces.Split(',', '\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
        }
        if (string.IsNullOrEmpty(first)) return "";
        var path = Uri.TryCreate(first, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : first;
        return Path.GetFileName(path.TrimEnd('\\', '/'));
    }

    static int CountAgyProcesses() =>
        ProcessUtil.ProcessesByName("agy").Count(p => Path.GetFileName(p.Path).Equals("agy.exe", StringComparison.OrdinalIgnoreCase));
}
