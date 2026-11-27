using System.Security.Cryptography;
using System.Text;
using OBK.Core;

namespace OBK.AndroidApp;

/// <summary>Offline navigation for one notification; account IDs never include passwords or display names.</summary>
internal sealed record NotificationDestination(IReadOnlyList<string> AccountIds, string Folder)
{
    internal static NotificationDestination AllInbox { get; } = new([], "received");

    internal static string AccountId(Login account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Url + "\n" + account.Username)));

    internal IReadOnlyList<Login> ResolveAccounts(IReadOnlyList<Login> known) => AccountIds.Count == 0 ? known :
        known.Where(account => AccountIds.Contains(AccountId(account), StringComparer.Ordinal)).ToArray();

    internal static NotificationDestination FromPayload(string[]? accountIds, string? folder)
    {
        // Notifications posted before destination support opened all-children Inbox.
        if (accountIds is null && folder is null) return AllInbox;
        if (accountIds is null || accountIds.Any(id => id is null || id.Length != 64 || !id.All(Uri.IsHexDigit)) ||
            folder is not ("received" or "noticeboard") || (accountIds.Length == 0 && folder != "received"))
            throw new InvalidDataException("Invalid notification destination.");
        return new(accountIds.Distinct(StringComparer.Ordinal).ToArray(), folder);
    }
}
