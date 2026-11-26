using OBK.AndroidApp.Localization;
using OBK.Core;
using OBK.Core.Localization;

namespace OBK.AndroidApp;

internal sealed class DownloadStatusView : ContentView
{
    private readonly Label count = new() { FontSize = 14, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly Label notice = ReaderTheme.Metadata(new Label { FontSize = 12, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation });
    private readonly ProgressBar bar = new() { HeightRequest = 3 };
    private readonly ActivityIndicator activity = new()
    {
        WidthRequest = 24, HeightRequest = 24, Margin = new Thickness(0, 0, 8, 0), VerticalOptions = LayoutOptions.Center
    };
    private ReaderSession? session;
    private bool listening;

    internal ReaderSession? Session
    {
        get => session;
        set
        {
            if (listening && session is not null) session.DownloadChanged -= Changed;
            session = value;
            if (listening && session is not null) session.DownloadChanged += Changed;
            Refresh();
        }
    }

    public DownloadStatusView(ContentPage owner)
    {
        IsVisible = false;
        var details = ReaderTheme.Subtle(new Button { Text = SharedStrings.DownloadDetails, LineBreakMode = LineBreakMode.WordWrap });
        details.Clicked += async (_, _) =>
        {
            if (session?.Download is { } download)
                await owner.DisplayAlertAsync(SharedStrings.DownloadDetails, download.State.Details, AppStrings.AlertOkButton);
        };
        var heading = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        heading.Add(count);
        heading.Add(details, 1);
        var detail = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        detail.Add(activity);
        detail.Add(notice, 1);
        Content = new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                bar, new VerticalStackLayout { Padding = new Thickness(16, 2, 16, 8), Spacing = 2, Children = { heading, detail } }
            }
        };
        this.SetAppThemeColor(BackgroundColorProperty, ReaderTheme.Selection, ReaderTheme.DarkSelection);
        bar.SetAppThemeColor(ProgressBar.ProgressColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        activity.SetAppThemeColor(ActivityIndicator.ColorProperty, ReaderTheme.Accent, ReaderTheme.DarkAccent);
        ReaderTheme.Apply(this);
        Loaded += (_, _) =>
        {
            listening = true;
            if (session is not null) session.DownloadChanged += Changed;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            listening = false;
            if (session is not null) session.DownloadChanged -= Changed;
        };
    }

    private void Changed() => MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh()
    {
        var state = session?.Download?.State;
        IsVisible = session?.ReadingDuringDownload == true && state?.Phase == InitialDownloadPhase.Attachments;
        activity.IsVisible = activity.IsRunning = IsVisible && state is { Running: true } && ReaderTheme.AnimationsEnabled;
        if (!IsVisible || state is null) return;
        count.Text = state.AttachmentCount;
        bar.Progress = state.Fraction;
        notice.Text = state.Error is null ? SharedStrings.DownloadKeepOpen : state.Status;
        SemanticProperties.SetDescription(notice, state.Error is null ? state.Status + ". " + SharedStrings.DownloadKeepOpen : state.Status);
    }
}
