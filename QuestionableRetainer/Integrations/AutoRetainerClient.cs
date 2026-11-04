using AutoRetainerAPI.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using QuestionableRetainer.Automation;

namespace QuestionableRetainer.Integrations;

public sealed class AutoRetainerClient : IDisposable
{
    private readonly ICallGateSubscriber<List<ulong>> getRegisteredCharacters;
    private readonly ICallGateSubscriber<ulong, OfflineCharacterData> getCharacterData;
    private readonly ICallGateSubscriber<Dictionary<ulong, HashSet<string>>> getEnabledRetainers;
    private readonly ICallGateSubscriber<ulong, long?> getClosestRetainer;
    private readonly ICallGateSubscriber<ulong, bool?> areVesselsReady;
    private readonly ICallGateSubscriber<MultiModeCommonConfiguration> getRetainerConfiguration;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> getMultiModeStatus;
    private readonly ICallGateSubscriber<MultiModeType?, object> enableSingleMultiMode;
    private readonly ICallGateSubscriber<object> singleMultiModeFinished;
    private readonly IPluginLog log;
    private readonly IpcAvailabilityLog availability;

    public AutoRetainerClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        getRegisteredCharacters = pluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
        getCharacterData = pluginInterface.GetIpcSubscriber<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData");
        getEnabledRetainers = pluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>("AutoRetainer.PluginState.GetEnabledRetainers");
        getClosestRetainer = pluginInterface.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining");
        areVesselsReady = pluginInterface.GetIpcSubscriber<ulong, bool?>("AutoRetainer.PluginState.AreAnyEnabledVesselsReady");
        getRetainerConfiguration = pluginInterface.GetIpcSubscriber<MultiModeCommonConfiguration>("AutoRetainer.GetConfig.MultiModeRetainerConfiguration");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        getMultiModeStatus = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus");
        enableSingleMultiMode = pluginInterface.GetIpcSubscriber<MultiModeType?, object>("AutoRetainer.PluginState.EnableSingleMultiMode");
        singleMultiModeFinished = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.OnSingleMultiModeFinished");
        this.log = log;
        availability = new IpcAvailabilityLog(log);
        singleMultiModeFinished.Subscribe(OnSingleMultiModeFinished);
    }

    public event Action? SingleMultiModeFinished;

    public void Dispose()
    {
        singleMultiModeFinished.Unsubscribe(OnSingleMultiModeFinished);
    }

    public bool TryGetState(out bool busy, out bool multiModeActive)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            multiModeActive = getMultiModeStatus.InvokeFunc();
            availability.Succeeded("AutoRetainer state");
            return true;
        }
        catch (Exception ex)
        {
            availability.Failed("AutoRetainer state", ex);
            busy = false;
            multiModeActive = false;
            return false;
        }
    }

    public bool TryGetCharacter(ulong contentId, out OfflineCharacterData character)
    {
        try
        {
            character = getCharacterData.InvokeFunc(contentId);
            return character is not null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not read AutoRetainer character {ContentId}.", contentId);
            character = null!;
            return false;
        }
    }

    public bool TryGetRegisteredCharacters(out IReadOnlyList<OfflineCharacterData> characters)
    {
        try
        {
            characters = getRegisteredCharacters.InvokeFunc()
                .Select(contentId => getCharacterData.InvokeFunc(contentId))
                .Where(character => character is not null)
                .Cast<OfflineCharacterData>()
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.World, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not read registered AutoRetainer characters.");
            characters = [];
            return false;
        }
    }

    public bool TryScanReadiness(
        bool monitorRetainers,
        bool monitorDeployables,
        long nowUnixSeconds,
        out AutoRetainerReadinessSnapshot snapshot)
    {
        try
        {
            var registered = getRegisteredCharacters.InvokeFunc();
            var selected = monitorRetainers
                               ? getEnabledRetainers.InvokeFunc()
                               : new Dictionary<ulong, HashSet<string>>();
            var retainerConfiguration = monitorRetainers
                                            ? getRetainerConfiguration.InvokeFunc()
                                            : new MultiModeCommonConfiguration();
            var readyCount = 0;
            long? closestRetainerSeconds = null;

            foreach (var contentId in registered)
            {
                var character = getCharacterData.InvokeFunc(contentId);
                if (character is null)
                    continue;

                long? retainerSeconds = null;
                var retainerReady = monitorRetainers && IsRetainerReady(
                    character,
                    selected,
                    retainerConfiguration,
                    getClosestRetainer.InvokeFunc(contentId),
                    nowUnixSeconds,
                    out retainerSeconds);
                var deployableReady = monitorDeployables && character.WorkshopEnabled &&
                                      areVesselsReady.InvokeFunc(contentId) == true;

                if (retainerSeconds is { } seconds &&
                    (closestRetainerSeconds is null || seconds < closestRetainerSeconds))
                {
                    closestRetainerSeconds = seconds;
                }

                if (retainerReady || deployableReady)
                    readyCount++;
            }

            snapshot = new AutoRetainerReadinessSnapshot
            {
                RegisteredCharacterCount = registered.Count,
                ReadyCharacterCount = readyCount,
                ClosestRetainerSeconds = closestRetainerSeconds,
            };
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not query AutoRetainer readiness.");
            snapshot = new AutoRetainerReadinessSnapshot();
            return false;
        }
    }

    public bool TryEnableSinglePass(MultiModeType mode)
    {
        try
        {
            enableSingleMultiMode.InvokeAction(mode);
            log.Information("AutoRetainer single-pass IPC invoked. Mode={Mode} Accepted=true.", mode);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not enable AutoRetainer single-pass Multi Mode.");
            return false;
        }
    }

    private static bool IsRetainerReady(
        OfflineCharacterData character,
        IReadOnlyDictionary<ulong, HashSet<string>> selected,
        MultiModeCommonConfiguration configuration,
        long? closestEligibleSeconds,
        long nowUnixSeconds,
        out long? closestRetainerSeconds)
    {
        closestRetainerSeconds = closestEligibleSeconds;
        if (!character.Enabled || !selected.TryGetValue(character.CID, out var selectedNames))
            return false;

        var remaining = character.RetainerData
            .Where(retainer => selectedNames.Contains(retainer.Name) && retainer.HasVenture)
            .Select(retainer => retainer.VentureEndsAt - nowUnixSeconds)
            .ToArray();

        return ReadinessEvaluator.IsRetainerReady(new(
            remaining,
            closestEligibleSeconds,
            configuration.MultiWaitForAll,
            configuration.AdvanceTimer));
    }

    private void OnSingleMultiModeFinished()
    {
        SingleMultiModeFinished?.Invoke();
    }
}
