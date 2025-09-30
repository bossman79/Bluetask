using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Bluetask.Models;
using Bluetask.Services;
using System.Collections.Generic;
using System.Management;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Bluetask.ViewModels
{
    public enum PerformanceItemType
    {
        Cpu,
        Memory,
        Storage,
        Network,
        Gpu
    }

    public sealed partial class SidebarItem : ObservableObject
    {
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private PerformanceItemType _type;
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _iconGlyph = string.Empty;
        [ObservableProperty] private double _usage;
        [ObservableProperty] private string _subtext = string.Empty;
        [ObservableProperty] private string _valueText = string.Empty;
        [ObservableProperty] private object _data = null!;

        public Brush AccentBrush => GetAccentBrushForType(Type);
        public Brush SelectedBackgroundBrush => GetAccentBackgroundForType(Type);
        public Brush IconBackgroundBrush => GetIconBackgroundForType(Type);
        public Brush SelectedBorderBrush => GetSelectedBorderForType(Type);

        private static Brush GetAccentBrushForType(PerformanceItemType type)
        {
            var key = type switch
            {
                PerformanceItemType.Cpu => "App.CpuAccent",
                PerformanceItemType.Memory => "App.RamAccent",
                PerformanceItemType.Storage => "App.StorageAccent",
                PerformanceItemType.Network => "App.NetworkAccent",
                PerformanceItemType.Gpu => "App.GpuAccent",
                _ => "App.CpuAccent"
            };
            try
            {
                var res = Application.Current?.Resources[key];
                if (res is Brush b) return b;
            }
            catch { }
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
        }

        private static Brush GetAccentBackgroundForType(PerformanceItemType type)
        {
            try
            {
                if (GetAccentBrushForType(type) is SolidColorBrush sc)
                {
                    var c = sc.Color;
                    return new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B));
                }
            }
            catch { }
            return new SolidColorBrush(Color.FromArgb(0x38, 48, 144, 240));
        }

        private static Brush GetIconBackgroundForType(PerformanceItemType type)
        {
            try
            {
                if (GetAccentBrushForType(type) is SolidColorBrush sc)
                {
                    var c = sc.Color;
                    return new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
                }
            }
            catch { }
            return new SolidColorBrush(Color.FromArgb(0x33, 48, 144, 240));
        }

        private static Brush GetSelectedBorderForType(PerformanceItemType type)
        {
            try
            {
                if (GetAccentBrushForType(type) is SolidColorBrush sc)
                {
                    var c = sc.Color;
                    return new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
                }
            }
            catch { }
            return new SolidColorBrush(Color.FromArgb(0x66, 48, 144, 240));
        }

        public string TypeTitle => Type switch
        {
            PerformanceItemType.Cpu => "CPU",
            PerformanceItemType.Memory => "Memory",
            PerformanceItemType.Storage => "Storage",
            PerformanceItemType.Network => "Network",
            PerformanceItemType.Gpu => "GPU",
            _ => Type.ToString()
        };
    }

    public sealed partial class PerformanceViewModel : ObservableObject, IDisposable
    {
        // Reentrancy guards to prevent selection/restore loops
        private bool _handlingSelectionChange;
        private bool _restoringSelection;

        private readonly SystemMonitorService _systemMonitor;
        private readonly ProcessMonitorService _processMonitor;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);
        private System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();
        private Task? _updateTask;
        private readonly DispatcherQueue _dispatcher;
        private bool _disposed;
        private readonly List<double> _cpuHistory = new List<double>(60);
        private readonly List<double> _ramHistory = new List<double>(60);
        private readonly Dictionary<string, List<double>> _storageHistories = new();
        private readonly Dictionary<string, List<double>> _gpuHistories = new();
        private DateTime _systemStartTime;
        private List<Bluetask.Models.CpuCoreInfo> _cpuCores = new List<Bluetask.Models.CpuCoreInfo>();
        // Prevent flooding the UI thread with multiple enqueued updates
        private int _uiUpdateScheduled;

        [ObservableProperty] private CpuInfo _cpu = new CpuInfo();
        [ObservableProperty] private RamInfo _ram = new RamInfo();
        public ObservableCollection<GpuInfo> Gpus { get; } = new ObservableCollection<GpuInfo>();
        public ObservableCollection<StorageInfo> Drives { get; } = new ObservableCollection<StorageInfo>();
        public ObservableCollection<StorageInfo> DrivesLeftColumn { get; } = new ObservableCollection<StorageInfo>();
        public ObservableCollection<StorageInfo> DrivesRightColumn { get; } = new ObservableCollection<StorageInfo>();
        [ObservableProperty] private NetworkInfo _network = new NetworkInfo();
        [ObservableProperty] private string _networkConnectionType = string.Empty;
        [ObservableProperty] private string _networkLinkSpeed = string.Empty;
        [ObservableProperty] private string _networkIpv4Address = string.Empty;
        [ObservableProperty] private string _networkStatus = string.Empty;
        [ObservableProperty] private int _processCount;
        [ObservableProperty] private int _threadCount;
        [ObservableProperty] private int _handleCount;
        [ObservableProperty] private string _upTime = string.Empty;
        [ObservableProperty] private string _cpuSpeedGHz = string.Empty;
        [ObservableProperty] private string _memoryUsedAndTotal = string.Empty;
        [ObservableProperty] private string _networkSpeed = string.Empty;
        [ObservableProperty] private string _selectedSubtitle = string.Empty;
        [ObservableProperty] private List<double> _chartHistory = new List<double>();
        [ObservableProperty] private string _chartYAxisTop = "100%";
        [ObservableProperty] private string _chartYAxisMid = "50%";
        [ObservableProperty] private string _chartYAxisBottom = "0%";
        [ObservableProperty] private bool _isCpuPerCore = Services.SettingsService.CpuPerCoreView;
        public ReadOnlyCollection<Bluetask.Models.CpuCoreInfo> CpuCores => new ReadOnlyCollection<Bluetask.Models.CpuCoreInfo>(_cpuCores);
        public ObservableCollection<SidebarItem> SidebarItems { get; } = new ObservableCollection<SidebarItem>();
        [ObservableProperty] private SidebarItem? _selectedSidebarItem;

        [ObservableProperty] private bool _hasThreeOrMoreDrives;
        [ObservableProperty] private bool _hasThreeOrMoreGpus;
        [ObservableProperty] private bool _hasFourDrives;
        [ObservableProperty] private bool _hasFourGpus;
        [ObservableProperty] private bool _hasFiveOrMoreDrives;
        [ObservableProperty] private bool _hasOddFiveOrMoreDrives;
        [ObservableProperty] private bool _showDefaultDriveList;
        [ObservableProperty] private StorageInfo? _topDrive;
        [ObservableProperty] private bool _isCpuSelected;
        [ObservableProperty] private bool _isMemorySelected;
        [ObservableProperty] private bool _isStorageSelected;
        [ObservableProperty] private bool _isNetworkSelected;
        [ObservableProperty] private bool _isGpuSelected;
        [ObservableProperty] private StorageInfo? _selectedDrive;
        [ObservableProperty] private GpuInfo? _selectedGpu;
        [ObservableProperty] private string _selectedDriveKey = string.Empty;
        [ObservableProperty] private string _selectedGpuKey = string.Empty;

        public PerformanceViewModel()
        {
            _systemMonitor = SystemMonitorService.Shared;
            _processMonitor = new ProcessMonitorService();
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var bootTime = ManagementDateTimeConverter.ToDateTime(mo["LastBootUpTime"].ToString());
                    _systemStartTime = bootTime; break;
                }
            }
            catch { _systemStartTime = DateTime.Now.AddDays(-1); }

            void RefreshLayoutFlags()
            {
                HasThreeOrMoreDrives = Drives.Count >= 3;
                HasThreeOrMoreGpus = Gpus.Count >= 3;
                HasFourDrives = Drives.Count == 4;
                HasFourGpus = Gpus.Count == 4;
                HasFiveOrMoreDrives = Drives.Count >= 5;
                HasOddFiveOrMoreDrives = Drives.Count >= 5 && (Drives.Count % 2 == 1);
                ShowDefaultDriveList = !(HasFourDrives || HasOddFiveOrMoreDrives);
                TopDrive = Drives.Count > 0 ? Drives[0] : null;
                UpdateDriveColumns();
            }
            Drives.CollectionChanged += (_, __) => RefreshLayoutFlags();
            Gpus.CollectionChanged += (_, __) => RefreshLayoutFlags();
            RefreshLayoutFlags();

            _updateTask = Task.Run(UpdateLoopAsync, _cts.Token);
            InitializeSidebarItems();

            try { Services.SettingsService.CpuPerCoreViewChanged += (b) => { try { IsCpuPerCore = b; } catch { } }; } catch { }
        }

        private void UpdateDriveColumns()
        {
            try
            {
                DrivesLeftColumn.Clear();
                DrivesRightColumn.Clear();
                if (!HasOddFiveOrMoreDrives) return;
                if (Drives.Count <= 1) return;
                int remaining = Drives.Count - 1;
                int leftCount = (remaining + 1) / 2;
                for (int i = 1; i < Drives.Count; i++)
                {
                    if ((i - 1) < leftCount) DrivesLeftColumn.Add(Drives[i]); else DrivesRightColumn.Add(Drives[i]);
                }
            }
            catch { }
        }

        partial void OnSelectedSidebarItemChanged(SidebarItem? value)
        {
            if (_handlingSelectionChange) return;
            try
            {
                _handlingSelectionChange = true;
                UpdateSelectionBooleans();
                UpdateSelectedSubtitle();
                ChartHistory = GetSelectedHistory();
                UpdateYAxisLabels();
            }
            catch { }
            finally { _handlingSelectionChange = false; }
        }

        partial void OnChartHistoryChanged(List<double> value)
        {
            try { UpdateYAxisLabels(); } catch { }
        }

        private void UpdateYAxisLabels()
        {
            try
            {
                var type = SelectedSidebarItem?.Type;
                if (type == PerformanceItemType.Network)
                {
                    var hist = ChartHistory ?? new List<double>();
                    double vmax = 0.0;
                    for (int i = 0; i < hist.Count; i++)
                    {
                        var v = hist[i];
                        if (!double.IsNaN(v) && !double.IsInfinity(v) && v > vmax) vmax = v;
                    }
                    double top = vmax <= 0 ? 1.0 : (vmax * 1.1);
                    ChartYAxisTop = FormatAxisRate(top);
                    ChartYAxisMid = FormatAxisRate(top * 0.5);
                    ChartYAxisBottom = "0";
                }
                else
                {
                    ChartYAxisTop = "100%";
                    ChartYAxisMid = "50%";
                    ChartYAxisBottom = "0%";
                }
            }
            catch { }
        }

        private static string FormatAxisRate(double mbps)
        {
            try
            {
                double bps = mbps * 1_000_000.0;
                if (bps >= 1_000_000_000.0) return $"{(bps / 1_000_000_000.0):F1} Gbps";
                if (bps >= 1_000_000.0) return $"{(bps / 1_000_000.0):F1} Mbps";
                if (bps >= 1_000.0) return $"{(bps / 1_000.0):F0} Kbps";
                return $"{bps:F0} bps";
            }
            catch { return "0"; }
        }

        private async Task UpdateLoopAsync()
        {
            var updateCounter = 0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _systemMonitor.Update();
                    var cpu = _systemMonitor.GetCpuInfo();
                    var ram = _systemMonitor.GetRamInfo();
                    var shouldUpdateExpensive = (updateCounter % 3) == 0;
                    var gpus = _systemMonitor.GetGpuInfo().ToArray();
                    var drives = shouldUpdateExpensive ? _systemMonitor.GetDriveInfo().ToArray() : Array.Empty<StorageInfo>();
                    var net = _systemMonitor.GetNetworkInfo();

                    var (procCount, threadCount, handleCount) = shouldUpdateExpensive ? GetSystemCounts() : (ProcessCount, ThreadCount, HandleCount);
                    var cpuSpeed = shouldUpdateExpensive ? ExtractCpuSpeed(cpu) : CpuSpeedGHz;
                    var uptime = FormatUptime(DateTime.Now - _systemStartTime);

                    if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                    {
                        if (Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) == 0)
                        {
                            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                            {
                                try
                                {
                                    ApplyUpdate(cpu, ram, gpus, drives, net, shouldUpdateExpensive, procCount, threadCount, handleCount, cpuSpeed, uptime);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"PerformanceViewModel UI update error: {ex.Message}");
                                }
                                finally { Interlocked.Exchange(ref _uiUpdateScheduled, 0); }
                            });
                        }
                    }
                    else
                    {
                        ApplyUpdate(cpu, ram, gpus, drives, net, shouldUpdateExpensive, procCount, threadCount, handleCount, cpuSpeed, uptime);
                    }

                    updateCounter++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PerformanceViewModel update error: {ex.Message}");
                    if (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        try { await Task.Delay(Math.Min(5000, _interval.Milliseconds * 2), _cts.Token).ConfigureAwait(false); } catch { }
                    }
                }

                try { await Task.Delay(_interval, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        private void ApplyUpdate(CpuInfo cpu, RamInfo ram, GpuInfo[] gpus, StorageInfo[] drives, NetworkInfo net,
                                  bool expensive, int procCount, int threadCount, int handleCount, string cpuSpeed, string uptime)
        {
            Cpu = cpu;
            Ram = ram;
            UpdateCollection(Gpus, gpus);
            if (expensive)
            {
                UpdateCollection(Drives, drives);
                ProcessCount = procCount;
                ThreadCount = threadCount;
                HandleCount = handleCount;
                CpuSpeedGHz = cpuSpeed;
            }
            // Force property change notifications for nested bindings
            try
            {
                var target = Network ?? new NetworkInfo();
                target.UploadSpeed = net?.UploadSpeed ?? string.Empty;
                target.DownloadSpeed = net?.DownloadSpeed ?? string.Empty;
                target.TopProcesses = net?.TopProcesses ?? new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>();
                target.UploadHistory = net?.UploadHistory ?? new List<double>();
                target.DownloadHistory = net?.DownloadHistory ?? new List<double>();
                target.ConnectionType = net?.ConnectionType ?? string.Empty;
                target.LinkSpeed = net?.LinkSpeed ?? string.Empty;
                target.Ipv4Address = net?.Ipv4Address ?? string.Empty;
                target.Status = net?.Status ?? string.Empty;
                if (!object.ReferenceEquals(Network, target)) Network = target;
            }
            catch { }
            NetworkConnectionType = net?.ConnectionType ?? string.Empty;
            NetworkLinkSpeed = net?.LinkSpeed ?? string.Empty;
            NetworkIpv4Address = net?.Ipv4Address ?? string.Empty;
            NetworkStatus = net?.Status ?? string.Empty;
            UpTime = uptime;
            _cpuHistory.Add(cpu.Usage);
            if (_cpuHistory.Count > 60) _cpuHistory.RemoveAt(0);
            Cpu.UsageHistory = new List<double>(_cpuHistory);

            // Update per-core collection efficiently (aggregate logical to PHYSICAL cores)
            try
            {
                var logicalUsages = cpu?.PerCoreUsages ?? new List<double>();
                int physical = Math.Max(1, cpu?.PhysicalCoreCount > 0 ? cpu.PhysicalCoreCount : 0);
                int logical = Math.Max(1, cpu?.LogicalProcessorCount > 0 ? cpu.LogicalProcessorCount : logicalUsages.Count);
                var list = AggregateLogicalToPhysical(logicalUsages, physical, logical);
                int targetCount = Math.Max(physical > 0 ? physical : list.Count, list.Count);
                if (targetCount != _cpuCores.Count)
                {
                    _cpuCores = new List<Bluetask.Models.CpuCoreInfo>(targetCount);
                    for (int i = 0; i < targetCount; i++)
                    {
                        double v = i < list.Count ? list[i] : 0.0;
                        _cpuCores.Add(new Bluetask.Models.CpuCoreInfo { Index = i, Usage = v });
                    }
                }
                else
                {
                    for (int i = 0; i < _cpuCores.Count; i++)
                    {
                        double v = i < list.Count ? list[i] : 0.0;
                        _cpuCores[i].Usage = v;
                    }
                }
                OnPropertyChanged(nameof(CpuCores));
            }
            catch { }
            MemoryUsedAndTotal = ram.UsedAndTotal;
            NetworkSpeed = FormatNetworkSpeed(net);
            UpdateRamHistory(ram.Usage);

            foreach (var drive in drives)
            {
                var dkey = drive?.Name ?? string.Empty;
                if (!_storageHistories.TryGetValue(dkey, out var hist)) { hist = new List<double>(60); _storageHistories[dkey] = hist; }
                hist.Add(drive?.Usage ?? 0.0);
                if (hist.Count > 60) hist.RemoveAt(0);
            }
            foreach (var g in gpus)
            {
                var gkey = g?.Name ?? string.Empty;
                if (!_gpuHistories.TryGetValue(gkey, out var hist)) { hist = new List<double>(60); _gpuHistories[gkey] = hist; }
                hist.Add(g?.Usage ?? 0.0);
                if (hist.Count > 60) hist.RemoveAt(0);
            }

            for (int i = 0; i < Drives.Count; i++)
            {
                var d = Drives[i];
                if (_storageHistories.TryGetValue(d?.Name ?? string.Empty, out var hist)) d.UsageHistory = new List<double>(hist);
                try
                {
                    int dIdx = i;
                    var parts = (d.Name ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var letter = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                    d.CardTitle = $"Disk {dIdx}{(string.IsNullOrWhiteSpace(letter) ? string.Empty : $" ({letter})")}";
                    d.IsLast = (i == Drives.Count - 1);
                }
                catch { }
            }
            for (int i = 0; i < Gpus.Count; i++)
            {
                var g = Gpus[i];
                if (_gpuHistories.TryGetValue(g?.Name ?? string.Empty, out var hist)) g.UsageHistory = new List<double>(hist);
                try { g.CardTitle = $"GPU {i}"; g.IsLast = (i == Gpus.Count - 1); } catch { }
            }

            RestoreSelectedDrive();
            RestoreSelectedGpu();
            EnsureAggregateSidebarItems();

            var cpuItem = SidebarItems.FirstOrDefault(i => i.Type == PerformanceItemType.Cpu);
            if (cpuItem != null)
            {
                cpuItem.Usage = Cpu.Usage;
                cpuItem.Subtext = string.Format("{0:F2} GHz", Cpu.CurrentClockGhz);
                cpuItem.ValueText = string.Format("{0:F0}%", Cpu.Usage);
                cpuItem.Name = "CPU";
            }
            var memItem = SidebarItems.FirstOrDefault(i => i.Type == PerformanceItemType.Memory);
            if (memItem != null)
            {
                memItem.Usage = Ram.Usage;
                memItem.Subtext = MemoryUsedAndTotal;
                memItem.ValueText = GetValueTextForItem(Ram);
            }
            var netItem = SidebarItems.FirstOrDefault(i => i.Type == PerformanceItemType.Network);
            if (netItem != null)
            {
                netItem.Subtext = "Connected";
                netItem.ValueText = GetValueTextForItem(Network);
            }

            UpdateSelectionBooleans();
            UpdateSelectedSubtitle();
            ChartHistory = GetSelectedHistory();
        }

        partial void OnNetworkChanged(NetworkInfo value)
        {
            try
            {
                NetworkConnectionType = value?.ConnectionType ?? string.Empty;
                NetworkLinkSpeed = value?.LinkSpeed ?? string.Empty;
                NetworkIpv4Address = value?.Ipv4Address ?? string.Empty;
                NetworkStatus = value?.Status ?? string.Empty;
            }
            catch { }
        }

        private static List<double> AggregateLogicalToPhysical(List<double> logicalUsages, int physicalCores, int logicalProcessors)
        {
            try
            {
                var src = logicalUsages ?? new List<double>();
                if (physicalCores <= 0)
                {
                    // Unknown physical: return what we have
                    return new List<double>(src);
                }
                if (src.Count == physicalCores)
                {
                    return new List<double>(src);
                }
                // Average logical threads per physical core
                int threadsPerCore = 1;
                if (logicalProcessors > 0 && logicalProcessors % physicalCores == 0)
                {
                    threadsPerCore = Math.Max(1, logicalProcessors / physicalCores);
                }
                var result = new List<double>(physicalCores);
                for (int c = 0; c < physicalCores; c++)
                {
                    double sum = 0.0; int count = 0;
                    for (int t = 0; t < threadsPerCore; t++)
                    {
                        int idx = c * threadsPerCore + t;
                        if (idx < src.Count)
                        {
                            double v = src[idx];
                            if (!double.IsNaN(v) && !double.IsInfinity(v)) { sum += v; count++; }
                        }
                    }
                    double avg = count > 0 ? (sum / count) : 0.0;
                    result.Add(Math.Clamp(avg, 0.0, 100.0));
                }
                return result;
            }
            catch { return logicalUsages ?? new List<double>(); }
        }

        partial void OnIsCpuPerCoreChanged(bool value)
        {
            try { Services.SettingsService.CpuPerCoreView = value; } catch { }
        }

        private static void UpdateCollection<T>(ObservableCollection<T> target, T[] source)
        {
            try
            {
                // Remove items not present anymore
                var desired = new List<T>(source);
                for (int i = target.Count - 1; i >= 0; i--)
                {
                    if (!desired.Contains(target[i]))
                    {
                        target.RemoveAt(i);
                    }
                }
                // Insert/move to match desired order
                for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
                {
                    var item = desired[targetIndex];
                    int currentIndex = target.IndexOf(item);
                    if (currentIndex == -1)
                    {
                        target.Insert(targetIndex, item);
                    }
                    else if (currentIndex != targetIndex)
                    {
                        target.Move(currentIndex, targetIndex);
                    }
                }
            }
            catch { }
        }

        private (int processes, int threads, int handles) GetSystemCounts()
        {
            try
            {
                var processes = Process.GetProcesses();
                int processCount = processes?.Length ?? 0;
                int threadCount = 0; int handleCount = 0;
                var category = new PerformanceCounterCategory("Process");
                System.Diagnostics.InstanceDataCollectionCollection data;
                try { data = category.ReadCategory(); } catch { return (processCount, 0, 0); }
                if (data == null) return (processCount, 0, 0);
                var idColl = data.Contains("ID Process") ? data["ID Process"] : null;
                var threadColl = data.Contains("Thread Count") ? data["Thread Count"] : null;
                var handleColl = data.Contains("Handle Count") ? data["Handle Count"] : null;
                if (idColl == null) return (processCount, 0, 0);
                foreach (System.Collections.DictionaryEntry entry in idColl)
                {
                    try
                    {
                        var instanceName = (string)entry.Key;
                        var raw = (System.Diagnostics.InstanceData)entry.Value;
                        int pid = (int)raw.RawValue;
                        if (pid <= 0) continue;
                        if (threadColl != null && threadColl.Contains(instanceName))
                        {
                            try { threadCount += (int)threadColl[instanceName].RawValue; } catch { }
                        }
                        if (handleColl != null && handleColl.Contains(instanceName))
                        {
                            try { handleCount += (int)handleColl[instanceName].RawValue; } catch { }
                        }
                    }
                    catch { }
                }
                return (processCount, threadCount, handleCount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSystemCounts error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        private string ExtractCpuSpeed(CpuInfo cpu)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                using var collection = searcher.Get();
                foreach (ManagementObject mo in collection)
                {
                    try
                    {
                        var maxSpeedObj = mo["MaxClockSpeed"];
                        if (maxSpeedObj != null)
                        {
                            var maxSpeed = Convert.ToUInt32(maxSpeedObj);
                            return $"{maxSpeed / 1000.0:F2} GHz";
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"ExtractCpuSpeed inner error: {ex.Message}"); }
                    finally { mo?.Dispose(); }
                }
                return "0.00 GHz";
            }
            catch (Exception ex) { Debug.WriteLine($"ExtractCpuSpeed error: {ex.Message}"); return "0.00 GHz"; }
        }

        private string FormatUptime(TimeSpan uptime)
        {
            try
            {
                if (uptime.TotalDays >= 1)
                    return $"{uptime.Days}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
                else
                    return $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
            }
            catch { return "00:00:00"; }
        }

        private string FormatNetworkSpeed(NetworkInfo network)
        {
            try
            {
                if (network == null || string.IsNullOrWhiteSpace(network.UploadSpeed) || string.IsNullOrWhiteSpace(network.DownloadSpeed))
                    return "0.0 Kbps";
                var uploadStr = network.UploadSpeed?.Replace(" Mbps", "")?.Replace(" Kbps", "")?.Replace(" Gbps", "") ?? "0";
                var downloadStr = network.DownloadSpeed?.Replace(" Mbps", "")?.Replace(" Kbps", "")?.Replace(" Gbps", "") ?? "0";
                if (double.TryParse(uploadStr, out var upload) && double.TryParse(downloadStr, out var download))
                {
                    var total = upload + download;
                    if (network.UploadSpeed?.Contains("Gbps") == true || network.DownloadSpeed?.Contains("Gbps") == true)
                        return $"{total:F1} Gbps";
                    else if (network.UploadSpeed?.Contains("Mbps") == true || network.DownloadSpeed?.Contains("Mbps") == true)
                        return $"{total:F1} Mbps";
                    else
                        return $"{total:F1} Kbps";
                }
                return "0.0 Kbps";
            }
            catch (Exception ex) { Debug.WriteLine($"FormatNetworkSpeed error: {ex.Message}"); return "0.0 Kbps"; }
        }

        private void InitializeSidebarItems()
        {
            SidebarItems.Add(new SidebarItem { Type = PerformanceItemType.Cpu, Key = "CPU", Name = "CPU", IconGlyph = "\xE950", Data = Cpu, Subtext = CpuSpeedGHz, ValueText = GetValueTextForItem(Cpu) });
            SidebarItems.Add(new SidebarItem { Type = PerformanceItemType.Memory, Key = "Memory", Name = "Memory", IconGlyph = "\xE957", Data = Ram, Subtext = MemoryUsedAndTotal, ValueText = GetValueTextForItem(Ram) });
            SidebarItems.Add(new SidebarItem { Type = PerformanceItemType.Network, Key = "Network", Name = "Network", IconGlyph = "\xE701", Data = Network, Subtext = "Connected", ValueText = GetValueTextForItem(Network) });
            SidebarItems.Add(new SidebarItem { Type = PerformanceItemType.Storage, Key = "Disks", Name = "Disks", IconGlyph = "\xE8B7", Data = new object(), Subtext = "0 drives attached", ValueText = "0%" });
            SidebarItems.Add(new SidebarItem { Type = PerformanceItemType.Gpu, Key = "GPUs", Name = "GPUs", IconGlyph = "\xEED8", Data = new object(), Subtext = "0 GPUs available", ValueText = "0%" });
            if (SidebarItems.Count > 0) SelectedSidebarItem = SidebarItems[0];
        }

        private void EnsureAggregateSidebarItems()
        {
            var disksItem = SidebarItems.FirstOrDefault(i => i.Type == PerformanceItemType.Storage);
            if (disksItem != null)
            {
                disksItem.Subtext = $"{Drives.Count} drives attached";
                disksItem.ValueText = string.Empty;
                disksItem.Data = Drives;
                disksItem.Name = "Disks";
            }
            var gpusItem = SidebarItems.FirstOrDefault(i => i.Type == PerformanceItemType.Gpu && i.Key == "GPUs");
            if (gpusItem != null)
            {
                gpusItem.Subtext = $"{Gpus.Count} GPUs available";
                gpusItem.ValueText = Gpus.Count > 0 ? $"{Gpus.Average(g => g.Usage):F0}%" : "0%";
                gpusItem.Data = Gpus;
                gpusItem.Name = "GPUs";
            }
        }

        private string GetValueTextForItem(object data) => data switch
        {
            CpuInfo c => $"{c.Usage:F0}%",
            RamInfo r => $"{r.Usage:F0}%",
            StorageInfo s => $"{s.Usage:F0}%",
            GpuInfo g => $"{g.Usage:F0}%",
            NetworkInfo => NetworkSpeed,
            _ => string.Empty
        };

        private void UpdateSelectedSubtitle()
        {
            if (SelectedSidebarItem == null) { SelectedSubtitle = string.Empty; return; }
            if (SelectedSidebarItem.Type == PerformanceItemType.Cpu && Cpu != null) SelectedSubtitle = Cpu.Name;
            else if (SelectedSidebarItem.Type == PerformanceItemType.Memory) SelectedSubtitle = string.IsNullOrWhiteSpace(Ram.ModuleConfiguration) ? Ram.Type : $"{Ram.Type} {Ram.ModuleConfiguration}";
            else if (SelectedSidebarItem.Type == PerformanceItemType.Storage) { var d = SelectedDrive ?? (Drives.Count > 0 ? Drives[0] : null); SelectedSubtitle = d != null ? d.Name : string.Empty; }
            else if (SelectedSidebarItem.Type == PerformanceItemType.Network) SelectedSubtitle = "Connected";
            else if (SelectedSidebarItem.Type == PerformanceItemType.Gpu) { var g = SelectedGpu ?? (Gpus.OrderByDescending(x => x.Usage).FirstOrDefault()); SelectedSubtitle = g != null ? $"{g.CardTitle}: {g.Name}" : string.Empty; }
        }

        private List<double> GetSelectedHistory()
        {
            if (SelectedSidebarItem?.Type == PerformanceItemType.Cpu) return new List<double>(_cpuHistory);
            if (SelectedSidebarItem?.Type == PerformanceItemType.Memory) return new List<double>(_ramHistory);
            if (SelectedSidebarItem?.Type == PerformanceItemType.Storage)
            {
                var d = SelectedDrive ?? (Drives.Count > 0 ? Drives[0] : null);
                return d?.UsageHistory != null ? new List<double>(d.UsageHistory) : new List<double>();
            }
            if (SelectedSidebarItem?.Type == PerformanceItemType.Gpu)
            {
                var g = SelectedGpu ?? (Gpus.OrderByDescending(x => x.Usage).FirstOrDefault());
                return g?.UsageHistory != null ? new List<double>(g.UsageHistory) : new List<double>();
            }
            if (SelectedSidebarItem?.Type == PerformanceItemType.Network)
            {
                try
                {
                    var up = Network?.UploadHistory ?? new List<double>();
                    var down = Network?.DownloadHistory ?? new List<double>();
                    int n = Math.Max(up.Count, down.Count);
                    var result = new List<double>(n);
                    for (int i = 0; i < n; i++)
                    {
                        double u = i < up.Count ? up[i] : 0.0;
                        double d2 = i < down.Count ? down[i] : 0.0;
                        result.Add((u + d2) / 2.0);
                    }
                    return result;
                }
                catch { }
            }
            return new List<double>();
        }

        partial void OnSelectedDriveChanged(StorageInfo? value)
        {
            try
            {
                SelectedDriveKey = value?.Name ?? string.Empty;
                UpdateDriveSelectionFlags();
                if (_restoringSelection) return;
                if (IsStorageSelected) { ChartHistory = GetSelectedHistory(); UpdateSelectedSubtitle(); }
            }
            catch { }
        }

        partial void OnSelectedGpuChanged(GpuInfo? value)
        {
            try
            {
                SelectedGpuKey = value?.Name ?? string.Empty;
                UpdateGpuSelectionFlags();
                if (_restoringSelection) return;
                if (IsGpuSelected)
                {
                    ChartHistory = GetSelectedHistory();
                    UpdateSelectedSubtitle();
                }
            }
            catch { }
        }

        private void UpdateRamHistory(double usage)
        {
            _ramHistory.Add(usage);
            if (_ramHistory.Count > 60) _ramHistory.RemoveAt(0);
            Ram.UsageHistory = new List<double>(_ramHistory);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _updateTask?.Wait(250); } catch (OperationCanceledException) { } catch { }
            try { _cts.Dispose(); } catch { }
            _cts = new System.Threading.CancellationTokenSource();
        }

        private void UpdateSelectionBooleans()
        {
            var t = SelectedSidebarItem?.Type;
            IsCpuSelected = t == PerformanceItemType.Cpu;
            IsMemorySelected = t == PerformanceItemType.Memory;
            IsStorageSelected = t == PerformanceItemType.Storage;
            IsNetworkSelected = t == PerformanceItemType.Network;
            IsGpuSelected = t == PerformanceItemType.Gpu;
        }

        public string GetDiskCardTitle(object dataContext) => (dataContext as StorageInfo)?.CardTitle ?? string.Empty;
        public string GetGpuCardTitle(object dataContext) => (dataContext as GpuInfo)?.CardTitle ?? string.Empty;

        public bool IsSelectedDrive(object dataContext)
        {
            try
            {
                var drive = dataContext as StorageInfo;
                if (drive == null) return false;
                return string.Equals(SelectedDrive?.Name ?? string.Empty, drive.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void RestoreSelectedDrive()
        {
            try
            {
                if (Drives == null || Drives.Count == 0) { SelectedDrive = null; return; }
                var match = Drives.FirstOrDefault(d => !string.IsNullOrWhiteSpace(SelectedDriveKey) && string.Equals(d?.Name, SelectedDriveKey, StringComparison.OrdinalIgnoreCase));
                if (match == null) match = Drives[0];
                if (!object.ReferenceEquals(SelectedDrive, match)) { _restoringSelection = true; try { SelectedDrive = match; } finally { _restoringSelection = false; } }
                UpdateDriveSelectionFlags();
            }
            catch { }
        }

        private void UpdateDriveSelectionFlags()
        {
            try
            {
                for (int i = 0; i < Drives.Count; i++)
                {
                    var d = Drives[i];
                    d.IsSelected = string.Equals(d?.Name ?? string.Empty, SelectedDrive?.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        private void RestoreSelectedGpu()
        {
            try
            {
                if (Gpus == null || Gpus.Count == 0) { SelectedGpu = null; return; }
				GpuInfo? match = null;
				if (!string.IsNullOrWhiteSpace(SelectedGpuKey))
				{
					match = Gpus.FirstOrDefault(g => string.Equals(g?.Name, SelectedGpuKey, StringComparison.OrdinalIgnoreCase));
				}
				if (match == null)
				{
					match = ChoosePreferredGpu(Gpus) ?? (Gpus.Count > 0 ? Gpus[0] : null);
				}
                if (!object.ReferenceEquals(SelectedGpu, match)) { _restoringSelection = true; try { SelectedGpu = match; } finally { _restoringSelection = false; } }
                UpdateGpuSelectionFlags();
            }
            catch { }
        }

		private GpuInfo? ChoosePreferredGpu(System.Collections.Generic.IList<GpuInfo> gpus)
		{
			try
			{
				if (gpus == null || gpus.Count == 0) return null;
				// 1) Prefer discrete GPUs by vendor/product keywords
				var discrete = gpus.Where(g => IsDiscreteGpuName(g?.Name ?? string.Empty)).ToList();
				if (discrete.Count > 0)
				{
					// Prefer the one with the largest total VRAM when available
					var best = discrete
						.OrderByDescending(g => ExtractTotalVideoMemoryGb(g))
						.ThenByDescending(g => g?.Usage ?? 0.0)
						.FirstOrDefault();
					return best ?? discrete[0];
				}

				// 2) Fallback: choose the GPU with higher current usage; else first
				var byUsage = gpus.OrderByDescending(g => g?.Usage ?? 0.0).FirstOrDefault();
				return byUsage ?? gpus[0];
			}
			catch { return gpus?.Count > 0 ? gpus[0] : null; }
		}

		private static bool IsDiscreteGpuName(string name)
		{
			try
			{
				var n = (name ?? string.Empty).ToUpperInvariant();
				if (string.IsNullOrWhiteSpace(n)) return false;
				if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("RTX") || n.Contains("GTX")) return true;
				if (n.Contains("RADEON") || n.Contains("AMD")) return true;
				if (n.Contains("MICROSOFT BASIC")) return false;
				if (n.Contains("INTEL")) return false;
				return false;
			}
			catch { return false; }
		}

		private static double ExtractTotalVideoMemoryGb(GpuInfo g)
		{
			try
			{
				var s = g?.Memory ?? string.Empty; // expected "X GB / Y GB" or "X.X GB / Y.Y GB"
				var parts = s.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 2)
				{
					var totalPart = parts[1].Trim();
					if (totalPart.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
					{
						var num = totalPart.Substring(0, totalPart.Length - 2).Trim();
						if (double.TryParse(num, out var gb)) return gb;
					}
					if (totalPart.EndsWith("TB", StringComparison.OrdinalIgnoreCase))
					{
						var num = totalPart.Substring(0, totalPart.Length - 2).Trim();
						if (double.TryParse(num, out var tb)) return tb * 1000.0;
					}
				}
				// Fallback to dedicated memory field
				var d = g?.DedicatedMemory ?? string.Empty; // e.g., "12 GB"
				if (d.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
				{
					var num = d.Substring(0, d.Length - 2).Trim();
					if (double.TryParse(num, out var gb2)) return gb2;
				}
			}
			catch { }
			return 0.0;
		}

        private void UpdateGpuSelectionFlags()
        {
            try
            {
                for (int i = 0; i < Gpus.Count; i++)
                {
                    var g = Gpus[i];
                    g.IsSelected = string.Equals(g?.Name ?? string.Empty, SelectedGpu?.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }
    }
}


