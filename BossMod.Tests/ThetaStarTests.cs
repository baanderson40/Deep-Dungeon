using BossMod;
using BossMod.Pathfinding;
using Xunit;

namespace BossMod.Tests;

public class ThetaStarTests
{
    [Theory]
    [InlineData(0.005f, true, true)]
    [InlineData(0.05f, true, false)]
    [InlineData(0.05f, false, true)]
    [InlineData(0.11f, false, false)]
    public void IntermediateWaypointUsesSmallerArrivalDeadzone(float distance, bool hasNextWaypoint, bool expected)
        => Assert.Equal(expected, NavigationDecision.ReachedDestination(distance * distance, hasNextWaypoint));

    [Fact]
    public void SingleSegmentPathHasNoNextWaypointAndUsesFinalArrivalRadius()
    {
        var map = new Map(0.5f, default, 5, 5);
        var start = map.GridToIndex(10, 10);
        var goal = map.GridToIndex(15, 10);
        var pathfinder = new ThetaStar();
        pathfinder.Start(map, map.GridToWorld(10, 10, 0.5f, 0.5f), 1);
        pathfinder.NodeByIndex(goal) = new ThetaStar.Node { GScore = 1, ParentIndex = start };

        var (destination, nextWaypoint) = NavigationDecision.GetFirstWaypoints(pathfinder, goal);
        var decision = new NavigationDecision { Destination = destination, PathfindDestination = destination, NextWaypoint = nextWaypoint };

        Assert.NotNull(destination);
        Assert.Null(nextWaypoint);
        Assert.False(decision.HasIntermediateWaypoint);
        Assert.Equal(0.1f, NavigationDecision.DestinationArrivalRadius(decision.HasIntermediateWaypoint));
    }

    [Fact]
    public void DiagnosticGridPositionUsesCapturedPathfindMapTransform()
    {
        var map = new Map(0.5f, new(12, -7), 5, 5, 23.Degrees());
        var decision = new NavigationDecision
        {
            PathfindMapCenter = map.Center,
            PathfindMapLocalZDivRes = map.LocalZDivRes,
            PathfindMapWidth = map.Width,
            PathfindMapHeight = map.Height,
        };

        var world = map.GridToWorld(4, 13, 0.5f, 0.5f);
        Assert.Equal((4, 13), decision.GridPosition(world));
    }

    [Fact]
    public void AdjustedCombatDestinationDoesNotInheritPathWaypointArrivalMode()
    {
        var decision = new NavigationDecision
        {
            Destination = new WPos(1, 0),
            PathfindDestination = new WPos(2, 0),
            NextWaypoint = new WPos(3, 0),
        };

        Assert.False(decision.HasIntermediateWaypoint);
    }

    [Theory]
    [InlineData(10, 10, 0.01f, 11, 10, 15, 10)]
    [InlineData(9, 10, 0, 10, 10, 15, 10)]
    public void SameSafetyClassPrefersHigherGoalPriorityAcrossStartCellChanges(int startX, int startY, float startPriority, int nearX, int nearY, int highX, int highY)
    {
        var map = new Map(0.5f, default, 5, 5);
        var startIndex = map.GridToIndex(startX, startY);
        var nearIndex = map.GridToIndex(nearX, nearY);
        var highIndex = map.GridToIndex(highX, highY);
        var blockedMaxIndex = map.GridToIndex(18, 18);

        // This emulates a high-value interaction goal elsewhere in the local map that cannot
        // be reached, while room/chest attraction contributes low values to reachable cells.
        map.MaxPriority = 101;
        map.PixelPriority[startIndex] = startPriority;
        map.PixelPriority[nearIndex] = 0.01f;
        map.PixelPriority[highIndex] = 0.02f;
        map.PixelPriority[blockedMaxIndex] = map.MaxPriority;
        map.PixelMaxG[blockedMaxIndex] = -900;

        var pathfinder = new ThetaStar();
        pathfinder.Start(map, map.GridToWorld(startX, startY, 0.5f, 0.5f), 1);
        var bestIndex = pathfinder.Execute();

        Assert.Equal(0.02f, map.PixelPriority[bestIndex]);
        Assert.Equal(ThetaStar.Score.SafeBetterPrio, pathfinder.NodeByIndex(bestIndex).Score);
    }

    [Fact]
    public void TreasurePriorityWinsOverRoomRouteWhenBothAreSafelyReachable()
    {
        var map = new Map(0.5f, default, 5, 5);
        var routeIndex = map.GridToIndex(6, 10);
        var treasureIndex = map.GridToIndex(15, 10);
        map.MaxPriority = 0.02f;
        map.PixelPriority[routeIndex] = 0.01f;
        map.PixelPriority[treasureIndex] = 0.02f;

        var pathfinder = new ThetaStar();
        pathfinder.Start(map, map.GridToWorld(5, 10, 0.5f, 0.5f), 1);
        var bestIndex = pathfinder.Execute();

        Assert.Equal(0.02f, map.PixelPriority[bestIndex]);
        Assert.Equal(ThetaStar.Score.SafeMaxPrio, pathfinder.NodeByIndex(bestIndex).Score);
    }

    [Fact]
    public void ReachableRoomRouteRemainsFallbackWhenTreasureGoalIsSeparatedByStaticBlockers()
    {
        var map = new Map(0.5f, default, 5, 5);
        var startX = 4;
        var startY = 10;
        var routeIndex = map.GridToIndex(5, startY);
        var treasureIndex = map.GridToIndex(15, startY);
        map.MaxPriority = 0.02f;
        map.PixelPriority[routeIndex] = 0.01f;
        map.PixelPriority[treasureIndex] = 0.02f;
        for (var y = 0; y < map.Height; ++y)
        {
            var wallIndex = map.GridToIndex(10, y);
            map.PixelMaxG[wallIndex] = -900;
        }

        var pathfinder = new ThetaStar();
        pathfinder.Start(map, map.GridToWorld(startX, startY, 0.5f, 0.5f), 1);
        var bestIndex = pathfinder.Execute();

        Assert.Equal(0.01f, map.PixelPriority[bestIndex]);
        Assert.Equal(ThetaStar.Score.SafeBetterPrio, pathfinder.NodeByIndex(bestIndex).Score);
    }

    [Fact]
    public void FirstWaypointsPreserveThetaStarOffsetsInsteadOfCuttingBlockedCorner()
    {
        var map = new Map(0.5f, default, 5, 5);
        var start = map.GridToWorld(1, 1, 0.05f, 0.05f);
        var startIndex = map.GridToIndex(1, 1);
        var firstIndex = map.GridToIndex(2, 5);
        var nextIndex = map.GridToIndex(3, 8);
        var blocked = map.GridToIndex(2, 3);
        map.PixelMaxG[blocked] = -900;

        var pathfinder = new ThetaStar();
        pathfinder.Start(map, start, 1);
        pathfinder.NodeByIndex(firstIndex) = new ThetaStar.Node
        {
            GScore = 1,
            ParentIndex = startIndex,
            EnterOffset = new(-0.4f, -0.4f),
        };
        pathfinder.NodeByIndex(nextIndex) = new ThetaStar.Node
        {
            GScore = 2,
            ParentIndex = firstIndex,
            EnterOffset = new(-0.4f, -0.4f),
        };

        var waypoints = NavigationDecision.GetFirstWaypoints(pathfinder, nextIndex);
        Assert.Equal(pathfinder.NodePosition(firstIndex), waypoints.first);
        Assert.Equal(pathfinder.NodePosition(nextIndex), waypoints.second);
        var decision = new NavigationDecision { Destination = waypoints.first, PathfindDestination = waypoints.first, NextWaypoint = waypoints.second };
        Assert.True(decision.HasIntermediateWaypoint);

        var validatedEndpoint = pathfinder.NodePosition(firstIndex);
        var substitutedCellCenter = pathfinder.CellCenter(firstIndex);
        Assert.False(SegmentTouchesCell(map, start, validatedEndpoint, 2, 3));
        Assert.True(SegmentTouchesCell(map, start, substitutedCellCenter, 2, 3));
    }

    private static bool SegmentTouchesCell(Map map, WPos from, WPos to, int targetX, int targetY)
    {
        for (var i = 0; i <= 1000; ++i)
        {
            var cell = map.WorldToGrid(WPos.Lerp(from, to, i / 1000f));
            if (cell.x == targetX && cell.y == targetY)
                return true;
        }
        return false;
    }

    [Fact]
    public void EqualPriorityStillUsesPathCostAndLargerGAsTieBreak()
    {
        var map = new Map(0.5f, default, 2, 2);
        map.MaxPriority = 1;
        var pathfinder = new ThetaStar();
        pathfinder.Start(map, default, 1);

        var shorter = new ThetaStar.Node { Priority = 0.5f, Score = ThetaStar.Score.SafeBetterPrio, GScore = 1, HScore = 2 };
        var longer = new ThetaStar.Node { Priority = 0.5f, Score = ThetaStar.Score.SafeBetterPrio, GScore = 2, HScore = 2 };
        Assert.True(pathfinder.CompareNodeScores(ref shorter, ref longer) < 0);

        longer.HScore = 1;
        Assert.True(pathfinder.CompareNodeScores(ref shorter, ref longer) > 0); // equal F-score still favors larger G
    }
}
