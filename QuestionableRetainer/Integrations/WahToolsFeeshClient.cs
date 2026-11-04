using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableRetainer.Integrations;

public enum FeeshIpcResult
{
    Accepted,
    Refused,
    Unavailable,
}

public sealed class WahToolsFeeshClient
{
    private readonly ICallGateSubscriber<bool, bool> autoOcean;
    private readonly ICallGateSubscriber<bool> catchTheBoat;
    private readonly ICallGateSubscriber<bool> stop;
    private readonly IPluginLog log;

    public WahToolsFeeshClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        autoOcean = pluginInterface.GetIpcSubscriber<bool, bool>("WahTools.Feesh.AutoOcean");
        catchTheBoat = pluginInterface.GetIpcSubscriber<bool>("WahTools.Feesh.CatchTheBoat");
        stop = pluginInterface.GetIpcSubscriber<bool>("WahTools.Feesh.Stop");
        this.log = log;
    }

    public FeeshIpcResult DisableAutoOcean(out string error) => Invoke(autoOcean, false, "AutoOcean(false)", out error);

    public FeeshIpcResult CatchTheBoat(out string error)
    {
        try
        {
            var accepted = catchTheBoat.InvokeFunc();
            error = accepted ? string.Empty : "WahTools refused CatchTheBoat.";
            log.Information("WahTools Feesh CatchTheBoat IPC returned Accepted={Accepted}.", accepted);
            return accepted ? FeeshIpcResult.Accepted : FeeshIpcResult.Refused;
        }
        catch (Exception ex)
        {
            error = "WahTools CatchTheBoat IPC unavailable; launch outcome is ambiguous.";
            log.Warning(ex, "WahTools Feesh CatchTheBoat IPC failed; observing game state before retry.");
            return FeeshIpcResult.Unavailable;
        }
    }

    public FeeshIpcResult Stop(out string error)
    {
        try
        {
            var accepted = stop.InvokeFunc();
            error = accepted ? string.Empty : "WahTools refused Stop.";
            log.Information("WahTools Feesh Stop IPC returned Accepted={Accepted}.", accepted);
            return accepted ? FeeshIpcResult.Accepted : FeeshIpcResult.Refused;
        }
        catch (Exception ex)
        {
            error = "WahTools Stop IPC unavailable.";
            log.Warning(ex, "Could not stop WahTools Feesh automation.");
            return FeeshIpcResult.Unavailable;
        }
    }

    private FeeshIpcResult Invoke(ICallGateSubscriber<bool, bool> ipc, bool argument, string name, out string error)
    {
        try
        {
            var accepted = ipc.InvokeFunc(argument);
            error = accepted ? string.Empty : $"WahTools refused {name}.";
            log.Information("WahTools Feesh {Ipc} returned Accepted={Accepted}.", name, accepted);
            return accepted ? FeeshIpcResult.Accepted : FeeshIpcResult.Refused;
        }
        catch (Exception ex)
        {
            error = $"WahTools {name} IPC unavailable.";
            log.Warning(ex, "WahTools Feesh {Ipc} IPC failed.", name);
            return FeeshIpcResult.Unavailable;
        }
    }
}
