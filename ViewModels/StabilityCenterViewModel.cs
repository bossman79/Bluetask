using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bluetask.Models;
using Bluetask.Services;

namespace Bluetask.ViewModels
{
	public sealed partial class StabilityCenterViewModel : ObservableObject
	{
		private readonly EventLogService _service = EventLogService.Shared;
		private readonly DeepSeekService _deepSeekService = DeepSeekService.Shared;
		private readonly WinDbgService _winDbgService = WinDbgService.Shared;
		private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

		public ObservableCollection<SystemEventItem> AllEvents { get; } = new ObservableCollection<SystemEventItem>();
		public ObservableCollection<SystemEventItem> VisibleEvents { get; } = new ObservableCollection<SystemEventItem>();

		[ObservableProperty]
		private SystemEventItem? _selectedEvent;

		[ObservableProperty]
		private string _searchQuery = string.Empty;

		[ObservableProperty]
		private bool _showCritical = true;

		[ObservableProperty]
		private bool _showWarnings = true;

		[ObservableProperty]
		private bool _showInfo = true;

		[ObservableProperty]
		private int _countCritical;

		[ObservableProperty]
		private int _countWarnings;

		[ObservableProperty]
		private int _countInfo;

		[ObservableProperty]
		private int _countCrashes;

		[ObservableProperty]
		private bool _showCrashes = true;

		[ObservableProperty]
		private bool _isAnalyzing = false;

		[ObservableProperty]
		private bool _showAnalysisView = false;

		[ObservableProperty]
		private EventAnalysisResult? _currentAnalysis;

		[ObservableProperty]
		private bool _winDbgAvailable;

		[ObservableProperty]
		private string _winDbgStatus = string.Empty;

		public IRelayCommand RefreshCommand { get; }

		public StabilityCenterViewModel()
		{
			try { _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); } catch { }
			RefreshCommand = new RelayCommand(Load);
			
			// Defer WinDbg check and event loading to avoid blocking page creation
			_ = Task.Run(() =>
			{
				try
				{
					// Check WinDbg availability on background thread
					var available = _winDbgService.IsAvailable;
					var status = available 
						? "✓ Advanced crash dump analysis enabled (WinDbg integrated)" 
						: "⚠ Crash analysis tools not found - basic analysis only";
					
					// Update on UI thread
					if (_dispatcher != null)
					{
						_dispatcher.TryEnqueue(() =>
						{
							try
							{
								WinDbgAvailable = available;
								WinDbgStatus = status;
							}
							catch { }
						});
					}
					else
					{
						WinDbgAvailable = available;
						WinDbgStatus = status;
					}
				}
				catch { }
			});
			
			// Defer event log loading to avoid blocking startup
			_ = Task.Run(() =>
			{
				try { Load(); } catch { }
			});
		}

		partial void OnSearchQueryChanged(string value) => ApplyFilters();
		partial void OnShowCriticalChanged(bool value) => ApplyFilters();
		partial void OnShowWarningsChanged(bool value) => ApplyFilters();
		partial void OnShowInfoChanged(bool value) => ApplyFilters();
		partial void OnShowCrashesChanged(bool value)
		{
			if (value)
			{
				// When user turns on Crashes, default to show only crashes for clarity
				ShowCritical = false;
				ShowWarnings = false;
				ShowInfo = false;
			}
			else
			{
				// If all severities are off, default to Info to avoid empty screen
				if (!ShowCritical && !ShowWarnings && !ShowInfo)
				{
					ShowInfo = true;
				}
			}
			ApplyFilters();
		}

		public void Load()
		{
			try
			{
				var items = _service.GetRecentEvents(300).ToArray();
				
				// Update UI on dispatcher thread if available
				var updateAction = new Action(() =>
				{
					try
					{
						AllEvents.Clear();
						for (int i = 0; i < items.Length; i++) AllEvents.Add(items[i]);
						CountCrashes = items.Count(e => e.IsCrash);
						CountCritical = items.Count(e => !e.IsCrash && (e.Severity == EventSeverity.Critical || e.Severity == EventSeverity.Error));
						CountWarnings = items.Count(e => !e.IsCrash && e.Severity == EventSeverity.Warning);
						CountInfo = items.Count(e => !e.IsCrash && e.Severity == EventSeverity.Info);
						ApplyFilters();
					}
					catch { }
				});
				
				if (_dispatcher != null && !_dispatcher.HasThreadAccess)
				{
					_dispatcher.TryEnqueue(() => updateAction());
				}
				else
				{
					updateAction();
				}
			}
			catch { }
		}

		private void ApplyFilters()
		{
			try
			{
				string q = (SearchQuery ?? string.Empty).Trim();
				bool hasQ = !string.IsNullOrWhiteSpace(q);
				bool crashes = ShowCrashes;
				bool c = ShowCritical;
				bool w = ShowWarnings;
				bool i = ShowInfo;
			var desired = AllEvents.Where(ev =>
				// Severity filter: crashes or severity-specific events
				(
					(ev.IsCrash && crashes) ||
					(!ev.IsCrash && ((ev.Severity == EventSeverity.Critical || ev.Severity == EventSeverity.Error) ? c :
					 (ev.Severity == EventSeverity.Warning ? w : i)))
				)
				// AND search filter (applies to all events)
				&& (!hasQ || (ev.Title?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
					(ev.ProviderName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
					(ev.Message?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)))
			).Take(500).ToList();

				Reconcile(VisibleEvents, desired);
				if (SelectedEvent != null && !VisibleEvents.Contains(SelectedEvent)) SelectedEvent = null;
			}
			catch { }
		}

		private static void Reconcile(ObservableCollection<SystemEventItem> target, System.Collections.Generic.IList<SystemEventItem> desired)
		{
			for (int i = target.Count - 1; i >= 0; i--) if (!desired.Contains(target[i])) target.RemoveAt(i);
			for (int index = 0; index < desired.Count; index++)
			{
				var item = desired[index];
				int currentIndex = target.IndexOf(item);
				if (currentIndex == -1) target.Insert(index, item);
				else if (currentIndex != index) target.Move(currentIndex, index);
			}
		}

		public async Task AnalyzeSelectedEventAsync()
		{
			if (SelectedEvent == null || IsAnalyzing) return;

			try
			{
				IsAnalyzing = true;
				
				// Step 1: If this is a crash and WinDbg is available, analyze the dump first
				if (SelectedEvent.IsCrash && _winDbgService.IsAvailable)
				{
					try
					{
						System.Diagnostics.Debug.WriteLine($"[StabilityCenter] WinDbg is available, analyzing crash dump for: {SelectedEvent.AppName}");
						var dumpAnalysis = await _winDbgService.AnalyzeCrashForEventAsync(SelectedEvent);
						
						if (dumpAnalysis != null)
						{
							System.Diagnostics.Debug.WriteLine($"[StabilityCenter] WinDbg analysis completed. Success: {dumpAnalysis.Success}");
							if (!string.IsNullOrEmpty(dumpAnalysis.ErrorMessage))
								System.Diagnostics.Debug.WriteLine($"[StabilityCenter] WinDbg error: {dumpAnalysis.ErrorMessage}");
							
							if (dumpAnalysis.Success)
							{
								System.Diagnostics.Debug.WriteLine($"[StabilityCenter] Attaching WinDbg analysis to event (dump: {dumpAnalysis.DumpFilePath})");
								// Attach WinDbg analysis to the event for DeepSeek to use
								SelectedEvent.WinDbgAnalysis = dumpAnalysis;
							}
						}
						else
						{
							System.Diagnostics.Debug.WriteLine("[StabilityCenter] WinDbg returned null - no matching dump file found");
						}
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"[StabilityCenter] WinDbg analysis exception: {ex.Message}");
						// Don't fail the entire analysis if WinDbg fails
						// Just proceed without dump analysis
					}
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"[StabilityCenter] Skipping WinDbg: IsCrash={SelectedEvent.IsCrash}, Available={_winDbgService.IsAvailable}");
				}
				
				// Step 2: Send to DeepSeek with enhanced context
				System.Diagnostics.Debug.WriteLine($"[StabilityCenter] Sending to DeepSeek. Has WinDbg data: {SelectedEvent.WinDbgAnalysis != null}");
				var result = await _deepSeekService.AnalyzeEventAsync(SelectedEvent);
				CurrentAnalysis = result;
				
				if (result.Success)
				{
					ShowAnalysisView = true;
				}
			}
			catch (Exception ex)
			{
				CurrentAnalysis = new EventAnalysisResult
				{
					Success = false,
					ErrorMessage = $"Analysis failed: {ex.Message}"
				};
			}
			finally
			{
				IsAnalyzing = false;
			}
		}

		public void CloseAnalysisView()
		{
			ShowAnalysisView = false;
			CurrentAnalysis = null;
		}
	}
}


