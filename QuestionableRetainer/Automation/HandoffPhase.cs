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

public sealed class HandoffSession
{
    public int SessionVersion { get; set; } = 4;

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
