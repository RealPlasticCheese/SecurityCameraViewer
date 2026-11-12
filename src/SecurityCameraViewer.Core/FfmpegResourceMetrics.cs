using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SecurityCameraViewer.Core;

internal readonly record struct FfmpegResources(double CpuSeconds, long WorkingSetBytes);

/// <summary>Observes native child work without logging URLs or probing processes when no collector is attached.</summary>
internal sealed class FfmpegResourceMetrics
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Pipeline, int Channel), Group> _groups = [];
    private readonly ObservableCounter<double> _cpu;
    private readonly ObservableGauge<double> _memory;
    private readonly ObservableGauge<double> _count;

    public FfmpegResourceMetrics(Meter meter)
    {
        _cpu = meter.CreateObservableCounter("scv.ffmpeg.cpu.time", () => Observe(0), "s", "Cumulative FFmpeg CPU time, including retired children.");
        _memory = meter.CreateObservableGauge("scv.ffmpeg.working_set", () => Observe(1), "By", "Resident memory of active FFmpeg children.");
        _count = meter.CreateObservableGauge("scv.ffmpeg.processes", () => Observe(2), "{process}", "Active FFmpeg children.");
    }

    public IDisposable Track(Process process, string pipeline, int channel) => Track(pipeline, channel, () =>
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Process.Refresh/WorkingSet64 enumerate Windows process information. Query this child directly instead.
                return WindowsResources.Read(process.SafeHandle);
            }
            process.Refresh();
            return new(process.TotalProcessorTime.TotalSeconds, process.WorkingSet64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Child exit and /proc removal may race collection. Preserve its last observed CPU sample.
            return null;
        }
    });

    internal IDisposable Track(string pipeline, int channel, Func<FfmpegResources?> sample)
    {
        lock (_sync)
        {
            var key = (pipeline, channel);
            if (!_groups.TryGetValue(key, out var group))
            {
                group = new(pipeline, channel);
                _groups.Add(key, group);
            }
            var child = new Child(sample);
            group.Children.Add(child);
            return new Registration(this, group, child);
        }
    }

    private Measurement<double>[] Observe(int kind)
    {
        lock (_sync)
        {
            return _groups.Values.Select(group =>
            {
                double value = kind == 0 ? group.RetiredCpu : kind == 2 ? group.Children.Count : 0;
                if (kind != 2)
                {
                    foreach (var child in group.Children)
                    {
                        var current = child.Sample();
                        child.Cpu = Math.Max(child.Cpu, current?.CpuSeconds ?? 0);
                        value += kind == 0 ? child.Cpu : current?.WorkingSetBytes ?? 0;
                    }
                }
                return new Measurement<double>(value, group.Tags);
            }).ToArray();
        }
    }

    private void Release(Group group, Child child)
    {
        lock (_sync)
        {
            if (!group.Children.Remove(child))
            {
                return;
            }
            if (_cpu.Enabled)
            {
                child.Cpu = Math.Max(child.Cpu, child.Sample()?.CpuSeconds ?? 0);
            }
            group.RetiredCpu += child.Cpu;
        }
    }

    private sealed class Child(Func<FfmpegResources?> sample)
    {
        public Func<FfmpegResources?> Sample { get; } = sample;
        public double Cpu
        {
            get; set;
        }
    }

    private sealed class Group(string pipeline, int channel)
    {
        public KeyValuePair<string, object?>[] Tags { get; } = [new("pipeline", pipeline), new("channel", channel)];
        public HashSet<Child> Children { get; } = [];
        public double RetiredCpu
        {
            get; set;
        }
    }

    private sealed class Registration(FfmpegResourceMetrics owner, Group group, Child child) : IDisposable
    {
        private FfmpegResourceMetrics? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(group, child);
    }

    private static class WindowsResources
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public nuint PeakWorkingSet;
            public nuint WorkingSet;
            public nuint PeakPagedPool;
            public nuint PagedPool;
            public nuint PeakNonPagedPool;
            public nuint NonPagedPool;
            public nuint Pagefile;
            public nuint PeakPagefile;
        }

        public static FfmpegResources? Read(SafeProcessHandle handle)
        {
            if (!GetProcessTimes(handle, out _, out _, out var kernel, out var user))
            {
                return null;
            }
            var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            var hasMemory = GetProcessMemoryInfo(handle, ref memory, memory.Size);
            return new((kernel + user) / 10_000_000d, hasMemory ? (long)memory.WorkingSet : 0);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    }
}
