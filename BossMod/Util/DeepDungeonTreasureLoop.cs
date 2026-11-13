namespace BossMod;

// One chest's bounded physical history, including intervening room-owned movement.
// Crossing the stationary diagnostic radius must not hide a loop.
internal sealed class DeepDungeonTreasureLoop
{
    private readonly Queue<(DateTime At, WPos Position, WDir Direction)> _samples = new();
    private string? _context;
    private ulong _actor;
    private DateTime _lastObservation;
    private DateTime _lastSample;
    internal bool IsOscillating { get; private set; }

    internal void Reset()
    {
        _samples.Clear();
        _context = null;
        _actor = 0;
        _lastObservation = _lastSample = default;
        IsOscillating = false;
    }

    internal bool Observe(DateTime now, string context, ulong actor, bool eligible, WPos player, WDir direction)
    {
        if (!eligible)
        {
            Reset();
            return false;
        }
        if (_context != context || (_lastObservation != default && (now < _lastObservation || now - _lastObservation > TimeSpan.FromMilliseconds(500)))
            || actor != 0 && actor != _actor)
            Reset();
        _context = context;
        _lastObservation = now;
        if (actor != 0)
            _actor = actor;
        if (_lastSample == default || now - _lastSample >= TimeSpan.FromMilliseconds(250))
        {
            _samples.Enqueue((now, player, direction));
            _lastSample = now;
        }
        while (_samples.Count > 60 || _samples.TryPeek(out var oldest) && now - oldest.At > TimeSpan.FromSeconds(15))
            _samples.Dequeue();
        IsOscillating = false;
        if (actor == 0 || _samples.Count < 8)
            return false;
        var samples = _samples.ToArray();
        if (samples[^1].At - samples[0].At < TimeSpan.FromSeconds(3))
            return false;
        var traveled = 0f;
        for (int i = 1; i < samples.Length; ++i)
            traveled += (samples[i].Position - samples[i - 1].Position).Length();
        var net = (samples[^1].Position - samples[0].Position).Length();
        // Include genuine circuits, not just 180-degree reversals. Never infer collision.
        return IsOscillating = net <= 1.5f && traveled >= 3
            && (traveled >= 12 || DeepDungeonPolicy.CountDirectionReversals(samples.Select(s => s.Direction).ToArray()) >= 4);
    }
}
