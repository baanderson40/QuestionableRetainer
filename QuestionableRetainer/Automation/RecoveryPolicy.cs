namespace QuestionableRetainer.Automation;

public readonly record struct RecoveryContext(
    RecoveryFailureCode Failure,
    HandoffPhase Phase,
    HandoffLeg Leg,
    bool SessionValid,
    bool HenchmanStateKnown,
    bool HenchmanBusy,
    bool HenchmanOwnedThisRuntime,
    bool OceanDutyActive,
    bool OceanDutyStarted,
    bool OceanDutyCompleted,
    bool AutoRetainerStateKnown,
    bool AutoRetainerBusy,
    bool AutoRetainerMultiModeActive,
    bool AutoRetainerActivityObserved,
    bool OnOriginalCharacter,
    bool PlayerSafe);

public readonly record struct RecoveryDecision(RecoveryAction Action, string Reason);

public static class RecoveryBackoffPolicy
{
    public static TimeSpan GetDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        4 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(1),
    };
}

public static class RecoveryPolicy
{
    public static RecoveryDecision Decide(RecoveryContext context)
    {
        if (!context.SessionValid || context.Failure is RecoveryFailureCode.MissingSession or RecoveryFailureCode.ContradictorySession)
            return new(RecoveryAction.ManualIntervention, "Handoff identity or state cannot be proven.");

        if (context.Phase == HandoffPhase.WaitingForQuestionable && context.QuestionableFailure())
            return new(RecoveryAction.RetryCurrentPhase, "Questionable stop state is not yet observable.");

        if (context.Leg == HandoffLeg.OceanFishing)
        {
            if (context.OceanDutyActive)
                return new(RecoveryAction.WaitForActiveAutomation, "Ocean Fishing duty is active; waiting for a safe exit.");

            if (!context.OceanDutyStarted && context.HenchmanOwnedThisRuntime && context.HenchmanBusy)
                return new(RecoveryAction.CancelHenchmanThenRestore, "Cancelling QR-owned pre-duty Henchman work.");

            if (context.OceanDutyCompleted)
                return new(RecoveryAction.SkipCurrentLeg, "Ocean Fishing duty completed; resolving cleanup without requiring Henchman activity.");

            return new(RecoveryAction.SkipCurrentLeg, "Ocean Fishing leg cannot continue safely; skipping it.");
        }

        if (context.Leg == HandoffLeg.AutoRetainer)
        {
            if (context.AutoRetainerStateKnown && (context.AutoRetainerBusy || context.AutoRetainerMultiModeActive))
                return new(RecoveryAction.WaitForActiveAutomation, "AutoRetainer is active; waiting without preemption.");

            if (context.AutoRetainerActivityObserved)
                return new(RecoveryAction.SkipCurrentLeg, "AutoRetainer became idle after activity; resolving the leg.");

            return new(RecoveryAction.SkipCurrentLeg, "AutoRetainer pass was not confirmed; skipping without redispatch.");
        }

        if (context.OnOriginalCharacter && context.PlayerSafe)
            return new(RecoveryAction.ReturnOriginalAndResume, "Restoring Questionable after recoverable handoff failure.");

        return new(RecoveryAction.ReturnOriginalAndResume, "Returning to the original character before restoring Questionable.");
    }

    private static bool QuestionableFailure(this RecoveryContext context) =>
        context.Failure is RecoveryFailureCode.QuestionableStop or RecoveryFailureCode.QuestionableStopTimeout;
}
