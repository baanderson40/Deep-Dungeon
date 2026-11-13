using Xunit;
using Owner = BossMod.DeepDungeonRoomRejoinPolicy.NavigationOwner;

namespace BossMod.Tests;

// Exercise the production arbitration -> failure-feedback gate -> rejoin state-machine contract.
// Solver/goal integration is covered alongside this in DeepDungeonCoordinationNavigationTests.
public class DeepDungeonNavigationOwnershipTests
{
    private const string Context = "POTD/101/598/8/ClosedPassage";
    private const string Route = Context + "/segment:12>7";
    private static readonly WPos Source = new(303.3f, -292.9f);
    private static readonly WDir Size = new(29.3f, 28.9f);
    private static readonly WPos Next = new(312.3f, -357.6f);
    private static readonly WPos Outside = new(335.4f, -257.1f);

    [Theory]
    [InlineData(true, true, true, true, "Manual")]
    [InlineData(false, true, true, false, "Actor")]
    [InlineData(false, false, true, true, "Actor")]
    [InlineData(false, false, true, false, "RoomRoute")]
    [InlineData(false, true, false, false, "Suspended")]
    public void ArbitrationPrioritizesManualThenReadinessAndActorIntent(bool manual, bool actor, bool canNavigate, bool interacting, string expected)
        => Assert.Equal(expected, DeepDungeonRoomRejoinPolicy.SelectOwner(manual, actor, canNavigate, interacting).ToString());

    [Fact]
    public void LoggedChestDetourDoesNotConsumeRecoveryAttemptsEvenWithStaleRoomFailureSignal()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
        for (long now = 1000; now <= 90000; now += 250)
        {
            var player = now < 3000 ? new WPos(302.5f - (now - 1000) * 0.005f, -303.5f) : new WPos(290.8f, -306.2f);
            Assert.False(Tick(policy, now, owner, player, true));
            Assert.Equal(0, policy.Attempts);
        }
        // Chest disappears after loot. Reachable normal routing must resume without a recovery maneuver.
        for (long now = 90250; now <= 110000; now += 250)
        {
            Assert.False(Tick(policy, now, Owner.RoomRoute, new(290.8f, -306.2f), false));
            Assert.Equal(0, policy.Attempts);
        }
    }

    [Fact]
    public void MovingRoomOwnedDetourIsNotMistakenForStallEvenWhenFarOutsideCorridor()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        for (long now = 1000; now <= 60000; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside + new WDir((now - 1000) / 1000f, 0), true));
        Assert.Equal(0, policy.Attempts);
    }

    [Fact]
    public void GenuineRoomOwnedFailureRequiresTenSecondsOfFreshStationaryEvidence()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        for (long now = 1000; now < 11000; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true));
        Assert.True(Tick(policy, 11000, Owner.RoomRoute, Outside, true));
        Assert.Equal(1, policy.Attempts);
    }

    [Theory]
    [InlineData("wrong-owner", 1000, 1000, true)]
    [InlineData(Route, 2000, 1000, true)]
    [InlineData(Route, 1000, 1100, true)]
    [InlineData(Route, 1000, 1000, false)]
    public void StaleMismatchedFutureAndHealthyFeedbackCannotStartRecovery(string key, long now, long at, bool failed)
        => Assert.False(DeepDungeonRoomRejoinPolicy.FreshFailedRoute(Route, key, now, at, failed));

    [Fact]
    public void ActorPreemptionRetiresManeuverAndPausesBudgetsWithoutLosingAttemptHistory()
    {
        var policy = Started();
        Assert.False(Tick(policy, 11250, Owner.Actor, Outside, true));
        var generation = policy.Generation;
        for (long now = 11500; now <= 250000; now += 250)
        {
            Assert.False(Tick(policy, now, Owner.Actor, Outside, true));
            Assert.Equal(1, policy.Attempts);
            Assert.Equal(generation, policy.Generation);
        }
        Assert.False(Tick(policy, 250250, Owner.RoomRoute, Outside, false));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, policy.State);
        // New stationary failure after resuming room ownership consumes attempt two, not a stale probe.
        for (long now = 250500; now < 260500; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true));
        Assert.True(Tick(policy, 260500, Owner.RoomRoute, Outside, true));
        Assert.Equal(2, policy.Attempts);
    }

    [Fact]
    public void CastingCombatInteractionOrLoadingEligibilityCannotArmOrChargeRecovery()
    {
        var policy = Started();
        for (long now = 11250; now <= 250000; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true, eligible: false));
        Assert.Equal(1, policy.Attempts);
        Assert.NotEqual(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
    }

    [Fact]
    public void ObsoleteGoalIsCancelledBeforeAFormerFailureHoldCanBlockPassageSelection()
    {
        var policy = Failed();
        Assert.False(Tick(policy, 70250, Owner.Actor, Outside, true, context: "POTD/101/598/8/OpenPassage"));
        Assert.Equal(0, policy.Attempts);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, policy.State);
    }

    [Fact]
    public void CompletedObjectiveAndManualTakeoverClearStaleRecovery()
    {
        var completed = Failed();
        Assert.False(Tick(completed, 70250, Owner.Actor, Outside, true, context: null));
        Assert.Null(completed.Context);
        Assert.Equal(0, completed.Attempts);
        var manual = Started();
        Assert.False(Tick(manual, 11250, Owner.Manual, Outside, true));
        Assert.Equal(0, manual.Attempts);
    }

    [Fact]
    public void FirstFailedSampleDoesNotChargePreviousHealthyInterval()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        Assert.False(Tick(policy, 1000, Owner.RoomRoute, Outside, false));
        for (long now = 1250; now < 11250; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true));
        Assert.True(Tick(policy, 11250, Owner.RoomRoute, Outside, true));
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void ActorPreemptionDoesNotEraseFailureHoldOrAutomaticallyRetry()
    {
        var policy = Failed();
        Assert.False(Tick(policy, 70250, Owner.Actor, Outside, false));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
        Assert.True(Tick(policy, 70500, Owner.RoomRoute, Outside, false));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
        Assert.Equal(3, policy.Attempts);
    }

    [Fact]
    public void OwnerChangesAndObservationGapsRequireANewContinuousFailureWindow()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        for (long now = 1000; now <= 10000; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true));
        Assert.False(Tick(policy, 10250, Owner.Actor, Outside, true));
        for (long now = 10500; now < 20500; now += 250)
            Assert.False(Tick(policy, now, Owner.RoomRoute, Outside, true));
        Assert.True(Tick(policy, 20500, Owner.RoomRoute, Outside, true));

        var missing = new DeepDungeonRoomRejoinPolicy();
        for (long now = 1000; now <= 10000; now += 250)
            Tick(missing, now, Owner.RoomRoute, Outside, true);
        Assert.False(Tick(missing, 30000, Owner.RoomRoute, Outside, true));
        Assert.False(Tick(missing, 30250, Owner.RoomRoute, Outside, true));
    }

    private static DeepDungeonRoomRejoinPolicy Started()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        for (long now = 1000; now <= 11000; now += 250)
            Tick(policy, now, Owner.RoomRoute, Outside, true);
        Assert.Equal(1, policy.Attempts);
        return policy;
    }

    private static DeepDungeonRoomRejoinPolicy Failed()
    {
        var policy = Started();
        for (long now = 11250; now <= 70000; now += 250)
            Tick(policy, now, Owner.RoomRoute, Outside, true);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
        return policy;
    }

    private static bool Tick(DeepDungeonRoomRejoinPolicy policy, long now, Owner owner, WPos player, bool failed,
        bool eligible = true, string? context = Context)
    {
        var evidence = DeepDungeonRoomRejoinPolicy.FreshFailedRoute(Route, Route, now, now - 20, failed);
        return policy.Step(now, context, owner, eligible, evidence, player, Source, Size, Next, new(21, 21.8f));
    }
}
