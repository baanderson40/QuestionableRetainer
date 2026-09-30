using Dalamud.Configuration;
using QuestionableRetainer.Automation;

namespace QuestionableRetainer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; }

    public bool MonitorRetainers { get; set; } = true;

    public bool MonitorDeployables { get; set; } = true;

    public bool MonitorOceanFishing { get; set; }

    public int OceanFishingLeadMinutes { get; set; } = 5;

    public DateTime? LastHandledOceanFishingWindowUtc { get; set; }

    public bool ChatNotifications { get; set; } = true;

    public bool ToastNotifications { get; set; } = true;

    public HandoffSession? ActiveSession { get; set; }
}
