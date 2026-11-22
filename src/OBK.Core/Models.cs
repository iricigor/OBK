using OBK.Core.Localization;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OBK.Core;

public sealed class ObkException(string message, Exception? innerException = null) : Exception(message, innerException);

public readonly record struct SchoolYear(int StartYear)
{
    public DateTime Start => new(StartYear, 9, 1);
    public DateTime End => new(StartYear + 1, 9, 1);
    public string Label => $"{StartYear}/{StartYear + 1}";
    public static SchoolYear Current(DateTime? today = null)
    {
        var date = today ?? DateTime.Today;
        return new(date.Month >= 9 ? date.Year : date.Year - 1);
    }
}

public sealed class Message
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string SentDate { get; init; }
    public required List<Attachment> Attachments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Sender { get; init; }
    public string? RelevantName { get; init; }
    public bool? Read { get; init; }
    public bool? CanConfirm { get; init; }
    public bool? Confirmed { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    [JsonIgnore] public DateTimeOffset Date => ParseDate(SentDate);
    [JsonIgnore] public string SenderName =>
        Sender.ValueKind == JsonValueKind.Object &&
        Sender.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()! : "";

    public void Validate()
    {
        if (string.IsNullOrEmpty(Id) || Title is null || Text is null ||
            Attachments is null || Attachments.Any(a => a is null))
            throw new ObkException(SharedStrings.ErrorUnrecognizedMessage);
        _ = Date;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T") ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            throw new ObkException(SharedStrings.ErrorInvalidSentDate);
        return date;
    }
}

public sealed class Attachment
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? FileName { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    [JsonIgnore] public string Identifier => !string.IsNullOrEmpty(Id) ? Id :
        throw new ObkException(SharedStrings.ErrorAttachmentNoId);
    [JsonIgnore] public string SafeName => FileNames.Sanitize(Name ?? FileName ?? "attachment.bin");
}

public static class FileNames
{
    private static readonly HashSet<string> Openable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".rtf", ".zip"
    };
    public static bool CanOpen(string name) => Openable.Contains(Path.GetExtension(name));
    public static string Sanitize(string name)
    {
        name = name.Replace('\\', '/').Split('/')[^1];
        name = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").TrimEnd(' ', '.');
        if (name.Length == 0) name = "attachment.bin";
        var stem = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(COM|LPT)[1-9¹²³])$"))
            name = "_" + name;
        if (name.Length > 180)
        {
            var suffix = Path.GetExtension(name);
            if (suffix.Length > 20) suffix = suffix[..20];
            name = name[..(180 - suffix.Length)] + suffix;
        }
        return name;
    }
}

public sealed record Login(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("childName")] string? ChildName = null,
    [property: JsonPropertyName("schoolName")] string? SchoolName = null);

public sealed record AttachmentInfo(
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("size"), JsonRequired] long Size,
    [property: JsonPropertyName("sha256"), JsonRequired] string Sha256,
    [property: JsonPropertyName("content_type")] string? ContentType);

public sealed record Archive(string SchoolName, Dictionary<string, List<Message>> Folders, string SyncedAt);

public static class Folders
{
    public static IReadOnlyList<string> Keys { get; } = ["received", "sent", "noticeboard"];
    public static string Title(string key) => key switch
    {
        "received" => SharedStrings.FolderInbox,
        "sent" => SharedStrings.FolderSent,
        "noticeboard" => SharedStrings.FolderNoticeboard,
        _ => key
    };
    public static Dictionary<string, List<Message>> Empty() => Keys.ToDictionary(k => k, _ => new List<Message>());
    public static void Validate(Dictionary<string, List<Message>> folders)
    {
        if (folders is null || !folders.Keys.ToHashSet().SetEquals(Keys))
            throw new ObkException(SharedStrings.ErrorIncompleteFolderSet);
        foreach (var messages in folders.Values)
        {
            if (messages is null) throw new ObkException(SharedStrings.ErrorArchiveDamaged);
            foreach (var message in messages)
            {
                if (message is null) throw new ObkException(SharedStrings.ErrorArchiveDamaged);
                message.Validate();
            }
        }
    }
}
