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
}
