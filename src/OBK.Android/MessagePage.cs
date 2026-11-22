using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class MessagePage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Login account;
    private readonly VerticalStackLayout content = new() { Padding = 24, Spacing = 16 };
    private readonly Label status = new();
    private readonly List<Button> actions = [];
    private readonly Message message;
    private bool loaded;
    private bool busy;

    public MessagePage(ReaderSession session, Login account, Message message)
    {
        this.session = session;
        this.account = account;
        this.message = message;
        Title = "Message";
        content.Add(new Label { Text = message.Title, FontSize = 24, FontAttributes = FontAttributes.Bold });
        content.Add(new Label { Text = $"{message.SenderName}\n{message.Date:g}" });
        content.Add(new Label { Text = "Offline copy. Opening this message does not contact your school." });
        content.Add(status);
        Content = new ScrollView { Content = content };
        ReaderTheme.Apply(this);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (loaded) return;
        loaded = true;
        try
        {
            var parts = await Task.Run(() => MessageRenderer.Render(message.Text));
            var text = new FormattedString();
            foreach (var part in parts)
            {
                var span = new Span
                {
                    Text = part.Text,
                    FontAttributes = (part.Bold || part.Heading ? FontAttributes.Bold : FontAttributes.None) |
                        (part.Italic ? FontAttributes.Italic : FontAttributes.None),
                    TextDecorations = part.Underline ? TextDecorations.Underline : TextDecorations.None
                };
                text.Spans.Add(span);
            }
            content.Add(new Label { FormattedText = text, FontSize = 16 });
            var links = parts.Where(part => part.Link is not null).Select(part => part.Link!).Distinct().ToList();
            if (links.Count > 0) content.Add(new Label { Text = "Links", FontAttributes = FontAttributes.Bold });
            foreach (var link in links)
            {
                var button = new Button { Text = link, LineBreakMode = LineBreakMode.WordWrap };
                button.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    if (!await Launcher.TryOpenAsync(link))
                        throw new ObkException("No installed app can open this link.");
                });
                actions.Add(button);
                content.Add(button);
            }
            if (message.Attachments.Count > 0)
                content.Add(new Label { Text = "Attachments", FontAttributes = FontAttributes.Bold });
            foreach (var attachment in message.Attachments)
            {
                var info = await Task.Run(() => session.Store.AttachmentInfo(account.Url, account.Username, attachment.Identifier));
                content.Add(new Label { Text = attachment.SafeName });
                if (info is null)
                {
                    content.Add(new Label { Text = "Not downloaded. Refresh this account to cache the attachment." });
                    continue;
                }
                var open = new Button { Text = "Open attachment", IsVisible = FileNames.CanOpen(attachment.SafeName) };
                var share = new Button { Text = "Share / save a copy" };
                open.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    var path = await session.ExportAsync(account, attachment);
                    var file = AttachmentFile(path, info.ContentType);
                    if (!await Launcher.OpenAsync(new OpenFileRequest("Open attachment", file)))
                        throw new ObkException("No installed app can open this attachment. Use Share / save a copy instead.");
                });
                share.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    var path = await session.ExportAsync(account, attachment);
                    var file = AttachmentFile(path, info.ContentType);
                    await Share.RequestAsync(new ShareFileRequest("Share attachment", new ShareFile(file)));
                });
                actions.Add(open);
                actions.Add(share);
                content.Add(open);
                content.Add(share);
            }
            if (message.Attachments.Count > 0)
                content.Add(new Label { Text = "Opening or sharing creates a decrypted copy in OBK's private cache. It is cleared on the next app start; copies saved in another app are outside OBK's control." });
            ReaderTheme.Apply(content);
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            status.Text = error.Message;
            await DisplayAlertAsync("Cannot read this message", error.Message, "OK");
        }
    }

    private static ReadOnlyFile AttachmentFile(string path, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return new ReadOnlyFile(path);
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is not { } mime)
            throw new ObkException("The attachment has an invalid content type.");
        return new ReadOnlyFile(path, mime);
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        actions.ForEach(button => button.IsEnabled = false);
        try { await action(); }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException or FeatureNotSupportedException)
        {
            status.Text = error.Message;
            await DisplayAlertAsync("Cannot open or share", error.Message, "OK");
        }
        finally
        {
            busy = false;
            actions.ForEach(button => button.IsEnabled = true);
        }
    }
}
