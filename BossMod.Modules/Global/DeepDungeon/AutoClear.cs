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
    private long _lastAttributedInteractionSequence;
    private string? _lastOvercapCorrelationLogKey;
    private DateTime _lastOvercapCorrelationLogAt;
    private int _suppressedOvercapCorrelationLogs;
    private readonly HashSet<ulong> _fakeExits = [];
    private DeepDungeonState.PomanderState[] _lastPomanderSnapshot = [];
    private bool _pomanderSnapshotReady;
    private DeepDungeonState.DungeonType _observedDungeon;
    private byte _observedFloor;

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
            .Select(a => $"{a.InstanceID}:room:{(TryFindClosestRoom(a.Position, out var passageRoom) ? passageRoom.Index.ToString() : "?")}/near:{IsInThisRoomOrAdjacent(a)}/goalAllowed:{PassageActorGoalAllowed}"));
        Service.Log($"[AutoDD] actor navigation selection changed: selected={chosen}, changesSinceLastLog={_pendingActorGoalSelectionChanges}, transitions=[{transitions}{(_otherActorSelectionTransitions > 0 ? $",other:{_otherActorSelectionTransitions}" : "")}], lastTransitionContext={_lastActorSelectionContext}, rule=goal type then distance among movement-eligible actors, playerRoom={PlayerRoom}, playerXZ=({player.Position.X:F1},{player.Position.Z:F1}),inCombat={player.InCombat}, passageActive={Palace.PassageActive}, passageActorGoalAllowed={PassageActorGoalAllowed}, candidates=[{candidates}],passageRooms=[{passageRooms}].");
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
        _lastPomanderSnapshot = ws.DeepDungeon.Pomanders.ToArray();
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
            _floorInitializationGate.BeginTransition();
            ResetPomanderSnapshot();
            return;
        }

        if (Palace.DungeonId != previousDungeon)
        {
            if (previousDungeon != DeepDungeonState.DungeonType.None || !BetweenFloors)
                ClearState();
            _floorInitializationGate.ObserveContextChange();
            ResetPomanderSnapshot();
            Service.Log($"[AutoDD] dungeon context changed: dungeon={Palace.DungeonId}, floor={Palace.Floor}; awaiting current floor map before room initialization.");
        }
        else if (Palace.Floor != previousFloor)
        {
            if (!BetweenFloors)
                ClearState();
            _floorInitializationGate.ObserveContextChange();
            _pendingPomanderReadbacks.Clear();
            Service.Log($"[AutoDD] floor context changed: dungeon={Palace.DungeonId}, floor={Palace.Floor}; awaiting current floor map before room initialization.");
        }
    }

    private void ResetPomanderSnapshot()
    {
        _pomanderSnapshotReady = false;
        _pendingPomanderReadbacks.Clear();
    }

    private void TryInitializeFloor(string cause, bool mapDataUpdate = false)
    {
        if (!BetweenFloors || !_floorInitializationGate.CanInitialize(
            DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor),
            Palace.Rooms.Any(room => room != 0), mapDataUpdate))
            return;

        Service.Log($"[AutoDD] floor map initialized: cause={cause}, dungeon={Palace.DungeonId}, floor={Palace.Floor}, territory={World.CurrentZone}, tileset={DeepDungeonPolicy.DiagnosticTileset(Palace.IsBossFloor, Palace.Progress.Tileset)}, activeRooms={Palace.Rooms.Count(room => room != 0)}.");
        LoadWalls();
        LoadGeometry();
        FilterTraps(reloadProvider: true);
        BetweenFloors = false;
        _floorInitializationGate.MarkInitialized();
    }

    protected override void Dispose(bool disposing)
    {
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
            case 7255: // safety used
            case 7256: // sight used
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
        Kills = 0;
        Array.Fill(_playerImmunes, default);
        _chestContentsGold.Clear();
        _chestContentsSilver.Clear();
        World.DeepDungeonInteractions.Clear();
        _lastAttributedInteractionSequence = 0;
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
        return DeepDungeonPolicy.CanAutoUsePomander(
            validDungeonAndAction,
            validSlot,
            state.Count,
            state.Usable,
            Palace.Party.Count(member => member.EntityId > 0) <= 1,
            player.FindStatus(SID.ItemPenalty) != null,
            _config.AutoPoms[(int)p],
            requiresPox,
            !requiresPox || player.FindStatus(1087) != null,
            out reason);
    }

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
        LogEnableState();
        TryInitializeFloor("AI hint update");
        ExpirePomanderReadbacks();
        if (!_config.Enable)
        {
            MostInterestingObject = null;
            InterestingObjects.Clear();
        }

        if (!DeepDungeonPolicy.IsFloorContextReady((int)Palace.DungeonId, Palace.Floor))
        {
            LogAutoDDBlocker("waiting for deep dungeon state initialization");
            ResetObjectiveProgress("waiting for deep dungeon state initialization");
            return;
        }

        if (player.IsDead)
        {
            MostInterestingObject = null;
            InterestingObjects.Clear();
            LogAutoDDBlocker("player dead");
            ResetObjectiveProgress("player dead");
            return;
        }

        if (Palace.IsBossFloor || BetweenFloors)
        {
            var blocker = Palace.IsBossFloor ? "boss floor" : "between floors / map not initialized";
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
                hints.AddForbiddenZone(ShapeDistance.Union(tshape));
        }

        DrawAOEs(playerSlot, player, hints);

        if (_config.ForbidDOTs)
            foreach (var hpt in hints.PotentialTargets)
                hpt.ForbidDOTs = true;

        if (_config.Enable)
            CalculateExtraHints(playerSlot, player, hints);

        if (!geometryReady)
        {
            MostInterestingObject = null;
            LogAutoDDBlocker($"room geometry unavailable: {geometryReason}");
            foreach (var actor in World.Actors)
                if (RevealedTrapOIDs.Contains(actor.OID))
                    hints.AddForbiddenZone(ShapeDistance.Circle(actor.Position, TrapRadius));
            ResetObjectiveProgress("room geometry unavailable");
            return;
        }

        var numAggro = hints.PotentialTargets.Count(t => t.Actor.AggroPlayer && !t.Actor.IsDeadOrDestroyed);

        if (!TryFindClosestRoom(player.Position, out var playerRoom))
        {
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

        if (canNavigate && roomNavigationEnabled)
            HandleFloorPathfind(player, hints);

        if (!_config.Enable)
        {
            // HandleFloorPathfind may have completed and cleared a manual destination this frame.
            manualNavigation = DesiredRoom.Reason == PathfindReason.Manual;
            if (manualNavigation)
            {
                LogAutoDDBlocker(canNavigate ? null : $"manual navigation blocked: pull limit reached ({numAggro}/{_config.MaxPull})");
                LogObjectiveProgress(player, hints, numAggro, canNavigate);
            }
            else
            {
                LogAutoDDBlocker("AutoDD disabled (manual minimap destination only)");
                ResetObjectiveProgress("AutoDD disabled");
            }
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
                LogOvercapDecision(a, pid, canAutoUse, reason, state);
                if (canAutoUse)
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
            if (IsInThisRoomOrAdjacent(a))
            {
                if (!isTransformed)
                {
                    if (oid == OID.BandedCoffer && a.IsTargetable)
                        InterestingObjects.Add(new(a, GoalType.ChestHoard, dist));

                    if (_config.OpenTreasure)
                    {
                        if (oid == OID.GoldCoffer && a.IsTargetable)
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 3));

                        if (oid == OID.SilverCoffer && OpenSilver && a.IsTargetable)
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 2));

                        if (BronzeChestIDs.Contains(a.OID) && DeepDungeonPolicy.ShouldConsiderBronzeTreasure(a.IsTargetable, a.IsOpenTreasure))
                            InterestingObjects.Add(new(a, GoalType.Treasure, dist, 1));
                    }
                }

                if (oid == OID.BandedCofferIndicator && Palace.Progress.HoardCurrentFloor)
                    InterestingObjects.Add(new(a, GoalType.Hoard, dist));

                if (oid is OID.CairnPalace or OID.BeaconHoH or OID.PylonEO or OID.PylonPT && Palace.PassageActive && !player.InCombat)
                    InterestingObjects.Add(new(a, GoalType.Passage, dist));
            }

            if (RevealedTrapOIDs.Contains(a.OID))
                hints.AddForbiddenZone(ShapeDistance.Circle(a.Position, TrapRadius));
        }

        PickARoom(player, hints);

        InterestingObjects.SortBy(o => (o.Type, o.PlayerDistSq));

        MostInterestingObject = InterestingObjects.FirstOrNull(o => DeepDungeonPolicy.ShouldConsiderActorForNavigation(o.Type == GoalType.Passage, PassageActorGoalAllowed));
        LogSelectedActorGoal(player);

        LogAutoDDBlocker(canNavigate ? null : $"pull limit reached ({numAggro}/{_config.MaxPull})");

        var goalWeight = player.InCombat ? 1 : 0.01f;

        if (canNavigate)
        {
            switch (MostInterestingObject)
            {
                case { Type: GoalType.Passage, Actor: var c } when PassageActorGoalAllowed:
                    hints.AddGoalZone(AIHints.GoalSingleTarget(c.Position, 2, goalWeight), $"deepdungeon-passage:{c.InstanceID}:interact-range");
                    hints.AddGoalZone(AIHints.GoalSingleTarget(c.Position, 25, goalWeight), $"deepdungeon-passage:{c.InstanceID}:approach");
                    hints.AddGoalZone(DeepDungeonPolicy.PassageProgressGoal(c.Position, player.Position, 25, 100, 1, goalWeight), $"deepdungeon-passage:{c.InstanceID}:progress");
                    break;

                case { Type: GoalType.Hoard, Actor: var h }:
                    hints.AddGoalZone(AIHints.GoalSingleTarget(h.Position, 2, 10), $"deepdungeon-hoard:{h.InstanceID}");
                    break;

                case { IsTreasure: true, Actor: var d }:
                    hints.AddGoalZone(AIHints.GoalSingleTarget(d.Position, 25, DeepDungeonPolicy.TreasureApproachWeight(player.InCombat)), $"deepdungeon-treasure:{d.InstanceID}");
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
            // enemy is petrified, any damage will kill
            if (pp.Actor.FindStatus(SID.StoneCurse)?.ExpireAt > World.FutureTime(1.5f))
                pp.ForcePriority(0);

            // pomander of storms was used, enemy can't autoheal; any damage will kill
            else if (pp.Actor.FindStatus(SID.AutoHealPenalty) != null && pp.Actor.PendingHPRaw is > 0 and < 10)
                pp.ForcePriority(0);

            // if not at max pull size, prioritize mobs
            else if (canTarget && (numAggro < _config.MaxPull || _config.MaxPull == 0 && numAggro == 0) && !pp.Actor.Statuses.Any(s => IsDangerousOutOfCombatStatus(s.ID)) && IsInThisRoomOrAdjacent(pp.Actor) && !pp.Actor.AggroPlayer)
            {
                // passive mobs shouldn't be prioritized over aggro'd ones
                if (player.InCombat)
                    pp.Priority = 0;
                else
                    pp.ShouldBeTargeted = true;
            }
        }

        LogObjectiveProgress(player, hints, numAggro, canNavigate);
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
