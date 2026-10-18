using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VoxelBrutal.Demo;

/// <summary>
/// Windows' actual adapter utilization, sampled once per second outside the render thread.
/// Process counters are summed per physical GPU engine; the busiest engine represents the adapter,
/// following Task Manager's convention. This is device-wide utilization, including other programs.
/// Missing counters, an unknown Vulkan adapter LUID, and counter warm-up are reported as unavailable.
/// </summary>
public sealed class GpuUsageMonitor : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MaximumCounterBuffer = 64 * 1024 * 1024;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private double latest = double.NaN;
    private long latestTimestamp;
    private string status;
    private string? source;
    private int disposed;

    /// <summary>Null means unavailable or warming up. A measured idle adapter can legitimately read zero.</summary>
    public double? GpuUsagePercent
    {
        get
        {
            double value = Volatile.Read(ref latest);
            long timestamp = Volatile.Read(ref latestTimestamp);
            return double.IsFinite(value) && timestamp != 0 && Stopwatch.GetElapsedTime(timestamp).TotalSeconds < 5
                ? value : null;
        }
    }

    public string Status => Volatile.Read(ref status);
    public string? Source => Volatile.Read(ref source);

    public GpuUsageMonitor(ulong? adapterLuid, string? adapterUuid = null)
    {
        status = !OperatingSystem.IsWindows() ? "Unavailable on this operating system" :
            adapterLuid is null && string.IsNullOrWhiteSpace(adapterUuid) ? "Vulkan adapter identity unavailable" :
            "Warming up GPU counters";
        worker = OperatingSystem.IsWindows() && (adapterLuid is not null || !string.IsNullOrWhiteSpace(adapterUuid))
            ? Task.Run(() => CollectAsync(adapterLuid, adapterUuid, stopping.Token)) : Task.CompletedTask;
    }

    private async Task CollectAsync(ulong? adapterLuid, string? adapterUuid, CancellationToken cancellationToken)
    {
        nint query = 0, buffer = 0;
        nint counter = 0;
        int capacity = 0;
        NvmlSession? nvml = null;
        long nextPdhRetry = 0, nextNvmlRetry = 0;
        bool pdhAvailable = adapterLuid is not null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                string unavailable = "Vulkan adapter counters unavailable or warming up";
                bool pdhOpenedThisSample = false;
                if (query == 0 && pdhAvailable && now >= nextPdhRetry)
                {
                    nextPdhRetry = now + Stopwatch.Frequency * 10;
                    try
                    {
                        uint error = PdhOpenQueryW(null, 0, out query);
                        if (error == 0)
                            error = PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out counter);
                        if (error == 0)
                        {
                            // Rate counters require two collections one measurement interval apart.
                            PdhCollectQueryData(query);
                            pdhOpenedThisSample = true;
                        }
                        else
                        {
                            unavailable = $"Windows GPU counters unavailable (0x{error:X8})";
                            if (query != 0) { PdhCloseQuery(query); query = 0; }
                        }
                    }
                    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                    {
                        pdhAvailable = false;
                        unavailable = "Windows GPU counter API unavailable";
                        if (query != 0) { PdhCloseQuery(query); query = 0; }
                    }
                }
                double? measured = null;
                if (query != 0 && !pdhOpenedThisSample)
                {
                    uint error = PdhCollectQueryData(query);
                    if (error != 0)
                    {
                        unavailable = $"Windows GPU sample unavailable (0x{error:X8})";
                        PdhCloseQuery(query); query = 0;
                    }
                    else
                    {
                        measured = ReadUtilization(counter, adapterLuid!.Value, ref buffer, ref capacity, out uint readError);
                        if (readError != 0) unavailable = $"Windows GPU values unavailable (0x{readError:X8})";
                    }
                }
                if (measured is double pdhUsage)
                    Publish(pdhUsage, "Windows GPU engines", "Device-wide busiest GPU engine utilization");
                else
                {
                    // Some Windows installations/drivers expose no GPU Engine provider. The NVIDIA
                    // driver offers actual device utilization independently, matched strictly by UUID.
                    if (nvml is null && now >= nextNvmlRetry && !string.IsNullOrWhiteSpace(adapterUuid))
                    {
                        nextNvmlRetry = now + Stopwatch.Frequency * 10;
                        nvml = NvmlSession.TryCreate(adapterUuid);
                    }
                    if (nvml?.ReadUtilization() is double nvmlUsage)
                        Publish(nvmlUsage, "NVIDIA NVML", "Device-wide NVIDIA GPU utilization");
                    else
                        PublishUnavailable(unavailable);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Telemetry must never terminate rendering because a driver/provider is missing or fails.
            PublishUnavailable($"GPU monitoring unavailable ({exception.GetType().Name})");
        }
        finally
        {
            if (query != 0) PdhCloseQuery(query);
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            nvml?.Dispose();
        }
    }

    private static double? ReadUtilization(nint counter, ulong adapterLuid, ref nint buffer, ref int capacity,
        out uint error)
    {
        // Instances can appear/disappear between the size probe and the data read. Retry from a
        // fresh zero-sized probe; PDH explicitly does not guarantee a useful size on a short read.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            error = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref bytes, out _, 0);
            if (error != MoreData || bytes == 0 || bytes > MaximumCounterBuffer) return null;
            if (bytes > capacity)
            {
                nint replacement = Marshal.AllocHGlobal(checked((int)bytes));
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
                buffer = replacement;
                capacity = (int)bytes;
            }
            bytes = (uint)capacity;
            error = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref bytes, out uint count, buffer);
            if (error == MoreData) continue;
            if (error != 0) return null;
            int stride = Marshal.SizeOf<CounterItem>();
            if ((ulong)count * (uint)stride > bytes) return null;
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < count; index++)
            {
                CounterItem item = Marshal.PtrToStructure<CounterItem>(buffer + checked((int)index * stride));
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.DoubleValue) || item.Value.DoubleValue < 0)
                    continue;
                string? name = Marshal.PtrToStringUni(item.Name);
                if (name is null || !TryGetEngine(name, adapterLuid, out string engine)) continue;
                engineTotals.TryGetValue(engine, out double sum);
                double total = sum + item.Value.DoubleValue;
                if (!double.IsFinite(total)) return null;
                engineTotals[engine] = total;
            }
            if (engineTotals.Count == 0) return null;
            // Distinct engines run concurrently: adding them would report bogus values above 100%.
            return Math.Clamp(engineTotals.Values.Max(), 0, 100);
        }
        error = MoreData;
        return null;
    }

    private static bool TryGetEngine(string name, ulong adapterLuid, out string engine)
    {
        engine = string.Empty;
        const string marker = "luid_0x";
        int start = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        ReadOnlySpan<char> span = name.AsSpan(start + marker.Length);
        int separator = span.IndexOf("_0x", StringComparison.OrdinalIgnoreCase);
        if (separator < 1 || !uint.TryParse(span[..separator], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high))
            return false;
        span = span[(separator + 3)..];
        int end = span.IndexOf('_');
        if (end < 1 || !uint.TryParse(span[..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low) ||
            ((ulong)high << 32 | low) != adapterLuid) return false;
        int physical = name.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
        int engineId = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
        if (physical < 0 || engineId < physical) return false;
        int engineEnd = name.IndexOf('_', engineId + 5);
        if (engineEnd < 0) engineEnd = name.Length;
        engine = name[physical..engineEnd];
        return true;
    }

    private void PublishUnavailable(string reason)
    {
        Volatile.Write(ref latest, double.NaN);
        Volatile.Write(ref latestTimestamp, 0);
        Volatile.Write(ref status, reason);
        Volatile.Write(ref source, null);
    }

    private void Publish(double measured, string provider, string description)
    {
        Volatile.Write(ref latestTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref latest, measured);
        Volatile.Write(ref source, provider);
        Volatile.Write(ref status, description);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        worker.GetAwaiter().GetResult();
        stopping.Dispose();
        PublishUnavailable("GPU monitoring stopped");
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    private sealed class NvmlSession : IDisposable
    {
        private readonly nint library, device;
        private readonly NvmlShutdown shutdown;
        private readonly NvmlReadUsage readUsage;

        private NvmlSession(nint library, nint device, NvmlShutdown shutdown, NvmlReadUsage readUsage)
        { this.library = library; this.device = device; this.shutdown = shutdown; this.readUsage = readUsage; }

        public static NvmlSession? TryCreate(string adapterUuid)
        {
            string uuid = adapterUuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) ? adapterUuid[4..] : adapterUuid;
            if (!Guid.TryParse(uuid, out Guid canonical)) return null;
            string[] paths = [Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")];
            foreach (string path in paths)
            {
                if (!NativeLibrary.TryLoad(path, out nint library)) continue;
                NvmlShutdown? shutdown = null;
                bool initialized = false;
                try
                {
                    var initialize = Marshal.GetDelegateForFunctionPointer<NvmlInitialize>(NativeLibrary.GetExport(library, "nvmlInit_v2"));
                    shutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdown>(NativeLibrary.GetExport(library, "nvmlShutdown"));
                    var find = Marshal.GetDelegateForFunctionPointer<NvmlFindDevice>(NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByUUID"));
                    var read = Marshal.GetDelegateForFunctionPointer<NvmlReadUsage>(NativeLibrary.GetExport(library, "nvmlDeviceGetUtilizationRates"));
                    initialized = initialize() == 0;
                    if (initialized && find("GPU-" + canonical.ToString("D"), out nint device) == 0)
                        return new(library, device, shutdown, read);
                }
                catch (Exception) { /* A missing/incompatible optional vendor API remains unavailable. */ }
                if (initialized) shutdown?.Invoke();
                NativeLibrary.Free(library);
            }
            return null;
        }

        public double? ReadUtilization() => readUsage(device, out NvmlUtilization usage) == 0 && usage.Gpu <= 100
            ? usage.Gpu : null;

        public void Dispose()
        {
            shutdown();
            NativeLibrary.Free(library);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization { public uint Gpu, Memory; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlInitialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlShutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlFindDevice([MarshalAs(UnmanagedType.LPStr)] string uuid, out nint device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlReadUsage(nint device, out NvmlUtilization usage);
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint itemCount, nint buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
