using Xunit;

namespace BossMod.Tests;

public class DeepDungeonRoomRejoinTests
{
    private static readonly WPos Source = new(303.3f, -292.9f);
    private static readonly WDir SourceSize = new(29.3f, 28.9f);
    private static readonly WPos Next = new(312.3f, -357.6f);
    private static readonly WDir NextSize = new(21, 21.8f);
    private static readonly WPos LoggedPlayer = new(335.4f, -257.1f);

    [Fact]
    public void LoggedFloor94PositionStartsReentryOutsideRoomAndRouteCorridor()
    {
        var lateral = DeepDungeonRoomRejoinPolicy.LateralDistance(LoggedPlayer, Source, Next);
        Assert.InRange(lateral, 36, 38);
        Assert.False(DeepDungeonRoomRejoinPolicy.InsideRoom(LoggedPlayer, Source, SourceSize));
        var policy = Started();
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(1, policy.Attempts);
        Assert.Equal(Source, policy.Anchor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void OrdinaryCorridorPositionsDoNotStartReentry(float lateral)
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        policy.Bind("test");
        policy.Update(1000, new(0, lateral), default, new(20, 20), new(30, 0), new(10, 10), false);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, policy.State);
        Assert.Equal(0, policy.Attempts);
    }

    [Fact]
    public void ActiveReentryLocksGeometryRatherThanFollowingNearestRoomJitter()
    {
        var policy = Started();
        var generation = policy.Generation;
        var anchor = policy.Anchor;
        policy.Update(1250, LoggedPlayer, new(-100, 100), new(20, 20), new(-100, 200), new(20, 20), false);
        Assert.Equal(Source, policy.Source);
        Assert.Equal(Next, policy.Next);
        Assert.Equal(anchor, policy.Anchor);
        Assert.Equal(generation, policy.Generation);
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void RotatedRoomRouteUsesSameLateralEntryAndInteriorAnchorRules()
    {
        static WPos Rotate(WPos p) => new(-p.Z, p.X);
        var policy = new DeepDungeonRoomRejoinPolicy();
        policy.Bind("rotated");
        policy.Update(1000, Rotate(LoggedPlayer), Rotate(Source), new(SourceSize.Z, SourceSize.X),
            Rotate(Next), new(NextSize.Z, NextSize.X), false);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(Rotate(Source), policy.Anchor);
        Assert.InRange(DeepDungeonRoomRejoinPolicy.LateralDistance(Rotate(LoggedPlayer), policy.Source, policy.Next), 36, 38);
    }

    [Fact]
    public void NearCenterlineOutsideActualSourceRoomDoesNotProveReentry()
    {
        var policy = Started();
        var player = Source - (Next - Source).Normalized() * 50;
        for (long now = 1250; now <= 5000; now += 250)
            Update(policy, now, player);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void ReentryRequiresSustainedActualRoomMembershipAndRetainsBudgetOnRelapse()
    {
        var policy = Started();
        for (long now = 1250; now < 2000; now += 250)
        {
            Update(policy, now, Source);
            Assert.True(policy.OwnsNavigation);
        }
        Update(policy, 2000, Source);
        Assert.False(policy.OwnsNavigation);
        Assert.Equal(1, policy.Attempts);
        var relapse = Source + (Next - Source).Normalized().OrthoL() * 13;
        Update(policy, 2250, relapse);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(2, policy.Attempts);
    }

    [Fact]
    public void VerifiedForwardProgressRearmsOnlyAfterStableResume()
    {
        var policy = Started();
        for (long now = 1250; now <= 2000; now += 250)
            Update(policy, now, Source);
        Assert.Equal(1, policy.Attempts);
        var forward = Source + (Next - Source).Normalized() * 5;
        for (long now = 2250; now < 7000; now += 250)
        {
            Update(policy, now, forward);
            Assert.Equal(1, policy.Attempts);
        }
        Update(policy, 7000, forward);
        Assert.Equal(0, policy.Attempts);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, policy.State);
    }

    [Fact]
    public void ReachingNextConnectedRoomIsStructuralProgress()
    {
        var policy = Started();
        Update(policy, 1250, Next);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, policy.State);
        Assert.Equal(0, policy.Attempts);
    }

    [Fact]
    public void ProvisionalReentryWithoutSubsequentRouteProgressContinuesSameFiniteBudget()
    {
        var policy = Started();
        for (long now = 1250; now <= 2000; now += 250)
            Update(policy, now, Source);
        for (long now = 2250; now <= 17000; now += 250)
            Update(policy, now, Source);
        Assert.Equal(2, policy.Attempts);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
    }

    [Fact]
    public void ThreeBoundedAttemptsHaveDistinctInteriorAnchorsThenLatchFailure()
    {
        var policy = Started();
        var anchors = new HashSet<WPos> { policy.Anchor };
        for (long now = 1250; now <= 50000; now += 250)
        {
            Update(policy, now, LoggedPlayer);
            anchors.Add(policy.Anchor);
            Assert.True(DeepDungeonRoomRejoinPolicy.InsideRoom(policy.Anchor, Source, SourceSize));
            Assert.True(DeepDungeonRoomRejoinPolicy.LateralDistance(policy.Anchor, Source, Next) <= 8);
        }
        Assert.Equal(3, policy.Attempts);
        Assert.Equal(3, anchors.Count);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
        var generation = policy.Generation;
        Update(policy, 50250, LoggedPlayer);
        Assert.Equal(generation, policy.Generation);
    }

    [Fact]
    public void CombatSuspensionDoesNotRefreshBudgetOrCompleteReentry()
    {
        var policy = Started();
        for (long now = 1250; now <= 10000; now += 250)
            Update(policy, now, Source, true);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(1, policy.Attempts);
        Update(policy, 10250, LoggedPlayer);
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void LoadingGapDoesNotChargeAttemptButWallDeadlineStillBoundsEpisode()
    {
        var policy = Started();
        Update(policy, 150000, LoggedPlayer);
        Assert.Equal(1, policy.Attempts);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Update(policy, 181000, LoggedPlayer, true);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.FailedHold, policy.State);
    }

    [Fact]
    public void MissingObservationWindowCannotCompleteReentryVerification()
    {
        var policy = Started();
        for (long now = 1250; now <= 1750; now += 250)
            Update(policy, now, Source);
        Update(policy, 10000, Source);
        Update(policy, 10250, Source);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Rejoining, policy.State);
        Assert.Equal(1, policy.Attempts);
    }

    [Fact]
    public void ContextChangeAndExplicitResetAreDistinctFromModeSuspension()
    {
        var policy = Started();
        policy.Bind("POTD/94/596/3/Everything");
        Assert.Equal(1, policy.Attempts);
        policy.Bind("POTD/95/596/3/Everything");
        Assert.Equal(0, policy.Attempts);
        Update(policy, 1250, LoggedPlayer);
        Assert.Equal(1, policy.Attempts);
        policy.Reset();
        Assert.False(policy.OwnsNavigation);
        Assert.Null(policy.Context);
    }

    [Fact]
    public void ReentryGoalAllowsLateralAndBackwardRepositioningWithoutRewardingStandingStill()
    {
        var player = new WPos(5, 20);
        var goal = DeepDungeonRoomRejoinPolicy.RejoinGoal(player, default, 0.01f);
        var backwardsAndInward = new WPos(3, 15);
        Assert.Equal(0, DeepDungeonPolicy.ForwardRoomRoutePriority(backwardsAndInward, default, new(20, 0), 5, 10, 0.01f));
        Assert.Equal(0, goal(player));
        Assert.True(goal(backwardsAndInward) > 0);
        Assert.True(goal(default) > goal(backwardsAndInward));
    }

    private static DeepDungeonRoomRejoinPolicy Started()
    {
        var policy = new DeepDungeonRoomRejoinPolicy();
        policy.Bind("POTD/94/596/3/Everything");
        Update(policy, 1000, LoggedPlayer);
        return policy;
    }

    private static void Update(DeepDungeonRoomRejoinPolicy policy, long now, WPos player, bool suspended = false)
        => policy.Update(now, player, Source, SourceSize, Next, NextSize, suspended);
}
