using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIUsageMonitor.UI;

internal static class StandingSprite
{
    const int Top = 100, Eyes = 230, Chin = 274, Wrists = 387, Floor = 500;
    const int Width = 440;
    const double EyeSpacing = 59;
    public const int Height = Floor - Top;

    // Pixel landmarks on Assets/Standing/*.png at 315 × 420. The crown excludes
    // curls, ribbons and pigtails. Eye spacing and wrist width set the horizontal scale.
    readonly record struct Landmarks(int Crown, int Eyes, int Chin, int Wrists, int EyeSpacing, int WristWidth);
    static readonly Dictionary<string, Landmarks> Art = new()
    {
        ["orch"] = new(51, 137, 177, 283, 47, 133),
        ["claude"] = new(18, 144, 192, 296, 64, 139),
        ["gpt"] = new(12, 141, 186, 305, 59, 145),
        ["gemini"] = new(58, 159, 198, 284, 54, 129),
        ["grok"] = new(12, 139, 184, 293, 63, 140),
        ["deepseek"] = new(53, 153, 193, 295, 60, 144),
    };

    // Rest poses use the same face dimensions and the pose's own knee and foot height.
    readonly record struct RestLandmarks(int Crown, int Chin, int EyeSpacing, int BodyHeight);
    static readonly Dictionary<string, RestLandmarks> RestArt = new()
    {
        ["orch"] = new(80, 218, 50, 160),
        ["claude"] = new(32, 218, 65, 132),
        ["gpt"] = new(32, 210, 59, 165),
        ["gemini"] = new(85, 226, 51, 150),
        ["grok"] = new(32, 181, 48, 219),
        ["deepseek"] = new(82, 228, 60, 139),
    };

    public static BitmapSource Render(string character, BitmapSource art, Rect visible, bool resting)
    {
        if (art.PixelHeight != 420 || (!resting && art.PixelWidth != 315) || !Art.TryGetValue(character, out var reference))
            return art;

        int sole = (int)Math.Round(visible.Bottom * art.PixelHeight);
        var picture = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        using (var drawing = picture.RenderOpen())
        {
            void Draw(int first, int last, int top, int bottom, double scale)
            {
                var band = new CroppedBitmap(art, new Int32Rect(0, first, art.PixelWidth, last - first));
                double width = art.PixelWidth * scale;
                drawing.DrawImage(band, new Rect((Width - width) / 2, top, width, bottom - top));
            }

            if (resting)
            {
                var rest = RestArt[character];
                double scale = EyeSpacing / rest.EyeSpacing;
                int chin = Floor - rest.BodyHeight;
                int top = chin - (int)Math.Round(rest.Chin * (Chin - Top) / (double)(rest.Chin - rest.Crown));
                Draw(0, rest.Chin, top, chin, scale);
                Draw(rest.Chin, sole, chin, Floor, scale);
            }
            else
            {
                double headScale = EyeSpacing / reference.EyeSpacing;
                double bodyScale = 140.0 / reference.WristWidth;
                int upper = Top - (int)Math.Round(reference.Crown * (Eyes - Top) / (double)(reference.Eyes - reference.Crown));
                Draw(0, reference.Eyes, upper, Eyes, headScale);
                Draw(reference.Eyes, reference.Chin, Eyes, Chin, headScale);

                // Ease from face width into body width below the chin, where hair and
                // collars can span both regions.
                const int Blend = 32, Band = 4;
                int BodyY(int offset) => Chin + (int)Math.Round(offset * (Wrists - Chin) / (double)(reference.Wrists - reference.Chin));
                for (int offset = 0; offset < Blend; offset += Band)
                {
                    double progress = (offset + Band / 2.0) / Blend;
                    Draw(reference.Chin + offset, reference.Chin + offset + Band, BodyY(offset), BodyY(offset + Band),
                        headScale + (bodyScale - headScale) * progress);
                }
                Draw(reference.Chin + Blend, reference.Wrists, BodyY(Blend), Wrists, bodyScale);
                Draw(reference.Wrists, sole, Wrists, Floor, bodyScale);
            }
        }
        var sprite = new RenderTargetBitmap(Width, Floor, 96, 96, PixelFormats.Pbgra32);
        sprite.Render(picture);
        sprite.Freeze();
        return sprite;
    }
}
