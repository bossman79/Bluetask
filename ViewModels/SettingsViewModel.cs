using CommunityToolkit.Mvvm.ComponentModel;
using Bluetask.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using System;
using Microsoft.UI.Dispatching;

namespace Bluetask.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
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

        [ObservableProperty]
        private bool _updateAutoCheckOnLaunch = SettingsService.UpdateAutoCheckOnLaunch;

        [ObservableProperty]
        private bool _updateIncludePrereleases = SettingsService.UpdateIncludePrereleases;

        [ObservableProperty]
        private bool _isCheckingUpdate;

        [ObservableProperty]
        private string _updateStatus = string.Empty;

        [ObservableProperty]
        private bool _isUpdateAvailable;

        [ObservableProperty]
        private string _availableVersion = string.Empty;

        [ObservableProperty]
        private double _downloadProgress;

        public IAsyncRelayCommand CheckForUpdatesCommand { get; }
        public IAsyncRelayCommand DownloadAndInstallUpdateCommand { get; }

        public SettingsViewModel()
        {
            CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
            DownloadAndInstallUpdateCommand = new AsyncRelayCommand(DownloadAndInstallAsync, CanDownloadInstall);

            // Configure updater
            UpdateService.Shared.Configure("bossman79", "Bluetask", false);
            UpdateService.Shared.CheckingChanged += () =>
            {
                var checking = UpdateService.Shared.IsChecking;
                _dispatcher.TryEnqueue(() =>
                {
                    IsCheckingUpdate = checking;
                    if (checking)
                    {
                        UpdateStatus = "Checking for updates...";
                    }
                });
            };
            UpdateService.Shared.UpdateAvailable += info =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsUpdateAvailable = true;
                    var tag = info.TagName ?? string.Empty;
                    var shortTag = tag.Length > 7 ? tag.Substring(0, 7) : tag;
                    UpdateStatus = string.IsNullOrEmpty(shortTag)
                        ? "Update available"
                        : $"Update available: {shortTag}";
                    DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
                });
            };
            UpdateService.Shared.NoUpdateAvailable += info =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsUpdateAvailable = false;
                    AvailableVersion = string.Empty;
                    UpdateStatus = "You're up to date";
                    DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
                });
            };
            UpdateService.Shared.CheckFailed += msg =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    UpdateStatus = string.IsNullOrEmpty(msg) ? "Update check failed" : msg;
                });
            };
        }

        private bool CanDownloadInstall()
        {
            // For incremental updates, we allow install when an update is flagged
            return IsUpdateAvailable;
        }

        private async Task CheckForUpdatesAsync()
        {
            UpdateStatus = "Checking for updates...";
            await UpdateService.Shared.CheckForUpdatesAsync();
        }

        private async Task DownloadAndInstallAsync()
        {
            if (!CanDownloadInstall()) return;
            UpdateStatus = "Downloading update...";
            var progress = new Progress<double>(p => DownloadProgress = p);
            var staging = await UpdateService.Shared.DownloadProgramFolderToStagingAsync(progress);
            if (string.IsNullOrEmpty(staging))
            {
                UpdateStatus = "Download failed";
                return;
            }
            UpdateStatus = "Restarting to apply...";
            var scheduled = UpdateService.Shared.ScheduleReplaceAndRestart(staging);
            if (scheduled)
            {
                // Close app; script will restart
                App.Current.Exit();
            }
            else
            {
                UpdateStatus = "Failed to schedule restart";
            }
        }

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

        partial void OnUpdateAutoCheckOnLaunchChanged(bool value)
        {
            SettingsService.UpdateAutoCheckOnLaunch = value;
        }

        partial void OnUpdateIncludePrereleasesChanged(bool value)
        {
            SettingsService.UpdateIncludePrereleases = value;
        }
    }
}
