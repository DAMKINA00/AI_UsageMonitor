using System.IO;
using System.Text.Json;
using System.Windows;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterSelection()
    {
        // 목록 규칙: 정해진 순서, 중복·모르는 이름 제거, 하나만 켜고 끄기
        Check("five characters in ChatterBox order", CharacterSelection.All.SequenceEqual(["orch", "claude", "gpt", "gemini", "grok"]),
            string.Join(",", CharacterSelection.All));
        Check("selection is normalized", CharacterSelection.Normalize(["grok", "orch", "grok", "codex", null, "gpt"]).SequenceEqual(["orch", "gpt", "grok"]),
            "normalize");
        var picked = CharacterSelection.Set(["claude", "grok"], "orch", true);
        picked = CharacterSelection.Set(picked, "gpt", true);
        Check("several characters can be on at once", picked.SequenceEqual(["orch", "claude", "gpt", "grok"]), string.Join(",", picked));
        var oneOff = CharacterSelection.Set(picked, "claude", false);
        Check("turning one off leaves the others", oneOff.SequenceEqual(["orch", "gpt", "grok"]), string.Join(",", oneOff));
        Check("an unknown character is refused", Throws<ArgumentException>(() => CharacterSelection.Set(picked, "deepseek", true)), "no exception");

        // 단일 스위치 설정 마이그레이션 (예전 표시를 그대로 이어 간다)
        var legacyOn = AppSettings.Parse("""{"ShowCharacters": true, "SettingsVersion": 3}""");
        Check("legacy switch on becomes the four characters it showed", legacyOn.Migrated && legacyOn.SettingsVersion == AppSettings.CurrentSettingsVersion &&
            legacyOn.Characters!.SequenceEqual(CharacterSelection.Legacy) && legacyOn.EnabledCharacters.SequenceEqual(["claude", "gpt", "gemini", "grok"]),
            string.Join(",", legacyOn.Characters ?? []));
        var legacyOff = AppSettings.Parse("""{"ShowCharacters": false, "SettingsVersion": 3}""");
        Check("legacy switch off becomes an empty selection", legacyOff.Migrated && legacyOff.Characters is { Count: 0 } && !legacyOff.ShowCharacters,
            $"{legacyOff.Characters?.Count}");
        Check("a fresh install shows the same four characters", new AppSettings().EnabledCharacters.SequenceEqual(CharacterSelection.Legacy) &&
            AppSettings.Parse("{}").Characters!.SequenceEqual(CharacterSelection.Legacy), "fresh");
        var current = AppSettings.Parse("""{"SettingsVersion": 4, "ShowCharacters": true, "Characters": ["gpt", "orch", "nope"]}""");
        Check("current selection is kept and cleaned", !current.Migrated && current.EnabledCharacters.SequenceEqual(["orch", "gpt"]), string.Join(",", current.EnabledCharacters));
        var downgraded = AppSettings.Parse("""{"SettingsVersion": 4, "ShowCharacters": false}""");
        Check("a file rewritten by an older monitor follows its switch", downgraded.EnabledCharacters.Count == 0 && !downgraded.Migrated, "downgraded");

        // 예전 스위치 명령: 끄면 모두 숨기고, 켜면 마지막 선택을 되살린다
        var settings = new AppSettings();
        settings.SetCharacters(["orch", "gemini"]);
        settings.SetAllCharacters(false);
        Check("hide all keeps the legacy switch in step", settings.EnabledCharacters.Count == 0 && !settings.ShowCharacters, "hide");
        settings.SetAllCharacters(true);
        Check("show all brings back the last selection", settings.EnabledCharacters.SequenceEqual(["orch", "gemini"]) && settings.ShowCharacters,
            string.Join(",", settings.EnabledCharacters));
        var never = new AppSettings { Characters = [] };
        never.SetAllCharacters(true);
        Check("show all with no earlier selection shows all five", never.EnabledCharacters.SequenceEqual(CharacterSelection.All), string.Join(",", never.EnabledCharacters));

        // 재시작 복원: 저장 → 다시 읽기, 하나만 끈 것도 그대로
        var dir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Characters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var first = AppSettings.Load(file);
            first.SetCharacters(CharacterSelection.Set(first.EnabledCharacters, "orch", true));
            first.SetCharacters(CharacterSelection.Set(first.EnabledCharacters, "claude", false));
            first.Save();
            var restarted = AppSettings.Load(file);
            Check("the selection survives a restart", restarted.LoadedFrom == AppSettings.LoadResult.Loaded && !restarted.Migrated &&
                restarted.EnabledCharacters.SequenceEqual(["orch", "gpt", "gemini", "grok"]), string.Join(",", restarted.EnabledCharacters));
            restarted.SetAllCharacters(false);
            restarted.Save();
            var again = AppSettings.Load(file);
            again.SetAllCharacters(true);
            Check("hiding all and showing again after a restart restores the last selection",
                again.EnabledCharacters.SequenceEqual(["orch", "gpt", "gemini", "grok"]), string.Join(",", again.EnabledCharacters));
        }
        finally { TryDelete(dir); }

        // 예전 버전이 다시 저장한 v4 설정(Characters 없음, 스위치 켜짐): 네 캐릭터 → 모두 숨기기 → 재시작 → 켜기 = 그 네 캐릭터
        var olderDir = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Characters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(olderDir);
        try
        {
            var file = Path.Combine(olderDir, "settings.json");
            File.WriteAllText(file, """{"SettingsVersion": 4, "ShowCharacters": true}""");
            var rewritten = AppSettings.Load(file);
            Check("a v4 file rewritten by an older monitor shows the four characters",
                rewritten.EnabledCharacters.SequenceEqual(CharacterSelection.Legacy) && rewritten.Characters == null, string.Join(",", rewritten.EnabledCharacters));
            rewritten.SetAllCharacters(false);
            rewritten.Save();
            var afterRestart = AppSettings.Load(file);
            Check("hiding them keeps the four as the last selection across a restart", afterRestart.EnabledCharacters.Count == 0 &&
                CharacterSelection.Normalize(afterRestart.LastCharacters).SequenceEqual(CharacterSelection.Legacy),
                string.Join(",", afterRestart.LastCharacters ?? []));
            afterRestart.SetAllCharacters(true);
            Check("showing again restores exactly those four, not all five", afterRestart.EnabledCharacters.SequenceEqual(CharacterSelection.Legacy),
                string.Join(",", afterRestart.EnabledCharacters));
        }
        finally { TryDelete(olderDir); }

        // 표정 원화: ChatterBox의 5캐릭터 × 12표정이 모두 들어 있고, 모든 기분이 있는 표정으로 이어진다
        string[] expressions = ["neutral", "thinking", "speaking", "happy", "confident", "surprised", "embarrassed", "worried", "sad", "angry", "tired", "waiting"];
        var missing = CharacterSelection.All.SelectMany(c => expressions.Where(e => !CharacterTheme.HasFace(c, e)).Select(e => $"{c}/{e}")).ToList();
        Check("all 60 ChatterBox faces are app resources", missing.Count == 0, string.Join(",", missing));
        Check("every mood shows an existing face", Enum.GetValues<Mood>().All(m => expressions.Contains(CharacterStage.ExpressionFor(m))), "mood map");
        Check("face themes cover the five characters", CharacterTheme.Faces.Select(t => t.Key).SequenceEqual(CharacterSelection.All) &&
            CharacterTheme.For("orch") == CharacterTheme.Orch && CharacterTheme.For("gpt") == CharacterTheme.Gpt, "themes");
        var face = new CharacterStage { FaceArt = true, Character = "orch", Width = 144, Height = 178, FigureHeight = 106 };
        var faceAfter = new CharacterStage { Character = "gpt", FaceArt = true };
        Check("face stages find their art whichever property is set first", face.HasArt && faceAfter.HasArt, $"{face.HasArt}/{faceAfter.HasArt}");

        // 마스터는 가장 급한 기분을 따른다
        Check("Orch follows the most urgent mood", MoodTracker.Overall([Mood.Idle, Mood.Working, Mood.Panic]) == Mood.Panic &&
            MoodTracker.Overall([Mood.Sleeping, Mood.Idle]) == Mood.Idle && MoodTracker.Overall([Mood.Sleeping]) == Mood.Sleeping &&
            MoodTracker.Overall([]) == Mood.Idle, "overall");

        TestCharacterSelectionWindow();
        TestCharacterSelectionTray();
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static void TestCharacterSelectionWindow()
    {
        var vm = PlatformViewModel(4);
        var settings = new AppSettings { Topmost = false };
        settings.SetCharacters(["orch", "gpt", "grok"]);
        var window = new MainWindow(vm, settings, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            string Visible() => string.Join(",", window.Stages.Where(s => s.IsVisible).Select(s => s.Character));
            Check("only the selected characters stand on the monitor", Visible() == "orch,gpt,grok" && window.Stages.All(s => s.FaceArt),
                Visible());
            var size = new Rect(window.Left, window.Top, window.Width, window.Height);
            window.ApplyCharacters(CharacterSelection.Set(window.ShownCharacters, "gpt", false));
            PumpDisplay();
            Check("turning one character off hides only that one", Visible() == "orch,grok" && window.CharactersShown, Visible());
            window.ApplyCharacters(CharacterSelection.All);
            PumpDisplay();
            window.AdvanceCharacters(0.1);
            var lefts = window.Stages.Select(s => Canvas(s)).ToList();
            Check("all five can be shown together", Visible() == "orch,claude,gpt,gemini,grok" && lefts.All(double.IsFinite), Visible());
            Check("changing characters keeps the window position and size",
                window.Left == size.Left && window.Top == size.Top && window.Width == size.Width && window.Height == size.Height,
                $"{size} -> {window.Left},{window.Top},{window.Width},{window.Height}");
            window.ApplyCharacters([]);
            PumpDisplay();
            Check("an empty selection hides every character", Visible() == "" && !window.CharactersShown, Visible());
            RenderFixture(window, "character-selection");
        }
        finally { window.Close(); }

        static double Canvas(UIElement stage) => System.Windows.Controls.Canvas.GetLeft(stage);
    }

    static void TestCharacterSelectionTray()
    {
        var settings = new AppSettings();
        settings.SetCharacters(["claude", "gemini"]);
        using var tray = new Tray(settings, visible: false);
        var items = (Dictionary<string, WinForms.ToolStripMenuItem>)PrivateField<Tray>("_characterItems").GetValue(tray)!;
        Check("tray menu lists the five characters with the saved checks", items.Keys.SequenceEqual(CharacterSelection.All) &&
            items.Where(p => p.Value.Checked).Select(p => p.Key).SequenceEqual(["claude", "gemini"]),
            string.Join(",", items.Where(p => p.Value.Checked).Select(p => p.Key)));
        var calls = new List<string>();
        tray.CharacterChanged += (id, on) => calls.Add($"{id}:{on}");
        items["orch"].PerformClick();
        items["gemini"].PerformClick();
        Check("clicking a character asks to flip only that one", calls.SequenceEqual(["orch:True", "gemini:False"]), string.Join(",", calls));
        tray.SetCharacters(["orch", "claude"]);
        Check("tray checks follow the applied selection", items.Where(p => p.Value.Checked).Select(p => p.Key).SequenceEqual(["orch", "claude"]), "checks");
    }

    static void TestHostedCharacterCommand()
    {
        HostedCheck("character command parses with an object value",
            HostedProtocol.TryParseCommand("""{"command":"character","value":{"id":"gpt","on":false}}""", out var command, out _) &&
            command.Name == "character" && CharacterSelection.TryParseCommand(command.Data!.Value, out var id, out var on, out _) && id == "gpt" && !on);
        HostedCheck("character command needs an object", !HostedProtocol.TryParseCommand("""{"command":"character","value":true}""", out _, out var error) &&
            error.Contains("object", StringComparison.Ordinal));
        foreach (var bad in new[] { """{"id":"deepseek","on":true}""", """{"id":"gpt"}""", """{"id":"gpt","on":"yes"}""", """{"id":"gpt","on":true,"x":1}""" })
        {
            using var doc = JsonDocument.Parse(bad);
            HostedCheck($"invalid character value is refused: {bad}", !CharacterSelection.TryParseCommand(doc.RootElement, out _, out _, out var e) && e.Length > 0);
        }
        HostedCheck("legacy characters switch still parses", HostedProtocol.TryParseCommand("""{"command":"characters","value":false}""", out var legacy, out _) &&
            legacy.BoolValue == false);
    }
}
