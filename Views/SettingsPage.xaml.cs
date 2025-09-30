using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Bluetask.ViewModels;

namespace Bluetask.Views
{
	public sealed partial class SettingsPage : Page
	{
		public SettingsViewModel ViewModel { get; }
		public SettingsPage()
		{
			this.InitializeComponent();
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
	}
}


