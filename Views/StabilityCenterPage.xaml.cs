using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Bluetask.ViewModels;
using Bluetask.Services.Converters;
using Microsoft.UI;

namespace Bluetask.Views
{
	public sealed partial class StabilityCenterPage : Page
	{
		public StabilityCenterViewModel ViewModel { get; private set; }
		private bool _displayRecoveryPageLoaded = false;

	public StabilityCenterPage()
	{
		this.InitializeComponent();
		// Enable navigation caching for instant page switches
		this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
		ViewModel = new StabilityCenterViewModel();
		this.DataContext = ViewModel;
			ViewModel.PropertyChanged += ViewModel_PropertyChanged;
			
			// Default to Events tab
			ShowEventsTab();
		}

		private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(ViewModel.CurrentAnalysis) && ViewModel.CurrentAnalysis != null)
			{
				PopulateAnalysisCards();
			}
		}

		private void PopulateAnalysisCards()
		{
			try
			{
				var analysis = ViewModel.CurrentAnalysis;
				if (analysis == null) return;

				// Summary Card
				var summaryPanel = SummaryCard.Child as StackPanel;
				if (summaryPanel != null && summaryPanel.Children.Count > 0)
				{
					// Clear existing content except header
					while (summaryPanel.Children.Count > 1)
					{
						summaryPanel.Children.RemoveAt(1);
					}

					if (!string.IsNullOrWhiteSpace(analysis.Summary))
					{
						var summaryRtb = MarkdownToXamlConverter.ConvertToRichText(analysis.Summary);
						summaryPanel.Children.Add(summaryRtb);
					}
				}

				// Root Cause Card
				var rootCausePanel = RootCauseCard.Child as StackPanel;
				if (rootCausePanel != null && !string.IsNullOrWhiteSpace(analysis.RootCause))
				{
					while (rootCausePanel.Children.Count > 1)
					{
						rootCausePanel.Children.RemoveAt(1);
					}

					var rootCauseRtb = MarkdownToXamlConverter.ConvertToRichText(analysis.RootCause);
					rootCausePanel.Children.Add(rootCauseRtb);
					RootCauseCard.Visibility = Visibility.Visible;
				}
				else
				{
					RootCauseCard.Visibility = Visibility.Collapsed;
				}

				// Suggested Fixes Card (merge with additional context)
				var fixesPanel = FixesCard.Child as StackPanel;
				if (fixesPanel != null && !string.IsNullOrWhiteSpace(analysis.SuggestedFixes))
				{
					while (fixesPanel.Children.Count > 1)
					{
						fixesPanel.Children.RemoveAt(1);
					}

					// Merge additional context into fixes
					var combinedText = analysis.SuggestedFixes;
					if (!string.IsNullOrWhiteSpace(analysis.AdditionalContext))
					{
						combinedText += "\n\n" + analysis.AdditionalContext;
					}

					var fixesRtb = MarkdownToXamlConverter.ConvertToRichText(combinedText, "#D1FAE5");
					fixesPanel.Children.Add(fixesRtb);
					FixesCard.Visibility = Visibility.Visible;
				}
				else
				{
					FixesCard.Visibility = Visibility.Collapsed;
				}

				// Forum Research Card
				var forumPanel = ForumCard.Child as StackPanel;
				if (forumPanel != null && !string.IsNullOrWhiteSpace(analysis.ForumResearch))
				{
					while (forumPanel.Children.Count > 1)
					{
						forumPanel.Children.RemoveAt(1);
					}

					var forumRtb = MarkdownToXamlConverter.ConvertToRichText(analysis.ForumResearch);
					forumPanel.Children.Add(forumRtb);
					ForumCard.Visibility = Visibility.Visible;
				}
				else
				{
					ForumCard.Visibility = Visibility.Collapsed;
				}
			}
			catch { }
		}

		private void Refresh_Click(object sender, RoutedEventArgs e)
		{
			ViewModel?.Load();
		}

		private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (ViewModel != null)
			{
				ViewModel.SearchQuery = (sender as TextBox)?.Text ?? string.Empty;
			}
		}

		private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (ViewModel != null)
			{
				ViewModel.SelectedEvent = (sender as ListView)?.SelectedItem as Bluetask.Models.SystemEventItem;
			}
		}

		private void ToggleRaw_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (RawContainer.Visibility == Visibility.Visible)
				{
					RawContainer.Visibility = Visibility.Collapsed;
					SummaryContainer.Visibility = Visibility.Visible;
					ToggleRawButton.Content = "View raw";
				}
				else
				{
					SummaryContainer.Visibility = Visibility.Collapsed;
					RawContainer.Visibility = Visibility.Visible;
					ToggleRawButton.Content = "Hide raw";
				}
			}
			catch { }
		}

		private async void Analyze_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (ViewModel != null)
				{
					await ViewModel.AnalyzeSelectedEventAsync();
				}
			}
			catch { }
		}

		private void CloseAnalysis_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				ViewModel?.CloseAnalysisView();
			}
			catch { }
		}

		private void EventsTab_Click(object sender, RoutedEventArgs e)
		{
			ShowEventsTab();
		}

		private void DisplayRecoveryTab_Click(object sender, RoutedEventArgs e)
		{
			ShowDisplayRecoveryTab();
		}

		private void ShowEventsTab()
		{
			try
			{
				// Update button styles
				EventsTabButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 47, 62));
				DisplayRecoveryTabButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 31, 46));

				// Show/hide content
				EventsTabContent.Visibility = Visibility.Visible;
				DisplayRecoveryTabContent.Visibility = Visibility.Collapsed;
				SummaryCards.Visibility = Visibility.Visible;
			}
			catch { }
		}

		private void ShowDisplayRecoveryTab()
		{
			try
			{
				// Update button styles
				DisplayRecoveryTabButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 47, 62));
				EventsTabButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 31, 46));

				// Show/hide content
				EventsTabContent.Visibility = Visibility.Collapsed;
				DisplayRecoveryTabContent.Visibility = Visibility.Visible;
				SummaryCards.Visibility = Visibility.Collapsed;

				// Load display recovery page if not already loaded
				if (!_displayRecoveryPageLoaded)
				{
					DisplayRecoveryTabContent.Navigate(typeof(DisplayRecoveryPage));
					_displayRecoveryPageLoaded = true;
				}
			}
			catch { }
		}
	}
}



