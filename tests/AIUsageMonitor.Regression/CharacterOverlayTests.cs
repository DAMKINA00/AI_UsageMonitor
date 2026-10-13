using System.Reflection;
using System.Windows;
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
    }
}
