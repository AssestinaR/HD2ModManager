using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HD2ModCli;

/// <summary>System and process counters only; never reads game address space.</summary>
public sealed class ArmorMemoryMetrics : IDisposable
{
    private const int PdhFmtLarge = 0x400;
    private readonly IntPtr query;
    private readonly Dictionary<string, IntPtr> counters = new();
    private static readonly string[] MemoryCounters =
    [
        "Standby Cache Core Bytes", "Standby Cache Normal Priority Bytes",
        "Standby Cache Reserve Bytes", "Modified Page List Bytes",
        "Free & Zero Page List Bytes"
    ];

    public ArmorMemoryMetrics()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
        foreach (var name in MemoryCounters)
        {
            if (PdhAddEnglishCounter(query, @"\Memory\" + name, IntPtr.Zero, out var handle) == 0)
                counters[name] = handle;
        }
    }

    public object Capture(Process? game, int? expectedPid)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var physicalOk = GlobalMemoryStatusEx(ref memory);
        var perf = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        var commitOk = GetPerformanceInfo(ref perf, perf.Size);
        var values = new Dictionary<string, long?>();
        var collected = query != IntPtr.Zero && PdhCollectQueryData(query) == 0;
        foreach (var name in MemoryCounters)
        {
            long? value = null;
            if (collected && counters.TryGetValue(name, out var handle)
                && PdhGetFormattedCounterValue(handle, PdhFmtLarge, out _, out var formatted) == 0
                && formatted.Status is 0 or 1 && formatted.Value >= 0)
                value = formatted.Value;
            values[ToKey(name)] = value;
        }

        object? process = null;
        if (game is not null && expectedPid == game.Id)
        {
            try
            {
                game.Refresh();
                if (!game.HasExited)
                    process = new
                    {
                        pid = game.Id, workingSetBytes = game.WorkingSet64,
                        privateBytes = game.PrivateMemorySize64,
                        virtualBytes = game.VirtualMemorySize64,
                        pagedBytes = game.PagedMemorySize64,
                        handles = game.HandleCount, threads = game.Threads.Count
                    };
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process = new { pid = game.Id, state = "unavailable" };
            }
        }
        return new
        {
            utc = DateTimeOffset.UtcNow,
            physicalTotalBytes = physicalOk ? memory.TotalPhysical : (ulong?)null,
            physicalAvailableBytes = physicalOk ? memory.AvailablePhysical : (ulong?)null,
            commitUsedBytes = commitOk ? (ulong)perf.CommitTotal * (ulong)perf.PageSize : (ulong?)null,
            commitLimitBytes = commitOk ? (ulong)perf.CommitLimit * (ulong)perf.PageSize : (ulong?)null,
            standbyCoreBytes = values["standbyCacheCoreBytes"],
            standbyNormalBytes = values["standbyCacheNormalPriorityBytes"],
            standbyReserveBytes = values["standbyCacheReserveBytes"],
            modifiedPageBytes = values["modifiedPageListBytes"],
            freeZeroBytes = values["freeZeroPageListBytes"],
            process
        };
    }

    public static async Task SampleAsync(string directory, CancellationToken stop, Func<Process?> game,
        Func<int?> expectedPid, int intervalMs = 250)
    {
        using var metrics = new ArmorMemoryMetrics();
        using var output = new FileStream(Path.Combine(directory, "samples-memory.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var sample = 0;
        while (!stop.IsCancellationRequested && sample < 3600)
        {
            try
            {
                var line = JsonSerializer.Serialize(metrics.Capture(game(), expectedPid())) + "\n";
                output.Write(Encoding.UTF8.GetBytes(line));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                break;
            }
            if (++sample % 4 == 0) output.Flush(true);
            try { await Task.Delay(intervalMs, stop); }
            catch (OperationCanceledException) { break; }
        }
        output.Flush(true);
    }

    public static object Clean(bool aggressive)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator))
            return new { attempted = false, aggressive, result = "requires_administrator",
                steps = Array.Empty<object>() };
        var steps = new List<object>();
        foreach (var (name, value) in CleanSteps(aggressive))
        {
            var command = value; // SYSTEM_MEMORY_LIST_COMMAND from the Mem Reduct source.
            var status = NtSetSystemInformation(80, ref command, sizeof(int)); // SystemMemoryListInformation
            steps.Add(new { name, accepted = status >= 0, ntstatus = $"0x{status:x8}" });
        }
        return new { attempted = true, aggressive, result = "completed_with_status",
            steps = steps.ToArray() };
    }

    public static IReadOnlyList<(string Name, int Command)> CleanSteps(bool aggressive)
        => aggressive
            ? [("empty_working_sets", 2), ("flush_modified_list", 3), ("purge_standby", 4)]
            : [("purge_standby", 4)];

    private static string ToKey(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return char.ToLowerInvariant(words[0][0]) + words[0][1..]
            + string.Concat(words.Skip(1).Where(w => w != "&").Select(w => w[..1].ToUpperInvariant() + w[1..]));
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInfo
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public UIntPtr SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhValue
    {
        public int Status, Padding;
        public long Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInfo buffer, uint size);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, out int type, out PdhValue value);
    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);
}
