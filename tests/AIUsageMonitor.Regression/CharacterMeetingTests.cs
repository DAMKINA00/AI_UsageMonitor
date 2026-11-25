using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterMeeting()
    {
        var requestId = new string('e', 32);
        var time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var report = new MeetingMessage("report", time, "deepseek", "첫 근거를 확인했어요.", "discussion");
        var summary = """
            세 가지 안을 비교했어요.

            | 방안 | 확인 사항 |
            | --- | --- |
            | 첫 번째 | [원문](https://example.com/doc) |
            | 두 번째 | 추가 확인 필요 |

            ![구성도](https://example.com/diagram.png)
            """;
        var protocol = JsonSerializer.SerializeToElement(new { requestId, seq = 7, type = "draft",
            message = new { id = report.Id, at = time, sender = "deepseek", text = "" } });
        Check("room accepts an empty draft to indicate that the speaker started",
            MeetingEvent.Read(protocol) is { Seq: 7, Message.Text.Length: 0 }, "draft");
        var content = (StackPanel)ChatRichContent.Create(summary, Brushes.White, Brushes.Aquamarine, Brushes.DimGray);
        var table = (Grid)((ScrollViewer)((Border)content.Children[1]).Child).Content;
        var image = (StackPanel)((Border)content.Children[2]).Child;
        Check("paragraphs, Markdown table, and image preview render as separate blocks",
            content.Children[0] is TextBlock && table.RowDefinitions.Count == 3 && table.ColumnDefinitions.Count == 2
            && image.Children[0] is Image && image.Children[1] is Button, "rich content");
        var personal = new CharacterAskWindow("gpt", "지피티") { ShowActivated = false };
        personal.AddMessage(new CharacterMessage(false, summary));
        var bubbles = (StackPanel)PrivateField<CharacterAskWindow>("_messages").GetValue(personal)!;
        Check("rich content is used in the private conversation as well",
            ((Border)bubbles.Children[0]).Child is StackPanel { Children.Count: 3 }, "personal rich content");
        personal.Close();

        var portraits = new Dictionary<string, ImageSource?>();
        var room = new CharacterMeetingWindow(portraits, "", null)
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        Check("DeepSeek's meeting portrait is available when its desktop character is hidden",
            CharacterTheme.ConversationPortrait("deepseek") != null && CharacterTheme.ConversationPortrait("deepseek", true) != null, "layered portraits");
        room.PlaceIn(new Rect(0, 0, 1160, 920));
        Size? newSize = null;
        room.UserSizeChanged += size => newSize = size;
        try
        {
            room.Upsert(new MeetingMessage("agenda", time, "user", "세 가지 안의 장단점을 비교해 줘."), false);
            room.Upsert(new MeetingMessage("call", time, "orch", "@딥시크 · 비교 기준과 근거를 살펴봐 주세요.", "assignment"), false);
            room.Upsert(report with { Text = "" }, true);
            room.Upsert(report, true);
            var messages = (StackPanel)PrivateField<CharacterMeetingWindow>("_messages").GetValue(room)!;
            Check("the live draft updates the same chat row", messages.Children.Count == 4, $"rows={messages.Children.Count}");
            room.Upsert(report, false);
            Check("the finished message replaces its draft", messages.Children.Count == 4, $"rows={messages.Children.Count}");
            room.Upsert(new MeetingMessage("finish", time + 100, "orch", summary, "summary"), false);
            room.Upsert(new MeetingMessage("review", time + 200, "gpt", "두 번째 방안은 자료가 더 필요해요.", "discussion"), true);
            room.SetRoster(["orch", "gpt", "deepseek"], ["gpt"], ["deepseek"]);
            room.SetStatus("참가자들이 내용을 검토하는 중…", true);
            room.Show();
            PumpDisplay();
            RenderFixture(room, "boardroom");
            PrivateMethod<Window>("OnDeactivated").Invoke(room, [EventArgs.Empty]);
            PumpDisplay();
            Check("Boardroom stays visible when another window receives focus", room.IsVisible && newSize == null, "visible; default size is not saved");
            room.Width = 880;
            room.Height = 610;
            PumpDisplay();
            Check("resizing updates the normal Boardroom dimensions", newSize is { Width: 880, Height: 610 }, newSize?.ToString() ?? "missing");
            room.RestoreDraft("작성 중인 후속 질문");
            Check("toggling hides the visible window only", room.HideIfOpen() && !room.IsVisible && !room.HideIfOpen(), "hide");
            room.Show();
            PumpDisplay();
            Check("reopening preserves the size, chat rows and draft",
                room.Width == 880 && room.Height == 610 && messages.Children.Count == 6 && room.Draft == "작성 중인 후속 질문", "preserved");
            var settingsFile = Path.Combine(Paths.AppData, "boardroom-size-test.json");
            var settings = AppSettings.Load(settingsFile);
            settings.BoardroomWidth = newSize?.Width;
            settings.BoardroomHeight = newSize?.Height;
            settings.Save();
            var saved = AppSettings.Load(settingsFile);
            var restored = new CharacterMeetingWindow(portraits, "", null, saved.BoardroomWidth, saved.BoardroomHeight)
                { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                restored.PlaceIn(new Rect(0, 0, 740, 590));
                Check("stored size is clamped to a smaller work area", restored.Width == 728 && restored.Height == 578, "clamped");
                restored.PlaceIn(new Rect(0, 0, 1160, 920));
                Check("the original saved size returns on a larger work area", restored.Width == 880 && restored.Height == 610, "restored");
            }
            finally { restored.Close(); }
            room.Remove("review");
            Check("the interrupted draft disappears without removing the completed posts", messages.Children.Count == 5, "withdrawal");
        }
        finally { room.Close(); }
    }
}
