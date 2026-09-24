using QuestionableRetainer.Automation;

var tests = new (string Name, Action Test)[]
{
    ("any retainer honors advance timer", TestAnyRetainerAdvance),
    ("any retainer rejects later venture", TestAnyRetainerNotReady),
    ("wait-for-all requires every venture", TestWaitForAll),
    ("eligible undeployed retainer is ready", TestUndeployedRetainer),
    ("missing eligible retainer is not ready", TestNoEligibleRetainer),
    ("recovery waits for active AutoRetainer", TestActiveRecovery),
    ("recovery resumes on original character", TestOriginalRecovery),
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
    AssertEqual(HandoffPhase.WaitingForAutoRetainer, RecoveryPlanner.GetRecoveryPhase(true, false));
}

static void TestOriginalRecovery()
{
    AssertEqual(HandoffPhase.ResumingQuestionable, RecoveryPlanner.GetRecoveryPhase(false, true));
}

static void TestRemoteRecovery()
{
    AssertEqual(HandoffPhase.ReturningOriginal, RecoveryPlanner.GetRecoveryPhase(false, false));
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
