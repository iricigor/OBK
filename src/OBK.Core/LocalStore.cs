using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OBK.Core;

public sealed class LocalStore
{
    public string Path { get; }
    public LocalStore(string? path = null)
    {
        Path = path ?? DatabasePath();
        Access(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (name TEXT PRIMARY KEY, value BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS archives (
                    url TEXT NOT NULL, username TEXT NOT NULL, year INTEGER NOT NULL,
                    school_name TEXT NOT NULL, messages BLOB NOT NULL, synced_at TEXT NOT NULL,
                    PRIMARY KEY (url, username, year));
                CREATE TABLE IF NOT EXISTS attachments (
                    url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL, metadata BLOB NOT NULL,
                    PRIMARY KEY (url, username, id));
                CREATE TABLE IF NOT EXISTS attachment_chunks (
                    url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL, payload BLOB NOT NULL,
                    PRIMARY KEY (url, username, id, chunk_index));
                """;
            command.ExecuteNonQuery();
            return true;
        });
    }

    private static string DatabasePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            throw new ObkException("Windows LOCALAPPDATA is unavailable; no local database was opened.");
        return System.IO.Path.Combine(local, "OBK", "obk.sqlite3");
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private T Access<T>(Func<SqliteConnection, T> operation)
    {
        try { using var connection = Open(); return operation(connection); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new ObkException("Cannot access the local OBK database. Check disk space and access.");
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql,
        SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    public static byte[] Protect(byte[] value)
    {
        try { return ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new ObkException("Windows could not encrypt local data. No plaintext fallback is used.");
        }
    }

    public static byte[] Unprotect(byte[] value)
    {
        try { return ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new ObkException("Windows could not decrypt local data. Use the Windows account that created it; no plaintext fallback is used.");
        }
    }

    public static byte[] Encode<T>(T value) => Protect(JsonSerializer.SerializeToUtf8Bytes(value));
    public static T Decode<T>(byte[] value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Unprotect(value)) ??
                throw new ObkException("Local OBK data is damaged and could not be decoded.");
        }
        catch (JsonException) { throw new ObkException("Local OBK data is damaged and could not be decoded."); }
    }

    private Login? Setting(string name) => Access(connection =>
    {
        using var command = Command(connection, "SELECT value FROM settings WHERE name = $name",
            parameters: [("$name", name)]);
        var value = command.ExecuteScalar();
        if (value is null) return null;
        var login = Decode<Login>((byte[])value);
        if (login.Url is null || login.Username is null || (name == "remembered_login" && login.Password is null))
            throw new ObkException("The saved sign-in setting is damaged. Sign in again.");
        return login;
    });
    public Login? RememberedLogin() => RememberedLogins().LastOrDefault();
    public Login? LastAccount() => Setting("last_account");
    public IReadOnlyList<Login> KnownLogins()
    {
        var remembered = RememberedLogins();
        var history = Access(connection =>
        {
            using var command = Command(connection,
                "SELECT value FROM settings WHERE name = 'account_history'");
            if (command.ExecuteScalar() is not byte[] value) return (IReadOnlyList<Login>)[];
            var profiles = Decode<List<Login>>(value);
            if (profiles.Any(profile => profile.Url is null || profile.Username is null))
                throw new ObkException("The saved account list is damaged. Sign in again.");
            return (IReadOnlyList<Login>)profiles;
        });
        var archived = Access(connection =>
        {
            using var command = Command(connection,
                """
                SELECT url, username, school_name
                FROM archives
                GROUP BY url, username
                ORDER BY MAX(synced_at)
                """);
            using var reader = command.ExecuteReader();
            var profiles = new List<Login>();
            while (reader.Read())
                profiles.Add(new Login(reader.GetString(0), reader.GetString(1),
                    SchoolName: reader.GetString(2)));
            return (IReadOnlyList<Login>)profiles;
        });
        var known = LastAccount() is { } last ? history.Concat(archived).Append(last) :
            history.Concat(archived);
        return known
            .Concat(remembered)
            .GroupBy(profile => (profile.Url, profile.Username))
            .Select(group => remembered.FirstOrDefault(profile =>
                    group.Key == (profile.Url, profile.Username)) ?? group.Last())
            .ToArray();
    }

    public IReadOnlyList<Login> RememberedLogins() => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT value FROM settings WHERE name = 'remembered_logins'");
        if (command.ExecuteScalar() is byte[] value)
        {
            var profiles = Decode<List<Login>>(value);
            if (profiles.Any(profile => profile.Url is null || profile.Username is null ||
                profile.Password is null))
                throw new ObkException("The saved sign-in list is damaged. Sign in again.");
            return (IReadOnlyList<Login>)profiles;
        }
        var legacy = Setting("remembered_login");
        return legacy is null ? [] : [legacy];
    });

    public void ForgetCredentials() => Access(connection =>
    {
        using var command = Command(connection,
            "DELETE FROM settings WHERE name IN ('remembered_login', 'remembered_logins')");
        return command.ExecuteNonQuery();
    });

    public void ForgetLogin(string url, string username) =>
        SaveRememberedLogins(RememberedLogins()
            .Where(profile => !SameAccount(profile, KomensClient.NormalizeUrl(url), username))
            .ToArray());

    public void SaveAccount(string url, string username, string? childName = null, string? schoolName = null)
    {
        url = KomensClient.NormalizeUrl(url);
        var login = new Login(url, username, ChildName: OptionalLabel(childName),
            SchoolName: OptionalLabel(schoolName));
        var account = Encode(login);
        var history = Encode(UpsertKnownHistory(KnownLogins(), login));
        Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('last_account', $value)",
                transaction, ("$value", account));
            command.ExecuteNonQuery();
            using var saved = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('account_history', $value)",
                transaction, ("$value", history));
            saved.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }

    public void SaveLogin(string url, string username, string password, bool remember,
        string? childName = null, string? schoolName = null)
    {
        url = KomensClient.NormalizeUrl(url);
        childName = OptionalLabel(childName);
        schoolName = OptionalLabel(schoolName);
        var login = new Login(url, username, ChildName: childName, SchoolName: schoolName);
        var account = Encode(login);
        var history = Encode(UpsertKnownHistory(KnownLogins(), login));
        var remembered = RememberedLogins()
            .Where(profile => !SameAccount(profile, url, username))
            .ToList();
        if (remember) remembered.Add(new Login(url, username, password, childName, schoolName));
        var encodedRemembered = Encode(remembered);
        Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, "INSERT OR REPLACE INTO settings VALUES ('last_account', $value)",
                transaction, ("$value", account));
            command.ExecuteNonQuery();
            using var saved = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('remembered_logins', $value)", transaction,
                ("$value", encodedRemembered));
            saved.ExecuteNonQuery();
            using var savedHistory = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('account_history', $value)", transaction,
                ("$value", history));
            savedHistory.ExecuteNonQuery();
            using var legacy = Command(connection,
                "DELETE FROM settings WHERE name = 'remembered_login'", transaction);
            legacy.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }

    private void SaveRememberedLogins(IReadOnlyList<Login> profiles) => Access(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection,
            "INSERT OR REPLACE INTO settings VALUES ('remembered_logins', $value)",
            transaction, ("$value", Encode(profiles)));
        command.ExecuteNonQuery();
        using var legacy = Command(connection,
            "DELETE FROM settings WHERE name = 'remembered_login'", transaction);
        legacy.ExecuteNonQuery();
        transaction.Commit();
        return true;
    });

    private static bool SameAccount(Login profile, string url, string username) =>
        string.Equals(profile.Url, url, StringComparison.Ordinal) &&
        string.Equals(profile.Username, username, StringComparison.Ordinal);

    private static List<Login> UpsertKnownHistory(IEnumerable<Login> profiles, Login profile)
    {
        var result = profiles.Where(item => !SameAccount(item, profile.Url, profile.Username))
            .Select(item => item with { Password = null }).ToList();
        result.Add(profile with { Password = null });
        return result;
    }

    private static string? OptionalLabel(string? value) =>
        value?.Trim() is { Length: > 0 } label ? label : null;

    public string SaveArchive(string url, string username, SchoolYear year, string school,
        Dictionary<string, List<Message>> folders)
    {
        Folders.Validate(folders);
        var payload = Encode(folders);
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        return Access(connection =>
        {
            using var command = Command(connection,
                "INSERT OR REPLACE INTO archives VALUES ($url, $user, $year, $school, $messages, $stamp)",
                parameters: [("$url", KomensClient.NormalizeUrl(url)), ("$user", username),
                    ("$year", year.StartYear), ("$school", school), ("$messages", payload), ("$stamp", stamp)]);
            command.ExecuteNonQuery();
            return stamp;
        });
    }

    public Archive? LoadArchive(string url, string username, SchoolYear year) => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT school_name, messages, synced_at FROM archives WHERE url = $url AND username = $user AND year = $year",
            parameters: [("$url", KomensClient.NormalizeUrl(url)), ("$user", username), ("$year", year.StartYear)]);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var folders = Decode<Dictionary<string, List<Message>>>((byte[])reader[1]);
        Folders.Validate(folders);
        var stamp = reader.GetString(2);
        if (!DateTimeOffset.TryParse(stamp, out _)) throw new ObkException("The offline synchronization timestamp is damaged.");
        return new Archive(reader.GetString(0), folders, stamp);
    });

    public AttachmentInfo? AttachmentInfo(string url, string username, string id) => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT metadata FROM attachments WHERE url = $url AND username = $user AND id = $id",
            parameters: [("$url", KomensClient.NormalizeUrl(url)), ("$user", username), ("$id", id)]);
        var value = command.ExecuteScalar();
        if (value is null) return null;
        var info = Decode<AttachmentInfo>((byte[])value);
        if (info.Name is null || info.Name != FileNames.Sanitize(info.Name) || info.Size < 0 ||
            info.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(info.Sha256, "^[0-9a-f]{64}$"))
            throw new ObkException("Cached attachment metadata is damaged.");
        return info;
    });

    public async Task<long> SaveAttachmentAsync(string url, string username, Attachment attachment, Stream source,
        long? expectedSize = null, string contentType = "", CancellationToken cancellation = default)
    {
        url = KomensClient.NormalizeUrl(url);
        var id = attachment.Identifier;
        if (expectedSize < 0) throw new ObkException("The attachment has an invalid download size.");
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var delete = Command(connection,
                "DELETE FROM attachment_chunks WHERE url = $url AND username = $user AND id = $id", transaction,
                ("$url", url), ("$user", username), ("$id", id));
            delete.ExecuteNonQuery();
            var buffer = new byte[65536];
            long size = 0;
            var index = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                var count = await source.ReadAsync(buffer, readTimeout.Token);
                if (count == 0) break;
                size += count;
                digest.AppendData(buffer, 0, count);
                using var chunk = Command(connection,
                    "INSERT INTO attachment_chunks VALUES ($url, $user, $id, $index, $payload)", transaction,
                    ("$url", url), ("$user", username), ("$id", id),
                    ("$index", index++), ("$payload", Protect(buffer[..count])));
                chunk.ExecuteNonQuery();
            }
            if (expectedSize is not null && size != expectedSize)
                throw new ObkException("The attachment download was incomplete. Retry sync; no incomplete copy was saved.");
            var metadata = Encode(new AttachmentInfo(attachment.SafeName, size,
                Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), contentType));
            using var complete = Command(connection,
                "INSERT OR REPLACE INTO attachments VALUES ($url, $user, $id, $metadata)", transaction,
                ("$url", url), ("$user", username), ("$id", id), ("$metadata", metadata));
            complete.ExecuteNonQuery();
            transaction.Commit();
            return size;
        }
        catch (SqliteException) { throw new ObkException("Cannot cache an attachment. Check disk space and access."); }
    }

    public string ExportAttachment(string url, string username, string id, string destination)
    {
        var info = AttachmentInfo(url, username, id) ??
            throw new ObkException("This attachment is not downloaded. Sign in and sync first.");
        destination = System.IO.Path.GetFullPath(destination);
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, $".obk-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                Access(connection =>
                {
                    using var command = Command(connection,
                        "SELECT chunk_index, payload FROM attachment_chunks WHERE url = $url AND username = $user AND id = $id ORDER BY chunk_index",
                        parameters: [("$url", KomensClient.NormalizeUrl(url)), ("$user", username), ("$id", id)]);
                    using var reader = command.ExecuteReader();
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var index = 0;
                    long size = 0;
                    while (reader.Read())
                    {
                        if (reader.GetInt32(0) != index++) throw new ObkException("The cached attachment is incomplete.");
                        var chunk = Unprotect((byte[])reader[1]);
                        digest.AppendData(chunk);
                        size += chunk.Length;
                        output.Write(chunk);
                    }
                    if (size != info.Size || Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != info.Sha256)
                        throw new ObkException("The cached attachment failed its integrity check.");
                    return true;
                });
            }
            File.WriteAllText(temporary + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n", Encoding.ASCII);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ObkException("Cannot write the attachment copy. Check disk access and that Windows downloaded-file protection is supported by the destination.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ObkException("Could not remove a temporary decrypted attachment copy from the destination folder.");
            }
        }
    }
}
