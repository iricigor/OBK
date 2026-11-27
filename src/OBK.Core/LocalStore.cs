using OBK.Core.Localization;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OBK.Core;

public sealed class LocalStore
{
    private readonly ILocalDataProtection protection;
    public string Path { get; }
    public LocalStore(string? path = null, ILocalDataProtection? protection = null)
    {
        if (!OperatingSystem.IsWindows() && (path is null || protection is null))
            throw new ObkException(SharedStrings.ErrorPlatformStorageRequired);
        this.protection = protection ?? new WindowsDataProtection();
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
                CREATE TABLE IF NOT EXISTS opened_messages (
                    url TEXT NOT NULL, username TEXT NOT NULL, ids BLOB NOT NULL,
                    PRIMARY KEY (url, username));
                CREATE TABLE IF NOT EXISTS marks (
                    url TEXT NOT NULL, username TEXT NOT NULL, snapshot BLOB NOT NULL,
                    PRIMARY KEY (url, username));
                """;
            command.ExecuteNonQuery();
            return true;
        });
    }

    private static string DatabasePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            throw new ObkException(SharedStrings.ErrorLocalAppDataUnavailable);
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
            throw new ObkException(SharedStrings.ErrorDatabaseAccess);
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
        if (!OperatingSystem.IsWindows())
            throw new ObkException(SharedStrings.ErrorDataProtectionUnavailable);
        try { return ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new ObkException(SharedStrings.ErrorEncryptFailed);
        }
    }

    public static byte[] Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows())
            throw new ObkException(SharedStrings.ErrorDataProtectionUnavailable);
        try { return ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new ObkException(SharedStrings.ErrorDecryptFailed);
        }
    }

    public static byte[] Encode<T>(T value) => Protect(JsonSerializer.SerializeToUtf8Bytes(value));
    public static T Decode<T>(byte[] value)
        => Deserialize<T>(Unprotect(value));

    private byte[] EncodeStored<T>(T value) => protection.Protect(JsonSerializer.SerializeToUtf8Bytes(value));
    private T DecodeStored<T>(byte[] value) => Deserialize<T>(protection.Unprotect(value));

    private static T Deserialize<T>(byte[] value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value) ??
                throw new ObkException(SharedStrings.ErrorLocalDataDamaged);
        }
        catch (JsonException) { throw new ObkException(SharedStrings.ErrorLocalDataDamaged); }
    }

    private Login? Setting(string name) => Access(connection =>
    {
        using var command = Command(connection, "SELECT value FROM settings WHERE name = $name",
            parameters: [("$name", name)]);
        var value = command.ExecuteScalar();
        if (value is null) return null;
        var login = DecodeStored<Login>((byte[])value);
        if (login.Url is null || login.Username is null || (name == "remembered_login" && login.Password is null))
            throw new ObkException(SharedStrings.ErrorSavedSignInDamaged);
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
            var profiles = DecodeStored<List<Login>>(value);
            if (profiles.Any(profile => profile.Url is null || profile.Username is null))
                throw new ObkException(SharedStrings.ErrorSavedAccountsDamaged);
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
                    group.Key == (profile.Url, profile.Username)) ??
                group.Last() with
                {
                    ChildName = group.LastOrDefault(profile => profile.ChildName is not null)?.ChildName,
                    SchoolName = group.Last().SchoolName ?? group.LastOrDefault(profile => profile.SchoolName is not null)?.SchoolName
                })
            .ToArray();
    }

    public IReadOnlyList<Login> RememberedLogins() => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT value FROM settings WHERE name = 'remembered_logins'");
        if (command.ExecuteScalar() is byte[] value)
        {
            var profiles = DecodeStored<List<Login>>(value);
            if (profiles.Any(profile => profile.Url is null || profile.Username is null ||
                profile.Password is null))
                throw new ObkException(SharedStrings.ErrorSavedSignInsDamaged);
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

    /// <summary>Changes the optional child name everywhere this account's label is stored.</summary>
    public void RenameAccount(string url, string username, string? childName)
    {
        url = KomensClient.NormalizeUrl(url);
        childName = OptionalLabel(childName);
        var known = KnownLogins();
        if (!known.Any(profile => SameAccount(profile, url, username)))
            throw new ObkException(SharedStrings.ErrorAccountNotSaved);
        Login Renamed(Login profile) => SameAccount(profile, url, username) ? profile with { ChildName = childName } : profile;
        var history = EncodeStored(known.Select(profile => Renamed(profile) with { Password = null }).ToList());
        var remembered = EncodeStored(RememberedLogins().Select(Renamed).ToList());
        var last = LastAccount() is { } account && SameAccount(account, url, username) ? EncodeStored(Renamed(account)) : null;
        Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var savedHistory = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('account_history', $value)", transaction, ("$value", history));
            savedHistory.ExecuteNonQuery();
            using var saved = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('remembered_logins', $value)", transaction, ("$value", remembered));
            saved.ExecuteNonQuery();
            using var legacy = Command(connection,
                "DELETE FROM settings WHERE name = 'remembered_login'", transaction);
            legacy.ExecuteNonQuery();
            if (last is not null)
            {
                using var current = Command(connection,
                    "INSERT OR REPLACE INTO settings VALUES ('last_account', $value)", transaction, ("$value", last));
                current.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Remembers a password for a known account without changing the active account.</summary>
    public void RememberPassword(string url, string username, string password)
    {
        url = KomensClient.NormalizeUrl(url);
        var known = KnownLogins().FirstOrDefault(profile => SameAccount(profile, url, username)) ??
            throw new ObkException(SharedStrings.ErrorAccountNotSaved);
        var remembered = RememberedLogins().Where(profile => !SameAccount(profile, url, username)).ToList();
        remembered.Add(known with { Password = password });
        SaveRememberedLogins(remembered);
    }

    /// <summary>Forgets one saved password while keeping the account, its names, and its archive.</summary>
    public void ForgetPassword(string url, string username)
    {
        url = KomensClient.NormalizeUrl(url);
        var known = KnownLogins();
        var history = EncodeStored(known.Select(profile => profile with { Password = null }).ToList());
        var remembered = EncodeStored(RememberedLogins().Where(profile => !SameAccount(profile, url, username)).ToList());
        Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var savedHistory = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('account_history', $value)", transaction, ("$value", history));
            savedHistory.ExecuteNonQuery();
            using var saved = Command(connection,
                "INSERT OR REPLACE INTO settings VALUES ('remembered_logins', $value)", transaction, ("$value", remembered));
            saved.ExecuteNonQuery();
            using var legacy = Command(connection,
                "DELETE FROM settings WHERE name = 'remembered_login'", transaction);
            legacy.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }

    public void SaveAccount(string url, string username, string? childName = null, string? schoolName = null)
    {
        url = KomensClient.NormalizeUrl(url);
        var login = new Login(url, username, ChildName: OptionalLabel(childName),
            SchoolName: OptionalLabel(schoolName));
        var account = EncodeStored(login);
        var history = EncodeStored(UpsertKnownHistory(KnownLogins(), login));
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
        var account = EncodeStored(login);
        var history = EncodeStored(UpsertKnownHistory(KnownLogins(), login));
        var remembered = RememberedLogins()
            .Where(profile => !SameAccount(profile, url, username))
            .ToList();
        if (remember) remembered.Add(new Login(url, username, password, childName, schoolName));
        var encodedRemembered = EncodeStored(remembered);
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
            transaction, ("$value", EncodeStored(profiles)));
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
        var payload = EncodeStored(folders);
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
        var folders = DecodeStored<Dictionary<string, List<Message>>>((byte[])reader[1]);
        Folders.Validate(folders);
        var stamp = reader.GetString(2);
        if (!DateTimeOffset.TryParse(stamp, out _)) throw new ObkException(SharedStrings.ErrorSyncTimestampDamaged);
        return new Archive(reader.GetString(0), folders, stamp);
    });

    /// <summary>
    /// Returns message IDs opened on this device. The first call for an account records
    /// <paramref name="existing"/> as opened, so only messages arriving afterwards appear new.
    /// </summary>
    public HashSet<string> OpenedMessages(string url, string username, IEnumerable<string> existing)
    {
        url = KomensClient.NormalizeUrl(url);
        if (StoredOpened(url, username) is { } opened) return opened;
        var baseline = existing.ToHashSet(StringComparer.Ordinal);
        SaveOpened(url, username, baseline);
        return baseline;
    }

    public void MarkOpened(string url, string username, string id)
    {
        url = KomensClient.NormalizeUrl(url);
        var opened = StoredOpened(url, username) ?? new HashSet<string>(StringComparer.Ordinal);
        if (opened.Add(id)) SaveOpened(url, username, opened);
    }

    private HashSet<string>? StoredOpened(string url, string username) => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT ids FROM opened_messages WHERE url = $url AND username = $user",
            parameters: [("$url", url), ("$user", username)]);
        return command.ExecuteScalar() is byte[] value
            ? DecodeStored<List<string>>(value).ToHashSet(StringComparer.Ordinal) : null;
    });

    private void SaveOpened(string url, string username, HashSet<string> ids)
    {
        var payload = EncodeStored(ids.Order(StringComparer.Ordinal).ToList());
        Access(connection =>
        {
            using var command = Command(connection,
                "INSERT OR REPLACE INTO opened_messages VALUES ($url, $user, $ids)",
                parameters: [("$url", url), ("$user", username), ("$ids", payload)]);
            return command.ExecuteNonQuery();
        });
    }

    public MarksArchive? LoadMarks(string url, string username) => Access(connection =>
        StoredMarks(connection, KomensClient.NormalizeUrl(url), username));

    private MarksArchive? StoredMarks(SqliteConnection connection, string url, string username,
        SqliteTransaction? transaction = null)
    {
        using var command = Command(connection,
            "SELECT snapshot FROM marks WHERE url = $url AND username = $user", transaction,
            ("$url", url), ("$user", username));
        if (command.ExecuteScalar() is not byte[] payload) return null;
        var archive = DecodeStored<MarksArchive>(payload);
        archive.Validate();
        return archive;
    }

    public MarksArchive SaveMarks(string url, string username, MarksDownload download)
    {
        download.Data.Validate();
        download.Semester?.Validate();
        url = KomensClient.NormalizeUrl(url);
        return Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var previous = StoredMarks(connection, url, username, transaction);
            var current = download.Data.Subjects.SelectMany(subject => subject.Marks).ToArray();
            var keys = current.Select(mark => mark.Key).ToHashSet(StringComparer.Ordinal);
            var acknowledged = previous is null
                ? current.ToDictionary(mark => mark.Key, mark => mark.Version, StringComparer.Ordinal)
                : previous.Acknowledged.Where(pair => keys.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var archive = new MarksArchive
            {
                Data = download.Data, Semester = download.Semester,
                SyncedAt = DateTimeOffset.UtcNow.ToString("O"), Acknowledged = acknowledged
            };
            WriteMarks(connection, transaction, url, username, archive);
            transaction.Commit();
            return archive;
        });
    }

    /// <summary>Acknowledges only the version the user actually opened, never a concurrent refresh.</summary>
    public void AcknowledgeMark(string url, string username, string subjectId, string id, string version)
    {
        url = KomensClient.NormalizeUrl(url);
        Access(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var archive = StoredMarks(connection, url, username, transaction) ??
                throw new ObkException(SharedStrings.ErrorMarksNotDownloaded);
            var mark = archive.Data.Subjects.Where(subject => subject.Subject.Id == subjectId)
                .SelectMany(subject => subject.Marks).SingleOrDefault(mark => mark.Id == id);
            if (mark is null || mark.Version != version)
                throw new ObkException(SharedStrings.ErrorMarksChanged);
            archive.Acknowledged[mark.Key] = version;
            WriteMarks(connection, transaction, url, username, archive);
            transaction.Commit();
            return true;
        });
    }

    private void WriteMarks(SqliteConnection connection, SqliteTransaction transaction,
        string url, string username, MarksArchive archive)
    {
        archive.Validate();
        using var command = Command(connection,
            "INSERT OR REPLACE INTO marks VALUES ($url, $user, $snapshot)", transaction,
            ("$url", url), ("$user", username), ("$snapshot", EncodeStored(archive)));
        command.ExecuteNonQuery();
    }

    public AttachmentInfo? AttachmentInfo(string url, string username, string id) => Access(connection =>
    {
        using var command = Command(connection,
            "SELECT metadata FROM attachments WHERE url = $url AND username = $user AND id = $id",
            parameters: [("$url", KomensClient.NormalizeUrl(url)), ("$user", username), ("$id", id)]);
        var value = command.ExecuteScalar();
        if (value is null) return null;
        var info = DecodeStored<AttachmentInfo>((byte[])value);
        if (info.Name is null || info.Name != FileNames.Sanitize(info.Name) || info.Size < 0 ||
            info.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(info.Sha256, "^[0-9a-f]{64}$"))
            throw new ObkException(SharedStrings.ErrorAttachmentMetadataDamaged);
        return info;
    });

    public async Task<long> SaveAttachmentAsync(string url, string username, Attachment attachment, Stream source,
        long? expectedSize = null, string contentType = "", CancellationToken cancellation = default,
        TimeSpan? idleTimeout = null)
    {
        url = KomensClient.NormalizeUrl(url);
        var id = attachment.Identifier;
        if (expectedSize < 0) throw new ObkException(SharedStrings.ErrorAttachmentInvalidSize);
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
                readTimeout.CancelAfter(idleTimeout ?? KomensClient.DefaultRequestTimeout);
                var count = await source.ReadAsync(buffer, readTimeout.Token);
                if (count == 0) break;
                size += count;
                digest.AppendData(buffer, 0, count);
                using var chunk = Command(connection,
                    "INSERT INTO attachment_chunks VALUES ($url, $user, $id, $index, $payload)", transaction,
                    ("$url", url), ("$user", username), ("$id", id),
                    ("$index", index++), ("$payload", protection.Protect(buffer[..count])));
                chunk.ExecuteNonQuery();
            }
            if (expectedSize is not null && size != expectedSize)
                throw new ObkException(SharedStrings.ErrorAttachmentDownloadIncomplete);
            var metadata = EncodeStored(new AttachmentInfo(attachment.SafeName, size,
                Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), contentType));
            using var complete = Command(connection,
                "INSERT OR REPLACE INTO attachments VALUES ($url, $user, $id, $metadata)", transaction,
                ("$url", url), ("$user", username), ("$id", id), ("$metadata", metadata));
            complete.ExecuteNonQuery();
            transaction.Commit();
            return size;
        }
        catch (SqliteException) { throw new ObkException(SharedStrings.ErrorAttachmentCacheFailed); }
    }

    public string ExportAttachment(string url, string username, string id, string destination)
    {
        var info = AttachmentInfo(url, username, id) ??
            throw new ObkException(SharedStrings.ErrorAttachmentNotDownloaded);
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
                        if (reader.GetInt32(0) != index++) throw new ObkException(SharedStrings.ErrorAttachmentCacheIncomplete);
                        var chunk = protection.Unprotect((byte[])reader[1]);
                        digest.AppendData(chunk);
                        size += chunk.Length;
                        output.Write(chunk);
                    }
                    if (size != info.Size || Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != info.Sha256)
                        throw new ObkException(SharedStrings.ErrorAttachmentIntegrity);
                    return true;
                });
            }
            if (OperatingSystem.IsWindows())
                File.WriteAllText(temporary + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n", Encoding.ASCII);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ObkException(OperatingSystem.IsWindows()
                ? SharedStrings.ErrorAttachmentCopyProtected
                : SharedStrings.ErrorAttachmentCopy);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ObkException(SharedStrings.ErrorAttachmentCopyCleanup);
            }

        }
    }

    private sealed class WindowsDataProtection : ILocalDataProtection
    {
        public byte[] Protect(byte[] value) => LocalStore.Protect(value);
        public byte[] Unprotect(byte[] value) => LocalStore.Unprotect(value);
    }
}
