using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace Bluetask.Models
{
    public partial class CpuInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;
        [ObservableProperty]
        private double _usage;
        [ObservableProperty]
        private int _temperature;
        [ObservableProperty]
        private string _coresAndThreads = string.Empty;
        [ObservableProperty]
        private List<double> _usageHistory = new List<double>();

        [ObservableProperty]
        private double _currentClockGhz;

        // New: instantaneous CPU package power (Watts) and core/package voltage (Volts)
        [ObservableProperty]
        private double _powerWatts;
        [ObservableProperty]
        private double _voltageVolts;

        // Counts
        [ObservableProperty]
        private int _physicalCoreCount;
        [ObservableProperty]
        private int _logicalProcessorCount;

        // Current utilization for each logical processor thread (0..N-1)
        [ObservableProperty]
        private List<double> _perCoreUsages = new List<double>();

        // Compatibility property for older bindings expecting Speed
        public string Speed => Name; // not used; kept for legacy binds to avoid compile-time errors
    }

    public partial class CpuCoreInfo : ObservableObject
    {
        [ObservableProperty]
        private int _index;

        [ObservableProperty]
        private double _usage;

        public string Label => $"Core {Index}";
    }

    public partial class GpuInfo : ObservableObject, IEquatable<GpuInfo>
    {
        [ObservableProperty]
        private string _name = string.Empty;
        [ObservableProperty]
        private double _usage;
        [ObservableProperty]
        private int _temperature;
        [ObservableProperty]
        private string _memory = string.Empty;
        [ObservableProperty]
        private List<double> _usageHistory = new List<double>();

        [ObservableProperty]
        private string _cardTitle = string.Empty;

        // Optional extended details for the GPU section cards
        [ObservableProperty]
        private string _dedicatedMemory = string.Empty;
        [ObservableProperty]
        private string _sharedMemory = string.Empty;
        [ObservableProperty]
        private string _driverVersion = string.Empty;
        [ObservableProperty]
        private string _directXVersion = string.Empty;
        [ObservableProperty]
        private string _driverDate = string.Empty;

        // UI helper: mark last item so templates can hide trailing dividers
        [ObservableProperty]
        private bool _isLast;

        // UI selection flag for GPU cards
        [ObservableProperty]
        private bool _isSelected;

        public bool Equals(GpuInfo? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(Usage - other.Usage) < 0.0001
                && Temperature == other.Temperature
                && string.Equals(Memory, other.Memory, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as GpuInfo);
        public override int GetHashCode()
        {
            return HashCode.Combine(Name?.ToLowerInvariant(), Math.Round(Usage, 3), Temperature, Memory?.ToLowerInvariant());
        }
    }

    public partial class DriveInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;
        [ObservableProperty]
        private double _usage;
        [ObservableProperty]
        private string _space = string.Empty;
        [ObservableProperty]
        private string _driveType = string.Empty;
    }

    public partial class RamInfo : ObservableObject
    {
        [ObservableProperty]
        private double _usage;
        [ObservableProperty]
        private string _type = string.Empty;
        [ObservableProperty]
        private string _usedAndTotal = string.Empty;
        [ObservableProperty]
        private string _moduleConfiguration = string.Empty;
        [ObservableProperty]
        private List<double> _usageHistory = new List<double>();

        // Extended fields for Memory page layout
        [ObservableProperty]
        private string _available = string.Empty;
        [ObservableProperty]
        private string _reserved = string.Empty;
        [ObservableProperty]
        private string _formFactor = string.Empty;
        [ObservableProperty]
        private string _slotsUsedSummary = string.Empty; // e.g. "2 of 4"

        [ObservableProperty]
        private string _usedOnly = string.Empty; // e.g. "12.3 GB"
        [ObservableProperty]
        private string _committed = string.Empty;
        [ObservableProperty]
        private string _cached = string.Empty;
        [ObservableProperty]
        private string _pagedPool = string.Empty;
        [ObservableProperty]
        private string _nonPagedPool = string.Empty;
        [ObservableProperty]
        private string _xmpOrExpo = string.Empty;

        // New: display vendor/model
        [ObservableProperty]
        private string _brand = string.Empty; // Manufacturer (e.g., G.Skill, Corsair)
        [ObservableProperty]
        private string _model = string.Empty; // Part number (e.g., F5-6000J3038F16GX2)
        [ObservableProperty]
        private string _displayName = string.Empty; // Brand + Model for UI subtitle

        // Compatibility properties for older bindings
        public double Used => 0;
        public double Total => 0;
    }

    public partial class NetworkInfo : ObservableObject
    {
        [ObservableProperty]
        private string _uploadSpeed = string.Empty;
        [ObservableProperty]
        private string _downloadSpeed = string.Empty;
        [ObservableProperty]
        private System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo> _topProcesses = new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>();
        [ObservableProperty]
        private List<double> _uploadHistory = new List<double>();
        [ObservableProperty]
        private List<double> _downloadHistory = new List<double>();

        // Extended fields for Ethernet page
        [ObservableProperty]
        private string _connectionType = string.Empty; // Ethernet / Wi-Fi
        [ObservableProperty]
        private string _linkSpeed = string.Empty; // e.g. 1.0 Gbps
        [ObservableProperty]
        private string _ipv4Address = string.Empty;
        [ObservableProperty]
        private string _status = string.Empty; // Connected / Disconnected
    }

    public partial class NetworkProcessInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;
        [ObservableProperty]
        private string _speed = string.Empty;
        [ObservableProperty]
        private string _uploadSpeed = string.Empty;
        [ObservableProperty]
        private string _downloadSpeed = string.Empty;
    }

    public partial class StorageInfo : ObservableObject, IEquatable<StorageInfo>
    {
        [ObservableProperty]
        private string _name = string.Empty;
        [ObservableProperty]
        private double _usage;
        [ObservableProperty]
        private string _space = string.Empty;
        [ObservableProperty]
        private string _driveType = string.Empty;
        [ObservableProperty]
        private List<double> _usageHistory = new List<double>();

        [ObservableProperty]
        private string _cardTitle = string.Empty;

        // Extended fields for Disk page layout
        [ObservableProperty]
        private string _capacity = string.Empty; // Total capacity (e.g. "931 GB")
        [ObservableProperty]
        private bool _isSystemDisk;
        [ObservableProperty]
        private bool _hasPageFile;

        // New: brand/manufacturer for display
        [ObservableProperty]
        private string _brand = string.Empty;

        // New: SSD/HDD indicator for display
        [ObservableProperty]
        private string _mediaKind = string.Empty; // "SSD" or "HDD" when known

        // UI helper: mark last item so templates can hide trailing dividers
        [ObservableProperty]
        private bool _isLast;

        // UI selection flag for disk cards
        [ObservableProperty]
        private bool _isSelected;

        // Compatibility property for older bindings
        public string Model => Name;

        public bool Equals(StorageInfo? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(DriveType, other.DriveType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Space, other.Space, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(Usage - other.Usage) < 0.0001;
        }

        public override bool Equals(object? obj) => Equals(obj as StorageInfo);
        public override int GetHashCode()
        {
            return HashCode.Combine(Name?.ToLowerInvariant(), DriveType?.ToLowerInvariant(), Space?.ToLowerInvariant(), Math.Round(Usage, 3));
        }
    }

    public partial class StorageSummary : ObservableObject
    {
        [ObservableProperty]
        private string _topProcess = string.Empty;
    }
}
