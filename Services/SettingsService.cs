using System;

namespace Bluetask.Services
{
	public enum MemoryMetric
	{
		WorkingSet,
		Private
	}
	public static class SettingsService
	{
		private static readonly object _lock = new object();
		private static bool _groupSameProcessNames = true;
		private static bool _normalizeProcessUsage = false;
		private static MemoryMetric _memoryMetric = MemoryMetric.Private;
		// Storage layout: allow two-column drive layout at 4+ drives (default off; otherwise 5+)
		private static bool _twoColumnDrivesAtFour = false;
		// Performance page: toggle CPU per-core view
		private static bool _cpuPerCoreView = false;
		// Debug emulation settings (negative disables)
		private static int _debugGpuCount = -1;
		private static int _debugDiskCount = -1;

		static SettingsService()
		{
			LoadSettings();
			try
			{
				var g = Environment.GetEnvironmentVariable("BLUETASK_DEBUG_GPU_COUNT");
				if (int.TryParse(g, out var gi)) _debugGpuCount = gi;
			}
			catch { }
			try
			{
				var d = Environment.GetEnvironmentVariable("BLUETASK_DEBUG_DISK_COUNT");
				if (int.TryParse(d, out var di)) _debugDiskCount = di;
			}
			catch { }
		}

		public static event Action<bool>? GroupSameProcessNamesChanged;
		public static event Action<bool>? NormalizeProcessUsageChanged;
		public static event Action<MemoryMetric>? MemoryMetricChanged;
        public static event Action? DebugEmulationChanged;
		public static event Action<bool>? TwoColumnDrivesAtFourChanged;
		public static event Action<bool>? CpuPerCoreViewChanged;

		private static void LoadSettings()
		{
			_groupSameProcessNames = SettingsServiceHelper.GetValue(nameof(GroupSameProcessNames), true);
			_normalizeProcessUsage = SettingsServiceHelper.GetValue(nameof(NormalizeProcessUsage), false);
			_memoryMetric = (MemoryMetric)SettingsServiceHelper.GetValue(nameof(MemoryMetric), (int)MemoryMetric.Private);
			_twoColumnDrivesAtFour = SettingsServiceHelper.GetValue(nameof(TwoColumnDrivesAtFour), false);
			_cpuPerCoreView = SettingsServiceHelper.GetValue(nameof(CpuPerCoreView), false);
			_debugGpuCount = SettingsServiceHelper.GetValue(nameof(DebugGpuCount), -1);
			_debugDiskCount = SettingsServiceHelper.GetValue(nameof(DebugDiskCount), -1);
		}

		public static bool GroupSameProcessNames
		{
			get { lock (_lock) { return _groupSameProcessNames; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_groupSameProcessNames == value) return;
					_groupSameProcessNames = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(GroupSameProcessNames), value);
					try { GroupSameProcessNamesChanged?.Invoke(value); } catch { }
				}
			}
		}

		public static bool NormalizeProcessUsage
		{
			get { lock (_lock) { return _normalizeProcessUsage; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_normalizeProcessUsage == value) return;
					_normalizeProcessUsage = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(NormalizeProcessUsage), value);
					try { NormalizeProcessUsageChanged?.Invoke(value); } catch { }
				}
			}
		}

		public static MemoryMetric MemoryMetric
		{
			get { lock (_lock) { return _memoryMetric; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_memoryMetric == value) return;
					_memoryMetric = value;
					changed = true;
				}
				if (changed)
				{
                    SettingsServiceHelper.SetValue(nameof(MemoryMetric), (int)value);
					try { MemoryMetricChanged?.Invoke(value); } catch { }
				}
			}
		}

		public static bool TwoColumnDrivesAtFour
		{
			get { lock (_lock) { return _twoColumnDrivesAtFour; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_twoColumnDrivesAtFour == value) return;
					_twoColumnDrivesAtFour = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(TwoColumnDrivesAtFour), value);
					try { TwoColumnDrivesAtFourChanged?.Invoke(value); } catch { }
				}
			}
		}

		public static int DebugGpuCount
		{
			get { lock (_lock) { return _debugGpuCount; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_debugGpuCount == value) return;
					_debugGpuCount = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(DebugGpuCount), value);
					try { DebugEmulationChanged?.Invoke(); } catch { }
				}
			}
		}

		public static int DebugDiskCount
		{
			get { lock (_lock) { return _debugDiskCount; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_debugDiskCount == value) return;
					_debugDiskCount = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(DebugDiskCount), value);
					try { DebugEmulationChanged?.Invoke(); } catch { }
				}
			}
		}

		public static bool CpuPerCoreView
		{
			get { lock (_lock) { return _cpuPerCoreView; } }
			set
			{
				bool changed;
				lock (_lock)
				{
					if (_cpuPerCoreView == value) return;
					_cpuPerCoreView = value;
					changed = true;
				}
				if (changed)
				{
					SettingsServiceHelper.SetValue(nameof(CpuPerCoreView), value);
					try { CpuPerCoreViewChanged?.Invoke(value); } catch { }
				}
			}
		}
	}
}


