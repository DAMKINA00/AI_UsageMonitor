using System.IO;
using System.Reflection;
using System.Windows;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestFinishing()
    {
        TestNoRepeatedTemplate();
        TestGeminiUnblock().GetAwaiter().GetResult();
        TestGeminiLoginMessage();
        TestSettingsLoadSafety();
        TestSettingsLoadEdgeCases();
        TestReviewFollowUps();
    }

    /// <summary>잘못된 인코딩·사라진 파일·빈 파일·너무 큰 파일·백업 개수·저장 차단 로그 (임시 폴더에서만).</summary>
    static void TestSettingsLoadEdgeCases()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var retryHook = typeof(AppSettings).GetField("_readRetrying", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AppSettings).FullName, "_readRetrying");
        string[] Backups(string name) => Directory.GetFiles(dir, name + ".bak-*");
        try
        {
            // ANSI(cp949)로 저장한 한글("한글" = C7 D1 B1 DB)은 UTF-8 이 아니다: 깨진 채 읽지 않고 실패 → 백업 → 원본 보존
            var ansi = Path.Combine(dir, "ansi.json");
            byte[] ansiBytes = [.. "{\"SettingsVersion\": 2, \"AgyBlockedFingerprint\": \""u8, 0xC7, 0xD1, 0xB1, 0xDB, .. "\"}"u8];
            File.WriteAllBytes(ansi, ansiBytes);
            var ansiLoaded = AppSettings.Load(ansi);
            Check("an ANSI (cp949) encoded settings file fails instead of loading replacement characters, and the original is backed up",
                ansiLoaded is { LoadedFrom: AppSettings.LoadResult.Failed, Migrated: false, SaveBlocked: false, AgyBlockedFingerprint: null } &&
                Backups("ansi.json") is [var ansiBackup] && File.ReadAllBytes(ansiBackup).AsSpan().SequenceEqual(ansiBytes) &&
                File.ReadAllBytes(ansi).AsSpan().SequenceEqual(ansiBytes) && Tray.SettingsLoadNotice(ansiLoaded) != null,
                $"result={ansiLoaded.LoadedFrom}, fp={ansiLoaded.AgyBlockedFingerprint}, backups={Backups("ansi.json").Length}");
            // BOM 이 있어도 같은 엄격 디코딩: UTF-8 BOM 뒤의 cp949 바이트, 짝이 맞지 않는 UTF-16 서로게이트
            var bomAnsi = Path.Combine(dir, "bom-ansi.json");
            File.WriteAllBytes(bomAnsi, [0xEF, 0xBB, 0xBF, .. ansiBytes]);
            var badUtf16 = Path.Combine(dir, "bad-utf16.json");
            File.WriteAllBytes(badUtf16, [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("{\"A\": \""), 0x00, 0xD8, .. System.Text.Encoding.Unicode.GetBytes("\"}")]);
            Check("invalid bytes after a UTF-8 or UTF-16 BOM also fail strictly",
                AppSettings.Load(bomAnsi).LoadedFrom == AppSettings.LoadResult.Failed && AppSettings.Load(badUtf16).LoadedFrom == AppSettings.LoadResult.Failed,
                "");

            // 확인한 뒤 읽기 전에 파일이나 폴더가 사라지면 없는 파일과 같다 (저장 차단 없음)
            var vanishing = Path.Combine(dir, "vanishing.json");
            File.WriteAllText(vanishing, "{\"Topmost\": false}");
            var hold = new FileStream(vanishing, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            retryHook.SetValue(null, (Action<int>)(_ => { hold.Dispose(); File.Delete(vanishing); }));
            AppSettings vanished;
            try { vanished = AppSettings.Load(vanishing); }
            finally { retryHook.SetValue(null, null); hold.Dispose(); }
            var subDir = Path.Combine(dir, "sub");
            Directory.CreateDirectory(subDir);
            var vanishingDirFile = Path.Combine(subDir, "settings.json");
            File.WriteAllText(vanishingDirFile, "{\"Topmost\": false}");
            var holdDir = new FileStream(vanishingDirFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            retryHook.SetValue(null, (Action<int>)(_ => { holdDir.Dispose(); Directory.Delete(subDir, true); }));
            AppSettings vanishedDir;
            try { vanishedDir = AppSettings.Load(vanishingDirFile); }
            finally { retryHook.SetValue(null, null); holdDir.Dispose(); }
            vanished.Save();
            Check("a settings file or folder that disappears before reading is treated as missing, not as a failure",
                vanished is { LoadedFrom: AppSettings.LoadResult.Missing, Migrated: true, SaveBlocked: false, BackupFile: null } &&
                vanishedDir is { LoadedFrom: AppSettings.LoadResult.Missing, Migrated: true, SaveBlocked: false } &&
                AppSettings.Load(vanishing).LoadedFrom == AppSettings.LoadResult.Loaded && Backups("vanishing.json").Length == 0,
                $"file={vanished.LoadedFrom}, dir={vanishedDir.LoadedFrom}");

            // 빈 파일·BOM 만 있는 파일·공백뿐인 파일은 없는 파일처럼: 기본값, 버전 2 저장, 경고·0바이트 백업 없음
            var emptyCases = new (string Name, byte[] Bytes)[]
            {
                ("empty.json", []),
                ("bom-only.json", [0xEF, 0xBB, 0xBF]),
                ("utf16-bom-only.json", [0xFF, 0xFE]),
                ("blank.json", " \r\n\t "u8.ToArray()),
            };
            var emptyResults = new List<string>();
            bool emptyOk = true;
            foreach (var (name, bytes) in emptyCases)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, bytes);
                var loaded = AppSettings.Load(path);
                if (loaded.Migrated) loaded.Save();
                var reloaded = AppSettings.Load(path);
                bool ok = loaded is { LoadedFrom: AppSettings.LoadResult.Missing, Migrated: true, SaveBlocked: false, BackupFile: null, ShowCharacters: false } &&
                    Tray.SettingsLoadNotice(loaded) == null && Backups(name).Length == 0 &&
                    reloaded is { LoadedFrom: AppSettings.LoadResult.Loaded, SettingsVersion: AppSettings.CurrentSettingsVersion };
                emptyOk &= ok;
                emptyResults.Add($"{name}={loaded.LoadedFrom}/{reloaded.LoadedFrom}");
            }
            Check("empty, BOM-only and whitespace-only settings start like a missing file and save version 2 without a backup",
                emptyOk, string.Join(", ", emptyResults));

            // 1MB 를 넘으면 읽지 않고 실패로 보되 원본은 복사해 백업한다
            var huge = Path.Combine(dir, "huge.json");
            using (var stream = File.Create(huge))
            {
                stream.Write("{\"Topmost\": true,"u8);
                var padding = new byte[AppSettings.MaxSettingsBytes];
                Array.Fill(padding, (byte)' ');
                stream.Write(padding);
                stream.Write("\"SettingsVersion\": 2}"u8);
            }
            var hugeLength = new FileInfo(huge).Length;
            var hugeLoaded = AppSettings.Load(huge);
            var hugeAgain = AppSettings.Load(huge);
            Check("a settings file over 1MB fails without being parsed, and is copied to a single backup",
                hugeLoaded is { LoadedFrom: AppSettings.LoadResult.Failed, Migrated: false, SaveBlocked: false, Topmost: false } &&
                Backups("huge.json") is [var hugeBackup] && new FileInfo(hugeBackup).Length == hugeLength &&
                hugeAgain.BackupFile == hugeLoaded.BackupFile && new FileInfo(huge).Length == hugeLength,
                $"result={hugeLoaded.LoadedFrom}, backups={Backups("huge.json").Length}");

            // 백업은 최근 5개만 남긴다 (오래된 것부터 지운다)
            var many = Path.Combine(dir, "many.json");
            for (int i = 1; i <= 6; i++) File.WriteAllText(Path.Combine(dir, $"many.json.bak-2020010{i}-000000"), $"old {i}");
            File.WriteAllText(many, "{ broken");
            var manyLoaded = AppSettings.Load(many);
            var kept = Backups("many.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Check("only the five most recent settings backups are kept",
                kept.Length == AppSettings.MaxBackups && manyLoaded.BackupFile != null && kept.Contains(Path.GetFileName(manyLoaded.BackupFile)) &&
                kept.Contains("many.json.bak-20200106-000000") && kept.Contains("many.json.bak-20200103-000000") &&
                !kept.Contains("many.json.bak-20200102-000000") && !kept.Contains("many.json.bak-20200101-000000"),
                string.Join(", ", kept));

            // 저장 차단 상태에서 여러 번 저장해도 로그는 한 번만
            var lockedForLog = Path.Combine(dir, "locked-log.json");
            File.WriteAllText(lockedForLog, "{\"Topmost\": false}");
            AppSettings blockedForLog;
            using (new FileStream(lockedForLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) blockedForLog = AppSettings.Load(lockedForLog);
            int SkipLines() => File.Exists(Paths.LogFile)
                ? File.ReadAllLines(Paths.LogFile).Count(l => l.Contains("settings save skipped", StringComparison.Ordinal)) : 0;
            var before = SkipLines();
            blockedForLog.Save();
            blockedForLog.Save();
            blockedForLog.Save();
            Check("a blocked save is logged only once per run",
                blockedForLog.SaveBlocked && SkipLines() - before == 1 && File.ReadAllText(lockedForLog) == "{\"Topmost\": false}",
                $"lines={SkipLines() - before}");
        }
        finally
        {
            retryHook.SetValue(null, null);
            TryDelete(dir);
        }
    }

    /// <summary>깨진 설정·잠긴 설정·없는 설정을 읽을 때 원본을 지키는지 (임시 폴더에서만).</summary>
    static void TestSettingsLoadSafety()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var corrupt = "{ \"SecondaryAccounts\": [ { \"Id\": \"abc\" } ], \"AgyBlockedFingerprint\": \"fp\", \"WindowLeft\": 10, ";
            File.WriteAllText(file, corrupt);
            var broken = AppSettings.Load(file);
            var backups = Directory.GetFiles(dir, "settings.json.bak-*");
            Check("a corrupt settings file is backed up and not treated as a migration",
                broken.LoadedFrom == AppSettings.LoadResult.Failed && !broken.Migrated && broken.BackupFile != null &&
                backups.Length == 1 && File.ReadAllText(backups[0]) == corrupt && File.ReadAllText(file) == corrupt &&
                broken.SettingsVersion == AppSettings.CurrentSettingsVersion,
                $"result={broken.LoadedFrom}, migrated={broken.Migrated}, backups={backups.Length}");
            var again = AppSettings.Load(file);
            Check("loading the same corrupt file again does not create another backup",
                again.BackupFile == broken.BackupFile && Directory.GetFiles(dir, "settings.json.bak-*").Length == 1,
                $"backups={Directory.GetFiles(dir, "settings.json.bak-*").Length}");
            again.ShowCharacters = true;
            again.Save();
            Check("after the backup a later save writes valid settings while the original stays in the backup",
                AppSettings.Load(file) is { LoadedFrom: AppSettings.LoadResult.Loaded, ShowCharacters: true } && File.ReadAllText(backups[0]) == corrupt,
                File.ReadAllText(file).Length.ToString());

            // 다른 프로그램이 계속 잠가 두면: 백업도 못 하므로 그 실행에서는 저장하지 않는다
            var locked = Path.Combine(dir, "locked.json");
            File.WriteAllText(locked, "{\"Topmost\": true, \"SettingsVersion\": 2}");
            AppSettings blocked;
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) blocked = AppSettings.Load(locked);
            blocked.Save();
            Check("an unreadable settings file is never overwritten", blocked.LoadedFrom == AppSettings.LoadResult.Failed && !blocked.Migrated &&
                blocked.BackupFile == null && File.ReadAllText(locked) == "{\"Topmost\": true, \"SettingsVersion\": 2}",
                $"result={blocked.LoadedFrom}");

            // 잠깐 잠긴 경우는 다시 읽어 성공한다. 첫 읽기가 실패한 것을 신호로 잠금을 풀어 시간에 기대지 않는다
            var brief = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var retryHook = typeof(AppSettings).GetField("_readRetrying", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(AppSettings).FullName, "_readRetrying");
            var failedAttempts = new List<int>();
            retryHook.SetValue(null, (Action<int>)(attempt => { failedAttempts.Add(attempt); brief.Dispose(); }));
            AppSettings retried;
            try { retried = AppSettings.Load(locked); }
            finally { retryHook.SetValue(null, null); brief.Dispose(); }
            Check("a briefly locked settings file is read after a short retry",
                retried is { LoadedFrom: AppSettings.LoadResult.Loaded, Topmost: true } && failedAttempts.SequenceEqual([0]),
                $"result={retried.LoadedFrom}, failed attempts=[{string.Join(",", failedAttempts)}]");

            // 메모장·PowerShell 이 붙이는 BOM 이 있어도 정상 파일로 읽는다 (백업 없음, 마이그레이션 규칙 그대로)
            var bomCurrent = Path.Combine(dir, "bom-current.json");
            File.WriteAllBytes(bomCurrent, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"ShowCharacters\": true, \"SettingsVersion\": 2}")]);
            var bomOld = Path.Combine(dir, "bom-old.json");
            File.WriteAllBytes(bomOld, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"ShowCharacters\": true, \"Topmost\": false}")]);
            var bomUtf16 = Path.Combine(dir, "bom-utf16.json");
            File.WriteAllText(bomUtf16, "{\"ShowCharacters\": true, \"SettingsVersion\": 2}", System.Text.Encoding.Unicode);
            var bomUtf16Be = Path.Combine(dir, "bom-utf16be.json");
            File.WriteAllText(bomUtf16Be, "{\"ShowCharacters\": true, \"SettingsVersion\": 2, \"AgyBlockedFingerprint\": \"한글\"}",
                System.Text.Encoding.BigEndianUnicode);
            var current = AppSettings.Load(bomCurrent);
            var old = AppSettings.Load(bomOld);
            var utf16 = AppSettings.Load(bomUtf16);
            var utf16Be = AppSettings.Load(bomUtf16Be);
            Check("a settings file with a BOM (UTF-8, UTF-16 LE/BE) loads normally without a backup and keeps the migration rules",
                File.ReadAllBytes(bomUtf16Be) is [0xFE, 0xFF, ..] &&
                current is { LoadedFrom: AppSettings.LoadResult.Loaded, Migrated: false, ShowCharacters: true, BackupFile: null } &&
                old is { LoadedFrom: AppSettings.LoadResult.Loaded, Migrated: true, ShowCharacters: false, Topmost: false, SettingsVersion: AppSettings.CurrentSettingsVersion } &&
                utf16 is { LoadedFrom: AppSettings.LoadResult.Loaded, Migrated: false, ShowCharacters: true } &&
                utf16Be is { LoadedFrom: AppSettings.LoadResult.Loaded, Migrated: false, ShowCharacters: true, AgyBlockedFingerprint: "한글" } &&
                Directory.GetFiles(dir, "bom-*.bak-*").Length == 0,
                $"current={current.LoadedFrom}, old={old.LoadedFrom}/{old.Migrated}, utf16={utf16.LoadedFrom}, utf16be={utf16Be.LoadedFrom}");
            current.Save();
            var savedBytes = File.ReadAllBytes(bomCurrent);
            Check("settings are still saved without a BOM", savedBytes.Length > 3 && savedBytes[0] == (byte)'{',
                $"first byte=0x{savedBytes[0]:X2}");

            // 읽지 못했을 때 시작 풍선 문구: 백업 파일 이름 / 저장하지 않음 / 정상이면 없음
            var backupNotice = Tray.SettingsLoadNotice(broken);
            Check("the startup notice names the backup in the data folder and warns that the next save resets settings and accounts",
                backupNotice == $"설정을 읽지 못해 기본값으로 시작했습니다. 원본 백업: 데이터 폴더의 {Path.GetFileName(broken.BackupFile)}. " +
                    "다음 저장 때 settings.json 이 기본값으로 바뀌고 추가 계정 연결 목록도 비게 됩니다." &&
                backupNotice.Length <= 200,
                $"{backupNotice?.Length}: {backupNotice ?? "null"}");
            Check("the startup notice says it started with defaults and changes are not saved when the file cannot be read or backed up",
                blocked.SaveBlocked && Tray.SettingsLoadNotice(blocked) == "설정 파일을 읽을 수 없어 기본값으로 시작했습니다. 이번 실행의 변경은 저장되지 않습니다.",
                Tray.SettingsLoadNotice(blocked) ?? "null");
            Check("there is no startup notice for normally read or missing settings",
                Tray.SettingsLoadNotice(current) == null && Tray.SettingsLoadNotice(retried) == null &&
                Tray.SettingsLoadNotice(AppSettings.Load(Path.Combine(dir, "none.json"))) == null && !current.SaveBlocked,
                "");

            // 파일이 없으면 기본값으로 시작하고, 시작할 때처럼 저장하면 버전 2 로 기록된다
            var missingFile = Path.Combine(dir, "missing.json");
            var fresh = AppSettings.Load(missingFile);
            if (fresh.Migrated) fresh.Save();
            var written = File.Exists(missingFile) ? AppSettings.Load(missingFile) : null;
            Check("a missing settings file starts with defaults and saves version 2",
                fresh is { LoadedFrom: AppSettings.LoadResult.Missing, Migrated: true, ShowCharacters: false } &&
                written is { LoadedFrom: AppSettings.LoadResult.Loaded, SettingsVersion: AppSettings.CurrentSettingsVersion, Migrated: false },
                $"written={written?.SettingsVersion}");
        }
        finally { TryDelete(dir); }
    }

    static void TestReviewFollowUps()
    {
        var now = DateTimeOffset.Now;

        // L7: 접미사가 붙은 창 키도 5시간·주간으로 알아본다
        var suffixed = Presenter.DisplayUsage([new UsageWindow("gemini:w300:pro", "5시간", 95, now.AddHours(1)),
            new UsageWindow("gemini:w10080:pro", "주간", 30, now.AddDays(2))], now);
        var weeklySuffixed = Presenter.DisplayUsage([new UsageWindow("gemini:w300:pro", "5시간", 20, now.AddHours(1)),
            new UsageWindow("gemini:w10080:pro", "주간", 30, now.AddDays(2))], now);
        Check("window keys with a suffix are still five-hour and weekly limits",
            suffixed is [{ Label: "5시간 한도", Value: 95 }] && weeklySuffixed is [{ Label: "주간 한도", Value: 30 }] &&
            Presenter.DisplayUsage([new UsageWindow("gemini:pro_w300x", "기타", 50, now.AddHours(1))], now).Count == 0,
            $"{DescribeRows(suffixed)} / {DescribeRows(weeklySuffixed)}");

        // L6: 사용자가 직접 차단을 푼 직후의 확인 조회는 중립 문구
        var cleared = new AppSettings { GeminiEnabled = true, AgyBlockedFingerprint = null };
        using (var monitor = GeminiMonitor(cleared, new GeminiLocalState { Installed = true, ExecutableFingerprint = "fp" }, null,
                   new GeminiRemoteResult { Unsupported = true, Error = AgyCli.UnsupportedMessage }, busy: true))
        {
            var card = Presenter.Gemini(monitor, cleared);
            Check("right after a manual unblock the card says it is checking again",
                card.Status.Kind == StatusKind.Busy && card.Notice == "Gemini 사용량을 다시 확인하는 중입니다." && card.Notice?.Contains("바뀌었") == false,
                $"{card.Status.Text}/{card.Notice}");
        }

        // 5: 평문 로그인 안내로 자동 조회를 멈췄으면 60분 문구 없이 새로고침만 안내한다
        var settings = new AppSettings { GeminiEnabled = true };
        using (var waiting = GeminiMonitor(settings, GeminiLocalFixture(now), null,
                   new GeminiRemoteResult { LoginRequired = true, PlainTextLogin = true, Error = AgyCli.LoginRequiredMessage }, paused: true))
        {
            SetProperty(waiting, nameof(MonitorService.GeminiWaitingForManualRefresh), true);
            var card = Presenter.Gemini(waiting, settings);
            var tip = card.Status.ToolTipText;
            Check("waiting for a manual refresh drops the hourly retry wording",
                card.Status.Text == "로그인 필요" && tip.StartsWith(AgyCli.LoginRequiredMessage) && tip.Contains("새로고침") && !tip.Contains("60분") &&
                card.Notice?.Contains("새로고침") == true, tip);
        }

        // L5: 캐릭터 열의 말풍선은 첫 배치부터 글자 폭 120 DIP 이상이고, 그림 크기가 바뀌면 다시 줄바꿈한다
        var stage = new CharacterStage { Character = "gemini", BubblePlacement = BubblePlacement.Left, FigureHeight = 150, Width = MainWindow.CharacterColumn };
        var textWidth = PrivateMethod<CharacterStage>("BubbleTextWidth");
        double before = (double)textWidth.Invoke(stage, null)!;
        PrivateMethod<CharacterStage>("ShowBubble").Invoke(stage, ["사주세요… 아니,\n승인해 주세요!"]);
        var bubbleText = (System.Windows.Controls.TextBlock)PrivateField<CharacterStage>("_bubbleText").GetValue(stage)!;
        stage.FigureHeight = 60;
        double after = (double)textWidth.Invoke(stage, null)!;
        Check("the side bubble has at least 120 DIP before layout and rewraps when the figure changes",
            before >= 120 && after > before && bubbleText.Text.Replace("\n", " ").Contains("승인해 주세요!"),
            $"before={before:0.#}, after={after:0.#}");

        // L2·L3: 같은 구간 안에서 크기를 바꾸면 리소스를 다시 넣지 않고, 디스플레이가 바뀌면 화면 밖 창만 끌어온다
        var vm = new MainViewModel();
        vm.Update(CompactProviderCard("Claude", "C"), CompactProviderCard("Codex", "X"), "");
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = true }, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            var column = window.Resources["CharacterColumnWidth"];
            window.Width = 700;
            PumpDisplay();
            var sameColumn = window.Resources["CharacterColumnWidth"];
            window.Width = 500;
            PumpDisplay();
            Check("resizing within a layout band keeps the template resources",
                ReferenceEquals(column, sameColumn) && !ReferenceEquals(sameColumn, window.Resources["CharacterColumnWidth"]) &&
                window.Mode == LayoutMode.Medium, $"mode={window.Mode}");

            Rect Area() => (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, null)!;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            double virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            // 모든 모니터의 오른쪽 끝에 200 DIP 만 걸쳐 둔 창은 그대로
            window.Left = virtualRight - 200;
            PumpDisplay();
            SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero);
            PumpDisplay(300);
            Check("a window partly past the edge stays where the user put it", Math.Abs(window.Left - (virtualRight - 200)) < 0.5 && window.Width == 500,
                $"left={window.Left}, width={window.Width}");
            // 어느 모니터에도 거의 보이지 않으면 가장 가까운 작업 영역으로 끌어온다 (크기는 그대로)
            window.Left = virtualRight + 3000;
            PumpDisplay();
            SendMessage(hwnd, 0x001A, new IntPtr(0x002F), IntPtr.Zero);
            PumpDisplay(300);
            var area = Area();
            var visible = Rect.Intersect(new Rect(window.Left, window.Top, window.Width, window.Height), area);
            Check("a work-area change pulls a lost window back without resizing it",
                !visible.IsEmpty && visible.Width >= window.Width - 0.5 && window.Width == 500 && window.Height == MainWindow.DefaultHeight,
                $"left={window.Left}, area={area}");
        }
        finally { window.Close(); }
    }

    /// <summary>"리셋까지 {r}" 처럼 남은 시간만 바뀐 대사도 같은 대사로 보고 연달아 고르지 않는다.</summary>
    static void TestNoRepeatedTemplate()
    {
        var now = DateTimeOffset.Now;
        var card = new ProviderCard
        {
            Name = "Claude", Accent = Palette.Claude, AppStatus = "x", AppStatusBrush = Palette.Muted, Footnote = "", SessionsHeader = "",
            MaxUsed = 75, SoonestReset = now.AddHours(2),
        };
        var result = new MoodTracker("claude").Update(card);
        bool aligned = result.Lines.Length == result.Keys.Length && result.Keys.Any(k => k.Contains("{r}")) &&
            result.Lines.Zip(result.Keys).All(p => p.First.Contains('{') == false &&
                (p.Second.Contains('{') ? p.First != p.Second : p.First == p.Second));
        Check("mood result keeps the template of every filled line", result.Mood == Mood.Tired && aligned,
            string.Join(" | ", result.Keys));

        var stage = new CharacterStage();
        var pick = PrivateMethod<CharacterStage>("PickLine");
        var lastKey = PrivateField<CharacterStage>("_lastKey");
        string[] keys = ["리셋까지 {r} 남았네요…", "인간 싫어요…"];
        stage.SetMood(Mood.Tired, ["리셋까지 61분 남았네요…", "인간 싫어요…"], keys);
        var previous = (string?)lastKey.GetValue(stage);
        int repeats = 0, picks = 0;
        for (int minutes = 60; minutes > 0; minutes--)
        {
            // 렌더할 때마다 남은 시간이 바뀐 새 대사 목록이 들어온다
            stage.SetMood(Mood.Tired, [$"리셋까지 {minutes}분 남았네요…", "인간 싫어요…"], keys);
            var line = (string)pick.Invoke(stage, null)!;
            var key = (string?)lastKey.GetValue(stage);
            picks++;
            if (key == previous) repeats++;
            if (key == keys[0] && line != $"리셋까지 {minutes}분 남았네요…") repeats += 100;
            previous = key;
        }
        Check("a template never repeats back to back even when its filled time changes", repeats == 0 && picks == 60,
            $"repeats={repeats}");

        stage.SetMood(Mood.Tired, ["리셋까지 5분 남았네요…"], [keys[0]]);
        Check("a single available line is still shown", (string)pick.Invoke(stage, null)! == "리셋까지 5분 남았네요…", "no line");
    }

    static async Task TestGeminiUnblock()
    {
        var previousScenario = Environment.GetEnvironmentVariable(FakeAgyScenario);
        Environment.SetEnvironmentVariable(FakeAgyScenario, "success");
        try
        {
            // 가짜 agy(이 테스트 실행 파일)를 쓴다. 사용자 설정 파일에는 저장하지 않는다.
            var exe = Environment.ProcessPath!;
            int saves = 0;
            var settings = new AppSettings { GeminiEnabled = true, AgyExePath = exe, AgyBlockedFingerprint = AgyCli.Fingerprint(exe) };
            using var monitor = new MonitorService(settings, _ => saves++);
            var asked = new List<string>();

            Check("blocked agy offers the unblock action", monitor.GeminiUnsupported && Presenter.Gemini(monitor, settings).Status.Kind == StatusKind.Unsupported,
                Presenter.Gemini(monitor, settings).Status.Text);
            bool declined = await GeminiBlockAction.RunAsync(monitor, text => { asked.Add(text); return false; });
            Check("declining the confirmation keeps the block and runs nothing",
                !declined && asked.SequenceEqual([GeminiBlockAction.ConfirmText]) && settings.AgyBlockedFingerprint != null &&
                saves == 0 && monitor.GeminiRemoteLast == null, $"saves={saves}, asked={asked.Count}");
            Check("the confirmation warns that one quota use is possible",
                GeminiBlockAction.ConfirmText == "agy 가 /usage 를 일반 프롬프트로 처리하는 버전이면 이번 조회에서 쿼터가 1회 쓰일 수 있습니다. 계속할까요?" &&
                GeminiBlockAction.MenuText == "Gemini 조회 차단 해제", GeminiBlockAction.ConfirmText);

            bool cleared = await GeminiBlockAction.RunAsync(monitor, text => { asked.Add(text); return true; });
            Check("confirming clears the block, saves and queries once", cleared && settings.AgyBlockedFingerprint == null && saves >= 1 &&
                monitor.GeminiRemoteLast is { Ok: true } && !monitor.GeminiUnsupported &&
                Presenter.Gemini(monitor, settings).Status.Kind != StatusKind.Unsupported,
                $"cleared={cleared}, saves={saves}, last={monitor.GeminiRemoteLast?.Error ?? "ok"}");

            bool again = await GeminiBlockAction.RunAsync(monitor, text => { asked.Add(text); return true; });
            Check("the unblock action does nothing when Gemini is not blocked", !again && asked.Count == 2, $"asked={asked.Count}");

            // 지문을 알 수 없을 때도 차단이 남아 있으면 미지원으로 본다
            using var unknown = new MonitorService(new AppSettings { GeminiEnabled = true, AgyBlockedFingerprint = "unknown" }, _ => { });
            SetProperty(unknown, nameof(MonitorService.GeminiLocal), new GeminiLocalState { Installed = true, ExecutableFingerprint = null });
            var unknownCard = Presenter.Gemini(unknown);
            Check("a block with an unknown agy fingerprint stays visible with the unblock hint",
                unknown.GeminiUnsupported && unknownCard.Status.Text == AgyCli.UnsupportedMessage &&
                unknownCard.Status.ToolTipText.Contains(GeminiBlockAction.MenuText), unknownCard.Status.ToolTipText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeAgyScenario, previousScenario);
        }
    }

    static void TestGeminiLoginMessage()
    {
        var now = DateTimeOffset.Now;
        var login = new GeminiRemoteResult { LoginRequired = true, Error = AgyCli.LoginRequiredMessage };
        using var monitor = GeminiMonitor(new AppSettings { GeminiEnabled = true }, GeminiLocalFixture(now), null, login, paused: true);
        var card = Presenter.Gemini(monitor);
        var tip = card.Status.ToolTipText;
        int count = 0;
        for (int i = tip.IndexOf(AgyCli.LoginRequiredMessage, StringComparison.Ordinal); i >= 0;
             i = tip.IndexOf(AgyCli.LoginRequiredMessage, i + 1, StringComparison.Ordinal)) count++;
        Check("agy login message reads naturally in the chip, tooltip and notice",
            card.Status.Text == "로그인 필요" && tip.StartsWith(AgyCli.LoginRequiredMessage) && count == 1 && tip.Contains("60분") &&
            card.Notice?.StartsWith(AgyCli.LoginRequiredMessage) == true, $"{tip} / {card.Notice}");
    }
}
