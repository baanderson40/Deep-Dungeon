using Dalamud.Plugin.Ipc.Exceptions;
using static FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon;

namespace BossMod.Global.DeepDungeon;

abstract partial class AutoClear : ZoneModule
{
    private readonly List<Vector3> _trapsCurrentZone = [];
    private readonly List<WPos> ProblematicTrapLocations = [];
    private bool _trapsHidden = true;
    private HistoricalTrapInputState _palantirState = HistoricalTrapInputState.Unavailable;
    private string _palantirStatus = "not queried";
    private string _palantirFloorStatus = "floor data unavailable";
    private string? _palantirStateKey;
    private string? _palantirFloorDiagnosticKey;
    private string? _palantirError;
    private string _palantirProvider = "unqueried";
    private ushort _palantirTerritory;
    private bool _palantirLoaded;
    private DateTime _lastPalantirErrorLogAt;

    private readonly List<Vector3> _trapsCurrentFloor = [];

    private void FilterTraps(bool reloadProvider = false)
    {
        _trapsCurrentFloor.Clear();
        var territory = World.CurrentZone;
        if (reloadProvider || !_palantirLoaded || _palantirTerritory != territory)
        {
            _trapsCurrentZone.Clear();
            _palantirLoaded = true;
            _palantirTerritory = territory;
            try
            {
                var fetch = PalantirTrapData.Fetch(
                    () => _palantirGetTraps.InvokeFunc(territory),
                    () => _palantirListTraps.InvokeFunc(territory),
                    error => error is IpcNotReadyError);
                _palantirProvider = fetch.Provider;
                if (fetch.Error is { } fetchError)
                {
                    _palantirState = fetch.Unavailable ? HistoricalTrapInputState.Unavailable : HistoricalTrapInputState.Error;
                    _palantirError = PalantirTrapData.ErrorSummary(fetchError);
                    _palantirStatus = $"provider {(fetch.Unavailable ? "unavailable" : "error")}: provider={fetch.Provider}, territory={territory}";
                }
                else
                {
                    var input = PalantirTrapData.Process(fetch.Response, t => ProblematicTrapLocations.Any(l => l.AlmostEqual(t.ToWPos(), 0.1f)));
                    _trapsCurrentZone.AddRange(input.Locations);
                    _palantirState = input.State;
                    _palantirError = null;
                    _palantirStatus = $"{input.State}: provider={fetch.Provider}, territory={territory}, returned={input.Returned}, invalid={input.Invalid}, problematic={input.Problematic}, accepted={input.Accepted}";
                }
            }
            catch (Exception error)
            {
                _palantirState = HistoricalTrapInputState.Error;
                _palantirError = PalantirTrapData.ErrorSummary(error);
                _palantirProvider = "unknown";
                _palantirStatus = $"provider error: territory={territory}";
            }

            var stateKey = $"{_palantirState}:{_palantirProvider}:{territory}";
            var isError = _palantirState is HistoricalTrapInputState.Unavailable or HistoricalTrapInputState.Error;
            if (PalantirTrapData.ShouldLogProviderState(_palantirStateKey, stateKey, isError, _lastPalantirErrorLogAt, World.CurrentTime))
            {
                Service.Log($"[AutoDD] Palantir historical trap input: {_palantirStatus}{(_palantirError == null ? "" : $"; error={_palantirError}")}. These are historical candidates, not confirmed active traps.");
                if (isError)
                    _lastPalantirErrorLogAt = World.CurrentTime;
            }
            _palantirStateKey = stateKey;
        }

        var filteredOut = 0;
        if (TryGetCurrentRoomRects(out var roomRects, out var geometryReason))
        {
            var floorInput = PalantirTrapData.FilterFloor(_trapsCurrentZone, t => Enumerable.Range(0, Palace.Rooms.Length)
                .Any(i => Palace.Rooms[i] > 0 && !Palace.Rooms[i].HasFlag(RoomFlags.Home) && roomRects[i].Contains(t)));
            _trapsCurrentFloor.AddRange(floorInput.Locations);
            filteredOut = floorInput.FilteredOut;
            _palantirFloorStatus = $"floorAccepted={_trapsCurrentFloor.Count}, floorFiltered={filteredOut}";
        }
        else
            _palantirFloorStatus = $"floor unavailable ({geometryReason})";

        var floorDiagnosticKey = $"{_palantirState}:{territory}:{Palace.DungeonId}:{Palace.Floor}:{_trapsCurrentFloor.Count}:{filteredOut}:{_palantirFloorStatus}";
        if (_palantirFloorDiagnosticKey != floorDiagnosticKey)
        {
            Service.Log($"[AutoDD] Palantir historical trap floor filter: dungeon={Palace.DungeonId}, floor={Palace.Floor}, territory={territory}, state={_palantirState}, {_palantirFloorStatus}. Locations are historical candidates, not confirmed active traps.");
            _palantirFloorDiagnosticKey = floorDiagnosticKey;
        }

        // Revealed trap actors remain authoritative and independent of Palantir data.
        foreach (var a in World.Actors)
            HandleBeacon(a);
    }

    private void DrawTraps()
    {
        if (_config.DrawTraps)
            foreach (var p in _trapsCurrentFloor)
            {
                Service.Camera.AddCircleFilled(p, TrapRadius, 0x402929FF, occluded: false);
                Service.Camera.AddCircle(p, TrapRadius, 0xFF2929FF, 2, occluded: false);
            }
    }

    private void HandleTrap(Actor actor, ActorCastEvent ev)
    {
        var trapType = ev.Action.ID switch
        {
            6275 or 6276 or 6277 or 6278 or 6279 => "standard",
            11287 => "otter",
            32375 => "owl",
            44038 => "fae",
            _ => null,
        };
        if (trapType == null)
            return;

        var hasRoom = TryFindClosestRoom(actor.Position, out var room);
        if (hasRoom)
            _trapsCurrentFloor.RemoveAll(room.Room.Contains);

        var player = World.Party.Player();
        var engagedEnemies = World.Actors.Count(a => a.Type == ActorType.Enemy && !a.IsAlly && !a.IsDeadOrDestroyed && (a.InCombat || a.AggroPlayer));
        _lastTrapActivationAt = World.CurrentTime;
        _lastTrapActivation = $"{trapType}/action:{ev.Action.ID}/actor:{actor.InstanceID}@({actor.Position.X:F1},{actor.Position.Z:F1})/room:{(hasRoom ? room.Index.ToString() : "?")}";
        Service.Log($"[AutoDD] trap activation observed: type={trapType}, action={ev.Action.ID}, actor={actor.Name}:{actor.InstanceID}, position=({actor.Position.X:F1},{actor.Position.Z:F1}), room={(hasRoom ? room.Index.ToString() : "?")}, playerPosition={(player != null ? $"({player.Position.X:F1},{player.Position.Z:F1})" : "unknown")}, engagedEnemies={engagedEnemies}; event confirms trap action, not trap cause of aggro/death.");
    }

    private void HandleBeacon(Actor c)
    {
        if ((OID)c.OID is OID.BeaconHoH or OID.BandedCofferIndicator)
            _trapsCurrentFloor.RemoveAll(t => t.ToWPos().InCircle(c.Position, 2));
    }
}
