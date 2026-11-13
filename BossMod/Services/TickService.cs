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

internal sealed partial class TickService(
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
    private enum DeepDungeonDepartureStage { None, BossDefeated, LeaveRequested, PostLeaveTerritory, WaitingRoom, MovingToExit, InteractingWithExit, AwaitingExitTransition }

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
    private long _completionSuppressionToken;
    private ushort _bossSourceTerritory;
    private ushort _expectedWaitingTerritory;
    private ushort _postLeaveTerritory;
    private DeepDungeonDepartureStage _departureStage;
    private DateTime _departureStageChangedAt;
    private DateTime _departureManualInputResumeAt;
    private DateTime _lastManualInputDiagnosticAt;
    private ulong _departureExitActorID;
    private IntPtr _departureExitRawAddress;
    private ulong _departureExitRawEntityID;
    private Vector3 _departureExitPosition;
    private float _departureExitHitbox;
    private DateTime _exitDiscoveryStartedAt;
    private DateTime _nextExitDiscoveryPollAt;
    private DateTime _lastExitDiscoveryStatusAt;
    private int _exitDiscoveryPollCount;
    private bool _exitDiscoveryTimedOut;
    private long _exitConfirmationArmedAt;
    private long _exitConfirmationSubmittedAt;
    private bool _exitConfirmationVisible;
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
        _continuationMapSubscription = worldstateGlobal.DeepDungeon.MapDataChanged.Subscribe(_ => ObserveContinuationMap());
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
        _continuationMapSubscription?.Dispose();
        _continuationMapSubscription = null;
        clientState.Logout -= OnLogout;

        uiBuilder.Draw -= Update;
        uiBuilder.OpenConfigUi -= OpenUi;
        uiBuilder.OpenMainUi -= OpenUi;

        condition.ConditionChange -= OnConditionChanged;
        StopOwnedDeparturePath("service stopping");
        ReleaseCompletionSuppression();
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
            IsLoadingOrTransitioning(),
            Utils.IsScreenReady());
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

            _autoDDConfig = ResolveAutoDDConfig(configRoot);
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
                ReleaseCompletionSuppression();
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
        _autoDDConfig = ResolveAutoDDConfig(configRoot);
        if (_autoDDConfig?.AutoLeaveAfterBossDefeat != true)
        {
            StopOwnedDeparturePath("automatic departure disabled");
            if (_completionAutorotationSuppressed)
                ReleaseAutomaticDungeonPreset(restorePrevious: true);
            ResetDeepDungeonBossCompletion();
            return;
        }
        if (_continuation != null)
        {
            ObserveContinuationArrival();
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
        if (_autoDungeonPreset != null && rotation.PresetSelectionVersion == _presetVersionAfterAutoActivation
            && rotation.Presets.Contains(_autoDungeonPreset))
        {
            _completionSuppressionToken = rotation.BeginDeepDungeonCompletionSuppression();
            _presetVersionAfterAutoActivation = rotation.PresetSelectionVersion;
            _completionAutorotationSuppressed = true;
        }
        logger.Info($"[AutoDD] Supported boss defeat observed: dungeon={dd.DungeonId}, floor={dd.Floor}, module={_trackedDeepDungeonBossType?.Name}, actor={actor.InstanceID}; completionSuppressionOwned={rotation.OwnsDeepDungeonCompletionSuppression(_completionSuppressionToken)}, token={_completionSuppressionToken}, presetVersion={rotation.PresetSelectionVersion}; awaiting leave readiness.");
        BeginContinuationIfRequested();
    }

    private void ResetDeepDungeonBossCompletion()
    {
        ReleaseCompletionSuppression();
        _continuation = null;
        worldstateGlobal.DeepDungeon.ContinuationStatus = null;
        worldstateGlobal.DeepDungeon.ContinuationRetryRequested = false;
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
        _departureManualInputResumeAt = default;
        _lastManualInputDiagnosticAt = default;
        if (!_departurePathStopWhenReady)
        {
            _departureExitActorID = 0;
            _departureExitRawAddress = IntPtr.Zero;
            _departureExitRawEntityID = 0;
            _departureExitPosition = default;
            _departureExitHitbox = 0;
        }
        _exitDiscoveryStartedAt = default;
        _nextExitDiscoveryPollAt = default;
        _lastExitDiscoveryStatusAt = default;
        _exitDiscoveryPollCount = 0;
        _exitDiscoveryTimedOut = false;
        _exitConfirmationArmedAt = 0;
        _exitConfirmationSubmittedAt = 0;
        _exitConfirmationVisible = false;
        _lastDepartureIntent = null;
    }

    private void UpdateDeepDungeonDepartureHints()
    {
        if (!_deepDungeonBossDefeated || _departureStage == DeepDungeonDepartureStage.None)
            return;

        _autoDDConfig = ResolveAutoDDConfig(configRoot);
        if (_autoDDConfig?.AutoLeaveAfterBossDefeat != true)
        {
            StopOwnedDeparturePath("automatic boss leave disabled");
            CancelDeepDungeonDeparture("automatic boss leave disabled");
            return;
        }

        if (_autoDungeonPreset != null && rotation.PresetSelectionVersion != _presetVersionAfterAutoActivation)
        {
            StopOwnedDeparturePath("preset selection changed");
            ReleaseCompletionSuppression();
            _autoDungeonPreset = null;
            _presetsBeforeAutoDungeon = null;
            _completionAutorotationSuppressed = false;
            CancelDeepDungeonDeparture("preset selection changed");
            return;
        }

        if (ContinuationBlocksDeparture())
            return;

        var currentTerritory = worldstateGlobal.CurrentZone;
        var manualInput = movementOverride.IsManualMoveRequested();
        var now = worldstateGlobal.CurrentTime;
        if (manualInput)
            _departureManualInputResumeAt = now + TimeSpan.FromMilliseconds(750);
        if (manualInput && ContinuationHasPendingUi())
        {
            FailContinuation("manual movement interrupted pending entry UI");
            return;
        }
        if (_departurePathOwned && manualInput)
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
                if (_continuation != null)
                    FailContinuation("boss-floor leave timed out after two minutes");
                return;
            }
            if (_departureStage is DeepDungeonDepartureStage.BossDefeated or DeepDungeonDepartureStage.LeaveRequested
                && rotation.Player is { IsDead: false, InCombat: false, CastInfo: null }
                && !IsLoadingOrTransitioning() && Utils.IsScreenReady())
            {
                if (_departureStage == DeepDungeonDepartureStage.BossDefeated)
                {
                    logger.Info($"[AutoDD] boss-floor leave request ready: dungeon={_trackedBossDungeon}, floor={_trackedBossFloor}, territory={_bossSourceTerritory}, target={_autoDDConfig.TargetBossFloor}.");
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
            if (_departureStage is not (DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit or DeepDungeonDepartureStage.AwaitingExitTransition))
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
        else if (_departureStage is DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit or DeepDungeonDepartureStage.AwaitingExitTransition)
        {
            if (_continuation != null)
            {
                ObserveContinuationTerritoryChange();
                return;
            }
            StopOwnedDeparturePath("waiting-room territory changed");
            CompleteDeepDungeonDeparture($"left waiting room; current territory={currentTerritory}");
            return;
        }

        if (_departureStage is not (DeepDungeonDepartureStage.WaitingRoom or DeepDungeonDepartureStage.MovingToExit or DeepDungeonDepartureStage.InteractingWithExit or DeepDungeonDepartureStage.AwaitingExitTransition))
            return;

        if (currentTerritory != _expectedWaitingTerritory
            || rotation.Player is not { IsDead: false, InCombat: false, CastInfo: null } player)
        {
            StopOwnedDeparturePath("invalid player or waiting-room context");
            return;
        }
        if (DeepDungeonPolicy.ShouldPauseDepartureForManualInput(manualInput, now, _departureManualInputResumeAt))
        {
            if (_lastManualInputDiagnosticAt == default || now - _lastManualInputDiagnosticAt >= TimeSpan.FromSeconds(1))
            {
                _lastManualInputDiagnosticAt = now;
                LogDepartureDiagnostic($"manual movement input active; waiting-room Exit movement paused until {_departureManualInputResumeAt:HH:mm:ss.fff}");
            }
            return;
        }

        if (!DeepDungeonPolicy.CanSearchWaitingRoomExit(Utils.IsScreenReady(), IsLoadingOrTransitioning()))
        {
            StopOwnedDeparturePath("waiting for screen readiness before Exit discovery");
            LogDepartureDiagnostic(_continuation != null ? "waiting for screen readiness for waiting-room continuation"
                : _departureStage == DeepDungeonDepartureStage.AwaitingExitTransition ? "Exit Yes submitted; waiting for screen/territory transition"
                : "waiting for screen ready before scanning waiting-room Exit");
            return;
        }

        if (_continuation == null && !EnsureFinalExitAllowed())
            return;

        if (_continuation != null && UpdateWaitingRoomContinuation(player))
            return;

        if (DeepDungeonMenuVisible())
        {
            StopOwnedDeparturePath("unowned DeepDungeonMenu visible");
            LogDepartureDiagnostic("unowned DeepDungeonMenu left untouched; Exit approach paused");
            return;
        }

        if (UpdateDepartureExitConfirmation())
            return;

        if (_exitDiscoveryStartedAt == default)
        {
            _exitDiscoveryStartedAt = now;
            _nextExitDiscoveryPollAt = now;
            _lastExitDiscoveryStatusAt = default;
            _exitDiscoveryPollCount = 0;
            _exitDiscoveryTimedOut = false;
            LogDepartureTransition($"waiting-room Exit discovery polling started: 30s window, territory={currentTerritory}, target={_autoDDConfig?.TargetBossFloor}, intent=Exit");
        }

        var expectedExitOID = DeepDungeonPolicy.WaitingRoomExitBaseID(_trackedBossDungeon, currentTerritory);
        Dalamud.Game.ClientState.Objects.Types.IGameObject? rawExit = _departureExitRawAddress != IntPtr.Zero
            ? Service.ObjectTable.FirstOrDefault(obj => obj.Address == _departureExitRawAddress && obj.BaseId == expectedExitOID && obj.IsTargetable)
            : null;
        if (_departureExitRawAddress != IntPtr.Zero && rawExit == null)
        {
            _departureExitRawAddress = IntPtr.Zero;
            _departureExitRawEntityID = 0;
            _departureExitActorID = 0;
        }

        Actor? exit = rawExit == null && _departureExitActorID != 0 ? worldstateGlobal.Actors.Find(_departureExitActorID) : null;
        if (exit is not { Type: ActorType.EventObj, IsTargetable: true, IsDestroyed: false } || exit.OID != expectedExitOID)
        {
            exit = null;
            if (rawExit == null)
                _departureExitActorID = 0;
        }

        var discoveryDeadline = _exitDiscoveryStartedAt + DeepDungeonPolicy.WaitingRoomExitDiscoveryTimeout;
        if (exit == null && rawExit == null && _exitDiscoveryTimedOut)
            return;
        if (exit == null && rawExit == null && now >= discoveryDeadline)
        {
            _exitDiscoveryTimedOut = true;
            StopOwnedDeparturePath("waiting-room Exit discovery timed out");
            logger.Warning($"[AutoDD] waiting-room Exit discovery timed out: dungeon={_trackedBossDungeon}, territory={currentTerritory}, polls={_exitDiscoveryPollCount}, elapsed={(now - _exitDiscoveryStartedAt).TotalSeconds:F1}s; automation remains in waiting room.");
            return;
        }

        if (exit == null && rawExit == null)
        {
            if (!DeepDungeonPolicy.ShouldPollWaitingRoomExit(Utils.IsScreenReady(), IsLoadingOrTransitioning(), now, discoveryDeadline, _nextExitDiscoveryPollAt))
                return;
            _nextExitDiscoveryPollAt = now + DeepDungeonPolicy.WaitingRoomExitPollInterval;
            ++_exitDiscoveryPollCount;

            var rawExits = Service.ObjectTable.Where(obj => obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && obj.BaseId == expectedExitOID).ToArray();
            var rawTargetable = rawExits.Count(obj => obj.IsTargetable);
            var targetableCandidates = new List<Dalamud.Game.ClientState.Objects.Types.IGameObject>();
            var seenExitIdentities = new HashSet<ulong>();
            foreach (var candidate in rawExits.Where(obj => obj.IsTargetable))
            {
                var identity = DepartureExitObjectIdentity(candidate);
                if (seenExitIdentities.Add(identity))
                    targetableCandidates.Add(candidate);
            }
            var synchronizedExits = targetableCandidates.Select(obj => worldstateGlobal.Actors.Find(obj.EntityId))
                .Where(actor => actor is { Type: ActorType.EventObj, IsTargetable: true, IsDestroyed: false } && actor.OID == expectedExitOID)
                .Cast<Actor>().ToArray();

            var currentTarget = Service.TargetManager.Target;
            var selectedByClient = currentTarget != null ? targetableCandidates.FirstOrDefault(candidate => candidate.Address == currentTarget.Address
                || candidate.EntityId == currentTarget.EntityId && IsUsableDepartureEntityID(candidate.EntityId)) : null;
            Dalamud.Game.ClientState.Objects.Types.IGameObject? selectedRawExit = selectedByClient;
            if (selectedRawExit == null && targetableCandidates.Count == 1)
                selectedRawExit = targetableCandidates[0];
            if (selectedRawExit == null && targetableCandidates.Count > 1)
            {
                var nearest = targetableCandidates.Select(candidate => (Object: candidate, Distance: Vector3.Distance(player.Position.ToVec3(player.PosRot.Y), candidate.Position)))
                    .OrderBy(candidate => candidate.Distance).Take(2).ToArray();
                if (nearest.Length == 2 && DeepDungeonPolicy.IsUnambiguousNearestWaitingRoomExit(nearest[0].Distance, nearest[1].Distance))
                    selectedRawExit = nearest[0].Object;
            }

            if (_lastExitDiscoveryStatusAt == default || now - _lastExitDiscoveryStatusAt >= DeepDungeonPolicy.WaitingRoomExitStatusInterval)
            {
                _lastExitDiscoveryStatusAt = now;
                var details = string.Join("|", rawExits.Select(candidate => $"entity={candidate.EntityId:X},address=0x{candidate.Address.ToInt64():X},name='{candidate.Name.TextValue}',targetable={candidate.IsTargetable},pos={candidate.Position},distance={Vector3.Distance(player.Position.ToVec3(player.PosRot.Y), candidate.Position):F2}"));
                LogDepartureDiagnostic($"Exit discovery poll: elapsed={(now - _exitDiscoveryStartedAt).TotalSeconds:F0}s/{DeepDungeonPolicy.WaitingRoomExitDiscoveryTimeout.TotalSeconds:F0}s, poll={_exitDiscoveryPollCount}, objectTableMatches={rawExits.Length}, deduplicatedTargetable={targetableCandidates.Count}, synchronizedTargetable={synchronizedExits.Length}, clientTarget={(selectedByClient != null ? DepartureExitObjectIdentity(selectedByClient).ToString("X") : "none")}, selected={(selectedRawExit != null ? DepartureExitObjectIdentity(selectedRawExit).ToString("X") : "ambiguous")}, baseID={expectedExitOID}, candidates=[{details}]");
            }

            if (selectedRawExit == null || !DeepDungeonPolicy.IsWaitingRoomExitObjectValid(_trackedBossDungeon, currentTerritory, selectedRawExit.BaseId, selectedRawExit.IsTargetable))
            {
                StopOwnedDeparturePath("exit missing, ambiguous, or not synchronized/targetable");
                return;
            }

            var synchronizedExit = worldstateGlobal.Actors.Find(selectedRawExit.EntityId);
            if (synchronizedExit is { Type: ActorType.EventObj, IsTargetable: true, IsDestroyed: false } && synchronizedExit.OID == expectedExitOID)
            {
                exit = synchronizedExit;
                rawExit = null;
            }
            else
            {
                rawExit = selectedRawExit;
                _departureExitRawAddress = rawExit.Address;
                _departureExitRawEntityID = rawExit.EntityId;
                logger.Debug($"[AutoDD] selected object-table Exit without synchronized VBM actor: entity={rawExit.EntityId:X},address=0x{rawExit.Address.ToInt64():X},name='{rawExit.Name.TextValue}',position={rawExit.Position},targetable={rawExit.IsTargetable}.");
            }
        }

        var exitID = exit?.InstanceID ?? DepartureExitObjectIdentity(rawExit!);
        var exitOID = exit?.OID ?? rawExit!.BaseId;
        var destination = exit != null
            ? new Vector3(exit.PosRot.X, exit.PosRot.Y, exit.PosRot.Z)
            : new Vector3(rawExit!.Position.X, rawExit.Position.Y, rawExit.Position.Z);
        var exitHitbox = exit?.HitboxRadius ?? rawExit!.HitboxRadius;
        var range = exitHitbox + 2.0f;
        if (exitID != _departureExitActorID || rawExit?.Address != _departureExitRawAddress || Vector3.Distance(destination, _departureExitPosition) > 0.25f || Math.Abs(exitHitbox - _departureExitHitbox) > 0.05f)
        {
            if (_departurePathOwned)
            {
                StopOwnedDeparturePath("exit actor or live destination changed");
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
            }
            _departureExitActorID = exitID;
            _departureExitRawAddress = rawExit?.Address ?? IntPtr.Zero;
            _departureExitRawEntityID = rawExit?.EntityId ?? 0;
            _departureExitPosition = destination;
            _departureExitHitbox = exitHitbox;
            LogDepartureDiagnostic($"exit acquired: dungeon={_trackedBossDungeon}, territory={currentTerritory}, identity={exitID:X}, entity={rawExit?.EntityId.ToString("X") ?? "synchronized"}, baseID={exitOID}, position={destination}, hitbox={exitHitbox:F2}, stopRange={range:F2}");
        }

        var centerDistance = Vector3.Distance(new Vector3(player.PosRot.X, player.PosRot.Y, player.PosRot.Z), destination);
        if (centerDistance <= range)
        {
            StopOwnedDeparturePath("within exit hitbox plus 2y");
            if (exit != null)
                hintsGlobal.InteractWithTarget = exit;
            else
                hintsGlobal.InteractWithRawTarget = rawExit;
            hintsGlobal.OnInteractionIssued = ArmDepartureExitConfirmation;
            hintsGlobal.CanIssueInteraction = CanDispatchDepartureExitInteraction;
            if (_departureStage != DeepDungeonDepartureStage.InteractingWithExit)
            {
                _departureStage = DeepDungeonDepartureStage.InteractingWithExit;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                LogDepartureTransition($"within interaction movement range: centerDistance={centerDistance:F2}, stopRange={range:F2}, exit={exitID:X}");
            }
            return;
        }

        UpdateDepartureVnav(player, exitID, exitOID, destination, range, centerDistance);
    }

    private void ArmDepartureExitConfirmation(ulong exitID)
    {
        if (_exitConfirmationArmedAt != 0 || _departureStage != DeepDungeonDepartureStage.InteractingWithExit
            || exitID != _departureExitActorID || worldstateGlobal.CurrentZone != _expectedWaitingTerritory)
            return;
        _exitConfirmationArmedAt = Environment.TickCount64;
        LogDepartureTransition($"Exit interaction dispatched: exit={exitID:X}, target={_autoDDConfig?.TargetBossFloor}; awaiting localized SelectYesno confirmation");
    }

    private bool CanDispatchDepartureExitInteraction(ulong exitID)
        => exitID == _departureExitActorID && _departureStage == DeepDungeonDepartureStage.InteractingWithExit
            && worldstateGlobal.CurrentZone == _expectedWaitingTerritory
            && !IsLoadingOrTransitioning() && Utils.IsScreenReady() && !movementOverride.IsManualMoveRequested()
            && rotation.Player is { IsDead: false, InCombat: false, CastInfo: null }
            && _exitConfirmationArmedAt == 0 && _exitConfirmationSubmittedAt == 0
            && EnsureFinalExitAllowed();

    // Runs before rediscovering the Exit: opening its dialog may change the object's targetability.
    private unsafe bool UpdateDepartureExitConfirmation()
    {
        var now = Environment.TickCount64;
        if (_exitConfirmationSubmittedAt != 0)
        {
            if (now - _exitConfirmationSubmittedAt >= DeepDungeonExitConfirmation.TransitionTimeoutMs)
                CancelDeepDungeonDeparture("Exit Yes callback issued but no territory transition observed within 30s");
            return true;
        }
        if (!EnsureFinalExitAllowed())
            return true;
        if (_exitConfirmationArmedAt != 0 && now - _exitConfirmationArmedAt >= DeepDungeonExitConfirmation.PromptTimeoutMs)
        {
            CancelDeepDungeonDeparture("Exit confirmation missing or unrecognized after 10s; no automatic retry");
            return true;
        }

        try
        {
            var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("SelectYesno").Address;
            var visible = addon != null && addon->IsVisible;
            if (!visible)
            {
                if (_exitConfirmationArmedAt == 0)
                    return false;
                if (_exitConfirmationVisible)
                    CancelDeepDungeonDeparture("Exit confirmation dismissed without an automatic Yes callback; respecting cancellation");
                return true;
            }

            StopOwnedDeparturePath("confirmation dialog visible");
            if (_exitConfirmationArmedAt == 0)
            {
                LogDepartureDiagnostic("unowned SelectYesno visible; not interacting or confirming");
                return true;
            }
            _exitConfirmationVisible = true;
            if (!addon->IsReady || addon->AtkValues == null || addon->AtkValuesCount < 3)
                return true;
            var prompt = addon->AtkValues[0];
            if (prompt.Type is not (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String
                or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString
                or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString) || (byte*)prompt.String == null)
                return true;

            var row = DeepDungeonExitConfirmation.WarpQuestionRow(_trackedBossDungeon, worldstateGlobal.CurrentZone);
            var expected = row != 0 ? Service.LuminaRow<Lumina.Excel.Sheets.Warp>(row)?.Question.ToString() : null;
            var actual = Dalamud.Game.Text.SeStringHandling.SeString.Parse(prompt.String).TextValue;
            var player = rotation.Player;
            var owned = _departureStage == DeepDungeonDepartureStage.InteractingWithExit && _departureExitActorID != 0
                && player != null && Vector3.Distance(player.PosRot.XYZ(), _departureExitPosition) <= _departureExitHitbox + 2.5f;
            if (!DeepDungeonExitConfirmation.CanConfirm(_trackedBossDungeon, worldstateGlobal.CurrentZone, owned, true,
                _exitConfirmationArmedAt, now, false, expected, actual))
            {
                LogDepartureDiagnostic($"SelectYesno left untouched: owned={owned}, WarpQuestion={row}, localized prompt matches={string.Equals(expected?.Trim(), actual.Trim(), StringComparison.Ordinal)}");
                return true;
            }

            // Re-read current config at the irreversible boundary, not just during hint production.
            if (!EnsureFinalExitAllowed())
                return true;

            // SelectYesno's first (Yes) choice is callback argument 0. Submit only once.
            var value = new FFXIVClientStructs.FFXIV.Component.GUI.AtkValue
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int,
                Int = 0,
            };
            _exitConfirmationSubmittedAt = now;
            _departureStage = DeepDungeonDepartureStage.AwaitingExitTransition;
            addon->FireCallback(1, &value, true);
            LogDepartureTransition($"localized Exit confirmation Yes callback issued: WarpQuestion={row}, target={_autoDDConfig?.TargetBossFloor}, intent=Exit; awaiting territory transition, departure not yet confirmed");
            return true;
        }
        catch (Exception ex)
        {
            CancelDeepDungeonDeparture($"Exit confirmation unavailable/error: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static bool IsUsableDepartureEntityID(ulong entityID)
        => entityID != 0 && entityID != 0xE0000000 && (entityID & 0xFF000000) != 0xFF000000;

    private static ulong DepartureExitObjectIdentity(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
        => IsUsableDepartureEntityID(obj.EntityId) ? obj.EntityId : unchecked((ulong)obj.Address.ToInt64());

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

    private void UpdateDepartureVnav(Actor player, ulong exitID, uint exitOID, Vector3 destination, float range, float distance, string targetKind = "Exit")
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
                        LogDepartureDiagnostic($"ownership lost: live path endpoint does not match {targetKind} {exitID:X}; path left untouched");
                        return;
                    }
                    LogDepartureDiagnostic($"owned path active: target={targetKind}/{exitID:X}, stopRange={range:F2}, waypoints={waypoints.Count}");
                    return;
                }

                if (pathfindInProgress)
                {
                    LogDepartureDiagnostic($"owned pathfinding pending: target={targetKind}/{exitID:X}, stopRange={range:F2}");
                    return;
                }

                _departurePathOwned = false;
                _departurePathStopWhenReady = false;
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
                LogDepartureDiagnostic($"owned path ended before arrival: target={targetKind}/{exitID:X}, playerDistance={distance:F2}; retry eligible");
            }

            // An existing vnav route or pathfind belongs to another controller. Never replace it.
            if (!DeepDungeonPolicy.ShouldStartDeparturePath(ready, pathRunning, pathfindInProgress, true, rotation.Player is { IsDead: false, InCombat: false, CastInfo: null }))
            {
                LogDepartureDiagnostic($"vnav busy with unowned path; not taking control: target={targetKind}/{exitID:X}");
                return;
            }

            var started = _vnavMoveCloseTo.InvokeFunc(destination, false, range);
            if (started)
            {
                _departurePathOwned = true;
                _departureStage = DeepDungeonDepartureStage.MovingToExit;
                _departureStageChangedAt = worldstateGlobal.CurrentTime;
                LogDepartureTransition($"vnav approach requested: target={targetKind}/{exitID:X}, baseID={exitOID}, position={destination}, stopRange={range:F2}, playerDistance={distance:F2}");
            }
            else
            {
                LogDepartureDiagnostic($"vnav rejected approach request: target={targetKind}/{exitID:X}, position={destination}, stopRange={range:F2}, playerDistance={distance:F2}");
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
        {
            ReleaseCompletionSuppression();
            return;
        }

        var canRestore = restorePrevious
            && rotation.PresetSelectionVersion == _presetVersionAfterAutoActivation
            && (rotation.Presets.Contains(_autoDungeonPreset)
                || _completionAutorotationSuppressed && rotation.OwnsDeepDungeonCompletionSuppression(_completionSuppressionToken));
        ReleaseCompletionSuppression();
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

    private void ReleaseCompletionSuppression()
    {
        rotation.ReleaseDeepDungeonCompletionSuppression(_completionSuppressionToken);
        _completionSuppressionToken = 0;
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

        _continuationMapSubscription?.Dispose();
        ReleaseCompletionSuppression();
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
