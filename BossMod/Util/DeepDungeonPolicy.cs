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

        internal readonly bool CanInitialize(bool validContext, bool hasInitializedMap, bool mapDataUpdate)
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

    internal enum PomanderMaintenanceKind
    {
        Strength,
        Steel,
        Raising,
        Safety,
        Serenity,
        Affluence,
        Flight,
        Sight,
        Fortune,
        Intuition,
    }

    internal enum PomanderMaintenanceRequestPhase
    {
        AwaitingReadback,
        AwaitingEffect,
        OutcomeUnknown,
        Resolved,
    }

    internal static readonly TimeSpan PomanderMaintenanceReadbackWindow = TimeSpan.FromSeconds(5);
    internal readonly record struct PomanderMaintenanceRequest(
        DeepDungeonState.DungeonType Dungeon, byte Floor, PomanderID Pomander, DateTime RequestedAt, byte StockAtRequest,
        bool? EffectPresentAtRequest, bool EffectWasAbsentAfterRequest, bool StockDecrementObserved, PomanderMaintenanceRequestPhase Phase,
        string? EvidenceAtRequest = null);

    internal static string? PomanderReadinessBlocker(bool localPlayerAvailable, bool playerStateLoaded, bool betweenAreas, bool betweenAreas51, bool screenReady)
        => !localPlayerAvailable ? "local player unavailable"
        : !playerStateLoaded ? "player state not loaded"
        : betweenAreas || betweenAreas51 ? "area transition active"
        : !screenReady ? "loading screen visible"
        : null;

    internal static string? PomanderSchedulingReadinessBlocker(bool localPlayerAvailable, bool betweenAreas, bool betweenAreas51, bool screenReady)
        => !localPlayerAvailable ? "local player unavailable"
        : betweenAreas || betweenAreas51 ? "area transition active"
        : !screenReady ? "loading screen visible"
        : null;

    internal readonly record struct SafetyFloorUsage(DeepDungeonState.DungeonType Dungeon, byte Floor, bool Used);

    internal static SafetyFloorUsage ObserveSafetyFloorUsage(SafetyFloorUsage usage, DeepDungeonState.DungeonType dungeon, byte floor, bool effectActive)
    {
        if (usage.Dungeon != dungeon || usage.Floor != floor)
            usage = new(dungeon, floor, false);
        return usage with { Used = usage.Used || effectActive };
    }

    internal static bool IsMatchingSafetyUseMessage(
        PomanderMaintenanceRequest request, DeepDungeonState.DungeonType dungeon, byte floor, DateTime now)
        => request.Pomander is PomanderID.Safety or PomanderID.ProtoSafety
            && request.Dungeon == dungeon && request.Floor == floor
            && request.Phase != PomanderMaintenanceRequestPhase.Resolved
            && now >= request.RequestedAt;

    internal static bool IsMatchingSightUseMessage(
        PomanderMaintenanceRequest request, DeepDungeonState.DungeonType dungeon, byte floor, DateTime now)
        => request.Pomander is PomanderID.Sight or PomanderID.ProtoSight
            && request.Dungeon == dungeon && request.Floor == floor
            && request.Phase != PomanderMaintenanceRequestPhase.Resolved
            && now >= request.RequestedAt;

    internal static bool ShouldSuppressSightForSafety(bool safetyUsedThisFloor, bool safetyRequestUnresolved, bool safetyEnabled, bool safetyAvailable)
        => safetyUsedThisFloor || safetyRequestUnresolved || safetyEnabled && safetyAvailable;

    internal static bool ShouldSuppressSafetyForSight(bool sightUsedThisFloor, bool sightRequestUnresolved)
        => sightUsedThisFloor || sightRequestUnresolved;

    internal static bool? CurrentFloorGloom(DeepDungeonState.FloorEffects effects, DeepDungeonState.DungeonType dungeon, byte floor)
        => effects.Known && effects.GloomKnown && effects.Dungeon == dungeon && effects.Floor == floor ? effects.Gloom : null;

    internal static DeepDungeonState.FloorEffects ResetFloorEffectsForContextChange(DeepDungeonState.FloorEffects effects, bool contextChanged)
        => contextChanged ? default : effects;

    internal static bool IsBlockingPomanderMaintenanceRequest(PomanderMaintenanceRequestPhase phase)
        => phase is PomanderMaintenanceRequestPhase.AwaitingReadback or PomanderMaintenanceRequestPhase.AwaitingEffect;

    internal static bool IsFloorScopedMaintenanceKind(PomanderMaintenanceKind kind)
        => kind is PomanderMaintenanceKind.Safety or PomanderMaintenanceKind.Sight or PomanderMaintenanceKind.Serenity or PomanderMaintenanceKind.Fortune;

    internal static bool HasBlockingPomanderMaintenanceRequest(
        IEnumerable<KeyValuePair<PomanderMaintenanceKind, PomanderMaintenanceRequest>> requests,
        PomanderMaintenanceKind exceptKind)
        => requests.Any(entry => entry.Key != exceptKind && IsBlockingPomanderMaintenanceRequest(entry.Value.Phase));

    internal static PomanderID MaintenancePomander(DeepDungeonState.DungeonType dungeon, PomanderMaintenanceKind kind)
        => (dungeon, kind) switch
        {
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Strength) => PomanderID.Strength,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Steel) => PomanderID.Steel,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Raising) => PomanderID.Raising,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Safety) => PomanderID.Safety,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Sight) => PomanderID.Sight,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Serenity) => PomanderID.Serenity,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Fortune) => PomanderID.Fortune,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Intuition) => PomanderID.Intuition,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Affluence) => PomanderID.Affluence,
            (DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH, PomanderMaintenanceKind.Flight) => PomanderID.Flight,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Strength) => PomanderID.ProtoStrength,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Steel) => PomanderID.ProtoSteel,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Raising) => PomanderID.ProtoRaising,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Safety) => PomanderID.ProtoSafety,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Sight) => PomanderID.ProtoSight,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Serenity) => PomanderID.ProtoSerenity,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Fortune) => PomanderID.ProtoFortune,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Intuition) => PomanderID.ProtoIntuition,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Affluence) => PomanderID.ProtoAffluence,
            (DeepDungeonState.DungeonType.EO, PomanderMaintenanceKind.Flight) => PomanderID.ProtoFlight,
            _ => PomanderID.None,
        };

    internal static bool PomanderMaintenanceEffectPresent(PomanderMaintenanceKind kind, bool strengthDamageUp, bool strengthHealingPotencyUp, bool steelVulnerabilityDown, bool raisingItemActive, bool pomanderItemActive)
        => kind switch
        {
            PomanderMaintenanceKind.Strength => strengthDamageUp || strengthHealingPotencyUp,
            PomanderMaintenanceKind.Steel => steelVulnerabilityDown,
            PomanderMaintenanceKind.Raising => raisingItemActive,
            // Safety's use is a one-shot floor event, not a persistent item Active effect.
            PomanderMaintenanceKind.Safety => false,
            // Sight is confirmed by system log 7256, not the item Active bit.
            PomanderMaintenanceKind.Sight => false,
            PomanderMaintenanceKind.Fortune or PomanderMaintenanceKind.Intuition or PomanderMaintenanceKind.Affluence or PomanderMaintenanceKind.Flight => pomanderItemActive,
            _ => false,
        };

    internal static bool CanMaintainPomander(
        bool maintenanceEnabled, bool autoDDEnabled, bool floorReady, bool alive, bool solo, bool itemPenalty,
        bool validSlot, byte stock, bool usable, bool bossFloor, bool allowLastCharge, bool skipBossFloor, bool alreadyUsedThisFloor,
        bool effectActive, bool requestPending, bool outcomeUnknown,
        out string reason)
    {
        reason = !maintenanceEnabled ? "maintenance disabled"
            : !autoDDEnabled ? "AutoDD disabled"
            : !floorReady ? "floor not initialized"
            : !alive ? "player dead"
            : !solo ? "not solo"
            : itemPenalty ? "Item Penalty"
            : !validSlot ? "slot unavailable"
            : skipBossFloor && bossFloor ? "boss floor"
            : outcomeUnknown ? "previous outcome unknown"
            : requestPending ? "request pending"
            : effectActive ? "effect active"
            : alreadyUsedThisFloor ? "already used this floor"
            : !usable ? "unusable"
            : stock < (bossFloor || allowLastCharge ? 1 : 2) ? "reserve"
            : "eligible";
        return reason == "eligible";
    }

    internal static bool IsEnabledSerenityTrigger(bool itemPenalty, bool amnesia, bool gloom, bool damageDown, bool hpPenalty, bool autoHealPenalty,
        bool useItemPenalty, bool useAmnesia, bool useGloom, bool useDamageDown, bool useHPPenalty, bool useAutoHealPenalty)
        => itemPenalty && useItemPenalty || amnesia && useAmnesia || gloom && useGloom || damageDown && useDamageDown || hpPenalty && useHPPenalty || autoHealPenalty && useAutoHealPenalty;

    internal static bool SerenityMayIgnoreItemPenalty(PomanderMaintenanceKind kind) => kind == PomanderMaintenanceKind.Serenity;

    internal static bool IsNextFloorEligible(DeepDungeonState.DungeonType dungeon, byte floor)
    {
        var maxFloor = dungeon switch
        {
            DeepDungeonState.DungeonType.POTD => 200,
            DeepDungeonState.DungeonType.HOH or DeepDungeonState.DungeonType.EO or DeepDungeonState.DungeonType.PT => 100,
            _ => 0,
        };
        var nextFloor = floor + 1;
        return floor > 0 && floor < maxFloor && nextFloor % 10 != 0 && !(dungeon is DeepDungeonState.DungeonType.EO or DeepDungeonState.DungeonType.PT && nextFloor == 99);
    }

    internal static bool IsPreparingNextFloorPomander(bool enabled, bool passageActive, bool departureIntent, bool alive, bool inCombat,
        bool floorReady, DeepDungeonState.DungeonType dungeon, byte floor)
        => enabled && passageActive && departureIntent && alive && !inCombat && floorReady && IsNextFloorEligible(dungeon, floor);

    internal static PomanderMaintenanceRequest ObservePomanderMaintenanceRequest(
        PomanderMaintenanceRequest request, byte currentStock, bool? effectPresent, DateTime now)
    {
        var effectWasAbsentAfterRequest = request.EffectWasAbsentAfterRequest || effectPresent == false;
        if (effectPresent == true && (request.EffectPresentAtRequest == false || request.EffectWasAbsentAfterRequest))
            return request with { EffectWasAbsentAfterRequest = effectWasAbsentAfterRequest, Phase = PomanderMaintenanceRequestPhase.Resolved };

        var stockDecrementObserved = request.StockDecrementObserved || currentStock < request.StockAtRequest;
        if (request.Phase == PomanderMaintenanceRequestPhase.OutcomeUnknown)
            return request with { EffectWasAbsentAfterRequest = effectWasAbsentAfterRequest, StockDecrementObserved = stockDecrementObserved };
        if (now - request.RequestedAt >= PomanderMaintenanceReadbackWindow)
            return request with { EffectWasAbsentAfterRequest = effectWasAbsentAfterRequest, StockDecrementObserved = stockDecrementObserved, Phase = PomanderMaintenanceRequestPhase.OutcomeUnknown };
        return request with
        {
            EffectWasAbsentAfterRequest = effectWasAbsentAfterRequest,
            StockDecrementObserved = stockDecrementObserved,
            Phase = stockDecrementObserved ? PomanderMaintenanceRequestPhase.AwaitingEffect : PomanderMaintenanceRequestPhase.AwaitingReadback,
        };
    }

    internal enum OvercapAttributionStatus
    {
        Matched,
        Ambiguous,
        WrongFloorContext,
        ExpiredOrFutureInteraction,
        IncompatibleChest,
        ActorUnavailable,
        NoRecentInteraction,
    }

    internal readonly record struct OvercapAttribution(OvercapAttributionStatus Status, ulong ActorID, long InteractionSequence, int CandidateCount, ImmutableArray<ulong> CandidateActorIDs, TimeSpan EvidenceAge);

    internal static OvercapAttribution CorrelateOvercapMessage(
        DateTime messageAt, int dungeonID, byte floor, uint expectedChestOID,
        ReadOnlySpan<DeepDungeonInteractionHistory.TreasureCall> calls, long afterSequence = 0)
    {
        var allCalls = calls.ToArray().Where(call => call.Sequence > afterSequence).ToArray();
        var objectCalls = allCalls.Where(call => call.ObjectID == expectedChestOID).ToArray();
        var recentCalls = allCalls.Where(call => call.IssuedAt <= messageAt && messageAt - call.IssuedAt <= DeepDungeonInteractionHistory.Retention).ToArray();
        var contextCalls = recentCalls.Where(call => call.DungeonID == dungeonID && call.Floor == floor).ToArray();
        var compatibleCalls = contextCalls.Where(call => call.ObjectID == expectedChestOID).ToArray();
        var actorIDs = compatibleCalls.Select(call => call.ActorID).Distinct().ToArray();
        if (actorIDs.Length > 1)
            return new(OvercapAttributionStatus.Ambiguous, 0, 0, actorIDs.Length, [.. actorIDs.Take(3)], messageAt - compatibleCalls.Max(call => call.IssuedAt));
        if (actorIDs.Length == 1)
        {
            var latestCall = compatibleCalls.MaxBy(call => call.IssuedAt);
            return new(OvercapAttributionStatus.Matched, actorIDs[0], latestCall.Sequence, 1, [.. actorIDs], messageAt - latestCall.IssuedAt);
        }
        var otherContextIDs = objectCalls.Where(call => call.DungeonID != dungeonID || call.Floor != floor).Select(call => call.ActorID).Distinct().ToArray();
        if (recentCalls.Any(call => call.ObjectID == expectedChestOID) && contextCalls.All(call => call.ObjectID != expectedChestOID))
            return new(OvercapAttributionStatus.WrongFloorContext, 0, 0, otherContextIDs.Length, [.. otherContextIDs.Take(3)], TimeSpan.Zero);
        if (objectCalls.Length > 0)
            return new(OvercapAttributionStatus.ExpiredOrFutureInteraction, 0, 0, objectCalls.Select(call => call.ActorID).Distinct().Count(), [.. objectCalls.Select(call => call.ActorID).Distinct().Take(3)], TimeSpan.Zero);
        if (contextCalls.Length > 0)
        {
            var incompatibleIDs = contextCalls.Select(call => call.ActorID).Distinct().ToArray();
            return new(OvercapAttributionStatus.IncompatibleChest, 0, 0, incompatibleIDs.Length, [.. incompatibleIDs.Take(3)], TimeSpan.Zero);
        }
        return new(OvercapAttributionStatus.NoRecentInteraction, 0, 0, 0, [], TimeSpan.Zero);
    }

    internal static bool ShouldNavigate(bool autoDDEnabled, bool hasManualDestination) => autoDDEnabled || hasManualDestination;

    internal static bool IsFloorContextReady(int dungeonID, int floor) => dungeonID != 0 && floor > 0;

    internal static bool IsSupportedDeepDungeonBossEncounter(DeepDungeonState.DungeonType dungeon, byte floor, string? moduleNamespace, BossModuleInfo.GroupType groupType, int sortOrder, bool active)
    {
        var expectedNamespace = dungeon switch
        {
            DeepDungeonState.DungeonType.POTD => "BossMod.Heavensward.DeepDungeon.PalaceOfTheDead.",
            DeepDungeonState.DungeonType.HOH => "BossMod.Stormblood.DeepDungeon.HeavenOnHigh.",
            DeepDungeonState.DungeonType.EO => "BossMod.Endwalker.DeepDungeon.EurekaOrthos.",
            DeepDungeonState.DungeonType.PT => "BossMod.Dawntrail.DeepDungeon.PilgrimsTraverse.",
            _ => null,
        };
        return active && floor > 0 && groupType == BossModuleInfo.GroupType.CFC && sortOrder == floor
            && expectedNamespace != null && moduleNamespace?.StartsWith(expectedNamespace, StringComparison.Ordinal) == true;
    }

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

    internal static float ForwardRoomRoutePriority(WPos point, WPos source, WPos nextRoom, float startProgress, float halfWidth, float maxPriority)
    {
        if (maxPriority <= 0 || !IsInForwardRoomRoute(point, source, nextRoom, startProgress, halfWidth))
            return 0;

        var routeLength = (nextRoom - source).Length();
        var progress = (point - source).Dot((nextRoom - source) / routeLength);
        return maxPriority * (0.5f + 0.5f * Math.Clamp(progress / routeLength, 0, 1));
    }

    internal static bool ShouldKeepFullClearDestination(bool fullClearEnabled, bool roomActive, bool roomRevealed)
        => fullClearEnabled && roomActive && !roomRevealed;

    internal static bool ShouldClearReachedRoomDestination(bool isFullClearObjective, bool fullClearEnabled, bool roomActive, bool roomRevealed)
        => !isFullClearObjective || !ShouldKeepFullClearDestination(fullClearEnabled, roomActive, roomRevealed);

    internal static bool ShouldConsiderBronzeTreasure(bool targetable, bool opened)
        => targetable && !opened;

    internal static float TreasureApproachWeight(bool inCombat)
        => inCombat ? 1 : 0.02f;

    internal static readonly TimeSpan FloorStartPomanderWindow = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan TreasureRecoveryCooldown = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan TreasureStallThreshold = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TreasureOscillationThreshold = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan TreasureSelectionContinuity = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan HoHPomanderProposalTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan TreasureOscillationSignalMaxAge = TimeSpan.FromSeconds(10);

    internal static bool IsFloorStartPomanderEligible(bool configured, bool bossFloor, bool initialized, DateTime initializedAt, DateTime now)
        => configured && !bossFloor && initialized && initializedAt != default && now >= initializedAt && now - initializedAt <= FloorStartPomanderWindow;

    internal static bool WasPomanderAvailableAtFloorEntry(bool entrySnapshotReady, int entryCount)
        => entrySnapshotReady && entryCount > 0;

    internal static bool ShouldAutoUseWitching(bool enabled, bool bossFloor, int aggroCount, int threshold, bool validSlot, int stock, bool usable, bool solo, bool itemPenalty, bool alreadyHandled)
        => enabled && !bossFloor && threshold > 0 && aggroCount >= threshold && validSlot && stock > 0 && usable && solo && !itemPenalty && !alreadyHandled;

    internal static bool ShouldDeferTreasureForOscillation(bool oscillating, TimeSpan noProgressFor, bool roomObjective, bool manualNavigation,
        bool inCombat, bool casting, bool interactionInRange)
        => oscillating && noProgressFor >= TreasureOscillationThreshold && roomObjective && !manualNavigation && !inCombat && !casting && !interactionInRange;

    internal static bool ShouldDeferStalledTreasure(TimeSpan eligibleNoProgressFor, bool isTreasureSelected, bool manualNavigation,
        bool inCombat, bool casting, bool interactionHeld)
        => eligibleNoProgressFor >= TreasureStallThreshold && isTreasureSelected && !manualNavigation && !inCombat && !casting && !interactionHeld;

    internal static TimeSpan TreasureSelectedSampleDuration(DateTime lastSelectedAt, DateTime now, DateTime lastSampleAt)
        => lastSelectedAt != default && now >= lastSelectedAt && now - lastSelectedAt <= TreasureSelectionContinuity
            && lastSampleAt != default && now >= lastSampleAt ? now - lastSampleAt : TimeSpan.Zero;

    internal static bool IsHoHPomanderProposalPending(DateTime proposedAt, DateTime now)
        => proposedAt != default && now >= proposedAt && now - proposedAt < HoHPomanderProposalTimeout;

    internal static bool IsTreasureOscillationSignalValid(ulong signalActor, ulong selectedActor, byte signalFloor, byte currentFloor, DateTime signalAt, DateTime now)
        => signalActor != 0 && signalActor == selectedActor && signalFloor == currentFloor && signalAt != default
            && now >= signalAt && now - signalAt <= TreasureOscillationSignalMaxAge;

    internal static bool ShouldRequestDeepDungeonLeave(bool completionConfirmed, bool sourceTerritoryMatches, bool playerReady, bool transitioning)
        => completionConfirmed && sourceTerritoryMatches && playerReady && !transitioning;

    internal static uint WaitingRoomExitBaseID(DeepDungeonState.DungeonType dungeon, ushort territory)
        => (dungeon, territory) switch
        {
            (DeepDungeonState.DungeonType.POTD, 570) => 2006016,
            (DeepDungeonState.DungeonType.HOH, 780) => 2009523,
            _ => 0,
        };

    internal static bool IsVerifiedWaitingRoomExit(DeepDungeonState.DungeonType dungeon, ushort territory, uint baseID, bool targetable, int candidateCount)
        => WaitingRoomExitBaseID(dungeon, territory) == baseID && baseID != 0 && targetable && candidateCount == 1;

    internal static bool ShouldStartDeparturePath(bool navReady, bool pathRunning, bool pathfindInProgress, bool validExit, bool validPlayer)
        => navReady && !pathRunning && !pathfindInProgress && validExit && validPlayer;

    internal static bool OwnsDeparturePath(bool pathRunning, int waypointCount, float endpointDistance, float tolerance)
        => pathRunning && waypointCount > 0 && endpointDistance <= tolerance;

    internal static bool IsTreasureDeferred(DateTime deferredAt, DateTime now)
        => deferredAt != default && now >= deferredAt && now - deferredAt < TreasureRecoveryCooldown;

    internal static Func<WPos, float> TreasureProgressGoal(WPos target, WPos player, float maxRange, float minProgress, float weight)
    {
        var playerDistance = (target - player).Length();
        return p =>
        {
            var distance = (target - p).Length();
            return playerDistance > 2 && distance <= maxRange && distance + minProgress < playerDistance ? weight : 0;
        };
    }

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

    internal static Func<WPos, float> HoardProgressGoal(WPos target, WPos player, float maxRange, float minProgress, float weight)
    {
        var playerDistance = (target - player).Length();
        return p =>
        {
            var distance = (target - p).Length();
            return playerDistance > 2 && distance <= maxRange && distance + minProgress < playerDistance ? weight : 0;
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
        => hasRoomObjective && (actorGoalType is "Treasure" or "Hoard") && actorGoalActive ? $"room objective with {(actorGoalType == "Treasure" ? "treasure" : "hoard")} detour"
        : hasRoomObjective && (actorGoalType is "Treasure" or "Hoard") ? $"room objective; {(actorGoalType == "Treasure" ? "treasure" : "hoard")} candidate pending"
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

    internal static PomanderID SelectHoHFloorClearPomander(bool petrificationEnabled, bool petrificationAvailable, bool petrificationUsedThisFloor,
        bool frailtyEnabled, bool frailtyAvailable, bool frailtyUsedThisFloor)
        => petrificationUsedThisFloor ? PomanderID.None
        : petrificationEnabled && petrificationAvailable ? PomanderID.Petrification
        : frailtyEnabled && frailtyAvailable && !frailtyUsedThisFloor ? PomanderID.Frailty
        : PomanderID.None;

    internal static bool ShouldUseHoHPomanders(DeepDungeonState.DungeonType dungeon, bool normalFloor, bool passageActive)
        => dungeon == DeepDungeonState.DungeonType.HOH && normalFloor && !passageActive;

    internal static PomanderDispatchStatus GetPomanderDispatchStatus(bool animationLocked, bool validDungeonAndAction, bool directorAvailable, bool validSlot)
        => animationLocked ? PomanderDispatchStatus.AnimationLocked
        : !validDungeonAndAction ? PomanderDispatchStatus.InvalidDungeonOrAction
        : !directorAvailable || !validSlot ? PomanderDispatchStatus.MissingDirectorOrSlot
        : PomanderDispatchStatus.Issued;
}
