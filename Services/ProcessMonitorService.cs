using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Management;

namespace Bluetask.Services
{
    public sealed class ProcessMonitorService
    {
        private readonly ConcurrentDictionary<int, TimeSpan> _lastTotalProcessorTime = new();
        private DateTime _lastSampleUtc = DateTime.UtcNow;
        private readonly int _processorCount = Environment.ProcessorCount;

        // GPU Engine performance counters cache for efficient per-process GPU sampling
        private readonly object _gpuCountersLock = new();
        private Dictionary<string, PerformanceCounter> _gpuCountersByInstance = new();
        private Dictionary<string, int> _gpuPidByInstance = new();
        private DateTime _lastGpuRefreshUtc = DateTime.MinValue;
        private Dictionary<int, double> _lastGpuResultsByPid = new();
        private DateTime _lastGpuSampleUtc = DateTime.MinValue;
        private Dictionary<int, Dictionary<string, double>> _lastGpuByAdapterPerPid = new();

        public IReadOnlyList<ProcessInfo> SampleProcesses()
        {
            var nowUtc = DateTime.UtcNow;
            var elapsed = nowUtc - _lastSampleUtc;
            if (elapsed <= TimeSpan.Zero)
                elapsed = TimeSpan.FromMilliseconds(1);

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { processes = Array.Empty<Process>(); }
            var list = new List<ProcessInfo>(processes.Length);

            // Batch-read memory counters once per tick to avoid per-process PerformanceCounter allocations
            var workingSetByPid = new Dictionary<int, long>();
            var privateWsByPid = new Dictionary<int, long>();
            try
            {
                var procCategory = new PerformanceCounterCategory("Process");
                var data = procCategory.ReadCategory();
                if (data != null && data.Contains("ID Process"))
                {
                    var idColl = data["ID Process"];
                    System.Diagnostics.InstanceDataCollection? wsColl = data.Contains("Working Set") ? data["Working Set"] : null;
                    System.Diagnostics.InstanceDataCollection? pwsColl = data.Contains("Working Set - Private") ? data["Working Set - Private"] : null;
                    foreach (System.Collections.DictionaryEntry entry in idColl)
                    {
                        try
                        {
                            var instanceName = (string)entry.Key;
                            var raw = (System.Diagnostics.InstanceData)entry.Value;
                            int pid = (int)raw.RawValue;
                            if (pid <= 0) continue;
                            if (wsColl != null && wsColl.Contains(instanceName))
                            {
                                try { workingSetByPid[pid] = (long)wsColl[instanceName].RawValue; } catch { }
                            }
                            if (pwsColl != null && pwsColl.Contains(instanceName))
                            {
                                try { privateWsByPid[pid] = (long)pwsColl[instanceName].RawValue; } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Build ParentProcessId map (cache refreshed every few seconds to reduce WMI load)
            var parentMap = ParentProcessMapCache.Instance.GetParentMap();

            foreach (var p in processes)
            {
                try
                {
                    var totalTime = p.TotalProcessorTime;
                    if (!_lastTotalProcessorTime.TryGetValue(p.Id, out var prevTotal))
                    {
                        prevTotal = totalTime;
                    }
                    var delta = totalTime - prevTotal;
                    _lastTotalProcessorTime[p.Id] = totalTime;

                    double cpu = 0.0;
                    if (elapsed.TotalMilliseconds > 0)
                    {
                        cpu = (delta.TotalMilliseconds / (elapsed.TotalMilliseconds * _processorCount)) * 100.0;
                        if (cpu < 0) cpu = 0;
                    }

                    long workingSet = 0;
                    if (!workingSetByPid.TryGetValue(p.Id, out workingSet))
                    {
                        try { workingSet = p.WorkingSet64; } catch { workingSet = 0; }
                    }
                    long privateBytes = 0;
                    privateWsByPid.TryGetValue(p.Id, out privateBytes);

                    if (!parentMap.TryGetValue(p.Id, out var parentId))
                    {
                        parentId = 0;
                    }

                    list.Add(new ProcessInfo
                    {
                        ProcessId = p.Id,
                        Name = p?.ProcessName ?? string.Empty,
                        CpuPercent = cpu,
                        MemoryBytes = Bluetask.Services.SettingsService.MemoryMetric == Bluetask.Services.MemoryMetric.WorkingSet ? workingSet : privateBytes,
                        ParentId = parentId
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access denied - skip this process
                }
                catch (InvalidOperationException)
                {
                    // Process exited - skip
                }
                catch
                {
                    // Other transient errors - skip
                }
            }

            // Per-process GPU usage (best-effort via GPU Engine perf counters)
            try
            {
                // Sample only counters for current processes to avoid touching thousands of instances
                var pidSet = new HashSet<int>();
                try { foreach (var pr in processes) pidSet.Add(pr.Id); } catch { }
                var gpuByPid = SampleGpuUsageByPid(pidSet);
                foreach (var pi in list)
                {
                    if (gpuByPid.TryGetValue(pi.ProcessId, out var gpu))
                    {
                        pi.GpuPercent = gpu;
                    }
                    if (_lastGpuByAdapterPerPid.TryGetValue(pi.ProcessId, out var perAdapter))
                    {
                        pi.GpuByAdapterPercent = new System.Collections.Generic.Dictionary<string, double>(perAdapter, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // Ignore GPU sampling failures; leave GpuPercent as 0
            }

            _lastSampleUtc = nowUtc;
            // Return top 200 by CPU usage; names normalized to include .exe for consistency with header filters/search
            foreach (var pi in list)
            {
                if (!string.IsNullOrEmpty(pi.Name) && !pi.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    pi.Name = pi.Name + ".exe";
                }
            }

            return list
                .OrderByDescending(pi => pi.CpuPercent)
                .Take(200)
                .ToArray();
        }

        public sealed class ProcessInfo
        {
            public int ProcessId { get; set; }
            public string Name { get; set; } = string.Empty;
            public double CpuPercent { get; set; }
            public long MemoryBytes { get; set; }
            public double GpuPercent { get; set; }
            public int ParentId { get; set; }
            public System.Collections.Generic.Dictionary<string, double> GpuByAdapterPercent { get; set; } = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ParentProcessMapCache
        {
            private static readonly Lazy<ParentProcessMapCache> _lazy = new Lazy<ParentProcessMapCache>(() => new ParentProcessMapCache());
            public static ParentProcessMapCache Instance => _lazy.Value;

            private Dictionary<int, int> _lastMap = new Dictionary<int, int>();
            private DateTime _lastRefreshUtc = DateTime.MinValue;
            private readonly TimeSpan _ttl = TimeSpan.FromSeconds(5);
            private readonly object _lock = new object();

            public Dictionary<int, int> GetParentMap()
            {
                var now = DateTime.UtcNow;
                lock (_lock)
                {
                    if ((now - _lastRefreshUtc) < _ttl && _lastMap.Count > 0)
                    {
                        return _lastMap;
                    }
                    var map = new Dictionary<int, int>();
                    try
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                            map[pid] = ppid;
                        }
                        _lastMap = map;
                        _lastRefreshUtc = now;
                    }
                    catch { }
                    return _lastMap;
                }
            }
        }

        // Returns per-process GPU utilization percentage, summed across engine instances per PID.
        // Uses cached PerformanceCounter instances and refreshes the instance list periodically.
        private Dictionary<int, double> SampleGpuUsageByPid(HashSet<int> targetPids)
        {
            var result = new Dictionary<int, double>();
            var perAdapter = new Dictionary<int, Dictionary<string, double>>();
            try
            {
                EnsureGpuCounters();

                // Throttle GPU reads to at most once per 1s and reuse last values when called more frequently
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastGpuSampleUtc) < TimeSpan.FromSeconds(1) && _lastGpuResultsByPid.Count > 0)
                {
                    // Return a filtered copy of cached results
                    foreach (var pid in targetPids)
                    {
                        if (_lastGpuResultsByPid.TryGetValue(pid, out var v)) result[pid] = v;
                    }
                    return result;
                }

                Dictionary<string, PerformanceCounter> snapshotCounters;
                Dictionary<string, int> snapshotPidByInstance;
                lock (_gpuCountersLock)
                {
                    snapshotCounters = _gpuCountersByInstance;
                    snapshotPidByInstance = _gpuPidByInstance;
                }

                foreach (var kvp in snapshotCounters)
                {
                    try
                    {
                        var instanceKey = kvp.Key ?? string.Empty;
                        if (string.IsNullOrEmpty(instanceKey)) continue;
                        if (!snapshotPidByInstance.TryGetValue(instanceKey, out var pid)) continue;
                        if (targetPids.Count > 0 && !targetPids.Contains(pid)) continue;

                        var value = kvp.Value.NextValue();
                        if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                        if (!result.ContainsKey(pid)) result[pid] = 0.0;
                        result[pid] += value;

                        // Parse adapter key from instance name (e.g., "luid_0x..._0x...")
                        string instanceName = kvp.Key ?? string.Empty;
                        string adapterKey = "unknown";
                        try
                        {
                            int luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                            if (luidIdx >= 0)
                            {
                                int end = instanceName.IndexOf("_eng", luidIdx, StringComparison.OrdinalIgnoreCase);
                                if (end < 0) end = instanceName.Length;
                                adapterKey = instanceName.Substring(luidIdx, end - luidIdx);
                            }
                        }
                        catch { adapterKey = "unknown"; }

                        if (!perAdapter.TryGetValue(pid, out var map))
                        {
                            map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            perAdapter[pid] = map;
                        }
                        if (!map.ContainsKey(adapterKey)) map[adapterKey] = 0.0;
                        map[adapterKey] += value;
                    }
                    catch
                    {
                        // Ignore individual counter read failures
                    }
                }

                // Clamp to [0,100]
                foreach (var pid in result.Keys.ToArray())
                {
                    var v = result[pid];
                    if (v < 0.0) v = 0.0;
                    if (v > 100.0) v = 100.0;
                    result[pid] = v;

                    if (perAdapter.TryGetValue(pid, out var map))
                    {
                        double sum = 0.0;
                        foreach (var kv in map) sum += kv.Value;
                        if (sum > 0.0)
                        {
                            double scale = v / sum;
                            var keys = map.Keys.ToArray();
                            foreach (var k in keys)
                            {
                                map[k] = map[k] * scale;
                            }
                        }
                    }
                }

                // Cache results
                _lastGpuResultsByPid = result.ToDictionary(k => k.Key, v => v.Value);
                _lastGpuByAdapterPerPid = perAdapter.ToDictionary(k => k.Key, v => new Dictionary<string, double>(v.Value, StringComparer.OrdinalIgnoreCase));
                _lastGpuSampleUtc = nowUtc;
            }
            catch
            {
                // If the category is unavailable or access is denied, return empty and continue gracefully.
            }

            return result;
        }

        private void EnsureGpuCounters()
        {
            lock (_gpuCountersLock)
            {
                var nowUtc = DateTime.UtcNow;
                var needRefresh = _gpuCountersByInstance.Count == 0 || (nowUtc - _lastGpuRefreshUtc) > TimeSpan.FromSeconds(3);
                if (!needRefresh) return;

                _lastGpuRefreshUtc = nowUtc;

                string categoryName = "GPU Engine";
                string counterName = "Utilization Percentage";
                string[] instances;
                try
                {
                    var category = new PerformanceCounterCategory(categoryName);
                    instances = category.GetInstanceNames();
                }
                catch
                {
                    // Category not available
                    _gpuCountersByInstance = new Dictionary<string, PerformanceCounter>();
                    _gpuPidByInstance = new Dictionary<string, int>();
                    return;
                }

                var newCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
                var newPidByInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var instanceName in instances)
                {
                    // We only care about instances that belong to a process: they include "pid_####"
                    if (instanceName == null) continue;
                    var idx = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;

                    // Parse PID after "pid_"
                    int pid = 0;
                    try
                    {
                        // Instance format examples:
                        // "pid_1234_luid_0x00000000_0x00000000_eng_0_engtype_3D"
                        // Split by '_' and take the part after "pid"
                        var parts = instanceName.Split('_');
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            if (string.Equals(parts[i], "pid", StringComparison.OrdinalIgnoreCase))
                            {
                                if (int.TryParse(parts[i + 1], out var parsed)) pid = parsed;
                                break;
                            }
                        }
                    }
                    catch { }

                    if (pid <= 0) continue;

                    try
                    {
                        if (!_gpuCountersByInstance.TryGetValue(instanceName, out var existing))
                        {
                            var pc = new PerformanceCounter(categoryName, counterName, instanceName, true);
                            // Prime the counter
                            _ = pc.NextValue();
                            newCounters[instanceName] = pc;
                        }
                        else
                        {
                            newCounters[instanceName] = existing;
                        }
                        newPidByInstance[instanceName] = pid;
                    }
                    catch
                    {
                        // Skip counters we cannot create
                    }
                }

                // Dispose counters that are no longer present
                foreach (var old in _gpuCountersByInstance)
                {
                    if (!newCounters.ContainsKey(old.Key))
                    {
                        try { old.Value.Dispose(); } catch { }
                    }
                }

                _gpuCountersByInstance = newCounters;
                _gpuPidByInstance = newPidByInstance;
            }
        }
    }
}



