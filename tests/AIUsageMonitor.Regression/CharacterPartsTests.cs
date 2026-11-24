using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterParts()
    {
        // 파츠 원화: 여섯 캐릭터 × 9장이 리소스에 있다
        var missingParts = CharacterSelection.All.SelectMany(c => CharacterTheme.Parts.Where(p => !CharacterTheme.HasPart(c, p)).Select(p => $"{c}/{p}")).ToList();
        Check("all 54 part layers are app resources", missingParts.Count == 0 && CharacterSelection.All.All(CharacterTheme.HasParts), string.Join(",", missingParts));

        var frame = PrivateMethod<CharacterStage>("Frame");
        CharacterStage NewStage(string id)
        {
            var stage = new CharacterStage { FaceArt = true, EventSpeechOnly = true, Character = id, Width = 144, Height = 178, FigureHeight = 106 };
            stage.Measure(new Size(144, 178));
            stage.Arrange(new Rect(0, 0, 144, 178));
            stage.UpdateLayout();
            return stage;
        }
        void Run(CharacterStage stage, int frames) { for (int i = 0; i < frames; i++) frame.Invoke(stage, null); }
        string Mouth(CharacterStage s) => s.PartsState.Split('|')[2];
        string Brows(CharacterStage s) => s.PartsState.Split('|')[1];
        string Eyes(CharacterStage s) => s.PartsState.Split('|')[0];

        var gpt = NewStage("gpt");
        Run(gpt, 1);
        var img = (Image)PrivateField<CharacterStage>("_img").GetValue(gpt)!;
        Check("a parts character composes body, eyes, brows and mouth into one 256px image",
            gpt.UsesParts && gpt.HasArt && img.Source is BitmapSource { PixelWidth: 256, PixelHeight: 256 } &&
            gpt.PartsState.EndsWith("|brows-neutral|mouth-closed"),
            gpt.PartsState);

        // 깜빡임: 혼자 깜빡이고 다시 뜬다 (10초 = 150프레임 안에 적어도 한 번)
        var eyesSeen = new HashSet<string>();
        for (int i = 0; i < 150; i++) { Run(gpt, 1); eyesSeen.Add(Eyes(gpt)); }
        Check("eyes blink on their own and open again", eyesSeen.SetEquals(["eyes-open", "eyes-half", "eyes-closed"]), string.Join(",", eyesSeen));
        var blinkAt = (FieldInfo)PrivateField<CharacterStage>("_nextBlinkAt");
        var claude = NewStage("claude");
        Run(claude, 1);
        Check("each character keeps its own blink clock", !ReferenceEquals(gpt, claude) && (double)blinkAt.GetValue(gpt)! != (double)blinkAt.GetValue(claude)!, "same blink time");

        // 무음: 기분·대기 대사와 회복 안내는 말풍선을 띄우지 않고 입도 닫혀 있다
        var bubble = (Grid)PrivateField<CharacterStage>("_bubble").GetValue(gpt)!;
        gpt.SetMood(Mood.Waiting, ["대기 대사"]);
        gpt.ShowAnnouncement("100% 회복!");
        bool silent = true;
        for (int i = 0; i < 300; i++) { Run(gpt, 1); silent &= bubble.Visibility != Visibility.Visible && Mouth(gpt) == "mouth-closed" && !gpt.IsSpeaking; }
        Check("mood lines, idle lines and recovery notes stay silent", silent && gpt.Mood == Mood.Waiting, gpt.PartsState);

        // 생각 중: 작업 중 기분이면 눈썹이 바뀌고, 입은 그대로 닫혀 있다
        gpt.SetMood(Mood.Working, ["작업 대사"]);
        Run(gpt, 2);
        Check("working shows thinking brows without speaking", gpt.IsThinking && Brows(gpt) == "brows-thinking" && Mouth(gpt) == "mouth-closed" &&
            bubble.Visibility != Visibility.Visible, gpt.PartsState);

        // 발화: 실제 이벤트 한 줄만 말하고, 그동안만 입이 움직인다. 생각과 동시에 가능하다.
        gpt.Say("작업을 끝냈어요");
        var mouths = new HashSet<string>();
        bool thinkingWhileSpeaking = false;
        for (int i = 0; i < 20; i++) { Run(gpt, 1); mouths.Add(Mouth(gpt)); thinkingWhileSpeaking |= gpt.IsSpeaking && Brows(gpt) == "brows-thinking"; }
        Check("an event line moves the mouth while it is shown", gpt.IsSpeaking && bubble.Visibility == Visibility.Visible &&
            mouths.Contains("mouth-open") && mouths.Count >= 2, string.Join(",", mouths));
        Check("thinking and speaking can happen together", thinkingWhileSpeaking, gpt.PartsState);

        // 줄 서기: 말하는 중에 온 대사는 앞 대사가 끝난 뒤 한 번만 말한다
        gpt.Say("두 번째 소식");
        Check("a second line waits its turn", gpt.PendingSpeech == 1, gpt.PendingSpeech.ToString());
        var text = (TextBlock)PrivateField<CharacterStage>("_bubbleText").GetValue(gpt)!;
        bool sawSecond = false;
        for (int i = 0; i < 300 && !sawSecond; i++) { Run(gpt, 1); sawSecond = gpt.IsSpeaking && text.Text.Contains("두 번째"); }
        Check("queued lines are spoken in order after the first", sawSecond && gpt.PendingSpeech == 0, text.Text);
        for (int i = 0; i < 300; i++) Run(gpt, 1);
        Check("after the last line the bubble closes and the mouth stops", !gpt.IsSpeaking && Mouth(gpt) == "mouth-closed" &&
            bubble.Visibility != Visibility.Visible, gpt.PartsState);
        for (int i = 0; i < 12; i++) gpt.Say($"줄 {i}");
        Check("at most eight lines wait", gpt.PendingSpeech <= 8, gpt.PendingSpeech.ToString());

        // 졸음은 눈을 감는다
        claude.SetMood(Mood.Sleeping, []);
        Run(claude, 3);
        Check("sleeping keeps the eyes closed", Eyes(claude) == "eyes-closed", claude.PartsState);

        // 해제: 숨기면 그림을 놓고, 다시 켜면 읽는다
        claude.ReleaseArt();
        var claudeImg = (Image)PrivateField<CharacterStage>("_img").GetValue(claude)!;
        bool released = claudeImg.Source == null && ((System.Collections.IDictionary)PrivateField<CharacterStage>("_poseCache").GetValue(claude)!).Count == 0;
        claude.ReloadArt();
        Check("hidden characters release their art and reload when shown", released && claudeImg.Source != null, claude.PartsState);

        // 대신 그리기: 파츠를 쓰지 않는 무대(포즈 원화)는 그대로다
        var pose = new CharacterStage { Character = "claude" };
        Check("pose-art stages keep the old art", !pose.UsesParts && pose.HasArt && pose.PartsState == "", pose.HasArt.ToString());

        TestCharacterEventParsing();
        TestCharacterEventWindow();
    }

    static void TestCharacterEventParsing()
    {
        CharacterEvent? Parse(string json, out string error)
        {
            using var doc = JsonDocument.Parse(json);
            return CharacterEvent.TryParse(doc.RootElement, out var e, out error) ? e : null;
        }
        var ok = Parse("""{"id":"result:12","character":"gpt","kind":"result","ok":true,"text":"  완료했어요\n자세한 건 채팅에  ","chatId":3,"consultId":12}""", out _);
        Check("a result event parses and becomes one clean line", ok is { Character: "gpt", Kind: "result", Ok: true, ChatId: 3, ConsultId: 12 } &&
            ok.Text == "완료했어요 자세한 건 채팅에", ok?.Text ?? "null");
        var assign = Parse("""{"id":"assign:7","character":"orch","kind":"assign","text":"클로드에게 맡겼어요","chatId":3,"consultId":null}""", out _);
        Check("an assign event without consult id parses", assign is { Kind: "assign", ConsultId: null }, "assign");
        string[] bad =
        [
            """{"id":"","character":"gpt","kind":"result","text":"a","chatId":1}""",
            """{"id":"x","character":"codex","kind":"result","text":"a","chatId":1}""",
            """{"id":"x","character":"gpt","kind":"chatter","text":"a","chatId":1}""",
            """{"id":"x","character":"orch","kind":"done","ok":true,"text":"a","chatId":1}""",
            """{"id":"x","character":"gpt","kind":"result","text":"a"}""",
            """{"id":"x","character":"gpt","kind":"result","text":"   ","chatId":1}""",
            $$"""{"id":"x","character":"gpt","kind":"result","text":"{{new string('가', 121)}}","chatId":1}""",
            """{"id":"x","character":"gpt","kind":"result","text":"a","chatId":1,"extra":1}""",
        ];
        Check("invalid character events are refused with a reason", bad.All(b => Parse(b, out var e) == null && e.Length > 0), "bad");
        HostedCheck2("character-event is an object command",
            HostedProtocol.TryParseCommand("""{"command":"character-event","value":{"id":"a","character":"orch","kind":"done","text":"끝","chatId":1}}""", out var cmd, out _) &&
            cmd.Name == "character-event" && cmd.Data != null);

        var dedupe = new CharacterEventDeduper(3);
        bool first = dedupe.Accept("a") && dedupe.Accept("b") && dedupe.Accept("c");
        bool repeat = !dedupe.Accept("a");
        dedupe.Accept("d");
        Check("repeated ids are dropped and old ones age out", first && repeat && dedupe.Accept("a"), "dedupe");
    }

    static void HostedCheck2(string name, bool condition) => Check(name, condition, "unexpected result");

    static void TestCharacterEventWindow()
    {
        var vm = PlatformViewModel(4);
        var settings = new AppSettings { Topmost = false };
        settings.SetCharacters(["orch", "gpt", "deepseek"]);
        var window = new MainWindow(vm, settings, _ => { }) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true };
        try
        {
            window.Show();
            PumpDisplay();
            Check("all monitor stages draw parts and speak only on events", window.Stages.All(s => s.UsesParts && s.EventSpeechOnly) &&
                window.Stages.Select(s => s.Character).SequenceEqual(CharacterSelection.All), string.Join(",", window.Stages.Select(s => s.Character)));
            CharacterEvent Evt(string id, string who, string kind = "result") => new(id, who, kind, kind == "result" ? true : null, $"{who} 소식", 1, 5);
            Check("an event for a shown character is spoken once", window.ShowCharacterEvent(Evt("result:5", "gpt")) && window.Gpt.IsSpeaking, "first");
            Check("the same event id is not spoken again", !window.ShowCharacterEvent(Evt("result:5", "gpt")) && window.Gpt.PendingSpeech == 0, "repeat");
            Check("DeepSeek speaks its own result", window.ShowCharacterEvent(Evt("result:6", "deepseek")) && window.DeepSeek.IsSpeaking, "deepseek");
            Check("Orch announces assignments", window.ShowCharacterEvent(Evt("assign:7", "orch", "assign")) && window.Orch.IsSpeaking, "orch");
            Check("events for hidden characters are dropped", !window.ShowCharacterEvent(Evt("result:8", "claude")) && !window.Claude.IsSpeaking &&
                window.Claude.PendingSpeech == 0, "hidden");
            Check("hidden characters hold no art", PrivateField<CharacterStage>("_img").GetValue(window.Claude) is Image { Source: null }, "claude art");
            Check("a busy character queues the next event", window.ShowCharacterEvent(Evt("result:9", "gpt")) && window.Gpt.PendingSpeech == 1, "queue");
            RenderFixture(window, "character-parts");
        }
        finally { window.Close(); }
    }
}
