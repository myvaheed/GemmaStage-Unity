using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GemmaStage.Session.PoC;

internal sealed record ProcessMemorySnapshot(
    ulong? VramBytes,
    string VramSource,
    ulong? RssBytes);

internal sealed class RuntimeMemorySampler
{
    private const ulong NvmlValueNotAvailable = 0xFFFFFFFFFFFFFFFFul;
    private const int NvmlSuccess = 0;

    public ProcessMemorySnapshot Sample(string? backendName)
    {
        var rssBytes = QueryWorkingSetBytes();

        if (string.Equals(backendName, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            var nvmlBytes = QueryNvmlProcessUsedBytes();
            if (nvmlBytes > 0)
            {
                return new ProcessMemorySnapshot(
                    nvmlBytes,
                    "NVML(process)",
                    rssBytes);
            }
        }

        return new ProcessMemorySnapshot(
            rssBytes,
            "WorkingSet(fallback)",
            rssBytes);
    }

    private static ulong QueryWorkingSetBytes()
    {
        using var process = Process.GetCurrentProcess();
        return checked((ulong)Math.Max(0, process.WorkingSet64));
    }

    private static ulong? QueryNvmlProcessUsedBytes()
    {
        nint library = nint.Zero;
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out library) &&
                !NativeLibrary.TryLoad("nvml64.dll", out library))
            {
                return null;
            }

            var init = GetExport<NvmlInit>(library, "nvmlInit_v2");
            var shutdown = GetExport<NvmlShutdown>(library, "nvmlShutdown");
            var getCount = GetExport<NvmlDeviceGetCount>(library, "nvmlDeviceGetCount_v2");
            var getHandle = GetExport<NvmlDeviceGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getProcesses = GetExport<NvmlDeviceGetComputeRunningProcesses>(
                library,
                "nvmlDeviceGetComputeRunningProcesses");

            if (init is null || shutdown is null || getCount is null ||
                getHandle is null || getProcesses is null)
            {
                return null;
            }

            if (init() != NvmlSuccess)
            {
                return null;
            }

            try
            {
                if (getCount(out var deviceCount) != NvmlSuccess || deviceCount == 0)
                {
                    return null;
                }

                var currentPid = checked((uint)Environment.ProcessId);
                ulong processBytes = 0;
                for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    if (getHandle(deviceIndex, out var device) != NvmlSuccess || device == nint.Zero)
                    {
                        continue;
                    }

                    var processCount = 128u;
                    var processes = new NvmlProcessInfo[processCount];
                    var rc = getProcesses(device, ref processCount, processes);
                    if (rc != NvmlSuccess)
                    {
                        continue;
                    }

                    var count = Math.Min(processCount, checked((uint)processes.Length));
                    for (var i = 0; i < count; i++)
                    {
                        if (processes[i].Pid == currentPid &&
                            processes[i].UsedGpuMemory != NvmlValueNotAvailable)
                        {
                            processBytes += processes[i].UsedGpuMemory;
                        }
                    }
                }

                return processBytes > 0 ? processBytes : null;
            }
            finally
            {
                shutdown();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (library != nint.Zero)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    private static T? GetExport<T>(nint library, string name)
        where T : Delegate
    {
        return NativeLibrary.TryGetExport(library, name, out var export)
            ? Marshal.GetDelegateForFunctionPointer<T>(export)
            : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCount(out uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetComputeRunningProcesses(
        nint device,
        ref uint infoCount,
        [Out] NvmlProcessInfo[] infos);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
    }
}
