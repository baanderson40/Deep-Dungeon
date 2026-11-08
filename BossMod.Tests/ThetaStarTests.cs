using BossMod;
using BossMod.Pathfinding;
using Xunit;

namespace BossMod.Tests;

public class ThetaStarTests
{
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
