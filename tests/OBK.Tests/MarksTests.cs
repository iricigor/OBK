using Microsoft.Data.Sqlite;
using OBK.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OBK.Tests;

public sealed class MarksTests : IDisposable
{
    private const string Url = "https://school.example";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "obk-marks-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "marks.sqlite3");
    private LocalStore Store => new(Database);

    private static Mark Mark(string id = "m1", string grade = "3-") => new()
    {
        Id = id, SubjectId = "math", MarkText = grade,
        MarkDate = "2026-11-27T00:00:00+01:00", EditDate = "2026-11-27T14:30:00+01:00",
        Caption = "Fictional algebra assessment", Theme = "Equations", TeacherId = "teacher-1",
        Type = "W", TypeNote = "Short test", Weight = 3
    };

    private static MarksData Data(params Mark[] marks) => new()
    {
        Subjects =
        [
            new()
            {
                Subject = new() { Id = "math", Name = "Mathematics", Abbrev = "M" },
                Marks = marks.ToList(), AverageText = "2,86 ", SubjectNote = "Fictional subject note"
            }
        ]
    };

    private static object Capabilities(bool enabled = true) => new
    {
        EnabledModules = new[] { new { Module = "Marks", Rights = enabled ? new[] { "ShowMarks" } : Array.Empty<string>() } },
        SettingModules = new
        {
            Common = new
            {
                ActualSemester = new
                {
                    SemesterId = "1", From = "2026-09-01T00:00:00+02:00", To = "2027-01-31T23:59:59+01:00"
                }
            }
        }
    };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task MarksUseAuthenticatedGetAndPreserveTextPointsAndSemester()
    {
        var points = Mark("points", "85") with { IsPoints = true, Weight = null, MaxPoints = 100, PointsText = "85/100" };
        var handler = new Handler(Json(Capabilities()), Json(Data(Mark(), points)));
        using var client = new KomensClient(Url, handler);

        var result = await client.FetchMarksAsync("sample-token");

        Assert.Equal(["/api/3/user", "/api/3/marks"], handler.Requests.Select(r => r.Path));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer sample-token", request.Authorization);
        });
        var subject = Assert.Single(result.Data.Subjects);
        Assert.Equal("2,86 ", subject.AverageText);
        Assert.Equal("3-", subject.Marks[0].MarkText);
        Assert.Null(subject.Marks[1].Weight);
        Assert.True(subject.Marks[1].IsPoints);
        Assert.Equal("85/100", subject.Marks[1].PointsText);
        Assert.Equal("1", result.Semester!.SemesterId);
    }

    [Fact]
    public async Task MissingMarksRightDoesNotCallMarksOrReplaceOfflineData()
    {
        var store = Store;
        var original = store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        var handler = new Handler(Json(Capabilities(enabled: false)));
        using var client = new KomensClient(Url, handler);

        await Assert.ThrowsAsync<ObkException>(() => client.RefreshMarksAsync("token", store, "anna"));

        Assert.Single(handler.Requests);
        Assert.Equal(original.SyncedAt, Store.LoadMarks(Url, "anna")!.SyncedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"EnabledModules\":null}")]
    [InlineData("{\"EnabledModules\":[{\"Module\":\"Marks\",\"Rights\":[3]}]}")]
    [InlineData("{\"EnabledModules\":[{\"Module\":\"Marks\"}]}")]
    [InlineData("{\"EnabledModules\":[],\"SettingModules\":{\"Common\":{\"ActualSemester\":{\"SemesterId\":\"1\"}}}}")]
    public async Task MalformedCapabilitiesAreNotTreatedAsUnsupported(string body)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var client = new KomensClient(Url, handler);
        await Assert.ThrowsAsync<ObkException>(() => client.FetchMarksAsync("token"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AbsentSemesterDoesNotInventOneOrPreventMarksRetrieval()
    {
        using var client = new KomensClient(Url, new Handler(
            Json(new { EnabledModules = new[] { new { Module = "Marks", Rights = new[] { "ShowMarks" } } } }),
            Json(Data())));

        var result = await client.FetchMarksAsync("token");

        Assert.Null(result.Semester);
        Assert.Empty(Assert.Single(result.Data.Subjects).Marks);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Subjects\":null}")]
    [InlineData("{\"Subjects\":[null]}")]
    [InlineData("{\"Subjects\":[{\"Subject\":{\"Id\":\"math\",\"Name\":\"Mathematics\"},\"Marks\":null}]}")]
    [InlineData("{\"Subjects\":[{\"Subject\":{\"Id\":\"math\",\"Name\":\"Mathematics\"},\"Marks\":[{}]}]}")]
    public async Task MalformedMarksDoNotReplaceTheSnapshot(string body)
    {
        var store = Store;
        var original = store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        using var client = new KomensClient(Url, new Handler(Json(Capabilities()),
            new(HttpStatusCode.OK) { Content = new StringContent(body) }));

        await Assert.ThrowsAsync<ObkException>(() => client.RefreshMarksAsync("token", store, "anna"));

        Assert.Equal(original.SyncedAt, Store.LoadMarks(Url, "anna")!.SyncedAt);
        Assert.Equal("3-", Assert.Single(Assert.Single(Store.LoadMarks(Url, "anna")!.Data.Subjects).Marks).MarkText);
    }

    [Fact]
    public void DuplicateOrMismatchedAssessmentsAndInvalidDatesAreRejectedBeforeSaving()
    {
        var store = Store;
        store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        foreach (var invalid in new[]
        {
            Data(Mark(), Mark()),
            Data(Mark() with { SubjectId = "different" }),
            Data(Mark() with { MarkDate = "not a date" }),
            Data(Mark() with { EditDate = "not a date" }),
            Data(Mark() with { Weight = -1 }),
            Data(Mark() with { MaxPoints = -1 })
        })
            Assert.Throws<ObkException>(() => store.SaveMarks(Url, "anna", new(invalid, null)));

        Assert.Single(Assert.Single(Store.LoadMarks(Url, "anna")!.Data.Subjects).Marks);
    }

    [Fact]
    public async Task HttpFailureLeavesTheOriginalSnapshotReadable()
    {
        var store = Store;
        var original = store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        using var client = new KomensClient(Url, new Handler(Json(Capabilities()),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ObkException>(() => client.RefreshMarksAsync("token", store, "anna"));

        Assert.Equal(original.SyncedAt, Store.LoadMarks(Url, "anna")!.SyncedAt);
    }

    [Fact]
    public void FirstSnapshotIsBaselineButAnEmptyBaselineStillDetectsLaterArrivals()
    {
        var store = Store;
        var first = store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        Assert.False(first.IsNew(Mark()));
        store.SaveMarks(Url, "tomas", new(Data(), null));
        var later = store.SaveMarks(Url, "tomas", new(Data(Mark()), null));
        Assert.True(later.IsNew(Mark()));
        Assert.False(Store.LoadMarks(Url, "anna")!.IsNew(Mark()));
    }

    [Fact]
    public void NewAndEditedVersionsPersistAndStaleAcknowledgementIsRejected()
    {
        var store = Store;
        var original = Mark();
        store.SaveMarks(Url, "anna", new(Data(original), null));
        var edited = original with { MarkText = "2" };
        var arrived = Mark("m2", "1");
        var updated = store.SaveMarks(Url + "/", "anna", new(Data(edited, arrived), null));
        Assert.True(updated.IsNew(edited));
        Assert.True(updated.IsNew(arrived));
        Assert.Throws<ObkException>(() => store.AcknowledgeMark(Url, "anna", original.SubjectId, original.Id, original.Version));
        Assert.True(Store.LoadMarks(Url, "anna")!.IsNew(edited));

        store.AcknowledgeMark(Url + "/", "anna", edited.SubjectId, edited.Id, edited.Version);
        var reopened = Store.LoadMarks(Url, "anna")!;
        Assert.False(reopened.IsNew(edited));
        Assert.True(reopened.IsNew(arrived));
        var secondEdit = edited with { Caption = "Corrected description" };
        Assert.True(store.SaveMarks(Url, "anna", new(Data(secondEdit, arrived), null)).IsNew(secondEdit));
    }

    [Fact]
    public void ServerUnreadAndConfirmationChangesDoNotCreateLocalNewVersions()
    {
        var store = Store;
        var original = Mark() with
        {
            Extra = new() { ["IsNew"] = JsonSerializer.SerializeToElement(true) }
        };
        store.SaveMarks(Url, "anna", new(Data(original), null));
        var serverFlagsChanged = original with
        {
            Extra = new()
            {
                ["IsNew"] = JsonSerializer.SerializeToElement(false),
                ["MarkConfirmationState"] = JsonSerializer.SerializeToElement("Confirmed")
            }
        };

        var refreshed = store.SaveMarks(Url, "anna", new(Data(serverFlagsChanged), null));

        Assert.False(refreshed.IsNew(serverFlagsChanged));
    }

    [Fact]
    public void StoredPayloadIsEncryptedAndIsolatedBySchoolAndAccount()
    {
        var store = Store;
        store.SaveMarks(Url, "anna", new(Data(Mark()), null));
        store.SaveMarks(Url, "tomas", new(Data(Mark(grade: "1")), null));
        store.SaveMarks("https://other-school.example", "anna", new(Data(Mark(grade: "5")), null));
        Assert.Equal("3-", Store.LoadMarks(Url + "/", "anna")!.Data.Subjects[0].Marks[0].MarkText);
        Assert.Equal("1", Store.LoadMarks(Url, "tomas")!.Data.Subjects[0].Marks[0].MarkText);
        Assert.Equal("5", Store.LoadMarks("https://other-school.example", "anna")!.Data.Subjects[0].Marks[0].MarkText);
        Assert.Null(Store.LoadMarks(Url, "not-saved"));
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM marks LIMIT 1";
        var payload = (byte[])command.ExecuteScalar()!;
        Assert.DoesNotContain("Fictional algebra assessment", Encoding.UTF8.GetString(payload));
        Assert.DoesNotContain("Fictional subject note", Encoding.UTF8.GetString(payload));
    }

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> remaining = new(responses);
        public List<(string Path, HttpMethod Method, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Method, request.Headers.Authorization?.ToString()));
            return Task.FromResult(remaining.Dequeue());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
