using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.IO;
using System.Text.Json;
using System.Windows;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterAsk()
    {
        static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        var rid = new string('a', 32);
        var full = "첫 줄\n둘째 줄\n셋째 줄\n넷째 줄";
        var json = JsonSerializer.SerializeToElement(new { requestId = rid, character = "gpt", status = "answer", text = full });
        Check("private replies carry all lines to the presentation layer", CharacterReply.TryParse(json, out var r, out _) && r.Spoken == full, "reply");
        var bad = Json($$"""{"requestId":"{{rid}}","character":"gpt","status":"answer"}""");
        Check("empty answers are refused", !CharacterReply.TryParse(bad, out _, out _), "empty");
        var status = Json($$"""{"requestId":"{{rid}}","character":"orch","status":"busy"}""");
        Check("a note without text gets a default line", CharacterReply.TryParse(status, out var n, out _) && n.Spoken.Length > 0, "default");
        Check("a reply is a hosted command", HostedProtocol.TryParseCommand($$"""{"command":"character-reply","value":{{status.GetRawText()}}}""", out var cmd, out _) &&
            cmd.Name == "character-reply", "reply command");

        var asks = new CharacterAsk();
        var now = DateTimeOffset.Now;
        var id = asks.Start("gpt", "  지금 뭐 해?  ", now);
        Check("a question gets a 32-hex request id", CharacterAsk.IsRequestId(id) && asks.IsWaiting("gpt"), id ?? "null");
        Check("one question per character can be pending", asks.Start("gpt", "또 질문", now) == null && asks.Start("orch", "마스터에게", now) != null, "one each");
        Check("empty or too long questions are not sent", asks.Start("claude", "   ", now) == null &&
            asks.Start("claude", new string('a', CharacterAsk.MaxQuestionLength + 1), now) == null, "length");
        CharacterReply Reply(string requestId, string who) => new(requestId, who, "answer", "답");
        Check("the request id and character must match", !asks.Complete(Reply(id!, "claude")) && !asks.Complete(Reply(rid, "gpt")), "mismatch");
        Check("the matching reply completes the question once", asks.Complete(Reply(id!, "gpt")) && !asks.IsWaiting("gpt") && !asks.Complete(Reply(id!, "gpt")), "complete");
        var late = asks.Start("grok", "늦을 질문", now)!;
        Check("a question times out and ignores a late reply", asks.Expire(now.AddMinutes(2)).Count == 0 && asks.Expire(now.AddMinutes(3)).Contains("grok") &&
            !asks.IsWaiting("grok") && !asks.Complete(Reply(late, "grok")), "expire");

        var output = new StringWriter();
        new HostedProtocolWriter(output).CharacterAsk(rid, "gpt", "질문");
        using (var line = JsonDocument.Parse(output.ToString()))
        {
            var root = line.RootElement;
            Check("the host receives a private question without a desktop chat id", root.GetProperty("type").GetString() == "character-ask" &&
                root.GetProperty("requestId").GetString() == rid && root.GetProperty("character").GetString() == "gpt" &&
                !root.TryGetProperty("chatId", out _) && root.GetProperty("text").GetString() == "질문", output.ToString());
        }

        var box = new CharacterAskWindow("gpt", "지피티", "  지금 뭐 해?  ");
        bool focusClosed = false;
        box.Closed += (_, _) => focusClosed = true;
        try
        {
            Check("a character can be asked without a desktop chat", box.CanSend, "send");
            var sent = new List<string>();
            box.Submitted += sent.Add;
            box.Send();
            box.Question = "한 번 더";
            box.Send();
            Check("the input waits for the first reply", sent.SequenceEqual(["지금 뭐 해?"]) && !box.CanSend, string.Join("|", sent));
            box.AddMessage(new CharacterMessage(true, "윈도우 단축키를 어디서 확인해요?"));
            box.AddMessage(new CharacterMessage(false, full + "\n[Windows 도움말](https://support.microsoft.com/windows)"));
            box.SetWaiting(false);
            var transcript = (RichTextBox)PrivateField<CharacterAskWindow>("_scroll").GetValue(box)!;
            var messages = transcript.Document.Blocks;
            var reply = (Paragraph)((Section)messages.LastBlock!).Blocks.FirstBlock!;
            Check("the dedicated chat renders complete multi-line answers and keeps previous turns", messages.Count == 2 &&
                new TextRange(reply.ContentStart, reply.ContentEnd).Text.Contains("넷째 줄") && box.CanSend, "full answer");
            transcript.Selection.Select(messages.FirstBlock!.ContentStart, reply.ContentEnd);
            Check("the read-only personal transcript supports selection across turns and keeps its links enabled",
                transcript.IsReadOnly && transcript.IsDocumentEnabled && transcript.Selection.Text.Contains("윈도우 단축키") &&
                transcript.Selection.Text.Contains("Windows 도움말"), transcript.Selection.Text);
            transcript.Selection.Select(reply.ContentEnd, reply.ContentEnd);
            var link = reply.Inlines.OfType<Hyperlink>().Single();
            Check("the answer contains a clickable hyperlink with its original destination", link.NavigateUri.Host == "support.microsoft.com" &&
                ((Run)link.Inlines.FirstInline).Text == "Windows 도움말", link.NavigateUri.ToString());
            var raw = ChatLinks.Inlines("https://example.com/docs_(kr). file:///C:/Windows/a.cmd [bad](javascript:alert(1))", Brushes.CadetBlue).OfType<Hyperlink>().ToArray();
            Check("only HTTP or HTTPS text is made clickable, excluding trailing punctuation", raw.Length == 1 &&
                raw[0].NavigateUri.AbsoluteUri == "https://example.com/docs_(kr)", string.Join(",", raw.Select(item => item.NavigateUri)));
            box.Question = "";
            box.Opacity = 0;
            box.ShowActivated = false;
            box.PlaceBy(Rect.Empty, new Rect(0, 0, 1100, 900));
            box.Show();
            PumpDisplay();
            Check("the standalone chat opens at the reference size and stays inside the screen",
                box.ActualWidth >= 735 && box.ActualHeight >= 715 && box.Top >= 0 && box.Left >= 0 &&
                box.Left + box.ActualWidth <= 1100 && box.Top + box.ActualHeight <= 900,
                $"{box.Left}, {box.Top}, {box.ActualWidth}, {box.ActualHeight}");
            RenderFixture(box, "character-question-panel");
            box.Question = "아직 보내지 않은 메시지";
            PrivateMethod<Window>("OnDeactivated").Invoke(box, [EventArgs.Empty]);
            PumpDisplay();
            Check("focus loss closes the character chat with its draft intact",
                focusClosed && box.Question == "아직 보내지 않은 메시지", $"closed={focusClosed}, draft preserved={box.Question == "아직 보내지 않은 메시지"}");
        }
        finally { if (!focusClosed) box.Close(); }

        var shortcut = new WindowsSpaceChord();
        Check("plain Space and extra modifiers pass through", shortcut.Space(true, false, false, false) == (false, SpaceAction.None) &&
            shortcut.Space(false, false, false, false) == (false, SpaceAction.None) &&
            shortcut.Space(true, false, true, true) == (false, SpaceAction.None) &&
            shortcut.Space(false, false, false, false) == (false, SpaceAction.None) &&
            shortcut.Space(true, true, true, false) == (false, SpaceAction.None) &&
            shortcut.Space(false, true, true, false) == (false, SpaceAction.None), "pass through");
        Check("Win+Space opens Boardroom and Alt+Space opens Grok Bot once per key press",
            shortcut.Space(true, true, false, false) == (true, SpaceAction.Boardroom) &&
            shortcut.Space(true, true, false, false) == (true, SpaceAction.None) &&
            shortcut.Space(false, false, false, false) == (true, SpaceAction.None) &&
            shortcut.Space(true, false, true, false) == (true, SpaceAction.GrokBot) &&
            shortcut.Space(true, false, true, false) == (true, SpaceAction.None) &&
            shortcut.Space(false, false, false, false) == (true, SpaceAction.None), "Win+Space and Alt+Space");

        // 캐릭터 누르기
        var vm = PlatformViewModel(4);
        var settings = new AppSettings { Topmost = false };
        settings.SetCharacters(["orch", "gpt", "deepseek"]);
        var window = new MainWindow(vm, settings, _ => { }) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true };
        try
        {
            window.Show();
            PumpDisplay();
            var clicks = new List<string>();
            window.CharacterClicked += (who, rect) => { if (!rect.IsEmpty) clicks.Add(who); };
            Check("clicking does nothing until the host takes questions", !window.ClickCharacter("gpt") && clicks.Count == 0, "disabled");
            window.AskEnabled = true;
            Check("clicking a shown character opens a question for it", window.ClickCharacter("gpt") && clicks.SequenceEqual(["gpt"]), string.Join(",", clicks));
            Check("hidden characters cannot be clicked", !window.ClickCharacter("claude") && clicks.Count == 1, "hidden");

            var masterAnchor = window.CharacterAnchor("orch");
            Check("the rendered master has an on-screen anchor for its individual chat", !masterAnchor.IsEmpty && masterAnchor.IntersectsWith(window.WorkArea), masterAnchor.ToString());
            var masterBox = new CharacterAskWindow("orch", "마스터");
            try
            {
                var workArea = new Rect(0, 0, 1100, 700);
                var anchor = new Rect(490, 560, 60, 55);
                masterBox.PlaceBy(anchor, workArea);
                Check("an anchored master chat fits above the character on a shorter screen",
                    masterBox.Top >= workArea.Top && masterBox.Top + masterBox.Height <= anchor.Top - 10 &&
                    masterBox.Left + masterBox.Width / 2 == anchor.Left + anchor.Width / 2,
                    $"chat={masterBox.Left},{masterBox.Top},{masterBox.Width},{masterBox.Height}; anchor={anchor}");
            }
            finally { masterBox.Close(); }

            var deepSeekPortrait = CharacterTheme.ConversationPortrait("deepseek");
            var deepSeekBox = new CharacterAskWindow("deepseek", "딥시크", "질문", deepSeekPortrait)
                { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                deepSeekBox.Show();
                PumpDisplay();
                Check("DeepSeek opens using its layered portrait independently of the standing sprite",
                    !CharacterTheme.HasFace("deepseek", "neutral") && deepSeekPortrait is not null && deepSeekBox.IsVisible && deepSeekBox.CanSend &&
                    window.ClickCharacter("deepseek") && clicks.Contains("deepseek"), "DeepSeek chat");
            }
            finally { deepSeekBox.Close(); }

            // 숨김·최소화: 말하던 대사와 줄 선 대사를 버리고, 그동안 온 이벤트도 버린다
            CharacterEvent Evt(string id) => new(id, "gpt", "result", true, $"소식 {id}", 12, 1);
            Check("a shown monitor speaks an event", window.ShowCharacterEvent(Evt("r1")) && window.ShowCharacterEvent(Evt("r2")) &&
                window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 1, "shown");
            var bubble = (System.Windows.Controls.Grid)PrivateField<CharacterStage>("_bubble").GetValue(window.Gpt)!;
            window.Hide();
            PumpDisplay();
            Check("a hidden monitor has no visible shortcut anchor", window.CharacterAnchor("orch").IsEmpty, "hidden master");
            Check("hiding the monitor stops speech and clears the queue", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                bubble.Visibility != Visibility.Visible, $"speaking={window.Gpt.IsSpeaking}, pending={window.Gpt.PendingSpeech}");
            Check("events while hidden are dropped", !window.ShowCharacterEvent(Evt("r3")) && !window.Speak("gpt", "답") &&
                !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0, "hidden events");
            window.Show();
            PumpDisplay();
            Check("showing again brings back no old speech", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                bubble.Visibility != Visibility.Visible && !window.ShowCharacterEvent(Evt("r3")), "reshown");
            Check("a new event after showing again is spoken", window.ShowCharacterEvent(Evt("r4")) && window.Gpt.IsSpeaking, "after show");
            window.Gpt.StopSpeech();
            window.Speak("gpt", "첫 번째 이야기\n두 번째 이야기\n세 번째 이야기\n네 번째 이야기");
            PumpDisplay(200);
            var field = FindNamed<Canvas>(window, "CharacterField");
            var bubbleText = (TextBlock)PrivateField<CharacterStage>("_bubbleText").GetValue(window.Gpt)!;
            var shown = bubble.TransformToAncestor(field).TransformBounds(new Rect(bubble.RenderSize));
            Check("event bubbles stay inside the field and ellipsize after three lines",
                shown.Top >= 0 && shown.Bottom <= field.ActualHeight && bubbleText.Text.Split('\n').Length == 3 && bubbleText.Text.EndsWith("..."),
                $"{shown}: {bubbleText.Text}");
            RenderFixture(window, "character-speech-bubble");
            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            Check("minimizing stops speech and drops new events", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                !window.ShowCharacterEvent(Evt("r5")), $"state={window.WindowState}");
            window.WindowState = WindowState.Normal;
        }
        finally { window.Close(); }
    }
}
