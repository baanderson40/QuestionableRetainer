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
    private static readonly TimeSpan HenchmanStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OceanDutyStartTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OceanVoyageTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan OceanCleanupStableDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HenchmanStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OceanCleanupPollInterval = TimeSpan.FromSeconds(1);
    private const float OceanPositionTolerance = 0.1f;
    private const uint OceanFishingTerritoryOne = 900;
    private const uint OceanFishingTerritoryTwo = 1163;
    private readonly Configuration configuration;
    private readonly AutoRetainerClient autoRetainer;
    private readonly HenchmanClient henchman;
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
    private readonly IPluginLog log;
    private readonly Action save;
    private readonly MonitoringStatus monitoring = new();
    private DateTime nextUpdateUtc;
    private DateTime phaseStartedUtc;
    private DateTime lastActionUtc;
    private DateTime lastDependencyRefreshUtc;
    private DateTime nextQuestionableStopRetryUtc;
    private DateTime? oceanCleanupStableSinceUtc;
    private Vector3? oceanCleanupStablePosition;
    private uint oceanCleanupStableTerritory;
    private ulong oceanCleanupStableContentId;
    private DateTime nextOceanCleanupPollUtc;
    private MonitoringState lastLoggedMonitoringState;
    private string lastLoggedMonitoringReason = string.Empty;
    private DateTime lastMonitoringLogUtc;
    private bool completionEventReceived;
    private bool autoRetainerMultiModeObserved;
    private bool recoveryPending;
    private bool questionablePausedThisRuntime;
    private bool manualResumeAuthorized;

    public HandoffController(
        Configuration configuration,
        AutoRetainerClient autoRetainer,
        HenchmanClient henchman,
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
        IPluginLog log,
        Action save)
    {
        this.configuration = configuration;
        this.autoRetainer = autoRetainer;
        this.henchman = henchman;
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
        this.log = log;
        this.save = save;
        autoRetainer.SingleMultiModeFinished += OnSingleMultiModeFinished;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;

        Phase = configuration.ActiveSession?.Phase ?? HandoffPhase.Idle;
        phaseStartedUtc = DateTime.UtcNow;
        recoveryPending = configuration.ActiveSession is not null;
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

    public string LastError => configuration.ActiveSession?.LastError ?? string.Empty;

    public HandoffSession? Session => configuration.ActiveSession;

    public MonitoringStatus Monitoring => monitoring;

    public void Dispose()
    {
        autoRetainer.SingleMultiModeFinished -= OnSingleMultiModeFinished;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyCompleted -= OnDutyCompleted;
        yesAlreadyLease.Release();
    }

    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
            return;

        nextUpdateUtc = now + TimeSpan.FromMilliseconds(250);

        if (Phase != HandoffPhase.Idle && now >= lastDependencyRefreshUtc + TimeSpan.FromSeconds(1))
            RefreshLiveDiagnostics(now);

        if (recoveryPending)
        {
            TryInitializeRecovery();
            return;
        }

        switch (Phase)
        {
            case HandoffPhase.Idle:
                UpdateIdle(now);
                break;
            case HandoffPhase.WaitingForQuestionable:
                UpdateWaitingForQuestionable(now);
                break;
            case HandoffPhase.WaitingForHenchmanStart:
                UpdateWaitingForHenchmanStart(now);
                break;
            case HandoffPhase.WaitingForOceanDuty:
                UpdateWaitingForOceanDuty(now);
                break;
            case HandoffPhase.WaitingForOceanCleanup:
                UpdateWaitingForOceanCleanup(now);
                break;
            case HandoffPhase.WaitingForHenchmanStop:
                UpdateWaitingForHenchmanStop(now);
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
        }
    }

    public void Recover()
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
        recoveryPending = true;
        nextUpdateUtc = DateTime.MinValue;
        save();
        log.Information("Recovery requested. Session={Session} Phase={Phase}.", SessionId(), Phase);
    }

    public void ResumeQuestionable()
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

    public void CancelRecovery()
    {
        if (Phase is not (HandoffPhase.Error or HandoffPhase.AwaitingManualQuestionableResume))
        {
            log.Information("Discard command ignored. Session={Session} Phase={Phase}.", SessionId(), Phase);
            return;
        }

        if (configuration.ActiveSession is not { } session ||
            !TryGetAutoRetainerState(out var busy, out var multiModeActive) || busy || multiModeActive ||
            !TryGetLifestreamBusy(out var lifestreamBusy) || lifestreamBusy ||
            (session.Kind == HandoffKind.OceanFishing && (!henchman.TryGetState(out var henchmanBusy) || henchmanBusy)) ||
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
        if (session.Kind == HandoffKind.OceanFishing)
            ReleaseYesAlreadyLease(session);
        if (session.Kind == HandoffKind.OceanFishing && session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        configuration.ActiveSession = null;
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
            SessionVersion = 3,
            Kind = HandoffKind.AutoRetainer,
            CorrelationId = CreateCorrelationId(),
            OriginalContentId = original.CID,
            OriginalName = original.Name,
            OriginalWorld = original.World,
            QuestId = monitoring.QuestionableQuestId,
            StartedUtc = now,
            QuestionableWasRunning = questionableRunning,
        };
        ResetOceanCleanupStability();
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

        if (monitoring.HenchmanAvailable != true)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; Henchman IPC is unavailable.", now);
            return;
        }

        if (monitoring.HenchmanBusy)
        {
            SetMonitoring(MonitoringState.ReadyWorkBlocked, "Ocean Fishing ready; an existing Henchman task will not be preempted.", now);
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
            SessionVersion = 3,
            Kind = HandoffKind.OceanFishing,
            CorrelationId = CreateCorrelationId(),
            OriginalContentId = contentId,
            OriginalName = localPlayer.Name.ToString(),
            OriginalWorld = world,
            QuestId = monitoring.QuestionableQuestId,
            StartedUtc = now,
            QuestionableWasRunning = true,
            OceanWindowStartUtc = oceanState.Window.StartUtc,
        };
        ResetOceanCleanupStability();
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
        if (session.Kind == HandoffKind.OceanFishing)
        {
            save();
            StartHenchman(session);
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

    private void StartHenchman(HandoffSession session)
    {
        var leaseResult = yesAlreadyLease.TryAcquire(out var leaseError);
        if (leaseResult == YesAlreadyLeaseResult.Unavailable)
        {
            Fail(leaseError);
            return;
        }

        session.YesAlreadyLeaseRequired = leaseResult == YesAlreadyLeaseResult.Acquired;
        session.YesAlreadyLeaseAcquired = yesAlreadyLease.IsAcquired;
        save();

        if (!henchman.TryGetState(out var busy))
        {
            Fail("Henchman state IPC became unavailable before Ocean Fishing could start.");
            return;
        }

        if (busy)
        {
            Fail("Henchman was already busy before QR started Ocean Fishing.");
            return;
        }

        session.HenchmanStartRequested = true;
        save();
        if (!henchman.TryStartOnABoat(out var error))
        {
            Fail(error);
            return;
        }

        Notify("Henchman On A Boat started. Waiting for the Ocean Fishing voyage.");
        SetPhase(HandoffPhase.WaitingForHenchmanStart);
    }

    private bool MaintainYesAlreadyLease(HandoffSession session, DateTime now)
    {
        if (!session.YesAlreadyLeaseRequired)
            return true;

        var result = yesAlreadyLease.TryAcquire(out var error);
        if (result == YesAlreadyLeaseResult.Acquired)
        {
            if (!session.YesAlreadyLeaseAcquired)
            {
                session.YesAlreadyLeaseAcquired = true;
                save();
            }

            return true;
        }

        if (result == YesAlreadyLeaseResult.NotInstalled)
        {
            if (session.YesAlreadyLeaseAcquired)
            {
                session.YesAlreadyLeaseAcquired = false;
                save();
            }

            return true;
        }

        Wait(now, error);
        return false;
    }

    private void UpdateWaitingForHenchmanStart(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing })
        {
            Fail("Ocean Fishing handoff data is missing.");
            return;
        }

        if (HasTimedOut(now, HenchmanStartTimeout))
        {
            Fail("Henchman did not accept On A Boat within 30 seconds.");
            return;
        }

        if (!MaintainYesAlreadyLease(session, now))
            return;

        if (!henchman.TryGetState(out var busy))
        {
            Wait(now, "Waiting for Henchman state IPC after the start request.");
            return;
        }

        if (!busy)
        {
            Wait(now, "Waiting for Henchman to accept On A Boat.");
            return;
        }

        session.HenchmanStartObserved = true;
        save();
        log.Information("Henchman On A Boat ownership confirmed. Session={Session}.", SessionId());
        SetPhase(HandoffPhase.WaitingForOceanDuty);
    }

    private void UpdateWaitingForOceanDuty(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing })
        {
            Fail("Ocean Fishing handoff data is missing.");
            return;
        }

        if (session.OceanDutyCompleted)
        {
            SetPhase(HandoffPhase.WaitingForOceanCleanup);
            return;
        }

        if (!session.OceanDutyStarted && !MaintainYesAlreadyLease(session, now))
            return;

        if (!session.OceanDutyStarted && IsOceanFishingDutyActive())
        {
            session.OceanDutyStarted = true;
            ReleaseYesAlreadyLease(session);
            save();
            log.Information("Ocean Fishing duty observed. Session={Session}.", SessionId());
        }

        var timeout = session.OceanDutyStarted ? OceanVoyageTimeout : OceanDutyStartTimeout;
        if (HasTimedOut(now, timeout))
        {
            Fail(session.OceanDutyStarted
                     ? "Ocean Fishing voyage did not complete within two hours."
                     : "Henchman did not enter Ocean Fishing within 30 minutes.");
            return;
        }

        if (!henchman.TryGetState(out var busy))
        {
            Wait(now, "Waiting for Henchman state IPC while Ocean Fishing is in progress.");
            return;
        }

        if (!busy)
        {
            Fail("Henchman stopped before QR observed Ocean Fishing completion.");
            return;
        }

        Wait(
            now,
            session.OceanDutyStarted
                ? "Ocean Fishing voyage active; waiting for duty completion."
                : "Henchman is preparing and boarding for Ocean Fishing.");
    }

    private void UpdateWaitingForOceanCleanup(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing, OceanDutyCompleted: true })
        {
            Fail("Ocean Fishing cleanup started without a completed voyage.");
            return;
        }

        if (now < nextOceanCleanupPollUtc)
            return;

        nextOceanCleanupPollUtc = now + OceanCleanupPollInterval;
        if (!henchman.TryGetState(out var henchmanBusy))
        {
            ResetOceanCleanupStability();
            Wait(now, "Waiting for Henchman state IPC during Ocean Fishing cleanup.");
            return;
        }

        if (!henchmanBusy)
        {
            Fail("Henchman stopped before QR confirmed Ocean Fishing cleanup.");
            return;
        }

        var snapshot = CaptureOceanCleanupSnapshot();
        if (!OceanFishingCleanupPolicy.CanAccumulateStableTime(snapshot))
        {
            ResetOceanCleanupStability();
            Wait(now, "Waiting for Henchman to finish post-voyage cleanup and return.");
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
        {
            ResetOceanCleanupStability();
            Wait(now, "Waiting for the player to become available after Ocean Fishing.");
            return;
        }

        var position = localPlayer.Position;
        var contentId = playerState.ContentId;
        var territory = clientState.TerritoryType;
        if (oceanCleanupStableSinceUtc is null ||
            oceanCleanupStableTerritory != territory ||
            oceanCleanupStableContentId != contentId ||
            oceanCleanupStablePosition is not { } stablePosition ||
            Vector3.Distance(position, stablePosition) > OceanPositionTolerance)
        {
            oceanCleanupStableSinceUtc = now;
            oceanCleanupStablePosition = position;
            oceanCleanupStableTerritory = territory;
            oceanCleanupStableContentId = contentId;
            monitoring.OceanCleanupStableSinceUtc = now;
            Wait(now, "Ocean Fishing cleanup state became stable; confirming for 30 seconds.");
            return;
        }

        monitoring.OceanCleanupStableSinceUtc = oceanCleanupStableSinceUtc;
        if (now < oceanCleanupStableSinceUtc.Value + OceanCleanupStableDuration)
        {
            Wait(now, "Confirming stable post-voyage cleanup state before stopping Henchman.");
            return;
        }

        StopHenchman(session);
    }

    private void StopHenchman(HandoffSession session)
    {
        if (!henchman.TryGetState(out var busy))
        {
            Fail("Henchman state IPC became unavailable before cleanup completed.");
            return;
        }

        if (!busy)
        {
            session.HenchmanStopConfirmed = true;
            save();
            SetPhase(HandoffPhase.ReturningOriginal, reason: "Henchman already finished after Ocean Fishing cleanup.");
            return;
        }

        session.HenchmanStopRequested = true;
        save();
        if (!henchman.TryCancelAllTasks(out var error))
        {
            Fail(error);
            return;
        }

        log.Information("QR-owned Henchman cancellation requested. Session={Session}.", SessionId());
        SetPhase(HandoffPhase.WaitingForHenchmanStop);
    }

    private void ReleaseYesAlreadyLease(HandoffSession session)
    {
        if (!session.YesAlreadyLeaseAcquired)
            return;

        yesAlreadyLease.Release();
        session.YesAlreadyLeaseAcquired = false;
        log.Information("YesAlready lease released after Ocean Fishing duty ownership was established. Session={Session}.", SessionId());
    }

    private void UpdateWaitingForHenchmanStop(DateTime now)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing })
        {
            Fail("Ocean Fishing handoff data is missing while stopping Henchman.");
            return;
        }

        if (HasTimedOut(now, HenchmanStopTimeout))
        {
            Fail("Henchman did not stop within 30 seconds after QR cancellation.");
            return;
        }

        if (!henchman.TryGetState(out var busy))
        {
            Wait(now, "Waiting for Henchman state IPC after cancellation.");
            return;
        }

        if (busy)
        {
            Wait(now, "Waiting for QR-owned Henchman task to stop.");
            return;
        }

        session.HenchmanStopConfirmed = true;
        save();
        SetPhase(HandoffPhase.ReturningOriginal, reason: "Henchman stopped after Ocean Fishing cleanup.");
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
            SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer completion already received; returning to the original character.");
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

        var mode = configuration.MonitorRetainers && configuration.MonitorDeployables
                       ? MultiModeType.Everything
                       : configuration.MonitorRetainers
                           ? MultiModeType.Retainers
                           : MultiModeType.Submersibles;
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
            SetPhase(HandoffPhase.ReturningOriginal, reason: "AutoRetainer pass completed; returning to the original character.");
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

        SetPhase(HandoffPhase.ReturningOriginal);
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
                SetPhase(GetResumePhase());
            else
                Wait(now, $"Waiting on original character for a safe state; blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(CapturePlayerBlockers())}.");
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval)
        {
            Wait(now, "Waiting before retrying the Lifestream return request.");
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
            Wait(now, $"Lifestream return request was rejected: {error}.");
            return;
        }

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
            SetPhase(GetResumePhase());
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
        if (session.Kind == HandoffKind.OceanFishing)
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
        if (session.YesAlreadyLeaseRequired && !session.OceanDutyStarted)
        {
            var leaseResult = yesAlreadyLease.TryAcquire(out var leaseError);
            if (leaseResult != YesAlreadyLeaseResult.Acquired)
            {
                SetMonitoring(
                    MonitoringState.Recovering,
                    leaseResult == YesAlreadyLeaseResult.Unavailable
                        ? leaseError
                        : "Waiting for YesAlready before continuing the pre-voyage Ocean Fishing recovery.",
                    DateTime.UtcNow);
                return;
            }

            session.YesAlreadyLeaseAcquired = true;
            save();
        }

        if (!henchman.TryGetState(out var henchmanBusy))
        {
            SetMonitoring(MonitoringState.Recovering, "Waiting for Henchman state IPC before Ocean Fishing recovery can continue.", DateTime.UtcNow);
            return;
        }

        recoveryPending = false;
        if (henchmanBusy)
        {
            var recoveryPhase = session.HenchmanStopRequested && !session.HenchmanStopConfirmed
                                    ? HandoffPhase.WaitingForHenchmanStop
                                    : session.OceanDutyCompleted
                                    ? HandoffPhase.WaitingForOceanCleanup
                                    : session.OceanDutyStarted
                                        ? HandoffPhase.WaitingForOceanDuty
                                        : session.HenchmanStartObserved
                                            ? HandoffPhase.WaitingForOceanDuty
                                            : HandoffPhase.WaitingForHenchmanStart;
            SetPhase(recoveryPhase, reason: "Henchman remains busy; observing the persisted Ocean Fishing handoff.");
            Notify("Recovered an interrupted Ocean Fishing handoff; Henchman remains active.");
            return;
        }

        if (session.HenchmanStopRequested)
        {
            session.HenchmanStopConfirmed = true;
            save();
            var onOriginalAfterStop = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId;
            SetPhase(
                onOriginalAfterStop
                    ? manualResumeAuthorized ? HandoffPhase.ResumingQuestionable : HandoffPhase.AwaitingManualQuestionableResume
                    : HandoffPhase.ReturningOriginal,
                reason: "Recovered QR-owned Henchman cancellation after Ocean Fishing.");
            return;
        }

        if (!session.HenchmanStopConfirmed)
        {
            Fail("Henchman is idle but persisted Ocean Fishing cleanup was not confirmed.");
            return;
        }

        var onOriginal = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId;
        var resumePhase = onOriginal
                              ? manualResumeAuthorized ? HandoffPhase.ResumingQuestionable : HandoffPhase.AwaitingManualQuestionableResume
                              : HandoffPhase.ReturningOriginal;
        SetPhase(
            resumePhase,
            reason: "Ocean Fishing recovery requires manual Questionable resume authorization.");
        log.Information(
            "Ocean Fishing recovery initialized. Session={Session} HenchmanStopConfirmed={HenchmanStopConfirmed} OnOriginal={OnOriginal}.",
            SessionId(),
            session.HenchmanStopConfirmed,
            onOriginal);
        Notify("Recovered an interrupted Ocean Fishing handoff; manual Questionable resume is required.");
    }

    private void RefreshLiveDiagnostics(DateTime now)
    {
        var playerBlockers = CapturePlayerBlockers();
        monitoring.PlayerLoggedIn = clientState.IsLoggedIn;
        monitoring.PlayerContentIdAvailable = playerState.ContentId != 0;
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
        var henchmanAvailable = henchman.TryGetState(out var henchmanBusy);
        monitoring.HenchmanAvailable = henchmanAvailable;
        monitoring.HenchmanBusy = henchmanAvailable && henchmanBusy;
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
        monitoring.OceanDutyStarted = session?.Kind == HandoffKind.OceanFishing && session.OceanDutyStarted;
        monitoring.OceanDutyCompleted = session?.Kind == HandoffKind.OceanFishing && session.OceanDutyCompleted;
        monitoring.LastDependencyCheckUtc = now;
        lastDependencyRefreshUtc = now;
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

    private void ResetOceanCleanupStability()
    {
        nextOceanCleanupPollUtc = DateTime.MinValue;
        oceanCleanupStableSinceUtc = null;
        oceanCleanupStablePosition = null;
        oceanCleanupStableTerritory = 0;
        oceanCleanupStableContentId = 0;
        monitoring.OceanCleanupStableSinceUtc = null;
    }

    private void AddConditionBlocker(ICollection<string> blockers, ConditionFlag flag)
    {
        if (condition[flag])
            blockers.Add(flag.ToString());
    }

    private bool IsSafeForHandoff() => GetPlayerBlockers().Count == 0;

    private bool HasTimedOut(DateTime now, TimeSpan timeout) => now >= phaseStartedUtc + timeout;

    private void OnSingleMultiModeFinished()
    {
        completionEventReceived = true;
        log.Information(
            "AutoRetainer single-pass completion event received. Session={Session} Phase={Phase} StartRequested={StartRequested} MultiModeObserved={MultiModeObserved}.",
            SessionId(),
            Phase,
            configuration.ActiveSession?.AutoRetainerStartRequested ?? false,
            autoRetainerMultiModeObserved);
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing, HenchmanStartObserved: true } || !IsOceanFishingTerritory())
            return;

        if (session.OceanDutyStarted)
            return;

        session.OceanDutyStarted = true;
        ReleaseYesAlreadyLease(session);
        save();
        log.Information("Ocean Fishing duty-start event received. Session={Session}.", SessionId());
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        var session = configuration.ActiveSession;
        if (session is not { Kind: HandoffKind.OceanFishing, OceanDutyStarted: true } || session.OceanDutyCompleted)
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
        var phaseElapsed = now - phaseStartedUtc;
        if (previousPhase != phase)
        {
            log.Information(
                "Handoff phase changed. Session={Session} From={From} To={To} PreviousPhaseElapsedSeconds={PreviousPhaseElapsedSeconds:0.0} SessionElapsedSeconds={SessionElapsedSeconds:0.0}.",
                SessionId(),
                previousPhase,
                phase,
                phaseElapsed.TotalSeconds,
                SessionElapsedSeconds(now));
        }

        Phase = phase;
        if (previousPhase != phase)
            phaseStartedUtc = now;
        if (configuration.ActiveSession is not null)
            configuration.ActiveSession.Phase = phase;
        if (persist)
            save();
        if (phase == HandoffPhase.Error)
            SetMonitoring(MonitoringState.Error, reason ?? monitoring.Reason, now);
        else if (phase == HandoffPhase.AwaitingManualQuestionableResume)
        {
            SetMonitoring(
                MonitoringState.Recovering,
                reason ?? "Manual resume required; Questionable will not start automatically after reload.",
                now);
        }
        else if (phase != HandoffPhase.Idle)
        {
            RefreshLiveDiagnostics(now);
            SetMonitoring(MonitoringState.Processing, reason ?? $"Handoff phase: {MonitoringStatusFormatter.FormatPhase(phase)}.", now);
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
                : session?.Kind == HandoffKind.OceanFishing
                    ? "Ocean Fishing complete. Questionable resumed."
                    : "AutoRetainer pass complete. Questionable resumed.");
        if (session?.Kind == HandoffKind.OceanFishing)
            ReleaseYesAlreadyLease(session);
        if (session?.Kind == HandoffKind.OceanFishing && session.OceanWindowStartUtc is { } windowStart)
            configuration.LastHandledOceanFishingWindowUtc = windowStart;
        configuration.ActiveSession = null;
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
        configuration.ActiveSession = null;
        ResetReadinessDiagnostics();
        lastActionUtc = DateTime.MinValue;
        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.WaitingForReadyWork, "Recovery superseded; readiness rescan pending.", DateTime.UtcNow);
        save();
    }

    private void ResetReadinessDiagnostics()
    {
        ReadyCharacterCount = 0;
        monitoring.RegisteredCharacterCount = 0;
        monitoring.ReadyCharacterCount = 0;
        monitoring.ClosestRetainerSeconds = null;
        monitoring.LastReadinessScanUtc = null;
        monitoring.ReadinessScanAvailable = null;
        monitoring.TriggerRegisteredCharacterCount = null;
        monitoring.TriggerReadyCharacterCount = null;
        monitoring.TriggerClosestRetainerSeconds = null;
        monitoring.TriggerDetectedUtc = null;
    }

    private void Fail(string message)
    {
        if (configuration.ActiveSession is null)
            configuration.ActiveSession = new HandoffSession();
        else if (configuration.ActiveSession.Kind == HandoffKind.OceanFishing)
            ReleaseYesAlreadyLease(configuration.ActiveSession);
        EnsureCorrelationId(configuration.ActiveSession);
        log.Error("Questionable Retainer stopped. Session={Session} Message={Message}.", SessionId(), message);
        configuration.ActiveSession.LastError = message;
        monitoring.Reason = message;
        SetPhase(HandoffPhase.Error, reason: message);
        Notify(message, NotificationType.Error);
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
        log.Information(
            "Monitoring state: {State}. Session={Session} HandoffPhase={HandoffPhase} PhaseElapsedSeconds={PhaseElapsedSeconds:0.0} SessionElapsedSeconds={SessionElapsedSeconds:0.0}. WaitReason={WaitReason} QuestionableIPC={QuestionableIPC} Questionable={Questionable} Quest={Quest} AutoRetainer={AutoRetainer} Busy={AutoRetainerBusy} MultiMode={MultiMode} Lifestream={Lifestream} ReadinessScan={ReadinessScan} Registered={Registered} Ready={Ready} ClosestRetainerSeconds={ClosestRetainerSeconds} LastReadinessScanUtc={LastReadinessScanUtc} LastDependencyCheckUtc={LastDependencyCheckUtc} TriggerReady={TriggerReady} PlayerLoggedIn={PlayerLoggedIn} ContentId={ContentId} Blockers={Blockers}",
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

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("N")[..8];
}
