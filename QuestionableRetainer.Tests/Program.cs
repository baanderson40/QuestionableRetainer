using QuestionableRetainer.Automation;

var tests = new (string Name, Action Test)[]
{
    ("startup preserves manual Ocean resume despite historical duty flags", TestStartupPreservesManualOceanResume),
    ("startup preserves terminal recovery and its reason", TestStartupPreservesTerminalRecovery),
    ("startup restoration does not reopen Ocean cleanup", TestStartupRestorationPrecedence),
    ("startup resume requires fresh authorization", TestStartupResumeAuthorization),
    ("startup chain recovery never dispatches optional work", TestStartupChainRecovery),
    ("startup accepted Stop selects fresh post-stop observation", TestStartupAcceptedStop),
    ("startup ambiguous Stop requires manual resolution", TestStartupAmbiguousStop),
    ("resolved Ocean Stop cannot override later AutoRetainer work", TestStartupResolvedOceanStop),
    ("startup selection never dispatches a voyage", TestStartupNeverDispatchesVoyage),
    ("recovery dependencies distinguish unavailable from idle", TestRecoveryDependencyAvailability),
    ("recovery dependency gates are stage specific", TestRecoveryStageDependencies),
    ("dependency waiting preserves action timeout budget", TestRecoveryDependencyWaitClock),
    ("any retainer honors advance timer", TestAnyRetainerAdvance),
    ("any retainer rejects later venture", TestAnyRetainerNotReady),
    ("wait-for-all requires every venture", TestWaitForAll),
    ("eligible undeployed retainer is ready", TestUndeployedRetainer),
    ("missing eligible retainer is not ready", TestNoEligibleRetainer),
    ("recovery waits for active AutoRetainer", TestActiveRecovery),
    ("recovery awaits manual resume after reload", TestOriginalRecovery),
    ("runtime recovery resumes on original character", TestAuthorizedOriginalRecovery),
    ("recovery returns from another character", TestRemoteRecovery),
    ("waiting status explains no ready work", TestWaitingStatus),
    ("status formatter includes diagnostics", TestStatusFormatter),
    ("status formatter formats negative countdown", TestNegativeCountdown),
    ("unsafe status displays paused", TestUnsafeStatus),
    ("status formatter includes player blockers", TestPlayerBlockers),
    ("ready work blocked displays waiting state", TestReadyWorkBlocked),
    ("friendly blocker labels are grouped", TestFriendlyBlockers),
    ("countdown distinguishes overdue work", TestCountdownLabels),
    ("idle handoff displays not active", TestIdleHandoff),
    ("AutoRetainer preparation phase is clear", TestAutoRetainerPreparationPhase),
    ("recovery recognizes already running quest", TestAlreadyRunningQuest),
    ("recovery supersedes different running quest", TestDifferentRunningQuest),
    ("busy-only recovery waits for AutoRetainer idle", TestBusyOnlyRecovery),
    ("active Multi Mode recovery waits for completion", TestActiveMultiModeRecovery),
    ("completed AutoRetainer recovery returns to resume", TestCompletedAutoRetainerRecovery),
    ("monitoring heartbeat is throttled", TestMonitoringHeartbeat),
    ("successful Questionable stop continues handoff", TestSuccessfulQuestionableStop),
    ("unavailable Questionable stop blocks handoff", TestUnavailableQuestionableStop),
    ("failed Questionable stop fails handoff", TestFailedQuestionableStop),
    ("Ocean Fishing lead window is eligible", TestOceanFishingLeadWindow),
    ("Ocean Fishing minute thirteen remains eligible", TestOceanFishingMinuteThirteen),
    ("Ocean Fishing minute fourteen is closed to launch", TestOceanFishingMinuteFourteen),
    ("Ocean Fishing exact close selects next window", TestOceanFishingNextWindow),
    ("Ocean Fishing cleanup requires stable conditions", TestOceanFishingCleanup),
    ("Ocean Fishing cleanup rejects active result state", TestOceanFishingCleanupBlocked),
    ("chained planner prioritizes eligible Ocean Fishing", TestChainedOceanPriority),
    ("chained planner selects AutoRetainer after Ocean", TestChainedAutoRetainer),
    ("chained planner returns when work is resolved", TestChainedReturn),
    ("chained planner skips Ocean after cutoff", TestChainedOceanCutoff),
    ("recovery never stops unobservable Feesh work", TestRecoveryDoesNotStopFeesh),
    ("recovery skips foreign Ocean Fishing activity", TestRecoverySkipsForeignHenchman),
    ("recovery waits for active AutoRetainer", TestRecoveryWaitsForAutoRetainer),
    ("recovery skips unconfirmed AutoRetainer", TestRecoverySkipsUnconfirmedAutoRetainer),
    ("recovery keeps return failures on restoration path", TestRecoveryKeepsReturnPath),
    ("recovery keeps resume failures on restoration path", TestRecoveryKeepsResumePath),
    ("Ocean Fishing selects current character by default", TestOceanFishingSelectsCurrent),
    ("Ocean Fishing uses selected character", TestOceanFishingSelectsConfigured),
    ("Ocean Fishing rejects missing selected character", TestOceanFishingRejectsMissingSelection),
    ("Ocean Fishing exit without completion is unconfirmed", TestOceanFishingUnconfirmedExit),
    ("Ocean Fishing dispatch requires locked target and open window", TestOceanFishingDispatchGate),
    ("Ocean cleanup explains AutoRetainer Multi Mode blocker", TestOceanCleanupExplainsMultiMode),
    ("chained Ocean session is valid for WahTools stop phase", TestChainedOceanStopSession),
    ("post-stop stability accumulates for full duration", TestOceanFishingStabilityDuration),
    ("fresh WahTools status selects five-second confirmation", TestCleanupUsesFiveSecondsWithFreshReflection),
    ("stale WahTools status selects fallback duration", TestCleanupFallsBackWhenReflectionStale),
    ("WahTools cleanup source reasons are diagnostic", TestCleanupSourceReasons),
    ("post-stop confirmation requires a newer status sample", TestPostStopRequiresFreshStatus),
    ("reflection generation change resets cleanup confirmation", TestCleanupGenerationChange),
    ("WahTools trip failure is detected before boarding", TestWahToolsTripFailure),
    ("WahTools cleanup activity blocks handoff", TestWahToolsCleanupBlocks),
    ("WahTools cleaner failure remains visible", TestWahToolsCleanerFailure),
    ("unavailable WahTools status does not imply idle", TestWahToolsUnavailableIsUnknown),
    ("recovery backoff is capped", TestRecoveryBackoff),
};

static HandoffSession CreateStartupOceanSession(HandoffPhase phase) => new()
{
    Phase = phase,
    Kind = HandoffKind.OceanFishing,
    CurrentLeg = HandoffLeg.OceanFishing,
    OceanDutyCompleted = true,
    OceanWindowStartUtc = DateTime.UtcNow.AddDays(-1),
    StartedUtc = DateTime.UtcNow.AddHours(-30),
    WahToolsLaunchAccepted = true,
};

static void TestStartupPreservesManualOceanResume()
{
    var session = CreateStartupOceanSession(HandoffPhase.AwaitingManualQuestionableResume);
    // Monitoring is intentionally not a policy input: disabling new work does not erase recovery.
    AssertEqual((HandoffPhase?)HandoffPhase.AwaitingManualQuestionableResume,
        StartupRecoveryPolicy.SelectPhase(session, false, true));
    session.WahToolsStopRequested = true;
    AssertEqual((HandoffPhase?)HandoffPhase.AwaitingManualQuestionableResume,
        StartupRecoveryPolicy.SelectPhase(session, false, false));
}

static void TestStartupPreservesTerminalRecovery()
{
    foreach (var phase in new[] { HandoffPhase.ManualIntervention, HandoffPhase.Error })
    {
        var session = CreateStartupOceanSession(phase);
        session.RecoveryMessage = "Saved failure detail";
        session.WahToolsStopRequested = true;
        AssertEqual((HandoffPhase?)phase, StartupRecoveryPolicy.SelectPhase(session, false, true));
        AssertEqual("Saved failure detail", session.RecoveryMessage);
    }
}

static void TestStartupRestorationPrecedence()
{
    foreach (var phase in new[] { HandoffPhase.ReturningOriginal, HandoffPhase.WaitingForOriginal })
    {
        var session = CreateStartupOceanSession(phase);
        AssertEqual((HandoffPhase?)phase, StartupRecoveryPolicy.SelectPhase(session, false, false));
    }
}

static void TestStartupResumeAuthorization()
{
    foreach (var phase in new[] { HandoffPhase.ResumingQuestionable, HandoffPhase.WaitingForQuestionableResume,
                 HandoffPhase.AwaitingManualQuestionableResume })
    {
        var session = CreateStartupOceanSession(phase);
        AssertEqual((HandoffPhase?)HandoffPhase.AwaitingManualQuestionableResume,
            StartupRecoveryPolicy.SelectPhase(session, false, true));
        AssertEqual((HandoffPhase?)HandoffPhase.ResumingQuestionable,
            StartupRecoveryPolicy.SelectPhase(session, true, true));
        AssertEqual((HandoffPhase?)HandoffPhase.ReturningOriginal,
            StartupRecoveryPolicy.SelectPhase(session, true, false));
    }
}

static void TestStartupChainRecovery()
{
    var session = CreateStartupOceanSession(HandoffPhase.CheckingChainedWork);
    AssertEqual((HandoffPhase?)HandoffPhase.AwaitingManualQuestionableResume,
        StartupRecoveryPolicy.SelectPhase(session, false, true));
    AssertEqual((HandoffPhase?)HandoffPhase.ReturningOriginal,
        StartupRecoveryPolicy.SelectPhase(session, false, false));
}

static void TestStartupAcceptedStop()
{
    var session = CreateStartupOceanSession(HandoffPhase.WaitingForOceanCleanup);
    session.WahToolsStopRequested = true;
    session.WahToolsStopAcceptedUtc = DateTime.UtcNow.AddDays(-1);
    AssertEqual((HandoffPhase?)HandoffPhase.WaitingForWahToolsStop,
        StartupRecoveryPolicy.SelectPhase(session, false, true));
}

static void TestStartupAmbiguousStop()
{
    var session = CreateStartupOceanSession(HandoffPhase.WaitingForOceanCleanup);
    session.WahToolsStopRequested = true;
    AssertEqual((HandoffPhase?)HandoffPhase.ManualIntervention,
        StartupRecoveryPolicy.SelectPhase(session, false, true));
    session.Phase = HandoffPhase.RecoveringHandoff;
    AssertEqual((HandoffPhase?)HandoffPhase.ManualIntervention,
        StartupRecoveryPolicy.SelectPhase(session, false, true));
}

static void TestStartupResolvedOceanStop()
{
    var session = CreateStartupOceanSession(HandoffPhase.WaitingForAutoRetainer);
    session.OceanFishingLegCompleted = true;
    session.CurrentLeg = HandoffLeg.AutoRetainer;
    session.WahToolsStopRequested = true;
    session.WahToolsStopAcceptedUtc = DateTime.UtcNow.AddHours(-1);
    AssertEqual((HandoffPhase?)null, StartupRecoveryPolicy.SelectPhase(session, false, true));
}

static void TestStartupNeverDispatchesVoyage()
{
    foreach (var phase in Enum.GetValues<HandoffPhase>())
    {
        var selected = StartupRecoveryPolicy.SelectPhase(CreateStartupOceanSession(phase), false, false);
        AssertTrue(selected is not (HandoffPhase.StartingWahToolsFishing or HandoffPhase.PreparingFishingCharacter or
            HandoffPhase.WaitingForHenchmanStart or HandoffPhase.CheckingChainedWork));
    }
}

static void TestRecoveryDependencyAvailability()
{
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ReturningOriginal, null, false, false, false) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ReturningOriginal, false, null, false, false) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ReturningOriginal, true, false, false, false) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ReturningOriginal, false, false, false, false) is null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ReturningOriginal, false, false, false, true) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ResumingQuestionable, false, false, false, false) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.ResumingQuestionable, false, false, true, false) is null);
}

static void TestRecoveryStageDependencies()
{
    foreach (var phase in new[] { HandoffPhase.WaitingForOceanDuty, HandoffPhase.WaitingForOceanCleanup,
                 HandoffPhase.WaitingForWahToolsStop, HandoffPhase.AwaitingManualQuestionableResume, HandoffPhase.ManualIntervention })
        AssertTrue(RecoveryDependencyPolicy.GetWaitReason(phase, null, null, false, true) is null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.RecoveringHandoff, null, null, false, false) is not null);
    AssertTrue(RecoveryDependencyPolicy.GetWaitReason(HandoffPhase.WaitingForAutoRetainer, true, null, false, false) is null);
}

static void TestRecoveryDependencyWaitClock()
{
    var clock = new RecoveryDependencyWaitClock();
    var now = DateTime.UtcNow;
    AssertEqual(TimeSpan.Zero, clock.Update(true, now));
    AssertEqual(TimeSpan.Zero, clock.Update(true, now.AddMinutes(5)));
    AssertEqual(TimeSpan.FromMinutes(6), clock.Update(false, now.AddMinutes(6)));
    AssertEqual(TimeSpan.Zero, clock.Update(false, now.AddMinutes(7)));
    clock.Update(true, now.AddMinutes(8));
    clock.Reset();
    AssertEqual(TimeSpan.Zero, clock.Update(false, now.AddMinutes(10)));
}

foreach (var (name, test) in tests)
{
    test();
    Console.WriteLine($"PASS {name}");
}

static void TestAnyRetainerAdvance()
{
    AssertTrue(ReadinessEvaluator.IsRetainerReady(new([90, 3600], 90, false, 120)));
}

static void TestAnyRetainerNotReady()
{
    AssertFalse(ReadinessEvaluator.IsRetainerReady(new([121], 121, false, 120)));
}

static void TestWaitForAll()
{
    AssertFalse(ReadinessEvaluator.IsRetainerReady(new([30, 121], 30, true, 120)));
    AssertTrue(ReadinessEvaluator.IsRetainerReady(new([30, 120], 30, true, 120)));
}

static void TestUndeployedRetainer()
{
    AssertTrue(ReadinessEvaluator.IsRetainerReady(new([], -999, true, 60)));
}

static void TestNoEligibleRetainer()
{
    AssertFalse(ReadinessEvaluator.IsRetainerReady(new([], null, false, 60)));
}

static void TestActiveRecovery()
{
    AssertEqual(HandoffPhase.WaitingForAutoRetainer, RecoveryPlanner.GetRecoveryPhase(true, false, false));
}

static void TestOriginalRecovery()
{
    AssertEqual(HandoffPhase.AwaitingManualQuestionableResume, RecoveryPlanner.GetRecoveryPhase(false, true, false));
}

static void TestAuthorizedOriginalRecovery()
{
    AssertEqual(HandoffPhase.ResumingQuestionable, RecoveryPlanner.GetRecoveryPhase(false, true, true));
}

static void TestRemoteRecovery()
{
    AssertEqual(HandoffPhase.ReturningOriginal, RecoveryPlanner.GetRecoveryPhase(false, false, false));
}

static void TestWaitingStatus()
{
    var status = new MonitoringStatus
    {
        State = MonitoringState.WaitingForReadyWork,
        Reason = "Dependencies ready; no retainer or deployable work is ready.",
        QuestionableRunning = true,
        QuestionableQuestId = "3863",
        AutoRetainerAvailable = true,
        LifestreamAvailable = true,
        RegisteredCharacterCount = 8,
        ReadyCharacterCount = 0,
    };

    AssertEqual("Running, waiting for ready work", status.Summary);
    AssertTrue(status.Reason.Contains("no retainer", StringComparison.Ordinal));
}

static void TestStatusFormatter()
{
    var text = MonitoringStatusFormatter.Format(new MonitoringStatus
    {
        State = MonitoringState.WaitingForReadyWork,
        Reason = "No work",
        QuestionableRunning = true,
        QuestionableQuestId = "3863",
        AutoRetainerAvailable = true,
        LifestreamAvailable = true,
        RegisteredCharacterCount = 8,
        ReadyCharacterCount = 0,
    });

    AssertTrue(text.Contains("Quest=3863", StringComparison.Ordinal));
    AssertTrue(text.Contains("Registered=8", StringComparison.Ordinal));
    AssertTrue(text.Contains("Ready=0", StringComparison.Ordinal));
}

static void TestNegativeCountdown()
{
    AssertEqual("-00:00:05", MonitoringStatusFormatter.FormatSeconds(-5));
}

static void TestUnsafeStatus()
{
    var status = new MonitoringStatus { State = MonitoringState.WaitingForPlayer };
    AssertEqual("Paused", status.DisplayState);
}

static void TestPlayerBlockers()
{
    var text = MonitoringStatusFormatter.Format(new MonitoringStatus
    {
        State = MonitoringState.WaitingForPlayer,
        Reason = "Unsafe player state: OccupiedInQuestEvent.",
        PlayerBlockers = ["OccupiedInQuestEvent"],
    });

    AssertTrue(text.Contains("Blockers=OccupiedInQuestEvent", StringComparison.Ordinal));
}

static void TestReadyWorkBlocked()
{
    var status = new MonitoringStatus { State = MonitoringState.ReadyWorkBlocked };
    AssertEqual("Ready work waiting", status.DisplayState);
}

static void TestFriendlyBlockers()
{
    AssertEqual(
        "Bound by duty, In combat, In cutscene",
        MonitoringStatusFormatter.FormatPlayerBlockers([
            "BoundByDuty",
            "BoundByDuty56",
            "InCombat",
            "OccupiedInCutSceneEvent",
            "WatchingCutscene",
        ]));
}

static void TestCountdownLabels()
{
    AssertEqual("Overdue by 00:13:22", MonitoringStatusFormatter.FormatReadyWorkCountdown(-802));
    AssertEqual("Ready in 00:42:10", MonitoringStatusFormatter.FormatReadyWorkCountdown(2530));
}

static void TestIdleHandoff()
{
    AssertEqual("Not active", MonitoringStatusFormatter.FormatPhase(HandoffPhase.Idle));
}

static void TestAutoRetainerPreparationPhase()
{
    AssertEqual("Preparing AutoRetainer", MonitoringStatusFormatter.FormatPhase(HandoffPhase.WaitingForAutoRetainerIdle));
}

static void TestAlreadyRunningQuest()
{
    AssertEqual(
        RecoveryQuestDisposition.AlreadySatisfied,
        RecoveryPlanner.GetQuestDisposition("677", "677"));
}

static void TestDifferentRunningQuest()
{
    AssertEqual(
        RecoveryQuestDisposition.Supersede,
        RecoveryPlanner.GetQuestDisposition("677", "678"));
}

static void TestBusyOnlyRecovery()
{
    AssertEqual(
        HandoffPhase.WaitingForAutoRetainerIdle,
        RecoveryPlanner.GetRecoveryPhase(
            autoRetainerBusy: true,
            autoRetainerMultiModeActive: false,
            originalCharacterLoggedIn: true,
            resumeAuthorized: false,
            interruptedPhase: HandoffPhase.WaitingForAutoRetainerIdle,
            autoRetainerStartRequested: false,
            autoRetainerMultiModeObserved: false,
            autoRetainerCompletionReceived: false));
}

static void TestActiveMultiModeRecovery()
{
    AssertEqual(
        HandoffPhase.WaitingForAutoRetainer,
        RecoveryPlanner.GetRecoveryPhase(
            autoRetainerBusy: true,
            autoRetainerMultiModeActive: true,
            originalCharacterLoggedIn: false,
            resumeAuthorized: false,
            interruptedPhase: HandoffPhase.WaitingForAutoRetainer,
            autoRetainerStartRequested: true,
            autoRetainerMultiModeObserved: false,
            autoRetainerCompletionReceived: false));
}

static void TestCompletedAutoRetainerRecovery()
{
    AssertEqual(
        HandoffPhase.ResumingQuestionable,
        RecoveryPlanner.GetRecoveryPhase(
            autoRetainerBusy: false,
            autoRetainerMultiModeActive: false,
            originalCharacterLoggedIn: true,
            resumeAuthorized: true,
            interruptedPhase: HandoffPhase.WaitingForAutoRetainer,
            autoRetainerStartRequested: true,
            autoRetainerMultiModeObserved: true,
            autoRetainerCompletionReceived: false));
}

static void TestMonitoringHeartbeat()
{
    var now = DateTime.UtcNow;
    AssertFalse(MonitoringStatusFormatter.IsHeartbeatDue(now.AddSeconds(59), now, TimeSpan.FromMinutes(1)));
    AssertTrue(MonitoringStatusFormatter.IsHeartbeatDue(now.AddMinutes(1), now, TimeSpan.FromMinutes(1)));
    AssertFalse(MonitoringStatusFormatter.ShouldLog(
        MonitoringState.Disabled,
        true,
        now,
        now,
        TimeSpan.FromMinutes(1)));
}

static void TestUnavailableQuestionableStop()
{
    AssertEqual(
        QuestionableStopAction.RetryBeforeHandoff,
        QuestionableStopPolicy.GetAction(QuestionableStopResult.ReflectionUnavailable));
}

static void TestSuccessfulQuestionableStop()
{
    AssertEqual(
        QuestionableStopAction.Continue,
        QuestionableStopPolicy.GetAction(QuestionableStopResult.Succeeded));
}

static void TestFailedQuestionableStop()
{
    AssertEqual(
        QuestionableStopAction.FailHandoff,
        QuestionableStopPolicy.GetAction(QuestionableStopResult.InvocationFailed));
}

static void TestOceanFishingLeadWindow()
{
    var state = OceanFishingSchedule.GetState(
        new DateTime(2026, 9, 30, 11, 55, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(5));
    AssertTrue(state.LaunchEligible);
    AssertEqual(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), state.Window.StartUtc);
}

static void TestOceanFishingMinuteThirteen()
{
    var state = OceanFishingSchedule.GetState(
        new DateTime(2026, 9, 30, 12, 13, 59, DateTimeKind.Utc),
        TimeSpan.FromMinutes(5));
    AssertTrue(state.RegistrationOpen);
    AssertTrue(state.LaunchEligible);
}

static void TestOceanFishingMinuteFourteen()
{
    var state = OceanFishingSchedule.GetState(
        new DateTime(2026, 9, 30, 12, 14, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(5));
    AssertFalse(state.LaunchEligible);
}

static void TestOceanFishingNextWindow()
{
    var state = OceanFishingSchedule.GetState(
        new DateTime(2026, 9, 30, 12, 15, 0, DateTimeKind.Utc),
        TimeSpan.FromMinutes(5));
    AssertEqual(new DateTime(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc), state.Window.StartUtc);
    AssertFalse(state.RegistrationOpen);
}

static void TestOceanFishingCleanup()
{
    AssertTrue(OceanFishingCleanupPolicy.CanAccumulateStableTime(new(
        true,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        true,
        false,
        true,
        false,
        false)));
}

static void TestOceanFishingCleanupBlocked()
{
    AssertFalse(OceanFishingCleanupPolicy.CanAccumulateStableTime(new(
        true,
        false,
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        true,
        false,
        true,
        false,
        false)));
}

static void TestChainedOceanPriority()
{
    AssertEqual(
        ChainedWorkAction.StartOceanFishing,
        ChainedWorkPlanner.Choose(new(
            OceanMonitoringEnabled: true,
            OceanLegResolved: false,
            OceanWindowEligible: true,
            OceanWindowHandled: false,
            AutoRetainerMonitoringEnabled: true,
            AutoRetainerLegResolved: false)));
}

static void TestChainedAutoRetainer()
{
    AssertEqual(
        ChainedWorkAction.ScanAutoRetainer,
        ChainedWorkPlanner.Choose(new(
            OceanMonitoringEnabled: true,
            OceanLegResolved: true,
            OceanWindowEligible: false,
            OceanWindowHandled: true,
            AutoRetainerMonitoringEnabled: true,
            AutoRetainerLegResolved: false)));
}

static void TestChainedReturn()
{
    AssertEqual(
        ChainedWorkAction.ReturnAndResume,
        ChainedWorkPlanner.Choose(new(
            OceanMonitoringEnabled: true,
            OceanLegResolved: true,
            OceanWindowEligible: false,
            OceanWindowHandled: true,
            AutoRetainerMonitoringEnabled: true,
            AutoRetainerLegResolved: true)));
}

static void TestChainedOceanCutoff()
{
    var state = OceanFishingSchedule.GetState(
        new DateTime(2026, 9, 30, 12, 14, 0, DateTimeKind.Utc),
        TimeSpan.Zero);
    AssertEqual(
        ChainedWorkAction.ScanAutoRetainer,
        ChainedWorkPlanner.Choose(new(
            OceanMonitoringEnabled: true,
            OceanLegResolved: false,
            OceanWindowEligible: state.LaunchEligible,
            OceanWindowHandled: false,
            AutoRetainerMonitoringEnabled: true,
            AutoRetainerLegResolved: false)));
}

static void TestRecoveryDoesNotStopFeesh()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.OceanDutyStartTimeout,
        HandoffPhase.WaitingForOceanDuty,
        HandoffLeg.OceanFishing,
        true,
        true,
        true,
        true,
        false,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        true));
    AssertEqual(RecoveryAction.SkipCurrentLeg, decision.Action);
}

static void TestRecoverySkipsForeignHenchman()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.HenchmanConflict,
        HandoffPhase.WaitingForHenchmanStart,
        HandoffLeg.OceanFishing,
        true,
        true,
        true,
        false,
        false,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        true));
    AssertEqual(RecoveryAction.SkipCurrentLeg, decision.Action);
}

static void TestRecoveryWaitsForAutoRetainer()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.AutoRetainerRunTimeout,
        HandoffPhase.WaitingForAutoRetainer,
        HandoffLeg.AutoRetainer,
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        true,
        true,
        true,
        false,
        true));
    AssertEqual(RecoveryAction.WaitForActiveAutomation, decision.Action);
}

static void TestRecoverySkipsUnconfirmedAutoRetainer()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.AutoRetainerStartAmbiguous,
        HandoffPhase.WaitingForAutoRetainer,
        HandoffLeg.AutoRetainer,
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        true));
    AssertEqual(RecoveryAction.SkipCurrentLeg, decision.Action);
}

static void TestRecoveryKeepsReturnPath()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.OriginalLoginTimeout,
        HandoffPhase.WaitingForOriginal,
        HandoffLeg.OceanFishing,
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        true));
    AssertEqual(RecoveryAction.ReturnOriginalAndResume, decision.Action);
}

static void TestRecoveryKeepsResumePath()
{
    var decision = RecoveryPolicy.Decide(new(
        RecoveryFailureCode.QuestionableResume,
        HandoffPhase.WaitingForQuestionableResume,
        HandoffLeg.AutoRetainer,
        true,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        true,
        true,
        true,
        true,
        true));
    AssertEqual(RecoveryAction.ReturnOriginalAndResume, decision.Action);
}

static void TestOceanFishingSelectsCurrent()
{
    var current = new OceanFishingCharacterIdentity(10, "Quest", "World");
    AssertTrue(OceanFishingPolicy.TrySelectCharacter(
        OceanFishingCharacterMode.CurrentCharacter,
        current,
        new OceanFishingCharacterIdentity(20, "Other", "World"),
        out var target));
    AssertEqual(current, target);
}

static void TestOceanFishingSelectsConfigured()
{
    var selected = new OceanFishingCharacterIdentity(20, "Fisher", "World");
    AssertTrue(OceanFishingPolicy.TrySelectCharacter(
        OceanFishingCharacterMode.SelectedCharacter,
        new OceanFishingCharacterIdentity(10, "Quest", "World"),
        selected,
        out var target));
    AssertEqual(selected, target);
}

static void TestOceanFishingRejectsMissingSelection()
{
    AssertFalse(OceanFishingPolicy.TrySelectCharacter(
        OceanFishingCharacterMode.SelectedCharacter,
        new OceanFishingCharacterIdentity(10, "Quest", "World"),
        null,
        out _));
}

static void TestOceanFishingUnconfirmedExit()
{
    AssertEqual(
        OceanFishingObservation.TerritoryExitedUnconfirmed,
        OceanFishingPolicy.Observe(false, false, false, territoryEnteredPreviously: true));
    AssertEqual(
        OceanFishingObservation.DutyCompleted,
        OceanFishingPolicy.Observe(false, false, true, territoryEnteredPreviously: true));
}

static void TestOceanFishingDispatchGate()
{
    var start = new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);
    AssertTrue(OceanFishingPolicy.CanDispatch(start.AddMinutes(13), start, targetConfirmed: true, playerSafe: true));
    AssertFalse(OceanFishingPolicy.CanDispatch(start.AddMinutes(14), start, targetConfirmed: true, playerSafe: true));
    AssertFalse(OceanFishingPolicy.CanDispatch(start.AddMinutes(5), start, targetConfirmed: false, playerSafe: true));
}

static void TestOceanCleanupExplainsMultiMode()
{
    var reasons = OceanFishingCleanupPolicy.GetWaitReasons(new(
        true,
        false,
        false,
        true,
        false,
        false,
        false,
        false,
        false,
        true,
        true,
        false,
        true,
        false,
        true));
    AssertTrue(reasons.Any(reason => reason.Contains("AutoRetainer Multi Mode is active (Busy=False)", StringComparison.Ordinal)));
}

static void TestChainedOceanStopSession()
{
    var session = new HandoffSession
    {
        Kind = HandoffKind.AutoRetainer,
        OceanWindowStartUtc = new DateTime(2026, 10, 9, 16, 0, 0, DateTimeKind.Utc),
        WahToolsStopRequested = true,
    };
    AssertTrue(OceanFishingPolicy.HasWahToolsStopSession(session));
}

static void TestOceanFishingStabilityDuration()
{
    var stableSince = new DateTime(2026, 10, 9, 16, 30, 0, DateTimeKind.Utc);
    AssertFalse(OceanFishingPolicy.IsStableForReturn(stableSince, stableSince.AddSeconds(29), TimeSpan.FromSeconds(30)));
    AssertTrue(OceanFishingPolicy.IsStableForReturn(stableSince, stableSince.AddSeconds(30), TimeSpan.FromSeconds(30)));
}

static void TestCleanupUsesFiveSecondsWithFreshReflection()
{
    var tickStart = DateTime.UtcNow;
    var confirmationStart = DateTime.MinValue;
    var previousSource = OceanCleanupConfirmationSource.None;
    long? previousGeneration = null;
    for (var poll = 0; poll <= 5; poll++)
    {
        var pollTick = tickStart.AddSeconds(poll);
        var snapshot = CreateFeeshStatus() with
        {
            ObservedAtUtc = pollTick.AddMilliseconds(20),
            Generation = 12,
        };
        var evaluatedAt = pollTick.AddMilliseconds(30);
        var source = OceanCleanupConfirmationPolicy.SelectSource(snapshot, evaluatedAt);
        AssertEqual(OceanCleanupConfirmationSource.WahToolsReflection, source);
        AssertEqual(TimeSpan.FromSeconds(5), OceanCleanupConfirmationPolicy.GetRequiredDuration(source));
        if (!OceanCleanupConfirmationPolicy.IsSameEvidence(source, snapshot.Generation, previousSource, previousGeneration))
            confirmationStart = evaluatedAt;

        previousSource = source;
        previousGeneration = snapshot.Generation;
        AssertEqual(poll == 5,
            OceanCleanupConfirmationPolicy.HasConfirmedStableTime(confirmationStart, evaluatedAt, source));
    }

    var earlierEvaluation = tickStart;
    var slightlyFutureSnapshot = CreateFeeshStatus() with { ObservedAtUtc = tickStart.AddMilliseconds(1) };
    AssertEqual(OceanCleanupConfirmationSource.GameStateFallback,
        OceanCleanupConfirmationPolicy.SelectSource(slightlyFutureSnapshot, earlierEvaluation));
}

static void TestCleanupFallsBackWhenReflectionStale()
{
    var now = DateTime.UtcNow;
    var status = CreateFeeshStatus() with { ObservedAtUtc = now.AddSeconds(-3) };
    var source = OceanCleanupConfirmationPolicy.SelectSource(status, now);
    AssertEqual(OceanCleanupConfirmationSource.GameStateFallback, source);
    AssertEqual(TimeSpan.FromSeconds(30), OceanCleanupConfirmationPolicy.GetRequiredDuration(source));
}

static void TestCleanupSourceReasons()
{
    var now = DateTime.UtcNow;
    AssertEqual("NoSnapshot", OceanCleanupConfirmationPolicy.GetSourceReason(null, now));
    AssertEqual("ReflectionUnavailable", OceanCleanupConfirmationPolicy.GetSourceReason(
        WahToolsFeeshStatus.Unavailable(WahToolsStatusAvailability.NotInstalled, "missing", now), now));
    AssertEqual("SnapshotStale", OceanCleanupConfirmationPolicy.GetSourceReason(
        CreateFeeshStatus() with { ObservedAtUtc = now.AddSeconds(-3) }, now));
    AssertEqual("SnapshotFromFuture", OceanCleanupConfirmationPolicy.GetSourceReason(
        CreateFeeshStatus() with { ObservedAtUtc = now.AddMilliseconds(1) }, now));
    AssertEqual("AwaitingPostStopSample", OceanCleanupConfirmationPolicy.GetSourceReason(
        CreateFeeshStatus() with { ObservedAtUtc = now }, now, now));
    AssertEqual("FreshReflection", OceanCleanupConfirmationPolicy.GetSourceReason(
        CreateFeeshStatus() with { ObservedAtUtc = now }, now.AddMilliseconds(1)));
}

static void TestPostStopRequiresFreshStatus()
{
    var now = DateTime.UtcNow;
    var beforeStop = CreateFeeshStatus() with { ObservedAtUtc = now.AddSeconds(-1) };
    AssertEqual(OceanCleanupConfirmationSource.None,
        OceanCleanupConfirmationPolicy.SelectSource(beforeStop, now, requireObservedAfterUtc: now));

    var afterStop = CreateFeeshStatus() with { ObservedAtUtc = now.AddMilliseconds(1) };
    AssertEqual(OceanCleanupConfirmationSource.WahToolsReflection,
        OceanCleanupConfirmationPolicy.SelectSource(afterStop, now.AddMilliseconds(2), requireObservedAfterUtc: now));
}

static void TestCleanupGenerationChange()
{
    AssertTrue(OceanCleanupConfirmationPolicy.IsSameEvidence(
        OceanCleanupConfirmationSource.WahToolsReflection, 4,
        OceanCleanupConfirmationSource.WahToolsReflection, 4));
    AssertFalse(OceanCleanupConfirmationPolicy.IsSameEvidence(
        OceanCleanupConfirmationSource.WahToolsReflection, 5,
        OceanCleanupConfirmationSource.WahToolsReflection, 4));
}

static void TestWahToolsTripFailure()
{
    var failed = CreateFeeshStatus(tripState: "Failed", tripStatus: "No Fisher gearset saved");
    AssertTrue(WahToolsFeeshStatusPolicy.IsTripFailedBeforeBoarding(failed, voyageEntryObserved: false));
    AssertFalse(WahToolsFeeshStatusPolicy.IsTripFailedBeforeBoarding(failed, voyageEntryObserved: true));
}

static void TestWahToolsCleanupBlocks()
{
    var status = CreateFeeshStatus(cleaning: true, cleanerRunning: true, cleanerState: "Walking", cleanerStatus: "Heading for the innkeep");
    var blockers = WahToolsFeeshStatusPolicy.GetCleanupBlockers(status);
    AssertTrue(blockers.Any(reason => reason.Contains("WahTools cleaning flag is active", StringComparison.Ordinal)));
    AssertTrue(blockers.Any(reason => reason.Contains("Heading for the innkeep", StringComparison.Ordinal)));
}

static void TestWahToolsCleanerFailure()
{
    var status = CreateFeeshStatus(cleanerState: "Failed", cleanerStatus: "Shore leave took too long, stopping");
    AssertTrue(WahToolsFeeshStatusPolicy.HasCleanerFailure(status));
}

static void TestWahToolsUnavailableIsUnknown()
{
    var status = WahToolsFeeshStatus.Unavailable(WahToolsStatusAvailability.ReadFailed, "WahTools module reloading");
    AssertFalse(status.IsAvailable);
    AssertEqual(0, WahToolsFeeshStatusPolicy.GetCleanupBlockers(status).Count);
}

static WahToolsFeeshStatus CreateFeeshStatus(
    string tripState = "Idle",
    string tripStatus = "Idle",
    bool? tripRunning = false,
    bool? cleaning = false,
    string cleanerState = "Done",
    string cleanerStatus = "Done",
    bool? cleanerRunning = false,
    bool? desyntherRunning = false,
    string? desyntherStatus = "Idle",
    bool? discarderRunning = false,
    string? discarderStatus = "Idle",
    bool? buyerBusy = false) =>
    new(
        WahToolsStatusAvailability.Available,
        DateTime.UtcNow,
        string.Empty,
        true,
        tripState,
        tripStatus,
        tripRunning,
        false,
        cleaning,
        cleanerState,
        cleanerStatus,
        cleanerRunning,
        desyntherRunning,
        desyntherStatus,
        discarderRunning,
        discarderStatus,
        buyerBusy);

static void TestRecoveryBackoff()
{
    AssertEqual(TimeSpan.FromSeconds(2), RecoveryBackoffPolicy.GetDelay(1));
    AssertEqual(TimeSpan.FromSeconds(15), RecoveryBackoffPolicy.GetDelay(3));
    AssertEqual(TimeSpan.FromMinutes(1), RecoveryBackoffPolicy.GetDelay(20));
}

static void AssertTrue(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void AssertFalse(bool value)
{
    if (value)
        throw new InvalidOperationException("Expected false.");
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
