using OBK.Core.Localization;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBK.Core;

public sealed record AvailableUpdate(Version Version, Uri InstallerUrl, Uri ChecksumsUrl, string InstallerName);

public sealed class UpdateService : IDisposable
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/iricigor/OBK/releases/latest";
    private const string DownloadPrefix = "https://github.com/iricigor/OBK/releases/download/";
    private const long MaxInstallerBytes = 500L * 1024 * 1024;
    private readonly HttpClient http;
    private readonly string directory;

    public UpdateService(string directory, HttpMessageHandler? handler = null)
    {
        this.directory = directory;
        http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OBK", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static Version InstalledVersion
    {
        get
        {
            var text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParseVersion(text, out var version) ? version : new Version(0, 0, 0);
        }
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = Regex.Match(text ?? "", @"^v?(\d+)\.(\d+)\.(\d+)(?:[+-].*)?$");
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        version = new Version(major, minor, patch);
        return true;
    }

    public static string Display(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    /// <summary>Parses the GitHub "latest release" payload; returns null when it is unusable or not newer.</summary>
    public static AvailableUpdate? SelectUpdate(string json, Version installed)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(tag.GetString()!, @"^v\d+\.\d+\.\d+$") ||
                !TryParseVersion(tag.GetString(), out var version) || version <= installed) return null;
            var name = $"OBK-{version.Major}.{version.Minor}.{version.Build}-windows-x64-setup.exe";
            Uri? installer = null, checksums = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                if (assetName == name) installer = new Uri(url);
                else if (assetName == "SHA256SUMS.txt") checksums = new Uri(url);
            }
            return installer is null || checksums is null ? null : new AvailableUpdate(version, installer, checksums, name);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (match.Success && match.Groups[2].Value == fileName) return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    public async Task<AvailableUpdate?> CheckAsync(Version installed, CancellationToken cancel = default)
    {
        try
        {
            using var response = await http.GetAsync(LatestReleaseUrl, cancel);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return SelectUpdate(await response.Content.ReadAsStringAsync(cancel), installed);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new ObkException(SharedStrings.ErrorUpdateCheckFailed, error);
        }
    }

    /// <summary>Downloads and verifies the installer, returning its path. An already verified download is reused.</summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken cancel = default)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var sums = await http.GetStringAsync(update.ChecksumsUrl, cancel);
            var expected = FindChecksum(sums, update.InstallerName) ??
                throw new ObkException(SharedStrings.ErrorUpdateNoChecksum);
            var path = Path.Combine(directory, update.InstallerName);
            if (File.Exists(path) && await HashAsync(path, cancel) == expected) return path;
            var partial = path + ".download";
            using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxInstallerBytes)
                    throw new ObkException(SharedStrings.ErrorUpdateTooLarge);
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(partial);
                await source.CopyToAsync(target, cancel);
            }
            if (await HashAsync(partial, cancel) != expected)
            {
                File.Delete(partial);
                throw new ObkException(SharedStrings.ErrorUpdateVerificationFailed);
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new ObkException(SharedStrings.ErrorUpdateDownloadFailed, error);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancel)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancel)).ToLowerInvariant();
    }

    public static void StartInstaller(string installer, bool relaunch)
    {
        var arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS" + (relaunch ? " /Relaunch=1" : "");
        Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = false });
    }

    public void Dispose() => http.Dispose();
}
