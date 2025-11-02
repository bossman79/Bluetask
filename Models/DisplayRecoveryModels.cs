using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Bluetask.Models
{
    /// <summary>
    /// Represents a finding from the system health scan
    /// </summary>
    public class ScanFinding
    {
        public string Category { get; set; } = string.Empty; // Hardware, Process, Storage, Events, BSOD
        public string Severity { get; set; } = string.Empty; // Warning, Critical
        public string Icon { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string SeverityColor => Severity == "Critical" ? "#EF4444" : "#F59E0B";
    }

    /// <summary>
    /// Represents the results of a comprehensive system health scan
    /// </summary>
    public class ScanResults
    {
        public List<ScanFinding> Findings { get; set; } = new();
        public int DisplayIssues { get; set; }
        public int BsodRisks { get; set; }
        public bool HasFindings => Findings.Count > 0;
    }

    /// <summary>
    /// Represents a recovery operation performed by the display recovery system
    /// </summary>
    public partial class RecoveryOperation : ObservableObject
    {
        [ObservableProperty]
        private string _id = string.Empty;

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private RecoveryStatus _status = RecoveryStatus.Pending;

        [ObservableProperty]
        private DateTime _startTime;

        [ObservableProperty]
        private DateTime? _completedTime;

        public string RelativeTime
        {
            get
            {
                if (CompletedTime.HasValue)
                {
                    var elapsed = DateTime.Now - CompletedTime.Value;
                    if (elapsed.TotalSeconds < 60)
                        return "Completed just now";
                    else if (elapsed.TotalMinutes < 60)
                        return $"Completed {(int)elapsed.TotalMinutes} seconds ago";
                    else if (elapsed.TotalHours < 24)
                        return $"Completed {(int)elapsed.TotalHours} minutes ago";
                    else
                        return $"Completed {(int)elapsed.TotalDays} hours ago";
                }
                else
                {
                    var elapsed = DateTime.Now - StartTime;
                    if (elapsed.TotalSeconds < 2)
                        return "Starting...";
                    else
                        return $"In progress ({(int)elapsed.TotalSeconds}s)";
                }
            }
        }

        public string StatusIcon
        {
            get
            {
                return Status switch
                {
                    RecoveryStatus.Success => "✓",
                    RecoveryStatus.Failed => "✗",
                    RecoveryStatus.Warning => "!",
                    RecoveryStatus.InProgress => "⟳",
                    _ => "○"
                };
            }
        }

        public SolidColorBrush StatusBrush
        {
            get
            {
                return Status switch
                {
                    RecoveryStatus.Success => new SolidColorBrush(Colors.LimeGreen),
                    RecoveryStatus.Failed => new SolidColorBrush(Colors.Red),
                    RecoveryStatus.Warning => new SolidColorBrush(Colors.Orange),
                    RecoveryStatus.InProgress => new SolidColorBrush(Colors.CornflowerBlue),
                    _ => new SolidColorBrush(Colors.Gray)
                };
            }
        }
    }

    /// <summary>
    /// Status of a recovery operation
    /// </summary>
    public enum RecoveryStatus
    {
        Pending,
        InProgress,
        Success,
        Failed,
        Warning
    }

    /// <summary>
    /// Current status of the display system
    /// </summary>
    public partial class DisplaySystemStatus : ObservableObject
    {
        [ObservableProperty]
        private DateTime _timestamp;

        [ObservableProperty]
        private string _gpuDriverStatus = "Unknown";

        [ObservableProperty]
        private double _vramUsedGB;

        [ObservableProperty]
        private double _vramTotalGB;

        [ObservableProperty]
        private int _displaySurfacesActive;

        [ObservableProperty]
        private bool _dwmCompositorRunning;

        [ObservableProperty]
        private bool _dxgiRuntimeHealthy;

        [ObservableProperty]
        private string _displayConnectivity = "Unknown";

        [ObservableProperty]
        private bool _powerManagementStable;

        [ObservableProperty]
        private int _gpuTemperature;

        [ObservableProperty]
        private double _gpuLoad;

        [ObservableProperty]
        private string _gpuName = "Unknown GPU";

        [ObservableProperty]
        private double _systemMemoryGB;

        public string VramUtilizationDisplay => $"{VramUsedGB:F1} / {VramTotalGB:F0} GB";
        
        public string VramUtilizationPercent
        {
            get
            {
                if (VramTotalGB <= 0) return "0%";
                return $"{(VramUsedGB / VramTotalGB * 100.0):F0}%";
            }
        }

        public string GpuDriverStatusColor
        {
            get
            {
                return GpuDriverStatus switch
                {
                    "Operational" => "#10B981",
                    "Degraded" => "#F59E0B",
                    _ => "#EF4444"
                };
            }
        }

        public string DisplaySurfacesStatus
        {
            get
            {
                return DisplaySurfacesActive > 0 ? "All Active" : "Inactive";
            }
        }

        public string DisplaySurfacesColor
        {
            get
            {
                return DisplaySurfacesActive > 0 ? "#10B981" : "#EF4444";
            }
        }

        public string DwmCompositorStatus => DwmCompositorRunning ? "Running" : "Stopped";
        
        public string DwmCompositorColor => DwmCompositorRunning ? "#10B981" : "#EF4444";

        public string DxgiRuntimeStatus => DxgiRuntimeHealthy ? "Healthy" : "Degraded";
        
        public string DxgiRuntimeColor => DxgiRuntimeHealthy ? "#10B981" : "#F59E0B";

        public string PowerManagementStatus => PowerManagementStable ? "Stable" : "Unstable";
        
        public string PowerManagementColor => PowerManagementStable ? "#10B981" : "#F59E0B";

        public string DisplayConnectivityColor
        {
            get
            {
                if (DisplayConnectivity.Contains("2") || DisplayConnectivity.Contains("Active"))
                    return "#10B981";
                return "#F59E0B";
            }
        }
    }

    /// <summary>
    /// Diagnostic information item for the system diagnostics panel
    /// </summary>
    public partial class DiagnosticItem : ObservableObject
    {
        [ObservableProperty]
        private string _label = string.Empty;

        [ObservableProperty]
        private string _value = string.Empty;

        [ObservableProperty]
        private string _valueColor = "#FFFFFF";
    }
}

