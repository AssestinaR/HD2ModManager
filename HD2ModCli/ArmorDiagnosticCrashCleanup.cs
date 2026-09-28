using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HD2ModCli;

/// <summary>Only terminates the exact process named by a matching WER crash event.</summary>
public static class ArmorDiagnosticCrashCleanup
{
    public static bool HasMatchingCrash(string eventsJson, int pid, DateTime startedUtc)
    {
        using var document = JsonDocument.Parse(eventsJson);
        if (!document.RootElement.TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Array) return false;
        var started = startedUtc.ToUniversalTime().ToFileTimeUtc();
        foreach (var row in events.EnumerateArray())
        {
            if (!row.TryGetProperty("eventId", out var eventId) || eventId.GetInt32() != 1000
                || !row.TryGetProperty("ProcessId", out var rawPid)
                || !row.TryGetProperty("ProcessCreationTime", out var rawStart)
                || !TryHex(rawPid.GetString(), out var eventPid)
                || !TryHex(rawStart.GetString(), out var eventStart)) continue;
            if (eventPid == (ulong)pid && Math.Abs((long)eventStart - started) <= TimeSpan.TicksPerMillisecond)
                return true;
        }
        return false;
    }

    public static string TryCleanup(Process game, DateTime startedUtc, string expectedExe, string eventsJson)
    {
        if (!HasMatchingCrash(eventsJson, game.Id, startedUtc)) return "no_matching_crash_event";
        try
        {
            game.Refresh();
            if (game.HasExited) return "already_exited";
            if (game.StartTime.ToUniversalTime() != startedUtc.ToUniversalTime())
                return "process_identity_changed";
            if (!string.Equals(game.MainModule?.FileName, Path.GetFullPath(expectedExe),
                StringComparison.OrdinalIgnoreCase))
                return "executable_identity_unverified";
            // No process-tree kill: Steam and the anti-cheat are never targets.
            game.Kill(entireProcessTree: false);
            return game.WaitForExit(10000) ? "terminated_verified_crash_residual" : "termination_timeout";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
            or InvalidOperationException or System.UnauthorizedAccessException)
        {
            return "process_access_unavailable";
        }
    }

    private static bool TryHex(string? text, out ulong value)
        => ulong.TryParse(text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
                ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}
