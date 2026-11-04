using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace QuestionableRetainer.Integrations;

internal sealed class IpcAvailabilityLog(IPluginLog log)
{
    private readonly HashSet<string> unavailable = [];

    public void Failed(string endpoint, Exception exception)
    {
        if (exception is IpcNotReadyError)
        {
            if (unavailable.Add(endpoint))
                log.Debug("IPC endpoint {Endpoint} is not registered; waiting for the plugin to load.", endpoint);
            return;
        }

        log.Debug(exception, "Unexpected failure reading IPC endpoint {Endpoint}.", endpoint);
    }

    public void Succeeded(string endpoint)
    {
        if (unavailable.Remove(endpoint))
            log.Debug("IPC endpoint {Endpoint} is available again.", endpoint);
    }
}
