namespace QuestionableRetainer.Automation;

public enum MonitoringState
{
    Disabled,
    WaitingForPlayer,
    WaitingForQuestionable,
    WaitingForAutoRetainer,
    WaitingForLifestream,
    WaitingForReadyWork,
    ReadyWorkBlocked,
    ReadyWorkDetected,
    Processing,
    Recovering,
    Error,
}

public sealed class MonitoringStatus
{
    public MonitoringState State { get; internal set; } = MonitoringState.Disabled;

    public string Reason { get; internal set; } = "Monitoring is disabled.";

    public bool? QuestionableRunning { get; internal set; }

    public bool? QuestionableAvailable { get; internal set; }

    public string QuestionableQuestId { get; internal set; } = string.Empty;

    public bool? AutoRetainerAvailable { get; internal set; }

    public bool? ReadinessScanAvailable { get; internal set; }

    public bool AutoRetainerBusy { get; internal set; }

    public bool AutoRetainerMultiModeActive { get; internal set; }

    public bool? LifestreamAvailable { get; internal set; }

    public bool LifestreamBusy { get; internal set; }

    public bool? HenchmanAvailable { get; internal set; }

    public bool HenchmanBusy { get; internal set; }

    public bool YesAlreadyLeaseAcquired { get; internal set; }

    public HandoffKind? ActiveHandoffKind { get; internal set; }

    public HandoffLeg? CurrentHandoffLeg { get; internal set; }

    public bool OceanLegResolved { get; internal set; }

    public bool AutoRetainerLegResolved { get; internal set; }

    public AutoRetainerPassMode? PendingAutoRetainerMode { get; internal set; }

    public DateTime? NextOceanWindowUtc { get; internal set; }

    public bool OceanLaunchEligible { get; internal set; }

    public bool OceanWindowHandled { get; internal set; }

    public bool OceanDutyStarted { get; internal set; }

    public bool OceanDutyCompleted { get; internal set; }

    public DateTime? OceanCleanupStableSinceUtc { get; internal set; }

    public int RegisteredCharacterCount { get; internal set; }

    public int ReadyCharacterCount { get; internal set; }

    public long? ClosestRetainerSeconds { get; internal set; }

    public DateTime? LastReadinessScanUtc { get; internal set; }

    public DateTime? LastDependencyCheckUtc { get; internal set; }

    public int? TriggerRegisteredCharacterCount { get; internal set; }

    public int? TriggerReadyCharacterCount { get; internal set; }

    public long? TriggerClosestRetainerSeconds { get; internal set; }

    public DateTime? TriggerDetectedUtc { get; internal set; }

    public DateTime? LastStatusChangeUtc { get; internal set; }

    public DateTime? LastHeartbeatUtc { get; internal set; }

    public bool PlayerLoggedIn { get; internal set; }

    public bool PlayerContentIdAvailable { get; internal set; }

    public IReadOnlyList<string> PlayerBlockers { get; internal set; } = [];

    public string Summary => State switch
    {
        MonitoringState.Disabled => "Monitoring disabled",
        MonitoringState.WaitingForReadyWork => "Running, waiting for ready work",
        MonitoringState.ReadyWorkBlocked => "Ready work waiting",
        MonitoringState.ReadyWorkDetected => "Ready work detected",
        MonitoringState.Processing => "Processing handoff",
        MonitoringState.Recovering => "Recovering interrupted handoff",
        MonitoringState.Error => "Error",
        _ => Reason,
    };

    public string DisplayState => State switch
    {
        MonitoringState.Disabled => "Disabled",
        MonitoringState.WaitingForReadyWork => "Running",
        MonitoringState.ReadyWorkBlocked => "Ready work waiting",
        MonitoringState.ReadyWorkDetected => "Ready",
        MonitoringState.Processing => "Processing",
        MonitoringState.Recovering => "Recovering",
        MonitoringState.Error => "Error",
        _ => "Paused",
    };
}

public sealed class AutoRetainerReadinessSnapshot
{
    public int RegisteredCharacterCount { get; init; }

    public int ReadyCharacterCount { get; init; }

    public long? ClosestRetainerSeconds { get; init; }
}

public static class MonitoringStatusFormatter
{
    public static bool IsHeartbeatDue(DateTime now, DateTime lastLogUtc, TimeSpan interval) =>
        now >= lastLogUtc + interval;

    public static bool ShouldLog(
        MonitoringState state,
        bool changed,
        DateTime now,
        DateTime lastLogUtc,
        TimeSpan heartbeatInterval) =>
        state != MonitoringState.Disabled &&
        (changed || IsHeartbeatDue(now, lastLogUtc, heartbeatInterval));

    public static string Format(MonitoringStatus status)
    {
        var closest = status.ClosestRetainerSeconds is { } seconds
                           ? FormatSeconds(seconds)
                           : "none";
        var lastReadinessScan = status.LastReadinessScanUtc?.ToString("O") ?? "never";
        var lastDependencyCheck = status.LastDependencyCheckUtc?.ToString("O") ?? "never";
        return $"State={status.State}; Reason={status.Reason}; " +
               $"Questionable={(status.QuestionableRunning?.ToString() ?? "unknown")}; " +
               $"QuestionableIPC={FormatAvailability(status.QuestionableAvailable)}; " +
               $"Quest={(string.IsNullOrWhiteSpace(status.QuestionableQuestId) ? "none" : status.QuestionableQuestId)}; " +
               $"AutoRetainer={FormatAvailability(status.AutoRetainerAvailable)}; " +
               $"Busy={status.AutoRetainerBusy}; MultiMode={status.AutoRetainerMultiModeActive}; " +
               $"Lifestream={FormatAvailability(status.LifestreamAvailable)}; " +
               $"Henchman={FormatAvailability(status.HenchmanAvailable)}; HenchmanBusy={status.HenchmanBusy}; " +
               $"YesAlreadyLease={status.YesAlreadyLeaseAcquired}; " +
               $"HandoffKind={(status.ActiveHandoffKind?.ToString() ?? "none")}; " +
               $"HandoffLeg={(status.CurrentHandoffLeg?.ToString() ?? "none")}; OceanLegResolved={status.OceanLegResolved}; AutoRetainerLegResolved={status.AutoRetainerLegResolved}; PendingAutoRetainerMode={(status.PendingAutoRetainerMode?.ToString() ?? "none")}; " +
               $"NextOcean={status.NextOceanWindowUtc?.ToString("O") ?? "none"}; OceanEligible={status.OceanLaunchEligible}; OceanHandled={status.OceanWindowHandled}; " +
               $"OceanDutyStarted={status.OceanDutyStarted}; OceanDutyCompleted={status.OceanDutyCompleted}; " +
               $"OceanCleanupStableSince={status.OceanCleanupStableSinceUtc?.ToString("O") ?? "none"}; " +
               $"ReadinessScan={FormatAvailability(status.ReadinessScanAvailable)}; " +
               $"Registered={status.RegisteredCharacterCount}; Ready={status.ReadyCharacterCount}; " +
               $"ClosestRetainer={closest}; LastReadinessScanUtc={lastReadinessScan}; " +
               $"LastDependencyCheckUtc={lastDependencyCheck}; " +
               $"TriggerReady={status.TriggerReadyCharacterCount?.ToString() ?? "none"}; " +
               $"TriggerDetectedUtc={status.TriggerDetectedUtc?.ToString("O") ?? "none"}; " +
               $"PlayerLoggedIn={status.PlayerLoggedIn}; ContentId={status.PlayerContentIdAvailable}; " +
               $"Blockers={(status.PlayerBlockers.Count == 0 ? "none" : string.Join(',', status.PlayerBlockers))}";
    }

    public static string FormatPlayerBlockers(IReadOnlyList<string> blockers)
    {
        if (blockers.Count == 0)
            return "none";

        var friendly = new List<string>();
        foreach (var blocker in blockers)
        {
            var label = blocker switch
            {
                "BoundByDuty" or "BoundByDuty56" or "BoundByDuty95" => "Bound by duty",
                "InCombat" => "In combat",
                "BetweenAreas" or "BetweenAreas51" => "Changing areas",
                "WatchingCutscene" or "WatchingCutscene78" or "OccupiedInCutSceneEvent" => "In cutscene",
                "LoggingOut" => "Logging out",
                "NotLoggedIn" => "Not logged in",
                "MissingContentId" => "Content ID unavailable",
                _ when blocker.StartsWith("Occupied", StringComparison.Ordinal) => "Occupied",
                _ => blocker,
            };

            if (!friendly.Contains(label, StringComparer.Ordinal))
                friendly.Add(label);
        }

        return string.Join(", ", friendly);
    }

    public static string FormatReadyWorkCountdown(long? seconds)
    {
        if (seconds is not { } value)
            return "none";

        return value < 0
                   ? $"Overdue by {FormatSeconds(-value)}"
                   : $"Ready in {FormatSeconds(value)}";
    }

    public static string FormatPhase(HandoffPhase phase) =>
        phase switch
        {
            HandoffPhase.Idle => "Not active",
            HandoffPhase.WaitingForQuestionable => "Pausing Questionable",
            HandoffPhase.WaitingForAutoRetainer => "AutoRetainer processing",
            HandoffPhase.ReturningOriginal => "Starting return",
            HandoffPhase.WaitingForOriginal => "Logging into original character",
            HandoffPhase.ResumingQuestionable => "Restarting Questionable",
            HandoffPhase.WaitingForQuestionableResume => "Confirming Questionable resumed",
            HandoffPhase.AwaitingManualQuestionableResume => "Awaiting manual Questionable resume",
            HandoffPhase.WaitingForAutoRetainerIdle => "Preparing AutoRetainer",
            HandoffPhase.WaitingForHenchmanStart => "Starting Henchman",
            HandoffPhase.WaitingForOceanDuty => "Waiting for Ocean Fishing voyage",
            HandoffPhase.WaitingForOceanCleanup => "Waiting for Ocean Fishing cleanup",
            HandoffPhase.WaitingForHenchmanStop => "Stopping Henchman",
            HandoffPhase.CheckingChainedWork => "Checking chained work",
            HandoffPhase.Error => "Error",
            _ => phase.ToString(),
        };

    private static string FormatAvailability(bool? value) => value switch
    {
        true => "connected",
        false => "unavailable",
        _ => "checking",
    };

    public static string FormatSeconds(long seconds)
    {
        var sign = seconds < 0 ? "-" : string.Empty;
        var remaining = TimeSpan.FromSeconds(Math.Abs(seconds));
        return $"{sign}{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
