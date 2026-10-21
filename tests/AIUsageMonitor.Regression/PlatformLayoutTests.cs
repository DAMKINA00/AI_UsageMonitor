using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestPlatformLayout()
    {
        TestPlatformWalker();
        TestCharacterLayers();
        TestAnnouncementPresentation();

        var vm = PlatformViewModel(4);
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = true, Topmost = false }, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            var root = FindNamed<Grid>(window, "Root");
            var field = FindNamed<Canvas>(window, "CharacterField");
            var items = FindNamed<ItemsControl>(window, "PlatformItems");
            var scroll = FindNamed<ScrollViewer>(window, "PlatformScroll");
            var area = WorkArea(window);
            var fourBounds = MainWindow.DockBounds(area, 4);

            Check("platform fixture has transparent root and four named character stages",
                window.AllowsTransparency && window.Background is SolidColorBrush brush && brush.Color == Colors.Transparent &&
                root.Background == null && field.Background == null &&
                new[] { window.Claude, window.Codex, window.Gemini, window.Grok }.All(stage =>
                    stage.Width == 144 && stage.Height == 178 && stage.FigureHeight == 106 && stage.IsVisible),
                $"window={window.AllowsTransparency}, root={root.Background}, field={field.Background}");

            var fourSlots = items.Items.OfType<AccountUsageSlot>().ToList();
            var fourRects = PlatformRects(items, window);
            Check("four registered accounts render four non-empty platform blocks",
                fourSlots.Count == 4 && fourSlots.All(slot => !slot.IsEmpty) && fourRects.Count == 4,
                $"items={fourSlots.Count}, rects={fourRects.Count}, empty={fourSlots.Count(slot => slot.IsEmpty)}");
            var stages = new[] { window.Claude, window.Codex, window.Gemini, window.Grok };
            window.AdvanceCharacters(.1);
            var extent = Math.Max(0, field.ActualWidth - 144);
            Check("four character walkers stay within the resized canvas",
                stages.All(stage => Canvas.GetLeft(stage) is var left && double.IsFinite(left) && left >= -0.5 && left <= extent + 0.5 &&
                    Canvas.GetTop(stage) >= -0.5 && Canvas.GetTop(stage) + stage.Height <= field.ActualHeight + 0.5),
                string.Join(",", stages.Select(stage => $"{stage.Character}:{Canvas.GetLeft(stage):0.#}")));
            Check("four-account platform blocks are contiguous and right anchored",
                window.Width == fourBounds.Width && Math.Abs(window.Left - fourBounds.Left) < 1 &&
                fourRects.All(rect => rect.Width is >= 145 and <= 147 && rect.Left >= -1 && rect.Right <= root.ActualWidth + 1) &&
                fourRects.Zip(fourRects.Skip(1)).All(pair => Math.Abs(pair.Second.Left - pair.First.Right) <= 2) &&
                Math.Abs(fourRects[^1].Right - BoundsIn(scroll, window).Right) <= 2,
                $"window={window.Left}/{window.Width}, bounds={fourBounds}, blocks={string.Join(";", fourRects)}");

            var before = new Rect(window.Left, window.Top, window.Width, window.Height);
            SetPlatformAccounts(vm, 6);
            PumpDisplay();
            var sixBounds = MainWindow.DockBounds(area, 6);
            var sixRects = PlatformRects(items, window);
            Check("adding registered accounts grows the stage to the left",
                items.Items.Count == 6 && window.Width == sixBounds.Width &&
                Math.Abs((window.Left + window.Width) - (sixBounds.Left + sixBounds.Width)) < 1 &&
                (sixBounds.Width > fourBounds.Width ? window.Left < before.Left : window.Left == before.Left),
                $"before={before.Left}/{before.Width}, after={window.Left}/{window.Width}, expected={sixBounds}");
            Check("added accounts replace the fixture without empty placeholders",
                items.Items.OfType<AccountUsageSlot>().Count() == 6 && items.Items.OfType<AccountUsageSlot>().All(slot => !slot.IsEmpty) &&
                sixRects.Count == 6,
                $"items={items.Items.Count}, rects={sixRects.Count}");

            int overflowCount = (int)Math.Ceiling(area.Width / MainWindow.PlatformWidth) + 2;
            SetPlatformAccounts(vm, overflowCount);
            PumpDisplay(250);
            var overflowBounds = MainWindow.DockBounds(area, overflowCount);
            Check("many registered accounts clamp to the screen and expose horizontal overflow",
                items.Items.Count == overflowCount && window.Width == overflowBounds.Width && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
                scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth > 0 &&
                window.Height >= MainWindow.DefaultHeight,
                $"items={items.Items.Count}, window={window.Width}x{window.Height}, expected={overflowBounds}, scroll={scroll.ScrollableWidth}");
            RenderFixture(window, "platform-overflow");

            window.ApplyCharacters(false);
            PumpDisplay();
            Check("reduced character visibility does not resize the platform stage",
                !window.CharactersShown && window.Width == overflowBounds.Width && window.Height == overflowBounds.Height &&
                !window.Claude.IsVisible && !window.Grok.IsVisible,
                $"shown={window.CharactersShown}, size={window.Width}x{window.Height}, expected={overflowBounds.Width}x{overflowBounds.Height}");
            window.ApplyCharacters(true);
        }
        finally { window.Close(); }
    }

    static void TestAnnouncementPresentation()
    {
        var stage = new CharacterStage { Character = "claude" };
        stage.Measure(new Size(144, 178));
        stage.Arrange(new Rect(0, 0, 144, 178));
        var frame = PrivateMethod<CharacterStage>("Frame");
        var shown = PrivateField<CharacterStage>("_bubbleShown");
        var content = PrivateField<CharacterStage>("_bubbleContent");

        stage.SetMood(Mood.Idle, ["일상 대사"]);
        stage.ShowAnnouncement("리셋 완료");
        stage.SetMood(Mood.Working, ["작업 중"]);
        bool held = true;
        for (int i = 0; i < 75; i++)
        {
            frame.Invoke(stage, null);
            held &= (bool)shown.GetValue(stage)! && (string)content.GetValue(stage)! == "리셋 완료";
        }

        bool expired = false;
        for (int i = 0; i < 60; i++)
        {
            frame.Invoke(stage, null);
            if (!(bool)shown.GetValue(stage)! || (string)content.GetValue(stage)! != "리셋 완료")
            {
                expired = true;
                break;
            }
        }

        bool normalSeen = false, announcementReplayed = false;
        for (int i = 0; i < 600; i++)
        {
            frame.Invoke(stage, null);
            bool isShown = (bool)shown.GetValue(stage)!;
            string text = (string)content.GetValue(stage)!;
            if (isShown && text == "작업 중") normalSeen = true;
            if (normalSeen && isShown && text == "리셋 완료") announcementReplayed = true;
        }
        Check("announcement survives mood updates, expires once, and yields to normal dialogue",
            held && expired && normalSeen && !announcementReplayed,
            $"held={held}, expired={expired}, normal={normalSeen}, replayed={announcementReplayed}");
    }

    static void TestPlatformWalker()
    {
        var first = new PlatformWalker(31);
        var same = new PlatformWalker(31);
        var other = new PlatformWalker(32);
        first.Advance(0, 120, true, .25);
        same.Advance(0, 120, true, .25);
        other.Advance(0, 120, true, .25);
        Check("walker initial positions use independent seeded fractions", first.Position == 30 && same.Position == 30 && other.Position == 30 &&
            !first.IsMoving && !same.IsMoving, $"positions={first.Position}/{same.Position}/{other.Position}");

        bool sawPause = false, sawMove = false, seedDiverged = false, noLeap = true, bounded = true;
        for (int i = 0; i < 240; i++)
        {
            var before = first.Position;
            first.Advance(.1, 120, true, .25);
            same.Advance(.1, 120, true, .25);
            other.Advance(.1, 120, true, .25);
            var delta = Math.Abs(first.Position - before);
            sawPause |= !first.IsMoving;
            sawMove |= first.IsMoving;
            seedDiverged |= Math.Abs(first.Position - other.Position) > 0.0001;
            noLeap &= delta <= 1.101;
            bounded &= first.Position is >= 0 and <= 120 && same.Position is >= 0 and <= 120 && other.Position is >= 0 and <= 120;
        }
        Check("seeded walkers with the same seed agree while different seeds diverge",
            sawPause && sawMove && seedDiverged && noLeap && bounded,
            $"pause={sawPause}, move={sawMove}, diverged={seedDiverged}, leap={noLeap}, bounded={bounded}");
        var stopped = first.Position;
        first.Advance(.1, 120, false, .25);
        Check("disabled walking reports paused and keeps its position", !first.IsMoving && first.Position == stopped,
            $"moving={first.IsMoving}, position={first.Position}/{stopped}");

        foreach (double extent in new[] { 20.0, 120, 2000 })
        {
            var walker = new PlatformWalker(31);
            walker.Advance(0, extent, true, .5);
            double distance = 0, longest = 0, fastest = 0;
            int completed = 0;
            for (int tick = 0; tick < 3000; tick++)
            {
                var before = walker.Position;
                walker.Advance(.1, extent, true, .5);
                var delta = Math.Abs(walker.Position - before);
                fastest = Math.Max(fastest, delta / .1);
                if (walker.IsMoving) distance += delta;
                else if (distance > 0)
                {
                    longest = Math.Max(longest, distance);
                    completed++;
                    distance = 0;
                }
            }
            Check($"walker takes short slow trips even on a {extent} DIP platform",
                completed >= 10 && longest > 15 && longest <= 42.001 && fastest is > 6 and <= 11.001,
                $"trips={completed}, longest={longest:0.###}, speed={fastest:0.###}");
        }

        first.Advance(.1, -50, true, .5);
        Check("negative extents collapse safely to zero", first.Position == 0 && !first.IsMoving, $"position={first.Position}");
        first.Advance(0, 180, true, .5);
        first.Advance(.1, 24, true, .5);
        Check("resizing the stage re-clamps the walker without a leap", first.Position is >= 0 and <= 24,
            $"position={first.Position}");
    }

    static MainViewModel PlatformViewModel(int count)
    {
        var slots = Enumerable.Range(0, count).Select(i => PlatformSlot((i % 4) switch
        {
            0 => "Claude",
            1 => "Codex",
            2 => "Gemini",
            _ => "Grok",
        }, i)).ToList();
        ProviderCard Card(string provider) => new()
        {
            Name = provider,
            Accent = provider switch { "Claude" => Palette.Claude, "Codex" => Palette.Codex, "Gemini" => Palette.Gemini, _ => Palette.Grok },
            Account = provider + " main",
            AppStatus = "fixture",
            AppStatusBrush = Palette.Muted,
            Footnote = "fixture",
            SessionsHeader = "fixture",
            Status = new StatusChip(StatusKind.None, DateTimeOffset.Now, TimeSpan.FromMinutes(5), false),
            Usage = [],
            AccountSlots = slots.Where(slot => slot.Provider == provider).ToList(),
        };
        var vm = new MainViewModel();
        vm.Update(Card("Claude"), Card("Codex"), Card("Gemini"), Card("Grok"), "");
        return vm;
    }

    static AccountUsageSlot PlatformSlot(string provider, int index)
    {
        var now = DateTimeOffset.Now;
        var usage = Presenter.DisplayUsage([new UsageWindow("seven_day", "주간 한도", 25, now.AddDays(2))], now).First();
        return new AccountUsageSlot
        {
            Role = index < 4 ? "메인" : $"서브{index - 3}",
            Id = $"platform-{provider}-{index}",
            Provider = provider,
            Account = $"{provider.ToUpperInvariant()}_{index}",
            Plan = "fixture",
            Usage = usage,
            Status = "fixture",
        };
    }

    static void SetPlatformAccounts(MainViewModel vm, int count) =>
        vm.Update(PlatformCard("Claude", count), PlatformCard("Codex", count),
            PlatformCard("Gemini", count), PlatformCard("Grok", count), "");

    static ProviderCard PlatformCard(string provider, int count)
    {
        var slots = Enumerable.Range(0, count)
            .Select(i => PlatformSlot((i % 4) switch { 0 => "Claude", 1 => "Codex", 2 => "Gemini", _ => "Grok" }, i))
            .Where(slot => slot.Provider == provider).ToList();
        return new ProviderCard
        {
            Name = provider,
            Accent = provider switch { "Claude" => Palette.Claude, "Codex" => Palette.Codex, "Gemini" => Palette.Gemini, _ => Palette.Grok },
            Account = provider + " main", AppStatus = "fixture", AppStatusBrush = Palette.Muted,
            Footnote = "fixture", SessionsHeader = "fixture", Status = new StatusChip(StatusKind.None, DateTimeOffset.Now, TimeSpan.FromMinutes(5), false),
            Usage = [], AccountSlots = slots,
        };
    }

    static List<Rect> PlatformRects(ItemsControl items, Visual ancestor) => Enumerable.Range(0, items.Items.Count)
        .Select(i => items.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
        .Where(container => container != null)
        .Select(container => BoundsIn(container!, ancestor)).ToList();

    static Rect WorkArea(MainWindow window) => (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
}
