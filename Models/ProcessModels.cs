using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Bluetask.Models
{
    public sealed class ProcessModel : ObservableObject
    {
        private int _processId;
        public int ProcessId { get => _processId; set => SetProperty(ref _processId, value); }

        private string _name = string.Empty;
        public string Name { get => _name; set { if (SetProperty(ref _name, value)) { OnPropertyChanged(nameof(DisplayName)); } } }

        private double _cpuPercent;
        public double CpuPercent { get => _cpuPercent; set { if (SetProperty(ref _cpuPercent, value)) { OnPropertyChanged(nameof(CpuPercentDisplay)); } } }

        private long _memoryBytes;
        public long MemoryBytes { get => _memoryBytes; set { if (SetProperty(ref _memoryBytes, value)) { OnPropertyChanged(nameof(MemoryDisplay)); } } }

        private double _gpuPercent;
        public double GpuPercent { get => _gpuPercent; set { if (SetProperty(ref _gpuPercent, value)) { OnPropertyChanged(nameof(GpuPercentDisplay)); OnPropertyChanged(nameof(GpuPercentDisplayOrDash)); } } }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) { OnPropertyChanged(nameof(IsHighlighted)); } } }

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (SetProperty(ref _isPinned, value))
                {
                    OnPropertyChanged(nameof(IsHighlighted));
                }
            }
        }

        private long _pinSequence;
        public long PinSequence
        {
            get => _pinSequence;
            set => SetProperty(ref _pinSequence, value);
        }

        // Used by the UI to decide if the row should show the blue highlight overlay
        public bool IsHighlighted => IsSelected || IsPinned;

        public BitmapImage? Icon { get; set; }
        public ObservableCollection<ProcessModel> Children { get; } = new ObservableCollection<ProcessModel>();
        public bool HasChildren => Children.Count > 0;
        private bool _isExpanded = false; // Default to collapsed to hide children
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        private bool _isGroup;
        public bool IsGroup
        {
            get => _isGroup;
            set => SetProperty(ref _isGroup, value);
        }

        private int _instanceCount = 1;
        public int InstanceCount
        {
            get => _instanceCount;
            set { if (SetProperty(ref _instanceCount, value)) { OnPropertyChanged(nameof(DisplayName)); } }
        }

        public string MemoryDisplay => FormatBytes(MemoryBytes);
        public string CpuPercentDisplay => ($"{CpuPercent:F1}%");
        public string GpuPercentDisplay => ($"{GpuPercent:F1}%");
        public string GpuPercentDisplayOrDash => GpuPercent <= 0.0 ? "-" : GpuPercentDisplay;
        public string DisplayName => InstanceCount > 1 ? ($"{Name} × {InstanceCount}") : Name;

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            if (bytes >= GB) return ($"{bytes / (double)GB:0.0} GB");
            if (bytes >= MB) return ($"{bytes / (double)MB:0.0} MB");
            if (bytes >= KB) return ($"{bytes / (double)KB:0.0} KB");
            return ($"{bytes} B");
        }
    }
}



