 using System.Collections.ObjectModel;

namespace Bluetask.Models
{
    public sealed class GpuAdapterModel
    {
        public string Name { get; set; } = string.Empty;
        public float? UsagePercent { get; set; }
        public float? TemperatureC { get; set; }
        public float? MemoryUsedGb { get; set; }
        public float? MemoryTotalGb { get; set; }
    }

    public sealed class DriveModel
    {
        public string Name { get; set; } = string.Empty;
        public float? UsedGb { get; set; }
        public float? TotalGb { get; set; }
        public float? UsagePercent { get; set; }
    }

    public sealed class SystemOverviewModel
    {
        public float? CpuUsagePercent { get; set; }
        public float? CpuTemperatureC { get; set; }
        public string CpuName { get; set; } = string.Empty;
        public float? MemoryUsedGb { get; set; }
        public float? MemoryTotalGb { get; set; }
        public float? MemoryUsagePercent { get; set; }
        public ObservableCollection<GpuAdapterModel> Gpus { get; } = new ObservableCollection<GpuAdapterModel>();
        public ObservableCollection<DriveModel> Drives { get; } = new ObservableCollection<DriveModel>();
    }
}


