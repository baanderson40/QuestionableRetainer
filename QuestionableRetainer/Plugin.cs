using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using QuestionableRetainer.Automation;
using QuestionableRetainer.Integrations;
using QuestionableRetainer.Windows;

namespace QuestionableRetainer;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/questionableretainer";
    private const string CommandAlias = "/qr";

    [PluginService]
    private static IDalamudPluginInterface PluginInterface { get; set; } = null!;

    [PluginService]
    private static ICommandManager CommandManager { get; set; } = null!;

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    [PluginService]
    private static IClientState ClientState { get; set; } = null!;

    [PluginService]
    private static IPlayerState PlayerState { get; set; } = null!;

    [PluginService]
    private static IDutyState DutyState { get; set; } = null!;

    [PluginService]
    private static IObjectTable ObjectTable { get; set; } = null!;

    [PluginService]
    private static IGameGui GameGui { get; set; } = null!;

    [PluginService]
    private static ICondition Condition { get; set; } = null!;

    [PluginService]
    private static IChatGui ChatGui { get; set; } = null!;

    [PluginService]
    private static INotificationManager NotificationManager { get; set; } = null!;

    [PluginService]
    private static IPluginLog Log { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("QuestionableRetainer");
    private readonly Configuration configuration;
    private readonly AutoRetainerClient autoRetainer;
    private readonly HenchmanClient henchman;
    private readonly YesAlreadyLease yesAlreadyLease;
    private readonly HandoffController controller;
    private readonly ConfigurationWindow configurationWindow;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector);
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (configuration.Version < 2)
        {
            configuration.Version = 2;
            SaveConfiguration();
        }

        configuration.OceanFishingLeadMinutes = Math.Clamp(configuration.OceanFishingLeadMinutes, 0, 30);
        autoRetainer = new AutoRetainerClient(PluginInterface, Log);
        henchman = new HenchmanClient(PluginInterface, Log);
        yesAlreadyLease = new YesAlreadyLease(PluginInterface, Log);
        var questionable = new QuestionableClient(PluginInterface, Log);
        var lifestream = new LifestreamClient(PluginInterface, Log);
        controller = new HandoffController(
            configuration,
            autoRetainer,
            henchman,
            yesAlreadyLease,
            questionable,
            lifestream,
            ClientState,
            PlayerState,
            DutyState,
            ObjectTable,
            GameGui,
            Condition,
            ChatGui,
            NotificationManager,
            Framework,
            Log,
            SaveConfiguration);
        configurationWindow = new ConfigurationWindow(configuration, controller, SaveConfiguration);
        windowSystem.AddWindow(configurationWindow);
        Log.Information(
            "Questionable Retainer loaded. Monitoring enabled={Enabled}; retainers={Retainers}; deployables={Deployables}; oceanFishing={OceanFishing}.",
            configuration.Enabled,
            configuration.MonitorRetainers,
            configuration.MonitorDeployables,
            configuration.MonitorOceanFishing);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings, or use enable, disable, status, recover, resume, or discard.",
            ShowInHelp = true,
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /questionableretainer.",
            ShowInHelp = false,
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += configurationWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += configurationWindow.Toggle;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= configurationWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= configurationWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        controller.Dispose();
        autoRetainer.Dispose();
        yesAlreadyLease.Dispose();
        windowSystem.RemoveAllWindows();
        ECommonsMain.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        controller.Update();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "enable":
                configuration.Enabled = true;
                SaveConfiguration();
                Log.Information("Command enable accepted. Monitoring enabled=true.");
                ChatGui.Print("Enabled.", "Questionable Retainer");
                break;
            case "disable":
                configuration.Enabled = false;
                SaveConfiguration();
                Log.Information("Command disable accepted. Monitoring enabled=false.");
                ChatGui.Print("Monitoring disabled. Active recovery, if any, will continue.", "Questionable Retainer");
                break;
            case "status":
                ChatGui.Print(MonitoringStatusFormatter.Format(controller.Monitoring), "Questionable Retainer");
                break;
            case "recover":
                controller.Recover();
                break;
            case "resume":
                controller.ResumeQuestionable();
                break;
            case "discard":
                controller.CancelRecovery();
                break;
            default:
                configurationWindow.Toggle();
                break;
        }
    }

    private void SaveConfiguration()
    {
        PluginInterface.SavePluginConfig(configuration);
    }
}
