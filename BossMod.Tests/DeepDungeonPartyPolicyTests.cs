using Xunit;

namespace BossMod.Tests;

public class DeepDungeonPartyPolicyTests
{
    [Theory]
    [InlineData(true, true, true, true, true, false, false, true)]
    [InlineData(false, true, true, true, true, false, false, false)]
    [InlineData(true, false, true, true, true, false, false, false)]
    [InlineData(true, true, false, true, true, false, false, false)]
    [InlineData(true, true, true, false, true, false, false, false)]
    [InlineData(true, true, true, true, false, false, false, false)]
    [InlineData(true, true, true, true, true, true, false, false)]
    [InlineData(true, true, true, true, true, false, true, false)]
    public void BossInitiationRequiresReadyVerifiedLeaderWithoutRegroupOrCompletion(bool enabled, bool leaderRole, bool verified,
        bool bossFloor, bool ready, bool complete, bool regroup, bool expected)
        => Assert.Equal(expected, DeepDungeonPartyPolicy.CanInitiateBoss(enabled, leaderRole, verified, bossFloor, ready, complete, regroup));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, false)]
    public void BossPrioritizationPreservesLegacySoloButRequiresExplicitRolePermission(bool solo, bool roleControlled, bool leaderAllowed, bool expected)
        => Assert.Equal(expected, DeepDungeonPartyPolicy.CanPrioritizeBoss(solo, roleControlled, leaderAllowed));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    public void AssistRequiresEngagementNotJustLeaderTargetSelection(bool enmity, bool combat, bool targetsParty, bool expected)
        => Assert.Equal(expected, DeepDungeonPartyPolicy.CanAssist(enmity, combat, targetsParty));

    [Theory]
    [InlineData(true, true, 2, 0, true)]
    [InlineData(false, true, 1, 0, false)]
    [InlineData(true, false, 1, 0, false)]
    [InlineData(true, true, 3, 0, true)]
    [InlineData(true, true, 6, 0, true)]
    [InlineData(true, true, 7, 0, false)]
    [InlineData(true, true, float.NaN, 0, false)]
    [InlineData(true, true, 1, 10, false)]
    public void PassageStackRequiresOpenPassageAndLeaderNearby(bool active, bool present, float distance, float height, bool expected)
        => Assert.Equal(expected, DeepDungeonPartyPolicy.ShouldStack(active, present, distance, height));

    [Theory]
    [InlineData(3, 8, 15, true)]
    [InlineData(0, 8, 15, false)]
    [InlineData(8, 8, 15, false)]
    [InlineData(3, 15, 15, false)]
    [InlineData(float.NaN, 8, 15, false)]
    public void DistancesRequireFiniteOrderedThresholds(float follow, float resume, float max, bool expected)
        => Assert.Equal(expected, DeepDungeonPartyPolicy.ValidDistances(follow, resume, max));

    [Fact]
    public void LeaderWaitsFifteenSecondsThenRequiresFollowerMovementAndVerifiedResume()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(1000, true, false, true, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(1250, true, true, false, false));
        for (long now = 1500; now < 16250; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(now, true, true, false, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(16250, true, true, false, false));
        for (long now = 16500; now <= 19500; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(now, true, false, true, false));
        Assert.Equal(1, state.Attempts);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(19750, true, false, true, false, true));
        for (long now = 20000; now < 24750; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(now, true, false, true, false, true));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(24750, true, false, true, false, true));
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public void CombatAndUnavailableMembersPreventReturnOrSuccessfulRegroup()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        for (long now = 1000; now <= 20000; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(now, true, true, false, true));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(20250, true, true, false, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Blocked, state.Update(20500, false, false, true, false));
        Assert.NotEqual(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(20750, true, false, true, false));
    }

    [Fact]
    public void LoadingGapDoesNotAdvanceCatchupTimer()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        state.Update(1000, true, true, false, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(100000, true, true, false, false));
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public void CombatOrUnavailableSnapshotDoesNotResetSeparationEpisode()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        for (long now = 1000; now <= 17000; now += 250)
            state.Update(now, true, true, false, true);
        state.Update(17250, false, false, false, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(17500, true, true, false, false));
    }

    [Fact]
    public void EvenContinuousRecoveryProgressCannotExtendAttemptBeyondThirtySeconds()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        for (long now = 1000; now <= 46250; now += 250)
            state.Update(now, true, true, false, false, true);
        Assert.Equal(2, state.Attempts);
    }

    [Fact]
    public void RecoveryAttemptsAreBoundedAndFailureDoesNotRestart()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        for (long now = 1000; now <= 150000; now += 250)
            state.Update(now, true, true, false, false);
        Assert.Equal(3, state.Attempts);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.Update(150250, false, false, false, false));
        for (long now = 150500; now <= 153500; now += 250)
            state.Update(now, true, false, true, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        for (long now = 153750; now <= 158750; now += 250)
            state.Update(now, true, false, true, false, true);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
    }

    [Fact]
    public void OrdinaryCatchupBeforeReturningStillFinishesBySustainedProximity()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        state.Update(1000, true, true, false, false);
        for (long now = 1250; now <= 4000; now += 250)
            state.Update(now, true, false, true, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public void CombatDoesNotCompleteProximityRecoveryOrRefreshAttempt()
    {
        var state = StartedRecovery();
        for (long now = 16500; now <= 20500; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(now, true, false, true, true, true));
        Assert.Equal(1, state.Attempts);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(20750, true, false, true, false));
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public void ProvisionalResumeRetainsQualifiedProofWhenFollowerCorrectlyStops()
    {
        var state = StartedRecovery();
        state.Update(16500, true, false, true, false, true);
        for (long now = 16750; now < 21500; now += 250)
        {
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(now, true, false, true, false));
            Assert.True(state.FollowerMovementObserved);
        }
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(21500, true, false, true, false));
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public void VerificationRadiusJitterPausesCreditWithoutRevivingProbeOrErasingProof()
    {
        var state = StartedRecovery();
        state.Update(16500, true, false, true, false, true);
        for (long now = 16750; now <= 25000; now += 250)
        {
            var withinResume = now % 1000 != 0;
            state.Update(now, true, false, withinResume, false, beyondVerification: false);
            if (state.State == DeepDungeonPartyPolicy.RegroupState.Clearing)
                break;
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.State);
            Assert.Equal(1, state.Attempts);
            Assert.True(state.FollowerMovementObserved);
        }
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
    }

    [Fact]
    public void OnlySustainedVerificationExitStartsFreshBudgetedManeuver()
    {
        var state = StartedRecovery();
        state.Update(16500, true, false, true, false, true);
        for (long now = 16750; now < 17500; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying,
                state.Update(now, true, true, false, false, beyondVerification: true));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning,
            state.Update(17500, true, true, false, false, beyondVerification: true));
        Assert.Equal(2, state.Attempts);
        Assert.True(state.FollowerMovementObserved);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(17750, true, false, true, false));
        state.RejectAttempt("stale completed probe");
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.State);
        Assert.Equal(2, state.Attempts);
        Assert.Equal("follower response observed; verifying party convergence", state.LastAttemptOutcome);
    }

    [Fact]
    public void QualifiedFollowerProofSurvivesRejectedProbeAndPreventsAnotherProbeWhenPartyConverges()
    {
        var state = StartedRecovery();
        state.Update(16500, true, true, false, false, true);
        state.RejectAttempt("would separate another party member");
        Assert.True(state.FollowerMovementObserved);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(16750, true, false, true, false));
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public void LoggedRejectedCandidatesThenSuccessfulProbeDoesNotFailForTrailingPartyInsideMaximumLeash()
    {
        var state = StartedRecovery();
        state.Update(16500, true, true, false, false, leaderApproachProgress: true);
        state.RejectAttempt("probe would separate another living party member");
        state.Update(16750, true, true, false, false);
        Assert.Equal(2, state.Attempts);
        state.RejectAttempt("no safe solver path to probe region");
        state.Update(17000, true, true, false, false);
        Assert.Equal(3, state.Attempts);
        state.Update(17250, true, false, true, false, leaderApproachProgress: true);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(17500, true, false, true, false, true));
        Assert.Equal(2, state.RejectedCandidates);
        Assert.Equal(2, state.CandidatesWithLeaderApproach); // one return + one reposition, not three return trips
        Assert.Equal("follower response observed; verifying party convergence", state.LastAttemptOutcome);
        var outside = DeepDungeonPartyPolicy.BeyondVerificationLeash(11.64f, 0.54f, 15)
            || DeepDungeonPartyPolicy.BeyondVerificationLeash(11.54f, 0.03f, 15);
        Assert.False(outside);
        Assert.True(DeepDungeonPartyPolicy.HoldVerificationTravel(false, false));
        for (long now = 17750; now <= 25000; now += 250)
        {
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying,
                state.Update(now, true, false, false, false, beyondVerification: outside));
            Assert.Equal(3, state.Attempts);
            Assert.True(state.FollowerMovementObserved);
            Assert.Null(state.FailureReason);
        }
        state.RejectAttempt("stale completed probe");
        Assert.Equal(2, state.RejectedCandidates);
        for (long now = 25250; now <= 30000; now += 250)
            state.Update(now, true, false, true, false, beyondVerification: false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
        Assert.Equal(0, state.RejectedCandidates);
        Assert.Equal(0, state.CandidatesWithLeaderApproach);
    }

    [Fact]
    public void VerificationTravelWaitsForSoftLagButGenuineMaximumSeparationStillCountsAsRelapse()
    {
        Assert.True(DeepDungeonPartyPolicy.HoldVerificationTravel(false, false));
        Assert.True(DeepDungeonPartyPolicy.HoldVerificationTravel(true, true));
        Assert.False(DeepDungeonPartyPolicy.HoldVerificationTravel(true, false));
        Assert.False(DeepDungeonPartyPolicy.BeyondVerificationLeash(11.64f, 0.54f, 15));
        Assert.True(DeepDungeonPartyPolicy.BeyondVerificationLeash(15.1f, 0, 15));
        Assert.True(DeepDungeonPartyPolicy.BeyondVerificationLeash(5, 3.6f, 15));
    }

    [Fact]
    public void RepeatedRejectionCannotChargeTheSameCandidateTwice()
    {
        var state = StartedRecovery();
        state.RejectAttempt("unsafe candidate");
        state.RejectAttempt("duplicate rejection");
        Assert.Equal(1, state.Attempts);
        Assert.Equal(1, state.RejectedCandidates);
        Assert.Equal("unsafe candidate", state.LastAttemptOutcome);
    }

    [Fact]
    public void CombatAndUnavailableFramesSuspendVerificationWithoutDiscardingItsProof()
    {
        var state = StartedRecovery();
        state.Update(16500, true, false, true, false, true);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Blocked, state.Update(16750, false, false, false, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Waiting, state.Update(17000, true, false, true, true));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Verifying, state.Update(17250, true, false, true, false));
        Assert.True(state.FollowerMovementObserved);
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public void RelapseProbeUsesCurrentFollowerAndLeaderRatherThanOldProbeCoordinates()
    {
        var old = DeepDungeonPartyPolicy.RecoveryProbeFromCurrent(new(395, 0), new(402, 0), default, 3, 15, 1)!.Value;
        var current = DeepDungeonPartyPolicy.RecoveryProbeFromCurrent(new(371, 0), new(364, 0), new(-1, 0), 3, 15, 1)!.Value;
        Assert.Equal(new WPos(388.75f, 0), old);
        Assert.Equal(new WPos(377.25f, 0), current);
        Assert.Equal(6.25f, (current - new WPos(371, 0)).Length());
        Assert.True((old - new WPos(370, 0)).Length() > 15);
        Assert.True((current - new WPos(370, 0)).Length() < 15);
    }

    [Fact]
    public void RejectedProbesConsumeFiniteBudget()
    {
        var state = StartedRecovery();
        for (var attempt = 1; attempt <= 3; ++attempt)
        {
            Assert.Equal(attempt, state.Attempts);
            state.RejectAttempt();
            state.Update(16250 + attempt * 250, true, false, true, false);
        }
        Assert.Equal(3, state.Attempts);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
    }

    [Fact]
    public void WallDeadlineExpiresEvenWhenEveryExecutionFrameIsSuspended()
    {
        var state = StartedRecovery();
        state.Update(200000, true, false, true, true);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public void StationaryFollowerNeverProvesRecovery()
    {
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        for (long now = 1000; now <= 30000; now += 250)
            Assert.False(progress.Observe(now, new(246.3f, -370.5f), 0, true));
        Assert.Equal(0, progress.Displacement);
    }

    [Fact]
    public void LoggedOnePointSevenFiveYardResponseDoesNotFalselyMeetTwoYardProof()
    {
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        progress.Observe(1000, default, 0, true, new WPos(5, 0));
        for (long now = 1250; now <= 20000; now += 250)
            Assert.False(progress.Observe(now, new(1.75f, 0), 0, true, new WPos(5, 0)));
    }

    [Fact]
    public void FailureRetainsQualifiedResponseWhileFollowerCorrectlyStopsWithinFollowRadius()
    {
        var state = StartedRecovery();
        state.Update(16500, true, true, false, false, true);
        state.RejectAttempt();
        state.Update(16750, true, true, false, false);
        state.RejectAttempt();
        state.Update(17000, true, true, false, false);
        state.RejectAttempt();
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        Assert.True(state.FollowerMovementObserved);
        for (long now = 17250; now < 22000; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.Update(now, true, false, true, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(22000, true, false, true, false));
    }

    [Fact]
    public void FailureLatchesNewResponseWithoutDemandingContinuousMotionForFiveSeconds()
    {
        var state = StartedRecovery();
        for (int attempt = 1; attempt <= 3; ++attempt)
        {
            state.RejectAttempt();
            state.Update(16250 + attempt * 250, true, true, false, false);
        }
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        Assert.False(state.FollowerMovementObserved);
        state.Update(17250, true, false, true, false, true);
        for (long now = 17500; now < 22000; now += 250)
            Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.Update(now, true, false, true, false));
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.Update(22000, true, false, true, false));
    }

    [Fact]
    public void FollowerDisplacementRequiresStableWindowAndProgressTowardFixedProbe()
    {
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        var goal = new WPos(5, 0);
        Assert.False(progress.Observe(1000, default, 0, true, goal));
        for (long now = 1250; now < 4000; now += 250)
            Assert.False(progress.Observe(now, new(2.5f, 0), 0, true, goal));
        Assert.True(progress.Observe(4000, new(2.5f, 0), 0, true, goal));
        Assert.False(progress.Observe(4250, new(2.5f, 0), 0, false, goal));
    }

    [Fact]
    public void JitterWrongDirectionAndObservationGapsDoNotProveRecovery()
    {
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        for (long now = 1000; now <= 10000; now += 250)
            Assert.False(progress.Observe(now, new(now % 500 == 0 ? 0.2f : 0, 0), 0, true));
        progress.Reset();
        progress.Observe(1000, default, 0, true, new WPos(5, 0));
        for (long now = 1250; now <= 10000; now += 250)
            Assert.False(progress.Observe(now, new(-2.5f, 0), 0, true, new WPos(5, 0)));
        Assert.False(progress.Observe(20000, new(2.5f, 0), 0, true, new WPos(5, 0)));
    }

    [Fact]
    public void RecoveryProbesIncludePastFollowerAndBothLateralAnglesOutsideFollowRadius()
    {
        var anchor = new WPos(10, 10);
        var approach = new WDir(1, 0);
        var past = DeepDungeonPartyPolicy.RecoveryProbe(anchor, approach, 3, 15, 1)!.Value;
        var left = DeepDungeonPartyPolicy.RecoveryProbe(anchor, approach, 3, 15, 2)!.Value;
        var right = DeepDungeonPartyPolicy.RecoveryProbe(anchor, approach, 3, 15, 3)!.Value;
        Assert.True((past - anchor).Dot(approach) > 3);
        Assert.Equal(0, (left - anchor).Dot(approach));
        Assert.Equal(left - anchor, -(right - anchor));
        Assert.True((left - anchor).Length() > 3);
        Assert.Null(DeepDungeonPartyPolicy.RecoveryProbe(anchor, approach, 13, 15, 1));
        Assert.Null(DeepDungeonPartyPolicy.RecoveryProbe(anchor, default, 3, 15, 1));
    }

    [Fact]
    public void PassageLatchEntersOutsideOldTwoYardEdgeAndSurvivesThresholdJitter()
    {
        var assembly = new DeepDungeonPartyPolicy.PassageAssembly();
        Assert.False(assembly.Update(1000, 42, true, 2.05f, 0, false, false));
        Assert.False(assembly.Update(1250, 42, true, 2.05f, 0, false, false));
        Assert.True(assembly.Update(1500, 42, true, 2.05f, 0, false, false));
        for (long now = 1750; now <= 5000; now += 250)
            Assert.True(assembly.Update(now, 42, true, now % 500 == 0 ? 5.9f : 6.1f, 0, false, false));
        Assert.Equal(42UL, assembly.Passage);
    }

    [Fact]
    public void PassageLatchRequiresSustainedDepartureAndResetsOnFloorEvidence()
    {
        var assembly = new DeepDungeonPartyPolicy.PassageAssembly();
        Assert.True(assembly.Update(1000, 42, true, 0, 0, true, false));
        for (long now = 1250; now < 3000; now += 250)
            Assert.True(assembly.Update(now, 42, true, 11, 0, false, false));
        Assert.False(assembly.Update(3000, 42, true, 11, 0, false, false));
        Assert.True(assembly.Update(3250, 42, true, 0, 0, true, false));
        Assert.False(assembly.Update(3500, 42, false, 0, 0, false, false));
        Assert.Equal(0UL, assembly.Passage);
    }

    [Fact]
    public void PassageAssemblyHasActiveAndWallDeadlinesWithoutClaimingTransition()
    {
        var assembly = new DeepDungeonPartyPolicy.PassageAssembly();
        assembly.Update(1000, 42, true, 0, 0, true, false);
        for (long now = 1250; now <= 61000; now += 250)
            assembly.Update(now, 42, true, 0, 0, true, false);
        Assert.True(assembly.Blocked);
        assembly.Reset();
        assembly.Update(1000, 42, true, 0, 0, true, false);
        Assert.True(assembly.Update(181000, 42, true, 0, 0, true, true));
        Assert.True(assembly.Blocked);
    }

    [Fact]
    public void PassageGoalRewardsSafeInteriorNotTwoYardArrivalPlateau()
    {
        var goal = DeepDungeonPartyPolicy.PassageAssemblyGoal(default, 1);
        Assert.Equal(0, goal(new(2, 0)));
        Assert.Equal(0, goal(new(0.9f, 0)));
        Assert.True(goal(default) > goal(new(0.5f, 0)));
        Assert.True(DeepDungeonPartyPolicy.PassageGoalRadius + 0.1f < DeepDungeonPartyPolicy.PassageInnerRadius);
    }

    [Fact]
    public void PassageReplansAreBoundedAndNeverRefreshAssemblyDeadline()
    {
        var assembly = new DeepDungeonPartyPolicy.PassageAssembly();
        assembly.Update(1000, 42, true, 2.05f, 0, true, false);
        assembly.ObserveProgress(1000, 2.05f, false);
        for (long now = 1250; now <= 41000; now += 250)
        {
            assembly.Update(now, 42, true, 2.05f, 0, true, false);
            assembly.ObserveProgress(now, 2.05f, false);
        }
        Assert.Equal(3, assembly.Replans);
        Assert.True(assembly.Blocked);
    }

    [Fact]
    public void LeaderApproachCanExtendManeuverButCannotProveFollowerRecovery()
    {
        var state = StartedRecovery();
        for (long now = 16500; now <= 47000; now += 250)
        {
            state.Update(now, true, false, true, false, false, true);
            Assert.NotEqual(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
        }
        Assert.Equal(2, state.Attempts);
    }

    [Fact]
    public void DuplicateObservationTickDoesNotEraseProgressButChangedProbeDoes()
    {
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        progress.Observe(1000, default, 0, true, new WPos(5, 0));
        for (long now = 1250; now <= 4000; now += 250)
        {
            progress.Observe(now, new(2.5f, 0), 0, true, new WPos(5, 0));
            progress.Observe(now, new(2.5f, 0), 0, true, new WPos(5, 0));
        }
        Assert.True(progress.Observe(4000, new(2.5f, 0), 0, true, new WPos(5, 0)));
        Assert.False(progress.Observe(4250, new(2.5f, 0), 0, true, new WPos(0, 5)));
    }

    [Fact]
    public void ManualMovementEvidenceMustBeRecentAndNotFromFutureReplayTime()
    {
        var now = new DateTime(2026, 11, 11);
        Assert.False(DeepDungeonPartyPolicy.RecentManualMovement(default, now));
        Assert.False(DeepDungeonPartyPolicy.RecentManualMovement(now.AddSeconds(1), now));
        Assert.False(DeepDungeonPartyPolicy.RecentManualMovement(now.AddSeconds(-1), now));
        Assert.True(DeepDungeonPartyPolicy.RecentManualMovement(now.AddMilliseconds(-100), now));
    }

    [Fact]
    public void ConfirmedTargetDeathReassessesLivingPartyWithoutPretendingFollowerMoved()
    {
        var state = StartedRecovery();
        state.TargetNoLongerLiving();
        for (long now = 16500; now <= 19250; now += 250)
            state.Update(now, true, false, true, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
    }

    [Fact]
    public void ConfirmedTargetDeathDoesNotRefreshBudgetForAnotherLivingStraggler()
    {
        var state = StartedRecovery();
        state.TargetNoLongerLiving();
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.Update(16500, true, true, false, false));
        Assert.Equal(1, state.Attempts);
    }

    [Fact]
    public void FailureDiagnosticsPreserveProbeRejectionAndOriginalStopCause()
    {
        var state = StartedRecovery();
        for (var attempt = 1; attempt <= 3; ++attempt)
        {
            state.RejectAttempt("no safe solver path to probe region");
            state.Update(16250 + attempt * 250, true, true, false, false);
        }
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.FailedHold, state.State);
        Assert.Equal("attempt budget exhausted", state.FailureReason);
        Assert.Equal("no safe solver path to probe region", state.LastAttemptOutcome);
        var stoppedWall = state.WallMilliseconds;
        var stoppedActive = state.ActiveMilliseconds;
        state.Update(300000, false, true, false, false);
        Assert.Equal("attempt budget exhausted", state.FailureReason);
        Assert.Equal(stoppedWall, state.WallMilliseconds);
        Assert.Equal(stoppedActive, state.ActiveMilliseconds);
        Assert.Equal(3, state.Attempts);
        state.Reset();
        Assert.Null(state.FailureReason);
        Assert.Null(state.LastAttemptOutcome);
    }

    [Fact]
    public void FailureDiagnosticsDistinguishWallDeadlineFromActiveBudget()
    {
        var wall = StartedRecovery();
        wall.Update(200000, true, false, true, true);
        Assert.Equal("180s wall deadline expired", wall.FailureReason);
        Assert.True(wall.WallMilliseconds >= 180000);

        var active = StartedRecovery();
        for (long now = 16500; now <= 110000; now += 250)
            active.Update(now, true, true, false, false, true);
        Assert.Equal("90s active recovery budget exhausted", active.FailureReason);
        Assert.True(active.ActiveMilliseconds >= 90000);
    }

    [Fact]
    public void AttemptTimeoutDiagnosticsDoNotRefreshAttemptBudget()
    {
        var state = StartedRecovery();
        for (long now = 16500; now <= 31250; now += 250)
            state.Update(now, true, true, false, false);
        Assert.Equal("15s without leader approach or follower progress", state.LastAttemptOutcome);
        Assert.Equal(2, state.Attempts);
        Assert.Null(state.FailureReason);
        Assert.False(state.FollowerMovementObserved);
    }

    private static DeepDungeonPartyPolicy.Regroup StartedRecovery()
    {
        var state = new DeepDungeonPartyPolicy.Regroup();
        state.Update(1000, true, true, false, false);
        for (long now = 1250; now <= 16250; now += 250)
            state.Update(now, true, true, false, false);
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Returning, state.State);
        Assert.Equal(1, state.Attempts);
        return state;
    }
}
