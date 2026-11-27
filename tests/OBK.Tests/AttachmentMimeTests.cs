using OBK.Core;
using Xunit;

namespace OBK.Tests;

public sealed class AttachmentMimeTests
{
    [Theory]
    [InlineData("Report.PDF", "application/octet-stream", "application/pdf")]
    [InlineData("report.pdf", null, "application/pdf")]
    [InlineData("report.pdf", "not a type", "application/pdf")]
    [InlineData("report.pdf", "text/html", "application/pdf")]
    [InlineData("photo.jpg", "image/png", "image/jpeg")]
    public void KnownExtensionWins(string name, string? server, string expected) =>
        Assert.Equal(expected, AttachmentMime.Resolve(name, server));

    [Theory]
    [InlineData("scan.heic", "IMAGE/HEIC; name=\"scan.heic\"", "image/heic")]
    [InlineData("notes.log", "text/plain; charset=utf-8", "text/plain")]
    [InlineData("scan.heic", null, "application/octet-stream")]
    [InlineData("scan.heic", "", "application/octet-stream")]
    [InlineData("scan.heic", "garbage", "application/octet-stream")]
    [InlineData("scan.heic", "image/*", "application/octet-stream")]
    [InlineData("file.bin", "application/x-download", "application/octet-stream")]
    [InlineData("noextension", "binary/octet-stream", "application/octet-stream")]
    public void UnknownExtensionUsesSpecificServerTypeOrFallback(string name, string? server, string expected) =>
        Assert.Equal(expected, AttachmentMime.Resolve(name, server));

    [Theory]
    [InlineData("page.htm", "text/html; charset=utf-8")]
    [InlineData("image.svg", "image/svg+xml")]
    [InlineData("app.apk", "application/vnd.android.package-archive")]
    [InlineData("setup.exe", "application/x-msdownload")]
    [InlineData("script.js", "text/javascript")]
    public void RiskyServerTypesAreDowngraded(string name, string server) =>
        Assert.Equal(AttachmentMime.Fallback, AttachmentMime.Resolve(name, server));
}
