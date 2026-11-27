using System.Net.Http.Headers;

namespace OBK.Core;

/// <summary>
/// Chooses the MIME type handed to external apps. A known filename extension wins;
/// otherwise a specific, non-risky server type is used, else application/octet-stream.
/// </summary>
public static class AttachmentMime
{
    public const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        [".zip"] = "application/zip",
    };

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        Fallback, "binary/octet-stream", "application/binary", "application/unknown",
        "application/download", "application/x-download", "application/force-download",
    };

    // Types that could execute or render active content in a recipient app.
    private static readonly HashSet<string> Risky = new(StringComparer.Ordinal)
    {
        "text/html", "application/xhtml+xml", "image/svg+xml", "application/xml", "text/xml",
        "application/javascript", "text/javascript", "application/x-javascript",
        "application/ecmascript", "text/ecmascript",
        "application/vnd.android.package-archive", "application/java-archive",
        "application/x-msdownload", "application/x-msdos-program", "application/x-dosexec",
        "application/x-executable", "application/x-sh", "application/x-shellscript",
        "application/hta", "application/x-ms-installer", "application/x-msi",
    };

    public static bool IsKnownExtension(string fileName) => ByExtension.ContainsKey(Path.GetExtension(fileName));

    public static string Resolve(string fileName, string? serverContentType)
    {
        if (ByExtension.TryGetValue(Path.GetExtension(fileName), out var known)) return known;
        var server = Normalize(serverContentType);
        return server is null || Generic.Contains(server) || Risky.Contains(server) ? Fallback : server;
    }

    /// <summary>Returns the lowercase type/subtype without parameters, or null when missing or invalid.</summary>
    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !MediaTypeHeaderValue.TryParse(contentType.Trim(), out var parsed) ||
            parsed.MediaType is not { } mime)
            return null;
        mime = mime.ToLowerInvariant();
        var slash = mime.IndexOf('/');
        return slash <= 0 || slash == mime.Length - 1 || mime.Contains('*') ? null : mime;
    }
}
