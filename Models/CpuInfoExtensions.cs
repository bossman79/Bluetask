 using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluetask.Models
{
    public partial class CpuInfo
    {
        [ObservableProperty]
        private double _speedGhz;
    }
}