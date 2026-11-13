namespace BossMod.Global.DeepDungeon;

public abstract partial class AutoClear
{
    public override void CalculateEncounterAIHints(int playerSlot, Actor player, AIHints hints)
    {
        hints.DeepDungeonRoleActive = _config.Enable;
        if (!_config.Enable)
        {
            _partyRegroup.Reset();
            _partyContext = default;
            _passageAssembly.Reset();
            _passageFailureNotified = false;
            ResetRecovery();
            World.DeepDungeon.PartyCoordinationStatus = null;
            ResetRoomRejoin();
            return;
        }
        hints.DeepDungeonPartyMode = _config.Role == AutoDDConfig.PartyRole.Follower
            || World.Party.Members.Take(PartyState.MaxPartySize).Count(m => m.IsValid()) > 1;
        if (!DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor)
            || player.IsDead || World.DeepDungeon.BossCompletionObserved)
            return;
        CalculatePartyHints(player, hints);
    }

    private readonly DeepDungeonPartyPolicy.Regroup _partyRegroup = new();
    private (DeepDungeonState.DungeonType Dungeon, byte Floor, ushort Territory, AutoDDConfig.PartyRole Role, ulong Leader) _partyContext;
    private ulong _recoverMember;
    private ulong _recoverContentID;
    private readonly DeepDungeonPartyPolicy.FollowerProgress _followerProgress = new();
    private readonly DeepDungeonPartyPolicy.PassageAssembly _passageAssembly = new();
    private WPos _recoveryAnchor;
    private WDir _recoveryApproach;
    private WPos? _recoveryProbe;
    private WPos? _leaderApproachGoal;
    private float _leaderApproachDistance = float.MaxValue;
    private int _probeAttempt;
    private int _recoveryEpisode;
    private bool _verifyingFollow;
    private string? _lastPartyIntent;
    private long _partyNavigationGeneration;
    private long _passageFollowUntil;
    private bool _recoveryFailureNotified;
    private bool _passageFailureNotified;
    private string? _recoveryFailureDetail;
    private (bool Available, bool WithinResume, bool Suspended, bool FollowerProgress, bool LeaderApproachProgress) _regroupObservation;
    private long _regroupObservationAt;
    private string _partyNavigationMode = "hold";
    private bool _partyHolding;
    private WPos _partyHoldAnchor;
    private string? _partyStatus;

    private void LogPartyStatus(string status)
    {
        World.DeepDungeon.PartyCoordinationStatus = status;
        if (_partyStatus == status)
            return;
        _partyStatus = status;
        Service.Log($"[AutoDD] party coordination: {status}.");
        if (status.StartsWith("regroup failed;", StringComparison.Ordinal) && !_recoveryFailureNotified)
        {
            _recoveryFailureNotified = true;
            Service.ChatMessage($"AutoDD: {status}.");
        }
        else if (status.StartsWith("passage assembly blocked;", StringComparison.Ordinal) && !_passageFailureNotified)
        {
            _passageFailureNotified = true;
            Service.ChatMessage($"AutoDD: {status}.");
        }
    }

    // Returns true when party coordination owns objectives this frame.
    private bool CalculatePartyHints(Actor player, AIHints hints)
    {
        if (!_config.Enable)
        {
            _partyRegroup.Reset();
            _partyContext = default;
            _passageAssembly.Reset();
            _passageFailureNotified = false;
            ResetRecovery();
            World.DeepDungeon.PartyCoordinationStatus = null;
            ResetRoomRejoin();
            return false;
        }

        var party = Service.PartyList;
        var solo = party.Length <= 1 && World.Party.Members.Take(PartyState.MaxPartySize).Count(m => m.IsValid()) <= 1;
        ulong leaderID = 0;
        Actor? leader = null;
        if (solo)
        {
            leaderID = player.InstanceID;
            leader = player;
        }
        else if (party.PartyLeaderIndex < party.Length && party[(int)party.PartyLeaderIndex] is { } leaderMember)
        {
            // Party list ordering is unrelated to World.Party slot ordering.
            var member = World.Party.Members.Take(PartyState.MaxPartySize)
                .FirstOrDefault(m => m.ContentId != 0 && m.ContentId == (ulong)leaderMember.ContentId);
            leaderID = member.InstanceId;
            leader = World.Actors.Find(leaderID);
        }

        var context = (Palace.DungeonId, Palace.Floor, World.CurrentZone, _config.Role, leaderID);
        // An unresolved actor/list snapshot is not proof that the party changed leaders.
        if (!solo && leaderID == 0 && _partyContext.Leader != 0
            && _partyContext.Dungeon == Palace.DungeonId && _partyContext.Floor == Palace.Floor
            && _partyContext.Territory == World.CurrentZone && _partyContext.Role == _config.Role)
            return Hold("waiting for verified party leader identity");
        if (_partyContext != context)
        {
            _partyContext = context;
            _partyRegroup.Reset();
            ResetRecovery();
            _passageAssembly.Reset();
            _passageFailureNotified = false;
            _partyHolding = false;
            ClearDesiredRoom("party role or leader context changed");
            ResetRoomRejoin();
            _deferredTreasureAt.Clear();
            _treasureProgress.Clear();
        }

        if (World.DeepDungeon.PartyCoordinationRetryRequested)
        {
            World.DeepDungeon.PartyCoordinationRetryRequested = false;
            _partyRegroup.Reset();
            ResetRecovery();
            _passageAssembly.Reset();
            _passageFailureNotified = false;
            _partyHolding = false;
            ++_recoveryEpisode;
        }

        if (!DeepDungeonPartyPolicy.ValidDistances(_config.FollowDistance, _config.RegroupResumeDistance, _config.MaxLeadDistance))
            return Hold("invalid party distances: require 1 <= follow < resume < maximum lead");

        if (_config.Role == AutoDDConfig.PartyRole.Follower)
        {
            if (leader == null || leader.IsDeadOrDestroyed || leader == player)
                return Hold("follower waiting for a living external party leader");
            if (!Utils.IsScreenReady() || Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                || Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                return Hold("follower waiting for screen/territory readiness");
            if (DeepDungeonPartyPolicy.RecentManualMovement(hints.DeepDungeonCoordinationManualAt, World.CurrentTime))
            {
                _partyHolding = false;
                return Hold("follower yielding to manual movement");
            }

            var passage = Palace.PassageActive ? World.Actors.Where(a => !a.IsDestroyed
                && a.OID is (uint)OID.CairnPalace or (uint)OID.BeaconHoH or (uint)OID.PylonEO or (uint)OID.PylonPT)
                .Where(a => _passageAssembly.Passage == 0 || a.InstanceID == _passageAssembly.Passage)
                .Where(a => _passageAssembly.Passage != 0 || Math.Abs(a.PosRot.Y - leader.PosRot.Y) <= 3)
                .Where(a => _passageAssembly.Passage != 0 || TryFindClosestRoom(a.Position, out var passageRoom)
                    && TryFindClosestRoom(leader.Position, out var leaderRoom) && passageRoom.Index == leaderRoom.Index)
                .OrderBy(a => (a.Position - leader.Position).LengthSq()).FirstOrDefault() : null;
            if (Palace.PassageActive && passage == null && _passageAssembly.Passage != 0)
            {
                _passageAssembly.Update(Environment.TickCount64, _passageAssembly.Passage, true, float.MaxValue, 0, false, true);
                return Hold(_passageAssembly.Blocked
                    ? "passage assembly blocked; locked passage actor unavailable, explicitly retry when ready"
                    : "follower waiting for locked passage actor");
            }
            var stack = _passageAssembly.Update(Environment.TickCount64, passage?.InstanceID ?? 0, Palace.PassageActive,
                passage != null ? (passage.Position - leader.Position).Length() : float.MaxValue,
                passage != null ? passage.PosRot.Y - leader.PosRot.Y : float.MaxValue, false, player.InCombat || leader.InCombat);
            if (stack && passage != null && ObservePassageAssembly(player, passage, hints, player.InCombat || leader.InCombat)
                && !_passageAssembly.Blocked && (leader.Position - passage.Position).Length() > 3
                && (leader.Position - player.Position).Length() > _config.FollowDistance + 1)
            {
                // Local evidence only: ineffective stacking plus a leader outside the inner assembly area.
                // Briefly honor the leader's changed approach; retain the passage latch and finite budget.
                _passageFollowUntil = Environment.TickCount64 + 5000;
                Service.Log($"[AutoDD] passage assembly follow fallback: actor={passage.InstanceID:X}, duration=5s, replan={_passageAssembly.Replans}/3; remote recovery intent unknown.");
            }
            if (stack && _passageAssembly.Blocked)
                return Hold("passage assembly blocked; deadline exhausted, move party or explicitly retry");
            var passageFallback = stack && Environment.TickCount64 < _passageFollowUntil
                && passage != null && (leader.Position - passage.Position).Length() > 3;
            stack &= !passageFallback;
            _partyHolding = false;
            if (stack)
            {
                _partyNavigationMode = $"stack:{passage!.InstanceID:X}:replan:{_passageAssembly.Replans}";
                AddPassageGoals(player, passage, hints, 2, _passageAssembly.Replans);
            }
            else
            {
                _partyNavigationMode = $"follow:{leaderID:X}";
                // Progress gradient avoids a flat, distant circle yielding no useful local destination.
                hints.AddGoalZone(DeepDungeonPolicy.PassageProgressGoal(leader.Position, player.Position, _config.FollowDistance, 100, 1, 1), "deepdungeon-party-follow-progress");
                hints.AddGoalZone(AIHints.GoalSingleTarget(leader.Position, _config.FollowDistance, 2), "deepdungeon-party-follow");
            }
            var center = stack ? passage!.Position : leader.Position;
            var radius = stack ? DeepDungeonPartyPolicy.PassageInnerRadius : _config.FollowDistance;
            hints.DeepDungeonPartyCombatArea = p => p.InCircle(center, radius);
            LogPartyStatus(stack ? "follower stacking at passage" : passageFallback
                ? $"follower temporarily tracking leader {leaderID:X} after passage no-progress; assembly budget retained"
                : $"follower tracking party leader {leaderID:X}");
            OwnObjectives(leader);
            return true;
        }

        if (leader == null || leader != player)
            return Hold("configured leader is not the resolved party leader");
        if (solo)
        {
            if (DeepDungeonPartyPolicy.RecentManualMovement(hints.DeepDungeonCoordinationManualAt, World.CurrentTime))
            {
                _partyHolding = false;
                return Hold("leader yielding to manual movement");
            }
            if (_passageAssembly.Passage == 0)
            {
                _partyHolding = false;
            }
            AllowBossInitiation();
            return false;
        }

        // The live roster remains authoritative when a member has no synchronized actor yet.
        var actors = new List<Actor?>();
        var localContentID = World.Party.Members[PartyState.PlayerSlot].ContentId;
        for (var i = 0; i < party.Length && i < PartyState.MaxPartySize; ++i)
        {
            var liveMember = party[i];
            if (liveMember != null && localContentID != 0 && (ulong)liveMember.ContentId == localContentID)
                continue;
            var member = liveMember != null ? World.Party.Members.Take(PartyState.MaxPartySize)
                .FirstOrDefault(m => m.ContentId != 0 && m.ContentId == (ulong)liveMember.ContentId) : default;
            actors.Add(World.Actors.Find(member.InstanceId));
        }
        var available = actors.All(a => a is { IsDestroyed: false });
        var living = actors.Where(a => a is { IsDeadOrDestroyed: false }).Cast<Actor>().ToArray();
        var furthest = living.OrderByDescending(a => (a.Position - player.Position).LengthSq()).FirstOrDefault();
        // Large height differences are not successful regrouping, even when XZ positions coincide.
        var withinResume = available && living.All(a => (a.Position - player.Position).Length() <= _config.RegroupResumeDistance
            && Math.Abs(a.PosRot.Y - player.PosRot.Y) <= 3);
        var beyondMaximum = living.Any(a => (a.Position - player.Position).Length() > _config.MaxLeadDistance
            || Math.Abs(a.PosRot.Y - player.PosRot.Y) > 3);
        var beyondVerification = living.Any(a => DeepDungeonPartyPolicy.BeyondVerificationLeash(
            (a.Position - player.Position).Length(), a.PosRot.Y - player.PosRot.Y, _config.MaxLeadDistance));
        var recovering = living.FirstOrDefault(a => a.InstanceID == _recoverMember);
        if (_recoverMember != 0 && World.Actors.Find(_recoverMember) is { IsDead: true })
        {
            Service.Log($"[AutoDD] locked recovery member died: member={_recoverMember:X}, episode={_recoveryEpisode}; reassessing living party, attempt budget retained.");
            _partyRegroup.TargetNoLongerLiving();
            _recoverMember = _recoverContentID = 0;
            _recoveryProbe = null;
            _followerProgress.Reset();
        }
        if (recovering == null && _recoverContentID != 0)
        {
            var replacement = World.Party.Members.Take(PartyState.MaxPartySize).FirstOrDefault(m => m.ContentId == _recoverContentID);
            recovering = living.FirstOrDefault(a => a.InstanceID == replacement.InstanceId);
            if (recovering != null)
            {
                _recoverMember = recovering.InstanceID;
                _recoveryAnchor = recovering.Position;
                _recoveryApproach = (_recoveryAnchor - player.Position).Normalized();
                _recoveryProbe = null;
                _probeAttempt = 0;
                _leaderApproachGoal = null;
                _followerProgress.Reset();
            }
        }
        var manual = DeepDungeonPartyPolicy.RecentManualMovement(hints.DeepDungeonCoordinationManualAt, World.CurrentTime);
        var suspended = manual || player.InCombat || living.Any(a => a.InCombat) || player.CastInfo != null || !Utils.IsScreenReady()
            || hints.ForcedMovement != null || player.PendingKnockbacks.Count > 0 || World.Client.Flying;
        var now = Environment.TickCount64;
        var recoveryProgress = recovering != null && _followerProgress.Observe(now, recovering.Position, recovering.PosRot.Y,
            available && !suspended && recovering.PendingKnockbacks.Count == 0
                && (_recoveryProbe != null || _verifyingFollow || _partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.FailedHold),
            _verifyingFollow || _partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.FailedHold ? null : _recoveryProbe);
        var approachGoal = _recoveryProbe ?? recovering?.Position;
        var approachDistance = approachGoal is { } goal ? (goal - player.Position).Length() : float.MaxValue;
        var leaderApproachProgress = !suspended && approachGoal != null && approachGoal == _leaderApproachGoal
            && approachDistance + 1 < _leaderApproachDistance;
        if (approachGoal != _leaderApproachGoal || leaderApproachProgress)
        {
            _leaderApproachGoal = approachGoal;
            _leaderApproachDistance = approachDistance;
        }
        _regroupObservation = (available, withinResume, suspended, recoveryProgress, leaderApproachProgress);
        _regroupObservationAt = now;
        var state = _partyRegroup.Update(now, available, beyondMaximum, withinResume, suspended, recoveryProgress, leaderApproachProgress, beyondVerification);
        if (manual)
        {
            _partyHolding = false;
            _verifyingFollow = false;
            return Hold("leader yielding to manual movement");
        }
        if (state == DeepDungeonPartyPolicy.RegroupState.Clearing)
        {
            if (_passageAssembly.Passage == 0)
            {
                _partyHolding = false;
            }
            ResetRecovery();
            if (_passageAssembly.Passage == 0)
                LogPartyStatus("leader ready; party within leash");
            AllowBossInitiation();
            return false;
        }
        if (state == DeepDungeonPartyPolicy.RegroupState.Verifying)
        {
            // The policy retains qualifying movement proof while party geometry stabilizes.
            // A completed probe is no longer a valid destination after objectives resume.
            if (!_verifyingFollow)
            {
                if (_recoveryProbe != null)
                    Service.Log($"[AutoDD] recovery probe retired: episode={_recoveryEpisode}, member={_recoverMember:X}, probe={_recoveryProbe}; follower response observed, verification owns the remaining episode.");
                _recoveryProbe = null;
                _probeAttempt = 0;
                _leaderApproachGoal = null;
            }
            _verifyingFollow = true;
            if (DeepDungeonPartyPolicy.HoldVerificationTravel(withinResume, suspended))
                return Hold($"leader verifying follower response for {_recoverMember:X}; waiting for party convergence or combat completion; no new recovery candidate");
            _partyHolding = false;
            LogPartyStatus($"leader verifying resumed following for {_recoverMember:X}; episode {_recoveryEpisode}, budget retained");
            return false;
        }
        _verifyingFollow = false;
        if (state == DeepDungeonPartyPolicy.RegroupState.Returning && furthest != null)
        {
            _partyHolding = false;
            if (_recoverMember == 0)
            {
                recovering = furthest;
                _recoverMember = recovering.InstanceID;
                _recoverContentID = World.Party.Members.Take(PartyState.MaxPartySize).FirstOrDefault(m => m.InstanceId == _recoverMember).ContentId;
                _recoveryAnchor = recovering.Position;
                _recoveryApproach = (_recoveryAnchor - player.Position).Normalized();
                _followerProgress.Reset();
                ++_recoveryEpisode;
            }
            if (recovering == null)
                return Hold("leader waiting for locked recovery member");
            if (Math.Abs(recovering.PosRot.Y - player.PosRot.Y) > 3)
                return Hold("leader waiting for compatible recovery-member height; no safe repositioning assumed");
            if (_probeAttempt != _partyRegroup.Attempts)
            {
                _probeAttempt = _partyRegroup.Attempts;
                _recoveryProbe = null;
                _leaderApproachGoal = null;
            }
            var createdProbe = false;
            if (_recoveryProbe == null && (recovering.Position - player.Position).Length() <= _config.RegroupResumeDistance)
            {
                _recoveryAnchor = recovering.Position;
                var currentApproach = recovering.Position - player.Position;
                if (currentApproach.LengthSq() > 0.25f)
                    _recoveryApproach = currentApproach.Normalized();
                _recoveryProbe = DeepDungeonPartyPolicy.RecoveryProbeFromCurrent(recovering.Position, player.Position,
                    _recoveryApproach, _config.FollowDistance, _config.MaxLeadDistance, _probeAttempt);
                createdProbe = _recoveryProbe != null;
            }
            _partyNavigationMode = $"recovery:{_recoveryEpisode}:{_recoverMember:X}:{_probeAttempt}:{(_recoveryProbe != null ? "probe" : "return")}";
            var key = PartyNavigationKey(_partyNavigationMode);
            if (_recoveryProbe is { } probe)
            {
                if (living.Any(a => a.InstanceID != _recoverMember && (a.Position - probe).Length() > _config.MaxLeadDistance))
                {
                    _partyRegroup.RejectAttempt("probe would separate another living party member");
                    return Hold("recovery probe rejected; would separate another party member");
                }
                if (hints.DeepDungeonCoordinationResultKey == key && !hints.DeepDungeonCoordinationResultSafe)
                {
                    _partyRegroup.RejectAttempt("no safe solver path to probe region");
                    Service.Log($"[AutoDD] recovery probe rejected: episode={_recoveryEpisode}, member={_recoverMember:X}, attempt={_probeAttempt}/3, endpoint={probe}, reason=no safe solver path to probe region.");
                    return Hold("recovery probe rejected; no safe route to endpoint");
                }
                hints.DeepDungeonCoordinationGoal = probe;
                hints.DeepDungeonCoordinationGoalRadius = DeepDungeonPartyPolicy.RecoveryProbeRadius;
                if (createdProbe)
                    hints.DeepDungeonDiagnosticRequest = $"party-recovery episode={_recoveryEpisode},member={_recoverMember:X},attempt={_probeAttempt}/3,probe={probe},followerDisplacement={_followerProgress.Displacement:F2},configuredFollow={_config.FollowDistance:F2},requiredFollowerDisplacement={DeepDungeonPartyPolicy.RecoveryFollowerDisplacement:F2},probeRadius={DeepDungeonPartyPolicy.RecoveryProbeRadius:F2}";
                hints.AddGoalZone(AIHints.GoalSingleTarget(probe, DeepDungeonPartyPolicy.RecoveryProbeRadius, 2), "deepdungeon-party-recovery-probe");
                var phase = player.Position.InCircle(probe, DeepDungeonPartyPolicy.RecoveryProbeRadius + 0.1f) ? "observing follower at probe" : "repositioning";
                if (player.Position.InCircle(probe, DeepDungeonPartyPolicy.RecoveryProbeRadius + 0.1f))
                    hints.DeepDungeonIntentionalHoldReason = $"observing follower at recovery probe; episode={_recoveryEpisode},member={_recoverMember:X},attempt={_probeAttempt}/3; waiting for qualifying follower response";
                LogPartyStatus($"leader {phase} for {_recoverMember:X}; episode {_recoveryEpisode}, candidate {_probeAttempt}/3, probe={probe}");
            }
            else
            {
                if ((recovering.Position - player.Position).Length() <= _config.RegroupResumeDistance)
                {
                    _partyRegroup.RejectAttempt("configured distances leave no usable probe offset");
                    return Hold("recovery probe rejected; configured distances leave no safe offset");
                }
                hints.AddGoalZone(DeepDungeonPolicy.PassageProgressGoal(recovering.Position, player.Position, _config.FollowDistance, 100, 1, 1), "deepdungeon-party-return-progress");
                hints.AddGoalZone(AIHints.GoalSingleTarget(recovering.Position, _config.FollowDistance, 2), "deepdungeon-party-return");
                LogPartyStatus($"leader returning to {_recoverMember:X}; episode {_recoveryEpisode}, candidate {_partyRegroup.Attempts}/3");
            }
            OwnObjectives(null);
            return true;
        }
        return Hold(state == DeepDungeonPartyPolicy.RegroupState.FailedHold
            ? "regroup failed; holding until party recovers or automation is reset"
            : state == DeepDungeonPartyPolicy.RegroupState.Verifying ? $"leader verifying follower movement for {_recoverMember:X}"
            : state == DeepDungeonPartyPolicy.RegroupState.Blocked ? "leader waiting for unavailable party member"
            : "leader waiting for party catch-up or combat completion");

        void AllowBossInitiation()
        {
            hints.DeepDungeonBossPullAllowed = DeepDungeonPartyPolicy.CanInitiateBoss(_config.Enable,
                _config.Role == AutoDDConfig.PartyRole.Leader, leader == player, Palace.IsBossFloor,
                player is { IsDead: false, CastInfo: null } && Utils.IsScreenReady()
                    && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                    && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51],
                World.DeepDungeon.BossCompletionObserved, false);
        }

        bool Hold(string reason)
        {
            if (_partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.FailedHold)
            {
                if (_recoveryFailureDetail == null)
                {
                    var members = World.Party.Members.Take(PartyState.MaxPartySize)
                        .Where(m => m.IsValid() && m.InstanceId != player.InstanceID)
                        .Select(m => World.Actors.Find(m.InstanceId) is { } actor
                            ? $"{m.InstanceId:X}/dead:{actor.IsDead}/distance:{(actor.Position - player.Position).Length():F2}/height:{Math.Abs(actor.PosRot.Y - player.PosRot.Y):F2}"
                            : $"{m.InstanceId:X}/actor:missing");
                    _recoveryFailureDetail = $"reason={_partyRegroup.FailureReason ?? "unknown"},lastAttemptOutcome={_partyRegroup.LastAttemptOutcome ?? "none"},episode={_recoveryEpisode},member={_recoverMember:X},contentID={_recoverContentID},{RecoveryCandidateCounts},lastAction={_partyStatus ?? "none"},{RecoveryHoldConditions(player)},members=[{string.Join(",", members)}]";
                    Service.Log($"[AutoDD] recovery failure latched: {_recoveryFailureDetail}; no automatic retry; use Retry party coordination after checking the party.");
                }
                var cause = _partyRegroup.FailureReason == "attempt budget exhausted" ? "recovery candidate budget exhausted" : _partyRegroup.FailureReason ?? "unknown cause";
                reason = $"regroup failed; {cause}; member={_recoverMember:X}, {RecoveryCandidateCounts}; candidates are not return trips; holding until observed recovery or Retry party coordination";
            }
            hints.DeepDungeonIntentionalHoldReason = _partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.FailedHold
                ? $"{reason}; lastAttemptOutcome={_partyRegroup.LastAttemptOutcome ?? "none"}; {RecoveryHoldConditions(player)}"
                : reason;
            _partyNavigationMode = $"hold:{_partyRegroup.State}:{_recoveryEpisode}";
            if (!_partyHolding)
            {
                _partyHolding = true;
                _partyHoldAnchor = player.Position;
            }
            hints.AddGoalZone(AIHints.GoalSingleTarget(_partyHoldAnchor, 1, 1), "deepdungeon-party-hold");
            var anchor = _partyHoldAnchor;
            hints.DeepDungeonPartyCombatArea = p => p.InCircle(anchor, 1);
            LogPartyStatus(reason);
            OwnObjectives(null);
            return true;
        }

        void OwnObjectives(Actor? assistLeader)
        {
            MostInterestingObject = null;
            InterestingObjects.Clear();
            ClearDesiredRoom("party coordination owns navigation");
            ResetObjectiveProgress("party coordination owns navigation");
            foreach (var enemy in hints.PotentialTargets)
            {
                var engaged = DeepDungeonPartyPolicy.CanAssist(enemy.Actor.AggroPlayer, enemy.Actor.InCombat,
                    World.Party.TryFindSlot(enemy.Actor.TargetID, out var targetSlot) && targetSlot < PartyState.MaxPartySize);
                enemy.ShouldBeTargeted = engaged && assistLeader?.TargetID == enemy.Actor.InstanceID;
                if (!engaged)
                    enemy.ForcePriority(AIHints.Enemy.PriorityForbidden);
            }
            hints.DeepDungeonPartyGoals = [.. hints.GoalZones];
            var trapObjective = assistLeader == null ? null
                : _partyNavigationMode.StartsWith("stack:", StringComparison.Ordinal)
                    ? $"{_partyContext}/passage:{_passageAssembly.Passage:X}"
                    : $"{_partyContext}/follow:{assistLeader.InstanceID:X}";
            hints.SetDeepDungeonPotentialTrapObjective(trapObjective);
            hints.DeepDungeonPartyNavigationKey = PartyNavigationKey(_partyNavigationMode);
        }
    }

    private void ResetRecovery()
    {
        _recoverMember = _recoverContentID = 0;
        _recoveryProbe = null;
        _leaderApproachGoal = null;
        _leaderApproachDistance = float.MaxValue;
        _probeAttempt = 0;
        _verifyingFollow = false;
        _passageFollowUntil = 0;
        _recoveryFailureNotified = false;
        _recoveryFailureDetail = null;
        _regroupObservation = default;
        _regroupObservationAt = 0;
        _followerProgress.Reset();
    }

    private string RecoveryCandidateCounts => $"candidates={_partyRegroup.Attempts}/3,rejectedCandidates={_partyRegroup.RejectedCandidates},candidatesWithLeaderApproach={_partyRegroup.CandidatesWithLeaderApproach}";

    private string RecoveryHoldConditions(Actor player)
    {
        var member = World.Actors.Find(_recoverMember);
        var position = member != null ? member.Position.ToString() : "missing";
        var distance = member != null ? (member.Position - player.Position).Length().ToString("F2") : "unknown";
        var height = member != null ? Math.Abs(member.PosRot.Y - player.PosRot.Y).ToString("F2") : "unknown";
        var age = _regroupObservationAt > 0 ? (Environment.TickCount64 - _regroupObservationAt).ToString() : "unknown";
        return $"activeAtStopMs={_partyRegroup.ActiveMilliseconds}/90000,wallAtStopMs={_partyRegroup.WallMilliseconds}/180000,attemptAtStopMs={_partyRegroup.AttemptMilliseconds}/30000,noProgressAtStopMs={_partyRegroup.NoProgressMilliseconds}/15000,memberPosition={position},memberDistance={distance},memberHeight={height},followerDisplacement={_followerProgress.Displacement:F2},attemptFollowerMovementObserved={_partyRegroup.FollowerMovementObserved},lastObservationAgeMs={age},allAvailable={_regroupObservation.Available},allWithinResume={_regroupObservation.WithinResume},suspended={_regroupObservation.Suspended},followerProgressThisFrame={_regroupObservation.FollowerProgress},leaderApproachProgressThisFrame={_regroupObservation.LeaderApproachProgress},releaseVerificationMs={_partyRegroup.TogetherMilliseconds}/{_partyRegroup.ReleaseVerificationRequiredMilliseconds}; release requires valid living-party proximity/height plus observed follower progress (or confirmed target death), not leader proximity alone";
    }

    private string PartyNavigationKey(string mode)
    {
        var intent = $"{_partyContext}/{mode}";
        if (_lastPartyIntent != intent)
        {
            _lastPartyIntent = intent;
            ++_partyNavigationGeneration;
        }
        return $"{intent}/generation:{_partyNavigationGeneration}";
    }

    private string AutoNavigationKey(string owner)
        => PartyNavigationKey($"{owner}/verification:{(_partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.Verifying ? _recoveryEpisode : 0)}");

    private static void AddPassageApproachGoals(Actor player, Actor passage, AIHints hints, float weight)
    {
        var goals = DeepDungeonPolicy.PassageApproachGoals(passage.Position, player.Position, weight);
        hints.AddGoalZone(goals.Arrival, $"deepdungeon-passage:{passage.InstanceID}:interact-range");
        hints.AddGoalZone(goals.Approach, $"deepdungeon-passage:{passage.InstanceID}:approach");
        hints.AddGoalZone(goals.Progress, $"deepdungeon-passage:{passage.InstanceID}:progress");
    }

    private static void AddPassageGoals(Actor player, Actor passage, AIHints hints, float weight, int replan = 0)
    {
        hints.AddGoalZone(DeepDungeonPartyPolicy.PassageAssemblyGoal(passage.Position, weight, replan), $"deepdungeon-passage:{passage.InstanceID}:assembly");
        hints.AddGoalZone(DeepDungeonPolicy.PassageProgressGoal(passage.Position, player.Position,
            DeepDungeonPartyPolicy.PassageGoalRadius, 100, 0.25f, weight * 0.25f), $"deepdungeon-passage:{passage.InstanceID}:progress");
    }

    private bool ObservePassageAssembly(Actor player, Actor passage, AIHints hints, bool suspended)
    {
        var distance = (passage.Position - player.Position).Length();
        if (!_passageAssembly.ObserveProgress(Environment.TickCount64, distance, suspended))
            return false;
        var reason = $"passage-assembly actor={passage.InstanceID:X},role={_config.Role},distance={distance:F3},height={Math.Abs(player.PosRot.Y - passage.PosRot.Y):F2},innerRadius={DeepDungeonPartyPolicy.PassageInnerRadius},goalRadius={DeepDungeonPartyPolicy.PassageGoalRadius},replans={_passageAssembly.Replans}/3,blocked={_passageAssembly.Blocked}; local goal arrival does not prove server readiness";
        hints.DeepDungeonDiagnosticRequest = reason;
        Service.Log($"[AutoDD] {reason}.");
        return true;
    }
}
