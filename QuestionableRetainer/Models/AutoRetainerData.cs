namespace AutoRetainerAPI.Configuration;

// These IPC DTOs intentionally contain only fields consumed by this plugin.
public enum MultiModeType
{
    Retainers,
    Submersibles,
    Everything,
}

public sealed class OfflineCharacterData
{
    public ulong CID;
    public string Name = string.Empty;
    public string World = string.Empty;
    public bool Enabled;
    public bool WorkshopEnabled;
    public List<OfflineRetainerData> RetainerData = [];
}

public sealed class OfflineRetainerData
{
    public string Name = string.Empty;
    public long VentureEndsAt;
    public bool HasVenture;
}

public sealed class MultiModeCommonConfiguration
{
    public bool MultiWaitForAll;
    public int AdvanceTimer;
}
