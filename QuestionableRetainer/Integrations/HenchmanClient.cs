using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableRetainer.Integrations;

public sealed class HenchmanClient
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> startOnABoat;
    private readonly ICallGateSubscriber<object> cancelAllTasks;
    private readonly IPluginLog log;

    public HenchmanClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Henchman.IsBusy");
        startOnABoat = pluginInterface.GetIpcSubscriber<object>("Henchman.StartOnABoat");
        cancelAllTasks = pluginInterface.GetIpcSubscriber<object>("Henchman.CancelAllTasks");
        this.log = log;
    }

    public bool TryGetState(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Henchman state IPC is unavailable.");
            busy = false;
            return false;
        }
    }

    public bool TryStartOnABoat(out string error)
    {
        try
        {
            startOnABoat.InvokeAction();
            error = string.Empty;
            log.Information("Henchman On A Boat IPC invoked.");
            return true;
        }
        catch (Exception ex)
        {
            error = "Henchman On A Boat IPC unavailable.";
            log.Warning(ex, "Could not start Henchman On A Boat.");
            return false;
        }
    }

    public bool TryCancelAllTasks(out string error)
    {
        try
        {
            cancelAllTasks.InvokeAction();
            error = string.Empty;
            log.Information("Henchman cancel-all-tasks IPC invoked for QR-owned Ocean Fishing handoff.");
            return true;
        }
        catch (Exception ex)
        {
            error = "Henchman cancellation IPC unavailable.";
            log.Warning(ex, "Could not stop QR-owned Henchman On A Boat task.");
            return false;
        }
    }
}
