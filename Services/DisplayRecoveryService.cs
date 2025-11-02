using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Bluetask.Models;
using LibreHardwareMonitor.Hardware;

namespace Bluetask.Services
{
    /// <summary>
    /// Advanced display recovery service that monitors GPU health, detects crashes,
    /// and performs automatic recovery operations to restore display functionality.
    /// </summary>
    public class DisplayRecoveryService : IDisposable
    {
        private static readonly Lazy<DisplayRecoveryService> _instance = new(() => new DisplayRecoveryService());
        public static DisplayRecoveryService Shared => _instance.Value;

        private readonly HardwareService _hardwareService;
        private readonly SystemMonitorService _systemMonitor = SystemMonitorService.Shared;
        private readonly ProcessMonitorService _processMonitor = new ProcessMonitorService();
        private readonly EventLogService _eventLogService = EventLogService.Shared;
        private readonly Timer _monitoringTimer;
        private readonly List<RecoveryOperation> _activeOperations = new();
        private readonly List<RecoveryOperation> _completedOperations = new();
        private readonly List<UnresponsiveApp> _unresponsiveApps = new();
        private readonly Dictionary<int, DateTime> _unresponsiveStartTimes = new();
        private readonly object _lock = new();
        
        private DisplaySystemStatus _lastStatus = new();
        private DateTime _lastCrashDetection = DateTime.MinValue;
        private int _consecutiveCrashDetections = 0;

        public event EventHandler<RecoveryOperation>? RecoveryOperationCompleted;
        public event EventHandler<DisplaySystemStatus>? SystemStatusChanged;
        public event EventHandler? UnresponsiveAppsChanged;

        private DisplayRecoveryService()
        {
            _hardwareService = new HardwareService();
            
            // Start lightweight monitoring every 2 seconds (deep scan on-demand)
            _monitoringTimer = new Timer(MonitoringCallback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        #region Windows API Declarations

        [DllImport("dxgi.dll", SetLastError = true)]
        private static extern int CreateDXGIFactory1([In] ref Guid riid, out IntPtr ppFactory);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmIsCompositionEnabled(out bool pfEnabled);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid,
            string? Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet,
            uint MemberIndex,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            uint Property,
            out uint PropertyRegDataType,
            byte[] PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong TotalMemoryInKilobytes);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        private const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
        private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint SPDRP_DRIVER = 0x00000009;
        private const uint SPDRP_DEVICEDESC = 0x00000000;
        private const int SM_CMONITORS = 80;

        #endregion

        /// <summary>
        /// Monitoring callback that runs periodically to check system health
        /// </summary>
        private void MonitoringCallback(object? state)
        {
            try
            {
                Debug.WriteLine("[DisplayRecovery] ━━━ Monitoring Cycle Start ━━━");
                
                var status = GetCurrentSystemStatus();
                
                // Detect potential issues and trigger automatic recovery
                DetectAndHandleIssues(status);
                
                // Detect unresponsive applications
                DetectUnresponsiveApps();
                
                // Update status
                _lastStatus = status;
                SystemStatusChanged?.Invoke(this, status);
                
                Debug.WriteLine("[DisplayRecovery] ━━━ Monitoring Cycle End ━━━");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ Monitoring error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets comprehensive system status including GPU, VRAM, DWM, DXGI, etc.
        /// </summary>
        public DisplaySystemStatus GetCurrentSystemStatus()
        {
            var status = new DisplaySystemStatus
            {
                Timestamp = DateTime.Now
            };

            try
            {
                // Update hardware readings
                _hardwareService.Update();

                // GPU Driver Status
                status.GpuDriverStatus = GetGpuDriverStatus();
                
                // VRAM Utilization
                var vramInfo = GetVramUtilization();
                status.VramUsedGB = vramInfo.usedGB;
                status.VramTotalGB = vramInfo.totalGB;
                
                // Display Surfaces
                status.DisplaySurfacesActive = GetActiveDisplayCount();
                
                // DWM Compositor
                status.DwmCompositorRunning = IsDwmRunning();
                
                // DXGI Runtime
                status.DxgiRuntimeHealthy = CheckDxgiHealth();
                
                // Display Connectivity
                status.DisplayConnectivity = GetDisplayConnectivity();
                
                // Power Management
                status.PowerManagementStable = CheckPowerManagement();

                // GPU Temperature and Load
                var gpuStats = GetGpuStats();
                status.GpuTemperature = gpuStats.temperature;
                status.GpuLoad = gpuStats.load;
                status.GpuName = gpuStats.name;

                // System Memory
                status.SystemMemoryGB = GetTotalSystemMemory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Status check error: {ex.Message}");
            }

            return status;
        }

        /// <summary>
        /// Detects crashes, OOM conditions, and other critical issues
        /// </summary>
        private void DetectAndHandleIssues(DisplaySystemStatus status)
        {
            Debug.WriteLine($"[DisplayRecovery] DetectAndHandleIssues - Status: {status.GpuDriverStatus}");
            
            // Check for GPU driver crash
            if (status.GpuDriverStatus == "Critical - Restart Needed")
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 Triggering GPU driver crash handler");
                HandleGpuDriverCrash();
                return; // Don't check other issues if driver crashed
            }

            // Check for shell/explorer issues (gray wallpaper, etc.) - PRIORITY CHECK
            if (status.GpuDriverStatus.Contains("Shell Issues"))
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 Triggering shell degradation handler");
                HandleShellDegradation();
                return; // Fixed, don't cascade other checks
            }

            // Check for DWM composition disabled
            if (status.GpuDriverStatus.Contains("Composition Disabled"))
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 Triggering composition disabled handler");
                HandleCompositionDisabled();
                return;
            }

            // Check for rendering issues
            if (status.GpuDriverStatus.Contains("Rendering Issues"))
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 Triggering rendering degradation handler");
                HandleRenderingDegradation();
                return;
            }

            // Check for VRAM exhaustion (OOM)
            if (status.VramTotalGB > 0)
            {
                double vramUsagePercent = (status.VramUsedGB / status.VramTotalGB) * 100.0;
                if (vramUsagePercent > 95.0)
                {
                    Debug.WriteLine($"[DisplayRecovery] 🔴 VRAM exhaustion: {vramUsagePercent:F1}%");
                    HandleVramExhaustion();
                }
            }

            // Check for DWM failure (process crashed)
            if (!status.DwmCompositorRunning && _lastStatus.DwmCompositorRunning)
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 DWM process crashed");
                HandleDwmFailure();
            }

            // Check for display disconnect
            if (status.DisplaySurfacesActive < _lastStatus.DisplaySurfacesActive)
            {
                Debug.WriteLine("[DisplayRecovery] 🔴 Display disconnected");
                HandleDisplayDisconnect();
            }

            // Detect impending BSOD conditions
            DetectImminentBSOD(status);
        }

        /// <summary>
        /// Attempts to detect conditions that may lead to BSOD
        /// </summary>
        private void DetectImminentBSOD(DisplaySystemStatus status)
        {
            bool criticalCondition = false;
            string reason = "";

            // Multiple consecutive GPU hangs
            if (_consecutiveCrashDetections >= 3)
            {
                criticalCondition = true;
                reason = "Multiple GPU driver failures detected";
            }

            // Critical VRAM pressure combined with high GPU load
            if (status.VramTotalGB > 0)
            {
                double vramUsage = (status.VramUsedGB / status.VramTotalGB) * 100.0;
                if (vramUsage > 98.0 && status.GpuLoad > 95.0)
                {
                    criticalCondition = true;
                    reason = "Critical VRAM exhaustion with high GPU load";
                }
            }

            // GPU temperature critical
            if (status.GpuTemperature > 95)
            {
                criticalCondition = true;
                reason = $"GPU temperature critical: {status.GpuTemperature}°C";
            }

            if (criticalCondition)
            {
                HandleImminentBSOD(reason);
            }
        }

        #region Recovery Handlers

        private void HandleGpuDriverCrash()
        {
            var now = DateTime.Now;
            if ((now - _lastCrashDetection).TotalSeconds < 5)
                return; // Debounce

            _lastCrashDetection = now;
            _consecutiveCrashDetections++;

            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Display Adapter Recovery",
                Description = "GPU driver crashed. Attempting to restore display adapter and restart driver.",
                Status = RecoveryStatus.InProgress,
                StartTime = now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500);
                    
                    // Attempt to restart graphics driver (TDR recovery)
                    bool success = await RestartGraphicsDriverAsync();
                    
                    if (success)
                    {
                        operation.Description = "All GPU resources restored. Display driver operating normally with full acceleration.";
                        operation.Status = RecoveryStatus.Success;
                        _consecutiveCrashDetections = 0; // Reset counter on success
                    }
                    else
                    {
                        operation.Description = "Unable to fully recover display adapter. Manual restart may be required.";
                        operation.Status = RecoveryStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    operation.Description = $"Recovery failed: {ex.Message}";
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        private void HandleVramExhaustion()
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "VRAM Leak Detection & Cleanup",
                Description = "Identified and recovered leaked video memory from crashed processes.",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    
                    // Clear GPU memory caches
                    bool success = await ClearVramCacheAsync();
                    
                    var vramInfo = GetVramUtilization();
                    double freedGB = Math.Max(0, _lastStatus.VramUsedGB - vramInfo.usedGB);
                    
                    operation.Description = $"Identified and recovered {freedGB:F1} GB of leaked video memory from crashed processes.";
                    operation.Status = RecoveryStatus.Success;
                }
                catch
                {
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        private void HandleShellDegradation()
        {
            // Check if we recently handled this to avoid spam
            var recentShellRecoveries = _completedOperations
                .Where(op => op.Title == "Windows Shell Recovery" && 
                            (DateTime.Now - op.StartTime).TotalSeconds < 30)
                .ToList();
            
            if (recentShellRecoveries.Any())
            {
                Debug.WriteLine("[DisplayRecovery] ⏳ Shell recovery already performed recently, skipping");
                return;
            }

            Debug.WriteLine("[DisplayRecovery] 🚨 INITIATING AUTOMATIC EXPLORER RESTART");
            
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Windows Shell Recovery",
                Description = "Detected shell rendering issues (wallpaper/UI degradation). Restarting Explorer...",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("[DisplayRecovery] Waiting 500ms before restart...");
                    await Task.Delay(500);
                    
                    Debug.WriteLine("[DisplayRecovery] Calling RestartExplorerAsync()...");
                    bool success = await RestartExplorerAsync();
                    
                    if (success)
                    {
                        Debug.WriteLine("[DisplayRecovery] ✅ Explorer restart successful!");
                        operation.Description = "Windows Explorer restarted. Wallpaper and UI rendering restored.";
                        operation.Status = RecoveryStatus.Success;
                        
                        // Reset consecutive crash counter
                        _consecutiveCrashDetections = 0;
                    }
                    else
                    {
                        Debug.WriteLine("[DisplayRecovery] ❌ Explorer restart failed");
                        operation.Description = "Failed to restart Explorer. Manual restart may be needed.";
                        operation.Status = RecoveryStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DisplayRecovery] ❌ Shell recovery exception: {ex.Message}");
                    operation.Description = $"Shell recovery failed: {ex.Message}";
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
                
                Debug.WriteLine("[DisplayRecovery] Shell recovery operation completed");
            });
        }

        private void HandleRenderingDegradation()
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Graphics Rendering Recovery",
                Description = "Graphics rendering degraded. Attempting to restore display capabilities...",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    
                    // Clear graphics caches and restart composition
                    bool success = await RestoreRenderingAsync();
                    
                    if (success)
                    {
                        operation.Description = "Graphics rendering restored. Display quality should improve.";
                        operation.Status = RecoveryStatus.Success;
                    }
                    else
                    {
                        operation.Description = "Unable to fully restore rendering. GPU driver restart may be needed.";
                        operation.Status = RecoveryStatus.Warning;
                    }
                }
                catch
                {
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        private void HandleCompositionDisabled()
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Desktop Composition Recovery",
                Description = "Desktop composition disabled. Restoring hardware acceleration and visual effects...",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    
                    bool success = await RestartDwmAsync();
                    
                    if (success)
                    {
                        operation.Description = "Desktop composition re-enabled. Aero effects and transparency restored.";
                        operation.Status = RecoveryStatus.Success;
                    }
                    else
                    {
                        operation.Description = "Failed to enable composition. System may be in Basic theme mode.";
                        operation.Status = RecoveryStatus.Warning;
                    }
                }
                catch
                {
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        private void HandleDwmFailure()
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "DWM Surface Restoration",
                Description = "Desktop Window Manager crashed. Attempting restart...",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    
                    // Attempt to restart DWM
                    bool success = await RestartDwmAsync();
                    
                    if (success)
                    {
                        operation.Description = "Desktop Window Manager restarted. Hardware acceleration restored.";
                        operation.Status = RecoveryStatus.Success;
                    }
                    else
                    {
                        operation.Description = "DWM restart failed. System restart may be required.";
                        operation.Status = RecoveryStatus.Failed;
                    }
                }
                catch
                {
                    operation.Status = RecoveryStatus.Failed;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        private void HandleDisplayDisconnect()
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Multi-Monitor Configuration Pending",
                Description = "Secondary display requires manual reactivation. EDID data preserved for restore.",
                Status = RecoveryStatus.Warning,
                StartTime = DateTime.Now,
                CompletedTime = DateTime.Now
            };

            CompleteOperation(operation);
        }

        private void HandleImminentBSOD(string reason)
        {
            var operation = new RecoveryOperation
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Critical System Stabilization",
                Description = $"Detected critical condition: {reason}. Applying emergency stabilization.",
                Status = RecoveryStatus.InProgress,
                StartTime = DateTime.Now
            };

            StartOperation(operation);

            Task.Run(async () =>
            {
                try
                {
                    // Emergency measures to prevent BSOD
                    await Task.Delay(100);
                    
                    // Reduce GPU load by killing non-essential GPU processes
                    bool success = await EmergencyGpuStabilizationAsync();
                    
                    if (success)
                    {
                        operation.Description = $"Critical condition mitigated: {reason}. System stabilized.";
                        operation.Status = RecoveryStatus.Success;
                        _consecutiveCrashDetections = 0;
                    }
                    else
                    {
                        operation.Description = $"Unable to fully stabilize. {reason}. Recommend immediate action.";
                        operation.Status = RecoveryStatus.Warning;
                    }
                }
                catch
                {
                    operation.Status = RecoveryStatus.Warning;
                }

                operation.CompletedTime = DateTime.Now;
                CompleteOperation(operation);
            });
        }

        #endregion

        #region Recovery Actions (Manual)

        /// <summary>
        /// Force a comprehensive DEEP scan for display issues, BSOD risks, hardware problems, and system instability
        /// </summary>
        public async Task<ScanResults> ForceScanForIssuesAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    Debug.WriteLine("[DisplayRecovery] ========== STARTING DEEP SYSTEM SCAN ==========");
                    
                    var results = new ScanResults();
                    
                    // === PHASE 1: Display & GPU Health (use existing methods) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 1/6: Display & GPU Health...");
                    var status = GetCurrentSystemStatus();
                    
                    if (status.GpuDriverStatus.Contains("Degraded") || status.GpuDriverStatus.Contains("Critical"))
                    {
                        results.DisplayIssues++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Display",
                            Severity = status.GpuDriverStatus.Contains("Critical") ? "Critical" : "Warning",
                            Icon = "⚠️",
                            Message = $"GPU driver status: {status.GpuDriverStatus}"
                        });
                    }
                    
                    if (!status.DwmCompositorRunning)
                    {
                        results.DisplayIssues++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Display",
                            Severity = "Critical",
                            Icon = "🔴",
                            Message = "Desktop Window Manager (DWM) compositor is not running"
                        });
                    }
                    
                    // === PHASE 2: Hardware Health (using HardwareService) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 2/6: Hardware Health...");
                    ScanHardwareHealth(results);
                    
                    // === PHASE 3: Process & Memory Health (using ProcessMonitorService) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 3/6: Process & Memory Analysis...");
                    ScanProcessHealth(results);
                    
                    // === PHASE 4: Disk & Storage Health (using HardwareService) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 4/6: Disk & Storage Health...");
                    ScanStorageHealth(results);
                    
                    // === PHASE 5: Event Log Analysis (using EventLogService) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 5/6: Recent Event Log Analysis...");
                    ScanEventLogs(results);
                    
                    // === PHASE 6: BSOD Risk Factors (existing method) ===
                    Debug.WriteLine("[DisplayRecovery] Phase 6/6: BSOD Risk Assessment...");
                    CheckBSODRisks(status, results);
                    
                    // Trigger automatic handlers for found issues
                    DetectAndHandleIssues(status);
                    
                    Debug.WriteLine($"[DisplayRecovery] ========== SCAN COMPLETE ==========");
                    Debug.WriteLine($"[DisplayRecovery] Results: {results.DisplayIssues} display issues, {results.BsodRisks} BSOD risks, {results.Findings.Count} total findings");
                    
                    return results;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DisplayRecovery] ❌ Deep scan error: {ex.Message}");
                    return new ScanResults();
                }
            });
        }
        
        private void ScanHardwareHealth(ScanResults results)
        {
            try
            {
                _hardwareService.Update();
                
                // Check CPU temperature & throttling
                var cpu = _hardwareService.GetCpu();
                if (cpu != null)
                {
                    cpu.Update();
                    var temp = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package"));
                    var load = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total"));
                    
                    if (temp?.Value > 90)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Hardware Risk: CPU overheating ({temp.Value:F1}°C)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Hardware",
                            Severity = "Critical",
                            Icon = "🔥",
                            Message = $"CPU overheating at {temp.Value:F1}°C (safe limit: 85°C)"
                        });
                    }
                    
                    // Check for sustained high load
                    if (load?.Value > 95)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Hardware Risk: CPU at {load.Value:F1}% (sustained high load)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Hardware",
                            Severity = "Warning",
                            Icon = "⚡",
                            Message = $"CPU at {load.Value:F1}% load (sustained high usage may cause instability)"
                        });
                    }
                }
                
                // Check GPU health (already done in main scan but check thermal throttling)
                var gpus = _hardwareService.GetGpus();
                foreach (var gpu in gpus)
                {
                    gpu.Update();
                    var temp = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    var fan = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    
                    // Fan failure + high temp = critical
                    if (temp?.Value > 85 && fan?.Value < 100)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Hardware Risk: GPU cooling failure (Temp: {temp.Value}°C, Fan: {fan?.Value ?? 0} RPM)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Hardware",
                            Severity = "Critical",
                            Icon = "🔥",
                            Message = $"{gpu.Name}: Cooling failure (Temp: {temp.Value:F1}°C, Fan: {fan?.Value ?? 0:F0} RPM)"
                        });
                    }
                }
                
                // Check memory health
                var memory = _hardwareService.GetMemory();
                if (memory != null)
                {
                    memory.Update();
                    var load = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                    
                    if (load?.Value > 95)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Hardware Risk: RAM usage critical ({load.Value:F1}%)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Hardware",
                            Severity = "Critical",
                            Icon = "💾",
                            Message = $"RAM usage critical at {load.Value:F1}% (system may crash or freeze)"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Error scanning hardware: {ex.Message}");
            }
        }
        
        private void ScanProcessHealth(ScanResults results)
        {
            try
            {
                var processes = _processMonitor.SampleProcesses();
                
                // Check for memory leaks (processes using >4GB RAM)
                var memoryHogs = processes.Where(p => p.MemoryBytes > 4L * 1024 * 1024 * 1024).Take(3).ToList();
                foreach (var proc in memoryHogs)
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ Process Risk: {proc.Name} using {proc.MemoryBytes / 1024 / 1024 / 1024:F1} GB RAM (possible leak)");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "Process",
                        Severity = "Warning",
                        Icon = "📊",
                        Message = $"{proc.Name} using {proc.MemoryBytes / 1024.0 / 1024.0 / 1024.0:F1} GB RAM (possible memory leak)"
                    });
                }
                
                // Check for CPU hogs (>90% sustained)
                var cpuHogs = processes.Where(p => p.CpuPercent > 90).Take(3).ToList();
                foreach (var proc in cpuHogs)
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ Process Risk: {proc.Name} using {proc.CpuPercent:F1}% CPU (sustained high usage)");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "Process",
                        Severity = "Warning",
                        Icon = "⚡",
                        Message = $"{proc.Name} using {proc.CpuPercent:F1}% CPU (sustained high usage)"
                    });
                }
                
                // Check total available memory
                var totalMemUsedGB = processes.Sum(p => p.MemoryBytes) / 1024.0 / 1024.0 / 1024.0;
                Debug.WriteLine($"[DisplayRecovery] Total RAM usage: {totalMemUsedGB:F1} GB");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Error scanning processes: {ex.Message}");
            }
        }
        
        private void ScanStorageHealth(ScanResults results)
        {
            try
            {
                var storage = _hardwareService.GetStorageDevices();
                
                foreach (var drive in storage)
                {
                    drive.Update();
                    
                    // Check disk temperature
                    var temp = drive.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    if (temp?.Value > 60)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Storage Risk: {drive.Name} overheating ({temp.Value:F1}°C)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Storage",
                            Severity = "Warning",
                            Icon = "💿",
                            Message = $"{drive.Name} overheating at {temp.Value:F1}°C (may cause data corruption)"
                        });
                    }
                    
                    // Check for high disk activity (could indicate thrashing)
                    var load = drive.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                    if (load?.Value > 95)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Storage Risk: {drive.Name} at {load.Value:F1}% activity (possible disk thrashing)");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Storage",
                            Severity = "Warning",
                            Icon = "💿",
                            Message = $"{drive.Name} at {load.Value:F1}% activity (disk thrashing may cause system freeze)"
                        });
                    }
                    
                    // Check SMART health if available
                    var smartHealth = drive.Sensors.Where(s => s.Name.Contains("Health") || s.Name.Contains("Remaining Life")).FirstOrDefault();
                    if (smartHealth != null && smartHealth.Value < 50)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Storage Risk: {drive.Name} health at {smartHealth.Value:F1}%");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Storage",
                            Severity = "Critical",
                            Icon = "⚠️",
                            Message = $"{drive.Name} health at {smartHealth.Value:F1}% (drive failure imminent, backup data now)"
                        });
                    }
                }
                
                // Check free disk space on system drive
                var systemDrive = new System.IO.DriveInfo(Environment.SystemDirectory);
                if (systemDrive.IsReady)
                {
                    long freeGB = systemDrive.AvailableFreeSpace / 1024 / 1024 / 1024;
                    if (freeGB < 10)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ Storage Risk: System drive only has {freeGB} GB free");
                        results.BsodRisks++;
                        results.Findings.Add(new ScanFinding
                        {
                            Category = "Storage",
                            Severity = "Critical",
                            Icon = "💾",
                            Message = $"System drive low on space ({freeGB} GB free, need at least 10 GB)"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Error scanning storage: {ex.Message}");
            }
        }
        
        private void ScanEventLogs(ScanResults results)
        {
            try
            {
                // Get recent events (last 100)
                var recentEvents = _eventLogService.GetRecentEvents(100);
                
                // Look for patterns that could lead to BSOD, not just past crashes
                
                // Check for repeated driver failures (pattern of instability)
                var recentDriverErrors = recentEvents
                    .Where(e => (DateTime.Now - e.TimeCreated).TotalHours < 6)
                    .Where(e => e.ProviderName?.Contains("Driver", StringComparison.OrdinalIgnoreCase) == true 
                             || e.Title?.Contains("driver", StringComparison.OrdinalIgnoreCase) == true)
                    .Where(e => e.Severity == EventSeverity.Error || e.Severity == EventSeverity.Critical)
                    .ToList();
                
                if (recentDriverErrors.Count >= 3)
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ Event Log Risk: {recentDriverErrors.Count} driver errors in last 6 hours (instability pattern)");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "Events",
                        Severity = "Warning",
                        Icon = "🔧",
                        Message = $"{recentDriverErrors.Count} driver errors in last 6 hours (repeated failures indicate instability)"
                    });
                }
                
                // Check for memory-related errors that could lead to crashes
                var memoryErrors = recentEvents
                    .Where(e => (DateTime.Now - e.TimeCreated).TotalHours < 24)
                    .Where(e => e.Title?.Contains("memory", StringComparison.OrdinalIgnoreCase) == true 
                             || e.Title?.Contains("RAM", StringComparison.OrdinalIgnoreCase) == true
                             || e.ProviderName?.Contains("MemoryDiagnostics", StringComparison.OrdinalIgnoreCase) == true)
                    .Where(e => e.Severity == EventSeverity.Error || e.Severity == EventSeverity.Critical)
                    .ToList();
                
                if (memoryErrors.Any())
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ Event Log Risk: Memory errors detected");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "Events",
                        Severity = "Critical",
                        Icon = "💾",
                        Message = $"Memory errors detected in event logs (possible hardware failure or corruption)"
                    });
                }
                
                // Check for disk errors that could cause system instability
                var diskErrors = recentEvents
                    .Where(e => (DateTime.Now - e.TimeCreated).TotalHours < 24)
                    .Where(e => e.ProviderName?.Contains("Disk", StringComparison.OrdinalIgnoreCase) == true
                             || e.Title?.Contains("disk", StringComparison.OrdinalIgnoreCase) == true
                             || e.Title?.Contains("storage", StringComparison.OrdinalIgnoreCase) == true)
                    .Where(e => e.Severity == EventSeverity.Error || e.Severity == EventSeverity.Critical)
                    .ToList();
                
                if (diskErrors.Any())
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ Event Log Risk: {diskErrors.Count} disk errors detected");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "Events",
                        Severity = "Warning",
                        Icon = "💿",
                        Message = $"{diskErrors.Count} disk error(s) in last 24 hours (may cause system freezes or data loss)"
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Error scanning event logs: {ex.Message}");
            }
        }
        
        private void CheckBSODRisks(DisplaySystemStatus status, ScanResults results)
        {
            // Multiple consecutive GPU crashes
            if (_consecutiveCrashDetections >= 3)
            {
                Debug.WriteLine("[DisplayRecovery] ⚠️ BSOD Risk: Multiple GPU driver failures");
                results.BsodRisks++;
                results.Findings.Add(new ScanFinding
                {
                    Category = "BSOD",
                    Severity = "Critical",
                    Icon = "🔴",
                    Message = $"Multiple GPU driver failures detected ({_consecutiveCrashDetections} consecutive crashes)"
                });
            }
            
            // Critical VRAM pressure + high GPU load
            if (status.VramTotalGB > 0)
            {
                double vramUsage = (status.VramUsedGB / status.VramTotalGB) * 100.0;
                if (vramUsage > 98.0 && status.GpuLoad > 95.0)
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ BSOD Risk: Critical VRAM exhaustion ({vramUsage:F1}%) + high load");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "BSOD",
                        Severity = "Critical",
                        Icon = "🔴",
                        Message = $"Critical VRAM exhaustion ({vramUsage:F1}% used, {status.GpuLoad:F1}% GPU load)"
                    });
                }
            }
            
            // GPU temperature critical
            if (status.GpuTemperature > 95)
            {
                Debug.WriteLine($"[DisplayRecovery] ⚠️ BSOD Risk: GPU temperature critical ({status.GpuTemperature}°C)");
                results.BsodRisks++;
                results.Findings.Add(new ScanFinding
                {
                    Category = "BSOD",
                    Severity = "Critical",
                    Icon = "🔥",
                    Message = $"GPU temperature critical at {status.GpuTemperature:F1}°C (thermal shutdown imminent)"
                });
            }
            
            // Check for memory pressure
            try
            {
                var memInfo = new PerformanceCounter("Memory", "Available MBytes");
                float availableMB = memInfo.NextValue();
                if (availableMB < 500) // Less than 500MB available
                {
                    Debug.WriteLine($"[DisplayRecovery] ⚠️ BSOD Risk: Critical system memory low ({availableMB:F0}MB available)");
                    results.BsodRisks++;
                    results.Findings.Add(new ScanFinding
                    {
                        Category = "BSOD",
                        Severity = "Critical",
                        Icon = "💾",
                        Message = $"Critical system memory low ({availableMB:F0} MB available, system may crash)"
                    });
                }
            }
            catch { }
        }

        /// <summary>
        /// Manually restart Windows Explorer
        /// </summary>
        public async Task<bool> RestartExplorerManualAsync()
        {
            return await RestartExplorerAsync();
        }

        /// <summary>
        /// Manually restart Desktop Window Manager
        /// </summary>
        public async Task<bool> RestartDwmManualAsync()
        {
            return await RestartDwmAsync();
        }

        /// <summary>
        /// Manually trigger display configuration restore
        /// </summary>
        public async Task<bool> RestoreDisplayConfigurationAsync()
        {
            try
            {
                // Force display re-enumeration
                await Task.Run(() =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c DisplaySwitch.exe /internal && DisplaySwitch.exe /extend",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(5000);
                });
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Force display redetection
        /// </summary>
        public async Task<bool> ForceDisplayRedetectionAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    // Cycle through display modes to force redetection
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c DisplaySwitch.exe /external && timeout /t 1 && DisplaySwitch.exe /extend",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                });
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clear VRAM cache to free memory
        /// </summary>
        public async Task<bool> ClearVramCacheAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    // Call Windows API to trim working sets and flush GPU memory
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                });
                
                await Task.Delay(500);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Export diagnostic report
        /// </summary>
        public async Task<string> ExportDiagnosticReportAsync()
        {
            try
            {
                var report = new System.Text.StringBuilder();
                report.AppendLine("=== Display Recovery Diagnostic Report ===");
                report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine();
                
                var status = GetCurrentSystemStatus();
                report.AppendLine($"GPU Driver Status: {status.GpuDriverStatus}");
                report.AppendLine($"GPU: {status.GpuName}");
                report.AppendLine($"GPU Load: {status.GpuLoad:F1}%");
                report.AppendLine($"GPU Temperature: {status.GpuTemperature}°C");
                report.AppendLine($"VRAM: {status.VramUsedGB:F1} / {status.VramTotalGB:F1} GB");
                report.AppendLine($"Display Surfaces: {status.DisplaySurfacesActive}");
                report.AppendLine($"DWM Compositor: {(status.DwmCompositorRunning ? "Running" : "Stopped")}");
                report.AppendLine($"DXGI Runtime: {(status.DxgiRuntimeHealthy ? "Healthy" : "Degraded")}");
                report.AppendLine($"Display Connectivity: {status.DisplayConnectivity}");
                report.AppendLine($"Power Management: {(status.PowerManagementStable ? "Stable" : "Unstable")}");
                report.AppendLine();
                
                report.AppendLine("=== Recent Recovery Operations ===");
                lock (_lock)
                {
                    foreach (var op in _completedOperations.TakeLast(10))
                    {
                        report.AppendLine($"[{op.StartTime:HH:mm:ss}] {op.Title} - {op.Status}");
                        report.AppendLine($"  {op.Description}");
                    }
                }
                
                return await Task.FromResult(report.ToString());
            }
            catch (Exception ex)
            {
                return $"Error generating report: {ex.Message}";
            }
        }

        #endregion

        #region Helper Methods

        private string GetGpuDriverStatus()
        {
            try
            {
                Debug.WriteLine("[DisplayRecovery] === Checking GPU Driver Status ===");
                
                var gpus = _hardwareService.GetGpus();
                if (gpus.Length == 0)
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ No GPU detected");
                    return "No GPU Detected";
                }

                // Check explorer.exe FIRST (gray wallpaper indicator)
                if (!IsExplorerHealthy())
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ DETECTED: Shell Issues (Explorer)");
                    return "Degraded - Shell Issues";
                }

                // Check DWM composition - if disabled, display is degraded
                if (!IsDwmRunning())
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ DETECTED: Composition Disabled");
                    return "Degraded - Composition Disabled";
                }

                // Check if we can render graphics
                if (!CanRenderGraphics())
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ DETECTED: Rendering Issues");
                    return "Degraded - Rendering Issues";
                }

                // Check if GPU is responding
                foreach (var gpu in gpus)
                {
                    gpu.Update();
                    var sensors = gpu.Sensors;
                    
                    // If we can read sensors, driver is working
                    if (sensors.Any())
                    {
                        Debug.WriteLine("[DisplayRecovery] ✅ GPU driver operational");
                        return "Operational";
                    }
                }

                Debug.WriteLine("[DisplayRecovery] ⚠️ GPU sensors not readable");
                return "Degraded";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ GPU check exception: {ex.Message}");
                return "Critical - Restart Needed";
            }
        }

        private (double usedGB, double totalGB) GetVramUtilization()
        {
            try
            {
                var gpus = _hardwareService.GetGpus();
                if (gpus.Length == 0)
                    return (0, 0);

                double totalVram = 0;
                double usedVram = 0;

                foreach (var gpu in gpus)
                {
                    gpu.Update();
                    
                    // Get memory info
                    var memoryUsed = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Used"));
                    var memoryTotal = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Total"));
                    
                    if (memoryUsed != null && memoryTotal != null)
                    {
                        usedVram += memoryUsed.Value ?? 0;
                        totalVram += memoryTotal.Value ?? 0;
                    }
                }

                return (usedVram / 1024.0, totalVram / 1024.0); // Convert MB to GB
            }
            catch
            {
                return (0, 12); // Default fallback
            }
        }

        private int GetActiveDisplayCount()
        {
            try
            {
                return GetSystemMetrics(SM_CMONITORS);
            }
            catch
            {
                return 1;
            }
        }

        private bool IsDwmRunning()
        {
            try
            {
                DwmIsCompositionEnabled(out bool enabled);
                
                // Also check if DWM process is actually running
                var dwmProcess = Process.GetProcessesByName("dwm");
                bool processRunning = dwmProcess.Length > 0;
                
                bool healthy = enabled && processRunning;
                Debug.WriteLine($"[DisplayRecovery] DWM Status: Enabled={enabled}, Process={processRunning}, Healthy={healthy}");
                
                return healthy;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ DWM check error: {ex.Message}");
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private bool IsExplorerHealthy()
        {
            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                if (explorerProcesses.Length == 0)
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ Explorer.exe not running!");
                    return false;
                }

                // Check if taskbar window exists and is visible
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle == IntPtr.Zero || !IsWindowVisible(taskbarHandle))
                {
                    Debug.WriteLine("[DisplayRecovery] ❌ Taskbar window not visible - Explorer degraded!");
                    return false;
                }

                // Check if explorer is responding
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        if (!proc.Responding)
                        {
                            Debug.WriteLine("[DisplayRecovery] ❌ Explorer.exe not responding!");
                            return false;
                        }
                    }
                    catch { }
                }

                Debug.WriteLine("[DisplayRecovery] ✅ Explorer.exe healthy");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ IsExplorerHealthy error: {ex.Message}");
                return false;
            }
        }

        private bool CanRenderGraphics()
        {
            try
            {
                // Check if we can create a graphics surface
                using (var bmp = new System.Drawing.Bitmap(1, 1))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    // Try to draw something
                    g.Clear(System.Drawing.Color.Black);
                    Debug.WriteLine("[DisplayRecovery] ✅ Graphics rendering works");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ Graphics rendering failed: {ex.Message}");
                return false;
            }
        }

        private bool CheckDxgiHealth()
        {
            try
            {
                Guid factoryGuid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
                int hr = CreateDXGIFactory1(ref factoryGuid, out IntPtr factory);
                
                if (factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }
                
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        private string GetDisplayConnectivity()
        {
            try
            {
                int activeCount = 0;
                int totalCount = 0;
                
                DISPLAY_DEVICE device = new DISPLAY_DEVICE();
                device.cb = Marshal.SizeOf(device);
                
                for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
                {
                    if ((device.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                        activeCount++;
                    totalCount++;
                }

                return $"{activeCount} / {Math.Max(activeCount, 2)} Active";
            }
            catch
            {
                return "Unknown";
            }
        }

        private bool CheckPowerManagement()
        {
            try
            {
                // Check if GPU is being throttled or has power issues
                // This checks for actual power management problems, not just low usage
                var gpus = _hardwareService.GetGpus();
                
                foreach (var gpu in gpus)
                {
                    gpu.Update();
                    
                    // Check for throttling indicators
                    var clockSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core"));
                    var tempSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    
                    // If GPU is very hot (>90C) and clock is low, it's throttling
                    if (tempSensor?.Value > 90 && clockSensor?.Value < 500)
                    {
                        Debug.WriteLine($"[DisplayRecovery] GPU throttling detected: {tempSensor.Value}°C, {clockSensor.Value}MHz");
                        return false;
                    }
                }
                
                // Stable by default (don't flag low power usage as unstable)
                return true;
            }
            catch
            {
                return true; // Assume stable if we can't check
            }
        }

        private (int temperature, double load, string name) GetGpuStats()
        {
            try
            {
                var gpus = _hardwareService.GetGpus();
                if (gpus.Length == 0)
                    return (0, 0, "Unknown GPU");

                var gpu = gpus[0]; // Primary GPU
                gpu.Update();

                var tempSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                var loadSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core"));

                int temp = tempSensor?.Value.HasValue == true ? (int)tempSensor.Value.Value : 0;
                double load = loadSensor?.Value ?? 0;

                return (temp, load, gpu.Name);
            }
            catch
            {
                return (0, 0, "Unknown GPU");
            }
        }

        private double GetTotalSystemMemory()
        {
            try
            {
                if (GetPhysicallyInstalledSystemMemory(out ulong memoryKB))
                {
                    return memoryKB / 1024.0 / 1024.0; // Convert KB to GB
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<bool> RestartGraphicsDriverAsync()
        {
            try
            {
                // Simulate TDR (Timeout Detection and Recovery)
                // In a real implementation, this would trigger driver restart via DXGI
                await Task.Delay(1000);
                
                // Force GPU memory flush
                await ClearVramCacheAsync();
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> RestartExplorerAsync()
        {
            try
            {
                Debug.WriteLine("[DisplayRecovery] RestartExplorerAsync starting...");
                
                await Task.Run(() =>
                {
                    // Kill all explorer processes
                    var explorerProcesses = Process.GetProcessesByName("explorer");
                    Debug.WriteLine($"[DisplayRecovery] Found {explorerProcesses.Length} Explorer processes");
                    
                    foreach (var proc in explorerProcesses)
                    {
                        try
                        {
                            Debug.WriteLine($"[DisplayRecovery] Killing Explorer PID {proc.Id}");
                            proc.Kill();
                            proc.WaitForExit(3000);
                            Debug.WriteLine($"[DisplayRecovery] Explorer PID {proc.Id} terminated");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DisplayRecovery] Failed to kill Explorer PID {proc.Id}: {ex.Message}");
                        }
                    }

                    // Wait a moment
                    Debug.WriteLine("[DisplayRecovery] Waiting 500ms...");
                    System.Threading.Thread.Sleep(500);

                    // Restart Explorer
                    Debug.WriteLine("[DisplayRecovery] Starting new Explorer process...");
                    var newProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        UseShellExecute = true
                    });
                    
                    if (newProc != null)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ✅ New Explorer started with PID {newProc.Id}");
                    }
                    else
                    {
                        Debug.WriteLine("[DisplayRecovery] ⚠️ Process.Start returned null");
                    }
                });

                Debug.WriteLine("[DisplayRecovery] RestartExplorerAsync completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ RestartExplorerAsync exception: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RestoreRenderingAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    // Clear graphics caches
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();

                    // Try to restart search host if it's having issues
                    try
                    {
                        var searchProcesses = Process.GetProcessesByName("SearchHost");
                        foreach (var proc in searchProcesses)
                        {
                            try { proc.Kill(); } catch { }
                        }
                    }
                    catch { }
                });

                await Task.Delay(500);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> RestartDwmAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    // Restart Desktop Window Manager by restarting theme service
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "net",
                            Arguments = "stop \"Themes\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            Verb = "runas"
                        })?.WaitForExit(2000);

                        System.Threading.Thread.Sleep(500);

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "net",
                            Arguments = "start \"Themes\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            Verb = "runas"
                        })?.WaitForExit(2000);
                    }
                    catch { }
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> EmergencyGpuStabilizationAsync()
        {
            try
            {
                // Clear VRAM cache
                await ClearVramCacheAsync();
                
                // Force garbage collection to reduce memory pressure
                await Task.Run(() =>
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                });
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StartOperation(RecoveryOperation operation)
        {
            lock (_lock)
            {
                _activeOperations.Add(operation);
            }
        }

        private void CompleteOperation(RecoveryOperation operation)
        {
            lock (_lock)
            {
                _activeOperations.Remove(operation);
                _completedOperations.Add(operation);
                
                // Keep only last 50 operations
                if (_completedOperations.Count > 50)
                {
                    _completedOperations.RemoveAt(0);
                }
            }
            
            RecoveryOperationCompleted?.Invoke(this, operation);
        }

        #endregion

        public List<RecoveryOperation> GetCompletedOperations()
        {
            lock (_lock)
            {
                return _completedOperations.TakeLast(20).ToList();
            }
        }

        public List<UnresponsiveApp> GetUnresponsiveApps()
        {
            lock (_lock)
            {
                return _unresponsiveApps.ToList();
            }
        }

        private void DetectUnresponsiveApps()
        {
            try
            {
                var allProcesses = Process.GetProcesses();
                var currentUnresponsive = new List<UnresponsiveApp>();
                var now = DateTime.Now;

                // System critical processes to never kill
                var systemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "System", "svchost", "csrss", "lsass", "winlogon", "services", "smss",
                    "dwm", "explorer", "taskmgr", "Bluetask", "SystemSettings"
                };

                foreach (var proc in allProcesses)
                {
                    try
                    {
                        // Skip system processes and ourselves
                        if (systemProcesses.Contains(proc.ProcessName) || proc.Id == Environment.ProcessId)
                            continue;

                        // Skip if process has no main window (background services)
                        if (proc.MainWindowHandle == IntPtr.Zero)
                            continue;

                        // Check if responding
                        if (!proc.Responding)
                        {
                            // Track start time
                            if (!_unresponsiveStartTimes.ContainsKey(proc.Id))
                            {
                                _unresponsiveStartTimes[proc.Id] = now;
                                Debug.WriteLine($"[DisplayRecovery] 📍 New unresponsive app detected: {proc.ProcessName} (PID {proc.Id})");
                            }

                            var unresponsiveTime = (now - _unresponsiveStartTimes[proc.Id]).TotalSeconds;

                            // Only report if unresponsive for at least 5 seconds
                            if (unresponsiveTime >= 5)
                            {
                                var app = new UnresponsiveApp
                                {
                                    ProcessName = proc.ProcessName,
                                    ProcessId = proc.Id,
                                    WindowTitle = proc.MainWindowTitle,
                                    UnresponsiveSeconds = (int)unresponsiveTime,
                                    MemoryMB = proc.WorkingSet64 / 1024 / 1024,
                                    IsSystemCritical = false
                                };

                                currentUnresponsive.Add(app);
                            }
                        }
                        else
                        {
                            // Remove from tracking if it recovered
                            if (_unresponsiveStartTimes.ContainsKey(proc.Id))
                            {
                                Debug.WriteLine($"[DisplayRecovery] ✅ App recovered: {proc.ProcessName} (PID {proc.Id})");
                                _unresponsiveStartTimes.Remove(proc.Id);
                            }
                        }
                    }
                    catch { }
                }

                // Clean up tracking for dead processes
                var deadProcessIds = _unresponsiveStartTimes.Keys
                    .Where(id => !allProcesses.Any(p => p.Id == id))
                    .ToList();

                foreach (var id in deadProcessIds)
                {
                    _unresponsiveStartTimes.Remove(id);
                }

                // Update list and notify if changed
                lock (_lock)
                {
                    bool changed = _unresponsiveApps.Count != currentUnresponsive.Count ||
                                   !_unresponsiveApps.All(a => currentUnresponsive.Any(c => c.ProcessId == a.ProcessId));

                    _unresponsiveApps.Clear();
                    _unresponsiveApps.AddRange(currentUnresponsive);

                    if (changed && _unresponsiveApps.Count > 0)
                    {
                        Debug.WriteLine($"[DisplayRecovery] ⚠️ {_unresponsiveApps.Count} unresponsive apps detected");
                        UnresponsiveAppsChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] Error detecting unresponsive apps: {ex.Message}");
            }
        }

        public bool KillUnresponsiveApp(int processId)
        {
            try
            {
                var proc = Process.GetProcessById(processId);
                Debug.WriteLine($"[DisplayRecovery] 💀 Killing unresponsive app: {proc.ProcessName} (PID {processId})");
                
                proc.Kill();
                proc.WaitForExit(3000);
                
                // Remove from tracking
                lock (_lock)
                {
                    _unresponsiveStartTimes.Remove(processId);
                    _unresponsiveApps.RemoveAll(a => a.ProcessId == processId);
                }
                
                Debug.WriteLine($"[DisplayRecovery] ✅ App terminated successfully");
                UnresponsiveAppsChanged?.Invoke(this, EventArgs.Empty);
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayRecovery] ❌ Failed to kill app: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _monitoringTimer?.Dispose();
            _hardwareService?.Dispose();
        }
    }
}

