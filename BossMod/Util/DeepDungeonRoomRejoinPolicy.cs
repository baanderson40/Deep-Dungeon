namespace BossMod;

// Rejoin room-route geometry through the normal safety-aware pathfinder. This policy does not
// infer that a point or a straight segment is traversable from room rectangles alone.
internal sealed class DeepDungeonRoomRejoinPolicy
{
    internal enum Phase { Idle, Rejoining, FailedHold }
    internal enum NavigationOwner { RoomRoute, Actor, Manual, Suspended }
    internal Phase State { get; private set; }
    internal string? Context { get; private set; }
    internal int Attempts { get; private set; }
    internal long Generation { get; private set; }
    internal WPos Source { get; private set; }
    internal WPos Next { get; private set; }
    internal WDir SourceSize { get; private set; }
    internal WDir NextSize { get; private set; }
    internal WPos Anchor { get; private set; }
    internal bool OwnsNavigation => State != Phase.Idle;
    private long _lastTick;
    private long _started;
    private long _activeMs;
    private long _attemptMs;
    private long _noProgressMs;
    private long _rejoinedMs;
    private long _healthyMs;
    private long _resumeNoProgressMs;
    private float _bestDistance;
    private float _resumeProgress;
    private float _resumeBestProgress;
    private WPos _entryPosition;
    private long _pausedAt;
    private long _gateTick;
    private long _failedStationaryMs;
    private bool _hasStationaryAnchor;
    private WPos _stationaryAnchor;
    private WPos _lastPlayer;
    private bool _hadFailureSample;

    internal void Reset()
    {
        State = Phase.Idle;
        Context = null;
        Attempts = 0;
        _lastTick = _started = _activeMs = _attemptMs = _noProgressMs = _rejoinedMs = _healthyMs = _resumeNoProgressMs = 0;
        _pausedAt = _gateTick = _failedStationaryMs = 0;
        _hasStationaryAnchor = false;
        _hadFailureSample = false;
        ++Generation;
    }

    internal void Bind(string? context)
    {
        if (Context == context)
            return;
        Reset();
        Context = context;
    }

    internal static NavigationOwner SelectOwner(bool manual, bool actorSelected, bool canNavigate, bool interactionHeld)
        => manual ? NavigationOwner.Manual : !canNavigate ? NavigationOwner.Suspended
        : actorSelected || interactionHeld ? NavigationOwner.Actor : NavigationOwner.RoomRoute;

    internal static bool AllowsRoomGoal(NavigationOwner owner)
        => owner is NavigationOwner.RoomRoute or NavigationOwner.Manual;

    internal static bool FreshFailedRoute(string key, string? resultKey, long now, long resultAt, bool failed)
        => key == resultKey && resultAt > 0 && now - resultAt is >= 0 and <= 500 && failed;

    internal void Pause(long now)
    {
        if (State == Phase.Rejoining)
        {
            // A different owner can legitimately relocate the player. Do not revive its old maneuver
            // on return; retain the budget and require fresh room-failure evidence before another try.
            State = Phase.Idle;
            _resumeProgress = _resumeBestProgress = (_lastPlayer - Source).Dot((Next - Source).Normalized());
            _resumeNoProgressMs = 0;
            ++Generation;
        }
        if (_pausedAt == 0)
            _pausedAt = now;
        _lastTick = _gateTick = now;
        _failedStationaryMs = _rejoinedMs = _healthyMs = 0;
        _hasStationaryAnchor = false;
        _hadFailureSample = false;
    }

    // Production entry point: owner arbitration and fresh navigation failure evidence precede geometry recovery.
    internal bool Step(long now, string? context, NavigationOwner owner, bool eligible, bool routeFailed,
        WPos player, WPos source, WDir sourceSize, WPos next, WDir nextSize)
    {
        Bind(context); // revalidate even while an actor, combat, or a previous failed hold owns movement
        _lastPlayer = player;
        if (context == null || owner == NavigationOwner.Manual)
        {
            if (Context != null || Attempts != 0)
                Reset();
            return false;
        }
        if (owner != NavigationOwner.RoomRoute || !eligible)
        {
            Pause(now);
            return false;
        }
        if (_pausedAt != 0)
        {
            if (_started != 0)
                _started += Math.Max(0, now - _pausedAt);
            _pausedAt = 0;
            _lastTick = now;
        }
        if (_gateTick > 0 && (now < _gateTick || now - _gateTick > 500))
        {
            _failedStationaryMs = 0;
            _hadFailureSample = false;
        }
        var elapsed = _gateTick > 0 && now >= _gateTick && now - _gateTick <= 500 ? now - _gateTick : 0;
        _gateTick = now;
        if (!_hasStationaryAnchor || (player - _stationaryAnchor).Length() >= 0.75f)
        {
            _stationaryAnchor = player;
            _hasStationaryAnchor = true;
            _failedStationaryMs = 0;
            elapsed = 0;
        }
        _failedStationaryMs = routeFailed && _hadFailureSample ? _failedStationaryMs + elapsed : 0;
        _hadFailureSample = routeFailed;
        var allowStart = _failedStationaryMs >= 10000 && LateralDistance(player, source, next) > 12;
        if (Attempts > 0)
            Update(now, player, Source, SourceSize, Next, NextSize, false, allowStart);
        else if (allowStart)
            Update(now, player, source, sourceSize, next, nextSize, false, true);
        return OwnsNavigation;
    }

    internal static float LateralDistance(WPos player, WPos source, WPos next)
    {
        var route = next - source;
        return route.LengthSq() > 0.01f ? Math.Abs((player - source).Cross(route.Normalized())) : float.NaN;
    }

    internal static bool InsideRoom(WPos player, WPos center, WDir size, float margin = 0.5f)
        => size.X > margin && size.Z > margin && Math.Abs(player.X - center.X) <= size.X - margin
            && Math.Abs(player.Z - center.Z) <= size.Z - margin;

    internal void Update(long now, WPos player, WPos source, WDir sourceSize, WPos next, WDir nextSize, bool suspended, bool allowStart = true)
    {
        if (_lastTick > 0 && (now < _lastTick || now - _lastTick > 500))
            _rejoinedMs = _healthyMs = 0; // missing observations cannot complete a continuous verification window
        var elapsed = _lastTick > 0 && now >= _lastTick && now - _lastTick <= 500 ? now - _lastTick : 0;
        _lastTick = now;
        if (suspended)
        {
            _rejoinedMs = _healthyMs = 0;
            if (OwnsNavigation && _started != 0 && now - _started >= 180000)
                Fail();
            return;
        }

        if (Attempts > 0)
        {
            var direction = (Next - Source).Normalized();
            var lateral = LateralDistance(player, Source, Next);
            // Entering the next connected room is genuine structural progress, not nearest-room jitter.
            if (InsideRoom(player, Next, NextSize))
            {
                var context = Context;
                Reset();
                Context = context;
                return;
            }
            if (OwnsNavigation)
            {
                _rejoinedMs = InsideRoom(player, Source, SourceSize) && lateral <= 8 ? _rejoinedMs + elapsed : 0;
                if (_rejoinedMs >= 1000)
                {
                    State = Phase.Idle;
                    _resumeProgress = (player - Source).Dot(direction);
                    _resumeBestProgress = _resumeProgress;
                    _healthyMs = _resumeNoProgressMs = 0;
                    ++Generation;
                    return; // resume normal route provisionally; do not refresh the episode budget yet
                }
            }
            else
            {
                _activeMs += elapsed;
                _healthyMs = lateral <= 10 ? _healthyMs + elapsed : 0;
                var progress = (player - Source).Dot(direction);
                if (progress >= _resumeBestProgress + 0.75f)
                {
                    _resumeBestProgress = progress;
                    _resumeNoProgressMs = 0;
                }
                else
                    _resumeNoProgressMs += elapsed;
                if (_healthyMs >= 5000 && progress >= _resumeProgress + 2)
                {
                    var context = Context;
                    Reset();
                    Context = context;
                }
                else if (allowStart && (_resumeNoProgressMs >= 15000 || now - _started >= 180000))
                {
                    BeginAttempt(now, player);
                    return;
                }
            }
        }

        if (State == Phase.Idle)
        {
            var lateral = LateralDistance(player, source, next);
            if (!allowStart || !float.IsFinite(lateral) || lateral <= 12 || sourceSize.X <= 2 || sourceSize.Z <= 2)
                return;
            Source = source;
            SourceSize = sourceSize;
            Next = next;
            NextSize = nextSize;
            _entryPosition = player;
            BeginAttempt(now, player);
            return;
        }
        if (State == Phase.FailedHold)
            return;

        _activeMs += elapsed;
        _attemptMs += elapsed;
        var distance = (player - Anchor).Length();
        if (distance + 0.75f < _bestDistance)
        {
            _bestDistance = distance;
            _noProgressMs = 0;
        }
        else
            _noProgressMs += elapsed;
        if (_activeMs >= 90000 || now - _started >= 180000)
            Fail();
        else if (_attemptMs >= 30000 || _noProgressMs >= 15000)
            BeginAttempt(now, player);
    }

    private void BeginAttempt(long now, WPos player)
    {
        if (Attempts >= 3 || _activeMs >= 90000 || _started != 0 && now - _started >= 180000)
        {
            Fail();
            return;
        }
        ++Attempts;
        if (_started == 0)
            _started = now;
        State = Phase.Rejoining;
        var direction = (Next - Source).Normalized();
        var inner = new WDir(Math.Max(0, SourceSize.X - 2), Math.Max(0, SourceSize.Z - 2));
        var extent = Math.Min(Math.Abs(direction.X) > 0.001f ? inner.X / Math.Abs(direction.X) : float.MaxValue,
            Math.Abs(direction.Z) > 0.001f ? inner.Z / Math.Abs(direction.Z) : float.MaxValue);
        var projection = Math.Clamp((_entryPosition - Source).Dot(direction), 0, extent * 0.5f);
        Anchor = Attempts switch
        {
            1 => Source + direction * projection,
            2 => Source + direction * (extent * 0.75f),
            _ => Source + direction.OrthoL() * (Math.Sign((_entryPosition - Source).Cross(direction)) * Math.Min(3, Math.Min(inner.X, inner.Z))),
        };
        _bestDistance = (player - Anchor).Length();
        _attemptMs = _noProgressMs = _rejoinedMs = 0;
        ++Generation;
    }

    private void Fail()
    {
        if (State == Phase.FailedHold)
            return;
        State = Phase.FailedHold;
        ++Generation;
    }

    internal static Func<WPos, float> RejoinGoal(WPos player, WPos anchor, float weight)
    {
        var startDistance = (player - anchor).Length();
        return p =>
        {
            var distance = (p - anchor).Length();
            if (distance <= 1)
                return weight * (2 - distance);
            return startDistance > 1 && distance + 0.25f < startDistance
                ? weight * (0.25f + 0.5f * (1 - distance / startDistance)) : 0;
        };
    }
}
