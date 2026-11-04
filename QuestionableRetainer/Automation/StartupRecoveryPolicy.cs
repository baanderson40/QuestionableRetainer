namespace QuestionableRetainer.Automation;

public static class StartupRecoveryPolicy
{
    // Later workflow stages take precedence over historical Ocean duty flags.
    // This policy never selects a dispatch phase.
    public static HandoffPhase? SelectPhase(HandoffSession session, bool resumeAuthorized, bool onOriginal)
    {
        if (session.Phase is HandoffPhase.Error or HandoffPhase.ManualIntervention)
            return session.Phase;

        if (session.Phase is HandoffPhase.AwaitingManualQuestionableResume or
            HandoffPhase.ResumingQuestionable or HandoffPhase.WaitingForQuestionableResume)
            return !resumeAuthorized ? HandoffPhase.AwaitingManualQuestionableResume
                : onOriginal ? HandoffPhase.ResumingQuestionable : HandoffPhase.ReturningOriginal;

        if (session.Phase is HandoffPhase.ReturningOriginal or HandoffPhase.WaitingForOriginal)
            return session.Phase;

        if (session.Phase == HandoffPhase.CheckingChainedWork)
            return onOriginal ? HandoffPhase.AwaitingManualQuestionableResume : HandoffPhase.ReturningOriginal;

        var unresolvedOcean = session.CurrentLeg == HandoffLeg.OceanFishing ||
                              (session.Kind == HandoffKind.OceanFishing &&
                               !session.OceanFishingLegCompleted && !session.OceanFishingLegSkipped);
        if ((unresolvedOcean || session.Phase == HandoffPhase.WaitingForWahToolsStop) &&
            session.OceanWindowStartUtc is not null && session.WahToolsStopRequested)
            return session.WahToolsStopAcceptedUtc is null
                ? HandoffPhase.ManualIntervention : HandoffPhase.WaitingForWahToolsStop;

        if (session.Phase == HandoffPhase.RecoveringHandoff)
            return HandoffPhase.RecoveringHandoff;

        return null;
    }
}

public readonly record struct RecoveryDependencies(bool AutoRetainer, bool Lifestream, bool Questionable);

public sealed class RecoveryDependencyWaitClock
{
    private DateTime? startedUtc;

    public TimeSpan Update(bool waiting, DateTime now)
    {
        if (waiting)
        {
            startedUtc ??= now;
            return TimeSpan.Zero;
        }

        var elapsed = startedUtc is { } started ? now - started : TimeSpan.Zero;
        Reset();
        return elapsed;
    }

    public void Reset() => startedUtc = null;
}

public static class RecoveryDependencyPolicy
{
    public static RecoveryDependencies RequiredFor(HandoffPhase phase) => phase switch
    {
        HandoffPhase.ReturningOriginal or HandoffPhase.WaitingForOriginal => new(true, true, false),
        HandoffPhase.ResumingQuestionable => new(true, true, true),
        HandoffPhase.WaitingForQuestionableResume => new(false, false, true),
        HandoffPhase.RecoveringHandoff or HandoffPhase.WaitingForAutoRetainer or
            HandoffPhase.WaitingForAutoRetainerIdle => new(true, false, false),
        _ => default,
    };

    // null means unavailable; false means known idle. Never conflate them.
    public static string? GetWaitReason(HandoffPhase phase, bool? autoRetainerBusy, bool? lifestreamBusy,
        bool questionableAvailable, bool identityPending)
    {
        var required = RequiredFor(phase);
        if ((required.AutoRetainer || required.Lifestream || required.Questionable) && identityPending)
            return "Waiting for current character identity before recovery can continue.";
        if (required.AutoRetainer && autoRetainerBusy is null)
            return "Waiting for AutoRetainer state IPC before recovery can continue.";
        if (required.Lifestream && autoRetainerBusy == true)
            return "Waiting for AutoRetainer to become idle before character restoration or resume.";
        if (required.Lifestream && lifestreamBusy is null)
            return "Waiting for Lifestream state IPC before recovery can continue.";
        if (required.Lifestream && lifestreamBusy == true)
            return "Waiting for Lifestream to become idle before character restoration or resume.";
        if (required.Questionable && !questionableAvailable)
            return "Waiting for Questionable state IPC before recovery can continue.";
        return null;
    }
}
