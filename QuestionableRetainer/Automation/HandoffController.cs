using System.Numerics;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using QuestionableRetainer.Integrations;

namespace QuestionableRetainer.Automation;

public sealed class HandoffController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuestionableStopRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MonitoringHeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OceanDutyStartTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OceanVoyageTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan OceanCleanupPollInterval = TimeSpan.FromSeconds(1);
    private const float OceanPositionTolerance = 0.1f;
    private const uint OceanFishingTerritoryOne = 900;
    private const uint OceanFishingTerritoryTwo = 1163;
    private readonly Configuration configuration;
    private readonly AutoRetainerClient autoRetainer;
    private readonly WahToolsFeeshClient wahToolsFeesh;
    private readonly WahToolsFeeshStatusReader wahToolsStatusReader;
    private readonly YesAlreadyLease yesAlreadyLease;
    private readonly QuestionableClient questionable;
    private readonly LifestreamClient lifestream;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDutyState dutyState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly INotificationManager notificationManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Action save;
    private readonly MonitoringStatus monitoring = new();
    private readonly VerboseDiagnosticLog diagnosticLog;
    private DateTime nextUpdateUtc;
    private DateTime phaseStartedUtc;
    private DateTime lastActionUtc;
    private DateTime lastDependencyRefreshUtc;
    private DateTime nextQuestionableStopRetryUtc;
    private DateTime? oceanCleanupStableSinceUtc;
    private Vector3? oceanCleanupStablePosition;
    private uint oceanCleanupStableTerritory;
    private ulong oceanCleanupStableContentId;
    private long? oceanCleanupConfirmationGeneration;
    private DateTime nextOceanCleanupPollUtc;
    private MonitoringState lastLoggedMonitoringState;
    private string lastLoggedMonitoringReason = string.Empty;
    private DateTime lastMonitoringLogUtc;
    private DateTime nextFishingCharacterListRefreshUtc;
    private DateTime nextWahToolsStatusRefreshUtc;
    private IReadOnlyList<OfflineCharacterData> fishingCharacterCache = [];
    private bool fishingCharacterListAvailable;
    private bool completionEventReceived;
    private bool autoRetainerMultiModeObserved;
    private bool recoveryPending;
    private bool questionablePausedThisRuntime;
    private bool manualResumeAuthorized;
    private bool offFrameworkUpdateLogged;
    private bool wahToolsSessionObservedThisRuntime;
    private readonly RecoveryDependencyWaitClock dependencyWaitClock = new();
    private bool evaluatingStartupRecovery;
    private string? activeDependencyGateFingerprint;

    public HandoffController(
        Configuration configuration,
        AutoRetainerClient autoRetainer,
        WahToolsFeeshClient wahToolsFeesh,
        WahToolsFeeshStatusReader wahToolsStatusReader,
        YesAlreadyLease yesAlreadyLease,
        QuestionableClient questionable,
        LifestreamClient lifestream,
        IClientState clientState,
        IPlayerState playerState,
        IDutyState dutyState,
        IObjectTable objectTable,
        IGameGui gameGui,
        ICondition condition,
        IChatGui chatGui,
        INotificationManager notificationManager,
        IFramework framework,
        IPluginLog log,
        Action save)
    {
        this.configuration = configuration;
        this.autoRetainer = autoRetainer;
        this.wahToolsFeesh = wahToolsFeesh;
        this.wahToolsStatusReader = wahToolsStatusReader;
        this.yesAlreadyLease = yesAlreadyLease;
        this.questionable = questionable;
        this.lifestream = lifestream;
        this.clientState = clientState;
        this.playerState = playerState;
        this.dutyState = dutyState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.condition = condition;
        this.chatGui = chatGui;
        this.notificationManager = notificationManager;
        this.framework = framework;
        this.log = log;
        diagnosticLog = new VerboseDiagnosticLog(log);
        this.save = save;
        autoRetainer.SingleMultiModeFinished += OnSingleMultiModeFinished;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;

        Phase = configuration.ActiveSession?.Phase ?? HandoffPhase.Idle;
        phaseStartedUtc = DateTime.UtcNow;
        recoveryPending = configuration.ActiveSession is not null;
        if (configuration.ActiveSession is { } persistedSession)
        {
            NormalizeSession(persistedSession);
            Phase = persistedSession.Phase;
        }
        if (configuration.ActiveSession is { } existingSession && string.IsNullOrWhiteSpace(existingSession.CorrelationId))
        {
            existingSession.CorrelationId = CreateCorrelationId();
            save();
        }

        if (recoveryPending)
        {
            monitoring.State = MonitoringState.Recovering;
            monitoring.Reason = "Interrupted handoff requires recovery.";
        }
    }

    public HandoffPhase Phase { get; private set; }

    public int ReadyCharacterCount { get; private set; }

    public string LastError => configuration.ActiveSession?.LastError ?? configuration.ManualRecoveryMessage;

    public HandoffSession? Session => configuration.ActiveSession;

    public MonitoringStatus Monitoring => monitoring;

    public bool TryGetFishingCharacters(out IReadOnlyList<OfflineCharacterData> characters)
    {
        var now = DateTime.UtcNow;
        if (now >= nextFishingCharacterListRefreshUtc)
        {
            fishingCharacterListAvailable = autoRetainer.TryGetRegisteredCharacters(out fishingCharacterCache);
            nextFishingCharacterListRefreshUtc = now + TimeSpan.FromSeconds(15);
        }

        characters = fishingCharacterCache;
        return fishingCharacterListAvailable;
    }

    public void Dispose()
    {
        autoRetainer.SingleMultiModeFinished -= OnSingleMultiModeFinished;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyCompleted -= OnDutyCompleted;
        yesAlreadyLease.Release();
    }

    public void Update()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            if (!offFrameworkUpdateLogged)
            {
                offFrameworkUpdateLogged = true;
                log.Warning("Handoff update was invoked outside the Dalamud framework thread; update skipped.");
            }

            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
            return;

        nextUpdateUtc = now + TimeSpan.FromMilliseconds(250);

        if (Phase != HandoffPhase.Idle && now >= lastDependencyRefreshUtc + TimeSpan.FromSeconds(1))
            RefreshLiveDiagnostics(now);

        if (recoveryPending)
        {
            evaluatingStartupRecovery = true;
            try
            {
                TryInitializeRecovery();
            }
            finally
            {
                evaluatingStartupRecovery = false;
            }
            if (!recoveryPending)
            {
                // A recovered phase gets a fresh runtime budget, even when its name is unchanged.
                phaseStartedUtc = DateTime.UtcNow;
                dependencyWaitClock.Reset();
            }
            return;
        }

        RefreshWahToolsStatus(now);

        if (!CanAdvanceWithDependencies(now))
            return;

        switch (Phase)
        {
            case HandoffPhase.Idle:
                UpdateIdle(now);
                break;
            case HandoffPhase.WaitingForQuestionable:
                UpdateWaitingForQuestionable(now);
                break;
            case HandoffPhase.PreparingFishingCharacter:
                PrepareFishingCharacter(now);
                break;
            case HandoffPhase.WaitingForFishingCharacter:
                UpdateWaitingForFishingCharacter(now);
                break;
            case HandoffPhase.StartingWahToolsFishing:
                StartWahToolsFishing(now);
                break;
            case HandoffPhase.WaitingForOceanDuty:
                UpdateWaitingForOceanDuty(now);
                break;
            case HandoffPhase.WaitingForOceanCleanup:
                UpdateWaitingForOceanCleanup(now);
                break;
            case HandoffPhase.WaitingForWahToolsStop:
                UpdateWaitingForWahToolsStop(now);
                break;
            case HandoffPhase.CheckingChainedWork:
                UpdateCheckingChainedWork(now);
                break;
            case HandoffPhase.RecoveringHandoff:
                UpdateRecoveringHandoff(now);
                break;
            case HandoffPhase.WaitingForAutoRetainerIdle:
                UpdateWaitingForAutoRetainerIdle(now);
                break;
            case HandoffPhase.WaitingForAutoRetainer:
                UpdateWaitingForAutoRetainer(now);
                break;
            case HandoffPhase.ReturningOriginal:
                UpdateReturningOriginal(now);
                break;
            case HandoffPhase.WaitingForOriginal:
                UpdateWaitingForOriginal(now);
                break;
            case HandoffPhase.ResumingQuestionable:
                UpdateResumingQuestionable(now);
                break;
            case HandoffPhase.WaitingForQuestionableResume:
                UpdateWaitingForQuestionableResume(now);
                break;
            case HandoffPhase.AwaitingManualQuestionableResume:
                SetMonitoring(MonitoringState.Recovering, monitoring.Reason, now);
                break;
            case HandoffPhase.Error:
                SetMonitoring(MonitoringState.Error, monitoring.Reason, now);
                break;
            case HandoffPhase.ManualIntervention:
                SetMonitoring(MonitoringState.Error, monitoring.Reason, now);
                break;
        }
    }

    public void Recover() => DispatchToFrameworkThread(RecoverCore, nameof(Recover));

    public void ConfirmFeeshStopped() => DispatchToFrameworkThread(ConfirmFeeshStoppedCore, nameof(ConfirmFeeshStopped));

    public void ResetPluginState() => DispatchToFrameworkThread(ResetPluginStateCore, nameof(ResetPluginState));

    private void ResetPluginStateCore()
    {
        var session = configuration.ActiveSession;
        log.Warning(
            "Questionable Retainer plugin state manually reset. Session={Session} Phase={Phase} Original={Original}@{World} Fishing={Fishing}@{FishingWorld} Reason={Reason}.",
            session?.CorrelationId ?? "none",
            Phase,
            session?.OriginalName ?? "none",
            session?.OriginalWorld ?? "none",
            session?.FishingName ?? "none",
            session?.FishingWorld ?? "none",
            !string.IsNullOrWhiteSpace(session?.RecoveryMessage)
                ? session.RecoveryMessage
                : !string.IsNullOrWhiteSpace(session?.LastError)
                    ? session.LastError
                    : monitoring.Reason);

        configuration.Enabled = false;
        if (session?.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        configuration.ActiveSession = null;
        configuration.ManualRecoveryMessage = string.Empty;
        diagnosticLog.Reset();
        yesAlreadyLease.Release();

        recoveryPending = false;
        questionablePausedThisRuntime = false;
        manualResumeAuthorized = false;
        completionEventReceived = false;
        autoRetainerMultiModeObserved = false;
        wahToolsSessionObservedThisRuntime = false;
        wahToolsStatusReader.Invalidate();
        nextWahToolsStatusRefreshUtc = DateTime.MinValue;
        lastActionUtc = DateTime.MinValue;
        fishingCharacterCache = [];
        fishingCharacterListAvailable = false;
        nextFishingCharacterListRefreshUtc = DateTime.MinValue;
        nextQuestionableStopRetryUtc = DateTime.MinValue;
        lastDependencyRefreshUtc = DateTime.MinValue;
        nextUpdateUtc = DateTime.MinValue;
        ResetOceanCleanupStability();
        ResetReadinessDiagnostics();

        monitoring.QuestionableAvailable = null;
        monitoring.QuestionableRunning = null;
        monitoring.QuestionableQuestId = string.Empty;
        monitoring.AutoRetainerAvailable = null;
        monitoring.AutoRetainerBusy = false;
        monitoring.AutoRetainerMultiModeActive = false;
        monitoring.ReadinessScanAvailable = null;
        monitoring.LifestreamAvailable = null;
        monitoring.LifestreamBusy = false;
        monitoring.WahToolsAvailable = null;
        monitoring.WahToolsLaunchAccepted = false;
        monitoring.WahToolsStatus = null;
        monitoring.WahToolsStatusEvaluationUtc = null;
        monitoring.WahToolsStatusAgeSeconds = null;
        monitoring.YesAlreadyLeaseAcquired = false;
        monitoring.NextOceanWindowUtc = null;
        monitoring.OceanLaunchEligible = false;
        monitoring.OceanWindowHandled = session?.OceanWindowStartUtc is not null;
        monitoring.OceanDutyStarted = false;
        monitoring.OceanDutyCompleted = false;
        monitoring.OceanCleanupWaitReason = string.Empty;
        monitoring.OceanCleanupStableElapsedSeconds = null;
        monitoring.OceanCleanupRequiredSeconds = null;
        monitoring.OceanCleanupConfirmationSource = OceanCleanupConfirmationSource.None;
        monitoring.CurrentContentId = null;
        monitoring.FishingCharacterContentId = null;
        monitoring.LastDependencyCheckUtc = null;
        monitoring.PlayerLoggedIn = false;
        monitoring.PlayerContentIdAvailable = false;
        monitoring.PlayerBlockers = [];

        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.Disabled, "Plugin state reset; monitoring disabled.", DateTime.UtcNow);
        lastLoggedMonitoringState = MonitoringState.Disabled;
        lastLoggedMonitoringReason = "Plugin state reset; monitoring disabled.";
        lastMonitoringLogUtc = DateTime.UtcNow;
        save();
        Notify("Plugin state reset. Monitoring is disabled; other plugin automation was left untouched.", NotificationType.Warning);
    }

    private void ConfirmFeeshStoppedCore()
    {
        if (Phase != HandoffPhase.ManualIntervention ||
            configuration.ActiveSession is not { OceanWindowStartUtc: not null } session)
            return;

        if (IsOceanFishingDutyActive() || IsOceanFishingTerritory() ||
            !TryGetAutoRetainerState(out var autoBusy, out var multiModeActive) || autoBusy || multiModeActive ||
            !TryGetLifestreamBusy(out var lifestreamBusy) || lifestreamBusy ||
            (monitoring.WahToolsStatus is { IsAvailable: true } feeshStatus &&
             WahToolsFeeshStatusPolicy.GetCleanupBlockers(feeshStatus).Count > 0) ||
            (clientState.IsLoggedIn && !IsSafeForHandoff()))
        {
            Notify("Cannot continue while Ocean Fishing, AutoRetainer, Lifestream, or player activity remains active.", NotificationType.Warning);
            return;
        }

        session.WahToolsLaunchAccepted = false;
        session.WahToolsLaunchAmbiguous = false;
        session.WahToolsStopRequested = true;
        session.WahToolsStopRequestedUtc ??= DateTime.UtcNow;
        session.WahToolsStopAcceptedUtc = DateTime.UtcNow;
        session.OceanFishingLegSkipped = true;
        session.CurrentLeg = HandoffLeg.None;
        session.RecoveryAction = RecoveryAction.None;
        session.RecoveryMessage = string.Empty;
        session.LastError = string.Empty;
        session.RecoveryFailureCode = null;
        if (session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        save();
        SetPhase(HandoffPhase.ReturningOriginal, reason: "Feesh stop was manually confirmed; returning to original character.");
        Notify("Feesh stop confirmed. Returning to original character.");
    }

    private void RecoverCore()
    {
        if (configuration.ActiveSession is null)
        {
            log.Information("Recovery command ignored. No active handoff session exists.");
            return;
        }

        if (Phase == HandoffPhase.AwaitingManualQuestionableResume)
        {
            log.Information(
                "Recovery command ignored. Session={Session} Phase={Phase} Reason=Manual resume or discard is required.",
                SessionId(),
                Phase);
            Notify("Recovery is awaiting your decision. Use Resume Questionable or Discard Recovery.", NotificationType.Warning);
            return;
        }

        configuration.ActiveSession.LastError = string.Empty;
        configuration.ActiveSession.RecoveryNextAttemptUtc = DateTime.UtcNow;
        recoveryPending = false;
        if (Phase is HandoffPhase.Error or HandoffPhase.ManualIntervention)
        {
            var session = configuration.ActiveSession;
            if (session.RecoveryAction == RecoveryAction.ManualIntervention)
            {
                if (session.RecoveryOriginPhase is HandoffPhase.Idle or HandoffPhase.Error or
                    HandoffPhase.ManualIntervention or HandoffPhase.RecoveringHandoff)
                {
                    Notify("Recovery has no safe retry origin. Confirm Feesh stopped or reset QR state.", NotificationType.Warning);
                    return;
                }

                session.RecoveryAction = RecoveryAction.None;
                SetPhase(session.RecoveryOriginPhase, reason: "Manual retry requested; reconciling saved evidence without redispatch.");
                recoveryPending = true;
            }
            else
                SetPhase(HandoffPhase.RecoveringHandoff, reason: "Manual retry requested.");
        }
        nextUpdateUtc = DateTime.MinValue;
        save();
        log.Information("Recovery retry requested. Session={Session} Phase={Phase}.", SessionId(), Phase);
    }

    public void ResumeQuestionable() => DispatchToFrameworkThread(ResumeQuestionableCore, nameof(ResumeQuestionable));

    private void ResumeQuestionableCore()
    {
        if (configuration.ActiveSession is null)
        {
            log.Information("Resume command ignored. No active handoff session exists.");
            return;
        }

        manualResumeAuthorized = true;
        configuration.ActiveSession.LastError = string.Empty;
        recoveryPending = true;
        nextUpdateUtc = DateTime.MinValue;
        save();
        log.Information("Manual Questionable resume authorized. Session={Session} Phase={Phase}.", SessionId(), Phase);
    }

    public void CancelRecovery() => DispatchToFrameworkThread(CancelRecoveryCore, nameof(CancelRecovery));

    private void CancelRecoveryCore()
    {
        if (Phase is not (HandoffPhase.Error or HandoffPhase.ManualIntervention or HandoffPhase.AwaitingManualQuestionableResume))
        {
            log.Information("Discard command ignored. Session={Session} Phase={Phase}.", SessionId(), Phase);
            return;
        }

        if (configuration.ActiveSession is not { } session ||
            !TryGetAutoRetainerState(out var busy, out var multiModeActive) || busy || multiModeActive ||
            !TryGetLifestreamBusy(out var lifestreamBusy) || lifestreamBusy ||
            (IsOceanFishingDutyActive() || IsOceanFishingTerritory()) ||
            !clientState.IsLoggedIn || playerState.ContentId != session.OriginalContentId ||
            CapturePlayerBlockers().Count > 0)
        {
            log.Information(
                "Discard command rejected. Session={Session} Phase={Phase} Reason=Automation busy, wrong character, unsafe player state, or unavailable state.",
                SessionId(),
                Phase);
            Notify("Recovery can only be cancelled while automation is idle on the original character and the player is safe.", NotificationType.Error);
            return;
        }

        log.Information("Recovery discarded. Session={Session} Phase={Phase}.", SessionId(), Phase);
        if (IsOceanLeg(session))
            ReleaseYesAlreadyLease(session);
        if (IsOceanLeg(session) && session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        configuration.ActiveSession = null;
        configuration.ManualRecoveryMessage = string.Empty;
        ReadyCharacterCount = 0;
        SetPhase(HandoffPhase.Idle, persist: false);
        save();
    }

    private void UpdateIdle(DateTime now)
    {
        if (!configuration.Enabled)
        {
            SetMonitoring(MonitoringState.Disabled, "Monitoring is disabled.", now);
            return;
        }

        if (now < lastActionUtc + PollInterval)
            return;

        lastActionUtc = now;
        RefreshLiveDiagnostics(now);
        var playerBlockers = monitoring.PlayerBlockers;
        var questionableAvailable = monitoring.QuestionableAvailable == true;
        var questionableRunning = monitoring.QuestionableRunning == true;
        var questAvailable = monitoring.QuestionableAvailable != false;
        var autoRetainerAvailable = monitoring.AutoRetainerAvailable == true;
        var autoRetainerBusy = monitoring.AutoRetainerBusy;
        var multiModeActive = monitoring.AutoRetainerMultiModeActive;
        var lifestreamAvailable = monitoring.LifestreamAvailable == true;
        var lifestreamBusy = monitoring.LifestreamBusy;

        var readinessAvailable = false;
        if (autoRetainerAvailable)
        {
            readinessAvailable = autoRetainer.TryScanReadiness(
                configuration.MonitorRetainers,
                configuration.MonitorDeployables,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                out var readiness);
            if (readinessAvailable)
            {
                monitoring.RegisteredCharacterCount = readiness.RegisteredCharacterCount;
                monitoring.ReadyCharacterCount = readiness.ReadyCharacterCount;
                monitoring.ClosestRetainerSeconds = readiness.ClosestRetainerSeconds;
                monitoring.LastReadinessScanUtc = now;
                ReadyCharacterCount = readiness.ReadyCharacterCount;
            }
            else
            {
                monitoring.RegisteredCharacterCount = 0;
                monitoring.ReadyCharacterCount = 0;
                monitoring.ClosestRetainerSeconds = null;
            }
        }
        else
        {
            monitoring.RegisteredCharacterCount = 0;
            monitoring.ReadyCharacterCount = 0;
            monitoring.ClosestRetainerSeconds = null;
        }

        monitoring.ReadinessScanAvailable = readinessAvailable ? true : autoRetainerAvailable ? false : null;
        var hasReadyWork = monitoring.ReadyCharacterCount > 0;
        var readinessFingerprint = $"{SessionId()}|available={readinessAvailable};registered={monitoring.RegisteredCharacterCount};ready={monitoring.ReadyCharacterCount};closest={monitoring.ClosestRetainerSeconds?.ToString() ?? "none"};retainers={configuration.MonitorRetainers};deployables={configuration.MonitorDeployables}";
        diagnosticLog.Write("readiness", readinessFingerprint,
            $"ReadinessScanCompleted Session={SessionId()} Available={readinessAvailable} Registered={monitoring.RegisteredCharacterCount} Ready={monitoring.ReadyCharacterCount} ClosestRetainerSeconds={monitoring.ClosestRetainerSeconds?.ToString() ?? "none"} Retainers={configuration.MonitorRetainers} Deployables={configuration.MonitorDeployables} EvidenceUtc={monitoring.LastReadinessScanUtc?.ToString("O") ?? "none"}.", now);

        if (playerBlockers.Count > 0)
        {
            var state = hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForPlayer;
            var reason = hasReadyWork
                             ? $"Ready work found; waiting for: {MonitoringStatusFormatter.FormatPlayerBlockers(playerBlockers)}."
                             : $"Unsafe player state: {string.Join(", ", playerBlockers)}.";
            SetMonitoring(state, reason, now);
            return;
        }

        if (!questionableAvailable)
        {
            SetMonitoring(
                hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForQuestionable,
                hasReadyWork ? "Ready work found; Questionable IPC is unavailable." : "Questionable IPC is unavailable.",
                now);
            return;
        }

        if (!questionableRunning)
        {
            SetMonitoring(MonitoringState.WaitingForQuestionable, "Questionable automation is not running.", now);
            return;
        }

        if (!questAvailable)
        {
            SetMonitoring(MonitoringState.WaitingForQuestionable, "Questionable quest IPC is unavailable.", now);
            return;
        }

        if (string.IsNullOrWhiteSpace(monitoring.QuestionableQuestId))
        {
            SetMonitoring(MonitoringState.WaitingForQuestionable, "Questionable is running but has no current quest ID.", now);
            return;
        }

        var oceanState = OceanFishingSchedule.GetState(
            now,
            TimeSpan.FromMinutes(Math.Clamp(configuration.OceanFishingLeadMinutes, 0, 30)));
        if (configuration.MonitorOceanFishing && oceanState.LaunchEligible && !monitoring.OceanWindowHandled)
        {
            TryStartOceanFishing(now, oceanState);
            return;
        }

        if (!autoRetainerAvailable)
        {
            SetMonitoring(
                hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForAutoRetainer,
                hasReadyWork ? "Ready work found; AutoRetainer state IPC is unavailable." : "AutoRetainer state IPC is unavailable.",
                now);
            return;
        }

        if (autoRetainerBusy || multiModeActive)
        {
            SetMonitoring(
                hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForAutoRetainer,
                hasReadyWork ? "Ready work found; AutoRetainer is already processing work." : "AutoRetainer is already processing work.",
                now);
            return;
        }

        if (!lifestreamAvailable)
        {
            SetMonitoring(
                hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForLifestream,
                hasReadyWork ? "Ready work found; Lifestream IPC is unavailable." : "Lifestream IPC is unavailable.",
                now);
            return;
        }

        if (lifestreamBusy)
        {
            SetMonitoring(
                hasReadyWork ? MonitoringState.ReadyWorkBlocked : MonitoringState.WaitingForLifestream,
                hasReadyWork ? "Ready work found; Lifestream is busy." : "Lifestream is busy.",
                now);
            return;
        }

        if (!readinessAvailable)
        {
            SetMonitoring(MonitoringState.WaitingForAutoRetainer, "AutoRetainer readiness scan failed.", now);
            return;
        }

        if (monitoring.ReadyCharacterCount == 0)
        {
            SetMonitoring(MonitoringState.WaitingForReadyWork, "Dependencies ready; no retainer or deployable work is ready.", now);
            return;
        }

        if (now < nextQuestionableStopRetryUtc)
        {
            SetMonitoring(
                MonitoringState.ReadyWorkBlocked,
                "Ready work found; safe Questionable stop integration unavailable. Retrying in one minute.",
                now);
            return;
        }

        SetMonitoring(
            MonitoringState.ReadyWorkDetected,
            $"Found ready work on {monitoring.ReadyCharacterCount} of {monitoring.RegisteredCharacterCount} registered character(s).",
            now);
        monitoring.TriggerRegisteredCharacterCount = monitoring.RegisteredCharacterCount;
        monitoring.TriggerReadyCharacterCount = monitoring.ReadyCharacterCount;
        monitoring.TriggerClosestRetainerSeconds = monitoring.ClosestRetainerSeconds;
        monitoring.TriggerDetectedUtc = now;
        if (playerState.ContentId == 0 ||
            !autoRetainer.TryGetCharacter(playerState.ContentId, out var original))
        {
            SetMonitoring(MonitoringState.WaitingForAutoRetainer, "Ready work found, but current character is missing from AutoRetainer data.", now);
            return;
        }

        var session = new HandoffSession
        {
            SessionVersion = 8,
            Kind = HandoffKind.AutoRetainer,
            CurrentLeg = HandoffLeg.AutoRetainer,
            PendingAutoRetainerMode = GetConfiguredAutoRetainerMode(),
            CorrelationId = CreateCorrelationId(),
            OriginalContentId = original.CID,
            OriginalName = original.Name,
            OriginalWorld = original.World,
            QuestId = monitoring.QuestionableQuestId,
            StartedUtc = now,
            QuestionableWasRunning = questionableRunning,
        };
        ResetOceanCleanupStability();
        diagnosticLog.Reset();
        configuration.ActiveSession = session;
        save();

        TryRequestQuestionableStop(
            session,
            now,
            $"Pausing Questionable for AutoRetainer work on {monitoring.ReadyCharacterCount} character(s).");
    }

    private void TryStartOceanFishing(DateTime now, OceanFishingScheduleState oceanState)
    {
        if (monitoring.AutoRetainerAvailable != true)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; AutoRetainer state IPC is unavailable.", now);
            return;
        }

        if (monitoring.AutoRetainerBusy || monitoring.AutoRetainerMultiModeActive)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; active AutoRetainer work will not be preempted.", now);
            return;
        }

        if (monitoring.LifestreamAvailable != true)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; Lifestream IPC is unavailable.", now);
            return;
        }

        if (monitoring.LifestreamBusy)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; Lifestream is busy.", now);
            return;
        }

        if (monitoring.PlayerBlockers.Count > 0)
        {
            SetMonitoring(
                MonitoringState.ReadyWorkBlocked,
                $"Ocean Fishing ready; waiting for: {MonitoringStatusFormatter.FormatPlayerBlockers(monitoring.PlayerBlockers)}.",
                now);
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        var contentId = playerState.ContentId;
        var world = localPlayer?.HomeWorld.ValueNullable?.Name.ToString();
        if (contentId == 0 || localPlayer is null || string.IsNullOrWhiteSpace(world))
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; local player identity is unavailable.", now);
            return;
        }

        var session = new HandoffSession
        {
            SessionVersion = 8,
            Kind = HandoffKind.OceanFishing,
            CurrentLeg = HandoffLeg.OceanFishing,
            CorrelationId = CreateCorrelationId(),
            OriginalContentId = contentId,
            OriginalName = localPlayer.Name.ToString(),
            OriginalWorld = world,
            QuestId = monitoring.QuestionableQuestId,
            StartedUtc = now,
            QuestionableWasRunning = true,
            OceanWindowStartUtc = oceanState.Window.StartUtc,
        };
        if (!TrySelectFishingCharacter(session, out var selectionError))
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, selectionError, now);
            return;
        }
        ResetOceanCleanupStability();
        diagnosticLog.Reset();
        configuration.ActiveSession = session;
        save();

        log.Information(
            "Ocean Fishing handoff claimed. Session={Session} WindowStartUtc={WindowStartUtc} LeadMinutes={LeadMinutes}.",
            session.CorrelationId,
            oceanState.Window.StartUtc,
            configuration.OceanFishingLeadMinutes);

        TryRequestQuestionableStop(session, now, "Pausing Questionable for Ocean Fishing.");
    }

    private bool TryRequestQuestionableStop(HandoffSession session, DateTime now, string notification)
    {
        var stopResult = questionable.TryStopWithoutCommand(out var stopError);
        switch (QuestionableStopPolicy.GetAction(stopResult))
        {
            case QuestionableStopAction.RetryBeforeHandoff:
                nextQuestionableStopRetryUtc = now + QuestionableStopRetryInterval;
                configuration.ActiveSession = null;
                SetPhase(HandoffPhase.Idle, persist: false);
                SetMonitoring(
                    MonitoringState.ReadyWorkBlocked,
                    "Safe Questionable stop integration unavailable. Retrying in one minute.",
                    now);
                save();
                return false;
            case QuestionableStopAction.FailHandoff:
                session.QuestionableStopRequested = true;
                save();
                Fail(stopError);
                return false;
        }

        session.QuestionableStopRequested = true;
        save();
        Notify(notification);
        SetPhase(HandoffPhase.WaitingForQuestionable);
        return true;
    }

    private void UpdateWaitingForQuestionable(DateTime now)
    {
        if (!questionable.TryIsRunning(out var running))
        {
            if (HasTimedOut(now, TimeSpan.FromSeconds(30)))
            {
                Fail("Questionable state remained unavailable for 30 seconds while confirming stop.");
                return;
            }

            Wait(now, "Waiting for Questionable state IPC while confirming stop.");
            return;
        }

        if (running)
        {
            if (HasTimedOut(now, TimeSpan.FromSeconds(30)))
            {
                Fail("Questionable did not stop within 30 seconds.");
                return;
            }

            Wait(now, "Waiting for Questionable to stop.");
            return;
        }

        var blockers = CapturePlayerBlockers();
        if (blockers.Count > 0)
        {
            Wait(now, $"Waiting for a safe handoff; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(blockers)}.");
            return;
        }

        questionablePausedThisRuntime = true;
        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing after Questionable stopped.");
            return;
        }

        manualResumeAuthorized = session.Kind == HandoffKind.AutoRetainer;
        session.QuestionableStopConfirmed = true;
        if (IsOceanLeg(session))
        {
            save();
            SetPhase(HandoffPhase.PreparingFishingCharacter, reason: "Questionable paused; preparing selected Ocean Fishing character.");
            PrepareFishingCharacter(now);
            return;
        }

        if (session.Kind == HandoffKind.AutoRetainer)
        {
            save();
        }

        completionEventReceived = false;
        autoRetainerMultiModeObserved = false;
        log.Information(
            "Questionable stop confirmed. Session={Session}; waiting for AutoRetainer to become idle before starting its pass.",
            SessionId());
        SetPhase(HandoffPhase.WaitingForAutoRetainerIdle);
    }

    private bool TrySelectFishingCharacter(HandoffSession session, out string error)
    {
        OfflineCharacterData? selectedCharacter = null;
        if (configuration.OceanFishingCharacterMode == OceanFishingCharacterMode.SelectedCharacter &&
            configuration.OceanFishingCharacterContentId != 0)
            autoRetainer.TryGetCharacter(configuration.OceanFishingCharacterContentId, out selectedCharacter!);

        var current = new OceanFishingCharacterIdentity(session.OriginalContentId, session.OriginalName, session.OriginalWorld);
        OceanFishingCharacterIdentity? selected = selectedCharacter is null
                                                      ? null
                                                      : new(selectedCharacter.CID, selectedCharacter.Name, selectedCharacter.World);
        if (!OceanFishingPolicy.TrySelectCharacter(configuration.OceanFishingCharacterMode, current, selected, out var target))
        {
            error = "Selected Ocean Fishing character is no longer available in AutoRetainer; update selection before the next window.";
            return false;
        }

        session.FishingContentId = target.ContentId;
        session.FishingName = target.Name;
        session.FishingWorld = target.World;
        error = string.Empty;
        return true;
    }

    private void PrepareFishingCharacter(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session))
        {
            Fail("Ocean Fishing handoff data is missing before character selection.");
            return;
        }

        if (!TryGetAutoRetainerState(out var autoBusy, out var multiModeActive) || autoBusy || multiModeActive)
        {
            Wait(now, "Waiting for AutoRetainer to become idle before fishing-character selection.");
            return;
        }

        if (!TryGetLifestreamBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            Wait(now, "Waiting for Lifestream to become idle before fishing-character selection.");
            return;
        }

        if (clientState.IsLoggedIn && playerState.ContentId == session.FishingContentId)
        {
            if (IsSafeForHandoff())
                SetPhase(HandoffPhase.StartingWahToolsFishing, reason: "Fishing character confirmed; preparing WahTools voyage.");
            else
                Wait(now, $"Waiting for fishing character safe state; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        if (HasTimedOut(now, TimeSpan.FromMinutes(10)))
        {
            Fail("Fishing character login did not complete within ten minutes.");
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval)
        {
            Wait(now, "Waiting before requesting fishing-character login.");
            return;
        }

        if (session.NextFishingCharacterSwitchAttemptUtc is { } nextSwitch && now < nextSwitch)
        {
            Wait(now, "Waiting before retrying fishing-character login.");
            return;
        }

        lastActionUtc = now;
        if (!lifestream.TryChangeCharacter(session.FishingName, session.FishingWorld, out var error))
        {
            session.FishingCharacterSwitchAttempts++;
            session.NextFishingCharacterSwitchAttemptUtc = now + RecoveryBackoffPolicy.GetDelay(session.FishingCharacterSwitchAttempts);
            save();
            diagnosticLog.Write("character-switch-retry", $"fishing|{session.FishingCharacterSwitchAttempts}|{error}",
                $"CharacterSwitchRetry Session={SessionId()} Phase={Phase} Operation=FishingCharacter Target={session.FishingName}@{session.FishingWorld} TargetContentId={session.FishingContentId} Attempt={session.FishingCharacterSwitchAttempts} Result=Rejected Error={error} NextAttemptUtc={session.NextFishingCharacterSwitchAttemptUtc:O}.", now);
            Wait(now, $"Lifestream fishing-character request rejected: {error}.");
            return;
        }

        session.NextFishingCharacterSwitchAttemptUtc = null;
        save();
        SetPhase(HandoffPhase.WaitingForFishingCharacter, reason: $"Logging into fishing character {session.FishingName}@{session.FishingWorld}.");
    }

    private void UpdateWaitingForFishingCharacter(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session))
        {
            Fail("Ocean Fishing session is missing while waiting for fishing character.");
            return;
        }

        if (HasTimedOut(now, TimeSpan.FromMinutes(10)))
        {
            Fail("Fishing character login did not complete within ten minutes.");
            return;
        }

        if (clientState.IsLoggedIn && playerState.ContentId == session.FishingContentId)
        {
            if (IsSafeForHandoff())
                SetPhase(HandoffPhase.StartingWahToolsFishing, reason: "Fishing character confirmed; preparing WahTools voyage.");
            else
                Wait(now, $"Waiting for fishing character safe state; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        Wait(now, "Waiting for Lifestream to finish logging into the fishing character.");
    }

    private void StartWahToolsFishing(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session))
        {
            Fail("Ocean Fishing handoff data is missing before WahTools dispatch.");
            return;
        }

        if (!clientState.IsLoggedIn || playerState.ContentId != session.FishingContentId)
        {
            SetPhase(HandoffPhase.PreparingFishingCharacter, reason: "Fishing character changed before dispatch; confirming target again.");
            return;
        }

        if (!TryGetAutoRetainerState(out var autoBusy, out var multiModeActive) || autoBusy || multiModeActive)
        {
            Wait(now, "Waiting for AutoRetainer to become idle before WahTools launch.");
            return;
        }

        if (!TryGetLifestreamBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            Wait(now, "Waiting for Lifestream to become idle before WahTools launch.");
            return;
        }

        if (!IsSafeForHandoff())
        {
            Wait(now, $"Waiting for safe WahTools launch state; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        if (!OceanFishingPolicy.CanDispatch(now, session.OceanWindowStartUtc,
                clientState.IsLoggedIn && playerState.ContentId == session.FishingContentId,
                playerSafe: true))
        {
            session.OceanFishingLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Fishing-character login completed after Ocean Fishing cutoff; restoring original character.");
            return;
        }

        var leaseResult = yesAlreadyLease.TryAcquire(out var leaseError);
        if (leaseResult == YesAlreadyLeaseResult.Unavailable)
        {
            Fail(leaseError);
            return;
        }

        session.YesAlreadyLeaseRequired = leaseResult == YesAlreadyLeaseResult.Acquired;
        session.YesAlreadyLeaseAcquired = yesAlreadyLease.IsAcquired;
        save();
        var autoOcean = wahToolsFeesh.DisableAutoOcean(out var autoError);
        monitoring.WahToolsAvailable = autoOcean != FeeshIpcResult.Unavailable;
        if (autoOcean != FeeshIpcResult.Accepted)
        {
            Fail(autoError);
            return;
        }

        session.WahToolsLaunchRequested = true;
        session.WahToolsLaunchAmbiguous = true;
        save();
        var result = wahToolsFeesh.CatchTheBoat(out var launchError);
        monitoring.WahToolsAvailable = result != FeeshIpcResult.Unavailable;
        monitoring.WahToolsLaunchAccepted = result == FeeshIpcResult.Accepted;
        if (result == FeeshIpcResult.Refused)
        {
            session.WahToolsLaunchAmbiguous = false;
            session.OceanFishingLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: launchError);
            return;
        }

        session.WahToolsLaunchAccepted = result == FeeshIpcResult.Accepted;
        session.WahToolsLaunchAmbiguous = result == FeeshIpcResult.Unavailable;
        wahToolsSessionObservedThisRuntime = result == FeeshIpcResult.Accepted;
        session.OceanFishingLegSkipped = false;
        save();
        Notify("WahTools Ocean Fishing request sent; observing game state for boarding.");
        SetPhase(HandoffPhase.WaitingForOceanDuty);
    }

    private void UpdateCheckingChainedWork(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing while checking chained work.");
            return;
        }

        if (!clientState.IsLoggedIn || playerState.ContentId == 0)
        {
            session.AutoRetainerLegSkipped = true;
            session.PendingAutoRetainerMode = null;
            session.ReturningForAutoRetainer = false;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Optional chained work will not start while logged out; restoring the original character.");
            return;
        }

        var oceanState = OceanFishingSchedule.GetState(
            now,
            TimeSpan.FromMinutes(Math.Clamp(configuration.OceanFishingLeadMinutes, 0, 30)));
        var action = ChainedWorkPlanner.Choose(new(
            configuration.MonitorOceanFishing,
            session.OceanFishingLegCompleted || session.OceanFishingLegSkipped,
            oceanState.LaunchEligible,
            OceanFishingSchedule.IsWindowHandled(configuration.LastHandledOceanFishingWindowUtc, oceanState.Window.StartUtc),
            configuration.MonitorRetainers || configuration.MonitorDeployables,
            session.AutoRetainerLegCompleted || session.AutoRetainerLegSkipped));

        if (!session.OceanFishingLegCompleted &&
            !session.OceanFishingLegSkipped &&
            now >= oceanState.Window.LaunchCutoffUtc &&
            !oceanState.LaunchEligible)
        {
            session.OceanFishingLegSkipped = true;
            save();
            log.Information("Ocean Fishing leg skipped after launch cutoff. Session={Session} WindowStartUtc={WindowStartUtc}.", SessionId(), oceanState.Window.StartUtc);
        }

        switch (action)
        {
            case ChainedWorkAction.StartOceanFishing:
                if (!TryStartChainedOceanFishing(session, now, oceanState))
                    return;
                return;
            case ChainedWorkAction.ScanAutoRetainer:
                CheckChainedAutoRetainer(session, now);
                return;
            default:
                SetPhase(HandoffPhase.ReturningOriginal, reason: "All chained work is resolved; returning to the original character.");
                return;
        }
    }

    private void CheckChainedAutoRetainer(HandoffSession session, DateTime now)
    {
        if (session.ChainedReadinessChecked)
        {
            if (session.ChainedReadyCharacterCount is not > 0)
            {
                session.AutoRetainerLegSkipped = true;
                save();
                SetPhase(HandoffPhase.ReturningOriginal, reason: "No AutoRetainer work is ready after the completed leg.");
                return;
            }

            StartChainedAutoRetainer(session, now);
            return;
        }

        if (now < session.ChainedReadinessScanUtc.GetValueOrDefault() + TimeSpan.FromSeconds(5))
        {
            Wait(now, "Waiting before refreshing AutoRetainer readiness after the completed leg.");
            return;
        }

        if (monitoring.AutoRetainerAvailable != true)
        {
            if (HasTimedOut(now, TimeSpan.FromMinutes(1)))
            {
                session.AutoRetainerLegSkipped = true;
                save();
                SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer state IPC remained unavailable; skipping optional chained work.");
            }
            else
                Wait(now, "Waiting for AutoRetainer state IPC before checking chained work.");
            return;
        }

        var scanned = autoRetainer.TryScanReadiness(
            configuration.MonitorRetainers,
            configuration.MonitorDeployables,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            out var readiness);
        if (!scanned)
        {
            diagnosticLog.Write("chained-readiness", $"unavailable|{session.CurrentLeg}",
                $"ChainedReadinessScan Session={SessionId()} Fresh=true Available=false CurrentLeg={session.CurrentLeg} EvidenceUtc={now:O}; scheduling retry.", now);
            session.ChainedReadinessScanUtc = now;
            save();
            if (HasTimedOut(now, TimeSpan.FromMinutes(1)))
            {
                session.AutoRetainerLegSkipped = true;
                save();
                SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer readiness remained unavailable; skipping optional chained work.");
            }
            else
                Wait(now, "Waiting for a fresh AutoRetainer readiness scan.");
            return;
        }

        session.ChainedReadinessChecked = true;
        session.ChainedRegisteredCharacterCount = readiness.RegisteredCharacterCount;
        session.ChainedReadyCharacterCount = readiness.ReadyCharacterCount;
        session.ChainedClosestRetainerSeconds = readiness.ClosestRetainerSeconds;
        session.ChainedReadinessScanUtc = now;
        monitoring.RegisteredCharacterCount = readiness.RegisteredCharacterCount;
        monitoring.ReadyCharacterCount = readiness.ReadyCharacterCount;
        monitoring.ClosestRetainerSeconds = readiness.ClosestRetainerSeconds;
        monitoring.LastReadinessScanUtc = now;
        ReadyCharacterCount = readiness.ReadyCharacterCount;
        save();
        diagnosticLog.Write("chained-readiness", $"available|{readiness.RegisteredCharacterCount}|{readiness.ReadyCharacterCount}|{readiness.ClosestRetainerSeconds}",
            $"ChainedReadinessScan Session={SessionId()} Fresh=true Available=true Registered={readiness.RegisteredCharacterCount} Ready={readiness.ReadyCharacterCount} ClosestRetainerSeconds={readiness.ClosestRetainerSeconds?.ToString() ?? "none"} EvidenceUtc={now:O} Retainers={configuration.MonitorRetainers} Deployables={configuration.MonitorDeployables}.", now);

        if (readiness.ReadyCharacterCount == 0)
        {
            session.AutoRetainerLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "No AutoRetainer work is ready after the completed leg.");
            return;
        }

        StartChainedAutoRetainer(session, now);
    }

    private void StartChainedAutoRetainer(HandoffSession session, DateTime now)
    {
        session.PendingAutoRetainerMode ??= GetConfiguredAutoRetainerMode();
        if (session.PendingAutoRetainerMode is null)
        {
            session.AutoRetainerLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer monitoring is disabled; skipping optional chained work.");
            return;
        }

        if (!clientState.IsLoggedIn || playerState.ContentId == 0)
        {
            session.AutoRetainerLegSkipped = true;
            session.PendingAutoRetainerMode = null;
            session.ReturningForAutoRetainer = false;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Optional chained AutoRetainer work skipped while logged out; restoring the original character.");
            return;
        }

        if (autoRetainer.TryGetCharacter(playerState.ContentId, out _))
        {
            session.CurrentLeg = HandoffLeg.AutoRetainer;
            session.ReturningForAutoRetainer = false;
            completionEventReceived = false;
            autoRetainerMultiModeObserved = false;
            session.AutoRetainerStartRequested = false;
            session.AutoRetainerMultiModeObserved = false;
            session.AutoRetainerCompletionReceived = false;
            save();
            log.Information("Starting chained AutoRetainer pass from current character. Session={Session}.", SessionId());
            SetPhase(HandoffPhase.WaitingForAutoRetainerIdle, reason: "Starting chained AutoRetainer work without resuming Questionable.");
            return;
        }

        if (playerState.ContentId == session.OriginalContentId && autoRetainer.TryGetCharacter(session.OriginalContentId, out _))
        {
            session.CurrentLeg = HandoffLeg.AutoRetainer;
            session.ReturningForAutoRetainer = false;
            save();
            SetPhase(HandoffPhase.WaitingForAutoRetainerIdle, reason: "Original character is already registered for chained AutoRetainer work.");
            return;
        }

        if (autoRetainer.TryGetCharacter(session.OriginalContentId, out _))
        {
            session.CurrentLeg = HandoffLeg.AutoRetainer;
            session.ReturningForAutoRetainer = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Returning to the registered original character before chained AutoRetainer work.");
            return;
        }

        session.AutoRetainerLegSkipped = true;
        save();
        SetPhase(HandoffPhase.ReturningOriginal, reason: "No registered current or original character is available for chained AutoRetainer work.");
    }

    private bool TryStartChainedOceanFishing(HandoffSession session, DateTime now, OceanFishingScheduleState oceanState)
    {
        if (now >= oceanState.Window.LaunchCutoffUtc)
        {
            session.OceanFishingLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer finished after the Ocean Fishing launch cutoff; skipping this voyage.");
            return false;
        }

        if (monitoring.AutoRetainerAvailable != true || monitoring.AutoRetainerBusy || monitoring.AutoRetainerMultiModeActive)
        {
            Wait(now, "Waiting for AutoRetainer to become idle before starting chained Ocean Fishing.");
            return false;
        }

        if (monitoring.LifestreamAvailable != true || monitoring.LifestreamBusy)
        {
            Wait(now, "Waiting for Lifestream to become idle before starting chained Ocean Fishing.");
            return false;
        }

        if (monitoring.PlayerBlockers.Count > 0)
        {
            Wait(now, $"Waiting for a safe player state before chained Ocean Fishing; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(monitoring.PlayerBlockers)}.");
            return false;
        }

        session.CurrentLeg = HandoffLeg.OceanFishing;
        session.OceanWindowStartUtc = oceanState.Window.StartUtc;
        session.OceanDutyStarted = false;
        session.OceanDutyCompleted = false;
        session.OceanTerritoryEntered = false;
        session.OceanTerritoryExited = false;
        session.WahToolsLaunchRequested = false;
        session.WahToolsLaunchAccepted = false;
        session.WahToolsLaunchAmbiguous = false;
        ResetOceanCleanupStability();
        save();
        log.Information("Starting chained Ocean Fishing without resuming Questionable. Session={Session} WindowStartUtc={WindowStartUtc}.", SessionId(), oceanState.Window.StartUtc);
        if (!TrySelectFishingCharacter(session, out var selectionError))
        {
            session.OceanFishingLegSkipped = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: selectionError);
            return false;
        }
        SetPhase(HandoffPhase.PreparingFishingCharacter, reason: "Chained Ocean Fishing selected; preparing fishing character.");
        PrepareFishingCharacter(now);
        return true;
    }

    private AutoRetainerPassMode? GetConfiguredAutoRetainerMode() =>
        (configuration.MonitorRetainers, configuration.MonitorDeployables) switch
        {
            (true, true) => AutoRetainerPassMode.Everything,
            (true, false) => AutoRetainerPassMode.Retainers,
            (false, true) => AutoRetainerPassMode.Deployables,
            _ => null,
        };

    private void UpdateWaitingForOceanDuty(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session))
        {
            Fail("Ocean Fishing handoff data is missing.");
            return;
        }

        if (session.OceanDutyCompleted)
        {
            SetPhase(HandoffPhase.WaitingForOceanCleanup);
            return;
        }

        var observation = OceanFishingPolicy.Observe(
            IsOceanFishingTerritory(),
            IsOceanFishingDutyActive(),
            session.OceanDutyCompleted,
            session.OceanTerritoryEntered);
        if (observation == OceanFishingObservation.TerritoryEntered)
        {
            session.OceanTerritoryEntered = true;
            session.OceanDutyStarted = true;
            save();
            log.Information("Ocean Fishing territory entry observed. Session={Session} Territory={Territory}.", SessionId(), clientState.TerritoryType);
        }

        if (observation == OceanFishingObservation.DutyActive && !session.OceanDutyStarted)
        {
            session.OceanTerritoryEntered = true;
            session.OceanDutyStarted = true;
            ReleaseYesAlreadyLease(session);
            save();
            log.Information("Ocean Fishing duty observed. Session={Session}.", SessionId());
        }

        if (monitoring.WahToolsStatus is { IsAvailable: true } feeshStatus &&
            WahToolsFeeshStatusPolicy.IsTripFailed(feeshStatus))
        {
            var reason = string.IsNullOrWhiteSpace(feeshStatus.TripStatus)
                             ? "WahTools boat trip failed."
                             : $"WahTools boat trip failed: {feeshStatus.TripStatus}";
            if (session.OceanTerritoryEntered)
                HandleFailure(RecoveryFailureCode.OceanDutyExitedUnconfirmed, reason);
            else
                Fail(reason);
            return;
        }

        var timeout = session.OceanDutyStarted ? OceanVoyageTimeout : OceanDutyStartTimeout;
        if (HasTimedOut(now, timeout))
        {
            Fail(session.OceanDutyStarted
                     ? "Ocean Fishing voyage did not complete within two hours."
                     : "WahTools did not enter Ocean Fishing within 30 minutes.");
            return;
        }

        if (observation == OceanFishingObservation.TerritoryExitedUnconfirmed)
        {
            HandleFailure(
                RecoveryFailureCode.OceanDutyExitedUnconfirmed,
                "Ocean territory exited without confirmed duty completion.");
            return;
        }

        if (session.WahToolsLaunchAmbiguous && !session.OceanTerritoryEntered && HasTimedOut(now, TimeSpan.FromMinutes(2)))
        {
            session.OceanFishingLegSkipped = true;
            save();
            EnterManualIntervention("WahTools launch outcome is ambiguous and no voyage entry was observed. Confirm Feesh stopped before returning to another character.");
            return;
        }

        Wait(
            now,
            session.OceanDutyStarted
                ? "Ocean Fishing voyage active; waiting for duty completion."
                : "Waiting for WahTools to enter Ocean Fishing.");
    }

    private void UpdateWaitingForOceanCleanup(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session) || !session.OceanDutyCompleted)
        {
            Fail("Ocean Fishing cleanup started without a completed voyage.");
            return;
        }

        if (now < nextOceanCleanupPollUtc)
            return;

        nextOceanCleanupPollUtc = now + OceanCleanupPollInterval;
        var snapshot = CaptureOceanCleanupSnapshot();
        var cleanupReasons = OceanFishingCleanupPolicy.GetWaitReasons(snapshot, CapturePlayerBlockers()).ToList();
        var confirmationSource = SelectCleanupConfirmationSource(null, out var confirmationNowUtc);
        if (confirmationSource == OceanCleanupConfirmationSource.WahToolsReflection && monitoring.WahToolsStatus is { } feeshStatus)
        {
            if (WahToolsFeeshStatusPolicy.IsTripFailed(feeshStatus))
            {
                EnterManualIntervention($"WahTools boat trip failed during cleanup: {feeshStatus.TripStatus ?? "no failure detail"}");
                return;
            }

            if (WahToolsFeeshStatusPolicy.HasCleanerFailure(feeshStatus))
            {
                EnterManualIntervention($"WahTools cleaner failed: {feeshStatus.CleanerStatus ?? "no failure detail"}");
                return;
            }

            cleanupReasons.AddRange(WahToolsFeeshStatusPolicy.GetCleanupBlockers(feeshStatus));
        }
        if (cleanupReasons.Count > 0)
        {
            var fallback = confirmationSource == OceanCleanupConfirmationSource.GameStateFallback
                               ? $"WahTools status unavailable or stale ({monitoring.WahToolsStatus?.Detail ?? "not read"}); using game-state fallback; "
                               : string.Empty;
            monitoring.OceanCleanupWaitReason = fallback + string.Join("; ", cleanupReasons);
            ResetOceanCleanupStability(monitoring.OceanCleanupWaitReason);
            monitoring.OceanCleanupStableElapsedSeconds = null;
            Wait(now, "Waiting for WahTools cleanup and safe post-voyage conditions.");
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
        {
            monitoring.OceanCleanupWaitReason = "local player unavailable";
            ResetOceanCleanupStability(monitoring.OceanCleanupWaitReason);
            Wait(now, "Ocean Fishing cleanup blocked: local player unavailable.");
            return;
        }

        var position = localPlayer.Position;
        var contentId = playerState.ContentId;
        var territory = clientState.TerritoryType;
        if (!UpdateCleanupConfirmation(confirmationNowUtc, position, contentId, territory, confirmationSource))
        {
            var sourceName = confirmationSource == OceanCleanupConfirmationSource.WahToolsReflection ? "reflected" : "fallback";
            Wait(now, $"Confirming {sourceName} post-voyage state.");
            return;
        }

        monitoring.OceanCleanupWaitReason = $"{confirmationSource} cleanup state confirmed; requesting WahTools stop";
        StopWahTools(session);
    }

    private void StopWahTools(HandoffSession session)
    {
        if (session.WahToolsStopRequested)
        {
            if (session.WahToolsStopAcceptedUtc is null)
            {
                EnterManualIntervention("WahTools Stop outcome is ambiguous. Confirm Feesh stopped before returning to another character.");
                return;
            }

            SetPhase(HandoffPhase.WaitingForWahToolsStop, reason: "WahTools Stop was accepted; awaiting fresh post-stop status.");
            return;
        }

        if (!session.WahToolsLaunchAccepted || session.WahToolsLaunchAmbiguous || !wahToolsSessionObservedThisRuntime)
        {
            if (session.OceanFishingLegSkipped)
                EnterManualIntervention("Feesh stop ownership is unproven. Confirm Feesh stopped before returning to another character.");
            else
                CompleteOceanFishingLeg(session, "Ocean Fishing completed; no WahTools stop issued because launch ownership is uncertain.");
            return;
        }

        session.WahToolsStopRequested = true;
        session.WahToolsStopRequestedUtc = DateTime.UtcNow;
        session.WahToolsStopStatusGeneration = monitoring.WahToolsStatus?.Generation;
        save();
        var result = wahToolsFeesh.Stop(out var error);
        if (result == FeeshIpcResult.Unavailable)
        {
            EnterManualIntervention($"WahTools stop IPC unavailable; cannot prove Feesh travel stopped safely: {error}");
            return;
        }

        if (result == FeeshIpcResult.Refused)
        {
            EnterManualIntervention($"WahTools refused Stop; cannot prove Feesh travel stopped safely: {error}");
            return;
        }

        session.WahToolsStopAcceptedUtc = DateTime.UtcNow;
        save();
        ResetOceanCleanupStability();
        SetPhase(HandoffPhase.WaitingForWahToolsStop, reason: "WahTools Stop was accepted; awaiting fresh post-stop status.");
    }

    private void ReleaseYesAlreadyLease(HandoffSession session)
    {
        if (!session.YesAlreadyLeaseAcquired)
            return;

        yesAlreadyLease.Release();
        session.YesAlreadyLeaseAcquired = false;
        log.Information("YesAlready Ocean Fishing lease released. Session={Session}.", SessionId());
    }

    private void UpdateWaitingForAutoRetainerIdle(DateTime now)
    {
        if (HasTimedOut(now, TimeSpan.FromMinutes(5)))
        {
            Fail("AutoRetainer remained busy for five minutes after Questionable stopped.");
            return;
        }

        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (!TryGetAutoRetainerState(out var busy, out var multiModeActive))
        {
            Wait(now, "Waiting for AutoRetainer state IPC before starting its pass.");
            return;
        }

        if (multiModeActive)
        {
            MarkAutoRetainerMultiModeObserved(session);
            SetPhase(HandoffPhase.WaitingForAutoRetainer);
            return;
        }

        if (completionEventReceived || session.AutoRetainerCompletionReceived)
        {
            CompleteAutoRetainerLeg(session, "AutoRetainer completion already received; checking chained work.");
            return;
        }

        if (busy)
        {
            Wait(now, "Waiting for transient AutoRetainer activity to finish before starting Multi Mode.");
            return;
        }

        if (!questionablePausedThisRuntime)
        {
            var recoveryPhase = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId
                                    ? GetResumePhase()
                                    : HandoffPhase.ReturningOriginal;
            SetPhase(
                recoveryPhase,
                reason: "Persisted handoff will not start a new AutoRetainer pass after reload.");
            return;
        }

        var blockers = CapturePlayerBlockers();
        if (blockers.Count > 0)
        {
            Wait(now, $"Waiting for a safe player state before starting AutoRetainer; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(blockers)}.");
            return;
        }

        session.AutoRetainerStartRequested = true;
        session.AutoRetainerMultiModeObserved = false;
        session.AutoRetainerCompletionReceived = false;
        autoRetainerMultiModeObserved = false;
        completionEventReceived = false;
        save();

        var mode = session.PendingAutoRetainerMode switch
        {
            AutoRetainerPassMode.Everything => MultiModeType.Everything,
            AutoRetainerPassMode.Retainers => MultiModeType.Retainers,
            AutoRetainerPassMode.Deployables => MultiModeType.Submersibles,
            _ => GetConfiguredAutoRetainerMode() switch
            {
                AutoRetainerPassMode.Everything => MultiModeType.Everything,
                AutoRetainerPassMode.Retainers => MultiModeType.Retainers,
                AutoRetainerPassMode.Deployables => MultiModeType.Submersibles,
                _ => MultiModeType.Everything,
            },
        };
        if (!autoRetainer.TryEnableSinglePass(mode))
        {
            Fail("AutoRetainer rejected the single-pass Multi Mode request.");
            return;
        }

        SetPhase(HandoffPhase.WaitingForAutoRetainer);
    }

    private void UpdateWaitingForAutoRetainer(DateTime now)
    {
        if (HasTimedOut(now, TimeSpan.FromHours(2)))
        {
            Fail("AutoRetainer did not finish within two hours.");
            return;
        }

        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (!TryGetAutoRetainerState(out var busy, out var multiModeActive))
        {
            Wait(now, "Waiting for AutoRetainer state IPC.");
            return;
        }

        if (multiModeActive)
        {
            MarkAutoRetainerMultiModeObserved(session);
            Wait(now, "Waiting for AutoRetainer Multi Mode to finish.");
            return;
        }

        if (busy)
        {
            Wait(now, "Waiting for AutoRetainer Multi Mode to finish.");
            return;
        }

        if (completionEventReceived)
            MarkAutoRetainerCompletionReceived(session);

        if (session.AutoRetainerCompletionReceived || autoRetainerMultiModeObserved || session.AutoRetainerMultiModeObserved)
        {
            CompleteAutoRetainerLeg(session, "AutoRetainer pass completed; checking chained work.");
            return;
        }

        if (!session.AutoRetainerCompletionReceived && !autoRetainerMultiModeObserved)
        {
            if (now >= phaseStartedUtc + TimeSpan.FromSeconds(30))
                Fail("AutoRetainer did not enter Multi Mode within 30 seconds.");
            else
                Wait(now, "Waiting for AutoRetainer to enter Multi Mode.");
            return;
        }

        CompleteAutoRetainerLeg(session, "AutoRetainer pass completed; checking chained work.");
    }

    private void CompleteAutoRetainerLeg(HandoffSession session, string reason)
    {
        session.AutoRetainerLegCompleted = true;
        session.CurrentLeg = HandoffLeg.None;
        session.PendingAutoRetainerMode = null;
        session.ReturningForAutoRetainer = false;
        save();
        log.Information("AutoRetainer leg complete; checking chained work. Session={Session}.", SessionId());
        SetPhase(HandoffPhase.CheckingChainedWork, reason: reason);
    }

    private void UpdateReturningOriginal(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (HasTimedOut(now, TimeSpan.FromMinutes(2)))
        {
            Fail("Lifestream could not start the return within two minutes.");
            return;
        }

        if (clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId)
        {
            if (IsSafeForHandoff())
                SetPhase(session.ReturningForAutoRetainer
                             ? HandoffPhase.WaitingForAutoRetainerIdle
                             : GetResumePhase());
            else
                Wait(now, $"Waiting on original character for a safe state; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval)
        {
            Wait(now, "Waiting before retrying the Lifestream return request.");
            return;
        }

        if (session.NextReturnCharacterAttemptUtc is { } nextReturn && now < nextReturn)
        {
            Wait(now, "Waiting before retrying original-character login.");
            return;
        }

        if (clientState.IsLoggedIn && !IsSafeForHandoff())
        {
            Wait(now, $"Waiting for a safe player state before returning; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        if (!TryGetLifestreamBusy(out var lifestreamBusy))
        {
            Wait(now, "Waiting for Lifestream state IPC.");
            return;
        }

        if (lifestreamBusy)
        {
            Wait(now, "Waiting for Lifestream to become idle.");
            return;
        }

        lastActionUtc = now;
        if (!lifestream.TryChangeCharacter(session.OriginalName, session.OriginalWorld, out var error))
        {
            session.ReturnCharacterAttempts++;
            session.NextReturnCharacterAttemptUtc = now + RecoveryBackoffPolicy.GetDelay(session.ReturnCharacterAttempts);
            save();
            diagnosticLog.Write("character-switch-retry", $"original|{session.ReturnCharacterAttempts}|{error}",
                $"CharacterSwitchRetry Session={SessionId()} Phase={Phase} Operation=ReturnOriginal Target={session.OriginalName}@{session.OriginalWorld} TargetContentId={session.OriginalContentId} Attempt={session.ReturnCharacterAttempts} Result=Rejected Error={error} NextAttemptUtc={session.NextReturnCharacterAttemptUtc:O}.", now);
            Wait(now, $"Lifestream return request was rejected: {error}.");
            return;
        }

        session.NextReturnCharacterAttemptUtc = null;
        save();
        Notify($"Returning to {session.OriginalName}@{session.OriginalWorld}.");
        SetPhase(HandoffPhase.WaitingForOriginal);
    }

    private void UpdateWaitingForOriginal(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (HasTimedOut(now, TimeSpan.FromMinutes(10)))
        {
            Fail("The original character did not finish logging in within ten minutes.");
            return;
        }

        if (clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId && IsSafeForHandoff())
            SetPhase(session.ReturningForAutoRetainer
                         ? HandoffPhase.WaitingForAutoRetainerIdle
                         : GetResumePhase());
        else if (!clientState.IsLoggedIn)
            Wait(now, "Waiting for the original character to finish logging in.");
        else if (playerState.ContentId != session.OriginalContentId)
            Wait(now, "Waiting to reach the original character.");
        else
            Wait(now, $"Waiting for a safe player state after login; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
    }

    private void UpdateResumingQuestionable(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (HasTimedOut(now, TimeSpan.FromMinutes(2)))
        {
            Fail("Dependencies were not ready to resume Questionable within two minutes.");
            return;
        }

        if (!clientState.IsLoggedIn)
        {
            Wait(now, "Waiting for the original character to finish logging in.");
            return;
        }

        if (playerState.ContentId != session.OriginalContentId)
        {
            Wait(now, "Waiting to reach the original character.");
            return;
        }

        var blockers = CapturePlayerBlockers();
        if (blockers.Count > 0)
        {
            Wait(now, $"Waiting for a safe player state before resuming Questionable; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(blockers)}.");
            return;
        }

        if (!questionablePausedThisRuntime && !manualResumeAuthorized)
        {
            SetPhase(HandoffPhase.AwaitingManualQuestionableResume);
            return;
        }

        if (!TryGetAutoRetainerState(out var autoRetainerBusy, out var multiModeActive))
        {
            Wait(now, "Waiting for AutoRetainer state IPC before resuming Questionable.");
            return;
        }

        if (autoRetainerBusy || multiModeActive)
        {
            Wait(now, "Waiting for AutoRetainer to become idle before resuming Questionable.");
            return;
        }

        if (!TryGetLifestreamBusy(out var lifestreamBusy))
        {
            Wait(now, "Waiting for Lifestream state IPC before resuming Questionable.");
            return;
        }

        if (lifestreamBusy)
        {
            Wait(now, "Waiting for Lifestream to become idle before resuming Questionable.");
            return;
        }

        if (!questionable.TryIsRunning(out var questionableRunning))
        {
            Wait(now, "Waiting for Questionable state IPC before resuming.");
            return;
        }

        if (questionableRunning)
        {
            var hasCurrentQuest = questionable.TryGetCurrentQuestId(out var currentQuestId, out var questAvailable);
            if (!questAvailable || !hasCurrentQuest || string.IsNullOrWhiteSpace(currentQuestId))
            {
                Wait(now, "Waiting for Questionable current-quest IPC.");
                return;
            }

            if (RecoveryPlanner.GetQuestDisposition(session.QuestId, currentQuestId) == RecoveryQuestDisposition.AlreadySatisfied)
                Complete();
            else
                SupersedeRecovery(currentQuestId, session.QuestId);
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval)
        {
            Wait(now, "Waiting before retrying Questionable resume.");
            return;
        }

        lastActionUtc = now;
        if (!questionable.TryStart(session.QuestId))
        {
            Fail($"Questionable rejected quest {session.QuestId}.");
            return;
        }

        SetPhase(HandoffPhase.WaitingForQuestionableResume);
    }

    private void UpdateWaitingForQuestionableResume(DateTime now)
    {
        if (HasTimedOut(now, TimeSpan.FromSeconds(30)))
        {
            Fail("Questionable did not resume within 30 seconds.");
            return;
        }

        var session = configuration.ActiveSession;
        if (session is null)
        {
            Fail("Recovery data is missing.");
            return;
        }

        if (!questionable.TryIsRunning(out var running))
        {
            Wait(now, "Waiting for Questionable state IPC after resume request.");
            return;
        }

        if (!running)
        {
            Wait(now, "Waiting for Questionable to start after resume request.");
            return;
        }

        if (!questionable.TryGetCurrentQuestId(out var currentQuestId, out var questAvailable) ||
            !questAvailable || string.IsNullOrWhiteSpace(currentQuestId))
        {
            Wait(now, "Waiting for Questionable current-quest IPC after resume request.");
            return;
        }

        if (RecoveryPlanner.GetQuestDisposition(session.QuestId, currentQuestId) == RecoveryQuestDisposition.AlreadySatisfied)
            Complete();
        else
            Complete(currentQuestId);
    }

    private bool CanAdvanceWithDependencies(DateTime now)
    {
        var gatePhase = Phase == HandoffPhase.RecoveringHandoff &&
                        configuration.ActiveSession is { RecoveryAction: RecoveryAction.WaitForActiveAutomation } oceanSession &&
                        IsOceanLeg(oceanSession)
            ? HandoffPhase.WaitingForOceanDuty : Phase;
        var required = RecoveryDependencyPolicy.RequiredFor(gatePhase);
        bool? autoBusy = null;
        bool? lifestreamBusy = null;
        if (required.AutoRetainer && TryGetAutoRetainerState(out var busy, out var multiMode))
            autoBusy = busy || multiMode;
        if (required.Lifestream && TryGetLifestreamBusy(out var streamBusy))
            lifestreamBusy = streamBusy;
        var questionableAvailable = !required.Questionable || questionable.TryIsRunning(out _);
        var waitReason = RecoveryDependencyPolicy.GetWaitReason(gatePhase, autoBusy, lifestreamBusy,
            questionableAvailable, clientState.IsLoggedIn && (playerState.ContentId == 0 || objectTable.LocalPlayer is null));
        if (waitReason is not null)
        {
            dependencyWaitClock.Update(waiting: true, now);
            activeDependencyGateFingerprint = $"{gatePhase}|{waitReason}";
            diagnosticLog.Write("dependency-gate", activeDependencyGateFingerprint,
                $"DependencyGateBlocked Session={SessionId()} Phase={Phase} GatePhase={gatePhase} AutoRetainer={autoBusy?.ToString() ?? "unavailable"} Lifestream={lifestreamBusy?.ToString() ?? "unavailable"} QuestionableAvailable={questionableAvailable} IdentityPending={clientState.IsLoggedIn && (playerState.ContentId == 0 || objectTable.LocalPlayer is null)} Reason={waitReason}.", now);
            SetMonitoring(MonitoringState.Recovering, waitReason, now);
            return false;
        }

        // Dependency waiting is not an attempted action and must not exhaust its timeout.
        var dependencyWaitElapsed = dependencyWaitClock.Update(waiting: false, now);
        phaseStartedUtc += dependencyWaitElapsed;
        if (activeDependencyGateFingerprint is { } previousGate)
        {
            diagnosticLog.Write("dependency-gate-release", previousGate,
                $"DependencyGateReleased Session={SessionId()} Phase={Phase} Gate={previousGate} BlockedSeconds={dependencyWaitElapsed.TotalSeconds:0.0} TimeoutBudgetAdjustedSeconds={dependencyWaitElapsed.TotalSeconds:0.0}.", now);
            activeDependencyGateFingerprint = null;
        }
        return true;
    }

    private void LogStartupRecoveryDecision(HandoffSession session, HandoffPhase savedPhase, HandoffPhase selectedPhase, string reason)
    {
        var contentId = clientState.IsLoggedIn && playerState.ContentId != 0 ? (ulong?)playerState.ContentId : null;
        log.Information(
            "Startup recovery decision. Session={Session} SavedPhase={SavedPhase} SelectedPhase={SelectedPhase} SessionAgeSeconds={SessionAgeSeconds:0.0} Leg={Leg} OceanDutyCompleted={OceanDutyCompleted} OceanLegCompleted={OceanLegCompleted} CurrentContentId={CurrentContentId} OnOriginal={OnOriginal} ResumeAuthorized={ResumeAuthorized} MonitoringEnabled={MonitoringEnabled} Reason={Reason}.",
            SessionId(), savedPhase, selectedPhase, SessionElapsedSeconds(DateTime.UtcNow), session.CurrentLeg,
            session.OceanDutyCompleted, session.OceanFishingLegCompleted, contentId?.ToString() ?? "unknown",
            contentId is not null && contentId == session.OriginalContentId,
            manualResumeAuthorized || questionablePausedThisRuntime, configuration.Enabled, reason);
    }

    private void TryInitializeRecovery()
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            recoveryPending = false;
            log.Warning("Recovery initialization found no active handoff session.");
            return;
        }

        EnsureCorrelationId(session);
        var onOriginalForStartup = clientState.IsLoggedIn && playerState.ContentId != 0 &&
                                   playerState.ContentId == session.OriginalContentId;
        var selectedPhase = StartupRecoveryPolicy.SelectPhase(session,
            manualResumeAuthorized || questionablePausedThisRuntime, onOriginalForStartup);
        if (selectedPhase is { } startupPhase)
        {
            recoveryPending = false;
            ResetOceanCleanupStability();
            var savedPhase = session.Phase;
            var reason = startupPhase is HandoffPhase.Error or HandoffPhase.ManualIntervention
                ? !string.IsNullOrWhiteSpace(session.RecoveryMessage) ? session.RecoveryMessage
                    : !string.IsNullOrWhiteSpace(session.LastError) ? session.LastError
                    : "Persisted recovery requires manual intervention."
                : startupPhase == HandoffPhase.AwaitingManualQuestionableResume
                    ? "Manual resume required; Questionable will not start automatically after reload."
                    : $"Recovered saved workflow stage: {MonitoringStatusFormatter.FormatPhase(startupPhase)}.";
            if (startupPhase == HandoffPhase.ManualIntervention &&
                savedPhase is not (HandoffPhase.ManualIntervention or HandoffPhase.Error))
                EnterManualIntervention("Persisted WahTools Stop request has no acceptance result. Verify Feesh stopped before returning.");
            else
                SetPhase(startupPhase, reason: reason);
            return;
        }

        if (IsOceanLeg(session))
        {
            TryInitializeOceanRecovery(session);
            return;
        }

        if (!TryGetAutoRetainerState(out var busy, out var multiModeActive))
        {
            SetMonitoring(MonitoringState.Recovering, "Waiting for AutoRetainer state IPC before recovery can continue.", DateTime.UtcNow);
            return;
        }

        recoveryPending = false;
        completionEventReceived = session.AutoRetainerCompletionReceived;
        autoRetainerMultiModeObserved = session.AutoRetainerMultiModeObserved;
        var onOriginal = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId;
        if (!busy && !multiModeActive && onOriginal && questionable.TryIsRunning(out var questionableRunning) && questionableRunning &&
            questionable.TryGetCurrentQuestId(out var currentQuestId, out var questAvailable) &&
            questAvailable && !string.IsNullOrWhiteSpace(currentQuestId))
        {
            var disposition = RecoveryPlanner.GetQuestDisposition(session.QuestId, currentQuestId);
            if (disposition == RecoveryQuestDisposition.AlreadySatisfied)
            {
                log.Information(
                    "Recovery already satisfied. Session={Session} ExpectedQuest={ExpectedQuest} CurrentQuest={CurrentQuest} Action=ClearRecovery.",
                    SessionId(),
                    session.QuestId,
                    currentQuestId);
                Complete(currentQuestId, alreadyRunning: true);
                return;
            }

            SupersedeRecovery(currentQuestId, session.QuestId);
            return;
        }

        var resumeAuthorized = manualResumeAuthorized || questionablePausedThisRuntime;
        var recoveryPhase = RecoveryPlanner.GetRecoveryPhase(
            busy,
            multiModeActive,
            onOriginal,
            resumeAuthorized,
            session.Phase,
            session.AutoRetainerStartRequested,
            session.AutoRetainerMultiModeObserved,
            session.AutoRetainerCompletionReceived);
        var recoveryReason = recoveryPhase == HandoffPhase.AwaitingManualQuestionableResume
                                 ? "Manual resume required; Questionable will not start automatically after reload."
                                 : $"Recovery selected {MonitoringStatusFormatter.FormatPhase(recoveryPhase)}.";
        log.Information(
            "Recovery decision. Session={Session} Phase={Phase} AutoRetainerBusy={AutoRetainerBusy} MultiModeActive={MultiModeActive} StartRequested={StartRequested} MultiModeObserved={MultiModeObserved} CompletionReceived={CompletionReceived} OnOriginal={OnOriginal} ResumeAuthorized={ResumeAuthorized} Reason={Reason}.",
            SessionId(),
            recoveryPhase,
            busy,
            multiModeActive,
            session.AutoRetainerStartRequested,
            session.AutoRetainerMultiModeObserved,
            session.AutoRetainerCompletionReceived,
            onOriginal,
            resumeAuthorized,
            recoveryReason);
        SetPhase(recoveryPhase, reason: recoveryReason);
        Notify("Recovered an interrupted Questionable Retainer handoff.");
        if (!resumeAuthorized)
        {
            log.Information(
                "Persisted handoff recovered. Questionable will not be started automatically; manual resume required. Session={Session} Quest={QuestId}.",
                SessionId(),
                session.QuestId);
        }
    }

    private void TryInitializeOceanRecovery(HandoffSession session)
    {
        recoveryPending = false;
        ResetOceanCleanupStability();
        if (IsOceanFishingDutyActive())
        {
            session.OceanDutyStarted = true;
            session.OceanTerritoryEntered = true;
            wahToolsSessionObservedThisRuntime = true;
            ReleaseYesAlreadyLease(session);
            save();
            SetPhase(session.OceanDutyCompleted ? HandoffPhase.WaitingForOceanCleanup : HandoffPhase.WaitingForOceanDuty,
                reason: "Ocean Fishing duty is active; observing until safe exit.");
            Notify("Recovered an interrupted Ocean Fishing handoff; voyage remains active.");
            return;
        }

        if (session.OceanDutyCompleted)
        {
            session.OceanTerritoryExited = true;
            save();
            SetPhase(
                HandoffPhase.WaitingForOceanCleanup,
                reason: "Recovered completed Ocean Fishing voyage; confirming safe cleanup state.");
            return;
        }

        if (session.WahToolsLaunchAmbiguous)
        {
            EnterManualIntervention("WahTools launch outcome is ambiguous and no voyage entry was observed. Confirm Feesh stopped before returning to another character.");
            return;
        }

        if (session.WahToolsStopRequested && !session.OceanDutyCompleted)
        {
            EnterManualIntervention("WahTools stop outcome was interrupted before confirmation. Confirm Feesh stopped before returning to another character.");
            return;
        }

        session.OceanFishingLegSkipped = true;
        if (session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        save();
        SetPhase(HandoffPhase.ReturningOriginal, reason: "Reload interrupted Ocean Fishing before completion; restoring original character without stopping unowned Feesh work.");
        log.Information(
            "Ocean Fishing recovery initialized. Session={Session} DutyActive=false LaunchAccepted={LaunchAccepted}.",
            SessionId(), session.WahToolsLaunchAccepted);
        Notify("Recovered an interrupted Ocean Fishing handoff; restoring original character.");
    }

    private void RefreshLiveDiagnostics(DateTime now)
    {
        var playerBlockers = CapturePlayerBlockers();
        monitoring.PlayerLoggedIn = clientState.IsLoggedIn;
        monitoring.PlayerContentIdAvailable = playerState.ContentId != 0;
        monitoring.CurrentContentId = clientState.IsLoggedIn && playerState.ContentId != 0
                                          ? playerState.ContentId
                                          : null;
        monitoring.PlayerBlockers = playerBlockers;

        var questionableAvailable = questionable.TryIsRunning(out var questionableRunning);
        monitoring.QuestionableAvailable = questionableAvailable;
        monitoring.QuestionableRunning = questionableAvailable ? questionableRunning : null;
        monitoring.QuestionableQuestId = string.Empty;
        var questAvailable = true;
        if (questionableAvailable && questionableRunning)
        {
            if (questionable.TryGetCurrentQuestId(out var questId, out questAvailable))
                monitoring.QuestionableQuestId = questId;
            if (!questAvailable)
                monitoring.QuestionableAvailable = false;
        }

        TryGetAutoRetainerState(out _, out _);
        TryGetLifestreamBusy(out _);
        monitoring.YesAlreadyLeaseAcquired = yesAlreadyLease.IsAcquired;

        var oceanState = OceanFishingSchedule.GetState(
            now,
            TimeSpan.FromMinutes(Math.Clamp(configuration.OceanFishingLeadMinutes, 0, 30)));
        monitoring.NextOceanWindowUtc = oceanState.Window.StartUtc;
        monitoring.OceanWindowHandled = OceanFishingSchedule.IsWindowHandled(
            configuration.LastHandledOceanFishingWindowUtc,
            oceanState.Window.StartUtc);
        monitoring.OceanLaunchEligible = configuration.MonitorOceanFishing && oceanState.LaunchEligible && !monitoring.OceanWindowHandled;
        monitoring.ActiveHandoffKind = configuration.ActiveSession?.Kind;
        var session = configuration.ActiveSession;
        monitoring.FishingCharacterContentId = session is { FishingContentId: > 0 }
                                                   ? session.FishingContentId
                                                   : null;
        monitoring.CurrentHandoffLeg = session?.CurrentLeg;
        monitoring.OceanLegResolved = session?.OceanFishingLegCompleted == true || session?.OceanFishingLegSkipped == true;
        monitoring.AutoRetainerLegResolved = session?.AutoRetainerLegCompleted == true || session?.AutoRetainerLegSkipped == true;
        monitoring.PendingAutoRetainerMode = session?.PendingAutoRetainerMode;
        monitoring.WahToolsLaunchAccepted = session?.WahToolsLaunchAccepted == true;
        monitoring.OceanDutyStarted = session is not null && IsOceanLeg(session) && session.OceanDutyStarted;
        monitoring.OceanDutyCompleted = session is not null && IsOceanLeg(session) && session.OceanDutyCompleted;
        monitoring.LastDependencyCheckUtc = now;
        lastDependencyRefreshUtc = now;
    }

    private void RefreshWahToolsStatus(DateTime now)
    {
        var session = configuration.ActiveSession;
        monitoring.CurrentContentId = clientState.IsLoggedIn && playerState.ContentId != 0
                                          ? playerState.ContentId
                                          : null;
        monitoring.FishingCharacterContentId = session is { FishingContentId: > 0 }
                                                   ? session.FishingContentId
                                                   : null;
        var shouldObserve = session?.OceanWindowStartUtc is not null &&
                            (IsOceanLeg(session) || Phase is HandoffPhase.WaitingForWahToolsStop or HandoffPhase.ManualIntervention);
        if (!shouldObserve)
        {
            monitoring.WahToolsStatus = null;
            monitoring.WahToolsAvailable = null;
            monitoring.WahToolsLaunchAccepted = false;
            monitoring.WahToolsStatusEvaluationUtc = null;
            monitoring.WahToolsStatusAgeSeconds = null;
            return;
        }

        if (now < nextWahToolsStatusRefreshUtc)
            return;

        var status = wahToolsStatusReader.Read();
        var statusObservedEvaluationUtc = DateTime.UtcNow;
        nextWahToolsStatusRefreshUtc = statusObservedEvaluationUtc + TimeSpan.FromSeconds(1);
        monitoring.WahToolsStatus = status;
        monitoring.WahToolsAvailable = status.IsAvailable;
        monitoring.WahToolsStatusEvaluationUtc = statusObservedEvaluationUtc;
        monitoring.WahToolsStatusAgeSeconds = (statusObservedEvaluationUtc - status.ObservedAtUtc).TotalSeconds;
        var snapshotFingerprint = status.IsAvailable
            ? string.Join('|', SessionId(), status.Generation, status.TripInitialized, status.TripState, status.TripStatus,
                status.TripRunning, status.OnBoat, status.Cleaning, status.CleanerState, status.CleanerStatus,
                status.CleanerRunning, status.DesyntherRunning, status.DesyntherStatus,
                status.DiscarderRunning, status.DiscarderStatus, status.BuyerBusy)
            : $"{SessionId()}|{status.Availability}|{status.Detail}";
        diagnosticLog.Write("feesh-snapshot", snapshotFingerprint,
            $"WahToolsSnapshot Session={SessionId()} Phase={Phase} Available={status.IsAvailable} Generation={status.Generation} ObservedUtc={status.ObservedAtUtc:O} EvaluationUtc={statusObservedEvaluationUtc:O} RawAgeSeconds={(statusObservedEvaluationUtc - status.ObservedAtUtc).TotalSeconds:0.000} Detail={status.Detail} TripInitialized={status.TripInitialized} TripState={status.TripState} TripStatus={status.TripStatus} TripRunning={status.TripRunning} OnBoat={status.OnBoat} Cleaning={status.Cleaning} CleanerState={status.CleanerState} CleanerStatus={status.CleanerStatus} CleanerRunning={status.CleanerRunning} DesyntherRunning={status.DesyntherRunning} DesyntherStatus={status.DesyntherStatus} DiscarderRunning={status.DiscarderRunning} DiscarderStatus={status.DiscarderStatus} BuyerBusy={status.BuyerBusy}.", statusObservedEvaluationUtc);
    }

    private OceanCleanupConfirmationSource SelectCleanupConfirmationSource(
        DateTime? requireObservedAfterUtc,
        out DateTime evaluatedAtUtc)
    {
        evaluatedAtUtc = DateTime.UtcNow;
        monitoring.WahToolsStatusEvaluationUtc = evaluatedAtUtc;
        monitoring.WahToolsStatusAgeSeconds = monitoring.WahToolsStatus is { } status
                                                  ? (evaluatedAtUtc - status.ObservedAtUtc).TotalSeconds
                                                  : null;
        var sourceReason = OceanCleanupConfirmationPolicy.GetSourceReason(
            monitoring.WahToolsStatus, evaluatedAtUtc, requireObservedAfterUtc);
        var snapshotAge = monitoring.WahToolsStatus is { } snapshot
            ? (evaluatedAtUtc - snapshot.ObservedAtUtc).TotalSeconds
            : (double?)null;
        diagnosticLog.Write("cleanup-source", $"{SessionId()}|{sourceReason}|{monitoring.WahToolsStatus?.Generation}",
            $"CleanupSourceDecision Session={SessionId()} Phase={Phase} Reason={sourceReason} Source={OceanCleanupConfirmationPolicy.SelectSource(monitoring.WahToolsStatus, evaluatedAtUtc, requireObservedAfterUtc)} Generation={monitoring.WahToolsStatus?.Generation.ToString() ?? "none"} SnapshotUtc={monitoring.WahToolsStatus?.ObservedAtUtc.ToString("O") ?? "none"} EvaluationUtc={evaluatedAtUtc:O} RawAgeSeconds={snapshotAge?.ToString("0.000") ?? "none"} RequireObservedAfterUtc={requireObservedAfterUtc?.ToString("O") ?? "none"}.", evaluatedAtUtc);
        return OceanCleanupConfirmationPolicy.SelectSource(
            monitoring.WahToolsStatus,
            evaluatedAtUtc,
            requireObservedAfterUtc);
    }

    private IReadOnlyList<string> GetPlayerBlockers()
    {
        var blockers = new List<string>();
        if (!clientState.IsLoggedIn)
            blockers.Add("NotLoggedIn");
        if (playerState.ContentId == 0)
            blockers.Add("MissingContentId");

        AddConditionBlocker(blockers, ConditionFlag.BoundByDuty);
        AddConditionBlocker(blockers, ConditionFlag.BoundByDuty56);
        AddConditionBlocker(blockers, ConditionFlag.BoundByDuty95);
        AddConditionBlocker(blockers, ConditionFlag.InDutyQueue);
        AddConditionBlocker(blockers, ConditionFlag.InCombat);
        AddConditionBlocker(blockers, ConditionFlag.Occupied);
        AddConditionBlocker(blockers, ConditionFlag.Occupied30);
        AddConditionBlocker(blockers, ConditionFlag.Occupied33);
        AddConditionBlocker(blockers, ConditionFlag.Occupied38);
        AddConditionBlocker(blockers, ConditionFlag.Occupied39);
        AddConditionBlocker(blockers, ConditionFlag.OccupiedInEvent);
        AddConditionBlocker(blockers, ConditionFlag.OccupiedSummoningBell);
        AddConditionBlocker(blockers, ConditionFlag.OccupiedInQuestEvent);
        AddConditionBlocker(blockers, ConditionFlag.OccupiedInCutSceneEvent);
        AddConditionBlocker(blockers, ConditionFlag.WatchingCutscene);
        AddConditionBlocker(blockers, ConditionFlag.WatchingCutscene78);
        AddConditionBlocker(blockers, ConditionFlag.BetweenAreas);
        AddConditionBlocker(blockers, ConditionFlag.BetweenAreas51);
        AddConditionBlocker(blockers, ConditionFlag.LoggingOut);
        return blockers;
    }

    private IReadOnlyList<string> CapturePlayerBlockers()
    {
        var blockers = GetPlayerBlockers();
        monitoring.PlayerLoggedIn = clientState.IsLoggedIn;
        monitoring.PlayerContentIdAvailable = playerState.ContentId != 0;
        monitoring.PlayerBlockers = blockers;
        return blockers;
    }

    private bool TryGetAutoRetainerState(out bool busy, out bool multiModeActive)
    {
        var available = autoRetainer.TryGetState(out busy, out multiModeActive);
        monitoring.AutoRetainerAvailable = available;
        monitoring.AutoRetainerBusy = available && busy;
        monitoring.AutoRetainerMultiModeActive = available && multiModeActive;
        return available;
    }

    private bool TryGetLifestreamBusy(out bool busy)
    {
        var available = lifestream.TryIsBusy(out busy);
        monitoring.LifestreamAvailable = available;
        monitoring.LifestreamBusy = available && busy;
        return available;
    }

    private bool IsOceanFishingDutyActive() =>
        IsOceanFishingTerritory() &&
        (dutyState.IsDutyStarted || condition[ConditionFlag.BoundByDuty]);

    private static bool IsOceanLeg(HandoffSession? session) =>
        session is not null &&
        (session.CurrentLeg == HandoffLeg.OceanFishing ||
         (session.Kind == HandoffKind.OceanFishing && !session.OceanFishingLegCompleted && !session.OceanFishingLegSkipped));

    private bool IsOceanFishingTerritory() =>
        clientState.TerritoryType is OceanFishingTerritoryOne or OceanFishingTerritoryTwo;

    private OceanFishingCleanupSnapshot CaptureOceanCleanupSnapshot()
    {
        var blockers = CapturePlayerBlockers();
        var player = objectTable.LocalPlayer;
        var resultAddon = gameGui.GetAddonByName("IKDResult");
        var autoRetainerAvailable = TryGetAutoRetainerState(out var autoRetainerBusy, out var multiModeActive);
        var lifestreamAvailable = TryGetLifestreamBusy(out var lifestreamBusy);
        return new(
            configuration.ActiveSession?.OceanDutyCompleted == true,
            IsOceanFishingTerritory(),
            !resultAddon.IsNull && resultAddon.IsVisible,
            condition[ConditionFlag.NormalConditions],
            blockers.Count > 0,
            condition[ConditionFlag.Fishing],
            condition[ConditionFlag.ExecutingGatheringAction],
            condition[ConditionFlag.Casting],
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            clientState.IsLoggedIn && playerState.ContentId != 0 && player is not null,
            lifestreamAvailable,
            lifestreamBusy,
            autoRetainerAvailable,
            autoRetainerBusy,
            multiModeActive);
    }

    private void UpdateWaitingForWahToolsStop(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null ||
            !OceanFishingPolicy.HasWahToolsStopSession(session) ||
            session.WahToolsStopAcceptedUtc is not { } stopAcceptedUtc)
        {
            EnterManualIntervention("WahTools Stop acceptance is unknown; verify Feesh stopped before returning.");
            return;
        }

        var source = SelectCleanupConfirmationSource(stopAcceptedUtc, out var confirmationNowUtc);
        if (source == OceanCleanupConfirmationSource.None)
        {
            monitoring.OceanCleanupWaitReason = "awaiting fresh WahTools status observed after Stop";
            ResetOceanCleanupStability(monitoring.OceanCleanupWaitReason);
            Wait(now, "WahTools Stop accepted; awaiting fresh post-stop snapshot.");
            return;
        }

        if (source == OceanCleanupConfirmationSource.WahToolsReflection &&
            monitoring.WahToolsStatus is { } freshStatus &&
            session.WahToolsStopStatusGeneration != freshStatus.Generation)
        {
            log.Information(
                "WahTools instance generation changed after Stop. Session={Session} Before={Before} After={After}; restarting cleanup confirmation.",
                SessionId(),
                session.WahToolsStopStatusGeneration?.ToString() ?? "unknown",
                freshStatus.Generation);
            session.WahToolsStopStatusGeneration = freshStatus.Generation;
            save();
        }

        if (source == OceanCleanupConfirmationSource.WahToolsReflection && monitoring.WahToolsStatus is { } feeshStatus)
        {
            if (WahToolsFeeshStatusPolicy.IsTripFailed(feeshStatus))
            {
                EnterManualIntervention($"WahTools trip failed after Stop: {feeshStatus.TripStatus ?? "no failure detail"}");
                return;
            }

            if (WahToolsFeeshStatusPolicy.HasCleanerFailure(feeshStatus))
            {
                EnterManualIntervention($"WahTools cleaner failed after Stop: {feeshStatus.CleanerStatus ?? "no failure detail"}");
                return;
            }
        }

        if (!CanAccumulateSafeFishingReturn(confirmationNowUtc, source))
        {
            Wait(now, $"Confirming {GetConfirmationSourceLabel(source)} post-stop safe state.");
            return;
        }

        if (session.OceanFishingLegSkipped)
        {
            session.CurrentLeg = HandoffLeg.None;
            session.OceanTerritoryExited = !IsOceanFishingTerritory();
            ReleaseYesAlreadyLease(session);
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Failed Ocean Fishing attempt stopped and confirmed; restoring original character.");
            return;
        }

        CompleteOceanFishingLeg(session, "Ocean Fishing completed and WahTools Stop was observed idle.");
    }

    private void CompleteOceanFishingLeg(HandoffSession session, string reason)
    {
        session.OceanFishingLegCompleted = true;
        session.CurrentLeg = HandoffLeg.None;
        session.ReturningForAutoRetainer = false;
        if (session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        ReleaseYesAlreadyLease(session);
        ResetOceanCleanupDiagnostics();
        save();
        log.Information("Ocean Fishing leg complete; checking chained work. Session={Session}.", SessionId());
        SetPhase(HandoffPhase.CheckingChainedWork, reason: reason);
    }

    private void ResetOceanCleanupDiagnostics()
    {
        ResetOceanCleanupStability();
        monitoring.OceanCleanupWaitReason = string.Empty;
        monitoring.WahToolsStatus = null;
        monitoring.WahToolsAvailable = null;
        monitoring.WahToolsLaunchAccepted = false;
        monitoring.WahToolsStatusEvaluationUtc = null;
        monitoring.WahToolsStatusAgeSeconds = null;
    }

    private bool CanAccumulateSafeFishingReturn(DateTime now, OceanCleanupConfirmationSource source)
    {
        var player = objectTable.LocalPlayer;
        var blockers = CapturePlayerBlockers();
        var reasons = GetSafeFishingReturnBlockers(player, blockers, source);

        if (reasons.Count > 0)
        {
            monitoring.OceanCleanupWaitReason = string.Join("; ", reasons);
            ResetOceanCleanupStability(monitoring.OceanCleanupWaitReason);
            return false;
        }

        if (source == OceanCleanupConfirmationSource.GameStateFallback)
            monitoring.OceanCleanupWaitReason = $"WahTools status unavailable or stale ({monitoring.WahToolsStatus?.Detail ?? "not read"}); using game-state fallback";

        return UpdateCleanupConfirmation(now, player!.Position, playerState.ContentId, clientState.TerritoryType, source);
    }

    private List<string> GetSafeFishingReturnBlockers(
        object? player,
        IReadOnlyList<string> blockers,
        OceanCleanupConfirmationSource source)
    {
        var reasons = new List<string>();
        if (IsOceanFishingTerritory())
            reasons.Add("still in Ocean Fishing territory");
        if (player is null)
            reasons.Add("local player unavailable");
        if (blockers.Count > 0)
            reasons.Add($"player blockers: {string.Join(',', blockers)}");
        if (!condition[ConditionFlag.NormalConditions])
            reasons.Add("player is not in normal conditions");
        if (condition[ConditionFlag.Fishing])
            reasons.Add("fishing action active");
        if (condition[ConditionFlag.ExecutingGatheringAction])
            reasons.Add("gathering action active");
        if (condition[ConditionFlag.Casting])
            reasons.Add("casting active");
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            reasons.Add("player changing areas");

        if (!TryGetAutoRetainerState(out var autoBusy, out var multiModeActive))
            reasons.Add("AutoRetainer state unavailable");
        else
        {
            if (autoBusy)
                reasons.Add("AutoRetainer busy");
            if (multiModeActive)
                reasons.Add($"AutoRetainer Multi Mode active (Busy={autoBusy})");
        }

        if (!TryGetLifestreamBusy(out var lifestreamBusy))
            reasons.Add("Lifestream state unavailable");
        else if (lifestreamBusy)
            reasons.Add("Lifestream busy");

        if (source == OceanCleanupConfirmationSource.WahToolsReflection && monitoring.WahToolsStatus is { } status)
            reasons.AddRange(WahToolsFeeshStatusPolicy.GetCleanupBlockers(status));
        return reasons;
    }

    private bool UpdateCleanupConfirmation(DateTime now, Vector3 position, ulong contentId, uint territory, OceanCleanupConfirmationSource source)
    {
        var generation = source == OceanCleanupConfirmationSource.WahToolsReflection
                             ? monitoring.WahToolsStatus?.Generation
                             : null;
        var sourceChanged = !OceanCleanupConfirmationPolicy.IsSameEvidence(
            source,
            generation,
            monitoring.OceanCleanupConfirmationSource,
            oceanCleanupConfirmationGeneration);
        var identityOrPositionChanged = oceanCleanupStableSinceUtc is not null &&
                                        (oceanCleanupStableTerritory != territory ||
                                         oceanCleanupStableContentId != contentId ||
                                         oceanCleanupStablePosition is not { } currentStablePosition ||
                                         Vector3.Distance(position, currentStablePosition) > OceanPositionTolerance);
        if ((sourceChanged || identityOrPositionChanged) && oceanCleanupStableSinceUtc is not null)
        {
            var distance = oceanCleanupStablePosition is { } oldPosition ? Vector3.Distance(position, oldPosition) : 0;
            var resetFingerprint = $"{source}|{generation}|{territory}|{contentId}|{identityOrPositionChanged}";
            diagnosticLog.Write("cleanup-reset", resetFingerprint,
                $"CleanupConfirmationReset Session={SessionId()} SourceChanged={sourceChanged} PreviousSource={monitoring.OceanCleanupConfirmationSource} CurrentSource={source} PreviousGeneration={oceanCleanupConfirmationGeneration?.ToString() ?? "none"} CurrentGeneration={generation?.ToString() ?? "none"} IdentityOrTerritoryChanged={identityOrPositionChanged} PreviousTerritory={oceanCleanupStableTerritory} CurrentTerritory={territory} PreviousContentId={oceanCleanupStableContentId} CurrentContentId={contentId} MovementDistance={distance:0.000} MovementTolerance={OceanPositionTolerance:0.000} PreviousElapsedSeconds={(now - oceanCleanupStableSinceUtc.Value).TotalSeconds:0.0}.", now);
        }

        if (sourceChanged ||
            identityOrPositionChanged ||
            oceanCleanupStableSinceUtc is null ||
            oceanCleanupStableTerritory != territory ||
            oceanCleanupStableContentId != contentId ||
            oceanCleanupStablePosition is not { } stablePosition ||
            Vector3.Distance(position, stablePosition) > OceanPositionTolerance)
        {
            oceanCleanupStableSinceUtc = now;
            oceanCleanupStablePosition = position;
            oceanCleanupStableTerritory = territory;
            oceanCleanupStableContentId = contentId;
            oceanCleanupConfirmationGeneration = generation;
            monitoring.OceanCleanupConfirmationSource = source;
            monitoring.OceanCleanupStableSinceUtc = now;
            monitoring.OceanCleanupStableElapsedSeconds = 0;
            monitoring.OceanCleanupRequiredSeconds = OceanCleanupConfirmationPolicy.GetRequiredDuration(source).TotalSeconds;
            monitoring.OceanCleanupWaitReason = $"{GetConfirmationSourceLabel(source)} confirmation active";
            diagnosticLog.Write("cleanup-confirmation", $"started|{source}|{generation}|{territory}|{contentId}",
                $"CleanupConfirmationStarted Session={SessionId()} Source={source} Generation={generation?.ToString() ?? "none"} StableSinceUtc={now:O} RequiredSeconds={monitoring.OceanCleanupRequiredSeconds:0.0} Territory={territory} ContentId={contentId} Position={position}.", now);
            return false;
        }

        monitoring.OceanCleanupStableSinceUtc = oceanCleanupStableSinceUtc;
        monitoring.OceanCleanupStableElapsedSeconds = (now - oceanCleanupStableSinceUtc.Value).TotalSeconds;
        monitoring.OceanCleanupRequiredSeconds = OceanCleanupConfirmationPolicy.GetRequiredDuration(source).TotalSeconds;
        var confirmed = OceanCleanupConfirmationPolicy.HasConfirmedStableTime(oceanCleanupStableSinceUtc.Value, now, source);
        monitoring.OceanCleanupWaitReason = confirmed
            ? $"{GetConfirmationSourceLabel(source)} confirmation complete"
            : $"{GetConfirmationSourceLabel(source)} confirmation in progress";
        diagnosticLog.Write("cleanup-confirmation", confirmed ? $"complete|{source}|{generation}" : $"progress|{source}|{generation}",
            $"CleanupConfirmation{(confirmed ? "Completed" : "Progress")} Session={SessionId()} Source={source} Generation={generation?.ToString() ?? "none"} ElapsedSeconds={monitoring.OceanCleanupStableElapsedSeconds:0.0} RequiredSeconds={monitoring.OceanCleanupRequiredSeconds:0.0}.", now);
        return confirmed;
    }

    private static string GetConfirmationSourceLabel(OceanCleanupConfirmationSource source) => source switch
    {
        OceanCleanupConfirmationSource.WahToolsReflection => "WahTools reflected idle",
        OceanCleanupConfirmationSource.GameStateFallback => "game-state fallback",
        _ => "status unknown",
    };

    private void ResetOceanCleanupStability(string? reason = null)
    {
        if (oceanCleanupStableSinceUtc is { } stableSince && reason is not null)
        {
            diagnosticLog.Write("cleanup-blocker-reset", reason,
                $"CleanupConfirmationResetByBlocker Session={SessionId()} Reason={reason} PreviousElapsedSeconds={Math.Max(0, (DateTime.UtcNow - stableSince).TotalSeconds):0.0}.", DateTime.UtcNow);
        }

        nextOceanCleanupPollUtc = DateTime.MinValue;
        oceanCleanupStableSinceUtc = null;
        oceanCleanupStablePosition = null;
        oceanCleanupStableTerritory = 0;
        oceanCleanupStableContentId = 0;
        oceanCleanupConfirmationGeneration = null;
        monitoring.OceanCleanupStableSinceUtc = null;
        monitoring.OceanCleanupStableElapsedSeconds = null;
        monitoring.OceanCleanupRequiredSeconds = null;
        monitoring.OceanCleanupConfirmationSource = OceanCleanupConfirmationSource.None;
    }

    private void AddConditionBlocker(ICollection<string> blockers, ConditionFlag flag)
    {
        if (condition[flag])
            blockers.Add(flag.ToString());
    }

    private bool IsSafeForHandoff() => GetPlayerBlockers().Count == 0;

    private bool HasTimedOut(DateTime now, TimeSpan timeout) => now >= phaseStartedUtc + timeout;

    private void OnSingleMultiModeFinished() =>
        DispatchToFrameworkThread(HandleSingleMultiModeFinished, "AutoRetainer completion callback");

    private void HandleSingleMultiModeFinished()
    {
        if (configuration.ActiveSession is not { CurrentLeg: HandoffLeg.AutoRetainer, AutoRetainerStartRequested: true })
        {
            log.Debug("Ignoring AutoRetainer completion callback without a matching active QR dispatch.");
            return;
        }

        completionEventReceived = true;
        log.Information(
            "AutoRetainer single-pass completion event received. Session={Session} Phase={Phase} StartRequested={StartRequested} MultiModeObserved={MultiModeObserved}.",
            SessionId(),
            Phase,
            configuration.ActiveSession?.AutoRetainerStartRequested ?? false,
            autoRetainerMultiModeObserved);
    }

    private void OnDutyStarted(IDutyStateEventArgs args) =>
        DispatchToFrameworkThread(HandleDutyStarted, "duty-start callback");

    private void HandleDutyStarted()
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session) || !session.WahToolsLaunchRequested || !IsOceanFishingTerritory())
            return;

        if (session.OceanDutyStarted)
            return;

        session.OceanTerritoryEntered = true;
        session.OceanDutyStarted = true;
        wahToolsSessionObservedThisRuntime = true;
        ReleaseYesAlreadyLease(session);
        save();
        log.Information("Ocean Fishing duty-start event received. Session={Session}.", SessionId());
    }

    private void OnDutyCompleted(IDutyStateEventArgs args) =>
        DispatchToFrameworkThread(HandleDutyCompleted, "duty-completed callback");

    private void HandleDutyCompleted()
    {
        var session = configuration.ActiveSession;
        if (session is null || !IsOceanLeg(session) || !session.OceanDutyStarted || session.OceanDutyCompleted)
            return;

        session.OceanDutyCompleted = true;
        save();
        log.Information("Ocean Fishing duty-completed event received. Session={Session}.", SessionId());
    }

    private void MarkAutoRetainerMultiModeObserved(HandoffSession session)
    {
        autoRetainerMultiModeObserved = true;
        if (session.AutoRetainerMultiModeObserved)
            return;

        session.AutoRetainerMultiModeObserved = true;
        save();
        log.Information("AutoRetainer Multi Mode activity observed. Session={Session}.", SessionId());
    }

    private void MarkAutoRetainerCompletionReceived(HandoffSession session)
    {
        completionEventReceived = true;
        if (session.AutoRetainerCompletionReceived)
            return;

        session.AutoRetainerCompletionReceived = true;
        save();
        log.Information("AutoRetainer completion persisted. Session={Session}.", SessionId());
    }

    private void SetPhase(HandoffPhase phase, bool persist = true, string? reason = null)
    {
        var now = DateTime.UtcNow;
        var previousPhase = Phase;
        var effectiveReason = reason ?? (phase is HandoffPhase.Error or HandoffPhase.ManualIntervention
            ? monitoring.Reason
            : phase == HandoffPhase.AwaitingManualQuestionableResume
                ? "Manual resume required; Questionable will not start automatically after reload."
                : phase == HandoffPhase.RecoveringHandoff ? "Automatic recovery in progress."
                : $"Handoff phase: {MonitoringStatusFormatter.FormatPhase(phase)}.");
        var phaseElapsed = now - phaseStartedUtc;
        if (evaluatingStartupRecovery && configuration.ActiveSession is { } recoveredSession)
            LogStartupRecoveryDecision(recoveredSession, previousPhase, phase, effectiveReason);
        if (previousPhase != phase)
        {
            log.Information(
                "Handoff phase changed. Session={Session} From={From} To={To} PreviousPhaseElapsedSeconds={PreviousPhaseElapsedSeconds:0.0} SessionElapsedSeconds={SessionElapsedSeconds:0.0} Reason={Reason}.",
                SessionId(),
                previousPhase,
                phase,
                phaseElapsed.TotalSeconds,
                SessionElapsedSeconds(now),
                effectiveReason);
        }

        Phase = phase;
        if (previousPhase != phase)
        {
            phaseStartedUtc = now;
            dependencyWaitClock.Reset();
        }
        if (configuration.ActiveSession is not null)
            configuration.ActiveSession.Phase = phase;
        if (persist)
            save();
        if (phase is HandoffPhase.Error or HandoffPhase.ManualIntervention)
            SetMonitoring(MonitoringState.Error, effectiveReason, now);
        else if (phase == HandoffPhase.RecoveringHandoff)
        {
            RefreshLiveDiagnostics(now);
            SetMonitoring(MonitoringState.Recovering, effectiveReason, now);
        }
        else if (phase == HandoffPhase.AwaitingManualQuestionableResume)
        {
            SetMonitoring(
                MonitoringState.Recovering,
                effectiveReason,
                now);
        }
        else if (phase != HandoffPhase.Idle)
        {
            RefreshLiveDiagnostics(now);
            SetMonitoring(MonitoringState.Processing, effectiveReason, now);
        }
    }

    private HandoffPhase GetResumePhase() =>
        questionablePausedThisRuntime || manualResumeAuthorized
            ? HandoffPhase.ResumingQuestionable
            : HandoffPhase.AwaitingManualQuestionableResume;

    private void Complete()
    {
        Complete(null, alreadyRunning: false);
    }

    private void Complete(string? actualQuestId, bool alreadyRunning = false)
    {
        var session = configuration.ActiveSession;
        var questId = session?.QuestId ?? monitoring.QuestionableQuestId;
        if (!string.IsNullOrWhiteSpace(actualQuestId) && actualQuestId != questId)
        {
            log.Information(
                "Questionable Automatic mode advanced from requested quest {RequestedQuestId} to quest {ActualQuestId}; accepting progression.",
                questId,
                actualQuestId);
        }

        log.Information(
            alreadyRunning
                ? "Handoff recovery already satisfied. Questionable is running quest {QuestId}. Session={Session}."
                : "Handoff complete. Returned to original character and resumed Questionable quest {QuestId}. Session={Session}.",
            actualQuestId ?? questId,
            SessionId());
        Notify(
            alreadyRunning
                ? "Recovery complete. Questionable is already running."
                : session?.OceanFishingLegCompleted == true
                    ? "Ocean Fishing complete. Questionable resumed."
                    : "AutoRetainer pass complete. Questionable resumed.");
        if (session is not null)
        {
            ReleaseYesAlreadyLease(session);
            if (session.OceanWindowStartUtc is { } windowStart)
                configuration.LastHandledOceanFishingWindowUtc = windowStart;
        }
        configuration.ActiveSession = null;
        configuration.ManualRecoveryMessage = string.Empty;
        diagnosticLog.Reset();
        ResetOceanCleanupStability();
        ResetReadinessDiagnostics();
        monitoring.PlayerBlockers = [];
        lastActionUtc = DateTime.MinValue;
        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.WaitingForReadyWork, "Handoff complete; readiness rescan pending.", DateTime.UtcNow);
        save();
    }

    private void SupersedeRecovery(string currentQuestId, string expectedQuestId)
    {
        var completionKnown = questionable.TryIsQuestComplete(expectedQuestId, out var complete, out var completionAvailable);
        var reason = completionKnown && complete
                         ? $"Saved quest {expectedQuestId} is complete; Questionable is already running quest {currentQuestId}."
                         : $"Questionable is already running quest {currentQuestId}; stale recovery for quest {expectedQuestId} was cleared.";
        if (!completionAvailable)
            reason += " Completion state unavailable.";

        log.Warning("{Reason} Session={Session}.", reason, SessionId());
        Notify(reason, NotificationType.Warning);
        if (configuration.ActiveSession is { } staleSession)
            ReleaseYesAlreadyLease(staleSession);
        configuration.ActiveSession = null;
        configuration.ManualRecoveryMessage = string.Empty;
        ResetReadinessDiagnostics();
        lastActionUtc = DateTime.MinValue;
        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.WaitingForReadyWork, "Recovery superseded; readiness rescan pending.", DateTime.UtcNow);
        save();
    }

    private void UpdateRecoveringHandoff(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            EnterManualIntervention("Recovery data disappeared while automatic recovery was active.");
            return;
        }

        if (session.RecoveryAction == RecoveryAction.ManualIntervention)
        {
            EnterManualIntervention(session.RecoveryMessage);
            return;
        }

        if (session.RecoveryNextAttemptUtc is { } nextAttempt && now < nextAttempt)
        {
            Wait(now, $"Automatic recovery waiting before retry {session.RecoveryAttemptCount + 1}.");
            return;
        }

        session.RecoveryAttemptCount++;
        session.RecoveryLastAttemptUtc = now;
        session.RecoveryNextAttemptUtc = now + RecoveryBackoffPolicy.GetDelay(session.RecoveryAttemptCount);
        save();
        diagnosticLog.Write("recovery-attempt", $"{session.RecoveryAction}|{session.RecoveryAttemptCount}",
            $"RecoveryAttempt Session={SessionId()} Phase={Phase} Leg={session.CurrentLeg} Operation={session.RecoveryAction} Attempt={session.RecoveryAttemptCount} RecoveryOriginPhase={session.RecoveryOriginPhase} RecoveryOriginLeg={session.RecoveryOriginLeg} NextAttemptUtc={session.RecoveryNextAttemptUtc:O}.", now);

        switch (session.RecoveryAction)
        {
            case RecoveryAction.RetryCurrentPhase:
                var retryPhase = session.RecoveryOriginPhase;
                ClearRecoveryAttempt(session);
                if (retryPhase is HandoffPhase.Idle or HandoffPhase.RecoveringHandoff)
                {
                    EnterManualIntervention("Recovery retry has no valid originating handoff phase.");
                    return;
                }

                SetPhase(retryPhase, reason: "Retrying recoverable handoff operation.");
                return;

            case RecoveryAction.AbandonUnstartedHandoff:
                AbandonUnstartedHandoff(session);
                return;

            case RecoveryAction.SkipCurrentLeg:
                SkipCurrentLegAndContinue(session);
                return;

            case RecoveryAction.WaitForActiveAutomation:
                if (IsOceanLeg(session))
                {
                    if (IsOceanFishingDutyActive())
                    {
                        Wait(now, "Waiting for active Ocean Fishing duty to finish; QR will not preempt it.");
                        return;
                    }

                    SkipCurrentLegAndContinue(session);
                    return;
                }

                if (!TryGetAutoRetainerState(out var autoBusy, out var multiModeActive))
                {
                    Wait(now, "Waiting for AutoRetainer state before resolving recovery.");
                    return;
                }

                if (autoBusy || multiModeActive)
                {
                    Wait(now, "Waiting for active AutoRetainer automation to finish.");
                    return;
                }

                SkipCurrentLegAndContinue(session);
                return;

            case RecoveryAction.ReturnOriginalAndResume:
                ClearRecoveryAttempt(session);
                if (clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId && IsSafeForHandoff())
                    SetPhase(GetResumePhase(), reason: "Handoff recovered; restoring Questionable.");
                else
                    SetPhase(HandoffPhase.ReturningOriginal, reason: "Handoff recovered; returning to the original character.");
                return;

            default:
                EnterManualIntervention(session.RecoveryMessage);
                return;
        }
    }

    private void AbandonUnstartedHandoff(HandoffSession session)
    {
        ReleaseYesAlreadyLease(session);
        configuration.ActiveSession = null;
        lastActionUtc = DateTime.MinValue;
        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.WaitingForReadyWork, "Handoff abandoned safely; readiness rescan pending.", DateTime.UtcNow);
        save();
        Notify("Handoff abandoned safely; Questionable remains in control.", NotificationType.Warning);
    }

    private void SkipCurrentLegAndContinue(HandoffSession session)
    {
        if (IsOceanLeg(session))
        {
            if (session.OceanDutyCompleted)
            {
                ClearRecoveryAttempt(session);
                save();
                SetPhase(HandoffPhase.WaitingForOceanCleanup, reason: "Voyage completion is confirmed; waiting for safe cleanup before returning.");
                return;
            }

            session.OceanFishingLegSkipped = !session.OceanDutyCompleted;
            session.OceanFishingLegCompleted = session.OceanDutyCompleted;
            session.OceanWindowConsumed = true;
            session.OceanTerritoryExited = !IsOceanFishingTerritory();
            session.CurrentLeg = HandoffLeg.None;
            if (session.OceanWindowStartUtc is { } windowStart)
                configuration.LastHandledOceanFishingWindowUtc = windowStart;
            ReleaseYesAlreadyLease(session);
            if (!session.OceanDutyCompleted &&
                session.WahToolsLaunchAccepted &&
                !session.WahToolsLaunchAmbiguous &&
                wahToolsSessionObservedThisRuntime)
            {
                session.WahToolsStopRequested = true;
                session.WahToolsStopRequestedUtc = DateTime.UtcNow;
                session.WahToolsStopStatusGeneration = monitoring.WahToolsStatus?.Generation;
                save();
                var stopResult = wahToolsFeesh.Stop(out var stopError);
                if (stopResult != FeeshIpcResult.Accepted)
                {
                    EnterManualIntervention($"Ocean Fishing attempt failed before duty entry and WahTools Stop was not confirmed: {stopError}");
                    return;
                }

                session.WahToolsStopAcceptedUtc = DateTime.UtcNow;
                ClearRecoveryAttempt(session);
                session.AutoRetainerLegSkipped = true;
                session.PendingAutoRetainerMode = null;
                session.ReturningForAutoRetainer = false;
                save();
                ResetOceanCleanupStability();
                SetPhase(HandoffPhase.WaitingForWahToolsStop, reason: "Failed voyage launch stopped; waiting for stable state before restoring original character.");
                return;
            }
        }
        else if (session.CurrentLeg == HandoffLeg.AutoRetainer)
        {
            session.AutoRetainerLegSkipped = !session.AutoRetainerMultiModeObserved && !session.AutoRetainerCompletionReceived;
            session.AutoRetainerLegCompleted = !session.AutoRetainerLegSkipped;
            session.PendingAutoRetainerMode = null;
            session.CurrentLeg = HandoffLeg.None;
        }

        ClearRecoveryAttempt(session);
        save();
        session.AutoRetainerLegSkipped = true;
        session.PendingAutoRetainerMode = null;
        session.ReturningForAutoRetainer = false;
        save();
        SetPhase(HandoffPhase.ReturningOriginal, reason: "Recoverable handoff leg resolved; restoring the original character before optional work.");
    }

    private void ClearRecoveryAttempt(HandoffSession session)
    {
        session.RecoveryAction = RecoveryAction.None;
        session.RecoveryFailureCode = null;
        session.RecoveryOriginPhase = HandoffPhase.Idle;
        session.RecoveryOriginLeg = HandoffLeg.None;
        session.RecoveryStartedUtc = null;
        session.RecoveryNextAttemptUtc = null;
        session.RecoveryLastAttemptUtc = null;
        session.RecoveryAttemptCount = 0;
        session.RecoveryMessage = string.Empty;
        session.LastError = string.Empty;
    }

    private void EnterManualIntervention(string message)
    {
        log.Warning(
            "Manual intervention required. Session={Session} From={From} Reason={Reason} FailureCode={FailureCode} StopRequestedUtc={StopRequestedUtc} StopAcceptedUtc={StopAcceptedUtc}.",
            SessionId(), Phase, message, configuration.ActiveSession?.RecoveryFailureCode?.ToString() ?? "none",
            configuration.ActiveSession?.WahToolsStopRequestedUtc?.ToString("O") ?? "none",
            configuration.ActiveSession?.WahToolsStopAcceptedUtc?.ToString("O") ?? "none");
        if (configuration.ActiveSession is { } session)
        {
            if (Phase is not (HandoffPhase.Error or HandoffPhase.ManualIntervention or HandoffPhase.RecoveringHandoff))
            {
                session.RecoveryOriginPhase = Phase;
                session.RecoveryOriginLeg = session.CurrentLeg;
            }
            session.RecoveryAction = RecoveryAction.ManualIntervention;
            session.RecoveryMessage = message;
            session.LastError = message;
            ReleaseYesAlreadyLease(session);
            save();
        }
        else
        {
            configuration.ManualRecoveryMessage = message;
            save();
        }

        monitoring.Reason = message;
        SetPhase(HandoffPhase.ManualIntervention, reason: message);
        Notify(message, NotificationType.Error);
    }

    private void ResetReadinessDiagnostics()
    {
        ReadyCharacterCount = 0;
        monitoring.ActiveHandoffKind = null;
        monitoring.CurrentHandoffLeg = null;
        monitoring.OceanLegResolved = false;
        monitoring.AutoRetainerLegResolved = false;
        monitoring.PendingAutoRetainerMode = null;
        monitoring.RegisteredCharacterCount = 0;
        monitoring.ReadyCharacterCount = 0;
        monitoring.ClosestRetainerSeconds = null;
        monitoring.LastReadinessScanUtc = null;
        monitoring.ReadinessScanAvailable = null;
        monitoring.TriggerRegisteredCharacterCount = null;
        monitoring.TriggerReadyCharacterCount = null;
        monitoring.TriggerClosestRetainerSeconds = null;
        monitoring.TriggerDetectedUtc = null;
        monitoring.RecoveryAction = RecoveryAction.None;
        monitoring.RecoveryFailureCode = null;
        monitoring.RecoveryAttemptCount = 0;
        monitoring.RecoveryNextAttemptUtc = null;
    }

    private void Fail(string message)
    {
        HandleFailure(ClassifyFailure(message), message);
    }

    private void HandleFailure(RecoveryFailureCode failure, string message)
    {
        var session = configuration.ActiveSession;
        if (session is null)
        {
            log.Error("Questionable Retainer requires manual intervention. Session=none Message={Message}.", message);
            EnterManualIntervention(message);
            return;
        }

        EnsureCorrelationId(session);
        var now = DateTime.UtcNow;
        var phase = Phase;
        var leg = session.CurrentLeg;
        if (failure is RecoveryFailureCode.MissingSession or RecoveryFailureCode.ContradictorySession)
        {
            EnterManualIntervention(message);
            return;
        }

        var autoKnown = TryGetAutoRetainerState(out var autoBusy, out var multiModeActive);
        var dutyActive = IsOceanFishingDutyActive();
        var decision = RecoveryPolicy.Decide(new(
            failure,
            phase,
            leg,
            SessionValid: true,
            HenchmanStateKnown: false,
            HenchmanBusy: false,
            HenchmanOwnedThisRuntime: false,
            dutyActive,
            session.OceanDutyStarted,
            session.OceanDutyCompleted,
            autoKnown,
            autoBusy,
            multiModeActive,
            session.AutoRetainerMultiModeObserved || session.AutoRetainerCompletionReceived,
            clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId,
            IsSafeForHandoff()));
        var action = decision.Action;

        if (!autoKnown && leg == HandoffLeg.AutoRetainer)
            action = RecoveryAction.RetryCurrentPhase;

        session.RecoveryFailureCode = failure;
        session.RecoveryAction = action;
        session.RecoveryOriginPhase = phase;
        session.RecoveryOriginLeg = leg;
        session.RecoveryStartedUtc ??= now;
        session.RecoveryAttemptCount = 0;
        session.RecoveryNextAttemptUtc = now;
        session.RecoveryMessage = message;
        session.LastError = message;
        monitoring.Reason = message;
        save();
        log.Warning(
            "Questionable Retainer entered automatic recovery. Session={Session} Failure={Failure} Action={Action} OriginPhase={Phase} Message={Message}.",
            SessionId(),
            failure,
            action,
            phase,
            message);
        SetPhase(HandoffPhase.RecoveringHandoff, reason: $"Automatic recovery: {message}");
        Notify($"Automatic recovery started: {message}", NotificationType.Warning);
    }

    private RecoveryFailureCode ClassifyFailure(string message)
    {
        if (message.Contains("Recovery data is missing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("handoff data is missing", StringComparison.OrdinalIgnoreCase))
            return RecoveryFailureCode.MissingSession;
        if (Phase == HandoffPhase.WaitingForQuestionable)
            return message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                       ? RecoveryFailureCode.QuestionableStopTimeout
                       : RecoveryFailureCode.QuestionableStop;
        if (Phase == HandoffPhase.WaitingForOceanDuty)
            return RecoveryFailureCode.OceanDutyStartTimeout;
        if (Phase == HandoffPhase.WaitingForOceanCleanup)
            return RecoveryFailureCode.OceanCleanupStateInvalid;
        if (Phase == HandoffPhase.WaitingForOriginal)
            return RecoveryFailureCode.OriginalLoginTimeout;
        if (Phase is HandoffPhase.ReturningOriginal or HandoffPhase.ResumingQuestionable or HandoffPhase.WaitingForQuestionableResume)
            return RecoveryFailureCode.LifestreamReturn;
        if (message.Contains("Questionable", StringComparison.OrdinalIgnoreCase))
            return RecoveryFailureCode.QuestionableResume;
        if (message.Contains("AutoRetainer", StringComparison.OrdinalIgnoreCase))
            return RecoveryFailureCode.AutoRetainerRunTimeout;
        if (message.Contains("Lifestream", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("original character", StringComparison.OrdinalIgnoreCase))
            return RecoveryFailureCode.LifestreamReturn;
        if (message.Contains("YesAlready", StringComparison.OrdinalIgnoreCase))
            return RecoveryFailureCode.YesAlreadyUnavailable;
        return RecoveryFailureCode.Unknown;
    }

    private void Notify(string message, NotificationType type = NotificationType.Info)
    {
        if (configuration.ChatNotifications)
            chatGui.Print(message, "Questionable Retainer");

        if (configuration.ToastNotifications)
        {
            notificationManager.AddNotification(new Notification
            {
                Title = "Questionable Retainer",
                Content = message,
                Type = type,
            });
        }
    }

    private void DispatchToFrameworkThread(Action action, string operation)
    {
        if (framework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        try
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Framework-thread operation failed. Operation={Operation}.", operation);
                }
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not dispatch operation to the Dalamud framework thread. Operation={Operation}.", operation);
        }
    }

    private void SetMonitoring(MonitoringState state, string reason, DateTime now)
    {
        var changed = state != lastLoggedMonitoringState || !string.Equals(reason, lastLoggedMonitoringReason, StringComparison.Ordinal);

        monitoring.State = state;
        monitoring.Reason = reason;
        if (changed)
            monitoring.LastStatusChangeUtc = now;

        if (!MonitoringStatusFormatter.ShouldLog(
                state,
                changed,
                now,
                lastMonitoringLogUtc,
                MonitoringHeartbeatInterval))
            return;

        monitoring.LastHeartbeatUtc = now;
        lastLoggedMonitoringState = state;
        lastLoggedMonitoringReason = reason;
        lastMonitoringLogUtc = now;
        log.Verbose(
            "Monitoring state: {State}. Session={Session} HandoffPhase={HandoffPhase} PhaseElapsedSeconds={PhaseElapsedSeconds:0.0} SessionElapsedSeconds={SessionElapsedSeconds:0.0}. WaitReason={WaitReason} QuestionableIPC={QuestionableIPC} Questionable={Questionable} Quest={Quest} AutoRetainer={AutoRetainer} Busy={AutoRetainerBusy} MultiMode={MultiMode} Lifestream={Lifestream} ReadinessScan={ReadinessScan} Registered={Registered} Ready={Ready} ClosestRetainerSeconds={ClosestRetainerSeconds} LastReadinessScanUtc={LastReadinessScanUtc} LastDependencyCheckUtc={LastDependencyCheckUtc} TriggerReady={TriggerReady} PlayerLoggedIn={PlayerLoggedIn} ContentIdAvailable={ContentIdAvailable} CurrentContentId={CurrentContentId} FishingContentId={FishingContentId} OceanCleanupWait={OceanCleanupWait} OceanCleanupSource={OceanCleanupSource} OceanCleanupStableElapsedSeconds={OceanCleanupStableElapsedSeconds} OceanCleanupRequiredSeconds={OceanCleanupRequiredSeconds} WahToolsSnapshotUtc={WahToolsSnapshotUtc} WahToolsStatusEvaluationUtc={WahToolsStatusEvaluationUtc} WahToolsStatusAgeSeconds={WahToolsStatusAgeSeconds} Blockers={Blockers}",
            state,
            SessionId(),
            MonitoringStatusFormatter.FormatPhase(Phase),
            (now - phaseStartedUtc).TotalSeconds,
            SessionElapsedSeconds(now),
            reason,
            monitoring.QuestionableAvailable?.ToString() ?? "checking",
            monitoring.QuestionableRunning?.ToString() ?? "unknown",
            string.IsNullOrWhiteSpace(monitoring.QuestionableQuestId) ? "none" : monitoring.QuestionableQuestId,
            monitoring.AutoRetainerAvailable?.ToString() ?? "unknown",
            monitoring.AutoRetainerBusy,
            monitoring.AutoRetainerMultiModeActive,
            monitoring.LifestreamAvailable?.ToString() ?? "unknown",
            monitoring.ReadinessScanAvailable?.ToString() ?? "checking",
            monitoring.RegisteredCharacterCount,
            monitoring.ReadyCharacterCount,
            monitoring.ClosestRetainerSeconds?.ToString() ?? "none",
            monitoring.LastReadinessScanUtc?.ToString("O") ?? "never",
            monitoring.LastDependencyCheckUtc?.ToString("O") ?? "never",
            monitoring.TriggerReadyCharacterCount?.ToString() ?? "none",
            monitoring.PlayerLoggedIn,
            monitoring.PlayerContentIdAvailable,
            monitoring.CurrentContentId?.ToString() ?? "none",
            monitoring.FishingCharacterContentId?.ToString() ?? "none",
            monitoring.OceanCleanupWaitReason,
            monitoring.OceanCleanupConfirmationSource,
            monitoring.OceanCleanupStableElapsedSeconds?.ToString("0.0") ?? "none",
            monitoring.OceanCleanupRequiredSeconds?.ToString("0.0") ?? "none",
            monitoring.WahToolsStatus?.ObservedAtUtc.ToString("O") ?? "none",
            monitoring.WahToolsStatusEvaluationUtc?.ToString("O") ?? "none",
            monitoring.WahToolsStatusAgeSeconds?.ToString("0.000") ?? "none",
            monitoring.PlayerBlockers.Count == 0 ? "none" : string.Join(',', monitoring.PlayerBlockers));
    }

    private void Wait(DateTime now, string reason)
    {
        var state = Phase switch
        {
            HandoffPhase.AwaitingManualQuestionableResume => MonitoringState.Recovering,
            HandoffPhase.Error => MonitoringState.Error,
            _ => MonitoringState.Processing,
        };
        SetMonitoring(state, reason, now);
    }

    private double SessionElapsedSeconds(DateTime now)
    {
        var started = configuration.ActiveSession?.StartedUtc;
        return started is { } value && value != default
                   ? Math.Max(0, (now - value).TotalSeconds)
                   : 0;
    }

    private string SessionId() => configuration.ActiveSession?.CorrelationId is { Length: > 0 } id ? id : "none";

    private void EnsureCorrelationId(HandoffSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.CorrelationId))
            return;

        session.CorrelationId = CreateCorrelationId();
        save();
    }

    private void NormalizeSession(HandoffSession session)
    {
        if (session.SessionVersion >= 8)
            return;

        if (session.SessionVersion < 6 && session.Kind == HandoffKind.OceanFishing)
        {
            session.WahToolsLaunchAmbiguous = true;
            session.YesAlreadyLeaseRequired = false;
            session.YesAlreadyLeaseAcquired = false;
        }

        session.SessionVersion = 8;
        if (session.CurrentLeg == HandoffLeg.None)
        {
            session.CurrentLeg = session.Kind == HandoffKind.OceanFishing || session.Phase is
                HandoffPhase.WaitingForHenchmanStart or
                HandoffPhase.WaitingForOceanDuty or
                HandoffPhase.WaitingForOceanCleanup or
                HandoffPhase.WaitingForHenchmanStop
                    ? HandoffLeg.OceanFishing
                    : HandoffLeg.AutoRetainer;
        }

        if (session.CurrentLeg == HandoffLeg.AutoRetainer && session.PendingAutoRetainerMode is null)
            session.PendingAutoRetainerMode = GetConfiguredAutoRetainerMode();
        save();
    }

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("N")[..8];
}
