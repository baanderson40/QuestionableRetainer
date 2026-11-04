using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons;

namespace QuestionableRetainer.Integrations;

public sealed class LifestreamClient
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, string, ErrorCode> changeCharacter;
    private readonly IPluginLog log;
    private readonly IpcAvailabilityLog availability;

    public LifestreamClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeCharacter = pluginInterface.GetIpcSubscriber<string, string, ErrorCode>("Lifestream.ChangeCharacter");
        this.log = log;
        availability = new IpcAvailabilityLog(log);
    }

    public bool TryIsBusy(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            availability.Succeeded("Lifestream.IsBusy");
            return true;
        }
        catch (Exception ex)
        {
            availability.Failed("Lifestream.IsBusy", ex);
            busy = false;
            return false;
        }
    }

    public bool TryChangeCharacter(string name, string world, out string error)
    {
        try
        {
            var result = changeCharacter.InvokeFunc(name, world);
            error = result == ErrorCode.Success ? string.Empty : result.ToString();
            if (result == ErrorCode.Success)
                log.Information("Lifestream character-change IPC accepted. Target={Target}@{World}.", name, world);
            else
                log.Verbose(
                    "Lifestream character-change IPC rejected. Target={Target}@{World} Result={Result}.",
                    name,
                    world,
                    result);
            return result == ErrorCode.Success;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not return through Lifestream.");
            error = "Lifestream IPC unavailable";
            return false;
        }
    }
}
