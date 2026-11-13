using Xunit;

namespace BossMod.Tests;

public class DeepDungeonTreasureSelectionTests
{
    private const string Context = "POTD/107/598";
    private const ulong Chest = 1073804056;
    private static readonly DateTime Start = new(2026, 11, 11);

    [Fact]
    public void RetentionRequiresPreviouslySelectedAdjacentChestInSameFloorContext()
    {
        var selection = new DeepDungeonTreasureSelection();
        Assert.False(selection.CanRetain(Context, Chest));
        selection.Select(Context, Chest, Start, false);
        Assert.False(selection.CanRetain(Context, Chest));
        selection.Select(Context, Chest, Start, true);
        Assert.True(selection.CanRetain(Context, Chest));
        Assert.False(selection.CanRetain("POTD/108/598", Chest));
        Assert.False(selection.CanRetain(Context, Chest + 1));
        Assert.False(selection.CanRetain(Context, 0));
    }

    [Fact]
    public void AdjacentAndRetainedSamplesCannotRefreshObjectiveDeadline()
    {
        var selection = new DeepDungeonTreasureSelection();
        selection.Select(Context, Chest, Start, true);
        for (int second = 1; second < 60; ++second)
        {
            var now = Start.AddSeconds(second);
            Assert.True(selection.CanRetain(Context, Chest));
            Assert.False(selection.DeadlineExpired(Context, Chest, now));
            selection.Select(Context, Chest, now, second % 2 == 0);
        }
        Assert.True(selection.DeadlineExpired(Context, Chest, Start.AddSeconds(60)));
        // Expiry is acted on through normal eligible recovery/deferral, not a silent candidate drop.
        Assert.True(selection.CanRetain(Context, Chest));
        Assert.False(selection.DeadlineExpired("POTD/108/598", Chest, Start.AddSeconds(60)));
        Assert.False(selection.DeadlineExpired(Context, Chest + 1, Start.AddSeconds(60)));
    }

    [Fact]
    public void CompletionDeferralOrSelectionOfAnotherObjectiveReleasesRetentionImmediately()
    {
        var selection = new DeepDungeonTreasureSelection();
        selection.Select(Context, Chest, Start, true);
        selection.Select(Context, 0, Start, false);
        Assert.False(selection.CanRetain(Context, Chest));
        selection.Select(Context, Chest, Start, true);
        selection.Reset(); // manual takeover and floor reset
        Assert.False(selection.CanRetain(Context, Chest));
        selection.Select(Context, Chest + 1, Start, true);
        Assert.False(selection.CanRetain(Context, Chest));
    }

    [Fact]
    public void Floor121TrajectoryBeyondOldFourYardBoundKeepsOwnerUntilFifteenSecondDeferral()
    {
        var positions = new WPos[] { new(396.6f, -358.1f), new(396.6f, -359.5f), new(400.7f, -362.7f),
            new(399.2f, -362.8f), new(397.5f, -359.1f) };
        Assert.True((positions[2] - positions[0]).Length() > 4);
        var selection = new DeepDungeonTreasureSelection();
        var lastSelected = Start;
        var lastSample = Start;
        var noProgress = TimeSpan.Zero;
        DateTime deferredAt = default;
        for (int frame = 0; frame <= 60; ++frame)
        {
            var now = Start.AddMilliseconds(frame * 250);
            var position = positions[frame % positions.Length];
            var adjacent = position.X <= 396.6f; // chest room 3 adjacent from 8, not 9
            Assert.True(adjacent || selection.CanRetain(Context, Chest));
            Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor,
                DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false));
            noProgress += DeepDungeonPolicy.TreasureSelectedSampleDuration(lastSelected, now, lastSample);
            lastSelected = lastSample = now;
            var defer = DeepDungeonPolicy.ShouldDeferStalledTreasure(noProgress, true, false, false, false, false);
            Assert.Equal(frame == 60, defer);
            selection.Select(Context, defer ? 0 : Chest, now, adjacent);
            if (defer)
                deferredAt = now;
        }
        Assert.Equal(Start.AddSeconds(15), deferredAt);
        Assert.False(selection.CanRetain(Context, Chest));
        Assert.True(DeepDungeonPolicy.IsTreasureDeferred(deferredAt, deferredAt.AddSeconds(59.999)));
        Assert.False(DeepDungeonPolicy.IsTreasureDeferred(deferredAt, deferredAt.AddSeconds(60)));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.RoomRoute,
            DeepDungeonRoomRejoinPolicy.SelectOwner(false, false, true, false));
    }
}
