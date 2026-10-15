using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    const string FakeAgyScenario = "AIUSAGEMONITOR_FAKE_AGY";
    const string FakeAgyDirectory = "AIUSAGEMONITOR_FAKE_AGY_DIR";

    // 실측한 `agy -p /usage --output-format json` 출력 (agy 1.3.5). 계정 식별자는 원래 포함되지 않는다.
    const string AgyUsageFixture = """
        {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t96%\t2026-10-21T01:22:12Z\n","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[{"name":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.9636138081550598,"reset_time":"2026-10-21T01:22:12Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.982385516166687,"reset_time":"2026-10-15T03:53:26Z"}]},{"name":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-10-22T02:50:47Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-10-15T07:50:47Z"}]}]}}}
        """;

    // 인수 없는 agy 처럼 /usage 가 프롬프트로 실행된 경우의 출력 모양 (본문은 결과·로그에 남으면 안 된다)
    const string AgyPromptOutput = """
        {"conversation_id":"c-123","status":"SUCCESS","response":"Hello! How can I assist you with your project today?","num_turns":1,"usage":{"total_tokens":42}}
        """;

    static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-10-15T01:00:00Z");

    /// <summary>테스트 실행 파일이 가짜 agy 로도 동작한다. 시나리오는 환경 변수로 받는다.</summary>
    static int FakeAgy(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable(FakeAgyScenario) ?? "success";
        var dir = Environment.GetEnvironmentVariable(FakeAgyDirectory);
        if (dir != null)
        {
            // 부모 테스트가 돌린 데이터 폴더를 물려받았는지 확인용
            File.WriteAllText(Path.Combine(dir, "datadir.txt"), Paths.AppData);
            File.WriteAllText(Path.Combine(dir, "pid.txt"), Environment.ProcessId.ToString());
            File.WriteAllText(Path.Combine(dir, "args.txt"), string.Join("\n", args));
            int logIndex = Array.IndexOf(args, "--log-file");
            if (logIndex >= 0 && logIndex + 1 < args.Length)
            {
                File.WriteAllText(args[logIndex + 1], "fake log");
                File.WriteAllText(Path.Combine(dir, "logpath.txt"), args[logIndex + 1]);
            }
        }
        switch (scenario)
        {
            case "hang":
                Thread.Sleep(TimeSpan.FromSeconds(60));
                return 0;
            case "flood":
                var chunk = new string('x', 64 * 1024);
                for (int i = 0; i < 64; i++) Console.Out.Write(chunk);
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(60));
                return 0;
            case "slow":
                Thread.Sleep(TimeSpan.FromSeconds(2));
                break;
            case "login":
                Console.WriteLine("""{"conversation_id":"","status":"ERROR","error":"You are not logged into Antigravity. Sign in as person@example.test"}""");
                return 1;
            case "error":
                Console.WriteLine("""{"conversation_id":"","status":"ERROR","error":"quota service unavailable"}""");
                return 1;
            case "prompt":
                Console.WriteLine(AgyPromptOutput.Trim());
                return 0;
            case "plain-login":
                Console.WriteLine("You are not logged in.\nPlease sign in with agy to continue.");
                return 0;
            case "orphan-pipe":
                // 출력 파이프를 물려받은 자식을 남기고 먼저 끝난다 (자식이 PID 를 남길 때까지 기다린다)
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("hold-pipe");
                Process.Start(psi)?.Dispose();
                for (int i = 0; i < 100 && dir != null && !File.Exists(Path.Combine(dir, "orphan.txt")); i++) Thread.Sleep(50);
                break;
        }
        Console.WriteLine(AgyUsageFixture.Trim());
        return 0;
    }

    static int HoldPipe()
    {
        if (Environment.GetEnvironmentVariable(FakeAgyDirectory) is { } dir)
            File.WriteAllText(Path.Combine(dir, "orphan.txt"), Environment.ProcessId.ToString());
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }

    static void TestAgyUsageParse()
    {
        var parsed = AgyCli.ParseOutput(AgyUsageFixture, "", 0, FixtureNow);
        var windows = parsed.Usage?.Windows ?? [];
        Check("agy usage fixture parses all groups and windows", parsed.Ok && windows.Select(w => w.Key).SequenceEqual(
            ["gemini:w300", "gemini:w10080", "claude_gpt:w300", "claude_gpt:w10080"]), DescribeWindows(windows));
        var weekly = windows.FirstOrDefault(w => w.Key == "gemini:w10080");
        Check("Gemini weekly used percent is inverted remaining fraction",
            weekly != null && Math.Abs(weekly.UsedPercent - (1 - 0.9636138081550598) * 100) < 1e-9 &&
            weekly.ResetsAt == DateTimeOffset.Parse("2026-10-21T01:22:12Z"), $"{weekly?.UsedPercent}/{weekly?.ResetsAt:o}");
        Check("agy usage labels distinguish the Claude and GPT group",
            windows.Select(w => w.Label).SequenceEqual(["5시간 한도", "주간 한도", "Claude·GPT · 5시간 한도", "Claude·GPT · 주간 한도"]),
            string.Join(",", windows.Select(w => w.Label)));
        Check("agy usage source and warning", parsed.Usage?.Source == AgyCli.Source && parsed.Usage.Warning == null && parsed.Error == null,
            $"{parsed.Usage?.Source}/{parsed.Usage?.Warning}/{parsed.Error}");

        var rows = Presenter.DisplayUsage(windows, FixtureNow);
        Check("existing display rule picks the Gemini weekly window", rows.Count == 1 && rows[0].Label == "주간 한도" &&
            Math.Abs(rows[0].Value - weekly!.UsedPercent) < 1e-9, string.Join(",", rows.Select(r => $"{r.Label}={r.Value}")));

        // remaining_fraction 생략: 리셋 전이면 소진(100%), 리셋 시각이 없거나 지났으면 제외
        var mixed = Usage("""
            [{"name":"Gemini Models","buckets":[
              {"id":"gemini-weekly","window":"weekly","reset_time":"2026-10-21T01:22:12Z"},
              {"id":"gemini-1h","window":"1h"},
              {"id":"gemini-2h","window":"2h","reset_time":"2026-10-01T00:00:00Z"},
              {"id":"gemini-5h","window":"5h","remaining_fraction":{"remainingFraction":0.25}},
              {"id":"gemini-daily","window":"daily","remaining_fraction":"0.5","disabled":true},
              {"id":"gemini-monthly","name":"Monthly","window":"monthly","remaining_fraction":1.2}]}]
            """);
        Check("omitted fraction before reset is treated as exhausted",
            mixed.Windows.FirstOrDefault(w => w.Key == "gemini:w10080")?.UsedPercent == 100 &&
            mixed.Warning?.Contains(AgyCli.InferredExhaustedWarning) == true && mixed.Warning.Contains("도달"),
            $"{DescribeWindows(mixed.Windows)} warning={mixed.Warning}");
        Check("omitted fraction without a future reset is excluded with a warning",
            mixed.Windows.All(w => w.Key is not ("gemini:w60" or "gemini:w120")) && mixed.Warning?.StartsWith(AgyCli.MissingFractionWarning) == true,
            $"{DescribeWindows(mixed.Windows)} warning={mixed.Warning}");
        Check("nested remaining fraction is read", mixed.Windows.FirstOrDefault(w => w.Key == "gemini:w300")?.UsedPercent == 75,
            DescribeWindows(mixed.Windows));
        Check("disabled bucket is skipped", mixed.Windows.All(w => w.Key != "gemini:w1440"), DescribeWindows(mixed.Windows));
        Check("unknown window keeps bucket id and clamps fraction",
            mixed.Windows.FirstOrDefault(w => w.Key == "gemini:gemini_monthly") is { UsedPercent: 0, Label: "Monthly" },
            DescribeWindows(mixed.Windows));

        var otherGroup = Usage("""
            [{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.5}]},
             {"name":"Claude and GPT models","buckets":[{"id":"3p-weekly","window":"weekly","remaining_fraction":0},
               {"id":"3p-5h","window":"5h","reset_time":"2026-10-15T05:00:00Z"}]}]
            """);
        Check("exhausted other group does not raise the Gemini limit warning",
            otherGroup.Windows.FirstOrDefault(w => w.Key == "claude_gpt:w10080")?.UsedPercent == 100 &&
            otherGroup.Windows.FirstOrDefault(w => w.Key == "claude_gpt:w300")?.UsedPercent == 100 && otherGroup.Warning == null,
            $"warning={otherGroup.Warning}");

        var exhausted = Usage("""[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0}]}]""");
        Check("exhausted Gemini bucket shows 100% with a limit warning",
            exhausted.Windows.Single().UsedPercent == 100 && exhausted.Warning == AgyCli.ExhaustedWarning, $"warning={exhausted.Warning}");
        var weeklyOnly = Usage("""[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.4}]}]""");
        Check("weekly-only account is supported", weeklyOnly.Windows.Single().Key == "gemini:w10080" && weeklyOnly.Warning == null,
            DescribeWindows(weeklyOnly.Windows));
        var duplicate = Usage("""
            [{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.4},
              {"id":"gemini-weekly-pro","window":"weekly","remaining_fraction":0.9}]}]
            """);
        Check("buckets with the same window keep both values",
            duplicate.Windows.Select(w => w.Key).SequenceEqual(["gemini:w10080", "gemini:w10080:gemini_weekly_pro"]) &&
            Math.Abs(duplicate.Windows[1].UsedPercent - 10) < 1e-9, DescribeWindows(duplicate.Windows));

        var noWindows = AgyCli.ParseOutput("""
            {"conversation_id":"","status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"x","disabled":true}]}]}}}
            """, "", 0, FixtureNow);
        Check("success without any quota window is a format change", noWindows is { Ok: false, LoginRequired: false, Unsupported: false } &&
            noWindows.Error == AgyCli.FormatChangedMessage, noWindows.Error ?? "");

        // 어느 버킷에도 remaining_fraction 이 없으면 필드 이름이 바뀐 것으로 보고 소진으로 추정하지 않는다
        var renamed = AgyCli.ParseOutput("""
            {"conversation_id":"","status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[
              {"id":"gemini-weekly","window":"weekly","remainingRatio":0.4,"reset_time":"2026-10-21T01:22:12Z"},
              {"id":"gemini-5h","window":"5h","remainingRatio":0.9,"reset_time":"2026-10-15T03:53:26Z"}]}]}}}
            """, "", 0, FixtureNow);
        Check("response without any remaining_fraction is a format change, not exhausted",
            renamed is { Ok: false, Unsupported: false, Error: AgyCli.FormatChangedMessage } && renamed.Usage == null, renamed.Error ?? "");
        var noFractionUsage = Usage("""[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","reset_time":"2026-10-21T01:22:12Z"}]}]""");
        Check("no inferred limit warning without any measured bucket",
            noFractionUsage.Windows.Count == 0 && noFractionUsage.Warning == AgyCli.MissingFractionWarning, $"warning={noFractionUsage.Warning}");

        // 머리말 뒤에 여러 줄로 들여 쓴 JSON
        var pretty = JsonSerializer.Serialize(JsonDocument.Parse(AgyUsageFixture).RootElement, new JsonSerializerOptions { WriteIndented = true });
        var multiLine = AgyCli.ParseOutput("agy 1.3.5\nChecking quota...\n" + pretty + "\n", "", 0, FixtureNow);
        Check("indented JSON after a header is parsed", multiLine.Ok && multiLine.Usage?.Windows.Count == 4, multiLine.Error ?? "");
        var braceHeader = AgyCli.ParseOutput("Loading profile {default}...\n" + pretty + "\nDone {ok}\n", "", 0, FixtureNow);
        Check("JSON after a header containing braces is parsed", braceHeader.Ok && braceHeader.Usage?.Windows.Count == 4, braceHeader.Error ?? "");

        // 프롬프트로 실행된 출력: 미지원으로 분류하고 응답 본문을 남기지 않는다
        var prompt = AgyCli.ParseOutput(AgyPromptOutput, "", 0, FixtureNow);
        Check("prompt run is classified as unsupported without its response",
            prompt is { Ok: false, Unsupported: true, Error: AgyCli.UnsupportedMessage } && prompt.Error?.Contains("Hello") == false, prompt.Error ?? "");
        var turns = AgyCli.ParseOutput(AgyUsageFixture.Replace("\"num_turns\":0", "\"num_turns\":2"), "", 0, FixtureNow);
        var tokens = AgyCli.ParseOutput(AgyUsageFixture.Replace("\"total_tokens\":0", "\"total_tokens\":7"), "", 0, FixtureNow);
        var otherCommand = AgyCli.ParseOutput(AgyUsageFixture.Replace("\"name\":\"usage\"", "\"name\":\"model\""), "", 0, FixtureNow);
        var plainText = AgyCli.ParseOutput("Hello! How can I assist you with your project today?\n", "", 0, FixtureNow);
        Check("turns, tokens, another command or plain text answers are unsupported",
            turns.Unsupported && tokens.Unsupported && otherCommand.Unsupported && plainText.Unsupported &&
            plainText.Error == AgyCli.UnsupportedMessage, $"{turns.Unsupported}/{tokens.Unsupported}/{otherCommand.Unsupported}/{plainText.Error}");

        var login = AgyCli.ParseOutput("""{"status":"ERROR","error":"You are not logged into Antigravity. Sign in as person@example.test"}""", "", 1);
        Check("not logged in is reported as login required without the account",
            login is { Ok: false, LoginRequired: true } && login.Error?.Contains("example.test") == false, login.Error ?? "");
        var structured = AgyCli.ParseOutput("""{"status":"UNAUTHENTICATED","error":"token expired"}""", "", 1);
        var code401 = AgyCli.ParseOutput("""{"status":"ERROR","error":{"code":401,"message":"rejected"}}""", "", 1);
        Check("structured status and 401 code mean login required", structured.LoginRequired && code401.LoginRequired,
            $"{structured.Error}/{code401.Error}");
        var structuredTimeout = AgyCli.ParseOutput("""{"status":"UNAUTHENTICATED","error":"token refresh timed out"}""", "", 1);
        Check("structured login code wins over network wording", structuredTimeout.LoginRequired, structuredTimeout.Error ?? "");
        var plainLogin = AgyCli.ParseOutput("You are not logged in.\nPlease sign in with agy to continue as person@example.test\n", "", 0);
        Check("plain-text login notice is login required, not unsupported",
            plainLogin is { LoginRequired: true, Unsupported: false, PlainTextLogin: true, Error: AgyCli.LoginRequiredMessage }, $"{plainLogin.Unsupported}/{plainLogin.Error}");
        var longPlain = AgyCli.ParseOutput("Please sign in. " + string.Concat(Enumerable.Repeat("Here is a long answer about your project. ", 10)), "", 0);
        var manyLines = AgyCli.ParseOutput("Hello!\nYou are not logged in? Let me explain.\nFirst...\nSecond...\n", "", 0);
        Check("long or many-line plain text is unsupported even if it mentions sign-in",
            longPlain is { Unsupported: true, LoginRequired: false } && manyLines is { Unsupported: true, LoginRequired: false },
            $"{longPlain.Unsupported}/{manyLines.Unsupported}");
        string[] transient =
        [
            """{"status":"ERROR","error":"failed to sign in: dial tcp: lookup daily-cloudcode-pa.googleapis.com: i/o timeout"}""",
            """{"status":"ERROR","error":"request timed out while signing in; please sign in again later"}""",
            """{"status":"ERROR","error":"login server unreachable"}""",
            """{"status":"ERROR","error":"could not refresh model catalog in time"}""",
            """{"status":"ERROR","error":"OAuth credentials could not be refreshed"}""",
        ];
        var wrong = transient.Select(t => AgyCli.ParseOutput(t, "", 1)).Where(r => r.LoginRequired).Select(r => r.Error).ToList();
        Check("network and vague auth wording is not login required", wrong.Count == 0, string.Join(" | ", wrong));

        var agyError = AgyCli.ParseOutput("", "noise\nAGY_ERROR: {\"short_error\":\"model capacity exhausted\"}\n", 3);
        Check("stderr AGY_ERROR is used as the failure", agyError is { Ok: false, LoginRequired: false, Error: "model capacity exhausted" },
            agyError.Error ?? "");
        var empty = AgyCli.ParseOutput("", "", 2);
        Check("empty output names the exit code", empty is { Ok: false, Error: "agy 종료 코드 2" }, empty.Error ?? "");
        var noisy = AgyCli.ParseOutput("Checking for updates...\n" + AgyUsageFixture.Trim() + "\n", "", 0, FixtureNow);
        Check("JSON after progress lines is still parsed", noisy.Ok && noisy.Usage?.Windows.Count == 4, noisy.Error ?? "");
        var token = AgyCli.ParseOutput("""{"status":"ERROR","error":"backend rejected ya29.a0AfB_byC-secret-token-value"}""", "", 1);
        Check("token-like values are removed from errors", token.Error?.Contains("ya29") == false && token.Error.Contains("***"), token.Error ?? "");
        var pathError = AgyCli.ParseOutput(JsonSerializer.Serialize(new
        {
            status = "ERROR",
            error = $"cannot open {Path.Combine(Paths.Home, ".gemini", "x.db")} or {Paths.Home.Replace('\\', '/')}/y",
        }), "", 1);
        Check("user folder in errors is shortened to ~",
            pathError.Error?.Contains(Paths.Home, StringComparison.OrdinalIgnoreCase) == false && pathError.Error.Contains(@"~\.gemini\x.db") &&
            pathError.Error.Contains("~/y"), pathError.Error ?? "");

        static UsageSnapshot Usage(string groups)
        {
            using var doc = JsonDocument.Parse("{\"groups\":" + groups + "}");
            return AgyCli.ParseUsage(doc.RootElement, FixtureNow);
        }
    }

    static async Task TestAgyProcess()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Environment.ProcessPath!;
        var previousScenario = Environment.GetEnvironmentVariable(FakeAgyScenario);
        var previousDir = Environment.GetEnvironmentVariable(FakeAgyDirectory);
        Environment.SetEnvironmentVariable(FakeAgyDirectory, dir);
        void Scenario(string name)
        {
            Environment.SetEnvironmentVariable(FakeAgyScenario, name);
            foreach (var f in new[] { "pid.txt", "args.txt", "logpath.txt", "orphan.txt" }) File.Delete(Path.Combine(dir, f));
        }
        int Pid(string file = "pid.txt") => int.TryParse(ReadOrEmpty(Path.Combine(dir, file)), out var p) ? p : 0;
        try
        {
            Scenario("success");
            var ok = await AgyCli.QueryAsync(exe);
            var args = ReadOrEmpty(Path.Combine(dir, "args.txt")).Split('\n');
            Check("agy is run only with the read-only usage command",
                args.Take(5).SequenceEqual(["-p", "/usage", "--output-format", "json", "--log-file"]) && args.Length == 6, string.Join(" ", args));
            var logPath = ReadOrEmpty(Path.Combine(dir, "logpath.txt"));
            Check("agy usage query succeeds and removes its private log", ok.Ok && ok.Usage?.Windows.Count == 4 &&
                logPath.Length > 0 && !File.Exists(logPath), $"ok={ok.Ok}, error={ok.Error}, log={logPath}");
            var childData = ReadOrEmpty(Path.Combine(dir, "datadir.txt"));
            Check("fake agy child inherits the temporary data folder",
                childData.Length > 0 && childData == Environment.GetEnvironmentVariable(Paths.DataDirVariable) && childData == Paths.AppData,
                $"child={childData}, parent={Paths.AppData}");

            Scenario("hang");
            var watch = Stopwatch.StartNew();
            var hang = await AgyCli.QueryAsync(exe, timeout: TimeSpan.FromSeconds(2));
            Check("silent agy times out", hang is { Ok: false, Error: "agy 응답 시간 초과" } && watch.Elapsed < TimeSpan.FromSeconds(15),
                $"{hang.Error} after {watch.Elapsed.TotalSeconds:0.0}s");
            Check("timed-out agy process is terminated", await ExitedSoon(Pid()), $"pid={Pid()}");

            Scenario("hang");
            using (var lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500)))
            {
                var cancelled = await AgyCli.QueryAsync(exe, lifetime.Token);
                Check("cancelled agy query stops the process", cancelled.Error == "조회 취소됨" && await ExitedSoon(Pid()),
                    $"{cancelled.Error}, pid={Pid()}");
            }

            Scenario("flood");
            var flood = await AgyCli.QueryAsync(exe, timeout: TimeSpan.FromSeconds(20));
            Check("oversized agy output is rejected", flood is { Ok: false, Error: "agy 출력이 너무 큽니다." }, flood.Error ?? "");
            Check("oversized agy output terminates the process", await ExitedSoon(Pid()), $"pid={Pid()}");

            Scenario("slow");
            var slow = AgyCli.QueryAsync(exe);
            int childPid = 0;
            bool ownWhileRunning = false, hiddenFromScan = false;
            for (int i = 0; i < 100 && !slow.IsCompleted; i++)
            {
                childPid = Pid();
                if (childPid != 0)
                {
                    ownWhileRunning = ProcessUtil.IsOwnProcess(childPid);
                    var name = Path.GetFileNameWithoutExtension(exe);
                    var scan = ProcessUtil.ProcessesByName(name);
                    hiddenFromScan = scan.All(p => p.Pid != childPid) && scan.Any(p => p.Pid == Environment.ProcessId);
                    break;
                }
                await Task.Delay(50);
            }
            var slowResult = await slow;
            Check("monitor-started agy is excluded from process detection", slowResult.Ok && childPid != 0 && ownWhileRunning && hiddenFromScan,
                $"pid={childPid}, own={ownWhileRunning}, hidden={hiddenFromScan}, error={slowResult.Error}");
            Check("finished agy is no longer marked as own", !ProcessUtil.IsOwnProcess(childPid), $"pid={childPid}");

            Scenario("orphan-pipe");
            watch.Restart();
            var orphan = await AgyCli.QueryAsync(exe, timeout: TimeSpan.FromSeconds(25));
            Check("child holding the output pipe does not block the result", orphan.Ok && watch.Elapsed < TimeSpan.FromSeconds(15),
                $"ok={orphan.Ok}, error={orphan.Error}, elapsed={watch.Elapsed.TotalSeconds:0.0}s");
            // 테스트가 직접 끝내지 않는다 — Job 을 닫으면 남은 자식도 함께 끝나야 한다
            var orphanPid = Pid("orphan.txt");
            Check("orphaned agy child exits after the result", orphanPid != 0 && await ExitedSoon(orphanPid), $"orphan pid={orphanPid}");

            Scenario("login");
            var login = await AgyCli.QueryAsync(exe);
            Check("agy login failure is reported as login required", login is { Ok: false, LoginRequired: true }, login.Error ?? "");

            Scenario("prompt");
            var prompt = await AgyCli.QueryAsync(exe);
            Check("agy answering as a prompt is unsupported", prompt is { Ok: false, Unsupported: true, Error: AgyCli.UnsupportedMessage }, prompt.Error ?? "");

            Check("explicit agy override is used when it exists", AgyCli.FindExecutable(exe) == exe, AgyCli.FindExecutable(exe) ?? "null");
            var missingPath = Path.Combine(dir, "missing-agy.exe");
            var missing = await AgyCli.QueryAsync(missingPath);
            Check("missing agy override is an error instead of another agy",
                AgyCli.FindExecutable(missingPath) == null && missing is { Ok: false, Error: "지정한 agy 경로를 찾지 못했습니다." }, missing.Error ?? "");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeAgyScenario, previousScenario);
            Environment.SetEnvironmentVariable(FakeAgyDirectory, previousDir);
            TryDelete(dir);
        }
    }

    static async Task<bool> ExitedSoon(int pid)
    {
        if (pid == 0) return false;
        for (int i = 0; i < 50; i++)
        {
            if (!ProcessUtil.IsAlive(pid, null)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return ""; }
    }

    const string SummariesSchema = """
        CREATE TABLE conversation_summaries (conversation_id TEXT, title TEXT, preview TEXT, step_count INTEGER,
          last_modified_time datetime, workspace_uris TEXT, status TEXT, source TEXT, project_id TEXT, agent_name TEXT,
          parent_conversation_id TEXT, nesting_depth INTEGER, battle_id TEXT, winning_conversation_id TEXT,
          not_fully_idle numeric, killed numeric, last_user_input_time datetime, last_user_input_step_index INTEGER,
          app_data_dir TEXT, raw_summary BLOB, group_id TEXT);
        """;

    static string SummaryTime(DateTime t) =>
        t.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) + "+00:00";

    static string SummaryInsert(string id, string title, string modified, string workspaces, string status,
        int killed = 0, int notFullyIdle = 0, string parent = "")
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        return "INSERT INTO conversation_summaries (conversation_id, title, preview, last_modified_time, workspace_uris, status, " +
            "parent_conversation_id, not_fully_idle, killed, raw_summary) VALUES (" +
            $"{Q(id)}, {Q(title)}, 'SECRET-PREVIEW', {Q(modified)}, {Q(workspaces)}, {Q(status)}, {Q(parent)}, {notFullyIdle}, {killed}, x'00');";
    }

    static void TestGeminiLocal()
    {
        var home = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var db = Path.Combine(home, "conversation_summaries.db");
        try
        {
            var now = DateTime.UtcNow;
            FixtureSql(db, SummariesSchema + string.Concat(
                SummaryInsert("running", "실행 중", SummaryTime(now.AddMinutes(-1)), """["file:///C:/Work/alpha"]""", "CASCADE_RUN_STATUS_RUNNING"),
                SummaryInsert("recent-busy", "", SummaryTime(now.AddMinutes(-5)), """["file:///C:/Work/My%20Proj","file:///C:/Other"]""", "CASCADE_RUN_STATUS_BUSY"),
                SummaryInsert("stale-running", "멈춘 작업", SummaryTime(now.AddMinutes(-45)), "", "CASCADE_RUN_STATUS_RUNNING"),
                SummaryInsert("old-busy", "오래된 작업", SummaryTime(now.AddDays(-3)), "", "CASCADE_RUN_STATUS_BUSY"),
                SummaryInsert("idle", "끝남", SummaryTime(now.AddMinutes(-30)), "", "CASCADE_RUN_STATUS_IDLE"),
                SummaryInsert("old-idle", "오래됨", SummaryTime(now.AddDays(-3)), "", "CASCADE_RUN_STATUS_IDLE"),
                SummaryInsert("killed", "중단", SummaryTime(now.AddMinutes(-10)), "", "CASCADE_RUN_STATUS_IDLE", killed: 1),
                SummaryInsert("canceling", "취소 중", SummaryTime(now.AddMinutes(-1)), "", "CASCADE_RUN_STATUS_CANCELING"),
                SummaryInsert("not-idle", "마무리 중", SummaryTime(now.AddMinutes(-1)), "", "CASCADE_RUN_STATUS_IDLE", notFullyIdle: 1),
                SummaryInsert("subagent", "하위 작업", SummaryTime(now.AddMinutes(-1)), "", "CASCADE_RUN_STATUS_RUNNING", parent: "running"),
                SummaryInsert("zero-time", "시각 없음", "0001-01-01 00:00:00+00:00", "", "CASCADE_RUN_STATUS_IDLE")));

            int processes = 1;
            var reader = new GeminiLocalReader(home, _ => @"C:\fake\agy.exe", () => processes);
            var state = reader.Read(12);
            SessionInfo? S(string id) => state.Sessions.FirstOrDefault(s => s.Id == "agy:" + id);
            Check("Gemini local state reports installation and processes",
                state is { Installed: true, ExecutablePath: @"C:\fake\agy.exe", CliProcessCount: 1, SessionsError: null }, state.SessionsError ?? "");
            Check("running conversation is working", S("running") is { State: SessionState.Working, StateText: "작업 중", Subtitle: "alpha · Antigravity CLI" },
                $"{S("running")?.State}/{S("running")?.Subtitle}");
            Check("recent busy conversation uses the workspace folder as title",
                S("recent-busy") is { State: SessionState.Working, Title: "My Proj", Subtitle: "My Proj · Antigravity CLI" },
                $"{S("recent-busy")?.Title}/{S("recent-busy")?.Subtitle}");
            // 진행 중 기록이 30분 넘게 바뀌지 않으면 비정상 종료로 남은 기록으로 본다 (agy 가 다른 대화로 실행 중이어도)
            Check("running record unchanged for 30 minutes is stopped, not working",
                S("stale-running") is { State: SessionState.Stale, StateText: "중단됨" }, $"{S("stale-running")?.State}/{S("stale-running")?.StateText}");
            Check("idle, killed, canceling and not-fully-idle states are mapped",
                S("idle")?.StateText == "대기" && S("killed")?.StateText == "중단됨" && S("canceling")?.StateText == "중단 중" &&
                S("not-idle")?.State == SessionState.Working, string.Join(",", state.Sessions.Select(s => $"{s.Id}={s.StateText}")));
            // 이전에는 오래된 BUSY 기록을 진행 중으로 계속 보였지만, 30분 기준으로 중단됨이 되어 12시간 목록에서 빠진다
            Check("old busy, old idle, subagent and timeless conversations are excluded",
                S("old-busy") == null && S("old-idle") == null && S("subagent") == null && S("zero-time") == null && state.Sessions.Count == 7,
                string.Join(",", state.Sessions.Select(s => s.Id)));
            Check("conversation activity time is parsed", S("idle")?.LastActivity is { } at && Math.Abs((at.UtcDateTime - now.AddMinutes(-30)).TotalSeconds) < 1,
                $"{S("idle")?.LastActivity:o}");
            Check("summary query does not read previews, summaries or steps",
                !GeminiLocalReader.SummaryQuery.Contains("preview", StringComparison.OrdinalIgnoreCase) &&
                !GeminiLocalReader.SummaryQuery.Contains("raw_summary", StringComparison.OrdinalIgnoreCase) &&
                !GeminiLocalReader.SummaryQuery.Contains("steps", StringComparison.OrdinalIgnoreCase), GeminiLocalReader.SummaryQuery);

            processes = 0;
            state = reader.Read(12);
            Check("working record without agy is not shown as working",
                S("running") is { State: SessionState.Stale, StateText: "중단됨" } && S("canceling") is { State: SessionState.Idle, StateText: "중단됨" } &&
                state.Sessions.Count == 7, $"{S("running")?.State}/{S("canceling")?.State}/{state.Sessions.Count}");

            // 다른 연결이 쓰기 잠금을 잡고 있으면 이전 목록을 유지한다
            processes = 1;
            reader.Read(12);
            var locker = OpenFixture(db);
            try
            {
                Check("fixture lock acquired", Exec(locker, "BEGIN EXCLUSIVE; UPDATE conversation_summaries SET title='잠김';") == 0, "lock failed");
                File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddSeconds(5));
                var locked = reader.Read(12);
                Check("locked summaries keep the previous list", locked.SessionsError != null && locked.Sessions.Count == 7 &&
                    locked.Sessions.All(s => s.Title != "잠김"), $"error={locked.SessionsError}, count={locked.Sessions.Count}");
                Exec(locker, "ROLLBACK;");
            }
            finally { SqliteCloseFixture(locker); }
            File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddSeconds(10));
            var recovered = reader.Read(12);
            Check("summaries are read again after the lock is released", recovered.SessionsError == null && recovered.Sessions.Count == 7,
                recovered.SessionsError ?? "");

            var missing = new GeminiLocalReader(Path.Combine(home, "missing"), _ => null, () => 0).Read(12);
            Check("missing agy and database is a quiet not-installed state",
                missing is { Installed: false, ExecutablePath: null, SessionsError: null } && missing.Sessions.Count == 0, missing.SessionsError ?? "");

            TestGeminiWalSummaries(home);
        }
        finally
        {
            TryDelete(home);
        }
    }

    /// <summary>agy 는 summaries DB 를 WAL 모드로 쓴다. 쓰는 중에도, agy 가 닫혀 -wal/-shm 이 없을 때도 읽기 전용으로 읽혀야 한다.</summary>
    static void TestGeminiWalSummaries(string root)
    {
        var home = Path.Combine(root, "wal");
        Directory.CreateDirectory(home);
        var db = Path.Combine(home, "conversation_summaries.db");
        string[] Files() => Directory.GetFiles(home).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;
        var writer = OpenFixture(db);
        try
        {
            Check("WAL fixture is created",
                Exec(writer, "PRAGMA journal_mode=WAL;" + SummariesSchema +
                    SummaryInsert("wal-running", "WAL 작업", SummaryTime(DateTime.UtcNow.AddMinutes(-1)), "", "CASCADE_RUN_STATUS_RUNNING")) == 0 &&
                File.Exists(db + "-wal"), string.Join(",", Files()));
            var before = Files();
            var live = new GeminiLocalReader(home, _ => @"C:\fake\agy.exe", () => 1).Read(12);
            Check("WAL summaries are read while the writer is open",
                live.SessionsError == null && live.Sessions.SingleOrDefault()?.Id == "agy:wal-running", live.SessionsError ?? "");
            Check("reading WAL summaries does not add files", Files().SequenceEqual(before), string.Join(",", Files()));
        }
        finally { SqliteCloseFixture(writer); }

        var closed = Files();
        var afterClose = new GeminiLocalReader(home, _ => @"C:\fake\agy.exe", () => 1).Read(12);
        Check("WAL summaries are read after the writer closed",
            afterClose.SessionsError == null && afterClose.Sessions.SingleOrDefault()?.Id == "agy:wal-running",
            $"error={afterClose.SessionsError}, files={string.Join(",", closed)}");
        Check("read-only open does not create WAL files in the agy folder", Files().SequenceEqual(closed),
            $"before={string.Join(",", closed)}, after={string.Join(",", Files())}");
    }

    static async Task TestGeminiMonitor()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var previousScenario = Environment.GetEnvironmentVariable(FakeAgyScenario);
        var previousDir = Environment.GetEnvironmentVariable(FakeAgyDirectory);
        Environment.SetEnvironmentVariable(FakeAgyDirectory, dir);
        int saves = 0;
        // 사용자 settings.json 을 건드리지 않도록 저장은 횟수만 센다
        void Save(AppSettings _) => saves++;
        var exe = Environment.ProcessPath!;
        try
        {
            using var auto = new MonitorService(new AppSettings(), Save);
            Check("Gemini is off until agy is found", !auto.GeminiEnabled, "enabled without local state");
            SetProperty(auto, nameof(MonitorService.GeminiLocal), new GeminiLocalState { Installed = true });
            Check("Gemini turns on automatically when agy is installed", auto.GeminiEnabled, "not enabled");
            using var off = new MonitorService(new AppSettings { GeminiEnabled = false }, Save);
            SetProperty(off, nameof(MonitorService.GeminiLocal), new GeminiLocalState { Installed = true });
            Check("explicit Gemini off wins over detection", !off.GeminiEnabled, "enabled");

            var settings = new AppSettings { GeminiEnabled = true, AgyExePath = exe, GeminiPollSeconds = 30 };
            using var monitor = new MonitorService(settings, Save);
            var next = PrivateField<MonitorService>("_nextGemini");
            double Delay() => ((DateTime)next.GetValue(monitor)! - DateTime.UtcNow).TotalSeconds;

            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            await monitor.RefreshGeminiRemoteAsync();
            Check("successful Gemini query is kept with the minimum poll interval",
                monitor.GeminiRemoteGood?.Usage?.Windows.Count == 4 && !monitor.GeminiRemoteBusy && Delay() is > 100 and <= 120,
                $"good={monitor.GeminiRemoteGood?.Ok}, delay={Delay():0}");

            Environment.SetEnvironmentVariable(FakeAgyScenario, "error");
            await monitor.RefreshGeminiRemoteAsync();
            Check("first Gemini failure backs off for ten minutes and keeps the last usage",
                monitor.GeminiRemoteLast is { Ok: false } && monitor.GeminiRemoteGood?.Ok == true && Delay() is > 590 and <= 600, $"delay={Delay():0}");
            await monitor.RefreshGeminiRemoteAsync();
            Check("repeated Gemini failure backs off for thirty minutes", Delay() is > 1790 and <= 1800, $"delay={Delay():0}");

            Environment.SetEnvironmentVariable(FakeAgyScenario, "login");
            await monitor.RefreshGeminiRemoteAsync();
            Check("Gemini login requirement pauses polling for an hour and clears usage",
                monitor.GeminiPollingPaused && monitor.GeminiRemoteGood == null && monitor.GeminiRemoteLast?.LoginRequired == true &&
                Delay() is > 3590 and <= 3600, $"delay={Delay():0}, error={monitor.GeminiRemoteLast?.Error}");
            var pausedLast = monitor.GeminiRemoteLast;
            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            await monitor.RefreshGeminiRemoteAsync();
            Check("automatic refresh waits while login is required", ReferenceEquals(monitor.GeminiRemoteLast, pausedLast), "query ran");
            next.SetValue(monitor, DateTime.UtcNow.AddSeconds(-1));
            await monitor.RefreshGeminiRemoteAsync();
            Check("hourly retry resumes Gemini polling after sign-in", !monitor.GeminiPollingPaused && monitor.GeminiRemoteGood?.Ok == true,
                monitor.GeminiRemoteLast?.Error ?? "");
            Environment.SetEnvironmentVariable(FakeAgyScenario, "login");
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Check("manual refresh resumes Gemini polling", !monitor.GeminiPollingPaused && monitor.GeminiRemoteGood?.Ok == true, monitor.GeminiRemoteLast?.Error ?? "");

            // 외부 agy 종료로 앞당기는 조회는 시간당 6회까지
            var trigger = PrivateMethod<MonitorService>("OnExternalAgyFinished");
            var lastQuery = PrivateField<MonitorService>("_lastGeminiQuery");
            lastQuery.SetValue(monitor, DateTime.UtcNow.AddHours(-1));
            int advanced = 0;
            for (int i = 0; i < 9; i++)
            {
                next.SetValue(monitor, DateTime.UtcNow.AddHours(1));
                trigger.Invoke(monitor, null);
                if (Delay() < 60) advanced++;
            }
            Check("external agy exits advance the query at most six times an hour", advanced == 6, $"advanced={advanced}");

            // 프롬프트로 실행되면 같은 agy 로는 다시 실행하지 않는다 (재시작 후에도)
            Environment.SetEnvironmentVariable(FakeAgyScenario, "prompt");
            int savesBefore = saves;
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Check("prompt-like agy output blocks Gemini with a saved fingerprint",
                monitor.GeminiUnsupported && monitor.GeminiUnsupportedReason == AgyCli.UnsupportedMessage &&
                settings.AgyBlockedFingerprint == AgyCli.Fingerprint(exe) && saves == savesBefore + 1 && monitor.GeminiRemoteGood == null &&
                monitor.GeminiRemoteLast?.Error?.Contains("Hello") == false,
                $"unsupported={monitor.GeminiUnsupported}, fp={settings.AgyBlockedFingerprint}, saves={saves - savesBefore}");
            Check("unsupported agy also waits a day before any automatic query", Delay() > 86_000, $"delay={Delay():0}");
            var blockedLast = monitor.GeminiRemoteLast;
            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            next.SetValue(monitor, DateTime.UtcNow.AddSeconds(-1));
            await monitor.RefreshGeminiRemoteAsync();
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Check("blocked agy is not run automatically or manually", ReferenceEquals(monitor.GeminiRemoteLast, blockedLast), "query ran");
            using (var restarted = new MonitorService(settings, Save))
                Check("Gemini block survives a restart", restarted.GeminiUnsupported, "not blocked after restart");
            settings.AgyBlockedFingerprint = AgyCli.Fingerprint(exe) + "-before-update";
            Check("updated agy is no longer blocked", !monitor.GeminiUnsupported && monitor.GeminiUnsupportedReason == null, "still blocked");
            Environment.SetEnvironmentVariable(FakeAgyScenario, "error");
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Check("a failed retry after an update keeps the block record",
                monitor.GeminiRemoteLast is { Ok: false, Unsupported: false } && settings.AgyBlockedFingerprint != null, $"fp={settings.AgyBlockedFingerprint}");
            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Check("updated agy is retried and the block is cleared",
                monitor.GeminiRemoteGood?.Ok == true && settings.AgyBlockedFingerprint == null, $"fp={settings.AgyBlockedFingerprint}");

            // 사용자가 차단을 풀면 저장하고 바로 한 번 조회한다
            Environment.SetEnvironmentVariable(FakeAgyScenario, "prompt");
            await monitor.RefreshGeminiRemoteAsync(manual: true);
            Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
            savesBefore = saves;
            await monitor.ClearGeminiBlock();
            Check("ClearGeminiBlock removes the fingerprint, saves and queries once",
                !monitor.GeminiUnsupported && settings.AgyBlockedFingerprint == null && saves == savesBefore + 1 && monitor.GeminiRemoteGood?.Ok == true,
                $"fp={settings.AgyBlockedFingerprint}, saves={saves - savesBefore}, good={monitor.GeminiRemoteGood?.Ok}");

            // 지문을 얻지 못한 채 차단됐으면 지문을 다시 얻기 전까지 막는다
            using (var unknown = new MonitorService(new AppSettings { GeminiEnabled = true, AgyExePath = exe, AgyBlockedFingerprint = "unknown" }, Save))
            {
                SetProperty(unknown, nameof(MonitorService.GeminiLocal), new GeminiLocalState { Installed = true, ExecutablePath = exe });
                await unknown.RefreshGeminiRemoteAsync(manual: true);
                Check("block with an unknown fingerprint stays while the fingerprint is unknown",
                    unknown.GeminiUnsupported && unknown.GeminiRemoteLast == null, $"unsupported={unknown.GeminiUnsupported}");
                // 지문을 다시 얻어도 "unknown" 차단은 유지되고, ClearGeminiBlock 으로만 풀린다
                SetProperty(unknown, nameof(MonitorService.GeminiLocal),
                    new GeminiLocalState { Installed = true, ExecutablePath = exe, ExecutableFingerprint = AgyCli.Fingerprint(exe) });
                await unknown.RefreshGeminiRemoteAsync(manual: true);
                Check("unknown block stays even when the fingerprint is known again",
                    unknown.GeminiUnsupported && unknown.GeminiRemoteLast == null, $"unsupported={unknown.GeminiUnsupported}");
                Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
                await unknown.ClearGeminiBlock();
                Check("only ClearGeminiBlock lifts an unknown block", !unknown.GeminiUnsupported && unknown.GeminiRemoteGood?.Ok == true,
                    unknown.GeminiRemoteLast?.Error ?? "");
            }

            // 차단된 agy 가 업데이트되면 24시간 대기를 앞당겨 한 번 다시 확인한다
            using (var updated = new MonitorService(new AppSettings { GeminiEnabled = true, AgyExePath = exe, AgyBlockedFingerprint = "old|1|1" }, Save))
            {
                var updatedNext = PrivateField<MonitorService>("_nextGemini");
                var changed = PrivateMethod<MonitorService>("OnGeminiLocalChanged");
                double UpdatedDelay() => ((DateTime)updatedNext.GetValue(updated)! - DateTime.UtcNow).TotalSeconds;
                var before = new GeminiLocalState { Installed = true, ExecutableFingerprint = "old|1|1" };
                updatedNext.SetValue(updated, DateTime.UtcNow.AddHours(24));
                changed.Invoke(updated, [before, new GeminiLocalState { Installed = true, ExecutableFingerprint = "old|1|1" }]);
                bool unchangedWaits = UpdatedDelay() > 86_000;
                changed.Invoke(updated, [before, new GeminiLocalState { Installed = true, ExecutableFingerprint = "new|2|2" }]);
                Check("an agy update shortens the day-long wait after a block", unchangedWaits && UpdatedDelay() < 60,
                    $"unchanged waits={unchangedWaits}, delay={UpdatedDelay():0}");
                var settingsUnknown = new AppSettings { GeminiEnabled = true, AgyExePath = exe, AgyBlockedFingerprint = "unknown" };
                using var unknownUpdate = new MonitorService(settingsUnknown, Save);
                updatedNext.SetValue(unknownUpdate, DateTime.UtcNow.AddHours(24));
                changed.Invoke(unknownUpdate, [before, new GeminiLocalState { Installed = true, ExecutableFingerprint = "new|2|2" }]);
                Check("an unknown block is not retried on agy updates",
                    ((DateTime)updatedNext.GetValue(unknownUpdate)! - DateTime.UtcNow).TotalSeconds > 86_000, "retry scheduled");
            }

            // 평문 로그인 안내는 수동 새로고침 전까지 다시 실행하지 않는다
            using (var plain = new MonitorService(new AppSettings { GeminiEnabled = true, AgyExePath = exe }, Save))
            {
                var plainNext = PrivateField<MonitorService>("_nextGemini");
                Environment.SetEnvironmentVariable(FakeAgyScenario, "plain-login");
                await plain.RefreshGeminiRemoteAsync();
                var plainLast = plain.GeminiRemoteLast;
                Check("plain-text login waits for a manual refresh",
                    plain.GeminiWaitingForManualRefresh && plain.GeminiPollingPaused && plainLast is { LoginRequired: true, PlainTextLogin: true },
                    plainLast?.Error ?? "");
                Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
                plainNext.SetValue(plain, DateTime.UtcNow.AddSeconds(-1));
                await plain.RefreshGeminiRemoteAsync();
                PrivateMethod<MonitorService>("OnExternalAgyFinished").Invoke(plain, null);
                Check("no automatic retry after a plain-text login notice",
                    ReferenceEquals(plain.GeminiRemoteLast, plainLast) && ((DateTime)plainNext.GetValue(plain)! - DateTime.UtcNow).TotalSeconds < 0,
                    "query ran or was rescheduled");
                await plain.RefreshGeminiRemoteAsync(manual: true);
                Check("manual refresh resumes after a plain-text login notice",
                    !plain.GeminiWaitingForManualRefresh && plain.GeminiRemoteGood?.Ok == true, plain.GeminiRemoteLast?.Error ?? "");
            }

            // 설정 저장은 주입된 저장 방법을 쓴다 (사용자 settings.json 을 건드리지 않는다)
            using (var claudeSettings = new MonitorService(new AppSettings { ClaudeDesktopEnabled = true, ClaudeWebEnabled = false }, Save))
            {
                savesBefore = saves;
                claudeSettings.SetClaudeDesktopEnabled(false);
                Check("Claude desktop toggle uses the injected settings saver", saves == savesBefore + 1, $"saves={saves - savesBefore}");
            }

            // 앱이 끝나면(Dispose) 진행 중인 조회를 취소하고 새 조회를 시작하지 않는다
            using (var closing = new MonitorService(new AppSettings { GeminiEnabled = true, AgyExePath = exe }, Save))
            {
                Environment.SetEnvironmentVariable(FakeAgyScenario, "hang");
                File.Delete(Path.Combine(dir, "pid.txt"));
                var running = closing.RefreshGeminiRemoteAsync();
                int hangPid = 0;
                for (int i = 0; i < 100 && hangPid == 0; i++)
                {
                    await Task.Delay(50);
                    hangPid = int.TryParse(ReadOrEmpty(Path.Combine(dir, "pid.txt")), out var p) ? p : 0;
                }
                var watch = Stopwatch.StartNew();
                closing.Dispose();
                await running;
                Check("dispose cancels the running agy query",
                    closing.GeminiRemoteLast?.Error == "조회 취소됨" && watch.Elapsed < TimeSpan.FromSeconds(10) && await ExitedSoon(hangPid),
                    $"error={closing.GeminiRemoteLast?.Error}, pid={hangPid}, elapsed={watch.Elapsed.TotalSeconds:0.0}s");
                var afterDispose = closing.GeminiRemoteLast;
                await closing.RefreshGeminiRemoteAsync(manual: true);
                Check("no Gemini query starts after dispose", ReferenceEquals(closing.GeminiRemoteLast, afterDispose), "query ran");
            }

            using var throwing = new MonitorService(new AppSettings { GeminiEnabled = true, AgyExePath = exe,
                CodexExePath = Path.Combine(dir, "missing-codex.exe") }, Save);
            throwing.Changed += () => throw new InvalidOperationException("fixture subscriber failure");
            var codexFailure = Observe(throwing.RefreshCodexRemoteAsync());
            Check("Codex busy flag is cleared when a Changed subscriber throws",
                codexFailure is InvalidOperationException && !throwing.CodexRemoteBusy, $"busy={throwing.CodexRemoteBusy}");
            var geminiFailure = Observe(throwing.RefreshGeminiRemoteAsync());
            Check("Gemini busy flag is cleared when a Changed subscriber throws",
                geminiFailure is InvalidOperationException && !throwing.GeminiRemoteBusy, $"busy={throwing.GeminiRemoteBusy}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeAgyScenario, previousScenario);
            Environment.SetEnvironmentVariable(FakeAgyDirectory, previousDir);
            TryDelete(dir);
        }
    }

    // 테스트용 SQLite 파일은 Windows 기본 winsqlite3 로 직접 만든다.
    static void FixtureSql(string path, string sql)
    {
        var db = OpenFixture(path);
        try
        {
            if (Exec(db, sql) != 0) throw new IOException("fixture SQL failed");
        }
        finally { SqliteCloseFixture(db); }
    }

    static IntPtr OpenFixture(string path)
    {
        if (SqliteOpenFixture(path, out var db, 6, IntPtr.Zero) != 0) throw new IOException("fixture database open failed"); // READWRITE | CREATE
        return db;
    }

    static int Exec(IntPtr db, string sql) => SqliteExecFixture(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteOpenFixture([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteExecFixture(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr error);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    static extern int SqliteCloseFixture(IntPtr db);
}
