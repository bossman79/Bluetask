using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Bluetask.ViewModels;
using Bluetask.Services;
using Microsoft.UI.Xaml.Input;

namespace Bluetask.Views
{
	public sealed partial class SettingsPage : Page
	{
		public SettingsViewModel ViewModel { get; }
		private int _debugTapCount = 0;
		private DateTime _lastDebugTapTime = DateTime.MinValue;
	public SettingsPage()
	{
		this.InitializeComponent();
		// Enable navigation caching for instant page switches
		this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
		ViewModel = new SettingsViewModel();
		this.DataContext = ViewModel;
		// Reflect last auto-check status on open
		try
		{
			var info = Bluetask.Services.UpdateService.Shared.LastInfo;
			if (info != null)
			{
				if (info.LatestVersion > info.CurrentVersion)
				{
					ViewModel.IsUpdateAvailable = true;
					ViewModel.AvailableVersion = info.LatestVersion.ToString();
					ViewModel.UpdateStatus = $"Update available: v{ViewModel.AvailableVersion}";
				}
				else
				{
					ViewModel.IsUpdateAvailable = false;
					ViewModel.AvailableVersion = info.LatestVersion.ToString();
					ViewModel.UpdateStatus = "You're up to date";
				}
			}
		}
		catch { }
	}

		private async void DebugTitle_Tapped(object sender, TappedRoutedEventArgs e)
		{
			var now = DateTime.UtcNow;
			if ((now - _lastDebugTapTime).TotalSeconds > 3)
			{
				_debugTapCount = 0;
			}
			_lastDebugTapTime = now;
			_debugTapCount++;
			if (_debugTapCount >= 5)
			{
				_debugTapCount = 0;
				try
				{
					if (TokenDialog != null)
					{
						TokenDialog.XamlRoot = this.Content.XamlRoot;
						try { TokenBox.Password = SettingsService.UpdateAuthToken ?? string.Empty; } catch { }
						var result = await TokenDialog.ShowAsync();
						if (result == ContentDialogResult.Primary)
						{
							try
							{
								var token = TokenBox?.Password ?? string.Empty;
								SettingsService.UpdateAuthToken = token ?? string.Empty;
							}
							catch { }
						}
					}
				}
				catch { }
			}
		}
	}
}


