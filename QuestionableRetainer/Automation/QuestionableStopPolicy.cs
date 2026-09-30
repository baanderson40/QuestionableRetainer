namespace QuestionableRetainer.Automation;

public enum QuestionableStopResult
{
    Succeeded,
    ReflectionUnavailable,
    InvocationFailed,
}

public enum QuestionableStopAction
{
    Continue,
    RetryBeforeHandoff,
    FailHandoff,
}

public static class QuestionableStopPolicy
{
    public static QuestionableStopAction GetAction(QuestionableStopResult result) =>
        result switch
        {
            QuestionableStopResult.Succeeded => QuestionableStopAction.Continue,
            QuestionableStopResult.ReflectionUnavailable => QuestionableStopAction.RetryBeforeHandoff,
            QuestionableStopResult.InvocationFailed => QuestionableStopAction.FailHandoff,
            _ => QuestionableStopAction.FailHandoff,
        };
}
