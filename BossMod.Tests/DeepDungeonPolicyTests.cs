using BossMod;
using BossMod.Global.DeepDungeon;
using System.Numerics;
using Xunit;

namespace BossMod.Tests;

public class DeepDungeonPolicyTests
{
    private static bool[] FullRoomSet() => Enumerable.Repeat(true, 25).ToArray();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void NavigationRequiresEnableOrManualDestination(bool enabled, bool manual, bool expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ShouldNavigate(enabled, manual));

    [Fact]
    public void FloorInitializationRequiresDungeonFloorAndMapReadiness()
    {
        Assert.False(DeepDungeonPolicy.CanInitializeFloor(0, 0, true));
        Assert.False(DeepDungeonPolicy.CanInitializeFloor(0, 31, true));
        Assert.False(DeepDungeonPolicy.CanInitializeFloor(1, 0, true));
        Assert.False(DeepDungeonPolicy.CanInitializeFloor(1, 31, false));
        Assert.True(DeepDungeonPolicy.CanInitializeFloor(1, 31, true));
    }

    [Fact]
    public void FloorTransitionGateRejectsStaleMapUntilContextAndFreshMapArrive()
    {
        var gate = new DeepDungeonPolicy.FloorInitializationGate();

        // Module attach can use already initialized context/map.
        Assert.True(gate.CanInitialize(true, true, mapDataUpdate: false));
        gate.MarkInitialized();

        // Transference can leave old floor data visible for several frames.
        gate.BeginTransition();
        Assert.False(gate.CanInitialize(true, true, mapDataUpdate: false));
        Assert.False(gate.CanInitialize(true, true, mapDataUpdate: true));

        // New floor progress alone is insufficient; wait for its map-data event.
        gate.ObserveContextChange();
        Assert.False(gate.CanInitialize(true, true, mapDataUpdate: false));
        Assert.True(gate.CanInitialize(true, true, mapDataUpdate: true));

        gate.MarkInitialized();
        Assert.True(gate.CanInitialize(true, true, mapDataUpdate: false));
    }

    [Fact]
    public void GeometryReadinessAcceptsSupportedInitializedLayout()
    {
        var rooms = FullRoomSet();
        var boxes = FullRoomSet();

        Assert.True(DeepDungeonPolicy.IsGeometryReady(1, 2, 25, 25, rooms, boxes, out var reason));
        Assert.Equal("ready", reason);
    }

    [Theory]
    [InlineData(2, 2, 25, 25, "no geometry")]
    [InlineData(0, 2, 24, 25, "map room count")]
    [InlineData(0, 2, 25, 25, "no initialized rooms")]
    [InlineData(0, 2, 25, 24, "rectangle count")]
    public void GeometryReadinessRejectsMissingOrInvalidLayout(int tileset, int layouts, int mapRooms, int rectCount, string expectedReason)
    {
        var rooms = mapRooms == 25 ? FullRoomSet() : new bool[Math.Max(mapRooms, 0)];
        if (expectedReason != "no initialized rooms")
            Array.Fill(rooms, true);
        else
            Array.Clear(rooms);
        var boxes = Enumerable.Repeat(true, Math.Max(rectCount, 0)).ToArray();

        Assert.False(DeepDungeonPolicy.IsGeometryReady(tileset, layouts, mapRooms, 25, rooms, boxes, out var reason));
        Assert.Contains(expectedReason, reason);
    }

    [Fact]
    public void GeometryReadinessRejectsActiveRoomWithoutValidBox()
    {
        var rooms = FullRoomSet();
        var boxes = FullRoomSet();
        boxes[7] = false;

        Assert.False(DeepDungeonPolicy.IsGeometryReady(0, 1, 25, 25, rooms, boxes, out var reason));
        Assert.Contains("7", reason);
    }

    [Fact]
    public void RoomRouteGoalOnlyRewardsProgressTowardNextConnectedRoom()
    {
        var source = new WPos(0, 0);
        var next = new WPos(20, 0);

        Assert.True(DeepDungeonPolicy.IsInForwardRoomRoute(new(10, 0), source, next, 0, 10));
        Assert.True(DeepDungeonPolicy.IsInForwardRoomRoute(new(20, 10), source, next, 0, 10));
        Assert.False(DeepDungeonPolicy.IsInForwardRoomRoute(new(-1, 0), source, next, 0, 10));
        Assert.False(DeepDungeonPolicy.IsInForwardRoomRoute(new(21, 0), source, next, 0, 10));
        Assert.False(DeepDungeonPolicy.IsInForwardRoomRoute(new(10, 10.1f), source, next, 0, 10));
    }

    [Fact]
    public void RoomRouteGoalRejectsDegenerateRoomCenters()
        => Assert.False(DeepDungeonPolicy.IsInForwardRoomRoute(default, default, default, 0, 10));

    [Fact]
    public void RoomRouteGoalCoversLocalMapWhenApproachingSourceRoomFromHallway()
    {
        var source = new WPos(-304.3f, 355.0f);
        var next = new WPos(-308.3f, 300.8f);
        var player = new WPos(-311.4f, 386.8f);
        var direction = (next - source).Normalized();
        var playerProgress = (player - source).Dot(direction);
        var localForwardPoint = player + direction * 16;
        var behindPlayer = player - direction * 1;

        Assert.True(playerProgress < 0);
        Assert.True(DeepDungeonPolicy.IsInForwardRoomRoute(localForwardPoint, source, next, playerProgress, 10));
        Assert.False(DeepDungeonPolicy.IsInForwardRoomRoute(behindPlayer, source, next, playerProgress, 10));

        // Reproduce logged 120x120 map around player and the existing player-relative exclusion.
        var map = new BossMod.Pathfinding.Map(0.5f, new(-311.5f, 386.5f), 30, 30);
        var routeCardinal = new WDir(0, -1);
        BossMod.Pathfinding.NavigationDecision.RasterizeGoalZones(map,
        [
            p => !p.InRect(player, routeCardinal, 10, 100, 100)
                && DeepDungeonPolicy.IsInForwardRoomRoute(p, source, next, playerProgress, 10) ? 0.01f : 0
        ], false);

        Assert.Contains(map.PixelPriority, priority => priority > 0);
    }

    [Theory]
    [InlineData(20, 0, 10, 0)]
    [InlineData(-20, 0, -10, 0)]
    [InlineData(0, 20, 0, 10)]
    [InlineData(0, -20, 0, -10)]
    public void RoomRouteGoalSupportsAllCardinalDirections(float nextX, float nextZ, float pointX, float pointZ)
        => Assert.True(DeepDungeonPolicy.IsInForwardRoomRoute(new(pointX, pointZ), default, new(nextX, nextZ), 0, 2));

    [Fact]
    public void AutomaticPomanderRequiresValidUsableStockAndPolicyEligibility()
    {
        AssertAutoPomRejected(validDungeon: false, reason: "invalid dungeon");
        AssertAutoPomRejected(validSlot: false, reason: "slot unavailable");
        AssertAutoPomRejected(stock: 0, reason: "no stock");
        AssertAutoPomRejected(usable: false, reason: "unusable");
        AssertAutoPomRejected(solo: false, reason: "not solo");
        AssertAutoPomRejected(itemPenalty: true, reason: "Item Penalty");
        AssertAutoPomRejected(enabledByConfig: false, reason: "configuration");
        AssertAutoPomRejected(requiresPox: true, hasPox: false, reason: "Pox");

        Assert.True(DeepDungeonPolicy.CanAutoUsePomander(true, true, 1, true, true, false, true, false, false, out var eligibleReason));
        Assert.Equal("eligible", eligibleReason);
        Assert.True(DeepDungeonPolicy.CanAutoUsePomander(true, true, 1, true, true, false, true, true, true, out _));
    }

    [Fact]
    public void OvercapAttributionMatchesRecentCompatibleInteractionCall()
    {
        var messageAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
        DeepDungeonInteractionHistory.TreasureCall[] calls =
        [
            new(123, 0x1EA13E, 1, 31, messageAt.AddSeconds(-2), 1),
            new(456, 0x1EA13D, 1, 31, messageAt.AddSeconds(-1), 2),
        ];

        var result = DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, calls);

        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.Matched, result.Status);
        Assert.Equal((ulong)123, result.ActorID);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(TimeSpan.FromSeconds(2), result.EvidenceAge);
    }

    [Theory]
    [InlineData(ActorType.EventObj, DeepDungeonInteractionHistory.GoldCofferOID, true)]
    [InlineData(ActorType.EventObj, DeepDungeonInteractionHistory.SilverCofferOID, true)]
    [InlineData(ActorType.EventObj, 0x123456, false)]
    [InlineData(ActorType.Treasure, 0x123456, true)]
    public void CofferInteractionEvidenceAcceptsCofferOIDsRegardlessOfActorType(ActorType type, uint objectID, bool expected)
        => Assert.Equal(expected, DeepDungeonInteractionHistory.IsTreasureInteractionTarget(type, objectID));

    [Fact]
    public void EventObjectCofferInteractionCorrelatesToGoldOvercap()
    {
        var messageAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
        var history = new DeepDungeonInteractionHistory();
        Assert.True(DeepDungeonInteractionHistory.IsTreasureInteractionTarget(ActorType.EventObj, DeepDungeonInteractionHistory.GoldCofferOID));
        history.RecordTreasureCall(123, DeepDungeonInteractionHistory.GoldCofferOID, 1, 31, messageAt.AddSeconds(-1));

        var result = DeepDungeonPolicy.CorrelateOvercapMessage(
            messageAt, 1, 31, DeepDungeonInteractionHistory.GoldCofferOID, history.Snapshot(messageAt));

        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.Matched, result.Status);
        Assert.Equal((ulong)123, result.ActorID);
    }

    [Fact]
    public void OvercapAttributionAllowsRepeatedCallsToSameChestButRejectsMultipleChestCandidates()
    {
        var messageAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
        DeepDungeonInteractionHistory.TreasureCall[] repeatedSameChest =
        [
            new(123, 0x1EA13E, 1, 31, messageAt.AddSeconds(-2), 1),
            new(123, 0x1EA13E, 1, 31, messageAt.AddSeconds(-1), 2),
        ];
        var matched = DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, repeatedSameChest);
        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.Matched, matched.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), matched.EvidenceAge);
        Assert.Equal(2, matched.InteractionSequence);
        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.NoRecentInteraction,
            DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, repeatedSameChest, matched.InteractionSequence).Status);

        DeepDungeonInteractionHistory.TreasureCall[] twoChests =
        [
            new(123, 0x1EA13E, 1, 31, messageAt.AddSeconds(-2), 1),
            new(456, 0x1EA13E, 1, 31, messageAt.AddSeconds(-1), 2),
        ];
        var ambiguous = DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, twoChests);
        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.CandidateCount);
        Assert.Equal(new ulong[] { 123, 456 }, ambiguous.CandidateActorIDs);
    }

    [Theory]
    [InlineData(1, 30, 0x1EA13E, "WrongFloorContext")]
    [InlineData(1, 31, 0x1EA13D, "IncompatibleChest")]
    [InlineData(1, 31, 0x1EA13E, "ExpiredOrFutureInteraction")]
    public void OvercapAttributionRejectsWrongContextTypeOrTiming(int callDungeon, byte callFloor, uint callObjectID, string expectedStatus)
    {
        var messageAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
        var callAt = expectedStatus == "ExpiredOrFutureInteraction" ? messageAt.AddSeconds(-6) : messageAt.AddSeconds(-1);
        DeepDungeonInteractionHistory.TreasureCall[] calls = [new(123, callObjectID, callDungeon, callFloor, callAt, 1)];

        var result = DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, calls);

        Assert.Equal(expectedStatus, result.Status.ToString());
        Assert.Equal((ulong)0, result.ActorID);
    }

    [Fact]
    public void OvercapAttributionWithoutInteractionEvidenceIsUnattributed()
    {
        var result = DeepDungeonPolicy.CorrelateOvercapMessage(
            new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc), 1, 31, 0x1EA13E, []);
        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.NoRecentInteraction, result.Status);
        Assert.Equal(0, result.CandidateCount);
    }

    [Fact]
    public void OvercapAttributionRejectsCallsAfterMessageAndHistoryIsBounded()
    {
        var messageAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);
        DeepDungeonInteractionHistory.TreasureCall[] futureCall = [new(123, 0x1EA13E, 1, 31, messageAt.AddMilliseconds(1), 1)];
        Assert.Equal(DeepDungeonPolicy.OvercapAttributionStatus.ExpiredOrFutureInteraction,
            DeepDungeonPolicy.CorrelateOvercapMessage(messageAt, 1, 31, 0x1EA13E, futureCall).Status);

        var history = new DeepDungeonInteractionHistory();
        for (var i = 1; i <= DeepDungeonInteractionHistory.Capacity + 3; ++i)
            history.RecordTreasureCall((ulong)i, 0x1EA13E, 1, 31, messageAt.AddMilliseconds(i));
        Assert.Equal(DeepDungeonInteractionHistory.Capacity, history.Snapshot(messageAt.AddMilliseconds(35)).Length);
        Assert.Empty(history.Snapshot(messageAt.AddSeconds(6)));
        history.Clear();
        Assert.Empty(history.Snapshot(messageAt.AddSeconds(6)));
    }

    [Fact]
    public void PomanderInitializationZerosBecomeBaselineButLaterStockChangesRemainVisible()
    {
        var baselineReady = false;

        bool Observe(bool validContext, bool anyPomanderUsable)
        {
            if (!DeepDungeonPolicy.ShouldReportPomanderChange(validContext, baselineReady))
            {
                baselineReady = DeepDungeonPolicy.ShouldEstablishPomanderBaseline(validContext, baselineReady, anyPomanderUsable);
                return false;
            }
            return true;
        }

        Assert.False(Observe(false, false)); // outside DD: don't interpret stale readback
        Assert.False(Observe(true, false));  // transient all-unusable initialization snapshot
        Assert.False(Observe(true, true));   // first usable state establishes baseline
        Assert.True(baselineReady);
        Assert.True(Observe(true, true));    // later genuine stock/flag changes are observable

        baselineReady = false;              // new DD context invalidates baseline
        Assert.False(Observe(true, true));   // first state in new context is baseline only
        Assert.True(baselineReady);
    }

    [Fact]
    public void ReachedRoomDoesNotChurnDestinationWhileMapRevealIsDelayed()
    {
        for (var frame = 0; frame < 20; ++frame)
        {
            var selectedRoom = 0;
            if (DeepDungeonPolicy.ShouldSelectRoomDestination(true, true, false))
                selectedRoom = 7; // full-clear candidate still ahead
            if (DeepDungeonPolicy.ShouldSelectRoomDestination(true, true, true))
                selectedRoom = 12; // passage candidate is already occupied

            Assert.Equal(7, selectedRoom);
        }

        // Once the player reaches the remaining candidate, neither stale nor updated map state
        // should create another room-navigation objective from the room they already occupy.
        Assert.False(DeepDungeonPolicy.ShouldSelectRoomDestination(true, true, true));
        Assert.False(DeepDungeonPolicy.ShouldSelectRoomDestination(true, false, true));
        Assert.False(DeepDungeonPolicy.ShouldSelectRoomDestination(true, false, false)); // delayed reveal has now arrived
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void FullClearDestinationIsKeptOnlyWhileRoomRemainsPending(bool enabled, bool active, bool revealed, bool expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ShouldKeepFullClearDestination(enabled, active, revealed));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void BronzeTreasureRequiresTargetableUnopenedActor(bool targetable, bool opened, bool expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ShouldConsiderBronzeTreasure(targetable, opened));

    [Theory]
    [InlineData(false, 0.02f)]
    [InlineData(true, 1f)]
    public void TreasureApproachWeightBreaksOutOfCombatRouteTieWithoutChangingCombatWeight(bool inCombat, float expected)
        => Assert.Equal(expected, DeepDungeonPolicy.TreasureApproachWeight(inCombat));

    [Theory]
    [InlineData(false, false, false, true)]  // no room objective: actor goal can guide to active passage
    [InlineData(true, true, false, true)]    // room objective is passage itself: goals are compatible
    [InlineData(true, false, false, false)]  // exploration/other room objective owns movement
    [InlineData(false, false, true, false)]  // manual destination always owns movement
    [InlineData(true, true, true, false)]    // manual destination takes precedence even if room reason matches passage
    public void PassageActorGoalDoesNotCompeteWithOtherNavigationOwner(bool hasRoomDestination, bool headingToPassage, bool manual, bool expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ShouldAddPassageActorGoal(hasRoomDestination, headingToPassage, manual));

    [Fact]
    public void PassageProgressGoalRewardsCloserLocalPositionsWithoutRewardingStandingStill()
    {
        var player = new WPos(0, 0);
        var passage = new WPos(72.5f, 0);
        var goal = DeepDungeonPolicy.PassageProgressGoal(passage, player, 25, 100, 1, 0.01f);

        Assert.Equal(0, goal(player));
        Assert.Equal(0.01f, goal(new WPos(10, 0)));
        Assert.Equal(0.01f, goal(new WPos(50, 0)));
        Assert.Equal(0, goal(new WPos(-10, 0)));
        Assert.Equal(0, goal(new WPos(180, 0)));
    }

    [Fact]
    public void PassageProgressGoalDefersToArrivalGoalWhenAlreadyNearPassage()
    {
        var player = new WPos(55, 0);
        var passage = new WPos(72.5f, 0);
        var goal = DeepDungeonPolicy.PassageProgressGoal(passage, player, 25, 100, 1, 0.01f);

        Assert.Equal(0, goal(new WPos(55, 0)));
        Assert.Equal(0, goal(new WPos(60, 0)));
    }

    [Fact]
    public void DiagnosticSampleBufferRemainsBounded()
    {
        var samples = new Queue<int>([1, 2, 3, 4]);
        Assert.Equal(2, DeepDungeonPolicy.TrimDiagnosticSamples(samples, 2));
        Assert.Equal(new[] { 3, 4 }, samples.ToArray());
        Assert.Equal(2, DeepDungeonPolicy.TrimDiagnosticSamples(samples, 0));
        Assert.Empty(samples);
    }

    [Fact]
    public void RouteDiagnosticsCountDirectionReversalsAndIgnoreZeroSamples()
    {
        WDir[] directions = [new(1, 0), default, new(0.8f, 0.2f), new(-1, 0), new(-0.5f, 1)];
        Assert.Equal(1, DeepDungeonPolicy.CountDirectionReversals(directions));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void MovementIneligiblePassageCannotDisplaceOtherActorGoal(bool passageActor, bool passageGoalAllowed, bool expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ShouldConsiderActorForNavigation(passageActor, passageGoalAllowed));

    [Fact]
    public void ManualNativeDispatchIsNotSubjectToAutomaticPomanderEligibility()
    {
        // Native dispatch only validates runtime context and slot. Stock, usability, party,
        // configuration, penalty, and Pox are automatic policy gates, not manual-use gates.
        Assert.Equal(DeepDungeonPolicy.PomanderDispatchStatus.Issued,
            DeepDungeonPolicy.GetPomanderDispatchStatus(false, true, true, true));
    }

    [Theory]
    [InlineData(true, true, true, true, (int)DeepDungeonPolicy.PomanderDispatchStatus.AnimationLocked)]
    [InlineData(false, false, true, true, (int)DeepDungeonPolicy.PomanderDispatchStatus.InvalidDungeonOrAction)]
    [InlineData(false, true, false, true, (int)DeepDungeonPolicy.PomanderDispatchStatus.MissingDirectorOrSlot)]
    [InlineData(false, true, true, false, (int)DeepDungeonPolicy.PomanderDispatchStatus.MissingDirectorOrSlot)]
    [InlineData(false, true, true, true, (int)DeepDungeonPolicy.PomanderDispatchStatus.Issued)]
    public void PomanderDispatchResultReflectsNativeRequestPrerequisites(bool locked, bool validAction, bool director, bool slot, int expected)
        => Assert.Equal((DeepDungeonPolicy.PomanderDispatchStatus)expected, DeepDungeonPolicy.GetPomanderDispatchStatus(locked, validAction, director, slot));

    [Fact]
    public void ReadbackCorrelationIsBoundedAndRejectsFutureRequestTimestamp()
    {
        var requested = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(DeepDungeonPolicy.IsWithinPomanderReadbackWindow(requested, requested.AddSeconds(15)));
        Assert.False(DeepDungeonPolicy.IsWithinPomanderReadbackWindow(requested, requested.AddSeconds(15).AddTicks(1)));
        Assert.False(DeepDungeonPolicy.IsWithinPomanderReadbackWindow(requested, requested.AddTicks(-1)));
    }

    [Fact]
    public void InteractionRequestSummariesAreRateLimitedToFiveSeconds()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(DeepDungeonPolicy.ShouldSummarizeInteractionRequests(default, now));
        Assert.False(DeepDungeonPolicy.ShouldSummarizeInteractionRequests(now, now.AddSeconds(4.99)));
        Assert.True(DeepDungeonPolicy.ShouldSummarizeInteractionRequests(now, now.AddSeconds(5)));
    }

    [Fact]
    public void ProgressDiagnosticsUseMeaningfulDeltaAndBoundedStallInterval()
    {
        Assert.False(DeepDungeonPolicy.HasMeaningfulProgress(10, 9.3f));
        Assert.True(DeepDungeonPolicy.HasMeaningfulProgress(10, 9.25f));

        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(DeepDungeonPolicy.ShouldLogStall(TimeSpan.FromSeconds(9.99), default, now));
        Assert.True(DeepDungeonPolicy.ShouldLogStall(TimeSpan.FromSeconds(10), default, now));
        Assert.False(DeepDungeonPolicy.ShouldLogStall(TimeSpan.FromSeconds(40), now, now.AddSeconds(29.99)));
        Assert.True(DeepDungeonPolicy.ShouldLogStall(TimeSpan.FromSeconds(40), now, now.AddSeconds(30)));
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, false, 0, 0, "NavigationOscillation")]
    [InlineData(false, true, false, false, false, false, false, 0, 0, "BossFloorIdle")]
    [InlineData(false, false, true, false, false, true, false, 0, 0, "InteractionHold")]
    [InlineData(false, false, true, false, false, true, true, 0, 0, "InteractionHold")]
    [InlineData(false, false, false, true, false, true, false, 0, 1, "CastingHold")]
    [InlineData(false, false, false, false, true, false, false, 1.5, 0, "CombatPositioningSatisfied")]
    [InlineData(false, false, false, false, true, true, true, 1.5, 0, "NavigationStall")]
    [InlineData(false, false, false, false, false, true, true, 0, 0.2, "NavigationStall")]
    [InlineData(false, false, false, false, false, false, false, 0, 0, "NoNavigationDestination")]
    public void MovementDiagnosticClassificationDistinguishesExpectedHolds(
        bool oscillating, bool bossFloor, bool interactionSuppressed, bool castingSuppressed, bool inCombat, bool hasDestination, bool movementIssued,
        float goalHere, float goalDestination, string expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ClassifyMovementDiagnostic(oscillating, bossFloor, interactionSuppressed, castingSuppressed, inCombat, hasDestination, movementIssued, goalHere, goalDestination).ToString());

    [Theory]
    [InlineData(false, false, false, false, true, "hazards present; blockage unproven")]
    [InlineData(true, false, false, false, true, "pull limit")]
    [InlineData(false, true, false, true, true, "interaction hold")]
    [InlineData(false, false, false, true, true, "combat hold")]
    [InlineData(false, false, false, false, false, "no meaningful distance progress")]
    public void ObjectiveBlockerDoesNotClaimHazardsBlockedMovement(bool pullLimit, bool interaction, bool casting, bool inCombat, bool hazards, string expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ObjectiveStallBlocker(pullLimit, interaction, casting, inCombat, hazards));

    [Theory]
    [InlineData(true, "Treasure", true, "room objective with treasure detour")]
    [InlineData(true, "Hoard", true, "room objective with hoard detour")]
    [InlineData(true, "Treasure", false, "room objective; treasure candidate pending")]
    [InlineData(true, "Hoard", false, "room objective; hoard candidate pending")]
    [InlineData(true, "Passage", true, "room objective")]
    [InlineData(false, "Treasure", true, "actor objective (Treasure)")]
    [InlineData(false, "Treasure", false, "actor candidate pending (Treasure)")]
    [InlineData(false, null, false, "none")]
    public void ObjectiveOwnerDistinguishesActiveDetourFromPendingCandidate(bool hasRoomObjective, string? actorGoal, bool active, string expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ObjectiveNavigationOwner(hasRoomObjective, actorGoal, active));

    [Fact]
    public void ObjectivePathSnapshotRequestedOnlyForUnownedOutOfCombatRoomStall()
    {
        Assert.True(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, false, false, false, false, false));
        Assert.False(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, true, false, false, false, false));
        Assert.False(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, false, true, false, false, false));
        Assert.False(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, false, false, true, false, false));
        Assert.False(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, false, false, false, true, false));
        Assert.False(DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(true, false, false, false, false, true));
    }

    [Theory]
    [InlineData(false, false, "previous-candidate-absent")]
    [InlineData(true, false, "previous-candidate-filtered")]
    [InlineData(true, true, "previous-candidate-still-eligible")]
    public void ActorSelectionDiagnosticsDescribePreviousCandidateState(bool inCandidates, bool eligible, string expected)
        => Assert.Equal(expected, DeepDungeonPolicy.ActorSelectionTransitionReason(inCandidates, eligible));

    [Fact]
    public void BossFloorTilesetIsNotReportedAsOrdinaryGeometryInput()
    {
        Assert.Equal("n/a (boss floor; raw=255)", DeepDungeonPolicy.DiagnosticTileset(true, 255));
        Assert.Equal("1", DeepDungeonPolicy.DiagnosticTileset(false, 1));
    }

    [Fact]
    public void PalantirTrapInputsDistinguishUnavailableNullEmptyAndFiltered()
    {
        var nullResponse = PalantirTrapData.Process(null, _ => false);
        Assert.Equal(HistoricalTrapInputState.NullResponse, nullResponse.State);
        Assert.Equal(0, nullResponse.Returned);

        var empty = PalantirTrapData.Process([], _ => false);
        Assert.Equal(HistoricalTrapInputState.Empty, empty.State);
        Assert.Equal(0, empty.Accepted);

        var filtered = PalantirTrapData.Process([new Vector3(1, 0, 1)], _ => true);
        Assert.Equal(HistoricalTrapInputState.FilteredOut, filtered.State);
        Assert.Equal(1, filtered.Problematic);
        Assert.Empty(filtered.Locations);

        var available = PalantirTrapData.Process([new Vector3(1, 0, 1)], _ => false);
        Assert.Equal(HistoricalTrapInputState.Available, available.State);
        Assert.Single(available.Locations);
    }

    [Fact]
    public void PalantirPrefersCurrentEndpointAndUsesLegacyOnlyWhenUnregistered()
    {
        var legacyCalls = 0;
        var current = PalantirTrapData.Fetch(
            () => new List<Vector3> { new(1, 0, 1) },
            () => { ++legacyCalls; return Array.Empty<Vector3>(); },
            error => error.Message == "not registered");
        Assert.Equal("Palantir.GetTraps", current.Provider);
        Assert.Null(current.Error);
        Assert.Equal(0, legacyCalls);

        var legacy = PalantirTrapData.Fetch(
            () => throw new InvalidOperationException("not registered"),
            () => { ++legacyCalls; return [new Vector3(2, 0, 2)]; },
            error => error.Message == "not registered");
        Assert.Equal("Palantir.Traps (legacy)", legacy.Provider);
        Assert.Null(legacy.Error);
        Assert.Single(PalantirTrapData.Process(legacy.Response, _ => false).Locations);
        Assert.Equal(1, legacyCalls);
    }

    [Fact]
    public void PalantirDoesNotFallBackForSuccessfulNullEmptyOrProviderErrors()
    {
        var legacyCalls = 0;
        var nullResponse = PalantirTrapData.Fetch(
            () => null,
            () => { ++legacyCalls; return Array.Empty<Vector3>(); },
            error => error.Message == "not registered");
        Assert.Equal("Palantir.GetTraps", nullResponse.Provider);
        Assert.Null(nullResponse.Error);
        Assert.Equal(HistoricalTrapInputState.NullResponse, PalantirTrapData.Process(nullResponse.Response, _ => false).State);

        var emptyResponse = PalantirTrapData.Fetch(
            () => new List<Vector3>(),
            () => { ++legacyCalls; return Array.Empty<Vector3>(); },
            error => error.Message == "not registered");
        Assert.Equal(HistoricalTrapInputState.Empty, PalantirTrapData.Process(emptyResponse.Response, _ => false).State);
        Assert.Equal(0, legacyCalls);

        var providerError = PalantirTrapData.Fetch(
            () => throw new InvalidOperationException("provider failed"),
            () => { ++legacyCalls; return Array.Empty<Vector3>(); },
            error => error.Message == "not registered");
        Assert.Equal("Palantir.GetTraps", providerError.Provider);
        Assert.NotNull(providerError.Error);
        Assert.False(providerError.Unavailable);
        Assert.Equal(0, legacyCalls);
    }

    [Fact]
    public void PalantirReportsUnavailableWhenBothEndpointsAreUnregistered()
    {
        var fetch = PalantirTrapData.Fetch(
            () => throw new InvalidOperationException("not registered"),
            () => throw new InvalidOperationException("not registered"),
            error => error.Message == "not registered");

        Assert.Equal("Palantir.Traps (legacy)", fetch.Provider);
        Assert.True(fetch.Unavailable);
        Assert.Equal("not registered", fetch.Error?.Message);
    }

    [Fact]
    public void PalantirTrapInputRejectsNonfiniteCoordinatesBeforeProblemLocationFiltering()
    {
        var input = PalantirTrapData.Process(
            [new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(1, 0, 1)],
            location => location.X == 1);

        Assert.Equal(HistoricalTrapInputState.FilteredOut, input.State);
        Assert.Equal(3, input.Returned);
        Assert.Equal(2, input.Invalid);
        Assert.Equal(1, input.Problematic);
        Assert.Empty(input.Locations);
    }

    [Fact]
    public void PalantirFloorFilterReportsRejectedCandidatesWithoutCallingThemSafe()
    {
        var input = PalantirTrapData.FilterFloor(
            [new Vector3(1, 0, 1), new Vector3(20, 0, 20), new Vector3(2, 0, 2)],
            location => location.X < 10);

        Assert.Equal(2, input.Locations.Count);
        Assert.Equal(1, input.FilteredOut);
        Assert.Equal(new Vector3(1, 0, 1), input.Locations[0]);
        Assert.Equal(new Vector3(2, 0, 2), input.Locations[1]);
    }

    [Fact]
    public void PalantirDiagnosticsLogProviderTransitionsAndThrottleRepeatedErrors()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(PalantirTrapData.ShouldLogProviderState(null, "Unavailable:1", true, default, now));
        Assert.False(PalantirTrapData.ShouldLogProviderState("Unavailable:1", "Unavailable:1", true, now, now.AddSeconds(29)));
        Assert.True(PalantirTrapData.ShouldLogProviderState("Unavailable:1", "Unavailable:1", true, now, now.AddSeconds(30)));
        Assert.True(PalantirTrapData.ShouldLogProviderState("Unavailable:1", "Available:1", false, now, now.AddSeconds(1)));
        Assert.True(PalantirTrapData.ShouldLogProviderState("Available:1", "Unavailable:1", true, now, now.AddSeconds(1)));

        var summary = PalantirTrapData.ErrorSummary(new InvalidOperationException(new string('x', 200) + "\nsecond line"));
        Assert.Equal(160, summary.Length);
        Assert.DoesNotContain('\n', summary);
    }

    private static void AssertAutoPomRejected(
        bool validDungeon = true,
        bool validSlot = true,
        byte stock = 1,
        bool usable = true,
        bool solo = true,
        bool itemPenalty = false,
        bool enabledByConfig = true,
        bool requiresPox = false,
        bool hasPox = false,
        string reason = "")
    {
        Assert.False(DeepDungeonPolicy.CanAutoUsePomander(validDungeon, validSlot, stock, usable, solo, itemPenalty, enabledByConfig, requiresPox, hasPox, out var actualReason));
        Assert.Contains(reason, actualReason, StringComparison.OrdinalIgnoreCase);
    }
}
