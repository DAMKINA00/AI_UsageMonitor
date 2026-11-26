namespace AIUsageMonitor.UI;

internal sealed class DesktopCharacterMotion(Random random)
{
    static readonly int[][] Gestures =
    [
        [0, 1, 2, 1, 0], // blink
        [0, 3, 4, 3, 0], // nod
        [0, 5, 6, 5, 0], // glance sideways
        [0, 7, 8, 7, 0], // tilt head
    ];
    readonly Random _random = random;
    readonly double _restDelay = 2.8 + random.NextDouble() * 2.8;
    readonly double _breathPeriod = 3.4 + random.NextDouble() * 2.5;
    readonly double _phase = random.NextDouble() * 2 * Math.PI;
    double _quietTime, _dragDistance, _lean, _settle;
    double _untilGesture = 3.8 + random.NextDouble() * 4;
    int _gestureStep, _gestureIndex = random.Next(Gestures.Length);
    bool _dragging;

    public bool Resting { get; private set; }
    public int RestFrame { get; private set; }

    public void SlideBy(double distance)
    {
        _dragging = true;
        _dragDistance += distance;
    }

    public void Release() => _dragging = false;

    public (double Stretch, double Angle) Advance(double seconds, double time, bool walking, bool speaking, bool animate)
    {
        _quietTime = _dragging || walking || speaking ? 0 : _quietTime + seconds;
        bool resting = _quietTime >= _restDelay;
        if (Resting != resting)
        {
            Resting = resting;
            _settle = resting ? 0.035 : -0.045;
        }
        RestFrame = NextRestFrame(seconds, resting && animate);

        var target = Math.Clamp(-_dragDistance / seconds / 46, -7, 7);
        _dragDistance = 0;
        _lean += (target - _lean) * (1 - Math.Exp(-9 * seconds));
        _settle *= Math.Exp(-11 * seconds);
        if (!animate) return (1, 0);

        double breath = Math.Sin(time * 2 * Math.PI / _breathPeriod + _phase);
        double talk = speaking ? 1.6 * Math.Sin(time * 2 * Math.PI / 0.8 + _phase) : 0;
        return (1 + 0.008 * breath + _settle, _lean + 0.65 * breath + talk);
    }

    int NextRestFrame(double seconds, bool enabled)
    {
        if (!enabled)
        {
            _gestureStep = 0;
            return 0;
        }
        if (_gestureStep == 0)
        {
            _untilGesture -= seconds;
            if (_untilGesture > 0) return 0;
            _untilGesture = 4.5 + _random.NextDouble() * 7;
        }
        var gesture = Gestures[_gestureIndex];
        int ticksPerFrame = _gestureIndex == 0 ? 1 : 2;
        int frame = gesture[_gestureStep++ / ticksPerFrame];
        if (_gestureStep == gesture.Length * ticksPerFrame)
        {
            _gestureStep = 0;
            _gestureIndex = (_gestureIndex + 1) % Gestures.Length;
        }
        return frame;
    }
}
