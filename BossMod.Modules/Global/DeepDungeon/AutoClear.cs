using BossMod.Pathfinding;
using Dalamud.Plugin.Ipc;

namespace BossMod.Global.DeepDungeon;

enum OID : uint
{
    CairnPalace = 0x1EA094,
    BeaconHoH = 0x1EA9A3,
    PylonEO = 0x1EB867,
    PylonPT = 0x1EBE24,
    SilverCoffer = 0x1EA13D,
    GoldCoffer = 0x1EA13E,
    BandedCofferIndicator = 0x1EA1F6,
    BandedCoffer = 0x1EA1F7,
}

enum SID : uint
{
    Silence = 7,
    Pacification = 620,
    HPPenalty = 1089,
    DamageDown = 1090,
    Amnesia = 1092,
    ItemPenalty = 1094,

    PhysicalDamageUp = 53,
    DamageUp = 61,
    DreadBeastAura = 2056, // unnamed status, displays red fog vfx on actor
    EvasionUp = 2402, // applied by Peculiar Light from orthos diplocaulus

    StoneCurse = 437, // petrification (on enemies)
    AutoHealPenalty = 1097,
}

public abstract partial class AutoClear : ZoneModule
{
    public record struct GoalObject(Actor Actor, GoalType Type, float PlayerDistSq, int Tier = 0)
    {
        public readonly bool IsTreasure => Type is GoalType.Treasure or GoalType.ChestHoard;
    }

    public enum GoalType
    {
        ChestHoard,
        Hoard,
        Passage,
        Treasure,
    }

    public readonly int LevelCap;
    private readonly DeepDungeonTreasureSelection _treasureSelection = new();
    private DeepDungeonPolicy.PassageSelection _passageSelection;
    private string TreasureSelectionContext => $"{Palace.DungeonId}/{Palace.Floor}/{World.CurrentZone}";
    private string? PotentialTrapObjectiveKey => MostInterestingObject is { } goal
        ? $"{TreasureSelectionContext}/actor:{goal.Type}:{goal.Actor.InstanceID}"
        : RoomRejoinContext is { } room ? $"{room}/room-route" : null;

    public static readonly HashSet<uint> BronzeChestIDs = [
        // PotD
        782, 783, 784, 785, 786, 787, 788, 789, 790, 802, 803, 804, 805,
        // HoH
        1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049,
        // EO
        1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1550, 1551, 1552, 1553, 1554,
        // PT
        1881, 1882, 1883, 1884, 1885, 1886, 1887, 1888, 1889, 1890, 1891, 1892, 1893, 1906, 1907, 1908
    ];
    public static readonly HashSet<uint> RevealedTrapOIDs = [0x1EA08E, 0x1EA08F, 0x1EA090, 0x1EA091, 0x1EA092, 0x1EA9A0, 0x1EB864, 0x1EBEDB];

    protected readonly List<(Actor Source, float Inner, float Outer, Angle HalfAngle)> Donuts = [];
    protected readonly List<(Actor Source, float Radius)> Circles = [];
    protected readonly List<(Actor Source, float Radius)> KnockbackZones = [];
    protected readonly List<(Actor Source, AOEShape Zone, DateTime Activation, int Counter)> Voidzones = [];
    private readonly List<Gaze> Gazes = [];
    protected readonly List<Actor> Interrupts = [];
    protected readonly List<Actor> Stuns = [];
    protected readonly List<(Actor Actor, DateTime Timeout)> Spikes = [];
    protected readonly List<Actor> HintDisabled = [];
    private readonly List<Actor> LOS = [];

    private readonly Dictionary<ulong, (WPos, Bitmap)> _losCache = [];

    public record class Gaze(Actor Source, AOEShape Shape);

    protected readonly AutoDDConfig _config = Service.Config.Get<AutoDDConfig>();
    private readonly EventSubscriptions _subscriptions;

    private readonly Dictionary<ulong, PomanderID> _chestContentsGold = [];
    private readonly Dictionary<ulong, int> _chestContentsSilver = [];
    private readonly Dictionary<ulong, string> _lastOvercapDecision = [];
    private readonly Dictionary<PomanderID, (DateTime RequestedAt, byte Count, byte Flags)> _pendingPomanderReadbacks = [];
    private readonly Dictionary<DeepDungeonPolicy.PomanderMaintenanceKind, DeepDungeonPolicy.PomanderMaintenanceRequest> _pomanderMaintenanceRequests = [];
    private readonly Dictionary<DeepDungeonPolicy.PomanderMaintenanceKind, string> _lastPomanderMaintenanceDecision = [];
    private readonly HashSet<(DeepDungeonState.DungeonType Dungeon, byte Floor, DeepDungeonPolicy.PomanderMaintenanceKind Kind)> _confirmedFloorPomanderUses = [];
    private readonly HashSet<(DeepDungeonState.DungeonType Dungeon, byte Floor, PomanderID Pomander)> _hohFloorPomanderConfirmedUses = [];
    private readonly Dictionary<PomanderID, DateTime> _hohPomanderProposals = [];
    private readonly HashSet<(DeepDungeonState.DungeonType Dungeon, byte Floor, PomanderID Pomander)> _hohFloorPomanderEffectsLogged = [];
    private readonly Dictionary<PomanderID, string> _lastHoHPomanderDecision = [];
    private long _lastAttributedInteractionSequence;
    private string? _lastOvercapCorrelationLogKey;
    private DateTime _lastOvercapCorrelationLogAt;
    private int _suppressedOvercapCorrelationLogs;
    private readonly HashSet<ulong> _fakeExits = [];
    private readonly DeepDungeonState.PomanderState[] _lastPomanderSnapshot = [];
    private bool _pomanderSnapshotReady;
    private int[] _floorEntryPomanderCounts = [];
    private bool _floorEntryPomanderSnapshotReady;
    private bool _autoWitchingPullHandled;
    private string? _lastPomanderReadinessBlocker;
    private string? _lastHoHPomanderReadinessBlocker;
    private DeepDungeonState.DungeonType _observedDungeon;
    private byte _observedFloor;
    private DeepDungeonPolicy.SafetyFloorUsage _safetyFloorUsage;
    private DateTime _floorStartPomanderWindowAt;
    private readonly Dictionary<ulong, DateTime> _deferredTreasureAt = [];
    private sealed class TreasureProgress
    {
        internal DateTime LastSampleAt;
        internal DateTime LastSelectedAt;
        internal TimeSpan EligibleNoProgress;
        internal float BestDistance;
        internal WPos LastPlayerPosition;
        internal bool DiagnosticAt10SecondsRequested;
        internal bool DiagnosticBeforeDeferralRequested;
    }
    private readonly Dictionary<ulong, TreasureProgress> _treasureProgress = [];

    private readonly List<GoalObject> InterestingObjects = [];
    private GoalObject? MostInterestingObject;

    private int Kills;
    private bool BetweenFloors = true;
    private bool? _lastAutoDDEenabled;
    private DeepDungeonPolicy.FloorInitializationGate _floorInitializationGate;
    private string? _lastAutoDDBlocker;
    private string? _trackedObjectiveKey;
    private string? _trackedObjectiveDescription;
    private DateTime _objectiveStartedAt;
    private DateTime _lastObjectiveProgressAt;
    private DateTime _lastObjectiveStallLogAt;
    private float _bestObjectiveDistance;
    private string? _lastGeometryState;
    private string? _lastSelectedActorGoalKey;
    private DateTime _lastActorGoalSelectionLogAt;
    private int _pendingActorGoalSelectionChanges;
    private string? _lastSelectedActorGoalType;
    private ulong _lastSelectedActorGoalID;
    private string? _lastActorSelectionContext;
    private readonly Dictionary<string, int> _actorSelectionTransitions = [];
    private int _otherActorSelectionTransitions;
    private DateTime _lastTrapActivationAt;
    private string? _lastTrapActivation;

    private const uint StrengthDamageUpStatus = 687;
    private const uint StrengthHealingPotencyUpStatus = 1099;
    private const uint SteelVulnerabilityDownStatus = 1100;

    private bool PassageActorGoalAllowed => DeepDungeonPolicy.ShouldAddPassageActorGoal(
        DesiredRoom.Reason != PathfindReason.Nothing,
        DesiredRoom.Reason == PathfindReason.OpenPassage,
        DesiredRoom.Reason == PathfindReason.Manual);

    private void LogEnableState()
    {
        if (_lastAutoDDEenabled == _config.Enable)
            return;
        _lastAutoDDEenabled = _config.Enable;
        Service.Log($"[AutoDD] {(_config.Enable ? "enabled" : "disabled")}: dungeon={Palace.DungeonId}, floor={Palace.Floor}, destination={(DesiredRoom.Reason == PathfindReason.Manual ? $"manual room {DesiredRoom.Index}" : DesiredRoom.Reason == PathfindReason.Nothing ? "none" : $"automatic room {DesiredRoom.Index}")}.");
    }

    private void LogAutoDDBlocker(string? blocker)
    {
        if (_lastAutoDDBlocker == blocker)
            return;
        _lastAutoDDBlocker = blocker;
        if (blocker != null)
            Service.Log($"[AutoDD] navigation/objectives paused: {blocker}; dungeon={Palace.DungeonId}, floor={Palace.Floor}.");
        else
            Service.Log($"[AutoDD] navigation/objectives unblocked: dungeon={Palace.DungeonId}, floor={Palace.Floor}.");
    }

    private void SetDesiredRoom((int Index, PathfindReason Reason) destination, string cause)
    {
        if (DesiredRoom == destination)
            return;
        var previous = DesiredRoom;
        DesiredRoom = destination;
        if (destination != previous)
        {
            _fullClearRevealWaitRoom = 0;
            _fullClearRevealWaitStarted = default;
            _fullClearRevealStallLogged = false;
        }
        ResetObjectiveProgress(destination.Reason == PathfindReason.Nothing ? cause : null);
        Service.Log($"[AutoDD] destination {(destination.Reason == PathfindReason.Nothing ? "cleared" : previous.Reason == PathfindReason.Nothing ? "selected" : "replaced")}: from={(previous.Reason == PathfindReason.Nothing ? "none" : $"{previous.Index}/{ReasonReadable(previous.Reason)}")}, to={(destination.Reason == PathfindReason.Nothing ? "none" : $"{destination.Index}/{ReasonReadable(destination.Reason)}")}, cause={cause}.");
    }

    private void ClearDesiredRoom(string cause) => SetDesiredRoom(default, cause);

    private void ResetObjectiveProgress(string? cause)
    {
        if (_trackedObjectiveKey == null)
            return;
        if (cause != null)
            Service.Log($"[AutoDD] objective tracking stopped: objective={_trackedObjectiveDescription}, cause={cause}.");
        _trackedObjectiveKey = null;
        _trackedObjectiveDescription = null;
        _objectiveStartedAt = default;
        _lastObjectiveProgressAt = default;
        _lastObjectiveStallLogAt = default;
        _bestObjectiveDistance = 0;
    }

    private void LogObjectiveProgress(Actor player, AIHints hints, int numAggro, bool canNavigate)
    {
        var objectiveKey = DesiredRoom.Reason != PathfindReason.Nothing
            ? $"room:{DesiredRoom.Index}:{DesiredRoom.Reason}"
            : MostInterestingObject is { } goal ? $"actor:{goal.Actor.InstanceID}:{goal.Type}" : null;
        var objectiveDescription = DesiredRoom.Reason != PathfindReason.Nothing
            ? $"room {DesiredRoom.Index} ({ReasonReadable(DesiredRoom.Reason)})"
            : MostInterestingObject is { } obj ? $"{obj.Type} actor={obj.Actor.InstanceID}" : null;
        if (objectiveKey == null)
        {
            ResetObjectiveProgress("no active objective");
            return;
        }

        float distance;
        if (DesiredRoom.Reason != PathfindReason.Nothing)
        {
            if (!TryGetCurrentRoomRects(out var rects, out _) || DesiredRoom.Index is < 0 or >= 25)
                return;
            distance = MathF.Sqrt(DistanceBox2D(player.Position, rects[DesiredRoom.Index]));
        }
        else
            distance = MathF.Sqrt((player.Position - MostInterestingObject!.Value.Actor.Position).LengthSq());

        var now = World.CurrentTime;
        if (_trackedObjectiveKey != objectiveKey)
        {
            ResetObjectiveProgress(null);
            _trackedObjectiveKey = objectiveKey;
            _trackedObjectiveDescription = objectiveDescription;
            _objectiveStartedAt = _lastObjectiveProgressAt = now;
            _bestObjectiveDistance = distance;
            Service.Log($"[AutoDD] objective tracking started: objective={objectiveDescription}, distance={distance:F1}.");
            return;
        }

        if (DeepDungeonPolicy.HasMeaningfulProgress(_bestObjectiveDistance, distance))
        {
            var stalledFor = now - _lastObjectiveProgressAt;
            if (_lastObjectiveStallLogAt != default)
                Service.Log($"[AutoDD] objective progress resumed: objective={_trackedObjectiveDescription}, distance={distance:F1}, noProgressFor={stalledFor.TotalSeconds:F0}s.");
            _bestObjectiveDistance = distance;
            _lastObjectiveProgressAt = now;
            _lastObjectiveStallLogAt = default;
            return;
        }

        var noProgressFor = now - _lastObjectiveProgressAt;
        if (DeepDungeonPolicy.ShouldLogStall(noProgressFor, _lastObjectiveStallLogAt, now))
        {
            var pause = DeepDungeonPolicy.ObjectiveStallBlocker(!canNavigate, hints.InteractWithTarget != null, player.CastInfo != null, player.InCombat, hints.ForbiddenZones.Count > 0);
            if (!canNavigate)
                pause += $" ({numAggro}/{_config.MaxPull})";
            var actorGoalActive = canNavigate && MostInterestingObject != null
                && (MostInterestingObject.Value.Type != GoalType.Passage || PassageActorGoalAllowed);
            var navigationOwner = DeepDungeonPolicy.ObjectiveNavigationOwner(DesiredRoom.Reason != PathfindReason.Nothing, MostInterestingObject?.Type.ToString(), actorGoalActive);
            var age = now - _objectiveStartedAt;
            var candidateSummary = string.Join(",", InterestingObjects.OrderBy(o => (o.Type, o.PlayerDistSq)).Take(6)
                .Select(o => $"{o.Type}:{o.Actor.InstanceID}@{MathF.Sqrt(o.PlayerDistSq):F1}/g{hints.GoalZones.Sum(goal => goal(o.Actor.Position)):F2}"));
            var currentGoalScore = hints.GoalZones.Sum(goal => goal(player.Position));
            var actorGoalScore = MostInterestingObject is { } selected ? hints.GoalZones.Sum(goal => goal(selected.Actor.Position)) : 0;
            var roomGoalScore = DesiredRoom.Reason != PathfindReason.Nothing && TryGetCurrentRoomRects(out var rects, out _) && DesiredRoom.Index is >= 0 and < 25
                ? hints.GoalZones.Sum(goal => goal(rects[DesiredRoom.Index].Pos)) : 0;
            var interactionTarget = hints.InteractWithTarget is { } target ? $"{target.InstanceID}" : "none";
            if (DeepDungeonPolicy.ShouldRequestObjectivePathDiagnostic(
                DesiredRoom.Reason != PathfindReason.Nothing, player.InCombat, MostInterestingObject != null,
                hints.InteractWithTarget != null, !canNavigate, player.CastInfo != null))
                hints.DeepDungeonDiagnosticRequest = $"room={DesiredRoom.Index}/{ReasonReadable(DesiredRoom.Reason)},noProgress={noProgressFor.TotalSeconds:F0}s,playerRoom={PlayerRoom}";
            Service.Log($"[AutoDD] objective no-progress: objective={_trackedObjectiveDescription}, age={age.TotalSeconds:F0}s, noProgressFor={noProgressFor.TotalSeconds:F0}s, distance={distance:F1}, playerRoom={PlayerRoom}, blocker={pause},navigationOwner={navigationOwner}; navSelection=room:{(DesiredRoom.Reason == PathfindReason.Nothing ? "none" : $"{DesiredRoom.Index}/{ReasonReadable(DesiredRoom.Reason)}")},actor:{(MostInterestingObject is { } selectedActor ? $"{selectedActor.Type}/{selectedActor.Actor.InstanceID}" : "none")},interaction={interactionTarget},candidates=[{candidateSummary}],goalScores(current={currentGoalScore:F2},actor={actorGoalScore:F2},room={roomGoalScore:F2},zones={hints.GoalZones.Count}); observing only, no automatic recovery.");
            _lastObjectiveStallLogAt = now;
        }
    }

    private void LogSelectedActorGoal(Actor player)
    {
        var key = MostInterestingObject is { } selected ? $"{selected.Type}:{selected.Actor.InstanceID}" : "none";
        if (_lastSelectedActorGoalKey != key)
        {
            var selectedType = MostInterestingObject?.Type.ToString() ?? "none";
            if (_lastSelectedActorGoalType is { } previousType)
            {
                var previousInCandidates = _lastSelectedActorGoalID != 0 && InterestingObjects.Any(o => o.Actor.InstanceID == _lastSelectedActorGoalID);
                var previousEligible = previousInCandidates && InterestingObjects.Any(o => o.Actor.InstanceID == _lastSelectedActorGoalID
                    && DeepDungeonPolicy.ShouldConsiderActorForNavigation(o.Type == GoalType.Passage, PassageActorGoalAllowed));
                var reason = DeepDungeonPolicy.ActorSelectionTransitionReason(previousInCandidates, previousEligible);
                var transition = $"{previousType}>{selectedType}/{reason}";
                if (_actorSelectionTransitions.TryGetValue(transition, out var count))
                    _actorSelectionTransitions[transition] = count + 1;
                else if (_actorSelectionTransitions.Count < 16)
                    _actorSelectionTransitions[transition] = 1;
                else
                    ++_otherActorSelectionTransitions;
            }
            _lastSelectedActorGoalType = selectedType;
            _lastSelectedActorGoalKey = key;
            _lastSelectedActorGoalID = MostInterestingObject?.Actor.InstanceID ?? 0;
            _lastActorSelectionContext = $"selected={key},playerRoom={PlayerRoom},playerXZ=({player.Position.X:F1},{player.Position.Z:F1}),candidateCount={InterestingObjects.Count}";
            ++_pendingActorGoalSelectionChanges;
        }

        var now = World.CurrentTime;
        if (_pendingActorGoalSelectionChanges == 0 || _lastActorGoalSelectionLogAt != default && now - _lastActorGoalSelectionLogAt < TimeSpan.FromSeconds(5))
            return;

        var candidates = string.Join(",", InterestingObjects.OrderBy(o => (o.Type, o.PlayerDistSq)).Take(6)
            .Select(o => $"{o.Type}:{o.Actor.InstanceID}@{MathF.Sqrt(o.PlayerDistSq):F1}/room:{(TryFindClosestRoom(o.Actor.Position, out var actorRoom) ? actorRoom.Index.ToString() : "?")}/near:{IsInThisRoomOrAdjacent(o.Actor)}"));
        var chosen = MostInterestingObject is { } goal ? $"{goal.Type}:{goal.Actor.InstanceID}@{MathF.Sqrt(goal.PlayerDistSq):F1}" : "none";
        var transitions = string.Join(",", _actorSelectionTransitions.Select(t => $"{t.Key}:{t.Value}"));
        var passageRooms = string.Join(",", World.Actors
            .Where(a => Palace.PassageActive && !player.InCombat && (a.OID == (uint)OID.CairnPalace || a.OID == (uint)OID.BeaconHoH || a.OID == (uint)OID.PylonEO || a.OID == (uint)OID.PylonPT))
            .Take(6)
            .Select(a => $"{a.InstanceID}:room:{(TryFindClosestRoom(a.Position, out var passageRoom) ? passageRoom.Index.ToString() : "?")}/near:{IsInThisRoomOrAdjacent(a)}/retained:{_passageSelection.CanRetain(TreasureSelectionContext, a.InstanceID)}/goalAllowed:{PassageActorGoalAllowed}"));
        Service.Log($"[AutoDD] actor navigation selection changed: selected={chosen}, changesSinceLastLog={_pendingActorGoalSelectionChanges}, transitions=[{transitions}{(_otherActorSelectionTransitions > 0 ? $",other:{_otherActorSelectionTransitions}" : "")}], lastTransitionContext={_lastActorSelectionContext}, rule=goal type then retained treasure then distance among movement-eligible actors, playerRoom={PlayerRoom}, playerXZ=({player.Position.X:F1},{player.Position.Z:F1}),inCombat={player.InCombat}, passageActive={Palace.PassageActive}, passageActorGoalAllowed={PassageActorGoalAllowed}, candidates=[{candidates}],passageRooms=[{passageRooms}].");
        _lastActorGoalSelectionLogAt = now;
        _pendingActorGoalSelectionChanges = 0;
        _actorSelectionTransitions.Clear();
        _otherActorSelectionTransitions = 0;
    }

    protected struct PlayerImmuneState
    {
        public DateTime RoleBuffExpire; // 0 if not active
        public DateTime JobBuffExpire; // 0 if not active
        public bool KnockbackPenalty;

        public readonly bool ImmuneAt(DateTime time) => KnockbackPenalty || RoleBuffExpire > time || JobBuffExpire > time;
    }

    private readonly PlayerImmuneState[] _playerImmunes = new PlayerImmuneState[4];

    private readonly ObstacleMapManager _obstacles;

    protected DeepDungeonState Palace => World.DeepDungeon;

    private readonly ICallGateSubscriber<ushort, List<Vector3>> _palantirGetTraps = Service.PluginInterface.GetIpcSubscriber<ushort, List<Vector3>>("Palantir.GetTraps");
    private readonly ICallGateSubscriber<ushort, Vector3[]> _palantirListTraps = Service.PluginInterface.GetIpcSubscriber<ushort, Vector3[]>("Palantir.Traps");

    const float TrapRadius = 1.75f;

    protected AutoClear(WorldState ws, int LevelCap) : base(ws)
    {
        this.LevelCap = LevelCap;
        _obstacles = new(ws);
        _lastPomanderSnapshot = [.. ws.DeepDungeon.Pomanders];
        _observedDungeon = ws.DeepDungeon.DungeonId;
        _observedFloor = ws.DeepDungeon.Floor;
        _pomanderSnapshotReady = DeepDungeonPolicy.IsFloorContextReady((int)_observedDungeon, _observedFloor) && _lastPomanderSnapshot.Any(pomander => pomander.Usable);

        _subscriptions = new(
            ws.DeepDungeon.ProgressChanged.Subscribe(OnProgressChanged),
            ws.SystemLogMessage.Subscribe(OnSystemLogMessage),
            ws.Actors.CastStarted.Subscribe(OnCastStarted),
            ws.Actors.CastFinished.Subscribe(OnCastFinished),
            ws.Actors.CastEvent.Subscribe(HandleCastEvent),
            ws.Actors.Added.Subscribe(HandleActorAdd),
            ws.Actors.InCombatChanged.Subscribe(OnActorCombatChanged),
            ws.Actors.StatusGain.Subscribe(OnActorStatusGain),
            ws.Actors.StatusLose.Subscribe(OnActorStatusLose),
            ws.Actors.IsDeadChanged.Subscribe(OnActorDeadChanged),
            ws.Actors.EventObjectAnimation.Subscribe(OnEObjAnim),
            ws.Client.ActionRequested.Subscribe(op => OnClientActionRequested(op.Request)),
            ws.DeepDungeon.PomandersChanged.Subscribe(OnPomandersChanged),
            ws.DeepDungeon.MapDataChanged.Subscribe(_ => TryInitializeFloor("map data update", mapDataUpdate: true))
        );

        LoadedFloors = Utils.LoadResource<Dictionary<string, Floor<Wall>>>("BossMod.Global.DeepDungeon.Walls.json");
        ProblematicTrapLocations = Utils.LoadResource<List<WPos>>("BossMod.Global.DeepDungeon.BadTraps.json");

        TryInitializeFloor("module attach");
    }

    private void OnProgressChanged(DeepDungeonState.OpProgressChange _)
    {
        var previousDungeon = _observedDungeon;
        var previousFloor = _observedFloor;
        _observedDungeon = Palace.DungeonId;
        _observedFloor = Palace.Floor;

        var validContext = DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor);
        if (!validContext)
        {
            if (DeepDungeonPolicy.IsFloorContextReady((int)previousDungeon, previousFloor) && !BetweenFloors)
                ClearState();
            ResetFloorScopedNavigationRecovery();
            _floorEntryPomanderCounts = [];
            _floorEntryPomanderSnapshotReady = false;
            _floorInitializationGate.BeginTransition();
            ResetPomanderSnapshot();
            return;
        }

        if (Palace.DungeonId != previousDungeon)
        {
            ResetFloorScopedNavigationRecovery();
            CaptureFloorEntryPomanderSnapshot();
            if (previousDungeon != DeepDungeonState.DungeonType.None || !BetweenFloors)
                ClearState();
            _floorStartPomanderWindowAt = World.CurrentTime;
            _floorInitializationGate.ObserveContextChange();
            ResetPomanderSnapshot();
            Service.Log($"[AutoDD] dungeon context changed: dungeon={Palace.DungeonId}, floor={Palace.Floor}; awaiting current floor map before room initialization.");
        }
        else if (Palace.Floor != previousFloor)
        {
            ResetFloorScopedNavigationRecovery();
            CaptureFloorEntryPomanderSnapshot();
            _confirmedFloorPomanderUses.RemoveWhere(use => use.Dungeon != Palace.DungeonId || use.Floor != Palace.Floor);
            _hohFloorPomanderConfirmedUses.RemoveWhere(use => use.Dungeon != Palace.DungeonId || use.Floor != Palace.Floor);
            _hohPomanderProposals.Clear();
            _hohFloorPomanderEffectsLogged.RemoveWhere(use => use.Dungeon != Palace.DungeonId || use.Floor != Palace.Floor);
            if (!BetweenFloors)
                ClearState();
            _floorStartPomanderWindowAt = World.CurrentTime;
            _floorInitializationGate.ObserveContextChange();
            _pendingPomanderReadbacks.Clear();
            Service.Log($"[AutoDD] floor context changed: dungeon={Palace.DungeonId}, floor={Palace.Floor}; awaiting current floor map before room initialization.");
        }

        if (Palace.DungeonId != previousDungeon)
        {
            _hohFloorPomanderConfirmedUses.Clear();
            _hohPomanderProposals.Clear();
            _hohFloorPomanderEffectsLogged.Clear();
            foreach (var (kind, request) in _pomanderMaintenanceRequests.ToArray())
                if (request.Dungeon != Palace.DungeonId)
                {
                    _pomanderMaintenanceRequests.Remove(kind);
                    _lastPomanderMaintenanceDecision.Remove(kind);
                }
        }
    }

    private void ResetPomanderSnapshot()
    {
        _pomanderSnapshotReady = false;
        _pendingPomanderReadbacks.Clear();
    }

    private void CaptureFloorEntryPomanderSnapshot()
    {
        _floorEntryPomanderCounts = [.. Palace.Pomanders.Select(pomander => (int)pomander.Count)];
        _floorEntryPomanderSnapshotReady = true;
        var safetySlot = Palace.GetPomanderSlot(MaintenancePomander(DeepDungeonPolicy.PomanderMaintenanceKind.Safety));
        var sightSlot = Palace.GetPomanderSlot(MaintenancePomander(DeepDungeonPolicy.PomanderMaintenanceKind.Sight));
        var safetyCount = safetySlot is >= 0 && safetySlot < _floorEntryPomanderCounts.Length ? _floorEntryPomanderCounts[safetySlot] : 0;
        var sightCount = sightSlot is >= 0 && sightSlot < _floorEntryPomanderCounts.Length ? _floorEntryPomanderCounts[sightSlot] : 0;
        Service.Log($"[AutoDD] floor-entry pomander stock captured: dungeon={Palace.DungeonId}, floor={Palace.Floor}, Safety={safetyCount}, Sight={sightCount}.");
        if (Palace.DungeonId == DeepDungeonState.DungeonType.HOH)
            Service.Log($"[AutoDD] HoH floor-entry stock snapshot: floor={Palace.Floor}, Petrification={Palace.GetPomanderState(PomanderID.Petrification).Count}, Frailty={Palace.GetPomanderState(PomanderID.Frailty).Count}; snapshot time={World.CurrentTime:O}.");
    }

    private void ResetFloorScopedNavigationRecovery()
    {
        _passageSelection = default;
        _floorStartPomanderWindowAt = default;
        _autoWitchingPullHandled = false;
        _deferredTreasureAt.Clear();
        ResetTreasureProgress();
    }

    private void TryInitializeFloor(string cause, bool mapDataUpdate = false)
    {
        if (!BetweenFloors || !_floorInitializationGate.CanInitialize(
            DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor),
            Palace.Rooms.Any(room => room != 0), mapDataUpdate))
            return;

        Service.Log($"[AutoDD] floor map initialized: cause={cause}, dungeon={Palace.DungeonId}, floor={Palace.Floor}, territory={World.CurrentZone}, tileset={DeepDungeonPolicy.DiagnosticTileset(Palace.IsBossFloor, Palace.Progress.Tileset)}, activeRooms={Palace.Rooms.Count(room => room != 0)}.");
        if (!_floorEntryPomanderSnapshotReady)
            CaptureFloorEntryPomanderSnapshot();
        _floorStartPomanderWindowAt = _floorStartPomanderWindowAt == default ? World.CurrentTime : _floorStartPomanderWindowAt;
        LoadWalls();
        LoadGeometry();
        FilterTraps(reloadProvider: true);
        BetweenFloors = false;
        _floorInitializationGate.MarkInitialized();
    }

    protected override void Dispose(bool disposing)
    {
        _pomanderMaintenanceRequests.Clear();
        _subscriptions.Dispose();
        _obstacles.Dispose();
        base.Dispose(disposing);
    }

    public override void OnWindowClose()
    {
        _config.Enable = false;
        _config.EnableMinimap = false;
        _config.Modified.Fire();
    }

    protected virtual void OnCastStarted(Actor actor) { }

    protected virtual void OnCastFinished(Actor actor) { }
    private void HandleCastEvent(Actor actor, ActorCastEvent ev)
    {
        HandleTrap(actor, ev);
        OnEventCast(actor, ev);
    }
    protected virtual void OnEventCast(Actor actor, ActorCastEvent ev) { }

    private void OnActorStatusGain(Actor actor, int index)
    {
        var status = actor.Statuses[index];

        switch (status.ID)
        {
            case (uint)WHM.SID.Surecast:
            case (uint)WAR.SID.ArmsLength:
                if (World.Party.TryFindSlot(actor.InstanceID, out var slot1))
                    _playerImmunes[slot1].RoleBuffExpire = status.ExpireAt;
                break;
            case (uint)WAR.SID.InnerStrength:
                if (World.Party.TryFindSlot(actor.InstanceID, out var slot2))
                    _playerImmunes[slot2].JobBuffExpire = status.ExpireAt;
                break;
            // Knockback Penalty floor effect
            case 1096:
            case 1512:
                if (World.Party.TryFindSlot(actor.InstanceID, out var slot3))
                    _playerImmunes[slot3].KnockbackPenalty = true;
                break;
        }

        OnStatusGain(actor, status);
    }

    protected virtual void OnStatusGain(Actor actor, ActorStatus status) { }

    private void OnActorStatusLose(Actor actor, int index)
    {
        var status = actor.Statuses[index];
        OnStatusLose(actor, status);
    }

    protected virtual void OnStatusLose(Actor actor, ActorStatus status) { }

    protected virtual void OnActorCombatChanged(Actor actor)
    {
        if (!actor.InCombat || actor.IsAlly || actor.Type != ActorType.Enemy)
            return;

        var player = World.Party.Player();
        var distance = player != null ? (actor.Position - player.Position).Length() : float.NaN;
        var nearbyTrap = RecentTrapSummary();
        Service.Log($"[AutoDD] enemy entered combat: floor={Palace.Floor}, enemy={actor.Name}:{actor.InstanceID}, position=({actor.Position.X:F1},{actor.Position.Z:F1}), playerDistance={distance:F1}, playerRoom={(player != null && TryFindClosestRoom(player.Position, out var room) ? room.Index.ToString() : "?")}, aggroPlayer={actor.AggroPlayer}, recentTrap={nearbyTrap}.");
    }

    private void OnActorDeadChanged(Actor actor)
    {
        if (!actor.IsAlly && actor.IsDead)
            Kills++;

        if (World.Party.Player()?.InstanceID != actor.InstanceID)
            return;

        if (actor.IsDead)
        {
            var enemies = World.Actors
                .Where(a => a.Type == ActorType.Enemy && !a.IsAlly && !a.IsDeadOrDestroyed && (a.InCombat || a.AggroPlayer))
                .OrderBy(a => (a.Position - actor.Position).LengthSq())
                .Take(5)
                .Select(a => $"{a.Name}:{a.InstanceID}@{(a.Position - actor.Position).Length():F1}/combat:{a.InCombat}/aggro:{a.AggroPlayer}");
            Service.Log($"[AutoDD] player death observed: floor={Palace.Floor}, position=({actor.Position.X:F1},{actor.Position.Z:F1}), recentTrap={RecentTrapSummary()}, engagedEnemies={string.Join(",", enemies)}; trap causality unconfirmed.");
        }
        else
            Service.Log($"[AutoDD] player revival observed: floor={Palace.Floor}, position=({actor.Position.X:F1},{actor.Position.Z:F1}); objective tracking may resume.");
    }

    private string RecentTrapSummary()
    {
        var age = World.CurrentTime - _lastTrapActivationAt;
        return _lastTrapActivation != null && age <= TimeSpan.FromSeconds(30)
            ? $"{_lastTrapActivation},age={age.TotalSeconds:F1}s"
            : "none";
    }

    private void OnSystemLogMessage(WorldState.OpSystemLogMessage op)
    {
        switch (op.MessageId)
        {
            case 7222: // pomander overcap
                if (op.Args.Length == 0 || op.Args[0] <= 0 || op.Args[0] >= (int)PomanderID.Count)
                {
                    Service.Log($"[AutoDD] overcap correlation rejected: message=7222, reason=invalid pomander argument ({(op.Args.Length > 0 ? op.Args[0] : "missing")}).");
                    break;
                }
                CorrelateOvercapMessage("pomander", (uint)OID.GoldCoffer, (PomanderID)op.Args[0]);
                break;
            case 7248: // transference initiated
                ClearState();
                _floorInitializationGate.BeginTransition();
                break;
            case 7255: // all traps on the current floor have been removed (effect confirmation)
                if (Palace.DungeonId != DeepDungeonState.DungeonType.None && Palace.Floor > 0)
                {
                    _safetyFloorUsage = DeepDungeonPolicy.ObserveSafetyFloorUsage(_safetyFloorUsage, Palace.DungeonId, Palace.Floor, effectActive: true);
                    _confirmedFloorPomanderUses.Add((Palace.DungeonId, Palace.Floor, DeepDungeonPolicy.PomanderMaintenanceKind.Safety));
                }
                if (_pomanderMaintenanceRequests.TryGetValue(DeepDungeonPolicy.PomanderMaintenanceKind.Safety, out var safetyRequest)
                    && DeepDungeonPolicy.IsMatchingSafetyUseMessage(safetyRequest, Palace.DungeonId, Palace.Floor, World.CurrentTime))
                {
                    _pomanderMaintenanceRequests.Remove(DeepDungeonPolicy.PomanderMaintenanceKind.Safety);
                    Service.Log($"[AutoDD] Safety effect confirmed by system log 7255 (all traps removed): requestedFloor={safetyRequest.Floor}, stockAtRequest={safetyRequest.StockAtRequest}, stockDecrementObserved={safetyRequest.StockDecrementObserved}; stock remains separate evidence.");
                }
                else
                    Service.Log($"[AutoDD] Safety effect system log 7255 not correlated: dungeon={Palace.DungeonId}, floor={Palace.Floor}; no preceding unresolved same-floor request.");
                _trapsCurrentFloor.Clear();
                break;
            case 7256: // sight used
                if (Palace.DungeonId != DeepDungeonState.DungeonType.None && Palace.Floor > 0)
                    _confirmedFloorPomanderUses.Add((Palace.DungeonId, Palace.Floor, DeepDungeonPolicy.PomanderMaintenanceKind.Sight));
                if (_pomanderMaintenanceRequests.TryGetValue(DeepDungeonPolicy.PomanderMaintenanceKind.Sight, out var sightRequest)
                    && DeepDungeonPolicy.IsMatchingSightUseMessage(sightRequest, Palace.DungeonId, Palace.Floor, World.CurrentTime))
                {
                    _pomanderMaintenanceRequests.Remove(DeepDungeonPolicy.PomanderMaintenanceKind.Sight);
                    Service.Log($"[AutoDD] Sight use confirmed by system log 7256: requestedFloor={sightRequest.Floor}, stockAtRequest={sightRequest.StockAtRequest}, stockDecrementObserved={sightRequest.StockDecrementObserved}; stock remains separate evidence.");
                }
                else
                    Service.Log($"[AutoDD] Sight use observed from system log 7256: dungeon={Palace.DungeonId}, floor={Palace.Floor}; floor use recorded even without a correlated request.");
                _trapsCurrentFloor.Clear();
                break;
            case 9208: // magicite overcap
            case 10287: // demiclone overcap
                CorrelateOvercapMessage(op.MessageId == 9208 ? "magicite" : "demiclone", (uint)OID.SilverCoffer, null);
                break;
            case 11251:
                if (op.Args[1] == 4) // mazeroot balm used, reveals map and traps
                    _trapsCurrentFloor.Clear();
                break;
        }
    }

    private void CorrelateOvercapMessage(string kind, uint expectedChestOID, PomanderID? pomander)
    {
        var now = World.CurrentTime;
        var attribution = DeepDungeonPolicy.CorrelateOvercapMessage(
            now, (int)Palace.DungeonId, Palace.Floor, expectedChestOID, World.DeepDungeonInteractions.Snapshot(now), _lastAttributedInteractionSequence);
        var status = attribution.Status;
        if (status == DeepDungeonPolicy.OvercapAttributionStatus.Matched)
        {
            _lastAttributedInteractionSequence = attribution.InteractionSequence;
            var chest = World.Actors.Find(attribution.ActorID);
            if (chest == null || !chest.IsTargetable || chest.OID != expectedChestOID || !DeepDungeonInteractionHistory.IsTreasureInteractionTarget(chest.Type, chest.OID))
                status = DeepDungeonPolicy.OvercapAttributionStatus.ActorUnavailable;
            else if (pomander is { } pid)
                _chestContentsGold[chest.InstanceID] = pid;
            else
                _chestContentsSilver[chest.InstanceID] = 1;
        }

        var candidates = string.Join(",", attribution.CandidateActorIDs);
        var logKey = $"{kind}/{pomander}/{status}/{attribution.ActorID}/{candidates}/{Palace.DungeonId}/{Palace.Floor}";
        if (logKey == _lastOvercapCorrelationLogKey && now - _lastOvercapCorrelationLogAt < TimeSpan.FromSeconds(5))
        {
            ++_suppressedOvercapCorrelationLogs;
            return;
        }
        var suppressed = _suppressedOvercapCorrelationLogs;
        _suppressedOvercapCorrelationLogs = 0;
        _lastOvercapCorrelationLogKey = logKey;
        _lastOvercapCorrelationLogAt = now;
        Service.Log($"[AutoDD] overcap correlation: kind={kind}, pomander={pomander?.ToString() ?? "n/a"}, result={status}, evidenceAge={attribution.EvidenceAge.TotalSeconds:F2}s, selectedChest={(attribution.ActorID == 0 ? "none" : attribution.ActorID.ToString())}, candidateCount={attribution.CandidateCount}, candidates=[{candidates}], dungeon={Palace.DungeonId}, floor={Palace.Floor}, suppressedIdentical={suppressed}; interaction issuance is not proof of server outcome, stock consumption, or effect.");
    }

    private void OnClientActionRequested(ClientActionRequest request)
    {
        if (request.Action.Type != ActionType.Pomander || Palace.DungeonId == DeepDungeonState.DungeonType.None || request.Action.ID is 0 or >= (uint)PomanderID.Count)
            return;

        var pid = (PomanderID)request.Action.ID;
        var state = Palace.GetPomanderState(pid);
        if (Palace.DungeonId == DeepDungeonState.DungeonType.HOH && pid is PomanderID.Frailty or PomanderID.Petrification)
        {
            _hohPomanderProposals.Remove(pid);
            Service.Log($"[AutoDD] HoH floor pomander action observed: item={pid}, floor={Palace.Floor}, stock={state.Count}, active={state.Active}, source=action request; consumption/effect not yet confirmed.");
        }
        if (pid is PomanderID.Witching or PomanderID.ProtoWitching && !Palace.IsBossFloor)
            _autoWitchingPullHandled = true;
        if (MaintenanceKindFor(pid) is { } kind)
        {
            var player = World.Party.Player();
            bool? effectPresent = player != null ? MaintenanceEffectPresent(player, kind, state) : null;
            if (_pomanderMaintenanceRequests.TryGetValue(kind, out var previous))
                Service.Log($"[AutoDD] overlapping tracked pomander action request observed: item={pid}, floor={Palace.Floor}, originalFloor={previous.Floor}, originalRequestAge={(World.CurrentTime - previous.RequestedAt).TotalSeconds:F1}s, originalPhase={previous.Phase}; original verification deadline retained, source unknown.");
            else
            {
                _pomanderMaintenanceRequests[kind] = new(
                    Palace.DungeonId, Palace.Floor, pid, World.CurrentTime, state.Count, effectPresent, effectPresent == false, false,
                    DeepDungeonPolicy.PomanderMaintenanceRequestPhase.AwaitingReadback,
                    kind == DeepDungeonPolicy.PomanderMaintenanceKind.Serenity && player != null ? SerenityTriggerSummary(player) : null);
                Service.Log($"[AutoDD] tracked pomander effect request observed: item={pid}, floor={Palace.Floor}, stock={state.Count}, itemEffectAtRequest={effectPresent?.ToString() ?? "unavailable"}, evidenceAtRequest={_pomanderMaintenanceRequests[kind].EvidenceAtRequest ?? "n/a"}, source=unknown; stock/effect unconfirmed.");
            }
        }
        if (!_pomanderSnapshotReady)
        {
            Service.Log($"[AutoDD] pomander request observed: pomander={pid}, seq={request.SourceSequence}, stock={state.Count}, usable={state.Usable}, active={state.Active}; state baseline not ready, readback correlation skipped.");
            return;
        }
        _pendingPomanderReadbacks[pid] = (World.CurrentTime, state.Count, state.Flags);
        Service.Log($"[AutoDD] pomander request observed: pomander={pid}, seq={request.SourceSequence}, stock={state.Count}, usable={state.Usable}, active={state.Active}; request observed, consumption/effect not confirmed.");
    }

    private void OnPomandersChanged(DeepDungeonState.OpPomandersChange _)
    {
        var validContext = DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor);
        if (!validContext)
        {
            Array.Copy(Palace.Pomanders, _lastPomanderSnapshot, Palace.Pomanders.Length);
            _pomanderSnapshotReady = false;
            return;
        }

        if (!DeepDungeonPolicy.ShouldReportPomanderChange(validContext, _pomanderSnapshotReady))
        {
            Array.Copy(Palace.Pomanders, _lastPomanderSnapshot, Palace.Pomanders.Length);
            _pomanderSnapshotReady = DeepDungeonPolicy.ShouldEstablishPomanderBaseline(validContext, false, Palace.Pomanders.Any(pomander => pomander.Usable));
            return;
        }

        var now = World.CurrentTime;
        for (var slot = 0; slot < Palace.Pomanders.Length; ++slot)
        {
            var previous = _lastPomanderSnapshot[slot];
            var current = Palace.Pomanders[slot];
            _lastPomanderSnapshot[slot] = current;
            if (previous == current)
                continue;

            var pid = Palace.GetPomanderID(slot);
            if (pid == PomanderID.None)
                continue;

            var countChange = previous.Count == current.Count ? "unchanged" : $"{previous.Count}->{current.Count}";
            var flagsChange = previous.Flags == current.Flags ? "unchanged" : $"0x{previous.Flags:X2}->0x{current.Flags:X2}";
            Service.Log($"[AutoDD] pomander state readback changed: pomander={pid}, count={countChange}, flags={flagsChange}, usable={current.Usable}, active={current.Active}.");

            if (Palace.DungeonId == DeepDungeonState.DungeonType.HOH && pid is PomanderID.Frailty or PomanderID.Petrification
                && current.Count < previous.Count && _pendingPomanderReadbacks.TryGetValue(pid, out var useRequest)
                && DeepDungeonPolicy.IsWithinPomanderReadbackWindow(useRequest.RequestedAt, now))
            {
                _hohFloorPomanderConfirmedUses.Add((Palace.DungeonId, Palace.Floor, pid));
                _hohPomanderProposals.Remove(pid);
                Service.Log($"[AutoDD] HoH pomander stock consumption observed: item={pid}, floor={Palace.Floor}, stock={previous.Count}->{current.Count}; request temporally correlated, causation not proven.");
            }

            if (_pendingPomanderReadbacks.TryGetValue(pid, out var pending) && DeepDungeonPolicy.IsWithinPomanderReadbackWindow(pending.RequestedAt, now))
            {
                Service.Log($"[AutoDD] pomander readback temporally correlated: pomander={pid}, requestAge={(now - pending.RequestedAt).TotalSeconds:F1}s, stockAtRequest={pending.Count}, stockNow={current.Count}, activeAtRequest={(pending.Flags & 2) != 0}, activeNow={current.Active}; correlation is temporal, not proof of causation.");
                _pendingPomanderReadbacks.Remove(pid);
            }
        }
    }

    private void ExpirePomanderReadbacks()
    {
        var now = World.CurrentTime;
        foreach (var (pid, pending) in _pendingPomanderReadbacks.ToArray())
        {
            if (DeepDungeonPolicy.IsWithinPomanderReadbackWindow(pending.RequestedAt, now))
                continue;
            Service.Log($"[AutoDD] pomander request outcome unknown: pomander={pid}, age={(now - pending.RequestedAt).TotalSeconds:F0}s, stockAtRequest={pending.Count}; no matching stock/flag readback observed.");
            _pendingPomanderReadbacks.Remove(pid);
        }
    }

    private static DeepDungeonPolicy.PomanderMaintenanceKind? MaintenanceKindFor(PomanderID pid) => pid switch
    {
        PomanderID.Strength or PomanderID.ProtoStrength => DeepDungeonPolicy.PomanderMaintenanceKind.Strength,
        PomanderID.Steel or PomanderID.ProtoSteel => DeepDungeonPolicy.PomanderMaintenanceKind.Steel,
        PomanderID.Raising or PomanderID.ProtoRaising => DeepDungeonPolicy.PomanderMaintenanceKind.Raising,
        PomanderID.Safety or PomanderID.ProtoSafety => DeepDungeonPolicy.PomanderMaintenanceKind.Safety,
        PomanderID.Sight or PomanderID.ProtoSight => DeepDungeonPolicy.PomanderMaintenanceKind.Sight,
        PomanderID.Serenity or PomanderID.ProtoSerenity => DeepDungeonPolicy.PomanderMaintenanceKind.Serenity,
        PomanderID.Fortune or PomanderID.ProtoFortune => DeepDungeonPolicy.PomanderMaintenanceKind.Fortune,
        PomanderID.Intuition or PomanderID.ProtoIntuition => DeepDungeonPolicy.PomanderMaintenanceKind.Intuition,
        PomanderID.Affluence or PomanderID.ProtoAffluence => DeepDungeonPolicy.PomanderMaintenanceKind.Affluence,
        PomanderID.Flight or PomanderID.ProtoFlight => DeepDungeonPolicy.PomanderMaintenanceKind.Flight,
        _ => null,
    };

    private PomanderID MaintenancePomander(DeepDungeonPolicy.PomanderMaintenanceKind kind)
        => DeepDungeonPolicy.MaintenancePomander(Palace.DungeonId, kind);

    private bool? MaintenanceEffectPresent(Actor player, DeepDungeonPolicy.PomanderMaintenanceKind kind, DeepDungeonState.PomanderState itemState)
    {
        if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Serenity)
            return SerenityEffectEvidence(player);
        if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Safety)
            return false; // use is confirmed by system log 7255, not the item Active bit.
        return DeepDungeonPolicy.PomanderMaintenanceEffectPresent(
            kind,
            player.FindStatus(StrengthDamageUpStatus) != null,
            player.FindStatus(StrengthHealingPotencyUpStatus) != null,
            player.FindStatus(SteelVulnerabilityDownStatus) != null,
            itemState.Active,
            itemState.Active);
    }

    private bool? CurrentFloorGloom => DeepDungeonPolicy.CurrentFloorGloom(Palace.FloorEffectsSnapshot, Palace.DungeonId, Palace.Floor);
    private bool CurrentFloorGloomKnown => CurrentFloorGloom != null;
    private bool CurrentFloorGloomPresent => CurrentFloorGloom == true;

    private bool SerenityTriggerPresent(Actor player)
        => DeepDungeonPolicy.IsEnabledSerenityTrigger(
            player.FindStatus(SID.ItemPenalty) != null,
            player.FindStatus(SID.Amnesia) != null,
            CurrentFloorGloomPresent,
            player.FindStatus(SID.DamageDown) != null,
            player.FindStatus(SID.HPPenalty) != null,
            player.FindStatus(SID.AutoHealPenalty) != null,
            _config.SerenityOnItemPenalty, _config.SerenityOnAmnesia, _config.SerenityOnGloom,
            _config.SerenityOnDamageDown, _config.SerenityOnHPPenalty, _config.SerenityOnAutoHealPenalty);

    private bool? SerenityEffectEvidence(Actor player)
    {
        if (SerenityTriggerPresent(player))
            return false;
        if (_config.SerenityOnGloom && CurrentFloorGloom == null)
            return null;
        return true;
    }

    private string SerenityTriggerSummary(Actor player)
    {
        var statuses = new (string Name, bool Present, bool Selected)[]
        {
            ("ItemPenalty", player.FindStatus(SID.ItemPenalty) != null, _config.SerenityOnItemPenalty),
            ("Amnesia", player.FindStatus(SID.Amnesia) != null, _config.SerenityOnAmnesia),
            ("Gloom", CurrentFloorGloomPresent, _config.SerenityOnGloom),
            ("DamageDown", player.FindStatus(SID.DamageDown) != null, _config.SerenityOnDamageDown),
            ("HPPenalty", player.FindStatus(SID.HPPenalty) != null, _config.SerenityOnHPPenalty),
            ("AutoHealPenalty", player.FindStatus(SID.AutoHealPenalty) != null, _config.SerenityOnAutoHealPenalty),
        };
        var present = string.Join(",", statuses.Where(s => s.Present).Select(s => s.Name));
        var selected = string.Join(",", statuses.Where(s => s.Selected).Select(s => s.Name));
        var floorEffects = Palace.FloorEffectsSnapshot;
        return $"AutoSerenity={_config.AutoSerenity},present=[{present}],selected=[{selected}],selectedPresent=[{string.Join(",", statuses.Where(s => s.Present && s.Selected).Select(s => s.Name))}],floorEffectsKnown={floorEffects.Known && floorEffects.Dungeon == Palace.DungeonId && floorEffects.Floor == Palace.Floor},gloomKnown={CurrentFloorGloomKnown},gloom={floorEffects.Gloom},effectIDs={floorEffects.StatusID}/{floorEffects.BanID}/{floorEffects.DangerID}";
    }

    private void ReconcilePomanderMaintenance(Actor player)
    {
        foreach (var (kind, request) in _pomanderMaintenanceRequests.ToArray())
        {
            if (Palace.DungeonId != request.Dungeon || DeepDungeonPolicy.MaintenancePomander(request.Dungeon, kind) != request.Pomander)
                continue;

            var current = Palace.GetPomanderState(request.Pomander);
            if (Palace.Floor != request.Floor)
            {
                if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Intuition && current.Active)
                {
                    _confirmedFloorPomanderUses.Add((Palace.DungeonId, Palace.Floor, kind));
                    _pomanderMaintenanceRequests.Remove(kind);
                    Service.Log($"[AutoDD] Intuition detection active after floor transition: requestedFloor={request.Floor}, currentFloor={Palace.Floor}; active effect suppresses reuse, stock remains separate evidence.");
                    continue;
                }
                // A floor transition is not evidence that a current-floor effect was removed or a next-floor effect prepared.
                var stale = DeepDungeonPolicy.ObservePomanderMaintenanceRequest(request, current.Count, false, World.CurrentTime);
                if (stale != request)
                {
                    _pomanderMaintenanceRequests[kind] = stale;
                    if (stale.Phase == DeepDungeonPolicy.PomanderMaintenanceRequestPhase.OutcomeUnknown)
                        Service.Log($"[AutoDD] pomander outcome unknown after floor transition: item={request.Pomander}, requestedFloor={request.Floor}, currentFloor={Palace.Floor}; transition is not effect evidence, retries on the requested floor remain blocked.");
                }
                // One-shot floor uses are scoped to one floor. Preserve the unknown result in this
                // floor's log, but don't let it block a new, independent use on a later floor.
                if (Palace.Floor > 0 && DeepDungeonPolicy.IsFloorScopedMaintenanceKind(kind))
                {
                    Service.Log($"[AutoDD] prior-floor one-shot request retired from active checks: item={request.Pomander}, requestedFloor={request.Floor}, currentFloor={Palace.Floor}, phase={stale.Phase}, stockDecrementObserved={stale.StockDecrementObserved}, evidenceAtRequest={request.EvidenceAtRequest ?? "n/a"}; old-floor outcome remains unconfirmed, current-floor use is independent.");
                    _pomanderMaintenanceRequests.Remove(kind);
                }
                continue;
            }
            var effectPresent = MaintenanceEffectPresent(player, kind, current);
            var updated = DeepDungeonPolicy.ObservePomanderMaintenanceRequest(request, current.Count, effectPresent, World.CurrentTime);
            if (updated.Phase == DeepDungeonPolicy.PomanderMaintenanceRequestPhase.Resolved)
            {
                _pomanderMaintenanceRequests.Remove(kind);
                if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Serenity or DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition or DeepDungeonPolicy.PomanderMaintenanceKind.Affluence or DeepDungeonPolicy.PomanderMaintenanceKind.Flight)
                    _confirmedFloorPomanderUses.Add((request.Dungeon, request.Floor, kind));
                Service.Log($"[AutoDD] pomander item effect observed after request: item={request.Pomander}, requestedFloor={request.Floor}, evidence={MaintenanceEffectLabel(kind)}, effectAtRequest={request.EffectPresentAtRequest?.ToString() ?? "unavailable"}, evidenceAtRequest={request.EvidenceAtRequest ?? "n/a"}, effectWasAbsentAfterRequest={updated.EffectWasAbsentAfterRequest}, stockAtRequest={request.StockAtRequest}, stockNow={current.Count}; source/causation unconfirmed.");
            }
            else if (updated != request)
            {
                _pomanderMaintenanceRequests[kind] = updated;
                if (updated.Phase == DeepDungeonPolicy.PomanderMaintenanceRequestPhase.OutcomeUnknown)
                    Service.Log($"[AutoDD] pomander outcome unknown: item={request.Pomander}, requestedFloor={request.Floor}, stockAtRequest={request.StockAtRequest}, stockNow={current.Count}, stockDecrementObserved={updated.StockDecrementObserved}, itemEffectAtRequest={request.EffectPresentAtRequest?.ToString() ?? "unavailable"}, itemEffectNow={effectPresent}, evidenceAtRequest={request.EvidenceAtRequest ?? "n/a"}; same-floor same-item maintenance remains blocked until later effect evidence.");
                else if (updated.StockDecrementObserved)
                    Service.Log($"[AutoDD] pomander stock decrement observed: item={request.Pomander}, stock={request.StockAtRequest}->{current.Count}; awaiting item-effect evidence; source/causation unconfirmed.");
            }
        }
    }

    private static string MaintenanceEffectLabel(DeepDungeonPolicy.PomanderMaintenanceKind kind) => kind switch
    {
        DeepDungeonPolicy.PomanderMaintenanceKind.Strength => "player status 687/1099",
        DeepDungeonPolicy.PomanderMaintenanceKind.Steel => "player status 1100",
        DeepDungeonPolicy.PomanderMaintenanceKind.Raising => "Raising item Active flag",
        DeepDungeonPolicy.PomanderMaintenanceKind.Safety => "system log 7255: all traps removed",
        DeepDungeonPolicy.PomanderMaintenanceKind.Sight => "system log 7256: Sight used",
        DeepDungeonPolicy.PomanderMaintenanceKind.Serenity => "selected enchantments cleared",
        DeepDungeonPolicy.PomanderMaintenanceKind.Fortune => "Fortune item Active flag",
        DeepDungeonPolicy.PomanderMaintenanceKind.Intuition => "Intuition item Active flag",
        DeepDungeonPolicy.PomanderMaintenanceKind.Affluence => "next-floor Affluence item Active flag",
        DeepDungeonPolicy.PomanderMaintenanceKind.Flight => "next-floor Flight item Active flag",
        _ => "unknown",
    };

    private void LogPomanderMaintenanceDecision(DeepDungeonPolicy.PomanderMaintenanceKind kind, PomanderID pid, DeepDungeonState.PomanderState state, bool bossFloor, string reason, string? detail = null)
    {
        var decision = $"{Palace.Floor}/{pid}/{bossFloor}/{state.Count}/{state.Usable}/{reason}/{detail}";
        if (_lastPomanderMaintenanceDecision.TryGetValue(kind, out var previous) && previous == decision)
            return;
        _lastPomanderMaintenanceDecision[kind] = decision;
        var reserve = kind is DeepDungeonPolicy.PomanderMaintenanceKind.Raising or DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight or DeepDungeonPolicy.PomanderMaintenanceKind.Serenity or DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition or DeepDungeonPolicy.PomanderMaintenanceKind.Affluence or DeepDungeonPolicy.PomanderMaintenanceKind.Flight || bossFloor ? 0 : 1;
        Service.Log($"[AutoDD] pomander maintenance candidate: item={pid}, floor={Palace.Floor}, boss={bossFloor}, stock={state.Count}, usable={state.Usable}, reserve={reserve}, result={reason}{(detail != null ? $", {detail}" : "")}.");
    }

    private static bool HasPomanderAction(AIHints hints)
        => hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Pomander);

    private void TryQueueAutoWitching(Actor player, AIHints hints, bool bossFloor, int aggroCount)
    {
        var threshold = Math.Max(1, _config.AutoWitchingPullThreshold);
        if (aggroCount < threshold)
            _autoWitchingPullHandled = false;

        var pid = Palace.DungeonId switch
        {
            DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH => PomanderID.Witching,
            DeepDungeonState.DungeonType.EO => PomanderID.ProtoWitching,
            _ => PomanderID.None,
        };
        var slot = pid != PomanderID.None ? Palace.GetPomanderSlot(pid) : -1;
        var validSlot = slot is >= 0 and < DeepDungeonState.NumPomanderSlots;
        var state = validSlot ? Palace.Pomanders[slot] : default;
        var itemPenalty = player.FindStatus(SID.ItemPenalty) != null;
        if (!DeepDungeonPolicy.ShouldAutoUseWitching(
            _config.Enable && _config.AutoWitching && !BetweenFloors && _pomanderSnapshotReady,
            bossFloor, aggroCount, threshold, validSlot, state.Count, state.Usable, itemPenalty, _autoWitchingPullHandled))
            return;
        if (HasPomanderAction(hints))
            return;

        hints.ActionsToExecute.Push(new ActionID(ActionType.Pomander, (uint)pid), null, ActionQueue.Priority.VeryHigh);
        _autoWitchingPullHandled = true;
        Service.Log($"[AutoDD] automatic Witching proposed: item={pid}, floor={Palace.Floor}, aggroedEnemies={aggroCount}, threshold={threshold}, stock={state.Count}; one proposal per pull episode, dispatch/effect not confirmed.");
    }

    private void TryQueueAutoHoHPomanders(Actor player, AIHints hints, int livingTargets)
    {
        if (Palace.DungeonId != DeepDungeonState.DungeonType.HOH || Palace.IsBossFloor || BetweenFloors
            || !_config.Enable || !_pomanderSnapshotReady)
            return;

        var readinessBlocker = DeepDungeonPolicy.PomanderSchedulingReadinessBlocker(
            player.InstanceID != 0,
            Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas],
            Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51],
            Utils.IsScreenReady());
        if (readinessBlocker != _lastHoHPomanderReadinessBlocker)
        {
            if (readinessBlocker != null)
                Service.Log($"[AutoDD] HoH pomander policies blocked: {readinessBlocker}; no action proposed.");
            else if (_lastHoHPomanderReadinessBlocker != null)
                Service.Log("[AutoDD] HoH pomander policies unblocked: local player ready.");
            _lastHoHPomanderReadinessBlocker = readinessBlocker;
        }
        if (readinessBlocker != null || livingTargets == 0)
            return;

        if (player.IsDead)
        {
            LogHoHPomanderDecision(PomanderID.Petrification, "blocked: player dead");
            return;
        }

        if (!DeepDungeonPolicy.ShouldUseHoHPomanders(Palace.DungeonId, normalFloor: true, Palace.PassageActive))
        {
            LogHoHPomanderDecision(PomanderID.Petrification, "Frailty/Petrification policy stopped: passage active (Full Clear does not override)");
            return;
        }

        var petrificationState = Palace.GetPomanderState(PomanderID.Petrification);
        var frailtyState = Palace.GetPomanderState(PomanderID.Frailty);
        var petrificationSlot = Palace.GetPomanderSlot(PomanderID.Petrification);
        var frailtySlot = Palace.GetPomanderSlot(PomanderID.Frailty);
        var now = World.CurrentTime;
        foreach (var (pid, proposedAt) in _hohPomanderProposals.ToArray())
            if (!DeepDungeonPolicy.IsHoHPomanderProposalPending(proposedAt, now))
                _hohPomanderProposals.Remove(pid);
        var petrificationUsed = _hohFloorPomanderConfirmedUses.Contains((Palace.DungeonId, Palace.Floor, PomanderID.Petrification));
        var frailtyUsed = _hohFloorPomanderConfirmedUses.Contains((Palace.DungeonId, Palace.Floor, PomanderID.Frailty));
        var petrificationActive = World.Actors.Any(actor => actor.FindStatus(SID.StoneCurse) is { } curse && curse.ExpireAt > World.CurrentTime);
        if (petrificationState.Active)
        {
            LogHoHPomanderEffect(PomanderID.Petrification, "Petrification pomander Active flag observed; source/use attribution unconfirmed");
            petrificationActive = true;
        }
        if (petrificationActive)
        {
            if (!petrificationUsed)
                _hohFloorPomanderConfirmedUses.Add((Palace.DungeonId, Palace.Floor, PomanderID.Petrification));
            LogHoHPomanderEffect(PomanderID.Petrification, "enemy Stone Curse status 437 observed; source/use attribution unconfirmed");
            petrificationUsed = true;
        }

        var frailtyEnabled = _config.AutoFrailty;
        var petrificationEnabled = _config.AutoPetrification;
        var frailtyEligible = CanAutoUse(PomanderID.Frailty, player, out var frailtyReason, out frailtyState)
            && frailtySlot is >= 0 and < DeepDungeonState.NumPomanderSlots && !frailtyUsed && !frailtyState.Active;
        var petrificationEligible = CanAutoUse(PomanderID.Petrification, player, out var petrificationReason, out petrificationState)
            && petrificationSlot is >= 0 and < DeepDungeonState.NumPomanderSlots && !petrificationUsed && !petrificationActive && !petrificationState.Active;

        if (frailtyState.Active)
        {
            if (!frailtyUsed)
                _hohFloorPomanderConfirmedUses.Add((Palace.DungeonId, Palace.Floor, PomanderID.Frailty));
            LogHoHPomanderEffect(PomanderID.Frailty, "Frailty pomander Active flag observed; source/use attribution unconfirmed");
            frailtyEligible = false;
            frailtyUsed = true;
        }

        var selected = DeepDungeonPolicy.SelectHoHFloorClearPomander(
            petrificationEnabled, petrificationEligible, petrificationUsed,
            frailtyEnabled, frailtyEligible, frailtyUsed);
        if (selected == PomanderID.None)
        {
            LogHoHPomanderDecision(PomanderID.Petrification,
                $"no use: enabled(Petrification={petrificationEnabled},Frailty={frailtyEnabled}), stock(Petrification={petrificationState.Count},Frailty={frailtyState.Count}), eligible(Petrification={petrificationEligible},Frailty={frailtyEligible}), reason(Petrification={petrificationReason},Frailty={frailtyReason}), confirmedUsedThisFloor(Petrification={petrificationUsed},Frailty={frailtyUsed}), active(Petrification={petrificationActive || petrificationState.Active},Frailty={frailtyState.Active}), enemies={livingTargets}");
            return;
        }
        if (_hohPomanderProposals.ContainsKey(selected) || _pendingPomanderReadbacks.TryGetValue(selected, out var requested)
            && DeepDungeonPolicy.IsWithinPomanderReadbackWindow(requested.RequestedAt, now))
        {
            LogHoHPomanderDecision(selected, "proposal/action request pending confirmation; no use inferred");
            return;
        }
        if (HasPomanderAction(hints))
        {
            LogHoHPomanderDecision(selected, "deferred: another pomander action already owns this frame");
            return;
        }

        hints.ActionsToExecute.Push(new ActionID(ActionType.Pomander, (uint)selected), null, ActionQueue.Priority.VeryHigh);
        _hohPomanderProposals[selected] = now;
        LogHoHPomanderDecision(selected,
            $"proposal queued: selected={selected}, enabled(Petrification={petrificationEnabled},Frailty={frailtyEnabled}), stock(Petrification={petrificationState.Count},Frailty={frailtyState.Count}), eligible(Petrification={petrificationEligible},Frailty={frailtyEligible}), confirmedUsedThisFloor(Petrification={petrificationUsed},Frailty={frailtyUsed}), enemies={livingTargets}; retry after {DeepDungeonPolicy.HoHPomanderProposalTimeout.TotalSeconds:F0}s unless request/effect/stock evidence arrives");
    }

    private void LogHoHPomanderDecision(PomanderID pomander, string decision)
    {
        if (_lastHoHPomanderDecision.TryGetValue(pomander, out var previous) && previous == decision)
            return;
        _lastHoHPomanderDecision[pomander] = decision;
        Service.Log($"[AutoDD] HoH pomander decision: item={pomander}, floor={Palace.Floor}, {decision}.");
    }

    private void LogHoHPomanderEffect(PomanderID pomander, string evidence)
    {
        _hohFloorPomanderConfirmedUses.Add((Palace.DungeonId, Palace.Floor, pomander));
        if (!_hohFloorPomanderEffectsLogged.Add((Palace.DungeonId, Palace.Floor, pomander)))
            return;
        Service.Log($"[AutoDD] HoH pomander effect evidence: item={pomander}, floor={Palace.Floor}, evidence={evidence}; temporal evidence only, source/cause unconfirmed.");
    }

    private void TryQueuePomanderMaintenance(Actor player, AIHints hints, bool bossFloor, bool departureIntent = false)
    {
        if (!_config.Enable || BetweenFloors || !_pomanderSnapshotReady)
            return;

        var readinessBlocker = DeepDungeonPolicy.PomanderSchedulingReadinessBlocker(
            player.InstanceID != 0,
            Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas],
            Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51],
            Utils.IsScreenReady());
        if (readinessBlocker != _lastPomanderReadinessBlocker)
        {
            if (readinessBlocker != null)
                Service.Log($"[AutoDD] pomander maintenance blocked: {readinessBlocker}; no action request queued.");
            else if (_lastPomanderReadinessBlocker != null)
                Service.Log("[AutoDD] pomander maintenance unblocked: local player ready.");
            _lastPomanderReadinessBlocker = readinessBlocker;
        }
        if (readinessBlocker != null)
            return;

        foreach (var kind in new[] { DeepDungeonPolicy.PomanderMaintenanceKind.Serenity, DeepDungeonPolicy.PomanderMaintenanceKind.Safety, DeepDungeonPolicy.PomanderMaintenanceKind.Sight, DeepDungeonPolicy.PomanderMaintenanceKind.Raising, DeepDungeonPolicy.PomanderMaintenanceKind.Steel, DeepDungeonPolicy.PomanderMaintenanceKind.Strength, DeepDungeonPolicy.PomanderMaintenanceKind.Fortune, DeepDungeonPolicy.PomanderMaintenanceKind.Intuition, DeepDungeonPolicy.PomanderMaintenanceKind.Affluence, DeepDungeonPolicy.PomanderMaintenanceKind.Flight })
        {
            var serenityTriggerPresent = kind == DeepDungeonPolicy.PomanderMaintenanceKind.Serenity && SerenityTriggerPresent(player);
            var configured = kind switch
            {
                DeepDungeonPolicy.PomanderMaintenanceKind.Serenity => _config.AutoSerenity,
                DeepDungeonPolicy.PomanderMaintenanceKind.Raising => _config.MaintainRaising,
                DeepDungeonPolicy.PomanderMaintenanceKind.Safety => _config.AutoSafety,
                DeepDungeonPolicy.PomanderMaintenanceKind.Sight => _config.AutoSight,
                DeepDungeonPolicy.PomanderMaintenanceKind.Steel => _config.MaintainSteel,
                DeepDungeonPolicy.PomanderMaintenanceKind.Strength => _config.MaintainStrength,
                DeepDungeonPolicy.PomanderMaintenanceKind.Fortune => _config.AutoFortune,
                DeepDungeonPolicy.PomanderMaintenanceKind.Intuition => _config.AutoIntuition,
                DeepDungeonPolicy.PomanderMaintenanceKind.Affluence => DeepDungeonPolicy.IsPreparingNextFloorPomander(_config.AutoAffluence, Palace.PassageActive, departureIntent, !player.IsDead, player.InCombat, _pomanderSnapshotReady, Palace.DungeonId, Palace.Floor),
                DeepDungeonPolicy.PomanderMaintenanceKind.Flight => DeepDungeonPolicy.IsPreparingNextFloorPomander(_config.AutoFlight, Palace.PassageActive, departureIntent, !player.IsDead, player.InCombat, _pomanderSnapshotReady, Palace.DungeonId, Palace.Floor),
                _ => false,
            };
            var pid = MaintenancePomander(kind);
            var slot = pid != PomanderID.None ? Palace.GetPomanderSlot(pid) : -1;
            var validSlot = slot is >= 0 and < DeepDungeonState.NumPomanderSlots;
            var state = validSlot ? Palace.Pomanders[slot] : default;
            var availableAtFloorEntry = true;
            if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight)
            {
                var entryCount = validSlot && slot < _floorEntryPomanderCounts.Length ? _floorEntryPomanderCounts[slot] : 0;
                availableAtFloorEntry = DeepDungeonPolicy.WasPomanderAvailableAtFloorEntry(_floorEntryPomanderSnapshotReady, entryCount);
                configured = DeepDungeonPolicy.IsFloorStartPomanderEligible(configured, bossFloor, !BetweenFloors && _pomanderSnapshotReady, _floorStartPomanderWindowAt, World.CurrentTime)
                    && availableAtFloorEntry;
            }
            if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Safety)
                _safetyFloorUsage = DeepDungeonPolicy.ObserveSafetyFloorUsage(_safetyFloorUsage, Palace.DungeonId, Palace.Floor, effectActive: false);
            var effectActive = kind switch
            {
                // Serenity is eligible only while a selected trigger is present.
                DeepDungeonPolicy.PomanderMaintenanceKind.Serenity => !serenityTriggerPresent,
                _ => MaintenanceEffectPresent(player, kind, state) == true,
            };
            var oneShotPerFloor = kind is DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight or DeepDungeonPolicy.PomanderMaintenanceKind.Serenity or DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition or DeepDungeonPolicy.PomanderMaintenanceKind.Affluence or DeepDungeonPolicy.PomanderMaintenanceKind.Flight;
            var alreadyUsedThisFloor = oneShotPerFloor && _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, kind));
            if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition && state.Active)
            {
                alreadyUsedThisFloor = true;
                _confirmedFloorPomanderUses.Add((Palace.DungeonId, Palace.Floor, kind));
            }
            if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Affluence or DeepDungeonPolicy.PomanderMaintenanceKind.Flight && state.Active)
            {
                alreadyUsedThisFloor = true;
                _confirmedFloorPomanderUses.Add((Palace.DungeonId, Palace.Floor, kind));
            }
            var hasRequest = _pomanderMaintenanceRequests.TryGetValue(kind, out var pending);
            var unknown = hasRequest && pending.Phase == DeepDungeonPolicy.PomanderMaintenanceRequestPhase.OutcomeUnknown;
            var ownPending = hasRequest && DeepDungeonPolicy.IsBlockingPomanderMaintenanceRequest(pending.Phase);
            var waitingForOther = DeepDungeonPolicy.HasBlockingPomanderMaintenanceRequest(_pomanderMaintenanceRequests, kind);
            var pendingRequest = ownPending || waitingForOther;
            var safetyUsedThisFloor = _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, DeepDungeonPolicy.PomanderMaintenanceKind.Safety)) || _safetyFloorUsage is { } safetyUsage && safetyUsage.Dungeon == Palace.DungeonId && safetyUsage.Floor == Palace.Floor && safetyUsage.Used;
            var sightUsedThisFloor = _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, DeepDungeonPolicy.PomanderMaintenanceKind.Sight));
            var safetyRequestUnresolved = _pomanderMaintenanceRequests.TryGetValue(DeepDungeonPolicy.PomanderMaintenanceKind.Safety, out var trackedSafety)
                && trackedSafety.Dungeon == Palace.DungeonId && trackedSafety.Floor == Palace.Floor && trackedSafety.Phase != DeepDungeonPolicy.PomanderMaintenanceRequestPhase.Resolved;
            var sightRequestUnresolved = _pomanderMaintenanceRequests.TryGetValue(DeepDungeonPolicy.PomanderMaintenanceKind.Sight, out var trackedSight)
                && trackedSight.Dungeon == Palace.DungeonId && trackedSight.Floor == Palace.Floor && trackedSight.Phase != DeepDungeonPolicy.PomanderMaintenanceRequestPhase.Resolved;
            var safetySlotForSight = Palace.GetPomanderSlot(MaintenancePomander(DeepDungeonPolicy.PomanderMaintenanceKind.Safety));
            var safetyAvailableForSight = safetySlotForSight is >= 0 and < DeepDungeonState.NumPomanderSlots
                && Palace.Pomanders[safetySlotForSight].Count > 0 && Palace.Pomanders[safetySlotForSight].Usable;
            var eligible = DeepDungeonPolicy.CanMaintainPomander(
                configured, _config.Enable, !BetweenFloors && _pomanderSnapshotReady, !player.IsDead,
                !DeepDungeonPolicy.SerenityMayIgnoreItemPenalty(kind) && player.FindStatus(SID.ItemPenalty) != null,
                validSlot, state.Count, state.Usable, bossFloor,
                kind is DeepDungeonPolicy.PomanderMaintenanceKind.Raising or DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight or DeepDungeonPolicy.PomanderMaintenanceKind.Serenity or DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition or DeepDungeonPolicy.PomanderMaintenanceKind.Affluence or DeepDungeonPolicy.PomanderMaintenanceKind.Flight,
                kind is DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight or DeepDungeonPolicy.PomanderMaintenanceKind.Serenity or DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition,
                kind == DeepDungeonPolicy.PomanderMaintenanceKind.Safety && safetyUsedThisFloor || alreadyUsedThisFloor,
                effectActive, pendingRequest, unknown, out var reason);
            if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight && !availableAtFloorEntry)
            {
                eligible = false;
                reason = "not available at floor entry";
            }
            if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Intuition && Palace.Progress.HoardCurrentFloor)
            {
                eligible = false;
                reason = "current-floor hoard already found";
            }
            if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Sight
                && DeepDungeonPolicy.ShouldSuppressSightForSafety(safetyUsedThisFloor, safetyRequestUnresolved, _config.AutoSafety, safetyAvailableForSight))
            {
                eligible = false;
                reason = safetyUsedThisFloor ? "Safety used this floor"
                    : safetyRequestUnresolved ? "Safety request pending or outcome unknown"
                    : "Safety preferred";
            }
            if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Safety
                && DeepDungeonPolicy.ShouldSuppressSafetyForSight(sightUsedThisFloor, sightRequestUnresolved))
            {
                eligible = false;
                reason = sightUsedThisFloor ? "Sight used this floor" : "Sight request pending or outcome unknown";
            }
            if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Serenity && configured && !serenityTriggerPresent && !unknown && !pendingRequest)
                reason = "no selected enchantment detected";
            if (waitingForOther)
            {
                var other = _pomanderMaintenanceRequests.First(entry => entry.Key != kind && DeepDungeonPolicy.IsBlockingPomanderMaintenanceRequest(entry.Value.Phase));
                reason = $"waiting for {other.Value.Pomander} readback";
            }
            LogPomanderMaintenanceDecision(kind, pid, state, bossFloor, reason,
                kind == DeepDungeonPolicy.PomanderMaintenanceKind.Serenity ? SerenityTriggerSummary(player) : null);
            if (!eligible)
                continue;

            var action = new ActionID(ActionType.Pomander, (uint)pid);
            if (hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Pomander))
                return; // An overcap/manual pomander proposal already owns this action slot for this frame.

            hints.ActionsToExecute.Push(action, null, ActionQueue.Priority.Low);
            return;
        }
    }

    private void OnEObjAnim(Actor actor, ushort p1, ushort p2)
    {
        // fake beacon deactivation; accompanied by system log #9217 but it does not indicate a specific actor
        if (actor.OID == (uint)OID.BeaconHoH && p1 == 0x0400 && p2 == 0x0800)
            _fakeExits.Add(actor.InstanceID);
    }

    protected virtual void OnChangeFloors() { }

    private void ClearState()
    {
        Donuts.Clear();
        Circles.Clear();
        Gazes.Clear();
        Interrupts.Clear();
        Stuns.Clear();
        Spikes.Clear();
        HintDisabled.Clear();
        LOS.Clear();
        Walls.Clear();
        ClearDesiredRoom("floor state reset");
        ResetObjectiveProgress("floor state reset");
        _passageSelection = default;
        Kills = 0;
        Array.Fill(_playerImmunes, default);
        _chestContentsGold.Clear();
        _chestContentsSilver.Clear();
        World.DeepDungeonInteractions.Clear();
        _lastAttributedInteractionSequence = 0;
        _floorStartPomanderWindowAt = default;
        _deferredTreasureAt.Clear();
        ResetTreasureProgress();
        _lastOvercapCorrelationLogKey = null;
        _lastOvercapCorrelationLogAt = default;
        _suppressedOvercapCorrelationLogs = 0;
        _lastOvercapDecision.Clear();
        _pendingPomanderReadbacks.Clear();
        _lastSelectedActorGoalKey = null;
        _lastSelectedActorGoalType = null;
        _lastSelectedActorGoalID = 0;
        _lastActorSelectionContext = null;
        _pendingActorGoalSelectionChanges = 0;
        _actorSelectionTransitions.Clear();
        _otherActorSelectionTransitions = 0;
        _trapsHidden = true;
        _fakeExits.Clear();
        OnChangeFloors();
        BetweenFloors = true;
    }

    protected void AddGaze(Actor Source, AOEShape Shape) => Gazes.Add(new(Source, Shape));
    protected void AddGaze(Actor Source, float Radius) => AddGaze(Source, new AOEShapeCircle(Radius));
    protected void AddDonut(Actor Source, float Inner, float Outer, Angle? HalfAngle = null) => Donuts.Add((Source, Inner, Outer, HalfAngle ?? 180.Degrees()));
    protected void AddVoidzone(Actor Source, AOEShape Shape, DateTime Activation = default, int Counter = 0) => Voidzones.Add((Source, Shape, Activation, Counter));

    protected void AddLOS(Actor Source, float Range)
    {
        if (_config.AutoLOS)
            AddLOSFromTerrain(Source, Range);
        else
            Circles.Add((Source, Range));
    }

    private bool OpenSilver
    {
        get
        {
            // sanity check
            if (World.Party.Player() is not { } player)
                return false;

            // explosive silver chests deal 70% max hp damage
            if (player.HPMP.CurHP <= player.HPMP.MaxHP * 0.7f)
                return false;

            // upgrade weapon if desired
            if (Palace.Progress.WeaponLevel + Palace.Progress.ArmorLevel < 198)
                return true;

            return Palace.DungeonId switch
            {
                DeepDungeonState.DungeonType.PT => true,
                DeepDungeonState.DungeonType.HOH or DeepDungeonState.DungeonType.EO => Palace.Floor >= 7, // per-dungeon gimmick items start dropping on floor 7
                _ => false,
            };
        }
    }

    private bool CanAutoUse(PomanderID p, Actor player, out string reason, out DeepDungeonState.PomanderState state)
    {
        state = default;
        var validDungeonAndAction = Palace.DungeonId != DeepDungeonState.DungeonType.None && (uint)p > 0 && (uint)p < (uint)PomanderID.Count;
        var slot = validDungeonAndAction ? Palace.GetPomanderSlot(p) : -1;
        var validSlot = slot is >= 0 and < DeepDungeonState.NumPomanderSlots;
        if (validSlot)
            state = Palace.Pomanders[slot];

        var requiresPox = p is PomanderID.Purity or PomanderID.ProtoPurity;
        var eligible = DeepDungeonPolicy.CanAutoUsePomander(
            validDungeonAndAction,
            validSlot,
            state.Count,
            state.Usable,
            player.FindStatus(SID.ItemPenalty) != null,
            _config.AutoPoms[(int)p],
            requiresPox,
            !requiresPox || player.FindStatus(1087) != null,
            out reason);
        if (!eligible || MaintenanceKindFor(p) is not { } kind)
            return eligible;

        if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight)
        {
            var slotAtEntry = Palace.GetPomanderSlot(p);
            var entryCount = slotAtEntry is >= 0 && slotAtEntry < _floorEntryPomanderCounts.Length ? _floorEntryPomanderCounts[slotAtEntry] : 0;
            var eligibleAtFloorStart = DeepDungeonPolicy.IsFloorStartPomanderEligible(
                true, Palace.IsBossFloor, _floorEntryPomanderSnapshotReady, _floorStartPomanderWindowAt, World.CurrentTime)
                && DeepDungeonPolicy.WasPomanderAvailableAtFloorEntry(_floorEntryPomanderSnapshotReady, entryCount);
            if (!eligibleAtFloorStart)
            {
                reason = !_floorEntryPomanderSnapshotReady || entryCount == 0 ? "not available at floor entry" : "outside floor-start window";
                return false;
            }
        }

        var usedThisFloor = _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, kind));
        var requestUnresolved = HasCurrentUnresolvedPomanderRequest(kind);
        if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Fortune or DeepDungeonPolicy.PomanderMaintenanceKind.Intuition)
        {
            if (state.Active)
                reason = "effect active";
            else if (usedThisFloor || requestUnresolved)
                reason = usedThisFloor ? "already used this floor" : "request pending or outcome unknown";
            else if (kind == DeepDungeonPolicy.PomanderMaintenanceKind.Intuition && Palace.Progress.HoardCurrentFloor)
                reason = "current-floor hoard already found";
            else
                return true;
            return false;
        }

        if (kind is DeepDungeonPolicy.PomanderMaintenanceKind.Safety or DeepDungeonPolicy.PomanderMaintenanceKind.Sight)
        {
            var safetyKind = DeepDungeonPolicy.PomanderMaintenanceKind.Safety;
            var sightKind = DeepDungeonPolicy.PomanderMaintenanceKind.Sight;
            var safetyUsed = _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, safetyKind))
                || _safetyFloorUsage.Dungeon == Palace.DungeonId && _safetyFloorUsage.Floor == Palace.Floor && _safetyFloorUsage.Used;
            var sightUsed = _confirmedFloorPomanderUses.Contains((Palace.DungeonId, Palace.Floor, sightKind));
            var safetySlot = Palace.GetPomanderSlot(MaintenancePomander(safetyKind));
            var safetyState = safetySlot is >= 0 and < DeepDungeonState.NumPomanderSlots ? Palace.Pomanders[safetySlot] : default;
            var safetyAvailable = safetyState.Count > 0 && safetyState.Usable;
            var safetyRequest = HasCurrentUnresolvedPomanderRequest(safetyKind);
            var sightRequest = HasCurrentUnresolvedPomanderRequest(sightKind);
            if (kind == safetyKind && (safetyUsed || safetyRequest))
                reason = safetyUsed ? "already used this floor" : "request pending or outcome unknown";
            else if (kind == sightKind && (sightUsed || sightRequest))
                reason = sightUsed ? "already used this floor" : "request pending or outcome unknown";
            else if (kind == safetyKind && DeepDungeonPolicy.ShouldSuppressSafetyForSight(sightUsed, sightRequest))
                reason = sightUsed ? "Sight used this floor" : "Sight request pending or outcome unknown";
            else if (kind == sightKind && DeepDungeonPolicy.ShouldSuppressSightForSafety(safetyUsed, safetyRequest, _config.Enable && _config.AutoSafety, safetyAvailable))
                reason = safetyUsed ? "Safety used this floor"
                    : safetyRequest ? "Safety request pending or outcome unknown"
                    : "Safety preferred";
            else
                return true;
            return false;
        }
        return true;
    }

    private bool HasCurrentUnresolvedPomanderRequest(DeepDungeonPolicy.PomanderMaintenanceKind kind)
        => _pomanderMaintenanceRequests.TryGetValue(kind, out var request)
            && request.Dungeon == Palace.DungeonId && request.Floor == Palace.Floor
            && request.Phase != DeepDungeonPolicy.PomanderMaintenanceRequestPhase.Resolved;

    private void LogOvercapDecision(Actor chest, PomanderID pid, bool accepted, string reason, DeepDungeonState.PomanderState state)
    {
        var decision = $"{pid}/{accepted}/{reason}/{state.Count}/{state.Usable}/{state.Active}";
        if (_lastOvercapDecision.TryGetValue(chest.InstanceID, out var previous) && previous == decision)
            return;
        _lastOvercapDecision[chest.InstanceID] = decision;
        Service.Log($"[AutoDD] overcap pomander candidate {(accepted ? "accepted" : "rejected")}: chest={chest.InstanceID}, pomander={pid}, stock={state.Count}, usable={state.Usable}, active={state.Active}, reason={reason}.");
    }

    private void ProcessAll<T>(List<T> items, Func<T, bool> expire, Action<T> action, Action<T>? onRemove = null)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (expire(item))
            {
                items.RemoveAt(i);
                onRemove?.Invoke(item);
            }
            else
                action(item);
        }
    }

    private void HandleActorAdd(Actor c)
    {
        HandleBeacon(c);
        OnActorCreated(c);
    }
    protected virtual void OnActorCreated(Actor c) { }

    private DateTime CastFinishAt(Actor c) => World.FutureTime(c.CastInfo!.NPCRemainingTime);

    protected virtual void CalculateExtraHints(int playerSlot, Actor player, AIHints hints) { }

    public override void CalculateAIHints(int playerSlot, Actor player, AIHints hints)
    {
        if (!_config.Enable || player.IsDead || DesiredRoom.Reason == PathfindReason.Manual
            || DeepDungeonPartyPolicy.RecentManualMovement(hints.DeepDungeonCoordinationManualAt, World.CurrentTime))
            _treasureSelection.Reset();
        LogEnableState();
        TryInitializeFloor("AI hint update");
        if (!_config.Enable || player.IsDead || !Palace.PassageActive || !PassageActorGoalAllowed
            || _passageSelection.ActorId != 0 && (World.Actors.Find(_passageSelection.ActorId) is not { IsDestroyed: false }
                || _fakeExits.Contains(_passageSelection.ActorId)))
            _passageSelection = default;
        ExpirePomanderReadbacks();
        ReconcilePomanderMaintenance(player);
        hints.DeepDungeonRoleActive = _config.Enable;
        hints.DeepDungeonPartyMode = _config.Enable && (_config.Role == AutoDDConfig.PartyRole.Follower
            || World.Party.Members.Take(PartyState.MaxPartySize).Count(m => m.IsValid()) > 1);
        if (!_config.Enable)
        {
            _partyRegroup.Reset();
            _partyContext = default;
            ResetRoomRejoin();
            MostInterestingObject = null;
            InterestingObjects.Clear();
        }

        if (!DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor))
        {
            _roomRejoin.Pause(Environment.TickCount64);
            LogAutoDDBlocker("waiting for deep dungeon state initialization");
            ResetObjectiveProgress("waiting for deep dungeon state initialization");
            return;
        }

        if (World.DeepDungeon.BossCompletionObserved)
        {
            ResetRoomRejoin();
            MostInterestingObject = null;
            InterestingObjects.Clear();
            ClearDesiredRoom("boss completion observed; awaiting departure");
            LogAutoDDBlocker("boss completion observed; pomander maintenance and navigation suppressed");
            return;
        }

        if (player.IsDead)
        {
            _roomRejoin.Pause(Environment.TickCount64);
            MostInterestingObject = null;
            InterestingObjects.Clear();
            LogAutoDDBlocker("player dead");
            ResetObjectiveProgress("player dead");
            return;
        }

        if (BetweenFloors && !Palace.IsBossFloor)
        {
            _roomRejoin.Pause(Environment.TickCount64);
            var blocker = "between floors / map not initialized";
            LogAutoDDBlocker(blocker);
            ResetObjectiveProgress(blocker);
            return;
        }

        var geometryReady = TryGetCurrentRoomRects(out _, out var geometryReason);
        LogGeometryState(geometryReady, geometryReason);

        foreach (var (w, rot) in Walls)
            hints.TemporaryObstacles.Add(ShapeDistance.Rect(w.Position, (rot ? 90f : 0f).Degrees(), w.Depth, w.Depth, 20));

        if (_config.TrapHints && _trapsHidden)
        {
            var tshape = _trapsCurrentFloor.Select(t => new WPos(t.X, t.Z)).Where(f => f.AlmostEqual(player.Position, 30)).Select(f => ShapeDistance.Circle(f, TrapRadius)).ToList();
            if (tshape.Count > 0)
            {
                var potentialTraps = ShapeDistance.Union(tshape);
                hints.DeepDungeonPotentialTrapDistance = potentialTraps;
                hints.AddForbiddenZone(potentialTraps, source: AIHints.DeepDungeonPotentialTrapTag);
            }
        }

        DrawAOEs(playerSlot, player, hints);

        // Revealed traps are safety data, not leader-only object selection.
        foreach (var actor in World.Actors)
            if (RevealedTrapOIDs.Contains(actor.OID))
                hints.AddForbiddenZone(ShapeDistance.Circle(actor.Position, TrapRadius));

        if (CalculatePartyHints(player, hints))
        {
            _roomRejoin.Pause(Environment.TickCount64);
            return;
        }

        if (Palace.IsBossFloor)
        {
            ResetRoomRejoin();
            if (!BetweenFloors)
                TryQueuePomanderMaintenance(player, hints, bossFloor: true, departureIntent: Palace.PassageActive && !player.InCombat);
            ResetObjectiveProgress("boss floor");
            return;
        }

        if (_config.ForbidDOTs)
            foreach (var hpt in hints.PotentialTargets)
                hpt.ForbidDOTs = true;

        if (_config.Enable)
            CalculateExtraHints(playerSlot, player, hints);

        var numAggro = hints.PotentialTargets.Count(t => t.Actor.AggroPlayer && !t.Actor.IsDeadOrDestroyed);
        var livingTargets = hints.PotentialTargets.Count(t => !t.Actor.IsDeadOrDestroyed);
        TryQueueAutoHoHPomanders(player, hints, livingTargets);
        TryQueueAutoWitching(player, hints, bossFloor: false, numAggro);

        if (!geometryReady)
        {
            _roomRejoin.Pause(Environment.TickCount64);
            MostInterestingObject = null;
            TryQueuePomanderMaintenance(player, hints, bossFloor: false);
            LogAutoDDBlocker($"room geometry unavailable: {geometryReason}");
            ResetObjectiveProgress("room geometry unavailable");
            return;
        }

        if (!TryFindClosestRoom(player.Position, out var playerRoom))
        {
            _roomRejoin.Pause(Environment.TickCount64);
            TryQueuePomanderMaintenance(player, hints, bossFloor: false);
            LogAutoDDBlocker("player room could not be resolved from current geometry");
            ResetObjectiveProgress("player room unavailable");
            return;
        }
        (PlayerRoom, PlayerRoomBox) = playerRoom;

        // allow navigating when at max pull, i.e. so we can walk our dog to the nearest chest
        var canNavigate = numAggro <= _config.MaxPull;
        var manualNavigation = DesiredRoom.Reason == PathfindReason.Manual;
        var roomNavigationEnabled = DeepDungeonPolicy.ShouldNavigate(_config.Enable, manualNavigation);

        if (!roomNavigationEnabled && DesiredRoom.Reason != PathfindReason.Nothing)
            ClearDesiredRoom("automatic navigation disabled");

        _roomRejoinOwnsFrame = false;
        if (!_config.Enable || manualNavigation)
        {
            ResetRoomRejoin();
            MostInterestingObject = null;
            InterestingObjects.Clear();
            if (canNavigate && roomNavigationEnabled)
                HandleFloorPathfind(player, hints, DeepDungeonRoomRejoinPolicy.NavigationOwner.Manual);
            // HandleFloorPathfind may have completed and cleared a manual destination this frame.
            manualNavigation = DesiredRoom.Reason == PathfindReason.Manual;
            if (manualNavigation)
            {
                LogAutoDDBlocker(canNavigate ? null : $"manual navigation blocked: pull limit reached ({numAggro}/{_config.MaxPull})");
                LogObjectiveProgress(player, hints, numAggro, canNavigate);
            }
            else
            {
                LogAutoDDBlocker(_config.Enable ? null : "AutoDD disabled (manual minimap destination only)");
                ResetObjectiveProgress(_config.Enable ? "manual destination reached" : "AutoDD disabled");
            }
            if (_config.Enable)
                TryQueuePomanderMaintenance(player, hints, bossFloor: false);
            return;
        }

        var isTransformed = IsPlayerTransformed(player);
        var isStunned = isTransformed || player.Statuses.Any(s => (SID)s.ID is SID.Silence or SID.Pacification);
        var isOccupied = player.InCombat || isStunned;

        InterestingObjects.Clear();

        foreach (var a in World.Actors)
        {
            if (_chestContentsGold.TryGetValue(a.InstanceID, out var pid) && Palace.GetPomanderState(pid).Count == 3 && a.IsTargetable)
            {
                var canAutoUse = CanAutoUse(pid, player, out var reason, out var state);
                var pomanderActionAlreadyProposed = HasPomanderAction(hints);
                LogOvercapDecision(a, pid, canAutoUse && !pomanderActionAlreadyProposed,
                    pomanderActionAlreadyProposed && canAutoUse ? "another pomander action already owns this frame" : reason, state);
                if (canAutoUse && !pomanderActionAlreadyProposed)
                    hints.ActionsToExecute.Push(new ActionID(ActionType.Pomander, (uint)pid), null, ActionQueue.Priority.VeryHigh);
                continue;
            }

            if (_chestContentsSilver.ContainsKey(a.InstanceID) && Palace.Magicite.All(m => m > 0))
                // TODO use magicite/demiclone to prevent overcap
                continue;

            if (_fakeExits.Contains(a.InstanceID))
                continue;

            var dist = (player.Position - a.Position).LengthSq();

            var oid = (OID)a.OID;
            var adjacent = IsInThisRoomOrAdjacent(a);
            var retainedTreasure = MostInterestingObject is { IsTreasure: true } previous && previous.Actor.InstanceID == a.InstanceID
                && _treasureSelection.CanRetain(TreasureSelectionContext, a.InstanceID);
            var passageCandidate = oid is OID.CairnPalace or OID.BeaconHoH or OID.PylonEO or OID.PylonPT
                && _passageSelection.CanAdmit(TreasureSelectionContext, a.InstanceID, adjacent,
                    !a.IsDestroyed && Palace.PassageActive && !player.InCombat);
            if (adjacent || retainedTreasure || passageCandidate)
            {
                if (!isTransformed)
                {
                    if (oid == OID.BandedCoffer && a.IsTargetable && !a.IsOpenTreasure)
                        InterestingObjects.Add(new(a, GoalType.ChestHoard, dist));

                    if (_config.OpenTreasure)
                    {
                        if (oid == OID.GoldCoffer && a.IsTargetable && !a.IsOpenTreasure)
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 3));

                        if (oid == OID.SilverCoffer && OpenSilver && a.IsTargetable && !a.IsOpenTreasure)
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 2));

                        if (BronzeChestIDs.Contains(a.OID) && DeepDungeonPolicy.ShouldConsiderBronzeTreasure(a.IsTargetable, a.IsOpenTreasure))
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 1));
                    }
                }

                if (adjacent && oid == OID.BandedCofferIndicator && Palace.Progress.HoardCurrentFloor)
                    InterestingObjects.Add(new(a, GoalType.Hoard, dist));

                if (passageCandidate)
                    InterestingObjects.Add(new(a, GoalType.Passage, dist));
            }

        }

        PickARoom(player, hints);

        var now = World.CurrentTime;
        foreach (var actorID in _deferredTreasureAt.Keys.ToArray())
            if (!DeepDungeonPolicy.IsTreasureDeferred(_deferredTreasureAt[actorID], now))
                _deferredTreasureAt.Remove(actorID);
        InterestingObjects.RemoveAll(o => o.Type == GoalType.Treasure
            && _deferredTreasureAt.TryGetValue(o.Actor.InstanceID, out var deferredAt) && DeepDungeonPolicy.IsTreasureDeferred(deferredAt, now));

        InterestingObjects.SortBy(o => (o.Type, o.IsTreasure && _treasureSelection.CanRetain(TreasureSelectionContext, o.Actor.InstanceID) ? 0 : 1, o.PlayerDistSq));

        MostInterestingObject = InterestingObjects.FirstOrNull(o => DeepDungeonPolicy.ShouldConsiderActorForNavigation(o.Type == GoalType.Passage, PassageActorGoalAllowed));
        UpdateTreasureStallRecovery(player, hints, canNavigate, manualNavigation);
        UpdateTreasureOscillationRecovery(player, hints, canNavigate, manualNavigation);
        var selectedTreasure = MostInterestingObject is { IsTreasure: true } treasure ? treasure.Actor : null;
        _treasureSelection.Select(TreasureSelectionContext, selectedTreasure?.InstanceID ?? 0, World.CurrentTime,
            selectedTreasure != null && IsInThisRoomOrAdjacent(selectedTreasure));
        LogSelectedActorGoal(player);

        var selectedPassage = MostInterestingObject is { Type: GoalType.Passage, Actor: var passageActor } && PassageActorGoalAllowed ? passageActor : null;
        _passageSelection.Select(TreasureSelectionContext, selectedPassage?.InstanceID ?? 0);
        var localPassage = selectedPassage != null && DeepDungeonPartyPolicy.UseLocalPassageGoal(selectedPassage.InstanceID,
            _passageAssembly.Passage, (selectedPassage.Position - player.Position).Length(), selectedPassage.PosRot.Y - player.PosRot.Y);

        // Select/revalidate objectives before any room recovery can claim navigation. Actor goals
        // own attraction exclusively; unrelated room goals must not reverse their movement.
        var owner = DeepDungeonRoomRejoinPolicy.SelectOwner(manualNavigation,
            MostInterestingObject != null, canNavigate,
            hints.InteractWithTarget != null || hints.InteractWithRawTarget != null);
        _roomRejoin.Bind(RoomRejoinContext);
        if (_roomRejoin.Attempts == 0)
            World.DeepDungeon.RoomRejoinStatus = null;
        if (canNavigate && roomNavigationEnabled)
            HandleFloorPathfind(player, hints, owner);
        else
            _roomRejoin.Pause(Environment.TickCount64);
        if (_roomRejoinOwnsFrame)
        {
            foreach (var enemy in hints.PotentialTargets)
            {
                var engaged = DeepDungeonPartyPolicy.CanAssist(enemy.Actor.AggroPlayer, enemy.Actor.InCombat,
                    World.Party.TryFindSlot(enemy.Actor.TargetID, out var partySlot) && partySlot < PartyState.MaxPartySize);
                if (!engaged)
                {
                    enemy.ShouldBeTargeted = false;
                    enemy.ForcePriority(AIHints.Enemy.PriorityForbidden);
                }
            }
            hints.DeepDungeonPartyGoals = [.. hints.GoalZones];
            hints.SetDeepDungeonPotentialTrapObjective(PotentialTrapObjectiveKey);
            LogObjectiveProgress(player, hints, numAggro, canNavigate);
            TryQueuePomanderMaintenance(player, hints, bossFloor: false);
            return;
        }

        LogAutoDDBlocker(canNavigate ? null : $"pull limit reached ({numAggro}/{_config.MaxPull})");

        var goalWeight = player.InCombat ? 1 : 0.01f;
        var assemblyActive = _passageAssembly.Update(Environment.TickCount64, localPassage ? selectedPassage!.InstanceID : 0, Palace.PassageActive,
            selectedPassage != null ? (selectedPassage.Position - player.Position).Length() : float.MaxValue,
            selectedPassage != null ? selectedPassage.PosRot.Y - player.PosRot.Y : float.MaxValue,
            localPassage, player.InCombat || player.CastInfo != null || !canNavigate);
        var assembling = localPassage && assemblyActive;
        if (assembling && selectedPassage != null)
            ObservePassageAssembly(player, selectedPassage, hints, player.InCombat || player.CastInfo != null || !canNavigate);
        if (assembling)
            hints.DeepDungeonPartyNavigationKey = AutoNavigationKey($"passage:{_passageAssembly.Passage:X}:blocked:{_passageAssembly.Blocked}:replan:{_passageAssembly.Replans}");
        else if (owner == DeepDungeonRoomRejoinPolicy.NavigationOwner.Actor)
            hints.DeepDungeonPartyNavigationKey = AutoNavigationKey($"actor:{MostInterestingObject?.Type}:{MostInterestingObject?.Actor.InstanceID ?? hints.InteractWithTarget?.InstanceID ?? 0}");
        else if (owner == DeepDungeonRoomRejoinPolicy.NavigationOwner.Suspended)
            hints.DeepDungeonPartyNavigationKey = AutoNavigationKey("automatic-navigation-suspended");

        if (canNavigate)
        {
            switch (MostInterestingObject)
            {
                case { Type: GoalType.Passage, Actor: var c } when PassageActorGoalAllowed:
                    if (!localPassage)
                    {
                        AddPassageApproachGoals(player, c, hints, goalWeight);
                    }
                    else if (_passageAssembly.Blocked)
                    {
                        if (!_partyHolding)
                        {
                            _partyHolding = true;
                            _partyHoldAnchor = player.Position;
                        }
                        hints.AddGoalZone(AIHints.GoalSingleTarget(_partyHoldAnchor, 1, 1), "deepdungeon-passage-blocked-hold");
                        hints.DeepDungeonIntentionalHoldReason = "passage assembly blocked; deadline or bounded replans exhausted; explicit retry or changed evidence required";
                        LogPartyStatus("passage assembly blocked; deadline exhausted, move party or explicitly retry");
                    }
                    else
                    {
                        AddPassageGoals(player, c, hints, goalWeight, _passageAssembly.Replans);
                        LogPartyStatus($"leader assembling at passage {c.InstanceID:X}; localArrival={player.Position.InCircle(c.Position, DeepDungeonPartyPolicy.PassageInnerRadius)}");
                    }
                    break;

                case { Type: GoalType.Hoard, Actor: var h }:
                    hints.AddGoalZone(AIHints.GoalSingleTarget(h.Position, 2, 10), $"deepdungeon-hoard:{h.InstanceID}");
                    hints.AddGoalZone(DeepDungeonPolicy.HoardProgressGoal(h.Position, player.Position, 100, 1, 10), $"deepdungeon-hoard:{h.InstanceID}:progress");
                    break;

                case { IsTreasure: true, Actor: var d }:
                    hints.AddGoalZone(AIHints.GoalSingleTarget(d.Position, 2, DeepDungeonPolicy.TreasureApproachWeight(player.InCombat)), $"deepdungeon-treasure:{d.InstanceID}:interact-range");
                    hints.AddGoalZone(DeepDungeonPolicy.TreasureProgressGoal(d.Position, player.Position, 100, 1, DeepDungeonPolicy.TreasureApproachWeight(player.InCombat)), $"deepdungeon-treasure:{d.InstanceID}:progress");
                    hints.DeepDungeonTreasureGoalID = d.InstanceID;
                    if ((player.Position - d.Position).LengthSq() <= 3.5f * 3.5f)
                        hints.InteractWithTarget = d;
                    break;
            }
        }

        var passageRevealed = Palace.Rooms.Any(r => r.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags.Passage) && r.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags.Revealed));

        var canTarget = _config.AutoClear switch
        {
            AutoDDConfig.ClearBehavior.Passage => passageRevealed && !Palace.PassageActive,
            AutoDDConfig.ClearBehavior.Leveling => player.Level < LevelCap || passageRevealed && !Palace.PassageActive,
            AutoDDConfig.ClearBehavior.All => true,
            _ => false
        };

        foreach (var pp in hints.PotentialTargets)
        {
            if ((selectedPassage != null || _partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.Verifying)
                && !DeepDungeonPartyPolicy.CanAssist(pp.Actor.AggroPlayer, pp.Actor.InCombat,
                    World.Party.TryFindSlot(pp.Actor.TargetID, out var targetSlot) && targetSlot < PartyState.MaxPartySize))
            {
                pp.ShouldBeTargeted = false;
                pp.ForcePriority(AIHints.Enemy.PriorityForbidden);
                continue;
            }
            // enemy is petrified, any damage will kill
            var petrified = pp.Actor.FindStatus(SID.StoneCurse)?.ExpireAt > World.FutureTime(1.5f);
            var stopProactivePetrificationSweep = Palace.DungeonId == DeepDungeonState.DungeonType.HOH
                && _config.AutoPetrification
                && Palace.PassageActive;
            if (petrified && !stopProactivePetrificationSweep)
            {
                pp.ForcePriority(0);
                if (Palace.DungeonId == DeepDungeonState.DungeonType.HOH && _config.AutoPetrification
                    && DeepDungeonPolicy.ShouldUseHoHPomanders(Palace.DungeonId, true, Palace.PassageActive))
                    pp.ShouldBeTargeted = true;
            }

            // pomander of storms was used, enemy can't autoheal; any damage will kill
            else if (!petrified && pp.Actor.FindStatus(SID.AutoHealPenalty) != null && pp.Actor.PendingHPRaw is > 0 and < 10)
                pp.ForcePriority(0);

            // if not at max pull size, prioritize mobs
            else if (selectedPassage == null && _partyRegroup.State != DeepDungeonPartyPolicy.RegroupState.Verifying && canTarget && (numAggro < _config.MaxPull || _config.MaxPull == 0 && numAggro == 0) && !pp.Actor.Statuses.Any(s => IsDangerousOutOfCombatStatus(s.ID)) && IsInThisRoomOrAdjacent(pp.Actor) && !pp.Actor.AggroPlayer)
            {
                // passive mobs shouldn't be prioritized over aggro'd ones
                if (player.InCombat)
                    pp.Priority = 0;
                else
                    pp.ShouldBeTargeted = true;
            }
        }

        if (assembling || _partyRegroup.State == DeepDungeonPartyPolicy.RegroupState.Verifying)
        {
            hints.DeepDungeonPartyGoals = [.. hints.GoalZones];
            if (assembling && selectedPassage != null)
            {
                var assemblyCenter = selectedPassage.Position;
                hints.DeepDungeonPartyCombatArea = p => p.InCircle(assemblyCenter, DeepDungeonPartyPolicy.PassageInnerRadius);
            }
        }
        hints.SetDeepDungeonPotentialTrapObjective(canNavigate ? PotentialTrapObjectiveKey : null);
        LogObjectiveProgress(player, hints, numAggro, canNavigate);
        TryQueuePomanderMaintenance(player, hints, bossFloor: false, departureIntent: Palace.PassageActive && DesiredRoom.Reason == PathfindReason.OpenPassage);
    }

    private void UpdateTreasureOscillationRecovery(Actor player, AIHints hints, bool canNavigate, bool manualNavigation)
    {
        var actorID = hints.DeepDungeonOscillatingTreasureID;
        var signalFloor = hints.DeepDungeonOscillationFloor;
        var signalAt = hints.DeepDungeonOscillationAt;
        // Consume the latched signal; a fresh oscillation must be reported by movement again.
        hints.DeepDungeonOscillatingTreasureID = 0;
        hints.DeepDungeonOscillationFloor = 0;
        hints.DeepDungeonOscillationAt = default;
        if (!canNavigate || manualNavigation || hints.DeepDungeonPotentialTrapCrossingRequested || player.InCombat || player.CastInfo != null || MostInterestingObject is not { IsTreasure: true } selected
            || !DeepDungeonPolicy.IsTreasureOscillationSignalValid(actorID, selected.Actor.InstanceID, signalFloor, Palace.Floor, signalAt, World.CurrentTime))
            return;

        var now = World.CurrentTime;
        var noProgressFor = _treasureProgress.TryGetValue(actorID, out var progress) ? progress.EligibleNoProgress : TimeSpan.Zero;
        var inInteractionRange = MathF.Sqrt(selected.PlayerDistSq) <= 3.5f;
        if (!DeepDungeonPolicy.ShouldDeferTreasureForOscillation(true, noProgressFor,
            DesiredRoom.Reason != PathfindReason.Nothing, manualNavigation, player.InCombat, player.CastInfo != null, inInteractionRange))
            return;

        hints.DeepDungeonDiagnosticRequest ??= $"treasure-oscillation actor={actorID},noProgress={noProgressFor.TotalSeconds:F0}s,checkpoint=before-oscillation-recovery";
        _deferredTreasureAt[actorID] = now;
        MostInterestingObject = InterestingObjects.FirstOrNull(o => o.Actor.InstanceID != actorID
            && DeepDungeonPolicy.ShouldConsiderActorForNavigation(o.Type == GoalType.Passage, PassageActorGoalAllowed));
        Service.Log($"[AutoDD] treasure oscillation recovery: actor={actorID}, action=defer chest for {DeepDungeonPolicy.TreasureRecoveryCooldown.TotalSeconds:F0}s and select next eligible navigation goal; noProgressFor={noProgressFor.TotalSeconds:F0}s. No hazard zones changed.");
    }

    private void ResetTreasureProgress()
    {
        _treasureSelection.Reset();
        _treasureProgress.Clear();
    }

    private void UpdateTreasureStallRecovery(Actor player, AIHints hints, bool canNavigate, bool manualNavigation)
    {
        var now = World.CurrentTime;
        if (MostInterestingObject is not { IsTreasure: true } selected)
            return;

        var actorID = selected.Actor.InstanceID;
        var distance = MathF.Sqrt(selected.PlayerDistSq);
        foreach (var staleID in _treasureProgress.Where(entry => now - entry.Value.LastSelectedAt > DeepDungeonPolicy.TreasureRecoveryCooldown).Select(entry => entry.Key).ToArray())
            _treasureProgress.Remove(staleID);

        if (!_treasureProgress.TryGetValue(actorID, out var progress))
        {
            progress = new() { BestDistance = distance };
            _treasureProgress.Add(actorID, progress);
        }

        var interactionHeld = hints.InteractWithTarget != null || hints.InteractWithRawTarget != null;
        var eligible = canNavigate && !manualNavigation && !player.InCombat && player.CastInfo == null && !interactionHeld;
        var playerMovement = progress.LastSelectedAt == default ? 0 : (player.Position - progress.LastPlayerPosition).Length();
        var sampleElapsed = DeepDungeonPolicy.TreasureSelectedSampleDuration(progress.LastSelectedAt, now, progress.LastSampleAt);
        progress.LastSelectedAt = now;
        progress.LastPlayerPosition = player.Position;
        if (!eligible)
        {
            progress.LastSampleAt = default;
            return;
        }

        progress.LastSampleAt = now;
        // Deadline is checked only when navigation can act; combat/casting/interaction do not
        // trigger recovery. Successful small improvements cannot renew this admitted objective forever.
        var deadlineExpired = _treasureSelection.DeadlineExpired(TreasureSelectionContext, actorID, now);
        if (!deadlineExpired && DeepDungeonPolicy.HasMeaningfulProgress(progress.BestDistance, distance))
        {
            progress.BestDistance = distance;
            progress.EligibleNoProgress = TimeSpan.Zero;
            progress.DiagnosticAt10SecondsRequested = false;
            progress.DiagnosticBeforeDeferralRequested = false;
            return;
        }

        // Do not charge unselected gaps; cap scheduler gaps while continuously selected.
        progress.EligibleNoProgress += TimeSpan.FromMilliseconds(Math.Min(sampleElapsed.TotalMilliseconds, 1000));
        if (!progress.DiagnosticAt10SecondsRequested && progress.EligibleNoProgress >= DeepDungeonPolicy.TreasureStallDiagnosticThreshold)
        {
            hints.DeepDungeonDiagnosticRequest = $"treasure-stall actor={actorID},eligibleNoProgress={progress.EligibleNoProgress.TotalSeconds:F0}s,checkpoint=10s";
            progress.DiagnosticAt10SecondsRequested = true;
        }
        if (!deadlineExpired && !DeepDungeonPolicy.ShouldDeferStalledTreasure(progress.EligibleNoProgress, true, manualNavigation,
            player.InCombat, player.CastInfo != null, interactionHeld))
            return;

        // Comparison routes are prepared without changing movement. At the normal 15s stall
        // threshold let a fresh, validated prediction-only crossing run before abandoning the chest.
        if (!deadlineExpired && hints.DeepDungeonPotentialTrapDistance != null
            && DeepDungeonPotentialTrapCrossing.FreshAlternative(PotentialTrapObjectiveKey, hints.DeepDungeonPotentialTrapResult, Environment.TickCount64)
            && (hints.DeepDungeonPotentialTrapResult.Active || progress.EligibleNoProgress < TimeSpan.FromSeconds(18)))
        {
            hints.DeepDungeonPotentialTrapCrossingRequested = true;
            return;
        }

        if (!progress.DiagnosticBeforeDeferralRequested)
        {
            hints.DeepDungeonDiagnosticRequest = $"treasure-stall actor={actorID},eligibleNoProgress={progress.EligibleNoProgress.TotalSeconds:F0}s,objectiveDeadlineExpired={deadlineExpired},checkpoint=before-deferral";
            progress.DiagnosticBeforeDeferralRequested = true;
        }
        _deferredTreasureAt[actorID] = now;
        MostInterestingObject = InterestingObjects.FirstOrNull(o => o.Actor.InstanceID != actorID
            && DeepDungeonPolicy.ShouldConsiderActorForNavigation(o.Type == GoalType.Passage, PassageActorGoalAllowed));
        Service.Log($"[AutoDD] treasure stall recovery: actor={actorID}, action=defer chest for {DeepDungeonPolicy.TreasureRecoveryCooldown.TotalSeconds:F0}s, eligibleNoProgress={progress.EligibleNoProgress.TotalSeconds:F0}s, objectiveDeadlineExpired={deadlineExpired}, objectiveDeadline={DeepDungeonTreasureSelection.ObjectiveDeadline.TotalSeconds:F0}s, distance={distance:F1}, bestDistance={progress.BestDistance:F1}, distanceImprovement={progress.BestDistance - distance:F2}, playerMovement={playerMovement:F2}y since selected sample; unselected gaps/combat/casting/manual/interaction holds excluded from no-progress time, hazard zones unchanged.");
        _treasureProgress.Remove(actorID);
    }

    private void DrawAOEs(int playerSlot, Actor player, AIHints hints)
    {
        ProcessAll(HintDisabled, g => g.CastInfo == null, g =>
        {
            hints.ForbiddenZones.RemoveAll(z => z.tag == g.InstanceID);
        });

        ProcessAll(Gazes, g => g.Source.CastInfo == null, d =>
        {
            if (d.Shape.Check(player.Position, d.Source))
                hints.ForbiddenDirections.Add((player.AngleTo(d.Source), 45.Degrees(), CastFinishAt(d.Source)));
        });

        ProcessAll(Donuts, d => d.Source.CastInfo == null, d =>
        {
            hints.AddForbiddenZone(new AOEShapeDonutSector(d.Inner, d.Outer, d.HalfAngle), d.Source.Position, d.Source.CastInfo!.Rotation, CastFinishAt(d.Source));
        });

        ProcessAll(Circles, d => d.Source.CastInfo == null, d =>
        {
            hints.AddForbiddenZone(new AOEShapeCircle(d.Radius), d.Source.Position, default, CastFinishAt(d.Source));

            // some enrages are way bigger than pathfinding map size (e.g. slime explosion is 60y)
            // in these cases, if the player is inside the aoe, add a goal zone telling it to GTFO as far as possible
            if (d.Radius >= 30)
            {
                var distToSource = (player.Position - d.Source.Position).Length();
                if (distToSource <= d.Radius)
                {
                    var desiredDistance = distToSource + 10;
                    hints.AddGoalZone(p =>
                    {
                        var dist = (p - d.Source.Position).Length();
                        return dist >= desiredDistance ? 100 : 0;
                    }, $"deepdungeon-hazard-escape:{d.Source.InstanceID}");
                }
            }
        });

        ProcessAll(Interrupts, d => d.CastInfo == null, d =>
        {
            if (hints.FindEnemy(d) is { } e)
                e.ShouldBeInterrupted = true;
        });

        ProcessAll(Stuns, d => d.CastInfo == null, d =>
        {
            if (hints.FindEnemy(d) is { } e)
                e.ShouldBeStunned = true;
        });

        ProcessAll(LOS, d => d.CastInfo == null, caster =>
        {
            if (!_losCache.TryGetValue(caster.InstanceID, out var dangermap))
                return;

            var origin = dangermap.Item1;
            var map = dangermap.Item2;

            hints.AddForbiddenZone(Sdf.Discrete(p =>
            {
                var offset = (p - origin) / map.PixelSize;
                return map[(int)offset.X, (int)offset.Z];
            }), CastFinishAt(caster));
        }, d => _losCache.Remove(d.InstanceID));

        ProcessAll(Voidzones, d => d.Source.IsDeadOrDestroyed, d =>
        {
            hints.AddForbiddenZone(d.Zone, d.Source.Position, d.Source.Rotation, d.Activation);
        });

        ProcessAll(KnockbackZones, d => d.Source.CastInfo == null, kb =>
        {
            var castFinish = CastFinishAt(kb.Source);
            if (_playerImmunes[playerSlot].ImmuneAt(castFinish))
                return;

            hints.AddForbiddenZone(new AOEShapeCircle(kb.Radius), kb.Source.Position, default, castFinish);
        });

        ProcessAll(Spikes, t => t.Timeout <= World.CurrentTime, t =>
        {
            if (hints.FindEnemy(t.Actor) is { } enemy)
                enemy.Spikes = true;
        });
    }

    private static bool IsPlayerTransformed(Actor player) => player.Statuses.Any(Autorotation.RotationModuleManager.IsTransformStatus);
    private static bool IsDangerousOutOfCombatStatus(uint statusRaw) => (SID)statusRaw is SID.DamageUp or SID.DreadBeastAura or SID.PhysicalDamageUp;

    protected void AddLOSFromTerrain(Actor Source, float Range)
    {
        var (entry, data) = _obstacles.Find(Source.PosRot.XYZ());
        if (entry == null || data == null)
        {
            Service.Log($"no bitmap found for {Source}, not adding LOS hints");
            return;
        }

        var pixelRange = (int)(Range / data.PixelSize);
        var casterOff = Source.Position - entry.Origin;
        var casterCell = casterOff / data.PixelSize;
        var casterX = (int)casterCell.X;
        var casterZ = (int)casterCell.Z;

        var bm = new Bitmap(data.Width, data.Height, data.Color0, data.Color1, data.Resolution);
        for (var i = Math.Max(0, casterX - pixelRange); i <= Math.Min(data.Width, casterX + pixelRange); i++)
        {
            for (var j = Math.Max(0, casterZ - pixelRange); j <= Math.Min(data.Height, casterZ + pixelRange); j++)
            {
                var pt = new Vector2(i, j);
                var cc = new Vector2(casterX, casterZ);
                if (!IsBlocked(data, pt, cc, pixelRange))
                    bm[i, j] = true;
            }
        }

        _losCache[Source.InstanceID] = (entry.Origin, bm);
        LOS.Add(Source);
    }

    private static bool IsBlocked(Bitmap map, Vector2 point, Vector2 origin, float maxRange)
    {
        var dir = origin - point;
        var dist = dir.Length();
        if (dist >= maxRange)
            return true;

        dir /= dist;

        var ox = point.X;
        var oy = point.Y;
        var vx = dir.X;
        var vy = dir.Y;

        for (var i = 0; i < (int)dist; i++)
        {
            if (map[(int)ox, (int)oy])
                return true;
            ox += vx;
            oy += vy;
        }

        return false;
    }
}
