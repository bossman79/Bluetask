using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Bluetask.Services
{
	internal static class NativeProcess
	{
		private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		private const uint PROCESS_VM_READ = 0x0010;

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr hObject);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

		[DllImport("psapi.dll", SetLastError = true)]
		private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

		[StructLayout(LayoutKind.Sequential)]
		private struct PROCESS_MEMORY_COUNTERS
		{
			public uint cb;
			public uint PageFaultCount;
			public UIntPtr PeakWorkingSetSize;
			public UIntPtr WorkingSetSize;
			public UIntPtr QuotaPeakPagedPoolUsage;
			public UIntPtr QuotaPagedPoolUsage;
			public UIntPtr QuotaPeakNonPagedPoolUsage;
			public UIntPtr QuotaNonPagedPoolUsage;
			public UIntPtr PagefileUsage;
			public UIntPtr PeakPagefileUsage;
		}

		public static bool TryReadTimesAndMemory(int pid, out TimeSpan totalTime, out ulong workingSet)
		{
			totalTime = TimeSpan.Zero;
			workingSet = 0;
			IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
			if (h == IntPtr.Zero)
				return false;
			try
			{
				if (!GetProcessTimes(h, out var _, out var _, out var kernel, out var user))
					return false;
				var memOk = GetProcessMemoryInfo(h, out var mem, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>());
				workingSet = memOk ? mem.WorkingSetSize.ToUInt64() : 0UL;
				totalTime = TimeSpan.FromTicks(kernel + user);
				return true;
			}
			finally
			{
				CloseHandle(h);
			}
		}

		public static string GetProcessNameSafe(int pid)
		{
			IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
			if (h == IntPtr.Zero) return $"pid_{pid}.exe";
			try
			{
				var sb = new StringBuilder(512);
				int len = sb.Capacity;
				if (QueryFullProcessImageNameW(h, 0, sb, ref len))
				{
					try
					{
						var path = sb.ToString(0, len);
						return System.IO.Path.GetFileName(path);
					}
					catch { return $"pid_{pid}.exe"; }
				}
				return $"pid_{pid}.exe";
			}
			finally { CloseHandle(h); }
		}
	}
}




