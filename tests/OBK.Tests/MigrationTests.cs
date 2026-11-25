using Microsoft.Data.Sqlite;
using OBK.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OBK.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "obk-tests-" + Guid.NewGuid().ToString("N"));
    private const string Url = "https://school.example";
    private readonly SchoolYear year = new(2026);
    private LocalStore Store => new(Path.Combine(directory, "cache.sqlite3"));
    private static Message Message(string id, string date = "2026-09-01T00:00:00+02:00", string text = "Private content",
        List<Attachment>? attachments = null) => new()
        { Id = id, Title = "A message", Text = text, SentDate = date, Attachments = attachments ?? [] };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Page(params Message[] messages) => Json(new { Messages = messages });

    [Fact]
    public async Task InjectedProtectionPersistsAccountsArchivesAndAttachmentChunks()
    {
        using var protection = new TestProtection();
        var path = Path.Combine(directory, "portable.sqlite3");
        var store = new LocalStore(path, protection);
        store.SaveLogin(Url, "child", "secret", true);
        var folders = Folders.Empty();
        folders["received"].Add(Message("portable"));
        store.SaveArchive(Url, "child", year, "School", folders);
        var attachment = new Attachment { Id = "file", Name = "note.txt" };
        var payload = Encoding.UTF8.GetBytes(new string('x', 70000));
        await store.SaveAttachmentAsync(Url, "child", attachment, new MemoryStream(payload), payload.Length);

        var reopened = new LocalStore(path, protection);
        Assert.Equal("secret", Assert.Single(reopened.RememberedLogins()).Password);
        Assert.Equal("portable", Assert.Single(reopened.LoadArchive(Url, "child", year)!.Folders["received"]).Id);
        var exported = reopened.ExportAttachment(Url, "child", "file", Path.Combine(directory, "note.txt"));
        Assert.Equal(payload, File.ReadAllBytes(exported));
    }

    [Fact]
    public void OpenedMessagesTreatExistingMessagesAsOpenedOnlyOnFirstUse()
    {
        using var protection = new TestProtection();
        var path = Path.Combine(directory, "opened.sqlite3");
        var store = new LocalStore(path, protection);
        Assert.Equal(["old"], store.OpenedMessages(Url, "child", ["old"]));
        Assert.Equal(["old"], store.OpenedMessages(Url, "child", ["old", "arrived-later"]));
        store.MarkOpened(Url + "/", "child", "arrived-later");
        var reopened = new LocalStore(path, protection);
        Assert.Equal(["arrived-later", "old"], reopened.OpenedMessages(Url, "child", []).Order());
        Assert.Empty(reopened.OpenedMessages(Url, "sibling", []));
    }

    [Fact]
    public void InjectedProtectionRejectsWrongKeysWithoutReplacingStoredData()
    {
        using var original = new TestProtection();
        using var wrongKey = new TestProtection();
        var path = Path.Combine(directory, "portable.sqlite3");
        new LocalStore(path, original).SaveLogin(Url, "child", "secret", true);
        var unreadable = new LocalStore(path, wrongKey);
        Assert.Throws<ObkException>(() => unreadable.RememberedLogins());
        Assert.Throws<ObkException>(() => unreadable.SaveLogin(Url, "child", "replacement", true));
        Assert.Equal("secret", Assert.Single(new LocalStore(path, original).RememberedLogins()).Password);
    }

    private sealed class TestProtection : ILocalDataProtection, IDisposable
    {
        private readonly AesGcm cipher = new(RandomNumberGenerator.GetBytes(32), 16);
        public byte[] Protect(byte[] value)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var payload = new byte[12 + 16 + value.Length];
            nonce.CopyTo(payload, 0);
            cipher.Encrypt(nonce, value, payload.AsSpan(28), payload.AsSpan(12, 16));
            return payload;
        }
        public byte[] Unprotect(byte[] value)
        {
            var plaintext = new byte[value.Length - 28];
            try
            {
                cipher.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plaintext);
                return plaintext;
            }
            catch (CryptographicException error) { throw new ObkException("Test data could not be decrypted.", error); }
        }
        public void Dispose() => cipher.Dispose();
    }

    [Theory]
    [InlineData("2026-08-31", 2025)]
    [InlineData("2026-09-01", 2026)]
    [InlineData("2027-01-01", 2026)]
    public void SchoolYearBoundary(string today, int expected) => Assert.Equal(expected, SchoolYear.Current(DateTime.Parse(today)).StartYear);

    [Fact]
    public void SchoolDirectorySearchMatchesNamesTownsAndDistinctUrlParts()
    {
        var schools = Enumerable.Range(1, 8).Select(index => new SchoolDirectoryEntry
        {
            Id = $"S{index}",
            Name = $"Základní škola {index} Bystřice",
            SchoolUrl = $"https://zs{index}-bystřice.bakalari.cz",
            UrlValid = true,
            MunicipalityMatches = [index == 1 ? "Malá Bystřice" : $"Town {index}"]
        });
        var directory = new SchoolDirectory(schools);

        var byName = directory.Search("bystrice");
        Assert.Equal(8, byName.Total);
        Assert.Equal(6, byName.Visible.Count);
        Assert.Single(directory.Search("malabyst").Visible);
        Assert.Single(directory.Search("zs7-bystřice").Visible);
        Assert.Equal(0, directory.Search("https://www.bakalari.cz").Total);
        Assert.Equal("S7", directory.FindByUrl("https://zs7-bystřice.bakalari.cz/")?.Id);
    }

    [Theory]
    [InlineData("http://school.example")]
    [InlineData("https://user:pass@school.example")]
    [InlineData("https://school.example?secret=x")]
    [InlineData("https://school.example#fragment")]
    [InlineData("https://school.example/login/")]
    [InlineData("https://school.example\\other")]
    [InlineData("https://school.example:0")]
    public void UnsafeSchoolUrlsAreRejected(string url) => Assert.Throws<ObkException>(() => KomensClient.NormalizeUrl(url));

    [Fact]
    public async Task LoginIsFormEncodedAndTokensStayOutOfStorage()
    {
        var handler = new FakeHandler(Json(new { access_token = "test-token" }));
        using var client = new KomensClient(Url, handler);
        Assert.Equal("test-token", await client.SignInAsync(" user ", "p&ss"));
        Assert.Equal("/api/login", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("client_id=ANDR", handler.Requests[0].Body);
        Assert.Contains("password=p%26ss", handler.Requests[0].Body);
        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task TransientNetworkFailuresAreRetriedOnceWithTheSameRequest()
    {
        var handler = new FlakyHandler(1, () => Json(new { access_token = "test-token" }));
        using (var client = new KomensClient(Url, handler))
            Assert.Equal("test-token", await client.SignInAsync("user", "p&ss"));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, body => Assert.Contains("password=p%26ss", body));

        var offline = new FlakyHandler(2, () => Json(new { access_token = "unused" }));
        using (var client = new KomensClient(Url, offline))
            Assert.Contains("Connection failed", (await Assert.ThrowsAsync<ObkException>(() => client.SignInAsync("user", "pass"))).Message);
        Assert.Equal(2, offline.Bodies.Count);

        var slow = new FlakyHandler(0, () => Json(new { }), hang: true);
        using (var client = new KomensClient(Url, slow, TimeSpan.FromMilliseconds(100)))
            Assert.Contains("did not respond", (await Assert.ThrowsAsync<ObkException>(() => client.SchoolNameAsync("token"))).Message);
        Assert.Equal(2, slow.Bodies.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailuresAreNotSuccessfulSyncs(HttpStatusCode status)
    {
        using var client = new KomensClient(Url, new FakeHandler(new HttpResponseMessage(status)));
        await Assert.ThrowsAsync<ObkException>(() => client.SignInAsync("u", "p"));
    }

    [Fact]
    public async Task OversizedAndInvalidJsonAreRejected()
    {
        foreach (var body in new[] { "not json", new string('x', 2 * 1024 * 1024 + 1) })
        {
            using var client = new KomensClient(Url, new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body) }));
            await Assert.ThrowsAsync<ObkException>(() => client.SchoolNameAsync("token"));
        }
    }

    [Fact]
    public async Task PaginationContinuesPastShortPagesAndFiltersSchoolYearLocally()
    {
        var handler = new FakeHandler(Page(Message("old", "2026-08-31T23:59:59+02:00"), Message("start")),
            Page(Message("last", "2027-08-31T23:59:59.999+02:00"), Message("next", "2027-09-01T00:00:00+02:00")), Page());
        using var client = new KomensClient(Url, handler);
        var result = await client.FetchFolderAsync("token", "received", year);
        Assert.Equal(new[] { "last", "start" }, result.Select(m => m.Id));
        Assert.Equal(3, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("2026-09-01T00:00:00.000", first.RootElement.GetProperty("DateFrom").GetString());
        Assert.Equal("2027-08-31T23:59:59.999", first.RootElement.GetProperty("DateTo").GetString());
        Assert.False(first.RootElement.GetProperty("IncludeHidden").GetBoolean());
    }

    [Fact]
    public async Task IncrementalSyncOverlapsNewestDateMergesAndPreservesOlderMessages()
    {
        var cached = new List<Message> { Message("old"), Message("latest", "2026-11-21T09:30:00+01:00", "old text") };
        var handler = new FakeHandler(Page(Message("latest", "2026-11-21T09:30:00+01:00", "edited"),
            Message("same-time", "2026-11-21T09:30:00+01:00")), Page());
        using var client = new KomensClient(Url, handler);
        var result = await client.FetchFolderAsync("token", "received", year, cached);
        Assert.Equal(3, result.Count);
        Assert.Equal("edited", result.Single(m => m.Id == "latest").Text);
        using var request = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("2026-11-21T09:30:00.000", request.RootElement.GetProperty("DateFrom").GetString());
    }

    [Fact]
    public async Task RepeatedPagesAndMalformedMessagesFailWithoutReplacingArchive()
    {
        var store = Store;
        var original = Folders.Empty();
        original["received"].Add(Message("original"));
        store.SaveArchive(Url, "u", year, "School", original);
        using (var client = new KomensClient(Url, new FakeHandler(Page(Message("one")), Page(Message("one")))))
            await Assert.ThrowsAsync<ObkException>(() => client.FetchYearAsync("t", year));
        using (var client = new KomensClient(Url, new FakeHandler(Json(new { Messages = new[] { new { Id = "bad" } } }))))
            await Assert.ThrowsAsync<ObkException>(() => client.FetchYearAsync("t", year));
        Assert.Equal("original", Assert.Single(store.LoadArchive(Url, "u", year)!.Folders["received"]).Id);
        Assert.Throws<ObkException>(() => store.SaveArchive(Url, "u", year, "School", new() { ["received"] = [] }));
    }

    [Fact]
    public async Task FullRefreshUsesOnlyReadOnlyListEndpointsAndDropsStaleMessages()
    {
        var handler = new FakeHandler(Page(Message("new")), Page(), Page(), Page());
        using var client = new KomensClient(Url, handler);
        var result = await client.FetchYearAsync("token", year);
        Assert.Single(result["received"]);
        Assert.Empty(result["sent"]);
        Assert.Empty(result["noticeboard"]);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("Bearer token", r.Authorization);
            Assert.StartsWith("/api/3/komens/messages/", r.Uri.AbsolutePath);
        });
    }

    [Fact]
    public async Task ExistingPythonSchemaAndDpapiPayloadsAreReadWithoutMigration()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.sqlite3");
        var fixture = Environment.GetEnvironmentVariable("OBK_LEGACY_FIXTURE");
        if (fixture is not null) File.Copy(fixture, path);
        else
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE settings (name TEXT PRIMARY KEY, value BLOB NOT NULL);
                CREATE TABLE archives (url TEXT NOT NULL, username TEXT NOT NULL, year INTEGER NOT NULL,
                    school_name TEXT NOT NULL, messages BLOB NOT NULL, synced_at TEXT NOT NULL,
                    PRIMARY KEY (url, username, year));
                CREATE TABLE attachments (url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL, metadata BLOB NOT NULL,
                    PRIMARY KEY (url, username, id));
                CREATE TABLE attachment_chunks (url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY (url, username, id, chunk_index));
                INSERT INTO settings VALUES ('remembered_login', $login);
                INSERT INTO settings VALUES ('last_account', $account);
                INSERT INTO archives VALUES ('https://school.example', 'u', 2026, 'Legacy school', $messages, '2026-11-21T12:00:00+00:00');
                INSERT INTO attachments VALUES ('https://school.example', 'u', 'legacy-file', $metadata);
                INSERT INTO attachment_chunks VALUES ('https://school.example', 'u', 'legacy-file', 0, $chunk);
                """;
            byte[] Protect(string json) => ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            command.Parameters.AddWithValue("$login", Protect("""{"url": "https://school.example", "username": "u", "password": "fixture-password"}"""));
            command.Parameters.AddWithValue("$account", Protect("""{"url": "https://school.example", "username": "u"}"""));
            command.Parameters.AddWithValue("$messages", Protect("""
                {"received": [{"Id": "legacy", "Title": "Žák", "Text": "<p>Legacy body</p>", "SentDate": "2026-09-01T00:00:00+02:00",
                "Attachments": [{"Id": "legacy-file", "Name": "agenda.pdf"}], "Read": false, "UnknownField": {"keep": true}}],
                "sent": [], "noticeboard": []}
                """));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("legacy payload"))).ToLowerInvariant();
            command.Parameters.AddWithValue("$metadata", Protect($"{{\"name\":\"agenda.pdf\",\"size\":14,\"sha256\":\"{hash}\",\"content_type\":\"application/pdf\"}}"));
            command.Parameters.AddWithValue("$chunk", Protect("legacy payload"));
            command.ExecuteNonQuery();
        }
        var store = new LocalStore(path);
        Assert.Equal("fixture-password", store.RememberedLogin()!.Password);
        Assert.Equal("u", store.LastAccount()!.Username);
        var archive = store.LoadArchive(Url + "/", "u", year)!;
        var message = Assert.Single(archive.Folders["received"]);
        Assert.Equal("Žák", message.Title);
        Assert.True(message.Extra!["UnknownField"].GetProperty("keep").GetBoolean());
        Assert.Equal("Legacy school", archive.SchoolName);
        var export = Path.Combine(directory, "legacy.pdf");
        store.ExportAttachment(Url, "u", "legacy-file", export);
        Assert.Equal("legacy payload", await File.ReadAllTextAsync(export));
        store.SaveArchive(Url, "u", year, archive.SchoolName, archive.Folders);
        Assert.True(store.LoadArchive(Url, "u", year)!.Folders["received"][0].Extra!["UnknownField"].GetProperty("keep").GetBoolean());
        if (Environment.GetEnvironmentVariable("OBK_LEGACY_OUTPUT") is { } output)
            File.Copy(path, output, overwrite: true);
    }

    [Fact]
    public void CredentialOptInAccountIsolationAndNoPlaintextFallback()
    {
        var store = Store;
        store.SaveLogin(Url, "u", "private-password", false, "Anežka", "School A");
        Assert.Null(store.RememberedLogin());
        Assert.Equal("Anežka", Assert.Single(store.KnownLogins()).ChildName);
        Assert.Null(Assert.Single(store.KnownLogins()).Password);
        store.SaveLogin(Url, "u", "private-password", true, "Anežka", "School A");
        Assert.Equal("Anežka", store.RememberedLogin()!.ChildName);
        Assert.Equal("School A", store.LastAccount()!.SchoolName);
        store.SaveLogin("https://other-school.example", "second", "other-password", true, "Jan", "School B");
        Assert.Equal(2, store.RememberedLogins().Count);
        Assert.Equal(2, store.KnownLogins().Count);
        store.SaveLogin(Url, "u", "private-password", false);
        Assert.Single(store.RememberedLogins());
        Assert.Equal("second", store.RememberedLogin()!.Username);
        store.SaveLogin(Url, "u", "private-password", true, "Anežka", "School A");
        var folders = Folders.Empty();
        folders["received"].Add(Message("private"));
        store.SaveArchive(Url, "u", year, "School", folders);
        Assert.DoesNotContain("private-password", Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)));
        Assert.DoesNotContain("Private content", Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)));
        Assert.Null(store.LoadArchive(Url, "other-user", year));
        Assert.Null(store.LoadArchive("https://other.example", "u", year));
        Assert.Null(store.LoadArchive(Url, "u", new(2025)));
        store.ForgetCredentials();
        Assert.Null(store.RememberedLogin());
        Assert.NotNull(store.LoadArchive(Url, "u", year));
        Assert.Contains(store.KnownLogins(), profile => profile.Url == Url && profile.Username == "u");
        store.SaveAccount("https://other-school.example", "second", "Jan", "School B");
        Assert.Equal("Anežka", store.KnownLogins().Single(profile => profile.Username == "u").ChildName);
        Assert.Throws<ObkException>(() => LocalStore.Decode<Login>(Encoding.UTF8.GetBytes("""{"password":"plaintext"}""")));
    }

    [Fact]
    public void SettingsAccountActionsKeepOtherAccountsAndActiveAccount()
    {
        var store = Store;
        store.SaveLogin("https://other-school.example", "second", "other-password", true, "Jan", "School B");
        store.SaveLogin(Url, "u", "unused", false, "Anežka", "School A");

        store.RenameAccount("https://other-school.example", "second", "  Honza ");
        Assert.Equal("Honza", store.KnownLogins().Single(profile => profile.Username == "second").ChildName);
        Assert.Equal("Honza", store.RememberedLogins().Single().ChildName);
        Assert.Equal("u", store.LastAccount()!.Username);

        store.RenameAccount(Url, "u", "");
        Assert.Null(store.KnownLogins().Single(profile => profile.Username == "u").ChildName);
        Assert.Null(store.LastAccount()!.ChildName);

        store.RememberPassword(Url, "u", "new-password");
        Assert.Equal("new-password", store.RememberedLogins().Single(profile => profile.Username == "u").Password);
        Assert.Equal("u", store.LastAccount()!.Username);

        store.ForgetPassword("https://other-school.example", "second");
        Assert.Equal("u", Assert.Single(store.RememberedLogins()).Username);
        Assert.Equal("Honza", store.KnownLogins().Single(profile => profile.Username == "second").ChildName);
        Assert.Throws<ObkException>(() => store.RememberPassword("https://unknown.example", "x", "p"));
    }

    [Fact]
    public async Task EncryptedAttachmentsAreDeduplicatedReusedAndMarkedOnExport()
    {
        var store = Store;
        var attachment = new Attachment { Id = "file/one?x", Name = "agenda.pdf" };
        var folders = Folders.Empty();
        folders["received"].Add(Message("one", attachments: [attachment]));
        folders["sent"].Add(Message("two", attachments: [attachment]));
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("private-file-content", 10000)));
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using (var client = new KomensClient(Url, handler))
            Assert.Equal(1, await client.DownloadAttachmentsAsync("t", folders, store, "u"));
        Assert.Single(handler.Requests);
        Assert.EndsWith("/file%2Fone%3Fx", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.DoesNotContain("private-file-content", Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)));
        using (var client = new KomensClient(Url, new FakeHandler()))
            Assert.Equal(1, await client.DownloadAttachmentsAsync("t", folders, store, "u"));
        var destination = Path.Combine(directory, "export.pdf");
        store.ExportAttachment(Url, "u", attachment.Id, destination);
        Assert.Equal(payload, File.ReadAllBytes(destination));
        Assert.Contains("ZoneId=3", File.ReadAllText(destination + ":Zone.Identifier"));
        Assert.Null(store.AttachmentInfo(Url, "other-user", attachment.Id));
    }

    [Fact]
    public async Task FailedReplacementRollsBackAndCorruptExportPreservesDestination()
    {
        var store = Store;
        var attachment = new Attachment { Id = "file", Name = "agenda.pdf" };
        await store.SaveAttachmentAsync(Url, "u", attachment, new MemoryStream("original"u8.ToArray()));
        await Assert.ThrowsAsync<ObkException>(() => store.SaveAttachmentAsync(Url, "u", attachment, new MemoryStream("short"u8.ToArray()), 100));
        var destination = Path.Combine(directory, "export.pdf");
        store.ExportAttachment(Url, "u", "file", destination);
        Assert.Equal("original", File.ReadAllText(destination));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAttachmentAsync(Url, "u", attachment, new BrokenStream()));
        using (var connection = new SqliteConnection($"Data Source={store.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE attachment_chunks SET payload = $payload";
            command.Parameters.AddWithValue("$payload", LocalStore.Protect("bad"u8.ToArray()));
            command.ExecuteNonQuery();
        }
        Assert.Throws<ObkException>(() => store.ExportAttachment(Url, "u", "file", destination));
        Assert.Equal("original", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(directory, ".obk-*"));
    }

    [Theory]
    [InlineData("../../agenda.pdf", "agenda.pdf")]
    [InlineData("..\\..\\agenda.pdf", "agenda.pdf")]
    [InlineData("CON.pdf", "_CON.pdf")]
    [InlineData("LPT¹.txt", "_LPT¹.txt")]
    [InlineData("bad:name?.pdf", "bad_name_.pdf")]
    [InlineData("file.pdf. ", "file.pdf")]
    public void ExportFilenamesAreSafe(string original, string expected) => Assert.Equal(expected, FileNames.Sanitize(original));

    [Fact]
    public void HtmlRemainsInertWithReadableFormattingAndNoFetchedResources()
    {
        var parts = MessageRenderer.Render("""
            <p>Hello &amp; <strong>school</strong><br>Next line</p><a href="https://school.example/info">Details</a>
            <script>alert("secret")</script><style>hidden</style><iframe>bad</iframe>
            <img src="https://tracker.example/pixel" alt="Diagram"><ul><li>First</li><li>Second</li></ul>
            <table><tr><td>A</td><td>B</td></tr></table><pre>line 1
              line 2</pre>
            """);
        var text = string.Concat(parts.Select(p => p.Text));
        Assert.Contains("Hello & school\nNext line", text);
        Assert.Contains(parts, p => p.Text == "school" && p.Bold);
        Assert.Contains(parts, p => p.Text == "Details" && p.Link == "https://school.example/info");
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("tracker.example", text);
        Assert.Contains("[Image: Diagram]", text);
        Assert.Contains("- First\n- Second", text);
        Assert.Contains("\tA\tB", text);
        Assert.Contains("line 1\n  line 2", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

internal sealed class FakeHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> pending = new(responses);
    public List<(Uri Uri, HttpMethod Method, string Body, string? Authorization)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Method,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString()));
        return pending.Dequeue();
    }
}

internal sealed class FlakyHandler(int failures, Func<HttpResponseMessage> success, bool hang = false) : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Bodies.Count <= failures) throw new HttpRequestException("offline");
        return success();
    }
}

internal sealed class BrokenStream : MemoryStream
{
    private bool started;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (started) throw new IOException("interrupted");
        started = true;
        "partial"u8.CopyTo(buffer.Span);
        return ValueTask.FromResult(7);
    }
}
