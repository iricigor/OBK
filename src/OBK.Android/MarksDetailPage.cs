using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class MarksDetailPage : ContentPage
{
    private readonly ReaderSession session;
    private MarksEntry entry;
    private string? openedId;
    private readonly VerticalStackLayout content = new() { Spacing = 0 };
    private readonly Label status = ReaderTheme.Metadata(new Label { FontSize = 12, Padding = 18, IsVisible = false });
    private bool initial = true;
    internal bool Busy { get; private set; }
    internal MarksDetailPage Rebuild() => new(session, entry, openedId, initial);

    internal MarksDetailPage(ReaderSession session, MarksEntry entry, string? openedId = null, bool initial = true)
    {
        this.session = session;
        this.entry = entry;
        this.openedId = openedId ?? entry.Assessment?.Id;
        this.initial = initial;
        Title = entry.Subject.Subject.Name;
        LanguageToolbar.Add(this);
        Content = new ScrollView { Content = content };
        ReaderTheme.Apply(this);
        Render();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initial)
        {
            initial = false;
            if (entry.Assessment is not null) await OpenAsync(entry);
        }
    }

    private void Render()
    {
        content.Children.Clear();
        var header = new VerticalStackLayout
        {
            Padding = 20, Spacing = 8,
            Children =
            {
                new Label { Text = entry.Subject.Subject.Name, FontSize = 22, FontAttributes = FontAttributes.Bold },
                new Label { Text = entry.OwnerLabel, FontAttributes = FontAttributes.Bold },
                ReaderTheme.Metadata(new Label { Text = entry.Period, FontSize = 12 })
            }
        };
        header.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.MessageHeaderSurface, ReaderTheme.DarkMessageHeaderSurface);
        content.Add(header);
        var summary = new VerticalStackLayout
        {
            Padding = new Thickness(20, 16), Spacing = 4,
            Children =
            {
                new Label { Text = entry.AverageLabel, FontSize = 12 },
                new Label { Text = (entry with { Assessment = null }).Value, FontSize = 24, FontAttributes = FontAttributes.Bold },
                new Label { Text = entry.CountText, FontSize = 12 }
            }
        };
        summary.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.RelatedSurface, ReaderTheme.DarkRelatedSurface);
        content.Add(summary);
        content.Add(status);
        content.Add(new Label { Text = SharedStrings.MarksAssessments, Padding = new Thickness(20, 8), FontAttributes = FontAttributes.Bold });
        if (entry.Subject.Marks.Count == 0) content.Add(ReaderTheme.Metadata(new Label { Text = SharedStrings.MarksEmpty, Padding = 20 }));
        foreach (var assessment in entry.Assessments)
        {
            var button = ReaderTheme.StartAlignedButton(new Button
            {
                Text = assessment.Value + "  ·  " + assessment.Title,
                Padding = new Thickness(20, 12), LineBreakMode = LineBreakMode.WordWrap
            });
            SemanticProperties.SetDescription(button, assessment.AccessibleName);
            button.Clicked += async (_, _) => { if (!Busy) await OpenAsync(assessment); };
            content.Add(button);
            content.Add(ReaderTheme.Metadata(new Label
            {
                Text = assessment.Detail +
                    (assessment.IsNew ? " · " + SharedStrings.MarksNew : ""),
                Padding = new Thickness(20, 0, 20, 12), FontSize = 12
            }));
            if (assessment.Assessment!.Id == openedId)
            {
                var detail = ReaderTheme.Panel(new Label { Text = assessment.AssessmentDetail, FontSize = 13 }, 16);
                detail.Margin = new Thickness(20, 0, 20, 16);
                content.Add(detail);
            }
        }
        var notes = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(entry.Subject.TemporaryMark))
            notes.Add(new Label { Text = Localized.Format(SharedStrings.MarksProvisional, entry.Subject.TemporaryMark) });
        if (!string.IsNullOrWhiteSpace(entry.Subject.TemporaryMarkNote))
            notes.Add(new Label { Text = entry.Subject.TemporaryMarkNote, FontSize = 13 });
        if (!string.IsNullOrWhiteSpace(entry.Subject.SubjectNote))
            notes.Add(new Label { Text = entry.Subject.SubjectNote, FontSize = 13 });
        notes.Add(ReaderTheme.Metadata(new Label { Text = entry.SnapshotText, FontSize = 12 }));
        notes.Add(ReaderTheme.Metadata(new Label { Text = SharedStrings.MarksLimit, FontSize = 12 }));
        content.Add(notes);
        ReaderTheme.Apply(content);
    }

    private async Task OpenAsync(MarksEntry assessment)
    {
        if (assessment.Assessment is not { } mark) return;
        Busy = true;
        openedId = mark.Id;
        try
        {
            await Task.Run(() => session.Store.AcknowledgeMark(entry.Owner.Url, entry.Owner.Username,
                mark.SubjectId, mark.Id, mark.Version));
            var snapshot = await Task.Run(() => session.Store.LoadMarks(entry.Owner.Url, entry.Owner.Username)) ??
                throw new ObkException(SharedStrings.ErrorMarksNotDownloaded);
            var subject = snapshot.Data.Subjects.SingleOrDefault(subject => subject.Subject.Id == entry.Subject.Subject.Id) ??
                throw new ObkException(SharedStrings.ErrorMarksChanged);
            entry = new(entry.Owner, subject, snapshot);
            status.Text = "";
            status.IsVisible = false;
        }
        catch (ObkException error)
        {
            status.Text = error.Message;
            status.IsVisible = true;
            await DisplayAlertAsync(AppStrings.ReaderOperationFailedAlertTitle, error.Message, AppStrings.AlertOkButton);
        }
        finally { Busy = false; Render(); }
    }
}
