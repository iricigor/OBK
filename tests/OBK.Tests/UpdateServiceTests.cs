using OBK.Core;
using Xunit;

namespace OBK.Tests;

public sealed class UpdateServiceTests
{
    private const string Download = "https://github.com/iricigor/OBK/releases/download/";

    private static string Release(string tag, bool prerelease = false, string host = Download) => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"assets":[
        {"name":"OBK-{{tag[1..]}}-windows-x64-setup.exe","browser_download_url":"{{host}}{{tag}}/OBK-{{tag[1..]}}-windows-x64-setup.exe"},
        {"name":"SHA256SUMS.txt","browser_download_url":"{{host}}{{tag}}/SHA256SUMS.txt"}]}
        """;

    private static string AndroidRelease(string tag = "v1.0.27") => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":false,"assets":[
        {"name":"OBK-{{tag[1..]}}-android.apk","browser_download_url":"{{Download}}{{tag}}/OBK-{{tag[1..]}}-android.apk"},
        {"name":"ANDROID-SHA256SUMS.txt","browser_download_url":"{{Download}}{{tag}}/ANDROID-SHA256SUMS.txt"}]}
        """;

    [Fact]
    public void AndroidSelectionUsesOnlyTheAndroidAssetsAndNumericVersion()
    {
        var update = UpdateService.SelectAndroidUpdate(AndroidRelease(), new Version(1, 0, 9))!;
        Assert.Equal(new Version(1, 0, 27), update.Version);
        Assert.Equal("OBK-1.0.27-android.apk", update.InstallerName);
        Assert.Equal($"{Download}v1.0.27/ANDROID-SHA256SUMS.txt", update.ChecksumsUrl.AbsoluteUri);
        Assert.Null(UpdateService.SelectAndroidUpdate(AndroidRelease(), new Version(1, 0, 27)));
        Assert.Null(UpdateService.SelectAndroidUpdate(AndroidRelease(), new Version(1, 0, 28)));
        Assert.Null(UpdateService.SelectAndroidUpdate(AndroidRelease().Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(1, 0, 1)));
        Assert.Null(UpdateService.SelectAndroidUpdate(AndroidRelease().Replace("\"draft\":false", "\"draft\":true"), new Version(1, 0, 1)));
        Assert.Null(UpdateService.SelectUpdate(AndroidRelease(), new Version(1, 0, 1)));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-apk")]
    [InlineData("missing-checksum")]
    [InlineData("foreign-host")]
    [InlineData("wrong-tag")]
    [InlineData("duplicate")]
    [InlineData("version-overflow")]
    public void UnusableNewAndroidReleaseIsAnExplicitFailure(string fault)
    {
        var json = fault switch
        {
            "malformed" => "not json",
            "missing-apk" => Release("v1.0.27"),
            "missing-checksum" => AndroidRelease().Replace("ANDROID-SHA256SUMS.txt", "SHA256SUMS.txt"),
            "foreign-host" => AndroidRelease().Replace(Download, "https://example.com/"),
            "wrong-tag" => AndroidRelease().Replace($"{Download}v1.0.27/", $"{Download}v1.0.26/"),
            "duplicate" => AndroidRelease().Replace("]}", $",{{\"name\":\"OBK-1.0.27-android.apk\",\"browser_download_url\":\"{Download}v1.0.27/OBK-1.0.27-android.apk\"}}]}}"),
            "version-overflow" => AndroidRelease("v1.0.2100000000"),
            _ => throw new ArgumentException(fault)
        };
        Assert.Throws<ObkException>(() => UpdateService.SelectAndroidUpdate(json, new Version(1, 0, 1)));
    }

    [Fact]
    public async Task AndroidCheckAndDownloadUseAndroidChecksumAndVerifyBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-android-update-" + Guid.NewGuid().ToString("N"));
        var bytes = "apk"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var requests = new List<string>();
        try
        {
            using var service = new UpdateService(directory, new ResponseHandler(request =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage
                {
                    Content = request.RequestUri.AbsoluteUri == UpdateService.LatestReleaseUrl
                        ? new StringContent(AndroidRelease())
                        : request.RequestUri.AbsolutePath.EndsWith("ANDROID-SHA256SUMS.txt")
                            ? new StringContent($"{hash}  OBK-1.0.27-android.apk") : new ByteArrayContent(bytes)
                };
            }));
            var update = (await service.CheckAndroidAsync(new Version(1, 0, 1)))!;
            var path = await service.DownloadAsync(update);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            await service.DownloadAsync(update);
            Assert.Equal(1, requests.Count(url => url.EndsWith(".apk")));
            Assert.DoesNotContain(requests, url => url.EndsWith("/SHA256SUMS.txt"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AndroidCheckHttpFailuresAreNotReportedAsUpToDate(int status)
    {
        using var service = new UpdateService(Path.GetTempPath(), new ResponseHandler(_ =>
            new HttpResponseMessage((System.Net.HttpStatusCode)status)));
        await Assert.ThrowsAsync<ObkException>(() => service.CheckAndroidAsync(new Version(1, 0, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedAndroidDownloadsRemovePartialFiles(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-interrupted-update-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var update = UpdateService.SelectAndroidUpdate(AndroidRelease(), new Version(1, 0, 1))!;
        try
        {
            using var service = new UpdateService(directory, new ResponseHandler(request => new HttpResponseMessage
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
                    ? new StringContent($"{new string('a', 64)}  {update.InstallerName}")
                    : new StreamContent(new InterruptedStream(cancellation, cancel))
            }));
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(update, cancellation.Token));
            else
                await Assert.ThrowsAsync<ObkException>(() => service.DownloadAsync(update));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndroidDownloadRejectsOversizedBodiesEvenWithoutContentLength(bool declared)
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-large-update-" + Guid.NewGuid().ToString("N"));
        var update = UpdateService.SelectAndroidUpdate(AndroidRelease(), new Version(1, 0, 1))!;
        try
        {
            using var service = new UpdateService(directory, new ResponseHandler(request =>
            {
                HttpContent content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
                    ? new StringContent($"{new string('a', 64)}  {update.InstallerName}")
                    : new StreamContent(new LargeStream());
                if (declared && content is StreamContent) content.Headers.ContentLength = 500L * 1024 * 1024 + 1;
                return new HttpResponseMessage { Content = content };
            }));
            await Assert.ThrowsAsync<ObkException>(() => service.DownloadAsync(update));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("v0.1.3", "0.1.2", true)]
    [InlineData("v0.1.10", "0.1.9", true)]
    [InlineData("v1.0.25", "0.1.10", true)]
    [InlineData("v0.1.2", "0.1.2", false)]
    [InlineData("v0.1.1", "0.1.2", false)]
    public void OnlyNewerReleasesAreOffered(string tag, string installed, bool offered)
    {
        UpdateService.TryParseVersion(installed, out var version);
        Assert.Equal(offered, UpdateService.SelectUpdate(Release(tag), version) is not null);
    }

    [Fact]
    public void InformationalVersionSuffixIsIgnored()
    {
        Assert.True(UpdateService.TryParseVersion("0.1.2+abc123", out var version));
        Assert.Equal(new Version(0, 1, 2), version);
    }

    [Fact]
    public void PrereleasesAndForeignAssetHostsAreRejected()
    {
        var old = new Version(0, 1, 0);
        Assert.Null(UpdateService.SelectUpdate(Release("v0.1.3", prerelease: true), old));
        Assert.Null(UpdateService.SelectUpdate(Release("v0.1.3", host: "https://example.com/"), old));
        Assert.Null(UpdateService.SelectUpdate("not json", old));
    }

    [Fact]
    public void ChecksumIsFoundByInstallerName()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateService.FindChecksum($"{hash}  OBK-0.1.3-windows-x64-setup.exe\r\n", "OBK-0.1.3-windows-x64-setup.exe"));
        Assert.Null(UpdateService.FindChecksum($"{hash}  other.exe\n", "OBK-0.1.3-windows-x64-setup.exe"));
    }

    [Fact]
    public async Task DownloadIsVerifiedAndCorruptFilesAreDiscarded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = "installer"u8.ToArray();
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var update = UpdateService.SelectUpdate(Release("v0.1.3"), new Version(0, 1, 2))!;
            using (var service = new UpdateService(directory, new FakeHandler($"{good}  {update.InstallerName}", bytes)))
                Assert.Equal(bytes, await File.ReadAllBytesAsync(await service.DownloadAsync(update)));
            using (var service = new UpdateService(directory + "-bad", new FakeHandler($"{new string('0', 64)}  {update.InstallerName}", bytes)))
            {
                await Assert.ThrowsAsync<ObkException>(() => service.DownloadAsync(update));
                Assert.Empty(Directory.GetFiles(directory + "-bad"));
            }
        }
        finally
        {
            foreach (var path in new[] { directory, directory + "-bad" }) if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private sealed class FakeHandler(string sums, byte[] installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") ? new StringContent(sums) : new ByteArrayContent(installer)
            });
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(respond(request));
    }

    private sealed class InterruptedStream(CancellationTokenSource cancellation, bool cancel) : MemoryStream
    {
        private bool started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!started) { started = true; buffer.Span[0] = 1; return ValueTask.FromResult(1); }
            if (!cancel) throw new IOException("Download interrupted.");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class LargeStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }
    }
}
