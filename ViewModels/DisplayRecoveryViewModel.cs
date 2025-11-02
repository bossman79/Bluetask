using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bluetask.Models;
using Bluetask.Services;

namespace Bluetask.ViewModels
{
    public partial class DisplayRecoveryViewModel : ObservableObject
    {
        private readonly DisplayRecoveryService _service = DisplayRecoveryService.Shared;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private DateTime _lastStatusUpdate = DateTime.MinValue;
        private DateTime _lastUnresponsiveUpdate = DateTime.MinValue;
        private bool _isUpdating = false;
        
        [ObservableProperty]
        private int _lastScanDisplayIssues = 0;
        
        [ObservableProperty]
        private int _lastScanBsodRisks = 0;

        public ObservableCollection<RecoveryOperation> RecentOperations { get; } = new();
        public ObservableCollection<UnresponsiveApp> UnresponsiveApps { get; } = new();
        public ObservableCollection<ScanFinding> ScanFindings { get; } = new();

        [ObservableProperty]
        private DisplaySystemStatus _systemStatus = new();

        [ObservableProperty]
        private bool _isMonitoring = true;

        [ObservableProperty]
        private string _statusMessage = "Monitoring system health...";

        public bool HasNoOperations => RecentOperations.Count == 0;
        public bool HasUnresponsiveApps => UnresponsiveApps.Count > 0;
        public bool HasNoUnresponsiveApps => UnresponsiveApps.Count == 0;
        public bool HasScanFindings => ScanFindings.Count > 0;
        public bool HasNoScanFindings => ScanFindings.Count == 0;
        
        public string DisplayIssuesColor => LastScanDisplayIssues == 0 ? "#10B981" : "#EF4444";
        public string FrozenAppsColor => UnresponsiveApps.Count == 0 ? "#10B981" : "#F59E0B";
        public string BsodRisksColor => LastScanBsodRisks == 0 ? "#10B981" : "#EF4444";

        public IAsyncRelayCommand RestoreDisplayConfigCommand { get; }
        public IAsyncRelayCommand ForceDisplayRedetectionCommand { get; }
        public IAsyncRelayCommand ClearVramCacheCommand { get; }
        public IAsyncRelayCommand ExportDiagnosticReportCommand { get; }
        public IAsyncRelayCommand RestartExplorerCommand { get; }
        public IAsyncRelayCommand RestartDwmCommand { get; }
        public IAsyncRelayCommand ScanForIssuesCommand { get; }

        public DisplayRecoveryViewModel()
        {
            try 
            { 
                _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); 
            } 
            catch { }

            // Subscribe to service events
            _service.RecoveryOperationCompleted += OnRecoveryOperationCompleted;
            _service.SystemStatusChanged += OnSystemStatusChanged;
            _service.UnresponsiveAppsChanged += OnUnresponsiveAppsChanged;

            // Initialize commands
            RestoreDisplayConfigCommand = new AsyncRelayCommand(RestoreDisplayConfigAsync);
            ForceDisplayRedetectionCommand = new AsyncRelayCommand(ForceDisplayRedetectionAsync);
            ClearVramCacheCommand = new AsyncRelayCommand(ClearVramCacheAsync);
            ExportDiagnosticReportCommand = new AsyncRelayCommand(ExportDiagnosticReportAsync);
            RestartExplorerCommand = new AsyncRelayCommand(RestartExplorerAsync);
            RestartDwmCommand = new AsyncRelayCommand(RestartDwmAsync);
            ScanForIssuesCommand = new AsyncRelayCommand(ScanForIssuesAsync);

            // Load initial data
            LoadInitialData();
            
            // Subscribe to operations collection changes
            RecentOperations.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasNoOperations));
            
            // Subscribe to unresponsive apps collection changes
            UnresponsiveApps.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasUnresponsiveApps));
                OnPropertyChanged(nameof(HasNoUnresponsiveApps));
            };
        }

        private void LoadInitialData()
        {
            Task.Run(() =>
            {
                try
                {
                    // Get current status
                    var status = _service.GetCurrentSystemStatus();
                    UpdateStatus(status);

                    // Load recent operations
                    var operations = _service.GetCompletedOperations();
                    
                    if (_dispatcher != null)
                    {
                        _dispatcher.TryEnqueue(() =>
                        {
                            RecentOperations.Clear();
                            foreach (var op in operations)
                            {
                                RecentOperations.Insert(0, op);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DisplayRecovery] Error loading initial data: {ex.Message}");
                }
            });
        }

        private void OnSystemStatusChanged(object? sender, DisplaySystemStatus status)
        {
            // Throttle status updates to max once per second to reduce UI overhead
            var now = DateTime.Now;
            if ((now - _lastStatusUpdate).TotalMilliseconds < 1000 || _isUpdating)
                return;
            
            _lastStatusUpdate = now;
            UpdateStatus(status);
        }

        private void UpdateStatus(DisplaySystemStatus status)
        {
            if (_isUpdating) return;
            
            _isUpdating = true;
            
            try
            {
                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        // Only update if values have changed
                        if (status.GpuDriverStatus != SystemStatus.GpuDriverStatus ||
                            status.DwmCompositorRunning != SystemStatus.DwmCompositorRunning ||
                            Math.Abs(status.VramUsedGB - SystemStatus.VramUsedGB) > 0.1 ||
                            Math.Abs(status.GpuLoad - SystemStatus.GpuLoad) > 5.0)
                        {
                            SystemStatus = status;
                            UpdateStatusMessage(status);
                        }
                        _isUpdating = false;
                    });
                }
                else
                {
                    SystemStatus = status;
                    UpdateStatusMessage(status);
                    _isUpdating = false;
                }
            }
            catch
            {
                _isUpdating = false;
            }
        }

        private void UpdateStatusMessage(DisplaySystemStatus status)
        {
            string message = "System healthy - All display components operational";

            if (status.GpuDriverStatus != "Operational")
            {
                message = "⚠ GPU driver issues detected - Recovery in progress";
            }
            else if (!status.DwmCompositorRunning)
            {
                message = "⚠ Desktop Window Manager stopped - Attempting restart";
            }
            else if (!status.DxgiRuntimeHealthy)
            {
                message = "⚠ DXGI runtime degraded - Monitoring closely";
            }
            else if (status.VramTotalGB > 0 && (status.VramUsedGB / status.VramTotalGB) > 0.90)
            {
                message = "⚠ VRAM usage high - Memory pressure detected";
            }
            else if (status.GpuTemperature > 85)
            {
                message = $"⚠ GPU temperature elevated ({status.GpuTemperature}°C) - Monitoring thermal state";
            }

            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(() => StatusMessage = message);
            }
            else
            {
                StatusMessage = message;
            }
        }

        private void OnRecoveryOperationCompleted(object? sender, RecoveryOperation operation)
        {
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    // Add to top of list
                    RecentOperations.Insert(0, operation);

                    // Keep only last 20
                    while (RecentOperations.Count > 20)
                    {
                        RecentOperations.RemoveAt(RecentOperations.Count - 1);
                    }
                });
            }
        }

        private async Task RestoreDisplayConfigAsync()
        {
            try
            {
                StatusMessage = "Restoring display configuration...";
                bool success = await _service.RestoreDisplayConfigurationAsync();
                
                if (success)
                {
                    StatusMessage = "Display configuration restored successfully";
                    
                    // Show success for 3 seconds, then revert
                    await Task.Delay(3000);
                    UpdateStatusMessage(SystemStatus);
                }
                else
                {
                    StatusMessage = "Failed to restore display configuration";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task ForceDisplayRedetectionAsync()
        {
            try
            {
                StatusMessage = "Forcing display redetection...";
                bool success = await _service.ForceDisplayRedetectionAsync();
                
                if (success)
                {
                    StatusMessage = "Display redetection completed";
                    await Task.Delay(3000);
                    UpdateStatusMessage(SystemStatus);
                }
                else
                {
                    StatusMessage = "Failed to redetect displays";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task ClearVramCacheAsync()
        {
            try
            {
                StatusMessage = "Clearing VRAM cache...";
                bool success = await _service.ClearVramCacheAsync();
                
                if (success)
                {
                    StatusMessage = "VRAM cache cleared successfully";
                    await Task.Delay(3000);
                    UpdateStatusMessage(SystemStatus);
                }
                else
                {
                    StatusMessage = "Failed to clear VRAM cache";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task ExportDiagnosticReportAsync()
        {
            try
            {
                StatusMessage = "Generating diagnostic report...";
                string report = await _service.ExportDiagnosticReportAsync();
                
                // Save to file
                string filename = $"DisplayDiagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    filename
                );
                
                await System.IO.File.WriteAllTextAsync(path, report);
                
                StatusMessage = $"Report saved to Desktop: {filename}";
                await Task.Delay(5000);
                UpdateStatusMessage(SystemStatus);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting report: {ex.Message}";
            }
        }

        private async Task RestartExplorerAsync()
        {
            try
            {
                StatusMessage = "Restarting Windows Explorer...";
                bool success = await _service.RestartExplorerManualAsync();
                
                if (success)
                {
                    StatusMessage = "Windows Explorer restarted successfully. Wallpaper and taskbar should be restored.";
                    await Task.Delay(3000);
                    UpdateStatusMessage(SystemStatus);
                }
                else
                {
                    StatusMessage = "Failed to restart Explorer";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task RestartDwmAsync()
        {
            try
            {
                StatusMessage = "Restarting Desktop Window Manager...";
                bool success = await _service.RestartDwmManualAsync();
                
                if (success)
                {
                    StatusMessage = "Desktop Window Manager restarted. Visual effects restored.";
                    await Task.Delay(3000);
                    UpdateStatusMessage(SystemStatus);
                }
                else
                {
                    StatusMessage = "Failed to restart DWM";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        private async Task ScanForIssuesAsync()
        {
            try
            {
                StatusMessage = "Scanning for display issues, frozen apps, and BSOD risks...";
                var results = await _service.ForceScanForIssuesAsync();
                
                // Store scan results for display
                LastScanDisplayIssues = results.DisplayIssues;
                LastScanBsodRisks = results.BsodRisks;
                
                // Update findings list (batch the updates)
                if (_dispatcher != null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        ScanFindings.Clear();
                        foreach (var finding in results.Findings)
                        {
                            ScanFindings.Add(finding);
                        }
                        
                        // Batch notify UI changes
                        OnPropertyChanged(nameof(DisplayIssuesColor));
                        OnPropertyChanged(nameof(FrozenAppsColor));
                        OnPropertyChanged(nameof(BsodRisksColor));
                        OnPropertyChanged(nameof(HasScanFindings));
                        OnPropertyChanged(nameof(HasNoScanFindings));
                    });
                }
                else
                {
                    ScanFindings.Clear();
                    foreach (var finding in results.Findings)
                    {
                        ScanFindings.Add(finding);
                    }
                    
                    OnPropertyChanged(nameof(DisplayIssuesColor));
                    OnPropertyChanged(nameof(FrozenAppsColor));
                    OnPropertyChanged(nameof(BsodRisksColor));
                    OnPropertyChanged(nameof(HasScanFindings));
                    OnPropertyChanged(nameof(HasNoScanFindings));
                }
                
                await Task.Delay(2000);
                
                int frozenApps = UnresponsiveApps.Count;
                int totalIssues = results.DisplayIssues + frozenApps + results.BsodRisks;
                
                if (totalIssues == 0)
                {
                    StatusMessage = "✅ Scan complete - No issues detected. All systems healthy.";
                }
                else
                {
                    var issues = new List<string>();
                    
                    if (results.DisplayIssues > 0)
                        issues.Add($"{results.DisplayIssues} display issue(s)");
                    
                    if (frozenApps > 0)
                        issues.Add($"{frozenApps} frozen app(s)");
                    
                    if (results.BsodRisks > 0)
                        issues.Add($"🔴 {results.BsodRisks} BSOD risk(s)");
                    
                    StatusMessage = "⚠️ Scan complete - Found: " + string.Join(", ", issues);
                }
                
                await Task.Delay(4000);
                UpdateStatusMessage(SystemStatus);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Scan error: {ex.Message}";
            }
        }

        private void OnUnresponsiveAppsChanged(object? sender, EventArgs e)
        {
            // Throttle unresponsive app updates to max once per 2 seconds
            var now = DateTime.Now;
            if ((now - _lastUnresponsiveUpdate).TotalMilliseconds < 2000)
                return;
            
            _lastUnresponsiveUpdate = now;
            
            if (_dispatcher != null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    var currentApps = _service.GetUnresponsiveApps();
                    
                    // Only update if the list actually changed
                    if (currentApps.Count != UnresponsiveApps.Count || 
                        !currentApps.SequenceEqual(UnresponsiveApps))
                    {
                        UnresponsiveApps.Clear();
                        foreach (var app in currentApps)
                        {
                            UnresponsiveApps.Add(app);
                        }
                        
                        OnPropertyChanged(nameof(HasUnresponsiveApps));
                        OnPropertyChanged(nameof(HasNoUnresponsiveApps));
                        OnPropertyChanged(nameof(FrozenAppsColor));
                    }
                });
            }
        }

        public void KillApp(int processId)
        {
            try
            {
                StatusMessage = "Terminating unresponsive application...";
                bool success = _service.KillUnresponsiveApp(processId);
                
                if (success)
                {
                    StatusMessage = "Application terminated successfully";
                }
                else
                {
                    StatusMessage = "Failed to terminate application";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }
    }
}

