using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OBK.Core;

public sealed class SchoolDirectoryEntry
{
    [JsonPropertyName("id"), JsonRequired]
    public string Id { get; init; } = "";

    [JsonPropertyName("name"), JsonRequired]
    public string Name { get; init; } = "";

    [JsonPropertyName("schoolUrl"), JsonRequired]
    public string SchoolUrl { get; init; } = "";

    [JsonPropertyName("urlValid")]
    public bool UrlValid { get; init; }

    [JsonPropertyName("municipalityMatches"), JsonRequired]
    public List<string> MunicipalityMatches { get; init; } = [];

    public string Towns => string.Join(", ", MunicipalityMatches);
}

public sealed record SchoolSearchResult(int Total, IReadOnlyList<SchoolDirectoryEntry> Visible);

public sealed class SchoolDirectory
{
    private static readonly HashSet<string> CommonUrlParts = new(StringComparer.Ordinal)
    {
        "http", "https", "www", "bakalari", "cz", "sk", "com", "net", "org", "edu", "eu", "io"
    };
    private readonly IReadOnlyList<SchoolDirectoryEntry> schools;
    private readonly IReadOnlyList<SearchSchool> searchIndex;
    private readonly IReadOnlyDictionary<string, SchoolDirectoryEntry> byUrl;

    public SchoolDirectory(IEnumerable<SchoolDirectoryEntry> entries)
    {
        var usable = new List<SchoolDirectoryEntry>();
        var urls = new Dictionary<string, SchoolDirectoryEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.UrlValid || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) ||
                string.IsNullOrWhiteSpace(entry.SchoolUrl) || entry.MunicipalityMatches is null)
                continue;
            try
            {
                var normalized = KomensClient.NormalizeUrl(entry.SchoolUrl);
                usable.Add(entry);
                urls.TryAdd(normalized, entry);
            }
            catch (ObkException)
            {
                // Legacy or malformed directory URLs stay in the snapshot, but cannot be offered by the HTTPS-only client.
            }
        }
        schools = usable.OrderBy(school => school.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        searchIndex = schools.Select(school => new SearchSchool(
            school,
            NormalizeText(school.Name),
            school.MunicipalityMatches.Select(NormalizeText).ToArray(),
            NormalizeUrlText(school.SchoolUrl))).ToArray();
        byUrl = urls;
    }

    public int Count => schools.Count;

    public static SchoolDirectory Load(Stream compressedJson)
    {
        try
        {
            using var gzip = new GZipStream(compressedJson, CompressionMode.Decompress, leaveOpen: true);
            var document = JsonSerializer.Deserialize<DirectoryDocument>(gzip, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new ObkException("The bundled school directory is empty.");
            if (document.FormatVersion != 1 || document.Schools is null)
                throw new ObkException("The bundled school directory format is not supported.");
            var directory = new SchoolDirectory(document.Schools);
            if (directory.Count == 0)
                throw new ObkException("The bundled school directory has no usable HTTPS schools.");
            return directory;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            throw new ObkException("The bundled school directory is damaged. Enter the school URL manually.", error);
        }
    }

    public SchoolDirectoryEntry? FindByUrl(string url)
    {
        string normalized;
        try { normalized = KomensClient.NormalizeUrl(url); }
        catch (ObkException) { return null; }
        return byUrl.GetValueOrDefault(normalized);
    }

    public SchoolSearchResult Search(string query, int visibleLimit = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleLimit, 1);
        var text = NormalizeText(query);
        var url = NormalizeUrlText(query);
        if (text.Length == 0 && url.Length == 0) return new(0, []);
        var matches = searchIndex.Where(index =>
                (text.Length > 0 && (index.Name.Contains(text, StringComparison.Ordinal) ||
                    index.Towns.Any(town => town.Contains(text, StringComparison.Ordinal)))) ||
                (url.Length > 0 && index.Url.Contains(url, StringComparison.Ordinal)))
            .Select(index => index.School)
            .ToArray();
        return new(matches.Length, matches.Take(visibleLimit).ToArray());
    }

    private static string NormalizeText(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        return string.Concat(normalized.Where(character =>
                UnicodeCategory.NonSpacingMark != CharUnicodeInfo.GetUnicodeCategory(character) &&
                char.IsLetterOrDigit(character)))
            .ToLowerInvariant();
    }

    private static string NormalizeUrlText(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormD)
            .ToLowerInvariant();
        var words = Regex.Matches(normalized, @"[\p{L}\p{N}]+")
            .Select(match => string.Concat(match.Value.Where(character =>
                UnicodeCategory.NonSpacingMark != CharUnicodeInfo.GetUnicodeCategory(character))))
            .Where(word => !CommonUrlParts.Contains(word));
        return string.Concat(words);
    }

    private sealed class DirectoryDocument
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("schools"), JsonRequired]
        public List<SchoolDirectoryEntry>? Schools { get; init; }
    }

    private sealed record SearchSchool(
        SchoolDirectoryEntry School, string Name, IReadOnlyList<string> Towns, string Url);
}
