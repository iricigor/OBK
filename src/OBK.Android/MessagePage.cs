using Microsoft.Maui.Controls.Shapes;
using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class MessagePage : ContentPage
{
    private readonly ReaderSession session;
    private readonly Login account;
    private readonly MessageRef item;
    private readonly Archive? archive;
    private readonly Func<Login, MessageRef, Task> openRelated;
    private readonly VerticalStackLayout content = new() { Padding = 20, Spacing = 16 };
    private readonly Label status = new() { IsVisible = false };
    private readonly List<Button> actions = [];
    private readonly Message message;
    private bool loaded;
    private bool busy;
    private bool loading;
    internal bool Busy => busy || loading;
    internal MessagePage Rebuild(Func<Login, MessageRef, Task> open) => new(session, account, item, archive, open);

    /// <param name="item">The message and its server folder key (received, sent or noticeboard).</param>
    /// <param name="archive">The owning account's archive, searched for possibly related messages.</param>
    /// <param name="openRelated">Opens a related message with the owning account on top of this page.</param>
    public MessagePage(ReaderSession session, Login account, MessageRef item, Archive? archive, Func<Login, MessageRef, Task> openRelated)
    {
        this.session = session;
        this.account = account;
        this.item = item;
        this.archive = archive;
        this.openRelated = openRelated;
        message = item.Message;
        Title = AppStrings.MessagePageTitle;
        LanguageToolbar.Add(this);
        var subject = new Label { Text = message.Title, FontAttributes = FontAttributes.Bold };
        subject.SetDynamicResource(Label.FontSizeProperty, "ReaderSubjectSize");
        var sender = new Label { Text = message.SenderName };
        sender.SetDynamicResource(Label.FontSizeProperty, "ReaderSenderSize");
        var date = ReaderTheme.Metadata(new Label
        {
            Text = message.Date.ToString(SharedStrings.DateLongFormat, LanguageSettings.Culture)
        }, "ReaderMetaSize");
        sender.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.MessageHeaderSecondary, ReaderTheme.DarkMessageHeaderSecondary);
        date.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.MessageHeaderSecondary, ReaderTheme.DarkMessageHeaderSecondary);
        var header = ReaderTheme.Panel(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                subject, sender, date
            }
        }, 16);
        header.StrokeThickness = 0;
        header.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.MessageHeaderSurface, ReaderTheme.DarkMessageHeaderSurface);
        content.Add(header);
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
        loading = true;
        try
        {
            var (parts, related) = await Task.Run(() => (MessageRenderer.Render(message.Text), FindRelated()));
            if (related.Count > 0) content.Add(RelatedBand(related));
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
            var body = new Label { FormattedText = text };
            body.SetDynamicResource(Label.FontSizeProperty, "ReaderBodySize");
            content.Add(body);
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
            var attachments = new VerticalStackLayout { Spacing = 12 };
            if (message.Attachments.Count > 0)
            {
                attachments.Add(new Label { Text = AppStrings.MessageAttachmentsHeader, FontAttributes = FontAttributes.Bold });
                var band = ReaderTheme.Panel(attachments, 16);
                band.StrokeThickness = 0;
                band.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.AttachmentSurface, ReaderTheme.DarkAttachmentSurface);
                content.Add(band);
            }
            foreach (var attachment in message.Attachments)
            {
                var info = await Task.Run(() => session.Store.AttachmentInfo(account.Url, account.Username, attachment.Identifier));
                var fileContent = new VerticalStackLayout { Spacing = 8 };
                fileContent.Add(new Label { Text = attachment.SafeName, FontSize = 14, FontAttributes = FontAttributes.Bold });
                attachments.Add(ReaderTheme.Panel(fileContent, 12));
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
        finally { loading = false; }
    }

    private IReadOnlyList<RelatedLink> FindRelated()
    {
        if (archive is null) return [];
        var all = archive.Folders.SelectMany(folder => folder.Value.Select(other => new MessageRef(other, folder.Key)));
        return RelatedLinks.Build(MessageInsights.FindRelated(item, all), message.Date, DateTime.Today);
    }

    private Border RelatedBand(IReadOnlyList<RelatedLink> links)
    {
        var title = new Label
        {
            Text = SharedStrings.RelatedTitle, FontSize = 13, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center
        };
        title.SetAppThemeColor(Label.TextColorProperty, ReaderTheme.MessageHeaderSecondary, ReaderTheme.DarkMessageHeaderSecondary);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level2);
        var help = ReaderTheme.Icon("info", SharedStrings.RelatedHelpName);
        help.Padding = 14;
        help.Margin = new Thickness(-8, 0, 0, 0);
        help.Clicked += async (_, _) => await PerformAsync(() =>
            DisplayAlertAsync(SharedStrings.RelatedTitle, SharedStrings.RelatedHelpTooltip, AppStrings.AlertOkButton));
        var rows = new VerticalStackLayout { Spacing = 8 };
        foreach (var link in links) rows.Add(RelatedRow(link));
        // The 48 dp help button already leaves room around the title, so no top padding or gap is added there.
        var band = new Border
        {
            Content = new VerticalStackLayout { Children = { new HorizontalStackLayout { Children = { title, help } }, rows } },
            Padding = new Thickness(12, 0, 12, 12), StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 6 }
        };
        band.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.RelatedSurface, ReaderTheme.DarkRelatedSurface);
        return band;
    }

    private Grid RelatedRow(RelatedLink link)
    {
        var related = link.Item.Message;
        var from = link.FromYou ? SharedStrings.RelatedFromYou :
            string.Join(' ', related.SenderName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var label = Localized.Format(SharedStrings.RelatedLabel, link.When, from);
        var direction = new Span { Text = link.Direction, FontAttributes = FontAttributes.Bold };
        direction.SetAppThemeColor(Span.TextColorProperty, ReaderTheme.MessageHeaderSecondary, ReaderTheme.DarkMessageHeaderSecondary);
        var context = new Span { Text = label };
        context.SetAppThemeColor(Span.TextColorProperty, ReaderTheme.RelatedLink, ReaderTheme.DarkRelatedLink);
        var heading = new Label
        {
            FormattedText = new FormattedString { Spans = { direction, new Span { Text = " " }, context } },
            FontSize = 13, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation
        };
        var subject = new Label { Text = related.Title, FontSize = 14, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        var arrow = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center };
        ReaderTheme.SetIcon(arrow, link.IsNext ? "related_next" : "related_previous");
        var details = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12, InputTransparent = true };
        details.Add(arrow);
        details.Add(new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { heading, subject } }, 1);
        AutomationProperties.SetIsInAccessibleTree(details, false);
        var card = new Border
        {
            Content = details, Padding = new Thickness(12, 8), StrokeThickness = 1, MinimumHeightRequest = 56,
            StrokeShape = new RoundRectangle { CornerRadius = 6 }, InputTransparent = true
        };
        card.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.MessageHeaderSurface, ReaderTheme.DarkMessageHeaderSurface);
        card.SetAppTheme<Brush>(Border.StrokeProperty, new SolidColorBrush(ReaderTheme.RelatedStroke), new SolidColorBrush(ReaderTheme.DarkRelatedStroke));
        var open = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0 };
        SemanticProperties.SetDescription(open, Localized.Format(AppStrings.MessageRelatedDescription,
            Localized.Format(SharedStrings.RelatedAccessibleName, link.Direction, label, link.FullDate), related.Title));
        open.Clicked += async (_, _) => await PerformAsync(() => openRelated(account, link.Item));
        actions.Add(open);
        return new Grid { MinimumHeightRequest = 56, Children = { card, open } };
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
