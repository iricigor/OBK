using OBK.AndroidApp.Localization;
using OBK.Core;
#if DEBUG
using OBK.Verification;
#endif

namespace OBK.AndroidApp;

internal sealed class ReaderSession(LocalStore store, SchoolDirectory schools, string attachmentDirectory)
{
    // Foreground downloads exclude each other and wait for a running scheduled check; all share one process-wide sync gate.
    private static readonly SemaphoreSlim ForegroundGate = new(1, 1);
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    public LocalStore Store { get; } = store;
    public SchoolDirectory Schools { get; } = schools;
    public string AttachmentDirectory { get; } = attachmentDirectory;
    public bool IsVerification { get; private init; }
    internal InitialDownload? Download { get; private set; }
    internal bool ReadingDuringDownload { get; private set; }
    internal event Action? DownloadChanged;
#if DEBUG
    internal bool IsInitialDownloadVerification { get; private init; }
    private InitialDownloadFixture? verificationDownload;
#endif
    internal static bool Syncing => SyncGate.CurrentCount == 0;
    internal static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "obk.sqlite3");

    public static Task<ReaderSession> OpenAsync() => Task.Run(async () =>
    {
        var verification = false;
#if DEBUG
        verification = Platform.CurrentActivity?.Intent?.GetBooleanExtra("obk_verification", false) == true;
        var initialVerification = verification &&
            Platform.CurrentActivity?.Intent?.GetBooleanExtra("obk_verification_initial_download", false) == true;
#endif
        var path = verification ? Path.Combine(FileSystem.AppDataDirectory, "verification.sqlite3") : DatabasePath;
#if DEBUG
        if (initialVerification)
        {
            path = Path.Combine(FileSystem.AppDataDirectory, "verification-login.sqlite3");
            if (File.Exists(path)) File.Delete(path);
        }
#endif
        var protection = new AndroidDataProtection(File.Exists(path));
        var check = "OBK encryption check"u8.ToArray();
        if (!protection.Unprotect(protection.Protect(check)).SequenceEqual(check))
            throw new ObkException(AppStrings.ErrorStorageCheckFailed);
        var store = new LocalStore(path, protection);
        using var stream = typeof(ReaderSession).Assembly.GetManifestResourceStream("OBK.schools.json.gz") ??
            throw new ObkException(AppStrings.ErrorSchoolDirectoryMissing);
        var schools = SchoolDirectory.Load(stream);
        var directory = Path.Combine(FileSystem.CacheDirectory, "attachment-view");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(file);
#if DEBUG
        if (verification && !initialVerification) await VerificationData.PrepareAsync(store, protection, directory);
#else
        await Task.CompletedTask;
#endif
        return new ReaderSession(store, schools, directory)
        {
            IsVerification = verification,
#if DEBUG
            IsInitialDownloadVerification = initialVerification
#endif
        };
    });

    public async Task SynchronizeAsync(Login login, string password, bool remember, IProgress<string> progress)
    {
        if (IsVerification)
            throw new ObkException(AppStrings.ErrorVerificationCannotSync);
        if (!await ForegroundGate.WaitAsync(0))
            throw new ObkException(AppStrings.ErrorSyncInProgress);
        try
        {
            if (!await SyncGate.WaitAsync(0))
            {
                progress.Report(AppStrings.StatusWaitingForCheck);
                await SyncGate.WaitAsync();
            }
            try
            {
                await Task.Run(async () =>
                {
                    var year = SchoolYear.Current();
                    await DownloadAsync(Store, login, password, remember, year,
                        Store.LoadArchive(login.Url, login.Username, year), progress);
                });
            }
            finally { SyncGate.Release(); }
        }
        finally { ForegroundGate.Release(); }
    }

    internal async Task SynchronizeInitialAsync(InitialDownload download, string password, bool remember, Action<string> waiting)
    {
        if (IsVerification)
        {
#if DEBUG
            if (!IsInitialDownloadVerification) throw new ObkException(AppStrings.ErrorVerificationCannotSync);
#else
            throw new ObkException(AppStrings.ErrorVerificationCannotSync);
#endif
        }

        if (!await ForegroundGate.WaitAsync(0)) throw new ObkException(AppStrings.ErrorSyncInProgress);
        Download = download;
        ReadingDuringDownload = false;
        void Changed() => DownloadChanged?.Invoke();
        download.Changed += Changed;
        try
        {
            if (!await SyncGate.WaitAsync(0))
            {
                waiting(AppStrings.StatusWaitingForCheck);
                await SyncGate.WaitAsync();
            }
            try
            {
#if DEBUG
                using var client = new KomensClient(download.Account.Url,
                    IsInitialDownloadVerification ? verificationDownload = new InitialDownloadFixture(automatic: true) : null,
                    IsInitialDownloadVerification ? TimeSpan.FromMinutes(2) : null);
#else
                using var client = new KomensClient(download.Account.Url);
#endif
                await Task.Run(() => download.RunAsync(client, Store, password, remember));
            }
            finally { SyncGate.Release(); }
        }
        finally
        {
            download.Changed -= Changed;
            ForegroundGate.Release();
            Changed();
        }
    }

    internal async Task RefreshMarksAsync(Login login, string password, Action<string> waiting)
    {
        if (IsVerification) throw new ObkException(AppStrings.ErrorVerificationCannotSync);
        if (!await ForegroundGate.WaitAsync(0)) throw new ObkException(AppStrings.ErrorSyncInProgress);
        try
        {
            if (!await SyncGate.WaitAsync(0))
            {
                waiting(AppStrings.StatusWaitingForCheck);
                await SyncGate.WaitAsync();
            }
            try
            {
                using var client = new KomensClient(login.Url);
                var token = await client.SignInAsync(login.Username, password);
                await client.RefreshMarksAsync(token, Store, login.Username);
            }
            finally { SyncGate.Release(); }
        }
        finally { ForegroundGate.Release(); }
    }

    internal void ReadWhileDownloading()
    {
        if (Download?.State is not { MessagesReady: true } state ||
            (state.Running && !state.AttachmentsStarted))
            throw new ObkException(OBK.Core.Localization.SharedStrings.DownloadWait);
        ReadingDuringDownload = true;
        DownloadChanged?.Invoke();
#if DEBUG
        verificationDownload?.ContinueAfterOne();
#endif
    }

    internal void ClearInitialDownloadStatus()
    {
        if (Download?.State.Running == true) throw new ObkException(AppStrings.ErrorSyncInProgress);
        Download = null;
        ReadingDuringDownload = false;
        DownloadChanged?.Invoke();
    }

    /// <summary>
    /// Downloads one account like Refresh for a scheduled check, or returns null when it has no saved password. It
    /// returns the folders before and after, and the IDs opened in the reader. The opened baseline is recorded before
    /// downloading, so arrivals stay new in the reader; an account's very first download is recorded as opened
    /// afterwards, as the reader's first load would. Saved logins and the last used account are left unchanged.
    /// </summary>
    internal static async Task<(Dictionary<string, List<Message>>? Before, Dictionary<string, List<Message>> After,
        HashSet<string> Opened)?> CheckAsync(LocalStore store, Login login)
    {
        await SyncGate.WaitAsync();
        try
        {
            // Read under the lock, so a password forgotten while earlier children were checked is not used.
            if (store.RememberedLogins().FirstOrDefault(saved => saved.Url == login.Url && saved.Username == login.Username)
                ?.Password is not { } password) return null;
            var year = SchoolYear.Current();
            var before = store.LoadArchive(login.Url, login.Username, year);
            if (before is not null) store.OpenedMessages(login.Url, login.Username, Ids(before.Folders));
            var after = await DownloadAsync(store, login, password, null, year, before, null);
            return (before?.Folders, after, store.OpenedMessages(login.Url, login.Username, Ids(after)));
        }
        finally { SyncGate.Release(); }
    }

    private static IEnumerable<string> Ids(Dictionary<string, List<Message>> folders) =>
        folders.Values.SelectMany(folder => folder).Select(message => message.Id);

    /// <param name="remember">
    /// Whether to save the password; null for a scheduled check, which changes neither saved logins nor the last used account.
    /// </param>
    private static async Task<Dictionary<string, List<Message>>> DownloadAsync(LocalStore store, Login login, string password,
        bool? remember, SchoolYear year, Archive? cached, IProgress<string>? progress)
    {
        using var client = new KomensClient(login.Url);
        var token = await client.SignInAsync(login.Username, password);
        if (remember is { } save) store.SaveLogin(login.Url, login.Username, password, save, login.ChildName, login.SchoolName);
        var schoolName = await client.SchoolNameAsync(token);
        var folders = await client.FetchYearAsync(token, year, cached?.Folders, progress);
        await client.DownloadAttachmentsAsync(token, folders, store, login.Username, progress);
        store.SaveArchive(login.Url, login.Username, year, schoolName, folders);
        if (remember is not null) store.SaveAccount(login.Url, login.Username, login.ChildName, schoolName);
        return folders;
    }

    public Task<string> ExportAsync(Login login, Attachment attachment) => Task.Run(() =>
    {
        var name = $"{Guid.NewGuid():N}-{attachment.SafeName}";
        return Store.ExportAttachment(login.Url, login.Username, attachment.Identifier,
            Path.Combine(AttachmentDirectory, name));
    });
}
