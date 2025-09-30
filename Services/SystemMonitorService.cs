using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Bluetask.Models;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Bluetask.Services
{
    public class SystemMonitorService
    {
        public static SystemMonitorService Shared { get; } = new SystemMonitorService();

        private readonly Computer _computer;
        private bool _computerOpened;
        private readonly object _updateLock = new object();
        private bool _disableProcPerfCounters;
        private bool _disableDiskIdleCounters;
        private NetworkInterface[] _networkInterfaces;
        private readonly Dictionary<string, long> _lastBytesSent;
        private readonly Dictionary<string, long> _lastBytesReceived;
        private DateTime _lastNetworkSampleTime;
        private readonly List<double> _uploadHistory = new List<double>(30);
        private readonly List<double> _downloadHistory = new List<double>(30);
        private readonly Dictionary<string, PerformanceCounter> _idleCountersByDrive = new();
        private readonly Dictionary<string, double> _lastDriveActivityByLetter = new();

        // CPU current clock fallback via perf counter
        private PerformanceCounter? _cpuPerfCounter; // Processor Information/% Processor Performance/_Total
        // Authoritative total CPU usage to align with Task Manager
        private PerformanceCounter? _cpuTotalCounter; // Processor/% Processor Time/_Total
        // Per-core fallback via perf counters (Processor/% Processor Time/[0..N-1])
        private PerformanceCounter[]? _perCoreCpuCounters;
        private DateTime _lastPerCoreCpuSampleUtc;
        private readonly System.Collections.Generic.List<double> _lastPerCoreCpuValues = new System.Collections.Generic.List<double>();
        private double _baseClockGhzCached = -1.0;

        // Lightweight per-process network sampling using ReadCategory snapshots
        private readonly object _procNetLock = new object();
        private readonly Dictionary<string, int> _pidByInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Diagnostics.CounterSample> _lastOtherBytesSampleByInstance = new Dictionary<string, System.Diagnostics.CounterSample>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastProcSampleUtc = DateTime.MinValue;
        private List<Bluetask.Models.NetworkProcessInfo> _lastTopProcNet = new List<Bluetask.Models.NetworkProcessInfo>(3);
        private int _procRefreshInProgress = 0; // Interlocked flag

        // Lightweight per-process DISK sampling (read+write bytes/sec)
        private readonly object _procDiskLock = new object();
        private readonly Dictionary<string, System.Diagnostics.CounterSample> _lastIoReadSampleByInstance = new Dictionary<string, System.Diagnostics.CounterSample>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Diagnostics.CounterSample> _lastIoWriteSampleByInstance = new Dictionary<string, System.Diagnostics.CounterSample>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastDiskProcSampleUtc = DateTime.MinValue;
        private List<string> _lastTopDiskProc = new List<string>(1);
        private int _diskProcRefreshInProgress = 0; // Interlocked flag

        // ETW-based per-process network accounting
        private static readonly object _etwInitLock = new object();
        private static bool _etwInitialized;
        private static TraceEventSession? _kernelSession;
        private static readonly Dictionary<int, (long sentBytes, long recvBytes)> _etwBytesByPid = new Dictionary<int, (long, long)>();
        private static readonly object _etwBytesLock = new object();
        private static Dictionary<int, (long sentBytes, long recvBytes)> _etwLastTotalsByPid = new Dictionary<int, (long, long)>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pidToNameCache = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        private static DateTime _etwLastSnapshotUtc = DateTime.MinValue;
        private static bool _etwActive;
        private static long _etwEventCount;

        // ETW-based per-process disk accounting
        private static readonly object _etwDiskBytesLock = new object();
        private static readonly Dictionary<int, long> _etwDiskBytesByPid = new Dictionary<int, long>();
        private static Dictionary<int, long> _etwDiskLastTotalsByPid = new Dictionary<int, long>();
        private static DateTime _etwDiskLastSnapshotUtc = DateTime.MinValue;
        private static readonly bool _countDiskIoLayer = false; // prefer FileIO layer to avoid double counting

        // IP Helper fallback: list processes owning sockets (no rates)
        private const int AF_INET = 2;   // IPv4
        private const int AF_INET6 = 23; // IPv6
        private enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL,
            TCP_TABLE_OWNER_MODULE_LISTENER,
            TCP_TABLE_OWNER_MODULE_CONNECTIONS,
            TCP_TABLE_OWNER_MODULE_ALL
        }
        private enum UDP_TABLE_CLASS
        {
            UDP_TABLE_BASIC,
            UDP_TABLE_OWNER_PID,
            UDP_TABLE_OWNER_MODULE
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint localAddr;
            public uint localPort;
            public uint owningPid;
        }
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool order, int ipVersion, TCP_TABLE_CLASS tableClass, uint reserved);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool order, int ipVersion, UDP_TABLE_CLASS tableClass, uint reserved);

        public SystemMonitorService()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true
            };
            // Best-effort enable additional sensor groups if supported by the library version
            try { typeof(Computer).GetProperty("IsControllerEnabled")?.SetValue(_computer, true); } catch { }
            try { typeof(Computer).GetProperty("IsMainboardEnabled")?.SetValue(_computer, true); } catch { }
            try { typeof(Computer).GetProperty("IsNetworkEnabled")?.SetValue(_computer, true); } catch { }
            // Defer opening hardware until first Update to avoid blocking startup
            _computerOpened = false;
            
            _networkInterfaces = Array.Empty<NetworkInterface>();
            _lastBytesSent = new Dictionary<string, long>();
            _lastBytesReceived = new Dictionary<string, long>();
            _lastNetworkSampleTime = DateTime.Now;

            // Defer disk/CPU counters and ETW start until first use to speed startup
        }

        private void EnsureComputerOpened()
        {
            if (_computerOpened) return;
            try { _computer.Open(); _computerOpened = true; } catch { _computerOpened = true; }
        }

        private void EnsureNetworkInterfaces()
        {
            try
            {
                if (_networkInterfaces == null || _networkInterfaces.Length == 0)
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    _networkInterfaces = nics ?? Array.Empty<NetworkInterface>();
                    _lastBytesSent.Clear();
                    _lastBytesReceived.Clear();
                    for (int i = 0; i < _networkInterfaces.Length; i++)
                    {
                        try
                        {
                            var ni = _networkInterfaces[i];
                            var stats = ni.GetIPStatistics();
                            _lastBytesSent[ni.Id] = stats.BytesSent;
                            _lastBytesReceived[ni.Id] = stats.BytesReceived;
                        }
                        catch { }
                    }
                    _lastNetworkSampleTime = DateTime.Now;
                }
            }
            catch { _networkInterfaces = Array.Empty<NetworkInterface>(); }
        }

        private void EnsureCpuCounters()
        {
            if (_cpuPerfCounter == null)
            {
                try
                {
                    _cpuPerfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    try { _ = _cpuPerfCounter.NextValue(); } catch { }
                }
                catch { _cpuPerfCounter = null; }
            }
            if (_cpuTotalCounter == null)
            {
                try
                {
                    _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    try { _ = _cpuTotalCounter.NextValue(); } catch { }
                }
                catch { _cpuTotalCounter = null; }
            }
        }

        private void EnsurePerCoreCounters()
        {
            if (_perCoreCpuCounters != null) return;
            try
            {
                int logical = Environment.ProcessorCount;
                _perCoreCpuCounters = new PerformanceCounter[logical];
                for (int i = 0; i < logical; i++)
                {
                    try
                    {
                        var pc = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                        _ = pc.NextValue();
                        _perCoreCpuCounters[i] = pc;
                    }
                    catch { _perCoreCpuCounters[i] = null!; }
                }
                _lastPerCoreCpuSampleUtc = DateTime.MinValue;
            }
            catch { _perCoreCpuCounters = null; }
        }

        private void EnsureDiskCounters()
        {
            if (_idleCountersByDrive.Count > 0 || _disableDiskIdleCounters) return;
            try
            {
                foreach (var d in System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed))
                {
                    var letter = d.Name.TrimEnd('\\');
                    if (_idleCountersByDrive.ContainsKey(letter)) continue;
                    PerformanceCounter? counter = null;
                    try
                    {
                        counter = new PerformanceCounter("LogicalDisk", "% Idle Time", letter, true);
                    }
                    catch { counter = null; }

                    if (counter == null)
                    {
                        try
                        {
                            var physCat = new PerformanceCounterCategory("PhysicalDisk");
                            var instances = physCat.GetInstanceNames();
                            var match = instances.FirstOrDefault(n => n.IndexOf(letter, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!string.IsNullOrEmpty(match))
                            {
                                counter = new PerformanceCounter("PhysicalDisk", "% Idle Time", match!, true);
                            }
                        }
                        catch { counter = null; }
                    }

                    if (counter != null)
                    {
                        try { _ = counter.NextValue(); } catch { }
                        _idleCountersByDrive[letter] = counter;
                    }
                }
            }
            catch { }
        }
        
        public void Update()
        {
            // Prevent overlapping updates (shared singleton may be called from multiple loops)
            lock (_updateLock)
            {
                EnsureComputerOpened();
                try
                {
                    foreach (var hardware in _computer.Hardware)
                    {
                        UpdateHardwareRecursive(hardware);
                    }
                }
                catch { }

                // Refresh drive activity from performance counters
                EnsureDiskCounters();
                if (!_disableDiskIdleCounters)
                {
                    foreach (var kvp in _idleCountersByDrive.ToArray())
                    {
                        try
                        {
                            var idle = kvp.Value.NextValue();
                            var activity = 100.0 - idle;
                            if (double.IsNaN(activity) || double.IsInfinity(activity)) activity = 0.0;
                            var raw = Math.Clamp(activity, 0.0, 100.0);
                            // Light smoothing to reduce counter jitter
                            if (_lastDriveActivityByLetter.TryGetValue(kvp.Key, out var prev))
                            {
                                raw = (prev * 0.6) + (raw * 0.4);
                            }
                            _lastDriveActivityByLetter[kvp.Key] = Math.Clamp(raw, 0.0, 100.0);
                        }
                        catch
                        {
                            // If any counter starts failing repeatedly, disable this entire path (some systems lack counters)
                            _lastDriveActivityByLetter[kvp.Key] = 0.0;
                            _disableDiskIdleCounters = true;
                            try
                            {
                                foreach (var c in _idleCountersByDrive.Values) c?.Dispose();
                            }
                            catch { }
                            _idleCountersByDrive.Clear();
                            break;
                        }
                    }
                }
            }
        }

        private static void UpdateHardwareRecursive(IHardware hardware)
        {
            try
            {
                hardware.Update();
                var sub = hardware.SubHardware;
                if (sub != null)
                {
                    for (int i = 0; i < sub.Length; i++)
                    {
                        var child = sub[i];
                        if (child != null)
                        {
                            UpdateHardwareRecursive(child);
                        }
                    }
                }
            }
            catch { }
        }

        public CpuInfo GetCpuInfo()
        {
            // Ensure perf counters are available when needed (lazy-init for startup perf)
            EnsureCpuCounters();
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            try { cpu?.Update(); } catch { }
            // Aggregate sensors from CPU and all sub-hardware nodes (CCD/package/die temps often live there)
            ISensor[] allCpuSensors = Array.Empty<ISensor>();
            try
            {
                if (cpu != null)
                {
                    var list = new System.Collections.Generic.List<ISensor>();
                    CollectSensorsRecursive(cpu, list);
                    allCpuSensors = list.ToArray();
                }
            }
            catch { allCpuSensors = cpu?.Sensors ?? Array.Empty<ISensor>(); }

            var usageSensor = allCpuSensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0);
            // Gather per-core utilization sensors (exclude aggregate/total)
            var coreLoadSensors = allCpuSensors
                .Where(s => s.SensorType == SensorType.Load
                            && s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) < 0
                            && (s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0
                                || s.Name.IndexOf("CPU Core", StringComparison.OrdinalIgnoreCase) >= 0))
                .ToArray();
            System.Collections.Generic.List<double> perCoreUsages = new System.Collections.Generic.List<double>();
            try
            {
                // Sort by core index when present in the name; else keep order
                System.Func<ISensor, int> getIndex = (s) =>
                {
                    try
                    {
                        var n = s?.Name ?? string.Empty;
                        // Extract digits after '#'
                        int hash = n.IndexOf('#');
                        if (hash >= 0)
                        {
                            string num = new string(n.Skip(hash + 1).TakeWhile(char.IsDigit).ToArray());
                            if (int.TryParse(num, out var idx)) return idx;
                        }
                        // Fallback: find last digits in name
                        var digits = new string(n.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                        if (int.TryParse(digits, out var idx2)) return idx2;
                    }
                    catch { }
                    return int.MaxValue;
                };
                foreach (var s in coreLoadSensors.OrderBy(getIndex))
                {
                    try
                    {
                        var v = s?.Value ?? 0.0f;
                        perCoreUsages.Add((double)SanitizePercent(v));
                    }
                    catch { perCoreUsages.Add(0.0); }
                }
            }
            catch { }

            // Fallback to performance counters if sensors missing or count < logical processors
            try
            {
                int logical = Environment.ProcessorCount;
                // Make sure per-core counters are ready if we need them
                if (perCoreUsages.Count < logical)
                {
                    EnsurePerCoreCounters();
                }
                if (perCoreUsages.Count < logical && _perCoreCpuCounters != null)
                {
                    // Throttle to once per ~1s
                    var now = DateTime.UtcNow;
                    if ((now - _lastPerCoreCpuSampleUtc).TotalMilliseconds > 800)
                    {
                        _lastPerCoreCpuSampleUtc = now;
                        _lastPerCoreCpuValues.Clear();
                        for (int i = 0; i < _perCoreCpuCounters.Length; i++)
                        {
                            try
                            {
                                var pc = _perCoreCpuCounters[i];
                                if (pc == null) { _lastPerCoreCpuValues.Add(0.0); continue; }
                                var val = pc.NextValue();
                                _lastPerCoreCpuValues.Add(SanitizePercent(val));
                            }
                            catch { _lastPerCoreCpuValues.Add(0.0); }
                        }
                    }
                    // Use last sampled values to fill or replace
                    if (_lastPerCoreCpuValues.Count > 0)
                    {
                        perCoreUsages = new System.Collections.Generic.List<double>(_lastPerCoreCpuValues);
                    }
                }
            }
            catch { }

            // CPU temperature: Enhanced detection with better fallback logic
            int temperatureC = 0;
            try
            {
                var tempSensors = allCpuSensors
                    .Where(s => s.SensorType == SensorType.Temperature)
                    .ToArray();

                static int ScoreTempSensor(ISensor s)
                {
                    var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                    int score = 0;
                    if (n.Contains("TDIE")) score += 100;           // AMD true die temp
                    if (n.Contains("TCTL")) score += 90;            // AMD control temp
                    if (n.Contains("PACKAGE")) score += 85;         // CPU Package
                    if (n.Contains("CPU DIE")) score += 80;         // Intel/AMD die avg
                    if (n.Contains("CORE MAX")) score += 75;        // Max of cores
                    if (n.Contains("CCD")) score += 60;             // CCD temps
                    if (n.Contains("CPU")) score += 50;             // Any CPU temp
                    if (n.Contains("CORE")) score += 40;            // Individual core temps
                    return score;
                }

                // Try to find valid CPU temperature sensors with broader criteria
                var validTempSensors = tempSensors
                    .Where(s => s?.Value.HasValue == true && 
                               !double.IsNaN(s.Value!.Value) && 
                               !double.IsInfinity(s.Value!.Value) && 
                               s.Value!.Value > 0 && 
                               s.Value!.Value < 150) // Reasonable max temp
                    .ToArray();

                var best = validTempSensors
                    .OrderByDescending(ScoreTempSensor)
                    .ThenByDescending(s => s.Value!.Value)
                    .FirstOrDefault();
                    
                if (best != null)
                {
                    temperatureC = (int)Math.Round(best.Value!.Value);
                }
                else if (validTempSensors.Length > 0)
                {
                    // If no scored sensor found, use the highest valid temperature
                    var maxTemp = validTempSensors.Max(s => s.Value!.Value);
                    temperatureC = (int)Math.Round(maxTemp);
                }

                // Enhanced motherboard fallback: CPU-related Super I/O sensors under motherboard
                if (temperatureC <= 0)
                {
                    try
                    {
                        var mobo = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                        if (mobo != null)
                        {
                            mobo.Update(); // Ensure motherboard sensors are updated
                            var boardSensors = new System.Collections.Generic.List<ISensor>();
                            CollectSensorsRecursive(mobo, boardSensors);
                            var candidate = boardSensors
                                .Where(s => s.SensorType == SensorType.Temperature)
                                .Where(s =>
                                {
                                    var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                                    // Exclude non-CPU sensors
                                    if (n.Contains("VRM") || n.Contains("PCH") || n.Contains("CHIPSET") || 
                                        n.Contains("GPU") || n.Contains("SYSTEM") || n.Contains("AMBIENT")) return false;
                                    // Include CPU-related sensors
                                    return n.Contains("CPU") || n.Contains("SOCKET") || n.Contains("TDIE") || 
                                           n.Contains("TCTL") || n.Contains("PACKAGE") || n.Contains("CORE");
                                })
                                .Where(s => s?.Value.HasValue == true && 
                                           !double.IsNaN(s.Value!.Value) && 
                                           !double.IsInfinity(s.Value!.Value) && 
                                           s.Value!.Value > 0 && 
                                           s.Value!.Value < 150)
                                .OrderByDescending(ScoreTempSensor)
                                .ThenByDescending(s => s.Value!.Value)
                                .FirstOrDefault();
                            if (candidate != null)
                            {
                                temperatureC = (int)Math.Round(candidate.Value!.Value);
                            }
                        }
                    }
                    catch { }
                }

                // Enhanced ACPI fallback
                if (temperatureC <= 0)
                {
                    temperatureC = TryGetCpuTemperatureFromAcpiC();
                }

                // Final fallback: try to get any reasonable temperature from any thermal zone
                if (temperatureC <= 0)
                {
                    temperatureC = TryGetAnyReasonableTemperature();
                }
            }
            catch { }

            // Current clock: prefer effective/average core clocks when available
            double ghz = 0.0;
            try
            {
                var clockValues = allCpuSensors
                    .Where(s => s.SensorType == SensorType.Clock && (s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)
                                                                   || s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)
                                                                   || s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                                                   || s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)))
                    .Select(s => (double?)(s.Value))
                    .Where(v => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) && v.Value > 0)
                    .Select(v => v!.Value)
                    .ToList() ?? new List<double>();
                var mhz = clockValues.Count > 0 ? clockValues.Max() : 0.0;
                ghz = mhz > 0 ? (mhz / 1000.0) : 0.0;
            }
            catch { ghz = 0.0; }

            if (ghz <= 0.0)
            {
                var perfGhz = TryGetCurrentClockFromPerfCounterGhz();
                if (perfGhz > 0) ghz = perfGhz;
            }

            // Core/thread counts used by dashboard and per-core aggregation
            int physicalCores = 0;
            int logicalCount = Environment.ProcessorCount;
            try
            {
                // LHM sometimes exposes SmallData sensors for counts
                var coreCountSensor = allCpuSensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("Core Count", StringComparison.OrdinalIgnoreCase) >= 0);
                var threadCountSensor = allCpuSensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("Thread Count", StringComparison.OrdinalIgnoreCase) >= 0);
                if (coreCountSensor?.Value.HasValue == true && coreCountSensor.Value.Value > 0) physicalCores = (int)Math.Round(coreCountSensor.Value.Value);
                if (threadCountSensor?.Value.HasValue == true && threadCountSensor.Value.Value > 0) logicalCount = (int)Math.Round(threadCountSensor.Value.Value);

                if (physicalCores <= 0)
                {
                    using var searcher = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                    int coresSum = 0, logicalSum = 0;
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                    {
                        try { coresSum += Convert.ToInt32(mo["NumberOfCores"] ?? 0); } catch { }
                        try { logicalSum += Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? 0); } catch { }
                    }
                    if (coresSum > 0) physicalCores = coresSum;
                    if (logicalSum > 0) logicalCount = logicalSum;
                }
            }
            catch { }

            // Final safety fallback: infer cores from logical processors
            if (physicalCores <= 0)
            {
                try
                {
                    // Common case: SMT/HT with 2 threads per core
                    if (logicalCount >= 2 && (logicalCount % 2) == 0)
                        physicalCores = logicalCount / 2;
                    else
                        physicalCores = logicalCount; // best-effort
                }
                catch { physicalCores = Math.Max(1, logicalCount); }
            }

            // CPU package power (Watts)
            double powerWatts = 0.0;
            try
            {
                var powerSensors = allCpuSensors
                    .Where(s => s.SensorType == SensorType.Power)
                    .ToArray();

                static int ScorePowerSensor(ISensor s)
                {
                    var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                    int score = 0;
                    if (n.Contains("PACKAGE")) score += 100;   // CPU package power
                    if (n.Contains("SOCKET")) score += 90;     
                    if (n.Contains("SOC")) score += 80;
                    if (n.Contains("CORE")) score += 60;
                    if (n.Contains("CPU")) score += 50;
                    return score;
                }

                var validPower = powerSensors
                    .Where(s => s?.Value.HasValue == true
                                && !double.IsNaN(s.Value!.Value)
                                && !double.IsInfinity(s.Value!.Value)
                                && s.Value!.Value > 0.5
                                && s.Value!.Value < 400.0)
                    .ToArray();

                var bestPower = validPower
                    .OrderByDescending(ScorePowerSensor)
                    .ThenByDescending(s => s.Value!.Value)
                    .FirstOrDefault();

                if (bestPower != null)
                {
                    powerWatts = bestPower.Value!.Value;
                }

                // If no single package sensor, try summing core/uncore/DRAM-like sensors to estimate package
                if (powerWatts <= 0.0 && validPower.Length > 0)
                {
                    double sum = 0.0;
                    for (int i = 0; i < validPower.Length; i++)
                    {
                        var n = (validPower[i]?.Name ?? string.Empty).ToUpperInvariant();
                        if (n.Contains("GPU")) continue; // avoid iGPU if exposed separately
                        if (n.Contains("IA") || n.Contains("CORE") || n.Contains("CORES") || n.Contains("UNCORE") || n.Contains("DRAM") || n.Contains("SOC") || n.Contains("SOCKET"))
                        {
                            sum += Math.Max(0.0, validPower[i].Value!.Value);
                        }
                    }
                    if (sum > 1.0 && sum < 500.0)
                    {
                        powerWatts = sum;
                    }
                }
            }
            catch { }

            // CPU core/package voltage (Volts)
            double voltageVolts = 0.0;
            try
            {
                var voltageSensors = allCpuSensors
                    .Where(s => s.SensorType == SensorType.Voltage)
                    .ToArray();

                static int ScoreVoltageSensor(ISensor s)
                {
                    var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                    int score = 0;
                    if (n.Contains("VCORE")) score += 100;      // Core voltage
                    if (n.Contains("VDDCR") && n.Contains("CPU")) score += 95; // AMD VDDCR CPU
                    if (n.Contains("CORE")) score += 80;
                    if (n.Contains("CPU")) score += 60;
                    if (n.Contains("VID")) score += 50;         // Requested voltage (approx)
                    return score;
                }

                var validVolt = voltageSensors
                    .Where(s => s?.Value.HasValue == true
                                && !double.IsNaN(s.Value!.Value)
                                && !double.IsInfinity(s.Value!.Value)
                                && s.Value!.Value > 0.2
                                && s.Value!.Value < 2.5)
                    .ToArray();

                var bestVolt = validVolt
                    .OrderByDescending(ScoreVoltageSensor)
                    .ThenByDescending(s => s.Value!.Value)
                    .FirstOrDefault();

                if (bestVolt != null)
                {
                    voltageVolts = bestVolt.Value!.Value;
                }
            }
            catch { }

            // Broader fallback search for power and voltage across all hardware (e.g., motherboard Super I/O)
            if (powerWatts <= 0.0 || voltageVolts <= 0.0)
            {
                try
                {
                    var allSensors = new System.Collections.Generic.List<ISensor>();
                    foreach (var hw in _computer.Hardware)
                    {
                        try
                        {
                            hw.Update();
                            CollectSensorsRecursive(hw, allSensors);
                        }
                        catch { }
                    }

                    if (powerWatts <= 0.0)
                    {
                        static int ScoreAnyCpuPower(ISensor s)
                        {
                            var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                            int score = 0;
                            if (n.Contains("PACKAGE")) score += 100;
                            if (n.Contains("SOCKET")) score += 90;
                            if (n.Contains("SOC")) score += 80;
                            if (n.Contains("CPU")) score += 70;
                            if (n.Contains("CORE")) score += 60;
                            if (n.Contains("PPT")) score += 50; // AMD PPT
                            return score;
                        }

                        var cpuPower = allSensors
                            .Where(s => s.SensorType == SensorType.Power)
                            .Where(s => s?.Value.HasValue == true
                                        && !double.IsNaN(s.Value!.Value)
                                        && !double.IsInfinity(s.Value!.Value)
                                        && s.Value!.Value > 0.5
                                        && s.Value!.Value < 500.0)
                            .Where(s =>
                            {
                                var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                                // exclude GPU power sensors
                                if (n.Contains("GPU")) return false;
                                // include likely CPU sensors
                                return n.Contains("CPU") || n.Contains("PACKAGE") || n.Contains("SOCKET") || n.Contains("SOC") || n.Contains("PPT") || n.Contains("CORE");
                            })
                            .OrderByDescending(ScoreAnyCpuPower)
                            .ThenByDescending(s => s.Value!.Value)
                            .FirstOrDefault();

                        if (cpuPower != null)
                        {
                            powerWatts = cpuPower.Value!.Value;
                        }

                        // As an alternative, sum multiple CPU-related power sensors if present
                        if (powerWatts <= 0.0)
                        {
                            var cpuRelatedPowers = allSensors
                                .Where(s => s.SensorType == SensorType.Power)
                                .Where(s => s?.Value.HasValue == true
                                            && !double.IsNaN(s.Value!.Value)
                                            && !double.IsInfinity(s.Value!.Value)
                                            && s.Value!.Value > 0.5
                                            && s.Value!.Value < 500.0)
                                .Where(s =>
                                {
                                    var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                                    if (n.Contains("GPU")) return false;
                                    return n.Contains("IA") || n.Contains("CORE") || n.Contains("CORES") || n.Contains("UNCORE") || n.Contains("DRAM") || n.Contains("SOC") || n.Contains("SOCKET") || n.Contains("CPU") || n.Contains("PPT");
                                })
                                .ToArray();
                            if (cpuRelatedPowers.Length > 0)
                            {
                                double sum = 0.0;
                                for (int i = 0; i < cpuRelatedPowers.Length; i++) sum += Math.Max(0.0, cpuRelatedPowers[i].Value!.Value);
                                if (sum > 1.0 && sum < 500.0) powerWatts = sum;
                            }
                        }
                    }

                    if (voltageVolts <= 0.0)
                    {
                        static int ScoreAnyCpuVoltage(ISensor s)
                        {
                            var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                            int score = 0;
                            if (n.Contains("VCORE")) score += 100;
                            if (n.Contains("VDDCR") && n.Contains("CPU")) score += 95;
                            if (n.Contains("CORE")) score += 80;
                            if (n.Contains("CPU")) score += 70;
                            if (n.Contains("VID")) score += 50;
                            return score;
                        }

                        var cpuVolt = allSensors
                            .Where(s => s.SensorType == SensorType.Voltage)
                            .Where(s => s?.Value.HasValue == true
                                        && !double.IsNaN(s.Value!.Value)
                                        && !double.IsInfinity(s.Value!.Value)
                                        && s.Value!.Value > 0.2
                                        && s.Value!.Value < 2.5)
                            .Where(s =>
                            {
                                var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                                if (n.Contains("GPU")) return false;
                                return n.Contains("VCORE") || n.Contains("CORE") || n.Contains("CPU") || n.Contains("VDDCR") || n.Contains("VID");
                            })
                            .OrderByDescending(ScoreAnyCpuVoltage)
                            .ThenByDescending(s => s.Value!.Value)
                            .FirstOrDefault();

                        if (cpuVolt != null)
                        {
                            voltageVolts = cpuVolt.Value!.Value;
                        }
                    }

                    // Last-ditch: compute power from voltage * current if both look plausible
                    if (powerWatts <= 0.0 && voltageVolts > 0.2)
                    {
                        var cpuCurrent = allSensors
                            .Where(s => s.SensorType == SensorType.Current)
                            .Where(s => s?.Value.HasValue == true
                                        && !double.IsNaN(s.Value!.Value)
                                        && !double.IsInfinity(s.Value!.Value)
                                        && s.Value!.Value > 1.0
                                        && s.Value!.Value < 200.0)
                            .FirstOrDefault(s =>
                            {
                                var n = (s?.Name ?? string.Empty).ToUpperInvariant();
                                if (n.Contains("GPU")) return false;
                                return n.Contains("CPU") || n.Contains("CORE") || n.Contains("VCORE") || n.Contains("IA");
                            });

                        if (cpuCurrent != null)
                        {
                            try
                            {
                                powerWatts = Math.Clamp(voltageVolts * cpuCurrent.Value!.Value, 0.0, 500.0);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return new CpuInfo
            {
                Name = cpu?.Name ?? "N/A",
                Usage = ComputeAuthoritativeCpuUsage(usageSensor?.Value ?? 0, perCoreUsages),
                Temperature = temperatureC,
                CoresAndThreads = $"{physicalCores}C/{logicalCount}T",
                CurrentClockGhz = ghz,
                PowerWatts = powerWatts,
                VoltageVolts = voltageVolts,
                PhysicalCoreCount = physicalCores,
                LogicalProcessorCount = logicalCount,
                PerCoreUsages = perCoreUsages
            };
        }

        // Prefer the Windows performance counter for total usage to match Task Manager
        private double ComputeAuthoritativeCpuUsage(float sensorValue, System.Collections.Generic.List<double> perCore)
        {
            try
            {
                // Try Task Manager-aligned counter first
                if (_cpuTotalCounter != null)
                {
                    try
                    {
                        var v = _cpuTotalCounter.NextValue();
                        var pct = SanitizePercent(v);
                        if (pct > 0.0 || v == 0.0f)
                        {
                            return pct;
                        }
                    }
                    catch { }
                }

                double raw = SanitizePercent(sensorValue);
                if ((raw <= 0.1 || double.IsNaN(raw)) && perCore != null && perCore.Count > 0)
                {
                    raw = Math.Clamp(perCore.Average(), 0.0, 100.0);
                }
                return Math.Clamp(raw, 0.0, 100.0);
            }
            catch { return SanitizePercent(sensorValue); }
        }

        private static void CollectSensorsRecursive(IHardware hardware, System.Collections.Generic.List<ISensor> dest)
        {
            try
            {
                if (hardware?.Sensors != null)
                {
                    for (int i = 0; i < hardware.Sensors.Length; i++)
                    {
                        var s = hardware.Sensors[i];
                        if (s != null) dest.Add(s);
                    }
                }
                var sub = hardware?.SubHardware;
                if (sub != null)
                {
                    for (int i = 0; i < sub.Length; i++)
                    {
                        var child = sub[i];
                        if (child != null)
                        {
                            // Some providers require updating before sensors report values
                            try { child.Update(); } catch { }
                            CollectSensorsRecursive(child, dest);
                        }
                    }
                }
            }
            catch { }
        }
        
        // ACPI thermal zone temperature: returns integer Celsius or 0 on failure
        private static int TryGetCpuTemperatureFromAcpiC()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    try
                    {
                        var rawObj = mo["CurrentTemperature"];
                        double raw = 0.0;
                        if (rawObj != null)
                        {
                            try { raw = Convert.ToDouble(rawObj); } catch { raw = 0.0; }
                        }
                        if (raw > 0)
                        {
                            var c = (raw / 10.0) - 273.15; // tenths of Kelvin
                            if (!double.IsNaN(c) && !double.IsInfinity(c) && c > 0)
                            {
                                return (int)Math.Round(c);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            // Secondary WMI source (some platforms): Win32_PerfFormattedData_Counters_ThermalZoneInformation or Win32_TemperatureProbe
            try
            {
                using var probe = new System.Management.ManagementObjectSearcher("SELECT CurrentReading FROM Win32_TemperatureProbe");
                foreach (System.Management.ManagementObject mo in probe.Get())
                {
                    try
                    {
                        var val = mo["CurrentReading"];
                        if (val != null)
                        {
                            // Win32_TemperatureProbe is often not implemented; when it is, it may be Kelvin * 10
                            double raw = Convert.ToDouble(val);
                            if (raw > 0)
                            {
                                var c = (raw / 10.0) - 273.15;
                                if (!double.IsNaN(c) && !double.IsInfinity(c) && c > 0)
                                {
                                    return (int)Math.Round(c);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return 0;
        }

        // Final fallback: attempt to get any reasonable temperature from any thermal sensor
        private int TryGetAnyReasonableTemperature()
        {
            try
            {
                // Look through all hardware for any temperature sensors that might be CPU-related
                foreach (var hardware in _computer.Hardware)
                {
                    try
                    {
                        hardware.Update();
                        var allSensors = new System.Collections.Generic.List<ISensor>();
                        CollectSensorsRecursive(hardware, allSensors);
                        
                        var tempSensors = allSensors
                            .Where(s => s.SensorType == SensorType.Temperature)
                            .Where(s => s?.Value.HasValue == true && 
                                       !double.IsNaN(s.Value!.Value) && 
                                       !double.IsInfinity(s.Value!.Value) && 
                                       s.Value!.Value > 15 &&  // Minimum reasonable CPU temp
                                       s.Value!.Value < 150)   // Maximum reasonable CPU temp
                            .ToArray();

                        if (tempSensors.Length > 0)
                        {
                            // Prefer sensors with CPU-like names
                            var cpuLikeSensor = tempSensors
                                .Where(s =>
                                {
                                    var name = (s?.Name ?? string.Empty).ToUpperInvariant();
                                    return name.Contains("CPU") || name.Contains("CORE") || 
                                           name.Contains("PACKAGE") || name.Contains("SOCKET") ||
                                           name.Contains("TDIE") || name.Contains("TCTL");
                                })
                                .OrderByDescending(s => s.Value!.Value)
                                .FirstOrDefault();

                            if (cpuLikeSensor != null)
                            {
                                return (int)Math.Round(cpuLikeSensor.Value!.Value);
                            }

                            // If no CPU-like sensors found, take the highest reasonable temperature
                            // (likely to be CPU as it's usually the hottest component)
                            var highestTemp = tempSensors.Max(s => s.Value!.Value);
                            if (highestTemp >= 25) // Only if it seems reasonable for a CPU
                            {
                                return (int)Math.Round(highestTemp);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Last resort: try Windows Performance Counter for processor thermal throttle
            try
            {
                using var pc = new PerformanceCounter("Thermal Zone Information", "Temperature", "_TZ.TZ00", true);
                var temp = pc.NextValue();
                if (temp > 0 && temp < 150)
                {
                    // Performance counter may return in Kelvin or Celsius, try to detect
                    if (temp > 200) // Likely Kelvin
                    {
                        temp = (float)(temp - 273.15);
                    }
                    if (temp > 15 && temp < 150)
                    {
                        return (int)Math.Round(temp);
                    }
                }
            }
            catch { }

            return 0;
        }
        
        public IEnumerable<GpuInfo> GetGpuInfo()
        {
            var hardware = _computer.Hardware.Where(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel).ToArray();
            int targetCount = SettingsService.DebugGpuCount >= 0 ? SettingsService.DebugGpuCount : hardware.Length;

            if (targetCount <= 0) yield break;

            // Pre-fetch driver details per video controller for better per-adapter mapping
            var controllerDrivers = QueryVideoControllerDrivers();

            int realCount = Math.Min(hardware.Length, targetCount);
            for (int i = 0; i < realCount; i++)
            {
                var gpu = hardware[i];
                var tempSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                var usageSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                var memUsedSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Used"));
                var memTotalSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Total"));
                var dedicated = memTotalSensor?.Value ?? 0;
                var (usedGb, totalGb) = NormalizeGpuMemoryValues(memUsedSensor?.Value ?? 0, memTotalSensor?.Value ?? 0);

                // Try to match this hardware to a specific Win32_VideoController entry for driver info
                var (drvVersion, drvDate) = TryMatchDriverForAdapter(gpu?.Name ?? string.Empty, controllerDrivers);
                if (string.IsNullOrWhiteSpace(drvVersion)) drvVersion = GetGpuDriverVersion();
                if (string.IsNullOrWhiteSpace(drvDate)) drvDate = GetGpuDriverDate();

                yield return new GpuInfo
                {
                    Name = gpu.Name,
                    Usage = SanitizePercent(usageSensor?.Value ?? 0),
                    Temperature = (int)(tempSensor?.Value ?? 0),
                    Memory = $"{FormatSizeInGb(usedGb)} / {FormatSizeInGb(totalGb)}",
                    DedicatedMemory = dedicated > 0 ? $"{dedicated:F0} GB" : string.Empty,
                    SharedMemory = string.Empty,
                    DriverVersion = drvVersion,
                    DriverDate = drvDate,
                    DirectXVersion = "12",
                    CardTitle = $"GPU {i}"
                };
            }

            for (int i = realCount; i < targetCount; i++)
            {
                yield return new GpuInfo
                {
                    Name = $"Emulated GPU {i}",
                    Usage = 0,
                    Temperature = 0,
                    Memory = "0.0 GB / 0.0 GB",
                    CardTitle = $"GPU {i}",
                    DriverVersion = "emulated",
                    DirectXVersion = "12"
                };
            }
        }
        
        public RamInfo GetRamInfo()
        {
            var memory = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
            var usageSensor = memory?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
            var usedSensor = memory?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used");
            var availSensor = memory?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Available");

            var used = usedSensor?.Value ?? 0; // GB
            var avail = availSensor?.Value ?? 0; // GB
            var totalGb = used + avail;

            // Additional metrics via WMI PerfOS_Memory (bytes)
            string committed = string.Empty, cached = string.Empty, paged = string.Empty, nonPaged = string.Empty;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT CommittedBytes, CacheBytes, PoolPagedBytes, PoolNonpagedBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    double committedBytes = Convert.ToDouble(mo["CommittedBytes"]);
                    double cacheBytes = Convert.ToDouble(mo["CacheBytes"]);
                    double poolPagedBytes = Convert.ToDouble(mo["PoolPagedBytes"]);
                    double poolNonPagedBytes = Convert.ToDouble(mo["PoolNonpagedBytes"]);
                    committed = FormatSizeInGb(committedBytes / (1024.0 * 1024 * 1024));
                    cached = FormatSizeInGb(cacheBytes / (1024.0 * 1024 * 1024));
                    paged = FormatSizeInGb(poolPagedBytes / (1024.0 * 1024 * 1024));
                    nonPaged = FormatSizeInGb(poolNonPagedBytes / (1024.0 * 1024 * 1024));
                    break;
                }
            }
            catch { }

            // Brand/model via Win32_PhysicalMemory
            string brand = string.Empty, model = string.Empty, displayName = string.Empty;
            try
            {
                using var dimmSearcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, PartNumber FROM Win32_PhysicalMemory");
                var manufacturers = new List<string>();
                var parts = new List<string>();
                foreach (System.Management.ManagementObject mo in dimmSearcher.Get())
                {
                    var b = (mo["Manufacturer"]?.ToString() ?? string.Empty).Trim();
                    var p = (mo["PartNumber"]?.ToString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(b)) manufacturers.Add(b);
                    if (!string.IsNullOrWhiteSpace(p)) parts.Add(p);
                }
                brand = ResolveRamBrand(manufacturers, parts);
                if (parts.Count > 0) model = string.Join(" | ", parts.Distinct());
                displayName = brand;
            }
            catch { }

            // Dynamic memory details
            string typeAndSpeed = string.Empty;
            string formFactor = string.Empty;
            string slotsUsedSummary = string.Empty;
            string moduleConfig = string.Empty;

            try
            {
                double maxConfigured = 0.0;
                double maxSpeed = 0.0;
                int bestSmbiosType = 0;
                var formFactorCounts = new Dictionary<int, int>();
                var moduleSizesGb = new List<double>();
                int usedSlots = 0;

                using var memSearcher = new System.Management.ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, FormFactor, SMBIOSMemoryType, Capacity FROM Win32_PhysicalMemory");
                foreach (System.Management.ManagementObject mo in memSearcher.Get())
                {
                    usedSlots++;
                    try { maxConfigured = Math.Max(maxConfigured, Convert.ToDouble(mo["ConfiguredClockSpeed"] ?? 0.0)); } catch { }
                    try { maxSpeed = Math.Max(maxSpeed, Convert.ToDouble(mo["Speed"] ?? 0.0)); } catch { }
                    try
                    {
                        int ff = Convert.ToInt32(mo["FormFactor"] ?? 0);
                        if (!formFactorCounts.ContainsKey(ff)) formFactorCounts[ff] = 0;
                        formFactorCounts[ff] += 1;
                    }
                    catch { }
                    try { bestSmbiosType = Math.Max(bestSmbiosType, Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0)); } catch { }
                    try
                    {
                        double capBytes = Convert.ToDouble(mo["Capacity"] ?? 0.0);
                        double capGb = capBytes / (1024.0 * 1024.0 * 1024.0);
                        if (capGb > 0) moduleSizesGb.Add(capGb);
                    }
                    catch { }
                }

                // Determine total slots from memory array, fallback to used count
                int totalSlots = 0;
                try
                {
                    using var arrSearcher = new System.Management.ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                    foreach (System.Management.ManagementObject mo in arrSearcher.Get())
                    {
                        try { totalSlots = Math.Max(totalSlots, Convert.ToInt32(mo["MemoryDevices"] ?? 0)); } catch { }
                    }
                }
                catch { }

                // Compose strings
                slotsUsedSummary = usedSlots > 0 ? (totalSlots > 0 ? $"{usedSlots} of {totalSlots}" : usedSlots.ToString()) : string.Empty;

                // Form factor: choose most common non-zero code
                int ffBest = formFactorCounts
                    .Where(kv => kv.Key > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                formFactor = MapMemoryFormFactor(ffBest);

                // DDR type + speed
                string ddrGen = MapSmbiosMemoryType(bestSmbiosType);
                double dataRate = Math.Max(maxConfigured, maxSpeed);
                if (dataRate > 0 && !string.IsNullOrWhiteSpace(ddrGen))
                {
                    int rounded = (int)(Math.Round(dataRate / 100.0) * 100.0); // round to nearest 100 MT/s
                    typeAndSpeed = $"{ddrGen}-{rounded}";
                }
                else if (!string.IsNullOrWhiteSpace(ddrGen))
                {
                    typeAndSpeed = ddrGen;
                }

                moduleConfig = SummarizeModuleConfiguration(moduleSizesGb);
                if (string.IsNullOrWhiteSpace(moduleConfig))
                {
                    try
                    {
                        int count = usedSlots > 0 ? usedSlots : moduleSizesGb.Count;
                        if (count > 0)
                        {
                            double avgGb;
                            if (moduleSizesGb.Count > 0)
                            {
                                avgGb = moduleSizesGb.Average();
                            }
                            else
                            {
                                // Fallback: approximate from total when individual capacities are unavailable
                                double tot = Math.Max(totalGb, 0.0);
                                avgGb = count > 0 ? (tot / count) : 0.0;
                            }
                            int approx = Math.Max(1, (int)Math.Round(avgGb));
                            moduleConfig = $"{count}x{approx}GB";
                        }
                        else if (totalGb > 0)
                        {
                            // Last-resort heuristic: assume dual-channel unless total is very small
                            int guessCount = (totalGb >= 12.0) ? 2 : 1;
                            int approx = Math.Max(1, (int)Math.Round(totalGb / guessCount));
                            moduleConfig = $"{guessCount}x{approx}GB";
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return new RamInfo
            {
                Usage = SanitizePercent(usageSensor?.Value ?? 0),
                Type = string.IsNullOrWhiteSpace(typeAndSpeed) ? string.Empty : typeAndSpeed,
                UsedAndTotal = $"{FormatSizeInGb(used)} / {FormatSizeInGb(totalGb)}",
                ModuleConfiguration = moduleConfig,
                Available = FormatSizeInGb(avail),
                Reserved = string.Empty,
                FormFactor = string.IsNullOrWhiteSpace(formFactor) ? string.Empty : formFactor,
                SlotsUsedSummary = string.IsNullOrWhiteSpace(slotsUsedSummary) ? string.Empty : slotsUsedSummary,
                UsedOnly = FormatSizeInGb(used),
                Committed = committed,
                Cached = cached,
                PagedPool = paged,
                NonPagedPool = nonPaged,
                XmpOrExpo = GetMemoryXmpOrExpo(),
                Brand = brand,
                Model = model,
                DisplayName = displayName
            };
        }

        private static double SanitizePercent(double value)
        {
            try
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
                if (value < 0) return 0.0;
                if (value > 100) return 100.0;
                return value;
            }
            catch { return 0.0; }
        }
        
        public IEnumerable<Bluetask.Models.StorageInfo> GetDriveInfo()
        {
            // Always list logical fixed drives using System.IO for reliability
            var realDrives = System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed).ToArray();
            int targetCount = SettingsService.DebugDiskCount >= 0 ? SettingsService.DebugDiskCount : realDrives.Length;
            if (targetCount <= 0) yield break;

            int realCount = Math.Min(realDrives.Length, targetCount);
            for (int i = 0; i < realCount; i++)
            {
                var d = realDrives[i];
                var letter = d.Name.TrimEnd('\\');
                _lastDriveActivityByLetter.TryGetValue(letter, out var activityPercent);
                var totalGb = (d.TotalSize / (1024.0 * 1024 * 1024));
                var systemRoot = System.IO.Path.GetPathRoot(System.Environment.SystemDirectory) ?? "C:\\";
                var isSystem = string.Equals((System.IO.Path.GetPathRoot(d.Name) ?? string.Empty), systemRoot, System.StringComparison.OrdinalIgnoreCase);
                string brand = GetDriveBrandForLetter(letter);
                string mediaKind = GetDriveMediaKind(letter);

                yield return new Bluetask.Models.StorageInfo
                {
                    Name = $"{letter} {d.VolumeLabel}",
                    Usage = activityPercent, // activity, NOT capacity
                    Space = $"{FormatSizeInGb(((d.TotalSize - d.AvailableFreeSpace) / (1024.0 * 1024 * 1024)))} / {FormatSizeInGb((d.TotalSize / (1024.0 * 1024 * 1024)))}",
                    DriveType = d.DriveFormat,
                    Capacity = FormatSizeInGb(totalGb),
                    IsSystemDisk = isSystem,
                    HasPageFile = isSystem, // simple heuristic
                    Brand = brand,
                    MediaKind = mediaKind,
                    CardTitle = $"Disk {i}{(string.IsNullOrWhiteSpace(letter) ? string.Empty : $" ({letter})")}"
                };
            }

            for (int i = realCount; i < targetCount; i++)
            {
                yield return new Bluetask.Models.StorageInfo
                {
                    Name = $"E:{i} Emulated",
                    Usage = 0,
                    Space = "0.0 GB / 0.0 GB",
                    DriveType = "NTFS",
                    Capacity = "0.0 GB",
                    IsSystemDisk = false,
                    HasPageFile = false,
                    CardTitle = $"Disk {i}",
                    Brand = "Emulated"
                };
            }
        }

        // Best effort to get drive brand using WMI. Matches by drive letter when possible.
        private static string GetDriveBrandForLetter(string letter)
        {
            try
            {
                string brand = string.Empty;
                // Map logical disk -> partition -> disk drive
                using var ldSearcher = new System.Management.ManagementObjectSearcher($"SELECT DeviceID, VolumeName FROM Win32_LogicalDisk WHERE DeviceID='{letter}'");
                foreach (System.Management.ManagementObject ld in ldSearcher.Get())
                {
                    string deviceId = ld["DeviceID"]?.ToString() ?? string.Empty; // e.g. C:
                    string devIdNoSlash = deviceId.TrimEnd('\\'); // C:
                    // Get partition association
                    using var assoc = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + deviceId + "'} WHERE AssocClass = Win32_LogicalDiskToPartition");
                    foreach (System.Management.ManagementObject part in assoc.Get())
                    {
                        string? partId = part["DeviceID"]?.ToString(); // e.g. Disk #0, Partition #1
                        if (string.IsNullOrWhiteSpace(partId)) continue;
                        using var assoc2 = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partId.Replace("\\", "\\\\") + "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                        foreach (System.Management.ManagementObject drive in assoc2.Get())
                        {
                            string mfr = (drive["Manufacturer"]?.ToString() ?? string.Empty).Trim();
                            string model = (drive["Model"]?.ToString() ?? string.Empty).Trim();
                            // Prefer model parsing for brand cues
                            string parsed = ResolveStorageBrand(mfr, model);
                            if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string ResolveStorageBrand(string manufacturer, string model)
        {
            try
            {
                var m = (manufacturer ?? string.Empty).ToUpperInvariant();
                var mdl = (model ?? string.Empty).ToUpperInvariant();
                if (mdl.Contains("SAMSUNG") || m.Contains("SAMSUNG")) return "Samsung";
                if (mdl.Contains("WD ") || mdl.Contains("WESTERN DIGITAL") || m.Contains("WESTERN DIGITAL")) return "Western Digital";
                if (mdl.Contains("WDC")) return "Western Digital";
                if (mdl.Contains("SEAGATE") || m.Contains("SEAGATE")) return "Seagate";
                if (mdl.Contains("CRUCIAL") || m.Contains("CRUCIAL") || mdl.StartsWith("CT")) return "Crucial";
                if (mdl.Contains("KINGSTON") || m.Contains("KINGSTON") || mdl.StartsWith("SA") || mdl.StartsWith("SNV")) return "Kingston";
                if (mdl.Contains("INTEL") || m.Contains("INTEL")) return "Intel";
                if (mdl.Contains("MICRON") || m.Contains("MICRON")) return "Micron";
                if (mdl.Contains("SK HYNIX") || mdl.Contains("HYNIX") || m.Contains("HYNIX")) return "SK hynix";
                if (mdl.Contains("ADATA") || m.Contains("ADATA")) return "ADATA";
                if (mdl.Contains("SANDISK") || m.Contains("SANDISK")) return "SanDisk";
                if (mdl.Contains("TOSHIBA") || m.Contains("TOSHIBA")) return "Toshiba";
                if (mdl.Contains("HGST") || m.Contains("HGST")) return "HGST";
            }
            catch { }
            return string.IsNullOrWhiteSpace(manufacturer) ? model : manufacturer;
        }

        // Attempts to detect SSD vs HDD using multiple signals
        private static string GetDriveMediaKind(string letter)
        {
            try
            {
                using var ldSearcher = new System.Management.ManagementObjectSearcher($"SELECT DeviceID FROM Win32_LogicalDisk WHERE DeviceID='{letter}'");
                foreach (System.Management.ManagementObject ld in ldSearcher.Get())
                {
                    string deviceId = ld["DeviceID"]?.ToString() ?? string.Empty;
                    using var assoc = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + deviceId + "'} WHERE AssocClass = Win32_LogicalDiskToPartition");
                    foreach (System.Management.ManagementObject part in assoc.Get())
                    {
                        string? partId = part["DeviceID"]?.ToString();
                        if (string.IsNullOrWhiteSpace(partId)) continue;
                        using var assoc2 = new System.Management.ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partId.Replace("\\", "\\\\") + "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                        foreach (System.Management.ManagementObject drive in assoc2.Get())
                        {
                            // 1) Most reliable: RotationRate (0 -> SSD, >0 -> HDD)
                            try
                            {
                                var rotObj = drive.Properties?["RotationRate"]?.Value;
                                if (rotObj != null)
                                {
                                    uint rot = 0;
                                    try { rot = System.Convert.ToUInt32(rotObj); } catch { rot = 0; }
                                    if (rot == 0) return "SSD";
                                    if (rot > 0) return "HDD";
                                }
                            }
                            catch { }

                            // 2) Storage namespace (MSFT_PhysicalDisk) MediaType, matched by model when possible
                            try
                            {
                                string model = (drive["Model"]?.ToString() ?? string.Empty);
                                var scope = new System.Management.ManagementScope(@"\\.\\ROOT\\Microsoft\\Windows\\Storage");
                                scope.Connect();
                                var pdSearcher = new System.Management.ManagementObjectSearcher(scope, new System.Management.ObjectQuery("SELECT MediaType, FriendlyName, SerialNumber, Model FROM MSFT_PhysicalDisk"));
                                foreach (System.Management.ManagementObject pd in pdSearcher.Get())
                                {
                                    string pdModel = pd["Model"]?.ToString() ?? pd["FriendlyName"]?.ToString() ?? string.Empty;
                                    int media = 0; // 3=HDD, 4=SSD
                                    try { media = System.Convert.ToInt32(pd["MediaType"] ?? 0); } catch { }
                                    if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(pdModel) && pdModel.IndexOf(model, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (media == 4) return "SSD";
                                        if (media == 3) return "HDD";
                                    }
                                }
                            }
                            catch { }

                            // 3) Fallback heuristics: MediaType/Model keywords
                            string mediaType = (drive["MediaType"]?.ToString() ?? string.Empty).ToUpperInvariant();
                            string modelUp = (drive["Model"]?.ToString() ?? string.Empty).ToUpperInvariant();
                            string pnpUp = (drive["PNPDeviceID"]?.ToString() ?? string.Empty).ToUpperInvariant();
                            if (mediaType.Contains("SSD") || modelUp.Contains("SSD") || modelUp.Contains("SOLID STATE") || pnpUp.Contains("NVME") || modelUp.Contains("NVME") || modelUp.Contains("NV-ME") || modelUp.Contains("M.2")) return "SSD";
                            if (mediaType.Contains("HDD") || mediaType.Contains("HARD DISK") || modelUp.Contains("HDD")) return "HDD";
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        // Return the current best-known top process by disk activity (read+write bytes/sec)
        public string GetTopDiskProcessName()
        {
            try
            {
                // Prefer ETW disk accounting when available (closer to Task Manager)
                EnsureEtwNetworkAccounting();
                if (_etwActive && TryComputeEtwDiskTop(out var etwTop) && !string.IsNullOrWhiteSpace(etwTop))
                {
                    return etwTop;
                }

                // Fallback to performance counters when not elevated or ETW not available
                var list = GetTopDiskProcessesNonBlocking(1);
                if (list.Count > 0) return list[0];
                return string.Empty;
            }
            catch { return string.Empty; }
        }

        public NetworkInfo GetNetworkInfo()
        {
            // Prefer ETW totals to match per-process list. Fallback to NIC counters otherwise.
            if (TryComputeEtwRates(3, out var etwTop, out var etwUp, out var etwDown))
            {
                _uploadHistory.Add(etwUp);
                if (_uploadHistory.Count > 30) _uploadHistory.RemoveAt(0);
                _downloadHistory.Add(etwDown);
                if (_downloadHistory.Count > 30) _downloadHistory.RemoveAt(0);

                // Also populate connection details when using ETW so UI bottom card has data
                var upInterfacesEtw = _networkInterfaces.Where(n => n.OperationalStatus == OperationalStatus.Up).ToArray();
                var primaryNicEtw = upInterfacesEtw
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
                string connTypeEtw = primaryNicEtw?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                string linkSpeedEtw = FormatRate(primaryNicEtw?.Speed ?? 0);
                string ipv4Etw = string.Empty;
                try
                {
                    var ipProps = primaryNicEtw?.GetIPProperties();
                    var addr = ipProps?.UnicastAddresses?.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
                    ipv4Etw = addr?.ToString() ?? string.Empty;
                }
                catch { }

                return new NetworkInfo
                {
                    UploadSpeed = FormatRate(etwUp * 1_000_000.0),
                    DownloadSpeed = FormatRate(etwDown * 1_000_000.0),
                    TopProcesses = new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>(etwTop.Count > 0 ? etwTop : SampleTopProcessesBySocketPresence(3)),
                    UploadHistory = new List<double>(_uploadHistory),
                    DownloadHistory = new List<double>(_downloadHistory),
                    ConnectionType = connTypeEtw,
                    LinkSpeed = linkSpeedEtw,
                    Ipv4Address = string.IsNullOrWhiteSpace(ipv4Etw) ? "" : ipv4Etw,
                    Status = upInterfacesEtw.Length > 0 ? "Connected" : "Disconnected"
                };
            }

            long totalBytesSent = 0;
            long totalBytesReceived = 0;
            var upInterfaces = _networkInterfaces.Where(n => n.OperationalStatus == OperationalStatus.Up).ToArray();
            foreach (var ni in upInterfaces)
            {
                var stats = ni.GetIPStatistics();
                totalBytesSent += stats.BytesSent;
                totalBytesReceived += stats.BytesReceived;
            }

            var now = DateTime.Now;
            var timeSpan = now - _lastNetworkSampleTime;
            if (timeSpan.TotalSeconds < 0.1) 
            {
                _lastNetworkSampleTime = now;
                return new NetworkInfo
                {
                    UploadSpeed = "0.0 Mbps",
                    DownloadSpeed = "0.0 Mbps",
                    TopProcesses = new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>(),
                    UploadHistory = new List<double>(_uploadHistory),
                    DownloadHistory = new List<double>(_downloadHistory)
                };
            }
            
            long sent = 0;
            long received = 0;
            foreach (var ni in upInterfaces)
            {
                var stats = ni.GetIPStatistics();
                sent += stats.BytesSent - _lastBytesSent[ni.Id];
                received += stats.BytesReceived - _lastBytesReceived[ni.Id];
                _lastBytesSent[ni.Id] = stats.BytesSent;
                _lastBytesReceived[ni.Id] = stats.BytesReceived;
            }

            _lastNetworkSampleTime = now;

            var uploadBps = (sent / timeSpan.TotalSeconds) * 8.0; // bits per second
            var downloadBps = (received / timeSpan.TotalSeconds) * 8.0; // bits per second
            var uploadMbps = uploadBps / 1_000_000.0;
            var downloadMbps = downloadBps / 1_000_000.0;

            _uploadHistory.Add(uploadMbps);
            if (_uploadHistory.Count > 30) _uploadHistory.RemoveAt(0);

            _downloadHistory.Add(downloadMbps);
            if (_downloadHistory.Count > 30) _downloadHistory.RemoveAt(0);

            var fallbackTop = SampleTopProcessesBySocketPresence(3);

            // Choose a primary NIC for display details
            var primaryNic = upInterfaces
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
            string connType = primaryNic?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
            string linkSpeed = FormatRate(primaryNic?.Speed ?? 0);
            string ipv4 = string.Empty;
            try
            {
                var ipProps = primaryNic?.GetIPProperties();
                var addr = ipProps?.UnicastAddresses?.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
                ipv4 = addr?.ToString() ?? string.Empty;
            }
            catch { }

            return new NetworkInfo
            {
                UploadSpeed = FormatRate(uploadBps),
                DownloadSpeed = FormatRate(downloadBps),
                TopProcesses = new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>(fallbackTop),
                UploadHistory = new List<double>(_uploadHistory),
                DownloadHistory = new List<double>(_downloadHistory),
                ConnectionType = connType,
                LinkSpeed = linkSpeed,
                Ipv4Address = string.IsNullOrWhiteSpace(ipv4) ? "" : ipv4,
                Status = upInterfaces.Length > 0 ? "Connected" : "Disconnected"
            };
        }

        // Gracefully stop background listeners/sessions to avoid crashes on exit
        public void Shutdown()
        {
            try
            {
                lock (_etwInitLock)
                {
                    try
                    {
                        if (_kernelSession != null)
                        {
                            try { _kernelSession.Dispose(); } catch { }
                        }
                    }
                    catch { }
                    finally
                    {
                        _kernelSession = null;
                        _etwActive = false;
                        _etwInitialized = true;
                    }
                }
            }
            catch { }
        }

        private static string FormatRate(double bitsPerSecond)
        {
            try
            {
                if (bitsPerSecond >= 1_000_000_000.0)
                {
                    return $"{(bitsPerSecond / 1_000_000_000.0):F1} Gbps";
                }
                if (bitsPerSecond >= 1_000_000.0)
                {
                    return $"{(bitsPerSecond / 1_000_000.0):F1} Mbps";
                }
                if (bitsPerSecond >= 1_000.0)
                {
                    return $"{(bitsPerSecond / 1_000.0):F1} Kbps";
                }
                return $"{bitsPerSecond:F0} bps";
            }
            catch
            {
                return "0.0 Kbps";
            }
        }

        private static string FormatSizeInGb(double gb)
        {
            try
            {
                if (double.IsNaN(gb) || double.IsInfinity(gb)) return "0 GB";
                if (gb >= 1000.0)
                {
                    var tb = gb / 1000.0;
                    return $"{tb:F1} TB";
                }
                if (gb >= 100.0) return $"{gb:F0} GB";
                return $"{gb:F1} GB";
            }
            catch { return "0 GB"; }
        }

        private static (double usedGb, double totalGb) NormalizeGpuMemoryValues(double usedRaw, double totalRaw)
        {
            try
            {
                bool valuesAreMb = totalRaw > 512 || (totalRaw <= 0 && usedRaw > 512);
                if (valuesAreMb)
                {
                    return (usedRaw / 1024.0, totalRaw / 1024.0);
                }
                return (usedRaw, totalRaw);
            }
            catch { return (usedRaw, totalRaw); }
        }

        private static string GetGpuDriverVersion()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT DriverVersion FROM Win32_VideoController");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    var v = mo["DriverVersion"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string GetGpuDriverDate()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT DriverDate FROM Win32_VideoController");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    var v = mo["DriverDate"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        try { return System.Management.ManagementDateTimeConverter.ToDateTime(v).ToString("yyyy-MM-dd"); } catch { return v; }
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string GetMemoryXmpOrExpo()
        {
            try
            {
                string cpuVendor = string.Empty;
                try
                {
                    using var cpuSearcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor");
                    foreach (System.Management.ManagementObject mo in cpuSearcher.Get())
                    {
                        var v = mo["Manufacturer"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(v)) { cpuVendor = v; break; }
                    }
                }
                catch { }

                double configuredMax = 0.0;
                int smbiosTypeMax = 0;
                try
                {
                    using var memSearcher = new System.Management.ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                    foreach (System.Management.ManagementObject mo in memSearcher.Get())
                    {
                        try { configuredMax = Math.Max(configuredMax, Convert.ToDouble(mo["ConfiguredClockSpeed"] ?? 0.0)); } catch { }
                        try { smbiosTypeMax = Math.Max(smbiosTypeMax, Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0)); } catch { }
                    }
                }
                catch { }

                bool isDdr5 = smbiosTypeMax == 34; // 34 = DDR5 per SMBIOS
                // Coarse baselines for inference
                double baseline = isDdr5 ? 5600.0 : 3200.0;
                double threshold = isDdr5 ? 6000.0 : 3600.0;
                bool overclocked = configuredMax >= threshold || (configuredMax > baseline + 150);
                if (overclocked)
                {
                    bool isAmdCpu = cpuVendor.IndexOf("amd", StringComparison.OrdinalIgnoreCase) >= 0;
                    return isAmdCpu ? "EXPO" : "XMP";
                }
            }
            catch { }
            return "—";
        }

        // Map SMBIOS MemoryDevice.FormFactor codes to display string
        private static string MapMemoryFormFactor(int code)
        {
            try
            {
                switch (code)
                {
                    case 8: return "DIMM"; // 8 = DIMM
                    case 9: return "TBDIMM"; // RIMM/older; keep generic
                    case 12: return "SO-DIMM"; // 12 = SODIMM
                    case 16: return "RDIMM"; // Registered DIMM
                    case 17: return "Mini-RDIMM";
                    case 18: return "UDIMM"; // Unbuffered DIMM
                    default: return string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        // Map SMBIOSMemoryType codes to DDR generation string
        private static string MapSmbiosMemoryType(int code)
        {
            try
            {
                switch (code)
                {
                    case 20: return "DDR";
                    case 21: return "DDR2";
                    case 24: return "DDR3";
                    case 26: return "DDR4";
                    case 34: return "DDR5";
                    default: return string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        // Build a short summary like "2x16GB"; if sizes vary, fall back to "Nx" summary
        private static string SummarizeModuleConfiguration(List<double> moduleSizesGb)
        {
            try
            {
                if (moduleSizesGb == null || moduleSizesGb.Count == 0) return string.Empty;
                // Round each module to nearest whole GB for readability
                var rounded = moduleSizesGb
                    .Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v))
                    .Select(v => (int)Math.Round(v))
                    .ToList();
                if (rounded.Count == 0) return string.Empty;

                // If all equal, show NxSIZEGB
                bool allEqual = rounded.All(r => r == rounded[0]);
                if (allEqual)
                {
                    return $"{rounded.Count}x{rounded[0]}GB";
                }

                // Otherwise, show the most common size if strong majority, else generic count
                var grouped = rounded
                    .GroupBy(r => r)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .ToList();
                if (grouped.Count > 0 && grouped[0].Count() >= Math.Max(2, (int)Math.Ceiling(rounded.Count * 0.5)))
                {
                    return $"{grouped[0].Count()}x{grouped[0].Key}GB";
                }

                return $"{rounded.Count} modules";
            }
            catch { return string.Empty; }
        }

        private static List<(string Name, string DriverVersion, string DriverDate, string PnpId)> QueryVideoControllerDrivers()
        {
            var list = new List<(string Name, string DriverVersion, string DriverDate, string PnpId)>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    string name = mo["Name"]?.ToString() ?? string.Empty;
                    string ver = mo["DriverVersion"]?.ToString() ?? string.Empty;
                    string dateStr = mo["DriverDate"]?.ToString() ?? string.Empty;
                    string date = string.Empty;
                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        try { date = System.Management.ManagementDateTimeConverter.ToDateTime(dateStr).ToString("yyyy-MM-dd"); } catch { date = dateStr; }
                    }
                    string pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                    list.Add((name, ver, date, pnp));
                }
            }
            catch { }
            
            // Fallback: supplement versions/dates via Win32_PnPSignedDriver when missing
            try
            {
                using var pnpSearcher = new System.Management.ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate, Class FROM Win32_PnPSignedDriver WHERE Class='DISPLAY'");
                var pnpList = new List<(string DeviceName, string Version, string Date)>();
                foreach (System.Management.ManagementObject mo in pnpSearcher.Get())
                {
                    string dev = mo["DeviceName"]?.ToString() ?? string.Empty;
                    string ver = mo["DriverVersion"]?.ToString() ?? string.Empty;
                    string dateStr = mo["DriverDate"]?.ToString() ?? string.Empty;
                    string date = string.Empty;
                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        try { date = System.Management.ManagementDateTimeConverter.ToDateTime(dateStr).ToString("yyyy-MM-dd"); } catch { date = dateStr; }
                    }
                    if (!string.IsNullOrWhiteSpace(dev)) pnpList.Add((dev, ver, date));
                }

                if (pnpList.Count > 0)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(list[i].DriverVersion) && !string.IsNullOrWhiteSpace(list[i].DriverDate)) continue;
                        var match = pnpList.FirstOrDefault(p => NamesRoughlyMatch(p.DeviceName, list[i].Name));
                        if (!string.IsNullOrWhiteSpace(match.DeviceName))
                        {
                            var updated = list[i];
                            if (string.IsNullOrWhiteSpace(updated.DriverVersion)) updated.DriverVersion = match.Version;
                            if (string.IsNullOrWhiteSpace(updated.DriverDate)) updated.DriverDate = match.Date;
                            list[i] = updated;
                        }
                    }
                }
            }
            catch { }

            return list;
        }

        private static (string Version, string Date) TryMatchDriverForAdapter(string adapterName, List<(string Name, string DriverVersion, string DriverDate, string PnpId)> controllers)
        {
            try
            {
                if (controllers == null || controllers.Count == 0) return (string.Empty, string.Empty);
                if (string.IsNullOrWhiteSpace(adapterName))
                {
                    var first = controllers[0];
                    return (first.DriverVersion, first.DriverDate);
                }

                // Best-effort fuzzy match by token overlap
                string normAdapter = NormalizeAdapterName(adapterName);
                (string Name, string DriverVersion, string DriverDate, string PnpId)? best = null;
                int bestScore = -1;
                foreach (var c in controllers)
                {
                    string normC = NormalizeAdapterName(c.Name);
                    int score = ComputeTokenOverlapScore(normAdapter, normC);
                    // Light vendor bonus to break ties
                    if (normAdapter.Contains("NVIDIA") && normC.Contains("NVIDIA")) score += 2;
                    if (normAdapter.Contains("RADEON") && normC.Contains("RADEON")) score += 2;
                    if (normAdapter.Contains("AMD") && normC.Contains("AMD")) score += 1;
                    if (normAdapter.Contains("INTEL") && normC.Contains("INTEL")) score += 2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                    }
                }

                if (best.HasValue)
                {
                    return (best.Value.DriverVersion, best.Value.DriverDate);
                }

                var fallback = controllers[0];
                return (fallback.DriverVersion, fallback.DriverDate);
            }
            catch { return (string.Empty, string.Empty); }
        }

        private static string NormalizeAdapterName(string s)
        {
            try
            {
                var t = (s ?? string.Empty).ToUpperInvariant();
                t = t.Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace("(C)", string.Empty);
                t = t.Replace("GRAPHICS", string.Empty).Replace("GPU", string.Empty).Replace("VIDEO", string.Empty);
                // Collapse spaces
                t = System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
                return t;
            }
            catch { return s ?? string.Empty; }
        }

        private static int ComputeTokenOverlapScore(string a, string b)
        {
            try
            {
                var at = new HashSet<string>((a ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var bt = new HashSet<string>((b ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                int score = at.Intersect(bt).Count();
                // Prefer longer matches slightly
                score += Math.Min(at.Count, bt.Count) >= 3 ? 1 : 0;
                return score;
            }
            catch { return 0; }
        }

        private static bool NamesRoughlyMatch(string a, string b)
        {
            try
            {
                string na = NormalizeAdapterName(a);
                string nb = NormalizeAdapterName(b);
                if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb)) return false;
                if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return true;
                int score = ComputeTokenOverlapScore(na, nb);
                if (score >= 2) return true;
                // Vendor cues can still be useful in low-score cases
                if (na.Contains("NVIDIA") && nb.Contains("NVIDIA")) return true;
                if ((na.Contains("RADEON") || na.Contains("AMD")) && (nb.Contains("RADEON") || nb.Contains("AMD"))) return true;
                if (na.Contains("INTEL") && nb.Contains("INTEL")) return true;
                return false;
            }
            catch { return false; }
        }

        // Non-ETW fallback: list processes that currently own TCP/UDP sockets
        private List<NetworkProcessInfo> SampleTopProcessesBySocketPresence(int topN)
        {
            var pidScores = new Dictionary<int, int>();
            try
            {
                void ScorePid(int pid)
                {
                    if (pid <= 0) return;
                    if (!pidScores.ContainsKey(pid)) pidScores[pid] = 0;
                    pidScores[pid] += 1;
                }

                // Prefer processes with established outbound TCP connections (closer to Task Manager)
                foreach (var pid in EnumerateEstablishedTcpOwnerPidsV4()) ScorePid(pid);

                var top = pidScores
                    .OrderByDescending(k => k.Value)
                    .Take(Math.Max(0, topN))
                    .Select(k =>
                    {
                        string name;
                        try
                        {
                            using var p = Process.GetProcessById(k.Key);
                            var pn = p.ProcessName ?? k.Key.ToString();
                            name = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        }
                        catch { name = k.Key.ToString(); }

                        return new NetworkProcessInfo
                        {
                            Name = name,
                            Speed = string.Empty,
                            UploadSpeed = string.Empty,
                            DownloadSpeed = string.Empty
                        };
                    })
                    .ToList();

                return top;
            }
            catch
            {
                return new List<NetworkProcessInfo>();
            }
        }

        // Enumerate IPv4 TCP connections that are ESTABLISHED with a non-loopback remote
        // and return their owning PIDs. This is a heuristic to approximate "active network" processes.
        private IEnumerable<int> EnumerateEstablishedTcpOwnerPidsV4()
        {
            int size = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0 && size <= 0) yield break;
                buf = Marshal.AllocHGlobal(size);
                ret = GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) yield break;

                int num = Marshal.ReadInt32(buf);
                IntPtr rowPtr = buf + 4;
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < num; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    const uint MIB_TCP_STATE_ESTABLISHED = 5;
                    if (row.state == MIB_TCP_STATE_ESTABLISHED)
                    {
                        // Ignore loopback/unspecified
                        if (row.remoteAddr != 0 && row.remoteAddr != 0x0100007F)
                        {
                            yield return unchecked((int)row.owningPid);
                        }
                    }
                    rowPtr += rowSize;
                }
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        private bool TryComputeEtwRates(int topN, out List<NetworkProcessInfo> top, out double uploadMbps, out double downloadMbps)
        {
            top = new List<NetworkProcessInfo>();
            uploadMbps = 0.0;
            downloadMbps = 0.0;
            try
            {
                EnsureEtwNetworkAccounting();
                Dictionary<int, (long sentBytes, long recvBytes)> totals;
                DateTime nowUtc = DateTime.UtcNow;
                lock (_etwBytesLock)
                {
                    if (_etwBytesByPid.Count == 0) return false;
                    totals = new Dictionary<int, (long, long)>(_etwBytesByPid);
                }

                var elapsed = nowUtc - _etwLastSnapshotUtc;
                if (elapsed <= TimeSpan.Zero || _etwLastSnapshotUtc == DateTime.MinValue)
                {
                    _etwLastTotalsByPid = totals;
                    _etwLastSnapshotUtc = nowUtc;
                    return false; // no rate yet
                }

                var rates = new List<(int pid, double upBps, double downBps, string name)>();
                long sumUp = 0;
                long sumDown = 0;
                foreach (var kv in totals)
                {
                    var pid = kv.Key;
                    var cur = kv.Value;
                    _etwLastTotalsByPid.TryGetValue(pid, out var prev);
                    var deltaUp = Math.Max(0L, cur.sentBytes - prev.sentBytes);
                    var deltaDown = Math.Max(0L, cur.recvBytes - prev.recvBytes);
                    sumUp += deltaUp;
                    sumDown += deltaDown;
                    double upBps = deltaUp / Math.Max(0.001, elapsed.TotalSeconds);
                    double downBps = deltaDown / Math.Max(0.001, elapsed.TotalSeconds);
                    string name = _pidToNameCache.GetOrAdd(pid, static key =>
                    {
                        try
                        {
                            using var p = Process.GetProcessById(key);
                            var pn = p.ProcessName ?? key.ToString();
                            return pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        }
                        catch { return key.ToString(); }
                    });
                    rates.Add((pid, upBps, downBps, name));
                }

                _etwLastTotalsByPid = totals;
                _etwLastSnapshotUtc = nowUtc;

                uploadMbps = ((sumUp / Math.Max(0.001, elapsed.TotalSeconds)) * 8.0) / (1024.0 * 1024.0);
                downloadMbps = ((sumDown / Math.Max(0.001, elapsed.TotalSeconds)) * 8.0) / (1024.0 * 1024.0);

                top = rates
                    .OrderByDescending(r => (r.upBps + r.downBps))
                    .Take(Math.Max(0, topN))
                    .Select(r => new NetworkProcessInfo
                    {
                        Name = r.name,
                        Speed = $"{((r.upBps + r.downBps) * 8.0) / (1024.0 * 1024.0):F1} Mbps",
                        UploadSpeed = $"{(r.upBps * 8.0) / (1024.0 * 1024.0):F1} Mbps",
                        DownloadSpeed = $"{(r.downBps * 8.0) / (1024.0 * 1024.0):F1} Mbps"
                    })
                    .ToList();
                return true;
            }
            catch { return false; }
        }
        private List<NetworkProcessInfo> GetTopNetworkProcessesFromEtwOrFallback(int topN)
        {
            try
            {
                EnsureEtwNetworkAccounting();
                Dictionary<int, (long sentBytes, long recvBytes)> totals;
                DateTime nowUtc = DateTime.UtcNow;
                lock (_etwBytesLock)
                {
                    if (_etwBytesByPid.Count == 0)
                    {
                        return new List<NetworkProcessInfo>();
                    }
                    totals = new Dictionary<int, (long, long)>(_etwBytesByPid);
                }

                var elapsed = nowUtc - _etwLastSnapshotUtc;
                if (elapsed <= TimeSpan.Zero || _etwLastSnapshotUtc == DateTime.MinValue)
                {
                    _etwLastTotalsByPid = totals;
                    _etwLastSnapshotUtc = nowUtc;
                    return new List<NetworkProcessInfo>(); // first run has no rate yet
                }

                // Compute deltas since last snapshot
                var rates = new List<(int pid, double mbps, string name)>();
                foreach (var kv in totals)
                {
                    var pid = kv.Key;
                    var cur = kv.Value;
                    _etwLastTotalsByPid.TryGetValue(pid, out var prev);
                    var deltaBytes = Math.Max(0L, (cur.sentBytes + cur.recvBytes) - (prev.sentBytes + prev.recvBytes));
                    var bytesPerSec = deltaBytes / Math.Max(0.001, elapsed.TotalSeconds);
                    var mbps = (bytesPerSec * 8.0) / (1024.0 * 1024.0);
                    string name = _pidToNameCache.GetOrAdd(pid, static key =>
                    {
                        try
                        {
                            using var p = Process.GetProcessById(key);
                            var pn = p.ProcessName ?? key.ToString();
                            return pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        }
                        catch { return key.ToString(); }
                    });
                    rates.Add((pid, mbps, name));
                }

                _etwLastTotalsByPid = totals;
                _etwLastSnapshotUtc = nowUtc;

                var top = rates
                    .OrderByDescending(r => r.mbps)
                    .Take(Math.Max(0, topN))
                    .Select(r => new NetworkProcessInfo { Name = r.name, Speed = $"{r.mbps:F1} Mbps" })
                    .ToList();
                return top;
            }
            catch
            {
                return new List<NetworkProcessInfo>();
            }
        }

        private void EnsureEtwNetworkAccounting()
        {
            if (_etwInitialized) return;
            lock (_etwInitLock)
            {
                if (_etwInitialized) return;
                try
                {
                    // Kernel session provides TCP/IP send/receive events (admin required)
                    var isElevated = TraceEventSession.IsElevated();
                    if (isElevated != true)
                    {
                        _etwActive = false;
                        _etwInitialized = true; // Do not retry aggressively if not elevated
                        return;
                    }
                    // Try a unique real-time private session first
                    var privateSessionName = $"Bluetask-Kernel-Network-RT-{Process.GetCurrentProcess().Id}";
                    try
                    {
                        _kernelSession = new TraceEventSession(privateSessionName, null);
                        _kernelSession.StopOnDispose = true;
                        _kernelSession.EnableKernelProvider(
                            KernelTraceEventParser.Keywords.NetworkTCPIP |
                            KernelTraceEventParser.Keywords.DiskIO |
                            KernelTraceEventParser.Keywords.FileIO |
                            KernelTraceEventParser.Keywords.DiskFileIO);

                        var kernel = _kernelSession.Source.Kernel;
                        kernel.TcpIpSend += data =>
                        {
                            lock (_etwBytesLock)
                            {
                                var pid = data.ProcessID;
                                if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                cur.sentBytes += data.size;
                                _etwBytesByPid[pid] = cur;
                            }
                            System.Threading.Interlocked.Increment(ref _etwEventCount);
                        };
                        kernel.TcpIpRecv += data =>
                        {
                            lock (_etwBytesLock)
                            {
                                var pid = data.ProcessID;
                                if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                cur.recvBytes += data.size;
                                _etwBytesByPid[pid] = cur;
                            }
                            System.Threading.Interlocked.Increment(ref _etwEventCount);
                        };
                        kernel.UdpIpSend += data =>
                        {
                            lock (_etwBytesLock)
                            {
                                var pid = data.ProcessID;
                                if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                cur.sentBytes += data.size;
                                _etwBytesByPid[pid] = cur;
                            }
                            System.Threading.Interlocked.Increment(ref _etwEventCount);
                        };
                        kernel.UdpIpRecv += data =>
                        {
                            lock (_etwBytesLock)
                            {
                                var pid = data.ProcessID;
                                if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                cur.recvBytes += data.size;
                                _etwBytesByPid[pid] = cur;
                            }
                            System.Threading.Interlocked.Increment(ref _etwEventCount);
                        };

                        // Disk IO events (lower level). Disabled by default to avoid double counting with FileIO.
                        kernel.DiskIORead += data =>
                        {
                            if (!_countDiskIoLayer) return;
                            try
                            {
                                long sz = ExtractDiskIoSize(data);
                                if (sz <= 0) return;
                                lock (_etwDiskBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                    cur += sz;
                                    _etwDiskBytesByPid[pid] = cur;
                                }
                            }
                            catch { }
                        };
                        kernel.DiskIOWrite += data =>
                        {
                            if (!_countDiskIoLayer) return;
                            try
                            {
                                long sz = ExtractDiskIoSize(data);
                                if (sz <= 0) return;
                                lock (_etwDiskBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                    cur += sz;
                                    _etwDiskBytesByPid[pid] = cur;
                                }
                            }
                            catch { }
                        };

                        // File IO events (higher level, matches Task Manager disk column more closely)
                        kernel.FileIORead += data =>
                        {
                            try
                            {
                                long sz = ExtractDiskIoSize(data);
                                if (sz <= 0) return;
                                lock (_etwDiskBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                    cur += sz;
                                    _etwDiskBytesByPid[pid] = cur;
                                }
                            }
                            catch { }
                        };
                        kernel.FileIOWrite += data =>
                        {
                            try
                            {
                                long sz = ExtractDiskIoSize(data);
                                if (sz <= 0) return;
                                lock (_etwDiskBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                    cur += sz;
                                    _etwDiskBytesByPid[pid] = cur;
                                }
                            }
                            catch { }
                        };

                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try { _kernelSession.Source.Process(); } catch { }
                        });

                        _etwActive = true;
                    }
                    catch
                    {
                        // Fallback to attaching to the global kernel logger
                        try
                        {
                            _kernelSession = new TraceEventSession(KernelTraceEventParser.KernelSessionName, null);
                            _kernelSession.StopOnDispose = false; // never stop the global session
                            _kernelSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.DiskFileIO);

                            var kernel = _kernelSession.Source.Kernel;
                            kernel.TcpIpSend += data =>
                            {
                                lock (_etwBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                    cur.sentBytes += data.size;
                                    _etwBytesByPid[pid] = cur;
                                }
                                System.Threading.Interlocked.Increment(ref _etwEventCount);
                            };
                            kernel.TcpIpRecv += data =>
                            {
                                lock (_etwBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                    cur.recvBytes += data.size;
                                    _etwBytesByPid[pid] = cur;
                                }
                                System.Threading.Interlocked.Increment(ref _etwEventCount);
                            };
                            kernel.UdpIpSend += data =>
                            {
                                lock (_etwBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                    cur.sentBytes += data.size;
                                    _etwBytesByPid[pid] = cur;
                                }
                                System.Threading.Interlocked.Increment(ref _etwEventCount);
                            };
                            kernel.UdpIpRecv += data =>
                            {
                                lock (_etwBytesLock)
                                {
                                    var pid = data.ProcessID;
                                    if (!_etwBytesByPid.TryGetValue(pid, out var cur)) cur = (0, 0);
                                    cur.recvBytes += data.size;
                                    _etwBytesByPid[pid] = cur;
                                }
                                System.Threading.Interlocked.Increment(ref _etwEventCount);
                            };

                            kernel.DiskIORead += data =>
                            {
                                if (!_countDiskIoLayer) return;
                                try
                                {
                                    long sz = ExtractDiskIoSize(data);
                                    if (sz <= 0) return;
                                    lock (_etwDiskBytesLock)
                                    {
                                        var pid = data.ProcessID;
                                        if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                        cur += sz;
                                        _etwDiskBytesByPid[pid] = cur;
                                    }
                                }
                                catch { }
                            };
                            kernel.DiskIOWrite += data =>
                            {
                                if (!_countDiskIoLayer) return;
                                try
                                {
                                    long sz = ExtractDiskIoSize(data);
                                    if (sz <= 0) return;
                                    lock (_etwDiskBytesLock)
                                    {
                                        var pid = data.ProcessID;
                                        if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                        cur += sz;
                                        _etwDiskBytesByPid[pid] = cur;
                                    }
                                }
                                catch { }
                            };

                            kernel.FileIORead += data =>
                            {
                                try
                                {
                                    long sz = ExtractDiskIoSize(data);
                                    if (sz <= 0) return;
                                    lock (_etwDiskBytesLock)
                                    {
                                        var pid = data.ProcessID;
                                        if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                        cur += sz;
                                        _etwDiskBytesByPid[pid] = cur;
                                    }
                                }
                                catch { }
                            };
                            kernel.FileIOWrite += data =>
                            {
                                try
                                {
                                    long sz = ExtractDiskIoSize(data);
                                    if (sz <= 0) return;
                                    lock (_etwDiskBytesLock)
                                    {
                                        var pid = data.ProcessID;
                                        if (!_etwDiskBytesByPid.TryGetValue(pid, out var cur)) cur = 0;
                                        cur += sz;
                                        _etwDiskBytesByPid[pid] = cur;
                                    }
                                }
                                catch { }
                            };

                            _ = System.Threading.Tasks.Task.Run(() =>
                            {
                                try { _kernelSession.Source.Process(); } catch { }
                            });

                            _etwActive = true;
                        }
                        catch
                        {
                            _etwActive = false;
                        }
                    }
                }
                catch
                {
                    _etwActive = false;
                }
                finally
                {
                    _etwInitialized = true;
                }
            }
        }

        private static long ExtractDiskIoSize(object evt)
        {
            try
            {
                var t = evt.GetType();
                var prop = t.GetProperty("TransferSize", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                           ?? t.GetProperty("Size", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                           ?? t.GetProperty("IoSize", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                           ?? t.GetProperty("Bytes", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    var v = prop.GetValue(evt);
                    if (v is IConvertible) return Convert.ToInt64(v);
                }
            }
            catch { }
            return 0;
        }

        private bool TryComputeEtwDiskTop(out string topProcessName)
        {
            topProcessName = string.Empty;
            try
            {
                EnsureEtwNetworkAccounting();
                Dictionary<int, long> totals;
                DateTime nowUtc = DateTime.UtcNow;
                lock (_etwDiskBytesLock)
                {
                    if (_etwDiskBytesByPid.Count == 0) return false;
                    totals = new Dictionary<int, long>(_etwDiskBytesByPid);
                }

                var elapsed = nowUtc - _etwDiskLastSnapshotUtc;
                if (elapsed <= TimeSpan.Zero || _etwDiskLastSnapshotUtc == DateTime.MinValue)
                {
                    _etwDiskLastTotalsByPid = totals;
                    _etwDiskLastSnapshotUtc = nowUtc;
                    return false; // first snapshot has no rate
                }

                double maxBps = -1.0;
                int maxPid = -1;
                foreach (var kv in totals)
                {
                    var pid = kv.Key;
                    var cur = kv.Value;
                    _etwDiskLastTotalsByPid.TryGetValue(pid, out var prev);
                    var delta = Math.Max(0L, cur - prev);
                    var bps = delta / Math.Max(0.001, elapsed.TotalSeconds);
                    if (bps > maxBps)
                    {
                        maxBps = bps;
                        maxPid = pid;
                    }
                }

                _etwDiskLastTotalsByPid = totals;
                _etwDiskLastSnapshotUtc = nowUtc;

                if (maxPid > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(maxPid);
                        var pn = p.ProcessName ?? maxPid.ToString();
                        topProcessName = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                    }
                    catch { topProcessName = maxPid.ToString(); }
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        // Returns cached results immediately; kicks off a background refresh when stale
        private List<NetworkProcessInfo> GetTopNetworkProcessesNonBlocking(int topN)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var cached = _lastTopProcNet;
                var isStale = (nowUtc - _lastProcSampleUtc) > TimeSpan.FromSeconds(2);
                if (isStale && System.Threading.Interlocked.Exchange(ref _procRefreshInProgress, 1) == 0)
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { _ = SampleTopNetworkProcesses(topN); }
                        catch { }
                        finally { System.Threading.Interlocked.Exchange(ref _procRefreshInProgress, 0); }
                    });
                }
                return cached;
            }
            catch { return _lastTopProcNet; }
        }

        private List<NetworkProcessInfo> SampleTopNetworkProcesses(int topN)
        {
            try
            {
                if (_disableProcPerfCounters) return _lastTopProcNet;
                // Throttle reads to once per ~1s; reuse last if requested sooner
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastProcSampleUtc) < TimeSpan.FromMilliseconds(800) && _lastTopProcNet.Count > 0)
                {
                    return _lastTopProcNet;
                }

                var category = new PerformanceCounterCategory("Process");
                System.Diagnostics.CounterSample CalculateRate(string instanceName,
                    System.Diagnostics.InstanceDataCollection collection,
                    Dictionary<string, System.Diagnostics.CounterSample> cache,
                    out double rate)
                {
                    rate = 0.0;
                    if (collection == null) return default;
                    if (!collection.Contains(instanceName)) return default;
                    var newSample = collection[instanceName].Sample;
                    if (cache.TryGetValue(instanceName, out var oldSample))
                    {
                        try { rate = CounterSample.Calculate(oldSample, newSample); }
                        catch { rate = 0.0; }
                    }
                    cache[instanceName] = newSample;
                    return newSample;
                }

                System.Diagnostics.InstanceDataCollectionCollection data;
                try
                {
                    data = category.ReadCategory();
                }
                catch
                {
                    _disableProcPerfCounters = true;
                    return _lastTopProcNet;
                }
                if (data == null) return _lastTopProcNet;

                System.Diagnostics.InstanceDataCollection? otherBytesColl = null;
                System.Diagnostics.InstanceDataCollection? idColl = null;
                if (data.Contains("IO Other Bytes/sec")) otherBytesColl = data["IO Other Bytes/sec"];
                if (data.Contains("ID Process")) idColl = data["ID Process"];
                if (otherBytesColl == null || idColl == null) return _lastTopProcNet;

                var otherC = otherBytesColl!;
                var idC = idColl!;

                var bytesPerSecByPid = new Dictionary<int, double>();
                lock (_procNetLock)
                {
                    foreach (System.Collections.DictionaryEntry entry in otherC)
                    {
                        var instanceName = (string)entry.Key;
                        if (string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase)) continue;

                        // Map instance -> PID once from ID Process
                        int pid = 0;
                        try
                        {
                            if (idC.Contains(instanceName))
                            {
                                pid = (int)idC[instanceName].RawValue;
                            }
                        }
                        catch { pid = 0; }
                        if (pid <= 0) continue;

                        _pidByInstance[instanceName] = pid;

                        // Calculate bytes/sec since last sample for this instance
                        double bytesPerSec;
                        CalculateRate(instanceName, otherC, _lastOtherBytesSampleByInstance, out bytesPerSec);
                        if (double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec) || bytesPerSec < 0) continue;

                        if (!bytesPerSecByPid.ContainsKey(pid)) bytesPerSecByPid[pid] = 0.0;
                        bytesPerSecByPid[pid] += bytesPerSec;
                    }
                }

                var top = bytesPerSecByPid
                    .OrderByDescending(k => k.Value)
                    .Take(Math.Max(0, topN))
                    .Select(k =>
                    {
                        string name;
                        try
                        {
                            using var p = Process.GetProcessById(k.Key);
                            var pn = p.ProcessName ?? k.Key.ToString();
                            name = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        }
                        catch { name = k.Key.ToString(); }

                        var mbps = (k.Value * 8.0) / (1024.0 * 1024.0);
                        return new NetworkProcessInfo { Name = name, Speed = $"{mbps:F1} Mbps" };
                    })
                    .ToList();

                _lastTopProcNet = top;
                _lastProcSampleUtc = nowUtc;
                return top;
            }
            catch
            {
                return _lastTopProcNet;
            }
        }

        // Non-blocking cached result; triggers background refresh when stale
        private List<string> GetTopDiskProcessesNonBlocking(int topN)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var cached = _lastTopDiskProc;
                var isStale = (nowUtc - _lastDiskProcSampleUtc) > TimeSpan.FromSeconds(2);
                if (isStale && System.Threading.Interlocked.Exchange(ref _diskProcRefreshInProgress, 1) == 0)
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { _ = SampleTopDiskProcesses(topN); }
                        catch { }
                        finally { System.Threading.Interlocked.Exchange(ref _diskProcRefreshInProgress, 0); }
                    });
                }
                return cached;
            }
            catch { return _lastTopDiskProc; }
        }

        private List<string> SampleTopDiskProcesses(int topN)
        {
            try
            {
                if (_disableProcPerfCounters) return _lastTopDiskProc;
                // Throttle reads to once per ~1s; reuse last if requested sooner
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastDiskProcSampleUtc) < TimeSpan.FromMilliseconds(800) && _lastTopDiskProc.Count > 0)
                {
                    return _lastTopDiskProc;
                }

                var category = new PerformanceCounterCategory("Process");

                System.Diagnostics.CounterSample CalcRate(string instanceName,
                    System.Diagnostics.InstanceDataCollection collection,
                    Dictionary<string, System.Diagnostics.CounterSample> cache,
                    out double rate)
                {
                    rate = 0.0;
                    if (collection == null) return default;
                    if (!collection.Contains(instanceName)) return default;
                    var newSample = collection[instanceName].Sample;
                    if (cache.TryGetValue(instanceName, out var oldSample))
                    {
                        try { rate = CounterSample.Calculate(oldSample, newSample); }
                        catch { rate = 0.0; }
                    }
                    cache[instanceName] = newSample;
                    return newSample;
                }

                System.Diagnostics.InstanceDataCollectionCollection data;
                try
                {
                    data = category.ReadCategory();
                }
                catch
                {
                    _disableProcPerfCounters = true;
                    return _lastTopDiskProc;
                }
                if (data == null) return _lastTopDiskProc;

                System.Diagnostics.InstanceDataCollection? readColl = null;
                System.Diagnostics.InstanceDataCollection? writeColl = null;
                System.Diagnostics.InstanceDataCollection? idColl = null;
                if (data.Contains("IO Read Bytes/sec")) readColl = data["IO Read Bytes/sec"];
                if (data.Contains("IO Write Bytes/sec")) writeColl = data["IO Write Bytes/sec"];
                if (data.Contains("ID Process")) idColl = data["ID Process"];
                if (idColl == null || (readColl == null && writeColl == null)) return _lastTopDiskProc;

                var idC = idColl!;
                var bytesPerSecByPid = new Dictionary<int, double>();

                // Use the union of instance names seen in read/write collections
                var instanceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (readColl != null) foreach (System.Collections.DictionaryEntry e in readColl) instanceNames.Add((string)e.Key);
                if (writeColl != null) foreach (System.Collections.DictionaryEntry e in writeColl) instanceNames.Add((string)e.Key);

                lock (_procDiskLock)
                {
                    foreach (var instanceName in instanceNames)
                    {
                        if (string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase)) continue;

                        int pid = 0;
                        try
                        {
                            if (idC.Contains(instanceName)) pid = (int)idC[instanceName].RawValue;
                        }
                        catch { pid = 0; }
                        if (pid <= 0) continue;

                        double readRate = 0.0, writeRate = 0.0;
                        if (readColl != null)
                        {
                            CalcRate(instanceName, readColl!, _lastIoReadSampleByInstance, out readRate);
                        }
                        if (writeColl != null)
                        {
                            CalcRate(instanceName, writeColl!, _lastIoWriteSampleByInstance, out writeRate);
                        }
                        var total = 0.0;
                        if (!double.IsNaN(readRate) && !double.IsInfinity(readRate) && readRate > 0) total += readRate;
                        if (!double.IsNaN(writeRate) && !double.IsInfinity(writeRate) && writeRate > 0) total += writeRate;
                        if (total <= 0) continue;

                        if (!bytesPerSecByPid.ContainsKey(pid)) bytesPerSecByPid[pid] = 0.0;
                        bytesPerSecByPid[pid] += total;
                    }
                }

                var top = bytesPerSecByPid
                    .OrderByDescending(k => k.Value)
                    .Take(Math.Max(0, topN))
                    .Select(k =>
                    {
                        try
                        {
                            using var p = Process.GetProcessById(k.Key);
                            var pn = p.ProcessName ?? k.Key.ToString();
                            return pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        }
                        catch { return k.Key.ToString(); }
                    })
                    .ToList();

                _lastTopDiskProc = top;
                _lastDiskProcSampleUtc = nowUtc;
                return top;
            }
            catch
            {
                return _lastTopDiskProc;
            }
        }

        private double TryGetCurrentClockFromPerfCounterGhz()
        {
            try
            {
                if (_cpuPerfCounter == null) return 0.0;
                var pct = _cpuPerfCounter.NextValue();
                if (double.IsNaN(pct) || pct <= 0) return 0.0;
                var baseGhz = GetBaseClockGhz();
                if (baseGhz <= 0) return 0.0;
                var ghz = (baseGhz * (pct / 100.0));
                if (double.IsNaN(ghz) || double.IsInfinity(ghz) || ghz <= 0) return 0.0;
                return ghz;
            }
            catch { return 0.0; }
        }

        private double GetBaseClockGhz()
        {
            if (_baseClockGhzCached > 0) return _baseClockGhzCached;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    var max = mo["MaxClockSpeed"];
                    if (max != null)
                    {
                        var mhz = Convert.ToDouble(max);
                        if (!double.IsNaN(mhz) && mhz > 0)
                        {
                            _baseClockGhzCached = mhz / 1000.0;
                            break;
                        }
                    }
                }
            }
            catch { _baseClockGhzCached = -1.0; }
            return _baseClockGhzCached;
        }

        private static string ResolveRamBrand(List<string> manufacturers, List<string> partNumbers)
        {
            try
            {
                static bool IsPlaceholder(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return true;
                    var t = s.Trim();
                    return t.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("Not Available", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("N/A", StringComparison.OrdinalIgnoreCase);
                }

                var valid = manufacturers.Where(m => !IsPlaceholder(m)).Select(m => m.Trim()).Distinct().ToList();
                if (valid.Count > 0)
                {
                    // Prefer the longest/most descriptive manufacturer string
                    return valid.OrderByDescending(s => s.Length).First();
                }

                // Heuristic from part numbers
                var joined = string.Join(" ", partNumbers).ToUpperInvariant();
                if (joined.Contains("GSKILL") || joined.Contains("G.SKILL") || joined.Contains("F4-") || joined.Contains("F5-")) return "G.SKILL";
                if (joined.Contains("CORSAIR") || joined.StartsWith("CM")) return "Corsair";
                if (joined.Contains("KINGSTON") || joined.Contains("HYPERX") || joined.StartsWith("KF") || joined.StartsWith("KVR") || joined.StartsWith("KSM")) return "Kingston";
                if (joined.Contains("CRUCIAL") || joined.StartsWith("CT")) return "Crucial";
                if (joined.Contains("TEAM") || joined.Contains("T-FORCE") || joined.StartsWith("TF")) return "TeamGroup";
                if (joined.Contains("ADATA") || joined.Contains("XPG") || joined.StartsWith("AX4U")) return "ADATA";
                if (joined.Contains("PATRIOT") || joined.StartsWith("PV")) return "Patriot";
                if (joined.Contains("GEIL") || joined.StartsWith("GL")) return "GeIL";
                if (joined.Contains("SAMSUNG") || joined.StartsWith("M378") || joined.StartsWith("M") ) return "Samsung";
                if (joined.Contains("HYNIX") || joined.Contains("SKHYNIX") || joined.StartsWith("HMA") || joined.StartsWith("HMT")) return "SK hynix";
                if (joined.Contains("MICRON") || joined.StartsWith("MT")) return "Micron";
            }
            catch { }
            return "Unknown";
        }
    }
}

