namespace BossMod;

// Local observation only: no inference about another client's pathfinder or input state.
internal static class DeepDungeonPartyPolicy
{
    internal enum RegroupState { Clearing, Waiting, Returning, Verifying, Blocked, FailedHold }
    internal const float PassageInnerRadius = 1;
    // Rasterization requires all four 0.5y-cell corners inside the goal: worst-case radius
    // is sqrt(0.5^2 + 0.5^2). Leave room for retry offsets and movement's 0.1y deadzone too.
    internal const float PassageGoalRadius = 0.75f;
    internal const float PassageReplanOffset = 0.125f;
    internal const float RecoveryFollowerDisplacement = 2;
    internal const float RecoveryProbeRadius = 0.75f;
    // Both clients may stop short of their requested centers. Reserve probe radius plus
    // arrival/grid slack so a healthy follower moves far enough to satisfy the response test.
    internal const float RecoveryArrivalSlack = 0.5f;
    internal static bool ValidDistances(float follow, float resume, float maximum)
        => float.IsFinite(follow) && float.IsFinite(resume) && float.IsFinite(maximum)
            && follow >= 1 && follow < resume && resume < maximum;

    internal static bool CanAssist(bool onEnmityList, bool inCombat, bool targetsPartyMember)
        => onEnmityList || inCombat && targetsPartyMember;

    internal static bool CanInitiateBoss(bool enabled, bool leaderRole, bool verifiedLeader, bool bossFloor, bool ready, bool completed, bool regrouping)
        => enabled && leaderRole && verifiedLeader && bossFloor && ready && !completed && !regrouping;

    internal static bool CanPrioritizeBoss(bool solo, bool roleControlled, bool leaderAllowed)
        => leaderAllowed || solo && !roleControlled;

    internal static bool RecentManualMovement(DateTime at, DateTime now)
        => at != default && now >= at && now - at < TimeSpan.FromSeconds(0.5);

    internal static bool BeyondVerificationLeash(float distance, float height, float maximum)
        => !float.IsFinite(distance) || !float.IsFinite(height) || distance > maximum || Math.Abs(height) > 3.5f;

    internal static bool HoldVerificationTravel(bool allWithinResume, bool suspended)
        => !allWithinResume || suspended;

    // Entry inference only; server readiness is never inferred from proximity.
    internal static bool ShouldStack(bool passageActive, bool passagePresent, float leaderDistance, float heightDifference)
        => passageActive && passagePresent && float.IsFinite(leaderDistance) && leaderDistance is >= 0 and <= 6
            && float.IsFinite(heightDifference) && Math.Abs(heightDifference) <= 3;

    internal static bool UseLocalPassageGoal(ulong passage, ulong lockedPassage, float distance, float height)
        => passage != 0 && float.IsFinite(distance) && distance >= 0 && float.IsFinite(height) && Math.Abs(height) <= 3
            && distance <= (lockedPassage == passage ? 10 : 6);

    internal static Func<WPos, float> PassageAssemblyGoal(WPos center, float weight, int replan = 0)
    {
        // Actual alternative inner targets, not generation-only retries. Entire arrival region
        // stays inside the 1y assembly radius; none of these goals asserts server readiness.
        var offset = replan switch { 1 => new WDir(PassageReplanOffset, 0), 2 => new WDir(0, PassageReplanOffset), 3 => new WDir(-PassageReplanOffset, 0), _ => default };
        var target = center + offset;
        return p =>
        {
            var distance = (p - target).Length();
            return distance <= PassageGoalRadius ? weight * (2 - distance / PassageGoalRadius) : 0;
        };
    }

    internal static WPos? RecoveryProbe(WPos anchor, WDir approach, float follow, float maximum, int attempt)
    {
        var offset = follow + RecoveryFollowerDisplacement + RecoveryProbeRadius + RecoveryArrivalSlack;
        if (!float.IsFinite(offset) || offset + 1 >= maximum || approach.LengthSq() < 0.5f || attempt is < 1 or > 3)
            return null;
        var direction = attempt == 1 ? approach : attempt == 2 ? approach.OrthoL() : -approach.OrthoL();
        return anchor + direction * offset;
    }

    internal static WPos? RecoveryProbeFromCurrent(WPos follower, WPos leader, WDir fallbackApproach, float follow, float maximum, int attempt)
    {
        var approach = follower - leader;
        return RecoveryProbe(follower, approach.LengthSq() > 0.25f ? approach.Normalized() : fallbackApproach, follow, maximum, attempt);
    }

    internal sealed class PassageAssembly
    {
        internal ulong Passage { get; private set; }
        internal bool Blocked { get; private set; }
        internal int Replans { get; private set; }
        private long _lastTick;
        private long _candidateMs;
        private long _awayMs;
        private long _activeMs;
        private long _started;
        private ulong _candidate;
        private float _bestDistance = float.MaxValue;
        private long _lastProgress;

        internal void Reset()
        {
            Passage = _candidate = 0;
            Blocked = false;
            Replans = 0;
            _bestDistance = float.MaxValue;
            _lastProgress = 0;
            _lastTick = _candidateMs = _awayMs = _activeMs = _started = 0;
        }

        internal bool ObserveProgress(long now, float distance, bool suspended)
        {
            if (Passage == 0 || Blocked || suspended || !float.IsFinite(distance))
            {
                _lastProgress = now;
                return false;
            }
            if (_lastProgress == 0 || distance + 0.5f < _bestDistance || distance <= PassageInnerRadius)
            {
                _bestDistance = distance;
                _lastProgress = now;
                return false;
            }
            if (now - _lastProgress < 10000)
                return false;
            _lastProgress = now;
            if (Replans < 3)
                ++Replans;
            else
                Blocked = true;
            return true;
        }

        internal bool Update(long now, ulong passage, bool active, float leaderDistance, float height, bool committed, bool suspended)
        {
            if (Passage == 0 && (now < _lastTick || now - _lastTick > 500 || suspended))
                _candidateMs = 0;
            var elapsed = Elapsed(now, _lastTick);
            _lastTick = now;
            if (Passage != 0 && now - _started >= 180000)
                Blocked = true;
            if (active && suspended && Passage != 0)
                return true;
            if (!active || passage == 0 || Passage != 0 && passage != Passage)
            {
                Reset();
                _lastTick = now;
                return false;
            }
            if (suspended)
                return Passage != 0;
            if (Passage == 0)
            {
                if (!ShouldStack(active, true, leaderDistance, height))
                {
                    _candidate = 0;
                    _candidateMs = 0;
                    return false;
                }
                if (_candidate != passage)
                {
                    _candidate = passage;
                    _candidateMs = 0;
                }
                _candidateMs += elapsed;
                if (!committed && _candidateMs < 500)
                    return false;
                Passage = passage;
                _started = now;
            }
            _awayMs = leaderDistance > 10 || Math.Abs(height) > 3 ? _awayMs + elapsed : 0;
            if (_awayMs >= 2000)
            {
                Reset();
                return false;
            }
            _activeMs += elapsed;
            Blocked |= _activeMs >= 60000 || now - _started >= 180000;
            return true;
        }
    }

    // Fixed-anchor displacement, never leader-to-member distance. A gap starts a new evidence window.
    internal sealed class FollowerProgress
    {
        private WPos _anchor;
        private WPos _lastPosition;
        private float _height;
        private long _lastTick;
        private long _movingMs;
        private bool _initialized;
        private WPos? _goal;
        internal float Displacement { get; private set; }

        internal void Reset() { _initialized = false; _lastTick = _movingMs = 0; Displacement = 0; }

        internal bool Observe(long now, WPos position, float height, bool eligible, WPos? goal = null)
        {
            var elapsed = Elapsed(now, _lastTick);
            if (!eligible || !_initialized || now < _lastTick || now - _lastTick > 500 || _goal != goal
                || (position - _lastPosition).Length() > 5 || Math.Abs(height - _height) > 3)
            {
                _anchor = position;
                _movingMs = 0;
                _initialized = eligible;
            }
            _lastTick = now;
            _lastPosition = position;
            _height = height;
            _goal = goal;
            Displacement = (position - _anchor).Length();
            var towardGoal = goal is not { } target || (position - target).Length() + 1 < (_anchor - target).Length();
            _movingMs = eligible && Displacement >= RecoveryFollowerDisplacement && towardGoal ? _movingMs + elapsed : 0;
            return _movingMs >= 3000;
        }
    }

    private static long Elapsed(long now, long previous) => previous > 0 && now >= previous && now - previous <= 500 ? now - previous : 0;

    internal sealed class Regroup
    {
        internal RegroupState State { get; private set; }
        internal int Attempts { get; private set; }
        internal int RejectedCandidates { get; private set; }
        internal int CandidatesWithLeaderApproach { get; private set; }
        internal string? FailureReason { get; private set; }
        internal string? LastAttemptOutcome { get; private set; }
        internal long ActiveMilliseconds => FailureReason == null ? _activeMs : _failureTiming.Active;
        internal long AttemptMilliseconds => FailureReason == null ? _returnMs : _failureTiming.Attempt;
        internal long NoProgressMilliseconds => FailureReason == null ? _noProgressMs : _failureTiming.NoProgress;
        internal long TogetherMilliseconds => _togetherMs;
        internal long ReleaseVerificationRequiredMilliseconds => _targetNoLongerLiving ? 3000 : 5000;
        internal long WallMilliseconds => FailureReason == null ? CurrentWallMilliseconds : _failureTiming.Wall;
        internal bool FollowerMovementObserved => _followerMoved;
        private long CurrentWallMilliseconds => _started > 0 ? Math.Max(0, _lastTick - _started) : 0;
        private (long Active, long Attempt, long NoProgress, long Wall) _failureTiming;
        private long _lastTick;
        private long _separatedMs;
        private long _togetherMs;
        private long _returnMs;
        private long _noProgressMs;
        private long _activeMs;
        private long _started;
        private bool _attemptActive;
        private bool _followerMoved;
        private bool _targetNoLongerLiving;
        private bool _verifying;
        private long _verificationOutsideMs;
        private int _lastApproachCandidate;

        internal void Reset()
        {
            State = RegroupState.Clearing;
            Attempts = 0;
            RejectedCandidates = CandidatesWithLeaderApproach = _lastApproachCandidate = 0;
            _lastTick = _separatedMs = _togetherMs = _returnMs = _noProgressMs = 0;
            _activeMs = _started = 0;
            _attemptActive = _followerMoved = false;
            _targetNoLongerLiving = false;
            FailureReason = LastAttemptOutcome = null;
            _failureTiming = default;
            _verifying = false;
            _verificationOutsideMs = 0;
        }

        internal void TargetNoLongerLiving()
        {
            _targetNoLongerLiving = true;
            _followerMoved = false;
            _togetherMs = 0;
            _verifying = false;
            _verificationOutsideMs = 0;
        }

        internal void RejectAttempt(string reason = "attempt rejected")
        {
            if (_verifying || !_attemptActive)
                return; // a completed probe cannot spend more attempts while regrouping is being verified
            ++RejectedCandidates;
            _attemptActive = false;
            LastAttemptOutcome = reason;
            if (Attempts >= 3)
                Fail("attempt budget exhausted");
            else
                State = RegroupState.Waiting;
        }

        private void Fail(string reason)
        {
            if (FailureReason == null)
            {
                FailureReason = reason;
                _failureTiming = (_activeMs, _returnMs, _noProgressMs, CurrentWallMilliseconds);
            }
            State = RegroupState.FailedHold;
        }

        internal RegroupState Update(long now, bool available, bool beyondMaximum, bool allWithinResume, bool inCombat,
            bool recoveryProgress = false, bool leaderApproachProgress = false, bool? beyondVerification = null)
        {
            // Missing/loading frames do not advance the catch-up/attempt clocks.
            var elapsed = Elapsed(now, _lastTick);
            _lastTick = now;
            if (_started != 0 && now - _started >= 180000)
                Fail("180s wall deadline expired");
            if (!available)
            {
                if (!_verifying)
                    _togetherMs = 0;
                _verificationOutsideMs = 0;
                if (State != RegroupState.FailedHold)
                    State = RegroupState.Blocked;
                return State;
            }
            if (State == RegroupState.Clearing)
            {
                if (!beyondMaximum)
                    return State;
                elapsed = 0; // Do not charge the last clearing frame to a new separation episode.
            }
            if (inCombat)
            {
                if (!_verifying)
                    _togetherMs = 0;
                _verificationOutsideMs = 0;
                _separatedMs += elapsed;
                if (State != RegroupState.FailedHold)
                    State = RegroupState.Waiting;
                return State;
            }
            if (Attempts > 0)
                _activeMs += elapsed;
            if (!allWithinResume)
                _targetNoLongerLiving = false; // another living straggler still needs this episode's budget
            if (_activeMs >= 90000)
                Fail("90s active recovery budget exhausted");
            _followerMoved |= Attempts > 0 && recoveryProgress;
            if (_attemptActive && !_verifying && leaderApproachProgress && _lastApproachCandidate != Attempts)
            {
                _lastApproachCandidate = Attempts;
                ++CandidatesWithLeaderApproach;
            }
            if (State == RegroupState.FailedHold)
            {
                // Failure remains latched unless physical recovery is observed, or retry is explicit.
                _togetherMs = allWithinResume && (_followerMoved || _targetNoLongerLiving) ? _togetherMs + elapsed : 0;
                if (_togetherMs >= (_targetNoLongerLiving ? 3000 : 5000))
                {
                    Reset();
                    _lastTick = now;
                }
                return State;
            }
            if (_verifying)
            {
                _verificationOutsideMs = (beyondVerification ?? beyondMaximum) ? _verificationOutsideMs + elapsed : 0;
                if (_verificationOutsideMs < 1000)
                {
                    // Resume-radius jitter pauses credit; it does not erase proof or revive a retired probe.
                    if (allWithinResume)
                        _togetherMs += elapsed;
                    if (_togetherMs >= 5000)
                    {
                        Reset();
                        _lastTick = now;
                        return State;
                    }
                    return State = RegroupState.Verifying;
                }
                _verifying = false;
                LastAttemptOutcome = "party left maximum verification leash; recovery candidate required";
                _togetherMs = _verificationOutsideMs = 0;
                _attemptActive = false; // genuine sustained relapse starts a fresh, budgeted local maneuver
            }
            // An ordinary catch-up can finish by proximity. Once returning starts, the follower must move.
            _togetherMs = allWithinResume && (Attempts == 0 || _targetNoLongerLiving) ? _togetherMs + elapsed : 0;
            if (_togetherMs >= (Attempts == 0 || _targetNoLongerLiving ? 3000 : 5000))
            {
                Reset();
                _lastTick = now;
                return State;
            }
            if (_followerMoved && allWithinResume)
            {
                LastAttemptOutcome = "follower response observed; verifying party convergence";
                _verifying = true;
                _togetherMs = _verificationOutsideMs = 0;
                return State = RegroupState.Verifying;
            }
            _separatedMs += elapsed;
            if ((Attempts == 0 || _targetNoLongerLiving) && allWithinResume || _separatedMs < 15000)
            {
                State = RegroupState.Waiting;
                return State;
            }
            if (!_attemptActive)
            {
                if (Attempts >= 3)
                {
                    Fail("attempt budget exhausted");
                    return State;
                }
                ++Attempts;
                _attemptActive = true;
                if (_started == 0)
                    _started = now;
                _returnMs = _noProgressMs = 0;
                elapsed = 0;
            }
            State = RegroupState.Returning;
            _returnMs += elapsed;
            // Leader progress keeps a bounded maneuver alive, but never satisfies follower recovery.
            _noProgressMs = recoveryProgress || leaderApproachProgress ? 0 : _noProgressMs + elapsed;
            if (_activeMs >= 90000)
                Fail("90s active recovery budget exhausted");
            else if (_returnMs >= 30000 || _noProgressMs >= 15000)
                RejectAttempt(_returnMs >= 30000 ? "30s attempt deadline expired" : "15s without leader approach or follower progress");
            return State;
        }
    }
}
