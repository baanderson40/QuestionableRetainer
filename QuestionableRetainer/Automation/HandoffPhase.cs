namespace QuestionableRetainer.Automation;

public enum HandoffPhase
{
    Idle,
    WaitingForQuestionable,
    WaitingForAutoRetainer,
    ReturningOriginal,
    WaitingForOriginal,
    ResumingQuestionable,
    WaitingForQuestionableResume,
    Error,
    AwaitingManualQuestionableResume,
    WaitingForAutoRetainerIdle,
    WaitingForHenchmanStart,
    WaitingForOceanDuty,
    WaitingForOceanCleanup,
    WaitingForHenchmanStop,
    CheckingChainedWork,
    RecoveringHandoff,
    ManualIntervention,
    PreparingFishingCharacter,
    WaitingForFishingCharacter,
    StartingWahToolsFishing,
    WaitingForWahToolsStop,
}

public enum HandoffKind
{
    AutoRetainer,
    OceanFishing,
}

public enum HandoffLeg
{
    None,
    AutoRetainer,
    OceanFishing,
}

public enum AutoRetainerPassMode
{
    Retainers,
    Deployables,
    Everything,
}

public enum RecoveryAction
{
    None,
    RetryCurrentPhase,
    AbandonUnstartedHandoff,
    SkipCurrentLeg,
    CancelHenchmanThenRestore,
    WaitForActiveAutomation,
    ReturnOriginalAndResume,
    ManualIntervention,
}

public enum RecoveryFailureCode
{
    Unknown,
    QuestionableStop,
    QuestionableStopTimeout,
    YesAlreadyUnavailable,
    HenchmanStateUnavailable,
    HenchmanConflict,
    HenchmanStartAmbiguous,
    HenchmanStartTimeout,
    OceanDutyStartTimeout,
    OceanVoyageTimeout,
    HenchmanStopped,
    OceanCleanupStateInvalid,
    HenchmanCancellation,
    AutoRetainerBusyTimeout,
    AutoRetainerStartAmbiguous,
    AutoRetainerRunTimeout,
    LifestreamReturn,
    OriginalLoginTimeout,
    QuestionableResume,
    MissingSession,
    ContradictorySession,
    OceanDutyExitedUnconfirmed,
}

public enum HenchmanOwnership
{
    None,
    StartIntentPersisted,
    OwnedThisRuntime,
    Ambiguous,
    CancellationRequested,
    Stopped,
}

public enum OceanFishingCharacterMode
{
    CurrentCharacter,
    SelectedCharacter,
}

public sealed class HandoffSession
{
    public int SessionVersion { get; set; } = 8;

    public string CorrelationId { get; set; } = string.Empty;

    public ulong OriginalContentId { get; set; }

    public string OriginalName { get; set; } = string.Empty;

    public string OriginalWorld { get; set; } = string.Empty;

    public string QuestId { get; set; } = string.Empty;

    public HandoffKind Kind { get; set; }

    public HandoffLeg CurrentLeg { get; set; }

    public bool AutoRetainerLegCompleted { get; set; }

    public bool AutoRetainerLegSkipped { get; set; }

    public bool OceanFishingLegCompleted { get; set; }

    public bool OceanFishingLegSkipped { get; set; }

    public AutoRetainerPassMode? PendingAutoRetainerMode { get; set; }

    public bool ReturningForAutoRetainer { get; set; }

    public bool ChainedReadinessChecked { get; set; }

    public int? ChainedRegisteredCharacterCount { get; set; }

    public int? ChainedReadyCharacterCount { get; set; }

    public long? ChainedClosestRetainerSeconds { get; set; }

    public DateTime? ChainedReadinessScanUtc { get; set; }

    public DateTime? OceanWindowStartUtc { get; set; }

    public HandoffPhase Phase { get; set; }

    public DateTime StartedUtc { get; set; }

    public bool QuestionableWasRunning { get; set; }

    public bool QuestionableStopRequested { get; set; }

    public bool QuestionableStopConfirmed { get; set; }

    public bool AutoRetainerStartRequested { get; set; }

    public bool AutoRetainerMultiModeObserved { get; set; }

    public bool AutoRetainerCompletionReceived { get; set; }

    public bool HenchmanStartRequested { get; set; }

    public bool HenchmanStartObserved { get; set; }

    public bool YesAlreadyLeaseRequired { get; set; }

    public bool YesAlreadyLeaseAcquired { get; set; }

    public bool OceanDutyStarted { get; set; }

    public bool OceanDutyCompleted { get; set; }

    public bool HenchmanStopRequested { get; set; }

    public bool HenchmanStopConfirmed { get; set; }

    public string LastError { get; set; } = string.Empty;

    public string RecoveryMessage { get; set; } = string.Empty;

    public RecoveryAction RecoveryAction { get; set; }

    public RecoveryFailureCode? RecoveryFailureCode { get; set; }

    public HandoffPhase RecoveryOriginPhase { get; set; }

    public HandoffLeg RecoveryOriginLeg { get; set; }

    public DateTime? RecoveryStartedUtc { get; set; }

    public DateTime? RecoveryNextAttemptUtc { get; set; }

    public DateTime? RecoveryLastAttemptUtc { get; set; }

    public int RecoveryAttemptCount { get; set; }

    public bool OceanAbortRequested { get; set; }

    public bool OceanAbortConfirmed { get; set; }

    public bool OceanWindowConsumed { get; set; }

    public bool AutoRetainerDispatchAmbiguous { get; set; }

    public HenchmanOwnership HenchmanOwnership { get; set; }

    public ulong FishingContentId { get; set; }

    public string FishingName { get; set; } = string.Empty;

    public string FishingWorld { get; set; } = string.Empty;

    public bool WahToolsLaunchRequested { get; set; }

    public bool WahToolsLaunchAccepted { get; set; }

    public bool WahToolsLaunchAmbiguous { get; set; }

    public bool WahToolsStopRequested { get; set; }

    public DateTime? WahToolsStopRequestedUtc { get; set; }

    public DateTime? WahToolsStopAcceptedUtc { get; set; }

    public long? WahToolsStopStatusGeneration { get; set; }

    public bool OceanTerritoryEntered { get; set; }

    public bool OceanTerritoryExited { get; set; }

    public int FishingCharacterSwitchAttempts { get; set; }

    public DateTime? NextFishingCharacterSwitchAttemptUtc { get; set; }

    public int ReturnCharacterAttempts { get; set; }

    public DateTime? NextReturnCharacterAttemptUtc { get; set; }
}

public enum RecoveryQuestDisposition
{
    Resume,
    AlreadySatisfied,
    Supersede,
}

public static class RecoveryPlanner
{
    public static RecoveryQuestDisposition GetQuestDisposition(string expectedQuestId, string currentQuestId) =>
        string.Equals(expectedQuestId, currentQuestId, StringComparison.Ordinal)
            ? RecoveryQuestDisposition.AlreadySatisfied
            : RecoveryQuestDisposition.Supersede;

    public static HandoffPhase GetRecoveryPhase(
        bool autoRetainerActive,
        bool originalCharacterLoggedIn,
        bool resumeAuthorized)
    {
        return GetRecoveryPhase(
            autoRetainerActive,
            autoRetainerMultiModeActive: autoRetainerActive,
            originalCharacterLoggedIn,
            resumeAuthorized,
            HandoffPhase.Idle,
            autoRetainerStartRequested: false,
            autoRetainerMultiModeObserved: false,
            autoRetainerCompletionReceived: false);
    }

    public static HandoffPhase GetRecoveryPhase(
        bool autoRetainerBusy,
        bool autoRetainerMultiModeActive,
        bool originalCharacterLoggedIn,
        bool resumeAuthorized,
        HandoffPhase interruptedPhase,
        bool autoRetainerStartRequested,
        bool autoRetainerMultiModeObserved,
        bool autoRetainerCompletionReceived)
    {
        if (autoRetainerMultiModeActive)
            return HandoffPhase.WaitingForAutoRetainer;

        if (autoRetainerBusy)
            return HandoffPhase.WaitingForAutoRetainerIdle;

        if (autoRetainerCompletionReceived || autoRetainerMultiModeObserved)
            return GetCharacterRecoveryPhase(originalCharacterLoggedIn, resumeAuthorized);

        if (resumeAuthorized &&
            (interruptedPhase is HandoffPhase.WaitingForQuestionable or HandoffPhase.WaitingForAutoRetainerIdle))
            return HandoffPhase.WaitingForAutoRetainerIdle;

        if (autoRetainerStartRequested && interruptedPhase == HandoffPhase.WaitingForAutoRetainer)
            return GetCharacterRecoveryPhase(originalCharacterLoggedIn, resumeAuthorized);

        return GetCharacterRecoveryPhase(originalCharacterLoggedIn, resumeAuthorized);
    }

    private static HandoffPhase GetCharacterRecoveryPhase(bool originalCharacterLoggedIn, bool resumeAuthorized) =>
        originalCharacterLoggedIn
            ? resumeAuthorized ? HandoffPhase.ResumingQuestionable : HandoffPhase.AwaitingManualQuestionableResume
            : HandoffPhase.ReturningOriginal;
}
