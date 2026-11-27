using OBK.Core.Localization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OBK.Core;

public sealed record Mark
{
    public required string Id { get; init; }
    public required string SubjectId { get; init; }
    public required string MarkDate { get; init; }
    public required string EditDate { get; init; }
    public required string MarkText { get; init; }
    public string? Caption { get; init; }
    public string? Theme { get; init; }
    public string? TeacherId { get; init; }
    public string? Type { get; init; }
    public string? TypeNote { get; init; }
    public int? Weight { get; init; }
    public bool IsPoints { get; init; }
    public string? PointsText { get; init; }
    public decimal MaxPoints { get; init; }
    public string? CalculatedMarkText { get; init; }
    public string? ClassRankText { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    [JsonIgnore] public DateTimeOffset Date => ParseDate(MarkDate);
    [JsonIgnore] public DateTimeOffset Edited => ParseDate(EditDate);
    [JsonIgnore] public string Key => $"{SubjectId.Length}:{SubjectId}{Id}";

    // Server unread/confirmation flags must not create local "changed" assessments.
    [JsonIgnore] public string Version => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Id, SubjectId, MarkDate, EditDate, MarkText, Caption, Theme, TeacherId, Type, TypeNote,
        Weight, IsPoints, PointsText, MaxPoints, CalculatedMarkText, ClassRankText
    })));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(SubjectId) ||
            MarkText is null || Weight < 0 || MaxPoints < 0)
            throw new ObkException(SharedStrings.ErrorMarksInvalid);
        _ = Date;
        _ = Edited;
    }

    internal static DateTimeOffset ParseDate(string? value)
    {
        if (value is null || value.Length < 11 || value[10] != 'T' ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ObkException(SharedStrings.ErrorMarksInvalid);
        return date;
    }
}

public sealed record MarkSubject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Abbrev { get; init; }
}

public sealed record SubjectMarks
{
    public required MarkSubject Subject { get; init; }
    public required List<Mark> Marks { get; init; }
    public string? AverageText { get; init; }
    public string? TemporaryMark { get; init; }
    public string? TemporaryMarkNote { get; init; }
    public string? SubjectNote { get; init; }
    public bool PointsOnly { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record MarksData
{
    public required List<SubjectMarks> Subjects { get; init; }

    public void Validate()
    {
        if (Subjects is null) throw new ObkException(SharedStrings.ErrorMarksInvalid);
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Subjects)
        {
            if (entry?.Subject is not { } subject || string.IsNullOrWhiteSpace(subject.Id) ||
                string.IsNullOrWhiteSpace(subject.Name) || !subjects.Add(subject.Id) || entry.Marks is null)
                throw new ObkException(SharedStrings.ErrorMarksInvalid);
            var marks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mark in entry.Marks)
            {
                if (mark is null) throw new ObkException(SharedStrings.ErrorMarksInvalid);
                mark.Validate();
                if (mark.SubjectId != subject.Id || !marks.Add(mark.Id))
                    throw new ObkException(SharedStrings.ErrorMarksInvalid);
            }
        }
    }
}

public sealed record MarksSemester
{
    public required string SemesterId { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SemesterId) || Mark.ParseDate(From) > Mark.ParseDate(To))
            throw new ObkException(SharedStrings.ErrorMarksCapabilitiesInvalid);
    }
}

public sealed record MarksCapabilities(bool CanShowMarks, MarksSemester? Semester);
public sealed record MarksDownload(MarksData Data, MarksSemester? Semester);

public sealed record MarksArchive
{
    public required MarksData Data { get; init; }
    public required string SyncedAt { get; init; }
    public MarksSemester? Semester { get; init; }
    public required Dictionary<string, string> Acknowledged { get; init; }

    public bool IsNew(Mark mark) => !Acknowledged.TryGetValue(mark.Key, out var version) || version != mark.Version;

    public void Validate()
    {
        if (Data is null || Acknowledged is null ||
            Acknowledged.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Value is null ||
                pair.Value.Length != 64 || pair.Value.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ObkException(SharedStrings.ErrorLocalDataDamaged);
        Data.Validate();
        _ = Mark.ParseDate(SyncedAt);
        Semester?.Validate();
    }
}
