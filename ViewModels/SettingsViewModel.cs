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
                    AvailableVersion = info.LatestVersion.ToString();
                    UpdateStatus = $"Update available: v{AvailableVersion}";
                    DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
                });
            };
            UpdateService.Shared.NoUpdateAvailable += info =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsUpdateAvailable = false;
                    AvailableVersion = info.LatestVersion.ToString();
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
            return IsUpdateAvailable && UpdateService.Shared.LastInfo != null && !string.IsNullOrEmpty(UpdateService.Shared.LastInfo.AssetDownloadUrl);
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
            var path = await UpdateService.Shared.DownloadInstallerAsync(progress);
            if (string.IsNullOrEmpty(path))
            {
                UpdateStatus = "Download failed";
                return;
            }
            var launched = UpdateService.Shared.TryLaunchInstaller(path);
            UpdateStatus = launched ? "Installer launched" : "Failed to launch installer";
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
