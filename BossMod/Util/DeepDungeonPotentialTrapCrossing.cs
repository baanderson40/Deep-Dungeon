namespace BossMod;

// One bounded, explicitly validated prediction-only crossing per navigation objective.
// No movement is generated here; normal pathfinding remains the only movement owner.
internal sealed class DeepDungeonPotentialTrapCrossing
{
    internal enum Phase { Waiting, Crossing, Finished }
    internal Phase State { get; private set; }
    internal string? Context { get; private set; }
    private long _lastTick;
    private long _blockedMs;
    private long _crossingStarted;
    private WPos _anchor;
    private bool _initialized;
    internal bool Expired(long now) => State == Phase.Crossing && (now < _crossingStarted || now - _crossingStarted >= 15000);

    internal void Reset()
    {
        State = Phase.Waiting;
        Context = null;
        _lastTick = _blockedMs = _crossingStarted = 0;
        _initialized = false;
    }

    internal bool Update(long now, string? context, bool eligible, bool normalGoalReached, bool validatedAlternative,
        WPos player, bool requestedByObjective = false)
    {
        if (context != Context)
        {
            Reset();
            Context = context;
        }
        var gap = _lastTick > 0 && (now < _lastTick || now - _lastTick > 500);
        var elapsed = _lastTick > 0 && !gap ? now - _lastTick : 0;
        _lastTick = now;
        if (context == null || !eligible || gap)
        {
            if (State == Phase.Crossing)
                State = Phase.Finished;
            _initialized = false;
            _blockedMs = 0;
            return false;
        }
        if (State == Phase.Finished)
            return false;
        if (State == Phase.Crossing)
        {
            if (normalGoalReached || !validatedAlternative || now - _crossingStarted >= 15000)
            {
                State = Phase.Finished;
                return false;
            }
            return true;
        }
        if (!_initialized || (player - _anchor).Length() >= 0.75f || normalGoalReached)
        {
            _anchor = player;
            _initialized = true;
            _blockedMs = 0;
            elapsed = 0;
        }
        _blockedMs = normalGoalReached ? 0 : _blockedMs + elapsed;
        if (!normalGoalReached && validatedAlternative && (_blockedMs >= 15000 || requestedByObjective))
        {
            State = Phase.Crossing;
            _crossingStarted = now;
            return true;
        }
        return false;
    }

    internal static bool FreshAlternative(string? key, (string? Key, long At, bool Available, bool Active) result, long now)
        => key != null && key == result.Key && result.At > 0 && now - result.At is >= 0 and <= 500 && result.Available;
}
