using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace QuestionableRetainer.Integrations;

public enum YesAlreadyLeaseResult
{
    Acquired,
    NotInstalled,
    Unavailable,
}

public sealed class YesAlreadyLease : IDisposable
{
    private const string DataTag = "YesAlready.StopRequests";
    private const string Owner = "QuestionableRetainer.OceanFishing";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private HashSet<string>? stopRequests;
    private bool acquired;

    public YesAlreadyLease(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool IsAcquired => acquired;

    public YesAlreadyLeaseResult TryAcquire(out string error)
    {
        var wasAcquired = acquired;
        if (!DalamudReflector.TryGetDalamudPlugin("YesAlready", out _, suppressErrors: true, ignoreCache: true))
        {
            acquired = false;
            stopRequests = null;
            error = string.Empty;
            return YesAlreadyLeaseResult.NotInstalled;
        }

        if (!pluginInterface.TryGetData<HashSet<string>>(DataTag, out var data) || data is null)
        {
            error = "YesAlready is loaded but its stop-request data is unavailable.";
            log.Warning("Could not acquire YesAlready stop-request lease because shared data is unavailable.");
            return YesAlreadyLeaseResult.Unavailable;
        }

        stopRequests = data;
        stopRequests.Add(Owner);
        acquired = true;
        error = string.Empty;
        if (!wasAcquired)
            log.Information("YesAlready stop-request lease acquired. Owner={Owner}.", Owner);
        return YesAlreadyLeaseResult.Acquired;
    }

    public void Release()
    {
        if (!acquired)
            return;

        stopRequests?.Remove(Owner);
        acquired = false;
        stopRequests = null;
        log.Information("YesAlready stop-request lease released. Owner={Owner}.", Owner);
    }

    public void Dispose()
    {
        Release();
    }
}
