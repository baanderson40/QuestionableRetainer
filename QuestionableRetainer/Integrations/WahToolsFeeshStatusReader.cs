using System.Collections;
using System.Reflection;
using ECommons.Reflection;
using Dalamud.Plugin.Services;
using QuestionableRetainer.Automation;

namespace QuestionableRetainer.Integrations;

public sealed class WahToolsFeeshStatusReader(IPluginLog log)
{
    private const string FeeshModuleTypeName = "WahTools.Modules.Feesh.FeeshModule";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Dictionary<(Type Type, string Name), MemberInfo> memberCache = [];
    private string lastUnavailableMessage = string.Empty;
    private Type? cachedFeeshModuleType;
    private WeakReference<object>? currentModule;
    private long instanceGeneration;

    public WahToolsFeeshStatus Read()
    {
        var now = DateTime.UtcNow;
        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin("WahTools", out var plugin, suppressErrors: true, ignoreCache: true) || plugin is null)
            {
                ClearMemberCache();
                return ReportUnavailable(WahToolsStatusAvailability.NotInstalled, "WahTools is not loaded.", now);
            }

            var moduleManager = ReadMember(plugin, "Modules");
            var modules = ReadMember(moduleManager, "Modules") as IEnumerable;
            if (modules is null)
            {
                ClearMemberCache();
                return ReportUnavailable(WahToolsStatusAvailability.Incompatible, "WahTools module collection is unavailable.", now);
            }

            object? feesh = null;
            foreach (var module in modules)
            {
                if (module?.GetType().FullName == FeeshModuleTypeName)
                {
                    feesh = module;
                    break;
                }
            }

            if (feesh is null)
            {
                ClearMemberCache();
                return ReportUnavailable(WahToolsStatusAvailability.Incompatible, "WahTools Feesh module was not found.", now);
            }

            if (cachedFeeshModuleType != feesh.GetType())
            {
                ClearMemberCache();
                cachedFeeshModuleType = feesh.GetType();
            }

            if (currentModule is null ||
                !currentModule.TryGetTarget(out var previousModule) ||
                !ReferenceEquals(previousModule, feesh))
            {
                currentModule = new WeakReference<object>(feesh);
                instanceGeneration++;
            }

            if (ReadRequired<bool>(feesh, "IsLoaded") != true)
            {
                ClearMemberCache();
                return ReportUnavailable(WahToolsStatusAvailability.NotInstalled, "WahTools Feesh module is not loaded.", now);
            }

            var trip = ReadPrivateField(feesh, "trip");
            var cleaner = ReadRequiredObject(feesh, "Cleaner");
            var desynther = ReadRequiredObject(feesh, "Desynther");
            var discarder = ReadRequiredObject(feesh, "Discarder");
            var buyer = ReadRequiredObject(feesh, "Buyer");
            var ocean = ReadMember(feesh, "Ocean");

            var status = new WahToolsFeeshStatus(
                WahToolsStatusAvailability.Available,
                now,
                string.Empty,
                trip is not null,
                trip is null ? "Idle" : ReadOptionalString(trip, "State"),
                trip is null ? "Trip not initialized" : ReadOptionalString(trip, "StatusText"),
                trip is null ? false : ReadOptional<bool>(trip, "IsRunning"),
                ReadRequired<bool>(ocean, "OnBoat"),
                ReadRequired<bool>(feesh, "Cleaning"),
                ReadOptionalString(cleaner, "State"),
                ReadOptionalString(cleaner, "StatusText"),
                ReadRequired<bool>(cleaner, "Running"),
                ReadRequired<bool>(desynther, "Running"),
                ReadOptionalString(desynther, "Status"),
                ReadRequired<bool>(discarder, "Running"),
                ReadOptionalString(discarder, "Status"),
                ReadRequired<bool>(buyer, "Busy"));

            if (lastUnavailableMessage.Length > 0)
            {
                log.Information("WahTools Feesh status reflection recovered.");
                lastUnavailableMessage = string.Empty;
            }

            return status with { Generation = instanceGeneration };
        }
        catch (MissingMemberException ex)
        {
            ClearMemberCache();
            var message = $"WahTools Feesh reflection layout is incompatible: {ex.Message}";
            return ReportUnavailable(WahToolsStatusAvailability.Incompatible, message, now, ex);
        }
        catch (Exception ex)
        {
            ClearMemberCache();
            var message = $"WahTools Feesh status read failed: {ex.GetBaseException().Message}";
            return ReportUnavailable(WahToolsStatusAvailability.ReadFailed, message, now, ex);
        }
    }

    public void Invalidate()
    {
        lastUnavailableMessage = string.Empty;
        ClearMemberCache();
    }

    private WahToolsFeeshStatus ReportUnavailable(
        WahToolsStatusAvailability availability,
        string message,
        DateTime observedAtUtc,
        Exception? exception = null)
    {
        var key = $"{availability}:{message}";
        if (!string.Equals(lastUnavailableMessage, key, StringComparison.Ordinal))
        {
            if (exception is null)
                log.Warning("{Message}", message);
            else
                log.Warning(exception, "{Message}", message);
            lastUnavailableMessage = key;
        }

        return WahToolsFeeshStatus.Unavailable(availability, message, observedAtUtc) with { Generation = instanceGeneration };
    }

    private object ReadRequiredObject(object? target, string memberName) =>
        ReadMember(target, memberName) ?? throw new MissingMemberException($"WahTools Feesh member {memberName} is null.");

    private T ReadRequired<T>(object? target, string memberName)
    {
        var value = ReadMember(target, memberName);
        return value is T typed
                   ? typed
                   : throw new MissingMemberException($"WahTools Feesh member {memberName} is missing or has an unexpected type.");
    }

    private T? ReadOptional<T>(object? target, string memberName)
        where T : struct => ReadMember(target, memberName) is T value ? value : null;

    private string? ReadOptionalString(object? target, string memberName) =>
        ReadMember(target, memberName)?.ToString();

    private object? ReadPrivateField(object target, string fieldName)
    {
        var member = GetCachedMember(target.GetType(), fieldName, privateFieldOnly: true);
        return member is FieldInfo field
                   ? field.GetValue(target)
                   : throw new MissingFieldException(target.GetType().FullName, fieldName);
    }

    private object? ReadMember(object? target, string memberName)
    {
        if (target is null)
            throw new MissingMemberException($"WahTools Feesh target is null while reading {memberName}.");

        var type = target.GetType();
        var member = GetCachedMember(type, memberName);
        if (member is PropertyInfo property)
            return property.GetValue(target);
        if (member is FieldInfo field)
            return field.GetValue(target);

        throw new MissingMemberException(type.FullName, memberName);
    }

    private MemberInfo GetCachedMember(Type type, string memberName, bool privateFieldOnly = false)
    {
        if (memberCache.TryGetValue((type, memberName), out var cached))
            return cached;

        MemberInfo? member = privateFieldOnly
                                 ? type.GetField(memberName, BindingFlags.Instance | BindingFlags.NonPublic)
                                 : type.GetProperty(memberName, InstanceMembers) ?? (MemberInfo?)type.GetField(memberName, InstanceMembers);
        if (member is null)
            throw new MissingMemberException(type.FullName, memberName);

        memberCache[(type, memberName)] = member;
        return member;
    }

    private void ClearMemberCache()
    {
        memberCache.Clear();
        cachedFeeshModuleType = null;
        if (currentModule is not null)
            instanceGeneration++;
        currentModule = null;
    }
}
