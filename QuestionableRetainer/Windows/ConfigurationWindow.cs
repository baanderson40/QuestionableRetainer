using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AutoRetainerAPI.Configuration;
using QuestionableRetainer.Automation;

namespace QuestionableRetainer.Windows;

public sealed class ConfigurationWindow : Window
{
    private readonly Configuration configuration;
    private readonly HandoffController controller;
    private readonly Action save;

    public ConfigurationWindow(Configuration configuration, HandoffController controller, Action save)
        : base("Questionable Retainer##configuration", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.controller = controller;
        this.save = save;
        Size = new Vector2(560, 490);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Automation");
        ImGui.Separator();

        DrawSetting("Enable automatic handoffs", configuration.Enabled, value => configuration.Enabled = value);
        DrawSetting("Monitor retainer ventures", configuration.MonitorRetainers, value => configuration.MonitorRetainers = value);
        DrawSetting("Monitor deployables", configuration.MonitorDeployables, value => configuration.MonitorDeployables = value);
        DrawSetting("Monitor Ocean Fishing (experimental)", configuration.MonitorOceanFishing, value => configuration.MonitorOceanFishing = value);
        var characterMode = (int)configuration.OceanFishingCharacterMode;
        var characterModes = new[] { "Current character", "Selected character" };
        if (ImGui.Combo("Ocean Fishing character", ref characterMode, characterModes, characterModes.Length))
        {
            configuration.OceanFishingCharacterMode = (OceanFishingCharacterMode)characterMode;
            save();
        }

        if (configuration.OceanFishingCharacterMode == OceanFishingCharacterMode.SelectedCharacter)
        {
            if (controller.TryGetFishingCharacters(out var fishingCharacters))
            {
                var selected = fishingCharacters.FirstOrDefault(character => character.CID == configuration.OceanFishingCharacterContentId);
                var currentLabel = selected is null
                                       ? "Select registered character"
                                       : $"{selected.Name}@{selected.World}";
                if (ImGui.BeginCombo("Fishing character", currentLabel))
                {
                    foreach (var character in fishingCharacters)
                    {
                        var label = $"{character.Name}@{character.World}##{character.CID}";
                        if (ImGui.Selectable(label, character.CID == configuration.OceanFishingCharacterContentId))
                        {
                            configuration.OceanFishingCharacterContentId = character.CID;
                            configuration.OceanFishingCharacterName = character.Name;
                            configuration.OceanFishingCharacterWorld = character.World;
                            save();
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else
                ImGui.TextWrapped("AutoRetainer character list unavailable.");
        }
        var oceanLeadMinutes = configuration.OceanFishingLeadMinutes;
        if (ImGui.InputInt("Ocean Fishing lead minutes", ref oceanLeadMinutes))
        {
            configuration.OceanFishingLeadMinutes = Math.Clamp(oceanLeadMinutes, 0, 30);
            save();
        }
        DrawSetting("Print chat notifications", configuration.ChatNotifications, value => configuration.ChatNotifications = value);
        DrawSetting("Show toast notifications", configuration.ToastNotifications, value => configuration.ToastNotifications = value);
        if (ImGui.Button("Reset Plugin State"))
            controller.ResetPluginState();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears QR handoff/recovery state and disables monitoring. Does not stop or restart other plugin automation.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted($"Status: {controller.Monitoring.DisplayState}");
            ImGui.TextWrapped($"Reason: {controller.Monitoring.Reason}");
            ImGui.TextUnformatted($"Handoff: {MonitoringStatusFormatter.FormatPhase(controller.Phase)}");
            ImGui.TextUnformatted($"Current leg: {controller.Monitoring.CurrentHandoffLeg?.ToString() ?? "none"}");
            if (controller.Monitoring.TriggerReadyCharacterCount is { } triggerReady)
            {
                ImGui.TextUnformatted($"Trigger scan: {triggerReady} / {controller.Monitoring.TriggerRegisteredCharacterCount} characters ready");
                ImGui.TextUnformatted($"Triggered at: {FormatTimestamp(controller.Monitoring.TriggerDetectedUtc)}");
                ImGui.TextUnformatted($"Trigger closest retainer: {MonitoringStatusFormatter.FormatReadyWorkCountdown(controller.Monitoring.TriggerClosestRetainerSeconds)}");
            }
            else
            {
                ImGui.TextUnformatted($"Ready characters: {controller.Monitoring.ReadyCharacterCount} / {controller.Monitoring.RegisteredCharacterCount}");
            }
            ImGui.TextUnformatted($"Questionable: {FormatAvailability(controller.Monitoring.QuestionableAvailable)} " +
                                  $"Running: {FormatOptionalBool(controller.Monitoring.QuestionableRunning)} " +
                                  $"Quest: {FormatQuest(controller.Monitoring.QuestionableQuestId)}");
            ImGui.TextUnformatted($"AutoRetainer: {FormatAvailability(controller.Monitoring.AutoRetainerAvailable)} " +
                                  $"Busy: {controller.Monitoring.AutoRetainerBusy} " +
                                  $"Multi Mode: {controller.Monitoring.AutoRetainerMultiModeActive}");
            ImGui.TextUnformatted($"Lifestream: {FormatAvailability(controller.Monitoring.LifestreamAvailable)} " +
                                  $"Busy: {controller.Monitoring.LifestreamBusy}");
            ImGui.TextUnformatted($"WahTools: {FormatAvailability(controller.Monitoring.WahToolsAvailable)} " +
                                  $"Launch accepted: {controller.Monitoring.WahToolsLaunchAccepted}");
            if (controller.Monitoring.WahToolsStatus is { } feeshStatus)
            {
                ImGui.TextWrapped($"WahTools trip: {feeshStatus.TripState ?? "unknown"} — {feeshStatus.TripStatus ?? feeshStatus.Detail}");
                ImGui.TextWrapped($"WahTools cleanup: cleaning={feeshStatus.Cleaning?.ToString() ?? "unknown"}; cleaner={feeshStatus.CleanerState ?? "unknown"} — {feeshStatus.CleanerStatus ?? ""}; desynth={feeshStatus.DesyntherRunning?.ToString() ?? "unknown"}; discard={feeshStatus.DiscarderRunning?.ToString() ?? "unknown"}; buyer={feeshStatus.BuyerBusy?.ToString() ?? "unknown"}");
                ImGui.TextUnformatted($"WahTools snapshot age: {Math.Max(0, (DateTime.UtcNow - feeshStatus.ObservedAtUtc).TotalSeconds):0.0}s");
            }
            ImGui.TextUnformatted($"YesAlready lease: {controller.Monitoring.YesAlreadyLeaseAcquired}");
            ImGui.TextUnformatted($"Recovery: {controller.Monitoring.RecoveryAction} " +
                                  $"Failure: {controller.Monitoring.RecoveryFailureCode?.ToString() ?? "none"} " +
                                  $"Attempt: {controller.Monitoring.RecoveryAttemptCount}");
            if (controller.Monitoring.RecoveryNextAttemptUtc is { } nextRecovery)
                ImGui.TextUnformatted($"Next recovery attempt: {nextRecovery.ToLocalTime():HH:mm:ss}");
            ImGui.TextUnformatted($"Ocean Fishing: {FormatTimestamp(controller.Monitoring.NextOceanWindowUtc)} " +
                                  $"Eligible: {controller.Monitoring.OceanLaunchEligible} " +
                                  $"Handled: {controller.Monitoring.OceanWindowHandled}");
            ImGui.TextUnformatted($"Ocean duty: started={controller.Monitoring.OceanDutyStarted} " +
                                  $"completed={controller.Monitoring.OceanDutyCompleted} " +
                                  $"stable since={FormatTimestamp(controller.Monitoring.OceanCleanupStableSinceUtc)}");
            if (!string.IsNullOrWhiteSpace(controller.Monitoring.OceanCleanupWaitReason))
                ImGui.TextWrapped($"Cleanup: {controller.Monitoring.OceanCleanupWaitReason}");
            if (controller.Monitoring.OceanCleanupStableElapsedSeconds is { } stableElapsed)
                ImGui.TextUnformatted($"Cleanup stability ({controller.Monitoring.OceanCleanupConfirmationSource}): " +
                                      $"{stableElapsed:0.0}/{controller.Monitoring.OceanCleanupRequiredSeconds?.ToString("0.0") ?? "?"} seconds");
            ImGui.TextUnformatted($"Current content ID: {controller.Monitoring.CurrentContentId?.ToString() ?? "none"} " +
                                  $"Fishing target: {controller.Monitoring.FishingCharacterContentId?.ToString() ?? "none"}");
            ImGui.TextUnformatted($"Chained legs: Ocean resolved={controller.Monitoring.OceanLegResolved} " +
                                  $"AutoRetainer resolved={controller.Monitoring.AutoRetainerLegResolved} " +
                                  $"pending AutoRetainer={controller.Monitoring.PendingAutoRetainerMode?.ToString() ?? "none"}");
            ImGui.TextUnformatted($"Readiness scan: {FormatAvailability(controller.Monitoring.ReadinessScanAvailable)}");
            ImGui.TextUnformatted($"Player blockers: {MonitoringStatusFormatter.FormatPlayerBlockers(controller.Monitoring.PlayerBlockers)}");
            if (controller.Monitoring.TriggerReadyCharacterCount is null)
                ImGui.TextUnformatted($"Closest retainer: {MonitoringStatusFormatter.FormatReadyWorkCountdown(controller.Monitoring.ClosestRetainerSeconds)}");
            ImGui.TextUnformatted($"Last readiness scan: {FormatTimestamp(controller.Monitoring.LastReadinessScanUtc)}");
            ImGui.TextUnformatted($"Last dependency check: {FormatTimestamp(controller.Monitoring.LastDependencyCheckUtc)}");

            if (controller.Session is { } session)
            {
                ImGui.TextUnformatted($"Original character: {session.OriginalName}@{session.OriginalWorld}");
                if (session.Kind == HandoffKind.OceanFishing)
                    ImGui.TextUnformatted($"Fishing character: {session.FishingName}@{session.FishingWorld}");
                ImGui.TextUnformatted($"Quest: {session.QuestId}");
            }

            if (!string.IsNullOrWhiteSpace(controller.LastError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
                ImGui.TextWrapped(controller.LastError);
                ImGui.PopStyleColor();
            }

            if (controller.Phase == HandoffPhase.AwaitingManualQuestionableResume)
            {
                ImGui.TextWrapped("QR recovered a persisted handoff. It will not start Questionable automatically after reload.");
                if (ImGui.Button("Resume Questionable"))
                    controller.ResumeQuestionable();
                ImGui.SameLine();
                if (ImGui.Button("Discard Recovery"))
                    controller.CancelRecovery();
            }
            else if (controller.Phase is HandoffPhase.Error or HandoffPhase.ManualIntervention)
            {
                var manualFeeshRecovery = controller.Phase == HandoffPhase.ManualIntervention && controller.Session?.OceanWindowStartUtc is not null;
                if (manualFeeshRecovery)
                {
                    if (ImGui.Button("Feesh is stopped — continue return"))
                        controller.ConfirmFeeshStopped();
                    ImGui.SameLine();
                }

                if (!manualFeeshRecovery)
                {
                    if (ImGui.Button("Retry Now"))
                        controller.Recover();
                    ImGui.SameLine();
                }
                if (ImGui.Button("Discard Recovery"))
                    controller.CancelRecovery();
            }
        }

    }

    private void DrawSetting(string label, bool current, Action<bool> update)
    {
        var value = current;
        if (!ImGui.Checkbox(label, ref value))
            return;

        update(value);
        save();
    }

    private static string FormatOptionalBool(bool? value) => value?.ToString() ?? "Unknown";

    private static string FormatAvailability(bool? value) => value switch
    {
        true => "Connected",
        false => "Unavailable",
        _ => "Checking...",
    };

    private static string FormatQuest(string questId) => string.IsNullOrWhiteSpace(questId) ? "none" : questId;

    private static string FormatTimestamp(DateTime? timestamp) => timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "never";
}
