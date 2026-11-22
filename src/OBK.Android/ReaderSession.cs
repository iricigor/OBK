using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class ReaderSession(LocalStore store, SchoolDirectory schools, string attachmentDirectory)
{
    private readonly SemaphoreSlim syncGate = new(1, 1);
    public LocalStore Store { get; } = store;
    public SchoolDirectory Schools { get; } = schools;
    public string AttachmentDirectory { get; } = attachmentDirectory;
    public bool IsVerification { get; private init; }

    public static Task<ReaderSession> OpenAsync() => Task.Run(async () =>
    {
        var verification = false;
#if DEBUG
        verification = Platform.CurrentActivity?.Intent?.GetBooleanExtra("obk_verification", false) == true;
#endif
        var path = Path.Combine(FileSystem.AppDataDirectory, verification ? "verification.sqlite3" : "obk.sqlite3");
        var protection = new AndroidDataProtection(File.Exists(path));
        var check = "OBK encryption check"u8.ToArray();
        if (!protection.Unprotect(protection.Protect(check)).SequenceEqual(check))
            throw new ObkException("Android encrypted storage failed its initialization check.");
        var store = new LocalStore(path, protection);
        using var stream = typeof(ReaderSession).Assembly.GetManifestResourceStream("OBK.schools.json.gz") ??
            throw new ObkException("The offline school directory is missing from this installation.");
        var schools = SchoolDirectory.Load(stream);
        var directory = Path.Combine(FileSystem.CacheDirectory, "attachment-view");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(file);
#if DEBUG
        if (verification) await VerificationData.PrepareAsync(store, protection, directory);
#else
        await Task.CompletedTask;
#endif
        return new ReaderSession(store, schools, directory) { IsVerification = verification };
    });

    public async Task SynchronizeAsync(Login login, string password, bool remember, IProgress<string> progress)
    {
        if (IsVerification)
            throw new ObkException("Fictional verification data cannot contact a school. Restart without the verification flag to sign in.");
        if (!await syncGate.WaitAsync(0))
            throw new ObkException("Another account is still downloading. Wait for it to finish before refreshing again.");
        try
        {
            await Task.Run(async () =>
            {
                using var client = new KomensClient(login.Url);
                var token = await client.SignInAsync(login.Username, password);
                Store.SaveLogin(login.Url, login.Username, password, remember, login.ChildName, login.SchoolName);
                var schoolName = await client.SchoolNameAsync(token);
                var year = SchoolYear.Current();
                var cached = Store.LoadArchive(login.Url, login.Username, year);
                var folders = await client.FetchYearAsync(token, year, cached?.Folders, progress);
                await client.DownloadAttachmentsAsync(token, folders, Store, login.Username, progress);
                Store.SaveArchive(login.Url, login.Username, year, schoolName, folders);
                Store.SaveAccount(login.Url, login.Username, login.ChildName, schoolName);
            });
        }
        finally { syncGate.Release(); }
    }

    public Task<string> ExportAsync(Login login, Attachment attachment) => Task.Run(() =>
    {
        var name = $"{Guid.NewGuid():N}-{attachment.SafeName}";
        return Store.ExportAttachment(login.Url, login.Username, attachment.Identifier,
            Path.Combine(AttachmentDirectory, name));
    });
}
