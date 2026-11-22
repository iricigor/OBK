#if DEBUG
using OBK.Core;
using System.Text.Json;

namespace OBK.AndroidApp;

internal static class VerificationData
{
    public static async Task PrepareAsync(LocalStore store, AndroidDataProtection protection, string directory)
    {
        var value = "Fictional attachment content."u8.ToArray();
        var encrypted = protection.Protect(value);
        var another = protection.Protect(value);
        if (encrypted.SequenceEqual(another)) throw new ObkException("Verification failed: encryption reused its nonce.");
        encrypted[^1] ^= 1;
        var rejected = false;
        try { protection.Unprotect(encrypted); }
        catch (ObkException) { rejected = true; }
        if (!rejected) throw new ObkException("Verification failed: modified ciphertext was accepted.");

        const string url = "https://fictional-school.example";
        var year = SchoolYear.Current();
        var attachment = new Attachment { Id = "sample-note", Name = "trip-details.txt" };
        foreach (var user in new[] { "anna", "tomas" })
        {
            if (store.LoadArchive(url, user, year) is { } existing)
            {
                AddListSamples(existing.Folders["received"], year);
                store.SaveArchive(url, user, year, "Fictional school", existing.Folders);
                continue;
            }
            store.SaveAccount(url, user, user == "anna" ? "Anna" : "Tomas", "Fictional school");
            var folders = Folders.Empty();
            folders["received"].Add(new Message
            {
                Id = "sample-trip",
                Title = user == "anna" ? "School trip: Friday arrangements" : "Next week's class meeting",
                Text = "<p>This is <strong>fictional sample data</strong> for the Android reader.</p>" +
                    "<p>Meet at the school entrance at 08:15. Bring lunch and a water bottle.</p>" +
                    "<p>Details: <a href=\"https://example.com\">example.com</a></p>",
                SentDate = new DateTimeOffset(year.StartYear, 11, 20, 8, 0, 0, TimeSpan.Zero).ToString("O"),
                Sender = JsonSerializer.SerializeToElement(new { Name = "Eva Novak" }),
                Attachments = [attachment]
            });
            folders["noticeboard"].Add(new Message
            {
                Id = "sample-notice", Title = "School library opening hours",
                Text = "<p>The library is open after classes. This notice is fictional.</p>",
                SentDate = new DateTimeOffset(year.StartYear, 11, 19, 8, 0, 0, TimeSpan.Zero).ToString("O"),
                Sender = JsonSerializer.SerializeToElement(new { Name = "School library" }), Attachments = []
            });
            await store.SaveAttachmentAsync(url, user, attachment, new MemoryStream(value), value.Length, "text/plain");
            AddListSamples(folders["received"], year);
            store.SaveArchive(url, user, year, "Fictional school", folders);
        }
        var copy = store.ExportAttachment(url, "anna", attachment.Id, Path.Combine(directory, "verification.txt"));
        if (!File.ReadAllBytes(copy).SequenceEqual(value))
            throw new ObkException("Verification failed: attachment content did not round-trip.");
        File.Delete(copy);
        store.SaveAccount(url, "anna", "Anna", "Fictional school");
        if (Platform.CurrentActivity?.Intent?.GetBooleanExtra("obk_verification_saved_password", false) == true)
            store.RememberPassword(url, "anna", "fictional-password");
        global::Android.Util.Log.Info("OBK.Verify", "Keystore nonce/tamper checks and encrypted archive/attachment round-trip passed.");
    }

    private static void AddListSamples(List<Message> messages, SchoolYear year)
    {
        foreach (var index in Enumerable.Range(1, 2))
        {
            var id = $"sample-list-{index}";
            if (messages.Any(message => message.Id == id)) continue;
            messages.Add(new Message
            {
                Id = id,
                Title = index == 1 ? "Class meeting: arrangements and questions for the next school term" : "Library hours",
                Text = "<p>Fictional content for message-list verification.</p>",
                SentDate = new DateTimeOffset(year.StartYear, 11, 20 - index, 9, 30, 0, TimeSpan.Zero).ToString("O"),
                Sender = JsonSerializer.SerializeToElement(new { Name = index == 1 ? "Fictional school administration and class teachers" : "School library" }),
                Attachments = []
            });
        }
    }
}
#endif
