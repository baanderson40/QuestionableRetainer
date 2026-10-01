namespace QuestionableRetainer.Automation;

public enum ChainedWorkAction
{
    StartOceanFishing,
    ScanAutoRetainer,
    ReturnAndResume,
}

public readonly record struct ChainedWorkContext(
    bool OceanMonitoringEnabled,
    bool OceanLegResolved,
    bool OceanWindowEligible,
    bool OceanWindowHandled,
    bool AutoRetainerMonitoringEnabled,
    bool AutoRetainerLegResolved);

public static class ChainedWorkPlanner
{
    public static ChainedWorkAction Choose(ChainedWorkContext context)
    {
        if (context.OceanMonitoringEnabled &&
            !context.OceanLegResolved &&
            context.OceanWindowEligible &&
            !context.OceanWindowHandled)
        {
            return ChainedWorkAction.StartOceanFishing;
        }

        if (context.AutoRetainerMonitoringEnabled && !context.AutoRetainerLegResolved)
            return ChainedWorkAction.ScanAutoRetainer;

        return ChainedWorkAction.ReturnAndResume;
    }
}
