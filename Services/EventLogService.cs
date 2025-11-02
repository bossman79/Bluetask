using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using Bluetask.Models;

namespace Bluetask.Services
{
	public sealed class EventLogService
	{
		public static EventLogService Shared { get; } = new EventLogService();

		private EventLogService() { }

		public IEnumerable<SystemEventItem> GetRecentEvents(int maxPerLog = 200)
		{
			var results = new List<SystemEventItem>(maxPerLog * 2);
			string[] logs = new[] { "Application", "System" };

			foreach (var log in logs)
			{
				// Check if log exists before attempting to read
				if (!EventLogExists(log))
					continue;

				EventLogQuery query = new EventLogQuery(log, PathType.LogName, "*[*]");
				query.ReverseDirection = true; // newest first
				try
				{
					using var reader = new EventLogReader(query);
					int count = 0;
					for (EventRecord? rec = reader.ReadEvent(); rec != null && count < maxPerLog; rec = reader.ReadEvent())
					{
						SystemEventItem item = ConvertRecord(rec, log);
						results.Add(item);
						count++;
					}
				}
				catch (EventLogNotFoundException)
				{
					// Log doesn't exist or is not accessible - silently skip
				}
				catch (UnauthorizedAccessException)
				{
					// No permission to access this log - silently skip
				}
				catch
				{
					// Other errors - skip this log
				}
			}

			return results;
		}

		private static bool EventLogExists(string logName)
		{
			try
			{
				// Quick check if log exists without throwing exceptions
				using var session = new EventLogSession();
				var logInfo = session.GetLogInformation(logName, PathType.LogName);
				return logInfo != null;
			}
			catch (EventLogNotFoundException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
			catch
			{
				return false;
			}
		}

		public static EventSeverity MapLevel(byte? level, int? task)
		{
			try
			{
				if (level == null) return EventSeverity.Unknown;
				switch (level)
				{
					case 1: return EventSeverity.Critical;
					case 2: return EventSeverity.Error;
					case 3: return EventSeverity.Warning;
					case 4: return EventSeverity.Info;
					case 5:
					case 0: return EventSeverity.Verbose;
				}
			}
			catch { }
			return EventSeverity.Unknown;
		}

		private static SystemEventItem ConvertRecord(EventRecord rec, string logName)
		{
			var item = new SystemEventItem();
			try { item.RecordId = rec.RecordId ?? 0; } catch { }
			try { item.EventId = rec.Id; } catch { }
			try { item.TimeCreated = rec.TimeCreated ?? DateTimeOffset.Now; } catch { }
			try { item.ProviderName = rec.ProviderName ?? string.Empty; } catch { }
			try { item.Severity = MapLevel((byte?)rec.Level, rec.Task); } catch { }
			try { item.Message = rec.FormatDescription() ?? string.Empty; } catch { item.Message = string.Empty; }
			item.LogName = logName;
			try { item.TaskDisplayName = rec.TaskDisplayName; } catch { }
			try { item.OpcodeDisplayName = rec.OpcodeDisplayName; } catch { }
			// Heuristic crash detection: app hangs, app faults, service terminated unexpectedly, WER reports, .NET unhandled exceptions
			try
			{
				string src = (item.ProviderName ?? string.Empty).ToLowerInvariant();
				string msg = (item.Message ?? string.Empty).ToLowerInvariant();
				int id = item.EventId;
				// Strict signal terms for crashes/hangs
				bool isAppHang = src.Contains("application hang") || msg.Contains("stopped responding") || msg.Contains("application stopped responding");
				bool isAppError = src.Contains("application error") && (msg.Contains("faulting module") || msg.Contains("exception code") || msg.Contains("stopped working") || msg.Contains("crash"));
				bool isWer = src.Contains("windows error reporting") && (msg.Contains("crash") || msg.Contains("stopped working"));
				bool isSvcCrash = (src.Contains("service control manager") && (msg.Contains("terminated unexpectedly") || msg.Contains("terminated with the following error")));
				bool isDotNetUnhandled = msg.Contains("unhandled exception");
				bool isDwm = (src.Contains("dwm") || src.Contains("desktop window manager")) && (msg.Contains("stopped working") || msg.Contains("crash"));
				// Event IDs commonly tied to app/service crashes or hangs
				bool byId = id == 1000 /*App Error*/ || id == 1001 /*WER*/ || id == 1002 /*App Hang*/ || id == 7031 /*Service terminated unexpectedly*/ || id == 7034 /*Service terminated*/ || id == 1005 /*App Error: Access*/ || id == 1009 /*App Error*/;
				item.IsCrash = isAppHang || isAppError || isWer || isSvcCrash || isDotNetUnhandled || isDwm || byId;
			}
			catch { item.IsCrash = false; }

			// Parse common fields for App Error/App Hang messages
			try
			{
				var msg = item.Message ?? string.Empty;
				// Faulting application name: X, version: ...\nFaulting module name: Y, ...\nException code: 0x...
				int ia = msg.IndexOf("Faulting application name:", StringComparison.OrdinalIgnoreCase);
				if (ia >= 0)
				{
					var rest = msg.Substring(ia + 26).Trim();
					var comma = rest.IndexOf(',');
					if (comma > 0) item.AppName = rest.Substring(0, comma).Trim(); else item.AppName = rest;
				}
				int im = msg.IndexOf("Faulting module name:", StringComparison.OrdinalIgnoreCase);
				if (im >= 0)
				{
					var rest = msg.Substring(im + 23).Trim();
					var comma = rest.IndexOf(',');
					if (comma > 0) item.ModuleName = rest.Substring(0, comma).Trim(); else item.ModuleName = rest;
				}
				int ie = msg.IndexOf("Exception code:", StringComparison.OrdinalIgnoreCase);
				if (ie >= 0)
				{
					var rest = msg.Substring(ie + 15).Trim();
					int sep = -1;
					for (int i = 0; i < rest.Length; i++)
					{
						char ch = rest[i];
						if (char.IsWhiteSpace(ch) || ch == ',') { sep = i; break; }
					}
					item.ExceptionCode = (sep > 0 ? rest.Substring(0, sep) : rest).Trim();
				}
				
				// Extract dump file path if present (common patterns)
				var dumpPatterns = new[] 
				{
					@"([A-Z]:\\[^:\r\n]*\.dmp)",  // Full path: C:\Users\...\file.dmp
					@"(\\[^:\r\n]*\\CrashDumps\\[^:\r\n]*\.dmp)",  // Relative path with CrashDumps
					@"Report Id:\s*([0-9a-f\-]+)"  // Report ID (we'll construct path from this)
				};
				
				foreach (var pattern in dumpPatterns)
				{
					var match = System.Text.RegularExpressions.Regex.Match(msg, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
					if (match.Success)
					{
						string extractedPath = match.Groups[1].Value;
						
						// If it's a relative path, try to construct full path
						if (extractedPath.StartsWith("\\") && extractedPath.Contains("CrashDumps"))
						{
							// Try common root paths
							var roots = new[] 
							{
								Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
								Environment.GetEnvironmentVariable("USERPROFILE")
							};
							
							foreach (var root in roots)
							{
								if (!string.IsNullOrEmpty(root))
								{
									string fullPath = root + extractedPath;
									if (System.IO.File.Exists(fullPath))
									{
										item.DumpFilePath = fullPath;
										break;
									}
								}
							}
						}
						else if (!extractedPath.Contains("\\") && extractedPath.Contains("-"))
						{
							// It's a Report ID - construct WER path
							string werPath = System.IO.Path.Combine(
								Environment.GetEnvironmentVariable("ProgramData") ?? "C:\\ProgramData",
								"Microsoft", "Windows", "WER", "ReportQueue",
								$"AppCrash_{extractedPath}"
							);
							if (System.IO.Directory.Exists(werPath))
							{
								var dmpFiles = System.IO.Directory.GetFiles(werPath, "*.dmp");
								if (dmpFiles.Length > 0)
									item.DumpFilePath = dmpFiles[0];
							}
						}
						else
						{
							// It's already a full path
							item.DumpFilePath = extractedPath;
						}
						
						if (!string.IsNullOrEmpty(item.DumpFilePath))
							break;
					}
				}
			}
			catch { }

			// Knowledge base classification and enrichment
			try
			{
				var match = KnowledgeService.Shared.Classify(item);
				if (match != null)
				{
					item.KnowledgeId = match.Rule.Id;
					item.KnowledgeTitle = match.Rule.Title;
					item.KnowledgeSummary = match.Rule.Summary;
					try { item.Guidance = new System.Collections.Generic.List<string>(match.Rule.Guidance ?? Array.Empty<string>()); } catch { }
					if (string.Equals(match.Rule.Severity, "Crash", StringComparison.OrdinalIgnoreCase))
					{
						item.IsCrash = true;
					}
				}
			}
			catch { }
			return item;
		}
	}
}



