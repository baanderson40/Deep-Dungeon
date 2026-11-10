using Autofac.Features.AttributeFilters;
using BossMod.AI;
using BossMod.Autorotation;
using BossMod.Dev;
using BossMod.Interfaces;
using DalaMock.Host.Mediator;
using DalaMock.Shared.Interfaces;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BossMod.Services;

internal sealed class TickService(
    ILogger<DisposableMediatorSubscriberBase> mLogger,
    MediatorService mediator,
    [KeyFilter(Keys.ConfigDirectory)] DirectoryInfo configDir,
    [KeyFilter(Keys.ReplayDirectory)] DirectoryInfo replayDir,
    IUiBuilder uiBuilder,
    ICondition condition,
    IPluginLog logger,
    IClientState clientState,
    IFileDialogManager fileDialogManager,
    ICamera camera,
    IGameGui gameGui,
    IAmex amex,
    IMovementOverride movementOverride,
    IWorldStateGameSync wsSync,
    IHintExecutor hintExecutor,
    ConfigRoot configRoot,
    BossModuleManager bossmod,
    RotationDatabase rotationDB,
    RotationModuleManager rotation,
    ZoneModuleManager zonemod,
    ReplayManager replayManager,
    WorldState worldstateGlobal,
    AIHintsBuilder hintsBuilder,
    AIHints hintsGlobal,
    IEnumerable<MainDevWindow> devWindow,
    IEnumerable<MainDebugWindow> debugWindow
) : DisposableMediatorSubscriberBase(mLogger, mediator), IHostedService
{
    private enum DeepDungeonDepartureStage { None, BossDefeated, LeaveRequested, PostLeaveTerritory, WaitingRoom, MovingToExit, InteractingWithExit }

#nullable disable
    private BossModuleMainWindow _wndBossmod;
    private BossModuleHintsWindow _wndBossmodHints;
    private ZoneModuleWindow _wndZone;
    private ReplayManagementWindow _wndReplay;
    private UIRotationWindow _wndRotation;
    private AIWindow _wndAI;
    private ConfigWindow _wndConfig;
    private ConfigChangelogWindow _wndChangelog;
#nullable restore

    private TimeSpan _prevUpdateTime;
    private readonly DtrConfig _uiConfig = configRoot.Get<DtrConfig>();
    private IAutoDungeonAutorotationConfig? _autoDDConfig = ResolveAutoDDConfig(configRoot);
    private bool _deepDungeonVisit;
    private bool _deepDungeonSuspended;
    private ushort _deepDungeonTerritory;
    private Preset? _autoDungeonPreset;
    private Preset[]? _presetsBeforeAutoDungeon;
    private long _presetVersionAtEntry;
    private long _presetVersionAfterAutoActivation;
    private bool _loggedMissingDungeonPreset;
    private bool _autoActivationWasEnabled;
    private Type? _trackedDeepDungeonBossType;
    private Actor? _trackedDeepDungeonBossActor;
    private DeepDungeonState.DungeonType _trackedBossDungeon;
    private byte _trackedBossFloor;
    private bool _trackedBossWasAlive;
    private bool _deepDungeonBossDefeated;
    private bool _completionAutorotationSuppressed;
    private ushort _bossSourceTerritory;
    private ushort _expectedWaitingTerritory;
    private ushort _postLeaveTerritory;
    private DeepDungeonDepartureStage _departureStage;
    private DateTime _departureStageChangedAt;
    private ulong _departureExitActorID;
    private Vector3 _departureExitPosition;
    private float _departureExitHitbox;
    private bool _departurePathOwned;
    private bool _departurePathStopWhenReady;
    private bool? _lastVnavReady;
    private bool? _lastPathRunning;
    private string? _lastDepartureDiagnostic;
    private readonly ICallGateSubscriber<bool> _vnavIsReady = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private readonly ICallGateSubscriber<bool> _vnavPathfindInProgress = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _vnavMoveCloseTo = Service.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
    private readonly ICallGateSubscriber<bool> _vnavSimpleMoveInProgress = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
    private readonly ICallGateSubscriber<bool> _vnavPathIsRunning = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    private readonly ICallGateSubscriber<List<Vector3>> _vnavPathWaypoints = Service.PluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
    private readonly ICallGateSubscriber<object> _vnavPathStop = Service.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        MigratePlans(configDir.FullName);

        _wndConfig = new(new(worldstateGlobal, replayDir, rotationDB));
        _wndBossmod = new BossModuleMainWindow(bossmod, zonemod, hintsGlobal);
        _wndBossmodHints = new BossModuleHintsWindow(bossmod, zonemod, hintsGlobal);
        _wndZone = new ZoneModuleWindow(zonemod, rotation);
        _wndReplay = new ReplayManagementWindow(worldstateGlobal, bossmod, replayDir, replayManager);
        _wndRotation = new UIRotationWindow(rotation, amex);
        _wndAI = new AIWindow(rotation, MediatorService);
        _wndChangelog = new();

        if (Service.IsMock)
            configRoot.Get<ReplayManagementConfig>().ShowUI = _wndReplay.IsOpen = true;

        devWindow.FirstOrDefault()?.IsOpen = true;

        if (configRoot.Get<DeveloperConfig>().DebugWindowOpen)
            debugWindow.FirstOrDefault()?.IsOpen = true;

        Version cutoff = new(7, 5, 1, 13);
        if (configRoot.PreviousAssemblyVersion < cutoff && configRoot.AssemblyVersion >= cutoff)
        {
            logger.Info($"Removing potentially broken bitmaps from {hintsBuilder.Obstacles.UserRoot.FullName}");
            hintsBuilder.Obstacles.ClearGenerated();
        }

        // TODO: this should be in worldstate, but how?
        clientState.Logout += OnLogout;

        uiBuilder.Draw += Update;
        uiBuilder.DisableAutomaticUiHide = true;
        uiBuilder.OpenConfigUi += OpenUi;
        uiBuilder.OpenMainUi += OpenUi;

        condition.ConditionChange += OnConditionChanged;

        MediatorService.Subscribe<OpenWindow>(this, o =>
        {
            Service.WindowSystem.Windows.FirstOrDefault(w => w.GetType() == o.WindowType)?.IsOpen = true;
        });
        MediatorService.Subscribe<ToggleWindow>(this, t =>
        {
            Service.WindowSystem.Windows.FirstOrDefault(w => w.GetType() == t.WindowType)?.IsOpen ^= true;
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        clientState.Logout -= OnLogout;

        uiBuilder.Draw -= Update;
        uiBuilder.OpenConfigUi -= OpenUi;
        uiBuilder.OpenMainUi -= OpenUi;

        condition.ConditionChange -= OnConditionChanged;
        StopOwnedDeparturePath("service stopping");
    }

    void Update()
    {
        var tsStart = DateTime.Now;
        var moveImminent = movementOverride.IsMoveRequested() && (!amex.Config.PreventMovingWhileCasting || movementOverride.IsForceUnblocked());

        camera.Update();
        wsSync.Update(_prevUpdateTime);
        bossmod.Update();
        zonemod.ActiveModule?.Update();
        hintsBuilder.Update(hintsGlobal, PartyState.PlayerSlot, moveImminent);
        UpdateDeepDungeonBossCompletion();
        UpdateDeepDungeonDepartureHints();
        MonitorCancelledDeparturePath();
        hintsGlobal.ShouldLeaveDuty |= DeepDungeonPolicy.ShouldRequestDeepDungeonLeave(
            _departureStage == DeepDungeonDepartureStage.LeaveRequested,
            worldstateGlobal.CurrentZone == _bossSourceTerritory,
            rotation.Player is { IsDead: false, InCombat: false, CastInfo: null },
            IsLoadingOrTransitioning());
        amex.QueueManualActions();
        UpdateDeepDungeonAutorotation();
        rotation.Update(amex.AnimationLockDelayEstimate, movementOverride.IsMoving(), condition[ConditionFlag.DutyRecorderPlayback]);
        amex.FinishActionGather();
        wsSync.UpdateReplayState(rotation, movementOverride, hintsGlobal);

        var uiHidden = _uiConfig.HideWithGameUi && (gameGui.GameUiHidden || condition.Any(ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene78, ConditionFlag.WatchingCutscene));
        if (!uiHidden)
        {
            Service.WindowSystem.Draw();
            fileDialogManager.Draw();
        }

        hintExecutor.Execute();

        _prevUpdateTime = DateTime.Now - tsStart;
    }

    private void UpdateDeepDungeonAutorotation()
    {
        var dd = worldstateGlobal.DeepDungeon;
        var inDeepDungeon = dd.DungeonId is DeepDungeonState.DungeonType.POTD or DeepDungeonState.DungeonType.HOH or DeepDungeonState.DungeonType.EO or DeepDungeonState.DungeonType.PT
            && dd.Floor > 0;

        if (inDeepDungeon)
        {
            if (!_deepDungeonVisit)
            {
                _deepDungeonVisit = true;
                _deepDungeonTerritory = worldstateGlobal.CurrentZone;
                _presetVersionAtEntry = rotation.PresetSelectionVersion;
                _deepDungeonSuspended = false;
                _loggedMissingDungeonPreset = false;
            }
            else if (worldstateGlobal.CurrentZone != _deepDungeonTerritory && !IsLoadingOrTransitioning())
            {
                // Dungeon floor/instance context is authoritative while active; retain ownership across zone changes within it.
                _deepDungeonTerritory = worldstateGlobal.CurrentZone;
            }

            _autoDDConfig ??= ResolveAutoDDConfig(configRoot);
            var autoActivationEnabled = _autoDDConfig?.AutoActivateDungeonAI == true;
            if (autoActivationEnabled && !_autoActivationWasEnabled)
            {
                // Enabling the option in an already-running visit is an explicit request to arm it now.
                _presetVersionAtEntry = rotation.PresetSelectionVersion;
                _deepDungeonSuspended = false;
            }
            _autoActivationWasEnabled = autoActivationEnabled;

            if (_autoDungeonPreset != null && rotation.PresetSelectionVersion != _presetVersionAfterAutoActivation)
            {
                // A manager-mediated user or safety change takes precedence over automation.
                _autoDungeonPreset = null;
                _presetsBeforeAutoDungeon = null;
                _deepDungeonSuspended = true;
                logger.Debug("[DungeonAI] Preset selection changed; automatic control suspended until leaving the Deep Dungeon.");
            }

            if (_deepDungeonBossDefeated)
                return;

            if (!autoActivationEnabled)
            {
                ReleaseAutomaticDungeonPreset(restorePrevious: true);
                _deepDungeonSuspended = true;
                return;
            }

            if (_deepDungeonSuspended || _autoDungeonPreset != null || !IsDungeonAutorotationReady())
                return;

            if (rotation.PresetSelectionVersion != _presetVersionAtEntry)
            {
                _deepDungeonSuspended = true;
                logger.Debug("[DungeonAI] Preset selection changed before activation; automatic control suspended until leaving the Deep Dungeon.");
                return;
            }

            if (rotation.IsForceDisabled)
            {
                _deepDungeonSuspended = true;
                return;
            }

            var preset = rotation.Database.Presets.DefaultPresets.FirstOrDefault(p => p.Name == "Dungeon AI");
            if (preset == null)
            {
                if (!_loggedMissingDungeonPreset)
                    logger.Warning("[DungeonAI] Built-in 'Dungeon AI' preset not found; automatic activation skipped.");
                _loggedMissingDungeonPreset = true;
                _deepDungeonSuspended = true;
                return;
            }

            if (rotation.Presets.Contains(preset))
            {
                // Preset was already selected by the user; do not claim or later restore their selection.
                _deepDungeonSuspended = true;
                return;
            }

            _presetsBeforeAutoDungeon = [.. rotation.Presets];
            _autoDungeonPreset = preset;
            rotation.Activate(preset, exclusive: true);
            _presetVersionAfterAutoActivation = rotation.PresetSelectionVersion;
            logger.Info("[DungeonAI] Deep Dungeon entered; activated built-in 'Dungeon AI' preset.");
            return;
        }

        // DungeonId can briefly be None during floor transitions. Only a territory change confirms exit.
        if (_departureStage != DeepDungeonDepartureStage.None)
            return;
        if (_deepDungeonVisit && worldstateGlobal.CurrentZone != _deepDungeonTerritory && !IsLoadingOrTransitioning())
        {
            ReleaseAutomaticDungeonPreset(restorePrevious: true);
            _deepDungeonVisit = false;
            _deepDungeonSuspended = false;
            _deepDungeonTerritory = 0;
            _presetVersionAtEntry = rotation.PresetSelectionVersion;
            _loggedMissingDungeonPreset = false;
            _autoActivationWasEnabled = false;
        }
    }

    private bool IsDungeonAutorotationReady() => rotation.Player is { IsDead: false } && !IsLoadingOrTransitioning();

    private static IAutoDungeonAutorotationConfig? ResolveAutoDDConfig(ConfigRoot configRoot)
    {
        var configType = configRoot.Nodes.FirstOrDefault(t => typeof(IAutoDungeonAutorotationConfig).IsAssignableFrom(t));
        if (configType == null)
            return null;

        if (configRoot.Get<ConfigNode>(configType) is not IAutoDungeonAutorotationConfig autoDDConfig)
            return null;

        // Migrate the former Autorotation-page setting once, preserving existing opt-in users.
        var legacyConfig = configRoot.Get<AutorotationConfig>();
        if (legacyConfig.AutoActivateDungeonAI)
        {
            autoDDConfig.AutoActivateDungeonAI = true;
            legacyConfig.AutoActivateDungeonAI = false;
            if (autoDDConfig is ConfigNode node)
                node.Modified.Fire();
            legacyConfig.Modified.Fire();
        }

        return autoDDConfig;
    }

    private void UpdateDeepDungeonBossCompletion()
    {
        var dd = worldstateGlobal.DeepDungeon;
        _autoDDConfig ??= ResolveAutoDDConfig(configRoot);
        if (_autoDDConfig?.AutoLeaveAfterBossDefeat != true)
        {
            if (_completionAutorotationSuppressed)
                ReleaseAutomaticDungeonPreset(restorePrevious: true);
            ResetDeepDungeonBossCompletion();
            return;
        }
        if (dd.DungeonId == DeepDungeonState.DungeonType.None || dd.Floor == 0 || !dd.IsBossFloor)
        {
            if (_deepDungeonBossDefeated && dd.DungeonId != DeepDungeonState.DungeonType.None && dd.Floor > 0
                && (dd.DungeonId != _trackedBossDungeon || dd.Floor != _trackedBossFloor))
            {
                ReleaseAutomaticDungeonPreset(restorePrevious: true);
                ResetDeepDungeonBossCompletion();
            }
            if (!_deepDungeonBossDefeated && _trackedBossDungeon != DeepDungeonState.DungeonType.None && (dd.DungeonId != _trackedBossDungeon || dd.Floor != _trackedBossFloor))
                ResetDeepDungeonBossCompletion();
            return;
        }

        if (_trackedBossDungeon != dd.DungeonId || _trackedBossFloor != dd.Floor)
        {
            ResetDeepDungeonBossCompletion();
            _trackedBossDungeon = dd.DungeonId;
            _trackedBossFloor = dd.Floor;
            _bossSourceTerritory = worldstateGlobal.CurrentZone;
        }

        if (_deepDungeonBossDefeated)
            return;

        var encounter = bossmod.LoadedModules.FirstOrDefault(module =>
        {
            var info = module.Info;
            return info != null && DeepDungeonPolicy.IsSupportedDeepDungeonBossEncounter(dd.DungeonId, dd.Floor,
                info.ModuleType.Namespace, info.GroupType, info.SortOrder, module.StateMachine.ActiveState != null);
        });

        if (encounter != null)
        {
            _trackedDeepDungeonBossType = encounter.Info?.ModuleType;
            _trackedDeepDungeonBossActor = encounter.PrimaryActor;
            if (!encounter.PrimaryActor.IsDeadOrDestroyed && encounter.PrimaryActor.HPMP.CurHP > 0)
                _trackedBossWasAlive = true;
        }

        // Observe death on the retained actor reference: the boss module may unload on its terminal phase.
        var actor = _trackedDeepDungeonBossActor;
        if (!_trackedBossWasAlive || actor == null || !actor.IsDead)
            return;

        _deepDungeonBossDefeated = true;
        _departureStage = DeepDungeonDepartureStage.BossDefeated;
        _departureStageChangedAt = worldstateGlobal.CurrentTime;
        _expectedWaitingTerritory = dd.DungeonId switch
        {
            DeepDungeonState.DungeonType.POTD => 570,
            DeepDungeonState.DungeonType.HOH => 780,
            _ => 0,
        };
        worldstateGlobal.DeepDungeon.BossCompletionObserved = true;
        hintsGlobal.ActionsToExecute.Entries.RemoveAll(entry => !entry.Manual && entry.Action.Type == ActionType.Pomander);
        if (_autoDungeonPreset != null && rotation.PresetSelectionVersion == _presetVersionAfterAutoActivation)
        {
            rotation.SetForceDisabled();
            _presetVersionAfterAutoActivation = rotation.PresetSelectionVersion;
            _completionAutorotationSuppressed = true;
        }
        logger.Info($"[AutoDD] Supported boss defeat observed: dungeon={dd.DungeonId}, floor={dd.Floor}, module={_trackedDeepDungeonBossType?.Name}, actor={actor.InstanceID}; owned autorotation suppressed before leave request.");
    }

    private void ResetDeepDungeonBossCompletion()
    {
        worldstateGlobal.DeepDungeon.BossCompletionObserved = false;
        _trackedDeepDungeonBossType = null;
        _trackedDeepDungeonBossActor = null;
        _trackedBossDungeon = DeepDungeonState.DungeonType.None;
        _trackedBossFloor = 0;
        _trackedBossWasAlive = false;
        _deepDungeonBossDefeated = false;
        _bossSourceTerritory = 0;
        _expectedWaitingTerritory = 0;
        _postLeaveTerritory = 0;
        _departureStage = DeepDungeonDepartureStage.None;
        _departureStageChangedAt = default;
    }

    private void UpdateDeepDungeonDepartureHints()
    {
        if (!_deepDungeonBossDefeated || _departureStage == DeepDungeonDepartureStage.None)
            return;

        _autoDDConfig ??= ResolveAutoDDConfig(configRoot);
        if (_autoDDConfig?.AutoLeaveAfterBossDefeat != true)
        {
            StopOwnedDeparturePath("automatic boss leave disabled");
            CancelDeepDungeonDeparture("automatic boss leave disabled");
            return;
        }

        if (_autoDungeonPreset != null && rotation.PresetSelectionVersion != _presetVersionAfterAutoActivation)
        {
            StopOwnedDeparturePath("preset selection changed");
            _autoDungeonPreset = null;
            _presetsBeforeAutoDungeon = null;
            _completionAutorotationSuppressed = false;
            CancelDeepDungeonDeparture("preset selection changed");
            return;
        }

        var currentTerritory = worldstateGlobal.CurrentZone;
        if (_departurePathOwned && movementOverride.IsMoveRequested())
        {
            StopOwnedDeparturePath("manual movement input detected");
            _departureStage = DeepDungeonDepartureStage.WaitingRoom;
            LogDepartureTransition("manual movement input took precedence over Exit approach");
        }
        if (currentTerritory == _bossSourceTerritory)
        {
            if (_departureStage == DeepDungeonDepartureStage.LeaveRequested
                && worldstateGlobal.CurrentTime - _departureStageChangedAt > TimeSpan.FromMinutes(2))
            {
                _departureStage = DeepDungeonDepartureStage.PostLeaveTerritory;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                logger.Warning("[AutoDD] boss-floor leave request timed out; automatic leave retries stopped, completion suppression retained.");
                return;
            }
            if (_departureStage is DeepDungeonDepartureStage.BossDefeated or DeepDungeonDepartureStage.LeaveRequested
                && rotation.Player is { IsDead: false, InCombat: false, CastInfo: null } && !IsLoadingOrTransitioning())
            {
                if (_departureStage == DeepDungeonDepartureStage.BossDefeated)
                {
                    logger.Info($"[AutoDD] boss-floor leave request ready: dungeon={_trackedBossDungeon}, floor={_trackedBossFloor}, territory={_bossSourceTerritory}.");
                    _departureStage = DeepDungeonDepartureStage.LeaveRequested;
                    _departureStageChangedAt = worldstateGlobal.CurrentTime;
                }
            }
            return;
        }

        if (IsLoadingOrTransitioning())
            return;

        if (_expectedWaitingTerritory != 0 && currentTerritory == _expectedWaitingTerritory)
        {
            if (_departureStage is not (DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit))
            {
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                LogDepartureTransition($"matching waiting room confirmed: dungeon={_trackedBossDungeon}, territory={currentTerritory}");
            }
        }
        else if (_departureStage is DeepDungeonDepartureStage.BossDefeated or DeepDungeonDepartureStage.LeaveRequested)
        {
            if (_expectedWaitingTerritory != 0)
            {
                CancelDeepDungeonDeparture($"unexpected territory after boss leave: {currentTerritory}");
                return;
            }
            _departureStage = DeepDungeonDepartureStage.PostLeaveTerritory;
            _postLeaveTerritory = currentTerritory;
            _departureStageChangedAt = worldstateGlobal.CurrentTime;
            logger.Info($"[AutoDD] post-boss territory observed: source={_bossSourceTerritory}, current={currentTerritory}; no further boss-floor leave requests.");
        }
        else if (_departureStage == DeepDungeonDepartureStage.PostLeaveTerritory && _postLeaveTerritory == 0)
        {
            _postLeaveTerritory = currentTerritory;
        }
        else if (_departureStage == DeepDungeonDepartureStage.PostLeaveTerritory && currentTerritory != _postLeaveTerritory)
        {
            CompleteDeepDungeonDeparture($"left post-boss territory; previous={_postLeaveTerritory}, current={currentTerritory}");
            return;
        }
        else if (_departureStage is DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit)
        {
            StopOwnedDeparturePath("waiting-room territory changed");
            CompleteDeepDungeonDeparture($"left waiting room; current territory={currentTerritory}");
            return;
        }

        if (_departureStage is not (DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit))
            return;

        if (currentTerritory != _expectedWaitingTerritory
            || rotation.Player is not { IsDead: false, InCombat: false, CastInfo: null } player)
        {
            StopOwnedDeparturePath("invalid player or waiting-room context");
            return;
        }
        if (movementOverride.IsMoveRequested())
        {
            LogDepartureDiagnostic("manual movement input active; waiting-room Exit movement paused");
            return;
        }

        var expectedExitOID = DeepDungeonPolicy.WaitingRoomExitBaseID(_trackedBossDungeon, currentTerritory);
        var exits = worldstateGlobal.Actors.Where(actor => actor.Type == ActorType.EventObj && actor.OID == expectedExitOID && actor.IsTargetable && !actor.IsDestroyed).ToArray();
        if (!DeepDungeonPolicy.IsVerifiedWaitingRoomExit(_trackedBossDungeon, currentTerritory, exits.FirstOrDefault()?.OID ?? 0,
            exits.Length == 1 && exits[0].IsTargetable, exits.Length))
        {
            StopOwnedDeparturePath("exit missing, ambiguous, or unsupported");
            LogDepartureDiagnostic($"exit not eligible: territory={currentTerritory}, baseID={expectedExitOID}, targetableMatches={exits.Length}");
            return;
        }

        var exit = exits[0];
        var destination = new Vector3(exit.PosRot.X, exit.PosRot.Y, exit.PosRot.Z);
        var range = exit.HitboxRadius + 2.0f;
        if (exit.InstanceID != _departureExitActorID || Vector3.Distance(destination, _departureExitPosition) > 0.25f || Math.Abs(exit.HitboxRadius - _departureExitHitbox) > 0.05f)
        {
            if (_departurePathOwned)
            {
                StopOwnedDeparturePath("exit actor or live destination changed");
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
            }
            _departureExitActorID = exit.InstanceID;
            _departureExitPosition = destination;
            _departureExitHitbox = exit.HitboxRadius;
            LogDepartureDiagnostic($"exit acquired: dungeon={_trackedBossDungeon}, territory={currentTerritory}, actor={exit.InstanceID}, baseID={exit.OID}, position={destination}, hitbox={exit.HitboxRadius:F2}, stopRange={range:F2}");
        }

        var centerDistance = Vector3.Distance(new Vector3(player.PosRot.X, player.PosRot.Y, player.PosRot.Z), destination);
        if (centerDistance <= range)
        {
            StopOwnedDeparturePath("within exit hitbox plus 2y");
            hintsGlobal.InteractWithTarget = exit;
            if (_departureStage != DeepDungeonDepartureStage.InteractingWithExit)
            {
                _departureStage = DeepDungeonDepartureStage.InteractingWithExit;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                LogDepartureTransition($"within interaction movement range: centerDistance={centerDistance:F2}, stopRange={range:F2}, actor={exit.InstanceID}");
            }
            return;
        }

        UpdateDepartureVnav(player, exit, destination, range, centerDistance);
    }

    private void CancelDeepDungeonDeparture(string reason)
    {
        StopOwnedDeparturePath(reason);
        if (_autoDungeonPreset != null && _completionAutorotationSuppressed)
            ReleaseAutomaticDungeonPreset(restorePrevious: true);
        ResetDeepDungeonBossCompletion();
        logger.Info($"[AutoDD] boss departure automation cancelled: reason={reason}.");
    }

    private void CompleteDeepDungeonDeparture(string reason)
    {
        StopOwnedDeparturePath(reason);
        ReleaseAutomaticDungeonPreset(restorePrevious: true);
        ResetDeepDungeonBossCompletion();
        _deepDungeonVisit = false;
        _deepDungeonSuspended = false;
        _deepDungeonTerritory = 0;
        _presetVersionAtEntry = rotation.PresetSelectionVersion;
        _loggedMissingDungeonPreset = false;
        _autoActivationWasEnabled = false;
        logger.Info($"[AutoDD] departure confirmed: {reason}.");
    }

    private void UpdateDepartureVnav(Actor player, Actor exit, Vector3 destination, float range, float distance)
    {
        try
        {
            var ready = _vnavIsReady.InvokeFunc();
            if (_lastVnavReady != ready)
            {
                _lastVnavReady = ready;
                LogDepartureDiagnostic($"vnav ready={ready}");
            }
            if (!ready)
            {
                StopOwnedDeparturePath("vnav unavailable");
                return;
            }

            var pathRunning = _vnavPathIsRunning.InvokeFunc();
            if (_lastPathRunning != pathRunning)
            {
                _lastPathRunning = pathRunning;
                LogDepartureDiagnostic($"vnav path running={pathRunning}");
            }
            var pathfindInProgress = _vnavPathfindInProgress.InvokeFunc() || _vnavSimpleMoveInProgress.InvokeFunc();
            if (_departurePathOwned)
            {
                if (pathRunning)
                {
                    var waypoints = _vnavPathWaypoints.InvokeFunc();
                    var matches = DeepDungeonPolicy.OwnsDeparturePath(pathRunning, waypoints.Count,
                        waypoints.Count > 0 ? Vector3.Distance(waypoints[^1], destination) : float.MaxValue, 1.5f);
                    if (!matches)
                    {
                        _departurePathOwned = false;
                        LogDepartureDiagnostic($"ownership lost: live path endpoint does not match Exit {exit.InstanceID}; path left untouched");
                        return;
                    }
                    LogDepartureDiagnostic($"owned path active: actor={exit.InstanceID}, stopRange={range:F2}, waypoints={waypoints.Count}");
                    return;
                }

                if (pathfindInProgress)
                {
                    LogDepartureDiagnostic($"owned pathfinding pending: actor={exit.InstanceID}, stopRange={range:F2}");
                    return;
                }

                _departurePathOwned = false;
                _departurePathStopWhenReady = false;
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
                LogDepartureDiagnostic($"owned path ended before arrival: actor={exit.InstanceID}, playerDistance={distance:F2}; retry eligible");
            }

            // An existing vnav route or pathfind belongs to another controller. Never replace it.
            if (!DeepDungeonPolicy.ShouldStartDeparturePath(ready, pathRunning, pathfindInProgress, true, rotation.Player is { IsDead: false, InCombat: false, CastInfo: null }))
            {
                LogDepartureDiagnostic($"vnav busy with unowned path; not taking control: actor={exit.InstanceID}");
                return;
            }

            var started = _vnavMoveCloseTo.InvokeFunc(destination, false, range);
            if (started)
            {
                _departurePathOwned = true;
                _departureStage = DeepDungeonDepartureStage.MovingToExit;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                LogDepartureTransition($"vnav approach requested: actor={exit.InstanceID}, position={destination}, hitbox={exit.HitboxRadius:F2}, stopRange={range:F2}, playerDistance={distance:F2}");
            }
            else
            {
                LogDepartureDiagnostic($"vnav rejected approach request: actor={exit.InstanceID}, position={destination}, stopRange={range:F2}, playerDistance={distance:F2}");
            }
        }
        catch (Exception ex)
        {
            StopOwnedDeparturePath($"vnav IPC failure: {ex.GetType().Name}");
            LogDepartureDiagnostic($"vnav IPC unavailable/error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopOwnedDeparturePath(string reason)
    {
        if (!_departurePathOwned)
            return;

        try
        {
            // Stop only when the currently running route still terminates at our live Exit.
            var running = _vnavPathIsRunning.InvokeFunc();
            var waypoints = running ? _vnavPathWaypoints.InvokeFunc() : [];
            var matches = DeepDungeonPolicy.OwnsDeparturePath(running, waypoints.Count,
                waypoints.Count > 0 ? Vector3.Distance(waypoints[^1], _departureExitPosition) : float.MaxValue, 1.5f);
            if (matches)
            {
                _vnavPathStop.InvokeAction();
                LogDepartureDiagnostic($"stopped owned vnav path: reason={reason}, actor={_departureExitActorID}, endpoint={waypoints[^1]}");
            }
            else if (running)
            {
                LogDepartureDiagnostic($"did not stop changed vnav path: reason={reason}, expected actor={_departureExitActorID}; path left untouched");
            }
            else
            {
                if (_vnavSimpleMoveInProgress.InvokeFunc())
                {
                    // vnav has no cancellation API for an accepted async SimpleMove request.
                    // Keep ownership until it resolves, then stop only if its route targets this Exit.
                    _departurePathStopWhenReady = true;
                    LogDepartureDiagnostic($"owned async path request still pending; defer stop until route is visible: reason={reason}, actor={_departureExitActorID}");
                    return;
                }
                LogDepartureDiagnostic($"owned vnav path no longer running: reason={reason}, actor={_departureExitActorID}");
            }
        }
        catch (Exception ex)
        {
            LogDepartureDiagnostic($"could not verify/stop owned vnav path: reason={reason}, error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!_departurePathStopWhenReady)
                _departurePathOwned = false;
        }
    }

    private void MonitorCancelledDeparturePath()
    {
        if (!_departurePathStopWhenReady)
            return;
        try
        {
            if (_vnavSimpleMoveInProgress.InvokeFunc())
                return;

            var running = _vnavPathIsRunning.InvokeFunc();
            var waypoints = running ? _vnavPathWaypoints.InvokeFunc() : [];
            var matches = DeepDungeonPolicy.OwnsDeparturePath(running, waypoints.Count,
                waypoints.Count > 0 ? Vector3.Distance(waypoints[^1], _departureExitPosition) : float.MaxValue, 1.5f);
            if (matches)
            {
                _vnavPathStop.InvokeAction();
                LogDepartureDiagnostic($"stopped deferred owned vnav path after async request resolved: actor={_departureExitActorID}, endpoint={waypoints[^1]}");
            }
            else if (running)
            {
                LogDepartureDiagnostic($"deferred request resolved to a different route; left untouched: actor={_departureExitActorID}");
            }
            _departurePathOwned = false;
            _departurePathStopWhenReady = false;
        }
        catch (Exception ex)
        {
            LogDepartureDiagnostic($"could not monitor deferred vnav path: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LogDepartureTransition(string message)
    {
        _lastDepartureDiagnostic = null;
        logger.Info($"[AutoDD] departure transition: {message}.");
    }

    private void LogDepartureDiagnostic(string message)
    {
        if (_lastDepartureDiagnostic == message)
            return;
        _lastDepartureDiagnostic = message;
        logger.Debug($"[AutoDD] departure diagnostic: {message}.");
    }

    private bool IsLoadingOrTransitioning() => condition.Any(
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.DutyRecorderPlayback);

    private void ReleaseAutomaticDungeonPreset(bool restorePrevious)
    {
        if (_autoDungeonPreset == null)
            return;

        var canRestore = restorePrevious
            && rotation.PresetSelectionVersion == _presetVersionAfterAutoActivation
            && (rotation.Presets.Contains(_autoDungeonPreset) || _completionAutorotationSuppressed && rotation.IsForceDisabled);
        if (canRestore)
        {
            rotation.Clear();
            foreach (var preset in _presetsBeforeAutoDungeon ?? [])
            {
                var currentPreset = rotation.Database.Presets.DefaultPresets.FirstOrDefault(p => p.Name == preset.Name)
                    ?? rotation.Database.Presets.UserPresets.FirstOrDefault(p => p.Name == preset.Name);
                if (currentPreset != null)
                    rotation.Activate(currentPreset);
            }
            logger.Info("[DungeonAI] Deep Dungeon exited; restored previous autorotation presets.");
        }

        _autoDungeonPreset = null;
        _presetsBeforeAutoDungeon = null;
        _completionAutorotationSuppressed = false;
    }

    void OnConditionChanged(ConditionFlag flag, bool value)
    {
        logger.Debug($"Condition change: {flag}={value}");
    }

    void OnLogout(int type, int code)
    {
        StopOwnedDeparturePath("logout");
        ReleaseAutomaticDungeonPreset(restorePrevious: true);
        _deepDungeonVisit = false;
        ResetDeepDungeonBossCompletion();
        _deepDungeonSuspended = false;
        _deepDungeonTerritory = 0;
        _autoActivationWasEnabled = false;
        bossmod.ForceUnload($"logout type={type}, code={code}");
        wsSync.RefreshInventory();
    }

    void OpenConfigUI(string tab = "")
    {
        MediatorService.Publish(new OpenSettingsWindow(tab));
    }

    void OpenUi() => OpenConfigUI();

    private void MigratePlans(string pluginConfigDir)
    {
        var source = Path.Join(pluginConfigDir, "autorot");

        // no autorot dir means fresh install
        if (!Path.Exists(source))
        {
            logger.Verbose($"[Migrator] No configs, nothing to do.");
            return;
        }

        var destination = Path.Join(Plugin.GetStorageDir(), "autorot");

        if (File.Exists(Path.Join(destination, ".migrate-ok")))
        {
            logger.Verbose($"[Migrator] Nothing to do.");
            return;
        }

        Utils.CopyRecursive(source, destination);

        File.Create(Path.Join(destination, ".migrate-ok"));

        logger.Info($"[Migrator] Done.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        _wndChangelog.Dispose();
        _wndAI.Dispose();
        _wndRotation.Dispose();
        _wndReplay.Dispose();
        _wndZone.Dispose();
        _wndBossmodHints.Dispose();
        _wndBossmod.Dispose();
        _wndConfig.Dispose();
    }
}
