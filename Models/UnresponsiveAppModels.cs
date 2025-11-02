using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Bluetask.Models
{
    public partial class UnresponsiveApp : ObservableObject
    {
        [ObservableProperty]
        private string _processName = string.Empty;

        [ObservableProperty]
        private int _processId;

        [ObservableProperty]
        private string _windowTitle = string.Empty;

        [ObservableProperty]
        private int _unresponsiveSeconds;

        [ObservableProperty]
        private long _memoryMB;

        [ObservableProperty]
        private bool _isSystemCritical;

        public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle) ? ProcessName : WindowTitle;

        public string Details => $"PID: {ProcessId} • {MemoryMB} MB • {UnresponsiveSeconds}s frozen";

        public SolidColorBrush SeverityBrush
        {
            get
            {
                if (UnresponsiveSeconds > 60) return new SolidColorBrush(Colors.Red);
                if (UnresponsiveSeconds > 30) return new SolidColorBrush(Colors.Orange);
                return new SolidColorBrush(Colors.Yellow);
            }
        }

        public string SeverityIcon
        {
            get
            {
                if (UnresponsiveSeconds > 60) return "🔴";
                if (UnresponsiveSeconds > 30) return "⚠️";
                return "⏸️";
            }
        }
    }
}

