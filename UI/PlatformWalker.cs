namespace AIUsageMonitor.UI;

/// <summary>Independent, bounded strolls. A small time step avoids jumps after sleep or a busy UI.</summary>
public sealed class PlatformWalker(int seed)
{
    readonly Random _random = new(seed);
    bool _initialized;
    double _target, _pause, _speed;
    public double Position { get; private set; }
    public bool FacingLeft { get; private set; }
    public bool IsMoving { get; private set; }

    public void Advance(double seconds, double extent, bool allowed, double initialFraction)
    {
        extent = double.IsFinite(extent) ? Math.Max(0, extent) : 0;
        seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 0.1) : 0;
        if (!_initialized)
        {
            Position = extent * Math.Clamp(initialFraction, 0, 1);
            _target = Position;
            _pause = _random.NextDouble() * 3;
            _initialized = extent > 0;
        }
        Position = Math.Clamp(Position, 0, extent);
        _target = Math.Clamp(_target, 0, extent);
        IsMoving = false;
        if (!allowed || extent <= 0 || seconds == 0) return;
        if (_pause > 0) { _pause -= seconds; return; }
        if (Math.Abs(_target - Position) < 0.5)
        {
            _target = _random.NextDouble() * extent;
            _speed = 12 + _random.NextDouble() * 15;
            _pause = 1 + _random.NextDouble() * 4;
            return;
        }
        FacingLeft = _target < Position;
        Position += Math.Sign(_target - Position) * Math.Min(Math.Abs(_target - Position), _speed * seconds);
        IsMoving = true;
    }
}
