using System.Collections;
using BossMod.Pathfinding;
using Xunit;

namespace BossMod.Tests;

// Exercise the same rasterizer, solver, waypoint extraction and probe validation as Build,
// without constructing AIHints' game-service-dependent action queue.
public class DeepDungeonCoordinationNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeRoomProgressChoosesDistantForwardCellRatherThanArrivalDeadzone(bool rotated)
    {
        var source = rotated ? new WPos(0, 33.5f) : new WPos(33.5f, 0);
        var next = rotated ? new WPos(0, 93.5f) : new WPos(93.5f, 0);
        var direction = (next - source).Normalized();
        var start = (default(WPos) - source).Dot(direction);
        var map = new Map(0.5f, default, 30, 30);
        var decision = Solve(map, default, p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, start, 10, 0.01f));
        Assert.NotNull(decision.Destination);
        Assert.True((decision.SearchGoalPosition!.Value - default(WPos)).Dot(direction) > 20);
        Assert.True((decision.Destination!.Value - default(WPos)).Length() > 0.1f);
    }

    [Fact]
    public void RoomRouteGradientDoesNotForceMovementAcrossBlockedForwardCorridor()
    {
        var map = new Map(0.5f, default, 8, 8);
        AddObstacle(map, ShapeDistance.Rect(new(1, 0), new WDir(1, 0), 1, 0, 8), false);
        var decision = Solve(map, default,
            p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, new(33.5f, 0), new(93.5f, 0), -33.5f, 2, 0.01f));
        Assert.True(decision.SearchGoalPosition!.Value.X < 1);
    }

    [Fact]
    public void ProbeAcceptsSafeSolverRouteToRequestedRegion()
    {
        var decision = Probe(new Map(0.5f, default, 8, 8));
        Assert.True(decision.HasCoordinationGoal);
        Assert.True(decision.CoordinationGoalSatisfied);
        Assert.NotNull(decision.Destination);
        Assert.True(decision.SearchGoalPosition!.Value.InCircle(new(4, 0), 0.75f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedProbeRejectsFallbackRatherThanWalkingTowardUnvalidatedEndpoint(bool hazard)
    {
        var map = new Map(0.5f, default, 8, 8);
        AddObstacle(map, ShapeDistance.Circle(new(4, 0), 2), hazard);
        var decision = Probe(map);
        Assert.True(decision.HasCoordinationGoal);
        Assert.False(decision.CoordinationGoalSatisfied);
        Assert.Null(decision.Destination);
        Assert.Null(decision.NextWaypoint);
    }

    [Fact]
    public void OrdinaryNavigationDoesNotRequireProbeValidation()
    {
        var decision = Solve(new Map(0.5f, default, 8, 8), new(-4, 0), AIHints.GoalSingleTarget(new(4, 0), 0.75f, 2));
        NavigationDecision.ValidateCoordinationGoal(ref decision, null, 0, false);
        Assert.False(decision.HasCoordinationGoal);
        Assert.NotNull(decision.Destination);
    }

    [Fact]
    public void ProbeCannotSuppressEmergencyEscapeFromCurrentHazard()
    {
        var map = new Map(0.5f, default, 8, 8);
        AddObstacle(map, ShapeDistance.Circle(new(-4, 0), 2), true);
        AddObstacle(map, ShapeDistance.Circle(new(4, 0), 2), false);
        var decision = Probe(map, true);
        Assert.False(decision.HasCoordinationGoal);
        Assert.NotNull(decision.Destination);
    }

    [Fact]
    public void AlreadyInsideValidatedProbeRegionNeedsNoForcedMovement()
    {
        var decision = Solve(new Map(0.5f, default, 8, 8), new(4, 0), AIHints.GoalSingleTarget(new(4, 0), 0.75f, 2));
        NavigationDecision.ValidateCoordinationGoal(ref decision, new WPos(4, 0), 0.75f, false);
        Assert.True(decision.HasCoordinationGoal);
        Assert.True(decision.CoordinationGoalSatisfied);
        Assert.Null(decision.Destination);
    }

    private static NavigationDecision Probe(Map map, bool emergency = false)
    {
        var decision = Solve(map, new(-4, 0), AIHints.GoalSingleTarget(new(4, 0), 0.75f, 2), emergency);
        NavigationDecision.ValidateCoordinationGoal(ref decision, new WPos(4, 0), 0.75f, emergency);
        return decision;
    }

    [Fact]
    public void Floor101ChestOwnerExcludesRoomAttractionThenHandsBackToRoom()
    {
        var source = new WPos(303.3f, -292.9f);
        var next = new WPos(312.3f, -357.6f);
        var player = new WPos(302.5f, -303.5f);
        var chest = new WPos(290.8f, -306.2f);
        var progress = (player - source).Dot((next - source).Normalized());
        float Room(WPos p) => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, progress, 10, 0.01f);
        var chestRange = AIHints.GoalSingleTarget(chest, 2, 0.02f);
        var chestProgress = DeepDungeonPolicy.TreasureProgressGoal(chest, player, 100, 1, 0.02f);
        var mixed = Solve(new Map(0.5f, player, 30, 30), player,
            p => (DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor) ? Room(p) : 0)
                + chestRange(p) + chestProgress(p));
        Assert.Equal(0, Room(mixed.SearchGoalPosition!.Value));
        Assert.True(chestRange(mixed.SearchGoalPosition.Value) + chestProgress(mixed.SearchGoalPosition.Value) >= 0.04f);
        var policy = new DeepDungeonRoomRejoinPolicy();
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
        for (long now = 1000; now <= 30000; now += 250)
            Assert.False(policy.Step(now, "floor101/room8", owner, true,
                NavigationDecision.RoomRouteNeedsRecovery(mixed, player, Room),
                chest, source, new(29.3f, 28.9f), next, new(21, 21.8f)));
        Assert.Equal(0, policy.Attempts);

        var afterLootProgress = (chest - source).Dot((next - source).Normalized());
        float AfterLootRoom(WPos p) => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, afterLootProgress, 10, 0.01f);
        var roomOnly = Solve(new Map(0.5f, chest, 30, 30), chest, AfterLootRoom);
        Assert.False(NavigationDecision.RoomRouteNeedsRecovery(roomOnly, chest, AfterLootRoom));
        for (long now = 30250; now <= 45000; now += 250)
            Assert.False(policy.Step(now, "floor101/room8", DeepDungeonRoomRejoinPolicy.NavigationOwner.RoomRoute, true,
                NavigationDecision.RoomRouteNeedsRecovery(roomOnly, chest, AfterLootRoom),
                chest, source, new(29.3f, 28.9f), next, new(21, 21.8f)));
        Assert.Equal(0, policy.Attempts);
    }

    [Fact]
    public void Floor107BoundaryCrossingsKeepTreasureOwnerAndSolverGoalUntilLootCompletion()
    {
        const string context = "POTD/107/598";
        const ulong chestID = 1073804056;
        var source = new WPos(370.9f, -254.5f);
        var next = new WPos(378, -195.9f);
        var chest = next;
        var selection = new DeepDungeonTreasureSelection();
        var recovery = new DeepDungeonRoomRejoinPolicy();
        for (int frame = 0; frame < 100; ++frame)
        {
            // Room 23 is adjacent from 18, but not from 17. Reproduce logged classification flip.
            var adjacent = frame % 2 == 0;
            var player = new WPos(332.2f, adjacent ? -243.1f : -241.8f);
            var candidate = adjacent || selection.CanRetain(context, chestID);
            Assert.True(candidate);
            selection.Select(context, candidate ? chestID : 0, DateTime.UnixEpoch.AddMilliseconds(frame * 100), adjacent);
            var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, candidate, true, false);
            Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor, owner);
            var progress = (player - source).Dot((next - source).Normalized());
            float Room(WPos p) => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, progress, 10, 0.01f);
            var treasure = DeepDungeonPolicy.TreasureProgressGoal(chest, player, 100, 1, 0.02f);
            var decision = Solve(new Map(0.5f, player, 30, 30), player,
                p => (DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner) ? Room(p) : 0) + treasure(p));
            Assert.True(treasure(decision.SearchGoalPosition!.Value) > 0);
            Assert.False(recovery.Step(1000 + frame * 100, context, owner, true,
                NavigationDecision.RoomRouteNeedsRecovery(decision, player, Room),
                player, source, new(25.4f, 22.6f), next, new(21, 22.1f)));
        }
        Assert.Equal(0, recovery.Attempts);
        selection.Select(context, 0, DateTime.UnixEpoch.AddSeconds(10), false); // opened/untargetable chest no longer reaches candidate selection
        Assert.False(selection.CanRetain(context, chestID));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.RoomRoute,
            DeepDungeonRoomRejoinPolicy.SelectOwner(false, false, true, false));
    }

    [Fact]
    public void RecoveryFeedbackDistinguishesNoGoalArrivalAndSafetyMovement()
    {
        var failed = new NavigationDecision { SearchGoalPosition = new(5, 0), LeewaySeconds = float.MaxValue };
        Assert.True(NavigationDecision.RoomRouteNeedsRecovery(failed, default, _ => 0));
        failed.Destination = new WPos(0.05f, 0);
        Assert.True(NavigationDecision.RoomRouteNeedsRecovery(failed, default, _ => 1));
        failed.NextWaypoint = new WPos(5, 0);
        failed.PathfindDestination = failed.Destination;
        Assert.False(NavigationDecision.RoomRouteNeedsRecovery(failed, default, _ => 1));
        failed.LeewaySeconds = 0;
        Assert.False(NavigationDecision.RoomRouteNeedsRecovery(failed, default, _ => 0));
    }

    [Fact]
    public void OffCorridorReentryOffersReachableLocalProgressWhenRouteGoalsAreDisconnected()
    {
        var player = new WPos(335.4f, -257.1f);
        var source = new WPos(303.3f, -292.9f);
        var next = new WPos(312.3f, -357.6f);
        var progress = (player - source).Dot((next - source).Normalized());
        Map LocalMap()
        {
            var map = new Map(0.5f, new(335, -257.5f), 30, 30);
            // Synthetic static bitmap separation, not a claim about the live floor's obstruction.
            // Mark the complete column band explicitly so SDF boundary sampling cannot leave a gap.
            for (var y = 0; y < map.Height; ++y)
                for (var x = 0; x < map.Width; ++x)
                    if (map.GridToWorld(x, y, 0.5f, 0.5f).X is >= 321 and <= 324)
                        map.PixelMaxG[map.GridToIndex(x, y)] = -1000;
            return map;
        }
        var normal = Solve(LocalMap(), player,
            p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, progress, 10, 0.01f),
            inspect: solver => Assert.Equal(0, solver.CountVisitedGoalCells().VisitedGoalCells));
        // A safe partial endpoint is allowed; it does not prove that any route goal was reachable.
        Assert.Equal(0, DeepDungeonPolicy.ForwardRoomRoutePriority(normal.SearchGoalPosition!.Value, source, next, progress, 10, 0.01f));
        var recovery = new DeepDungeonRoomRejoinPolicy();
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, false, true, false);
        var failure = NavigationDecision.RoomRouteNeedsRecovery(normal, player,
            p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next, progress, 10, 0.01f));
        Assert.True(failure);
        for (long now = 1000; now < 11000; now += 250)
            Assert.False(recovery.Step(now, "floor94/room3", owner, true, failure,
                player, source, new(29.3f, 28.9f), next, new(21, 21.8f)));
        Assert.True(recovery.Step(11000, "floor94/room3", owner, true, failure,
            player, source, new(29.3f, 28.9f), next, new(21, 21.8f)));
        Assert.Equal(1, recovery.Attempts);
        var rejoinGoal = DeepDungeonRoomRejoinPolicy.RejoinGoal(player, recovery.Anchor, 0.01f);
        var rejoin = Solve(LocalMap(), player, rejoinGoal,
            inspect: solver => Assert.True(solver.CountVisitedGoalCells().VisitedGoalCells > 0));
        Assert.NotNull(rejoin.Destination);
        Assert.True(rejoinGoal(rejoin.SearchGoalPosition!.Value) > 0);
        Assert.True((rejoin.SearchGoalPosition.Value - source).Length() < (normal.SearchGoalPosition.Value - source).Length());
        Assert.True(rejoin.SearchGoalPosition!.Value.X < player.X);
        Assert.True(rejoin.SearchGoalPosition.Value.X >= 324);
        Assert.Equal(float.MaxValue, rejoin.LeewaySeconds);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(6, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void ProbeAndFollowerArrivalRegionsLeaveEnoughActualDisplacementToCompleteRecovery(float follow, int attempt)
    {
        var follower = new WPos(0, 0);
        var leader = new WPos(-8, 0);
        var probe = DeepDungeonPartyPolicy.RecoveryProbeFromCurrent(follower, leader, default, follow, 20, attempt)!.Value;
        var radius = DeepDungeonPartyPolicy.RecoveryProbeRadius;
        var leaderDecision = Solve(new Map(0.5f, follower, 30, 30), leader, AIHints.GoalSingleTarget(probe, radius, 2));
        NavigationDecision.ValidateCoordinationGoal(ref leaderDecision, probe, radius, false);
        Assert.True(leaderDecision.CoordinationGoalSatisfied);
        // Include the movement executor's final-waypoint deadzone on both clients.
        var direction = (probe - follower).Normalized();
        var leaderStop = leaderDecision.SearchGoalPosition!.Value - direction * 0.1f;
        var followRange = AIHints.GoalSingleTarget(leaderStop, follow, 2);
        var followProgress = DeepDungeonPolicy.PassageProgressGoal(leaderStop, follower, follow, 100, 1, 1);
        var followerDecision = Solve(new Map(0.5f, follower, 30, 30), follower, p => followRange(p) + followProgress(p));
        var followerStop = followerDecision.SearchGoalPosition!.Value - direction * 0.1f;
        Assert.True((followerStop - follower).Length() >= DeepDungeonPartyPolicy.RecoveryFollowerDisplacement);
        Assert.True((followerStop - leaderStop).Length() <= follow + 0.1f);

        var state = new DeepDungeonPartyPolicy.Regroup();
        for (long now = 1000; now <= 16000; now += 250)
            state.Update(now, true, true, false, false);
        Assert.Equal(1, state.Attempts);
        var progress = new DeepDungeonPartyPolicy.FollowerProgress();
        progress.Observe(16250, follower, 0, true, probe);
        for (long now = 16500; now <= 25000; now += 250)
        {
            // Retiring a completed probe resets local measurement, not the episode's proven response.
            WPos? observationGoal = state.State == DeepDungeonPartyPolicy.RegroupState.Verifying ? null : probe;
            var response = progress.Observe(now, followerStop, 0, true, observationGoal);
            state.Update(now, true, false, true, false, response);
            Assert.Null(state.FailureReason);
            Assert.True(state.Attempts <= 1);
            if (state.State == DeepDungeonPartyPolicy.RegroupState.Clearing)
                break;
        }
        Assert.Equal(DeepDungeonPartyPolicy.RegroupState.Clearing, state.State);
    }

    [Fact]
    public void Floor111RoomSegmentFlipCannotChangeChestOwnedSolverObjective()
    {
        var player = new WPos(243.5f, -265.2f);
        var chest = new WPos(294, -264); // synthetic chest consistent with both logged progress plateaus
        var room11 = new WPos(241.7f, -286.9f);
        var room16 = new WPos(245.3f, -240.3f);
        var room10 = new WPos(181.5f, -299.5f);
        var north = new WPos(244.75f, -263.75f);
        var south = new WPos(251.75f, -279.75f);
        float Room(WPos p, WPos source, WPos next) => DeepDungeonPolicy.ForwardRoomRoutePriority(p, source, next,
            (player - source).Dot((next - source).Normalized()), 10, 0.01f);
        var treasure = DeepDungeonPolicy.TreasureProgressGoal(chest, player, 100, 1, 0.02f);
        Assert.Equal(0.02f, treasure(north));
        Assert.Equal(0.02f, treasure(south));
        Assert.True(Room(south, room16, room11) > Room(north, room16, room11));
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
        Assert.False(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner));
        Assert.False(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Suspended));
        WPos? previousGoal = null;
        for (int frame = 0; frame < 10; ++frame)
        {
            var source = frame % 2 == 0 ? room16 : room11;
            var next = frame % 2 == 0 ? room11 : room10;
            var decision = Solve(new Map(0.5f, player, 30, 30), player,
                p => treasure(p) + (DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner) ? Room(p, source, next) : 0));
            Assert.True(treasure(decision.SearchGoalPosition!.Value) > 0);
            if (previousGoal != null)
                Assert.Equal(previousGoal, decision.SearchGoalPosition);
            previousGoal = decision.SearchGoalPosition;
        }
        // Completion/defer releases the actor; ordinary and manual room routing remain available.
        var roomOwner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, false, true, false);
        Assert.True(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(roomOwner));
        Assert.True(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Manual));
        var roomDecision = Solve(new Map(0.5f, player, 30, 30), player, p => Room(p, room16, room11));
        Assert.True(Room(roomDecision.SearchGoalPosition!.Value, room16, room11) > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnlargedProbeStillRequiresSafeSolverAccessToItsActualArrivalRegion(bool hazard)
    {
        var player = new WPos(-4, 0);
        var probe = DeepDungeonPartyPolicy.RecoveryProbeFromCurrent(default, player, default, 3, 15, 1)!.Value;
        var map = new Map(0.5f, default, 12, 12);
        AddObstacle(map, ShapeDistance.Circle(probe, 2), hazard);
        var radius = DeepDungeonPartyPolicy.RecoveryProbeRadius;
        var decision = Solve(map, player, AIHints.GoalSingleTarget(probe, radius, 2));
        NavigationDecision.ValidateCoordinationGoal(ref decision, probe, radius, false);
        Assert.True(decision.HasCoordinationGoal);
        Assert.False(decision.CoordinationGoalSatisfied);
        Assert.Null(decision.Destination);
        Assert.Null(decision.NextWaypoint);
    }

    [Fact]
    public void DistantPassageUsesHistoricalActorApproachBeforeSwitchingToInnerAssembly()
    {
        var player = new WPos(293.5f, -217.3f);
        // Synthetic geometry: reproduce a 65y actor outside the local map.
        var passage = new WPos(228.3f, -217.3f);
        var local = DeepDungeonPartyPolicy.UseLocalPassageGoal(42, 0, (passage - player).Length(), 0.02f);
        Assert.False(local);
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
        Assert.False(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner));
        var assembly = DeepDungeonPartyPolicy.PassageAssemblyGoal(passage, 0.01f);
        var goals = DeepDungeonPolicy.PassageApproachGoals(passage, player, 0.01f);
        float Approach(WPos p) => goals.Arrival(p) + goals.Approach(p) + goals.Progress(p);
        var route = Solve(new Map(0.5f, player, 30, 30), player, Approach,
            inspect: solver => Assert.True(solver.CountVisitedGoalCells().VisitedGoalCells > 0));
        Assert.NotNull(route.Destination);
        Assert.True((route.SearchGoalPosition!.Value - passage).Length() + 1 < (player - passage).Length());
        Assert.Equal(0, assembly(route.SearchGoalPosition.Value));
        Assert.True(Approach(route.SearchGoalPosition.Value) > 0);

        var near = passage + new WDir(5.5f, 0);
        Assert.True(DeepDungeonPartyPolicy.UseLocalPassageGoal(42, 0, (near - passage).Length(), 0.02f));
        for (int retry = 0; retry <= 3; ++retry)
        {
            var goal = DeepDungeonPartyPolicy.PassageAssemblyGoal(passage, 0.01f, retry);
            var decision = Solve(new Map(0.5f, near, 10, 10), near, goal);
            Assert.NotNull(decision.Destination);
            Assert.True(decision.SearchGoalPosition!.Value.InCircle(passage, DeepDungeonPartyPolicy.PassageInnerRadius));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticFloor131ConstraintsRejectRoomCenterButKeepSafePassageApproach(bool hazard)
    {
        // Reproduce the logged failure pattern, not the unavailable exact floor-131 raster.
        var player = new WPos(392.3f, -252.5f);
        var room = new WPos(368.906f, -254.468f);
        var passage = player - new WDir(32.7f, 0); // synthetic actor bearing; log provides distance only
        Map ConstrainedMap()
        {
            var map = new Map(0.5f, player, 30, 30);
            AddObstacle(map, ShapeDistance.Circle(room, 6), hazard);
            return map;
        }
        var centerMap = ConstrainedMap();
        var oldGoal = AIHints.GoalSingleTarget(room, 3, 0.02f);
        var oldDecision = Solve(centerMap, player, oldGoal,
            inspect: solver => Assert.Equal(0, solver.CountVisitedGoalCells().VisitedGoalCells));
        Assert.Equal(0, centerMap.MaxPriority);
        Assert.Null(oldDecision.Destination);

        var actorMap = ConstrainedMap();
        var goals = DeepDungeonPolicy.PassageApproachGoals(passage, player, 0.01f);
        float ActorGoal(WPos p) => goals.Arrival(p) + goals.Approach(p) + goals.Progress(p);
        var decision = Solve(actorMap, player, ActorGoal,
            inspect: solver => Assert.True(solver.CountVisitedGoalCells().VisitedGoalCells > 0));
        Assert.True(actorMap.MaxPriority > 0);
        Assert.NotNull(decision.Destination);
        Assert.True((decision.SearchGoalPosition!.Value - passage).Length() < 25);
        Assert.True(ActorGoal(decision.SearchGoalPosition.Value) > 0);
        Assert.Equal(float.MaxValue, decision.LeewaySeconds);
        var endpoint = actorMap.GridToIndex(actorMap.WorldToGrid(decision.SearchGoalPosition.Value));
        Assert.Equal(float.MaxValue, actorMap.PixelMaxG[endpoint]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassageApproachDoesNotForceMovementWhenAllPositiveGoalCellsAreExcluded(bool hazard)
    {
        var passage = new WPos(0, 0);
        var player = new WPos(81.7f, 0);
        var map = new Map(0.5f, player, 30, 30);
        // All progress cells are closer than 80.7y; current player cell remains safe outside 81y.
        AddObstacle(map, ShapeDistance.Circle(passage, 81), hazard);
        var goals = DeepDungeonPolicy.PassageApproachGoals(passage, player, 0.01f);
        var decision = Solve(map, player, p => goals.Arrival(p) + goals.Approach(p) + goals.Progress(p),
            inspect: solver => Assert.Equal(0, solver.CountVisitedGoalCells().VisitedGoalCells));
        Assert.Equal(0, map.MaxPriority);
        Assert.Null(decision.Destination);
        Assert.False(decision.SearchGoalPosition!.Value.InCircle(passage, 25));
    }

    [Fact]
    public void PassageAssemblyAndRetriesHaveFullSafeRasterCellsAcrossGridAlignments()
    {
        for (int x = 0; x < 4; ++x)
            for (int z = 0; z < 4; ++z)
                for (int retry = 0; retry <= 3; ++retry)
                {
                    var passage = new WPos(x * 0.125f, z * 0.125f);
                    var player = new WPos(5.5f, 0);
                    var goal = DeepDungeonPartyPolicy.PassageAssemblyGoal(passage, 0.01f, retry);
                    var map = new Map(0.5f, default, 10, 10);
                    var decision = Solve(map, player, goal,
                        inspect: solver => Assert.True(solver.CountVisitedGoalCells().VisitedGoalCells > 0));
                    Assert.True(map.MaxPriority > 0);
                    Assert.NotNull(decision.Destination);
                    Assert.True(decision.SearchGoalPosition!.Value.InCircle(passage, DeepDungeonPartyPolicy.PassageInnerRadius));
                }
    }

    private static NavigationDecision Solve(Map map, WPos player, Func<WPos, float> goal, bool emergency = false, Action<ThetaStar>? inspect = null)
    {
        if (!emergency)
            NavigationDecision.RasterizeGoalZones(map, [goal], false);
        var solver = new ThetaStar();
        solver.Start(map, player, 1.0f / 6);
        var best = solver.Execute();
        inspect?.Invoke(solver);
        var (destination, nextWaypoint) = NavigationDecision.GetFirstWaypoints(solver, best);
        return new()
        {
            Destination = destination,
            PathfindDestination = destination,
            NextWaypoint = nextWaypoint,
            SearchGoalPosition = solver.CellCenter(best),
            LeewaySeconds = solver.NodeByIndex(best).PathLeeway,
        };
    }

    private static void AddObstacle(Map map, Func<WPos, float> obstacle, bool hazard)
    {
        var count = (map.Width + 1) * (map.Height + 1);
        NavigationDecision.RasterizeForbiddenZone(map, Sdf.Continuous(obstacle), hazard ? 0 : -1000, new float[count], new BitArray(count), 0);
    }
}
