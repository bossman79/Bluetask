using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bluetask.Models;

namespace Bluetask.Services
{
	/// <summary>
	/// Analyzes Windows crash dumps using WinDbg/CDB to extract detailed crash information
	/// before handing off to AI analysis.
	/// </summary>
	public sealed class WinDbgService
	{
		public static WinDbgService Shared { get; } = new WinDbgService();

		private readonly string? _cdbPath;
		private readonly List<string> _minidumpLocations;

		private WinDbgService()
		{
			// Find cdb.exe - prioritize bundled version, then fall back to system installation
			_cdbPath = FindDebugger();
			
			// Standard Windows crash dump locations
			_minidumpLocations = new List<string>
			{
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
				Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? "C:\\ProgramData", 
					"Microsoft", "Windows", "WER", "ReportQueue")
			};
		}

		public bool IsAvailable => !string.IsNullOrEmpty(_cdbPath) && File.Exists(_cdbPath);

		/// <summary>
		/// Attempts to find the most relevant crash dump for a given system event.
		/// </summary>
		public async Task<CrashDumpAnalysis?> AnalyzeCrashForEventAsync(SystemEventItem eventItem)
		{
			if (!IsAvailable || !eventItem.IsCrash)
			{
				System.Diagnostics.Debug.WriteLine($"[WinDbg] Skipping: Available={IsAvailable}, IsCrash={eventItem.IsCrash}");
				return null;
			}

			try
			{
				string? dumpFile = null;
				
				// Priority 1: Use dump file path from event data if available
				if (!string.IsNullOrWhiteSpace(eventItem.DumpFilePath))
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Event contains dump file path: {eventItem.DumpFilePath}");
					
					// Expand environment variables if present
					string expandedPath = Environment.ExpandEnvironmentVariables(eventItem.DumpFilePath);
					
					if (File.Exists(expandedPath))
					{
						dumpFile = expandedPath;
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Using dump file from event data: {dumpFile}");
					}
					else
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Dump file from event not found at: {expandedPath}");
					}
				}
				
				// Priority 2: Search for matching dump file
				if (string.IsNullOrEmpty(dumpFile))
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Searching for dump file for event: {eventItem.AppName} at {eventItem.TimeCreated}");
					dumpFile = FindMatchingDumpFile(eventItem);
				}
				
				if (string.IsNullOrEmpty(dumpFile))
				{
					System.Diagnostics.Debug.WriteLine("[WinDbg] No matching dump file found");
					return new CrashDumpAnalysis
					{
						Success = false,
						ErrorMessage = "No crash dump file found for this event. Windows may not have created a dump file for this crash."
					};
				}

				System.Diagnostics.Debug.WriteLine($"[WinDbg] Analyzing dump file: {dumpFile}");
				return await AnalyzeDumpFileAsync(dumpFile, eventItem);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[WinDbg] Exception in AnalyzeCrashForEventAsync: {ex}");
				return new CrashDumpAnalysis
				{
					Success = false,
					ErrorMessage = $"WinDbg analysis failed: {ex.Message}"
				};
			}
		}

		/// <summary>
		/// Analyzes a specific dump file using WinDbg commands.
		/// </summary>
		private async Task<CrashDumpAnalysis?> AnalyzeDumpFileAsync(string dumpPath, SystemEventItem eventItem)
		{
			if (string.IsNullOrEmpty(_cdbPath))
				return null;

			try
			{
				var analysis = new CrashDumpAnalysis
				{
					Success = true,
					DumpFilePath = dumpPath,
					DumpFileSize = new FileInfo(dumpPath).Length,
					AnalysisTimestamp = DateTimeOffset.Now
				};

				// Build WinDbg command script for automated analysis
				string commands = BuildAnalysisCommands();
				string commandFile = Path.GetTempFileName();
				await File.WriteAllTextAsync(commandFile, commands);

				try
				{
					// Run cdb.exe with the dump file
					var psi = new ProcessStartInfo
					{
						FileName = _cdbPath,
						Arguments = $"-z \"{dumpPath}\" -c \"$$<{commandFile};q\"",
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true,
						WorkingDirectory = Path.GetDirectoryName(_cdbPath)
					};

					using var process = new Process { StartInfo = psi };
					var outputBuilder = new StringBuilder();
					var errorBuilder = new StringBuilder();

					process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
					process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();

					// Timeout after 30 seconds
					if (!process.WaitForExit(30000))
					{
						try { process.Kill(); } catch { }
						return new CrashDumpAnalysis
						{
							Success = false,
							ErrorMessage = "WinDbg analysis timed out after 30 seconds"
						};
					}

					string output = outputBuilder.ToString();
					string errors = errorBuilder.ToString();

					// Parse the WinDbg output
					ParseWinDbgOutput(output, analysis);

					// If we got meaningful data, consider it successful
					if (!string.IsNullOrWhiteSpace(analysis.FaultingModule) || 
					    !string.IsNullOrWhiteSpace(analysis.StackTrace))
					{
						analysis.Success = true;
					}

					return analysis;
				}
				finally
				{
					try { File.Delete(commandFile); } catch { }
				}
			}
			catch (Exception ex)
			{
				return new CrashDumpAnalysis
				{
					Success = false,
					ErrorMessage = $"Dump analysis error: {ex.Message}"
				};
			}
		}

		/// <summary>
		/// Builds WinDbg command script for extracting crash details.
		/// </summary>
		private string BuildAnalysisCommands()
		{
			return @".sympath srv*https://msdl.microsoft.com/download/symbols
.reload
!analyze -v
.ecxr
k 30
lm
q";
		}

		/// <summary>
		/// Parses WinDbg output to extract structured crash information.
		/// </summary>
		private void ParseWinDbgOutput(string output, CrashDumpAnalysis analysis)
		{
			try
			{
				analysis.RawOutput = output;

				// Extract exception code
				var exMatch = Regex.Match(output, @"ExceptionCode:\s+(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
				if (exMatch.Success)
					analysis.ExceptionCode = exMatch.Groups[1].Value;

				// Extract faulting module
				var moduleMatch = Regex.Match(output, @"MODULE_NAME:\s+(\S+)", RegexOptions.IgnoreCase);
				if (moduleMatch.Success)
					analysis.FaultingModule = moduleMatch.Groups[1].Value;

				// Extract faulting driver
				var driverMatch = Regex.Match(output, @"IMAGE_NAME:\s+(\S+)", RegexOptions.IgnoreCase);
				if (driverMatch.Success)
					analysis.FaultingDriver = driverMatch.Groups[1].Value;

				// Extract module version
				var versionMatch = Regex.Match(output, $@"{Regex.Escape(analysis.FaultingModule ?? "")}.*?FileVersion:\s+([^\r\n]+)", RegexOptions.IgnoreCase);
				if (versionMatch.Success)
					analysis.ModuleVersion = versionMatch.Groups[1].Value.Trim();

				// Extract BUGCHECK (for kernel crashes)
				var bugcheckMatch = Regex.Match(output, @"BUGCHECK_CODE:\s+(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase);
				if (bugcheckMatch.Success)
					analysis.BugCheckCode = bugcheckMatch.Groups[1].Value;

				// Extract failure bucket ID (helpful for categorizing similar crashes)
				var bucketMatch = Regex.Match(output, @"FAILURE_BUCKET_ID:\s+([^\r\n]+)", RegexOptions.IgnoreCase);
				if (bucketMatch.Success)
					analysis.FailureBucketId = bucketMatch.Groups[1].Value.Trim();

				// Extract process name
				var processMatch = Regex.Match(output, @"PROCESS_NAME:\s+(\S+)", RegexOptions.IgnoreCase);
				if (processMatch.Success)
					analysis.ProcessName = processMatch.Groups[1].Value;

				// Extract stack trace
				var stackMatch = Regex.Match(output, @"STACK_TEXT:(.*?)(?=\r?\n\r?\n[A-Z_]+:|$)", 
					RegexOptions.Singleline | RegexOptions.IgnoreCase);
				if (stackMatch.Success)
				{
					analysis.StackTrace = stackMatch.Groups[1].Value.Trim();
				}
				else
				{
					// Fallback: try to capture after "ChildEBP RetAddr" or call stack header
					var altStackMatch = Regex.Match(output, @"(?:ChildEBP RetAddr|Call Site)(.*?)(?=\r?\n\r?\n|$)", 
						RegexOptions.Singleline | RegexOptions.IgnoreCase);
					if (altStackMatch.Success)
						analysis.StackTrace = altStackMatch.Groups[1].Value.Trim();
				}

				// Extract probable cause
				var causeMatch = Regex.Match(output, @"FOLLOWUP_NAME:\s+([^\r\n]+)", RegexOptions.IgnoreCase);
				if (causeMatch.Success)
					analysis.ProbableCause = causeMatch.Groups[1].Value.Trim();

				// Extract default analysis
				var defaultMatch = Regex.Match(output, @"DEFAULT_BUCKET_ID:\s+([^\r\n]+)", RegexOptions.IgnoreCase);
				if (defaultMatch.Success)
					analysis.DefaultBucketId = defaultMatch.Groups[1].Value.Trim();

				// Extract symbol problems (if any)
				if (output.Contains("symbols can be loaded", StringComparison.OrdinalIgnoreCase))
					analysis.SymbolProblems = true;

			}
			catch (Exception ex)
			{
				// Don't fail the entire analysis if parsing fails
				analysis.ErrorMessage = $"Parsing error: {ex.Message}";
			}
		}

		/// <summary>
		/// Finds a dump file matching the event's timestamp and application name.
		/// </summary>
		private string? FindMatchingDumpFile(SystemEventItem eventItem)
		{
			try
			{
				var candidateFiles = new List<(string path, DateTime modified)>();

				// Search all minidump locations
				System.Diagnostics.Debug.WriteLine($"[WinDbg] Searching {_minidumpLocations.Count} dump locations...");
				
				foreach (var location in _minidumpLocations)
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Checking location: {location}");
					
					if (!Directory.Exists(location))
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg]   Location does not exist");
						continue;
					}

					try
					{
						// Look for .dmp files
						var files = Directory.GetFiles(location, "*.dmp", SearchOption.AllDirectories);
						System.Diagnostics.Debug.WriteLine($"[WinDbg]   Found {files.Length} .dmp files");
						
						foreach (var file in files)
						{
							try
							{
								var fileInfo = new FileInfo(file);
								// Consider files within a reasonable time window
								// For recent events: 5 minutes, for older events: up to 24 hours
								var timeDiff = Math.Abs((fileInfo.LastWriteTime - eventItem.TimeCreated.LocalDateTime).TotalMinutes);
								var eventAge = (DateTimeOffset.Now - eventItem.TimeCreated).TotalHours;
								
								// Dynamic time window: 5 min for recent, 60 min for same-day, 24 hours for older
								double timeWindowMinutes = eventAge < 1 ? 5 : (eventAge < 24 ? 60 : 1440);
								
								System.Diagnostics.Debug.WriteLine($"[WinDbg]   Checking: {Path.GetFileName(file)} (Modified: {fileInfo.LastWriteTime}, TimeDiff: {timeDiff:F1} min, Window: {timeWindowMinutes} min)");
								
								if (timeDiff <= timeWindowMinutes)
								{
									candidateFiles.Add((file, fileInfo.LastWriteTime));
									System.Diagnostics.Debug.WriteLine($"[WinDbg]     → Added as candidate (within {timeWindowMinutes} min window)");
								}
							}
							catch (Exception ex)
							{
								System.Diagnostics.Debug.WriteLine($"[WinDbg]   Error checking file: {ex.Message}");
							}
						}
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg]   Error accessing location: {ex.Message}");
					}
				}

				System.Diagnostics.Debug.WriteLine($"[WinDbg] Total candidates found: {candidateFiles.Count}");
				
				if (candidateFiles.Count == 0)
					return null;

				// If we have app name, try to match by filename
				if (!string.IsNullOrWhiteSpace(eventItem.AppName))
				{
					string appNameLower = eventItem.AppName.ToLowerInvariant().Replace(".exe", "").Replace(".dll", "");
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Looking for app name match: '{appNameLower}'");
					
					var matchByName = candidateFiles
						.Where(f => Path.GetFileName(f.path).ToLowerInvariant().Contains(appNameLower))
						.OrderByDescending(f => f.modified)
						.FirstOrDefault();
					
					if (!string.IsNullOrEmpty(matchByName.path))
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Matched by app name: {matchByName.path}");
						return matchByName.path;
					}
					else
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg] No app name match found for '{appNameLower}'. Available dumps:");
						foreach (var cf in candidateFiles)
						{
							System.Diagnostics.Debug.WriteLine($"[WinDbg]   - {Path.GetFileName(cf.path)}");
						}
					}
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] No app name available in event (AppName is null/empty)");
				}

				// Otherwise, return the most recent dump file near the event time
				try
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Falling back to timestamp matching with {candidateFiles.Count} candidates");
					var closestMatch = candidateFiles
						.OrderBy(f => Math.Abs((f.modified - eventItem.TimeCreated.LocalDateTime).TotalSeconds))
						.First();
					
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Matched by timestamp: {closestMatch.path}");
					return closestMatch.path;
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Exception in timestamp matching: {ex.Message}");
					// If timestamp matching fails, just return the first candidate
					if (candidateFiles.Count > 0)
					{
						var fallback = candidateFiles[0].path;
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Using first candidate as fallback: {fallback}");
						return fallback;
					}
					return null;
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[WinDbg] Exception in FindMatchingDumpFile: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Attempts to locate cdb.exe - first in bundled tools, then system installation.
		/// </summary>
		private string? FindDebugger()
		{
			try
			{
				// Get application directory
				string appDir = AppContext.BaseDirectory;
				
				// Priority 1: Bundled debugging tools (ships with the app)
				var bundledPaths = new List<string>
				{
					Path.Combine(appDir, "DebugTools", "x64", "cdb.exe"),
					Path.Combine(appDir, "DebugTools", "arm64", "cdb.exe"),
					Path.Combine(appDir, "DebugTools", "cdb.exe"),
					Path.Combine(appDir, "cdb.exe")
				};

				foreach (var path in bundledPaths)
				{
					System.Diagnostics.Debug.WriteLine($"[WinDbg] Checking bundled: {path} - Exists: {File.Exists(path)}");
					if (File.Exists(path))
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Using bundled debugger: {path}");
						return path;
					}
				}

				// Priority 2: System-installed Windows SDK (fallback)
				var systemPaths = new List<string>
				{
					@"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe",
					@"C:\Program Files (x86)\Windows Kits\10\Debuggers\arm64\cdb.exe",
					@"C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe",
					@"C:\Program Files (x86)\Windows Kits\8.1\Debuggers\x64\cdb.exe",
					FindInPath("cdb.exe"),
					FindInPath("windbg.exe")
				};

				foreach (var path in systemPaths)
				{
					if (!string.IsNullOrEmpty(path) && File.Exists(path))
					{
						System.Diagnostics.Debug.WriteLine($"[WinDbg] Using system debugger: {path}");
						return path;
					}
				}

				System.Diagnostics.Debug.WriteLine("[WinDbg] No debugger found");
				return null;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[WinDbg] Error finding debugger: {ex.Message}");
				return null;
			}
		}

		private string? FindInPath(string executable)
		{
			try
			{
				var pathVar = Environment.GetEnvironmentVariable("PATH");
				if (string.IsNullOrEmpty(pathVar))
					return null;

				var paths = pathVar.Split(Path.PathSeparator);
				foreach (var path in paths)
				{
					try
					{
						var fullPath = Path.Combine(path, executable);
						if (File.Exists(fullPath))
							return fullPath;
					}
					catch { }
				}
			}
			catch { }

			return null;
		}

		/// <summary>
		/// Gets a summary of available crash dumps in the system.
		/// </summary>
		public List<string> GetAvailableDumpFiles()
		{
			var dumps = new List<string>();
			
			foreach (var location in _minidumpLocations)
			{
				if (!Directory.Exists(location))
					continue;

				try
				{
					var files = Directory.GetFiles(location, "*.dmp", SearchOption.AllDirectories);
					dumps.AddRange(files);
				}
				catch { }
			}

			return dumps.OrderByDescending(f => new FileInfo(f).LastWriteTime).ToList();
		}
	}

	/// <summary>
	/// Results from WinDbg crash dump analysis.
	/// </summary>
	public sealed class CrashDumpAnalysis
	{
		public bool Success { get; set; }
		public string? ErrorMessage { get; set; }
		
		// File information
		public string DumpFilePath { get; set; } = string.Empty;
		public long DumpFileSize { get; set; }
		public DateTimeOffset AnalysisTimestamp { get; set; }
		
		// Extracted crash details
		public string? ExceptionCode { get; set; }
		public string? FaultingModule { get; set; }
		public string? FaultingDriver { get; set; }
		public string? ModuleVersion { get; set; }
		public string? BugCheckCode { get; set; }
		public string? FailureBucketId { get; set; }
		public string? ProcessName { get; set; }
		public string? StackTrace { get; set; }
		public string? ProbableCause { get; set; }
		public string? DefaultBucketId { get; set; }
		public bool SymbolProblems { get; set; }
		
		// Raw output for detailed inspection
		public string RawOutput { get; set; } = string.Empty;

		/// <summary>
		/// Formats the analysis into a human-readable summary for DeepSeek.
		/// </summary>
		public string FormatForAI()
		{
			if (!Success)
				return $"WinDbg analysis failed: {ErrorMessage}";

			var sb = new StringBuilder();
			sb.AppendLine("## WinDbg CRASH DUMP ANALYSIS");
			sb.AppendLine();
			sb.AppendLine($"**Dump File:** `{Path.GetFileName(DumpFilePath)}`");
			sb.AppendLine($"**Size:** {DumpFileSize / 1024.0:F2} KB");
			sb.AppendLine($"**Analysis Time:** {AnalysisTimestamp:yyyy-MM-dd HH:mm:ss}");
			sb.AppendLine();

			if (!string.IsNullOrWhiteSpace(ExceptionCode))
				sb.AppendLine($"**Exception Code:** `{ExceptionCode}`");

			if (!string.IsNullOrWhiteSpace(BugCheckCode))
				sb.AppendLine($"**Bug Check Code:** `{BugCheckCode}`");

			if (!string.IsNullOrWhiteSpace(FaultingModule))
				sb.AppendLine($"**Faulting Module:** `{FaultingModule}`");

			if (!string.IsNullOrWhiteSpace(FaultingDriver))
				sb.AppendLine($"**Faulting Driver:** `{FaultingDriver}`");

			if (!string.IsNullOrWhiteSpace(ModuleVersion))
				sb.AppendLine($"**Module Version:** {ModuleVersion}");

			if (!string.IsNullOrWhiteSpace(ProcessName))
				sb.AppendLine($"**Process Name:** `{ProcessName}`");

			if (!string.IsNullOrWhiteSpace(FailureBucketId))
				sb.AppendLine($"**Failure Bucket ID:** `{FailureBucketId}` _(helps identify similar crashes)_");

			if (!string.IsNullOrWhiteSpace(DefaultBucketId))
				sb.AppendLine($"**Default Bucket:** `{DefaultBucketId}`");

			if (!string.IsNullOrWhiteSpace(ProbableCause))
				sb.AppendLine($"**Probable Cause:** {ProbableCause}");

			if (SymbolProblems)
				sb.AppendLine("⚠️ **Symbol Loading Issues:** Some symbols could not be loaded. Analysis may be incomplete.");

			sb.AppendLine();
			
			if (!string.IsNullOrWhiteSpace(StackTrace))
			{
				sb.AppendLine("### CALL STACK");
				sb.AppendLine("```");
				// Limit stack trace to first 30 lines to avoid token bloat
				var stackLines = StackTrace.Split('\n').Take(30);
				sb.AppendLine(string.Join("\n", stackLines));
				if (StackTrace.Split('\n').Length > 30)
					sb.AppendLine("... (truncated)");
				sb.AppendLine("```");
				sb.AppendLine();
			}

			sb.AppendLine("**This is detailed crash forensics data from the actual memory dump. Use this to provide SPECIFIC root cause analysis.**");

			return sb.ToString();
		}
	}
}


