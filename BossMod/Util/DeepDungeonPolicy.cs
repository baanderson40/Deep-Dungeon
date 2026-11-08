namespace BossMod;

internal static class DeepDungeonPolicy
{
    internal enum MovementDiagnosticKind
    {
        NavigationStall,
        NavigationOscillation,
        CombatPositioningSatisfied,
        InteractionHold,
        CastingHold,
        BossFloorIdle,
        NoNavigationDestination,
    }

    internal struct FloorInitializationGate
    {
        private bool _awaitingContextChange;
        private bool _awaitingMapData;

        internal void BeginTransition()
        {
            _awaitingContextChange = true;
            _awaitingMapData = true;
        }

        internal void ObserveContextChange()
        {
            _awaitingContextChange = false;
            _awaitingMapData = true;
        }

        internal bool CanInitialize(bool validContext, bool hasInitializedMap, bool mapDataUpdate)
            => validContext && hasInitializedMap && !_awaitingContextChange && (!_awaitingMapData || mapDataUpdate);

        internal void MarkInitialized()
        {
            _awaitingContextChange = false;
            _awaitingMapData = false;
        }
    }

    internal enum PomanderDispatchStatus
    {
        Issued,
        AnimationLocked,
        InvalidDungeonOrAction,
        MissingDirectorOrSlot,
    }

    internal static bool ShouldNavigate(bool autoDDEnabled, bool hasManualDestination) => autoDDEnabled || hasManualDestination;

    internal static bool IsFloorContextReady(int dungeonID, int floor) => dungeonID != 0 && floor > 0;

    internal static bool CanInitializeFloor(int dungeonID, int floor, bool hasInitializedMap)
        => IsFloorContextReady(dungeonID, floor) && hasInitializedMap;

    internal static bool ShouldEstablishPomanderBaseline(bool validContext, bool baselineReady, bool anyPomanderUsable)
        => validContext && !baselineReady && anyPomanderUsable;

    internal static bool ShouldReportPomanderChange(bool validContext, bool baselineReady)
        => validContext && baselineReady;

    internal static bool ShouldSelectRoomDestination(bool roomActive, bool objectivePending, bool playerInsideRoom)
        => roomActive && objectivePending && !playerInsideRoom;

    // Room-route attraction must cover progress from the player's projection toward the next connected room.
    // Starting at the source center can leave no local goal when approaching that center from a hallway;
    // extending behind the player instead would reward backwards travel and reintroduce route flipping.
    internal static bool IsInForwardRoomRoute(WPos point, WPos source, WPos nextRoom, float startProgress, float halfWidth)
    {
        var route = nextRoom - source;
        var length = route.Length();
        if (length <= 0.01f)
            return false;

        var direction = route / length;
        var offset = point - source;
        var progress = offset.Dot(direction);
        return progress >= startProgress && progress <= length && MathF.Abs(offset.Cross(direction)) <= halfWidth;
    }

    internal static bool ShouldKeepFullClearDestination(bool fullClearEnabled, bool roomActive, bool roomRevealed)
        => fullClearEnabled && roomActive && !roomRevealed;

    internal static bool ShouldConsiderBronzeTreasure(bool targetable, bool opened)
        => targetable && !opened;

    internal static float TreasureApproachWeight(bool inCombat)
        => inCombat ? 1 : 0.02f;

    internal static bool ShouldAddPassageActorGoal(bool hasRoomDestination, bool headingToPassage, bool hasManualDestination)
        => !hasManualDestination && (!hasRoomDestination || headingToPassage);

    internal static Func<WPos, float> PassageProgressGoal(WPos target, WPos player, float preferredRange, float maxRange, float minProgress, float weight)
    {
        var playerDistance = (target - player).Length();
        return p =>
        {
            var distance = (target - p).Length();
            return playerDistance > preferredRange && distance <= maxRange && distance + minProgress < playerDistance ? weight : 0;
        };
    }

    internal static bool ShouldConsiderActorForNavigation(bool isPassageActor, bool passageGoalAllowed)
        => !isPassageActor || passageGoalAllowed;

    internal static bool HasMeaningfulProgress(float bestDistance, float currentDistance) => bestDistance - currentDistance >= 0.75f;

    internal static bool ShouldLogStall(TimeSpan noProgressFor, DateTime lastLog, DateTime now)
        => noProgressFor >= TimeSpan.FromSeconds(10) && (lastLog == default || now - lastLog >= TimeSpan.FromSeconds(30));

    internal static MovementDiagnosticKind ClassifyMovementDiagnostic(bool oscillating, bool bossFloor, bool interactionSuppressed, bool castingSuppressed, bool inCombat, bool hasDestination, bool movementIssued, float? goalHere, float? goalDestination)
    {
        if (oscillating)
            return MovementDiagnosticKind.NavigationOscillation;
        if (interactionSuppressed)
            return MovementDiagnosticKind.InteractionHold;
        if (castingSuppressed)
            return MovementDiagnosticKind.CastingHold;
        if (!movementIssued && inCombat && goalHere is > 0 && (goalDestination == null || goalHere >= goalDestination))
            return MovementDiagnosticKind.CombatPositioningSatisfied;
        if (!movementIssued && bossFloor && !hasDestination)
            return MovementDiagnosticKind.BossFloorIdle;
        return hasDestination ? MovementDiagnosticKind.NavigationStall : MovementDiagnosticKind.NoNavigationDestination;
    }

    internal static string ObjectiveStallBlocker(bool pullLimit, bool interactionActive, bool casting, bool inCombat, bool hazardsPresent)
        => pullLimit ? "pull limit"
        : interactionActive ? "interaction hold"
        : casting ? "player casting"
        : inCombat ? "combat hold"
        : hazardsPresent ? "hazards present; blockage unproven"
        : "no meaningful distance progress";

    internal static string ObjectiveNavigationOwner(bool hasRoomObjective, string? actorGoalType, bool actorGoalActive)
        => hasRoomObjective && (actorGoalType is "Treasure" or "Hoard") && actorGoalActive ? $"room objective with {actorGoalType.ToLowerInvariant()} detour"
        : hasRoomObjective && (actorGoalType is "Treasure" or "Hoard") ? $"room objective; {actorGoalType.ToLowerInvariant()} candidate pending"
        : hasRoomObjective ? "room objective"
        : actorGoalType is null ? "none"
        : actorGoalActive ? $"actor objective ({actorGoalType})"
        : $"actor candidate pending ({actorGoalType})";

    internal static bool ShouldRequestObjectivePathDiagnostic(bool roomObjective, bool inCombat, bool actorGoalSelected, bool interactionActive, bool pullLimit, bool casting)
        => roomObjective && !inCombat && !actorGoalSelected && !interactionActive && !pullLimit && !casting;

    internal static string ActorSelectionTransitionReason(bool previousInCandidates, bool previousEligible)
        => !previousInCandidates ? "previous-candidate-absent"
        : !previousEligible ? "previous-candidate-filtered"
        : "previous-candidate-still-eligible";

    internal static string DiagnosticTileset(bool bossFloor, byte tileset)
        => bossFloor ? $"n/a (boss floor; raw={tileset})" : tileset.ToString();

    internal static bool ShouldSummarizeInteractionRequests(DateTime lastSummary, DateTime now)
        => lastSummary == default || now - lastSummary >= TimeSpan.FromSeconds(5);

    internal static bool IsWithinPomanderReadbackWindow(DateTime requestedAt, DateTime now)
        => now >= requestedAt && now - requestedAt <= TimeSpan.FromSeconds(15);

    internal static int TrimDiagnosticSamples<T>(Queue<T> samples, int capacity)
    {
        var removed = 0;
        while (samples.Count > Math.Max(0, capacity))
        {
            samples.Dequeue();
            ++removed;
        }
        return removed;
    }

    internal static int CountDirectionReversals(ReadOnlySpan<WDir> directions)
    {
        var reversals = 0;
        var previous = default(WDir);
        foreach (var direction in directions)
        {
            if (direction.LengthSq() <= 0.01f)
                continue;
            if (previous.LengthSq() > 0.01f && direction.Dot(previous) < 0)
                ++reversals;
            previous = direction;
        }
        return reversals;
    }

    internal static bool IsGeometryReady(int tileset, int layoutCount, int mapRoomCount, int expectedRoomCount, ReadOnlySpan<bool> activeRooms, ReadOnlySpan<bool> validRoomBoxes, out string reason)
    {
        if (mapRoomCount != expectedRoomCount)
        {
            reason = $"map room count {mapRoomCount}, expected {expectedRoomCount}";
            return false;
        }
        if (activeRooms.Length != mapRoomCount)
        {
            reason = $"active room count {activeRooms.Length}, expected {mapRoomCount}";
            return false;
        }
        if (tileset >= layoutCount)
        {
            reason = $"tileset {tileset} has no geometry (loaded layouts={layoutCount})";
            return false;
        }
        if (validRoomBoxes.Length != mapRoomCount)
        {
            reason = $"tileset {tileset} room rectangle count {validRoomBoxes.Length}, expected {mapRoomCount}";
            return false;
        }

        var activeCount = 0;
        Span<int> missingRooms = stackalloc int[8];
        var missingCount = 0;
        var totalMissing = 0;
        for (var room = 0; room < mapRoomCount; ++room)
        {
            if (!activeRooms[room])
                continue;
            ++activeCount;
            if (!validRoomBoxes[room])
            {
                if (missingCount < missingRooms.Length)
                    missingRooms[missingCount++] = room;
                ++totalMissing;
            }
        }

        if (activeCount == 0)
        {
            reason = "map has no initialized rooms";
            return false;
        }
        if (totalMissing > 0)
        {
            var missing = string.Join(",", missingRooms[..missingCount].ToArray());
            reason = $"tileset {tileset} missing/invalid geometry for active rooms [{missing}{(totalMissing > missingCount ? ",…" : "")}]";
            return false;
        }

        reason = "ready";
        return true;
    }

    internal static bool CanAutoUsePomander(bool validDungeonAndAction, bool validSlot, byte stock, bool usable, bool solo, bool itemPenalty, bool enabledByConfig, bool requiresPox, bool hasPox, out string reason)
    {
        if (!validDungeonAndAction)
            reason = "invalid dungeon or pomander";
        else if (!validSlot)
            reason = "pomander slot unavailable";
        else if (stock == 0)
            reason = "no stock";
        else if (!usable)
            reason = "game state marks pomander unusable";
        else if (!solo)
            reason = "party is not solo";
        else if (itemPenalty)
            reason = "Item Penalty active";
        else if (!enabledByConfig)
            reason = "disabled by AutoPoms configuration";
        else if (requiresPox && !hasPox)
            reason = "Pox not present";
        else
            reason = "eligible";

        return reason == "eligible";
    }

    internal static PomanderDispatchStatus GetPomanderDispatchStatus(bool animationLocked, bool validDungeonAndAction, bool directorAvailable, bool validSlot)
        => animationLocked ? PomanderDispatchStatus.AnimationLocked
        : !validDungeonAndAction ? PomanderDispatchStatus.InvalidDungeonOrAction
        : !directorAvailable || !validSlot ? PomanderDispatchStatus.MissingDirectorOrSlot
        : PomanderDispatchStatus.Issued;
}
