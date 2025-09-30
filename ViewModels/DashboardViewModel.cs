using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Bluetask.Models;
using Bluetask.Services;
using System.Management;
using System.Threading;

namespace Bluetask.ViewModels
{
	public sealed partial class DashboardViewModel : ObservableObject, IDisposable
	{
		private readonly Bluetask.Services.SystemMonitorService _systemMonitor;
		private readonly ProcessMonitorService _processMonitor;
		private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);
		private readonly System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();
		private Task? _updateTask;
		private readonly DispatcherQueue _dispatcher;
		private bool _disposed;
        // Sort stability and throttling
        private readonly System.Collections.Generic.Dictionary<ProcessModel, double> _smoothedCpu = new System.Collections.Generic.Dictionary<ProcessModel, double>();
        private readonly System.Collections.Generic.Dictionary<ProcessModel, double> _smoothedGpu = new System.Collections.Generic.Dictionary<ProcessModel, double>();
        private readonly System.Collections.Generic.Dictionary<ProcessModel, double> _smoothedMem = new System.Collections.Generic.Dictionary<ProcessModel, double>();
        private const double SmoothAlpha = 0.35; // weight for new value in EMA
        private DateTime _lastSortApplyUtc = DateTime.MinValue;
        private readonly TimeSpan _minSortInterval = TimeSpan.FromMilliseconds(900);

		// Keep stable ProcessModel instances across refreshes to avoid UI churn
		private readonly System.Collections.Generic.Dictionary<int, ProcessModel> _modelsByPid = new System.Collections.Generic.Dictionary<int, ProcessModel>();
		// Stable group rows by process name
		private readonly System.Collections.Generic.Dictionary<string, ProcessModel> _groupModelsByName = new System.Collections.Generic.Dictionary<string, ProcessModel>(System.StringComparer.OrdinalIgnoreCase);

		// Prevent flooding the UI thread with multiple enqueued updates
		private int _uiUpdateScheduled;

		[ObservableProperty]
		private CpuInfo _cpu = new CpuInfo();

		[ObservableProperty]
		private RamInfo _ram = new RamInfo();

		public ObservableCollection<GpuInfo> Gpus { get; } = new ObservableCollection<GpuInfo>();
		public ObservableCollection<StorageInfo> Drives { get; } = new ObservableCollection<StorageInfo>();
		// Computed layout collections for the Storage card (two-column mode)
		public ObservableCollection<StorageInfo> DrivesLeft { get; } = new ObservableCollection<StorageInfo>();
		public ObservableCollection<StorageInfo> DrivesRight { get; } = new ObservableCollection<StorageInfo>();

		[ObservableProperty]
		private NetworkInfo _network = new NetworkInfo();

		[ObservableProperty]
		private StorageSummary _storage = new StorageSummary();

		// System drive (e.g., C:) pinned full-width when many drives are present
		[ObservableProperty]
		private StorageInfo? _systemDrive;

		// Toggle UI layout: two columns when more than five drives detected
		[ObservableProperty]
		private bool _showTwoColumnDrives;

		// Only show the system drive as a full-width row when there are 5 or more drives
		[ObservableProperty]
		private bool _showSystemDriveFullRow;

		public bool ShowSingleColumnDrives => !ShowTwoColumnDrives;

		partial void OnShowTwoColumnDrivesChanged(bool value)
		{
			OnPropertyChanged(nameof(ShowSingleColumnDrives));
		}

		public ObservableCollection<ProcessModel> Processes { get; } = new ObservableCollection<ProcessModel>();
		public ObservableCollection<ProcessModel> TopProcesses { get; } = new ObservableCollection<ProcessModel>();
		public ObservableCollection<ProcessModel> VisibleProcesses { get; } = new ObservableCollection<ProcessModel>();

		// Toggle: group processes by same name into a single row
		[ObservableProperty]
		private bool _groupSameNames = true;

		private System.Collections.Generic.List<ProcessMonitorService.ProcessInfo> _lastProcessItems = new System.Collections.Generic.List<ProcessMonitorService.ProcessInfo>();

		private ProcessModel? _selectedProcess;
		public ProcessModel? SelectedProcess
		{
			get => _selectedProcess;
			set
			{
				if (_selectedProcess == value) return;
				var previous = _selectedProcess;
				_selectedProcess = value;
				try { if (previous != null) previous.IsSelected = false; } catch { }
				try { if (_selectedProcess != null) _selectedProcess.IsSelected = true; } catch { }
				OnPropertyChanged(nameof(SelectedProcess));
			}
		}

		public void ToggleProcessSelection(ProcessModel model)
		{
			if (model == null) return;
			if (SelectedProcess == model)
			{
				SelectedProcess = null;
			}
			else
			{
				SelectedProcess = model;
			}
		}


		public enum ProcessSortColumn { Name, Cpu, Ram, Gpu }

		// No per-column filters; only sorting direction is shown via chevrons.

		[ObservableProperty]
		private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;

		[ObservableProperty]
		private bool _sortDescending = true;

		public string NameSortGlyph => SortColumn == ProcessSortColumn.Name ? (SortDescending ? "\uE70D" : "\uE70E") : "";
		public string CpuSortGlyph => SortColumn == ProcessSortColumn.Cpu ? (SortDescending ? "\uE70D" : "\uE70E") : "";
		public string RamSortGlyph => SortColumn == ProcessSortColumn.Ram ? (SortDescending ? "\uE70D" : "\uE70E") : "";
		public string GpuSortGlyph => SortColumn == ProcessSortColumn.Gpu ? (SortDescending ? "\uE70D" : "\uE70E") : "";

		public IRelayCommand<ProcessModel> KillProcessCommand { get; }
		public IRelayCommand<ProcessModel> OpenProcessLocationCommand { get; }

		// When true, defer sort reorders to avoid scroll jitter during interaction
		public bool SuppressSortDueToInteraction { get; set; }
		
		public void TogglePin(ProcessModel? model)
		{
			if (model == null) return;
			try
			{
				model.IsPinned = !model.IsPinned;
				ApplySortOnly(force: true);
				UpdateVisibleProcesses();
				RefreshTopProcesses();
			}
			catch { }
		}

		public DashboardViewModel()
		{
			_systemMonitor = Bluetask.Services.SystemMonitorService.Shared;
			_processMonitor = new ProcessMonitorService();
			_dispatcher = DispatcherQueue.GetForCurrentThread();

			KillProcessCommand = new RelayCommand<ProcessModel>(OnKillProcess, CanKillProcess);
			OpenProcessLocationCommand = new RelayCommand<ProcessModel>(OnOpenProcessLocation, CanOpenProcessLocation);

			// Initialize settings
			try
			{
				GroupSameNames = Bluetask.Services.SettingsService.GroupSameProcessNames;
				Bluetask.Services.SettingsService.GroupSameProcessNamesChanged += OnGroupSettingChanged;
				// React to two-column drive threshold setting changes
				Bluetask.Services.SettingsService.TwoColumnDrivesAtFourChanged += OnTwoColumnDriveThresholdChanged;
			}
			catch { }

			_updateTask = Task.Run(UpdateLoopAsync, _cts.Token);
		}

		private void OnTwoColumnDriveThresholdChanged(bool atFour)
		{
			try
			{
				// Re-evaluate current layout using the current drive list
				var current = Drives?.ToArray() ?? Array.Empty<StorageInfo>();
				UpdateDriveLayout(current);
			}
			catch { }
		}

		private void OnGroupSettingChanged(bool value)
		{
			GroupSameNames = value;
		}

		partial void OnSortColumnChanged(ProcessSortColumn value)
		{
			OnPropertyChanged(nameof(NameSortGlyph));
			OnPropertyChanged(nameof(CpuSortGlyph));
			OnPropertyChanged(nameof(RamSortGlyph));
			OnPropertyChanged(nameof(GpuSortGlyph));
			ApplySortOnly(force: true);
			UpdateVisibleProcesses();
			RefreshTopProcesses();
		}

		partial void OnSortDescendingChanged(bool value)
		{
			OnPropertyChanged(nameof(NameSortGlyph));
			OnPropertyChanged(nameof(CpuSortGlyph));
			OnPropertyChanged(nameof(RamSortGlyph));
			OnPropertyChanged(nameof(GpuSortGlyph));
			ApplySortOnly(force: true);
			UpdateVisibleProcesses();
			RefreshTopProcesses();
		}

		[ObservableProperty]
		private string _searchQuery = string.Empty;

		partial void OnSearchQueryChanged(string value)
		{
			SelectedProcess = null; // do not auto select when filtering
			UpdateVisibleProcesses();
		}

		partial void OnGroupSameNamesChanged(bool value)
		{
			try
			{
				if (_lastProcessItems != null && _lastProcessItems.Count > 0)
				{
					UpdateProcesses(_lastProcessItems);
				}
				else
				{
					ApplySortOnly();
					RefreshTopProcesses();
				}
			}
			catch { }
		}

		private async Task UpdateLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				try
				{
					_systemMonitor.Update();
					var cpu = _systemMonitor.GetCpuInfo();
					var ram = _systemMonitor.GetRamInfo();
					var gpus = _systemMonitor.GetGpuInfo().ToArray();
					// Slightly defer first drive probe to speed cold start
					var drives = (Processes.Count == 0 && Gpus.Count == 0) ? Array.Empty<StorageInfo>() : _systemMonitor.GetDriveInfo().ToArray();
					var net = _systemMonitor.GetNetworkInfo();
					var procs = _processMonitor.SampleProcesses();
					var topDiskProc = _systemMonitor.GetTopDiskProcessName();

				if (_dispatcher != null)
				{
					if (Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) == 0)
					{
						_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
						{
							try
							{
								Cpu = cpu;
								Ram = ram;
								UpdateCollection(Gpus, gpus);
								UpdateCollection(Drives, drives);
								UpdateDriveLayout(drives);
								// Update existing Network instance to avoid object churn
								try
								{
									var target = Network ?? new NetworkInfo();
									target.UploadSpeed = net?.UploadSpeed ?? string.Empty;
									target.DownloadSpeed = net?.DownloadSpeed ?? string.Empty;
									target.TopProcesses = net?.TopProcesses ?? new System.Collections.ObjectModel.ObservableCollection<NetworkProcessInfo>();
									target.UploadHistory = net?.UploadHistory ?? new System.Collections.Generic.List<double>();
									target.DownloadHistory = net?.DownloadHistory ?? new System.Collections.Generic.List<double>();
									if (!object.ReferenceEquals(Network, target)) Network = target;
								}
								catch { Network = net; }
								Storage.TopProcess = topDiskProc;
								UpdateProcesses(procs);
							}
							finally { Interlocked.Exchange(ref _uiUpdateScheduled, 0); }
						});
					}
				}
				else
				{
					Cpu = cpu;
					Ram = ram;
					UpdateCollection(Gpus, gpus);
					UpdateCollection(Drives, drives);
					UpdateDriveLayout(drives);
					Network = net;
					Storage.TopProcess = topDiskProc;
					UpdateProcesses(procs);
				}
				}
				catch { }

				try { await Task.Delay(_interval, _cts.Token); } catch { }
			}
		}

		private static void UpdateCollection<T>(ObservableCollection<T> target, T[] source)
		{
			if (target.SequenceEqual(source))
			{
				return;
			}

			var desired = new System.Collections.Generic.List<T>(source);
			for (int i = target.Count - 1; i >= 0; i--)
			{
				if (!desired.Contains(target[i]))
				{
					target.RemoveAt(i);
				}
			}
			for (int i = 0; i < desired.Count; i++)
			{
				var item = desired[i];
				int currentIndex = target.IndexOf(item);
				if (currentIndex == -1)
				{
					target.Insert(i, item);
				}
				else if (currentIndex != i)
				{
					target.Move(currentIndex, i);
				}
			}
		}

		private void UpdateDriveLayout(StorageInfo[] drives)
		{
			try
			{
				if (drives == null) drives = Array.Empty<StorageInfo>();
				var atFour = false;
				try { atFour = Bluetask.Services.SettingsService.TwoColumnDrivesAtFour; } catch { }
				ShowTwoColumnDrives = drives.Length >= (atFour ? 4 : 5);

				// Determine system drive (fallback to first drive if unknown)
				var sys = drives.FirstOrDefault(d => d != null && d.IsSystemDisk)
					?? drives.FirstOrDefault();
				SystemDrive = sys;
				// Determine whether to show the full-width system row (5+ drives only)
				var showTop = drives.Length >= 5;
				ShowSystemDriveFullRow = showTop;

				// Distribute remaining drives into two columns. If not showing the top row (e.g., 4 drives), include system drive in the columns.
				var others = showTop ? drives.Where(d => !ReferenceEquals(d, sys)).ToArray() : drives;
				var left = new System.Collections.Generic.List<StorageInfo>();
				var right = new System.Collections.Generic.List<StorageInfo>();
				for (int i = 0; i < others.Length; i++)
				{
					if ((i % 2) == 0) left.Add(others[i]); else right.Add(others[i]);
				}

				UpdateCollection(DrivesLeft, left.ToArray());
				UpdateCollection(DrivesRight, right.ToArray());
			}
			catch { }
		}

		private void UpdateProcesses(System.Collections.Generic.IReadOnlyList<ProcessMonitorService.ProcessInfo> items)
		{
			_lastProcessItems = new System.Collections.Generic.List<ProcessMonitorService.ProcessInfo>(items);
			// Normalize per-process CPU so summed processes align with the total CPU card
			try
			{
				bool normalize = false;
				try { normalize = Bluetask.Services.SettingsService.NormalizeProcessUsage; } catch { }
				if (normalize)
				{
					double reportedTotal = Cpu?.Usage ?? 0.0;
				double measuredSum = 0.0;
				foreach (var pi in items) measuredSum += Math.Max(0.0, pi.CpuPercent);
				if (reportedTotal > 0.0 && measuredSum > 0.0)
				{
					double scale = reportedTotal / measuredSum;
					// Avoid jitter from tiny differences
					if (Math.Abs(scale - 1.0) > 0.02)
					{
						foreach (var pi in _lastProcessItems)
						{
							var v = pi.CpuPercent * scale;
							if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
							pi.CpuPercent = v;
						}
					}
				}

					// Per-adapter normalization: map process GPU per adapter to reported GPU cards by usage
					try
					{
						var adapters = Gpus?.ToArray() ?? Array.Empty<GpuInfo>();
						if (adapters.Length > 0)
						{
							// Sum measured per-adapter totals across all processes
							var measuredTotals = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
							foreach (var pi in items)
							{
								if (pi?.GpuByAdapterPercent == null) continue;
								foreach (var kv in pi.GpuByAdapterPercent)
								{
									if (!measuredTotals.ContainsKey(kv.Key)) measuredTotals[kv.Key] = 0.0;
									measuredTotals[kv.Key] += Math.Max(0.0, kv.Value);
								}
							}

							if (measuredTotals.Count > 0)
							{
								// Order adapters by reported Usage desc; order measured adapter keys by measured total desc
								var reportedOrdered = adapters.OrderByDescending(a => a.Usage).ToArray();
								var measuredOrdered = measuredTotals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
								int mapCount = Math.Min(reportedOrdered.Length, measuredOrdered.Length);
								var targetTotalsByAdapterKey = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
								for (int i = 0; i < measuredOrdered.Length; i++)
								{
									// Map top-N measured adapters to top-N reported. Others map to 0.
									if (i < mapCount)
									{
										targetTotalsByAdapterKey[measuredOrdered[i]] = Math.Max(0.0, reportedOrdered[i].Usage);
									}
									else
									{
										targetTotalsByAdapterKey[measuredOrdered[i]] = 0.0;
									}
								}

								// Compute per-adapter scales and apply to each process
								var scaleByAdapterKey = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
								foreach (var kv in measuredTotals)
								{
									double target = 0.0;
									targetTotalsByAdapterKey.TryGetValue(kv.Key, out target);
									double denom = kv.Value;
									double s = (denom > 0.0) ? (target / denom) : 0.0;
									// Avoid excessive scaling due to jitter
									if (double.IsNaN(s) || double.IsInfinity(s)) s = 0.0;
									scaleByAdapterKey[kv.Key] = s;
								}

								foreach (var pi in _lastProcessItems)
								{
									if (pi.GpuByAdapterPercent == null || pi.GpuByAdapterPercent.Count == 0) continue;
									var keys = pi.GpuByAdapterPercent.Keys.ToArray();
									double newSum = 0.0;
									foreach (var k in keys)
									{
										double s = 0.0;
										scaleByAdapterKey.TryGetValue(k, out s);
										var nv = Math.Max(0.0, pi.GpuByAdapterPercent[k] * s);
										if (nv > 100.0) nv = 100.0;
										pi.GpuByAdapterPercent[k] = nv;
										newSum += nv;
									}
									pi.GpuPercent = Math.Clamp(newSum, 0.0, 100.0);
								}
							}
						}
					}
					catch { }
				}
			}
			catch { }
			// Reuse or create models by PID to minimize UI resets
			var seenPids = new System.Collections.Generic.HashSet<int>();
			foreach (var pi in items)
			{
				if (!_modelsByPid.TryGetValue(pi.ProcessId, out var model))
				{
					model = new ProcessModel();
					_modelsByPid[pi.ProcessId] = model;
				}
				model.ProcessId = pi.ProcessId;
				model.Name = pi.Name;
				model.CpuPercent = Math.Clamp(pi.CpuPercent, 0.0, 100.0);
				model.MemoryBytes = pi.MemoryBytes;
				model.GpuPercent = Math.Clamp(pi.GpuPercent, 0.0, 100.0);
				// Ensure Children modifications occur on the UI thread to avoid COM exceptions
				try { if (model.Children == null) { var _ = model.HasChildren; } } catch { }
				seenPids.Add(pi.ProcessId);
			}

			// Remove models for processes that disappeared
			var removed = _modelsByPid.Keys.Where(pid => !seenPids.Contains(pid)).ToArray();
			foreach (var pid in removed)
			{
				_modelsByPid.Remove(pid);
			}
			// Clear selection if selected process has disappeared
			if (SelectedProcess != null && removed.Contains(SelectedProcess.ProcessId))
			{
				SelectedProcess = null;
			}

			// Build desired children mapping; avoid clearing UI collections to reduce churn
			var desiredChildren = new System.Collections.Generic.Dictionary<ProcessModel, System.Collections.Generic.List<ProcessModel>>();
			// Helper to determine whether a parent should be treated as a launcher/wrapper rather than a true owner.
			static bool IsLauncherParentName(string? name)
			{
				try
				{
					if (string.IsNullOrWhiteSpace(name)) return false;
					var n = name;
					// Curated list of common Windows launcher/wrapper parents
					if (string.Equals(n, "explorer.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "StartMenuExperienceHost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "ShellExperienceHost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "ApplicationFrameHost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "sihost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "winlogon.exe", System.StringComparison.OrdinalIgnoreCase)) return true; // DWM child
					if (string.Equals(n, "services.exe", System.StringComparison.OrdinalIgnoreCase)) return true; // SCM
					if (string.Equals(n, "svchost.exe", System.StringComparison.OrdinalIgnoreCase)) return true; // Service host
					if (string.Equals(n, "RuntimeBroker.exe", System.StringComparison.OrdinalIgnoreCase)) return true; // UWP broker
					if (string.Equals(n, "taskhostw.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "taskhost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "TextInputHost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (string.Equals(n, "SearchHost.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					// Conservative heuristics: many wrappers end with Host/Broker
					if (n.EndsWith("Host.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
					if (n.EndsWith("Broker.exe", System.StringComparison.OrdinalIgnoreCase)) return true;
				}
				catch { }
				return false;
			}
			foreach (var pi in items)
			{
				if (pi.ParentId > 0 && _modelsByPid.TryGetValue(pi.ParentId, out var parent) && _modelsByPid.TryGetValue(pi.ProcessId, out var child))
				{
					var parentName = parent.Name;
					if (!IsLauncherParentName(parentName))
					{
						if (!desiredChildren.TryGetValue(parent, out var list))
						{
							list = new System.Collections.Generic.List<ProcessModel>();
							desiredChildren[parent] = list;
						}
						list.Add(child);
					}
				}
			}

			// Top-level roots are those whose parent isn't present
			var roots = new System.Collections.Generic.List<ProcessModel>();
			foreach (var pi in items)
			{
				bool parentPresent = _modelsByPid.ContainsKey(pi.ParentId);
				bool parentIsLauncher = false;
				if (parentPresent)
				{
					var parent = _modelsByPid[pi.ParentId];
					parentIsLauncher = IsLauncherParentName(parent.Name);
				}
				if (pi.ParentId <= 0 || !parentPresent || parentIsLauncher)
				{
					var root = _modelsByPid[pi.ProcessId];
					// Reset per-refresh fields that depend on aggregation
					root.InstanceCount = 1;
					root.IsGroup = false;
					roots.Add(root);
				}
			}

			// Merge Steam helper processes under the Steam process wherever it is in the tree
			try
			{
				var allNodes = _modelsByPid.Values.ToArray();
				var steamAnchor = roots.FirstOrDefault(r => string.Equals(r?.Name, "steam.exe", System.StringComparison.OrdinalIgnoreCase))
					?? allNodes.FirstOrDefault(n => string.Equals(n?.Name, "steam.exe", System.StringComparison.OrdinalIgnoreCase));
				if (steamAnchor != null)
				{
					var helpers = allNodes.Where(n => n != null && !object.ReferenceEquals(n, steamAnchor)
						&& string.Equals(n.Name, "steamwebhelper.exe", System.StringComparison.OrdinalIgnoreCase)).ToArray();
					if (helpers.Length > 0)
					{
						bool ContainsRecursive(ProcessModel parent, ProcessModel child)
						{
							if (desiredChildren.TryGetValue(parent, out var kids) && kids.Contains(child)) return true;
							if (desiredChildren.TryGetValue(parent, out var list))
							{
								for (int i = 0; i < list.Count; i++)
								{
									if (ContainsRecursive(list[i], child)) return true;
								}
							}
							return false;
						}

						foreach (var h in helpers)
						{
							try
							{
								if (ContainsRecursive(steamAnchor, h)) continue; // already under Steam
								// Remove from current desired parent if any
								ProcessModel? currentParent = null;
								foreach (var kv in desiredChildren)
								{
									if (kv.Value.Contains(h)) { currentParent = kv.Key; break; }
								}
								if (currentParent != null)
								{
									try { desiredChildren[currentParent].Remove(h); } catch { }
								}
								// Remove from roots if present
								if (roots.Contains(h))
								{
									try { roots.Remove(h); } catch { }
								}
								// Attach under Steam anchor in desired mapping
								if (!desiredChildren.TryGetValue(steamAnchor, out var sKids)) { sKids = new System.Collections.Generic.List<ProcessModel>(); desiredChildren[steamAnchor] = sKids; }
								if (!sKids.Contains(h)) sKids.Add(h);
							}
							catch { }
						}
					}
				}
				else
				{
					// No steam.exe process present, but if helpers exist, create a synthetic Steam group container
					var helperRoots = roots.Where(r => r != null && string.Equals(r.Name, "steamwebhelper.exe", System.StringComparison.OrdinalIgnoreCase)).ToArray();
					if (helperRoots.Length > 0)
					{
						var steamGroup = new ProcessModel();
						steamGroup.Name = "steam.exe";
						steamGroup.IsGroup = true;
						steamGroup.InstanceCount = helperRoots.Length;
						double cpu = 0.0; long mem = 0; double gpu = 0.0;
						for (int i = 0; i < helperRoots.Length; i++)
						{
							var h = helperRoots[i];
							cpu += h.CpuPercent; mem += h.MemoryBytes; gpu += h.GpuPercent;
						}
						steamGroup.CpuPercent = System.Math.Clamp(cpu, 0.0, 100.0);
						steamGroup.MemoryBytes = mem;
						steamGroup.GpuPercent = System.Math.Clamp(gpu, 0.0, 100.0);
						// In desired mapping, attach helpers under the group
						desiredChildren[steamGroup] = new System.Collections.Generic.List<ProcessModel>(helperRoots);
						// Remove helpers from roots and add the new group
						for (int i = 0; i < helperRoots.Length; i++)
						{
							try { roots.Remove(helperRoots[i]); } catch { }
						}
						try { roots.Add(steamGroup); } catch { }
					}
				}
			}
			catch { }

			// Reconcile actual Children collections to desired mapping (minimal removes/moves/inserts)
			var parentsToUpdate = new System.Collections.Generic.HashSet<ProcessModel>();
			foreach (var kv in desiredChildren) parentsToUpdate.Add(kv.Key);
			foreach (var m in _modelsByPid.Values)
			{
				if (m.Children.Count > 0) parentsToUpdate.Add(m);
			}
			foreach (var parent in parentsToUpdate)
			{
				System.Collections.Generic.List<ProcessModel> list;
				if (!desiredChildren.TryGetValue(parent, out list)) list = new System.Collections.Generic.List<ProcessModel>();
				ReconcileChildrenCollection(parent.Children, list);
			}

			// Aggregate CPU/RAM/GPU up the tree so parent rows reflect descendants
			foreach (var root in roots)
			{
				AggregateProcessTree(root);
			}

			System.Collections.Generic.List<ProcessModel> newTopLevel;
			if (GroupSameNames)
			{
				// Group same-named roots under a synthetic group row per name
				newTopLevel = BuildGroupedTopLevel(roots);
			}
			else
			{
				newTopLevel = roots;
			}

			// Update root collection using minimal changes (remove/move/add) to preserve scroll position
			ReconcileTopLevelOrder(newTopLevel);
			ApplySortOnly();
			UpdateVisibleProcesses();
			RefreshTopProcesses();
		}

		private System.Collections.Generic.List<ProcessModel> BuildGroupedTopLevel(System.Collections.Generic.List<ProcessModel> roots)
		{
			var groups = roots
				.GroupBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var desired = new System.Collections.Generic.List<ProcessModel>(groups.Length);
			var seenNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var g in groups)
			{
				var name = g.Key ?? string.Empty;
				var items = g.ToArray();
				if (items.Length <= 1)
				{
					var single = items[0];
					single.InstanceCount = 1;
					single.IsGroup = false;
					desired.Add(single);
				}
				else
				{
					if (!_groupModelsByName.TryGetValue(name, out var group))
					{
						group = new ProcessModel();
						_groupModelsByName[name] = group;
					}
					group.Name = name;
					group.IsGroup = true;
					group.InstanceCount = items.Length;
					// Sum aggregated metrics from roots
					double cpu = 0.0;
					long mem = 0;
					double gpu = 0.0;
					foreach (var r in items)
					{
						cpu += r.CpuPercent;
						mem += r.MemoryBytes;
						gpu += r.GpuPercent;
					}
					group.CpuPercent = System.Math.Clamp(cpu, 0.0, 100.0);
					group.MemoryBytes = mem;
					group.GpuPercent = System.Math.Clamp(gpu, 0.0, 100.0);
					// Rebuild children list to contain the individual roots with minimal churn
					ReconcileChildrenCollection(group.Children, new System.Collections.Generic.List<ProcessModel>(items));
					desired.Add(group);
				}
				seenNames.Add(name);
			}

			// Trim unused group models to avoid stale references
			var stale = _groupModelsByName.Keys.Where(k => !seenNames.Contains(k)).ToArray();
			foreach (var k in stale)
			{
				_groupModelsByName.Remove(k);
			}

			return desired;
		}

		private (double cpu, long memBytes, double gpu) AggregateProcessTree(ProcessModel node)
		{
			double cpu = node.CpuPercent;
			long mem = node.MemoryBytes;
			double gpu = node.GpuPercent;
			foreach (var child in node.Children)
			{
				var (ccpu, cmem, cgpu) = AggregateProcessTree(child);
				cpu += ccpu;
				mem += cmem;
				gpu += cgpu;
			}
			node.CpuPercent = Math.Clamp(cpu, 0.0, 100.0);
			node.MemoryBytes = mem;
			node.GpuPercent = Math.Clamp(gpu, 0.0, 100.0);
			return (node.CpuPercent, node.MemoryBytes, node.GpuPercent);
		}

		private void UpdateVisibleProcesses()
		{
			try
			{
				var query = SearchQuery ?? string.Empty;
				var hasQuery = !string.IsNullOrWhiteSpace(query);
				System.Collections.Generic.List<ProcessModel> desired;
				if (!hasQuery)
				{
					desired = Processes.ToList();
				}
				else
				{
					desired = new System.Collections.Generic.List<ProcessModel>();
					foreach (var root in Processes)
					{
						if ((root.Name != null && root.Name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0))
						{
							desired.Add(root);
							continue;
						}
						bool childMatch = false;
						foreach (var child in root.Children)
						{
							if (child.Name != null && child.Name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0)
							{
								childMatch = true;
								break;
							}
						}
						if (childMatch)
						{
							desired.Add(root);
						}
					}
				}

				ReconcileCollection(VisibleProcesses, desired);
			}
			catch { }
		}

		private static void ReconcileCollection(ObservableCollection<ProcessModel> target, System.Collections.Generic.List<ProcessModel> desired)
		{
			try
			{
				for (int i = target.Count - 1; i >= 0; i--)
				{
					if (!desired.Contains(target[i]))
					{
						target.RemoveAt(i);
					}
				}
				for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
				{
					var item = desired[targetIndex];
					int currentIndex = target.IndexOf(item);
					if (currentIndex == -1)
					{
						target.Insert(targetIndex, item);
					}
					else if (currentIndex != targetIndex)
					{
						target.Move(currentIndex, targetIndex);
					}
				}
			}
			catch { }
		}

		public void SetSort(ProcessSortColumn column)
		{
			if (SortColumn == column)
			{
				SortDescending = !SortDescending;
			}
			else
			{
				SortColumn = column;
				SortDescending = column != ProcessSortColumn.Name; // default desc for numeric
			}
		}

		private void ApplySortOnly(bool force = false)
		{
			try
			{
				// Throttle during interaction to reduce scroll jitter
				if (!force)
				{
					if (SuppressSortDueToInteraction) return;
					var now = DateTime.UtcNow;
					if ((now - _lastSortApplyUtc) < _minSortInterval) return;
					_lastSortApplyUtc = now;
				}

				System.Func<ProcessModel, double> numericKey = (m) => m.CpuPercent;
				switch (SortColumn)
				{
					case ProcessSortColumn.Cpu:
						numericKey = (m) => GetSmoothed(_smoothedCpu, m, m.CpuPercent);
						break;
					case ProcessSortColumn.Ram:
						numericKey = (m) => GetSmoothed(_smoothedMem, m, m.MemoryBytes);
						break;
					case ProcessSortColumn.Gpu:
						numericKey = (m) => GetSmoothed(_smoothedGpu, m, m.GpuPercent);
						break;
					case ProcessSortColumn.Name:
						// Names are stable, use index fallback for tie-break
						numericKey = (m) => 0.0;
						break;
					default:
						numericKey = (m) => GetSmoothed(_smoothedCpu, m, m.CpuPercent);
						break;
				};

				bool IsPinnedRecursive(ProcessModel n)
				{
					if (n.IsPinned) return true;
					for (int i = 0; i < n.Children.Count; i++)
					{
						if (IsPinnedRecursive(n.Children[i])) return true;
					}
					return false;
				}

				System.Action<ProcessModel> sortSubtree = null!;
				sortSubtree = (node) =>
				{
					if (node.Children.Count > 0)
					{
						System.Func<ProcessModel, int> stableIndex = (m) => node.Children.IndexOf(m);
						var pinnedChildren = (SortDescending
							? node.Children.Where(IsPinnedRecursive).OrderByDescending(numericKey).ThenBy(stableIndex)
							: node.Children.Where(IsPinnedRecursive).OrderBy(numericKey).ThenBy(stableIndex)).ToArray();
						var otherChildren = (SortDescending
							? node.Children.Where(c => !IsPinnedRecursive(c)).OrderByDescending(numericKey).ThenBy(stableIndex)
							: node.Children.Where(c => !IsPinnedRecursive(c)).OrderBy(numericKey).ThenBy(stableIndex)).ToArray();
						var ordered = pinnedChildren.Concat(otherChildren).ToArray();
						for (int i = 0; i < ordered.Length; i++) { sortSubtree(ordered[i]); }
						ReconcileChildrenCollection(node.Children, ordered.ToList());
					}
				};

				System.Func<ProcessModel, int> rootIndex = (m) => Processes.IndexOf(m);
				var pinned = (SortDescending
					? Processes.Where(IsPinnedRecursive).OrderByDescending(numericKey).ThenBy(rootIndex)
					: Processes.Where(IsPinnedRecursive).OrderBy(numericKey).ThenBy(rootIndex)).ToArray();
				var others = (SortDescending
					? Processes.Where(m => !IsPinnedRecursive(m)).OrderByDescending(numericKey).ThenBy(rootIndex)
					: Processes.Where(m => !IsPinnedRecursive(m)).OrderBy(numericKey).ThenBy(rootIndex)).ToArray();
				var sorted = pinned.Concat(others).ToArray();

				// Move items into the desired order instead of clearing
				for (int targetIndex = 0; targetIndex < sorted.Length; targetIndex++)
				{
					var item = sorted[targetIndex];
					int currentIndex = Processes.IndexOf(item);
					if (currentIndex >= 0 && currentIndex != targetIndex)
					{
						Processes.Move(currentIndex, targetIndex);
					}
					sortSubtree(item);
				}
			}
			catch { }
		}

		private double GetSmoothed(System.Collections.Generic.Dictionary<ProcessModel, double> map, ProcessModel model, double current)
		{
			if (double.IsNaN(current) || double.IsInfinity(current)) current = 0.0;
			double prev;
			if (!map.TryGetValue(model, out prev))
			{
				prev = current;
			}
			double next = prev * (1.0 - SmoothAlpha) + current * SmoothAlpha;
			map[model] = next;
			return next;
		}

		private static void ReconcileChildrenCollection(System.Collections.ObjectModel.ObservableCollection<ProcessModel> target, System.Collections.Generic.IList<ProcessModel> desired)
		{
			try
			{
				for (int i = target.Count - 1; i >= 0; i--)
				{
					if (!desired.Contains(target[i]))
					{
						target.RemoveAt(i);
					}
				}
				for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
				{
					var item = desired[targetIndex];
					int currentIndex = target.IndexOf(item);
					if (currentIndex == -1)
					{
						target.Insert(targetIndex, item);
					}
					else if (currentIndex != targetIndex)
					{
						target.Move(currentIndex, targetIndex);
					}
				}
			}
			catch { }
		}

		private void ReconcileTopLevelOrder(System.Collections.Generic.List<ProcessModel> desired)
		{
			try
			{
				// Remove items not present anymore
				for (int i = Processes.Count - 1; i >= 0; i--)
				{
					if (!desired.Contains(Processes[i]))
					{
						Processes.RemoveAt(i);
					}
				}
				// Insert/move to match desired order
				for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
				{
					var item = desired[targetIndex];
					int currentIndex = Processes.IndexOf(item);
					if (currentIndex == -1)
					{
						Processes.Insert(targetIndex, item);
					}
					else if (currentIndex != targetIndex)
					{
						Processes.Move(currentIndex, targetIndex);
					}
				}
			}
			catch { }
		}

		private void RefreshTopProcesses()
		{
			try
			{
				// Select top 5 of current sorted Processes by the active sort key
				System.Func<ProcessModel, object?> keySelector = SortColumn switch
				{
					ProcessSortColumn.Name => m => m.Name,
					ProcessSortColumn.Cpu => m => m.CpuPercent,
					ProcessSortColumn.Ram => m => m.MemoryBytes,
					ProcessSortColumn.Gpu => m => m.GpuPercent,
					_ => m => m.CpuPercent
				};

				var ordered = SortDescending
					? Processes.OrderByDescending(keySelector).Take(5).ToArray()
					: Processes.OrderBy(keySelector).Take(5).ToArray();

				TopProcesses.Clear();
				foreach (var p in ordered)
				{
					TopProcesses.Add(p);
				}
			}
			catch { }
		}

		private static bool CanKillProcess(ProcessModel? model) => model != null;
		private void OnKillProcess(ProcessModel? model)
		{
			if (model == null) return;
			try
			{
				// Kill only this process by default; tree kill is invoked explicitly via KillProcessTree
				Process.GetProcessById(model.ProcessId).Kill();
			}
			catch { }
		}

		public void KillProcessTree(ProcessModel? model)
		{
			if (model == null) return;
			try
			{
				Process.GetProcessById(model.ProcessId).Kill(true);
			}
			catch { }
		}

		private static bool CanOpenProcessLocation(ProcessModel? model) => model != null;
		private void OnOpenProcessLocation(ProcessModel? model)
		{
			if (model == null) return;
			try
			{
				string path = string.Empty;
				try
				{
					// Prefer WMI to avoid AccessViolation when querying MainModule
					using var searcher = new ManagementObjectSearcher($"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId={model.ProcessId}");
					foreach (ManagementObject mo in searcher.Get())
					{
						try { path = mo["ExecutablePath"]?.ToString() ?? string.Empty; }
						catch { }
						finally { try { mo?.Dispose(); } catch { } }
					}
				}
				catch { }

				if (string.IsNullOrWhiteSpace(path))
				{
					try
					{
						using var p = Process.GetProcessById(model.ProcessId);
						try { path = p?.MainModule?.FileName ?? string.Empty; }
						catch (System.AccessViolationException) { path = string.Empty; }
						catch (System.ComponentModel.Win32Exception) { path = string.Empty; }
					}
					catch { }
				}

				if (!string.IsNullOrWhiteSpace(path))
				{
					var args = $"/select,\"{path}\"";
					Process.Start(new ProcessStartInfo
					{
						FileName = "explorer.exe",
						Arguments = args,
						UseShellExecute = true
					});
				}
			}
			catch { }
		}

		// Kill all instances represented by a grouped row.
		public void KillGroup(ProcessModel? group, bool killTrees)
		{
			if (group == null) return;
			try
			{
				// Only meaningful for grouped rows; but allow calling regardless.
				for (int i = 0; i < group.Children.Count; i++)
				{
					var child = group.Children[i];
					try
					{
						Process.GetProcessById(child.ProcessId).Kill(killTrees);
					}
					catch { }
				}
			}
			catch { }
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			try { _cts.Cancel(); } catch { }
			try { _updateTask?.Wait(250); } catch (OperationCanceledException) { } catch { }
		}
	}
}


