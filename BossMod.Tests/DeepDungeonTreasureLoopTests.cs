using Xunit;

namespace BossMod.Tests;

public class DeepDungeonTreasureLoopTests
{
    private static readonly DateTime Start = new(2026, 11, 11);
    private const string Context = "POTD/121/600";
    private const ulong Chest = 1073822279;

    private static WPos Square(int frame) => (frame % 16) switch
    {
        < 4 => new(frame % 16, 0),
        < 8 => new(4, frame % 16 - 4),
        < 12 => new(12 - frame % 16, 4),
        _ => new(0, 16 - frame % 16),
    };

    private static DeepDungeonTreasureLoop CompletedLoop()
    {
        var history = new DeepDungeonTreasureLoop();
        for (int frame = 0; frame <= 16; ++frame)
        {
            // Room-owned portions have actor 0. A full circuit exceeds the former 4y leash
            // and repeatedly crosses the stationary diagnostic's 0.75y reset radius.
            var actor = frame % 16 == 0 ? Chest : 0;
            var signal = history.Observe(Start.AddMilliseconds(frame * 250), Context, actor, true,
                Square(frame), Square(frame + 1) - Square(frame));
            if (frame < 16)
                Assert.False(signal);
            else
                Assert.True(signal);
        }
        return history;
    }

    [Fact]
    public void PhysicalCircuitSurvivesRoomHandoffsWithoutStationaryOrReversalRequirement()
        => Assert.True(CompletedLoop().IsOscillating);

    [Theory]
    [InlineData("new-chest")]
    [InlineData("new-floor")]
    [InlineData("pause")]
    [InlineData("gap")]
    [InlineData("clock-rewind")]
    public void UnrelatedObjectivesControlHoldsAndMissingObservationsCannotReuseLoopEvidence(string change)
    {
        var history = CompletedLoop();
        var now = change == "gap" ? Start.AddSeconds(6) : change == "clock-rewind" ? Start : Start.AddSeconds(4.25);
        Assert.False(history.Observe(now, change == "new-floor" ? "POTD/122/600" : Context,
            change == "new-chest" ? Chest + 1 : Chest, change != "pause", Square(17), new(1, 0)));
        Assert.False(history.IsOscillating);
    }

    [Fact]
    public void StationaryDirectionsAndOrdinaryForwardTravelDoNotProveLoop()
    {
        var stationary = new DeepDungeonTreasureLoop();
        var forward = new DeepDungeonTreasureLoop();
        for (int frame = 0; frame < 100; ++frame)
        {
            var now = Start.AddMilliseconds(frame * 250);
            Assert.False(stationary.Observe(now, Context, Chest, true, default, new(frame % 2 == 0 ? 1 : -1, 0)));
            Assert.False(forward.Observe(now, Context, Chest, true, new(frame, 0), new(1, 0)));
        }
    }
}
