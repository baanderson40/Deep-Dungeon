using Xunit;

namespace BossMod.Tests;

public class DeepDungeonPassageApproachTests
{
    private const string Context = "POTD/124/600/16/OpenPassage";

    [Fact]
    public void PassageAdmissionRequiresAdjacencyBeforeSelectionNotJustVisibility()
    {
        var selection = new DeepDungeonPolicy.PassageSelection();
        Assert.False(selection.CanAdmit("POTD/155/602", 42, false, true));
        Assert.True(selection.CanAdmit("POTD/155/602", 42, true, true));
        Assert.False(selection.CanRetain("POTD/155/602", 42)); // admission alone does not latch
        Assert.False(selection.CanAdmit("POTD/155/602", 0, true, true));
    }

    [Fact]
    public void LoggedRoomThirteenTwelveBoundaryFlappingKeepsPassageSelectionAndActorOwnership()
    {
        const string context = "POTD/155/602";
        const ulong passage = 1073853666;
        var selection = new DeepDungeonPolicy.PassageSelection();
        Assert.True(selection.CanAdmit(context, passage, true, true));
        selection.Select(context, passage);
        // Replay repeated loss of adjacency in room 12, followed by room 13.
        // Feed each frame's final selection back, as AutoClear does after PickARoom.
        for (var frame = 0; frame < 180; ++frame)
        {
            var adjacent = frame % 3 == 2;
            var candidate = selection.CanAdmit(context, passage, adjacent, true);
            var allowed = DeepDungeonPolicy.ShouldAddPassageActorGoal(true, true, false);
            var selected = candidate && allowed ? passage : 0;
            selection.Select(context, selected);
            Assert.Equal(passage, selected);
            Assert.True(selection.CanRetain(context, passage));
            Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor,
                DeepDungeonRoomRejoinPolicy.SelectOwner(false, selected != 0, true, false));
        }
        // Retention also survives a prolonged nonadjacent stretch, not just alternate frames.
        Assert.True(selection.CanAdmit(context, passage, false, true));
    }

    [Theory]
    [InlineData("POTD/156/602", 42UL)]
    [InlineData("POTD/155/603", 42UL)]
    [InlineData("HOH/155/602", 42UL)]
    [InlineData("POTD/155/602", 43UL)]
    public void PassageRetentionCannotCrossFloorZoneDungeonOrActorIdentity(string context, ulong actor)
    {
        var selection = new DeepDungeonPolicy.PassageSelection();
        selection.Select("POTD/155/602", 42);
        Assert.False(selection.CanRetain(context, actor));
        Assert.False(selection.CanAdmit(context, actor, false, true));
    }

    [Fact]
    public void PassageRetentionNeverOverridesEligibilityAndFinalSelectionClearsIt()
    {
        const string context = "POTD/155/602";
        var selection = new DeepDungeonPolicy.PassageSelection();
        selection.Select(context, 42);
        Assert.False(selection.CanAdmit(context, 42, false, false)); // inactive, destroyed, or combat
        Assert.False(selection.CanAdmit(context, 42, true, false));
        // PickARoom can revoke passage permission after the candidate list was collected.
        Assert.False(DeepDungeonPolicy.ShouldAddPassageActorGoal(true, false, false));
        Assert.False(DeepDungeonPolicy.ShouldAddPassageActorGoal(true, true, true));
        selection.Select(context, 0); // missing, excluded, disallowed, or another final goal
        Assert.False(selection.CanRetain(context, 42));
        Assert.False(selection.CanAdmit(context, 42, false, true));
        selection.Select(context, 42);
        selection = default; // floor/reset/disable lifecycle
        Assert.False(selection.CanRetain(context, 42));
    }

    [Fact]
    public void RetainedPassageDoesNotOverrideManualOrSuspendedNavigationOwnership()
    {
        var selection = new DeepDungeonPolicy.PassageSelection();
        selection.Select("POTD/155/602", 42);
        var admitted = selection.CanAdmit("POTD/155/602", 42, false, true);
        Assert.True(admitted);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Manual,
            DeepDungeonRoomRejoinPolicy.SelectOwner(true, admitted, true, false));
        Assert.NotEqual(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor,
            DeepDungeonRoomRejoinPolicy.SelectOwner(false, admitted, false, false));
    }

    [Theory]
    [InlineData(81.7f, 0, 0, false)]
    [InlineData(65.2f, 0, 42, false)]
    [InlineData(5.9f, 0, 0, true)]
    [InlineData(6.1f, 0, 0, false)]
    [InlineData(9, 0, 42, true)]
    [InlineData(9, 0, 43, false)]
    [InlineData(10.1f, 0, 42, false)]
    [InlineData(2, 4, 0, false)]
    [InlineData(float.NaN, 0, 0, false)]
    public void PassageStaysActorOwnedWhileOnlyLocalAssemblyUsesHeightAndExitHysteresis(float distance, float height, ulong locked, bool local)
    {
        var useLocal = DeepDungeonPartyPolicy.UseLocalPassageGoal(42, locked, distance, height);
        Assert.Equal(local, useLocal);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor,
            DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false));
    }

    [Fact]
    public void CommittedLeaderCannotStartAssemblyOrSpendReplansSixtyFiveYardsAway()
    {
        var assembly = new DeepDungeonPartyPolicy.PassageAssembly();
        for (long now = 1000; now <= 65000; now += 250)
        {
            Assert.False(assembly.Update(now, 42, true, 65.2f, 0.02f, true, false));
            Assert.False(assembly.ObserveProgress(now, 65.2f, false));
        }
        Assert.Equal(0UL, assembly.Passage);
        Assert.Equal(0, assembly.Replans);
        Assert.False(assembly.Blocked);
        Assert.True(assembly.Update(65250, 42, true, 5.9f, 0.02f, true, false));
        Assert.Equal(42UL, assembly.Passage);
    }

    [Fact]
    public void PassageActorCannotArmGeneralRoomRecoveryEvenOutsideRoomCorridor()
    {
        var rejoin = new DeepDungeonRoomRejoinPolicy();
        var player = new WPos(15, 40);
        var source = new WPos(0, 0);
        var next = new WPos(60, 0);
        var size = new WDir(20, 20);
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
        Assert.False(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner));
        for (long now = 1000; now <= 65000; now += 250)
            Assert.False(rejoin.Step(now, Context, owner, true, true, player, source, size, next, size));
        Assert.Equal(0, rejoin.Attempts);
        Assert.Equal(DeepDungeonRoomRejoinPolicy.Phase.Idle, rejoin.State);
    }

    [Theory]
    [InlineData(81.7f)]
    [InlineData(65.2f)]
    [InlineData(34.2f)]
    [InlineData(32.7f)]
    [InlineData(25)]
    [InlineData(6.1f)]
    public void DistantPassageGoalsMatchHistoricalRangesWeightsAndProgressCutoffs(float distance)
    {
        var target = new WPos(0, 0);
        var player = new WPos(distance, 0);
        foreach (var weight in new float[] { 0.01f, 1 })
        {
            var goals = DeepDungeonPolicy.PassageApproachGoals(target, player, weight);
            // Exact 06dee0844 formula, independently specified rather than inferred from new helpers.
            var arrival = AIHints.GoalSingleTarget(target, 2, weight);
            var approach = AIHints.GoalSingleTarget(target, 25, weight);
            var progress = DeepDungeonPolicy.PassageProgressGoal(target, player, 25, 100, 1, weight);
            foreach (var x in new float[] { -101, -25.1f, -2.1f, 0, 1.9f, 2.1f, 24.9f, 25.1f, distance - 1, distance, 99, 101 })
            {
                var point = new WPos(x, 0);
                Assert.Equal(arrival(point), goals.Arrival(point));
                Assert.Equal(approach(point), goals.Approach(point));
                Assert.Equal(progress(point), goals.Progress(point));
            }
        }
    }

    [Fact]
    public void LocalReplansChangeGoalPeakWithoutLeavingSafeInnerAssemblyRadius()
    {
        var offset = DeepDungeonPartyPolicy.PassageReplanOffset;
        var centers = new WPos[] { default, new(offset, 0), new(0, offset), new(-offset, 0) };
        Assert.True(offset + DeepDungeonPartyPolicy.PassageGoalRadius + 0.1f < DeepDungeonPartyPolicy.PassageInnerRadius);
        for (int replan = 0; replan <= 3; ++replan)
        {
            var goal = DeepDungeonPartyPolicy.PassageAssemblyGoal(default, 1, replan);
            Assert.Equal(2, goal(centers[replan]));
            if (replan != 0)
                Assert.True(goal(centers[replan]) > goal(default));
            for (int x = -12; x <= 12; ++x)
                for (int z = -12; z <= 12; ++z)
                {
                    var point = new WPos(x / 10f, z / 10f);
                    if (goal(point) > 0)
                        Assert.True(point.InCircle(default, DeepDungeonPartyPolicy.PassageInnerRadius));
                }
        }
    }

    [Fact]
    public void ReachedPassageRoomClearsObjectiveWithoutLosingPassageActorOwnership()
    {
        var target = new WPos(0, 0);
        var player = new WPos(32.7f, 0);
        for (int frame = 0; frame < 10; ++frame)
        {
            Assert.True(DeepDungeonPolicy.ShouldClearReachedRoomDestination(false, true, true, true));
            Assert.True(DeepDungeonPolicy.ShouldAddPassageActorGoal(false, false, false));
            var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, true, false);
            Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor, owner);
            Assert.False(DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner));
            var goals = DeepDungeonPolicy.PassageApproachGoals(target, player, 0.01f);
            Assert.True(goals.Approach(new(24, 0)) > 0);
            Assert.True(goals.Progress(new(24, 0)) > 0);
        }
        Assert.False(DeepDungeonPolicy.ShouldClearReachedRoomDestination(true, true, true, false));
    }

    [Fact]
    public void PassageOwnerYieldsToManualControlAndPullReadiness()
    {
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Manual,
            DeepDungeonRoomRejoinPolicy.SelectOwner(true, true, true, false));
        Assert.Equal(DeepDungeonRoomRejoinPolicy.NavigationOwner.Suspended,
            DeepDungeonRoomRejoinPolicy.SelectOwner(false, true, false, false));
    }
}
