using BossMod.Pathfinding;
using System.Threading.Tasks;

namespace BossMod.Autorotation.MiscAI;

public sealed class NormalMovement(RotationModuleManager manager, Actor player) : RotationModule(manager, player)
{

    enum SID : uint
    {
        ThinIce = 911,
        Spinning = 2973, // alzadaal's legacy, forced march at 3 units/sec, player can steer left and right
    }

    public enum Track { Destination, Range, Cast, SpecialModes, ForbiddenZoneCushion, DelayMovement, SeparateDodgeDelay, DodgeDelayMovement }
    public enum DestinationStrategy { None, Pathfind, Explicit }
    public enum RangeStrategy { Any, MaxRange, GreedGCDExplicit, GreedLastMomentExplicit, GreedAutomatic }
    public enum CastStrategy { Leeway, Explicit, Greedy, FinishMove, DropMove, FinishInstants, DropInstants }
    public enum ForbiddenZoneCushionStrategy { None, Small, Medium, Large }
    public enum SpecialModesStrategy { Automatic, Ignore }
    public enum DelayMovementStrategy { None, Short, Long }
    public enum SeparateDodgeDelayStrategy { Disabled, Enabled }

    public const float GreedTolerance = 0.15f;

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Automatic movement", "Automatically move character based on pathfinding or explicit coordinates.", "AI", "veyn", RotationModuleQuality.Good, new(~0ul), 1000, 1, RotationModuleOrder.Movement, CanUseWhileRoleplaying: true, PvP: PvPCompatibility.Any);
        res.Define(Track.Destination).As<DestinationStrategy>("Destination", "Destination", 30)
            .AddOption(DestinationStrategy.None, "No automatic movement")
            .AddOption(DestinationStrategy.Pathfind, "Use standard pathfinding to find best position")
            .AddOption(DestinationStrategy.Explicit, "Move to specific point", supportedTargets: ActionTargets.Area);

        // note that these options used to be melee-specific - internal names are kept unchanged for convenience
        res.Define(Track.Range).As<RangeStrategy>("Range", "Range", 20)
            .AddOption(RangeStrategy.Any, "Go directly to destination")
            .AddOption(RangeStrategy.MaxRange, "Stay within maximum effective range of target closest to destination", supportedTargets: ActionTargets.Hostile)
            .AddOption(RangeStrategy.GreedGCDExplicit, "Stay within effective range until last GCD; ensure destination is reached by the plan entry end", supportedTargets: ActionTargets.Hostile)
            .AddOption(RangeStrategy.GreedLastMomentExplicit, "Stay within effective range until last possible moment; ensure destination is reached by the plan entry end", supportedTargets: ActionTargets.Hostile)
            .AddOption(RangeStrategy.GreedAutomatic, "Stay within effective range as long as possible; try to ensure safety is reached before mechanic resolves", supportedTargets: ActionTargets.Hostile)
            /*.AddOption(RangeStrategy.Drag, "Drag", "Drag the target to specified spot, but maintain gcd uptime", supportedTargets: ActionTargets.Hostile)*/; // TODO

        res.Define(Track.Cast).As<CastStrategy>("Cast", "Cast", 10)
            .AddOption(CastStrategy.Leeway, "Continue slidecasting as long as there is enough time to get to safety")
            .AddOption(CastStrategy.Explicit, "Continue slidecasting as long as there is enough time to reach destination by the plan entry end")
            .AddOption(CastStrategy.Greedy, "Don't stop casting, even when it risks getting clipped by aoes")
            .AddOption(CastStrategy.FinishMove, "Start moving as soon as cast ends, use instants until destination is reached")
            .AddOption(CastStrategy.DropMove, "Start moving asap, interrupting casts if necessary, use instants until destination is reached")
            .AddOption(CastStrategy.FinishInstants, "Don't use any more casts after current cast ends")
            .AddOption(CastStrategy.DropInstants, "Don't cast, interrupt current cast if needed");
        res.Define(Track.SpecialModes).As<SpecialModesStrategy>("SpecialModes", "Special", -1)
            .AddOption(SpecialModesStrategy.Automatic, "Automatically deal with special conditions (knockbacks, pyretics, etc)")
            .AddOption(SpecialModesStrategy.Ignore, "Ignore any special conditions (knockbacks, pyretics, etc)");
        res.Define(Track.ForbiddenZoneCushion).As<ForbiddenZoneCushionStrategy>("ForbiddenZoneCushion", "Overdodge", 25)
            .AddOption(ForbiddenZoneCushionStrategy.None, "Do not use any buffer in pathfinding")
            .AddOption(ForbiddenZoneCushionStrategy.Small, "Prefer to stay 0.5y away from forbidden zones")
            .AddOption(ForbiddenZoneCushionStrategy.Medium, "Prefer to stay 1.5y away from forbidden zones")
            .AddOption(ForbiddenZoneCushionStrategy.Large, "Prefer to stay 3y away from forbidden zones");
        res.Define(Track.DelayMovement).As<DelayMovementStrategy>("DelayMovement", "Delay Movement", 9)
            .AddOption(DelayMovementStrategy.None, "Do not delay movement")
            .AddOption(DelayMovementStrategy.Short, "Delay movement by 0.5s")
            .AddOption(DelayMovementStrategy.Long, "Delay movement by 1s");
        res.Define(Track.SeparateDodgeDelay).As<SeparateDodgeDelayStrategy>("SeparateDodgeDelay", "Separate Dodge Delay", 8, renderer: typeof(DefaultOffRenderer))
            .AddOption(SeparateDodgeDelayStrategy.Disabled)
            .AddOption(SeparateDodgeDelayStrategy.Enabled);
        res.Define(Track.DodgeDelayMovement).As<DelayMovementStrategy>("DodgeDelayMovement", "Dodge Delay Movement", 7)
            .AddOption(DelayMovementStrategy.None, "Do not delay dodge movement")
            .AddOption(DelayMovementStrategy.Short, "Delay dodge movement by 0.5s")
            .AddOption(DelayMovementStrategy.Long, "Delay dodge movement by 1s")
            .VisibleWhen(Track.SeparateDodgeDelay, (int)SeparateDodgeDelayStrategy.Enabled);

        return res;
    }

    private NavigationDecision.Context CtxActive = new();
    private NavigationDecision.Context CtxInactive = new();

    public const float MeleeRange = 3;
    public const float CasterRange = 25;
    const float SpinningLookahead = 5.5f;

    private Task<NavigationDecision> _decisionTask = Task.FromResult(default(NavigationDecision));
    private NavigationDecision _lastDecision;
    private DateTime _lastDecisionUpdatedAt;
    private WPos _ddStationaryAnchor;
    private DateTime _ddStationarySince;
    private DateTime _lastDDNavigationDiagnostic;
    private DateTime _lastDDPathDiagnosticLoggedAt;
    private DateTime _ddObjectiveDiagnosticRequestedAt;
    private string? _ddObjectiveDiagnosticReason;
    private bool _hasDDStationaryAnchor;
    private bool _ddPathDiagnosticPending;
    private bool _ddPathDiagnosticRequested;
    private bool _ddObjectiveDiagnosticRequested;
    private long _nextDecisionSequence;
    private DateTime _lastDDRouteSampleAt;
    private int _ddRouteFloor = -1;
    private readonly Queue<RouteSample> _ddRouteSamples = new();

    private readonly record struct RouteSample(DateTime At, WPos Position, float HeightY, long Decision, WPos? Destination, WPos? SearchGoal, ulong Interaction, Vector3? RequestedDirection, WDir RouteDirection);

    private DateTime? TimeToMove;
    private bool? _delayMovementIsDodge;

    private static float DelaySeconds(DelayMovementStrategy strategy) => strategy switch
    {
        DelayMovementStrategy.Short => 0.5f,
        DelayMovementStrategy.Long => 1.0f,
        _ => 0f
    };

    private NavigationDecision GetDecision(float speed, float cushionSize, bool collectDiagnostics = false)
    {
        if (_decisionTask.IsCompletedSuccessfully)
        {
            _lastDecision = _decisionTask.Result;
            _lastDecisionUpdatedAt = World.CurrentTime;
            if (_lastDecision.PathfindDiagnostic != null)
                _ddPathDiagnosticPending = false;
            Manager.LastRasterizeMs = (float)_lastDecision.RasterizeTime.TotalMilliseconds;
            Manager.LastPathfindMs = (float)_lastDecision.PathfindTime.TotalMilliseconds;
        }

        if (_decisionTask.IsCompleted)
        {
            if (_decisionTask.Exception is { } exception)
            {
                Service.Log($"exception during pathfind: {exception}");
                _ddPathDiagnosticPending = false;
            }

            (CtxInactive, CtxActive) = (CtxActive, CtxInactive);
            var captureDiagnostics = collectDiagnostics || _ddPathDiagnosticRequested;
            _ddPathDiagnosticRequested = false;
            _ddPathDiagnosticPending |= captureDiagnostics;
            var pairedGoal = captureDiagnostics ? _ddRouteSamples.LastOrDefault().SearchGoal : null;
            _decisionTask = NavigationDecision.BuildAsync(CtxActive, World.CurrentTime, Hints, Player.Position, speed, World.Network.LastRTT, cushionSize, captureDiagnostics, ++_nextDecisionSequence, Player.PosRot.Y, pairedGoal);
        }

        return _lastDecision;
    }

    private bool RequestDeepDungeonPathDiagnostic()
    {
        var now = World.CurrentTime;
        if (Hints.DeepDungeonDiagnosticRequest is { } objectiveRequest && _ddObjectiveDiagnosticReason == null)
        {
            _ddObjectiveDiagnosticReason = objectiveRequest;
            _ddObjectiveDiagnosticRequestedAt = now;
        }

        if (_ddObjectiveDiagnosticReason != null && now - _ddObjectiveDiagnosticRequestedAt > TimeSpan.FromSeconds(20))
        {
            _ddObjectiveDiagnosticReason = null;
            _ddObjectiveDiagnosticRequestedAt = default;
            _ddObjectiveDiagnosticRequested = false;
        }

        if (World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.None || Hints.GoalZones.Count == 0 && Hints.InteractWithTarget == null && _ddObjectiveDiagnosticReason == null)
        {
            _ddPathDiagnosticPending = false;
            _ddPathDiagnosticRequested = false;
            return false;
        }

        if (_ddObjectiveDiagnosticReason != null && !_ddObjectiveDiagnosticRequested)
        {
            _ddObjectiveDiagnosticRequested = true;
            _ddPathDiagnosticRequested = true;
            return true;
        }

        var stationary = _hasDDStationaryAnchor && (Player.Position - _ddStationaryAnchor).LengthSq() < 0.75f * 0.75f;
        var oscillating = HasRepeatedRouteReversals();
        if (!stationary)
        {
            _ddPathDiagnosticRequested = false;
            return false;
        }
        var stationaryLongEnough = now - _ddStationarySince >= TimeSpan.FromSeconds(10);
        var noDestinationStall = stationaryLongEnough && _lastDecision.Destination == null;
        if (!oscillating && !noDestinationStall
            || _lastDDNavigationDiagnostic != default && now - _lastDDNavigationDiagnostic < TimeSpan.FromSeconds(oscillating ? 5 : 30)
            || _ddPathDiagnosticPending || _ddPathDiagnosticRequested)
            return false;

        _ddPathDiagnosticRequested = true;
        return true;
    }

    private bool HasRepeatedRouteReversals()
    {
        if (_ddRouteSamples.Count < 8)
            return false;
        var samples = _ddRouteSamples.ToArray();
        var directions = new WDir[samples.Length];
        var traveled = 0f;
        for (var i = 0; i < samples.Length; ++i)
        {
            directions[i] = samples[i].RouteDirection;
            if (i > 0)
                traveled += (samples[i].Position - samples[i - 1].Position).Length();
        }
        var net = (samples[^1].Position - samples[0].Position).Length();
        return traveled >= 3 && net <= 1.5f && DeepDungeonPolicy.CountDirectionReversals(directions) >= 4;
    }

    private void LogDeepDungeonNavigation(NavigationDecision decision, string movementState)
    {
        if (_ddRouteFloor != World.DeepDungeon.Floor)
        {
            _ddRouteFloor = World.DeepDungeon.Floor;
            _ddRouteSamples.Clear();
            _lastDDRouteSampleAt = default;
            _hasDDStationaryAnchor = false;
            _lastDDNavigationDiagnostic = default;
            _lastDDPathDiagnosticLoggedAt = default;
            _ddObjectiveDiagnosticRequestedAt = default;
            _ddObjectiveDiagnosticReason = null;
            _ddObjectiveDiagnosticRequested = false;
        }
        if (World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.None)
        {
            _hasDDStationaryAnchor = false;
            _ddStationarySince = default;
            _lastDDNavigationDiagnostic = default;
            _lastDDPathDiagnosticLoggedAt = default;
            _ddObjectiveDiagnosticRequestedAt = default;
            _ddObjectiveDiagnosticReason = null;
            _ddPathDiagnosticPending = false;
            _ddPathDiagnosticRequested = false;
            _ddObjectiveDiagnosticRequested = false;
            _ddRouteSamples.Clear();
            return;
        }

        if (Player.IsDead)
        {
            _hasDDStationaryAnchor = false;
            _ddStationarySince = default;
            _lastDDNavigationDiagnostic = default;
            _ddObjectiveDiagnosticRequestedAt = default;
            _ddObjectiveDiagnosticReason = null;
            _ddPathDiagnosticPending = false;
            _ddPathDiagnosticRequested = false;
            _ddObjectiveDiagnosticRequested = false;
            _ddRouteSamples.Clear();
            return;
        }

        if (Hints.GoalZones.Count == 0 && Hints.InteractWithTarget == null)
        {
            _hasDDStationaryAnchor = false;
            _ddStationarySince = default;
            if (_ddObjectiveDiagnosticReason == null)
            {
                _ddPathDiagnosticPending = false;
                _ddPathDiagnosticRequested = false;
            }
            _ddRouteSamples.Clear();
            if (_ddObjectiveDiagnosticReason == null)
                return;
        }

        var now = World.CurrentTime;
        var resetStationaryAnchor = false;
        if (!_hasDDStationaryAnchor || (Player.Position - _ddStationaryAnchor).LengthSq() >= 0.75f * 0.75f)
        {
            _ddStationaryAnchor = Player.Position;
            _ddStationarySince = now;
            _hasDDStationaryAnchor = true;
            _ddRouteSamples.Clear();
            _lastDDRouteSampleAt = default;
            resetStationaryAnchor = true;
        }

        if (!resetStationaryAnchor && (_lastDDRouteSampleAt == default || now - _lastDDRouteSampleAt >= TimeSpan.FromMilliseconds(250)))
        {
            var routeDirection = decision.Destination is { } target ? target - Player.Position : default;
            _ddRouteSamples.Enqueue(new(now, Player.Position, Player.PosRot.Y, decision.Sequence, decision.Destination, decision.SearchGoalPosition, Hints.InteractWithTarget?.InstanceID ?? 0, Hints.ForcedMovement, routeDirection));
            DeepDungeonPolicy.TrimDiagnosticSamples(_ddRouteSamples, 60);
            _lastDDRouteSampleAt = now;
        }

        var stationaryFor = now - _ddStationarySince;
        var pathTraceMatchesStationary = decision.PathfindDiagnosticAt >= _ddStationarySince;
        var pathTraceMatchesObjectiveRequest = _ddObjectiveDiagnosticRequestedAt != default && decision.PathfindDiagnosticAt >= _ddObjectiveDiagnosticRequestedAt;
        var hasNewPathTrace = decision.PathfindDiagnostic != null
            && (pathTraceMatchesStationary || pathTraceMatchesObjectiveRequest)
            && decision.PathfindDiagnosticAt > _lastDDPathDiagnosticLoggedAt;
        var objectiveSnapshotReady = _ddObjectiveDiagnosticReason != null && hasNewPathTrace;
        if (stationaryFor < TimeSpan.FromSeconds(10) && !objectiveSnapshotReady
            || !hasNewPathTrace && _lastDDNavigationDiagnostic != default && now - _lastDDNavigationDiagnostic < TimeSpan.FromSeconds(30))
            return;

        float? ScoreAt(WPos? point) => point is { } pos ? Hints.GoalZones.Sum(goal => goal(pos)) : null;
        var interaction = Hints.InteractWithTarget;
        var goalHere = ScoreAt(Player.Position);
        var goalDestination = ScoreAt(decision.Destination);
        var goalWaypoint = ScoreAt(decision.NextWaypoint);
        var goalInteraction = ScoreAt(interaction?.Position);
        var destinationDistance = decision.Destination is { } destination ? (destination - Player.Position).Length() : float.NaN;
        var interactionDistance = interaction != null ? (interaction.Position - Player.Position).Length() : float.NaN;
        var movement = Manager.MovementDiagnostic;
        var movementSampleAge = movement is { SampleAtMilliseconds: > 0 } sampleAge ? Environment.TickCount64 - sampleAge.SampleAtMilliseconds : long.MaxValue;
        var movementSampleFresh = movementSampleAge <= 500;
        var interactionSuppressed = movementSampleFresh && movement is { InteractionMovementSuppressed: true, InteractionSampleAtMilliseconds: > 0 }
            && Environment.TickCount64 - movement.Value.InteractionSampleAtMilliseconds <= 500;
        var movementIssued = movementState == "movement direction issued";
        var castingSuppressed = movementSampleFresh && Player.CastInfo != null && movement is { MovementBlocked: true } && !interactionSuppressed;
        var diagnosticKind = DeepDungeonPolicy.ClassifyMovementDiagnostic(
            HasRepeatedRouteReversals(), World.DeepDungeon.IsBossFloor, interactionSuppressed, castingSuppressed,
            Player.InCombat, decision.Destination != null, movementIssued, goalHere, goalDestination);
        if (diagnosticKind == DeepDungeonPolicy.MovementDiagnosticKind.NavigationOscillation && Hints.DeepDungeonTreasureGoalID != 0)
        {
            Hints.DeepDungeonOscillatingTreasureID = Hints.DeepDungeonTreasureGoalID;
            Hints.DeepDungeonOscillationFloor = World.DeepDungeon.Floor;
            Hints.DeepDungeonOscillationAt = now;
        }
        var decisionAge = _lastDecisionUpdatedAt == default ? "unknown" : $"{(now - _lastDecisionUpdatedAt).TotalSeconds:F1}s";
        var requestAge = decision.RequestedAt == default ? "unknown" : $"{(now - decision.RequestedAt).TotalSeconds:F2}s";
        static string FormatPosition(WPos? position) => position is { } p ? $"(x:{p.X:F1},z:{p.Z:F1})" : "none";
        static string FormatVector(Vector3? direction) => direction is { } d ? $"(x:{d.X:F2},y:{d.Y:F2},z:{d.Z:F2})" : "none";

        var pathTrace = hasNewPathTrace ? $" {decision.PathfindDiagnostic}" : "";
        var routeSummary = SummarizeRouteSamples();
        var movementSummary = movement is { SampleAtMilliseconds: > 0 } sample
            ? $"movementInput(ageMs={Environment.TickCount64 - sample.SampleAtMilliseconds},desiredWorldXYZ={FormatVector(sample.DesiredDirection)},userLeftForward=({sample.UserMove.X:F2},{sample.UserMove.Z:F2}),actualLeftForward=({sample.ActualMove.X:F2},{sample.ActualMove.Z:F2}),outputLeftForward=({sample.FinalOutput.X:F2},{sample.FinalOutput.Z:F2}),additive={sample.AdditiveInput},inputEnabled={sample.InputEnabled1}/{sample.InputEnabled2},allowed={sample.MovementAllowed},blocked={sample.MovementBlocked},interactionSuppressed={sample.InteractionMovementSuppressed}/target:{sample.InteractionTargetID}/ageMs:{Environment.TickCount64 - sample.InteractionSampleAtMilliseconds},followPath={sample.FollowPathActive},misdirection={sample.Misdirection},hooks={sample.HookCalls},nonzero={sample.NonzeroOutputs},zero={sample.ZeroOutputs})"
            : "movementInput=unavailable";
        var objectiveSnapshot = objectiveSnapshotReady ? $",objectiveSnapshot={_ddObjectiveDiagnosticReason}" : "";
        Service.Log($"[AutoDD] movement diagnostic: classification={diagnosticKind}{objectiveSnapshot}, floor={World.DeepDungeon.Floor}, stationaryFor={stationaryFor.TotalSeconds:F0}s, playerXZ=(x:{Player.Position.X:F1},z:{Player.Position.Z:F1}),playerY={Player.PosRot.Y:F2},state={movementState},inCombat={Player.InCombat}, decision={decision.Sequence}, requestedAt={decision.RequestedAt:HH:mm:ss.fff}, builtAt={decision.BuildCompletedAt:HH:mm:ss.fff},diagnosticCompletedAt={decision.DiagnosticCompletedAt:HH:mm:ss.fff},requestAge={requestAge}, requestedPositionXZ=(x:{decision.RequestedPosition.X:F1},z:{decision.RequestedPosition.Z:F1}),requestedHeightY={decision.RequestedHeightY:F2}, pathDestination={FormatPosition(decision.PathfindDestination)},searchGoal={FormatPosition(decision.SearchGoalPosition)},effectiveDestination={FormatPosition(decision.Destination)}@{destinationDistance:F1}, nextWaypoint={FormatPosition(decision.NextWaypoint)}, decisionConsumedAge={decisionAge}, buildMs={decision.BuildDurationMs:F1}, diagnosticMs={decision.DiagnosticDurationMs:F1}, timeToGoal={decision.TimeToGoal:F1}s, leeway={decision.LeewaySeconds:F1}s, interaction={(interaction == null ? "none" : $"{interaction.InstanceID}@{interactionDistance:F1}")}, goals={Hints.GoalZones.Count}, forbidden={Hints.ForbiddenZones.Count}, goalScores(here={goalHere?.ToString("F2") ?? "n/a"},destination={goalDestination?.ToString("F2") ?? "n/a"},waypoint={goalWaypoint?.ToString("F2") ?? "n/a"},interaction={goalInteraction?.ToString("F2") ?? "n/a"}), casting={Player.CastInfo != null}, {movementSummary}, {routeSummary}.{pathTrace}");
        if (hasNewPathTrace)
        {
            _lastDDPathDiagnosticLoggedAt = decision.PathfindDiagnosticAt;
            if (objectiveSnapshotReady)
            {
                _ddObjectiveDiagnosticReason = null;
                _ddObjectiveDiagnosticRequestedAt = default;
                _ddObjectiveDiagnosticRequested = false;
                _ddPathDiagnosticRequested = false;
            }
        }
        _lastDDNavigationDiagnostic = now;
    }

    private string SummarizeRouteSamples()
    {
        if (_ddRouteSamples.Count < 2)
            return $"routeSamples={_ddRouteSamples.Count}";

        var samples = _ddRouteSamples.ToArray();
        var traveled = 0f;
        var verticalTravel = 0f;
        var destinationChanges = 0;
        var interactionChanges = 0;
        var routeDirections = new WDir[samples.Length];
        var nonzeroHints = 0;
        for (var i = 1; i < samples.Length; ++i)
        {
            traveled += (samples[i].Position - samples[i - 1].Position).Length();
            verticalTravel += MathF.Abs(samples[i].HeightY - samples[i - 1].HeightY);
            if (samples[i].Destination is { } current && samples[i - 1].Destination is { } previous && (current - previous).LengthSq() > 4)
                ++destinationChanges;
            if (samples[i].Interaction != samples[i - 1].Interaction)
                ++interactionChanges;
            routeDirections[i] = samples[i].RouteDirection;
            if (samples[i].RequestedDirection is { } requested && requested != default)
                ++nonzeroHints;
        }
        routeDirections[0] = samples[0].RouteDirection;
        var reversals = DeepDungeonPolicy.CountDirectionReversals(routeDirections);
        var net = (samples[^1].Position - samples[0].Position).Length();
        var verticalNet = samples[^1].HeightY - samples[0].HeightY;
        var decisions = string.Join(",", samples.Where((sample, index) => index == 0 || index == samples.Length - 1 || sample.Decision != samples[index - 1].Decision)
            .Take(5).Select(sample => $"{sample.Decision}@{(sample.Destination is { } d ? $"x:{d.X:F1},z:{d.Z:F1}" : "none")}/interact:{sample.Interaction}"));
        return $"route(samples={samples.Length},span={(samples[^1].At - samples[0].At).TotalSeconds:F1}s,netXZ={net:F2},netY={verticalNet:F2},traveledXZ={traveled:F2},verticalTravel={verticalTravel:F2},reversals={reversals},destinationChanges={destinationChanges},interactionChanges={interactionChanges},nonzeroHints={nonzeroHints},decisions=[{decisions}])";
    }

    public override void Execute(StrategyValues strategy, ref Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        // do nothing if we're already being moved by some other module (i.e. quest battle pathfinding)
        if (Hints.ForcedMovement != null)
            return;

        // lots of assumptions made in this module are broken by being in flight (or diving)
        // e.g. being inside an obstacle is fine, AOEs may not reach the player depending on vertical distance, etc
        if (World.Client.Flying)
            return;

        // if the player on a ranged job pulls a dungeon boss from outside (e.g. Mistwake B1), pathfinder won't force it to move inside the arena, since their position isn't in the pathfinding map
        // TODO: what should the generic solution be? do we need multiple sets of bounds?
        // forcing the player to move directly toward the arena center works fine in this basic case, but would be terrible for other content
        //   - hunt marks can be hundreds of units away
        //   - araid/foray bosses are often located on an isolated platform with a clientpath leading to it, so VBM would just run directly forward into the abyss
        if (Bossmods.ActiveModule is { Info.Category: BossModuleInfo.Category.Dungeon or BossModuleInfo.Category.Variant } module && !module.Arena.InBounds(Player.Position))
        {
            Hints.ForcedMovement = Player.DirectionTo(module.Arena.Center).ToVec3();
            return;
        }

        var castOpt = strategy.Option(Track.Cast);
        var castStrategy = castOpt.As<CastStrategy>();
        if (castStrategy is CastStrategy.FinishInstants or CastStrategy.DropInstants)
        {
            Hints.MaxCastTime = 0;
            Hints.ForceCancelCast |= castStrategy == CastStrategy.DropInstants;
        }

        var allowSpecialModes = strategy.Option(Track.SpecialModes).As<SpecialModesStrategy>() == SpecialModesStrategy.Automatic;
        if (allowSpecialModes)
        {
            if (Player.PendingKnockbacks.Count > 0)
                return; // do not move if there are any unresolved knockbacks - the positions are taken at resolve time, so we might fuck things up

            if (Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Pyretic && Hints.ImminentSpecialMode.activation <= World.FutureTime(1))
            {
                Hints.ForceCancelCast = true; // this is only useful if autopyretic tweak is disabled
                return; // pyretic is imminent, do not move
            }

            if (Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.PyreticMove && Hints.ImminentSpecialMode.activation <= World.FutureTime(1))
                return;

            if (Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Freezing && Hints.ImminentSpecialMode.activation <= World.FutureTime(0.5f) && Player.PosRot == Player.PrevPosRot)
                Hints.WantJump = true;
        }

        if (Hints.InteractWithTarget != null)
        {
            var targetPos = Hints.InteractWithTarget.Position;
            // strongly prefer moving towards interact target
            Hints.AddGoalZone(p =>
            {
                var length = (p - targetPos).Length();

                // 99% of eventobjects have an interact range of 3.5y, while the rest have a range of 2.09y
                // checking only for the shorter range here would be fine in the vast majority of cases, but it can break interact pathfinding in the case that the target object is partially covered by a forbidden zone with a radius between 2.1 and 3.5
                // this is specifically an issue in the metal gear thancred solo duty in endwalker
                return length <= 2.09f ? 101 : length <= 3.5f ? 100 : 0;
            }, $"interaction-attraction:{Hints.InteractWithTarget.InstanceID}");
        }

        // fallback so that we can automatically start some quest battles xddd (the RP rotation is a component on the module, which isn't active until we pull, so no goal zone)
        if (Hints.GoalZones.Count == 0 && primaryTarget is { IsAlly: false, IsDead: false } && Player.Statuses.Any(s => RotationModuleManager.TransformationStatuses.Contains(s.ID)))
            Hints.AddGoalZone(Hints.GoalSingleTarget(primaryTarget, Player, World.Actors, 3), $"rotation-fallback-target:{primaryTarget.InstanceID}");

        var isSpinning = Player.FindStatus(SID.Spinning) != null;
        // simulate forward forced movement; this is kind of a hack, but it definitely doesn't belong in modules because it's part of the movement constraint
        if (isSpinning)
        {
            Hints.AddForbiddenZone(ShapeDistance.Rect(Player.Position, Player.Rotation, SpinningLookahead, SpinningLookahead + 2, SpinningLookahead + 2), World.FutureTime(2));
            Hints.AddForbiddenZone(ShapeDistance.Cone(Player.Position, 100, Player.Rotation + 180.Degrees(), 45.Degrees()), DateTime.MaxValue);
        }

        if (Player.FindStatus(SID.ThinIce) is { } thinIce)
        {
            var distance = thinIce.Extra * 0.1f;
            Hints.AddForbiddenZone(ShapeDistance.Donut(Player.Position, 1, distance - 1), World.FutureTime(2));
        }

        foreach (var enemy in Hints.PotentialTargets.Where(e => e.Actor.TargetID == Player.InstanceID))
        {
            if (enemy.CanMove && enemy.DesiredPosition is { } pos)
                Hints.AddGoalZone(Hints.PullTargetToLocation(enemy.Actor, pos, Player, GCD, 0.5f, enemy.Priority >= 0), $"rotation-pull-target:{enemy.Actor.InstanceID}");

            if (enemy.DesiredRotation is { } rot)
            {
                var dist = (enemy.Actor.Position - Player.Position).Length();
                var goal = enemy.Actor.Position + rot.ToDirection() * enemy.Actor.HitboxRadius;
                var sh = ShapeDistance.PrecisePosition(goal, new(0, 1), Hints.PathfindMapBounds.MapResolution, Player.Position, 0.1f);
                Hints.AddGoalZone(p => sh(p) >= 0 ? 10 : 0, $"rotation-positional:{enemy.Actor.InstanceID}");
            }
        }

        var speed = World.Client.MoveSpeed;
        var destinationOpt = strategy.Option(Track.Destination);
        var destinationStrategy = destinationOpt.As<DestinationStrategy>();
        var cushionStrategy = strategy.Option(Track.ForbiddenZoneCushion).As<ForbiddenZoneCushionStrategy>();
        var cushionSize = cushionStrategy switch
        {
            ForbiddenZoneCushionStrategy.Small => 0.5f,
            ForbiddenZoneCushionStrategy.Medium => 1.5f,
            ForbiddenZoneCushionStrategy.Large => 3.0f,
            _ => 0f
        };
        var movementDelay = DelaySeconds(strategy.Option(Track.DelayMovement).As<DelayMovementStrategy>());
        var separateDodgeDelay = strategy.Option(Track.SeparateDodgeDelay).As<SeparateDodgeDelayStrategy>() == SeparateDodgeDelayStrategy.Enabled;
        var dodgeDelay = DelaySeconds(strategy.Option(Track.DodgeDelayMovement).As<DelayMovementStrategy>());
        NavigationDecision navi = default;
        var resetStats = true;
        switch (destinationStrategy)
        {
            case DestinationStrategy.Pathfind:
                navi = GetDecision(speed, cushionSize, RequestDeepDungeonPathDiagnostic());
                resetStats = false;
                var isDodge = navi.LeewaySeconds < float.MaxValue;
                var delay = separateDodgeDelay
                    ? (isDodge ? dodgeDelay : movementDelay)
                    : movementDelay;
                if (separateDodgeDelay && _delayMovementIsDodge != isDodge)
                {
                    _delayMovementIsDodge = isDodge;
                    TimeToMove = delay > 0 ? World.FutureTime(delay) : null;
                }
                else if (delay > 0)
                    TimeToMove ??= World.FutureTime(delay);
                else
                    TimeToMove = null;
                break;
            case DestinationStrategy.Explicit:
                navi = new() { Destination = ResolveTargetLocation(destinationOpt.Value), TimeToGoal = destinationOpt.Value.ExpireIn };
                break;
        }

        if (resetStats)
        {
            _lastDecision = default;
            Manager.LastPathfindMs = 0;
            Manager.LastRasterizeMs = 0;
        }

        if (isSpinning)
        {
            if (Hints.SpinDirection == null && navi.Destination is { } wp)
                Hints.SpinDirection = Player.DirectionTo(wp).ToAngle();

            return;
        }

        if (navi.Destination == null)
        {
            TimeToMove = null;
            _delayMovementIsDodge = null;
            LogDeepDungeonNavigation(navi, "no pathfinder destination");
            return; // nothing to do
        }

        if (World.CurrentTime < TimeToMove)
        {
            LogDeepDungeonNavigation(navi, "movement delay active");
            return; // delaying movement
        }

        var rangeOpt = strategy.Option(Track.Range);
        var rangeStrategy = rangeOpt.As<RangeStrategy>();
        if (rangeStrategy != RangeStrategy.Any && Player.InCombat)
        {
            var rangeReference = ResolveTarget(rangeOpt.Value) ?? primaryTarget;
            if (rangeReference != null)
            {
                // TODO: instead of hardcoding, is it possible to reuse goal zones for this purpose?
                // it would allow greeding AOE actions as well, but requires modification to NavigationDecision to avoid duplicating work
                var effectiveRange = Player.Role is Role.Tank or Role.Melee ? MeleeRange : CasterRange;
                var toDestination = navi.Destination.Value - rangeReference.Position;
                var maxRange = Player.HitboxRadius + rangeReference.HitboxRadius + effectiveRange - GreedTolerance;
                var range = toDestination.Length();
                if (range > maxRange)
                {
                    var uptimePosition = rangeReference.Position + maxRange / range * toDestination;
                    var uptimeToDestinationTime = (range - maxRange) / speed;
                    switch (rangeStrategy)
                    {
                        case RangeStrategy.MaxRange:
                            navi.Destination = uptimePosition;
                            navi.LeewaySeconds -= uptimeToDestinationTime; // assume we'll want to reach destination later, so leeway has to be reduced
                            break;
                        case RangeStrategy.GreedGCDExplicit:
                        case RangeStrategy.GreedLastMomentExplicit:
                            navi.LeewaySeconds = destinationOpt.Value.ExpireIn - uptimeToDestinationTime;
                            if (navi.LeewaySeconds > (rangeStrategy == RangeStrategy.GreedGCDExplicit ? GCD : 0))
                                navi.Destination = uptimePosition;
                            break;

                        case RangeStrategy.GreedAutomatic:
                            var uptimeCell = CtxInactive.Map.GridToIndex(CtxInactive.Map.WorldToGrid(uptimePosition));
                            var curCell = CtxInactive.ThetaStar.StartNodeIndex;
                            if (navi.LeewaySeconds > 0)
                            {
                                if (CtxInactive.Map.PixelMaxG.BoundSafeAt(uptimeCell) >= CtxInactive.Map.PixelMaxG.BoundSafeAt(curCell))
                                    navi.Destination = uptimePosition;
                                else if (Player.DistanceToHitbox(primaryTarget) <= maxRange)
                                    navi.Destination = Player.Position;
                            }
                            break;
                    }
                }
                // else: destination is already in our effective range, nothing to adjust here
            }
        }

        var dir = navi.Destination.Value - Player.Position;
        var distSq = dir.LengthSq();
        if (distSq <= 0.01f)
        {
            // we're already very close to destination
            Hints.ForcedMovement = default;
            LogDeepDungeonNavigation(navi, "already at navigation destination");
            return;
        }

        // we want to move somewhere, check whether we're allowed to
        if (allowSpecialModes && Hints.ImminentSpecialMode.mode == AIHints.SpecialMode.Misdirection && Hints.ImminentSpecialMode.activation <= World.CurrentTime)
        {
            // special case for misdirection
            // assume it's always fine to drop casts during misdirection (add new option to the specialmode track if it's ever not the case, i guess...)
            // we have only two options really - either move to the current forced direction, or wait (and this direction will change) - so see whether moving now brings us closer to the destination
            // if our destination is not the last one (turn != 0), we can only move if it will move us *further* from second-next point - otherwise we're moving towards the wall
            // the tolerance angle can be inferred from following consideration: in the worst case our movement should keep us at the same distance to destination (or it can move us closer)
            // so let's consider isosceles triangle with legs equal to distance to target, and base equal to distance we move over a period of time - the base angle is then our threshold
            // this means that cos(threshold) = speed * dt / 2 / distance
            // assuming we wanna move at least for a second, speed is standard 6, threshold of 60 degrees would be fine for distances >= 6
            // for micro adjusts, if we move for 1 frame (1/60s), threshold of 60 degrees would be fine for distance 0.1, which is our typical threshold
            var threshold = 30.Degrees();
            var allowMovement = World.Client.ForcedMovementDirection.AlmostEqual(Angle.FromDirection(dir), threshold.Rad);
            if (allowMovement && destinationStrategy == DestinationStrategy.Pathfind)
            {
                // if we have a map, we can try to see if current direction has long enough unobstructed path
                // TODO: maybe just check a single closest grid cell that we would intersect if we go forward?..
                allowMovement = CalculateUnobstructedPathLength(World.Client.ForcedMovementDirection) >= Math.Min(4, distSq);
            }
            Hints.ForcedMovement = allowMovement ? World.Client.ForcedMovementDirection.ToDirection().ToVec3() : default;

            //var halfThreshold = Hints.MisdirectionThreshold; // even much smaller threshold seems to work fine in practice (TODO: reconsider...)
            //var idealDir = Angle.FromDirection(dir);
            //if (destinationStrategy == DestinationStrategy.Pathfind)
            //{
            //    var lenL = CalculateUnobstructedPathLength(idealDir + halfThreshold);
            //    var lenR = CalculateUnobstructedPathLength(idealDir - halfThreshold);
            //    if (lenL < 4)
            //        idealDir -= halfThreshold;
            //    if (lenR < 4)
            //        idealDir += halfThreshold;
            //}
            //var withinThreshold = World.Client.ForcedMovementDirection.AlmostEqual(idealDir, halfThreshold.Rad);
            //Hints.ForcedMovement = withinThreshold ? World.Client.ForcedMovementDirection.ToDirection().ToVec3(Player.PosRot.Y) : default;
        }
        else
        {
            // fine to move if we won't interrupt cast (or are explicitly allowed to)
            var allowMovement = Player.CastInfo == null || Player.CastInfo.EventHappened || castStrategy is CastStrategy.DropMove or CastStrategy.DropInstants;
            Hints.ForcedMovement = allowMovement ? dir.ToVec3() : default;
        }

        LogDeepDungeonNavigation(navi, Hints.ForcedMovement == default ? "movement suppressed or no direction" : "movement direction issued");

        var maxCastTime = castStrategy switch
        {
            CastStrategy.Leeway => navi.LeewaySeconds,
            CastStrategy.Explicit => castOpt.Value.ExpireIn,
            CastStrategy.Greedy => float.MaxValue,
            _ => 0,
        };
        Hints.MaxCastTime = Math.Max(0, Math.Min(Hints.MaxCastTime, maxCastTime));
        Hints.ForceCancelCast |= castStrategy == CastStrategy.DropMove;
        if (castStrategy is CastStrategy.Leeway && Player.CastInfo is { } castInfo)
        {
            var effectiveCastRemaining = Math.Max(0, castInfo.RemainingTime - 0.5f);
            if (Hints.EffectiveMaxCastTime < effectiveCastRemaining)
            {
                Hints.ForceCancelCast = true;
                // no leeway, cast might have been initiated by user, keep moving
                Hints.ForcedMovement = dir.ToVec3();
            }
        }
    }

    private float CalculateUnobstructedPathLength(Angle dir)
    {
        var start = CtxInactive.Map.WorldToGrid(Player.Position);
        if (!CtxInactive.Map.InBounds(start.x, start.y))
            return 0;

        var end = CtxInactive.Map.WorldToGrid(Player.Position + 100 * dir.ToDirection());
        var startG = CtxInactive.Map.PixelMaxG[CtxInactive.Map.GridToIndex(start.x, start.y)];
        foreach (var p in CtxInactive.Map.EnumeratePixelsInLine(start.x, start.y, end.x, end.y))
        {
            if (!CtxInactive.Map.InBounds(p.x, p.y) || CtxInactive.Map.PixelMaxG[CtxInactive.Map.GridToIndex(p.x, p.y)] < startG)
            {
                var dest = CtxInactive.Map.GridToWorld(p.x, p.y, 0.5f, 0.5f);
                return (dest - Player.Position).LengthSq();
            }
        }
        return float.MaxValue;
    }
}
