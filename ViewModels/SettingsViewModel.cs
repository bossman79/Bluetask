using CommunityToolkit.Mvvm.ComponentModel;
using Bluetask.Services;
using System.Collections.Generic;

namespace Bluetask.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool _groupSameProcessNames = SettingsService.GroupSameProcessNames;

        [ObservableProperty]
        private bool _normalizeProcessUsage = SettingsService.NormalizeProcessUsage;

        [ObservableProperty]
        private bool _twoColumnDrivesAtFour = SettingsService.TwoColumnDrivesAtFour;

        [ObservableProperty]
        private MemoryMetric _selectedMemoryMetric = SettingsService.MemoryMetric;

        [ObservableProperty]
        private bool _enableEmulation = SettingsService.DebugGpuCount >= 0 || SettingsService.DebugDiskCount >= 0;

        [ObservableProperty]
        private double _debugGpuCount = SettingsService.DebugGpuCount >= 0 ? SettingsService.DebugGpuCount : 0;

        [ObservableProperty]
        private double _debugDiskCount = SettingsService.DebugDiskCount >= 0 ? SettingsService.DebugDiskCount : 0;

        public List<MemoryMetric> MemoryMetricOptions { get; } = new List<MemoryMetric>
        {
            MemoryMetric.WorkingSet,
            MemoryMetric.Private
        };

        partial void OnGroupSameProcessNamesChanged(bool value)
        {
            SettingsService.GroupSameProcessNames = value;
        }

        partial void OnNormalizeProcessUsageChanged(bool value)
        {
            SettingsService.NormalizeProcessUsage = value;
        }

        partial void OnTwoColumnDrivesAtFourChanged(bool value)
        {
            SettingsService.TwoColumnDrivesAtFour = value;
        }

        partial void OnSelectedMemoryMetricChanged(MemoryMetric value)
        {
            SettingsService.MemoryMetric = value;
        }

        partial void OnEnableEmulationChanged(bool value)
        {
            if (!value)
            {
                SettingsService.DebugGpuCount = -1;
                SettingsService.DebugDiskCount = -1;
            }
            else
            {
                // If turning on without values yet, default to 1/1 for visibility
                if (SettingsService.DebugGpuCount < 0)
                {
                    SettingsService.DebugGpuCount = (int)DebugGpuCount;
                }
                if (SettingsService.DebugDiskCount < 0)
                {
                    SettingsService.DebugDiskCount = (int)DebugDiskCount;
                }
            }
        }

        partial void OnDebugGpuCountChanged(double value)
        {
            if (EnableEmulation)
            {
                SettingsService.DebugGpuCount = (int)value;
            }
        }

        partial void OnDebugDiskCountChanged(double value)
        {
            if (EnableEmulation)
            {
                SettingsService.DebugDiskCount = (int)value;
            }
        }
    }
}
