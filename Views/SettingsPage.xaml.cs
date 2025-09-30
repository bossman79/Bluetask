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
		}
	}
}


