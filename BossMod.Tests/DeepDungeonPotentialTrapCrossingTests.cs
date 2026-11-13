using System.Collections;
using BossMod.Pathfinding;
using Xunit;

namespace BossMod.Tests;

public class DeepDungeonPotentialTrapCrossingTests
{
    private const string Key = "POTD/143/602/actor:Treasure:1073756981";

    private static DeepDungeonPotentialTrapCrossing Started()
    {
        var policy = new DeepDungeonPotentialTrapCrossing();
        for (long now = 1000; now < 16000; now += 250)
            Assert.False(policy.Update(now, Key, true, false, true, default));
        Assert.True(policy.Update(16000, Key, true, false, true, default));
        return policy;
    }

    [Fact]
    public void AutomaticCrossingWaitsFifteenSecondsAndHasFiniteNonRenewableDeadline()
    {
        var policy = Started();
        for (long now = 16250; now < 31000; now += 250)
            Assert.True(policy.Update(now, Key, true, false, true, default));
        Assert.True(policy.Expired(31000));
        Assert.False(policy.Update(31000, Key, true, false, true, default));
        for (long now = 31250; now <= 65000; now += 250)
            Assert.False(policy.Update(now, Key, true, false, true, default, requestedByObjective: true));
        Assert.Equal(DeepDungeonPotentialTrapCrossing.Phase.Finished, policy.State);
    }

    [Fact]
    public void HealthyNormalRouteOrInvalidAlternativeNeverStartsCrossing()
    {
        var healthy = new DeepDungeonPotentialTrapCrossing();
        var blocked = new DeepDungeonPotentialTrapCrossing();
        for (long now = 1000; now <= 30000; now += 250)
        {
            Assert.False(healthy.Update(now, Key, true, true, true, default, requestedByObjective: true));
            Assert.False(blocked.Update(now, Key, true, false, false, default, requestedByObjective: true));
        }
    }

    [Theory]
    [InlineData("manual-or-hold")]
    [InlineData("recovered-route")]
    [InlineData("new-hazard")]
    [InlineData("observation-gap")]
    [InlineData("changed-objective")]
    public void CrossingStopsForControlSafetyAndContextChanges(string reason)
    {
        var policy = Started();
        Assert.False(policy.Update(reason == "observation-gap" ? 17000 : 16250,
            reason == "changed-objective" ? Key + "/new" : Key,
            reason != "manual-or-hold", reason == "recovered-route", reason != "new-hazard", default));
        Assert.NotEqual(DeepDungeonPotentialTrapCrossing.Phase.Crossing, policy.State);
    }

    [Fact]
    public void ObjectiveTimerCanRequestCrossingForCirclingButStillRequiresValidatedRoute()
    {
        var policy = new DeepDungeonPotentialTrapCrossing();
        for (long now = 1000; now < 16000; now += 250)
            Assert.False(policy.Update(now, Key, true, false, true, new(now % 1000 / 100f, 0)));
        Assert.False(policy.Update(16000, Key, true, false, false, default, requestedByObjective: true));
        Assert.True(policy.Update(16250, Key, true, false, true, default, requestedByObjective: true));
    }

    [Fact]
    public void CandidateFeedbackMustBeFreshAndMatchTheExactObjective()
    {
        Assert.True(DeepDungeonPotentialTrapCrossing.FreshAlternative(Key, (Key, 1000, true, false), 1500));
        Assert.False(DeepDungeonPotentialTrapCrossing.FreshAlternative(Key, (Key, 1000, true, false), 1501));
        Assert.False(DeepDungeonPotentialTrapCrossing.FreshAlternative(Key, (Key, 1000, true, false), 999));
        Assert.False(DeepDungeonPotentialTrapCrossing.FreshAlternative(Key, (Key + "/other", 1000, true, false), 1000));
        Assert.False(DeepDungeonPotentialTrapCrossing.FreshAlternative(Key, (Key, 1000, false, false), 1000));
        Assert.False(DeepDungeonPotentialTrapCrossing.FreshAlternative(null, (Key, 1000, true, true), 1000));
    }

    [Fact]
    public void ComparisonOmitsOnlyHistoricalTagAndNeverMutatesTheOriginalZones()
    {
        var shape = Sdf.Continuous(ShapeDistance.Circle(default, 2));
        var zones = new List<(Sdf shape, DateTime activation, ulong tag)>
        {
            (shape, default, AIHints.DeepDungeonPotentialTrapTag),
            (shape, default, 0), // revealed traps currently use source 0
            (shape, DateTime.UnixEpoch.AddSeconds(5), 42), // timed enemy AOE
        };
        Assert.Same(zones, NavigationDecision.PotentialTrapZonesForSolve(zones, false));
        var comparison = NavigationDecision.PotentialTrapZonesForSolve(zones, true);
        Assert.Equal(3, zones.Count);
        Assert.Equal(2, comparison.Count);
        Assert.DoesNotContain(comparison, z => z.tag == AIHints.DeepDungeonPotentialTrapTag);
        Assert.Contains(comparison, z => z.tag == 0);
        Assert.Contains(comparison, z => z.tag == 42 && z.activation == DateTime.UnixEpoch.AddSeconds(5));
    }

    [Fact]
    public void HistoricalBarrierComparisonProducesGoalReachingRouteWithoutForcedMovement()
    {
        var normal = Solve(false);
        var alternative = Solve(true);
        Assert.False(Valid(normal));
        Assert.True(Valid(alternative));
        Assert.Equal(ThetaStar.Score.SafeMaxPrio, alternative.Score);
        Assert.Equal(float.MaxValue, alternative.Decision.LeewaySeconds);
        Assert.NotNull(alternative.Decision.Destination);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ComparisonCannotCrossConfirmedHazardOrWall(bool confirmedHazard, bool wall)
        => Assert.False(Valid(Solve(true, confirmedHazard, wall)));

    [Fact]
    public void PredictionOnlyEmergencyCanExitButConfirmedEmergencyStillCannotBeApproved()
    {
        var normal = Solve(false, startInsidePrediction: true);
        var alternative = Solve(true, startInsidePrediction: true);
        Assert.True(normal.Emergency);
        Assert.False(alternative.Emergency);
        Assert.True(Valid(alternative));
        Assert.False(Valid(Solve(true, confirmedHazard: true, startInsidePrediction: true)));
    }

    [Fact]
    public void PartialUnsafeEmergencyAndInsidePredictionEndpointsNeverApproveCrossing()
    {
        var decision = new NavigationDecision
        {
            Destination = new(5, 0), PathfindDestination = new(5, 0), SearchGoalPosition = new(5, 0),
            LeewaySeconds = float.MaxValue,
        };
        Assert.True(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, false, default, _ => 1, _ => 1));
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeBetterPrio, false, default, _ => 1, _ => 1));
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, true, default, _ => 1, _ => 1));
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, false, default, _ => 0, _ => 1));
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, false, default, _ => 1, _ => 0.49f));
        decision.LeewaySeconds = 0;
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, false, default, _ => 1, _ => 1));
        decision.LeewaySeconds = float.MaxValue;
        decision.Destination = new WPos(0.05f, 0);
        Assert.False(NavigationDecision.ValidPotentialTrapRoute(decision, ThetaStar.Score.SafeMaxPrio, false, default, _ => 1, _ => 1));
    }

    [Fact]
    public void CurrentHardHazardsAndTemporaryObstaclesCancelPreparedPathEvenWithClearEndpoints()
    {
        var route = new WPos[] { new(-5, 0), new(5, 0) };
        var barrier = Sdf.Continuous(Barrier());
        var zones = new List<(Sdf shape, DateTime activation, ulong tag)> { (barrier, default, AIHints.DeepDungeonPotentialTrapTag) };
        Assert.True(NavigationDecision.PotentialTrapPathStillClear(route, zones, []));
        zones.Add((Sdf.Continuous(ShapeDistance.Circle(default, 1)), DateTime.UnixEpoch.AddSeconds(10), 42));
        Assert.False(NavigationDecision.PotentialTrapPathStillClear(route, zones, []));
        zones.RemoveAt(1);
        zones.Add((barrier, default, 0)); // same geometry revealed as a real trap
        Assert.False(NavigationDecision.PotentialTrapPathStillClear(route, zones, []));
        zones.RemoveAt(1);
        Assert.False(NavigationDecision.PotentialTrapPathStillClear(route, zones, [barrier]));
        Assert.False(NavigationDecision.PotentialTrapPathStillClear([], zones, []));
        zones.Add((Sdf.Continuous(_ => float.NaN), default, 42));
        Assert.False(NavigationDecision.PotentialTrapPathStillClear(route, zones, []));
    }

    private readonly record struct Result(NavigationDecision Decision, ThetaStar.Score Score, bool Emergency, WPos Player);
    private static bool Valid(Result result) => NavigationDecision.ValidPotentialTrapRoute(result.Decision, result.Score,
        result.Emergency, result.Player, AIHints.GoalSingleTarget(new(5, 0), 0.75f, 1), Barrier());

    private static Func<WPos, float> Barrier() => ShapeDistance.Rect(default, new WDir(1, 0), 1, 1, 4);

    private static Result Solve(bool omitPredictions, bool confirmedHazard = false, bool wall = false, bool startInsidePrediction = false)
    {
        var player = startInsidePrediction ? default : new WPos(-5, 0);
        var map = new Map(0.5f, default, 8, 4);
        var barrier = Sdf.Continuous(Barrier());
        if (wall)
            map.BlockPixelsInside(barrier, -1000);
        var zones = new List<(Sdf shape, DateTime activation, ulong tag)> { (barrier, default, AIHints.DeepDungeonPotentialTrapTag) };
        if (confirmedHazard)
            zones.Add((barrier, default, 42));
        NavigationDecision.RasterizeForbiddenZones(map, player, NavigationDecision.PotentialTrapZonesForSolve(zones, omitPredictions),
            DateTime.UnixEpoch, [], new BitArray(0), 0, 0);
        var emergency = map.PixelMaxG[map.GridToIndex(map.WorldToGrid(player))] == 0;
        if (!emergency)
            NavigationDecision.RasterizeGoalZones(map, [AIHints.GoalSingleTarget(new(5, 0), 0.75f, 1)], false);
        var solver = new ThetaStar();
        solver.Start(map, player, 1.0f / 6);
        var best = solver.Execute();
        var (destination, nextWaypoint) = NavigationDecision.GetFirstWaypoints(solver, best);
        return new(new()
        {
            Destination = destination, PathfindDestination = destination, NextWaypoint = nextWaypoint,
            SearchGoalPosition = solver.CellCenter(best), LeewaySeconds = solver.NodeByIndex(best).PathLeeway,
        }, solver.NodeByIndex(best).Score, emergency, player);
    }
}
