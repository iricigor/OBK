using OBK.AndroidApp.Localization;
using OBK.Core;

namespace OBK.AndroidApp;

internal sealed class MessagePage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Login account;
    private readonly VerticalStackLayout content = new() { Padding = 20, Spacing = 16 };
    private readonly Label status = new() { IsVisible = false };
    private readonly List<Button> actions = [];
    private readonly Message message;
    private bool loaded;
    private bool busy;

    public MessagePage(ReaderSession session, Login account, Message message)
    {
        this.session = session;
        this.account = account;
        this.message = message;
        Title = AppStrings.MessagePageTitle;
        content.Add(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = message.Title, FontSize = 20, FontAttributes = FontAttributes.Bold },
                new Label { Text = message.SenderName, FontSize = 14 },
                ReaderTheme.Metadata(new Label { Text = $"{message.Date:g}" })
            }
        });
        content.Add(status);
        Content = new ScrollView { Content = content };
        ReaderTheme.Apply(this);
        this.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Surface, ReaderTheme.DarkSurface);
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
            if (links.Count > 0) content.Add(new Label { Text = AppStrings.MessageLinksHeader, FontAttributes = FontAttributes.Bold });
            foreach (var link in links)
            {
                var button = ReaderTheme.Subtle(new Button { Text = link, LineBreakMode = LineBreakMode.WordWrap, HorizontalOptions = LayoutOptions.Fill });
                button.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    if (!await Launcher.TryOpenAsync(link))
                        throw new ObkException(AppStrings.ErrorNoAppForLink);
                });
                actions.Add(button);
                content.Add(button);
            }
            if (message.Attachments.Count > 0)
                content.Add(new Label { Text = AppStrings.MessageAttachmentsHeader, FontAttributes = FontAttributes.Bold });
            foreach (var attachment in message.Attachments)
            {
                var info = await Task.Run(() => session.Store.AttachmentInfo(account.Url, account.Username, attachment.Identifier));
                var fileContent = new VerticalStackLayout { Spacing = 8 };
                fileContent.Add(new Label { Text = attachment.SafeName, FontSize = 14, FontAttributes = FontAttributes.Bold });
                content.Add(ReaderTheme.Panel(fileContent, 12));
                if (info is null)
                {
                    fileContent.Add(ReaderTheme.Metadata(new Label { Text = AppStrings.MessageAttachmentNotDownloaded }));
                    continue;
                }
                var open = new Button { Text = AppStrings.MessageOpenAttachment, IsVisible = FileNames.CanOpen(attachment.SafeName), LineBreakMode = LineBreakMode.WordWrap };
                var share = ReaderTheme.Subtle(new Button { Text = AppStrings.MessageShareAttachment, LineBreakMode = LineBreakMode.WordWrap });
                open.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    var path = await session.ExportAsync(account, attachment);
                    var file = AttachmentFile(path, info.ContentType);
                    if (!await Launcher.OpenAsync(new OpenFileRequest(AppStrings.MessageOpenAttachmentChooserTitle, file)))
                        throw new ObkException(AppStrings.ErrorNoAppForAttachment);
                });
                share.Clicked += async (_, _) => await PerformAsync(async () =>
                {
                    var path = await session.ExportAsync(account, attachment);
                    var file = AttachmentFile(path, info.ContentType);
                    await Share.RequestAsync(new ShareFileRequest(AppStrings.MessageShareAttachmentChooserTitle, new ShareFile(file)));
                });
                actions.Add(open);
                actions.Add(share);
                var fileActions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
                fileActions.Add(open, 0);
                fileActions.Add(share, 1);
                fileContent.Add(fileActions);
            }
            content.Add(ReaderTheme.Metadata(new Label { Text = AppStrings.MessageOfflineNotice }));
            if (message.Attachments.Count > 0)
                content.Add(ReaderTheme.Metadata(new Label { Text = AppStrings.MessageDecryptedCopyNotice }));
            ReaderTheme.Apply(content);
        }
        catch (Exception error) when (error is ObkException or IOException or UnauthorizedAccessException)
        {
            status.Text = error.Message;
            status.IsVisible = true;
            await DisplayAlertAsync(AppStrings.MessageReadFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
    }

    private static ReadOnlyFile AttachmentFile(string path, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return new ReadOnlyFile(path);
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is not { } mime)
            throw new ObkException(AppStrings.ErrorInvalidAttachmentContentType);
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
            status.IsVisible = true;
            await DisplayAlertAsync(AppStrings.MessageActionFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally
        {
            busy = false;
            actions.ForEach(button => button.IsEnabled = true);
        }
    }
}
