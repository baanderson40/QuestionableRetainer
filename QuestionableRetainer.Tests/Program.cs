using QuestionableRetainer.Automation;

var tests = new (string Name, Action Test)[]
{
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
};

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
