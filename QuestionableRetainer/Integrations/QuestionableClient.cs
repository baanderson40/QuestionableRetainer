using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace QuestionableRetainer.Integrations;

public sealed class QuestionableClient
{
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string?> getCurrentQuestId;
    private readonly ICallGateSubscriber<string, bool> startQuest;
    private readonly ICallGateSubscriber<string, bool> stop;
    private readonly IPluginLog log;

    public QuestionableClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        getCurrentQuestId = pluginInterface.GetIpcSubscriber<string?>("Questionable.GetCurrentQuestId");
        startQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
        stop = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
        this.log = log;
    }

    public bool TryIsRunning(out bool running)
    {
        try
        {
            running = isRunning.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Questionable IPC is unavailable.");
            running = false;
            return false;
        }
    }

    public bool TryGetCurrentQuestId(out string questId, out bool available)
    {
        try
        {
            questId = getCurrentQuestId.InvokeFunc() ?? string.Empty;
            available = true;
            return !string.IsNullOrWhiteSpace(questId);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Questionable quest IPC is unavailable.");
            questId = string.Empty;
            available = false;
            return false;
        }
    }

    public bool TryStop()
    {
        try
        {
            return stop.InvokeFunc("Questionable Retainer handoff");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not stop Questionable.");
            return false;
        }
    }

    public bool TryStart(string questId)
    {
        try
        {
            return startQuest.InvokeFunc(questId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not resume Questionable quest {QuestId}.", questId);
            return false;
        }
    }
}
