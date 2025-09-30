using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bluetask.Services
{
	public sealed class UpdateService
	{
		private static readonly Lazy<UpdateService> _shared = new Lazy<UpdateService>(() => new UpdateService());
		public static UpdateService Shared => _shared.Value;

		private readonly HttpClient _httpClient;
		private string _repoOwner = "bossman79";
		private string _repoName = "Bluetask";
		private bool _includePrereleases = false;
		private volatile bool _isChecking = false;
		private UpdateInfo? _lastInfo;

		public event Action? CheckingChanged;
		public event Action<UpdateInfo>? UpdateAvailable;
		public event Action<UpdateInfo>? NoUpdateAvailable;
		public event Action<string>? CheckFailed;

		public bool IsChecking => _isChecking;
		public UpdateInfo? LastInfo => _lastInfo;

		private UpdateService()
		{
			_httpClient = new HttpClient();
			_httpClient.Timeout = TimeSpan.FromSeconds(20);
			try
			{
				_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Bluetask-Updater/1.0");
				_httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
			}
			catch { }
		}

		public void Configure(string repoOwner, string repoName, bool includePrereleases)
		{
			_repoOwner = string.IsNullOrWhiteSpace(repoOwner) ? _repoOwner : repoOwner;
			_repoName = string.IsNullOrWhiteSpace(repoName) ? _repoName : repoName;
			_includePrereleases = includePrereleases;
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
				var currentVersion = GetCurrentVersion();
				var latest = await FetchLatestReleaseAsync(_includePrereleases, cancellationToken).ConfigureAwait(false);
				latest.CurrentVersion = currentVersion;
				_lastInfo = latest;
				if (latest.LatestVersion > currentVersion)
				{
					try { UpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
				else
				{
					try { NoUpdateAvailable?.Invoke(_lastInfo); } catch { }
				}
			}
			catch (TaskCanceledException tce)
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

		private async Task<UpdateInfo> FetchLatestReleaseAsync(bool includePrereleases, CancellationToken ct)
		{
			var uri = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases?per_page=10";
			using var resp = await _httpClient.GetAsync(uri, ct).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();
			var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
			JsonElement chosen = default;
			bool found = false;
			foreach (var rel in doc.RootElement.EnumerateArray())
			{
				bool isPrerelease = rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
				if (!includePrereleases && isPrerelease) continue;
				if (!rel.TryGetProperty("tag_name", out var tagEl)) continue;
				var tag = tagEl.GetString() ?? string.Empty;
				if (!TryParseVersion(tag, out var v)) continue;
				chosen = rel;
				found = true;
				break; // API is sorted newest-first
			}
			if (!found)
			{
				throw new InvalidOperationException("No suitable release found.");
			}

			string tagName = chosen.GetProperty("tag_name").GetString() ?? string.Empty;
			TryParseVersion(tagName, out var latestVersion);
			string releaseName = chosen.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? tagName) : tagName;
			bool prereleaseFlag = chosen.TryGetProperty("prerelease", out var pre2) && pre2.GetBoolean();

			string? assetName = null;
			string? browserUrl = null;
			if (chosen.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
			{
				string[] priorities = new[] { ".msixbundle", ".msix", ".msi", ".exe", ".zip" };
				foreach (var ext in priorities)
				{
					foreach (var a in assets.EnumerateArray())
					{
						var name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
						var url = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
						if (!string.IsNullOrEmpty(name) && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
						{
							assetName = name;
							browserUrl = url;
							break;
						}
					}
					if (assetName != null) break;
				}
			}

			return new UpdateInfo
			{
				CurrentVersion = GetCurrentVersion(),
				LatestVersion = latestVersion,
				TagName = tagName,
				ReleaseName = releaseName,
				AssetName = assetName,
				AssetDownloadUrl = browserUrl,
				IsPrerelease = prereleaseFlag
			};
		}

		public async Task<string?> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken ct = default)
		{
			var info = _lastInfo;
			if (info == null || string.IsNullOrWhiteSpace(info.AssetDownloadUrl)) return null;
			var url = info.AssetDownloadUrl!;
			var fileName = info.AssetName ?? Path.GetFileName(new Uri(url).AbsolutePath);
			var tempPath = Path.Combine(Path.GetTempPath(), fileName);

			using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();
			var total = resp.Content.Headers.ContentLength;
			await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			await using var output = File.Create(tempPath);
			var buffer = new byte[81920];
			long readTotal = 0;
			int read;
			while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
			{
				await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				readTotal += read;
				if (total.HasValue && progress != null && total.Value > 0)
				{
					progress.Report((double)readTotal / total.Value);
				}
			}
			return tempPath;
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


