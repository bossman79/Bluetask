using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.IO.Compression;

namespace Bluetask.Services
{
	public sealed class UpdateService
	{
		private static readonly Lazy<UpdateService> _shared = new Lazy<UpdateService>(() => new UpdateService());
		public static UpdateService Shared => _shared.Value;

		private readonly HttpClient _httpClient;
		private string _authToken = string.Empty;
		private string _repoOwner = "bossman79";
		private string _repoName = "Bluetask";
		private bool _includePrereleases = false;
		private volatile bool _isChecking = false;
		private UpdateInfo? _lastInfo;
		private string? _lastError;

		public event Action? CheckingChanged;
		public event Action<UpdateInfo>? UpdateAvailable;
		public event Action<UpdateInfo>? NoUpdateAvailable;
		public event Action<string>? CheckFailed;

		public bool IsChecking => _isChecking;
		public UpdateInfo? LastInfo => _lastInfo;
		public string? LastError => _lastError;

		private UpdateService()
		{
			_httpClient = new HttpClient();
			_httpClient.Timeout = TimeSpan.FromSeconds(20);
			try
			{
				_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Bluetask-Updater/1.0");
				_httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
				_httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
			}
			catch { }

			// Optional token from environment variables to avoid public rate limits
			try
			{
				var envToken = Environment.GetEnvironmentVariable("BLUETASK_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
				if (!string.IsNullOrWhiteSpace(envToken))
				{
					SetAuthToken(envToken);
				}
			}
			catch { }

			// Hardcoded token per user request
			try { SetAuthToken("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"); } catch { }
		}

		public void Configure(string repoOwner, string repoName, bool includePrereleases)
		{
			_repoOwner = string.IsNullOrWhiteSpace(repoOwner) ? _repoOwner : repoOwner;
			_repoName = string.IsNullOrWhiteSpace(repoName) ? _repoName : repoName;
			_includePrereleases = includePrereleases;
		}

		public void SetAuthToken(string token)
		{
			_authToken = token ?? string.Empty;
			try
			{
				_httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(_authToken)
					? null
					: new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);
			}
			catch { }
		}

		public Version GetCurrentVersion()
		{
			try
			{
				// Prefer package version if available (MSIX packaged)
				try
				{
					var pkg = Windows.ApplicationModel.Package.Current;
					var v = pkg.Id.Version;
					return new Version(v.Major, v.Minor, v.Build, v.Revision);
				}
				catch { }

				var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
				var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
				if (infoAttr != null && TryParseVersion(infoAttr.InformationalVersion, out var infoVer))
				{
					return infoVer;
				}
				var v2 = asm.GetName().Version;
				if (v2 != null) return v2;
			}
			catch { }
			return new Version(0, 0, 0, 0);
		}

		public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
		{
			if (_isChecking) return;
			_isChecking = true;
			try { CheckingChanged?.Invoke(); } catch { }
			try
			{
				// Incremental updater: compare latest commit, notify if changed
				var head = await FetchLatestCommitShaAsync(cancellationToken).ConfigureAwait(false);
				_lastInfo = new UpdateInfo { TagName = head, ReleaseName = head };
				var previous = SettingsService.UpdateLastCommitSha;
				if (string.IsNullOrEmpty(head))
				{
					try { NoUpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
				else if (string.IsNullOrEmpty(previous))
				{
					// First run: baseline to current head; don't prompt update
					SettingsService.UpdateLastCommitSha = head;
					try { NoUpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
				else if (!string.Equals(previous, head, StringComparison.Ordinal))
				{
					try { UpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
				else
				{
					try { NoUpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
			}
			catch (TaskCanceledException)
			{
				try { CheckFailed?.Invoke("Update check timed out"); } catch { }
			}
			catch (Exception ex)
			{
				try { CheckFailed?.Invoke(ex.Message); } catch { }
			}
			finally
			{
				_isChecking = false;
				try { CheckingChanged?.Invoke(); } catch { }
			}
		}

		private async Task<string> FetchLatestCommitShaAsync(CancellationToken ct)
		{
			// Get default branch
			var repoUri = $"https://api.github.com/repos/{_repoOwner}/{_repoName}";
			using (var repoResp = await _httpClient.GetAsync(repoUri, ct).ConfigureAwait(false))
			{
				repoResp.EnsureSuccessStatusCode();
				var repoStream = await repoResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				using var repoDoc = await JsonDocument.ParseAsync(repoStream, cancellationToken: ct).ConfigureAwait(false);
				var defaultBranch = repoDoc.RootElement.TryGetProperty("default_branch", out var db) ? (db.GetString() ?? "main") : "main";
				var branchUri = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/commits/{defaultBranch}";
				using var branchResp = await _httpClient.GetAsync(branchUri, ct).ConfigureAwait(false);
				branchResp.EnsureSuccessStatusCode();
				var branchStream = await branchResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				using var branchDoc = await JsonDocument.ParseAsync(branchStream, cancellationToken: ct).ConfigureAwait(false);
				return branchDoc.RootElement.TryGetProperty("sha", out var shaEl) ? (shaEl.GetString() ?? string.Empty) : string.Empty;
			}
		}

		public async Task<string?> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken ct = default)
		{
			// For backward compatibility, call incremental updater and return null
			await ApplyIncrementalUpdateAsync(progress, ct).ConfigureAwait(false);
			return null;
		}

		public async Task<int> ApplyIncrementalUpdateAsync(IProgress<double>? progress = null, CancellationToken ct = default)
		{
			var head = await FetchLatestCommitShaAsync(ct).ConfigureAwait(false);
			if (string.IsNullOrEmpty(head)) return 0;
			var previous = SettingsService.UpdateLastCommitSha;
			if (string.IsNullOrEmpty(previous))
			{
				SettingsService.UpdateLastCommitSha = head;
				return 0;
			}
			var diffUri = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/compare/{previous}...{head}";
			using var diffResp = await _httpClient.GetAsync(diffUri, ct).ConfigureAwait(false);
			diffResp.EnsureSuccessStatusCode();
			var diffStream = await diffResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var diffDoc = await JsonDocument.ParseAsync(diffStream, cancellationToken: ct).ConfigureAwait(false);
			if (!diffDoc.RootElement.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
			{
				SettingsService.UpdateLastCommitSha = head;
				return 0;
			}
			int total = 0;
			foreach (var _ in filesEl.EnumerateArray()) total++;
			int replaced = 0;
			int index = 0;
			foreach (var f in filesEl.EnumerateArray())
			{
				index++;
				var status = f.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
				if (status == "removed") { Report(index, total); continue; }
				var rawUrl = f.TryGetProperty("raw_url", out var ru) ? ru.GetString() : null;
				var filename = f.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
				if (string.IsNullOrEmpty(rawUrl) || string.IsNullOrEmpty(filename)) { Report(index, total); continue; }
				var targetPath = MapRepoPathToLocal(filename);
				if (string.IsNullOrEmpty(targetPath)) { Report(index, total); continue; }
				using var fileResp = await _httpClient.GetAsync(rawUrl, ct).ConfigureAwait(false);
				fileResp.EnsureSuccessStatusCode();
				var dir = Path.GetDirectoryName(targetPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				await using var src = await fileResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				await using var dst = File.Create(targetPath);
				await src.CopyToAsync(dst, ct).ConfigureAwait(false);
				replaced++;
				Report(index, total);
			}
			SettingsService.UpdateLastCommitSha = head;
			return replaced;

			void Report(int i, int t)
			{
				try { progress?.Report(t <= 0 ? 0 : (double)i / t); } catch { }
			}
		}

		public async Task<string?> DownloadProgramFolderToStagingAsync(IProgress<double>? progress = null, CancellationToken ct = default)
		{
			try
			{
				// Prefer archive download to avoid REST rate limits
				var staging = await TryDownloadFromArchiveAsync("main", progress, ct).ConfigureAwait(false);
				if (!string.IsNullOrEmpty(staging)) return staging;
				staging = await TryDownloadFromArchiveAsync("master", progress, ct).ConfigureAwait(false);
				if (!string.IsNullOrEmpty(staging)) return staging;

				// Fallback: contents API recursive listing
				var repoUri = $"https://api.github.com/repos/{_repoOwner}/{_repoName}";
				using var repoResp = await _httpClient.GetAsync(repoUri, ct).ConfigureAwait(false);
				repoResp.EnsureSuccessStatusCode();
				await using var repoStream = await repoResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				using var repoDoc = await JsonDocument.ParseAsync(repoStream, cancellationToken: ct).ConfigureAwait(false);
				var defaultBranch = repoDoc.RootElement.TryGetProperty("default_branch", out var db) ? (db.GetString() ?? "main") : "main";

				var files = new System.Collections.Generic.List<(string path, string downloadUrl)>();
				foreach (var candidate in new[] { "Program", "program", "PROGRAM" })
				{
					await CollectContentsRecursiveAsync(candidate, defaultBranch, files, ct).ConfigureAwait(false);
					if (files.Count > 0) break;
				}
				if (files.Count == 0)
				{
					_lastError = "Program folder not found";
					return null;
				}

				var stagingRoot = Path.Combine(Path.GetTempPath(), "BluetaskUpdate", Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(stagingRoot);

				int index = 0; int total = files.Count;
				foreach (var f in files)
				{
					ct.ThrowIfCancellationRequested();
					var rel = f.path.StartsWith("Program/", StringComparison.OrdinalIgnoreCase) ? f.path.Substring("Program/".Length) : f.path;
					var dest = Path.Combine(stagingRoot, rel.Replace('/', Path.DirectorySeparatorChar));
					var destDir = Path.GetDirectoryName(dest);
					if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
					using var resp = await _httpClient.GetAsync(f.downloadUrl, ct).ConfigureAwait(false);
					resp.EnsureSuccessStatusCode();
					await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
					await using var output = File.Create(dest);
					await input.CopyToAsync(output, ct).ConfigureAwait(false);
					index++;
					try { progress?.Report(total <= 0 ? 0 : (double)index / total); } catch { }
				}

				return stagingRoot;
			}
			catch (Exception ex)
			{
				_lastError = ex.Message;
				return null;
			}
		}

		private async Task CollectContentsRecursiveAsync(string path, string branch, System.Collections.Generic.List<(string path, string downloadUrl)> files, CancellationToken ct)
		{
			var api = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/contents/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(branch)}";
			using var resp = await _httpClient.GetAsync(api, ct).ConfigureAwait(false);
			if (resp.StatusCode == HttpStatusCode.NotFound) return;
			resp.EnsureSuccessStatusCode();
			await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
			foreach (var item in doc.RootElement.EnumerateArray())
			{
				var type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
				var itemPath = item.TryGetProperty("path", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
				if (string.IsNullOrEmpty(itemPath)) continue;
				if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
				{
					var dl = item.TryGetProperty("download_url", out var du) ? du.GetString() : null;
					if (!string.IsNullOrEmpty(dl)) files.Add((itemPath, dl!));
				}
				else if (string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase))
				{
					await CollectContentsRecursiveAsync(itemPath, branch, files, ct).ConfigureAwait(false);
				}
			}
		}

		private async Task<string?> TryDownloadFromArchiveAsync(string branch, IProgress<double>? progress, CancellationToken ct)
		{
			try
			{
				var url = $"https://codeload.github.com/{_repoOwner}/{_repoName}/zip-refs/heads/{Uri.EscapeDataString(branch)}";
				var zipRoot = Path.Combine(Path.GetTempPath(), "BluetaskUpdate", Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(zipRoot);
				var zipPath = Path.Combine(zipRoot, "repo.zip");
				using (var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
				{
					resp.EnsureSuccessStatusCode();
					await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
					await using var file = File.Create(zipPath);
					await input.CopyToAsync(file, ct).ConfigureAwait(false);
				}
				var extractDir = Path.Combine(zipRoot, "extract");
				ZipFile.ExtractToDirectory(zipPath, extractDir);
				string? programDir = null;
				foreach (var dir in Directory.GetDirectories(extractDir))
				{
					var cand = Path.Combine(dir, "Program");
					if (Directory.Exists(cand)) { programDir = cand; break; }
					cand = Path.Combine(dir, "program");
					if (Directory.Exists(cand)) { programDir = cand; break; }
				}
				if (string.IsNullOrEmpty(programDir)) { _lastError = "Program folder not found in archive"; return null; }
				var stagingRoot = Path.Combine(Path.GetTempPath(), "BluetaskUpdate", Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(stagingRoot);
				var srcFiles = Directory.GetFiles(programDir, "*", SearchOption.AllDirectories);
				int idx = 0; int total = srcFiles.Length;
				foreach (var sf in srcFiles)
				{
					var rel = Path.GetRelativePath(programDir, sf);
					var dest = Path.Combine(stagingRoot, rel);
					var destDir = Path.GetDirectoryName(dest);
					if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
					File.Copy(sf, dest, true);
					idx++;
					try { progress?.Report(total <= 0 ? 0 : (double)idx / total); } catch { }
				}
				return stagingRoot;
			}
			catch (Exception ex)
			{
				_lastError = ex.Message;
				return null;
			}
		}

		public bool ScheduleReplaceAndRestart(string stagingRoot)
		{
			try
			{
				var exePath = Environment.ProcessPath ?? (Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
				if (string.IsNullOrEmpty(exePath)) return false;
				var pid = Process.GetCurrentProcess().Id;
				var targetDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
				var scriptPath = Path.Combine(stagingRoot, "apply_update.cmd");
				var sb = new StringBuilder();
				sb.AppendLine("@echo off");
				sb.AppendLine("setlocal enabledelayedexpansion");
				sb.AppendLine($"set SRC=\"{stagingRoot}\"");
				sb.AppendLine($"set DEST=\"{targetDir}\"");
				sb.AppendLine($"set EXE=\"{exePath}\"");
				sb.AppendLine($"set PID={pid}");
				sb.AppendLine(":wait");
				sb.AppendLine("for /f \"tokens=2 delims==\" %%a in ('wmic process where ProcessId^=!PID! get ProcessId /value ^| find \"=\"') do set found=%%a");
				sb.AppendLine("if defined found ( timeout /T 1 /NOBREAK >NUL & set found= & goto wait )");
				sb.AppendLine("robocopy %SRC% %DEST% /E /XO /R:3 /W:1 /NFL /NDL /NJH /NJS >NUL");
				sb.AppendLine("start \"\" %EXE%");
				sb.AppendLine("exit /b 0");
				File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);
				var psi = new ProcessStartInfo
				{
					FileName = scriptPath,
					UseShellExecute = true,
					WindowStyle = ProcessWindowStyle.Hidden
				};
				Process.Start(psi);
				return true;
			}
			catch { return false; }
		}

		private string? MapRepoPathToLocal(string repoPath)
		{
			repoPath = repoPath.Replace("\\", "/");
			if (repoPath.StartsWith("Services/", StringComparison.OrdinalIgnoreCase) ||
				repoPath.StartsWith("ViewModels/", StringComparison.OrdinalIgnoreCase) ||
				repoPath.StartsWith("Views/", StringComparison.OrdinalIgnoreCase) ||
				repoPath.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
				repoPath.Equals("App.xaml.cs", StringComparison.OrdinalIgnoreCase) ||
				repoPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
				repoPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					var root = LocateProjectRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
					return Path.Combine(root, repoPath.Replace('/', Path.DirectorySeparatorChar));
				}
				catch { return null; }
			}
			return null;
		}

		private static string? LocateProjectRoot(string start)
		{
			try
			{
				var dir = new DirectoryInfo(start);
				for (int i = 0; i < 6 && dir != null; i++)
				{
					if (File.Exists(Path.Combine(dir.FullName, "Bluetask.csproj"))) return dir.FullName;
					dir = dir.Parent;
				}
			}
			catch { }
			return null;
		}

		public bool TryLaunchInstaller(string installerPath)
		{
			try
			{
				var psi = new ProcessStartInfo(installerPath)
				{
					UseShellExecute = true
				};
				Process.Start(psi);
				return true;
			}
			catch { return false; }
		}

		private static bool TryParseVersion(string? tagOrVersion, out Version version)
		{
			version = new Version(0, 0, 0, 0);
			if (string.IsNullOrWhiteSpace(tagOrVersion)) return false;
			string s = tagOrVersion.Trim();
			if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
			int dash = s.IndexOf('-');
			if (dash >= 0) s = s.Substring(0, dash);
			var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) return false;
			int[] nums = new int[4];
			int count = Math.Min(parts.Length, 4);
			for (int i = 0; i < count; i++)
			{
				if (!int.TryParse(parts[i], out nums[i])) nums[i] = 0;
			}
			version = new Version(nums[0], nums[1], count > 2 ? nums[2] : 0, count > 3 ? nums[3] : 0);
			return true;
		}

		public sealed class UpdateInfo
		{
			public Version CurrentVersion { get; set; } = new Version(0, 0, 0, 0);
			public Version LatestVersion { get; set; } = new Version(0, 0, 0, 0);
			public string TagName { get; set; } = string.Empty;
			public string ReleaseName { get; set; } = string.Empty;
			public string? AssetName { get; set; }
			public string? AssetDownloadUrl { get; set; }
			public bool IsPrerelease { get; set; }
		}
	}
}


