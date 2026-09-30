using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons.Reflection;
using QuestionableRetainer.Automation;

namespace QuestionableRetainer.Integrations;

public sealed class QuestionableClient
{
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string?> getCurrentQuestId;
    private readonly ICallGateSubscriber<string, bool> isQuestComplete;
    private readonly ICallGateSubscriber<string, bool> startQuest;
    private readonly IPluginLog log;

    public QuestionableClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        getCurrentQuestId = pluginInterface.GetIpcSubscriber<string?>("Questionable.GetCurrentQuestId");
        isQuestComplete = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestComplete");
        startQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
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

    public QuestionableStopResult TryStopWithoutCommand(out string error)
    {
        const string stopLabel = "UI stop";
        var stopAttempted = false;

        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin("Questionable", out var plugin, suppressErrors: true, ignoreCache: true))
            {
                error = "Questionable reflection target is unavailable.";
                log.Warning("Could not perform the Questionable UI-equivalent stop: plugin instance unavailable.");
                return QuestionableStopResult.ReflectionUnavailable;
            }

            var pluginType = plugin.GetType();
            var serviceProviderField = pluginType.GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic);
            if (serviceProviderField?.GetValue(plugin) is not IServiceProvider serviceProvider)
            {
                error = "Questionable service provider is unavailable.";
                log.Warning("Could not perform the Questionable UI-equivalent stop: service provider unavailable.");
                return QuestionableStopResult.ReflectionUnavailable;
            }

            var questControllerType = pluginType.Assembly.GetType("Questionable.Controller.QuestController");
            if (questControllerType is null)
            {
                error = "Questionable quest controller type is unavailable.";
                log.Warning("Could not perform the Questionable UI-equivalent stop: quest controller type unavailable.");
                return QuestionableStopResult.ReflectionUnavailable;
            }

            var questController = serviceProvider.GetService(questControllerType);
            var stopMethod = questControllerType.GetMethod(
                "StopAllDueToConditionFailed",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            if (questController is null || stopMethod is null)
            {
                error = "Questionable quest controller stop method is unavailable.";
                log.Warning("Could not perform the Questionable UI-equivalent stop: controller or stop method unavailable.");
                return QuestionableStopResult.ReflectionUnavailable;
            }

            stopAttempted = true;
            stopMethod.Invoke(questController, [stopLabel]);
            error = string.Empty;
            log.Information("Questionable UI-equivalent stop invoked via reflection. Label={Label}.", stopLabel);
            return QuestionableStopResult.Succeeded;
        }
        catch (Exception ex)
        {
            if (!stopAttempted)
            {
                error = "Questionable reflection target could not be resolved.";
                log.Warning(ex, "Could not resolve the Questionable UI-equivalent stop reflection target.");
                return QuestionableStopResult.ReflectionUnavailable;
            }

            error = $"Questionable UI-equivalent stop invocation failed: {ex.GetBaseException().Message}";
            log.Error(ex, "Could not invoke the Questionable UI-equivalent stop.");
            return QuestionableStopResult.InvocationFailed;
        }
    }

    public bool TryStart(string questId)
    {
        try
        {
            var accepted = startQuest.InvokeFunc(questId);
            log.Information("Questionable start IPC invoked. Quest={QuestId} Accepted={Accepted}.", questId, accepted);
            return accepted;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not resume Questionable quest {QuestId}.", questId);
            return false;
        }
    }

    public bool TryIsQuestComplete(string questId, out bool complete, out bool available)
    {
        try
        {
            complete = isQuestComplete.InvokeFunc(questId);
            available = true;
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Questionable completion IPC is unavailable for quest {QuestId}.", questId);
            complete = false;
            available = false;
            return false;
        }
    }
}
