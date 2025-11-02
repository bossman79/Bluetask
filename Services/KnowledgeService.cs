using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bluetask.Models;

namespace Bluetask.Services
{
	public sealed class KnowledgeService
	{
		public static KnowledgeService Shared { get; } = new KnowledgeService();

		private readonly List<KnowledgeRule> _rules = new List<KnowledgeRule>();

		private KnowledgeService()
		{
			LoadRules();
		}

		private void LoadRules()
		{
			try
			{
				// Load embedded default JSON from disk if present; otherwise use built-in defaults
				var baseDir = AppContext.BaseDirectory;
				var path = Path.Combine(baseDir, "Program", "knowledge.rules.json");
				if (File.Exists(path))
				{
					var json = File.ReadAllText(path);
					var rules = JsonSerializer.Deserialize<List<KnowledgeRule>>(json, new JsonSerializerOptions
					{
						ReadCommentHandling = JsonCommentHandling.Skip,
						AllowTrailingCommas = true,
						PropertyNameCaseInsensitive = true
					});
					if (rules != null) _rules.AddRange(rules);
				}
				else
				{
					_rules.AddRange(GetBuiltInRules());
				}
			}
			catch { }
		}

		private static IEnumerable<KnowledgeRule> GetBuiltInRules()
		{
				return new[]
			{
				new KnowledgeRule
				{
					Id = "APPCRASH_1000",
					Title = "Application crashed",
					Summary = "Windows reported an application crash (Event ID 1000).",
					Severity = "Crash",
					MatchProviders = new [] { "Application Error" },
					MatchEventIds = new [] { 1000 },
					IncludeContains = new [] { "faulting module", "exception code", "stopped working" },
					Guidance = new []
					{
						"Check Reliability Monitor for patterns and recent driver/app updates.",
						"Update or reinstall the affected application.",
						"Scan system files: 'sfc /scannow' and 'DISM /Online /Cleanup-Image /RestoreHealth'."
					}
				},
				new KnowledgeRule
				{
					Id = "APPHANG_1002",
					Title = "Application not responding (hang)",
					Summary = "Windows detected an application hang (Event ID 1002).",
					Severity = "Crash",
					MatchProviders = new [] { "Application Hang" },
					MatchEventIds = new [] { 1002 },
					IncludeContains = new [] { "stopped responding" },
					Guidance = new []
					{
						"Check for add-ins or plugins causing deadlocks.",
						"Review GPU driver and overlay tools (Discord/GeForce Experience) that hook into apps."
					}
				},
				new KnowledgeRule
				{
					Id = "SERVICE_UNEXPECTED_7031",
					Title = "Service terminated unexpectedly",
					Summary = "A Windows service terminated unexpectedly (Event ID 7031).",
					Severity = "Crash",
					MatchProviders = new [] { "Service Control Manager" },
					MatchEventIds = new [] { 7031, 7034 },
					Guidance = new []
					{
						"Check service recovery options (restart on failure).",
						"Open Services.msc to view the failing service and its dependencies."
					}
				},
				new KnowledgeRule
				{
					Id = "WER_1001",
					Title = "Windows Error Reporting captured a crash",
					Summary = "WER collected a crash report (Event ID 1001).",
					Severity = "Crash",
					MatchProviders = new [] { "Windows Error Reporting" },
					MatchEventIds = new [] { 1001 },
					Guidance = new [] { "Open Reliability Monitor to view crash details and solutions." }
				}
				,
				// Guard: events that should never be treated as crashes
				new KnowledgeRule
				{
					Id = "SCM_7040_NONCRASH",
					Title = "Service configuration change",
					Summary = "A service start type or configuration changed.",
					Severity = "Info",
					MatchProviders = new [] { "Service Control Manager" },
					MatchEventIds = new [] { 7040, 7045 },
					IncludeContains = new [] { "start type" , "installed" }
				}
			};
		}

		public KnowledgeMatch? Classify(SystemEventItem ev)
		{
			try
			{
				string provider = (ev?.ProviderName ?? string.Empty).Trim();
				int id = ev?.EventId ?? 0;
				string msg = (ev?.Message ?? string.Empty);
				string lowMsg = msg.ToLowerInvariant();

				KnowledgeMatch? best = null;
				foreach (var r in _rules)
				{
					int score = 0;
					if (r.MatchProviders.Length == 0 || r.MatchProviders.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase))) score += 3;
					if (r.MatchEventIds.Length == 0 || r.MatchEventIds.Contains(id)) score += 3;
					if (r.IncludeContains.Length > 0)
					{
						int matched = 0;
						for (int i = 0; i < r.IncludeContains.Length; i++) if (lowMsg.Contains(r.IncludeContains[i].ToLowerInvariant())) matched++;
						score += matched * 2;
					}
					bool excluded = false;
					for (int i = 0; i < r.ExcludeContains.Length; i++) if (lowMsg.Contains(r.ExcludeContains[i].ToLowerInvariant())) { excluded = true; break; }
					if (excluded) continue;

					if (score <= 0) continue;
					var km = new KnowledgeMatch { Rule = r, Score = score };
					if (best == null || km.Score > best.Score) best = km;
				}

				return best;
			}
			catch { return null; }
		}
	}
}



