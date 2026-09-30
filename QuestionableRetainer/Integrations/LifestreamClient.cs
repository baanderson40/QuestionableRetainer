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

    public LifestreamClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeCharacter = pluginInterface.GetIpcSubscriber<string, string, ErrorCode>("Lifestream.ChangeCharacter");
        this.log = log;
    }

    public bool TryIsBusy(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Lifestream IPC is unavailable.");
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
            log.Information(
                "Lifestream return IPC invoked. Target=original-character Result={Result} Accepted={Accepted}.",
                result,
                result == ErrorCode.Success);
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
