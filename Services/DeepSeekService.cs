using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Bluetask.Models;

namespace Bluetask.Services
{
	public sealed class DeepSeekService
	{
		public static DeepSeekService Shared { get; } = new DeepSeekService();

		private const string ApiKey = "sk-d8d2340a4d674117ba511097079dcc15";
		private const string BaseUrl = "https://api.deepseek.com/chat/completions";

		private readonly HttpClient _httpClient;

		private DeepSeekService()
		{
			_httpClient = new HttpClient();
			_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
		}

		public async Task<EventAnalysisResult> AnalyzeEventAsync(SystemEventItem eventItem)
		{
			try
			{
				// Build comprehensive prompt with all available event details
				string prompt = BuildAnalysisPrompt(eventItem);

				var request = new DeepSeekRequest
				{
					Model = "deepseek-reasoner",
					Messages = new[]
					{
						new DeepSeekMessage
						{
							Role = "system",
							Content = "You are an expert Windows system diagnostics specialist with deep knowledge of event logs, driver issues, system stability, and troubleshooting. Your job is to analyze system events and provide clear, actionable guidance to users. Always research known issues and provide specific, tested solutions. Be thorough but concise."
						},
						new DeepSeekMessage
						{
							Role = "user",
							Content = prompt
						}
					},
					Temperature = 0.7,
					MaxTokens = 4000
				};

				var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
					DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
				});

				var content = new StringContent(json, Encoding.UTF8, "application/json");
				var response = await _httpClient.PostAsync(BaseUrl, content);
				response.EnsureSuccessStatusCode();

				var responseJson = await response.Content.ReadAsStringAsync();
				var deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(responseJson, new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
					PropertyNameCaseInsensitive = true
				});

				if (deepSeekResponse?.Choices != null && deepSeekResponse.Choices.Length > 0)
				{
					string analysisText = deepSeekResponse.Choices[0].Message?.Content ?? "No analysis generated.";
					return ParseAnalysisResponse(analysisText, eventItem);
				}

				return new EventAnalysisResult
				{
					Success = false,
					ErrorMessage = "No response from AI service."
				};
			}
			catch (Exception ex)
			{
				return new EventAnalysisResult
				{
					Success = false,
					ErrorMessage = $"Analysis failed: {ex.Message}"
				};
			}
		}

		private string BuildAnalysisPrompt(SystemEventItem ev)
		{
			var sb = new StringBuilder();
			sb.AppendLine("# URGENT: System Event Analysis Request");
			sb.AppendLine();
			sb.AppendLine("## EVENT DETAILS");
			sb.AppendLine($"**Event ID:** {ev.EventId}");
			sb.AppendLine($"**Event Type:** {ev.DisplayType}");
			sb.AppendLine($"**Is Crash:** {(ev.IsCrash ? "YES - This is a system crash or critical failure" : "No")}");
			sb.AppendLine($"**Log Source:** {ev.LogName}");
			sb.AppendLine($"**Provider:** {ev.ProviderName}");
			sb.AppendLine($"**Time Occurred:** {ev.TimeCreated:yyyy-MM-dd HH:mm:ss} ({ev.RelativeTime})");
			sb.AppendLine($"**Severity Level:** {ev.Severity}");
			
			if (!string.IsNullOrWhiteSpace(ev.AppName))
				sb.AppendLine($"**Faulting Application:** {ev.AppName}");
			
			if (!string.IsNullOrWhiteSpace(ev.ModuleName))
				sb.AppendLine($"**Faulting Module:** {ev.ModuleName}");
			
			if (!string.IsNullOrWhiteSpace(ev.ExceptionCode))
				sb.AppendLine($"**Exception Code:** {ev.ExceptionCode}");
			
			if (!string.IsNullOrWhiteSpace(ev.TaskDisplayName))
				sb.AppendLine($"**Task Category:** {ev.TaskDisplayName}");
			
			sb.AppendLine();
			sb.AppendLine("## FULL EVENT MESSAGE");
			sb.AppendLine("```");
			sb.AppendLine(ev.Message ?? "(No message available)");
			sb.AppendLine("```");
			sb.AppendLine();

			// Include knowledge base context if available
			if (!string.IsNullOrWhiteSpace(ev.KnowledgeTitle))
			{
				sb.AppendLine("## INTERNAL KNOWLEDGE BASE MATCH");
				sb.AppendLine($"**Title:** {ev.KnowledgeTitle}");
				sb.AppendLine($"**Summary:** {ev.KnowledgeSummary}");
				if (ev.Guidance != null && ev.Guidance.Count > 0)
				{
					sb.AppendLine("**Existing Guidance:**");
					foreach (var g in ev.Guidance)
						sb.AppendLine($"- {g}");
				}
				sb.AppendLine();
			}

			// Include WinDbg crash dump analysis if available
			if (ev.WinDbgAnalysis != null && ev.WinDbgAnalysis.Success)
			{
				sb.AppendLine(ev.WinDbgAnalysis.FormatForAI());
				sb.AppendLine();
			}

			sb.AppendLine("## YOUR TASK");
			sb.AppendLine("Provide a comprehensive root cause analysis following this EXACT structure. Keep it CONCISE and actionable:");
			sb.AppendLine();
			
			if (ev.WinDbgAnalysis != null && ev.WinDbgAnalysis.Success)
			{
				sb.AppendLine("**IMPORTANT:** WinDbg crash dump forensics data is provided above. Use the stack trace, faulting module, driver version, and exception codes to provide PRECISE root cause analysis. The call stack shows the exact sequence of function calls that led to the crash.");
				sb.AppendLine();
			}
			
			sb.AppendLine("### 1. SUMMARY");
			sb.AppendLine("Write 2-3 clear sentences explaining WHAT HAPPENED in plain English. No technical jargon.");
			sb.AppendLine("Example: 'Your NVIDIA display driver crashed while running multiple applications. Windows recovered the driver automatically, but this caused your screen to freeze temporarily. This is a known stability issue with driver version 531.41 when hardware acceleration is enabled.'");
			sb.AppendLine();
			sb.AppendLine("### 2. ROOT CAUSE");
			sb.AppendLine("Explain WHY this happened in 3-4 sentences:");
			sb.AppendLine("- Identify the specific component that failed (use WinDbg faulting module if available)");
			sb.AppendLine("- Explain the trigger (driver bug, incompatibility, hardware issue, etc.)");
			sb.AppendLine("- Mention severity level: **Critical/High/Medium/Low**");
			sb.AppendLine("- State if this is a known issue with the specific module/driver version");
			sb.AppendLine();
			sb.AppendLine("### 3. SUGGESTED FIXES");
			sb.AppendLine("Provide 3-5 SPECIFIC fixes in priority order. Use markdown formatting:");
			sb.AppendLine();
			sb.AppendLine("**1. Update NVIDIA Driver to Latest Version**");
			sb.AppendLine("- Open Device Manager → Display adapters → Right-click NVIDIA GPU");
			sb.AppendLine("- Select 'Update driver' → 'Search automatically'");
			sb.AppendLine("- Or download driver 546.33+ from `nvidia.com/drivers`");
			sb.AppendLine("- Expected result: Eliminates known crash bugs in version 531.41");
			sb.AppendLine();
			sb.AppendLine("**2. [Alternative Solution]**");
			sb.AppendLine("- [Step-by-step with commands]");
			sb.AppendLine("- Expected result: [What this accomplishes]");
			sb.AppendLine();
			sb.AppendLine("Keep each fix concise (4-5 lines max). Use **bold** for fix names, `backticks` for commands/paths, and bullet points for steps.");
			sb.AppendLine();
			sb.AppendLine("**Additional Diagnostics:**");
			sb.AppendLine("- Relevant diagnostic commands (e.g., `sfc /scannow`, Event Viewer filters)");
			sb.AppendLine("- Related Event IDs to monitor");
			sb.AppendLine("- When to escalate to Microsoft/vendor support");
			sb.AppendLine();
			sb.AppendLine("### 4. FORUM RESEARCH");
			sb.AppendLine("Summarize what the community says (2-3 sentences):");
			sb.AppendLine("- Are users reporting this on Reddit, Microsoft forums, or elsewhere?");
			sb.AppendLine("- What solutions worked for real users?");
			sb.AppendLine("- Any known workarounds or official patches?");
			sb.AppendLine();
			sb.AppendLine("**CRITICAL FORMATTING REQUIREMENTS:**");
			sb.AppendLine("- Use **bold** for emphasis and fix names");
			sb.AppendLine("- Use `backticks` for commands, file paths, registry keys, URLs");
			sb.AppendLine("- Use bullet points (- ) and numbered lists (1. 2. 3.)");
			sb.AppendLine("- Keep sections SHORT - no walls of text");
			sb.AppendLine("- Focus on NEW information, don't repeat the event message");
			sb.AppendLine("- Write like you're helping a friend troubleshoot");

			return sb.ToString();
		}

		private EventAnalysisResult ParseAnalysisResponse(string analysisText, SystemEventItem originalEvent)
		{
			try
			{
				// Parse the structured response
				var result = new EventAnalysisResult
				{
					Success = true,
					FullAnalysis = analysisText,
					EventTitle = originalEvent.DisplayTitle,
					EventId = originalEvent.EventId,
					ProviderName = originalEvent.ProviderName,
					Severity = originalEvent.DisplayType,
					TimeOccurred = originalEvent.TimeCreated,
					RelativeTime = originalEvent.RelativeTime
				};

				// Extract sections using simple string parsing
				result.Summary = ExtractSection(analysisText, "SUMMARY", "ROOT CAUSE") ?? 
				                ExtractSection(analysisText, "Summary", "Root Cause") ?? 
				                "Analysis completed. See full details below.";

				result.RootCause = ExtractSection(analysisText, "ROOT CAUSE", "SUGGESTED FIXES") ?? 
				                  ExtractSection(analysisText, "Root Cause", "Suggested Fixes") ?? 
				                  string.Empty;

				result.SuggestedFixes = ExtractSection(analysisText, "SUGGESTED FIXES", "FORUM RESEARCH") ?? 
				                       ExtractSection(analysisText, "Suggested Fixes", "Forum Research") ?? 
				                       string.Empty;

				result.ForumResearch = ExtractSection(analysisText, "FORUM RESEARCH", null) ?? 
				                      ExtractSection(analysisText, "Forum Research", null) ?? 
				                      string.Empty;

				// Impact and AdditionalContext are merged into other sections now
				result.Impact = string.Empty;
				result.AdditionalContext = string.Empty;

				return result;
			}
			catch
			{
				return new EventAnalysisResult
				{
					Success = true,
					FullAnalysis = analysisText,
					Summary = "Analysis completed.",
					EventTitle = originalEvent.DisplayTitle,
					EventId = originalEvent.EventId,
					ProviderName = originalEvent.ProviderName,
					Severity = originalEvent.DisplayType,
					TimeOccurred = originalEvent.TimeCreated,
					RelativeTime = originalEvent.RelativeTime
				};
			}
		}

		private string? ExtractSection(string text, string startMarker, string? endMarker)
		{
			try
			{
				int startIdx = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
				if (startIdx < 0) return null;

				startIdx = text.IndexOf('\n', startIdx);
				if (startIdx < 0) return null;
				startIdx++;

				int endIdx = text.Length;
				if (endMarker != null)
				{
					int markerIdx = text.IndexOf(endMarker, startIdx, StringComparison.OrdinalIgnoreCase);
					if (markerIdx > startIdx) endIdx = markerIdx;
				}

				return text.Substring(startIdx, endIdx - startIdx).Trim();
			}
			catch
			{
				return null;
			}
		}

		#region API Models

		private sealed class DeepSeekRequest
		{
			[JsonPropertyName("model")]
			public string Model { get; set; } = string.Empty;

			[JsonPropertyName("messages")]
			public DeepSeekMessage[] Messages { get; set; } = Array.Empty<DeepSeekMessage>();

			[JsonPropertyName("temperature")]
			public double Temperature { get; set; }

			[JsonPropertyName("max_tokens")]
			public int MaxTokens { get; set; }
		}

		private sealed class DeepSeekMessage
		{
			[JsonPropertyName("role")]
			public string Role { get; set; } = string.Empty;

			[JsonPropertyName("content")]
			public string Content { get; set; } = string.Empty;
		}

		private sealed class DeepSeekResponse
		{
			[JsonPropertyName("choices")]
			public DeepSeekChoice[]? Choices { get; set; }
		}

		private sealed class DeepSeekChoice
		{
			[JsonPropertyName("message")]
			public DeepSeekMessage? Message { get; set; }
		}

		#endregion
	}

	public sealed class EventAnalysisResult
	{
		public bool Success { get; set; }
		public string? ErrorMessage { get; set; }
		public string FullAnalysis { get; set; } = string.Empty;
		public string EventTitle { get; set; } = string.Empty;
		public int EventId { get; set; }
		public string ProviderName { get; set; } = string.Empty;
		public string Severity { get; set; } = string.Empty;
		public DateTimeOffset TimeOccurred { get; set; }
		public string RelativeTime { get; set; } = string.Empty;
		public string Summary { get; set; } = string.Empty;
		public string RootCause { get; set; } = string.Empty;
		public string Impact { get; set; } = string.Empty;
		public string SuggestedFixes { get; set; } = string.Empty;
		public string ForumResearch { get; set; } = string.Empty;
		public string AdditionalContext { get; set; } = string.Empty;
	}
}


