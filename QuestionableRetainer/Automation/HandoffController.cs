using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using QuestionableRetainer.Integrations;

namespace QuestionableRetainer.Automation;

public sealed class HandoffController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(2);
    private readonly Configuration configuration;
    private readonly AutoRetainerClient autoRetainer;
    private readonly QuestionableClient questionable;
    private readonly LifestreamClient lifestream;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
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
    private MonitoringState lastLoggedMonitoringState;
    private string lastLoggedMonitoringReason = string.Empty;
    private DateTime lastMonitoringLogUtc;
    private bool completionEventReceived;
    private bool autoRetainerObservedActive;
    private bool recoveryPending;

    public HandoffController(
        Configuration configuration,
        AutoRetainerClient autoRetainer,
        QuestionableClient questionable,
        LifestreamClient lifestream,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IChatGui chatGui,
        INotificationManager notificationManager,
        IPluginLog log,
        Action save)
    {
        this.configuration = configuration;
        this.autoRetainer = autoRetainer;
        this.questionable = questionable;
        this.lifestream = lifestream;
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.chatGui = chatGui;
        this.notificationManager = notificationManager;
        this.log = log;
        this.save = save;
        autoRetainer.SingleMultiModeFinished += OnSingleMultiModeFinished;

        Phase = configuration.ActiveSession?.Phase ?? HandoffPhase.Idle;
        phaseStartedUtc = DateTime.UtcNow;
        recoveryPending = configuration.ActiveSession is not null;
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
            case HandoffPhase.Error:
                break;
        }
    }

    public void Recover()
    {
        if (configuration.ActiveSession is null)
            return;

        configuration.ActiveSession.LastError = string.Empty;
        recoveryPending = true;
        nextUpdateUtc = DateTime.MinValue;
        save();
    }

    public void CancelRecovery()
    {
        if (Phase != HandoffPhase.Error)
            return;

        if (!autoRetainer.TryGetState(out var busy, out var multiModeActive) || busy || multiModeActive ||
            configuration.ActiveSession is not { } session ||
            !clientState.IsLoggedIn || playerState.ContentId != session.OriginalContentId)
        {
            Notify("Recovery can only be cancelled while AutoRetainer is idle on the original character.", NotificationType.Error);
            return;
        }

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

        configuration.ActiveSession = new HandoffSession
        {
            OriginalContentId = original.CID,
            OriginalName = original.Name,
            OriginalWorld = original.World,
            QuestId = monitoring.QuestionableQuestId,
            StartedUtc = now,
        };
        save();

        if (!questionable.TryStop())
        {
            Fail("Questionable rejected the stop request.");
            return;
        }

        Notify($"Pausing Questionable for AutoRetainer work on {monitoring.ReadyCharacterCount} character(s).");
        SetPhase(HandoffPhase.WaitingForQuestionable);
    }

    private void UpdateWaitingForQuestionable(DateTime now)
    {
        if (HasTimedOut(now, TimeSpan.FromSeconds(30)))
        {
            Fail("Questionable did not stop within 30 seconds.");
            return;
        }

        if (!questionable.TryIsRunning(out var running) || running || !IsSafeForHandoff())
            return;

        if (!autoRetainer.TryGetState(out var busy, out var multiModeActive))
            return;

        if (busy || multiModeActive)
        {
            Fail("AutoRetainer became active before this plugin could start its pass.");
            return;
        }

        completionEventReceived = false;
        autoRetainerObservedActive = false;
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

        if (!autoRetainer.TryGetState(out var busy, out var multiModeActive))
            return;

        autoRetainerObservedActive |= multiModeActive || busy;
        if (multiModeActive || busy)
            return;

        if (!completionEventReceived && !autoRetainerObservedActive)
        {
            if (now >= phaseStartedUtc + TimeSpan.FromSeconds(30))
                Fail("AutoRetainer did not enter Multi Mode within 30 seconds.");
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
                SetPhase(HandoffPhase.ResumingQuestionable);
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval || (clientState.IsLoggedIn && !IsSafeForHandoff()) ||
            !lifestream.TryIsBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            return;
        }

        lastActionUtc = now;
        if (!lifestream.TryChangeCharacter(session.OriginalName, session.OriginalWorld, out var error))
        {
            log.Debug("Lifestream return attempt failed: {Error}", error);
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
            SetPhase(HandoffPhase.ResumingQuestionable);
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

        if (!clientState.IsLoggedIn || playerState.ContentId != session.OriginalContentId || !IsSafeForHandoff())
            return;

        if (!autoRetainer.TryGetState(out var autoRetainerBusy, out var multiModeActive) ||
            autoRetainerBusy || multiModeActive ||
            !lifestream.TryIsBusy(out var lifestreamBusy) || lifestreamBusy ||
            !questionable.TryIsRunning(out var questionableRunning))
        {
            return;
        }

        if (questionableRunning)
        {
            if (questionable.TryGetCurrentQuestId(out var currentQuestId, out var questAvailable) && currentQuestId == session.QuestId)
                Complete();
            else if (!questAvailable)
                return;
            else
                Fail("Questionable is running a different quest; automatic recovery stopped.");
            return;
        }

        if (now < lastActionUtc + ActionRetryInterval)
            return;

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
        if (session is not null && questionable.TryIsRunning(out var running) && running &&
            questionable.TryGetCurrentQuestId(out var currentQuestId, out var questAvailable))
        {
            if (currentQuestId == session.QuestId)
                Complete();
            else
                Fail("Questionable resumed a different quest; automatic recovery stopped.");
        }
    }

    private void TryInitializeRecovery()
    {
        var session = configuration.ActiveSession;
        if (session is null || !autoRetainer.TryGetState(out var busy, out var multiModeActive))
            return;

        recoveryPending = false;
        autoRetainerObservedActive = busy || multiModeActive;
        var onOriginal = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId;
        SetPhase(RecoveryPlanner.GetRecoveryPhase(busy || multiModeActive, onOriginal));
        Notify("Recovered an interrupted Questionable Retainer handoff.");
        SetMonitoring(MonitoringState.Recovering, "Interrupted handoff requires recovery.", DateTime.UtcNow);
    }

    private void RefreshLiveDiagnostics(DateTime now)
    {
        var playerBlockers = GetPlayerBlockers();
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

        var autoRetainerAvailable = autoRetainer.TryGetState(out var autoRetainerBusy, out var multiModeActive);
        monitoring.AutoRetainerAvailable = autoRetainerAvailable;
        monitoring.AutoRetainerBusy = autoRetainerAvailable && autoRetainerBusy;
        monitoring.AutoRetainerMultiModeActive = autoRetainerAvailable && multiModeActive;

        var lifestreamAvailable = lifestream.TryIsBusy(out var lifestreamBusy);
        monitoring.LifestreamAvailable = lifestreamAvailable;
        monitoring.LifestreamBusy = lifestreamAvailable && lifestreamBusy;
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
    }

    private void SetPhase(HandoffPhase phase, bool persist = true)
    {
        Phase = phase;
        phaseStartedUtc = DateTime.UtcNow;
        if (configuration.ActiveSession is not null)
            configuration.ActiveSession.Phase = phase;
        if (persist)
            save();
        if (phase == HandoffPhase.Error)
            SetMonitoring(MonitoringState.Error, monitoring.Reason, DateTime.UtcNow);
        else if (phase != HandoffPhase.Idle)
        {
            var now = DateTime.UtcNow;
            RefreshLiveDiagnostics(now);
            SetMonitoring(MonitoringState.Processing, $"Handoff phase: {MonitoringStatusFormatter.FormatPhase(phase)}.", now);
        }
    }

    private void Complete()
    {
        var questId = configuration.ActiveSession?.QuestId ?? monitoring.QuestionableQuestId;
        log.Information("Handoff complete. Returned to original character and resumed Questionable quest {QuestId}.", questId);
        Notify("AutoRetainer pass complete. Questionable resumed.");
        configuration.ActiveSession = null;
        ReadyCharacterCount = 0;
        monitoring.TriggerRegisteredCharacterCount = null;
        monitoring.TriggerReadyCharacterCount = null;
        monitoring.TriggerClosestRetainerSeconds = null;
        monitoring.TriggerDetectedUtc = null;
        monitoring.PlayerBlockers = [];
        SetPhase(HandoffPhase.Idle, persist: false);
        SetMonitoring(MonitoringState.WaitingForReadyWork, "Handoff complete; monitoring resumed.", DateTime.UtcNow);
        save();
    }

    private void Fail(string message)
    {
        log.Error("Questionable Retainer stopped: {Message}", message);
        if (configuration.ActiveSession is null)
            configuration.ActiveSession = new HandoffSession();
        configuration.ActiveSession.LastError = message;
        monitoring.Reason = message;
        SetPhase(HandoffPhase.Error);
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
        var heartbeatDue = now >= lastMonitoringLogUtc + TimeSpan.FromMinutes(1);

        monitoring.State = state;
        monitoring.Reason = reason;
        if (changed)
            monitoring.LastStatusChangeUtc = now;

        if (!changed && !heartbeatDue)
            return;

        monitoring.LastHeartbeatUtc = now;
        lastLoggedMonitoringState = state;
        lastLoggedMonitoringReason = reason;
        lastMonitoringLogUtc = now;
        log.Information(
            "Monitoring state: {State}. HandoffPhase={HandoffPhase}. {Reason} QuestionableIPC={QuestionableIPC} Questionable={Questionable} Quest={Quest} AutoRetainer={AutoRetainer} Busy={AutoRetainerBusy} MultiMode={MultiMode} Lifestream={Lifestream} ReadinessScan={ReadinessScan} Registered={Registered} Ready={Ready} ClosestRetainerSeconds={ClosestRetainerSeconds} LastReadinessScanUtc={LastReadinessScanUtc} LastDependencyCheckUtc={LastDependencyCheckUtc} TriggerReady={TriggerReady} PlayerLoggedIn={PlayerLoggedIn} ContentId={ContentId} Blockers={Blockers}",
            state,
            MonitoringStatusFormatter.FormatPhase(Phase),
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
}
