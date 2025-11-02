using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Bluetask.Models
{
	public enum EventSeverity
	{
		Unknown = 0,
		Critical = 1,
		Error = 2,
		Warning = 3,
		Info = 4,
		Verbose = 5
	}

	public sealed class SystemEventItem
	{
		public long RecordId { get; set; }
		public int EventId { get; set; }
		public string LogName { get; set; } = string.Empty;
		public string ProviderName { get; set; } = string.Empty;
		public EventSeverity Severity { get; set; } = EventSeverity.Unknown;
		public DateTimeOffset TimeCreated { get; set; }
		public string Message { get; set; } = string.Empty;
		public string? TaskDisplayName { get; set; }
		public string? OpcodeDisplayName { get; set; }
		public bool IsCrash { get; set; }
		// Knowledge base enrichment
		public string? KnowledgeId { get; set; }
		public string? KnowledgeTitle { get; set; }
		public string? KnowledgeSummary { get; set; }
		public System.Collections.Generic.List<string> Guidance { get; set; } = new System.Collections.Generic.List<string>();
		// Parsed details for specific event types
		public string? AppName { get; set; }
		public string? ModuleName { get; set; }
		public string? ExceptionCode { get; set; }
		public string? AppPath { get; set; }
		public string? DumpFilePath { get; set; }  // Extracted from event data if available
		// WinDbg crash dump analysis (populated on-demand for crashes)
		public Bluetask.Services.CrashDumpAnalysis? WinDbgAnalysis { get; set; }

		public bool HasGuidance => Guidance != null && Guidance.Count > 0;

		public string DisplayTitle
		{
			get
			{
				var k = KnowledgeTitle;
				if (!string.IsNullOrWhiteSpace(k)) return k!;
				// Prefer concise app-centric titles for crashes/hangs
				if (IsCrash && !string.IsNullOrWhiteSpace(AppName)) return AppName + " crashed";
				// 1002 = App Hang
				try
				{
					if (EventId == 1002 && !string.IsNullOrWhiteSpace(AppName)) return AppName + " stopped responding";
				}
				catch { }
				return Title;
			}
		}

		public string ConciseSummary
		{
			get
			{
				// Build a compact reason line where possible
				if (!string.IsNullOrWhiteSpace(ModuleName) && !string.IsNullOrWhiteSpace(ExceptionCode))
				{
					return $"Faulting module {ModuleName}, exception {ExceptionCode}";
				}
				if (!string.IsNullOrWhiteSpace(KnowledgeSummary)) return KnowledgeSummary!;
				// Fallback: first sentence of raw message
				var msg = Message ?? string.Empty;
				int idx = msg.IndexOf('.') + 1;
				if (idx > 0 && idx <= msg.Length) return msg.Substring(0, idx).Trim();
				return msg.Length > 140 ? msg.Substring(0, 140) + "…" : msg;
			}
		}

		public string Title
		{
			get
			{
				var msg = Message ?? string.Empty;
				if (string.IsNullOrWhiteSpace(msg)) return $"Event {EventId}";
				var nl = msg.IndexOf('\n');
				if (nl >= 0) msg = msg.Substring(0, nl);
				return msg.Trim();
			}
		}

		public string RelativeTime
		{
			get
			{
				try
				{
					var span = DateTimeOffset.Now - TimeCreated;
					if (span.TotalSeconds < 60) return "just now";
					if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
					if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
					return $"{(int)span.TotalDays}d ago";
				}
				catch { return string.Empty; }
			}
		}

		public string CategoryDisplay => LogName ?? string.Empty;

		public string ReasonDisplay
		{
			get
			{
				if (!string.IsNullOrWhiteSpace(TaskDisplayName)) return TaskDisplayName!;
				if (!string.IsNullOrWhiteSpace(OpcodeDisplayName)) return OpcodeDisplayName!;
				return string.Empty;
			}
		}

		public string DetailsLine
		{
			get
			{
				try
				{
					var parts = new System.Collections.Generic.List<string>();
					parts.Add($"Event ID {EventId}");
					if (!string.IsNullOrWhiteSpace(CategoryDisplay)) parts.Add($"{CategoryDisplay} Category");
					if (!string.IsNullOrWhiteSpace(ReasonDisplay)) parts.Add(ReasonDisplay);
					return string.Join(" • ", parts);
				}
				catch { return string.Empty; }
			}
		}

		public Brush SeverityBrush
		{
			get
			{
				// Accent colors; crashes always use critical red regardless of mapped level
				if (IsCrash)
				{
					return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF4, 0x43, 0x36));
				}
				// Accent colors roughly matching the app palette
				return Severity switch
				{
					EventSeverity.Critical => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF4, 0x43, 0x36)),
					EventSeverity.Error => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x57, 0x22)),
					EventSeverity.Warning => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xB0, 0x30)),
					EventSeverity.Info => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x66, 0xCC, 0xFF)),
					_ => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x99, 0x99, 0x99))
				};
			}
		}

		public string DisplayType => IsCrash ? "Crash" : (Severity switch
		{
			EventSeverity.Critical => "Critical",
			EventSeverity.Error => "Error",
			EventSeverity.Warning => "Warning",
			EventSeverity.Info => "Info",
			_ => "Event"
		});
	}
}



