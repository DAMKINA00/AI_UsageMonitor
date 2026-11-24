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

        // 보낼 채팅
        Check("an open chat is a target with its title", CharacterTarget.TryParse(Json("""{"chatId":12,"title":"  모니터 작업 "}"""), out var t, out _) &&
            t is { ChatId: 12, Title: "모니터 작업" }, "target");
        Check("no open chat is a target without a chat", CharacterTarget.TryParse(Json("""{"chatId":null}"""), out var none, out _) && none.ChatId == null, "none");
        Check("invalid targets are refused", !CharacterTarget.TryParse(Json("""{"title":"x"}"""), out _, out _) &&
            !CharacterTarget.TryParse(Json("""{"chatId":3}"""), out _, out _) &&
            !CharacterTarget.TryParse(Json($$"""{"chatId":3,"title":"{{new string('a', 81)}}"}"""), out _, out _) &&
            !CharacterTarget.TryParse(Json("""{"chatId":0,"title":"x"}"""), out _, out _), "bad targets");

        // 답
        var rid = new string('a', 32);
        Check("an answer parses with its chat", CharacterReply.TryParse(Json($$"""{"requestId":"{{rid}}","character":"gpt","chatId":12,"status":"answer","text":"네, 끝났어요"}"""), out var r, out _) &&
            r is { Character: "gpt", ChatId: 12, Status: "answer", Spoken: "네, 끝났어요" }, "reply");
        Check("a note without text gets a default line", CharacterReply.TryParse(Json($$"""{"requestId":"{{rid}}","character":"orch","chatId":1,"status":"no-chat"}"""), out var n, out _) &&
            n.Spoken.Contains("채팅", StringComparison.Ordinal), "default");
        string[] badReplies =
        [
            $$"""{"requestId":"{{rid}}","character":"gpt","status":"answer","text":"x"}""",
            $$"""{"requestId":"xyz","character":"gpt","chatId":1,"status":"answer","text":"x"}""",
            $$"""{"requestId":"{{rid}}","character":"codex","chatId":1,"status":"answer","text":"x"}""",
            $$"""{"requestId":"{{rid}}","character":"gpt","chatId":1,"status":"answer"}""",
            $$"""{"requestId":"{{rid}}","character":"gpt","chatId":1,"status":"maybe","text":"x"}""",
            $$"""{"requestId":"{{rid}}","character":"gpt","chatId":1,"status":"answer","text":"{{new string('가', 201)}}"}""",
        ];
        Check("invalid replies are refused", badReplies.All(b => !CharacterReply.TryParse(Json(b), out _, out var e) && e.Length > 0), "bad replies");
        Check("ask commands are object commands", HostedProtocol.TryParseCommand("""{"command":"character-target","value":{"chatId":null}}""", out var tc, out _) &&
            tc.Name == "character-target" && HostedProtocol.TryParseCommand($$$"""{"command":"character-reply","value":{"requestId":"{{{rid}}}","character":"gpt","chatId":1,"status":"busy"}}""", out var rc, out _) &&
            rc.Name == "character-reply", "commands");

        // 질문 하나씩, 맞는 답만 한 번
        var asks = new CharacterAsk();
        var now = DateTimeOffset.Now;
        var id = asks.Start("gpt", 12, "  지금 뭐 해?  ", now);
        Check("a question gets a 32-hex request id", CharacterAsk.IsRequestId(id) && asks.IsWaiting("gpt"), id ?? "null");
        Check("a character waits for one question at a time", asks.Start("gpt", 12, "또 질문", now) == null && asks.Start("orch", 12, "마스터에게", now) != null, "one each");
        Check("empty or too long questions are not sent", asks.Start("claude", 1, "   ", now) == null && asks.Start("claude", 1, new string('a', 1001), now) == null, "length");
        CharacterReply Reply(string requestId, string who, long chat) => new(requestId, who, chat, "answer", "답");
        Check("a reply for another chat or character is ignored", !asks.Complete(Reply(id!, "gpt", 99)) && !asks.Complete(Reply(id!, "claude", 12)) &&
            !asks.Complete(Reply(new string('b', 32), "gpt", 12)), "mismatch");
        Check("the matching reply completes the question once", asks.Complete(Reply(id!, "gpt", 12)) && !asks.IsWaiting("gpt") && !asks.Complete(Reply(id!, "gpt", 12)), "complete");
        var late = asks.Start("grok", 5, "늦을 질문", now)!;
        Check("a question gives up after three minutes", asks.Expire(now.AddMinutes(2)).Count == 0 && asks.Expire(now.AddMinutes(3)).Contains("grok") && !asks.IsWaiting("grok"), "expire");
        Check("a late reply after giving up is ignored", !asks.Complete(Reply(late, "grok", 5)), "late");

        // 호스트로 보내는 줄
        var output = new StringWriter();
        new HostedProtocolWriter(output).CharacterAsk(rid, "gpt", 12, "질문");
        using (var line = JsonDocument.Parse(output.ToString()))
        {
            var root = line.RootElement;
            Check("the question goes to the host as one character-ask line", root.GetProperty("type").GetString() == "character-ask" &&
                root.GetProperty("requestId").GetString() == rid && root.GetProperty("character").GetString() == "gpt" &&
                root.GetProperty("chatId").GetInt64() == 12 && root.GetProperty("text").GetString() == "질문", output.ToString());
        }

        // 입력창: 보낼 채팅을 보여 주고, 없으면 안내하며 막는다
        var box = new CharacterAskWindow("gpt", "지피티", new CharacterTarget(null, ""));
        try
        {
            box.Question = "질문";
            Check("without an open chat the box explains and cannot send", !box.CanSend, "can send");
            var sent = new List<string>();
            box.Submitted += (chat, q) => sent.Add($"{chat}:{q}");
            box.SetTarget(new CharacterTarget(12, "모니터 작업"));
            box.Question = "  지금 뭐 해?  ";
            Check("with an open chat the box can send", box.CanSend && box.Target.ChatId == 12, "cannot send");
            box.Send();
            box.Send();
            Check("sending gives the trimmed question once, with its chat", sent.SequenceEqual(["12:지금 뭐 해?"]) && box.WasSent, string.Join("|", sent));
        }
        finally { box.Close(); }

        // 캐릭터 누르기
        var vm = PlatformViewModel(4);
        var settings = new AppSettings { Topmost = false };
        settings.SetCharacters(["orch", "gpt"]);
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

            // 숨김·최소화: 말하던 대사와 줄 선 대사를 버리고, 그동안 온 이벤트도 버린다
            CharacterEvent Evt(string id) => new(id, "gpt", "result", true, $"소식 {id}", 12, 1);
            Check("a shown monitor speaks an event", window.ShowCharacterEvent(Evt("r1")) && window.ShowCharacterEvent(Evt("r2")) &&
                window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 1, "shown");
            var bubble = (System.Windows.Controls.Grid)PrivateField<CharacterStage>("_bubble").GetValue(window.Gpt)!;
            window.Hide();
            PumpDisplay();
            Check("hiding the monitor stops speech and clears the queue", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                bubble.Visibility != Visibility.Visible, $"speaking={window.Gpt.IsSpeaking}, pending={window.Gpt.PendingSpeech}");
            Check("events while hidden are dropped", !window.ShowCharacterEvent(Evt("r3")) && !window.Speak("gpt", "답") &&
                !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0, "hidden events");
            window.Show();
            PumpDisplay();
            Check("showing again brings back no old speech", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                bubble.Visibility != Visibility.Visible && !window.ShowCharacterEvent(Evt("r3")), "reshown");
            Check("a new event after showing again is spoken", window.ShowCharacterEvent(Evt("r4")) && window.Gpt.IsSpeaking, "after show");
            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            Check("minimizing stops speech and drops new events", !window.Gpt.IsSpeaking && window.Gpt.PendingSpeech == 0 &&
                !window.ShowCharacterEvent(Evt("r5")), $"state={window.WindowState}");
            window.WindowState = WindowState.Normal;
        }
        finally { window.Close(); }

        TestCharacterAskDraftPinning();
    }

    static void TestCharacterAskDraftPinning()
    {
        // 초안은 처음 연 채팅에 묶인다: 활성 채팅이 바뀌면 글은 남기고 보내기만 막는다
        var a = new CharacterTarget(1, "채팅 A");
        var b = new CharacterTarget(2, "채팅 B");
        var box = new CharacterAskWindow("gpt", "지피티", a);
        var sent = new List<string>();
        box.Submitted += (chat, q) => sent.Add($"{chat}:{q}");
        try
        {
            box.Question = "A에 보낼 초안";
            box.SetTarget(b);
            box.Send();
            Check("a draft for chat A is never sent to chat B", sent.Count == 0 && !box.CanSend && box.TargetChanged &&
                box.DraftTarget.ChatId == 1 && box.Question == "A에 보낼 초안", $"sent={string.Join("|", sent)}");
            box.SetTarget(new CharacterTarget(null, ""));
            Check("closing every chat keeps the draft and its chat", !box.CanSend && box.DraftTarget.ChatId == 1 && box.Question == "A에 보낼 초안", "no chat");
            box.SetTarget(a);
            box.Send();
            Check("back on chat A the draft goes to chat A", sent.SequenceEqual(["1:A에 보낼 초안"]), string.Join("|", sent));
        }
        finally { box.Close(); }

        var empty = new CharacterAskWindow("orch", "마스터", new CharacterTarget(null, ""));
        try
        {
            empty.SetTarget(b);
            empty.Question = "B 질문";
            Check("a box opened before any chat takes the first chat that opens", empty.DraftTarget.ChatId == 2 && empty.CanSend, "adopt");
        }
        finally { empty.Close(); }

        var reopened = new CharacterAskWindow("gpt", "지피티", b, a, "다시 연 초안");
        try
        {
            Check("a reopened draft stays on its own chat and only explains", reopened.Question == "다시 연 초안" && reopened.DraftTarget.ChatId == 1 &&
                reopened.TargetChanged && !reopened.CanSend, reopened.DraftTarget.ChatId.ToString() ?? "");
        }
        finally { reopened.Close(); }
    }
}
