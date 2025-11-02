using System;
using System.Collections.Generic;

namespace Bluetask.Models
{
	public sealed class KnowledgeRule
	{
		public string Id { get; set; } = string.Empty; // e.g., "APPCRASH_1000"
		public string Title { get; set; } = string.Empty; // Human-friendly, e.g., "Application crashed"
		public string Summary { get; set; } = string.Empty; // One-line explanation
		public string[] Guidance { get; set; } = Array.Empty<string>(); // Steps to resolve
		public string Severity { get; set; } = "Info"; // Info/Warning/Critical/Crash
		public string[] MatchProviders { get; set; } = Array.Empty<string>(); // e.g., "Application Error"
		public int[] MatchEventIds { get; set; } = Array.Empty<int>(); // e.g., 1000
		public string[] IncludeContains { get; set; } = Array.Empty<string>(); // message contains (case-insensitive)
		public string[] ExcludeContains { get; set; } = Array.Empty<string>(); // negative filters
	}

	public sealed class KnowledgeMatch
	{
		public KnowledgeRule Rule { get; set; } = new KnowledgeRule();
		public double Score { get; set; }
	}
}




