using System.Windows;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestCharacterLayers()
    {
        var separate = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(20, 0, 10, 10),
        };
        var overlap = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(5, 0, 10, 10),
        };
        var layers = new CharacterLayers(2);
        var initial = layers.Update(separate, [true, false]).ToArray();
        Check("disjoint characters keep their initial back-to-front order",
            initial.SequenceEqual([0, 1]), Describe(initial));

        var movingFront = layers.Update(overlap, [true, false]).ToArray();
        Check("a moving character comes in front on new contact",
            movingFront.SequenceEqual([1, 0]), Describe(movingFront));

        var bothStopped = layers.Update(overlap, [false, false]).ToArray();
        Check("an overlapping pair keeps its order after stopping",
            bothStopped.SequenceEqual(movingFront), Describe(bothStopped));

        var backStartsMoving = layers.Update(overlap, [false, true]).ToArray();
        Check("an overlapping pair keeps its order when the back character moves",
            backStartsMoving.SequenceEqual(movingFront), Describe(backStartsMoving));

        var separated = layers.Update(separate, [false, true]).ToArray();
        Check("separating characters does not reorder them",
            separated.SequenceEqual(movingFront), Describe(separated));

        var oppositeReentry = layers.Update(overlap, [false, true]).ToArray();
        Check("the opposite mover gets a new order after reentry",
            oppositeReentry.SequenceEqual([0, 1]), Describe(oppositeReentry));

        var bothMovingLayers = new CharacterLayers(2);
        var bothMovingFirst = bothMovingLayers.Update(overlap, [true, true]).ToArray();
        var bothMovingAgain = bothMovingLayers.Update(overlap, [true, true]).ToArray();
        Check("two moving characters keep a stable order",
            bothMovingFirst.SequenceEqual([0, 1]) && bothMovingAgain.SequenceEqual(bothMovingFirst),
            $"first={Describe(bothMovingFirst)}, again={Describe(bothMovingAgain)}");

        var connectedLayers = new CharacterLayers(3);
        var firstPair = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(6, 0, 10, 10),
            new Rect(30, 0, 10, 10),
        };
        var connected = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(6, 0, 10, 10),
            new Rect(-8, 0, 10, 10),
        };
        var pairOrder = connectedLayers.Update(firstPair, [true, false, false]).ToArray();
        var withNewCharacter = connectedLayers.Update(connected, [true, false, false]).ToArray();
        Check("a new connected character preserves the existing pair order",
            pairOrder.SequenceEqual([1, 0, 2]) && withNewCharacter.SequenceEqual([2, 1, 0]),
            $"pair={Describe(pairOrder)}, connected={Describe(withNewCharacter)}");

        var thirdLeaves = connectedLayers.Update(firstPair, [true, false, false]).ToArray();
        Check("a separated third character does not cause another reorder",
            thirdLeaves.SequenceEqual(withNewCharacter), Describe(thirdLeaves));
    }

    static string Describe(IEnumerable<int> order) => string.Join(",", order);
}
