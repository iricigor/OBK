using OBK.Core.Localization;

namespace OBK.Core;

public enum InitialDownloadPhase { Authentication, Messages, Attachments, Complete }

public sealed record AttachmentDownloadProgress(int Completed, int Total, string? FileName = null);

public sealed record InitialDownloadState(InitialDownloadPhase Phase, string? Detail = null,
    int CompletedAttachments = 0, int TotalAttachments = 0, bool AttachmentsStarted = false, string? Error = null)
{
    public bool Authenticated => Phase != InitialDownloadPhase.Authentication;
    public bool MessagesReady => Phase is InitialDownloadPhase.Attachments or InitialDownloadPhase.Complete;
    public bool Running => Phase != InitialDownloadPhase.Complete && Error is null;
    public bool CanContinueInBackground => Running && MessagesReady && AttachmentsStarted &&
        CompletedAttachments < TotalAttachments;
    public double Fraction => TotalAttachments == 0 ? 0 : (double)CompletedAttachments / TotalAttachments;
    public string AttachmentCount => Localized.Format(SharedStrings.DownloadAttachmentCount,
        CompletedAttachments, TotalAttachments);
    public string Status => Error is not null ? Localized.Format(Phase switch
    {
        InitialDownloadPhase.Authentication => SharedStrings.DownloadAuthenticationFailed,
        InitialDownloadPhase.Messages => SharedStrings.DownloadMessagesFailed,
        _ => SharedStrings.DownloadAttachmentsFailed
    }, Error) : Phase switch
    {
        InitialDownloadPhase.Authentication => SharedStrings.DownloadSigningIn,
        InitialDownloadPhase.Messages => Detail ?? SharedStrings.DownloadMessages,
        InitialDownloadPhase.Attachments => Detail is null ? SharedStrings.DownloadAttachments :
            Localized.Format(SharedStrings.DownloadAttachmentFile, Detail),
        _ => SharedStrings.DownloadComplete
    };
    public string Details => Localized.Format(SharedStrings.DownloadDetailsText, AttachmentCount, Status,
        Running ? SharedStrings.DownloadBackgroundLimit : "");
}

/// <summary>
/// Sign-in downloads save all message folders before starting attachments. The caller owns the task and its
/// synchronization gate even when the user explicitly leaves the sign-in screen to read those messages.
/// </summary>
public sealed class InitialDownload(Login account, SchoolYear year)
{
    private InitialDownloadState state = new(InitialDownloadPhase.Authentication);
    private int started;
    public Login Account { get; private set; } = account with { Password = null };
    public SchoolYear Year { get; } = year;
    public InitialDownloadState State => Volatile.Read(ref state);
    public event Action? Changed;

    private void Publish(InitialDownloadState value)
    {
        Volatile.Write(ref state, value);
        Changed?.Invoke();
    }

    public async Task RunAsync(KomensClient client, LocalStore store, string password, bool remember,
        bool fullRefresh = false)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("An initial download can only be started once.");
        Publish(State);
        try
        {
            var token = await client.SignInAsync(Account.Username, password);
            Publish(new(InitialDownloadPhase.Messages));
            store.SaveLogin(Account.Url, Account.Username, password, remember, Account.ChildName, Account.SchoolName);
            var schoolName = await client.SchoolNameAsync(token);
            var cached = fullRefresh ? null : store.LoadArchive(Account.Url, Account.Username, Year);
            var folders = await client.FetchYearAsync(token, Year, cached?.Folders,
                new CallbackProgress<string>(text => Publish(State with { Detail = text })));
            foreach (var attachment in folders.Values.SelectMany(messages => messages).SelectMany(message => message.Attachments))
            {
                _ = attachment.Identifier;
                _ = attachment.SafeName;
            }
            store.SaveArchive(Account.Url, Account.Username, Year, schoolName, folders);
            Account = Account with { SchoolName = Account.SchoolName ?? schoolName };
            store.SaveAccount(Account.Url, Account.Username, Account.ChildName, Account.SchoolName);
            Publish(new(InitialDownloadPhase.Attachments));
            await client.DownloadAttachmentsAsync(token, folders, store, Account.Username,
                attachmentProgress: new CallbackProgress<AttachmentDownloadProgress>(progress =>
                    Publish(State with
                    {
                        CompletedAttachments = progress.Completed,
                        TotalAttachments = progress.Total,
                        Detail = progress.FileName,
                        AttachmentsStarted = State.AttachmentsStarted || progress.FileName is not null
                    })));
            Publish(State with { Phase = InitialDownloadPhase.Complete, Detail = null });
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            Publish(State with { Error = error.Message });
            if (error is ObkException) throw;
            throw new ObkException(error.Message, error);
        }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
