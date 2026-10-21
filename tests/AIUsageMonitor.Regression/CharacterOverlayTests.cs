using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterOverlay()
    {
        var stage = new CharacterStage();
        stage.SetActive(false);
        var frame = PrivateMethod<CharacterStage>("Frame");
        var wander = (TranslateTransform)PrivateField<CharacterStage>("_wander").GetValue(stage)!;
        var move = (TranslateTransform)PrivateField<CharacterStage>("_move").GetValue(stage)!;
        stage.SetMood(Mood.Idle, ["대기 중이에요"]);
        var positions = new List<Point>();
        for (int i = 0; i < 180; i++)
        {
            frame.Invoke(stage, null);
            positions.Add(new Point(wander.X, move.Y));
        }
        Check("idle character takes small hops within a narrow roaming area",
            positions.All(p => p.X >= -36.01 && p.X <= 0 && p.Y >= -8.01 && p.Y <= 0) &&
            positions.Min(p => p.X) < -30 && positions.Min(p => p.Y) < -6,
            $"x={positions.Min(p => p.X):0.##}, y={positions.Min(p => p.Y):0.##}");

        foreach (var mood in new[] { Mood.Playing, Mood.Working, Mood.WorkingHard, Mood.Sleeping })
        {
            stage.SetMood(mood, [mood == Mood.Playing ? "게임 중. 명령하지마라" : "상태 대사"]);
            bool atHome = wander.X == 0;
            for (int i = 0; i < 90; i++)
            {
                frame.Invoke(stage, null);
                atHome &= wander.X == 0 && move.X == 0 && move.Y == 0;
            }
            Check($"{mood} stays at the lower-right resting position", atHome,
                $"wander={wander.X}, body={move.X}/{move.Y}");
        }

        // 말풍선은 캔버스의 투명 여백 대신 실제 몸의 위쪽에 붙어야 한다.
        var pixels = new byte[4 * 5 * 4];
        pixels[(2 * 4 + 1) * 4 + 3] = 255;
        pixels[(3 * 4 + 2) * 4 + 3] = 255;
        var bitmap = BitmapSource.Create(4, 5, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var bounds = (Rect)typeof(CharacterStage).GetMethod("VisibleBounds", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [bitmap])!;
        Check("speech anchor ignores transparent space around the pose",
            bounds == new Rect(.25, .4, .5, .4), bounds.ToString());

        byte[] mattePixels = [255, 0, 255, 1, 60, 80, 100, 255];
        var matte = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, mattePixels, 8);
        var clean = (BitmapSource)typeof(CharacterStage).GetMethod("ClearTransparentNoise", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [matte])!;
        var cleaned = new byte[8];
        clean.CopyPixels(cleaned, 8, 0);
        Check("desktop sprites clear invisible matte while preserving body colors",
            cleaned.Take(4).All(value => value == 0) && cleaned.Skip(4).SequenceEqual(mattePixels.Skip(4)),
            string.Join(',', cleaned));

        TestCharacterPoseGeometry();
        TestPlatformFootContact();
    }

    static void TestPlatformFootContact()
    {
        var frame = PrivateMethod<CharacterStage>("Frame");
        var bodyBounds = PrivateMethod<CharacterStage>("BodyBoundsIn");
        foreach (var character in new[] { "claude", "codex", "gemini", "grok" })
        {
            var stage = new CharacterStage { Character = character, Width = 144, Height = 178,
                FigureHeight = 106, ExternalWalking = true };
            stage.SetActive(false);
            foreach (var mood in new[] { Mood.Idle, Mood.Working, Mood.WorkingHard, Mood.Sleeping, Mood.Waiting })
            {
                stage.SetMood(mood, []);
                double maxGap = 0;
                for (int tick = 0; tick < 60; tick++)
                {
                    stage.IsWalking = false;
                    stage.FacingLeft = tick >= 30;
                    stage.Measure(new Size(144, 178));
                    stage.Arrange(new Rect(0, 0, 144, 178));
                    stage.UpdateLayout();
                    frame.Invoke(stage, null);
                    stage.UpdateLayout();
                    var body = (Rect)bodyBounds.Invoke(stage, [stage])!;
                    maxGap = Math.Max(maxGap, Math.Abs(body.Bottom - stage.Height));
                }
                Check($"{character} {mood} feet stay on the platform through animation and turns", maxGap < 0.5,
                    $"maximum foot gap={maxGap:0.###} DIP");
            }
            stage.SetMood(Mood.Idle, []);
            stage.IsWalking = true;
            double lowest = double.MaxValue, highest = 0;
            int landings = 0;
            for (int tick = 0; tick < 60; tick++)
            {
                stage.FacingLeft = tick >= 30;
                frame.Invoke(stage, null);
                // A layout refresh must preserve, rather than accumulate or erase, the hop.
                PrivateMethod<CharacterStage>("GroundFeet").Invoke(stage, null);
                var body = (Rect)bodyBounds.Invoke(stage, [stage])!;
                double gap = stage.Height - body.Bottom;
                lowest = Math.Min(lowest, gap);
                highest = Math.Max(highest, gap);
                if (Math.Abs(gap) < .05) landings++;
            }
            Check($"{character} walking makes small repeated hops without sinking through the platform",
                lowest >= -.05 && highest is > 2 and <= 2.6 && landings >= 4,
                $"gap={lowest:0.###}..{highest:0.###}, landings={landings}");
            for (int tick = 0; tick < 6; tick++) frame.Invoke(stage, null);
            stage.IsWalking = false;
            frame.Invoke(stage, null);
            Check($"{character} stopping mid-hop returns its feet to the platform",
                Math.Abs(stage.Height - ((Rect)bodyBounds.Invoke(stage, [stage])!).Bottom) < .05,
                "feet did not return to the baseline");
        }
    }

    static void TestCharacterPoseGeometry()
    {
        var characters = new[] { "claude", "codex", "gemini", "grok" };
        var moods = Enum.GetValues<Mood>();
        var frame = PrivateMethod<CharacterStage>("Frame");
        var sheet = new Canvas { Width = 896, Height = moods.Length * 180, Background = new SolidColorBrush(Color.FromRgb(36, 39, 44)) };
        for (int column = 0; column < characters.Length; column++)
        {
            for (int row = 0; row < moods.Length; row++)
            {
                var stage = new CharacterStage { Character = characters[column], BubblePlacement = BubblePlacement.Left, Width = 224 };
                stage.SetActive(false);
                stage.SetMood(moods[row], []);
                var image = (Image)PrivateField<CharacterStage>("_img").GetValue(stage)!;
                var failures = new List<string>();
                foreach (double height in new[] { 80.0, 150.0 })
                {
                    stage.Height = stage.FigureHeight = height;
                    stage.Measure(new Size(224, height));
                    stage.Arrange(new Rect(0, 0, 224, height));
                    stage.UpdateLayout();
                    if (moods[row] == Mood.Idle)
                    {
                        var body = (Rect)PrivateField<CharacterStage>("_bodyBounds").GetValue(stage)!;
                        double bodyHeight = body.Height * image.ActualHeight * height / 318;
                        Check($"{characters[column]} at {height}: standing bodies share the same scale",
                            Math.Abs(bodyHeight - height * 240 / 318) < 0.1, bodyHeight.ToString());
                    }
                    var seen = new HashSet<string>();
                    for (int tick = 0; tick < 90; tick++)
                    {
                        frame.Invoke(stage, null);
                        stage.UpdateLayout();
                        var body = (Rect)PrivateField<CharacterStage>("_bodyBounds").GetValue(stage)!;
                        var local = new Rect(body.X * image.ActualWidth, body.Y * image.ActualHeight, body.Width * image.ActualWidth, body.Height * image.ActualHeight);
                        var placed = image.TransformToAncestor(stage).TransformBounds(local);
                        var clip = LayoutInformation.GetLayoutClip(image);
                        if (placed.Left < 0 || placed.Right > 224 || placed.Top < 0 || placed.Bottom > height ||
                            clip != null && !clip.Bounds.Contains(local)) failures.Add($"{height}/{tick}: {placed}, clip={clip}");
                        seen.Add((string)PrivateField<CharacterStage>("_pose").GetValue(stage)!);
                    }
                    if (moods[row] == Mood.Working && column < 3)
                        Check($"{characters[column]} at {height}: all three work images were exercised", seen.Count == 3, string.Join(",", seen));
                }
                Check($"{characters[column]} {moods[row]}: full sprite stays visible throughout animation", failures.Count == 0, string.Join("; ", failures.Take(3)));
                if (moods[row] == Mood.Exhausted)
                    Check($"{characters[column]} uses the collapsed exhaustion sprite", ((string)PrivateField<CharacterStage>("_pose").GetValue(stage)!) == "collapsed", "standing pose");

                Canvas.SetLeft(stage, column * 224);
                Canvas.SetTop(stage, row * 180 + 25);
                sheet.Children.Add(stage);
                var label = new TextBlock { Text = $"{characters[column]} / {moods[row]}", Foreground = Brushes.White, FontSize = 12 };
                Canvas.SetLeft(label, column * 224 + 8);
                Canvas.SetTop(label, row * 180 + 5);
                sheet.Children.Add(label);
            }
        }
        sheet.Measure(new Size(sheet.Width, sheet.Height));
        sheet.Arrange(new Rect(0, 0, sheet.Width, sheet.Height));
        sheet.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)sheet.Width, (int)sheet.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        var path = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-character-audit-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine($"INFO character pose audit: {path}");
    }
}
