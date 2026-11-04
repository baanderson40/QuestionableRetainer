using Dalamud.Plugin.Services;

namespace QuestionableRetainer.Automation;

internal sealed class VerboseDiagnosticLog(IPluginLog log)
{
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Entry> entries = [];

    public void Write(string key, string fingerprint, string message, DateTime nowUtc)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entries[key] = new Entry(fingerprint, nowUtc);
            log.Verbose("{Diagnostic}", message);
            return;
        }

        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            if (nowUtc < entry.LastLoggedUtc + RepeatInterval)
            {
                entries[key] = entry with { Fingerprint = fingerprint, Suppressed = entry.Suppressed + 1 };
                return;
            }

            entries[key] = new Entry(fingerprint, nowUtc);
            log.Verbose("{Diagnostic} SuppressedRepeats={SuppressedRepeats} Changed=true", message, entry.Suppressed);
            return;
        }

        if (nowUtc < entry.LastLoggedUtc + RepeatInterval)
        {
            entries[key] = entry with { Suppressed = entry.Suppressed + 1 };
            return;
        }

        entries[key] = new Entry(fingerprint, nowUtc);
        log.Verbose("{Diagnostic} SuppressedRepeats={SuppressedRepeats}", message, entry.Suppressed);
    }

    public void Reset(string? key = null)
    {
        if (key is null)
            entries.Clear();
        else
            entries.Remove(key);
    }

    private sealed record Entry(string Fingerprint, DateTime LastLoggedUtc, int Suppressed = 0);
}
