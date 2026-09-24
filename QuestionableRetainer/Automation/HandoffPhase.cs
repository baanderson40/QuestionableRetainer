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
}

public sealed class HandoffSession
{
    public ulong OriginalContentId { get; set; }

    public string OriginalName { get; set; } = string.Empty;

    public string OriginalWorld { get; set; } = string.Empty;

    public string QuestId { get; set; } = string.Empty;

    public HandoffPhase Phase { get; set; }

    public DateTime StartedUtc { get; set; }

    public string LastError { get; set; } = string.Empty;
}

public static class RecoveryPlanner
{
    public static HandoffPhase GetRecoveryPhase(bool autoRetainerActive, bool originalCharacterLoggedIn)
    {
        if (autoRetainerActive)
            return HandoffPhase.WaitingForAutoRetainer;

        return originalCharacterLoggedIn
                   ? HandoffPhase.ResumingQuestionable
                   : HandoffPhase.ReturningOriginal;
    }
}
